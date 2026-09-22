let fetchedMeetings = [];

// --- Init ---
async function init() {
  try {
    const { version } = await api('/api/version');
    document.getElementById('version').textContent = `v${version}`;
  } catch (e) { /* ignore */ }

  refreshHeaderWsStatus();

  // Default meeting date to today
  document.getElementById('req-meeting-date').value = new Date().toISOString().split('T')[0];

  // Pre-fill zoom email from settings
  try {
    const { settings } = await api('/api/settings');
    if (settings.default_zoom_user) {
      document.getElementById('req-zoom-email').value = settings.default_zoom_user;
      // Auto-fetch meetings if we have both email and date
      fetchMeetings();
    }
  } catch (e) { /* ignore */ }

  // Auto-fetch when date or zoom email changes
  document.getElementById('req-meeting-date').addEventListener('change', fetchMeetings);
  document.getElementById('req-zoom-email').addEventListener('change', fetchMeetings);
}

async function refreshHeaderWsStatus() {
  const el = document.getElementById('ws-header-status');
  if (!el) return;
  const label = el.querySelector('.ws-label');
  try {
    const data = await api('/api/zoom/websocket/status');
    if (data.status === 'connected') {
      el.className = 'ws-status ws-connected';
      if (label) label.textContent = 'Live';
    } else if (data.status === 'connecting') {
      el.className = 'ws-status ws-connecting';
      if (label) label.textContent = 'Connecting...';
    } else {
      el.className = 'ws-status ws-disconnected';
      if (label) label.textContent = 'Disconnected';
    }
  } catch (e) {
    el.className = 'ws-status ws-disconnected';
    if (label) label.textContent = 'Disconnected';
  }
}

// --- Fetch meetings for dropdown ---
async function fetchMeetings() {
  const zoomEmail = document.getElementById('req-zoom-email').value.trim();
  const meetingDate = document.getElementById('req-meeting-date').value;
  const select = document.getElementById('req-meeting-id');
  const statusEl = document.getElementById('meeting-fetch-status');

  if (!zoomEmail || !meetingDate) {
    select.innerHTML = '<option value="">Select a date and Zoom email first</option>';
    select.disabled = true;
    statusEl.textContent = '';
    fetchedMeetings = [];
    return;
  }

  select.innerHTML = '<option value="">Loading meetings...</option>';
  select.disabled = true;
  statusEl.textContent = '';

  try {
    const params = new URLSearchParams({ userId: zoomEmail, from: meetingDate, to: meetingDate });
    const data = await api(`/api/zoom/recordings?${params}`);
    fetchedMeetings = data.meetings || [];

    if (fetchedMeetings.length === 0) {
      select.innerHTML = '<option value="">No recordings found for this date</option>';
      select.disabled = true;
      statusEl.textContent = 'Try a different date or Zoom email.';
      return;
    }

    select.innerHTML = '<option value="">-- Select a meeting --</option>' +
      fetchedMeetings.map((m, i) => {
        const time = m.start_time
          ? new Date(m.start_time).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
          : '';
        const topic = m.topic || 'Untitled Meeting';
        const files = (m.recording_files || []).length;
        return `<option value="${i}">${escapeHtml(topic)} (${time}, ${files} files)</option>`;
      }).join('');
    select.disabled = false;
    statusEl.textContent = `${fetchedMeetings.length} recording(s) found.`;
  } catch (err) {
    select.innerHTML = '<option value="">Error fetching meetings</option>';
    select.disabled = true;
    statusEl.textContent = err.message;
    fetchedMeetings = [];
  }
}

// --- Form submission ---
document.getElementById('request-form').addEventListener('submit', async (e) => {
  e.preventDefault();

  const name = document.getElementById('req-name').value.trim();
  const surname = document.getElementById('req-surname').value.trim();
  const email = document.getElementById('req-email').value.trim();
  const meeting_date = document.getElementById('req-meeting-date').value;
  const reason = document.getElementById('req-reason').value.trim();

  const selectedIndex = document.getElementById('req-meeting-id').value;
  if (selectedIndex === '' || !fetchedMeetings[selectedIndex]) {
    alert('Please select a meeting.');
    return;
  }
  const meeting = fetchedMeetings[selectedIndex];
  const meeting_id = meeting.uuid;

  if (!name || !surname || !email || !meeting_id || !meeting_date) {
    alert('Please fill in all required fields.');
    return;
  }

  const submitBtn = document.getElementById('submit-btn');
  submitBtn.disabled = true;
  submitBtn.textContent = 'Submitting...';

  const progressSection = document.getElementById('progress-section');
  const logEl = document.getElementById('transfer-log');
  const resultMsg = document.getElementById('result-msg');
  progressSection.style.display = 'block';
  logEl.innerHTML = '';
  resultMsg.style.display = 'none';

  try {
    // Step 1: Create request
    appendLog('Creating transfer request...');
    const { id } = await api('/api/requests', {
      method: 'POST',
      body: { name, surname, email, meeting_id, meeting_date, reason },
    });
    appendLog(`Request #${id} created. Starting transfer...`);

    // Step 2: Stream transfer
    await streamTransfer(id, logEl, resultMsg);
  } catch (err) {
    appendLog(`Error: ${err.message}`, 'error');
    resultMsg.className = 'status-msg error';
    resultMsg.textContent = `Failed: ${err.message}`;
    resultMsg.style.display = 'block';
  } finally {
    submitBtn.disabled = false;
    submitBtn.textContent = 'Submit Request';
  }
});

function streamTransfer(requestId, logEl, resultMsg) {
  return new Promise((resolve, reject) => {
    fetch(`/api/requests/${requestId}/transfer`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
    }).then(response => {
      if (!response.ok) {
        return response.json().then(data => {
          throw new Error(data.error || `Request failed (${response.status})`);
        });
      }

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';

      function read() {
        reader.read().then(({ done, value }) => {
          if (done) { resolve(); return; }

          buffer += decoder.decode(value, { stream: true });
          const lines = buffer.split('\n');
          buffer = lines.pop();

          for (const line of lines) {
            if (line.startsWith('data: ')) {
              try {
                const event = JSON.parse(line.substring(6));
                if (event.type === 'progress') {
                  appendLog(event.message);
                } else if (event.type === 'complete') {
                  const msg = event.result
                    ? `Completed: ${event.result.filesUploaded} files uploaded to "${event.result.folderName}"`
                    : 'Transfer completed';
                  appendLog(msg, 'complete');
                  resultMsg.className = 'status-msg success';
                  resultMsg.textContent = msg;
                  resultMsg.style.display = 'block';
                } else if (event.type === 'error') {
                  appendLog(`Error: ${event.message}`, 'error');
                  resultMsg.className = 'status-msg error';
                  resultMsg.textContent = `Failed: ${event.message}`;
                  resultMsg.style.display = 'block';
                }
              } catch (e) { /* skip malformed */ }
            }
          }

          read();
        }).catch(reject);
      }

      read();
    }).catch(reject);
  });
}

function appendLog(message, type = '') {
  const logEl = document.getElementById('transfer-log');
  const line = document.createElement('div');
  line.className = 'log-line' + (type ? ` ${type}` : '');
  line.textContent = `[${new Date().toLocaleTimeString()}] ${message}`;
  logEl.appendChild(line);
  logEl.scrollTop = logEl.scrollHeight;
}

function escapeHtml(text) {
  const d = document.createElement('div');
  d.textContent = text;
  return d.innerHTML;
}

init();
