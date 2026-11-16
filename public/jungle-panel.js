const $ = id => document.getElementById(id);
const integer = value => Math.round(Number(value) || 0);
const clock = value => `${Math.floor(Math.max(0, value) / 60)}:${String(Math.floor(Math.max(0, value) % 60)).padStart(2, '0')}`;
const svgNS = 'http://www.w3.org/2000/svg';

export class JunglePanel {
  constructor({ send, canControl, isStudent }) {
    this.send = send; this.canControl = canControl; this.isStudent = isStudent;
    this.state = null; this.selected = null; this.lastSpoken = 0; this.utterance = null;
    $('jungleEnabled').addEventListener('change', () => {
      const sent = send({ type: 'room.set_tactics', enabled: $('jungleEnabled').checked });
      $('jungleEnabled').disabled = true;
      // Disable while pending; the server's state/error (or timeout) commits/rolls back the switch.
      clearTimeout(this.controlTimer);
      this.controlTimer = setTimeout(() => { if (this.state) this.render(this.state); }, sent ? 5000 : 0);
    });
    $('jungleRequest').addEventListener('click', () => this.request());
    $('jungleTreads').addEventListener('change', () => {
      send({ type: 'tactics.set_options', options: { treadsAttribute: $('jungleTreads').value } });
    });
    $('jungleCamp').addEventListener('change', () => { this.selected = $('jungleCamp').value; this.draw(); });
    $('jungleCleared').addEventListener('click', () => {
      if (this.selected) send({ type: 'tactics.mark_cleared', campId: this.selected });
    });
    $('jungleVoice').addEventListener('change', () => {
      if (!$('jungleVoice').checked) this.stopVoice();
    });
    document.addEventListener('keydown', event => {
      if (!event.repeat && event.altKey && event.code === 'KeyJ' &&
          !/INPUT|TEXTAREA|SELECT/.test(event.target?.tagName) && !event.target?.isContentEditable) {
        event.preventDefault(); this.request();
      }
    });
    setInterval(() => this.freshness(), 1000);
    this.reset();
  }
  request() {
    if (this.state?.enabled && this.canControl()) this.send({ type: 'tactics.request_route' });
  }
  stopVoice() {
    if (this.utterance && window.speechSynthesis) window.speechSynthesis.cancel();
    this.utterance = null;
  }
  reset() {
    clearTimeout(this.controlTimer);
    this.stopVoice(); this.state = null; this.selected = null;
    this.renderedPlanAt = null;
    $('jungleVoice').checked = false; $('jungleEnabled').checked = false;
    $('jungleEnabled').disabled = true; $('jungleRequest').disabled = true; $('jungleCleared').disabled = true;
    $('jungleBody').hidden = true;
    $('jungleStatus').textContent = '刷野规划默认关闭；连接后由学员或房主开启。';
  }
  accept(message) {
    if (message.type === 'room.error' && this.state) this.render(this.state);
    if (message.type === 'room.state' && message.tactics) this.render(message.tactics);
    if (message.type === 'tactics.state') this.render(message.state);
    if (message.type !== 'tactics.alert' || !this.state?.enabled || !this.isStudent() ||
        !$('jungleVoice').checked || !this.fresh() || Date.now() - message.createdAt > 2000 ||
        (!message.requested && Date.now() - this.lastSpoken < 15_000)) return;
    const voice = window.speechSynthesis?.getVoices().find(v => v.localService && /^zh[-_]/i.test(v.lang));
    if (!voice) {
      $('jungleVoiceStatus').textContent = '未发现本地中文音色；仍可查看文字路线。';
      return;
    }
    this.stopVoice();
    const utterance = new SpeechSynthesisUtterance(message.text);
    utterance.voice = voice; utterance.lang = voice.lang;
    this.utterance = utterance; this.lastSpoken = Date.now();
    utterance.onend = () => { if (this.utterance === utterance) this.utterance = null; };
    window.speechSynthesis.speak(utterance);
  }
  fresh() { return this.state?.updatedAt != null && Date.now() - this.state.updatedAt <= 8000; }
  freshness() {
    if (!this.state) return;
    const fresh = this.fresh();
    $('jungleRequest').disabled = !this.canControl() || !this.state.enabled || !fresh;
    $('jungleCleared').disabled = !this.canControl() || !this.state.enabled || !fresh || !this.selected;
    $('junglePlan').hidden = !this.state.route || !fresh;
    $('jungleReasoning').hidden = !this.state.route || !fresh;
    if (this.state.enabled && this.state.updatedAt && !fresh) {
      $('jungleStatus').textContent = 'GSI 已过期，路线已撤回；等待新的玩家数据。';
      this.stopVoice();
    }
  }
  render(state) {
    if (state.route && !['llm', 'numeric-benchmark'].includes(state.planner?.kind)) {
      state = { ...state, route: null, status: '服务端没有提供模型规划来源；已拒绝显示旧规则路线，请升级服务端。' };
    }
    clearTimeout(this.controlTimer);
    this.state = state;
    $('jungleEnabled').checked = state.enabled;
    $('jungleEnabled').disabled = !this.canControl();
    $('jungleVoice').disabled = !this.isStudent() || !window.speechSynthesis;
    $('jungleBody').hidden = !state.enabled;
    $('jungleStatus').textContent = state.status;
    const planner = state.planner;
    $('junglePlanner').textContent = planner?.kind === 'numeric-benchmark'
      ? '离线数值基准（测试注入，不是大模型决策）'
      : `大模型推理：${planner?.model || '未配置'}${planner?.pending ? ' · 正在推理' : ''} · 无规则回退 · 可能产生模型费用`;
    $('jungleReasoning').textContent = state.route?.reasoning
      ? `模型决策：${state.route.reasoning.summary}\n决策游戏时钟 ${clock(state.route.reasoning.decidedAt)}；路线已按新状态重放核算。` : '';
    $('jungleTreads').value = state.options?.treadsAttribute || 'unknown';
    $('jungleTreads').disabled = !state.enabled || !this.canControl();
    if (!state.enabled) this.stopVoice();
    const player = state.player, stats = state.stats;
    $('jungleStats').textContent = player && stats
      ? `${clock(player.clock)} · Lv.${player.level} · HP ${integer(player.health)}/${integer(player.maxHealth)} · MP ${integer(player.mana)}\n` +
        `估算 ATK ${integer(stats.damage)} · ARM ${stats.armor.toFixed(1)} · ${stats.attacksPerSecond.toFixed(2)} 次/秒 · MS ${integer(stats.moveSpeed)}`
      : '等待完整的本机玩家数据；缺失坐标时不会猜测位置。';
    $('jungleVersion').textContent = `静态数据 ${state.patch} · 校准 ${state.calibration.toFixed(2)} · 未实机验收`;
    const camps = state.camps.filter(c => !player || c.team === player.team);
    if (!camps.some(c => c.id === this.selected)) this.selected = state.route?.steps[0]?.campId || camps[0]?.id;
    $('jungleCamp').replaceChildren(...camps.map(c => {
      const option = document.createElement('option');
      option.value = c.id; option.textContent = `${c.label}${c.clearedAt !== null ? '（曾清空）' : ''}`;
      return option;
    }));
    $('jungleCamp').value = this.selected || '';
    const route = state.route;
    const scrollTop = this.renderedPlanAt === route?.plannedAt ? $('jungleSteps').scrollTop : 0;
    $('jungleSteps').replaceChildren();
    if (route) {
      const uniqueCamps = new Set(route.steps.map(step => step.campId)).size;
      $('jungleSummary').textContent = `预计总金钱 ${integer(route.gold)} 金 · ${integer(route.horizonSeconds)} 秒窗口内金钱优先\n` +
        `约 ${integer(route.seconds)} 秒 · ${route.steps.length} 次清野 / ${uniqueCamps} 个野点 · ${integer(route.xp)} 经验（仅展示）`;
      const visited = new Set();
      let cumulativeGold = 0;
      for (const step of route.steps) {
        const item = document.createElement('li');
        item.dataset.campId = step.campId;
        item.dataset.gold = String(step.gold);
        const title = document.createElement('strong');
        title.textContent = `${step.label}${visited.has(step.campId) ? ' · 回访' : ''} · ${clock(step.arrival)} 到达`;
        visited.add(step.campId);
        cumulativeGold += step.gold;
        const money = document.createElement('span');
        money.className = 'jungle-step-gold';
        money.textContent = `预计 +${integer(step.gold)} 金 · 累计 ${integer(cumulativeGold)} 金`;
        const detail = document.createElement('span');
        detail.textContent = `路程 ${Math.ceil(step.travelSeconds)} 秒 · 清野 ${Math.ceil(step.clearRange[0])}–${Math.ceil(step.clearRange[1])} 秒` +
          (step.complete === false ? '（截止时未清空，仅计已击杀）' : '') +
          ` · 剩 HP 约 ${integer(step.healthAfter)} / MP ${integer(step.manaAfter)}` +
          (step.waitSeconds ? ` · 框外等待 ${Math.ceil(step.waitSeconds)} 秒` : '');
        const spells = document.createElement('small');
        spells.textContent = (step.spawnAt != null ? `假定 ${clock(step.spawnAt)} 刷新 · ` : '') +
          (Object.keys(step.casts).length ? `技能计划：${Object.entries(step.casts).map(([k, v]) => `${k} ×${v}`).join('，')}` : '仅普攻/被动');
        item.append(title, money, detail, spells); $('jungleSteps').append(item);
      }
    }
    $('jungleSteps').scrollTop = scrollTop;
    this.renderedPlanAt = route?.plannedAt ?? null;
    $('jungleWarnings').replaceChildren(...(route?.warnings || stats?.warnings || []).map(text => {
      const li = document.createElement('li'); li.textContent = text; return li;
    }));
    this.draw(); this.freshness();
  }
  draw() {
    const svg = $('jungleMap'); svg.replaceChildren();
    if (!this.state) return;
    // Real world coordinate projection, NOT an invented satellite/map texture.
    const project = p => [(p.x + 10000) / 20000 * 360, (10000 - p.y) / 20000 * 360];
    const element = (tag, attrs) => {
      const node = document.createElementNS(svgNS, tag);
      for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, String(v));
      svg.append(node); return node;
    };
    for (let i = 60; i < 360; i += 60) {
      element('path', { d: `M${i} 0V360 M0 ${i}H360`, stroke: '#2a3952', 'stroke-width': 0.5 });
    }
    if (this.fresh()) for (const step of this.state.route?.steps || []) {
      element('polyline', { points: step.path.map(p => project(p).join(',')).join(' '),
        fill: 'none', stroke: '#f5b942', 'stroke-width': 2 });
    }
    for (const camp of this.state.camps) {
      const [cx, cy] = project(camp);
      const node = element('circle', { cx, cy, r: camp.kind === 'ancient' ? 5 : 3.5,
        fill: camp.team === 'radiant' ? '#3dd68c' : '#ff5c5c', stroke: this.selected === camp.id ? '#fff' : 'none',
        'stroke-width': 2 });
      const title = document.createElementNS(svgNS, 'title'); title.textContent = camp.label; node.append(title);
      if (!this.state.player || camp.team === this.state.player.team) {
        node.style.cursor = 'pointer';
        node.addEventListener('click', () => { this.selected = camp.id; $('jungleCamp').value = camp.id; this.draw(); });
      }
    }
    if (this.fresh()) {
      const stops = new Map();
      (this.state.route?.steps || []).forEach((step, i) => {
        if (!stops.has(step.campId)) stops.set(step.campId, { position: step.position, visits: [] });
        stops.get(step.campId).visits.push(i + 1);
        if (step.waitAt) {
          const [x, y] = project(step.waitAt);
          const marker = element('path', { d: `M${x - 2} ${y - 2}h4v4h-4z`, fill: '#a78bfa' });
          const title = document.createElementNS(svgNS, 'title');
          title.textContent = `第 ${i + 1} 步：在此框外等待约 ${Math.ceil(step.waitSeconds)} 秒`;
          marker.append(title);
        }
      });
      for (const { position, visits } of stops.values()) {
        const [x, y] = project(position);
        const label = element('text', { x: x + 7, y: y - 7, fill: '#f5b942', 'font-size': 10, 'font-weight': 700 });
        label.textContent = visits.slice(0, 3).join('/') + (visits.length > 3 ? '/…' : '');
        const title = document.createElementNS(svgNS, 'title');
        title.textContent = `路线第 ${visits.join('、')} 步`; label.append(title);
      }
    }
    if (this.state.player) {
      const [cx, cy] = project(this.state.player.position);
      element('circle', { cx, cy, r: 5, fill: '#fff', stroke: '#4f9cff', 'stroke-width': 2 });
    }
  }
}
