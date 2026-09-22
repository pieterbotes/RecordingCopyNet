// --- Helpers ---
function showMsg(id, msg, isError) {
  const el = document.getElementById(id);
  el.textContent = msg;
  el.className = 'status-msg ' + (isError ? 'error' : 'success');
  el.style.display = 'block';
}

function setStatus(id, configured) {
  const el = document.getElementById(id);
  el.textContent = configured ? 'Configured' : 'Not Configured';
  el.className = 'status ' + (configured ? 'status-ok' : 'status-none');
}

function onStorageMethodChange() {
  const method = document.getElementById('google-storage-method').value;
  document.getElementById('delegation-fields').style.display = method === 'delegation' ? 'block' : 'none';
  document.getElementById('shared-drive-fields').style.display = method === 'shared_drive' ? 'block' : 'none';
}

function showCredentialFields() {
  document.getElementById('google-creds-section').style.display = 'block';
  document.getElementById('google-creds-saved').style.display = 'none';
}

// --- Init ---
async function init() {
  try {
    const { version } = await api('/api/version');
    document.getElementById('version').textContent = `v${version}`;
  } catch (e) { /* ignore */ }

  try {
    const zoom = await api('/api/credentials/zoom');
    setStatus('zoom-status', zoom.configured);
  } catch (e) { /* ignore */ }

  try {
    const google = await api('/api/credentials/google');
    setStatus('google-status', google.configured);
    if (google.configured) {
      document.getElementById('google-creds-section').style.display = 'none';
      document.getElementById('google-creds-saved').style.display = 'block';
    }
  } catch (e) { /* ignore */ }

  try {
    const { settings } = await api('/api/settings');
    if (settings.google_folder_id) {
      document.getElementById('google-folder-id').value = settings.google_folder_id;
    }
    if (settings.google_impersonate_email) {
      document.getElementById('google-impersonate-email').value = settings.google_impersonate_email;
      document.getElementById('google-storage-method').value = 'delegation';
    }
    onStorageMethodChange();
    if (settings.default_zoom_user) {
      document.getElementById('default-zoom-user').value = settings.default_zoom_user;
    }
    document.getElementById('transfer-all-users').checked = settings.transfer_all_users === 'true';
    toggleAllUsers();
    if (settings.zoom_websocket_url) {
      document.getElementById('zoom-ws-url').value = settings.zoom_websocket_url;
    }
  } catch (e) { /* ignore */ }

  refreshWsStatus();
  startLogStream();
}

// --- Zoom ---
async function saveZoom() {
  try {
    await api('/api/credentials/zoom', {
      method: 'POST',
      body: {
        account_id: document.getElementById('zoom-account-id').value.trim(),
        client_id: document.getElementById('zoom-client-id').value.trim(),
        client_secret: document.getElementById('zoom-client-secret').value.trim(),
      },
    });
    setStatus('zoom-status', true);
    showMsg('zoom-msg', 'Zoom credentials saved.', false);
  } catch (e) {
    showMsg('zoom-msg', e.message, true);
  }
}

async function testZoom() {
  try {
    const res = await api('/api/zoom/test', { method: 'POST' });
    showMsg('zoom-msg', res.message, false);
  } catch (e) {
    showMsg('zoom-msg', e.message, true);
  }
}

async function clearZoom() {
  if (!confirm('Clear Zoom credentials?')) return;
  try {
    await api('/api/credentials/zoom', { method: 'DELETE' });
    setStatus('zoom-status', false);
    document.getElementById('zoom-account-id').value = '';
    document.getElementById('zoom-client-id').value = '';
    document.getElementById('zoom-client-secret').value = '';
    document.getElementById('zoom-ws-url').value = '';
    showMsg('zoom-msg', 'Zoom credentials cleared.', false);
    refreshWsStatus();
  } catch (e) {
    showMsg('zoom-msg', e.message, true);
  }
}

// --- Google ---
async function saveGoogle() {
  try {
    let clientEmail = document.getElementById('google-client-email').value.trim();
    let privateKey = document.getElementById('google-private-key').value.trim();
    let projectId = document.getElementById('google-project-id').value.trim();

    // Try parsing JSON first
    let clientId = '';
    const jsonText = document.getElementById('google-json').value.trim();
    if (jsonText) {
      const parsed = JSON.parse(jsonText);
      clientEmail = parsed.client_email || clientEmail;
      privateKey = parsed.private_key || privateKey;
      projectId = parsed.project_id || projectId;
      clientId = parsed.client_id || '';
    }

    // Only save credentials if new ones are provided
    const hasNewCreds = clientEmail && privateKey;
    const { configured: credsExist } = await api('/api/credentials/google');

    if (hasNewCreds) {
      await api('/api/credentials/google', {
        method: 'POST',
        body: { client_email: clientEmail, private_key: privateKey, project_id: projectId, client_id: clientId },
      });
    } else if (!credsExist) {
      throw new Error('Client email and private key are required');
    }

    // Save settings — clear impersonation email if Shared Drive method selected
    const storageMethod = document.getElementById('google-storage-method').value;
    const folderId = document.getElementById('google-folder-id').value.trim();
    const impersonateEmail = storageMethod === 'delegation'
      ? document.getElementById('google-impersonate-email').value.trim()
      : '';

    if (storageMethod === 'delegation' && !impersonateEmail) {
      throw new Error('Impersonation email is required for Domain-Wide Delegation');
    }

    await saveSettingsPatch({
      google_folder_id: folderId || null,
      google_impersonate_email: impersonateEmail || null,
    });

    setStatus('google-status', true);
    document.getElementById('google-creds-section').style.display = 'none';
    document.getElementById('google-creds-saved').style.display = 'block';
    const methodLabel = storageMethod === 'delegation' ? 'Domain-Wide Delegation' : 'Shared Drive';
    showMsg('google-msg', `Google settings saved (${methodLabel}).`, false);
    document.getElementById('google-json').value = '';
  } catch (e) {
    showMsg('google-msg', e.message, true);
  }
}

async function testGoogle() {
  try {
    const res = await api('/api/google/test', { method: 'POST' });
    showMsg('google-msg', res.message, !res.ok);
  } catch (e) {
    showMsg('google-msg', e.message, true);
  }
}

async function detectDrives() {
  const container = document.getElementById('drives-list');
  container.style.display = 'block';
  container.innerHTML = '<p style="color:var(--text-light)">Loading shared drives...</p>';
  try {
    const res = await api('/api/google/drives');
    if (!res.drives || res.drives.length === 0) {
      container.innerHTML = '<p style="color:var(--text-light)">No Shared Drives found. Make sure the service account has been added as a member of at least one Shared Drive.</p>';
      return;
    }
    let html = '<p style="margin:8px 0 4px; font-size:0.85rem; color:var(--text-light)">Click a drive to use its root as the folder ID:</p>';
    html += '<ul style="list-style:none; padding:0; margin:0">';
    for (const d of res.drives) {
      html += `<li style="margin:2px 0"><button class="btn btn-outline btn-sm" onclick="selectDrive('${d.id}')" style="text-align:left; width:100%">${d.name} <span style="color:var(--text-light); font-size:0.8rem">(${d.id})</span></button></li>`;
    }
    html += '</ul>';
    container.innerHTML = html;
  } catch (e) {
    container.innerHTML = `<p style="color:var(--danger)">${e.message}</p>`;
  }
}

function selectDrive(driveId) {
  document.getElementById('google-folder-id').value = driveId;
  document.getElementById('drives-list').style.display = 'none';
  showMsg('google-msg', 'Folder ID set to Shared Drive root. Click "Save" then "Test Connection" to verify.', false);
}

async function clearGoogle() {
  if (!confirm('Clear Google credentials?')) return;
  try {
    await api('/api/credentials/google', { method: 'DELETE' });
    setStatus('google-status', false);
    document.getElementById('google-json').value = '';
    document.getElementById('google-project-id').value = '';
    document.getElementById('google-client-email').value = '';
    document.getElementById('google-private-key').value = '';
    document.getElementById('google-impersonate-email').value = '';
    document.getElementById('google-storage-method').value = 'shared_drive';
    onStorageMethodChange();
    document.getElementById('google-creds-section').style.display = 'block';
    document.getElementById('google-creds-saved').style.display = 'none';
    showMsg('google-msg', 'Google credentials cleared.', false);
  } catch (e) {
    showMsg('google-msg', e.message, true);
  }
}

// --- App Settings ---

// POST /api/credentials/settings REPLACES the whole settings record, so every
// save must send all fields. Read-modify-write here in one place, so callers
// pass only the keys they are changing and can never silently blank the rest.
async function saveSettingsPatch(patch) {
  const existing = await api('/api/settings');
  const current = existing.settings || {};
  await api('/api/credentials/settings', {
    method: 'POST',
    body: {
      google_folder_id: current.google_folder_id || null,
      google_impersonate_email: current.google_impersonate_email || null,
      default_zoom_user: current.default_zoom_user || null,
      transfer_all_users: current.transfer_all_users || null,
      zoom_websocket_url: current.zoom_websocket_url || null,
      ...patch,
    },
  });
}

// Greys out the email field when all-users mode makes it irrelevant for
// auto-transfer. The value is kept — other pages still use it as a default.
function toggleAllUsers() {
  const allUsers = document.getElementById('transfer-all-users').checked;
  document.getElementById('default-zoom-user').disabled = allUsers;
}

async function saveAppSettings() {
  try {
    await saveSettingsPatch({
      default_zoom_user: document.getElementById('default-zoom-user').value.trim() || null,
      transfer_all_users: document.getElementById('transfer-all-users').checked ? 'true' : null,
    });
    const allUsers = document.getElementById('transfer-all-users').checked;
    showMsg('settings-msg', allUsers
      ? 'Settings saved. Auto-transfer is now active for ALL Zoom users.'
      : 'Settings saved.', false);
  } catch (e) {
    showMsg('settings-msg', e.message, true);
  }
}

// --- WebSocket ---
async function saveWebSocket() {
  try {
    await saveSettingsPatch({
      zoom_websocket_url: document.getElementById('zoom-ws-url').value.trim() || null,
    });
    showMsg('ws-msg', 'WebSocket URL saved.', false);
    setTimeout(refreshWsStatus, 1500);
  } catch (e) {
    showMsg('ws-msg', e.message, true);
  }
}

async function clearWebSocket() {
  document.getElementById('zoom-ws-url').value = '';
  try {
    await saveSettingsPatch({ zoom_websocket_url: null });
    showMsg('ws-msg', 'WebSocket URL cleared.', false);
    refreshWsStatus();
  } catch (e) {
    showMsg('ws-msg', e.message, true);
  }
}

async function toggleWebSocket() {
  const btn = document.getElementById('ws-toggle-btn');
  const statusEl = document.getElementById('ws-status');
  const isRunning = !statusEl.classList.contains('status-none');

  btn.disabled = true;
  const endpoint = isRunning ? '/api/zoom/websocket/stop' : '/api/zoom/websocket/start';
  await api(endpoint, { method: 'POST' });

  setTimeout(() => {
    refreshWsStatus();
    btn.disabled = false;
  }, 1000);
}

// One fetch, used for both the status badges and the log — the response
// already contains status, counters and log. Also the manual-Refresh path.
async function refreshWsStatus() {
  try {
    const data = await api('/api/zoom/websocket/status');
    applyWsStatus(data);
    renderCounters(data);
    renderLogLines(data.log || []);
  } catch (e) {
    applyWsStatus({ status: 'disconnected' });
  }
}

// Drives the badges from either a polled response or an SSE push.
function applyWsStatus(data) {
  const badge = document.getElementById('ws-status');
  const dot = document.getElementById('ws-header-status');
  const toggleBtn = document.getElementById('ws-toggle-btn');
  if (!badge || !dot) return;

  if (data.status === 'connected') {
    badge.textContent = 'Connected';
    badge.className = 'status status-ok';
    dot.className = 'ws-indicator ws-connected';
    dot.title = 'Zoom WebSocket: Connected';
    if (toggleBtn) toggleBtn.textContent = 'Stop';
  } else if (data.status === 'connecting') {
    badge.textContent = 'Connecting...';
    badge.className = 'status status-none';
    dot.className = 'ws-indicator ws-connecting';
    dot.title = 'Zoom WebSocket: Connecting...';
    if (toggleBtn) toggleBtn.textContent = 'Stop';
  } else {
    badge.textContent = 'Disconnected';
    badge.className = 'status status-none';
    dot.className = 'ws-indicator ws-disconnected';
    dot.title = 'Zoom WebSocket: Disconnected';
    if (toggleBtn) toggleBtn.textContent = 'Start';
  }
}

function renderCounters(data) {
  const c = data.counters || {};
  const t = data.transfers;
  const since = data.connectedAt ? new Date(data.connectedAt).toLocaleTimeString() : 'N/A';
  const transfers = t ? ` | Transfers: ${t.active}/${t.limit} active, ${t.queued} queued` : '';
  const el = document.getElementById('ws-counters');
  if (el) {
    el.textContent = `Messages: ${c.messages || 0} | Heartbeats: ${c.heartbeats || 0} | `
      + `Events: ${c.events || 0} | Errors: ${c.errors || 0} | Connected since: ${since}${transfers}`;
  }
}

const MAX_RENDERED_LOG_LINES = 50;

// Built as a DOM node with textContent — log messages embed Zoom-supplied data
// (meeting topics, raw payloads), so they must never be parsed as HTML.
function logLineEl(e) {
  const div = document.createElement('div');
  div.className = /error|fail/i.test(e.msg) ? 'log-line error' : 'log-line';
  div.textContent = `[${new Date(e.time).toLocaleTimeString()}] ${e.msg}`;
  return div;
}

function emptyLogEl() {
  const div = document.createElement('div');
  div.className = 'log-line log-empty';
  div.style.color = '#888';
  div.textContent = 'No log entries yet.';
  return div;
}

// Only auto-scroll when already pinned to the bottom, so scrolling back
// through history isn't yanked away every time a new line arrives.
function isPinnedToBottom(el) {
  return el.scrollHeight - el.scrollTop - el.clientHeight < 40;
}

function renderLogLines(entries) {
  const logEl = document.getElementById('ws-debug-log');
  if (!logEl) return;
  if (entries.length === 0) {
    logEl.replaceChildren(emptyLogEl());
    return;
  }
  const frag = document.createDocumentFragment();
  for (const e of entries.slice(-MAX_RENDERED_LOG_LINES)) frag.appendChild(logLineEl(e));
  logEl.replaceChildren(frag);
  logEl.scrollTop = logEl.scrollHeight;
}

function appendLogLine(entry) {
  const logEl = document.getElementById('ws-debug-log');
  if (!logEl) return;
  if (logEl.querySelector('.log-empty')) logEl.replaceChildren();

  const pinned = isPinnedToBottom(logEl);
  logEl.appendChild(logLineEl(entry));
  while (logEl.childElementCount > MAX_RENDERED_LOG_LINES) logEl.removeChild(logEl.firstElementChild);
  if (pinned) logEl.scrollTop = logEl.scrollHeight;
}

// --- Live log stream (SSE) — replaces manual refresh ---
let _logStream = null;

function startLogStream() {
  if (_logStream) return;
  _logStream = new EventSource('/api/zoom/websocket/stream');

  _logStream.addEventListener('snapshot', (ev) => {
    const data = JSON.parse(ev.data);
    applyWsStatus(data);
    renderCounters(data);
    renderLogLines(data.log || []);
  });

  _logStream.addEventListener('append', (ev) => {
    const data = JSON.parse(ev.data);
    applyWsStatus(data);
    renderCounters(data);
    if (data.entry) appendLogLine(data.entry);
  });

  // EventSource retries automatically; the snapshot on reconnect re-syncs state.
  _logStream.onerror = () => { /* transient */ };
}

window.addEventListener('beforeunload', () => {
  if (_logStream) _logStream.close();
});

init();