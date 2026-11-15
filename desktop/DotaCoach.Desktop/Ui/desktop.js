const $ = id => document.getElementById(id);
const bridge = globalThis.chrome?.webview;
const pending = new Map();
const names = { workspace: '工作台', history: '实时对话', subtitles: '悬浮字幕', settings: '设置' };
let state = { user: null, room: null, history: [], microphone: {}, listener: {} };
let processes = [], heroes = [], selectedHeroes = [], roomMode = 'create';
let pttRequested = false, pttStarting = false, toastTimer, overlayTimer, authBusy = false;
const overlayDefaults = { enabled: true, sourceColor: '#eef3ef', targetColor: '#b8f3cc',
  opacity: .95, backgroundOpacity: .35, fontSize: 28, top: 100, seconds: 8, monitor: 0 };
let overlay = { ...overlayDefaults };
const turns = new Map();
const emptyFeed = $('conversationFeed').innerHTML;

export function call(method, data = {}) {
  if (!bridge) return Promise.reject(new Error('请在 Dota Coach 桌面客户端中使用此功能'));
  const id = crypto.randomUUID();
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('操作超时，请检查连接后重试')); }, 65000);
    pending.set(id, { resolve, reject, timer });
    bridge.postMessage({ id, method, data });
  });
}
function toast(message, error = false) {
  clearTimeout(toastTimer);
  $('toast').textContent = message;
  $('toast').className = `toast${error ? ' error-toast' : ''}`;
  $('toast').hidden = false;
  toastTimer = setTimeout(() => { $('toast').hidden = true; }, error ? 9000 : 4500);
}
async function action(task, button) {
  if (button) button.disabled = true;
  try { return await task(); }
  catch (error) { toast(error.message, true); }
  finally { if (button) { button.disabled = false; renderControls(); } }
}
function setPage(page) {
  if (!names[page]) return;
  for (const element of document.querySelectorAll('[data-view]')) element.hidden = element.dataset.view !== page;
  for (const button of document.querySelectorAll('.nav-item')) {
    const active = button.dataset.page === page;
    button.classList.toggle('active', active);
    if (active) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
  }
  $('pageBreadcrumb').textContent = names[page];
}
document.querySelectorAll('[data-page]').forEach(button => button.addEventListener('click', () => setPage(button.dataset.page)));
document.querySelectorAll('[data-close]').forEach(button => button.addEventListener('click', () => button.closest('dialog').close()));
for (const dialog of document.querySelectorAll('dialog')) dialog.addEventListener('click', event => {
  if (event.target === dialog) {
    const rect = dialog.getBoundingClientRect();
    if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) dialog.close();
  }
});

function renderControls() {
  const mic = state.microphone || {}, listener = state.listener || {}, joined = !!state.room;
  $('micToggle').disabled = !mic.ready || pttStarting;
  $('micToggle').querySelector('span').textContent = mic.active ? '关闭麦克风' : '开启麦克风';
  $('micToggle').classList.toggle('danger', !!mic.active);
  $('micBadge').textContent = mic.active ? '正在采集' : mic.ready ? '已就绪' : '未连接';
  $('micBadge').classList.toggle('live', !!mic.active);
  $('micCard').classList.toggle('running', !!mic.active);
  $('micDot').classList.toggle('on', !!mic.active);
  $('micStatus').textContent = mic.status || '进入房间后即可开麦';
  $('micDevice').disabled = !!mic.active;
  $('pttButton').disabled = !mic.ready || (!!mic.active && !pttRequested);
  $('pttButton').textContent = pttRequested ? '松开结束' : '按住说话';
  $('listenToggle').disabled = !joined && !listener.active;
  $('listenToggle').querySelector('span').textContent = listener.connecting ? '取消连接' : listener.active || listener.ready ? '停止监听' : '开始监听';
  $('listenBadge').textContent = listener.active ? '正在监听' : listener.connecting ? '连接中…' : '仅自己可见';
  $('listenBadge').classList.toggle('live', !!listener.active);
  $('listenCard').classList.toggle('running', !!listener.active);
  $('listenDot').classList.toggle('on', !!listener.active);
  $('listenStatus').textContent = listener.status || '独立采音 · 不向房间广播';
  $('processSelect').disabled = !!listener.active || !!listener.connecting;
  $('roomConnection').textContent = joined ? mic.ready ? '已连接语音室' : '连接暂不可用' : '尚未连接';
  $('roomDot').classList.toggle('on', !!mic.ready);
  $('footerRoom').textContent = joined ? `# ${state.room.name}` : '未连接语音室';
  $('footerActivity').textContent = [mic.active ? '麦克风采集中' : '', listener.active ? `正在监听 ${listener.sourceName || '程序声音'}` : ''].filter(Boolean).join(' · ') || '没有正在采集的声音';
  if (!mic.active) updateLevel('microphone', 0);
  if (!listener.active) updateLevel('listener', 0);
  $('stageCapture').classList.toggle('on', !!listener.active);
  if (!listener.active) for (const id of ['stageUpload', 'stageTranscript', 'stageTranslation']) $(id).classList.remove('on');
  $('savePreferences').disabled = !joined;
  $('editHeroes').disabled = !state.room?.isOwner;
  $('gsiButton').disabled = !state.room?.isOwner;
  $('textInput').disabled = !mic.ready;
  $('textForm').querySelector('button').disabled = !mic.ready;
  $('manageRoom').disabled = !joined;
}
function renderState(next) {
  const changedRoom = state.room?.roomId !== next.room?.roomId;
  state = next;
  if (changedRoom || !state.user) { $('partialLine').textContent = ''; pttRequested = false; }
  $('appVersion').textContent = `v${state.version || '0.2.0'}`;
  $('accountName').textContent = state.user?.username || '登录账号';
  $('accountMeta').textContent = state.user ? '管理账户与偏好' : '连接你的教练与队友';
  $('welcomeStrip').hidden = !!state.room;
  $('welcomeLogin').textContent = state.user ? '选择语音室 →' : '登录账号 →';
  $('sidebarRoom').textContent = state.room?.name || '选择语音室';
  $('roleSelect').value = state.role || 'student';
  $('selfMonitor').checked = !!state.selfMonitor;
  if (document.activeElement !== $('serverAddress')) $('serverAddress').value = state.server || 'https://dota2.largeniu.com/';
  $('serviceStatus').textContent = state.room ? state.microphone?.ready || state.listener?.ready
    ? '已登录 · 会话已连接' : '已登录 · 音频连接已暂停' : state.user ? '已登录服务' : '本地界面已就绪';
  $('logoutButton').disabled = !state.user;
  $('saveServer').disabled = !!state.user;
  turns.clear();
  for (const turn of state.history || []) turns.set(turn.id, turn);
  renderConversation(); renderHeroes(); renderControls();
}
function updateLevel(source, level) {
  const prefix = source === 'microphone' ? 'mic' : 'listen';
  const percent = Math.max(0, Math.min(100, Math.round(Number(level) * 100)));
  $(`${prefix}Level`).style.width = `${percent}%`;
  $(`${prefix}LevelText`).textContent = `${percent}%`;
}
function makeTurn(turn) {
  const item = document.createElement('article');
  item.className = 'chat-turn';
  const meta = document.createElement('div'); meta.className = 'chat-meta';
  const who = document.createElement('strong'); who.textContent = turn.speaker || '发言';
  const direction = document.createElement('span'); direction.textContent = turn.role === 'coach' ? 'EN → 中文' : '中文 → EN';
  const time = document.createElement('time');
  time.textContent = new Date(turn.timestamp).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' });
  meta.append(who, direction);
  if (turn.sourceKind === 'process') { const privacy = document.createElement('span'); privacy.className = 'private-badge'; privacy.textContent = '外部监听 · 仅自己可见'; meta.append(privacy); }
  meta.append(time);
  const source = document.createElement('p'); source.className = 'chat-source'; source.textContent = turn.source || '原文识别中…';
  const target = document.createElement('p'); target.className = 'chat-target'; target.textContent = turn.target || '翻译中…';
  item.append(meta, source, target); return item;
}
function renderConversation() {
  $('historyCount').textContent = turns.size;
  for (const [id, limit] of [['conversationFeed', 30], ['historyFeed', 300]]) {
    const container = $(id), atBottom = container.scrollHeight - container.scrollTop - container.clientHeight < 80;
    if (!turns.size) { container.innerHTML = emptyFeed; continue; }
    container.replaceChildren(...[...turns.values()].slice(-limit).map(makeTurn));
    if (atBottom) container.scrollTop = container.scrollHeight;
  }
}
bridge?.addEventListener('message', ({ data: message }) => {
  if (!message || typeof message !== 'object') return;
  if (message.type === 'response') {
    const waiter = pending.get(message.id);
    if (!waiter) return;
    clearTimeout(waiter.timer); pending.delete(message.id);
    if (message.ok) waiter.resolve(message.result); else waiter.reject(new Error(message.error || '操作失败'));
  } else if (message.type === 'state') renderState(message.state);
  else if (message.type === 'notice') toast(message.message, true);
  else if (message.type === 'level') updateLevel(message.source, message.level);
  else if (message.type === 'receipt' && message.source === 'listener') $('stageUpload').classList.add('on');
  else if (message.type === 'conversation') {
    const turn = message.turn; turns.set(turn.id, turn);
    while (turns.size > 300) turns.delete(turns.keys().next().value);
    if (turn.sourceKind === 'process') {
      if (turn.source) $('stageTranscript').classList.add('on');
      if (turn.target) $('stageTranslation').classList.add('on');
    }
    $('partialLine').textContent = '';
    renderConversation();
  } else if (message.type === 'partial') $('partialLine').textContent = message.text;
  else if (message.type === 'overlay.changed') { overlay = normalizeOverlay(message.settings); renderOverlay(); }
});

function showLogin() { $('loginError').textContent = ''; $('loginDialog').showModal(); $('username').focus(); }
$('welcomeLogin').onclick = () => state.user ? action(showRooms) : showLogin();
$('accountButton').onclick = () => state.user ? setPage('settings') : showLogin();
$('roomButton').onclick = () => state.user ? action(showRooms) : showLogin();
$('loginForm').onsubmit = async event => {
  event.preventDefault(); if (authBusy) return;
  authBusy = true; $('loginSubmit').disabled = true; $('loginError').textContent = '';
  try {
    const password = $('password').value; $('password').value = '';
    const result = await call('login', { username: $('username').value.trim(), password });
    renderState(result); $('loginDialog').close(); await showRooms(); await refreshDevices();
  } catch (error) { $('loginError').textContent = error.message; }
  finally { authBusy = false; $('loginSubmit').disabled = false; }
};
$('logoutButton').onclick = () => action(async () => { renderState(await call('logout')); toast('已退出登录，所有采集已停止'); }, $('logoutButton'));
async function showRooms() {
  const { rooms } = await call('rooms.list');
  $('roomsList').replaceChildren();
  for (const room of rooms) {
    const item = document.createElement('div'); item.className = 'room-list-item';
    const label = document.createElement('div'), title = document.createElement('strong'), detail = document.createElement('small');
    title.textContent = room.name; detail.textContent = `房主 ${room.ownerUsername} · ${(room.allowedUsernames || []).length} 位获准成员`;
    label.append(title, detail);
    const join = document.createElement('button'); join.className = 'subtle'; join.textContent = '进入';
    join.onclick = () => action(async () => {
      $('roomsDialog').close();
      renderState(await call('room.join', { roomId: room.roomId, role: $('roleSelect').value }));
    }, join);
    item.append(label, join); $('roomsList').append(item);
  }
  $('newRoom').hidden = !state.user?.canCreateRooms;
  if (!$('roomsDialog').open) $('roomsDialog').showModal();
}
$('joinForm').onsubmit = event => {
  event.preventDefault();
  action(async () => { const result = await call('room.join', { link: $('inviteInput').value.trim(), role: $('roleSelect').value }); renderState(result); $('roomsDialog').close(); }, $('joinForm').querySelector('button'));
};
async function openRoom(mode) {
  roomMode = mode;
  const owner = mode === 'create' || state.room?.isOwner;
  const users = owner ? (await call('users.list')).users : [];
  $('roomDialogTitle').textContent = mode === 'create' ? '创建语音室' : '房间管理';
  $('roomName').value = mode === 'create' ? '' : state.room?.name || '';
  $('roomName').disabled = mode !== 'create';
  $('roomInviteActions').hidden = mode === 'create';
  $('copyInvite').disabled = !state.room?.isOwner;
  const settings = mode === 'create' ? state.config.defaults : state.room.settings;
  const current = state.roster?.roomId === state.room?.roomId && mode !== 'create' ? state.roster : settings;
  for (const [id, lang] of [['zhVoice', 'zh'], ['enVoice', 'en']]) {
    $(id).replaceChildren(...(state.config?.ttsVoiceCatalog[lang] || []).map(v => new Option(v.label, v.id)));
  }
  $('engineSelect').value = current.engineKind;
  $('asrSelect').value = current.voiceLiveAsrModel;
  $('zhVoice').value = current.ttsZhVoice;
  $('enVoice').value = current.ttsEnVoice;
  $('allowedUsers').replaceChildren();
  for (const user of users.filter(u => u.username !== state.user.username)) {
    const label = document.createElement('label'), input = document.createElement('input'), name = document.createElement('span');
    input.type = 'checkbox'; input.value = user.username;
    input.checked = mode !== 'create' && state.room.allowedUsernames?.includes(user.username);
    name.textContent = user.username; label.append(input, name); $('allowedUsers').append(label);
  }
  if (!users.length) $('allowedUsers').textContent = owner ? '暂无其他可邀请账号' : '邀请名单由房主管理';
  for (const id of ['engineSelect', 'asrSelect', 'zhVoice', 'enVoice', 'saveRoom']) $(id).disabled = !owner;
  $('saveRoom').textContent = mode === 'create' ? '创建并进入' : '保存房间设置';
  $('roomError').textContent = '';
  $('roomsDialog').close(); $('roomDialog').showModal();
}
$('newRoom').onclick = () => action(() => openRoom('create'));
$('manageRoom').onclick = () => action(() => openRoom('edit'));
$('roomForm').onsubmit = async event => {
  event.preventDefault(); $('saveRoom').disabled = true; $('roomError').textContent = '';
  const settings = { engineKind: $('engineSelect').value, voiceLiveAsrModel: $('asrSelect').value,
    ttsZhVoice: $('zhVoice').value, ttsEnVoice: $('enVoice').value };
  const allowedUsernames = [...$('allowedUsers').querySelectorAll('input:checked')].map(e => e.value);
  try {
    if (roomMode === 'create') {
      const { room } = await call('room.create', { name: $('roomName').value, settings, allowedUsernames });
      renderState(await call('room.join', { roomId: room.roomId, role: $('roleSelect').value }));
    } else {
      renderState(await call('room.access', { allowedUsernames }));
      renderState(await call('room.save', { settings }));
    }
    $('roomDialog').close(); toast('房间设置已保存');
  } catch (error) { $('roomError').textContent = error.message; }
  finally { $('saveRoom').disabled = false; }
};
$('copyInvite').onclick = () => action(async () => { await call('room.copyInvite'); toast('邀请链接已复制，只有获准账号可以加入'); });
$('reconnectRoom').onclick = () => action(async () => { renderState(await call('room.reconnect')); $('roomDialog').close(); });
$('leaveRoom').onclick = () => action(async () => { renderState(await call('room.leave')); $('roomDialog').close(); toast('已离开房间并停止采集'); });
$('savePreferences').onclick = () => action(async () => { renderState(await call('room.preferences', { role: $('roleSelect').value, selfMonitor: $('selfMonitor').checked })); toast('连麦偏好已保存'); });

async function refreshDevices() {
  const results = await Promise.allSettled([call('mic.devices'), call('process.list')]);
  if (results[0].status === 'fulfilled') {
    const old = $('micDevice').value;
    $('micDevice').replaceChildren(new Option('系统默认麦克风', ''), ...results[0].value.map(d => new Option(d.name, d.id)));
    if ([...$('micDevice').options].some(o => o.value === old)) $('micDevice').value = old;
  }
  if (results[1].status === 'fulfilled') fillProcesses(results[1].value);
}
function fillProcesses(list) {
  processes = list;
  const old = $('processSelect').value;
  const rank = p => /dota2/i.test(p.name) ? 0 : /yy|discord|teams/i.test(p.name) ? 1 : 2;
  processes.sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name));
  $('processSelect').replaceChildren(new Option('选择程序…', ''), ...processes.map(p => new Option(`${p.name} · ${p.pid}`, String(p.pid))));
  if (state.listener?.active && processes.some(p => p.pid === state.listener.pid)) $('processSelect').value = String(state.listener.pid);
  else if (processes.some(p => String(p.pid) === old)) $('processSelect').value = old;
  else { const preferred = processes.find(p => /dota2/i.test(p.name)); if (preferred) $('processSelect').value = String(preferred.pid); }
}
$('refreshMic').onclick = () => action(refreshDevices);
$('refreshProcesses').onclick = () => action(async () => fillProcesses(await call('process.list')));
$('micToggle').onclick = () => action(async () => {
  renderState(await call(state.microphone.active ? 'mic.stop' : 'mic.start', { deviceId: $('micDevice').value }));
}, $('micToggle'));
$('listenToggle').onclick = () => action(async () => {
  if (state.listener.active || state.listener.ready || state.listener.connecting) { renderState(await call('listener.stop')); return; }
  const process = processes.find(p => String(p.pid) === $('processSelect').value);
  if (!process) throw new Error('请先打开目标软件，刷新并选择声音来源');
  renderState(await call('listener.start', { pid: process.pid, startedAt: process.startedAt }));
}, $('listenToggle'));
async function startPtt() {
  if (pttRequested || pttStarting || state.microphone.active || !state.microphone.ready) return;
  pttRequested = true; pttStarting = true; renderControls();
  try {
    renderState(await call('mic.start', { deviceId: $('micDevice').value }));
    if (!pttRequested) renderState(await call('mic.stop'));
  } catch (error) { pttRequested = false; toast(error.message, true); }
  finally { pttStarting = false; renderControls(); }
}
function endPtt() {
  if (!pttRequested) return;
  pttRequested = false;
  if (!pttStarting) action(async () => renderState(await call('mic.stop')));
  renderControls();
}
$('pttButton').addEventListener('pointerdown', event => { if (event.button === 0) { $('pttButton').setPointerCapture(event.pointerId); startPtt(); } });
for (const name of ['pointerup', 'pointercancel', 'lostpointercapture']) $('pttButton').addEventListener(name, endPtt);
$('pttButton').addEventListener('keydown', event => { if ([' ', 'Enter'].includes(event.key) && !event.repeat) { event.preventDefault(); startPtt(); } });
$('pttButton').addEventListener('keyup', event => { if ([' ', 'Enter'].includes(event.key)) { event.preventDefault(); endPtt(); } });
window.addEventListener('blur', endPtt);
$('textForm').onsubmit = event => { event.preventDefault(); action(async () => { await call('text.send', { text: $('textInput').value }); $('textInput').value = ''; }); };

function normalizeOverlay(value) {
  const next = { ...overlayDefaults, ...(value && typeof value === 'object' ? value : {}) };
  for (const [key, min, max] of [['top',0,100],['fontSize',16,48],['opacity',.15,1],['backgroundOpacity',0,.9],['seconds',3,30],['monitor',0,15]]) {
    next[key] = Number.isFinite(Number(next[key])) ? Math.max(min, Math.min(max, Number(next[key]))) : overlayDefaults[key];
  }
  for (const key of ['sourceColor','targetColor']) if (!/^#[0-9a-f]{6}$/i.test(next[key])) next[key] = overlayDefaults[key];
  return next;
}
const overlayBindings = { overlayTop: 'top', overlayFontSize: 'fontSize', overlayOpacity: 'opacity',
  overlayBackground: 'backgroundOpacity', overlaySeconds: 'seconds', overlayMonitor: 'monitor',
  overlaySourceColor: 'sourceColor', overlayTargetColor: 'targetColor', overlayEnabled: 'enabled' };
function renderOverlay() {
  for (const [id, key] of Object.entries(overlayBindings)) {
    const input = $(id); if (input.type === 'checkbox') input.checked = overlay.enabled !== false;
    else input.value = String(overlay[key]);
  }
  $('overlayTopValue').textContent = `${overlay.top}%`;
  $('overlayTop').setAttribute('aria-valuetext', `${overlay.top === 0 ? '顶部' : overlay.top === 100 ? '底部' : overlay.top === 50 ? '居中' : '垂直位置'} ${overlay.top}%`);
  $('overlayFontSizeValue').textContent = `${overlay.fontSize} px`;
  $('overlayOpacityValue').textContent = `${Math.round(overlay.opacity * 100)}%`;
  $('overlayBackgroundValue').textContent = `${Math.round(overlay.backgroundOpacity * 100)}%`;
  $('previewEnglish').style.color = overlay.sourceColor;
  $('previewChinese').style.color = overlay.targetColor;
  $('previewEnglish').style.opacity = $('previewChinese').style.opacity = String(overlay.opacity);
  $('miniCaption').style.background = `rgba(0,0,0,${overlay.backgroundOpacity})`;
  $('miniCaption').style.top = `${8 + (overlay.top / 100) * 95}px`;
  $('miniCaption').style.bottom = 'auto';
  $('miniCaption').style.opacity = overlay.enabled === false ? '.25' : '1';
  for (const button of document.querySelectorAll('[data-position]')) {
    const selected = Number(button.dataset.position) === overlay.top;
    button.classList.toggle('selected', selected); button.setAttribute('aria-pressed', String(selected));
  }
}
function persistOverlay() {
  clearTimeout(overlayTimer);
  overlayTimer = setTimeout(() => action(() => call('overlay.settings', overlay)), 130);
}
for (const [id, key] of Object.entries(overlayBindings)) $(id).addEventListener('input', () => {
  overlay = normalizeOverlay({ ...overlay, [key]: $(id).type === 'checkbox' ? $(id).checked : $(id).value });
  renderOverlay(); persistOverlay();
});
document.querySelectorAll('[data-position]').forEach(button => button.onclick = () => {
  overlay.top = Number(button.dataset.position); renderOverlay(); persistOverlay();
});
async function preview() { clearTimeout(overlayTimer); await call('overlay.settings', overlay); await call('overlay.preview'); }
$('previewButton').onclick = () => action(preview);
$('largePreviewButton').onclick = () => action(preview);
$('trayButton').onclick = () => action(() => call('window.tray'));
$('exportDiagnostics').onclick = () => action(async () => { await call('diagnostics.export'); });
$('openWeb').onclick = () => action(() => call('web.open'));
$('saveServer').onclick = () => action(async () => { renderState(await call('server.set', { address: $('serverAddress').value })); toast('服务地址已更新'); });
$('gsiButton').onclick = () => action(() => call('gsi.download'));

function renderHeroes() {
  const selected = state.roster?.matchHeroes || [];
  $('heroLineup').replaceChildren();
  for (let i = 0; i < 10; i++) {
    const slot = document.createElement('span'); slot.className = 'hero-slot';
    const hero = heroes.find(h => h.name === selected[i]);
    if (hero?.portrait) { const img = document.createElement('img'); img.src = hero.portrait; img.alt = hero.zh; slot.title = hero.zh; slot.append(img); }
    else { slot.textContent = selected[i] ? selected[i].slice(0, 2) : '+'; slot.title = selected[i] || '未选择'; }
    $('heroLineup').append(slot);
  }
}
function drawHeroGrid() {
  const query = $('heroSearch').value.trim().toLowerCase();
  const list = heroes.filter(h => `${h.name} ${h.zh} ${(h.aliases || []).join(' ')}`.toLowerCase().includes(query));
  $('heroGrid').replaceChildren();
  for (const hero of list) {
    const button = document.createElement('button'); button.type = 'button'; button.className = `hero-tile${selectedHeroes.includes(hero.name) ? ' selected' : ''}`;
    button.setAttribute('aria-pressed', String(selectedHeroes.includes(hero.name)));
    const img = document.createElement('img'); img.src = hero.portrait; img.alt = ''; img.loading = 'lazy';
    const name = document.createElement('strong'); name.textContent = hero.zh || hero.name;
    button.title = `${hero.zh} · ${hero.name}`;
    button.append(img, name);
    button.onclick = () => {
      if (selectedHeroes.includes(hero.name)) selectedHeroes = selectedHeroes.filter(name => name !== hero.name);
      else if (selectedHeroes.length < 10) selectedHeroes.push(hero.name);
      else { toast('最多选择 10 位英雄'); return; }
      drawHeroGrid();
    };
    $('heroGrid').append(button);
  }
  $('heroCount').textContent = `${selectedHeroes.length} / 10`;
}
$('editHeroes').onclick = () => { selectedHeroes = [...state.roster?.matchHeroes || []]; $('heroSearch').value = ''; drawHeroGrid(); $('heroesDialog').showModal(); };
$('heroSearch').oninput = drawHeroGrid;
$('clearHeroes').onclick = () => { selectedHeroes = []; drawHeroGrid(); };
$('saveHeroes').onclick = () => action(async () => { await call('room.heroes', { heroes: selectedHeroes }); $('heroesDialog').close(); });

renderOverlay(); renderControls(); renderConversation(); renderHeroes();
try {
  const result = await call('bootstrap');
  renderState(result.state);
  overlay = normalizeOverlay(result.overlay);
  $('overlayMonitor').replaceChildren(...(result.monitors || []).map((name, index) => new Option(`显示器 ${index + 1} · ${name}`, String(index))));
  if (overlay.monitor >= $('overlayMonitor').options.length) overlay.monitor = 0;
  renderOverlay();
  $('shortcutStatus').textContent = Object.values(result.shortcuts || {}).every(Boolean)
    ? '快捷键已注册。只会暂停采集，不会在后台悄悄开启录音。'
    : '部分全局快捷键被其他应用占用，请使用托盘菜单。';
  if (result.initialLink?.includes('room=')) $('inviteInput').value = result.initialLink;
  await refreshDevices();
} catch (error) {
  $('serviceStatus').textContent = '桌面后台未连接';
  $('serviceStatus').parentElement.classList.add('offline');
  toast(error.message, true);
}
// This request reads a packaged resource only; credentials and service requests never use browser fetch.
try { heroes = await (await fetch('heroes.json')).json(); renderHeroes(); }
catch { /* Main audio features remain usable if optional portrait catalog is unavailable. */ }
