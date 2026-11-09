/* Hemsidan (klubbsidorna 2b): redigeringsytan för klubbens och kretsens egna sidor.
 *
 * Allt går genom SiteEditController (/umbraco/surface/SiteEdit/...), som prövar ägaren, villkoren, antiforgery och innehållet på servern. Det här
 * skriptet är bara ytan: servern litar aldrig på något härifrån. All text som skrivs ut sätts med textContent (aldrig innerHTML med data), och
 * rik text lever bara i CKEditor och skickas som HTML som servern saneras om.
 *
 * Autospar (ett utkast, skrivet på plats) — det som läses av besökare ändras först vid "Publicera ändringar". Konflikt (någon annan hann före),
 * personnummer och ogiltigt innehåll visas som tydliga rutor, aldrig som alert().
 */
(function () {
  'use strict';
  const root = document.getElementById('hemsidan-app');
  if (!root) return;
  const OWNER = { type: root.dataset.ownerType, id: parseInt(root.dataset.ownerId, 10), name: root.dataset.ownerName || '', url: root.dataset.ownerUrl || '/' };
  const body = document.getElementById('hsBody');
  const TERMS = JSON.parse(document.getElementById('hsTerms').textContent);
  const SAVE_IDLE_MS = 8000;

  // ── Hjälpare ───────────────────────────────────────────────────────────────
  function h(tag, attrs, ...kids) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v == null || v === false) continue;
      if (k === 'class') e.className = v;
      else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
      else if (k === 'value') e.value = v;
      else if (k === 'checked') e.checked = true;
      else if (k === 'disabled') e.disabled = true;
      else e.setAttribute(k, v === true ? '' : v);
    }
    for (const c of kids.flat()) { if (c == null || c === false) continue; e.append(c.nodeType ? c : document.createTextNode(String(c))); }
    return e;
  }
  const token = () => root.querySelector('input[name="__RequestVerificationToken"]').value;

  async function api(method, action, params) {
    const q = new URLSearchParams({ ownerType: OWNER.type, ownerId: OWNER.id });
    for (const [k, v] of Object.entries(params || {})) if (v !== undefined && v !== null) q.append(k, String(v));
    const opt = { method, credentials: 'same-origin', headers: {} };
    let url = '/umbraco/surface/SiteEdit/' + action;
    if (method === 'GET') url += '?' + q.toString();
    else {
      q.append('__RequestVerificationToken', token());
      opt.headers['Content-Type'] = 'application/x-www-form-urlencoded';
      opt.headers['RequestVerificationToken'] = token();
      opt.body = q.toString();
    }
    try {
      const r = await fetch(url, opt);
      const j = await r.json();
      return j;
    } catch {
      return { success: false, message: 'Det gick inte att nå servern. Kontrollera uppkopplingen och försök igen.', code: 'net' };
    }
  }

  const IS_REGION = OWNER.type === 'Region';
  const VIS = [
    { v: 0, t: 'Alla', s: 'Alla, även den som inte är inloggad' },
    { v: 1, t: 'Inloggade', s: 'Alla som är inloggade på pistol.nu' },
    IS_REGION ? { v: 2, t: 'Medlemmar i kretsens klubbar', s: 'Bara aktiva medlemmar i en klubb i kretsen' } : { v: 2, t: 'Medlemmar', s: 'Bara aktiva medlemmar i klubben' },
    IS_REGION ? { v: 3, t: 'Kretsstyrelsen', s: 'Bara kretsstyrelsen' } : { v: 3, t: 'Styrelsen', s: 'Bara styrelsen' },
    IS_REGION ? { v: 4, t: 'Kretsadministratörer', s: 'Bara kretsens administratörer' } : { v: 4, t: 'Administratörer', s: 'Bara klubbens administratörer' },
  ];
  const visOptions = () => VIS;
  const visLabel = v => (VIS.find(o => o.v === v) || VIS[4]).t;
  const CONSEQUENCE = {
    0: 'Sidan kan hittas och läsas av vem som helst på internet. Skriv inga personuppgifter.',
    1: 'Den som har ett konto på pistol.nu kan läsa sidan.',
    2: IS_REGION ? 'Bara aktiva medlemmar i en av kretsens klubbar kan läsa sidan. Andra som öppnar länken ser ingenting.' : 'Bara medlemmar i klubben kan läsa sidan. Andra som öppnar länken ser ingenting.',
    3: IS_REGION ? 'Bara kretsstyrelsen kan läsa sidan. Andra får beskedet att sidan inte finns.' : 'Bara styrelsen kan läsa sidan. Andra får beskedet att sidan inte finns.',
    4: IS_REGION ? 'Bara kretsens administratörer kan läsa sidan. Andra får beskedet att sidan inte finns.' : 'Bara klubbens administratörer kan läsa sidan. Andra får beskedet att sidan inte finns.',
  };
  const BLOCK_TYPES = [
    { type: 'text', label: 'Text', icon: 'bi-text-paragraph' },
    { type: 'heading', label: 'Rubrik', icon: 'bi-type-h2' },
    { type: 'links', label: 'Länkar', icon: 'bi-link-45deg' },
    { type: 'documents', label: 'Dokument', icon: 'bi-file-earmark-text' },
    { type: 'contact', label: 'Kontaktuppgifter', icon: 'bi-person-lines-fill' },
    { type: 'events', label: 'Kommande händelser', icon: 'bi-calendar-event' },
    { type: 'news', label: 'Nyheter', icon: 'bi-newspaper' },
    { type: 'pagelink', label: 'Länk till en sida', icon: 'bi-box-arrow-up-right' },
    { type: 'image', label: 'Bild', icon: 'bi-image' },
    // Kretsens egna block (2d): läses live ur kretsens data, lagrar inget i sidan.
    { type: 'region-clubs', label: 'Klubbar i kretsen', icon: 'bi-people', region: true },
    { type: 'region-ranges', label: 'Skjutbanor', icon: 'bi-geo-alt', region: true },
    { type: 'region-series', label: 'Serier', icon: 'bi-trophy', region: true },
    { type: 'region-stats', label: 'Kretsen i siffror', icon: 'bi-bar-chart', region: true },
  ].filter(t => !t.region || IS_REGION);
  const typeInfo = t => BLOCK_TYPES.find(x => x.type === t) || { label: t, icon: 'bi-square' };
  const minVisFor = type => Math.max(S.visibility, type === 'contact' ? 1 : 0);   // personblock aldrig "Alla"; ett block aldrig öppnare än sidan
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(8)), b => 'abcdefghijklmnopqrstuvwxyz0123456789'[b % 36]).join('');
  const pad = n => String(n).padStart(2, '0');
  const clock = () => { const d = new Date(); return pad(d.getHours()) + ':' + pad(d.getMinutes()); };

  const S = {
    st: null, page: null, blocks: [], title: '', visibility: 2, revision: 0, published: false, hasUnpublished: false,
    kind: 'free', minVis: 0, templateKey: null,
    dirty: false, editVersion: 0, saving: false, timer: null, ack: new Set(), editors: [], docs: null, banner: null, statusText: '', statusErr: false, hint: null,
  };

  // ── Dialoger (bootstrap-modal, aldrig alert/confirm) ────────────────────────
  function modal(title, bodyNodes, buttons, wide) {
    return new Promise(resolve => {
      const el = h('div', { class: 'modal fade', tabindex: '-1' },
        h('div', { class: 'modal-dialog modal-dialog-centered' + (wide ? ' modal-xl' : '') }, h('div', { class: 'modal-content' },
          h('div', { class: 'modal-header' }, h('h2', { class: 'modal-title h5' }, title), h('button', { type: 'button', class: 'btn-close', 'data-bs-dismiss': 'modal', 'aria-label': 'Stäng' })),
          h('div', { class: 'modal-body' }, bodyNodes),
          h('div', { class: 'modal-footer' }, buttons.map(b => h('button', {
            type: 'button', class: 'btn ' + (b.cls || 'btn-secondary'), disabled: b.disabled,
            onclick: () => { b.done = true; bs.hide(); resolve(b.value); },
          }, b.label))))));
      document.body.append(el);
      const bs = new bootstrap.Modal(el);
      el.addEventListener('hidden.bs.modal', () => { el.remove(); resolve(null); });
      bs.show();
    });
  }
  const confirmDialog = (title, text, ok, danger) => modal(title, h('p', {}, text), [
    { label: 'Avbryt', value: false }, { label: ok, value: true, cls: danger ? 'btn-danger' : 'btn-primary' }]).then(v => v === true);

  // ── Vyer ───────────────────────────────────────────────────────────────────
  function clearEditors() { for (const e of S.editors) { try { e.destroy(); } catch { /* redan borta */ } } S.editors = []; }
  function view(nodes) { clearEditors(); clearTimeout(S.timer); S.refreshWho = null; body.replaceChildren(...[].concat(nodes).filter(Boolean)); }
  const header = (sub) => h('div', { class: 'mb-3' },
    h('p', { class: 'mb-1' }, h('a', { href: OWNER.url, class: 'text-decoration-none' }, h('i', { class: 'bi bi-arrow-left me-1' }), OWNER.name)),
    h('h1', { class: 'h3 mb-1' }, 'Hemsidan'), sub ? h('p', { class: 'text-muted mb-0' }, sub) : null, supportBanner());

  // Stödläget: en person som inte är föreningens egen admin ändrar i ett loggat, tidsbegränsat läge. Det syns hela tiden, med ett sätt att avsluta.
  function supportBanner() {
    const sup = S.st && S.st.support;
    if (!sup) return null;
    const until = new Date(sup.expires);
    return h('div', { class: 'alert alert-warning d-flex flex-wrap gap-2 align-items-center mt-2', role: 'status' },
      h('span', { class: 'me-auto' }, h('strong', {}, 'Stödläge. '), 'Du ändrar ' + OWNER.name + 's sidor som stödperson. Allt loggas och föreningens administratörer meddelas. Slutar ' + pad(until.getHours()) + ':' + pad(until.getMinutes()) + '. Anledning: ' + sup.reason),
      h('button', { class: 'btn btn-sm btn-outline-dark', onclick: async () => { await api('POST', 'EndSupport'); S.st = null; load(); } }, 'Avsluta stödläge'));
  }

  function renderSupportStart() {
    const reason = h('textarea', { class: 'form-control', rows: '3', maxlength: '300', 'aria-label': 'Anledning' });
    const msg = h('div');
    const btn = h('button', { class: 'btn btn-primary', onclick: async () => {
      btn.disabled = true;
      const r = await api('POST', 'StartSupport', { reason: reason.value });
      if (!r.success) { btn.disabled = false; msg.replaceChildren(alertBox('danger', r.message || 'Stödläget kunde inte öppnas.')); return; }
      S.st = null; load();
    } }, 'Öppna stödläge');
    view([h('div', { class: 'mb-3' }, h('p', { class: 'mb-1' }, h('a', { href: OWNER.url, class: 'text-decoration-none' }, h('i', { class: 'bi bi-arrow-left me-1' }), OWNER.name)), h('h1', { class: 'h3' }, 'Hemsidan')),
      h('div', { class: 'hs-card p-3 p-md-4', style: 'max-width:46rem' },
        h('h2', { class: 'h5' }, 'Du är inte ' + OWNER.name + 's egen administratör'),
        h('p', {}, 'Du kan ändra föreningens sidor i ett stödläge. Det varar i 60 minuter, loggas, och föreningens egna administratörer får ett mejl med din anledning.'),
        h('label', { class: 'form-label fw-semibold' }, 'Varför behöver du ändra föreningens sidor?'), reason,
        h('div', { class: 'form-text mb-3' }, 'Minst tio tecken. Föreningens administratörer får läsa det du skriver.'), msg, btn)]);
  }
  const alertBox = (cls, ...kids) => h('div', { class: 'alert alert-' + cls }, kids);

  // ── Skalet (förslag 3, Ekonomins skal): vänstermeny med en plats per flik, en kolumn till höger ──────────
  // Platserna adresseras med hash (#sidor, #sida=home, #redigera=12, #raderade, #logg, #ny) så att bakåtknappen och en delad länk fungerar.
  // Varje vy sätter sin hash tyst (setHash); bara en hash som användaren själv ändrar leder till route().
  function setHash(hash) { if (location.hash === hash) return; S.hashSilent = hash; location.hash = hash; }
  // Lämnar man redigeraren läses läget om (publicerat, avbrutet), så att menyn och sidplatsen visar det som nu gäller.
  async function leaveEditor() {
    if (S.dirty) await saveNow();
    if (/^#redigera=/.test(location.hash)) { const st = await api('GET', 'State'); if (st.success) S.st = st; }
  }
  async function go(hash) { await leaveEditor(); if (location.hash !== hash) setHash(hash); route(); }
  function route() {
    const hsh = location.hash || '#sidor';
    let m;
    if ((m = /^#sida=([\w-]+)$/.exec(hsh))) return renderSystemPage(m[1]);
    if ((m = /^#redigera=(\d+)$/.exec(hsh))) return openEditor(parseInt(m[1], 10));
    if (hsh === '#raderade') return renderTrash();
    if (hsh === '#logg') return renderAudit();
    if (hsh === '#ny') return renderNew();
    return renderList();
  }
  window.addEventListener('hashchange', async () => {
    if (S.hashSilent === location.hash) { S.hashSilent = null; return; }
    S.hashSilent = null;
    if (!S.st || !S.st.termsAccepted) return;
    if (S.dirty) await saveNow();
    const st = await api('GET', 'State'); if (st.success) S.st = st;   // bakåtknappen/en delad länk: läs om läget (publicerat, avbrutet) innan vyn ritas
    route();
  });

  const MODE_CLASS = { asBefore: 'off', draft: 'wait', converted: 'ok' };
  const modePill = p => h('span', { class: 'hs-state ' + (MODE_CLASS[p.mode] || 'off') }, p.word);

  function rail(active) {
    const st = S.st, sys = st.systemPages || [];
    const nav = (key, label, hash, extra) => h('a', { href: hash, class: 'hs-nav' + (active === key ? ' on' : ''), 'aria-current': active === key ? 'page' : null,
      onclick: e => { e.preventDefault(); go(hash); } }, h('span', {}, label), extra || null);
    const pill = p => p.mode === 'draft' ? h('span', { class: 'hs-pill warn' }, 'Utkast') : p.mode === 'converted' ? h('span', { class: 'hs-pill own' }, 'Egen version') : null;
    const here = active === 'sidor' ? 'Sidor' : active === 'raderade' ? 'Raderade sidor' : active === 'logg' ? 'Vad som hänt' : ((sys.find(p => 'sys:' + p.key === active) || {}).label || 'Sidor');
    return h('nav', { class: 'hs-rail', 'aria-label': 'Hemsidans delar' },
      h('a', { href: OWNER.url, class: 'hs-back' }, h('i', { class: 'bi bi-arrow-left me-1' }), 'Tillbaka till ' + (IS_REGION ? 'kretsen' : 'klubben')),
      h('div', { class: 'hs-org' }, 'Hemsidan'),
      h('div', { class: 'hs-role' }, (st.support ? 'Stödläge' : IS_REGION ? 'Kretsadmin' : 'Klubbadmin') + ' · ' + OWNER.name),
      h('button', { type: 'button', class: 'hs-railtoggle', 'aria-expanded': 'false', 'aria-controls': 'hsRailNav',
        onclick: e => { const r = e.currentTarget.closest('.hs-rail'); const o = r.classList.toggle('open'); e.currentTarget.setAttribute('aria-expanded', String(o)); } },
        h('span', {}, 'Meny · ' + here), h('i', { class: 'bi bi-list', 'aria-hidden': 'true' })),
      h('div', { class: 'hs-railnav', id: 'hsRailNav' },
        nav('sidor', 'Sidor', '#sidor'),
        h('div', { class: 'hs-railh' }, IS_REGION ? 'Kretsens vanliga sidor' : 'Klubbens vanliga sidor'),
        sys.map(p => nav('sys:' + p.key, p.label, '#sida=' + p.key, pill(p))),
        st.trash.length || !st.support ? h('div', { class: 'hs-railh' }, 'Mer') : null,
        st.trash.length ? nav('raderade', 'Raderade sidor', '#raderade', h('span', { class: 'hs-pill' }, st.trash.length)) : null,
        st.support ? null : nav('logg', 'Vad som hänt', '#logg')));
  }
  /** Skalet runt en vy: rälsen till vänster, innehållet (och stödlägesbanderollen) till höger. */
  function shell(active, ...nodes) {
    return h('div', { class: 'hs-shell' }, rail(active), h('div', { class: 'hs-main' }, supportBanner(), nodes));
  }
  /** Rubrik med blå Åtgärder uppe till höger (adminlistans form, som Träningar). */
  function pageHead(title, sub, menuItems) {
    return h('div', { class: 'hs-head' }, h('div', {}, h('h1', {}, title), sub ? h('div', { class: 'hs-sub' }, sub) : null),
      menuItems && menuItems.filter(Boolean).length ? h('div', { class: 'dropdown' },
        h('button', { class: 'btn btn-primary shadow dropdown-toggle', type: 'button', 'data-bs-toggle': 'dropdown', 'data-bs-popper-config': '{"strategy":"fixed"}', 'aria-expanded': 'false' }, 'Åtgärder'),
        menuList(menuItems)) : null);
  }
  /** Menyrader: [text, fn, { href?, danger?, disabled? }]; '-' = avdelare; null hoppas över. */
  function menuList(items) {
    return h('ul', { class: 'dropdown-menu dropdown-menu-end' }, items.filter(Boolean).map(it => it === '-' ? h('li', {}, h('hr', { class: 'dropdown-divider' }))
      : h('li', {}, h('a', { class: 'dropdown-item' + (it[2] && it[2].danger ? ' text-danger' : '') + (it[2] && it[2].disabled ? ' disabled' : ''), href: (it[2] && it[2].href) || '#',
        target: it[2] && it[2].href ? '_blank' : null, rel: it[2] && it[2].href ? 'noopener' : null,
        onclick: e => { if (it[2] && it[2].href) return; e.preventDefault(); if (!(it[2] && it[2].disabled)) it[1](); } }, it[0]))));
  }
  /** Grå Åtgärder per rad (menyn är fixed-positionerad så att den inte klipps i tabellen). */
  const rowMenu = items => h('div', { class: 'dropdown' },
    h('button', { class: 'btn btn-sm btn-outline-secondary dropdown-toggle', type: 'button', 'data-bs-toggle': 'dropdown', 'data-bs-popper-config': '{"strategy":"fixed"}', 'aria-expanded': 'false' }, 'Åtgärder'),
    menuList(items));

  async function load() {
    view(h('p', { class: 'text-muted' }, 'Laddar …'));
    const st = await api('GET', 'State');
    if (!st.success && st.code === 'support') return renderSupportStart();
    if (!st.success) { view([header(), alertBox('warning', st.message || 'Hemsidan kan inte öppnas.')]); return; }
    S.st = st;
    if (!st.termsAccepted) return renderTerms();
    route();
  }

  function renderTerms() {
    const chk = h('input', { type: 'checkbox', class: 'form-check-input', id: 'hsTermsOk' });
    const btn = h('button', { class: 'btn btn-primary', disabled: true, onclick: async () => {
      btn.disabled = true;
      const r = await api('POST', 'AcceptTerms');
      if (!r.success) { btn.disabled = false; msg.replaceChildren(alertBox('danger', r.message || 'Det gick inte att spara.')); return; }
      load();
    } }, 'Godkänn och fortsätt');
    chk.addEventListener('change', () => { btn.disabled = !chk.checked; });
    const msg = h('div');
    view([header('Innan ni börjar'), h('div', { class: 'hs-card p-3 p-md-4', style: 'max-width:46rem' },
      h('p', {}, 'Här kan ' + OWNER.name + ' skriva egna sidor som visas på pistol.nu.'),
      h('h2', { class: 'h6' }, 'Det här kan du göra'),
      h('ul', { class: 'mb-3' },
        h('li', { class: 'mb-1' }, 'Skriva en egen sida, till exempel banregler, hitta hit eller information till nya medlemmar.'),
        h('li', { class: 'mb-1' }, 'Bestämma vem som får se varje sida: alla, inloggade, klubbens medlemmar eller bara styrelsen.'),
        h('li', { class: 'mb-1' }, 'Lägga egen text före eller efter de vanliga sidorna (Hem, Kalender och så vidare).'),
        h('li', { class: 'mb-1' }, 'Se sidan som en medlem eller en besökare ser den innan du publicerar.')),
      h('h2', { class: 'h6' }, 'Läs det här först'),
      h('ul', {}, TERMS.map(t => h('li', { class: 'mb-1' }, t))),
      h('div', { class: 'form-check my-3' }, chk, h('label', { class: 'form-check-label', for: 'hsTermsOk' }, 'Jag har läst och godkänner villkoren för ' + OWNER.name + 's hemsida.')),
      msg, btn)]);
  }

  function statusBadge(p) { return h('span', { class: 'badge ' + (p.status === 'Utkast' ? 'bg-secondary' : p.status === 'Publicerad' ? 'bg-success' : 'bg-warning text-dark') }, p.status); }
  const visBadge = v => h('span', { class: 'badge hs-badge-' + v }, visLabel(v));

  async function copyLink(url) {
    const full = location.origin + url;
    try { await navigator.clipboard.writeText(full); } catch {
      const t = h('textarea', { style: 'position:fixed;opacity:0' }); t.value = full; document.body.append(t); t.select(); try { document.execCommand('copy'); } catch { /* ingen utklippshanterare */ } t.remove();
    }
    flash('Länken är kopierad. Den fungerar för: ' + (VIS.find(o => o.v === S.visibility)?.s || 'dem som får se sidan').toLowerCase() + '.');
  }
  function flash(text) { S.banner = { cls: 'success', text }; const b = document.getElementById('hsBanner'); if (b) renderBanner(); }

  // Översikten (förslag 3): EN tabell över alla sidor — klubbens vanliga sidor och de egna — med läget som ord. Åtgärder är blå uppe till höger, radmenyn grå.
  function renderList() {
    setHash('#sidor');
    const st = S.st, sys = st.systemPages || [];
    const slotByKey = Object.fromEntries((st.slots || []).map(x => [x.key, x]));
    const hiddenCount = st.pages.filter(p => p.visibility === 4).length;
    const own = st.pages.filter(p => S.showHidden || p.visibility !== 4);
    const total = sys.length + st.pages.length, shown = sys.length + own.length;
    const full = st.pages.length >= st.maxPages;

    const sysRows = sys.map(p => {
      const slot = slotByKey[p.key];
      return h('tr', {},
        h('td', {}, h('a', { href: '#sida=' + p.key, class: 'fw-semibold text-decoration-none', onclick: e => { e.preventDefault(); go('#sida=' + p.key); } }, p.label)),
        h('td', {}, modePill(p), slot && slot.page && p.mode === 'asBefore' ? h('span', { class: 'text-muted small ms-2' }, 'med egen text') : null),
        h('td', {}, visBadge(p.level)),
        h('td', { class: 'text-end' }, rowMenu([['Öppna', () => go('#sida=' + p.key)]])));
    });
    const ownRows = own.map(p => h('tr', {},
      h('td', {}, h('a', { href: '#redigera=' + p.id, class: 'fw-semibold text-decoration-none', onclick: e => { e.preventDefault(); go('#redigera=' + p.id); } }, p.title)),
      h('td', {}, 'Egen sida ', statusBadge(p)),
      h('td', {}, visBadge(p.visibility)),
      h('td', { class: 'text-end' }, rowMenu([
        ['Ändra', () => go('#redigera=' + p.id)],
        p.status !== 'Utkast' && p.url ? ['Se sidan', null, { href: p.url }] : null,
        p.status !== 'Utkast' && p.url ? ['Kopiera länk', () => { S.visibility = p.visibility; copyLink(p.url); }] : null,
        '-',
        ['Radera', async () => {
          if (!await confirmDialog('Radera sidan?', 'Sidan "' + p.title + '" tas bort från sajten. Ni kan ångra det i 30 dagar.', 'Radera', true)) return;
          const r = await api('POST', 'DeletePage', { pageId: p.id, expectedRevision: p.revision });
          S.banner = r.success ? { cls: 'success', text: 'Sidan är raderad. Du kan ångra under "Raderade sidor".' } : { cls: 'danger', text: r.message };
          await reloadState();
        }, { danger: true }]]))));

    const filter = h('div', { class: 'd-flex flex-wrap gap-3 align-items-center px-3 pt-3 pb-1' },
      hiddenCount ? h('label', { class: 'd-flex gap-2 align-items-center' }, h('input', { type: 'checkbox', class: 'form-check-input mt-0', checked: !!S.showHidden,
        onchange: e => { S.showHidden = e.target.checked; renderList(); } }), 'Visa dolda (' + hiddenCount + ')') : null,
      h('span', { class: 'text-muted ms-auto' }, shown + ' av ' + total));
    view(shell('sidor',
      pageHead('Sidor', 'Här ser du alla sidor på ' + (IS_REGION ? 'kretsens' : 'klubbens') + ' hemsida. De vanliga sidorna finns redan. Egna sidor lägger du till under Åtgärder → Ny sida.', [
        ['Ny sida', () => go('#ny'), full ? { disabled: true } : null],
        st.trash.length ? ['Raderade sidor (' + st.trash.length + ')', () => go('#raderade')] : null]),
      h('div', { id: 'hsBanner' }),
      h('div', { class: 'hs-card p-0 overflow-hidden mb-3' }, filter,
        h('table', { class: 'table align-middle hs-list mb-0' },
          h('thead', {}, h('tr', {}, h('th', { class: 'ps-3' }, 'Sida'), h('th', {}, 'Vad är det'), h('th', {}, 'Vem ser den'), h('th', { class: 'text-end pe-3' }, 'Åtgärder'))),
          h('tbody', {}, sysRows, ownRows))),
      st.pages.length ? null : h('p', { class: 'text-muted' }, 'Ni har inga egna sidor än. Skapa en för till exempel banregler, klubbens historia eller hitta hit.'),
      h('p', { class: 'text-muted small' }, st.pages.length + ' av högst ' + st.maxPages + ' egna sidor.')));
    renderBanner();
  }

  // En vanlig sida (Hem, Om klubben …) som en plats i menyn: läget som ord, vad som visas, och ägarens egen text före/efter. Sidan är exakt som förut utan den.
  function renderSystemPage(key) {
    const st = S.st, p = (st.systemPages || []).find(x => x.key === key);
    if (!p) return renderList();
    setHash('#sida=' + key);
    const slot = (st.slots || []).find(x => x.key === key);
    const intro = p.mode === 'asBefore' ? 'Den här sidan visas som den alltid har gjort. Innehållet hämtas automatiskt.' + (p.canConvert ? ' Vill ni ändra hur sidan ser ut? Välj Åtgärder → Ändra den här sidan.' : '')
      : p.mode === 'draft' ? 'Det finns ett utkast till en egen version. Besökarna ser fortfarande den vanliga sidan.'
      : 'Sidan är er egen version.' + (p.pendingChanges ? ' Det finns ändringar som inte är publicerade.' : '');
    const actions = p.mode === 'asBefore' ? [p.canConvert ? ['Ändra den här sidan', () => convertFlow(p)] : null]
      : p.mode === 'draft' ? [['Fortsätt ändra utkastet', () => go('#redigera=' + p.pageId)], '-', ['Avbryt omvandlingen', () => cancelConversionFlow(p.key, p.pageId, p.revision, false)]]
      : [['Ändra sidan', () => go('#redigera=' + p.pageId)], '-', ['Gå tillbaka till den vanliga sidan', () => revertFlow(p)]];
    const panelsCard = p.mode === 'asBefore' && p.panels.length
      ? h('div', { class: 'hs-card p-3 mb-3' }, h('h2', { class: 'h5 mb-2' }, 'Det här visas på sidan'),
          p.panels.map(x => h('div', { class: 'hs-panelrow' }, h('div', {}, h('strong', {}, x.name), h('div', { class: 'text-muted small' }, x.description)),
            h('span', { class: 'text-muted small' }, 'Hämtas automatiskt'))))
      : null;
    view(shell('sys:' + key,
      pageHead(p.label, [modePill(p), ' ', intro], actions),
      h('div', { id: 'hsBanner' }),
      panelsCard, p.mode === 'asBefore' ? whoSeesCard(() => ({ templateKey: p.key }), p.canConvert
        ? { change: { intro: 'Vill du bestämma vem som ser delarna av sidan?', label: 'Ändra den här sidan', run: () => convertFlow(p) } }
        : { note: 'Vem som ser den här sidan går inte att ändra.' }) : null, p.mode === 'asBefore' ? slotCard(slot) : null));
    renderBanner();
  }

  // ── Ändra den här sidan / Avbryt / Gå tillbaka ──────────────────────────────
  async function afterChange(key, banner) {
    const st = await api('GET', 'State'); if (st.success) S.st = st;
    S.banner = banner;
    go('#sida=' + key);
  }
  async function convertFlow(p) {
    const ok = await confirmDialog('Ändra den här sidan?', 'Vi gör en kopia av hur "' + p.label + '" ser ut i dag. I kopian kan ni flytta och ta bort innehåll och lägga till eget. Besökarna ser inte kopian förrän ni publicerar den. Tills dess ser de er vanliga sida, och ni kan ångra när ni vill.', 'Ändra sidan', false);
    if (!ok) return;
    const r = await api('POST', 'ConvertPage', { templateKey: p.key });
    if (!r.success) { S.banner = { cls: 'danger', text: r.message || 'Sidan kunde inte göras om.' }; renderSystemPage(p.key); return; }
    const st = await api('GET', 'State'); if (st.success) S.st = st;
    go('#redigera=' + r.pageId);
  }
  async function cancelConversionFlow(key, pageId, revision, fromEditor) {
    const ok = await confirmDialog('Avbryta?', 'Utkastet kastas. Besökarna ser som förut den vanliga sidan, och den ändras inte.', 'Kasta utkastet', true);
    if (!ok) return;
    if (fromEditor) { clearTimeout(S.timer); S.dirty = false; }
    const r = await api('POST', 'CancelConversion', { pageId, expectedRevision: revision });
    if (!r.success) { setBanner('danger', r.message || 'Det gick inte att avbryta.'); return; }
    afterChange(key, { cls: 'success', text: 'Utkastet är kastat. Sidan är som förut.' });
  }
  async function revertFlow(p) {
    const ok = await confirmDialog('Gå tillbaka till den vanliga sidan?', 'Besökarna ser den vanliga sidan direkt. Det ni har ändrat på "' + p.label + '" försvinner från sajten. Ni kan göra om sidan igen, men då börjar ni från den vanliga sidan.', 'Gå tillbaka till den vanliga sidan', true);
    if (!ok) return;
    const r = await api('POST', 'RevertConversion', { pageId: p.pageId, expectedRevision: p.revision });
    if (!r.success) { S.banner = { cls: 'danger', text: r.message || 'Det gick inte att gå tillbaka.' }; renderSystemPage(p.key); return; }
    afterChange(p.key, { cls: 'success', text: 'Sidan är som förut. Besökarna ser den vanliga sidan.' });
  }

  // Egen text före/efter en vanlig sida: en mallkopia per sida. Sidan är exakt som förut utan den.
  function slotCard(x) {
    if (!x) return null;
    return h('div', { class: 'hs-card p-3 mb-3' }, h('h2', { class: 'h5 mb-1' }, 'Egen text på sidan'),
      h('p', { class: 'text-muted mb-2' }, 'Skriv egen text före eller efter innehållet. Sidan är som förut tills ni lägger till något.'),
      h('div', { class: 'd-flex flex-wrap gap-2 align-items-center' },
        x.page ? [visBadge(x.page.visibility), statusBadge({ status: x.page.status })] : h('span', { class: 'text-muted' }, 'Ingen egen text'),
        h('button', { class: 'btn btn-sm btn-outline-secondary ms-auto', onclick: () => x.page ? go('#redigera=' + x.page.id) : createSlot(x) }, x.page ? 'Ändra' : 'Lägg till egen text')));
  }

  // Ingen fråga före: texten skapas med det säkra förvalet (medlemmar, inloggade för en krets, aldrig öppnare än sidan tillåter) och inget publiceras förrän man själv
  // publicerar. Vem som ser texten väljs i editorn, som redan frågar det (och ber om en bekräftelse för "Alla, även utloggade").
  async function createSlot(x) {
    const vis = Math.min(3, Math.max(x.minVisibility, OWNER.type === 'Region' ? 1 : 2));
    const r = await api('POST', 'CreateSlotPage', { templateKey: x.key, visibility: vis });
    if (!r.success) { S.banner = { cls: 'danger', text: r.message || 'Det gick inte att skapa.' }; renderList(); return; }
    await reloadState();
    await openEditor(r.pageId);
    setBanner('info', 'Texten är ett utkast och syns för ' + visLabel(vis).toLowerCase() + ' när du publicerar. Ändra det under "Vem ser texten?" om du vill.');
  }

  // "Vad som hänt": vem gjorde vad och när (senaste 100). Bara föreningens egna administratörer läser loggen, så den visas inte i stödläge.
  function renderAudit() {
    if (S.st && S.st.support) return renderList();
    setHash('#logg');
    const out = h('div', { class: 'hs-card p-3' });
    view(shell('logg', pageHead('Vad som hänt', 'Vem som gjorde vad på hemsidan, och när (de senaste 100).'), out));
    auditLoad(out);
  }
  function auditLoad(out) {
    const fmt = s => new Date(s).toLocaleString('sv-SE', { dateStyle: 'short', timeStyle: 'short' });
    const load = async () => {
      out.replaceChildren(h('span', { class: 'text-muted' }, 'Laddar …'));
      const r = await api('GET', 'Audit');
      if (!r.success) { out.replaceChildren(alertBox('danger', r.message || 'Det gick inte att hämta loggen.')); return; }
      if (!r.rows.length) { out.replaceChildren(h('p', { class: 'text-muted mb-0' }, 'Inget har hänt än.')); return; }
      out.replaceChildren(h('div', { class: 'table-responsive' }, h('table', { class: 'table table-sm align-middle mb-0' }, h('tbody', {}, r.rows.map(x => h('tr', {},
        h('td', { class: 'text-nowrap text-muted' }, fmt(x.when)),
        h('td', {}, h('strong', {}, x.who), x.support ? h('span', { class: 'badge text-bg-warning ms-1' }, 'stödläge') : null, ' ' + x.text)))))));
    };
    load();
  }

  function renderBanner() {
    const el = document.getElementById('hsBanner'); if (!el) return;
    el.replaceChildren(S.banner ? alertBox(S.banner.cls, S.banner.text, S.banner.link ? [' ', h('a', { href: S.banner.link, target: '_blank', rel: 'noopener' }, 'Se sidan')] : null) : '');
  }
  async function reloadState() {
    const st = await api('GET', 'State'); if (st.success) S.st = st;
    renderList();
  }

  function renderTrash() {
    if (!S.st.trash.length) return renderList();
    setHash('#raderade');
    const rows = S.st.trash.map(p => h('tr', {}, h('td', {}, p.title), h('td', {}, visBadge(p.visibility)),
      h('td', { class: 'text-end' }, h('button', { class: 'btn btn-sm btn-outline-primary', onclick: async () => {
        const r = await api('POST', 'RestorePage', { pageId: p.id, expectedRevision: p.revision });
        S.banner = r.success ? { cls: 'success', text: 'Sidan "' + p.title + '" är tillbaka.' } : { cls: 'danger', text: r.message };
        await reloadState();
      } }, 'Ångra radering'))));
    view(shell('raderade', pageHead('Raderade sidor', 'En raderad sida kan ångras i 30 dagar. Därefter försvinner den för gott.'), h('div', { id: 'hsBanner' }),
      h('div', { class: 'hs-card p-0 overflow-hidden' }, h('table', { class: 'table align-middle hs-list mb-0' }, h('tbody', {}, rows)))));
    renderBanner();
  }

  // ── Ny sida i tre steg ──────────────────────────────────────────────────────
  function renderNew(w) {
    w = w || { step: 1, title: '', template: 'text', visibility: OWNER.type === 'Region' ? 1 : 2 };
    setHash('#ny');
    const err = h('div');
    const step = (n, t) => h('li', { class: 'list-inline-item ' + (w.step === n ? 'fw-bold' : 'text-muted') }, n + '. ' + t);
    let content, next, back = null;
    if (w.step === 1) {
      const inp = h('input', { class: 'form-control', maxlength: '120', value: w.title, placeholder: 'Till exempel: Regler för banan', 'aria-label': 'Sidans namn' });
      inp.addEventListener('input', () => { w.title = inp.value; });
      content = [h('label', { class: 'form-label fw-semibold' }, 'Vad ska sidan heta?'), inp, h('div', { class: 'form-text' }, 'Namnet blir sidans rubrik och en del av adressen.'), err];
      next = () => { if (!w.title.trim()) { err.replaceChildren(alertBox('warning', 'Skriv ett namn på sidan.')); return; } w.step = 2; renderNew(w); };
      setTimeout(() => inp.focus(), 0);
    } else if (w.step === 2) {
      const tpls = [
        ['text', 'Textsida', 'En sida där du skriver fri text. Passar det mesta.'],
        ['docs', 'Sida med dokument', 'Text och dokument som medlemmarna kan ladda ned, till exempel blanketter.'],
        ['contact', 'Hitta hit & kontakt', 'Text om hur man hittar, och föreningens kontaktuppgifter.'],
        ['meetings', 'Möten och kallelser', 'Kommande möten från kalendern, och plats för kallelser och protokoll.'],
        ...(IS_REGION ? [['overview', 'Klubbar, banor och serier', 'Kretsens klubbar, skjutbanor och serier i en lista, hämtade ur kretsens data.']] : []),
        ['empty', 'Tom sida', 'Börja helt utan innehåll.'],
      ];
      content = tpls.map(([k, t, s]) => h('label', { class: 'hs-choice ' + (w.template === k ? 'active' : '') },
        h('input', { type: 'radio', name: 'tpl', checked: w.template === k, onchange: () => { w.template = k; renderNew(w); } }), h('strong', {}, t), h('span', { class: 'hs-sub' }, s)));
      back = () => { w.step = 1; renderNew(w); };
      next = () => { w.step = 3; renderNew(w); };
    } else {
      content = [h('p', { class: 'fw-semibold mb-2' }, 'Vem ska kunna läsa sidan?'), visibilityChoices(w.visibility, v => { w.visibility = v; renderNew(w); }), err];
      back = () => { w.step = 2; renderNew(w); };
      next = async () => {
        if (w.visibility === 0 && !await confirmPublic()) return;
        const create = await api('POST', 'CreatePage', { title: w.title.trim(), visibility: w.visibility });
        if (!create.success) { err.replaceChildren(alertBox('danger', create.message || 'Det gick inte att skapa sidan.')); return; }
        // Mallens startinnehåll sparas som första utkast, därefter öppnas redigeraren.
        const blocks = templateBlocks(w.template, w.visibility);
        S.hint = w.template === 'docs' ? 'Lägg till dokumenten med knappen "Dokument" här under.' : w.template === 'meetings' ? 'Mötena hämtas ur kalendern (händelser av typen Möte). Lägg till kallelser och protokoll med knappen "Dokument" här under.' : null;
        if (blocks.length) {
          const save = await api('POST', 'SavePage', { pageId: create.pageId, expectedRevision: create.revision, title: w.title.trim(), blocksJson: JSON.stringify({ schema: 1, blocks }), visibility: w.visibility });
          if (!save.success) { err.replaceChildren(alertBox('danger', save.message || 'Sidan skapades men mallen kunde inte läggas in.')); }
        }
        await reloadState();
        openEditor(create.pageId);
      };
    }
    const nextBtn = h('button', { class: 'btn btn-primary', onclick: () => next() }, w.step === 3 ? 'Skapa sidan' : 'Nästa');
    view(shell('sidor', pageHead('Ny sida', 'En egen sida i tre steg: namn, mall och vem som får läsa den.'), h('ol', { class: 'list-inline mb-3' }, step(1, 'Namn'), step(2, 'Mall'), step(3, 'Vem ser den')),
      h('div', { class: 'hs-card p-3 p-md-4', style: 'max-width:46rem' }, content,
        h('div', { class: 'd-flex gap-2 mt-3' }, back ? h('button', { class: 'btn btn-outline-secondary', onclick: back }, 'Tillbaka') : h('button', { class: 'btn btn-outline-secondary', onclick: renderList }, 'Avbryt'), nextBtn))));
  }

  function templateBlocks(tpl, vis) {
    const text = () => ({ id: newId(), type: 'text', visibility: vis, data: { html: '' } });
    if (tpl === 'empty') return [];
    if (tpl === 'contact') return [text(), { id: newId(), type: 'contact', visibility: Math.max(vis, 1), data: { title: 'Kontakt' } }];
    if (tpl === 'meetings') return [text(), { id: newId(), type: 'events', visibility: vis, data: { title: 'Nästa möten', count: 3, type: 'Möte' } }];
    if (tpl === 'overview') return [text(),
      { id: newId(), type: 'region-clubs', visibility: vis, data: { title: 'Klubbar i kretsen' } },
      { id: newId(), type: 'region-ranges', visibility: vis, data: { title: 'Skjutbanor' } },
      { id: newId(), type: 'region-series', visibility: vis, data: { title: 'Serier', count: 10 } }];
    return [text()];
  }

  function visibilityChoices(current, onPick) {
    return h('div', {}, visOptions().map(o => h('label', { class: 'hs-choice ' + (current === o.v ? 'active' : '') },
      h('input', { type: 'radio', name: 'vis', checked: current === o.v, onchange: () => onPick(o.v) }), h('strong', {}, o.t), h('span', { class: 'hs-sub' }, o.s))),
      h('p', { class: 'text-muted small mt-2 mb-0' }, CONSEQUENCE[current]));
  }

  async function confirmPublic(panel) {
    const ok = h('input', { type: 'checkbox', class: 'form-check-input', id: 'hsPubOk' });
    const res = await modal(panel ? 'Visa ' + panel.name + ' för alla?' : 'Synlig för alla?', [
      h('p', {}, panel ? 'Alla som besöker pistol.nu kan se den här delen av sidan, även utan inloggning. Den kan också hittas via sökmotorer.' : 'Alla som besöker pistol.nu kan läsa den här sidan, även utan inloggning. Den kan också hittas via sökmotorer.'),
      panel && PUBLIC_WHAT[panel.key] ? h('p', {}, 'Det som då visas för alla: ' + PUBLIC_WHAT[panel.key]) : null,
      h('p', { class: 'fw-semibold' }, 'Skriv inga personuppgifter: inga namn på enskilda medlemmar, inga personnummer, telefonnummer eller adresser till privatpersoner.'),
      h('div', { class: 'form-check' }, ok, h('label', { class: 'form-check-label', for: 'hsPubOk' }, panel ? 'Jag förstår att innehållet blir synligt för alla.' : 'Jag har kontrollerat att sidan inte innehåller personuppgifter.'))],
      [{ label: 'Avbryt', value: false }, { label: 'Ja, gör den synlig för alla', value: true, cls: 'btn-primary' }]);
    return res === true && ok.checked;
  }

  // "Visa publikt": vad en panel visar för en utloggad när ägaren öppnar den (servern avgör samma sak; texten är den ärliga beskrivningen).
  const PUBLIC_WHAT = {
    ranges: 'klubbens banor med adress, karta och fasta tider. Banor med begränsad plats visas aldrig.',
    calendar: 'klubbens kalender med öppna händelser, träningar och tävlingar. Inga namn på personer och inga interna tävlingar.',
    competitions: 'klubbens tävlingar. Interna tävlingar visas aldrig.',
    records: IS_REGION ? 'kretsens rekord, med namn på rekordhållarna.' : 'klubbens rekord, med namn på rekordhållarna.',
  };

  // ── Redigeraren ─────────────────────────────────────────────────────────────
  async function openEditor(pageId) {
    setHash('#redigera=' + pageId);
    view(h('p', { class: 'text-muted' }, 'Laddar sidan …'));
    const r = await api('GET', 'Page', { pageId });
    if (!r.success) { view([header(), alertBox('danger', r.message || 'Sidan kunde inte öppnas.'), h('button', { class: 'btn btn-outline-secondary', onclick: load }, 'Tillbaka')]); return; }
    let parsed = null; try { parsed = JSON.parse(r.blocksJson); } catch { /* visas som fel nedan */ }
    const isSlots = r.kind === 'slots';
    const okShape = parsed && (isSlots ? parsed.slots && typeof parsed.slots === 'object' : Array.isArray(parsed.blocks));
    if (!okShape) { view([header(), alertBox('danger', 'Sidans innehåll kunde inte läsas.')]); return; }
    // Egen text på en systemsida har två platser, före och efter; i minnet bär varje block sin plats (pos) och den tas bort igen vid sparning.
    const blocks = isSlots
      ? [...(parsed.slots.before || []).map(b => ({ ...b, pos: 'before' })), ...(parsed.slots.after || []).map(b => ({ ...b, pos: 'after' }))]
      : parsed.blocks;
    // En omgjord sida (schema 2) är en lista block där paneler (hämtas automatiskt) och egna block blandas; panelkatalogen kommer med svaret.
    Object.assign(S, { kind: r.kind || 'free', minVis: r.minVisibility || 0, templateKey: r.templateKey || null, pageKey: r.pageKey || null, panelCat: r.panels || null, convertedLive: !!r.convertedLive });
    Object.assign(S, { page: r, blocks, title: r.title, visibility: r.visibility, revision: r.revision, published: r.published, hasUnpublished: r.hasUnpublishedChanges,
      dirty: false, editVersion: 0, banner: null, statusText: 'Sparat', statusErr: false });
    if (!S.docs) { const d = await api('GET', 'Documents'); S.docs = d.success ? d.documents : []; }
    if (!S.personas) { const pr = await api('GET', 'PreviewPersonas'); S.personas = pr.success ? pr.personas : []; }
    renderEditor();
  }

  function markDirty() {
    S.dirty = true; S.editVersion++; S.statusText = 'Inte sparat'; S.statusErr = false; paintStatus();
    // "Sidan är publicerad." får inte stå kvar när man ändrar igen: då visas i stället att ändringarna inte är publicerade (uppgiftsprovet: en nybörjare tror annars att "Sparat" betyder att det syns).
    if (S.banner && S.banner.cls === 'success') S.banner = null;
    paintEditorBanner();
    clearTimeout(S.timer); S.timer = setTimeout(() => saveNow(), SAVE_IDLE_MS);
  }
  function paintStatus() {
    const el = document.getElementById('hsStatus'); if (el) { el.textContent = S.statusText; el.className = 'hs-status' + (S.statusErr ? ' err' : ''); }
    const pub = document.getElementById('hsPublish'); if (pub) pub.disabled = S.saving || !(S.dirty || S.hasUnpublished);
    // En omvandling som inte är publicerad avbryts med "Avbryt omvandlingen" (inte "Kasta ändringarna", som skulle lämna sidan i ett halvt läge).
    const dis = document.getElementById('hsDiscard'); if (dis) dis.hidden = !(S.published && (S.dirty || S.hasUnpublished)) || (S.kind === 'converted' && !S.convertedLive);
    const cc = document.getElementById('hsCancelConv'); if (cc) cc.hidden = !(S.kind === 'converted' && !S.convertedLive);
  }

  function setBanner(cls, text, extra) { S.banner = { cls, text, extra }; paintEditorBanner(); }
  function paintEditorBanner() {
    const el = document.getElementById('hsBanner'); if (!el) return;
    const kids = [];
    if (S.banner) kids.push(h('div', { class: 'alert alert-' + S.banner.cls + ' d-flex flex-wrap gap-2 align-items-center' }, h('span', { class: 'me-auto' }, S.banner.text), S.banner.extra || null));
    else if (S.kind === 'converted' && !S.convertedLive) kids.push(h('div', { class: 'alert alert-secondary d-flex flex-wrap gap-2 align-items-center' }, h('span', { class: 'me-auto' }, 'Utkast. Besökarna ser fortfarande er vanliga sida.'),
      h('a', { href: OWNER.url, target: '_blank', rel: 'noopener' }, 'Se den vanliga sidan')));
    else if (S.published && (S.dirty || S.hasUnpublished)) kids.push(h('div', { class: 'alert alert-info' }, 'Du har ändringar som inte är publicerade. Besökarna ser fortfarande den publicerade versionen.'));
    else if (!S.published) kids.push(h('div', { class: 'alert alert-secondary' }, 'Sidan är inte publicerad än. Ingen annan kan se den förrän du publicerar.'));
    // En omgjord sida där varje block är för styrelsen eller högre: ingen annan ser något, och fliken försvinner för dem.
    if (S.kind === 'converted' && S.blocks.length && S.blocks.every(b => b.visibility >= 3 && b.visibility <= 4))
      kids.push(h('div', { class: 'alert alert-warning' }, 'Ingen utom styrelsen ser något på den här sidan.'));
    if (S.hint) kids.push(h('div', { class: 'alert alert-light border' }, S.hint));
    el.replaceChildren(...kids);
  }

  function collectJson() {
    const plain = b => ({ id: b.id, type: b.type, visibility: b.visibility, data: b.data });
    if (S.kind === 'slots') return JSON.stringify({ schema: 1, slots: { before: S.blocks.filter(b => b.pos === 'before').map(plain), after: S.blocks.filter(b => b.pos === 'after').map(plain) } });
    return JSON.stringify({ schema: S.kind === 'converted' ? 2 : 1, blocks: S.blocks.map(plain) });
  }

  // Sparar utkastet. Returnerar true när servern tog emot det.
  async function saveNow() {
    clearTimeout(S.timer);
    if (S.saving) return false;
    if (!S.title.trim()) { S.statusText = 'Sidan behöver ett namn'; S.statusErr = true; paintStatus(); return false; }
    if (!S.dirty) return true;
    S.saving = true; S.statusText = 'Sparar …'; S.statusErr = false; paintStatus();
    const version = S.editVersion;
    const r = await api('POST', 'SavePage', { pageId: S.page.id, expectedRevision: S.revision, title: S.title.trim(), blocksJson: collectJson(), visibility: S.visibility,
      allowPersonalNumber: S.ack.size > 0 ? 'true' : 'false' });
    S.saving = false;
    return handleWrite(r, version, 'save');
  }

  async function handleWrite(r, version, kind) {
    if (r.success) {
      S.revision = r.revision; S.hasUnpublished = true;
      if (kind === 'save' && S.editVersion === version) { S.dirty = false; S.statusText = 'Sparat kl. ' + clock(); }
      else if (kind === 'save') { S.statusText = 'Inte sparat'; clearTimeout(S.timer); S.timer = setTimeout(() => saveNow(), 1500); }
      S.statusErr = false; S.banner = null; paintStatus(); paintEditorBanner(); return true;
    }
    S.statusErr = true; S.statusText = 'Inte sparat';
    if (r.status === 'Conflict') {
      S.revision = r.revision;
      setBanner('warning', 'Någon annan har ändrat sidan sedan du öppnade den. Din text finns kvar här.', [
        h('button', { class: 'btn btn-sm btn-primary', onclick: async () => { S.banner = null; await saveNow(); } }, 'Spara min version ändå'),
        h('button', { class: 'btn btn-sm btn-outline-secondary', onclick: () => openEditor(S.page.id) }, 'Visa deras version (min text går förlorad)')]);
    } else if (r.status === 'PersonalNumber') {
      const m = r.match;
      const again = await modal('Det här ser ut som ett personnummer', [
        h('p', {}, 'Texten innehåller ', h('strong', {}, m), '. Personnummer får inte publiceras på en hemsida.'),
        h('p', { class: 'text-muted mb-0' }, 'Om det inte är ett personnummer (till exempel ett artikelnummer) kan du spara ändå. Valet loggas.')],
        [{ label: 'Ändra texten', value: false, cls: 'btn-primary' }, { label: 'Det är inget personnummer, spara ändå', value: true, cls: 'btn-outline-secondary' }]);
      if (again === true) { S.ack.add(m); S.statusText = 'Sparar …'; paintStatus(); return kind === 'publish' ? publishNow() : saveNow(); }
      setBanner('danger', 'Ändra texten som ser ut som ett personnummer (' + m + ') så sparas sidan.');
    } else if (r.status === 'Invalid' && r.errors && r.errors.length) {
      setBanner('danger', 'Sidan kan inte sparas: ' + r.errors.map(e => e.message).join(' '));
    } else if (r.code === 'terms') { load(); return false; }
    else setBanner('danger', r.message || 'Det gick inte att spara.');
    paintStatus();
    return false;
  }

  async function publishNow() {
    const noAlt = S.blocks.filter(b => b.type === 'image' && b.data.decorative !== true && !String(b.data.alt || '').trim()).length;
    if (noAlt > 0 && !S.altAck) {
      const go = await modal('Bilder utan beskrivning', [h('p', {}, noAlt === 1 ? 'En bild på sidan saknar beskrivning.' : noAlt + ' bilder på sidan saknar beskrivning.'),
        h('p', { class: 'text-muted mb-0' }, 'Den som använder skärmläsare får då inte veta vad bilden visar. Är bilden bara dekoration kan du kryssa i det på bilden.')],
        [{ label: 'Beskriv bilderna', value: false, cls: 'btn-primary' }, { label: 'Publicera ändå', value: true, cls: 'btn-outline-secondary' }]);
      if (go !== true) return false;
      S.altAck = true;
    }
    // En omgjord sida: berätta vad besökarna förlorar innan det publiceras (de borttagna panelerna).
    if (S.kind === 'converted') {
      const gone = removedPanels();
      if (gone.length) {
        const sure = await modal('Publicera ändringarna?', [h('p', {}, 'Det här tas bort från sidan för besökarna:'), h('ul', {}, gone.map(c => h('li', {}, c.name))),
          h('p', { class: 'text-muted mb-0' }, 'Ni kan lägga tillbaka dem när som helst.')], [{ label: 'Avbryt', value: false }, { label: 'Publicera', value: true, cls: 'btn-primary' }]);
        if (sure !== true) return false;
      }
    }
    if (S.dirty && !await saveNow()) return false;
    S.saving = true; paintStatus();
    const r = await api('POST', 'PublishPage',{ pageId: S.page.id, expectedRevision: S.revision, allowPersonalNumber: S.ack.size > 0 ? 'true' : 'false' });
    S.saving = false;
    if (r.success) {
      S.revision = r.revision; S.published = true; S.hasUnpublished = false; S.dirty = false; S.statusText = 'Publicerat kl. ' + clock(); S.statusErr = false;
      if (S.kind === 'converted') S.convertedLive = true;   // nu ser besökarna den egna versionen; den ångras med Gå tillbaka
      const url = S.page.url;
      setBanner('success', 'Sidan är publicerad.', url ? [h('a', { class: 'btn btn-sm btn-outline-success', href: url, target: '_blank', rel: 'noopener' }, 'Se sidan'),
        h('button', { class: 'btn btn-sm btn-outline-success', onclick: () => copyLink(url) }, 'Kopiera länk')] : null);
      paintStatus(); return true;
    }
    return handleWrite(r, 0, 'publish');
  }

  function setPageVisibility(v) {
    S.visibility = v;
    for (const b of S.blocks) b.visibility = Math.max(b.visibility, minVisFor(b.type));   // blocken följer med: aldrig öppnare än sidan
    markDirty(); renderEditor();
  }

  function renderEditor() {
    const slots = S.kind === 'slots', conv = S.kind === 'converted';
    const titleInp = h('input', { class: 'form-control form-control-lg', maxlength: '120', value: S.title, 'aria-label': 'Sidans rubrik' });
    titleInp.addEventListener('input', () => { S.title = titleInp.value; markDirty(); });
    const visSel = h('select', { class: 'form-select', 'aria-label': 'Vem ser sidan' }, visOptions().filter(o => (!slots && !conv) || o.v >= S.minVis).map(o => h('option', { value: o.v, selected: o.v === S.visibility ? true : null }, o.s)));
    visSel.addEventListener('change', async () => {
      const v = parseInt(visSel.value, 10);
      if (v === 0 && !await confirmPublic()) { visSel.value = S.visibility; return; }
      setPageVisibility(v);
    });
    const consequence = h('div', { class: 'text-muted small mt-1' }, CONSEQUENCE[S.visibility]);
    const blocksEl = h('div', { id: 'hsBlocks' });
    // Inställningen och resultatet är EN sektion, "Vem ser sidan?" (egen text på en vanlig sida har bara inställningen).
    const who = slots ? null : whoSeesCard(() => ({ pageId: S.page.id }), {
      beforeLoad: async () => { if (S.dirty) await saveNow(); }, fallbackUrl: () => (S.page && S.page.url) || null, previewPageId: () => S.page.id,
      setting: [
        h('label', { class: 'form-label small text-muted mb-1', for: 'hsVisSel' }, 'Vem får läsa sidan?'), visSel, consequence,
        !conv && S.page.url ? h('div', { class: 'small mt-1 text-muted' }, 'Adress: ', h('code', {}, S.page.url)) : null,
        conv ? null : h('div', { class: 'mt-3' }, menuToggle()),
      ],
    });
    visSel.id = 'hsVisSel';

    const bar = h('div', { class: 'hs-bar d-flex flex-wrap gap-2 align-items-center' },
      h('button', { class: 'btn btn-outline-secondary', onclick: async () => { if (S.dirty) await saveNow(); setHash(slots ? '#sida=' + S.templateKey : conv ? '#sida=' + S.pageKey : '#sidor'); load(); } },
        h('i', { class: 'bi bi-arrow-left me-1' }), slots || conv ? 'Tillbaka till sidan' : 'Sidor'),
      h('span', { id: 'hsStatus', class: 'hs-status ms-auto' }, S.statusText),
      h('button', { id: 'hsCancelConv', class: 'btn btn-outline-secondary', hidden: true, onclick: () => cancelConversionFlow(S.pageKey, S.page.id, S.revision, true) }, 'Avbryt omvandlingen'),
      h('button', { id: 'hsDiscard', class: 'btn btn-outline-secondary', hidden: true, onclick: async () => {
        if (!await confirmDialog('Kasta ändringarna?', 'Ändringarna sedan senaste publiceringen försvinner. Den publicerade sidan påverkas inte.', 'Kasta ändringarna', true)) return;
        clearTimeout(S.timer); S.dirty = false;
        const r = await api('POST', 'DiscardDraft', { pageId: S.page.id, expectedRevision: S.revision });
        if (!r.success) { setBanner('danger', r.message || 'Det gick inte att kasta ändringarna.'); return; }
        openEditor(S.page.id);
      } }, 'Kasta ändringarna'),
      h('button', { id: 'hsPublish', class: 'btn btn-primary', onclick: () => publishNow() }, 'Publicera ändringar'));

    view(shell(slots ? 'sys:' + S.templateKey : conv ? 'sys:' + S.pageKey : 'sidor', bar, h('div', { id: 'hsBanner' }),
      slots
        ? h('div', { class: 'mb-3' }, h('h1', { class: 'h3 mb-1' }, 'Egen text på "' + S.title + '"'),
            h('p', { class: 'text-muted mb-0' }, 'Texten visas före och efter sidans vanliga innehåll. Sidans eget innehåll ändras inte.'))
        : conv
          ? h('div', { class: 'mb-3' }, h('h1', { class: 'h3 mb-1' }, 'Ändra sidan "' + S.title + '"'),
              h('p', { class: 'text-muted mb-0' }, 'Här bestämmer ni hur sidan ser ut. Det som hämtas automatiskt visas som kort som ni kan flytta, och ta bort om det inte är obligatoriskt. Ni kan också lägga till eget innehåll. Ändringarna syns för besökarna först när ni publicerar.'))
          : h('div', { class: 'mb-3' }, h('label', { class: 'form-label fw-semibold' }, 'Rubrik'), titleInp),
      who || h('div', { class: 'mb-4' }, h('label', { class: 'form-label fw-semibold' }, 'Vem ser texten?'), visSel, consequence),
      blocksEl, slots ? null : addBlockBar(), moreCard()));
    S.refreshWho = who ? who._refresh : null;
    drawBlocks();
    paintStatus(); paintEditorBanner();
  }

  // Förhandsvisning av en besökares innehåll: servern renderar sidans block för en fast tittare (samma renderare och regler som besökarna), svaret läggs i en sandlådad iframe.
  function openPreviewModal(r, who) {
    const kids = [alertBox(r.decision === 'Serve' ? 'info' : 'warning', r.notice)];
    if (r.document) kids.push(h('iframe', { sandbox: '', srcdoc: r.document, title: 'Förhandsvisning', style: 'width:100%;height:55vh;border:1px solid var(--bs-border-color);border-radius:.5rem;background:#fff' }));
    modal('Så ser ' + who.toLowerCase() + ' sidans innehåll', kids, [{ label: 'Stäng', value: true, cls: 'btn-primary' }], true);
  }

  // "Vem ser sidan?": EN sektion med inställningen (opts.setting: rullgardinen, förklaringen, adressen, menyvalet), resultatet som tabell över vad varje tittare ser (sidan i menyn,
  // varje del av sidan; ur samma regler som besökarna möter) och vägen till den riktiga sidan. Tabellen uppdaterar sig själv när något som styr åtkomst ändras (el._refresh).
  // Servern ritar ingen paneldata, så tabellen är ärlig om vad den är: hur sidan är inställd. En förhandsvisning per tittare (opts.previewPageId) erbjuds bara när sidan inte har delar
  // som hämtas ur systemet, för då är den sann; en sida med paneler går inte att förhandsvisa som en annan person (panelerna hämtar innehåll med den riktiga inloggningen), och det står.
  // params = { templateKey } (vanlig sida) eller { pageId } (fri eller omgjord sida); beforeLoad körs först (spara utkastet).
  function whoSeesCard(params, opts) {
    opts = opts || {};
    let ready = false, timer = null;
    const out = h('div', { class: 'mt-3' }, h('span', { class: 'text-muted' }, 'Laddar …'));
    const cell = st => st === 'see' ? h('span', { class: 'text-success' }, h('i', { class: 'bi bi-check-lg', 'aria-hidden': 'true' }), h('span', { class: 'visually-hidden' }, 'Ser'))
      : st === 'lock' ? h('span', { class: 'text-body-secondary' }, h('i', { class: 'bi bi-lock-fill', 'aria-hidden': 'true' }), h('span', { class: 'visually-hidden' }, 'Ser menyvalet med ett hänglås, får en låsruta'))
      : h('span', { class: 'text-body-secondary' }, '–', h('span', { class: 'visually-hidden' }, 'Ser inte'));
    const load = async () => {
      if (!ready) out.replaceChildren(h('span', { class: 'text-muted' }, 'Laddar …'));   // vid en uppdatering ligger gamla tabellen kvar tills den nya är klar
      if (opts.beforeLoad) await opts.beforeLoad();
      const r = await api('POST', 'Visibility', params());
      if (!r.success) { out.replaceChildren(alertBox('danger', r.message || 'Det gick inte att visa vem som ser sidan.')); return; }
      const url = r.publicUrl || (opts.fallbackUrl && opts.fallbackUrl()) || null;
      const full = url ? new URL(url, location.origin).href : null;
      const basis = r.basis === 'draft' ? 'Tabellen visar utkastet. Besökarna ser det först när du har publicerat.'
        : r.basis === 'published' ? 'Tabellen visar den publicerade sidan.' : 'Tabellen visar sidan som den är idag.';
      const input = full ? h('input', { class: 'form-control', readonly: true, value: full, 'aria-label': 'Adressen till den riktiga sidan', onfocus: e => e.target.select() }) : null;
      const copyBtn = full ? h('button', { type: 'button', class: 'btn btn-outline-secondary', onclick: async e => {
        const b = e.currentTarget;
        try { await navigator.clipboard.writeText(full); b.textContent = 'Kopierad'; } catch { input.select(); b.textContent = 'Markerad, tryck Ctrl+C'; }
        setTimeout(() => { b.textContent = 'Kopiera adressen'; }, 2500);
      } }, 'Kopiera adressen') : null;
      const hasPanels = r.rows.some(x => x.kind === 'panel');
      const canPreview = !!opts.previewPageId && !hasPanels;
      const previewBtn = c => h('button', { type: 'button', class: 'btn btn-link btn-sm p-0 d-block mx-auto', onclick: async e => {
        const b = e.currentTarget; b.disabled = true;
        if (S.dirty) await saveNow();                                   // förhandsvisningen läser det sparade utkastet
        const pr = await api('POST', 'Preview', { pageId: opts.previewPageId(), persona: c.key });
        b.disabled = false;
        if (!pr.success) { setBanner('danger', pr.message || 'Förhandsvisningen gick inte att göra.'); return; }
        openPreviewModal(pr, c.label);
      } }, h('i', { class: 'bi bi-eye me-1', 'aria-hidden': 'true' }), 'Förhandsvisa');
      const put = (...nodes) => out.replaceChildren(...nodes.filter(Boolean));   // en tom nod skulle annars skrivas ut som texten "null"
      put(
        h('div', { class: 'table-responsive' }, h('table', { class: 'table table-sm align-middle mb-2' },
          h('thead', {}, h('tr', {}, h('th', { scope: 'col' }, ''), r.columns.map(c => h('th', { scope: 'col', class: 'text-center small fw-semibold' }, c.label, canPreview ? previewBtn(c) : null)))),
          h('tbody', {}, r.rows.map(row => h('tr', {}, h('th', { scope: 'row', class: 'fw-normal' }, h('span', { class: row.kind === 'page' ? 'fw-semibold' : '' }, row.label),
            row.detail ? h('span', { class: 'text-muted small ms-2' }, row.detail) : null), row.states.map(st => h('td', { class: 'text-center position-relative' }, cell(st)))))))),
        h('p', { class: 'small text-muted mb-2' }, [h('i', { class: 'bi bi-check-lg text-success me-1', 'aria-hidden': 'true' }), 'ser  ',
          h('i', { class: 'bi bi-lock-fill ms-3 me-1', 'aria-hidden': 'true' }), 'ser sidan i menyn med ett hänglås och får en låsruta  ', h('span', { class: 'ms-3' }, '– ser inte')]),
        h('p', { class: 'mb-1' }, r.summary + ' ' + basis),
        hasPanels ? h('p', { class: 'small text-muted mb-1' }, 'Delar som hämtas ur systemet (till exempel Banor och Kalender) går inte att förhandsvisa som en annan besökare. Tabellen visar vilka som ser dem.') : null,
        full ? h('div', { class: 'mt-3' },
          h('div', { class: 'fw-semibold mb-1' }, 'Se den riktiga sidan som utloggad'),
          h('p', { class: 'small text-muted mb-2' }, 'Öppna adressen i ett privat fönster, där du inte är inloggad. Öppnar du den här ser du sidan som dig själv.' + (r.published === false ? ' Tills du har publicerat får alla beskedet att sidan inte finns.' : '')),
          h('div', { class: 'd-flex flex-wrap gap-2' }, h('div', { class: 'flex-grow-1', style: 'min-width:14rem' }, input), copyBtn,
            h('a', { class: 'btn btn-outline-secondary', href: full, target: '_blank', rel: 'noopener' }, 'Öppna sidan ', h('i', { class: 'bi bi-box-arrow-up-right ms-1', 'aria-hidden': 'true' })))) : null);
    };
    const run = () => load().finally(() => { ready = true; });
    run();
    const el = h('div', { class: 'hs-card p-3 mb-3', id: 'hsWhoSees' },
      h('h2', { class: 'h5 mb-2' }, 'Vem ser sidan?'),
      opts.setting
        ? h('div', {}, opts.setting)
        : h('div', {}, h('p', { class: 'text-muted mb-1' }, 'Så här är sidan inställd. Innehållet hämtas när besökaren öppnar sidan.'),
            opts.change ? h('p', { class: 'mb-0' }, opts.change.intro + ' ', h('button', { type: 'button', class: 'btn btn-link p-0 align-baseline', onclick: opts.change.run }, opts.change.label))
              : opts.note ? h('p', { class: 'text-muted mb-0' }, opts.note) : null),
      out);
    // Uppdatera tabellen en kort stund efter en ändring som styr åtkomst (sidans nivå, ett blocks nivå, ett block som läggs till eller tas bort). Inte före första ritningen.
    el._refresh = () => { if (!ready) return; clearTimeout(timer); timer = setTimeout(load, 500); };
    return el;
  }

  // "Mer": återställningspunkt, tidigare versioner och adressbyte (avancerat). Inget av det publicerar något.
  function moreCard() {
    const versionsEl = h('div', { class: 'mt-2' });
    const slugMsg = h('div', { class: 'mt-2' });
    const fmt = s => new Date(s).toLocaleString('sv-SE', { dateStyle: 'short', timeStyle: 'short' });

    async function loadVersions() {
      versionsEl.replaceChildren(h('span', { class: 'text-muted' }, 'Laddar …'));
      const r = await api('GET', 'Versions', { pageId: S.page.id });
      if (!r.success) { versionsEl.replaceChildren(alertBox('danger', r.message || 'Det gick inte att hämta versionerna.')); return; }
      if (!r.versions.length) { versionsEl.replaceChildren(h('p', { class: 'text-muted mb-0' }, 'Det finns inga tidigare versioner än. De skapas när du publicerar eller sparar en återställningspunkt.')); return; }
      versionsEl.replaceChildren(h('div', { class: 'list-group' }, r.versions.map(v => h('div', { class: 'list-group-item d-flex flex-wrap align-items-center gap-2' },
        h('span', { class: 'me-auto' }, fmt(v.saved) + ' – ' + v.kind, v.support ? ' (stödläge)' : ''),
        h('button', { class: 'btn btn-sm btn-outline-primary', onclick: async () => {
          if (!await confirmDialog('Använda den här versionen?', 'Din nuvarande text i utkastet ersätts av versionen från ' + fmt(v.saved) + '. Ingenting publiceras förrän du publicerar.', 'Använd den här', false)) return;
          if (S.dirty) await saveNow();
          const x = await api('POST', 'RestoreVersion', { pageId: S.page.id, versionId: v.id, expectedRevision: S.revision });
          if (!x.success) { setBanner('danger', x.message || 'Versionen kunde inte användas.'); return; }
          await openEditor(S.page.id);
          setBanner('success', 'Versionen ligger nu i utkastet. Publicera när du är nöjd.');
        } }, 'Använd den här')))));
    }

    const slugInp = h('input', { class: 'form-control', maxlength: '60', value: S.page.slug || '', 'aria-label': 'Sidans adress' });
    return h('details', { class: 'hs-card p-3 mt-3' }, h('summary', { class: 'fw-semibold' }, 'Mer: versioner och adress'),
      h('div', { class: 'mt-3' },
        h('h2', { class: 'h6' }, 'Återställningspunkt'),
        h('p', { class: 'text-muted small' }, 'Spara en version du kan gå tillbaka till. Det publicerar ingenting.'),
        h('button', { class: 'btn btn-outline-secondary', onclick: async e => {
          e.target.disabled = true; if (S.dirty) await saveNow();
          const r = await api('POST', 'SaveCheckpoint', { pageId: S.page.id });
          e.target.disabled = false;
          setBanner(r.success ? 'success' : 'danger', r.success ? 'Återställningspunkten är sparad.' : (r.message || 'Det gick inte att spara.'));
        } }, 'Spara en återställningspunkt'),
        h('h2', { class: 'h6 mt-4' }, 'Tidigare versioner'),
        h('button', { class: 'btn btn-outline-secondary', onclick: loadVersions }, 'Visa tidigare versioner'), versionsEl,
        ...(S.kind !== 'free' ? [] : [h('h2', { class: 'h6 mt-4' }, 'Adress (avancerat)'),
        h('p', { class: 'text-muted small' }, 'Adressen ändras aldrig av sig själv. Byter du den skickas den gamla vidare till den nya, så länkar som redan delats fortsätter fungera. Sidor som bara styrelsen eller administratörerna ser skickas inte vidare.'),
        h('div', { class: 'd-flex gap-2 flex-wrap' }, h('div', { class: 'flex-grow-1', style: 'min-width:14rem' }, slugInp),
          h('button', { class: 'btn btn-outline-danger', onclick: async e => {
            const wanted = slugInp.value.trim().toLowerCase();
            if (!wanted || wanted === S.page.slug) { slugMsg.replaceChildren(alertBox('info', 'Skriv en ny adress.')); return; }
            if (!await confirmDialog('Byta adress?', 'Sidans adress blir /' + wanted + '/. Den gamla adressen skickas vidare till den nya.', 'Byt adress', false)) return;
            e.target.disabled = true;
            const r = await api('POST', 'SetSlug', { pageId: S.page.id, slug: wanted });
            e.target.disabled = false;
            if (!r.success) { slugMsg.replaceChildren(alertBox('danger', r.message || 'Adressen går inte att använda.')); return; }
            S.page.slug = r.slug; S.page.url = r.url; slugInp.value = r.slug;
            slugMsg.replaceChildren(alertBox('success', 'Adressen är bytt: ' + r.url));
          } }, 'Byt adress')), slugMsg])));
  }

  // "Visa sidan i menyn": sparas direkt (det är inte en del av texten) och gäller först när sidan är publicerad.
  function menuToggle() {
    const cb = h('input', { type: 'checkbox', class: 'form-check-input', id: 'hsInMenu', checked: S.page.inMenu });
    cb.addEventListener('change', async () => {
      cb.disabled = true;
      const r = await api('POST', 'SetInMenu', { pageId: S.page.id, inMenu: cb.checked ? 'true' : 'false' });
      cb.disabled = false;
      if (!r.success) { cb.checked = !cb.checked; setBanner('danger', r.message || 'Det gick inte att ändra menyn.'); return; }
      S.page.inMenu = cb.checked;
    });
    return h('div', { class: 'form-check mb-0' }, cb, h('label', { class: 'form-check-label', for: 'hsInMenu' }, 'Visa sidan i menyn'),
      h('div', { class: 'form-text' }, 'Länken syns i menyn när sidan är publicerad, och bara för dem som får läsa sidan.'));
  }

  function addBlockBar() {
    return h('div', { class: 'hs-card p-3' }, h('p', { class: 'fw-semibold mb-2' }, 'Lägg till'),
      h('div', { class: 'd-flex flex-wrap gap-2' }, BLOCK_TYPES.map(t => h('button', { class: 'btn btn-outline-primary', onclick: () => addBlock(t.type) }, h('i', { class: 'bi ' + t.icon + ' me-1' }), t.label))));
  }

  async function addBlock(type, pos) {
    if (type === 'image') {   // en bild utan vald fil kan inte sparas: välj först, lägg till sedan
      const f = await pickImage();
      if (!f) return;
      S.blocks.push({ id: newId(), type, visibility: minVisFor(type), data: { ref: 'file:' + f.id, alt: '', decorative: false, caption: '' } });
      S.hint = 'Beskriv bilden med några ord, så att den som inte ser bilden förstår vad den visar.';
      markDirty(); drawBlocks(); paintEditorBanner(); return;
    }
    const data = type === 'text' ? { html: '' } : type === 'heading' ? { text: '', level: 2 } : type === 'links' ? { items: [{ label: '', url: '' }] }
      : type === 'documents' ? { title: '', refs: [] } : type === 'events' ? { title: 'Kommande händelser', count: 5 } : type === 'news' ? { title: 'Nyheter', count: 5 } : type === 'pagelink' ? { key: (S.st.systemPages[0] || {}).key, label: '' }
      : type === 'region-clubs' ? { title: 'Klubbar i kretsen' } : type === 'region-ranges' ? { title: 'Skjutbanor' } : type === 'region-series' ? { title: 'Serier', count: 10 } : type === 'region-stats' ? { title: '' } : { title: 'Kontakt' };
    S.blocks.push({ id: newId(), type, visibility: minVisFor(type), data, ...(S.kind === 'slots' ? { pos } : {}) });
    S.hint = null; markDirty(); drawBlocks();
  }

  // Närmaste granne åt det håll i samma grupp (en slots före/efter är två egna listor); fri sida: bara nästa.
  function neighbour(i, d) {
    for (let j = i + d; j >= 0 && j < S.blocks.length; j += d) if (S.kind !== 'slots' || S.blocks[j].pos === S.blocks[i].pos) return j;
    return -1;
  }
  function move(i, d) { const j = neighbour(i, d); if (j < 0) return; [S.blocks[i], S.blocks[j]] = [S.blocks[j], S.blocks[i]]; markDirty(); drawBlocks(); }

  function drawBlocks() {
    clearEditors(); S.refreshWho && S.refreshWho();   // blocken (och deras nivåer) styr tabellen i Vem ser sidan?
    const el = document.getElementById('hsBlocks'); if (!el) return;
    if (S.kind === 'slots') {
      const group = (pos, title, help) => {
        const items = S.blocks.map((b, i) => [b, i]).filter(([b]) => b.pos === pos);
        return h('section', { class: 'mb-4', dataset: undefined },
          h('h2', { class: 'h5' }, title), h('p', { class: 'text-muted small' }, help),
          ...(items.length ? items.map(([b, i]) => blockCard(b, i)) : [h('div', { class: 'hs-card p-3 text-center text-muted mb-3' }, 'Ingen text här än.')]),
          h('div', { class: 'd-flex flex-wrap gap-2' }, ['text', 'links', 'news'].map(t => h('button', { class: 'btn btn-outline-primary', onclick: () => addBlock(t, pos) }, h('i', { class: 'bi ' + typeInfo(t).icon + ' me-1' }), typeInfo(t).label))));
      };
      el.replaceChildren(group('before', 'Före sidans innehåll', 'Visas överst på sidan.'), group('after', 'Efter sidans innehåll', 'Visas längst ner på sidan.'));
      el.querySelectorAll('[data-rte]').forEach(node => initRte(node, S.blocks.find(b => b.id === node.dataset.rte)));
      return;
    }
    if (S.kind === 'converted') {
      // En omgjord sida: paneler (kort med Åtgärder) och egna block i EN lista; borttagna paneler som grå rader med Lägg tillbaka.
      const present = new Set(S.blocks.filter(b => b.type === 'panel').map(b => b.data.key));
      const removed = removedPanels();
      el.replaceChildren(...S.blocks.map((b, i) => b.type === 'panel' ? panelCard(b, i) : blockCard(b, i)),
        ...removed.map(removedRow));
      el.querySelectorAll('[data-rte]').forEach(node => initRte(node, S.blocks.find(b => b.id === node.dataset.rte)));
      return;
    }
    el.replaceChildren(...(S.blocks.length ? S.blocks.map(blockCard) : [h('div', { class: 'hs-card p-4 text-center text-muted mb-3' }, 'Sidan har inget innehåll än. Lägg till ett block här under.')]));
    // CKEditor skapas först när elementen sitter i dokumentet.
    el.querySelectorAll('[data-rte]').forEach(node => initRte(node, S.blocks.find(b => b.id === node.dataset.rte)));
  }

  // ── Omgjorda sidor: panelkort ────────────────────────────────────────────────
  const ZONE_LABEL = { main: 'Huvudkolumnen', side: 'Sidokolumnen', full: 'Hela bredden' };
  const catOf = key => (S.panelCat || []).find(c => c.key === key) || { key, name: key, description: '', zone: 'full', mustKeep: true, systemOnly: false, level: 0, personalData: false };
  /** Panelerna som får finnas på sidan men inte ligger där nu (och som ägaren kan lägga tillbaka). */
  function removedPanels() {
    const present = new Set(S.blocks.filter(b => b.type === 'panel').map(b => b.data.key));
    return (S.panelCat || []).filter(c => !c.systemOnly && !present.has(c.key));
  }
  function setZone(b, zone) { b.data.zone = zone; markDirty(); drawBlocks(); }
  async function removePanel(b, i) {
    const c = catOf(b.data.key);
    if (!await confirmDialog('Ta bort "' + c.name + '"?', '"' + c.name + '" visas inte längre på sidan för besökarna när ni har publicerat. Ni kan lägga tillbaka den när ni vill, och när ni publicerar ser ni vad som har tagits bort.', 'Ta bort', true)) return;
    S.blocks.splice(i, 1); markDirty(); drawBlocks();
  }
  function restorePanel(c) {
    // Tillbaka på sin vanliga plats: före den första panel som ligger efter den i standardordningen, annars sist.
    const order = (S.panelCat || []).map(x => x.key), mine = order.indexOf(c.key);
    let at = S.blocks.findIndex(b => b.type === 'panel' && order.indexOf(b.data.key) > mine);
    if (at < 0) at = S.blocks.length;
    S.blocks.splice(at, 0, { id: newId(), type: 'panel', visibility: Math.max(c.level, S.visibility), data: { key: c.key, zone: c.zone } });
    markDirty(); drawBlocks();
  }
  function removedRow(c) {
    return h('div', { class: 'hs-card hs-removed p-2 px-3 mb-3 d-flex align-items-center gap-2 flex-wrap text-muted' },
      h('span', { class: 'me-auto' }, c.name + ' är borttagen.'),
      h('button', { class: 'btn btn-sm btn-outline-secondary', onclick: () => restorePanel(c) }, 'Lägg tillbaka'));
  }
  function panelCard(b, i) {
    const c = catOf(b.data.key), isHome = S.pageKey === 'home';
    // Aldrig öppnare än panelen är idag, än sidan, och aldrig "Alla" för personuppgifter (servern prövar samma sak).
    const min = Math.max(c.level, S.visibility, c.personalData ? 1 : 0);
    // "Visa publikt": en panel som får visas för alla kan väljas som Alla även när sidan inte är det än (då öppnas sidan också, efter en bekräftelse).
    const levels = visOptions().filter(o => o.v >= min || (c.publicAllowed && o.v === 0));
    const visSel = h('select', { class: 'form-select form-select-sm w-auto', 'aria-label': 'Vem ser ' + c.name },
      levels.map(o => h('option', { value: o.v, selected: o.v === b.visibility ? true : null }, o.v === 0 && c.publicAllowed ? 'Alla, även utloggade' : o.t)));
    visSel.addEventListener('change', async () => {
      const v = parseInt(visSel.value, 10);
      if (v === 0) {
        if (!await confirmPublic(c)) { visSel.value = b.visibility; return; }
        if (S.visibility > 0) { S.visibility = 0; }   // sidan måste själv vara öppen för alla, annars syns panelen inte
        b.visibility = 0; markDirty(); renderEditor(); return;
      }
      b.visibility = v; markDirty(); renderEditor();
    });
    const menu = rowMenu([
      ['Flytta upp', () => move(i, -1), { disabled: neighbour(i, -1) < 0 }],
      ['Flytta ner', () => move(i, 1), { disabled: neighbour(i, 1) < 0 }],
      isHome ? (b.data.zone === 'side' ? ['Flytta till huvudkolumnen', () => setZone(b, 'main')] : ['Flytta till sidokolumnen', () => setZone(b, 'side')]) : null,
      c.mustKeep ? null : '-', c.mustKeep ? null : ['Ta bort', () => removePanel(b, i), { danger: true }]]);
    return h('div', { class: 'hs-block hs-panelcard' },
      h('div', { class: 'hs-block-head' },
        h('span', { class: 'hs-type' }, h('i', { class: 'bi bi-grid-1x2 me-2', 'aria-hidden': 'true' }), c.name),
        c.systemOnly ? h('span', { class: 'small text-muted' }, 'Visas bara för den som inte är inloggad')
          : levels.length > 1 ? h('span', { class: 'd-flex align-items-center gap-1 small' }, 'Syns för', visSel) : h('span', { class: 'small text-muted' }, 'Syns för ' + visLabel(b.visibility)),
        menu),
      h('div', { class: 'hs-block-body' },
        h('div', {}, c.description || ''),
        b.visibility === 0 ? h('div', { class: 'small mt-1 text-warning-emphasis' }, h('i', { class: 'bi bi-globe2 me-1', 'aria-hidden': 'true' }), 'Visas publikt: alla på internet kan se den här delen.') : null,
        h('div', { class: 'text-muted small mt-1' }, 'Hämtas automatiskt vid varje besök.' + (isHome ? ' Plats: ' + (ZONE_LABEL[b.data.zone] || ZONE_LABEL.full) + '.' : '') + (c.mustKeep ? ' Kan flyttas men inte tas bort.' : ''))));
  }

  // Bildens bredd i procent: ett eget attribut på bildblocket som skrivs som style="width:N%" på bildens ram (figure) och läses tillbaka därifrån.
  // Servern släpper bara igenom just det (SiteHtmlSanitizer), så ingen annan storlek eller stil kan sparas.
  class SiteImageWidth {
    constructor(editor) { this.editor = editor; }
    init() {
      const ed = this.editor;
      ed.model.schema.extend('imageBlock', { allowAttributes: 'siteWidth' });
      ed.conversion.for('upcast').attributeToAttribute({
        view: { name: 'figure', styles: { width: /^\d{1,3}(\.\d+)?%$/ } },
        model: { key: 'siteWidth', value: el => el.getStyle('width') },
      });
      ed.conversion.for('downcast').add(dispatcher => dispatcher.on('attribute:siteWidth:imageBlock', (evt, data, api) => {
        if (!api.consumable.consume(data.item, evt.name)) return;
        const fig = api.mapper.toViewElement(data.item);
        if (!fig) return;
        if (data.attributeNewValue) api.writer.setStyle('width', data.attributeNewValue, fig); else api.writer.removeStyle('width', fig);
      }));
    }
  }

  // Storleksknapparna ovanför textrutan. Verkar på den markerade bilden; ingen markerad bild ger en hjälptext.
  function widthBar(getEd) {
    const hint = h('span', { class: 'small text-muted ms-2', role: 'status' });
    const set = (value, label) => h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', 'data-img-width': value || 'full', onclick: () => {
      const ed = getEd(); const el = ed && ed.model.document.selection.getSelectedElement();
      if (!el || el.name !== 'imageBlock') { hint.textContent = 'Klicka först på bilden du vill ändra.'; return; }
      hint.textContent = '';
      ed.model.change(w => { if (value) w.setAttribute('siteWidth', value, el); else w.removeAttribute('siteWidth', el); });
      ed.editing.view.focus();
    } }, label);
    return h('span', { class: 'd-inline-flex align-items-center gap-1 ms-2', role: 'group', 'aria-label': 'Bildens storlek' },
      h('span', { class: 'small text-muted' }, 'Bildens storlek:'), set('25%', '25 %'), set('50%', '50 %'), set('75%', '75 %'), set(null, 'Hela bredden'), hint);
  }

  function initRte(node, block) {
    if (!window.ClassicEditor || !block) { node.append(h('p', { class: 'text-danger' }, 'Textredigeraren kunde inte laddas. Ladda om sidan.')); return; }
    ClassicEditor.create(node, {
      toolbar: ['heading', '|', 'bold', 'italic', 'link', 'bulletedList', 'numberedList', 'blockQuote', 'insertTable', '|', 'undo', 'redo'],
      heading: { options: [{ model: 'paragraph', title: 'Vanlig text', class: 'ck-heading_paragraph' }, { model: 'heading2', view: 'h2', title: 'Rubrik', class: 'ck-heading_heading2' },
                           { model: 'heading3', view: 'h3', title: 'Underrubrik', class: 'ck-heading_heading3' }] },
      // Bilder: bara ur föreningens egna bilder (knappen Infoga bild nedan). Ingen uppladdning direkt i texten, ingen bildtext eller storleksändring
      // (saneraren släpper bara igenom en bild med sajtens egen adress och en beskrivning). Klick på en bild ger knappen för beskrivningen.
      removePlugins: ['ImageCaption', 'ImageUpload', 'EasyImage', 'CKFinder', 'CKFinderUploadAdapter', 'CKBox', 'CKBoxUI', 'CKBoxEditing', 'CKBoxUtils', 'CKBoxImageEdit', 'MediaEmbed'],
      // Klick på en bild: placering (vänster med text runt, mitten, höger med text runt), storlek i procent av textbredden, och beskrivningen.
      // Storleken (procent av textbredden) sätts med knapparna ovanför textrutan, se SiteImageWidth: det färdiga CKEditor-bygget har ingen storleksändring.
      image: {
        toolbar: ['imageStyle:alignLeft', 'imageStyle:alignCenter', 'imageStyle:alignRight', '|', 'imageTextAlternative'],
        styles: { options: ['alignLeft', 'alignCenter', 'alignRight'] },
      },
      extraPlugins: [SiteImageWidth],
    }).then(ed => {
      ed.setData(block.data.html || '');
      node.parentNode.insertBefore(h('div', { class: 'mb-2 d-flex flex-wrap align-items-center gap-1' }, h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', onclick: () => insertImage(ed) },
        h('i', { class: 'bi bi-image me-1', 'aria-hidden': 'true' }), 'Infoga bild'), widthBar(() => ed)), node);
      ed.model.document.on('change:data', () => { block.data.html = ed.getData(); markDirty(); });
      S.editors.push(ed);
    }).catch(err => { console.error('CKEditor kunde inte startas:', err && (err.message || err)); node.append(h('p', { class: 'text-danger' }, 'Textredigeraren kunde inte startas.')); });
  }

  // Infoga en av föreningens bilder i texten. Beskrivningen (eller "bara dekoration") krävs, som för bildblocket.
  async function insertImage(ed) {
    const f = await pickImage(); if (!f) return;
    let err = '';
    for (;;) {
      const alt = h('input', { class: 'form-control', maxlength: '200', placeholder: 'Till exempel: Skyttar på 25-metersbanan under årsmötet', 'aria-label': 'Beskriv bilden' });
      const deco = h('input', { type: 'checkbox', class: 'form-check-input', id: 'hsInlineDeco' });
      deco.addEventListener('change', () => { alt.disabled = deco.checked; });
      const r = await modal('Beskriv bilden', [
        err ? h('div', { class: 'alert alert-danger py-2' }, err) : null,
        h('p', { class: 'small text-muted' }, 'Läses upp för den som inte ser bilden. Skriv vad bilden visar, inte "bild på…".'), alt,
        h('div', { class: 'form-check mt-2' }, deco, h('label', { class: 'form-check-label', for: 'hsInlineDeco' }, 'Bara dekoration, ingen beskrivning behövs'))],
        [{ label: 'Avbryt', value: false }, { label: 'Infoga bilden', value: true, cls: 'btn-primary' }]);
      if (r !== true) return;
      const text = alt.value.trim();
      if (!deco.checked && !text) { err = 'Beskriv bilden, eller välj att den bara är dekoration.'; continue; }
      ed.model.change(w => { ed.model.insertContent(w.createElement('imageBlock', { src: imgUrl('file:' + f.id), alt: deco.checked ? '' : text })); });
      ed.editing.view.focus();
      return;
    }
  }

  function blockCard(b, i) {
    const info = typeInfo(b.type), min = minVisFor(b.type);
    const visSel = h('select', { class: 'form-select form-select-sm w-auto', 'aria-label': 'Vem ser blocket' },
      visOptions().filter(o => o.v >= min).map(o => h('option', { value: o.v, selected: o.v === b.visibility ? true : null }, o.t)));
    visSel.addEventListener('change', () => { b.visibility = parseInt(visSel.value, 10); markDirty(); S.refreshWho && S.refreshWho(); });
    const head = h('div', { class: 'hs-block-head' },
      h('span', { class: 'hs-type' }, h('i', { class: 'bi ' + info.icon + ' me-2' }), info.label),
      visOptions().filter(o => o.v >= min).length > 1 ? h('span', { class: 'd-flex align-items-center gap-1 small' }, 'Syns för', visSel) : null,
      h('button', { class: 'btn btn-sm btn-outline-secondary', disabled: neighbour(i, -1) < 0, 'aria-label': 'Flytta upp', onclick: () => move(i, -1) }, h('i', { class: 'bi bi-arrow-up' })),
      h('button', { class: 'btn btn-sm btn-outline-secondary', disabled: neighbour(i, 1) < 0, 'aria-label': 'Flytta ner', onclick: () => move(i, 1) }, h('i', { class: 'bi bi-arrow-down' })),
      h('button', { class: 'btn btn-sm btn-outline-danger', 'aria-label': 'Ta bort blocket', onclick: async () => {
        if (!await confirmDialog('Ta bort blocket?', 'Blocket "' + info.label + '" tas bort från sidan.', 'Ta bort', true)) return;
        S.blocks.splice(i, 1); markDirty(); drawBlocks();
      } }, h('i', { class: 'bi bi-trash' })));
    return h('div', { class: 'hs-block', dataset: undefined }, head, h('div', { class: 'hs-block-body' }, blockBody(b)));
  }

  // Etiketten kopplas till fältet (for/id), så att en skärmläsare säger vad fältet är och ett klick på etiketten går till fältet.
  let fieldSeq = 0;
  function field(label, input, help) {
    if (input && input.nodeType === 1 && /^(INPUT|SELECT|TEXTAREA)$/.test(input.tagName) && !input.id) input.id = 'hsf' + (++fieldSeq);
    return h('div', { class: 'mb-2' }, h('label', { class: 'form-label small fw-semibold mb-1', for: input && input.nodeType === 1 ? input.id : null }, label), input, help ? h('div', { class: 'form-text' }, help) : null);
  }
  function bind(inp, obj, key, conv) { inp.addEventListener('input', () => { obj[key] = conv ? conv(inp.value) : inp.value; markDirty(); }); return inp; }

  function blockBody(b) {
    const d = b.data;
    switch (b.type) {
      case 'text': return h('div', { 'data-rte': b.id });
      case 'heading': return [field('Rubrik', bind(h('input', { class: 'form-control', maxlength: '120', value: d.text || '' }), d, 'text')),
        field('Storlek', bind(h('select', { class: 'form-select w-auto' }, h('option', { value: '2', selected: d.level !== 3 ? true : null }, 'Stor'), h('option', { value: '3', selected: d.level === 3 ? true : null }, 'Mellan')), d, 'level', v => parseInt(v, 10)))];
      case 'links': return linksEditor(b);
      case 'documents': return documentsEditor(b);
      case 'contact': return [field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        h('p', { class: 'text-muted small mb-0' }, 'Visar föreningens kontaktuppgifter. De hämtas vid varje besök och visas aldrig för den som inte är inloggad.')];
      case 'news': return [field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        field('Så många nyheter', bind(h('select', { class: 'form-select w-auto' }, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map(n => h('option', { value: n, selected: n === (d.count || 5) ? true : null }, n))), d, 'count', v => parseInt(v, 10)),
          'Visar de senaste nyheterna ni publicerat som händelsetypen Nyhet. Varje nyhet har sin egen nivå (vem som får läsa den); den som inte får läsa en nyhet ser den inte i listan.')];
      case 'events': return [field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        field('Så många händelser', bind(h('select', { class: 'form-select w-auto' }, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map(n => h('option', { value: n, selected: n === (d.count || 5) ? true : null }, n))), d, 'count', v => parseInt(v, 10)),
          'Visar de närmaste kommande händelserna i kalendern.'),
        field('Vilka händelser?', bind(h('select', { class: 'form-select w-auto' }, h('option', { value: '', selected: !d.type ? true : null }, 'Alla händelser'), h('option', { value: 'Möte', selected: d.type === 'Möte' ? true : null }, 'Bara möten')), d, 'type', v => v || undefined))];
      case 'pagelink': return [field('Vilken sida?', bind(h('select', { class: 'form-select' }, S.st.systemPages.map(p => h('option', { value: p.key, selected: p.key === d.key ? true : null }, p.label))), d, 'key')),
        field('Text på knappen (valfri)', bind(h('input', { class: 'form-control', maxlength: '80', value: d.label || '' }), d, 'label'))];
      case 'image': return imageEditor(b);
      case 'region-clubs': case 'region-ranges': case 'region-stats': return [field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        h('p', { class: 'text-muted small mb-0' }, b.type === 'region-clubs' ? 'Listar kretsens klubbar med länk till deras sidor. Listan hämtas vid varje besök.'
          : b.type === 'region-ranges' ? 'Listar skjutbanorna som kretsens klubbar använder (namn och ort). Banor som är markerade som begränsade visas aldrig här.'
          : 'Visar antal klubbar, skjutbanor, serier och kommande tävlingar i kretsen. Siffrorna hämtas vid varje besök.')];
      case 'region-series': return [field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        field('Så många serier', bind(h('select', { class: 'form-select w-auto' }, [1, 2, 3, 5, 8, 10, 15, 20].map(n => h('option', { value: n, selected: n === (d.count || 10) ? true : null }, n))), d, 'count', v => parseInt(v, 10)),
          'Visar kretsens egna serier, de senaste först.')];
      default: return h('p', { class: 'text-danger' }, 'Okänt block.');
    }
  }

  // ── Bilder (2c) ────────────────────────────────────────────────────────────
  const imgUrl = ref => '/umbraco/surface/SiteFile/Image?id=' + parseInt(String(ref).replace('file:', ''), 10);
  const fmtSize = n => n >= 1048576 ? (n / 1048576).toFixed(1).replace('.', ',') + ' MB' : Math.max(1, Math.round(n / 1024)) + ' kB';

  function imageEditor(b) {
    const d = b.data;
    const altInp = bind(h('input', { class: 'form-control', maxlength: '200', value: d.alt || '', placeholder: 'Till exempel: Skyttar på 25-metersbanan under årsmötet', disabled: d.decorative === true }), d, 'alt');
    const deco = h('input', { type: 'checkbox', class: 'form-check-input', id: 'hsDeco' + b.id, checked: d.decorative === true });
    deco.addEventListener('change', () => { d.decorative = deco.checked; altInp.disabled = deco.checked; markDirty(); });
    const img = h('img', { class: 'hs-imgprev mb-2', src: imgUrl(d.ref), alt: d.alt || '' });
    img.addEventListener('error', () => img.replaceWith(h('p', { class: 'text-danger' }, 'Bilden finns inte längre. Välj en annan bild.')));
    return [img,
      h('div', { class: 'mb-2' }, h('button', { class: 'btn btn-sm btn-outline-secondary', onclick: async () => {
        const f = await pickImage(d.ref); if (!f) return;
        d.ref = 'file:' + f.id; markDirty(); drawBlocks();
      } }, 'Byt bild')),
      field('Beskriv bilden', altInp, 'Läses upp för den som inte ser bilden. Skriv vad bilden visar, inte "bild på…".'),
      h('div', { class: 'form-check mb-2' }, deco, h('label', { class: 'form-check-label', for: 'hsDeco' + b.id }, 'Bara dekoration, ingen beskrivning behövs')),
      field('Bildtext (valfri)', bind(h('input', { class: 'form-control', maxlength: '200', value: d.caption || '' }), d, 'caption'))];
  }

  async function loadFiles() {
    const r = await api('GET', 'Files');
    S.files = r.success ? r : { files: [], count: 0, bytes: 0, maxBytes: 0, maxFiles: 0, failed: r.message || 'Det gick inte att hämta bilderna.' };
    return S.files;
  }

  async function uploadImage(file) {
    const fd = new FormData();
    fd.append('upload', file);
    fd.append('ownerType', OWNER.type); fd.append('ownerId', String(OWNER.id)); fd.append('__RequestVerificationToken', token());
    try {
      const r = await fetch('/umbraco/surface/SiteEdit/UploadImage', { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': token() }, body: fd });
      if (r.status === 413) return { success: false, message: 'Bilden är för stor (högst 20 MB).' };
      return await r.json();
    } catch { return { success: false, message: 'Det gick inte att nå servern. Kontrollera uppkopplingen och försök igen.' }; }
  }

  // Bildväljaren: ladda upp en ny bild eller välj en av föreningens. Svarar med den valda filen, eller null.
  function pickImage(currentRef) {
    return new Promise(async resolve => {
      await loadFiles();
      let chosen = null, done = false;
      const msg = h('div'), info = h('p', { class: 'small text-muted mb-2' }), grid = h('div', { class: 'hs-imggrid' });
      const useBtn = h('button', { type: 'button', class: 'btn btn-primary', disabled: true }, 'Använd bilden');
      const fileInp = h('input', { type: 'file', accept: 'image/jpeg,image/png,image/gif,image/webp', class: 'd-none', 'aria-label': 'Välj en bild att ladda upp' });
      const upBtn = h('button', { type: 'button', class: 'btn btn-outline-primary', onclick: () => fileInp.click() }, h('i', { class: 'bi bi-upload me-1' }), 'Ladda upp en bild');
      const show = (cls, text) => msg.replaceChildren(text ? h('div', { class: 'alert alert-' + cls + ' py-2' }, text) : '');

      const paint = () => {
        const fs = S.files.files || [];
        info.textContent = S.files.failed ? '' : fs.length + ' bilder, ' + fmtSize(S.files.bytes || 0) + ' av ' + fmtSize(S.files.maxBytes || 0) + ' används.';
        if (S.files.failed) show('danger', S.files.failed);
        grid.replaceChildren(...(fs.length ? fs.map(f => h('div', { class: 'hs-imgtile' + (chosen && chosen.id === f.id ? ' sel' : '') },
          h('button', { type: 'button', class: 'hs-imgpick', 'aria-pressed': chosen && chosen.id === f.id ? 'true' : 'false', 'aria-label': 'Välj ' + f.name, onclick: () => { chosen = f; useBtn.disabled = false; paint(); } },
            h('img', { src: f.url, alt: '', loading: 'lazy' }), h('span', { class: 'hs-imgname d-block', title: f.name }, f.name)),
          h('span', { class: 'small text-muted' }, f.usedBy > 0 ? 'Används på ' + f.usedBy + (f.usedBy === 1 ? ' sida' : ' sidor') : 'Används inte'),
          f.usedBy === 0 ? h('button', { type: 'button', class: 'btn btn-sm btn-outline-danger', onclick: async () => {
            const r = await api('POST', 'DeleteFile', { fileId: f.id });
            if (!r.success) { show('danger', r.message || 'Det gick inte att ta bort bilden.'); return; }
            if (chosen && chosen.id === f.id) { chosen = null; useBtn.disabled = true; }
            await loadFiles(); show('', ''); paint();
          } }, 'Ta bort') : null))
          : [h('p', { class: 'text-muted' }, 'Ni har inga bilder än. Ladda upp den första.')]));
      };
      fileInp.addEventListener('change', async () => {
        const f = fileInp.files[0]; fileInp.value = ''; if (!f) return;
        upBtn.disabled = true; show('info', 'Laddar upp …');
        const r = await uploadImage(f);
        upBtn.disabled = false;
        if (!r.success) { show('danger', r.message || 'Det gick inte att ladda upp bilden.'); return; }
        show('success', 'Bilden är uppladdad.');
        await loadFiles(); chosen = (S.files.files || []).find(x => x.id === r.id) || null; useBtn.disabled = !chosen; paint();
      });

      const el = h('div', { class: 'modal fade', tabindex: '-1' },
        h('div', { class: 'modal-dialog modal-dialog-centered modal-lg modal-dialog-scrollable' }, h('div', { class: 'modal-content' },
          h('div', { class: 'modal-header' }, h('h2', { class: 'modal-title h5' }, 'Välj bild'), h('button', { type: 'button', class: 'btn-close', 'data-bs-dismiss': 'modal', 'aria-label': 'Stäng' })),
          h('div', { class: 'modal-body' },
            h('p', { class: 'text-muted small' }, 'Bilden skrivs om när den laddas upp: platsdata och annan dold information tas bort och en mycket stor bild förminskas. Lägg inte upp bilder där någon kan kännas igen utan att ha gett sitt tillstånd.'),
            h('div', { class: 'mb-3' }, upBtn, fileInp), msg, info, grid),
          h('div', { class: 'modal-footer' }, h('button', { type: 'button', class: 'btn btn-secondary', 'data-bs-dismiss': 'modal' }, 'Avbryt'), useBtn))));
      document.body.append(el);
      const bsm = new bootstrap.Modal(el);
      useBtn.addEventListener('click', () => { done = true; bsm.hide(); resolve(chosen); });
      el.addEventListener('hidden.bs.modal', () => { el.remove(); if (!done) resolve(null); });
      if (currentRef) chosen = (S.files.files || []).find(x => 'file:' + x.id === currentRef) || null;
      useBtn.disabled = !chosen; paint(); bsm.show();
    });
  }

  function linksEditor(b) {
    const d = b.data, wrap = h('div');
    const draw = () => {
      wrap.replaceChildren(...d.items.map((it, i) => h('div', { class: 'row g-2 mb-2 align-items-end' },
        h('div', { class: 'col-12 col-md-4' }, field(i === 0 ? 'Text på länken' : '', bind(h('input', { class: 'form-control', maxlength: '80', value: it.label || '', placeholder: 'Till exempel: Förbundet' }), it, 'label'))),
        h('div', { class: 'col-12 col-md-6' }, field(i === 0 ? 'Adress' : '', bind(h('input', { class: 'form-control', maxlength: '500', value: it.url || '', placeholder: 'https://…' }), it, 'url'))),
        h('div', { class: 'col-12 col-md-2 mb-2' }, h('button', { class: 'btn btn-outline-danger w-100', disabled: d.items.length < 2, onclick: () => { d.items.splice(i, 1); markDirty(); draw(); } }, 'Ta bort')))),
        h('button', { class: 'btn btn-sm btn-outline-primary', disabled: d.items.length >= 20, onclick: () => { d.items.push({ label: '', url: '' }); markDirty(); draw(); } }, '＋ Fler länkar'),
        h('p', { class: 'text-muted small mt-2 mb-0' }, 'Adressen ska börja med https:// (eller / för en sida på pistol.nu).'));
    };
    draw(); return wrap;
  }

  function documentsEditor(b) {
    const d = b.data;
    const chosen = new Set((d.refs || []).map(r => parseInt(String(r).replace('doc:', ''), 10)));
    const list = (S.docs || []).length
      ? h('div', { class: 'list-group mb-2' }, S.docs.map(doc => {
        const cb = h('input', { type: 'checkbox', class: 'form-check-input me-2', checked: chosen.has(doc.id) });
        cb.addEventListener('change', () => { if (cb.checked) chosen.add(doc.id); else chosen.delete(doc.id); d.refs = [...chosen].map(i => 'doc:' + i); markDirty(); });
        return h('label', { class: 'list-group-item d-flex align-items-center' }, cb, h('span', { class: 'me-auto' }, doc.title), h('span', { class: 'badge text-bg-light border' }, doc.level));
      }))
      : h('p', { class: 'text-muted' }, 'Ni har inga dokument i dokumentarkivet än. Ladda upp dokument under klubbens Dokument-flik, så kan ni välja dem här.');
    return [field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')), list,
      h('p', { class: 'text-muted small mb-0' }, 'Varje dokument har också sin egen nivå (vem som får öppna det). Den som inte får öppna ett dokument ser det inte i listan.')];
  }

  // Spara det som finns om sidan lämnas.
  window.addEventListener('beforeunload', e => { if (S.dirty) { e.preventDefault(); e.returnValue = ''; } });
  load();
})();
