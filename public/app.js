const roomInput = document.getElementById('roomInput');
const roleSelect = document.getElementById('roleSelect');
const nameInput = document.getElementById('nameInput');
const joinBtn = document.getElementById('joinBtn');
const copyCoachLinkBtn = document.getElementById('copyCoachLinkBtn');
const engineCards = document.querySelectorAll('.engine-card');
const rawVoiceRelayToggle = document.getElementById('rawVoiceRelayToggle');
const selfMonitorToggle = document.getElementById('selfMonitorToggle');
const cascadeTtsPanel = document.getElementById('cascadeTtsPanel');
const ttsZhVoiceSelect = document.getElementById('ttsZhVoiceSelect');
const ttsEnVoiceSelect = document.getElementById('ttsEnVoiceSelect');
const ttsStreamModeSelect = document.getElementById('ttsStreamModeSelect');
const voiceLiveAsrSelect = document.getElementById('voiceLiveAsrSelect');
const connBadge = document.getElementById('connBadge');
const participantsList = document.getElementById('participantsList');
const engineStatusBar = document.getElementById('engineStatusBar');
const openMicBtn = document.getElementById('openMicBtn');
const pttBtn = document.getElementById('pttBtn');
const micMeterFill = document.getElementById('micMeterFill');
const audioOutBadge = document.getElementById('audioOutBadge');
const livePartialBanner = document.getElementById('livePartialBanner');
const transcriptFeed = document.getElementById('transcriptFeed');
const textSendForm = document.getElementById('textSendForm');
const textSendInput = document.getElementById('textSendInput');
const benchmarkContainer = document.getElementById('benchmarkContainer');
const glossarySearch = document.getElementById('glossarySearch');
const glossaryList = document.getElementById('glossaryList');

// Hydrate room & role from URL query params so Coach invite links open ready-to-join
const params = new URLSearchParams(window.location.search);
if (params.get('room')) roomInput.value = params.get('room');
if (params.get('role') === 'coach') {
  roleSelect.value = 'coach';
  nameInput.value = params.get('name') || 'EN Coach';
} else if (params.get('name')) {
  nameInput.value = params.get('name');
}

roleSelect.addEventListener('change', () => {
  if (roleSelect.value === 'coach' && nameInput.value === 'CN Carry') {
    nameInput.value = 'EN Coach';
  } else if (roleSelect.value === 'student' && nameInput.value === 'EN Coach') {
    nameInput.value = 'CN Carry';
  }
});

let ws = null;
let selectedEngine = 'cascade';
let unifiedDictEntries = [];
let heroCatalog = [];
let activeDictCategory = 'all';
let matchHeroes = [];
const turnCards = new Map();

// WebAudio playback & 24kHz PCM16 capture state
let audioCtx = null;
let nextPlayTime = 0;
let micStream = null;
let workletNode = null;
let openMicActive = false;
let pttPressed = false;

function ensureAudioContext() {
  if (!audioCtx) {
    audioCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 24000 });
  }
  if (audioCtx.state === 'suspended') {
    audioCtx.resume();
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

async function ensureMicPipeline() {
  if (workletNode) return;
  const ctx = ensureAudioContext();
  micStream = await navigator.mediaDevices.getUserMedia({
    audio: {
      channelCount: 1,
      echoCancellation: true,
      noiseSuppression: true,
      autoGainControl: true,
    },
  });

  const blob = new Blob([WORKLET_CODE], { type: 'application/javascript' });
  const blobUrl = URL.createObjectURL(blob);
  await ctx.audioWorklet.addModule(blobUrl);
  URL.revokeObjectURL(blobUrl);

  const source = ctx.createMediaStreamSource(micStream);
  workletNode = new AudioWorkletNode(ctx, 'pcm24k-capture');
  workletNode.port.onmessage = (evt) => {
    const { pcm, rms } = evt.data;
    const pct = Math.min(100, Math.round(rms * 360));
    micMeterFill.style.width = openMicActive || pttPressed ? `${pct}%` : '0%';
    if ((openMicActive || pttPressed) && ws && ws.readyState === WebSocket.OPEN) {
      ws.send(pcm);
    }
  };
  source.connect(workletNode);
}

function connectRoom() {
  ensureAudioContext();
  if (ws) {
    ws.close();
    ws = null;
  }
  const room = encodeURIComponent(roomInput.value.trim() || 'roshan-pit');
  const role = encodeURIComponent(roleSelect.value);
  const name = encodeURIComponent(nameInput.value.trim() || 'Player');
  const selfMon = selfMonitorToggle.checked ? '1' : '0';
  const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  ws = new WebSocket(
    `${proto}//${window.location.host}/ws?room=${room}&role=${role}&name=${name}&selfMonitor=${selfMon}`,
  );
  ws.binaryType = 'arraybuffer';

  connBadge.textContent = 'Connecting...';
  connBadge.className = 'badge gold';

  ws.onopen = () => {
    connBadge.textContent = 'Connected';
    connBadge.className = 'badge green';
    joinBtn.textContent = 'Reconnect / 刷新重连';
  };

  ws.onclose = () => {
    connBadge.textContent = 'Disconnected';
    connBadge.className = 'badge';
  };

  ws.onmessage = (evt) => {
    if (evt.data instanceof ArrayBuffer) {
      if (evt.data.byteLength > 2) {
        const pcmSlice = new Uint8Array(evt.data, 2);
        playPcm24kMono(pcmSlice);
      }
      return;
    }

    const msg = JSON.parse(evt.data);
    handleServerMessage(msg);
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
      selectedEngine = msg.engineKind;
      rawVoiceRelayToggle.checked = Boolean(msg.englishOriginalVoiceRelay);
      if (cascadeTtsPanel) {
        cascadeTtsPanel.style.display =
          selectedEngine === 'cascade' || selectedEngine === 'gpt-realtime' ? 'flex' : 'none';
      }
      if (msg.ttsZhVoice && ttsZhVoiceSelect) ttsZhVoiceSelect.value = msg.ttsZhVoice;
      if (msg.ttsEnVoice && ttsEnVoiceSelect) ttsEnVoiceSelect.value = msg.ttsEnVoice;
      if (msg.ttsStreamMode && ttsStreamModeSelect) ttsStreamModeSelect.value = msg.ttsStreamMode;
      if (msg.voiceLiveAsrModel && voiceLiveAsrSelect) {
        voiceLiveAsrSelect.value = msg.voiceLiveAsrModel;
      }
      if (Array.isArray(msg.matchHeroes)) {
        matchHeroes = msg.matchHeroes;
        renderSelectedMatchHeroes();
      }
      if (msg.gsiState) {
        renderGsiStatus(msg.gsiState);
      }
      engineCards.forEach((c) => {
        c.classList.toggle('active', c.dataset.engine === selectedEngine);
      });
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

    case 'engine.status': {
      engineStatusBar.textContent = `[${msg.speaker}] ${msg.status}`;
      break;
    }

    case 'engine.error': {
      engineStatusBar.textContent = `⚠️ [${msg.speaker}] ${msg.message}`;
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
    .replace(/>/g, '&gt;');
}

// UI Bindings
joinBtn.addEventListener('click', connectRoom);

copyCoachLinkBtn.addEventListener('click', async () => {
  const url = new URL(window.location.href);
  url.searchParams.set('room', roomInput.value.trim() || 'roshan-pit');
  url.searchParams.set('role', 'coach');
  url.searchParams.set('name', 'Coach');
  await navigator.clipboard.writeText(url.toString());
  const old = copyCoachLinkBtn.textContent;
  copyCoachLinkBtn.textContent = 'Copied Coach URL!';
  setTimeout(() => (copyCoachLinkBtn.textContent = old), 1600);
});

engineCards.forEach((card) => {
  card.addEventListener('click', () => {
    const kind = card.dataset.engine;
    selectedEngine = kind;
    engineCards.forEach((c) => c.classList.toggle('active', c.dataset.engine === kind));
    if (cascadeTtsPanel) {
      cascadeTtsPanel.style.display =
        kind === 'cascade' || kind === 'gpt-realtime' ? 'flex' : 'none';
    }
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(
        JSON.stringify({
          type: 'room.set_engine',
          engineKind: kind,
          englishOriginalVoiceRelay: rawVoiceRelayToggle.checked,
        }),
      );
    }
  });
});

rawVoiceRelayToggle.addEventListener('change', () => {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(
      JSON.stringify({
        type: 'room.set_engine',
        engineKind: selectedEngine,
        englishOriginalVoiceRelay: rawVoiceRelayToggle.checked,
      }),
    );
  }
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
  if (!ws || ws.readyState !== WebSocket.OPEN) connectRoom();
  await ensureMicPipeline();
  openMicActive = !openMicActive;
  if (openMicActive) {
    openMicBtn.textContent = 'Stop Open Mic (正在连麦...)';
    openMicBtn.className = 'btn-dire';
  } else {
    openMicBtn.textContent = 'Start Open Mic (免按键连麦)';
    openMicBtn.className = 'btn-radiant';
    micMeterFill.style.width = '0%';
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: 'speech.end' }));
    }
  }
});

async function startPtt() {
  if (pttPressed) return;
  if (!ws || ws.readyState !== WebSocket.OPEN) connectRoom();
  await ensureMicPipeline();
  pttPressed = true;
  pttBtn.className = 'btn-dire';
  pttBtn.textContent = 'Speaking... Release to Commit';
}

function stopPtt() {
  if (!pttPressed) return;
  pttPressed = false;
  pttBtn.className = '';
  pttBtn.textContent = 'Hold Push-to-Talk (按住说话 / Space)';
  micMeterFill.style.width = '0%';
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify({ type: 'speech.end' }));
  }
}

pttBtn.addEventListener('mousedown', startPtt);
window.addEventListener('mouseup', stopPtt);
pttBtn.addEventListener('touchstart', (e) => {
  e.preventDefault();
  startPtt();
});
window.addEventListener('touchend', stopPtt);

window.addEventListener('keydown', (e) => {
  if (e.code === 'Space' && document.activeElement?.tagName !== 'INPUT') {
    e.preventDefault();
    startPtt();
  }
});
window.addEventListener('keyup', (e) => {
  if (e.code === 'Space' && document.activeElement?.tagName !== 'INPUT') {
    e.preventDefault();
    stopPtt();
  }
});

textSendForm.addEventListener('submit', (e) => {
  e.preventDefault();
  const txt = textSendInput.value.trim();
  if (!txt) return;
  if (!ws || ws.readyState !== WebSocket.OPEN) connectRoom();
  ensureAudioContext();
  const sendNow = () => {
    ws.send(JSON.stringify({ type: 'text.send', text: txt }));
    textSendInput.value = '';
  };
  if (ws.readyState === WebSocket.OPEN) sendNow();
  else ws.addEventListener('open', sendNow, { once: true });
});

document.querySelectorAll('.demo-btn').forEach((btn) => {
  btn.addEventListener('click', async () => {
    ensureAudioContext();
    if (!ws || ws.readyState !== WebSocket.OPEN) {
      connectRoom();
      await new Promise((r) => ws.addEventListener('open', r, { once: true }));
    }
    await fetch('/api/demo-speak', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        roomId: roomInput.value.trim() || 'roshan-pit',
        role: btn.dataset.role,
        lang: btn.dataset.lang,
        text: btn.dataset.text,
      }),
    });
  });
});

function toggleMatchHero(canonicalEn) {
  if (!canonicalEn) return;
  const exists = matchHeroes.some((h) => h.toLowerCase() === canonicalEn.toLowerCase());
  if (exists) {
    matchHeroes = matchHeroes.filter((h) => h.toLowerCase() !== canonicalEn.toLowerCase());
  } else {
    if (matchHeroes.length >= 10) return;
    matchHeroes.push(canonicalEn);
  }
  renderSelectedMatchHeroes();
  renderDictionary();
  syncMatchHeroesToRoom();
}

function renderDictionary() {
  const q = glossarySearch.value.trim().toLowerCase();
  const pool =
    activeDictCategory === 'all'
      ? unifiedDictEntries
      : unifiedDictEntries.filter((e) => e.cat === activeDictCategory);

  const filtered = q
    ? pool.filter((e) => e.searchBlob.includes(q))
    : pool;

  const shown = filtered.slice(0, 120);
  glossaryList.innerHTML = shown
    .map((g) => {
      const isHero = g.cat === 'heroes';
      const locked =
        isHero && matchHeroes.some((h) => h.toLowerCase() === g.en.toLowerCase());
      const heroBtn = isHero
        ? `<button type="button" data-toggle-hero="${escapeHtml(g.en)}" style="margin-left:6px;padding:1px 6px;font-size:11px;${
            locked ? 'border-color:var(--radiant);color:var(--radiant)' : ''
          }">${locked ? '✓ 已锁' : '+ 本局'}</button>`
        : '';
      return `
      <div class="glossary-item">
        <div class="glossary-item-row">
          <span><strong>${escapeHtml(g.en)}</strong></span>
          <span style="display:flex;align-items:center;justify-content:flex-end">
            <strong style="color: var(--gold); text-align: right">${escapeHtml(g.zh)}</strong>${heroBtn}
          </span>
        </div>
        ${g.sub ? `<div class="glossary-sub">${escapeHtml(g.sub)}</div>` : ''}
      </div>`;
    })
    .join('');

  glossaryList.querySelectorAll('[data-toggle-hero]').forEach((btn) => {
    btn.addEventListener('click', () => {
      toggleMatchHero(btn.getAttribute('data-toggle-hero'));
    });
  });

  if (filtered.length > shown.length) {
    glossaryList.insertAdjacentHTML(
      'beforeend',
      `<div style="font-size:11px;color:var(--text-muted);text-align:center;padding:4px">Showing 120 of ${filtered.length} entries — type above to filter</div>`,
    );
  } else if (filtered.length === 0) {
    glossaryList.innerHTML = `<div style="font-size:12px;color:var(--text-muted);padding:6px">No matching DOTA2 term found.</div>`;
  }
}

glossarySearch.addEventListener('input', renderDictionary);

document.querySelectorAll('.dict-tab').forEach((tab) => {
  tab.addEventListener('click', () => {
    activeDictCategory = tab.dataset.cat;
    document.querySelectorAll('.dict-tab').forEach((t) => t.classList.toggle('active', t === tab));
    renderDictionary();
  });
});

function renderSelectedMatchHeroes() {
  const countEl = document.getElementById('matchHeroesCount');
  const clearBtn = document.getElementById('clearHeroesBtn');
  const container = document.getElementById('selectedHeroTags');
  if (!countEl || !container) return;
  countEl.textContent = `${matchHeroes.length} / 10`;
  if (clearBtn) {
    clearBtn.style.display = matchHeroes.length > 0 ? 'inline-block' : 'none';
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
      return `<span class="hero-tag">${escapeHtml(label)} <button type="button" data-remove-hero="${escapeHtml(keyOrName)}">×</button></span>`;
    })
    .join('');

  container.querySelectorAll('[data-remove-hero]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const target = btn.getAttribute('data-remove-hero');
      matchHeroes = matchHeroes.filter((h) => h !== target);
      renderSelectedMatchHeroes();
      renderDictionary();
      syncMatchHeroesToRoom();
    });
  });
}

function syncMatchHeroesToRoom() {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify({ type: 'room.set_match_heroes', matchHeroes }));
  }
}

const clearHeroesBtn = document.getElementById('clearHeroesBtn');
if (clearHeroesBtn) {
  clearHeroesBtn.addEventListener('click', () => {
    if (!matchHeroes.length) return;
    matchHeroes = [];
    renderSelectedMatchHeroes();
    renderDictionary();
    syncMatchHeroesToRoom();
  });
}

const heroPickerInput = document.getElementById('heroPickerInput');
const addHeroBtn = document.getElementById('addHeroBtn');
if (addHeroBtn && heroPickerInput) {
  const tryAddHero = () => {
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
      renderDictionary();
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

function renderBenchmark(bench, domainAcc) {
  let html = '';
  if (domainAcc && domainAcc.textSummary) {
    const s = domainAcc.textSummary;
    const basePct = Math.round((s.baselinePassed / s.total) * 100);
    const tunedPct = Math.round((s.tunedPassed / s.total) * 100);
    html += `<div style="font-size:12px;margin-bottom:6px;padding:6px 8px;border-radius:6px;background:rgba(61,214,140,0.12);border:1px solid rgba(61,214,140,0.3)">
      🎯 <strong>DOTA2 专有名词评测:</strong> 裸模型 <code>${s.baselinePassed}/${s.total} (${basePct}%)</code> → 词典调优后 <strong style="color:var(--radiant)">${s.tunedPassed}/${s.total} (${tunedPct}%)</strong>
    </div>`;
  }
  if (!bench || !Array.isArray(bench.results) || !bench.results.length) {
    benchmarkContainer.innerHTML =
      html +
      `<div style="font-size:12px;color:var(--text-muted)">Run <code>npm run benchmark</code> to populate latency comparison.</div>`;
    return;
  }
  const rows = bench.results
    .map((r) => {
      const m = r.metrics;
      const shortScenario =
        r.scenario === 'coach_roshan_call'
          ? 'Coach EN→ZH'
          : r.scenario === 'student_zh_question'
            ? 'Student ZH→EN'
            : 'Student EN→EN';
      return `<tr>
        <td><strong>${escapeHtml(m.engine)}</strong></td>
        <td>${shortScenario}</td>
        <td style="color:var(--radiant);font-weight:700">${m.speechEndToFirstAudioMs} ms</td>
        <td>${escapeHtml(m.passThrough)}</td>
      </tr>`;
    })
    .join('');
  benchmarkContainer.innerHTML = `
    ${html}
    <table class="bench-table">
      <thead>
        <tr><th>Engine</th><th>Case</th><th>TTFA</th><th>Mode</th></tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function syncTtsOptionsToRoom() {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(
      JSON.stringify({
        type: 'room.set_tts_options',
        ttsZhVoice: ttsZhVoiceSelect?.value,
        ttsEnVoice: ttsEnVoiceSelect?.value,
        ttsStreamMode: ttsStreamModeSelect?.value,
        voiceLiveAsrModel: voiceLiveAsrSelect?.value,
      }),
    );
  }
}

[ttsZhVoiceSelect, ttsEnVoiceSelect, ttsStreamModeSelect, voiceLiveAsrSelect].forEach((sel) => {
  if (sel) sel.addEventListener('change', syncTtsOptionsToRoom);
});

async function initConfig() {
  const [cfgRes, dictRes] = await Promise.all([fetch('/api/config'), fetch('/api/dictionary')]);
  const cfg = await cfgRes.json();
  const dict = await dictRes.json();

  if (cfg.ttsVoiceCatalog) {
    if (ttsZhVoiceSelect && Array.isArray(cfg.ttsVoiceCatalog.zh)) {
      ttsZhVoiceSelect.innerHTML = cfg.ttsVoiceCatalog.zh
        .map((v) => `<option value="${escapeHtml(v.id)}">${escapeHtml(v.label)}</option>`)
        .join('');
    }
    if (ttsEnVoiceSelect && Array.isArray(cfg.ttsVoiceCatalog.en)) {
      ttsEnVoiceSelect.innerHTML = cfg.ttsVoiceCatalog.en
        .map((v) => `<option value="${escapeHtml(v.id)}">${escapeHtml(v.label)}</option>`)
        .join('');
    }
  }

  heroCatalog = dict.heroes || [];
  const dl = document.getElementById('heroPickerDatalist');
  if (dl) {
    dl.innerHTML = heroCatalog
      .map((h) => {
        const slang = [...(h.zhSlang || []), ...(h.enAliases || [])].join('/');
        return `<option value="${escapeHtml(h.en)}">${escapeHtml(h.zh)} ${slang ? `(${escapeHtml(slang)})` : ''}</option>`;
      })
      .join('');
  }

  unifiedDictEntries = [];
  for (const t of dict.tacticalTerms || []) {
    const sub = [...(t.enAliases || []), ...(t.zhAliases || [])].slice(0, 6).join(' · ');
    unifiedDictEntries.push({
      cat: 'tactics',
      en: t.en,
      zh: t.zh,
      sub: `[${t.category}] ${sub}`,
      searchBlob: `${t.en} ${t.zh} ${(t.enAliases || []).join(' ')} ${(t.zhAliases || []).join(' ')}`.toLowerCase(),
    });
  }
  for (const h of dict.heroes || []) {
    const sub = [
      h.enAliases?.length ? `EN: ${h.enAliases.join('/')}` : '',
      h.zhSlang?.length ? `俗称: ${h.zhSlang.join('/')}` : '',
    ]
      .filter(Boolean)
      .join(' · ');
    unifiedDictEntries.push({
      cat: 'heroes',
      en: h.en,
      zh: h.zh,
      sub,
      searchBlob: `${h.en} ${h.zh} ${(h.enAliases || []).join(' ')} ${(h.zhSlang || []).join(' ')}`.toLowerCase(),
    });
    for (const ab of h.abilities || []) {
      const abSub = [
        `${h.zh} (${h.en})`,
        ab.isInnate ? '先天技能' : '',
        ...(ab.enAliases || []),
        ...(ab.zhSlang || []),
      ]
        .filter(Boolean)
        .join(' · ');
      unifiedDictEntries.push({
        cat: 'abilities',
        en: ab.en,
        zh: ab.zh,
        sub: abSub,
        searchBlob: `${ab.en} ${ab.zh} ${h.en} ${h.zh} ${(ab.enAliases || []).join(' ')} ${(ab.zhSlang || []).join(' ')} ${(h.zhSlang || []).join(' ')}`.toLowerCase(),
      });
    }
  }
  for (const it of [...(dict.shopItems || []), ...(dict.neutralItems || [])]) {
    const tier =
      it.isEnhancement
        ? '中立附魔'
        : it.neutralTier >= 0
          ? `Tier ${it.neutralTier + 1} 中立道具`
          : '商店装备';
    const sub = [
      tier,
      it.enAliases?.length ? `EN: ${it.enAliases.join('/')}` : '',
      it.zhSlang?.length ? `俗称: ${it.zhSlang.join('/')}` : '',
    ]
      .filter(Boolean)
      .join(' · ');
    unifiedDictEntries.push({
      cat: 'items',
      en: it.en,
      zh: it.zh,
      sub,
      searchBlob: `${it.en} ${it.zh} ${(it.enAliases || []).join(' ')} ${(it.zhSlang || []).join(' ')}`.toLowerCase(),
    });
  }

  renderDictionary();
  renderSelectedMatchHeroes();
  renderBenchmark(cfg.benchmark, cfg.domainAccuracy);
  // Auto-connect room on page load so user can click Simulator or Mic immediately!
  connectRoom();
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
    const room = encodeURIComponent(roomInput.value.trim() || 'roshan-pit');
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
  if (visionBusy || !visionVideo) return;
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
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        roomId: roomInput.value.trim() || 'roshan-pit',
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
  const stream = await navigator.mediaDevices.getDisplayMedia({
    video: { frameRate: { ideal: 1, max: 2 } },
    audio: false,
  });
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
      const tempStream = await navigator.mediaDevices.getDisplayMedia({
        video: { frameRate: { ideal: 1, max: 2 } },
        audio: false,
      });
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

initConfig();
