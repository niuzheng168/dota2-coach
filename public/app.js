const $ = id => document.getElementById(id);
const roleSelect = $('roleSelect');
const joinBtn = $('joinBtn');
const selfMonitorToggle = $('selfMonitorToggle');
const ttsZhVoiceSelect = $('ttsZhVoiceSelect');
const ttsEnVoiceSelect = $('ttsEnVoiceSelect');
const voiceLiveAsrSelect = $('voiceLiveAsrSelect');
const connBadge = $('connBadge');
const participantsList = $('participantsList');
const engineStatusBar = $('engineStatusBar');
const openMicBtn = $('openMicBtn');
const pttBtn = $('pttBtn');
const micMeterFill = $('micMeterFill');
const audioOutBadge = $('audioOutBadge');
const micStatus = $('micStatus');
const livePartialBanner = $('livePartialBanner');
const transcriptFeed = $('transcriptFeed');
const textSendForm = $('textSendForm');
const textSendInput = $('textSendInput');
const initialUrl = new URL(location.href);
let pendingRoomId = initialUrl.searchParams.get('room');
let pendingInvite = new URLSearchParams(initialUrl.hash.slice(1)).get('invite') || initialUrl.searchParams.get('invite') || '';
let user = null;
let csrfToken = '';
let activeRoom = null;
let activeInvite = '';
let availableUsers = [];
let config = null;
let ws = null;
let engineReady = false;
let heroCatalog = [];
let matchHeroes = [];
const turnCards = new Map();
const ENGINE_LABELS = { cascade: 'Voice Live · Cascaded', 'gpt-realtime': 'Voice Live · Realtime 2.1', 'gpt-live': 'GPT-Live 1' };

function notice(message) {
  $('appNotice').textContent = message;
  $('appNotice').hidden = !message;
}
function formError(id, message = '') {
  $(id).textContent = message;
  $(id).hidden = !message;
}
async function api(url, options = {}) {
  const method = options.method || 'GET';
  const headers = { ...options.headers };
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';
  if (method !== 'GET' && csrfToken) headers['X-CSRF-Token'] = csrfToken;
  const response = await fetch(url, {
    method, headers, credentials: 'same-origin',
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  });
  const data = await response.json();
  if (!response.ok) {
    const error = new Error(data.error || '请求失败');
    error.status = response.status;
    if (response.status === 401 && url !== '/api/auth/login') showLogin(error.message);
    throw error;
  }
  return data;
}

function stopRoomMedia() {
  openMicActive = false;
  pttPressed = false;
  pttRequested = false;
  stopMicPipeline();
  engineReady = false;
  updateMicButtons();
  const context = audioCtx;
  audioCtx = null;
  if (context && context.state !== 'closed') context.close().catch(() => {});
  nextPlayTime = 0;
  stopVisionWatch();
  openMicBtn.textContent = 'Start Open Mic (免按键连麦)';
  openMicBtn.className = 'btn-radiant';
  pttBtn.textContent = 'Hold Push-to-Talk (按住说话 / Space)';
  pttBtn.className = '';
  micMeterFill.style.width = '0%';
  audioOutBadge.textContent = 'Speaker Ready';
  setMicStatus('麦克风未开启');
}

function leaveRoom(clearUrl = true) {
  const previous = ws;
  ws = null;
  previous?.close();
  stopRoomMedia();
  activeRoom = null;
  activeInvite = '';
  matchHeroes = [];
  turnCards.clear();
  transcriptFeed.replaceChildren();
  livePartialBanner.textContent = '等待发言…';
  $('roomView').hidden = true;
  for (const dialog of document.querySelectorAll('dialog[open]')) dialog.close();
  if (clearUrl) {
    pendingRoomId = null;
    pendingInvite = '';
    history.replaceState(null, '', location.pathname);
  }
}

function showLogin(message = '') {
  leaveRoom(false);
  user = null;
  csrfToken = '';
  $('loginView').hidden = false;
  $('lobbyView').hidden = true;
  $('accountControls').hidden = true;
  $('passwordInput').value = '';
  formError('loginError', message);
}

async function showLobby() {
  $('loginView').hidden = true;
  $('roomView').hidden = true;
  $('lobbyView').hidden = false;
  $('createRoomBtn').hidden = !user?.canCreateRooms;
  $('invitationNotice').hidden = !pendingRoomId;
  $('welcomeName').textContent = user.canCreateRooms ? '我的房间' : `欢迎，${user.username}`;
  $('lobbyDescription').textContent = user.canCreateRooms
    ? '创建语音室，选好同传方案，再邀请获准加入的账号。'
    : '请使用房主发来的邀请链接加入。你的账号不能创建或浏览他人的房间。';
  try {
    const data = await api('/api/rooms');
    $('roomList').replaceChildren();
    for (const room of data.rooms) {
      const card = document.createElement('div');
      card.className = 'room-card';
      const label = document.createElement('div');
      const title = document.createElement('strong');
      title.textContent = room.name;
      const subtitle = document.createElement('p');
      subtitle.className = 'muted small';
      subtitle.textContent = `${ENGINE_LABELS[room.settings.engineKind]} · ${room.allowedUsernames.length} 个允许加入的账号`;
      label.append(title, subtitle);
      const button = document.createElement('button');
      button.textContent = '进入房间';
      button.addEventListener('click', () => enterRoom(room.roomId).catch(error => notice(error.message)));
      card.append(label, button);
      $('roomList').append(card);
    }
    if (!data.rooms.length && user.canCreateRooms) $('roomList').textContent = '还没有房间，点击右上方创建。';
  } catch (error) {
    if (error.status === 401) showLogin(error.message);
    else notice(error.message);
  }
}

function renderRoomSettings() {
  const settings = activeRoom.settings;
  const voiceLabel = (language, id) => config.ttsVoiceCatalog[language].find(voice => voice.id === id)?.label || id;
  const rows = settings.engineKind === 'gpt-live'
    ? [['引擎', ENGINE_LABELS[settings.engineKind]], ['音色与转录', 'GPT-Live 内置']]
    : [['引擎', ENGINE_LABELS[settings.engineKind]], ['中文', voiceLabel('zh', settings.ttsZhVoice)], ['英文', voiceLabel('en', settings.ttsEnVoice)], ['ASR', settings.voiceLiveAsrModel]];
  $('roomSettingsSummary').innerHTML = rows.map(([label, value]) => `<dt>${escapeHtml(label)}</dt><dd>${escapeHtml(value)}</dd>`).join('');
}

async function enterRoom(roomId, invitation = '') {
  const account = user;
  const data = await api(`/api/rooms/${encodeURIComponent(roomId)}`, { headers: invitation ? { 'X-Room-Invite': invitation } : {} });
  if (!account || user !== account) return;
  leaveRoom(false);
  activeRoom = data.room;
  activeInvite = invitation;
  pendingRoomId = roomId;
  pendingInvite = invitation;
  notice('');
  $('loginView').hidden = true;
  $('lobbyView').hidden = true;
  $('roomView').hidden = false;
  $('roomTitle').textContent = activeRoom.name;
  $('roomOwner').textContent = `房主：${activeRoom.ownerUsername}`;
  document.querySelectorAll('[data-owner-only]').forEach(element => { element.hidden = !activeRoom.isOwner; });
  renderRoomSettings();
  renderSelectedMatchHeroes();
  const url = new URL(location.pathname, location.origin);
  url.searchParams.set('room', roomId);
  url.searchParams.set('role', roleSelect.value);
  if (invitation) url.hash = new URLSearchParams({ invite: invitation }).toString();
  history.replaceState(null, '', url);
  connectRoom();
}

function invitationUrl() {
  if (!activeRoom?.isOwner) return '';
  const url = new URL(location.pathname, location.origin);
  url.searchParams.set('room', activeRoom.roomId);
  url.searchParams.set('role', 'coach');
  url.hash = new URLSearchParams({ invite: activeRoom.inviteToken }).toString();
  return url.toString();
}

function renderAllowedUsers(containerId, allowed = []) {
  const container = $(containerId);
  container.replaceChildren();
  for (const candidate of availableUsers.filter(candidate => candidate.username !== user.username)) {
    const label = document.createElement('label');
    label.className = 'toggle-row';
    const span = document.createElement('span');
    span.textContent = candidate.username;
    const input = document.createElement('input');
    input.type = 'checkbox'; input.value = candidate.username; input.checked = allowed.includes(candidate.username);
    label.append(span, input);
    container.append(label);
  }
  if (!container.childElementCount) container.textContent = '当前没有其他账号。';
}
const allowedSelection = id => [...$(id).querySelectorAll('input:checked')].map(input => input.value);

async function copyInvite() {
  const value = invitationUrl();
  if (!value) return;
  try { await navigator.clipboard.writeText(value); notice('邀请链接已复制；只有允许名单内的账号能够加入。'); }
  catch {
    $('inviteLinkInput').value = value;
    if (!$('accessDialog').open) await openAccessDialog();
    $('inviteLinkInput').focus(); $('inviteLinkInput').select();
    notice('浏览器未允许自动复制，请复制已选中的邀请链接。');
  }
}

async function openAccessDialog() {
  if (!activeRoom?.isOwner) return;
  const roomId = activeRoom.roomId;
  const [detail, users] = await Promise.all([api(`/api/rooms/${activeRoom.roomId}`), api('/api/users')]);
  if (activeRoom?.roomId !== roomId) return;
  activeRoom = detail.room;
  availableUsers = users.users;
  renderAllowedUsers('accessAllowedUsers', activeRoom.allowedUsernames);
  $('inviteLinkInput').value = invitationUrl();
  formError('accessError');
  $('accessDialog').showModal();
}

async function initializeUser(session) {
  user = session.user;
  csrfToken = session.csrfToken;
  $('currentUser').textContent = user.username;
  $('accountControls').hidden = false;
  $('loginView').hidden = true;
  const [cfg, dict, accounts] = await Promise.all([
    api('/api/config'), api('/api/dictionary'),
    user.canCreateRooms ? api('/api/users') : Promise.resolve({ users: [] }),
  ]);
  config = cfg;
  availableUsers = accounts.users;
  heroCatalog = dict.heroes || [];
  for (const [select, language] of [[ttsZhVoiceSelect, 'zh'], [ttsEnVoiceSelect, 'en']]) {
    select.replaceChildren(...cfg.ttsVoiceCatalog[language].map(voice => new Option(voice.label, voice.id)));
  }
  $('heroPickerDatalist').replaceChildren(...heroCatalog.map(hero => {
    const option = new Option(`${hero.zh} ${(hero.zhSlang || []).join('/')} ${(hero.enAliases || []).join('/')}`, hero.en);
    return option;
  }));
  const requestedRole = initialUrl.searchParams.get('role');
  roleSelect.value = ['coach', 'student'].includes(requestedRole) ? requestedRole : (user.canCreateRooms ? 'student' : 'coach');
  await showLobby();
  if (pendingRoomId) {
    try { await enterRoom(pendingRoomId, pendingInvite); }
    catch (error) { notice(error.message); }
  }
}

$('loginForm').addEventListener('submit', async event => {
  event.preventDefault();
  formError('loginError');
  $('loginSubmitBtn').disabled = true;
  try {
    const session = await api('/api/auth/login', { method: 'POST', body: { username: $('usernameInput').value.trim(), password: $('passwordInput').value } });
    $('passwordInput').value = '';
    await initializeUser(session);
  } catch (error) { showLogin(error.message); }
  finally { $('loginSubmitBtn').disabled = false; }
});
$('logoutBtn').addEventListener('click', async () => {
  try { await api('/api/auth/logout', { method: 'POST' }); showLogin(); notice('已退出登录'); }
  catch (error) { if (error.status === 401) showLogin(); else notice(error.message); }
});
$('leaveRoomBtn').addEventListener('click', () => { leaveRoom(); showLobby(); });
$('retryInviteBtn').addEventListener('click', () => enterRoom(pendingRoomId, pendingInvite).catch(error => notice(error.message)));
$('copyCoachLinkBtn').addEventListener('click', () => copyInvite().catch(error => notice(error.message)));
$('copyDialogInviteBtn').addEventListener('click', () => copyInvite().catch(error => notice(error.message)));
$('manageAccessBtn').addEventListener('click', () => openAccessDialog().catch(error => notice(error.message)));
document.querySelectorAll('[data-close-dialog]').forEach(button => button.addEventListener('click', () => $(button.dataset.closeDialog).close()));
$('createRoomBtn').addEventListener('click', async () => {
  if (!user?.canCreateRooms) return;
  try {
    availableUsers = (await api('/api/users')).users;
    $('createRoomForm').reset();
    $('createEngineSelect').value = config.defaults.engineKind;
    ttsZhVoiceSelect.value = config.defaults.ttsZhVoice;
    ttsEnVoiceSelect.value = config.defaults.ttsEnVoice;
    voiceLiveAsrSelect.value = config.defaults.voiceLiveAsrModel;
    $('createVoiceOptions').hidden = false;
    $('liveEngineNote').hidden = true;
    renderAllowedUsers('createAllowedUsers');
    formError('createRoomError');
    $('createRoomDialog').showModal();
  } catch (error) { notice(error.message); }
});
$('createEngineSelect').addEventListener('change', () => {
  const live = $('createEngineSelect').value === 'gpt-live';
  $('createVoiceOptions').hidden = live;
  $('liveEngineNote').hidden = !live;
});
$('createRoomForm').addEventListener('submit', async event => {
  event.preventDefault();
  $('createRoomSubmitBtn').disabled = true;
  formError('createRoomError');
  try {
    const data = await api('/api/rooms', { method: 'POST', body: {
      name: $('newRoomName').value.trim(), allowedUsernames: allowedSelection('createAllowedUsers'),
      settings: { engineKind: $('createEngineSelect').value, ttsZhVoice: ttsZhVoiceSelect.value, ttsEnVoice: ttsEnVoiceSelect.value, voiceLiveAsrModel: voiceLiveAsrSelect.value },
    } });
    $('createRoomDialog').close();
    roleSelect.value = 'student';
    await enterRoom(data.room.roomId);
  } catch (error) { formError('createRoomError', error.message); notice(error.message); }
  finally { $('createRoomSubmitBtn').disabled = false; }
});
$('accessForm').addEventListener('submit', async event => {
  event.preventDefault();
  if (!activeRoom?.isOwner) return;
  $('saveAccessBtn').disabled = true;
  try {
    const data = await api(`/api/rooms/${activeRoom.roomId}/access`, { method: 'PATCH', body: { allowedUsernames: allowedSelection('accessAllowedUsers') } });
    activeRoom = data.room;
    $('accessDialog').close();
    notice('允许名单已保存，取消授权的用户已断开连接。');
  } catch (error) { formError('accessError', error.message); }
  finally { $('saveAccessBtn').disabled = false; }
});
roleSelect.addEventListener('change', () => {
  if (activeRoom) enterRoom(activeRoom.roomId, activeInvite).catch(error => notice(error.message));
});

// WebAudio playback & 24kHz PCM16 capture state
let audioCtx = null;
let nextPlayTime = 0;
let micStream = null;
let workletNode = null;
let micSource = null;
let micSink = null;
let micInitPromise = null;
let micGeneration = 0;
let pttRequested = false;
let micHealthTimer = null;
let micOpenedAt = 0;
let micLastFrameAt = 0;
let micLastLoudAt = 0;
let micLastAckAt = 0;
let lastCaptureStatus = '';
const workletRegistrations = new WeakMap();
let openMicActive = false;
let pttPressed = false;

function ensureAudioContext() {
  if (!audioCtx) {
    audioCtx = new (window.AudioContext || window.webkitAudioContext)();
  }
  if (audioCtx.state === 'suspended') {
    audioCtx.resume().catch(() => {});
  }
  return audioCtx;
}

function playPcm24kMono(int16Bytes) {
  const ctx = ensureAudioContext();
  const sampleCount = Math.floor(int16Bytes.byteLength / 2);
  if (sampleCount <= 0) return;

  const view = new DataView(int16Bytes.buffer, int16Bytes.byteOffset, sampleCount * 2);
  const float32 = new Float32Array(sampleCount);
  for (let i = 0; i < sampleCount; i++) {
    float32[i] = view.getInt16(i * 2, true) / 32768;
  }

  const audioBuffer = ctx.createBuffer(1, sampleCount, 24000);
  audioBuffer.getChannelData(0).set(float32);

  const src = ctx.createBufferSource();
  src.buffer = audioBuffer;
  src.connect(ctx.destination);

  const now = ctx.currentTime;
  if (nextPlayTime < now + 0.02) {
    nextPlayTime = now + 0.03;
  }
  src.start(nextPlayTime);
  nextPlayTime += audioBuffer.duration;

  audioOutBadge.textContent = 'Playing Audio...';
  audioOutBadge.className = 'badge green';
  setTimeout(() => {
    if (ctx.currentTime >= nextPlayTime - 0.05) {
      audioOutBadge.textContent = 'Speaker Ready';
      audioOutBadge.className = 'badge';
    }
  }, Math.ceil(audioBuffer.duration * 1000) + 80);
}

const WORKLET_CODE = `
class Pcm24kCaptureProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.buffer = new Int16Array(960); // 40ms @ 24kHz = 960 samples = 1920 bytes
    this.writeIdx = 0;
    this.resamplePos = 0;
  }
  process(inputs) {
    const ch = inputs[0] && inputs[0][0];
    if (!ch || ch.length === 0) return true;
    const ratio = sampleRate / 24000;
    let sumSq = 0;
    for (let i = 0; i < ch.length; i++) sumSq += ch[i] * ch[i];
    const rms = Math.sqrt(sumSq / ch.length);

    while (this.resamplePos < ch.length) {
      const idx = Math.min(ch.length - 1, Math.floor(this.resamplePos));
      const s = Math.max(-1, Math.min(1, ch[idx]));
      this.buffer[this.writeIdx++] = s < 0 ? s * 0x8000 : s * 0x7fff;
      if (this.writeIdx >= this.buffer.length) {
        const out = this.buffer.slice();
        this.port.postMessage({ pcm: out.buffer, rms }, [out.buffer]);
        this.writeIdx = 0;
      }
      this.resamplePos += ratio;
    }
    this.resamplePos -= ch.length;
    return true;
  }
}
registerProcessor('pcm24k-capture', Pcm24kCaptureProcessor);
`;

function setMicStatus(text, code = '') {
  micStatus.textContent = text;
  if (code && code !== lastCaptureStatus && ws?.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify({ type: 'client.audio.status', status: code }));
  }
  lastCaptureStatus = code;
}

function updateMicButtons() {
  openMicBtn.disabled = !engineReady;
  pttBtn.disabled = !engineReady;
}

function stopMicPipeline() {
  micGeneration++;
  micInitPromise = null;
  if (micHealthTimer) clearInterval(micHealthTimer);
  micHealthTimer = null;
  if (workletNode) {
    workletNode.port.onmessage = null;
    workletNode.port.close();
    workletNode.onprocessorerror = null;
  }
  for (const node of [micSource, workletNode, micSink]) {
    try { node?.disconnect(); } catch { /* Already detached. */ }
  }
  micStream?.getTracks().forEach(track => track.stop());
  micStream = micSource = workletNode = micSink = null;
  micMeterFill.style.width = '0%';
}

function microphoneFailed(message) {
  openMicActive = pttPressed = pttRequested = false;
  stopMicPipeline();
  openMicBtn.textContent = 'Start Open Mic (免按键连麦)';
  openMicBtn.className = 'btn-radiant';
  pttBtn.textContent = 'Hold Push-to-Talk (按住说话 / Space)';
  pttBtn.className = '';
  setMicStatus(message, 'capture_error');
  notice(message);
}

function startMicHealth() {
  if (micHealthTimer) return;
  micOpenedAt = Date.now();
  micLastFrameAt = micLastLoudAt = micLastAckAt = 0;
  setMicStatus(`正在采集：${micStream?.getAudioTracks()[0]?.label || '默认麦克风'}`, 'capturing');
  micHealthTimer = setInterval(() => {
    if (!openMicActive && !pttPressed) return;
    const now = Date.now();
    if (audioCtx?.state !== 'running') {
      setMicStatus('浏览器暂停了音频，请关闭再重新开启麦克风', 'context_suspended');
    } else if (now - micOpenedAt > 3000 && now - micLastFrameAt > 2500) {
      setMicStatus('麦克风没有输出音频，请检查权限或重新开启麦克风', 'capture_stalled');
    } else if (micStream?.getAudioTracks()[0]?.muted || (now - micOpenedAt > 5000 && now - micLastLoudAt > 5000)) {
      setMicStatus('已采集但没有检测到声音，请检查输入设备或系统静音', 'input_silent');
    } else if (now - micOpenedAt > 3000 && now - micLastAckAt > 3000) {
      setMicStatus('已采集，但服务器尚未确认音频，请重新连接', 'upload_stalled');
    } else if (micLastAckAt) {
      setMicStatus('服务器已收到语音，等待转录 / 翻译', 'capturing');
    }
  }, 1000);
}

async function ensureMicPipeline() {
  const requestedGeneration = micGeneration;
  const ctx = ensureAudioContext();
  await ctx.resume();
  if (requestedGeneration !== micGeneration) throw new DOMException('已取消麦克风请求', 'AbortError');
  if (ctx.state !== 'running') throw new Error('浏览器音频未运行，请重新点击开启麦克风');
  if (workletNode && micStream?.getAudioTracks()[0]?.readyState === 'live') return;
  if (micInitPromise) return micInitPromise;
  const generation = micGeneration;
  const roomId = activeRoom?.roomId;
  const connection = ws;
  const isCurrent = () => generation === micGeneration && activeRoom?.roomId === roomId && ws === connection && engineReady && ctx.state !== 'closed';
  const pending = (async () => {
    setMicStatus('正在请求麦克风权限…');
    let stream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: {
        channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true,
      } });
      if (!isCurrent()) throw new DOMException('已取消麦克风请求', 'AbortError');
      micStream = stream;
      if (!workletRegistrations.has(ctx)) {
        const registration = (async () => {
          const blobUrl = URL.createObjectURL(new Blob([WORKLET_CODE], { type: 'application/javascript' }));
          try { await ctx.audioWorklet.addModule(blobUrl); }
          catch (error) { workletRegistrations.delete(ctx); throw error; }
          finally { URL.revokeObjectURL(blobUrl); }
        })();
        workletRegistrations.set(ctx, registration);
      }
      await workletRegistrations.get(ctx);
      if (!isCurrent()) throw new DOMException('已取消麦克风请求', 'AbortError');
      micSource = ctx.createMediaStreamSource(stream);
      workletNode = new AudioWorkletNode(ctx, 'pcm24k-capture');
      // Keep the processing graph pulled by the destination without playing microphone audio locally.
      micSink = ctx.createGain();
      micSink.gain.value = 0;
      workletNode.port.onmessage = ({ data: { pcm, rms } }) => {
        if (!isCurrent()) return;
        micLastFrameAt = Date.now();
        if (rms > 0.003) micLastLoudAt = micLastFrameAt;
        micMeterFill.style.width = openMicActive || pttPressed ? `${Math.min(100, Math.round(rms * 360))}%` : '0%';
        if ((openMicActive || pttPressed) && ws?.readyState === WebSocket.OPEN && engineReady) ws.send(pcm);
      };
      workletNode.onprocessorerror = () => microphoneFailed('音频采集处理器中断，请重新开启麦克风');
      stream.getAudioTracks()[0]?.addEventListener('ended', () => {
        if (isCurrent()) microphoneFailed('麦克风设备已断开，请重新连接输入设备');
      });
      micSource.connect(workletNode);
      workletNode.connect(micSink);
      micSink.connect(ctx.destination);
    } catch (error) {
      stream?.getTracks().forEach(track => track.stop());
      if (generation === micGeneration) stopMicPipeline();
      throw error;
    }
  })();
  micInitPromise = pending;
  try { await pending; }
  finally { if (micInitPromise === pending) micInitPromise = null; }
}

function connectRoom() {
  if (!user || !activeRoom) return;
  const previous = ws;
  ws = null;
  previous?.close();
  engineReady = false;
  updateMicButtons();
  setMicStatus('等待语音引擎就绪后才能开麦…');
  const query = new URLSearchParams({ room: activeRoom.roomId, role: roleSelect.value, selfMonitor: selfMonitorToggle.checked ? '1' : '0' });
  if (activeInvite) query.set('invite', activeInvite);
  const socket = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws?${query}`);
  ws = socket;
  socket.binaryType = 'arraybuffer';
  connBadge.textContent = 'Connecting...';
  connBadge.className = 'badge gold';
  engineStatusBar.textContent = '正在连接语音服务…';
  socket.onopen = () => {
    if (ws !== socket) return;
    connBadge.textContent = 'Connected';
    connBadge.className = 'badge green';
  };
  socket.onclose = event => {
    if (ws !== socket) return;
    ws = null;
    stopRoomMedia();
    connBadge.textContent = 'Disconnected';
    connBadge.className = 'badge';
    if (event.code === 4001) showLogin('登录已失效，请重新登录');
    else if (event.code === 4003) {
      leaveRoom(false);
      showLobby();
      notice('房主已取消你的加入权限。');
    } else engineStatusBar.textContent = '连接已断开，请点击重新连接。';
  };
  socket.onerror = () => {
    if (ws === socket) notice('无法加入房间，请确认登录、邀请链接和房主授权仍有效。');
  };
  socket.onmessage = event => {
    if (ws !== socket || !activeRoom) return;
    if (event.data instanceof ArrayBuffer) {
      if (event.data.byteLength > 2) playPcm24kMono(new Uint8Array(event.data, 2));
      return;
    }
    handleServerMessage(JSON.parse(event.data));
  };
}

function getOrCreateTurnCard(turnId, role, speaker) {
  let entry = turnCards.get(turnId);
  if (!entry) {
    const card = document.createElement('div');
    card.className = `turn-card ${role}`;

    const meta = document.createElement('div');
    meta.className = 'turn-meta';
    const who = document.createElement('span');
    who.innerHTML = `<strong>${escapeHtml(speaker)}</strong> · ${
      role === 'coach' ? 'EN → ZH' : 'ZH/EN → EN'
    }`;
    const badges = document.createElement('span');
    meta.append(who, badges);

    const srcDiv = document.createElement('div');
    srcDiv.className = 'turn-source';
    const tgtDiv = document.createElement('div');
    tgtDiv.className = 'turn-target';
    tgtDiv.textContent = '...';

    card.append(meta, srcDiv, tgtDiv);
    transcriptFeed.appendChild(card);
    transcriptFeed.scrollTop = transcriptFeed.scrollHeight;

    entry = { card, badges, srcDiv, tgtDiv };
    turnCards.set(turnId, entry);
  }
  return entry;
}

function handleServerMessage(msg) {
  switch (msg.type) {
    case 'room.state': {
      if (Array.isArray(msg.matchHeroes)) {
        matchHeroes = msg.matchHeroes;
        renderSelectedMatchHeroes();
      }
      if (msg.gsiState) {
        renderGsiStatus(msg.gsiState);
      }
      renderParticipants(msg.participants || []);
      break;
    }

    case 'gsi.status': {
      renderGsiStatus(msg.gsiState);
      if (msg.resetHeroes) {
        const visionLine = document.getElementById('visionStatusLine');
        if (visionLine) {
          visionLine.textContent = `🔄 GSI 检测到新对局 (${msg.gsiState?.matchId || 'BP'})，已自动清空上一局英雄！`;
        }
      }
      break;
    }

    case 'vision.status': {
      const visionLine = document.getElementById('visionStatusLine');
      if (visionLine) {
        if (msg.phase === 'menu_or_other') {
          visionLine.textContent = `👁️ 顶栏视觉 (${msg.latencyMs}ms): 当前不在 BP / 局内界面，后台待命监听中...`;
        } else {
          const phaseZh = msg.phase === 'draft' ? 'BP 选人' : '局内对局';
          visionLine.textContent = `✅ 顶栏视觉 [${phaseZh} · ${msg.latencyMs}ms]: 天辉 ${msg.radiant.length} / 夜魇 ${msg.dire.length} (共锁 ${msg.heroes.length}/10)`;
        }
      }
      break;
    }

    case 'engine.state': {
      engineReady = msg.state === 'ready';
      updateMicButtons();
      if (!engineReady) {
        openMicActive = pttPressed = pttRequested = false;
        stopMicPipeline();
        openMicBtn.textContent = 'Start Open Mic (免按键连麦)';
        openMicBtn.className = 'btn-radiant';
        pttBtn.textContent = 'Hold Push-to-Talk (按住说话 / Space)';
        pttBtn.className = '';
      }
      setMicStatus(engineReady
        ? '语音已就绪，点击开启麦克风或按住说话'
        : msg.state === 'failed' ? '语音连接失败，请点击重新连接' : '语音服务连接中，暂不能开麦…');
      break;
    }

    case 'audio.input.status': {
      if (msg.engineReady && msg.forwardedFrames > 0) micLastAckAt = Date.now();
      else setMicStatus('服务器语音引擎尚未就绪，请等待连接完成');
      break;
    }

    case 'engine.status': {
      if (msg.speaker === user?.username && (!msg.role || msg.role === roleSelect.value)) {
        engineStatusBar.textContent = msg.status;
      }
      break;
    }

    case 'room.error': {
      notice(msg.message);
      break;
    }

    case 'engine.error': {
      if (msg.speaker === user?.username && (!msg.role || msg.role === roleSelect.value)) engineStatusBar.textContent = `⚠️ ${msg.message}`;
      break;
    }

    case 'subtitle.source.partial': {
      livePartialBanner.innerHTML = `🎙️ <strong>${escapeHtml(msg.speaker)}:</strong> ${escapeHtml(msg.text)}`;
      break;
    }

    case 'subtitle.source.final': {
      livePartialBanner.innerHTML = `🎙️ <strong>${escapeHtml(msg.speaker)}:</strong> ${escapeHtml(msg.text)}`;
      const entry = getOrCreateTurnCard(msg.turnId, msg.role, msg.speaker);
      entry.srcDiv.textContent = `原话: ${msg.text}`;
      break;
    }

    case 'subtitle.target.partial':
    case 'subtitle.target.final': {
      const entry = getOrCreateTurnCard(msg.turnId, msg.role, msg.speaker);
      entry.tgtDiv.textContent = msg.text;
      transcriptFeed.scrollTop = transcriptFeed.scrollHeight;
      break;
    }

    case 'turn.metrics': {
      const m = msg.metrics;
      const entry = getOrCreateTurnCard(m.turnId, m.role, msg.speaker);
      if (m.sourceText && !entry.srcDiv.textContent) {
        entry.srcDiv.textContent = `原话: ${m.sourceText}`;
      }
      if (m.targetText) {
        entry.tgtDiv.textContent = m.targetText;
      }

      const pills = [`<span class="badge green">TTFA ${m.speechEndToFirstAudioMs}ms</span>`];
      const engineBadge = m.voiceLiveModel ? `VL:${m.voiceLiveModel}` : m.engine;
      pills.push(`<span class="badge">${escapeHtml(engineBadge)}</span>`);
      if (m.ttsVoice) {
        const shortVoice = m.ttsVoice.replace(/^(zh-CN-|en-US-)/, '').replace(/LatestNeural$/, '');
        const modeTag = m.ttsStreamMode ? ` · ${m.ttsStreamMode}` : '';
        pills.push(`<span class="badge purple">${escapeHtml(shortVoice + modeTag)}</span>`);
      }
      if (m.voiceLiveAsrModel) {
        pills.push(`<span class="badge">${escapeHtml(m.voiceLiveAsrModel)}</span>`);
      }
      if (m.passThrough !== 'none') {
        pills.push(`<span class="badge gold">EN Pass-Through (${escapeHtml(m.passThrough)})</span>`);
      }
      if (m.asrMs !== undefined || m.llmFirstTokenMs !== undefined) {
        const stages = [];
        if (m.asrMs !== undefined) stages.push(`ASR ${m.asrMs}ms`);
        if (m.llmFirstTokenMs !== undefined) stages.push(`1stTok ${m.llmFirstTokenMs}ms`);
        if (m.ttsFirstChunkMs !== undefined) stages.push(`TTS ${m.ttsFirstChunkMs}ms`);
        pills.push(`<span class="badge">${stages.join(' · ')}</span>`);
      }
      entry.badges.innerHTML = pills.join(' ');
      break;
    }
  }
}

function renderParticipants(list) {
  if (!list.length) {
    participantsList.innerHTML = `<div class="participant-item" style="color: var(--text-muted)">Empty room</div>`;
    return;
  }
  participantsList.innerHTML = list
    .map(
      (p) => `
      <div class="participant-item">
        <span><strong>${escapeHtml(p.name)}</strong></span>
        <span class="badge ${p.role === 'coach' ? 'gold' : 'green'}">${
          p.role === 'coach' ? 'Coach (EN)' : 'Student (ZH/EN)'
        }</span>
      </div>`,
    )
    .join('');
}

function escapeHtml(str) {
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

joinBtn.addEventListener('click', () => {
  if (activeRoom) enterRoom(activeRoom.roomId, activeInvite).catch(error => {
    if (error.status === 401) showLogin(error.message);
    else notice(error.message);
  });
});

selfMonitorToggle.addEventListener('change', () => {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(
      JSON.stringify({
        type: 'client.set_self_monitor',
        selfMonitor: selfMonitorToggle.checked,
      }),
    );
  }
});

openMicBtn.addEventListener('click', async () => {
  if (openMicActive) {
    openMicActive = false;
    openMicBtn.textContent = 'Start Open Mic (免按键连麦)';
    openMicBtn.className = 'btn-radiant';
    if (!pttPressed) stopMicPipeline();
    if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'speech.end' }));
    setMicStatus('麦克风已关闭', 'stopped');
    return;
  }
  if (!activeRoom || ws?.readyState !== WebSocket.OPEN || !engineReady) { notice('请等待自己的语音引擎就绪'); return; }
  try {
    await ensureMicPipeline();
    if (!engineReady || !micStream) return;
    openMicActive = true;
    openMicBtn.textContent = 'Stop Open Mic (正在连麦...)';
    openMicBtn.className = 'btn-dire';
    startMicHealth();
  } catch (error) { if (error.name !== 'AbortError') microphoneFailed(`麦克风不可用：${error.message}`); }
});

async function startPtt() {
  if (pttRequested || pttPressed) return;
  if (!activeRoom || ws?.readyState !== WebSocket.OPEN || !engineReady) { notice('请等待自己的语音引擎就绪'); return; }
  pttRequested = true;
  try {
    await ensureMicPipeline();
    if (!pttRequested || !engineReady || !micStream) return;
    pttPressed = true;
    pttBtn.className = 'btn-dire';
    pttBtn.textContent = 'Speaking... Release to Commit';
    startMicHealth();
  } catch (error) { if (error.name !== 'AbortError') microphoneFailed(`麦克风不可用：${error.message}`); }
}

function stopPtt() {
  const wasPressed = pttPressed;
  pttPressed = pttRequested = false;
  pttBtn.className = '';
  pttBtn.textContent = 'Hold Push-to-Talk (按住说话 / Space)';
  if (!openMicActive) {
    stopMicPipeline();
    setMicStatus('麦克风未开启，点击开麦或按住说话', 'stopped');
  }
  if (wasPressed && ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'speech.end' }));
}

pttBtn.addEventListener('mousedown', startPtt);
window.addEventListener('mouseup', () => { if (pttRequested || pttPressed) stopPtt(); });
pttBtn.addEventListener('touchstart', event => { event.preventDefault(); startPtt(); });
window.addEventListener('touchend', () => { if (pttRequested || pttPressed) stopPtt(); });
window.addEventListener('blur', () => { if (pttRequested || pttPressed) stopPtt(); });
window.addEventListener('keydown', event => {
  const target = document.activeElement;
  const editing = target?.closest('input, textarea, select, [contenteditable=true]');
  if (!$('roomView').hidden && !document.querySelector('dialog[open]') && event.code === 'Space' && !event.repeat && !editing && (!target?.closest('button') || target === pttBtn)) {
    event.preventDefault(); startPtt();
  }
});
window.addEventListener('keyup', event => {
  if (event.code === 'Space' && (pttRequested || pttPressed)) { event.preventDefault(); stopPtt(); }
});

textSendForm.addEventListener('submit', (e) => {
  e.preventDefault();
  const txt = textSendInput.value.trim();
  if (!txt) return;
  if (!activeRoom || !ws || ws.readyState !== WebSocket.OPEN) { notice('请先连接房间'); return; }
  ensureAudioContext();
  const sendNow = () => {
    ws.send(JSON.stringify({ type: 'text.send', text: txt }));
    textSendInput.value = '';
  };
  if (ws.readyState === WebSocket.OPEN) sendNow();
  else ws.addEventListener('open', sendNow, { once: true });
});

function renderSelectedMatchHeroes() {
  const countEl = document.getElementById('matchHeroesCount');
  const clearBtn = document.getElementById('clearHeroesBtn');
  const container = document.getElementById('selectedHeroTags');
  if (!countEl || !container) return;
  countEl.textContent = `${matchHeroes.length} / 10`;
  if (clearBtn) {
    clearBtn.style.display = activeRoom?.isOwner && matchHeroes.length > 0 ? 'inline-block' : 'none';
  }
  if (!matchHeroes.length) {
    container.innerHTML = `<span style="font-size:11px;color:var(--text-muted)">未限定（已内置全部 127 英雄名与黑话；BP 选出英雄后点选可动态追加全技能）</span>`;
    return;
  }
  container.innerHTML = matchHeroes
    .map((keyOrName) => {
      const hero =
        heroCatalog.find(
          (h) =>
            h.en.toLowerCase() === keyOrName.toLowerCase() ||
            h.zh === keyOrName ||
            h.key === keyOrName,
        ) || { en: keyOrName, zh: '' };
      const label = hero.zh ? `${hero.zh} (${hero.en})` : hero.en;
      return `<span class="hero-tag">${escapeHtml(label)} ${activeRoom?.isOwner ? `<button type="button" data-remove-hero="${escapeHtml(keyOrName)}">×</button>` : ''}</span>`;
    })
    .join('');

  container.querySelectorAll('[data-remove-hero]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const target = btn.getAttribute('data-remove-hero');
      matchHeroes = matchHeroes.filter((h) => h !== target);
      renderSelectedMatchHeroes();
      syncMatchHeroesToRoom();
    });
  });
}

function syncMatchHeroesToRoom() {
  if (!activeRoom?.isOwner) return;
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify({ type: 'room.set_match_heroes', matchHeroes }));
  }
}

const clearHeroesBtn = document.getElementById('clearHeroesBtn');
if (clearHeroesBtn) {
  clearHeroesBtn.addEventListener('click', () => {
    if (!activeRoom?.isOwner || !matchHeroes.length) return;
    matchHeroes = [];
    renderSelectedMatchHeroes();
    syncMatchHeroesToRoom();
  });
}

const heroPickerInput = document.getElementById('heroPickerInput');
const addHeroBtn = document.getElementById('addHeroBtn');
if (addHeroBtn && heroPickerInput) {
  const tryAddHero = () => {
    if (!activeRoom?.isOwner) return;
    const raw = heroPickerInput.value.trim();
    if (!raw || matchHeroes.length >= 10) return;
    const q = raw.toLowerCase();
    const matched = heroCatalog.find(
      (h) =>
        h.en.toLowerCase() === q ||
        h.zh === raw ||
        `${h.zh} (${h.en})`.toLowerCase() === q ||
        (h.enAliases || []).some((a) => a.toLowerCase() === q) ||
        (h.zhSlang || []).some((s) => s.toLowerCase() === q),
    ) || heroCatalog.find(
      (h) =>
        h.en.toLowerCase().includes(q) ||
        h.zh.includes(raw) ||
        (h.zhSlang || []).some((s) => s.includes(raw)),
    );
    const canonical = matched ? matched.en : raw;
    if (!matchHeroes.some((h) => h.toLowerCase() === canonical.toLowerCase())) {
      matchHeroes.push(canonical);
      renderSelectedMatchHeroes();
      syncMatchHeroesToRoom();
    }
    heroPickerInput.value = '';
  };
  addHeroBtn.addEventListener('click', tryAddHero);
  heroPickerInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      tryAddHero();
    }
  });
  // Auto-lock immediately when clicking an item from the <datalist> dropdown
  heroPickerInput.addEventListener('input', () => {
    const val = heroPickerInput.value.trim();
    if (heroCatalog.some((h) => `${h.zh} (${h.en})` === val)) {
      tryAddHero();
    }
  });
}

// ============================================================================
// Option 2 (GSI) + Option 3 (Low-Frequency DOTA2 Window Top-Bar Vision Watcher)
// ============================================================================

const gsiStatusBadge = document.getElementById('gsiStatusBadge');
const downloadGsiCfgBtn = document.getElementById('downloadGsiCfgBtn');
const toggleVisionWatchBtn = document.getElementById('toggleVisionWatchBtn');
const scanVisionOnceBtn = document.getElementById('scanVisionOnceBtn');
const visionStatusLine = document.getElementById('visionStatusLine');

function renderGsiStatus(gsiState) {
  if (!gsiStatusBadge || !gsiState) return;
  const isFresh = gsiState.lastHeartbeatAt && Date.now() - gsiState.lastHeartbeatAt < 45_000;
  if (!isFresh) {
    gsiStatusBadge.textContent = 'GSI 待接入';
    gsiStatusBadge.className = 'badge';
    return;
  }
  const shortState = (gsiState.gameState || 'CONNECTED').replace(/^DOTA_GAMERULES_STATE_/, '');
  const modeLabel = gsiState.lastSourceMode === 'spectator_or_coach' ? '10人全视野' : '本机玩家';
  gsiStatusBadge.textContent = `GSI 在线 · ${shortState} (${modeLabel})`;
  gsiStatusBadge.className = 'badge green';
}

if (downloadGsiCfgBtn) {
  downloadGsiCfgBtn.addEventListener('click', () => {
    if (!activeRoom?.isOwner) return;
    const room = encodeURIComponent(activeRoom.roomId);
    window.location.href = `/api/gsi-config?room=${room}`;
  });
}

let visionStream = null;
let visionVideo = null;
let visionTimer = null;
let visionBusy = false;
let lastTopBarThumb = null;

const cropCanvas = document.createElement('canvas');
const cropCtx = cropCanvas.getContext('2d', { willReadFrequently: false });
const diffCanvas = document.createElement('canvas');
diffCanvas.width = 64;
diffCanvas.height = 12;
const diffCtx = diffCanvas.getContext('2d', { willReadFrequently: true });

function extractTopBarFrameAndDiff(videoEl) {
  const vw = videoEl.videoWidth;
  const vh = videoEl.videoHeight;
  if (!vw || !vh) return null;

  // Crop ONLY the top 18% HUD bar and stack left half (Radiant) over right half (Dire)
  // so VLM vision tiles preserve full native portrait resolution instead of downscaling an 11:1 ribbon!
  const srcH = Math.max(96, Math.round(vh * 0.18));
  const halfW = Math.floor(vw / 2);
  const scale = Math.min(1, 960 / Math.max(1, halfW));
  const rowW = Math.max(320, Math.round(halfW * scale));
  const rowH = Math.max(80, Math.round(srcH * scale));

  cropCanvas.width = rowW;
  cropCanvas.height = rowH * 2;
  // Top row: left half of top HUD (Radiant 5 slots + center clock)
  cropCtx.drawImage(videoEl, 0, 0, halfW, srcH, 0, 0, rowW, rowH);
  // Bottom row: right half of top HUD (center clock + Dire 5 slots)
  cropCtx.drawImage(videoEl, halfW, 0, vw - halfW, srcH, 0, rowH, rowW, rowH);

  diffCtx.drawImage(cropCanvas, 0, 0, 64, 12);
  const rgba = diffCtx.getImageData(0, 0, 64, 12).data;
  const luma = new Uint8Array(64 * 12);
  let sumLuma = 0;
  for (let i = 0, p = 0; i < luma.length; i++, p += 4) {
    const y = (rgba[p] * 77 + rgba[p + 1] * 150 + rgba[p + 2] * 29) >> 8;
    luma[i] = y;
    sumLuma += y;
  }
  const meanLuma = sumLuma / luma.length;

  let meanDiff = 999;
  if (lastTopBarThumb && lastTopBarThumb.length === luma.length) {
    let diffSum = 0;
    for (let i = 0; i < luma.length; i++) {
      diffSum += Math.abs(luma[i] - lastTopBarThumb[i]);
    }
    meanDiff = diffSum / luma.length;
  }

  return {
    dataUrl: cropCanvas.toDataURL('image/jpeg', 0.84),
    luma,
    meanLuma,
    meanDiff,
  };
}

async function runVisionTick(force = false) {
  if (!activeRoom?.isOwner || visionBusy || !visionVideo) return;
  const snap = extractTopBarFrameAndDiff(visionVideo);
  if (!snap) return;

  if (!force) {
    // Skip black/minimized frames
    if (snap.meanLuma < 8) return;
    // Once all 10 heroes are locked, sleep until a major HUD layout change (>18 luma diff, e.g. next match BP!)
    if (matchHeroes.length >= 10 && snap.meanDiff < 18) return;
    // Skip unchanged frames so static screens trigger 0 API calls
    if (snap.meanDiff < 4.5) return;
  }

  visionBusy = true;
  lastTopBarThumb = snap.luma;
  if (visionStatusLine) {
    visionStatusLine.textContent = '🔍 正在由 gpt-6-luna 识别顶部 10 英雄头像栏...';
  }

  try {
    const resp = await fetch('/api/vision-draft', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrfToken },
      body: JSON.stringify({
        roomId: activeRoom.roomId,
        imageDataUrl: snap.dataUrl,
      }),
    });
    const resJson = await resp.json();
    if (!resp.ok || !resJson.ok) {
      if (visionStatusLine) {
        visionStatusLine.textContent = `⚠️ 视觉识阵异常: ${resJson.error || resp.status}`;
      }
    }
  } catch (err) {
    if (visionStatusLine) {
      visionStatusLine.textContent = `⚠️ 视觉请求失败: ${err.message || err}`;
    }
  } finally {
    visionBusy = false;
  }
}

function stopVisionWatch() {
  if (visionTimer) {
    clearInterval(visionTimer);
    visionTimer = null;
  }
  if (visionStream) {
    visionStream.getTracks().forEach((t) => t.stop());
    visionStream = null;
  }
  if (visionVideo) {
    visionVideo.srcObject = null;
    visionVideo = null;
  }
  lastTopBarThumb = null;
  if (toggleVisionWatchBtn) {
    toggleVisionWatchBtn.textContent = '🖥️ 开启窗口自动识阵';
    toggleVisionWatchBtn.className = 'btn-radiant';
  }
}

async function startVisionWatch() {
  const roomId = activeRoom?.roomId;
  if (!activeRoom?.isOwner) return;
  const stream = await navigator.mediaDevices.getDisplayMedia({
    video: { frameRate: { ideal: 1, max: 2 } },
    audio: false,
  });
  if (activeRoom?.roomId !== roomId) { stream.getTracks().forEach(track => track.stop()); return; }
  const video = document.createElement('video');
  video.muted = true;
  video.playsInline = true;
  video.srcObject = stream;
  await video.play();

  visionStream = stream;
  visionVideo = video;
  lastTopBarThumb = null;

  stream.getVideoTracks()[0]?.addEventListener('ended', () => {
    stopVisionWatch();
    if (visionStatusLine) {
      visionStatusLine.textContent = '已停止窗口共享；已锁定的本局英雄继续保持生效。';
    }
  });

  if (toggleVisionWatchBtn) {
    toggleVisionWatchBtn.textContent = '⏹ 停止窗口识阵 (后台监听中)';
    toggleVisionWatchBtn.className = 'btn-dire';
  }
  if (visionStatusLine) {
    visionStatusLine.textContent = '🟢 窗口监听已开启：切进 DOTA2 即可，每 12 秒静默检测顶栏英雄头像变化。';
  }

  // Run initial check after 600ms, then every 12s with local pixel-diff gating
  setTimeout(() => runVisionTick(true), 600);
  visionTimer = setInterval(() => runVisionTick(false), 12_000);
}

if (toggleVisionWatchBtn) {
  toggleVisionWatchBtn.addEventListener('click', async () => {
    if (visionStream) {
      stopVisionWatch();
      return;
    }
    try {
      await startVisionWatch();
    } catch (err) {
      if (visionStatusLine) {
        visionStatusLine.textContent = `未开启窗口共享 (${err.message || '已取消'})`;
      }
    }
  });
}

if (scanVisionOnceBtn) {
  scanVisionOnceBtn.addEventListener('click', async () => {
    if (visionStream && visionVideo) {
      await runVisionTick(true);
      return;
    }
    try {
      const roomId = activeRoom?.roomId;
      if (!activeRoom?.isOwner) return;
      const tempStream = await navigator.mediaDevices.getDisplayMedia({
        video: { frameRate: { ideal: 1, max: 2 } },
        audio: false,
      });
      if (activeRoom?.roomId !== roomId) { tempStream.getTracks().forEach(track => track.stop()); return; }
      const tempVideo = document.createElement('video');
      tempVideo.muted = true;
      tempVideo.playsInline = true;
      tempVideo.srcObject = tempStream;
      await tempVideo.play();
      await new Promise((r) => setTimeout(r, 250));
      visionVideo = tempVideo;
      await runVisionTick(true);
      tempStream.getTracks().forEach((t) => t.stop());
      visionVideo = null;
    } catch (err) {
      if (visionStatusLine) {
        visionStatusLine.textContent = `单次截帧取消 (${err.message || '已取消'})`;
      }
    }
  });
}

api('/api/auth/session')
  .then(session => session.user ? initializeUser(session) : showLogin())
  .catch(error => showLogin(error.message));
