const ATTRIBUTES = [
  { id: 0, name: '力量', en: 'STRENGTH', icon: 'strength' },
  { id: 1, name: '敏捷', en: 'AGILITY', icon: 'agility' },
  { id: 2, name: '智力', en: 'INTELLIGENCE', icon: 'intelligence' },
  { id: 3, name: '全才', en: 'UNIVERSAL', icon: 'universal' },
];
const norm = value => String(value).trim().toLowerCase();
const heroMatches = (hero, value) => [hero.en, hero.zh, hero.key].some(name => norm(name) === norm(value));
const element = (tag, className, text) => {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
};

function portrait(hero) {
  // Only checked-in, same-origin assets. Missing artwork still leaves a named, usable button.
  if (!/^\/heroes\/[a-z0-9_]+\.(jpg|png)$/.test(hero?.portrait || '')) return null;
  const image = element('img', 'hero-portrait');
  image.alt = '';
  image.src = hero.portrait;
  image.decoding = 'async';
  image.draggable = false;
  image.addEventListener('error', () => {
    image.hidden = true;
    image.parentElement?.classList.add('portrait-unavailable');
  }, { once: true });
  return image;
}

export class HeroPicker {
  constructor(onChange) {
    this.onChange = onChange;
    this.catalog = [];
    this.selected = [];
    this.editable = false;
    this.connected = false;
    this.filter = 'all';
    this.cards = [];
    this.groups = [];
    this.dialog = document.getElementById('heroPickerDialog');
    this.board = document.getElementById('heroPickerBoard');
    this.search = document.getElementById('heroPickerSearch');
    this.onlySelected = document.getElementById('heroOnlySelected');
    document.getElementById('openHeroPickerBtn').addEventListener('click', () => this.open());
    for (const id of ['clearHeroesBtn', 'clearPickerHeroesBtn']) {
      document.getElementById(id).addEventListener('click', () => this.commit([]));
    }
    this.search.addEventListener('input', () => this.filterCards());
    this.search.addEventListener('keydown', event => {
      if (event.key !== 'Enter' || event.isComposing) return;
      event.preventDefault();
      const visible = this.cards.filter(({ button }) => !button.hidden);
      if (visible.length === 1) this.toggle(visible[0].hero);
    });
    this.onlySelected.addEventListener('change', () => this.filterCards());
    document.getElementById('resetHeroFiltersBtn').addEventListener('click', () => this.resetFilters());
    document.getElementById('heroAttributeFilters').addEventListener('click', event => {
      const button = event.target.closest('[data-attribute-filter]');
      if (!button) return;
      this.filter = button.dataset.attributeFilter;
      this.filterCards();
    });
  }

  setCatalog(heroes) {
    this.catalog = [...heroes].sort((a, b) => a.en.localeCompare(b.en, 'en'));
    this.cards = [];
    this.groups = [];
    this.board.replaceChildren();
  }

  open() {
    if (!this.editable) return;
    if (!this.cards.length) this.buildBoard();
    this.resetFilters();
    this.renderSelection(this.selected, this.editable, this.connected);
    if (!this.dialog.open) this.dialog.showModal();
    this.search.focus();
  }

  resetFilters() {
    this.search.value = '';
    this.onlySelected.checked = false;
    this.filter = 'all';
    this.filterCards();
  }

  buildBoard() {
    const attributes = [...ATTRIBUTES];
    // Keep a newly added hero usable if the local attribute metadata has not been refreshed yet.
    if (this.catalog.some(hero => !ATTRIBUTES.some(attr => attr.id === hero.primaryAttr))) {
      attributes.push({ id: 'unknown', name: '其他', en: 'UNCLASSIFIED' });
    }
    for (const attribute of attributes) {
      const section = element('section', 'hero-group');
      section.dataset.attribute = attribute.id;
      section.setAttribute('aria-labelledby', `hero-attribute-${attribute.id}`);
      const heading = element('h3', 'hero-group-heading');
      heading.id = `hero-attribute-${attribute.id}`;
      if (attribute.icon) {
        const icon = element('img');
        icon.src = `/heroes/attr_${attribute.icon}.png`;
        icon.alt = '';
        icon.width = icon.height = 24;
        heading.append(icon);
      }
      heading.append(element('span', '', attribute.name), element('small', '', attribute.en));
      const count = element('span', 'hero-group-count');
      heading.append(count);
      const grid = element('div', 'hero-grid');
      const group = { section, count, cards: [] };
      for (const hero of this.catalog) {
        const heroAttribute = ATTRIBUTES.some(attr => attr.id === hero.primaryAttr) ? hero.primaryAttr : 'unknown';
        if (heroAttribute !== attribute.id) continue;
        const button = element('button', 'hero-card');
        button.type = 'button';
        button.dataset.hero = hero.en;
        button.title = `${hero.zh} · ${hero.en}`;
        button.setAttribute('aria-label', `${hero.zh} · ${hero.en}`);
        button.setAttribute('aria-pressed', 'false');
        const image = portrait(hero);
        if (image) button.append(image);
        else button.classList.add('portrait-unavailable');
        button.append(element('span', 'hero-card-name', hero.zh || hero.en));
        const check = element('span', 'hero-card-check', '✓');
        check.setAttribute('aria-hidden', 'true');
        button.append(check);
        button.addEventListener('click', () => this.toggle(hero));
        const card = {
          hero, button, attribute: String(attribute.id),
          search: [hero.en, hero.zh, hero.key, ...(hero.enAliases || []), ...(hero.zhSlang || [])].map(norm).join(' '),
        };
        this.cards.push(card);
        group.cards.push(card);
        grid.append(button);
      }
      section.append(heading, grid);
      this.groups.push(group);
      this.board.append(section);
    }
  }

  filterCards() {
    const query = norm(this.search.value);
    const focusedCard = document.activeElement?.closest('.hero-card');
    let total = 0;
    for (const card of this.cards) {
      const selected = this.selected.some(value => heroMatches(card.hero, value));
      card.button.hidden = !(card.search.includes(query) &&
        (this.filter === 'all' || this.filter === card.attribute) &&
        (!this.onlySelected.checked || selected));
      if (!card.button.hidden) total++;
    }
    for (const group of this.groups) {
      const visible = group.cards.filter(card => !card.button.hidden).length;
      group.count.textContent = visible;
      group.section.hidden = visible === 0;
    }
    document.querySelectorAll('[data-attribute-filter]').forEach(button =>
      button.setAttribute('aria-pressed', String(button.dataset.attributeFilter === this.filter)));
    this.board.classList.toggle('is-filtered', this.filter !== 'all' || !!query || this.onlySelected.checked);
    document.getElementById('heroPickerEmpty').hidden = total > 0;
    if (focusedCard?.hidden) this.search.focus();
  }

  feedback(message, error = false) {
    for (const id of ['heroSelectionFeedback', 'heroPickerFeedback']) {
      const node = document.getElementById(id);
      node.textContent = message;
      node.classList.toggle('is-error', error);
    }
  }

  commit(next) {
    if (!this.editable) return;
    if (!this.connected || !this.onChange(next)) {
      this.feedback('房间尚未连接，请重新连接后选择英雄。', true);
      return;
    }
    this.renderSelection(next, this.editable, this.connected);
  }

  toggle(hero) {
    if (!this.editable) return;
    const index = this.selected.findIndex(value => heroMatches(hero, value));
    if (index >= 0) {
      this.commit(this.selected.filter((_, i) => i !== index));
    } else if (this.selected.length >= 10) {
      this.feedback('已选满 10 位英雄，请先点击已选头像移除。', true);
    } else {
      this.commit([...this.selected, hero.en]);
    }
  }

  renderLineup(id) {
    const container = document.getElementById(id);
    const focusedIndex = container.contains(document.activeElement) ? document.activeElement.dataset.slot : null;
    container.replaceChildren();
    for (let index = 0; index < 10; index++) {
      const value = this.selected[index];
      const hero = this.catalog.find(candidate => value && heroMatches(candidate, value));
      const label = hero?.zh || value;
      const slot = element(this.editable ? 'button' : 'div', `hero-slot${value ? ' is-filled' : ''}`);
      slot.dataset.slot = index;
      if (this.editable) {
        slot.type = 'button';
        slot.setAttribute('aria-label', value ? `移除 ${label}` : `选择第 ${index + 1} 位英雄`);
        slot.setAttribute('aria-disabled', String(!this.connected && !!value));
        slot.addEventListener('click', () => {
          if (value) this.commit(this.selected.filter((_, i) => i !== index));
          else if (this.dialog.open) {
            this.resetFilters();
            this.search.focus();
          } else this.open();
        });
      }
      slot.title = value ? `${label}${hero ? ` · ${hero.en}` : ''}${this.editable ? ' · 点击移除' : ''}` : '待选择英雄';
      if (hero) {
        const image = portrait(hero);
        if (image) slot.append(image);
      }
      slot.append(element('span', 'hero-slot-index', String(index + 1).padStart(2, '0')));
      if (value) {
        slot.append(element('span', 'hero-slot-name', label));
        if (this.editable) {
          const remove = element('span', 'hero-slot-remove', '×');
          remove.setAttribute('aria-hidden', 'true');
          slot.append(remove);
        }
      } else {
        const empty = element('span', 'hero-slot-empty', this.editable ? '+' : '—');
        empty.setAttribute('aria-hidden', 'true');
        slot.append(empty);
      }
      container.append(slot);
    }
    if (focusedIndex !== null) container.querySelector(`[data-slot="${focusedIndex}"]`)?.focus();
  }

  renderSelection(selected, editable, connected) {
    this.selected = [...selected];
    this.editable = editable;
    this.connected = connected;
    const count = `${this.selected.length} / 10`;
    document.getElementById('matchHeroesCount').textContent = count;
    document.getElementById('heroPickerCount').textContent = count;
    document.getElementById('clearHeroesBtn').hidden = !editable || !selected.length;
    document.getElementById('clearHeroesBtn').disabled = !connected;
    document.getElementById('clearPickerHeroesBtn').disabled = !editable || !connected || !selected.length;
    this.renderLineup('selectedHeroTags');
    this.renderLineup('heroPickerLineup');
    for (const { hero, button } of this.cards) {
      const picked = selected.some(value => heroMatches(hero, value));
      button.setAttribute('aria-pressed', String(picked));
      button.setAttribute('aria-disabled', String(!editable || !connected || (!picked && selected.length >= 10)));
    }
    this.filterCards();
    this.feedback(!connected && editable ? '房间连接中，连接后即可选择英雄。'
      : selected.length ? (editable ? '点击已选头像可移除 · 更改即时同步到房间' : '阵容由房主或自动识阵同步')
        : '未限定阵容，通用英雄词表仍然生效。');
  }
}
