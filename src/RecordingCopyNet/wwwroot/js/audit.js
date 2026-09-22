let offset = 0;
const LIMIT = 50;

async function init() {
  try {
    const { version } = await api('/api/version');
    document.getElementById('version').textContent = `v${version}`;
  } catch (e) { /* ignore */ }

  refreshHeaderWsStatus();
  loadRequests();
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

async function loadRequests() {
  try {
    const data = await api(`/api/requests?limit=${LIMIT}&offset=${offset}`);
    const requests = data.requests || [];

    if (requests.length === 0 && offset === 0) {
      document.getElementById('empty-state').style.display = 'block';
      return;
    }

    document.getElementById('empty-state').style.display = 'none';
    renderRows(requests);
    offset += requests.length;

    document.getElementById('load-more-wrap').style.display =
      requests.length >= LIMIT ? 'block' : 'none';
  } catch (err) {
    alert('Error loading requests: ' + err.message);
  }
}

function loadMore() {
  loadRequests();
}

function renderRows(requests) {
  const tbody = document.getElementById('audit-body');
  for (const r of requests) {
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td>${formatDate(r.created_at)}</td>
      <td>${escapeHtml(r.name)} ${escapeHtml(r.surname)}</td>
      <td>${escapeHtml(r.email)}</td>
      <td>${escapeHtml(r.meeting_id)}</td>
      <td>${escapeHtml(r.reason || '-')}</td>
      <td><span class="event-status event-status-${r.status}">${r.status}</span>${r.transfer_error ? `<div class="audit-error">${escapeHtml(r.transfer_error)}</div>` : ''}</td>
    `;
    tbody.appendChild(tr);
  }
}

function formatDate(dateStr) {
  if (!dateStr) return '-';
  const d = new Date(dateStr + 'Z');
  return d.toLocaleDateString() + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function escapeHtml(text) {
  const d = document.createElement('div');
  d.textContent = text;
  return d.innerHTML;
}

init();
