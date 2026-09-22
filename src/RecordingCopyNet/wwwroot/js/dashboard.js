let _events = [];
let _offset = 0;
const PAGE_SIZE = 50;
let _refreshTimer = null;

async function init() {
  try {
    const { version } = await api('/api/version');
    document.getElementById('version').textContent = `v${version}`;
  } catch (e) { /* ignore */ }

  refreshHeaderWsStatus();
  loadStats();
  loadEvents();

  _refreshTimer = setInterval(() => {
    if (document.getElementById('event-list-view').style.display !== 'none') {
      loadStats();
      refreshEvents();
    }
  }, 15000);
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

async function loadStats() {
  try {
    const stats = await api('/api/events/stats');
    document.getElementById('stat-total').textContent = stats.total || 0;
    document.getElementById('stat-completed').textContent = stats.completed || 0;
    document.getElementById('stat-failed').textContent = stats.failed || 0;
    document.getElementById('stat-skipped').textContent = stats.skipped || 0;
  } catch (e) { /* ignore */ }
}

async function loadEvents() {
  _offset = 0;
  _events = [];
  try {
    const data = await api(`/api/events?limit=${PAGE_SIZE}&offset=0`);
    _events = data.events || [];
    _offset = _events.length;
    renderEvents();
  } catch (e) {
    document.getElementById('event-list').innerHTML =
      '<div class="empty-state"><p>Error loading events.</p></div>';
  }
}

async function refreshEvents() {
  try {
    const data = await api(`/api/events?limit=${PAGE_SIZE}&offset=0`);
    const fresh = data.events || [];
    if (fresh.length > 0 && (_events.length === 0 || fresh[0].id !== _events[0].id)) {
      _events = fresh;
      _offset = _events.length;
      renderEvents();
      loadStats();
    }
  } catch (e) { /* ignore */ }
}

async function loadMore() {
  try {
    const data = await api(`/api/events?limit=${PAGE_SIZE}&offset=${_offset}`);
    const more = data.events || [];
    if (more.length > 0) {
      _events = _events.concat(more);
      _offset += more.length;
      renderEvents();
    }
    if (more.length < PAGE_SIZE) {
      document.getElementById('load-more-wrap').style.display = 'none';
    }
  } catch (e) { /* ignore */ }
}

function renderEvents() {
  const list = document.getElementById('event-list');
  const empty = document.getElementById('empty-state');
  const loadMoreWrap = document.getElementById('load-more-wrap');

  if (_events.length === 0) {
    list.innerHTML = '';
    empty.style.display = 'block';
    loadMoreWrap.style.display = 'none';
    return;
  }

  empty.style.display = 'none';
  loadMoreWrap.style.display = _events.length >= PAGE_SIZE ? 'block' : 'none';

  list.innerHTML = _events.map(ev => {
    const time = ev.received_at ? formatTime(ev.received_at) : '';
    const topic = escapeHtml(ev.meeting_topic || ev.event_type || 'Unknown');
    const host = ev.host_email ? escapeHtml(ev.host_email) : '';
    const badge = statusBadge(ev.status);
    const extra = ev.status === 'skipped' && ev.skip_reason ? ` <span class="event-skip-reason">(${escapeHtml(ev.skip_reason)})</span>` : '';
    const files = ev.status === 'completed' && ev.transfer_files_uploaded != null ? ` &bull; ${ev.transfer_files_uploaded} files` : '';

    return `<div class="event-row" onclick="showDetail(${ev.id})">
      <div class="event-row-main">
        <span class="event-time">${time}</span>
        <span class="event-topic">${topic}</span>
      </div>
      <div class="event-row-meta">
        <span class="event-host">${host}</span>
        ${badge}${extra}${files}
      </div>
    </div>`;
  }).join('');
}

function statusBadge(status) {
  const cls = {
    received: 'event-status-received',
    queued: 'event-status-queued',
    transferring: 'event-status-transferring',
    completed: 'event-status-completed',
    failed: 'event-status-failed',
    skipped: 'event-status-skipped',
  }[status] || 'event-status-received';
  return `<span class="event-status ${cls}">${escapeHtml(status || 'unknown')}</span>`;
}

async function showDetail(id) {
  document.getElementById('event-list-view').style.display = 'none';
  document.getElementById('event-detail-view').style.display = 'block';

  try {
    const ev = await api(`/api/events/${id}`);
    renderDetail(ev);
  } catch (e) {
    document.getElementById('detail-info').innerHTML = '<p>Error loading event details.</p>';
  }
}

function renderDetail(ev) {
  const info = document.getElementById('detail-info');
  const duration = ev.transfer_started_at && ev.transfer_completed_at
    ? formatDurationMs(new Date(ev.transfer_completed_at) - new Date(ev.transfer_started_at))
    : '';

  let html = `<h2>Event #${ev.id} ${statusBadge(ev.status)}</h2>`;
  html += `<table class="detail-table">`;
  html += row('Type', escapeHtml(ev.event_type));
  html += row('Received', ev.received_at || '');
  html += row('Meeting', escapeHtml(ev.meeting_topic || ''));
  html += row('UUID', escapeHtml(ev.meeting_uuid || ''));
  html += row('Host', escapeHtml(ev.host_email || ''));

  if (ev.status === 'skipped') {
    html += row('Skip Reason', escapeHtml(ev.skip_reason || ''));
  }
  if (ev.transfer_started_at) {
    html += row('Transfer Started', ev.transfer_started_at);
  }
  if (ev.transfer_completed_at) {
    html += row('Transfer Completed', ev.transfer_completed_at);
  }
  if (duration) {
    html += row('Duration', duration);
  }
  if (ev.transfer_folder_name) {
    html += row('Folder', escapeHtml(ev.transfer_folder_name));
  }
  if (ev.transfer_files_uploaded != null) {
    html += row('Files Uploaded', ev.transfer_files_uploaded);
  }
  if (ev.transfer_error) {
    html += row('Error', `<span style="color:var(--danger)">${escapeHtml(ev.transfer_error)}</span>`);
  }
  html += `</table>`;
  info.innerHTML = html;

  // Transfer log
  const logSection = document.getElementById('detail-log-section');
  const logEl = document.getElementById('detail-log');
  let logs = [];
  if (ev.transfer_logs) {
    try { logs = JSON.parse(ev.transfer_logs); } catch (e) { /* ignore */ }
  }
  if (logs.length > 0) {
    logSection.style.display = 'block';
    logEl.innerHTML = logs.map(l => `<div class="log-line">${escapeHtml(l)}</div>`).join('');
  } else {
    logSection.style.display = 'none';
  }

  // Raw payload
  const payloadSection = document.getElementById('detail-payload-section');
  const payloadEl = document.getElementById('detail-payload');
  if (ev.raw_payload) {
    payloadSection.style.display = 'block';
    payloadEl.style.display = 'none';
    document.getElementById('payload-toggle').textContent = '[show]';
    try {
      payloadEl.textContent = JSON.stringify(JSON.parse(ev.raw_payload), null, 2);
    } catch (e) {
      payloadEl.textContent = ev.raw_payload;
    }
  } else {
    payloadSection.style.display = 'none';
  }
}

function togglePayload() {
  const el = document.getElementById('detail-payload');
  const toggle = document.getElementById('payload-toggle');
  if (el.style.display === 'none') {
    el.style.display = 'block';
    toggle.textContent = '[hide]';
  } else {
    el.style.display = 'none';
    toggle.textContent = '[show]';
  }
}

function closeDetail() {
  document.getElementById('event-detail-view').style.display = 'none';
  document.getElementById('event-list-view').style.display = 'block';
  loadStats();
  refreshEvents();
}

function row(label, value) {
  return `<tr><td class="detail-label">${label}</td><td>${value}</td></tr>`;
}

function escapeHtml(text) {
  const d = document.createElement('div');
  d.textContent = text;
  return d.innerHTML;
}

function formatTime(iso) {
  try {
    const d = new Date(iso + 'Z');
    return d.toLocaleDateString([], { month: 'short', day: 'numeric' }) + ' ' +
           d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  } catch (e) {
    return iso;
  }
}

function formatDurationMs(ms) {
  if (ms < 1000) return `${ms}ms`;
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  return `${m}m ${s % 60}s`;
}

init();
