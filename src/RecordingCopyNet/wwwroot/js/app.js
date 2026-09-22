let meetings = [];

// --- Init ---
async function init() {
  try {
    const { version } = await api('/api/version');
    document.getElementById('version').textContent = `v${version}`;
  } catch (e) { /* ignore */ }

  try {
    const { settings } = await api('/api/settings');
    if (settings.default_zoom_user) {
      document.getElementById('user-email').value = settings.default_zoom_user;
    }
  } catch (e) { /* ignore */ }

  // Default date range: last 30 days
  const today = new Date();
  const from = new Date(today);
  from.setDate(from.getDate() - 30);
  document.getElementById('date-to').value = formatDate(today);
  document.getElementById('date-from').value = formatDate(from);

  refreshHeaderWsStatus();
}

async function refreshHeaderWsStatus() {
  const el = document.getElementById('ws-header-status');
  if (!el) return;
  const label = el.querySelector('.ws-label');
  try {
    const data = await api('/api/zoom/websocket/status');
    if (data.status === 'connected') {
      el.className = 'ws-status ws-connected';
      el.title = 'Zoom WebSocket: Connected';
      if (label) label.textContent = 'Live';
    } else if (data.status === 'connecting') {
      el.className = 'ws-status ws-connecting';
      el.title = 'Zoom WebSocket: Connecting...';
      if (label) label.textContent = 'Connecting...';
    } else {
      el.className = 'ws-status ws-disconnected';
      el.title = 'Zoom WebSocket: Disconnected';
      if (label) label.textContent = 'Disconnected';
    }
  } catch (e) {
    el.className = 'ws-status ws-disconnected';
    el.title = 'Zoom WebSocket: Disconnected';
    if (label) label.textContent = 'Disconnected';
  }
}

function formatDate(d) {
  return d.toISOString().split('T')[0];
}

function formatSize(bytes) {
  if (!bytes) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB'];
  let i = 0, size = bytes;
  while (size >= 1024 && i < units.length - 1) { size /= 1024; i++; }
  return size.toFixed(1) + ' ' + units[i];
}

function formatDuration(minutes) {
  if (!minutes) return '';
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return h > 0 ? `${h}h ${m}m` : `${m}m`;
}

// --- Fetch Recordings ---
async function fetchRecordings() {
  const userId = document.getElementById('user-email').value.trim();
  if (!userId) { alert('Please enter a Zoom user email.'); return; }

  const from = document.getElementById('date-from').value;
  const to = document.getElementById('date-to').value;
  const btn = document.getElementById('fetch-btn');

  btn.disabled = true;
  btn.textContent = 'Fetching...';

  try {
    const params = new URLSearchParams({ userId });
    if (from) params.set('from', from);
    if (to) params.set('to', to);

    const data = await api(`/api/zoom/recordings?${params}`);
    meetings = data.meetings || [];
    renderRecordings();
  } catch (e) {
    alert('Error fetching recordings: ' + e.message);
  } finally {
    btn.disabled = false;
    btn.textContent = 'Fetch Recordings';
  }
}

// --- Render ---
function renderRecordings() {
  const list = document.getElementById('recording-list');
  const section = document.getElementById('recordings-section');
  const empty = document.getElementById('empty-state');

  if (meetings.length === 0) {
    section.style.display = 'none';
    empty.style.display = 'block';
    empty.innerHTML = '<p>No recordings found for this date range.</p>';
    return;
  }

  empty.style.display = 'none';
  section.style.display = 'block';
  document.getElementById('recording-count').textContent = meetings.length;

  list.innerHTML = meetings.map((m, i) => {
    const files = m.recording_files || [];
    const totalSize = files.reduce((sum, f) => sum + (f.file_size || 0), 0);
    const types = [...new Set(files.map(f => f.file_type).filter(Boolean))];
    const date = m.start_time ? m.start_time.substring(0, 10) : 'Unknown date';
    const time = m.start_time ? new Date(m.start_time).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '';

    return `
      <div class="recording-card">
        <input type="checkbox" data-index="${i}" data-meeting-id="${m.uuid}" class="rec-checkbox">
        <div class="recording-info">
          <div class="topic">${escapeHtml(m.topic || 'Untitled Meeting')}</div>
          <div class="meta">
            ${date} ${time} &bull; ${formatDuration(m.duration)} &bull; ${files.length} files &bull; ${formatSize(totalSize)}
          </div>
          <div class="files">
            ${types.map(t => `<span class="file-badge">${t}</span>`).join('')}
          </div>
        </div>
      </div>
    `;
  }).join('');
}

function escapeHtml(text) {
  const d = document.createElement('div');
  d.textContent = text;
  return d.innerHTML;
}

function toggleSelectAll() {
  const checked = document.getElementById('select-all').checked;
  document.querySelectorAll('.rec-checkbox').forEach(cb => cb.checked = checked);
}

// --- Transfer ---
async function transferSelected() {
  const checkboxes = document.querySelectorAll('.rec-checkbox:checked');
  if (checkboxes.length === 0) { alert('Please select at least one recording.'); return; }

  const transferBtn = document.getElementById('transfer-btn');
  transferBtn.disabled = true;
  transferBtn.textContent = 'Transferring...';

  const logSection = document.getElementById('transfer-section');
  const logEl = document.getElementById('transfer-log');
  logSection.style.display = 'block';
  logEl.innerHTML = '';

  const selectedMeetings = Array.from(checkboxes).map(cb => ({
    index: parseInt(cb.dataset.index),
    meetingId: cb.dataset.meetingId,
  }));

  for (const { meetingId, index } of selectedMeetings) {
    const topic = meetings[index]?.topic || 'Meeting';
    appendLog(`--- Starting transfer: ${topic} ---`);

    try {
      await streamTransfer(meetingId, logEl);
    } catch (e) {
      appendLog(`Error: ${e.message}`, 'error');
    }
  }

  appendLog('--- All transfers complete ---', 'complete');
  transferBtn.disabled = false;
  transferBtn.textContent = 'Transfer Selected to Google Drive';
}

function streamTransfer(meetingId, logEl) {
  return new Promise((resolve, reject) => {
    const encodedId = encodeURIComponent(meetingId);

    fetch(`/api/transfer/${encodedId}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
    }).then(response => {
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
                  appendLog(event.result ? `Done: ${event.result.filesUploaded} files to "${event.result.folderName}"` : 'Done', 'complete');
                } else if (event.type === 'error') {
                  appendLog(`Error: ${event.message}`, 'error');
                }
              } catch (e) { /* skip malformed events */ }
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

init();
