const defaults = Object.freeze({
  enabled: true, sourceColor: '#ffffff', targetColor: '#ffe082',
  opacity: 0.95, backgroundOpacity: 0.35, fontSize: 28, top: 5, seconds: 8, monitor: 0,
});
const color = (value, fallback) => /^#[0-9a-f]{6}$/i.test(value) ? value : fallback;
const number = (value, fallback, min, max) => Number.isFinite(Number(value))
  ? Math.min(max, Math.max(min, Number(value))) : fallback;

export function overlaySettings(value = {}) {
  if (!value || typeof value !== 'object') value = {};
  return {
    enabled: value.enabled !== false,
    sourceColor: color(value.sourceColor, defaults.sourceColor),
    targetColor: color(value.targetColor, defaults.targetColor),
    opacity: number(value.opacity, defaults.opacity, 0.15, 1),
    backgroundOpacity: number(value.backgroundOpacity, defaults.backgroundOpacity, 0, 0.9),
    fontSize: number(value.fontSize, defaults.fontSize, 16, 48),
    top: number(value.top, defaults.top, 0, 100),
    seconds: number(value.seconds, defaults.seconds, 3, 30),
    monitor: Math.floor(number(value.monitor, 0, 0, 15)),
  };
}

/** Small bounded, stream-keyed cache: never combine one speaker's text with another's. */
export class OverlayCaptions {
  constructor(send) { this.send = send; this.sources = new Map(); this.turns = new Map(); }
  clear() { this.sources.clear(); this.turns.clear(); this.send({ type: 'overlay.clear' }); }
  accept(message) {
    if (message.type === 'turn.metrics' && message.metrics?.sourceText && message.metrics?.targetText) {
      this.send({ type: 'overlay.caption', speaker: message.speaker || '',
        source: message.metrics.sourceText.slice(0, 4000), target: message.metrics.targetText.slice(0, 4000) });
      return;
    }
    if (!message.type?.startsWith('subtitle.') || typeof message.text !== 'string') return;
    const stream = message.streamId || `${message.speaker}:${message.role}`;
    const key = `${stream}:${message.turnId || 'preview'}`;
    const sourceEvent = message.type.startsWith('subtitle.source.');
    if (sourceEvent) this.sources.set(stream, {
      text: message.text.slice(0, 4000), usedBy: this.turns.get(key)?.target ? key : null,
    });
    const pending = this.sources.get(stream);
    const turn = this.turns.get(key) || {
      source: pending && (!pending.usedBy || pending.usedBy === key) ? pending.text : '',
      target: '', speaker: message.speaker || '',
    };
    if (sourceEvent) turn.source = message.text.slice(0, 4000);
    else {
      turn.target = message.text.slice(0, 4000);
      if (pending && turn.source && !pending.usedBy) pending.usedBy = key;
    }
    this.turns.set(key, turn);
    for (const map of [this.sources, this.turns]) {
      while (map.size > 100) map.delete(map.keys().next().value);
    }
    this.send({ type: 'overlay.caption', ...turn });
  }
}

export class DesktopListener {
  constructor({ room, onSubtitle, root = document, bridge = globalThis.chrome?.webview,
    socketFactory = url => new WebSocket(url), location = globalThis.location }) {
    this.room = room;
    this.onSubtitle = onSubtitle;
    this.root = root;
    this.bridge = bridge;
    this.socketFactory = socketFactory;
    this.location = location;
    this.socket = null;
    this.captureId = null;
    this.ready = false;
    this.available = false;
    this.fullRangePosition = false;
    this.processes = [];
    this.timeout = null;
    this.lastFrame = 0;
    this.lastAck = 0;
    this.lastLevelAt = 0;
    this.captions = new OverlayCaptions(message => this.post(message));
    try { this.settings = overlaySettings(JSON.parse(localStorage.getItem('desktop.overlay') || '{}')); }
    catch { this.settings = overlaySettings(); }
    if (!bridge) return;
    bridge.addEventListener('message', event => this.receive(event.data));
    this.el('refreshProcessesBtn').addEventListener('click', () => this.post({ type: 'process.list' }));
    this.el('listenProcessBtn').addEventListener('click', () => this.captureId ? this.stop() : this.start());
    this.el('previewOverlayBtn').addEventListener('click', () => this.post({
      type: 'overlay.caption', speaker: '字幕预览', source: 'We should smoke and take Roshan.',
      target: '我们开雾去打肉山。',
    }));
    const bindings = {
      overlayEnabled: 'enabled', overlaySourceColor: 'sourceColor', overlayTargetColor: 'targetColor',
      overlayOpacity: 'opacity', overlayBackground: 'backgroundOpacity', overlayFontSize: 'fontSize',
      overlayTop: 'top', overlaySeconds: 'seconds', overlayMonitor: 'monitor',
    };
    for (const [id, key] of Object.entries(bindings)) {
      const element = this.el(id);
      if (element.type === 'checkbox') element.checked = this.settings[key];
      else element.value = String(this.settings[key]);
      element.addEventListener('input', () => {
        this.settings = overlaySettings({ ...this.settings,
          [key]: element.type === 'checkbox' ? element.checked : element.value });
        try { localStorage.setItem('desktop.overlay', JSON.stringify(this.settings)); } catch {}
        this.postOverlaySettings();
      });
    }
    this.post({ type: 'desktop.hello' });
  }

  el(id) { return this.root.getElementById(id); }
  post(message) { this.bridge?.postMessage(message); }
  status(message) { this.el('processListenStatus').textContent = message; }
  postOverlaySettings() {
    const max = this.fullRangePosition ? 100 : 60;
    // Preserve a newer saved bottom preference when temporarily running an older desktop binary.
    const top = Math.min(this.settings.top, max);
    const control = this.el('overlayTop');
    control.max = String(max);
    control.value = String(top);
    const label = this.fullRangePosition
      ? top === 0 ? '顶部' : top === 100 ? '底部' : top === 50 ? '居中' : '垂直位置'
      : '距屏幕顶部';
    control.setAttribute('aria-valuetext', `${label} ${top}%`);
    this.el('overlayTopValue').textContent = `${top}%`;
    this.el('overlayPositionLabel').textContent = this.fullRangePosition ? '字幕垂直位置' : '距屏幕顶部（旧版）';
    this.el('overlayPositionHint').textContent = this.fullRangePosition
      ? '0% 顶部 · 50% 居中 · 100% 底部，按实际字幕高度定位，四周保留少量边距。'
      : '当前助手只支持距顶部 0–60%。请升级到 v0.1.3 或更新版本，才能将字幕贴到屏幕底部。';
    this.post({ type: 'overlay.settings', ...this.settings, top });
  }
  showProcesses(processes) {
    const select = this.el('processSelect');
    const previous = select.value;
    this.processes = processes.filter(p => Number.isSafeInteger(p.pid) && p.pid > 0 &&
      typeof p.name === 'string' && typeof p.startedAt === 'string').slice(0, 512);
    select.replaceChildren();
    for (const process of this.processes) {
      const option = this.root.createElement('option');
      option.value = String(process.pid);
      option.textContent = `${process.name} · PID ${process.pid}`;
      select.append(option);
    }
    if (this.processes.some(p => String(p.pid) === previous)) select.value = previous;
    else {
      const dota = this.processes.find(p => /^dota2(\.exe)?$/i.test(p.name));
      if (dota) select.value = String(dota.pid);
    }
  }

  receive(message) {
    if (!message || typeof message !== 'object') return;
    switch (message.type) {
      case 'desktop.ready': {
        if (message.version !== 1) { this.status('本地助手版本不兼容，请更新'); return; }
        this.available = true;
        this.fullRangePosition = message.overlayPositioning === 'available-space-v1';
        this.el('desktopUnavailable').hidden = true;
        this.el('desktopControls').hidden = false;
        const monitor = this.el('overlayMonitor');
        monitor.replaceChildren();
        for (const [index, name] of (message.monitors || []).entries()) {
          const option = this.root.createElement('option');
          option.value = String(index); option.textContent = String(name);
          monitor.append(option);
        }
        if (this.settings.monitor >= monitor.options.length) this.settings.monitor = 0;
        monitor.value = String(this.settings.monitor);
        this.postOverlaySettings();
        this.post({ type: 'process.list' });
        break;
      }
      case 'process.list': this.showProcesses(Array.isArray(message.processes) ? message.processes : []); break;
      case 'capture.started':
        if (message.captureId !== this.captureId) return;
        clearTimeout(this.timeout);
        this.lastFrame = Date.now();
        this.lastAck = Date.now();
        this.timeout = setInterval(() => {
          if (Date.now() - this.lastFrame > 8000) this.stop('本地助手未送出音频，已停止；请重新开始');
          else if (Date.now() - this.lastAck > 12_000) this.stop('服务器未确认收到声音，已停止监听；请检查网络');
        }, 1000);
        this.status('正在监听 · 自动筛选英语 · 仅本机聊天与字幕，不朗读');
        break;
      case 'capture.audio': {
        if (message.captureId !== this.captureId || !this.ready || this.socket?.readyState !== 1) return;
        if (this.socket.bufferedAmount > 96_000) { this.stop('上传积压，已停止监听；请检查网络'); return; }
        try {
          if (typeof message.audio !== 'string' || message.audio.length > 40_000) throw new Error();
          const bytes = Uint8Array.from(atob(message.audio), c => c.charCodeAt(0));
          if (!bytes.length || bytes.length % 2 || bytes.length > 28_800) throw new Error();
          this.socket.send(bytes);
          this.lastFrame = Date.now();
          if (Date.now() - this.lastLevelAt > 1000) {
            this.lastLevelAt = Date.now();
            const samples = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
            let peak = 0;
            for (let i = 0; i < bytes.length; i += 2) peak = Math.max(peak, Math.abs(samples.getInt16(i, true)));
            this.status(peak > 260
              ? `正在监听 · 输入音量 ${Math.round(peak / 327.68)}% · 自动筛选英语 · 仅本人字幕`
              : '正在监听 · 当前输入较静；若持续无声请核对进程和音量 · 仅本人字幕');
          }
        } catch { this.stop('本地音频数据无效，已停止监听'); }
        break;
      }
      case 'capture.stopped':
        if (message.captureId === this.captureId) this.stop(message.message || '本地采集已停止');
        break;
      case 'desktop.error':
        this.stop(message.message || '本地助手出错');
        break;
    }
  }

  start() {
    const room = this.room();
    if (!this.available || !room || this.captureId) return;
    if (!this.el('processConsent').checked) {
      this.status('请先确认：你有权采集这些声音，并已告知对方音频会上传到云端翻译');
      return;
    }
    const process = this.processes.find(p => String(p.pid) === this.el('processSelect').value);
    if (!process) { this.status('请先打开目标软件、刷新列表并选择进程'); return; }
    this.captureId = crypto.randomUUID();
    const captureId = this.captureId;
    this.el('processSelect').disabled = true;
    this.el('listenProcessBtn').textContent = '停止监听';
    this.status('正在连接独立监听引擎…');
    const query = new URLSearchParams({ room: room.roomId, role: 'coach',
      inputKind: 'process', sourceLabel: process.name.slice(0, 80) });
    if (room.invite) query.set('invite', room.invite);
    let socket;
    try {
      socket = this.socketFactory(`${this.location.protocol === 'https:' ? 'wss:' : 'ws:'}//${this.location.host}/ws?${query}`);
    } catch { this.stop('无法创建监听连接'); return; }
    this.socket = socket;
    let listenerConfirmed = false;
    this.timeout = setTimeout(() => this.stop('监听启动超时，请重新开始'), 30_000);
    socket.onmessage = event => {
      if (this.socket !== socket || this.captureId !== captureId || typeof event.data !== 'string') return;
      let message;
      try { message = JSON.parse(event.data); } catch { return; }
      if (!message || typeof message !== 'object') return;
      if (message.type === 'listener.config') {
        listenerConfirmed = message.protocolVersion === 1 && message.privacy === 'connection' &&
          message.output === 'text' && message.sampleRate === 24000 && message.channels === 1;
        if (!listenerConfirmed) this.stop('监听服务协议不兼容，已阻止采集；请更新服务端');
      } else if (message.type === 'engine.state') {
        if (message.state === 'ready' && !this.ready) {
          if (!listenerConfirmed) {
            this.stop('服务端未确认私有监听通道，已阻止采集；请更新服务端并刷新页面');
            return;
          }
          this.ready = true;
          clearTimeout(this.timeout);
          this.timeout = setTimeout(() => this.stop('采集确认超时，请重新开始监听'), 60_000);
          this.post({ type: 'capture.start', captureId, pid: process.pid,
            startedAt: process.startedAt }); // Process tree is always included.
        } else if (this.ready && message.state !== 'ready') {
          this.stop('监听服务已断开，请重新开始');
        }
      } else if (message.type?.startsWith('subtitle.') && message.sourceKind === 'process' && message.private === true) {
        this.onSubtitle(message);
      } else if (message.type === 'audio.input.status' && message.engineReady && message.forwardedFrames > 0) {
        this.lastAck = Date.now();
      } else if ((message.type === 'engine.error' && message.sourceKind !== 'microphone') || message.type === 'room.error') {
        this.stop(message.message || '监听服务出错');
      } else if (message.type === 'room.settings.changed') {
        this.stop('房间设置已更改，请重新开始监听');
      }
    };
    socket.onclose = () => {
      if (this.socket === socket) this.stop('监听连接已断开，请重新开始');
    };
    socket.onerror = () => {
      if (this.socket === socket) this.stop('监听连接失败，请检查登录状态、房间权限与网络');
    };
  }

  stop(message = '监听已停止') {
    const captureId = this.captureId;
    this.captureId = null;
    this.ready = false;
    clearTimeout(this.timeout);
    clearInterval(this.timeout);
    this.timeout = null;
    const socket = this.socket;
    this.socket = null;
    if (socket) { socket.onclose = socket.onerror = socket.onmessage = null; socket.close(); }
    if (captureId) this.post({ type: 'capture.stop', captureId });
    this.el('processSelect').disabled = false;
    this.el('listenProcessBtn').textContent = '开始监听';
    this.status(message);
    this.captions.clear();
  }

  reset() {
    this.stop();
    this.el('processConsent').checked = false;
  }
}
