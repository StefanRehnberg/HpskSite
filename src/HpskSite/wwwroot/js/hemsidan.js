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
  // Blockregistret kommer ur servern (State.blockGroups / blockTypes / blockTasks, SiteBlockCatalog): grupper, etiketter, ikoner, beskrivningar, standardinnehåll och uppgiftskort.
  // Ingen egen lista här, så ett block heter samma sak i väljaren, i redigeraren och i "Vem ser sidan?". `description` = en rad som säger vad blocket visar.
  const blockGroups = () => (S.st && S.st.blockGroups) || [];
  const blockTypes = () => (S.st && S.st.blockTypes) || [];
  const blockTasks = () => (S.st && S.st.blockTasks) || [];
  const typeInfo = t => blockTypes().find(x => x.type === t) || { type: t, label: t, icon: 'bi-square', description: '', group: -1, slot: false, live: false };
  const minVisFor = type => Math.max(S.visibility, ((S.st && S.st.blockFloors && S.st.blockFloors[type]) || 0));   // golvet kommer ur serverns golv-tabell (State.blockFloors), aldrig en egen regel här   // personblock aldrig "Alla"; ett block aldrig öppnare än sidan
  const newId = () => Array.from(crypto.getRandomValues(new Uint8Array(8)), b => 'abcdefghijklmnopqrstuvwxyz0123456789'[b % 36]).join('');
  const pad = n => String(n).padStart(2, '0');
  const clock = () => { const d = new Date(); return pad(d.getHours()) + ':' + pad(d.getMinutes()); };

  const S = {
    st: null, page: null, blocks: [], title: '', visibility: 2, revision: 0, published: false, hasUnpublished: false,
    kind: 'free', minVis: 0, templateKey: null,
    dirty: false, editVersion: 0, saving: false, timer: null, ack: new Set(), editors: [], docs: null, banner: null, statusText: '', statusErr: false, hint: null,
    // Redigeraren: högst ett block är öppet (bara det har en textredigerare), det öppna blockets innehåll återanvänds när listan ritas om, ändringarna går att ångra,
    // och det senast tillagda blocket är markerat tills nästa ändring.
    openId: null, bodyCache: new Map(), undo: [], justAdded: null, toastTimer: null, focusAfterRte: null,
  };

  // ── Dialoger (bootstrap-modal, aldrig alert/confirm) ────────────────────────
  let modalSeq = 0;
  function modal(title, bodyNodes, buttons, wide) {
    return new Promise(resolve => {
      const tid = 'hsModalT' + (++modalSeq);   // dialogen får ett tillgängligt namn (rubriken)
      const el = h('div', { class: 'modal fade', tabindex: '-1', 'aria-labelledby': tid },
        h('div', { class: 'modal-dialog modal-dialog-centered' + (wide ? ' modal-xl' : '') }, h('div', { class: 'modal-content' },
          h('div', { class: 'modal-header' }, h('h2', { class: 'modal-title h5', id: tid }, title), h('button', { type: 'button', class: 'btn-close', 'data-bs-dismiss': 'modal', 'aria-label': 'Stäng' })),
          h('div', { class: 'modal-body' }, bodyNodes),
          h('div', { class: 'modal-footer' }, buttons.map(b => h('button', {
            type: 'button', class: 'btn ' + (b.cls || 'btn-secondary'), disabled: b.disabled,
            onclick: () => { b.done = true; bs.hide(); resolve(b.value); },
          }, b.label))))));
      document.body.append(el);
      const bs = new bootstrap.Modal(el);
      const opener = document.activeElement;   // fokus tillbaka till det som öppnade dialogen (Bootstrap gör det bara för data-attribut), om det finns kvar
      el.addEventListener('hidden.bs.modal', () => { el.remove(); resolve(null); if (opener && opener.isConnected && opener !== document.body) opener.focus(); });
      bs.show();
    });
  }
  const confirmDialog = (title, text, ok, danger) => modal(title, h('p', {}, text), [
    { label: 'Avbryt', value: false }, { label: ok, value: true, cls: danger ? 'btn-danger' : 'btn-primary' }]).then(v => v === true);

  // ── Vyer ───────────────────────────────────────────────────────────────────
  function clearEditors() { for (const e of S.editors) { try { e.destroy(); } catch { /* redan borta */ } } S.editors = []; S.bodyCache.clear(); }
  /** Städar textredigerarna utom det öppna blockets (som lever kvar när listan ritas om, så fokus och markör finns kvar). */
  function pruneEditors(keepId) {
    S.editors = S.editors.filter(e => { if (e._blockId === keepId && keepId != null) return true; try { e.destroy(); } catch { /* redan borta */ } return false; });
    for (const id of [...S.bodyCache.keys()]) if (id !== keepId) S.bodyCache.delete(id);
  }
  // Förhandsvisningen av en färg (Utseende) lever bara medan Utseende-vyn visas: varje annan vy städar bort den.
  function clearAppearancePreview() {
    const el = document.getElementById('hsApPreview'); if (el) el.remove();
    if (S.apBodyAdded) { document.body.classList.remove('owner-themed'); S.apBodyAdded = false; }
  }
  function view(nodes) { clearEditors(); clearAppearancePreview(); clearTimeout(S.timer); clearTimeout(S.toastTimer); S.refreshWho = null; document.documentElement.style.scrollPaddingTop = ''; body.replaceChildren(...[].concat(nodes).filter(Boolean)); }
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
  // ⚠️ kids.flat(): anropen skickar ibland en lista med noder som ett argument (till exempel [' ', länk]). Utan flat() blev den nästlade listan text, ", https://…", i stället för länken "Se klubbens sida".
  const alertBox = (cls, ...kids) => h('div', { class: 'alert alert-' + cls }, kids.flat());

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
    if (hsh === '#lankar') return renderLinks();
    if (hsh === '#utseende') return renderAppearance();
    if (hsh === '#bilder') return renderLibrary();
    if (hsh === '#anmalningar') return renderSignups();
    if (hsh === '#logg') return renderAudit();
    if ((m = /^#ny(?:=(\d+))?$/.exec(hsh))) return renderNew(null, m[1] ? parseInt(m[1], 10) : null);   // #ny=12 = en ny undersida till sidan 12
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

  // Tutorialnodens id i TutorialHelper (tutorialnodens fält tutorialId). Knappen "Guide" sitter i sidhuvudet, till vänster om Åtgärder (pageHead), som på de andra arbetsytorna.
  const GUIDE_ID = 'tutorial-designa-klubbsidan';

  function rail(active) {
    const st = S.st, sys = st.systemPages || [];
    const nav = (key, label, hash, extra) => h('a', { href: hash, class: 'hs-nav' + (active === key ? ' on' : ''), 'aria-current': active === key ? 'page' : null,
      onclick: e => { e.preventDefault(); go(hash); } }, h('span', {}, label), extra || null);
    const pill = p => p.mode === 'draft' ? h('span', { class: 'hs-pill warn' }, 'Utkast') : p.mode === 'converted' ? h('span', { class: 'hs-pill own' }, 'Anpassad') : null;
    const here = active === 'sidor' ? 'Sidor' : active === 'raderade' ? 'Raderade sidor' : active === 'lankar' ? 'Länkar i menyn' : active === 'utseende' ? 'Utseende' : active === 'bilder' ? 'Bilder' : active === 'anmalningar' ? 'Anmälningar' : active === 'logg' ? 'Vad som hänt' : ((sys.find(p => 'sys:' + p.key === active) || {}).label || 'Sidor');
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
        h('div', { class: 'hs-railh' }, 'Mer'),
        st.support ? null : nav('anmalningar', 'Anmälningar', '#anmalningar', st.signupNew ? h('span', { class: 'hs-pill warn' }, st.signupNew + ' nya') : null),   // personuppgifter: aldrig i stödläge
        nav('utseende', 'Utseende', '#utseende'),
        nav('bilder', 'Bilder', '#bilder'),
        nav('lankar', 'Länkar i menyn', '#lankar', (st.menuLinks || []).length ? h('span', { class: 'hs-pill' }, st.menuLinks.length) : null),
        st.trash.length ? nav('raderade', 'Raderade sidor', '#raderade', h('span', { class: 'hs-pill' }, st.trash.length)) : null,
        st.support ? null : nav('logg', 'Vad som hänt', '#logg')));
  }
  /** Skalet runt en vy: rälsen till vänster, innehållet (och stödlägesbanderollen) till höger. */
  function shell(active, ...nodes) {
    return h('div', { class: 'hs-shell' }, rail(active), h('div', { class: 'hs-main' }, supportBanner(), nodes));
  }
  /** Rubrik med blå Åtgärder uppe till höger (adminlistans form, som Träningar). */
  function pageHead(title, sub, menuItems) {
    const guide = GUIDE_ID && typeof TutorialHelper !== 'undefined' ? TutorialHelper.guideButton(GUIDE_ID, { title: 'Se instruktionsfilm om Hemsidan' }) : null;
    const actions = menuItems && menuItems.filter(Boolean).length ? h('div', { class: 'dropdown' },
      h('button', { class: 'btn btn-primary shadow dropdown-toggle', type: 'button', 'data-bs-toggle': 'dropdown', 'data-bs-popper-config': '{"strategy":"fixed"}', 'aria-expanded': 'false' }, 'Åtgärder'),
      menuList(menuItems)) : null;
    return h('div', { class: 'hs-head' }, h('div', {}, h('h1', {}, title), sub ? h('div', { class: 'hs-sub' }, sub) : null),
      guide || actions ? h('div', { class: 'hs-head-actions' }, guide, actions) : null);
  }
  /** Menyrader: [text, fn, { href?, danger?, disabled? }]; '-' = avdelare; null hoppas över. */
  function menuList(items) {
    return h('ul', { class: 'dropdown-menu dropdown-menu-end' }, items.filter(Boolean).map(it => it === '-' ? h('li', {}, h('hr', { class: 'dropdown-divider' }))
      : h('li', {}, h('a', { class: 'dropdown-item' + (it[2] && it[2].danger ? ' text-danger' : '') + (it[2] && it[2].disabled ? ' disabled' : ''), href: (it[2] && it[2].href) || '#',
        target: it[2] && it[2].href ? '_blank' : null, rel: it[2] && it[2].href ? 'noopener' : null,
        onclick: e => { if (it[2] && it[2].href) return; e.preventDefault(); if (!(it[2] && it[2].disabled)) it[1](); } }, it[0]))));
  }
  /** Grå Åtgärder per rad (menyn är fixed-positionerad så att den inte klipps i tabellen). `label` ger knappen ett eget namn ("Åtgärder för Rubrik, block 3 av 8") så att åtta lika knappar går att skilja åt. */
  const rowMenu = (items, label) => h('div', { class: 'dropdown' },
    h('button', { class: 'btn btn-sm btn-outline-secondary dropdown-toggle hs-menu-toggle', type: 'button', 'data-bs-toggle': 'dropdown', 'data-bs-popper-config': '{"strategy":"fixed"}', 'aria-expanded': 'false', 'aria-label': label || null }, 'Åtgärder'),
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
  // Nivåmärket: ordet bär nivån, ikonen och färgen förstärker (färg är aldrig ensam bärare). EN funktion för sidlistan, blocken och panelerna.
  const LEVEL_ICON = ['bi-globe2', 'bi-person-check-fill', 'bi-people-fill', 'bi-shield-fill', 'bi-lock-fill'];
  const levelIcon = v => h('i', { class: 'bi ' + (LEVEL_ICON[v] || 'bi-lock-fill') + ' me-1', 'aria-hidden': 'true' });
  const visBadge = v => h('span', { class: 'badge hs-badge-' + v }, levelIcon(v), visLabel(v));
  /** Små tillståndsmärken på ett blockkort (samma form som sidlistans hs-state). */
  const stateMark = (cls, text, title) => h('span', { class: 'hs-state ' + cls, title: title || null }, text);

  /**
   * Nivåmärket på ett kort, som en knapp: ordet (och ikonen) visar nivån, ett klick öppnar valet med en förklaring under varje nivå. Nivåer som blocket inte får ha visas nedtonade med
   * skälet (aldrig dolda). Ändringen uppdaterar BARA märket (ingen omritning, så textredigeraren och fokus lever kvar). opts: { min, extraAllowed(v), reason(v), onPick(v) → true/false }.
   * Golvet kommer ur serverns tabell (minVisFor); komponenten har ingen egen regel.
   */
  function levelPicker(item, name, opts) {
    const btn = h('button', { type: 'button', class: 'badge hs-lvl', 'data-bs-toggle': 'dropdown', 'data-bs-popper-config': '{"strategy":"fixed"}', 'aria-expanded': 'false', 'data-block-level': item.id });
    const paint = () => {
      btn.className = 'badge hs-lvl hs-badge-' + item.visibility;
      btn.dataset.level = String(item.visibility);
      btn.setAttribute('aria-label', 'Syns för: ' + visLabel(item.visibility) + '. Ändra.');
      btn.title = 'Syns för: ' + visLabel(item.visibility) + '. Klicka för att ändra.';
      btn.replaceChildren(levelIcon(item.visibility), visLabel(item.visibility), h('i', { class: 'bi bi-chevron-down ms-1', 'aria-hidden': 'true' }));
    };
    paint();
    const allowed = v => v >= opts.min || (opts.extraAllowed ? opts.extraAllowed(v) : false);
    const rows = visOptions().map(o => {
      const ok = allowed(o.v), on = item.visibility === o.v;
      return h('li', {}, h('button', { type: 'button', class: 'dropdown-item hs-lvl-item' + (ok ? '' : ' disabled'), 'aria-disabled': ok ? null : 'true', 'aria-current': on ? 'true' : null, 'data-level-pick': o.v,
        onclick: async e => {
          if (!ok) { e.stopPropagation(); return; }
          if (on) return;
          if (await opts.onPick(o.v) === false) return;
          paint();
        } },
        h('span', { class: 'd-flex align-items-center gap-1' }, h('span', { class: 'badge hs-badge-' + o.v }, levelIcon(o.v), o.t), on ? h('i', { class: 'bi bi-check-lg ms-auto', 'aria-hidden': 'true' }) : null),
        h('span', { class: 'd-block small ' + (ok ? 'text-body-secondary' : 'text-body-secondary fst-italic') }, ok ? o.s : (opts.reason ? opts.reason(o.v) : 'Går inte att välja här.'))));
    });
    return h('div', { class: 'dropdown d-inline-block' }, btn,
      h('ul', { class: 'dropdown-menu hs-lvl-menu' }, rows, h('li', {}, h('hr', { class: 'dropdown-divider' })),
        h('li', {}, h('button', { type: 'button', class: 'dropdown-item small', onclick: () => modal('Vem ser vad?', levelHelp(), [{ label: 'Stäng', value: true, cls: 'btn-primary' }]) }, 'Vad betyder nivåerna?'))));
  }

  // Kolumnrubrik med en (i)-knapp som öppnar en förklaring. Knappen har en egen etikett så att den går att nå och förstå med tangentbord och skärmläsare.
  const infoBtn = (label, title, body, extra) => h('button', { type: 'button', class: 'btn btn-link p-0 align-baseline hs-info ' + (extra || 'ms-1'), 'aria-label': 'Förklaring av ' + label, 'data-info': label,
    onclick: () => modal(title, body, [{ label: 'Stäng', value: true, cls: 'btn-primary' }]) }, h('i', { class: 'bi bi-info-circle', 'aria-hidden': 'true' }));
  const infoTh = (label, title, body) => h('th', {}, label, infoBtn(label, title, body));
  const helpRow = (head, text) => h('div', { class: 'mb-3' }, h('div', { class: 'mb-1' }, head), h('div', { class: 'text-muted' }, text));
  const typeHelp = () => h('div', {},
    helpRow(h('strong', {}, 'Standard'), 'pistol.nu:s färdiga sida. Innehållet hämtas automatiskt ur ' + (IS_REGION ? 'kretsens' : 'klubbens') + ' uppgifter. Ni kan lägga egen text före eller efter.'),
    helpRow(h('strong', {}, 'Anpassad'), 'Ni har gjort om standardsidan: flyttat, tagit bort eller lagt till egna delar. Besökarna ser er version.'),
    helpRow(h('strong', {}, 'Anpassad (utkast)'), 'Ni har börjat anpassa sidan men inte publicerat. Besökarna ser fortfarande standardsidan.'),
    helpRow(h('strong', {}, 'Egen sida'), 'En sida ni har skapat helt själva. Alla sidor i listan är publicerade, utom en egen sida som har märket Utkast, Publicerad, med osparade ändringar eller Stoppad av pistol.nu. Ett utkast ser bara ni. Undersidor är egna sidor som ligger under Om ' + (IS_REGION ? 'kretsen' : 'Klubben') + '.'));
  const levelHelp = () => h('div', {},
    h('p', { class: 'text-muted' }, 'Nivån säger vem som får läsa sidan. Den som inte får se en sida får beskedet att den inte finns (en låst medlemssida visas med ett hänglås).'),
    ...VIS.map(o => helpRow(h('span', {}, h('strong', {}, o.t), h('span', { class: 'text-muted' }, ' – ' + o.s.charAt(0).toLowerCase() + o.s.slice(1))), CONSEQUENCE[o.v])),
    h('p', { class: 'small text-muted mb-0' }, 'För en standardsida visas den öppnaste nivån av dess delar. Ett block på en sida kan aldrig vara öppnare än sidan, och block med personuppgifter kan aldrig vara öppna för alla.'));

  async function copyLink(url) {
    const full = location.origin + url;
    try { await navigator.clipboard.writeText(full); } catch {
      const t = h('textarea', { style: 'position:fixed;opacity:0' }); t.value = full; document.body.append(t); t.select(); try { document.execCommand('copy'); } catch { /* ingen utklippshanterare */ } t.remove();
    }
    flash('Länken är kopierad. Den fungerar för: ' + (VIS.find(o => o.v === S.visibility)?.s || 'dem som får se sidan').toLowerCase() + '.');
  }
  function flash(text) { S.banner = { cls: 'success', text }; const b = document.getElementById('hsBanner'); if (b) renderBanner(); }

  // I redigeraren för en egen sida: vilken sida den ligger under, eller hur många undersidor den har.
  function treeInfo() {
    const p = S.page || {};
    if (p.parentId) return h('p', { class: 'small text-muted mb-2' }, 'Undersida till ', h('a', { href: '#redigera=' + p.parentId, onclick: e => { e.preventDefault(); go('#redigera=' + p.parentId); } }, p.parentTitle || 'en annan sida'), '.');
    if (p.childCount > 0) return h('p', { class: 'small text-muted mb-2' }, 'Har ' + p.childCount + (p.childCount === 1 ? ' undersida' : ' undersidor') + '. ', h('a', { href: '#sidor', onclick: e => { e.preventDefault(); go('#sidor'); } }, 'Visa i sidlistan'), '.');
    return null;
  }

  // En omgjord Om-sida kan vara förälder (Stefans väg A, 2026-11-09). Den har en egen rad i servern (pageId) men står i systemsidorna, inte bland de egna: här får den samma form som en egen sida.
  const aboutParent = () => {
    const p = ((S.st && S.st.systemPages) || []).find(x => x.key === 'about' && x.mode === 'converted' && x.pageId);
    return p ? { id: p.pageId, title: p.label, visibility: p.level, parentId: null, system: true } : null;
  };
  // Sidorna som kan vara förälder: egna sidor på egen nivå, och en omgjord Om-sida.
  const parentCandidatesOf = () => [].concat(aboutParent() || [], (S.st.pages || []).filter(x => !x.parentId));
  const findParent = id => id ? parentCandidatesOf().find(x => x.id === id) || null : null;
  const childrenOfAbout = () => { const a = aboutParent(); return a ? (S.st.pages || []).filter(x => x.parentId === a.id) : []; };

  // Sidor under sidor: trädordningen (sidor på egen nivå i sin ordning, var och en följd av sina undersidor). Samma regel som servern (SitePageTree.Flatten): en undersida vars förälder saknas
  // räknas som egen nivå, och ingen sida får försvinna ur listan.
  function treeRows(pages) {
    const ids = new Set(pages.map(p => p.id));
    const valid = new Set(pages.filter(p => !p.parentId || !ids.has(p.parentId) || p.parentId === p.id).map(p => p.id));
    const by = (a, b) => (a.sortOrder - b.sortOrder) || a.title.localeCompare(b.title, 'sv') || (a.id - b.id);
    const top = pages.filter(p => !p.parentId || !valid.has(p.parentId) || p.parentId === p.id).sort(by);
    const out = [];
    for (const t of top) {
      out.push({ p: t, depth: 0 });
      if (valid.has(t.id)) for (const c of pages.filter(x => x.parentId === t.id && x.id !== t.id).sort(by)) out.push({ p: c, depth: 1 });
    }
    const seen = new Set(out.map(o => o.p.id));
    for (const x of pages.filter(x => !seen.has(x.id)).sort(by)) out.push({ p: x, depth: 0 });
    return out;
  }

  async function movePage(p, direction) {
    const r = await api('POST', 'MovePage', { pageId: p.id, direction });
    if (!r.success) S.banner = { cls: 'danger', text: r.message || 'Det gick inte att flytta sidan.' };
    await reloadState();
  }

  // Flytta en sida under en annan egen sida (eller till egen nivå). En undersida kan inte vara öppnare än sin förälder, så sidan får förälderns nivå om den var öppnare.
  async function chooseParent(p) {
    const candidates = parentCandidatesOf().filter(x => x.id !== p.id);
    let chosen = p.parentId || 0;
    const box = h('div');
    const draw = () => box.replaceChildren(
      h('label', { class: 'hs-choice ' + (chosen === 0 ? 'active' : '') }, h('input', { type: 'radio', name: 'parent', checked: chosen === 0, onchange: () => { chosen = 0; draw(); } }),
        h('strong', {}, 'Egen nivå'), h('span', { class: 'hs-sub' }, 'Sidan ligger direkt i menyn, inte under någon annan sida.')),
      ...candidates.map(c => h('label', { class: 'hs-choice ' + (chosen === c.id ? 'active' : '') }, h('input', { type: 'radio', name: 'parent', checked: chosen === c.id, onchange: () => { chosen = c.id; draw(); } }),
        h('strong', {}, c.title), h('span', { class: 'hs-sub' }, 'Syns för: ' + visLabel(c.visibility).toLowerCase() + (c.visibility > p.visibility ? '. Sidan får då samma nivå, eftersom en undersida inte kan vara öppnare.' : '')))),
      candidates.length ? null : h('p', { class: 'text-muted' }, 'Det finns ingen annan egen sida att lägga den under.'));
    draw();
    const ok = await modal('Flytta "' + p.title + '" under …', box, [{ label: 'Avbryt', value: false }, { label: 'Flytta', value: true, cls: 'btn-primary' }]);
    if (ok !== true || chosen === (p.parentId || 0)) return;
    const r = await api('POST', 'SetParent', { pageId: p.id, parentPageId: chosen });
    S.banner = r.success
      ? { cls: 'success', text: chosen ? 'Sidan ligger nu under en annan sida.' + (r.affected ? ' Dess nivå eller menyplats ändrades så att den passar där.' : '') : 'Sidan ligger nu på egen nivå.' }
      : { cls: 'danger', text: r.message || 'Det gick inte att flytta sidan.' };
    await reloadState();
  }

  // Översikten (förslag 3): EN tabell över alla sidor — klubbens vanliga sidor och de egna — med läget som ord. Åtgärder är blå uppe till höger, radmenyn grå.
  function renderList() {
    setHash('#sidor');
    const st = S.st, sys = st.systemPages || [];
    const slotByKey = Object.fromEntries((st.slots || []).map(x => [x.key, x]));
    const hiddenCount = st.pages.filter(p => p.visibility === 4).length;
    const own = st.pages.filter(p => S.showHidden || p.visibility !== 4);
    const total = sys.length + st.pages.length, shown = sys.length + own.length;
    const full = st.pages.length >= st.maxPages;

    const aboutP = aboutParent();
    const bySort = (a, b) => (a.sortOrder - b.sortOrder) || a.title.localeCompare(b.title, 'sv') || (a.id - b.id);
    // Egna sidor som träd: sidor på egen nivå i sin ordning, var och en följd av sina undersidor (indragna). Dolda sidor filtreras bort efteråt så att ordningen alltid är hela trädets.
    // Undersidorna till Om klubben ligger under Om-raden bland de vanliga sidorna, inte i trädet.
    const tree = treeRows(st.pages.filter(x => !(aboutP && x.parentId === aboutP.id)));
    const aboutKids = childrenOfAbout().sort(bySort);
    const siblingsOf = p => aboutP && p.parentId === aboutP.id ? aboutKids
      : tree.filter(t => (t.p.parentId || 0) === (p.parentId || 0) && t.depth === (p.parentId ? 1 : 0)).map(t => t.p);
    const ownRow = (p, depth) => {
      const sibs = siblingsOf(p), at = sibs.findIndex(x => x.id === p.id);
      return h('tr', {},
        h('td', { class: depth ? 'ps-4' : '' },
          depth ? h('span', { class: 'text-muted me-1', 'aria-hidden': 'true' }, '↳') : null,
          h('span', { class: 'hs-pageicon me-2' }, iconEl(p.icon)),
          h('a', { href: '#redigera=' + p.id, class: 'fw-semibold text-decoration-none', onclick: e => { e.preventDefault(); go('#redigera=' + p.id); } }, p.title),
          depth ? h('span', { class: 'visually-hidden' }, ' (undersida)') : null,
          p.childCount ? h('span', { class: 'text-muted small ms-2' }, p.childCount + (p.childCount === 1 ? ' undersida' : ' undersidor')) : null),
        h('td', {}, h('span', { class: 'hs-state own' }, depth ? 'Undersida' : 'Egen sida'), p.status === 'Publicerad' ? null : [' ', statusBadge(p)]),   // en publicerad sida är det normala och får inget märke, annars ser de andra raderna ut som opublicerade
        h('td', {}, visBadge(p.visibility)),
        h('td', { class: 'text-end' }, rowMenu([
          ['Ändra', () => go('#redigera=' + p.id)],
          p.status !== 'Utkast' && p.url ? ['Se sidan', null, { href: p.url }] : null,
          p.status !== 'Utkast' && p.url ? ['Kopiera länk', () => { S.visibility = p.visibility; copyLink(p.url); }] : null,
          '-',
          !p.parentId ? ['Ny undersida', () => go('#ny=' + p.id), full ? { disabled: true } : null] : null,   // bara en sida på egen nivå kan ha undersidor
          ['Flytta upp', () => movePage(p, -1), at <= 0 ? { disabled: true } : null],
          ['Flytta ner', () => movePage(p, 1), at < 0 || at >= sibs.length - 1 ? { disabled: true } : null],
          p.childCount ? null : ['Flytta under …', () => chooseParent(p)],   // en sida med undersidor kan inte själv bli en undersida (två nivåer)
          '-',
          p.childCount ? ['Radera (flytta undersidorna först)', null, { disabled: true }] : ['Radera', async () => {
            if (!await confirmDialog('Radera sidan?', 'Sidan "' + p.title + '" tas bort från sajten. Ni kan ångra det i 30 dagar.', 'Radera', true)) return;
            const r = await api('POST', 'DeletePage', { pageId: p.id, expectedRevision: p.revision });
            S.banner = r.success ? { cls: 'success', text: 'Sidan är raderad. Du kan ångra under "Raderade sidor".' } : { cls: 'danger', text: r.message };
            await reloadState();
          }, { danger: true }]])));
    };
    const visibleKids = () => aboutKids.filter(p => S.showHidden || p.visibility !== 4);
    const sysRows = sys.flatMap(p => {
      const slot = slotByKey[p.key];
      const canParent = aboutP && p.key === 'about' && p.pageId === aboutP.id;
      const row = h('tr', {},
        h('td', {}, h('a', { href: '#sida=' + p.key, class: 'fw-semibold text-decoration-none', onclick: e => { e.preventDefault(); go('#sida=' + p.key); } }, p.label),
          canParent && aboutKids.length ? h('span', { class: 'text-muted small ms-2' }, aboutKids.length + (aboutKids.length === 1 ? ' undersida' : ' undersidor')) : null),
        h('td', {}, modePill(p), slot && slot.page && p.mode === 'asBefore' ? h('span', { class: 'text-muted small ms-2' }, 'med egen text') : null),
        h('td', {}, visBadge(p.level)),
        h('td', { class: 'text-end' }, rowMenu([['Öppna', () => go('#sida=' + p.key)],
          canParent ? ['Ny undersida', () => go('#ny=' + p.pageId), full ? { disabled: true } : null] : null])));
      return canParent ? [row, ...visibleKids().map(c => ownRow(c, 1))] : [row];
    });
    const ownRows = tree.filter(t => S.showHidden || t.p.visibility !== 4).map(({ p, depth }) => ownRow(p, depth));

    const filter =h('div', { class: 'd-flex flex-wrap gap-3 align-items-center px-3 pt-3 pb-1' },
      hiddenCount ? h('label', { class: 'd-flex gap-2 align-items-center' }, h('input', { type: 'checkbox', class: 'form-check-input mt-0', checked: !!S.showHidden,
        onchange: e => { S.showHidden = e.target.checked; renderList(); } }), 'Visa dolda (' + hiddenCount + ')') : null,
      h('span', { class: 'text-muted ms-auto' }, shown + ' av ' + total));
    view(shell('sidor',
      pageHead('Sidor', 'Här ser du alla sidor på ' + (IS_REGION ? 'kretsens' : 'klubbens') + ' hemsida. De vanliga sidorna finns redan. Egna sidor lägger du till under Åtgärder → Ny sida.', [
        ['Ny sida', () => go('#ny'), full ? { disabled: true } : null],
        st.trash.length ? ['Raderade sidor (' + st.trash.length + ')', () => go('#raderade')] : null]),
      h('div', { id: 'hsBanner' }),
      h('div', { class: 'd-md-none d-flex flex-wrap gap-3 mb-2 small', 'data-phone-help': '1' },
        h('span', {}, 'Typ ', infoBtn('Typ', 'Vilken typ av sida det är', typeHelp(), 'ms-0')), h('span', {}, 'Vem ser den ', infoBtn('Vem ser den', 'Vem ser sidan?', levelHelp(), 'ms-0'))),
      h('div', { class: 'hs-card p-0 overflow-hidden mb-3' }, filter,
        h('table', { class: 'table align-middle hs-list mb-0' },
          h('thead', {}, h('tr', {}, h('th', { class: 'ps-3' }, 'Sida'), infoTh('Typ', 'Vilken typ av sida det är', typeHelp()), infoTh('Vem ser den', 'Vem ser sidan?', levelHelp()), h('th', { class: 'text-end pe-3' }, 'Åtgärder'))),
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
    const intro = p.mode === 'asBefore' ? 'Det här är standardsidan. Innehållet hämtas automatiskt.' + (p.canConvert ? ' Vill ni ändra hur sidan ser ut? Välj Åtgärder → Anpassa den här sidan.' : '')
      : p.mode === 'draft' ? 'Det finns ett utkast till en anpassad version. Besökarna ser fortfarande standardsidan.'
      : 'Sidan är anpassad: det är er version som besökarna ser.' + (p.pendingChanges ? ' Det finns ändringar som inte är publicerade.' : '');
    const actions = p.mode === 'asBefore' ? [p.canConvert ? ['Anpassa den här sidan', () => convertFlow(p)] : null]
      : p.mode === 'draft' ? [['Fortsätt ändra utkastet', () => go('#redigera=' + p.pageId)], '-', ['Avbryt omvandlingen', () => cancelConversionFlow(p.key, p.pageId, p.revision, false)]]   // ingen undersida förrän omvandlingen är publicerad (menyn visar den vanliga sidan)
      : [['Ändra sidan', () => go('#redigera=' + p.pageId)],
         p.key === 'about' && p.pageId ? ['Ny undersida', () => go('#ny=' + p.pageId), (st.pages || []).length >= st.maxPages ? { disabled: true } : null] : null, '-',
         p.key === 'about' && (st.pages || []).some(x => x.parentId === p.pageId)
           ? ['Gå tillbaka till standardsidan (flytta undersidorna först)', null, { disabled: true }]
           : ['Gå tillbaka till standardsidan', () => revertFlow(p)]];
    const panelsCard = p.mode === 'asBefore' && p.panels.length
      ? h('div', { class: 'hs-card p-3 mb-3' }, h('h2', { class: 'h5 mb-2' }, 'Det här visas på sidan'),
          p.panels.map(x => h('div', { class: 'hs-panelrow' }, h('div', {}, h('strong', {}, x.name), h('div', { class: 'text-muted small' }, x.description)),
            h('span', { class: 'text-muted small' }, 'Hämtas automatiskt'))))
      : null;
    // En omgjord sida: vad som finns på den (paneler och egna block) med nivå, och en knapp till redigeraren. Läses ur den sparade sidan.
    let ownCard = null;
    if (p.mode !== 'asBefore' && p.pageId) {
      const list = h('div', { 'data-own-list': '1' }, h('p', { class: 'text-muted mb-0' }, 'Laddar …'));
      ownCard = h('div', { class: 'hs-card p-3 mb-3', id: 'hsOwnCard' }, h('h2', { class: 'h5 mb-2' }, 'Det här visas på sidan'), list,
        h('div', { class: 'mt-3' }, h('button', { type: 'button', class: 'btn btn-primary', id: 'hsOwnEdit', onclick: () => go('#redigera=' + p.pageId) }, p.mode === 'draft' ? 'Fortsätt ändra utkastet' : 'Ändra sidan')));
      api('GET', 'Page', { pageId: p.pageId }).then(r => {
        if (!r.success) { list.replaceChildren(h('p', { class: 'text-muted mb-0' }, 'Det gick inte att läsa sidan.')); return; }
        let blocks = []; try { blocks = JSON.parse(r.blocksJson).blocks || []; } catch { /* visas som tom lista */ }
        const names = new Map((r.panels || []).map(c => [c.key, c.name]));
        if (!blocks.length) { list.replaceChildren(h('p', { class: 'text-muted mb-0' }, 'Sidan har inget innehåll än.')); return; }
        list.replaceChildren(...blocks.map(b => {
          const isPanel = b.type === 'panel', label = isPanel ? (names.get(b.data && b.data.key) || (b.data && b.data.key) || 'Panel') : typeInfo(b.type).label;
          return h('div', { class: 'hs-panelrow', 'data-own-block': b.type }, h('div', {}, h('strong', {}, label), isPanel ? h('div', { class: 'text-muted small' }, 'Hämtas automatiskt') : null),
            h('span', { class: 'text-muted small' }, 'Syns för: ' + visLabel(b.visibility)));
        }));
      });
    }
    view(shell('sys:' + key,
      pageHead(p.label, [modePill(p), ' ', intro], actions),
      h('div', { id: 'hsBanner' }),
      ownCard, panelsCard,p.mode === 'asBefore' ? whoSeesCard(() => ({ templateKey: p.key }), p.canConvert
        ? { change: { intro: 'Vill du bestämma vem som ser delarna av sidan?', label: 'Anpassa den här sidan', run: () => convertFlow(p) } }
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
    const ok = await confirmDialog('Anpassa den här sidan?', 'Vi gör en kopia av hur "' + p.label + '" ser ut i dag. I kopian kan ni flytta och ta bort innehåll och lägga till eget. Besökarna ser inte kopian förrän ni publicerar den. Tills dess ser de standardsidan, och ni kan ångra när ni vill.', 'Anpassa sidan', false);
    if (!ok) return;
    const r = await api('POST', 'ConvertPage', { templateKey: p.key });
    if (!r.success) { S.banner = { cls: 'danger', text: r.message || 'Sidan kunde inte göras om.' }; renderSystemPage(p.key); return; }
    const st = await api('GET', 'State'); if (st.success) S.st = st;
    go('#redigera=' + r.pageId);
  }
  async function cancelConversionFlow(key, pageId, revision, fromEditor) {
    const ok = await confirmDialog('Avbryta?', 'Utkastet kastas. Besökarna ser fortfarande standardsidan, och den ändras inte.', 'Kasta utkastet', true);
    if (!ok) return;
    if (fromEditor) { clearTimeout(S.timer); S.dirty = false; }
    const r = await api('POST', 'CancelConversion', { pageId, expectedRevision: revision });
    if (!r.success) { setBanner('danger', r.message || 'Det gick inte att avbryta.'); return; }
    afterChange(key, { cls: 'success', text: 'Utkastet är kastat. Besökarna ser standardsidan.' });
  }
  async function revertFlow(p) {
    const ok = await confirmDialog('Gå tillbaka till standardsidan?', 'Besökarna ser standardsidan direkt. Det ni har ändrat på "' + p.label + '" försvinner från sajten. Ni kan anpassa sidan igen, men då börjar ni från standardsidan.', 'Gå tillbaka till standardsidan', true);
    if (!ok) return;
    const r = await api('POST', 'RevertConversion', { pageId: p.pageId, expectedRevision: p.revision });
    if (!r.success) { S.banner = { cls: 'danger', text: r.message || 'Det gick inte att gå tillbaka.' }; renderSystemPage(p.key); return; }
    afterChange(p.key, { cls: 'success', text: 'Sidan är standardsidan igen. Besökarna ser den.' });
  }

  // Egen text före/efter en vanlig sida: en mallkopia per sida. Sidan är exakt som förut utan den.
  function slotCard(x) {
    if (!x) return null;
    return h('div', { class: 'hs-card p-3 mb-3' }, h('h2', { class: 'h5 mb-1' }, 'Egen text på sidan'),
      h('p', { class: 'text-muted mb-2' }, 'Skriv egen text före eller efter innehållet. Sidan är oförändrad tills ni lägger till något.'),
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
    // Meddelandet följer med in i redigeraren (en kort stund, eftersom adressbytet laddar om den en gång till).
    S.nextBanner = { until: Date.now() + 6000, banner: { cls: 'info', text: 'Texten är ett utkast och syns för ' + visLabel(vis).toLowerCase() + ' när du publicerar. Ändra det under "Vem ser texten?" om du vill.' } };
    await openEditor(r.pageId);
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

  // ── Länkar i menyn: externa länkar längst ned i besökarens meny (som Svenska Lags "externa länkar") ──────────────
  /** En dialog som STANNAR öppen när sparandet ger ett fel (modal() stänger direkt). onSave returnerar ett felmeddelande eller null när det gick bra. */
  function formDialog(title, bodyNodes, saveLabel, onSave) {
    const err = h('div', { class: 'mt-2', role: 'alert' });
    const save = h('button', { type: 'button', class: 'btn btn-primary' }, saveLabel);
    const el = h('div', { class: 'modal fade', tabindex: '-1' },
      h('div', { class: 'modal-dialog modal-dialog-centered' }, h('div', { class: 'modal-content' },
        h('div', { class: 'modal-header' }, h('h2', { class: 'modal-title h5' }, title), h('button', { type: 'button', class: 'btn-close', 'data-bs-dismiss': 'modal', 'aria-label': 'Stäng' })),
        h('div', { class: 'modal-body' }, bodyNodes, err),
        h('div', { class: 'modal-footer' }, h('button', { type: 'button', class: 'btn btn-secondary', 'data-bs-dismiss': 'modal' }, 'Avbryt'), save))));
    document.body.append(el);
    const bs = new bootstrap.Modal(el);
    el.addEventListener('hidden.bs.modal', () => el.remove());
    save.addEventListener('click', async () => {
      save.disabled = true; err.replaceChildren();
      const msg = await onSave();
      if (msg) { err.replaceChildren(alertBox('danger', msg)); save.disabled = false; return; }
      bs.hide();
    });
    bs.show();
    setTimeout(() => { const f = el.querySelector('input,select,textarea'); if (f) f.focus(); }, 300);
  }

  function linkDialog(link) {
    const st = S.st;
    const label = h('input', { class: 'form-control', maxlength: '80', value: link ? link.label : '', id: 'hsLinkLabel', placeholder: 'Till exempel: Skjuthandboken' });
    const url = h('input', { class: 'form-control', maxlength: '500', value: link ? link.url : '', id: 'hsLinkUrl', placeholder: 'https://', inputmode: 'url', autocapitalize: 'off', spellcheck: 'false' });
    const vis = h('select', { class: 'form-select', id: 'hsLinkVis' }, visOptions().map(o => h('option', { value: o.v, selected: o.v === (link ? link.visibility : 0) ? true : null }, o.t + ' – ' + o.s.toLowerCase())));
    formDialog(link ? 'Ändra länken' : 'Ny länk i menyn', [
      h('label', { class: 'form-label fw-semibold', for: 'hsLinkLabel' }, 'Namn i menyn'), label,
      h('label', { class: 'form-label fw-semibold mt-3', for: 'hsLinkUrl' }, 'Adress'), url,
      h('div', { class: 'form-text' }, 'Adressen måste börja med https://. Länken öppnas i en ny flik.'),
      h('label', { class: 'form-label fw-semibold mt-3', for: 'hsLinkVis' }, 'Vem ser länken?'), vis], 'Spara', async () => {
      const r = await api('POST', 'SaveMenuLink', { linkId: link ? link.id : undefined, label: label.value, url: url.value, visibility: vis.value });
      if (!r.success) return r.message || 'Det gick inte att spara länken.';
      const s = await api('GET', 'State'); if (s.success) S.st = s;
      renderLinks();
      return null;
    });
  }

  function renderLinks() {
    setHash('#lankar');
    const st = S.st, links = st.menuLinks || [], full = links.length >= (st.maxLinks || 15);
    const move = async (l, d) => {
      const r = await api('POST', 'MoveMenuLink', { linkId: l.id, direction: d });
      if (!r.success) S.banner = { cls: 'danger', text: r.message || 'Det gick inte att flytta länken.' };
      const s = await api('GET', 'State'); if (s.success) S.st = s;
      renderLinks();
    };
    const rows = links.map((l, i) => h('tr', {},
      h('td', { class: 'ps-3 fw-semibold' }, l.label),
      h('td', { class: 'text-break' }, h('a', { href: l.url, target: '_blank', rel: 'noopener noreferrer', class: 'text-decoration-none small' }, l.url)),
      h('td', {}, visBadge(l.visibility)),
      h('td', { class: 'text-end pe-3' }, rowMenu([
        ['Ändra', () => linkDialog(l)],
        ['Öppna länken', null, { href: l.url }],
        '-',
        ['Flytta upp', () => move(l, -1), i === 0 ? { disabled: true } : null],
        ['Flytta ner', () => move(l, 1), i === links.length - 1 ? { disabled: true } : null],
        '-',
        ['Ta bort', async () => {
          if (!await confirmDialog('Ta bort länken?', 'Länken "' + l.label + '" tas bort från menyn.', 'Ta bort', true)) return;
          const r = await api('POST', 'RemoveMenuLink', { linkId: l.id });
          S.banner = r.success ? { cls: 'success', text: 'Länken är borttagen.' } : { cls: 'danger', text: r.message || 'Det gick inte att ta bort länken.' };
          const s = await api('GET', 'State'); if (s.success) S.st = s;
          renderLinks();
        }, { danger: true }]]))));
    view(shell('lankar',
      pageHead('Länkar i menyn', 'Länkar till andra webbplatser, till exempel ' + (IS_REGION ? 'förbundet och andra kretsar' : 'kretsens hemsida, Facebook eller en webbshop') + '. De visas längst ned i ' + (IS_REGION ? 'kretsens' : 'klubbens') + ' meny.', [
        ['Ny länk', () => linkDialog(null), full ? { disabled: true } : null]]),
      h('div', { id: 'hsBanner' }),
      links.length
        ? h('div', { class: 'hs-card p-0 overflow-hidden mb-3' }, h('table', { class: 'table align-middle hs-list mb-0' },
            h('thead', {}, h('tr', {}, h('th', { class: 'ps-3' }, 'Namn i menyn'), h('th', {}, 'Adress'), h('th', {}, 'Vem ser den'), h('th', { class: 'text-end pe-3' }, 'Åtgärder'))),
            h('tbody', {}, rows)))
        : h('div', { class: 'hs-card p-3 mb-3 text-muted' }, 'Ni har inga länkar i menyn än. Välj Åtgärder → Ny länk.'),
      h('p', { class: 'text-muted small' }, links.length + ' av högst ' + (st.maxLinks || 15) + ' länkar. Menyn visas bara för dem som får se den, och varje länk har sin egen nivå.')));
    renderBanner();
  }

  // ── Utseende: grunddesign för sidhuvudet och grundfärg. Ren presentation; valen och färg-CSS:en kommer från servern (vitlistan i SiteAppearance). ──
  async function renderAppearance() {
    setHash('#utseende');
    view(shell('utseende', pageHead('Utseende', 'Välj hur ' + (IS_REGION ? 'kretsens' : 'klubbens') + ' sidhuvud ser ut och vilken färg sidorna har. Det påverkar bara utseendet, aldrig vem som ser vad.'),
      h('div', { id: 'hsBanner' }), h('div', { class: 'hs-card p-3 text-muted' }, 'Hämtar utseendet…')));
    const r = await api('GET', 'Appearance');
    if (!r.success) { view(shell('utseende', pageHead('Utseende'), h('div', { class: 'hs-card p-3' }, alertBox('danger', r.message || 'Utseendet kunde inte hämtas.')))); return; }
    const sel = { header: r.header, palette: r.palette };
    const saved = { header: r.header, palette: r.palette };
    const miniOf = key => {
      const logo = h('span', { class: 'm-logo' }), line = h('span', { class: 'm-line' }), banner = h('span', { class: 'm-banner' });
      return h('span', { class: 'hs-ap-mini m-' + key, 'aria-hidden': 'true' }, key === 'standard' ? [banner, logo, line] : key === 'banner-utan-logo' ? [banner, h('span', { class: 'm-row' }, logo, line)] : key === 'banner-hoger' ? [banner, h('span', { class: 'm-row' }, line, logo)] : /-hoger$/.test(key) ? [h('span', { class: 'm-row' }, line, logo)] : [h('span', { class: 'm-row' }, logo, line)]);
    };
    const save = h('button', { type: 'button', class: 'btn btn-primary', disabled: true }, 'Spara utseendet');
    const msg = h('div', { id: 'hsApMsg', role: 'status' });
    const dirty = () => sel.header !== saved.header || sel.palette !== saved.palette;
    const refresh = () => {
      save.disabled = !dirty();
      body.querySelectorAll('[data-ap-header]').forEach(l => l.classList.toggle('on', l.dataset.apHeader === sel.header));
      body.querySelectorAll('[data-ap-palette]').forEach(l => l.classList.toggle('on', l.dataset.apPalette === sel.palette));
      // Färgen förhandsvisas live på hela den här sidan (samma CSS som de publika sidorna får). Städas bort av nästa vy.
      let st = document.getElementById('hsApPreview');
      const pal = (r.palettes || []).find(p => p.key === sel.palette);
      if (!st) { st = document.createElement('style'); st.id = 'hsApPreview'; document.head.appendChild(st); }
      st.textContent = pal && pal.css ? pal.css : '';
      if (pal && pal.css) { if (!document.body.classList.contains('owner-themed')) { document.body.classList.add('owner-themed'); S.apBodyAdded = true; } }
      else if (S.apBodyAdded) { document.body.classList.remove('owner-themed'); S.apBodyAdded = false; }
    };
    const headerCards = (r.headers || []).map(o => {
      const id = 'hsApH_' + o.key;
      return h('label', { class: 'hs-ap-card', for: id, 'data-ap-header': o.key },
        h('input', { type: 'radio', name: 'hsApHeader', id, value: o.key, checked: o.key === sel.header ? true : null, class: 'visually-hidden', onchange: () => { sel.header = o.key; refresh(); } }),
        miniOf(o.key), h('span', { class: 'fw-semibold d-block mt-2' }, o.label), h('span', { class: 'small text-muted d-block' }, o.help));
    });
    const swatches = (r.palettes || []).map(p => {
      const id = 'hsApP_' + p.key;
      return h('label', { class: 'hs-ap-sw', for: id, 'data-ap-palette': p.key },
        h('input', { type: 'radio', name: 'hsApPalette', id, value: p.key, checked: p.key === sel.palette ? true : null, class: 'visually-hidden', onchange: () => { sel.palette = p.key; refresh(); } }),
        h('span', { class: 'sw', style: 'background:' + p.hex }), h('span', { class: 'small' }, p.label));
    });
    save.addEventListener('click', async () => {
      save.disabled = true; msg.replaceChildren();
      const res = await api('POST', 'SaveAppearance', { header: sel.header, palette: sel.palette });
      if (!res.success) { msg.replaceChildren(alertBox('danger', res.message || 'Utseendet kunde inte sparas.')); save.disabled = !dirty(); return; }
      saved.header = res.header; saved.palette = res.palette;
      msg.replaceChildren(alertBox('success', 'Utseendet är sparat.', [' ', h('a', { href: OWNER.url, target: '_blank', rel: 'noopener' }, 'Se ' + (IS_REGION ? 'kretsens' : 'klubbens') + ' sida')]));
      refresh();
    });
    view(shell('utseende', pageHead('Utseende', 'Välj hur ' + (IS_REGION ? 'kretsens' : 'klubbens') + ' sidhuvud ser ut och vilken färg sidorna har. Det påverkar bara utseendet, aldrig vem som ser vad.'),
      h('div', { id: 'hsBanner' }),
      h('div', { class: 'hs-card p-3 mb-3' }, h('h2', { class: 'h5' }, 'Grunddesign för sidhuvudet'),
        h('p', { class: 'text-muted small' }, 'Standard är hur sidan ser ut idag. De andra visar ' + (IS_REGION ? 'kretsens' : 'klubbens') + ' namn och logo utan bannerbild eller med logon under bilden.'),
        h('div', { class: 'hs-ap-grid' }, headerCards)),
      h('div', { class: 'hs-card p-3 mb-3' }, h('h2', { class: 'h5' }, 'Grundfärg'),
        h('p', { class: 'text-muted small' }, 'Färgen används för knappar, länkar och markeringar på ' + (IS_REGION ? 'kretsens' : 'klubbens') + ' sidor. Alla färger är provade så att texten är läsbar, också i mörkt läge. Förhandsvisningen visas direkt här på sidan.'),
        h('div', { class: 'hs-ap-swatches' }, swatches),
        h('div', { class: 'd-flex flex-wrap gap-2 align-items-center mt-3' },
          h('button', { type: 'button', class: 'btn btn-primary btn-sm' }, 'Knapp'), h('button', { type: 'button', class: 'btn btn-outline-primary btn-sm' }, 'Knapp med ram'),
          h('a', { href: '#utseende', onclick: e => e.preventDefault() }, 'En länk'), h('span', { class: 'badge text-bg-primary' }, 'Märke'))),
      h('div', { class: 'd-flex flex-wrap gap-3 align-items-center' }, save, msg)));
    refresh();
  }

  // ── Anmälningar: det som kommit in via formulär (blocket Anmälan/ansökan). Personuppgifter: bara föreningens egen administratör. ──
  const SG_STATUS = [{ v: 0, k: 'new', t: 'Ny', cls: 'bg-primary' }, { v: 1, k: 'queued', t: 'Köande', cls: 'bg-warning text-dark' }, { v: 3, k: 'approved', t: 'Godkänd', cls: 'bg-success' }, { v: 2, k: 'denied', t: 'Nekad', cls: 'bg-secondary' }];
  const sgBadge = v => { const s = SG_STATUS.find(x => x.v === v) || SG_STATUS[0]; return h('span', { class: 'badge ' + s.cls }, s.t); };
  const sgDate = iso => { if (!iso) return ''; const d = new Date(iso); return d.toLocaleDateString('sv-SE') + ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes()); };
  S.sg = { status: 'new', form: '', q: '', archived: false };

  function signupParams() {
    const sg = S.sg, [pid, bid] = (sg.form || '').split(':');
    return { status: sg.archived ? undefined : (sg.status || undefined), pageId: pid || undefined, blockId: bid || undefined, q: sg.q || undefined, archived: sg.archived ? 'true' : undefined };
  }

  async function renderSignups(banner) {
    setHash('#anmalningar');
    if (S.st.support) return renderList();   // personuppgifter: aldrig i stödläge
    view(shell('anmalningar', pageHead('Anmälningar', 'Laddar …'), h('p', { class: 'text-muted' }, 'Laddar …')));
    if (S.st.signupReady === false) { view(shell('anmalningar', pageHead('Anmälningar'), alertBox('warning', 'Anmälningar är inte tillgängliga just nu. Kontakta pistol.nu om det fortsätter.'))); return; }
    const sg = S.sg;
    const r = await api('GET', 'SignupEntries', { ...signupParams(), skip: 0 });
    if (!r.success) { view(shell('anmalningar', pageHead('Anmälningar'), alertBox('danger', r.message || 'Anmälningarna kan inte läsas.'))); return; }
    S.st.signupNew = r.counts.new;
    let items = r.items;
    const total = () => r.total;
    const act = async (action, params, okText) => {
      const res = await api('POST', action, params);
      await renderSignups(res.success ? { cls: 'success', text: okText } : { cls: 'danger', text: res.message || 'Det gick inte.' });
    };
    const exportHref = '/umbraco/surface/SiteEdit/ExportSignups?' + new URLSearchParams({ ownerType: OWNER.type, ownerId: OWNER.id, ...Object.fromEntries(Object.entries(signupParams()).filter(([, v]) => v !== undefined)) }).toString();
    const counts = r.counts, all = counts.new + counts.queued + counts.denied + counts.approved;
    const tabs = [['', 'Alla', all], ['new', 'Nya', counts.new], ['queued', 'Köande', counts.queued], ['approved', 'Godkända', counts.approved], ['denied', 'Nekade', counts.denied]];
    const search = h('input', { type: 'search', class: 'form-control', placeholder: 'Sök namn eller e-post', 'aria-label': 'Sök anmälningar', value: sg.q || '' });
    search.addEventListener('keydown', e => { if (e.key === 'Enter') { sg.q = search.value.trim(); renderSignups(); } });
    const formSel = r.forms.length > 1 ? h('select', { class: 'form-select', 'aria-label': 'Visa ett formulär' },
      h('option', { value: '' }, 'Alla formulär'), r.forms.map(f => h('option', { value: f.pageId + ':' + f.blockId, selected: sg.form === f.pageId + ':' + f.blockId ? true : null }, f.title + ' (' + f.total + ')'))) : null;
    if (formSel) formSel.addEventListener('change', () => { sg.form = formSel.value; renderSignups(); });

    const rowFor = it => h('tr', { 'data-entry': it.id },
      h('td', { class: 'ps-3 text-nowrap small' }, sgDate(it.created)),
      h('td', {}, h('a', { href: '#', class: 'fw-semibold text-decoration-none', onclick: e => { e.preventDefault(); showEntry(it.id); } }, it.name),
        h('div', { class: 'text-muted small text-break' }, it.email + (it.phone ? ' · ' + it.phone : '')), it.hasNote ? h('span', { class: 'badge text-bg-light border ms-0 mt-1' }, 'Anteckning') : null),
      h('td', {}, it.choice || it.formTitle, it.choice ? h('div', { class: 'text-muted small' }, it.formTitle) : null),
      h('td', {}, sgBadge(it.status), it.handled ? h('div', { class: 'text-muted small' }, new Date(it.handled).toLocaleDateString('sv-SE')) : null),
      h('td', { class: 'text-end pe-3' }, rowMenu([
        ['Visa', () => showEntry(it.id)],
        '-',
        it.status !== 3 ? ['Godkänn', () => act('SetSignupStatus', { id: it.id, status: 3 }, 'Anmälan är godkänd.')] : null,
        it.status !== 1 ? ['Sätt som köande', () => act('SetSignupStatus', { id: it.id, status: 1 }, 'Anmälan står nu i kö.')] : null,
        it.status !== 2 ? ['Neka', () => act('SetSignupStatus', { id: it.id, status: 2 }, 'Anmälan är nekad.')] : null,
        it.status !== 0 ? ['Sätt som ny', () => act('SetSignupStatus', { id: it.id, status: 0 }, 'Anmälan är satt som ny.')] : null,
        '-',
        ['Skriv till sökanden', () => { window.location.href = 'mailto:' + it.email + '?subject=' + encodeURIComponent('Din anmälan till ' + OWNER.name); }],
        '-',
        sg.archived ? ['Ta fram ur arkivet', () => act('ArchiveSignup', { id: it.id, archive: 'false' }, 'Anmälan är tillbaka bland de aktuella.')] : ['Arkivera', () => act('ArchiveSignup', { id: it.id, archive: 'true' }, 'Anmälan är arkiverad.')],
        ['Ta bort', async () => {
          if (!await confirmDialog('Ta bort anmälan?', 'Anmälan från ' + it.name + ' och alla uppgifter i den tas bort för gott. Det går inte att ångra.', 'Ta bort', true)) return;
          await act('DeleteSignup', { id: it.id }, 'Anmälan är borttagen.');
        }, { danger: true }]])));

    const moreBtn = h('button', { type: 'button', class: 'btn btn-outline-secondary', onclick: async () => {
      moreBtn.disabled = true;
      const m = await api('GET', 'SignupEntries', { ...signupParams(), skip: items.length });
      if (m.success) { items = items.concat(m.items); tbody.append(...m.items.map(rowFor)); moreWrap.hidden = items.length >= total(); count.textContent = items.length + ' av ' + total(); }
      moreBtn.disabled = false;
    } }, 'Visa fler');
    const moreWrap = h('div', { class: 'text-center mb-3', hidden: items.length >= total() }, moreBtn);
    const count = h('span', { class: 'text-muted small ms-auto' }, items.length + ' av ' + total());
    const tbody = h('tbody', {}, items.map(rowFor));

    view(shell('anmalningar',
      pageHead('Anmälningar', 'Anmälningar och ansökningar som kommit in via formulär på ' + (IS_REGION ? 'kretsens' : 'klubbens') + ' sidor. Ni godkänner, köar eller nekar dem här. Uppgifterna är personuppgifter: dela dem inte vidare i onödan.', [
        ['Exportera till Excel (CSV)', null, { href: exportHref }],
        [sg.archived ? 'Visa aktuella anmälningar' : 'Visa arkiverade (' + counts.archived + ')', () => { sg.archived = !sg.archived; sg.status = sg.archived ? '' : 'new'; renderSignups(); }]]),
      h('div', { id: 'hsBanner' }),
      sg.archived ? h('p', { class: 'fw-semibold' }, 'Arkiverade anmälningar (raderas automatiskt 90 dagar efter att de arkiverats)') : h('div', { class: 'd-flex flex-wrap gap-2 mb-3', role: 'group', 'aria-label': 'Filtrera på status' },
        tabs.map(([k, t, n]) => h('button', { type: 'button', class: 'btn btn-sm ' + ((sg.status || '') === k ? 'btn-primary' : 'btn-outline-secondary'), 'aria-pressed': (sg.status || '') === k ? 'true' : 'false',
          onclick: () => { sg.status = k; renderSignups(); } }, t + ' (' + n + ')'))),
      h('div', { class: 'd-flex flex-wrap gap-2 mb-3' }, h('div', { style: 'min-width:14rem;flex:1 1 14rem' }, search), formSel ? h('div', { style: 'min-width:12rem' }, formSel) : null, count),
      items.length ? h('div', { class: 'hs-card p-0 overflow-hidden mb-3' }, h('div', { class: 'table-responsive' }, h('table', { class: 'table align-middle hs-list mb-0' },
        h('thead', {}, h('tr', {}, h('th', { class: 'ps-3' }, 'Inkom'), h('th', {}, 'Namn'), h('th', {}, 'Gäller'), h('th', {}, 'Status'), h('th', { class: 'text-end pe-3' }, 'Åtgärder'))), tbody)))
        : h('div', { class: 'hs-card p-3 mb-3 text-muted' }, sg.q || sg.form || (!sg.archived && sg.status) ? 'Inga anmälningar matchar urvalet.' : 'Inga anmälningar än. Lägg blocket "Anmälan / ansökan" på en sida så kommer anmälningarna hit.'),
      moreWrap,
      h('p', { class: 'text-muted small' }, 'Personnummer tas bort automatiskt 30 dagar efter att ni avgjort en anmälan. Avgjorda anmälningar raderas efter 12 månader, andra efter 24 månader.')));
    if (banner) { S.banner = banner; renderBanner(); S.banner = null; }
  }

  async function showEntry(id) {
    const r = await api('GET', 'SignupEntry', { id });
    if (!r.success) { S.banner = { cls: 'danger', text: r.message || 'Anmälan kan inte läsas.' }; renderBanner(); return; }
    const e = r.entry;
    const row = (label, value, mono) => value ? [h('dt', { class: 'col-sm-4' }, label), h('dd', { class: 'col-sm-8' + (mono ? ' font-monospace' : ''), style: 'white-space:pre-wrap' }, value)] : [];
    const note = h('textarea', { class: 'form-control', rows: '3', maxlength: '500', 'aria-label': 'Anteckning (syns bara för administratörer)' }, e.adminNote || '');
    const statusBtns = SG_STATUS.map(s => h('button', { type: 'button', class: 'btn btn-sm ' + (e.status === s.v ? 'btn-primary' : 'btn-outline-secondary'), onclick: async () => {
      const res = await api('POST', 'SetSignupStatus', { id: e.id, status: s.v });
      if (res.success) { e.status = s.v; statusBtns.forEach((b, i) => { b.className = 'btn btn-sm ' + (SG_STATUS[i].v === s.v ? 'btn-primary' : 'btn-outline-secondary'); }); renderSignups(); }   // listan bakom dialogen följer med
    } }, s.t));
    formDialog('Anmälan från ' + e.name, [
      h('dl', { class: 'row mb-3' },
        row('Inkom', sgDate(e.created)), row('Formulär', e.formTitle), row('Gäller', e.choice), row('Namn', e.name),
        [h('dt', { class: 'col-sm-4' }, 'E-post'), h('dd', { class: 'col-sm-8 text-break' }, h('a', { href: 'mailto:' + e.email }, e.email))],
        row('Telefon', e.phone), row('Kön', e.gender), row('Personnummer', e.personalNumber, true),
        row('Adress', [e.address, [e.postalCode, e.city].filter(Boolean).join(' ')].filter(Boolean).join('\n')), row('Meddelande', e.message),
        e.handled ? row('Avgjord', sgDate(e.handled)) : []),
      e.personalNumber ? h('p', { class: 'small text-muted' }, 'Personnumret tas bort automatiskt 30 dagar efter att anmälan avgjorts. Skriv in det i medlemsregistret före dess.') : null,
      h('p', { class: 'form-label small fw-semibold mb-1' }, 'Status'), h('div', { class: 'd-flex flex-wrap gap-1 mb-3' }, statusBtns),
      h('label', { class: 'form-label small fw-semibold mb-1' }, 'Anteckning (syns bara för administratörer)'), note], 'Spara anteckning', async () => {
      if ((note.value || '').trim() !== (e.adminNote || '')) {
        const res = await api('POST', 'SaveSignupNote', { id: e.id, note: note.value });
        if (!res.success) return res.message || 'Det gick inte att spara anteckningen.';
      }
      renderSignups();
      return null;
    });
  }

  // ── Bilder: föreningens bildbibliotek. Här laddar ni upp, sorterar in i album, söker, ser vad som används och tar bort. Bilderna väljs sedan i bildblock, galleri och text. ──
  // Vyvalet (kort eller lista) är en bekvämlighet per webbläsare; utan lagring blir det kort.
  const libView = () => { try { return localStorage.getItem('hsLibView') === 'list' ? 'list' : 'cards'; } catch { return 'cards'; } };
  const setLibView = v => { try { localStorage.setItem('hsLibView', v); } catch { /* ingen lagring: valet gäller bara den här visningen */ } };
  // Bibliotekets val (album, sökning, sortering, markerade) lever så länge sidan är öppen.
  const libState = () => (S.lib = S.lib || { album: 'all', q: '', sort: 'new', sel: new Set() });
  const menuBtn = (label, items, cls, attrs) => h('div', { class: 'dropdown' },
    h('button', Object.assign({ class: 'btn btn-sm dropdown-toggle ' + (cls || 'btn-outline-primary'), type: 'button', 'data-bs-toggle': 'dropdown', 'data-bs-popper-config': '{"strategy":"fixed"}', 'aria-expanded': 'false' }, attrs || {}), label),
    menuList(items));
  const sortByName = (a, b) => a.name.localeCompare(b.name, 'sv');
  const swCount = (n, one, many) => n + ' ' + (n === 1 ? one : many);

  async function renderLibrary(banner) {   // banner = { cls, text }: ett besked som ska stå kvar i den nya vyn
    setHash('#bilder');
    view(shell('bilder', pageHead('Bilder', 'Laddar …'), h('p', { class: 'text-muted' }, 'Laddar …')));
    await loadFiles();
    const f = S.files, files = f.files || [], albums = f.albums || [], L = libState();
    const albumsOn = f.albumsReady !== false;
    const byId = new Map(albums.map(a => [a.id, a]));
    const byFile = new Map(files.map(x => [x.id, x]));
    const memberOf = new Map();
    for (const a of albums) for (const id of a.fileIds) { if (!memberOf.has(id)) memberOf.set(id, []); memberOf.get(id).push(a.id); }
    if (typeof L.album === 'number' && !byId.has(L.album)) L.album = 'all';
    if (L.sort === 'manual' && typeof L.album !== 'number') L.sort = 'new';
    for (const id of [...L.sel]) if (!byFile.has(id)) L.sel.delete(id);
    const albumNames = x => (memberOf.get(x.id) || []).map(i => byId.get(i).name);
    const msg = h('div', { id: 'hsBanner' });
    const show = (cls, text) => msg.replaceChildren(text ? alertBox(cls, text) : '');
    const again = async (cls, text) => { await renderLibrary(cls ? { cls, text } : undefined); };

    // ── Uppladdning ──
    const fileInp = h('input', { type: 'file', accept: 'image/jpeg,image/png,image/gif,image/webp', multiple: true, class: 'd-none', 'aria-label': 'Välj bilder att ladda upp' });
    fileInp.addEventListener('change', async () => {
      const list = [...fileInp.files]; fileInp.value = ''; if (!list.length) return;
      let okCount = 0, lastErr = null; const added = [];
      for (let i = 0; i < list.length; i++) {
        show('info', list.length > 1 ? 'Laddar upp ' + (i + 1) + ' av ' + list.length + ' …' : 'Laddar upp …');
        const r = await uploadImage(list[i]);
        if (!r.success) { lastErr = list[i].name + ': ' + (r.message || 'Det gick inte att ladda upp bilden.'); if (r.code === 'quota' || r.code === 'rate') break; continue; }
        okCount++; added.push(r.id);
      }
      // Är ett album öppet hamnar de nya bilderna direkt i det.
      let note = '';
      if (typeof L.album === 'number' && added.length) {
        const r = await api('POST', 'AlbumAdd', { albumId: L.album, fileIds: added.join(',') });
        note = r.success ? ' De ligger i albumet.' : ' De kunde inte läggas i albumet: ' + (r.message || '');
      }
      await again(lastErr ? 'danger' : 'success', lastErr ? (okCount ? okCount + ' uppladdade. ' : '') + lastErr : (okCount === 1 ? 'Bilden är uppladdad.' : okCount + ' bilder är uppladdade.') + note);
    });
    const pct = f.maxBytes ? Math.min(100, Math.round((f.bytes || 0) * 100 / f.maxBytes)) : 0;

    // ── Album: skapa, byta namn, ta bort, lägga i och ta ur ──
    const askName = (title, initial, saveLabel, run) => {
      const inp = h('input', { class: 'form-control', maxlength: '80', value: initial || '', placeholder: 'Till exempel: Klubbmästerskap 2025', 'data-album-name': '1' });
      formDialog(title, [field('Albumets namn', inp)], saveLabel, async () => {
        const name = inp.value.trim();
        if (!name) return 'Skriv ett namn på albumet.';
        const r = await run(name);
        if (r) return r;
        return null;
      });
    };
    const newAlbum = ids => askName('Nytt album', '', 'Skapa albumet', async name => {
      const r = await api('POST', 'SaveAlbum', { name });
      if (!r.success) return r.message || 'Det gick inte att skapa albumet.';
      let text = 'Albumet "' + name + '" är skapat.';
      if (ids && ids.length) {
        const a = await api('POST', 'AlbumAdd', { albumId: r.id, fileIds: ids.join(',') });
        text = a.success ? 'Albumet "' + name + '" är skapat med ' + swCount(a.count, 'bild', 'bilder') + '.' : text + ' Bilderna kunde inte läggas i det: ' + (a.message || '');
        L.sel.clear();
      }
      L.album = r.id; L.sort = 'manual'; L.q = '';
      await again('success', text);
      return null;
    });
    const renameAlbum = a => askName('Byt namn på albumet', a.name, 'Spara', async name => {
      const r = await api('POST', 'SaveAlbum', { albumId: a.id, name });
      if (!r.success) return r.message || 'Det gick inte att byta namn.';
      await again('success', 'Albumet heter nu "' + name + '".');
      return null;
    });
    const deleteAlbum = async a => {
      if (!await confirmDialog('Ta bort albumet?', 'Albumet "' + a.name + '" tas bort. Bilderna finns kvar i biblioteket och på de sidor där de används.', 'Ta bort albumet', true)) return;
      const r = await api('POST', 'DeleteAlbum', { albumId: a.id });
      if (r.success) L.album = 'all';
      await again(r.success ? 'success' : 'danger', r.success ? 'Albumet är borttaget.' : (r.message || 'Det gick inte att ta bort albumet.'));
    };
    const addTo = async (a, ids) => {
      const r = await api('POST', 'AlbumAdd', { albumId: a.id, fileIds: ids.join(',') });
      if (r.success) L.sel.clear();
      await again(r.success ? 'success' : 'danger', r.success ? (r.count ? swCount(r.count, 'bild lades', 'bilder lades') + ' i albumet "' + a.name + '".' : 'Bilderna låg redan i albumet "' + a.name + '".') : (r.message || 'Det gick inte att lägga bilderna i albumet.'));
    };
    const removeFrom = async (a, ids) => {
      const r = await api('POST', 'AlbumRemove', { albumId: a.id, fileIds: ids.join(',') });
      if (r.success) L.sel.clear();
      await again(r.success ? 'success' : 'danger', r.success ? swCount(r.count, 'bild togs', 'bilder togs') + ' ur albumet. De finns kvar i biblioteket.' : (r.message || 'Det gick inte att ta bilderna ur albumet.'));
    };
    const addMenu = ids => [...albums.map(a => ['Till ' + a.name, () => addTo(a, ids)]), albums.length ? '-' : null, ['Nytt album …', () => newAlbum(ids)]];

    // ── En bilds egen dialog: namn, beskrivning och vilka album den ligger i ──
    const editImage = x => {
      const name = h('input', { class: 'form-control', maxlength: '200', value: x.name });
      const desc = h('textarea', { class: 'form-control', rows: '2', maxlength: '200', placeholder: 'Till exempel: Prisutdelningen efter finalen' }, x.description || '');
      const mine = new Set(memberOf.get(x.id) || []);
      const boxes = albums.map(a => {
        const cb = h('input', { type: 'checkbox', class: 'form-check-input me-2', checked: mine.has(a.id), 'data-album-box': a.id });
        return [a, cb, h('label', { class: 'list-group-item d-flex align-items-center' }, cb, a.name)];
      });
      const fresh = h('input', { class: 'form-control', maxlength: '80', placeholder: 'Nytt album (valfritt)', 'aria-label': 'Lägg även i ett nytt album' });
      formDialog('Bilden: namn, beskrivning och album', [
        field('Namn', name), field('Beskrivning (valfri)', desc, 'Syns i listan och går att söka på. Bildtext och beskrivning för den som lyssnar skriver du där bilden används.'),
        albums.length ? h('p', { class: 'form-label small fw-semibold mb-1 mt-2' }, 'Ligger i albumen') : null,
        albums.length ? h('div', { class: 'list-group mb-2' }, boxes.map(b => b[2])) : null, field('', fresh)], 'Spara', async () => {
        const r = await api('POST', 'UpdateImage', { fileId: x.id, name: name.value, description: desc.value });
        if (!r.success) return r.message || 'Det gick inte att spara.';
        for (const [a, cb] of boxes) {
          if (cb.checked === mine.has(a.id)) continue;
          const m = await api('POST', cb.checked ? 'AlbumAdd' : 'AlbumRemove', { albumId: a.id, fileIds: String(x.id) });
          if (!m.success) return m.message || 'Det gick inte att ändra albumen.';
        }
        const nn = fresh.value.trim();
        if (nn) {
          const c = await api('POST', 'SaveAlbum', { name: nn });
          if (!c.success) return c.message || 'Det gick inte att skapa albumet.';
          const m = await api('POST', 'AlbumAdd', { albumId: c.id, fileIds: String(x.id) });
          if (!m.success) return m.message || 'Det gick inte att lägga bilden i albumet.';
        }
        await again('success', 'Ändringen är sparad.');
        return null;
      });
    };

    // ── Vad som visas just nu: album, sökning, sortering ──
    const visible = () => {
      let list = files;
      const inAlbum = typeof L.album === 'number' ? byId.get(L.album) : null;
      if (L.album === 'none') list = list.filter(x => !memberOf.has(x.id));
      else if (inAlbum) list = inAlbum.fileIds.map(id => byFile.get(id)).filter(Boolean);
      const q = L.q.trim().toLowerCase();
      if (q) list = list.filter(x => (x.name + ' ' + (x.description || '') + ' ' + albumNames(x).join(' ')).toLowerCase().includes(q));
      list = list.slice();
      if (L.sort === 'manual' && inAlbum) return list;
      if (L.sort === 'old') return list.sort((a, b) => a.id - b.id);
      if (L.sort === 'name') return list.sort(sortByName);
      return list.sort((a, b) => b.id - a.id);
    };

    const usedText = x => x.usedBy > 0 ? 'Används på ' + x.usedBy + (x.usedBy === 1 ? ' sida' : ' sidor') : 'Används inte';
    const toggleSel = (x, on) => { if (on) L.sel.add(x.id); else L.sel.delete(x.id); paint(); };
    const checkbox = x => h('input', { type: 'checkbox', class: 'form-check-input hs-imgcheck', checked: L.sel.has(x.id), 'aria-label': 'Markera ' + x.name, 'data-sel': x.id, onchange: e => toggleSel(x, e.target.checked) });
    // Samma radmeny i kort- och listvyn.
    const actions = (x, list) => {
      const inAlbum = typeof L.album === 'number' ? byId.get(L.album) : null;
      const manual = !!inAlbum && L.sort === 'manual' && !L.q.trim();
      const i = list.indexOf(x);
      const mv = to => async () => { const r = await api('POST', 'AlbumMove', { albumId: inAlbum.id, fileId: x.id, to }); await again(r.success ? '' : 'danger', r.success ? '' : (r.message || 'Det gick inte att flytta bilden.')); };
      return rowMenu([
        ['Öppna bilden', null, { href: x.url }],
        albumsOn ? ['Byt namn, beskrivning och album …', () => editImage(x)] : ['Byt namn och beskrivning …', () => editImage(x)],
        manual ? '-' : null,
        manual ? ['Flytta först', mv('first'), { disabled: i === 0 }] : null,
        manual ? ['Flytta upp', mv('up'), { disabled: i === 0 }] : null,
        manual ? ['Flytta ner', mv('down'), { disabled: i === list.length - 1 }] : null,
        manual ? ['Flytta sist', mv('last'), { disabled: i === list.length - 1 }] : null,
        inAlbum ? '-' : null,
        inAlbum ? ['Ta ur albumet', () => removeFrom(inAlbum, [x.id])] : null,
        '-',
        x.usedBy > 0 ? ['Ta bort (används på en sida)', null, { disabled: true }] : ['Ta bort', async () => {
          if (!await confirmDialog('Ta bort bilden?', 'Bilden "' + x.name + '" tas bort från biblioteket och från alla album. Det går inte att ångra.', 'Ta bort', true)) return;
          const r = await api('POST', 'DeleteFile', { fileId: x.id });
          L.sel.delete(x.id);
          await again(r.success ? 'success' : 'danger', r.success ? 'Bilden är borttagen.' : (r.message || 'Det gick inte att ta bort bilden.'));
        }, { danger: true }]]);
    };
    const tile = (x, list) => h('div', { class: 'hs-imgtile' + (L.sel.has(x.id) ? ' sel' : ''), 'data-file': x.id },
      h('div', { class: 'hs-imgwrap' }, checkbox(x),
        h('a', { href: x.url, target: '_blank', rel: 'noopener', class: 'hs-imgpick', 'aria-label': 'Öppna ' + x.name + ' i en ny flik' }, h('img', { src: x.url + '&w=640', alt: '', loading: 'lazy' }))),
      h('span', { class: 'hs-imgname', title: x.name + (x.description ? ' – ' + x.description : '') }, x.name),   // storlek, mått och användning visas i listvyn
      actions(x, list));
    // Klassisk lista: val, miniatyr, namn och beskrivning, album, mått, storlek, uppladdad, användning och Åtgärder. Mått och datum göms på telefon.
    const listRow = (x, list) => h('tr', { 'data-file': x.id, class: L.sel.has(x.id) ? 'table-active' : '' },
      h('td', { class: 'ps-3', style: 'width:2.5rem' }, checkbox(x)),
      h('td', { style: 'width:5.5rem' }, h('a', { href: x.url, target: '_blank', rel: 'noopener', 'aria-label': 'Öppna ' + x.name + ' i en ny flik' }, h('img', { class: 'hs-imgthumb', src: x.url + '&w=320', alt: '', loading: 'lazy' }))),
      h('td', { class: 'text-break' }, h('div', { class: 'fw-semibold' }, x.name), x.description ? h('div', { class: 'small text-muted' }, x.description) : null,
        h('div', { class: 'small text-muted d-md-none' }, x.width + ' × ' + x.height + ' · ' + fmtSize(x.size))),
      albumsOn ? h('td', { class: 'd-none d-md-table-cell small' }, albumNames(x).join(', ') || h('span', { class: 'text-muted' }, '–')) : null,
      h('td', { class: 'd-none d-lg-table-cell text-nowrap' }, x.width + ' × ' + x.height),
      h('td', { class: 'd-none d-lg-table-cell text-nowrap' }, fmtSize(x.size)),
      h('td', { class: 'd-none d-xl-table-cell text-nowrap' }, x.uploaded ? new Date(x.uploaded).toLocaleDateString('sv-SE') : ''),
      h('td', { class: x.usedBy > 0 ? '' : 'text-muted' }, usedText(x)),
      h('td', { class: 'text-end pe-3' }, actions(x, list)));

    // ── Verktygsrad: sök, sortera, kort eller lista ──
    const mode = libView();
    const search = h('input', { type: 'search', class: 'form-control', placeholder: 'Sök på namn, beskrivning eller album', 'aria-label': 'Sök bland bilderna', value: L.q, id: 'hsLibSearch' });
    search.addEventListener('input', () => { L.q = search.value; paint(); });
    const sortSel = h('select', { class: 'form-select w-auto', 'aria-label': 'Sortera bilderna', id: 'hsLibSort' });
    sortSel.addEventListener('change', () => { L.sort = sortSel.value; paint(); });
    const viewToggle = h('div', { class: 'btn-group', role: 'group', 'aria-label': 'Visa bilderna som' },
      h('button', { type: 'button', class: 'btn btn-sm ' + (mode === 'cards' ? 'btn-primary' : 'btn-outline-secondary'), 'aria-pressed': mode === 'cards' ? 'true' : 'false', 'data-lib-view': 'cards', onclick: () => { setLibView('cards'); renderLibrary(); } }, h('i', { class: 'bi bi-grid-3x2-gap me-1', 'aria-hidden': 'true' }), 'Kort'),
      h('button', { type: 'button', class: 'btn btn-sm ' + (mode === 'list' ? 'btn-primary' : 'btn-outline-secondary'), 'aria-pressed': mode === 'list' ? 'true' : 'false', 'data-lib-view': 'list', onclick: () => { setLibView('list'); renderLibrary(); } }, h('i', { class: 'bi bi-list-ul me-1', 'aria-hidden': 'true' }), 'Lista'));
    // Albumvalet ligger på samma rad som sökningen: två badges (Alla bilder, Utan album) och en väljare för de egna albumen.
    const navBox = h('div', { class: 'd-flex flex-wrap gap-2 align-items-center' }), mainBox = h('div');
    const toolbar = h('div', { class: 'd-flex flex-wrap gap-2 align-items-center mb-2' }, h('div', { class: 'flex-grow-1', style: 'min-width:12rem;max-width:22rem' }, search), sortSel, navBox, h('div', { class: 'ms-auto' }, viewToggle));
    const pick = v => { L.album = v; L.sel.clear(); L.sort = typeof v === 'number' ? 'manual' : (L.sort === 'manual' ? 'new' : L.sort); paint(); };
    const paint = () => {
      const inAlbum = typeof L.album === 'number' ? byId.get(L.album) : null;
      const list = visible();
      // sortering: "Egen ordning" finns bara i ett album
      sortSel.replaceChildren(...[['new', 'Nyast först'], ['old', 'Äldst först'], ['name', 'Namn A–Ö'], inAlbum ? ['manual', 'Egen ordning'] : null].filter(Boolean).map(([v, t]) => h('option', { value: v, selected: L.sort === v ? true : null }, t)));
      sortSel.value = L.sort;
      // vänster: albumlistan (dator) och en rullgardin (telefon)
      if (albumsOn) {
        const none = files.filter(x => !memberOf.has(x.id)).length;
        const badge = (key, label, n) => h('button', { type: 'button', class: 'btn btn-sm rounded-pill ' + (L.album === key ? 'btn-primary' : 'btn-outline-secondary'), 'aria-pressed': L.album === key ? 'true' : 'false', 'data-album': String(key), onclick: () => pick(key) }, label + ' ', h('span', { class: 'badge ' + (L.album === key ? 'text-bg-light' : 'text-bg-secondary') }, String(n)));
        const sel = h('select', { class: 'form-select w-auto' + (typeof L.album === 'number' ? ' border-primary' : ''), 'aria-label': 'Visa ett album', id: 'hsAlbumSel' },
          h('option', { value: '' }, albums.length ? 'Album (' + albums.length + ') …' : 'Inga album än'),
          albums.map(a => h('option', { value: String(a.id), selected: L.album === a.id ? true : null }, a.name + ' (' + a.fileIds.length + ')')));
        sel.disabled = !albums.length;
        sel.value = typeof L.album === 'number' ? String(L.album) : '';
        sel.addEventListener('change', () => { if (sel.value) pick(parseInt(sel.value, 10)); });
        navBox.replaceChildren(badge('all', 'Alla bilder', files.length), badge('none', 'Utan album', none), sel);
      } else navBox.replaceChildren();
      // höger: albumrubrik, markeringsrad och bilderna
      const ids = [...L.sel];
      const head = inAlbum ? h('div', { class: 'd-flex flex-wrap align-items-center gap-2 mb-2' }, h('h2', { class: 'h5 mb-0' }, inAlbum.name), h('span', { class: 'text-muted small' }, swCount(inAlbum.fileIds.length, 'bild', 'bilder')),
        h('div', { class: 'ms-auto d-flex gap-2' }, h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', 'data-album-rename': '1', onclick: () => renameAlbum(inAlbum) }, 'Byt namn'), h('button', { type: 'button', class: 'btn btn-sm btn-outline-danger', 'data-album-delete': '1', onclick: () => deleteAlbum(inAlbum) }, 'Ta bort albumet'))) : null;
      const bulk = ids.length ? h('div', { class: 'hs-card p-2 mb-2 d-flex flex-wrap align-items-center gap-2', role: 'region', 'aria-label': 'Markerade bilder', id: 'hsBulk' },
        h('strong', { class: 'me-1' }, swCount(ids.length, 'bild vald', 'bilder valda')),
        albumsOn ? menuBtn('Lägg i album …', addMenu(ids), 'btn-primary', { 'data-bulk': 'add' }) : null,
        inAlbum ? h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', 'data-bulk': 'remove', onclick: () => removeFrom(inAlbum, ids) }, 'Ta ur albumet') : null,
        h('button', { type: 'button', class: 'btn btn-sm btn-outline-danger', 'data-bulk': 'delete', onclick: async () => {
          const del = ids.filter(id => (byFile.get(id) || {}).usedBy === 0), keep = ids.length - del.length;
          if (!del.length) { show('warning', 'Bilderna du markerat används på sidor och kan inte tas bort.'); return; }
          if (!await confirmDialog('Ta bort ' + swCount(del.length, 'bild', 'bilder') + '?', 'De tas bort från biblioteket och från alla album. Det går inte att ångra.' + (keep ? ' ' + swCount(keep, 'markerad bild används', 'markerade bilder används') + ' på sidor och tas inte bort.' : ''), 'Ta bort', true)) return;
          let ok = 0, last = null;
          for (const id of del) { const r = await api('POST', 'DeleteFile', { fileId: id }); if (r.success) ok++; else last = r.message; }
          L.sel.clear();
          await again(last ? 'danger' : 'success', swCount(ok, 'bild är borttagen', 'bilder är borttagna') + (last ? '. ' + last : '.'));
        } }, 'Ta bort'),
        h('button', { type: 'button', class: 'btn btn-sm btn-link', 'data-bulk': 'clear', onclick: () => { L.sel.clear(); paint(); } }, 'Avmarkera')) : null;
      const selectAll = list.length ? h('button', { type: 'button', class: 'btn btn-sm btn-link px-0', 'data-select-all': '1', onclick: () => { list.forEach(x => L.sel.add(x.id)); paint(); } }, 'Markera alla ' + list.length + ' som visas') : null;
      const count = h('div', { class: 'd-flex flex-wrap justify-content-between align-items-center mb-1' }, h('span', { class: 'small text-muted', id: 'hsLibCount', 'aria-live': 'polite' }, 'Visar ' + list.length + ' av ' + swCount(files.length, 'bild', 'bilder')), selectAll);
      const manualHint = inAlbum && L.sort === 'manual' && !L.q.trim() ? h('p', { class: 'small text-muted mb-2' }, 'Bilderna ligger i den ordning du valt. Ändra med Åtgärder → Flytta på varje bild.') : null;
      let body;
      if (!files.length) body = h('div', { class: 'hs-card p-3 text-muted' }, 'Ni har inga bilder än. Välj Åtgärder → Ladda upp bilder.');
      else if (!list.length) body = h('div', { class: 'hs-card p-3 text-muted', id: 'hsLibEmpty' }, L.q.trim() ? 'Inga bilder matchar "' + L.q.trim() + '".' : inAlbum ? 'Albumet är tomt. Gå till Alla bilder, markera bilder och välj Lägg i album.' : 'Alla bilder ligger i något album.');
      else if (mode === 'list') body = h('div', { class: 'hs-card p-0 overflow-hidden mb-3' }, h('div', { class: 'table-responsive' }, h('table', { class: 'table align-middle hs-list mb-0' },
        h('thead', {}, h('tr', {}, h('th', { class: 'ps-3' }, h('span', { class: 'visually-hidden' }, 'Markera')), h('th', {}, h('span', { class: 'visually-hidden' }, 'Bild')), h('th', {}, 'Namn'), albumsOn ? h('th', { class: 'd-none d-md-table-cell' }, 'Album') : null,
          h('th', { class: 'd-none d-lg-table-cell' }, 'Mått'), h('th', { class: 'd-none d-lg-table-cell' }, 'Storlek'), h('th', { class: 'd-none d-xl-table-cell' }, 'Uppladdad'), h('th', {}, 'Används'), h('th', { class: 'text-end pe-3' }, 'Åtgärder'))),
        h('tbody', {}, list.map(x => listRow(x, list))))));
      else body = h('div', { class: 'hs-imggrid hs-imggrid-wide mb-3' }, list.map(x => tile(x, list)));
      mainBox.replaceChildren(...[head, bulk, count, manualHint, body].filter(Boolean));
    };

    view(shell('bilder',
      pageHead('Bilder', 'Föreningens bilder. Sortera dem i album, sök, och välj dem i bildblock, bildgalleri och text på era sidor. Bilder och dokument delar på samma utrymme. En bild som används på en sida kan inte tas bort förrän den tagits bort från sidan. Bilden skrivs om när den laddas upp: platsdata och annan dold information tas bort och en mycket stor bild förminskas. Lägg inte upp bilder där någon kan kännas igen utan att ha gett sitt tillstånd.', [
        ['Ladda upp bilder', () => fileInp.click()], albumsOn ? ['Nytt album …', () => newAlbum([])] : null]),
      msg, fileInp,
      f.failed ? alertBox('danger', f.failed) : h('div', { class: 'hs-card p-3 mb-3' },
        h('div', { class: 'd-flex flex-wrap justify-content-between gap-2 mb-1' }, h('strong', {}, files.length + (files.length === 1 ? ' bild' : ' bilder')), h('span', { class: 'text-muted small' }, fmtSize(f.bytes || 0) + ' av ' + fmtSize(f.maxBytes || 0) + ' används (bilder ' + fmtSize(f.imageBytes || 0) + ', dokument ' + fmtSize(f.documentBytes || 0) + ') · högst ' + (f.maxFiles || 0) + ' bilder')),
        h('div', { class: 'progress', role: 'progressbar', 'aria-label': 'Använt utrymme', 'aria-valuenow': String(pct), 'aria-valuemin': '0', 'aria-valuemax': '100', style: 'height:.5rem' }, h('div', { class: 'progress-bar' + (pct >= 90 ? ' bg-warning' : ''), style: 'width:' + pct + '%' }))),
      files.length ? toolbar : null,
      mainBox));
    paint();
    if (banner) show(banner.cls, banner.text);
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

  // ── Sidans ikon (visas i menyn) ─────────────────────────────────────────────
  // Biblioteket kommer från servern (State.pageIcons, ENDA källan). Ikonen sparas direkt, utan publicering, och prövas av servern mot samma lista.
  const pageIcons = () => (S.st && S.st.pageIcons) || [];
  const defaultIcon = () => (S.st && S.st.defaultIcon) || 'bi-file-earmark-text';
  const iconLabel = key => { const i = pageIcons().find(x => x.key === key); return i ? i.label.split(' ')[0] : 'Standard'; };
  const iconEl = key => h('i', { class: 'bi ' + (key || defaultIcon()), 'aria-hidden': 'true' });
  /** Väljarens dialog: sökbart rutnät över alla ikoner. Svarar med nyckeln, '' för standardikonen, eller null om man avbröt. */
  async function chooseIcon(current) {
    let picked;
    const grid = h('div', { class: 'hs-icongrid', role: 'group', 'aria-label': 'Ikoner' });
    const search = h('input', { type: 'search', class: 'form-control mb-3', placeholder: 'Sök ikon, till exempel pokal, karta, kalender', 'aria-label': 'Sök ikon' });
    const live = h('p', { class: 'visually-hidden', role: 'status' });
    const draw = () => {
      const q = search.value.trim().toLowerCase();
      const hits = pageIcons().filter(i => !q || i.label.toLowerCase().includes(q) || i.group.toLowerCase().includes(q) || i.key.includes(q));
      const groups = [...new Set(hits.map(i => i.group))];
      grid.replaceChildren(...(hits.length ? groups.flatMap(g => [
        h('p', { class: 'small fw-semibold text-muted mb-1 mt-2 hs-icongroup' }, g),
        h('div', { class: 'hs-iconrow' }, hits.filter(i => i.group === g).map(i => h('button', {
          type: 'button', class: 'hs-iconbtn' + (i.key === current ? ' active' : ''), title: i.label, 'aria-label': i.label.split(' ')[0] + (i.key === current ? ' (vald)' : ''), 'aria-pressed': i.key === current ? 'true' : 'false', 'data-icon': i.key,
          onclick: () => { picked = i.key; const x = document.querySelector('.modal.show .btn-close'); if (x) x.click(); },
        }, iconEl(i.key))))]) : [h('p', { class: 'text-muted' }, 'Ingen ikon matchar "' + search.value + '".')]));
      live.textContent = hits.length + (hits.length === 1 ? ' ikon' : ' ikoner');
    };
    search.addEventListener('input', draw);
    draw();
    setTimeout(() => { const el = document.querySelector('.modal.show input[type=search]'); if (el) el.focus(); }, 400);
    const v = await modal('Välj ikon för sidan', [search, live, grid], [{ label: 'Avbryt', value: false }, { label: 'Använd standardikonen', value: 'default', cls: 'btn-outline-secondary' }], true);
    if (picked) return picked;
    return v === 'default' ? '' : null;
  }
  /** Raden "Ikon: [symbol] Byt ikon" som visar nuvarande ikon och öppnar biblioteket. onPick(nyckel) anropas med det som valts ('' = standard). */
  function iconRow(current, onPick) {
    const holder = h('span', { class: 'hs-iconcur', 'data-page-icon': current || '' }, iconEl(current));
    const btn = h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', id: 'hsPageIconBtn', 'aria-label': 'Byt ikon för sidan' }, 'Byt ikon …');
    btn.addEventListener('click', async () => {
      const pick = await chooseIcon(current);
      if (pick === null) return;
      const ok = await onPick(pick);
      if (ok === false) return;
      current = pick; holder.dataset.pageIcon = pick; holder.replaceChildren(iconEl(pick));
    });
    return h('div', { class: 'd-flex align-items-center gap-2 hs-iconrowline' }, h('span', { class: 'form-label fw-semibold mb-0' }, 'Ikon i menyn'), holder, btn);
  }

  // ── Ny sida i tre steg ──────────────────────────────────────────────────────
  function renderNew(w, parentId) {
    // En ny undersida (parentId): föräldern måste vara en egen sida på egen nivå, och undersidan får aldrig vara öppnare än den.
    const parentRow = findParent(w ? w.parentId : parentId);
    const minV = parentRow ? parentRow.visibility : 0;
    w = w || { step: 1, title: '', template: 'text', visibility: Math.max(OWNER.type === 'Region' ? 1 : 2, minV), parentId: parentRow ? parentRow.id : null };
    setHash('#ny' + (parentRow ? '=' + parentRow.id : ''));
    const err = h('div');
    const step = (n, t) => h('li', { class: 'list-inline-item ' + (w.step === n ? 'fw-bold' : 'text-muted') }, n + '. ' + t);
    let content, next, back = null;
    if (w.step === 1) {
      const inp = h('input', { class: 'form-control', maxlength: '120', value: w.title, placeholder: 'Till exempel: Regler för banan', 'aria-label': 'Sidans namn', id: 'hsNewTitle' });
      // Adressen: räknas ut ur namnet tills man själv skriver i fältet. Servern prövar den (samma regel som när sidan skapas) och Nästa är avstängd tills den är ledig.
      const addr = h('input', { class: 'form-control', maxlength: '60', value: w.slug || '', 'aria-label': 'Sidans adress', id: 'hsNewSlug', autocomplete: 'off', spellcheck: 'false' });
      const addrMsg = h('div', { class: 'form-text', role: 'status', 'aria-live': 'polite', id: 'hsNewAddrMsg' });
      const prefix = ((OWNER.url || '').replace(/\/$/, '') || '') + '/';
      let seq = 0, timer = null;
      const setNext = ok => { const b = document.getElementById('hsNewNext'); if (b) b.disabled = !ok; };
      const paint = (kind, text) => { addrMsg.className = 'form-text ' + (kind === 'ok' ? 'text-success' : kind === 'bad' ? 'text-danger' : ''); addrMsg.textContent = text; };
      const check = async () => {
        const my = ++seq;
        const title = w.title.trim();
        if (!title && !w.slugEdited) { w.addrOk = false; setNext(false); paint('', 'Adressen visas här när du har skrivit ett namn.'); return; }
        const r = await api('GET', 'CheckAddress', { title, slug: w.slugEdited ? addr.value.trim() : undefined });
        if (my !== seq) return;   // ett nyare svar gäller
        if (!r.success) { w.addrOk = false; setNext(false); paint('bad', r.message || 'Adressen gick inte att kontrollera. Försök igen.'); return; }
        if (!r.ok) { w.addrOk = false; setNext(false); paint('bad', r.message || 'Adressen går inte att använda.'); return; }
        w.slug = r.slug; w.addrOk = true; w.addrUrl = r.url;
        if (!w.slugEdited) addr.value = r.slug;
        setNext(true);
        paint('ok', r.numbered ? 'Namnet var upptaget, så adressen blir ' + r.url + '. Du kan ändra den i fältet ovan.' : 'Adressen är ledig: ' + r.url);
      };
      const later = () => { w.addrOk = false; setNext(false); paint('', 'Kontrollerar adressen …'); clearTimeout(timer); timer = setTimeout(check, 350); };
      inp.addEventListener('input', () => { w.title = inp.value; later(); });
      addr.addEventListener('input', () => { w.slugEdited = addr.value.trim() !== ''; later(); });
      content = [parentRow ? h('p', { class: 'text-muted' }, 'Undersida till ', h('strong', {}, parentRow.title), '.') : null,
        h('label', { class: 'form-label fw-semibold', for: 'hsNewTitle' }, parentRow ? 'Vad ska undersidan heta?' : 'Vad ska sidan heta?'), inp, h('div', { class: 'form-text mb-3' }, 'Namnet blir sidans rubrik och ger förslaget på adress.'),
        h('label', { class: 'form-label fw-semibold', for: 'hsNewSlug' }, 'Sidans adress'),
        h('div', { class: 'input-group' }, h('span', { class: 'input-group-text text-break d-none d-sm-flex', style: 'max-width:60%;white-space:normal' }, prefix), addr), addrMsg,
        h('div', { class: 'form-text mb-3' }, 'Små bokstäver a–z, siffror och bindestreck. Skriv en egen adress om du vill, eller töm fältet för att få förslaget ur namnet igen.'),
        iconRow(w.icon || '', pick => { w.icon = pick; return true; }), h('div', { class: 'form-text' }, 'Ikonen syns bredvid sidans namn i menyn. Du kan byta den när som helst.'), err];
      next = () => { if (!w.title.trim()) { err.replaceChildren(alertBox('warning', 'Skriv ett namn på sidan.')); return; } if (!w.addrOk) return; w.step = 2; renderNew(w); };
      setTimeout(() => inp.focus(), 0);
      if (w.addrOk) paint('ok', 'Adressen är ledig: ' + (w.addrUrl || prefix + w.slug + '/')); else if (w.title.trim() || w.slugEdited) later(); else paint('', 'Adressen visas här när du har skrivit ett namn.');
    } else if (w.step === 2) {
      // Färdiga sidor = sidor som redan är byggda av blocken (och hämtar sitt innehåll live); Egen sida = börja med text eller tomt.
      const ready = readyTemplates();
      const own = [
        ['text', 'Textsida', 'En sida där du skriver fri text. Passar det mesta.'],
        ['empty', 'Tom sida', 'Börja helt utan innehåll.'],
      ];
      const choice = ([k, t, s]) => h('label', { class: 'hs-choice ' + (w.template === k ? 'active' : ''), 'data-template': k },
        h('input', { type: 'radio', name: 'tpl', checked: w.template === k, onchange: () => { w.template = k; renderNew(w); } }), h('strong', {}, t), h('span', { class: 'hs-sub' }, s));
      content = [h('p', { class: 'text-muted small fw-semibold mb-1' }, 'Färdiga sidor'), ...ready.map(choice),
        h('p', { class: 'text-muted small fw-semibold mb-1 mt-3' }, 'Egen sida'), ...own.map(choice)];
      back = () => { w.step = 1; renderNew(w); };
      next = () => { w.step = 3; renderNew(w); };
    } else {
      content = [h('p', { class: 'fw-semibold mb-2' }, 'Vem ska kunna läsa sidan?'),
        parentRow ? h('p', { class: 'text-muted small' }, 'En undersida kan inte vara öppnare än sidan den ligger under (' + visLabel(parentRow.visibility).toLowerCase() + ').') : null,
        visibilityChoices(w.visibility, v => { w.visibility = v; renderNew(w); }, minV), err];
      back = () => { w.step = 2; renderNew(w); };
      next = async () => {
        if (w.visibility === 0 && !await confirmPublic()) return;
        const create = await api('POST', 'CreatePage', { title: w.title.trim(), slug: w.slug || undefined, visibility: w.visibility, parentPageId: parentRow ? parentRow.id : undefined, icon: w.icon || undefined });
        if (!create.success) {
          // Någon hann ta adressen medan du valde nivå: tillbaka till steg 1 med ett nytt förslag.
          if (create.status === 'SlugTaken') { w.step = 1; w.addrOk = false; w.slugEdited = false; w.slug = ''; renderNew(w); document.getElementById('hsNewAddrMsg')?.replaceChildren('Adressen hann bli upptagen: ' + (create.message || '') + ' Kontrollerar en ny adress …'); return; }
          err.replaceChildren(alertBox('danger', create.message || 'Det gick inte att skapa sidan.')); return;
        }
        // Mallens startinnehåll sparas som första utkast, därefter öppnas redigeraren.
        const blocks = templateBlocks(w.template, w.visibility);
        S.hint = w.template === 'docs' ? 'Välj vilka dokument som ska visas i blocket Dokument (Ändra på kortet).' : w.template === 'meetings' ? 'Mötena hämtas ur kalendern (händelser av typen Möte). Välj kallelser och protokoll i blocket Dokument (Ändra på kortet).' : null;
        if (blocks.length) {
          const save = await api('POST', 'SavePage', { pageId: create.pageId, expectedRevision: create.revision, title: w.title.trim(), blocksJson: JSON.stringify({ schema: 1, blocks }), visibility: w.visibility });
          if (!save.success) { err.replaceChildren(alertBox('danger', save.message || 'Sidan skapades men mallen kunde inte läggas in.')); }
        }
        await reloadState();
        openEditor(create.pageId);
      };
    }
    const nextBtn = h('button', { class: 'btn btn-primary', id: 'hsNewNext', onclick: () => next() }, w.step === 3 ? 'Skapa sidan' : 'Nästa');
    if (w.step === 1 && !w.addrOk) nextBtn.disabled = true;   // steg 1 går inte vidare förrän adressen är kontrollerad och ledig
    view(shell('sidor', pageHead(parentRow ? 'Ny undersida' : 'Ny sida', 'En egen sida i tre steg: namn, mall och vem som får läsa den.'), h('ol', { class: 'list-inline mb-3' }, step(1, 'Namn'), step(2, 'Mall'), step(3, 'Vem ser den')),
      h('div', { class: 'hs-card p-3 p-md-4', style: 'max-width:46rem' }, content,
        h('div', { class: 'd-flex gap-2 mt-3' }, back ? h('button', { class: 'btn btn-outline-secondary', onclick: back }, 'Tillbaka') : h('button', { class: 'btn btn-outline-secondary', onclick: renderList }, 'Avbryt'), nextBtn))));
  }

  /** Färdiga sidor: EN lista för guiden Ny sida och för "börja från en färdig sida" på en tom sida. Varje nyckel har sin gren i templateBlocks. */
  function readyTemplates() {
    const who = IS_REGION ? 'kretsens' : 'klubbens';
    return [
      ['welcome', 'Välkomstsida', 'Presentationstext, kommande händelser, nyheter och tävlingar. Allt utom texten hämtas automatiskt.'],
      ['member', 'Bli medlem / Prova på', 'Text och ett anmälningsformulär för den som vill prova eller bli medlem, med kontaktuppgifter.'],
      ['news', 'Nyheter', 'Text och de senaste nyheterna. Nyheterna skriver ni under Nyheter på ' + who + ' sida.'],
      ['staff', 'Funktionärer', 'Text, de tävlingar som söker funktionärer och ett formulär för den som vill hjälpa till.'],
      ['aboutus', 'Om oss och kontakt', 'Föreningens presentation och kontaktuppgifter, hämtade ur ' + who + ' uppgifter.'],
      ['competitions', 'Tävlingar och resultat', 'Kommande tävlingar, ' + (IS_REGION ? 'kretsrekord' : 'klubbrekord') + ' och regerande mästare.'],
      ['tourney', 'Årets tävlingar', 'Kommande tävlingar och dokument som inbjudningar och programblad.'],
      ['ranges', 'Skjutbanor', IS_REGION ? 'Text och en lista över banorna kretsens klubbar använder.' : 'Text och en lista över klubbens banor, utan karta.'],
      ['photos', 'Fotoalbum', 'Text och ett bildgalleri. Bilderna väljer ni efteråt ur bildbiblioteket.'],
      ['board', 'Styrelse och stadgar', 'Text, dokument som stadgar och protokoll, och kontaktuppgifter.'],
      ['docs', 'Sida med dokument', 'Text och dokument som medlemmarna kan ladda ned, till exempel blanketter.'],
      ['contact', 'Hitta hit & kontakt', 'Text om hur man hittar, och föreningens kontaktuppgifter.'],
      ['meetings', 'Möten och kallelser', 'Kommande möten från kalendern, och dokument för kallelser och protokoll.'],
      ...(IS_REGION ? [['overview', 'Klubbar, banor och serier', 'Kretsens klubbar, skjutbanor och serier i en lista, hämtade ur kretsens data.']] : []),
    ];
  }

  function templateBlocks(tpl, vis) {
    const text = () => ({ id: newId(), type: 'text', visibility: vis, data: { html: '' } });
    if (tpl === 'empty') return [];
    if (tpl === 'contact') return [text(), { id: newId(), type: 'contact', visibility: Math.max(vis, ((S.st && S.st.blockFloors) || {}).contact || 0), data: { title: 'Kontakt' } }];
    if (tpl === 'meetings') return [text(), { id: newId(), type: 'events', visibility: vis, data: { title: 'Nästa möten', count: 3, type: 'Möte' } }, { id: newId(), type: 'documents', visibility: vis, data: { title: 'Kallelser och protokoll', refs: [] } }];
    if (tpl === 'docs') return [text(), { id: newId(), type: 'documents', visibility: vis, data: { title: 'Dokument', refs: [] } }];
    // Färdiga sidor ur biblioteket. Personblock (kontakt, mästare) får aldrig vara öppnare än Inloggade, och ett block aldrig öppnare än sidan.
    const floorOf = type => (S.st && S.st.blockFloors && S.st.blockFloors[type]) || 0;   // serverns golv-tabell
    const blk = (type, data) => ({ id: newId(), type, visibility: Math.max(vis, floorOf(type)), data });
    const noun = IS_REGION ? 'Kretsrekord' : 'Klubbrekord';
    if (tpl === 'welcome') return [blk('about', { title: '' }), blk('events', { title: 'Kommande händelser', count: 5 }), blk('news', { title: 'Senaste nyheterna', count: 3 }), blk('competitions', { title: 'Kommande tävlingar', count: 3 })];
    if (tpl === 'aboutus') return [blk('about', { title: '' }), blk('contact', { title: 'Kontakt' })];
    if (tpl === 'member') return [text(), blk('signup', { title: 'Bli medlem', intro: '', choices: [] }), blk('contact', { title: 'Kontakt' })];
    if (tpl === 'news') return [text(), blk('news', { title: 'Senaste nyheterna', count: 10 })];
    if (tpl === 'staff') return [text(), blk('staff-seeking', { title: 'Funktionärer sökes', count: 10 }), blk('signup', { title: 'Anmäl intresse', intro: '', choices: [] })];
    if (tpl === 'tourney') return [text(), blk('competitions', { title: 'Kommande tävlingar', count: 10 }), blk('documents', { title: 'Inbjudningar och programblad', refs: [] })];
    if (tpl === 'photos') return [text(), blk('gallery', { title: '', items: [] })];
    if (tpl === 'board') return [text(), blk('documents', { title: 'Stadgar och protokoll', refs: [] }), blk('contact', { title: 'Kontakt' })];
    if (tpl === 'competitions') return [text(), blk('competitions', { title: 'Kommande tävlingar', count: 10 }), blk('records', { title: noun }), blk('champions', { title: IS_REGION ? 'Regerande kretsmästare' : 'Regerande klubbmästare' })];
    if (tpl === 'ranges') return [text(), blk('ranges', { title: 'Skjutbanor' })];
    if (tpl === 'overview') return [text(),
      { id: newId(), type: 'region-clubs', visibility: vis, data: { title: 'Klubbar i kretsen' } },
      { id: newId(), type: 'region-ranges', visibility: vis, data: { title: 'Skjutbanor' } },
      { id: newId(), type: 'region-series', visibility: vis, data: { title: 'Serier', count: 10 } }];
    return [text()];
  }

  function visibilityChoices(current, onPick, min) {
    return h('div', {}, visOptions().filter(o => o.v >= (min || 0)).map(o => h('label', { class: 'hs-choice ' + (current === o.v ? 'active' : '') },
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
    'about-text': 'klubbens presentationstext, samma text som redan visas på startsidan för den som inte är inloggad.',
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
      dirty: false, editVersion: 0, banner: S.nextBanner && S.nextBanner.until > Date.now() ? S.nextBanner.banner : null, statusText: 'Sparat', statusErr: false });   // setHash utlöser en andra laddning som annars nollställer meddelandet
    if (!S.docs) { const d = await api('GET', 'Documents'); S.docs = d.success ? d.documents : []; }
    Object.assign(S, { openId: null, undo: [], justAdded: null, focusAfterRte: null });   // inget kvar från förra sidan (öppet kort, ångra-logg, markering)
    S.bodyCache.clear();
    renderEditor();
  }

  function markDirty() {
    S.dirty = true; S.editVersion++; S.statusText = 'Inte sparat'; S.statusErr = false; paintStatus(); paintCardLive();
    // "Sidan är publicerad." får inte stå kvar när man ändrar igen: då visas i stället att ändringarna inte är publicerade (uppgiftsprovet: en nybörjare tror annars att "Sparat" betyder att det syns).
    if (S.banner && S.banner.cls === 'success') S.banner = null;
    paintEditorBanner();
    clearTimeout(S.timer); S.timer = setTimeout(() => saveNow(), SAVE_IDLE_MS);
  }
  function paintStatus() {
    const el = document.getElementById('hsStatus'); if (el) { el.textContent = S.statusText; el.className = 'hs-status' + (S.statusErr ? ' err' : ''); }
    const pub = document.getElementById('hsPublish'); if (pub) pub.disabled = S.saving || !(S.dirty || S.hasUnpublished);
    // Raderna i Åtgärder-menyn visas bara när de gäller; hela menyn göms när ingen rad gäller.
    const item = (id, on) => { const e = document.getElementById(id); if (e && e.closest('li')) e.closest('li').hidden = !on; };
    // "Öppna den publicerade sidan": bara när sidan finns publicerad och har en adress (en utkastsida kan ingen annan öppna).
    const open = document.getElementById('hsOpenPage'); if (open) { const url = S.page && S.page.url; item('hsOpenPage', !!(S.published && url)); if (url) open.href = url; }
    // En omvandling som inte är publicerad avbryts med "Avbryt omvandlingen" (inte "Kasta ändringarna", som skulle lämna sidan i ett halvt läge).
    item('hsDiscard', !!(S.published && (S.dirty || S.hasUnpublished)) && !(S.kind === 'converted' && !S.convertedLive));
    item('hsCancelConv', S.kind === 'converted' && !S.convertedLive);
    const menu = document.getElementById('hsActions'); if (menu) menu.hidden = [...menu.querySelectorAll('li')].every(li => li.hidden);
    // Sidans läge som märke (samma ord som i listan).
    const ps = document.getElementById('hsPageState');
    if (ps) {
      const live = S.kind === 'converted' ? S.convertedLive : S.published;
      const [cls, txt] = !live ? ['wait', S.kind === 'converted' ? 'Utkast, besökarna ser standardsidan' : 'Utkast, inte publicerad'] : (S.dirty || S.hasUnpublished) ? ['wait', 'Ändringar inte publicerade'] : ['ok', 'Publicerad'];
      ps.className = 'hs-state ' + cls; ps.textContent = txt;
    }
  }

  function setBanner(cls, text, extra) { S.banner = { cls, text, extra }; paintEditorBanner(); }
  function paintEditorBanner() {
    const el = document.getElementById('hsBanner'); if (!el) return;
    const kids = [];
    if (S.banner) kids.push(h('div', { class: 'alert alert-' + S.banner.cls + ' d-flex flex-wrap gap-2 align-items-center' }, h('span', { class: 'me-auto' }, S.banner.text), S.banner.extra || null));
    else if (S.kind === 'converted' && !S.convertedLive) kids.push(h('div', { class: 'alert alert-secondary d-flex flex-wrap gap-2 align-items-center' }, h('span', { class: 'me-auto' }, 'Utkast. Besökarna ser fortfarande standardsidan.'),
      h('a', { href: OWNER.url, target: '_blank', rel: 'noopener' }, 'Se standardsidan')));
    else if (S.published && (S.dirty || S.hasUnpublished)) kids.push(h('div', { class: 'alert alert-info' }, 'Du har ändringar som inte är publicerade. Besökarna ser fortfarande den publicerade versionen.'));
    else if (!S.published) kids.push(h('div', { class: 'alert alert-secondary' }, 'Sidan är inte publicerad än. Ingen annan kan se den förrän du publicerar.'));
    // En omgjord sida där varje block är för styrelsen eller högre: ingen annan ser något, och fliken försvinner för dem.
    if (S.kind === 'converted' && S.blocks.length && S.blocks.every(b => b.visibility >= 3 && b.visibility <= 4))
      kids.push(h('div', { class: 'alert alert-warning' }, 'Ingen utom styrelsen ser något på den här sidan.'));
    if (S.hint) kids.push(h('div', { class: 'alert alert-light border' }, S.hint));
    el.replaceChildren(...kids);
  }

  /**
   * Det som sparas av ett block, eller null när blocket inte går att spara än. Servern vägrar ett länkblock utan en komplett länk, ett dokumentblock utan dokument och en tom rubrik, och en sida
   * med ett sådant block sparas inte alls (felet hamnade förut i en ruta högst upp). Därför sparas det som är komplett och det ofärdiga blocket ligger kvar på skärmen med märket "Tomt"
   * tills det fyllts i. Ett ofärdigt block som inte hann fyllas i före en omladdning är alltså borta, och det är det enda som går förlorat.
   */
  function savableBlock(b) {
    const d = b.data || {};
    if (b.type === 'links') {
      const items = (d.items || []).filter(it => String(it.label || '').trim() && String(it.url || '').trim());
      return items.length ? { ...b, data: { ...d, items } } : null;
    }
    if (b.type === 'documents') return (d.refs || []).length ? b : null;
    if (b.type === 'heading') return String(d.text || '').trim() ? b : null;
    return b;
  }
  function collectJson() {
    const plain = b => ({ id: b.id, type: b.type, visibility: b.visibility, data: b.data });
    const saved = S.blocks.map(savableBlock).filter(Boolean);
    if (S.kind === 'slots') return JSON.stringify({ schema: 1, slots: { before: saved.filter(b => b.pos === 'before').map(plain), after: saved.filter(b => b.pos === 'after').map(plain) } });
    return JSON.stringify({ schema: S.kind === 'converted' ? 2 : 1, blocks: saved.map(plain) });
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
    const noAlt = S.blocks.filter(b => b.type === 'image' && b.data.decorative !== true && !String(b.data.alt || '').trim()).length
      + S.blocks.filter(b => b.type === 'gallery').reduce((n, b) => n + (b.data.items || []).filter(it => !String(it.alt || '').trim() && !String(it.caption || '').trim()).length, 0);   // en bild i ett galleri räknas som beskriven om den har beskrivning eller bildtext
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
      S.banner = null; paintEditorBanner();   // ingen grön ruta: statusraden säger "Publicerat kl." och knappen "Öppna sidan" visas bredvid Publicera ändringar
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
    const titleInp = h('input', { class: 'form-control form-control-lg', maxlength: '120', value: S.title, 'aria-label': 'Sidans rubrik', id: 'hsTitle' });
    titleInp.addEventListener('input', () => { S.title = titleInp.value; markDirty(); });
    const visSel = h('select', { class: 'form-select', 'aria-label': 'Vem ser sidan' }, visOptions().filter(o => o.v >= S.minVis).map(o => h('option', { value: o.v, selected: o.v === S.visibility ? true : null }, o.s)));
    visSel.addEventListener('change', async () => {
      const v = parseInt(visSel.value, 10);
      if (v === 0 && !await confirmPublic()) { visSel.value = S.visibility; return; }
      // Gör man en förälder strängare får undersidorna samma nivå (en undersida kan inte vara öppnare än sidan den ligger under). Servern gör det alltid; här frågar vi först.
      const kids = !slots && !conv ? (S.page.childCount || 0) : 0;
      if (kids > 0 && v > S.visibility && !await confirmDialog('Undersidorna får också nivån', kids + (kids === 1 ? ' undersida får' : ' undersidor får') + ' också nivån "' + visLabel(v) + '", eftersom en undersida inte kan vara öppnare än sidan den ligger under.', 'Ändra', false)) { visSel.value = S.visibility; return; }
      setPageVisibility(v);
    });
    const consequence = h('div', { class: 'text-muted small mt-1' }, CONSEQUENCE[S.visibility]);
    const blocksEl = h('div', { id: 'hsBlocks' });
    // Inställningen och resultatet är EN sektion, "Vem ser sidan?" (egen text på en vanlig sida har bara inställningen).
    const who = slots ? null : whoSeesCard(() => ({ pageId: S.page.id }), {
      beforeLoad: async () => { if (S.dirty) await saveNow(); }, fallbackUrl: () => (S.page && S.page.url) || null, hint: visLabel(S.visibility),
      collapsed: !S.blocks.length && !S.published,   // en ny, tom sida: först blocken, inte en tabell som trycker ned dem (sidans nivå syns som märke i rubrikraden)
      defaultOpen: false,                             // och på en sida med innehåll ligger tabellen hopfälld tills man öppnar den, så att blocken syns utan rullning
      setting: [
        h('label', { class: 'form-label small text-muted mb-1', for: 'hsVisSel' }, 'Vem får läsa sidan?'), visSel, consequence,
        !conv && S.page.url ? h('div', { class: 'small mt-1 text-muted' }, 'Adress: ', h('code', {}, S.page.url)) : null,
        conv ? null : h('div', { class: 'mt-3' }, menuToggle()),
      ],
    });
    visSel.id = 'hsVisSel';

    // Fältet: Sidor, status, Ångra, Förhandsgranska, EN grå Åtgärder-meny (sajtens regel: tre eller fler kontroller blir en meny) och den enda blå knappen, Publicera ändringar.
    // Under raden ligger meddelanderaden (en live-region): det som just hände och Ångra, så att man ser det där man står.
    const actions = h('div', { class: 'dropdown', id: 'hsActions', hidden: true },
      h('button', { class: 'btn btn-outline-secondary dropdown-toggle', type: 'button', 'data-bs-toggle': 'dropdown', 'data-bs-popper-config': '{"strategy":"fixed"}', 'aria-expanded': 'false' }, 'Åtgärder'),
      h('ul', { class: 'dropdown-menu dropdown-menu-end' },
        h('li', { hidden: true }, h('a', { id: 'hsOpenPage', class: 'dropdown-item', href: (S.page && S.page.url) || '#', target: '_blank', rel: 'noopener', title: 'Öppna den publicerade sidan i en ny flik' }, 'Öppna den publicerade sidan')),
        h('li', { hidden: true }, h('button', { id: 'hsCancelConv', type: 'button', class: 'dropdown-item', onclick: () => cancelConversionFlow(S.pageKey, S.page.id, S.revision, true) }, 'Avbryt omvandlingen')),
        h('li', { hidden: true }, h('button', { id: 'hsDiscard', type: 'button', class: 'dropdown-item text-danger', onclick: async () => {
          if (!await confirmDialog('Kasta ändringarna?', 'Ändringarna sedan senaste publiceringen försvinner. Den publicerade sidan påverkas inte.', 'Kasta ändringarna', true)) return;
          clearTimeout(S.timer); S.dirty = false;
          const r = await api('POST', 'DiscardDraft', { pageId: S.page.id, expectedRevision: S.revision });
          if (!r.success) { setBanner('danger', r.message || 'Det gick inte att kasta ändringarna.'); return; }
          openEditor(S.page.id);
        } }, 'Kasta ändringarna'))));
    const bar = h('div', { class: 'hs-bar' },
      h('div', { class: 'd-flex flex-wrap gap-2 align-items-center' },
        h('button', { class: 'btn btn-outline-secondary', onclick: async () => { if (S.dirty) await saveNow(); setHash(slots ? '#sida=' + S.templateKey : conv ? '#sida=' + S.pageKey : '#sidor'); load(); } },
          h('i', { class: 'bi bi-arrow-left me-1' }), slots || conv ? 'Tillbaka till sidan' : 'Sidor'),
        h('span', { id: 'hsStatus', class: 'hs-status ms-auto' }, S.statusText),
        h('button', { id: 'hsUndo', type: 'button', class: 'btn btn-outline-secondary', disabled: true, onclick: () => undoLast() }, h('i', { class: 'bi bi-arrow-counterclockwise me-1', 'aria-hidden': 'true' }), 'Ångra'),
        h('button', { id: 'hsPreview', type: 'button', class: 'btn btn-outline-secondary', onclick: () => openPreview() }, h('i', { class: 'bi bi-eye me-1', 'aria-hidden': 'true' }), 'Förhandsgranska'),
        actions,
        h('button', { id: 'hsPublish', class: 'btn btn-primary', onclick: () => publishNow() }, 'Publicera ändringar')),
      h('div', { id: 'hsToast', class: 'hs-toast', role: 'status', 'aria-live': 'polite', 'aria-atomic': 'true' }));

    view(shell(slots ? 'sys:' + S.templateKey : conv ? 'sys:' + S.pageKey : 'sidor', bar, h('div', { id: 'hsBanner' }),
      slots
        ? h('div', { class: 'mb-3' }, h('h1', { class: 'h3 mb-1' }, 'Egen text på "' + S.title + '"'),
            h('p', { class: 'text-muted mb-0' }, 'Texten visas före och efter sidans vanliga innehåll. Sidans eget innehåll ändras inte.'))
        : conv
          ? h('div', { class: 'mb-3' }, h('h1', { class: 'h3 mb-1' }, 'Ändra sidan "' + S.title + '"'),
              h('p', { class: 'text-muted mb-0' }, 'Här bestämmer ni hur sidan ser ut. Det som hämtas automatiskt visas som kort som ni kan flytta, och ta bort om det inte är obligatoriskt. Ni kan också lägga till eget innehåll. Ändringarna syns för besökarna först när ni publicerar.'))
          : h('div', { class: 'mb-3' }, treeInfo(), h('label', { class: 'form-label fw-semibold', for: 'hsTitle' }, 'Rubrik'), titleInp,
              h('div', { class: 'mt-2' }, iconRow(S.page.icon === defaultIcon() ? '' : S.page.icon, async pick => {
                const r = await api('POST', 'SetIcon', { pageId: S.page.id, icon: pick });
                if (!r.success) { setBanner('danger', r.message || 'Det gick inte att byta ikon.'); return false; }
                S.page.icon = pick || defaultIcon();
                return true;
              }))),
      h('div', { class: 'd-flex flex-wrap align-items-center gap-2 mb-3 hs-pagemarks' }, h('span', { class: 'small text-muted' }, 'Sidan:'), h('span', { id: 'hsPageState' }), visBadge(S.visibility)),   // sidans läge och nivå, med samma märken som listan
      who || h('div', { class: 'mb-4' }, h('label', { class: 'form-label fw-semibold' }, 'Vem ser texten?'), visSel, consequence),
      h('h2', { class: 'visually-hidden' }, 'Sidans innehåll'), blocksEl, moreCard()));
    S.refreshWho = who ? who._refresh : null;
    drawBlocks();
    paintStatus(); paintEditorBanner(); paintUndo(); setBarHeight();
  }

  // "Vem ser sidan?": EN sektion med inställningen (opts.setting: rullgardinen, förklaringen, adressen, menyvalet) och resultatet som tabell över vad varje besökare ser (sidan i menyn,
  // varje del av sidan; ur samma regler som besökarna möter). Tabellen uppdaterar sig själv när något som styr åtkomst ändras (el._refresh).
  // "Förhandsvisa" under en besökare öppnar den RIKTIGA sidan i en ny flik, så som den besökaren ser den (förhandsläget: en signerad biljett, bara läsning, bara den här föreningens sidor).
  // Utloggad går alltid (fliken ser ingen inloggning alls). En annan besökare går bara på en publicerad egen sida, som ritas på servern för just den tittaren; sidor med delar ur systemet
  // hämtar dem med den inloggades egen session och kan inte visas som en annan person, så där finns länken bara för utloggad, och det står.
  // params = { templateKey } (vanlig sida) eller { pageId } (fri eller omgjord sida); beforeLoad körs först (spara utkastet).
  function whoSeesCard(params, opts) {
    opts = opts || {};
    let ready = false, timer = null;
    const msg = h('div', { class: 'mt-2' });
    const out = h('div', { class: 'mt-3' }, h('span', { class: 'text-muted' }, 'Laddar …'));
    const cell = st => st === 'see' ? h('span', { class: 'text-success' }, h('i', { class: 'bi bi-check-lg', 'aria-hidden': 'true' }), h('span', { class: 'visually-hidden' }, 'Ser'))
      : st === 'lock' ? h('span', { class: 'text-body-secondary' }, h('i', { class: 'bi bi-lock-fill', 'aria-hidden': 'true' }), h('span', { class: 'visually-hidden' }, 'Ser menyvalet med ett hänglås, får en låsruta'))
      : h('span', { class: 'text-body-secondary' }, '–', h('span', { class: 'visually-hidden' }, 'Ser inte'));
    const load = async () => {
      if (!ready) out.replaceChildren(h('span', { class: 'text-muted' }, 'Laddar …'));   // vid en uppdatering ligger gamla tabellen kvar tills den nya är klar
      if (opts.beforeLoad) await opts.beforeLoad();
      const r = await api('POST', 'Visibility', params());
      if (!r.success) { out.replaceChildren(alertBox('danger', r.message || 'Det gick inte att visa vem som ser sidan.')); return; }
      const pageUrl = r.publicUrl || (opts.fallbackUrl && opts.fallbackUrl()) || null;
      const basis = r.basis === 'draft' ? 'Tabellen visar utkastet. Besökarna ser det först när du har publicerat.'
        : r.basis === 'published' ? 'Tabellen visar den publicerade sidan.' : 'Tabellen visar sidan som den är idag.';
      const hasPanels = r.rows.some(x => x.kind === 'panel');
      const isFree = r.kind === 'free';
      const published = r.kind !== 'free' || r.published !== false;   // en egen sida måste vara publicerad för att finnas för besökarna
      // Den riktiga sidan finns för utloggad alltid, och för en annan besökare bara på en publicerad egen sida utan delar ur systemet.
      const canFor = c => !!pageUrl && published && (c.key === 'anon' || (isFree && !hasPanels));
      const previewLink = c => h('button', { type: 'button', class: 'btn btn-link btn-sm p-0 d-block mx-auto',
        title: 'Öppnar den riktiga sidan i en ny flik, så som ' + c.label.toLowerCase() + ' ser den. Det går bara att läsa.', onclick: async e => {
          const b = e.currentTarget;
          const w = window.open('', '_blank');   // öppnas direkt vid klicket, annars stoppar webbläsaren fliken; adressen sätts när biljetten är klar
          if (!w) { msg.replaceChildren(alertBox('warning', 'Webbläsaren stoppade den nya fliken. Tillåt popup-fönster för pistol.nu och försök igen.')); return; }
          msg.replaceChildren(); b.disabled = true;
          const pr = await api('POST', 'StartPreview', { persona: c.key, url: pageUrl });
          b.disabled = false;
          if (!pr.success) { w.close(); msg.replaceChildren(alertBox('danger', pr.message || 'Förhandsvisningen gick inte att starta.')); return; }
          w.location.href = pr.url;
        } }, h('i', { class: 'bi bi-box-arrow-up-right me-1', 'aria-hidden': 'true' }), 'Förhandsvisa');
      const anyPreview = r.columns.some(canFor);
      const put = (...nodes) => out.replaceChildren(...nodes.filter(Boolean));   // en tom nod skulle annars skrivas ut som texten "null"
      put(
        h('div', { class: 'table-responsive' }, h('table', { class: 'table table-sm align-middle mb-2' },
          h('thead', {}, h('tr', {}, h('th', { scope: 'col' }, ''), r.columns.map(c => h('th', { scope: 'col', class: 'text-center small fw-semibold' }, c.label, canFor(c) ? previewLink(c) : null)))),
          h('tbody', {}, r.rows.map(row => h('tr', {}, h('th', { scope: 'row', class: 'fw-normal' }, h('span', { class: row.kind === 'page' ? 'fw-semibold' : '' }, row.label),
            row.detail ? h('span', { class: 'text-muted small ms-2' }, row.detail) : null), row.states.map(st => h('td', { class: 'text-center position-relative' }, cell(st)))))))),
        h('p', { class: 'small text-muted mb-2' }, [h('i', { class: 'bi bi-check-lg text-success me-1', 'aria-hidden': 'true' }), 'ser  ',
          h('i', { class: 'bi bi-lock-fill ms-3 me-1', 'aria-hidden': 'true' }), 'ser sidan i menyn med ett hänglås och får en låsruta  ', h('span', { class: 'ms-3' }, '– ser inte')]),
        h('p', { class: 'mb-1' }, r.summary + ' ' + basis),
        anyPreview ? h('p', { class: 'small text-muted mb-1' }, 'Förhandsvisa öppnar den riktiga sidan i en ny flik, så som den besökaren ser den. Det går bara att läsa, inget kan ändras där.') : null,
        hasPanels ? h('p', { class: 'small text-muted mb-1' }, 'Förhandsvisa finns bara för utloggad här. Delar som hämtas ur systemet (till exempel Banor och Kalender) hämtas med den inloggades egen session, så de går inte att visa som en annan besökare. Tabellen visar vilka som ser dem.') : null,
        isFree && !published ? h('p', { class: 'small text-muted mb-1' }, 'Förhandsvisa finns när sidan är publicerad. Tills dess ser alla besökare beskedet att sidan inte finns.') : null);
    };
    const run = () => load().finally(() => { ready = true; });
    run();
    // Hopfällbar (som "Mer"-kortet): öppen som förval, och valet ligger kvar mellan sidor och omritningar i den här webbläsaren. Hopfälld syns rubriken, ikonen och nuvarande val.
    const OPEN_KEY = 'hsWhoSeesOpen';
    let isOpen = true;
    try { isOpen = localStorage.getItem(OPEN_KEY) !== '0'; } catch { /* ingen lagring: öppen */ }
    if (opts.collapsed) isOpen = false;
    else if (opts.defaultOpen === false) { try { isOpen = localStorage.getItem(OPEN_KEY) === '1'; } catch { isOpen = false; } }   // redigeraren: blocken är arbetsytan, tabellen öppnas när man vill se den (valet kommer ihåg)
    const el = h('details', { class: 'hs-card hs-who p-3 mb-3', id: 'hsWhoSees', open: isOpen ? true : null },
      h('summary', { class: 'd-flex align-items-center gap-2' },
        h('i', { class: 'bi bi-eye text-primary', 'aria-hidden': 'true' }),
        h('h2', { class: 'h5 mb-0' }, 'Vem ser sidan?'),
        opts.hint ? h('span', { class: 'text-muted small' }, '· ' + opts.hint) : null,
        h('i', { class: 'bi bi-chevron-down hs-chev ms-auto text-body-secondary', 'aria-hidden': 'true' })),
      h('div', { class: 'mt-2' },
        opts.setting
          ? h('div', {}, opts.setting)
          : h('div', {}, h('p', { class: 'text-muted mb-1' }, 'Så här är sidan inställd. Innehållet hämtas när besökaren öppnar sidan.'),
              opts.change ? h('p', { class: 'mb-0' }, opts.change.intro + ' ', h('button', { type: 'button', class: 'btn btn-link p-0 align-baseline', onclick: opts.change.run }, opts.change.label))
                : opts.note ? h('p', { class: 'text-muted mb-0' }, opts.note) : null),
        out, msg));
    el.addEventListener('toggle', () => { try { localStorage.setItem(OPEN_KEY, el.open ? '1' : '0'); } catch { /* ingen lagring */ } });
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
      if (r.affected) setBanner('info', r.affected + (r.affected === 1 ? ' undersida togs' : ' undersidor togs') + ' också ur menyn, eftersom de hör till den här sidan.');
    });
    return h('div', { class: 'form-check mb-0' }, cb, h('label', { class: 'form-check-label', for: 'hsInMenu' }, 'Visa sidan i menyn'),
      h('div', { class: 'form-text' }, 'Länken syns i menyn när sidan är publicerad, och bara för dem som får läsa sidan.'));
  }

  // Varför ett block inte går att lägga på den här sidan (tom sträng = det går). Servern prövar samma sak i SiteBlocks.
  function blockUnavailable(t) {
    if (t.type === 'signup' && S.kind !== 'free' && S.kind !== 'converted') return 'Går inte att lägga på den här sidan (sidan är en systemsida i den gamla formen). Gör om sidan först, eller använd en egen sida.';
    return '';
  }

  // ── Lägga till block: plats först, sedan block ───────────────────────────────
  // "＋ Lägg till här" finns före första blocket, mellan alla block och efter sista. Ett klick öppnar väljaren (sökbar, uppgifter överst, alla block under) som säger var blocket hamnar.
  // Samma väg för varje tillägg (en uppgift, ett block, en mall): golvet (minVisFor), platsen och otillgängliga block prövas på ETT ställe, aldrig i en parallell väg.
  const reducedMotion = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const OPENS_ON_ADD = new Set(['text', 'heading', 'links', 'documents', 'signup', 'pagelink', 'image', 'gallery']);   // blocken som behöver fyllas i öppnas direkt; övriga fungerar som de är
  const blockNo = i => 'block ' + (i + 1) + ' av ' + S.blocks.length;

  /** Ett platsknapp-element. `ctx`: { index (där i S.blocks blocket läggs), pos (slots: 'before'/'after'), after (blocket före, för texten) }. */
  function insertSlot(ctx) {
    // Namnet är unikt (blockets namn och nummer), så att en skärmläsare kan skilja åt tio lika knappar; den synliga texten "Lägg till här" ingår.
    const label = ctx.after ? 'efter ' + blockTitle(ctx.after) + ' (' + blockNo(indexOfId(ctx.after.id)) + ')' : S.blocks.some(b => !ctx.pos || b.pos === ctx.pos) ? 'först' : '';
    return h('button', { type: 'button', class: 'hs-insert', 'data-insert-at': ctx.index, 'data-insert-pos': ctx.pos || null,
      'aria-label': 'Lägg till här' + (label ? ', ' + label : ''), onclick: e => startAdd(ctx, e.currentTarget) },
      h('i', { class: 'bi bi-plus-lg me-1', 'aria-hidden': 'true' }), 'Lägg till här');
  }

  /** Öppnar väljaren för platsen och lägger det som väljs. */
  async function startAdd(ctx, opener) {
    const r = await openChooser({
      subtitle: ctx.after ? 'Läggs in efter "' + blockTitle(ctx.after) + '"' : ctx.pos ? (ctx.pos === 'before' ? 'Läggs in före sidans vanliga innehåll' : 'Läggs in efter sidans vanliga innehåll') : S.blocks.length ? 'Läggs in först på sidan' : 'Läggs in på sidan',
      slotOnly: S.kind === 'slots', zone: S.kind === 'converted' && S.pageKey === 'home',
    });
    if (!r) { afterModals(() => { const el = typeof opener === 'function' ? opener() : opener; if (el && el.isConnected) el.focus(); }); return; }   // avbrutet: fokus tillbaka där man var
    await addBlocks(r.types, ctx, r.zone);
  }

  /** Ett nytt block av en typ. Bild och galleri väljer fil först (avbryter man läggs inget till). Null = avbrutet. */
  async function makeBlock(type, zone) {
    const m = typeInfo(type);
    let data;
    if (type === 'image') {
      const f = await pickImage(); if (!f) return null;
      data = { ref: 'file:' + f.id, alt: '', decorative: false, caption: '' };
      S.hint = 'Beskriv bilden med några ord, så att den som inte ser bilden förstår vad den visar.';
    } else if (type === 'gallery') {
      const fs = await pickImage(null, { multi: true }); if (!fs || !fs.length) return null;
      data = { title: '', items: fs.slice(0, 24).map(f => ({ ref: 'file:' + f.id, alt: '', caption: '' })) };
      S.hint = 'Beskriv varje bild med några ord, så att den som inte ser bilderna förstår vad de visar.';
    } else if (type === 'pagelink') data = { key: (S.st.systemPages[0] || {}).key, label: '' };
    else data = JSON.parse(JSON.stringify(m.defaults || {}));
    if (zone && zone !== 'full') data.zone = zone;
    return { id: newId(), type, visibility: minVisFor(type), data };
  }

  /** Lägger blocken av `types` på platsen `ctx`, rullar till dem, markerar och fokuserar det första, och säger vad som hände (med Ångra). Alla eller inget: avbryts ett bildval läggs inget till. */
  async function addBlocks(types, ctx, zone) {
    const made = [];
    for (const t of types) { const b = await makeBlock(t, zone); if (!b) return; made.push(b); }
    if (!made.length) return;
    if (!types.some(t => typeInfo(t).needsImage)) S.hint = null;
    pushUndo('tillägg av ' + typeInfo(made[0].type).label);
    let at = ctx && ctx.index != null ? ctx.index : S.blocks.length;
    for (const b of made) { if (S.kind === 'slots') b.pos = ctx.pos || 'after'; S.blocks.splice(at++, 0, b); }
    S.justAdded = made.map(b => b.id);
    const first = made[0];
    if (OPENS_ON_ADD.has(first.type)) S.openId = first.id;   // ett block som behöver fyllas i öppnas (och tar över det som var öppet); ett som fungerar som det är lämnar det öppna kortet som det var
    S.focusAfterRte = first.type === 'text' ? first.id : null;
    markDirty(); drawBlocks();
    const i = S.blocks.indexOf(first);
    const names = made.map(b => typeInfo(b.type).label).join(' och ');
    toast('Lade till ' + names + ' som ' + blockNo(i) + '. ' + (typeInfo(first.type).live ? 'Innehållet hämtas automatiskt.' : OPENS_ON_ADD.has(first.type) ? 'Fyll i det nu.' : ''), true);
    revealCard(first.id, true);
  }

  /** Rullar till blockets kort, så att det syns under det klibbiga fältet, och flyttar fokus dit (till första fältet i ett öppet kort, annars till kortets namn). */
  function revealCard(id, focus) {
    afterModals(() => {
      const card = body.querySelector('[data-block-id="' + id + '"]'); if (!card) return;
      card.scrollIntoView({ block: 'nearest', behavior: reducedMotion() ? 'auto' : 'smooth' });
      if (!focus) return;
      const bodyEl = card.querySelector('.hs-block-body');
      if (bodyEl && !bodyEl.hidden) {
        if (card.querySelector('[data-rte]')) return;   // textredigeraren fokuseras när den är klar (initRte)
        const f = bodyEl.querySelector('input:not([type=hidden]):not([type=checkbox]),textarea,select,button');
        if (f) { f.focus({ preventScroll: true }); if (f.select && f.tagName === 'INPUT' && f.type === 'text') f.select(); return; }
      }
      const t = card.querySelector('.hs-type'); if (t) t.focus({ preventScroll: true });
    });
  }
  /** Väntar tills ingen dialog är öppen (Bootstrap flyttar annars fokus tillbaka till knappen som öppnade dialogen) och kör sedan `fn`. */
  function afterModals(fn) {
    let tries = 0;
    const tick = () => { if (document.querySelector('.modal.show') && tries++ < 30) { setTimeout(tick, 60); return; } fn(); };
    setTimeout(tick, 0);
  }

  // Blockets namn i texter ("Rubrik: Välkommen"): typens namn, och för text/rubrik de första orden.
  function blockTitle(b) { const s = summaryOf(b); return blockName(b) + (s && (b.type === 'heading' || b.type === 'text') ? ': ' + (s.length > 30 ? s.slice(0, 30) + '…' : s) : ''); }

  // ── Väljaren (modal) ────────────────────────────────────────────────────────
  /** Svarar { types: [...], zone } eller null (avbrutet). `slotOnly` = bara blocken som får ligga före/efter en systemsida; `zone` = Hem: välj plats på sidan. */
  function openChooser(ctx) {
    return new Promise(resolve => {
      const retired = new Set((S.st && S.st.retiredBlocks) || []);
      const types = blockTypes().filter(t => t.group >= 0 && !retired.has(t.type) && (!ctx.slotOnly || t.slot));
      const tasks = ctx.slotOnly ? [] : blockTasks().filter(t => t.blocks.every(b => types.some(x => x.type === b)));
      let sel = null, result = null;   // sel = { kind: 'block'|'task', key }
      const phone = window.matchMedia && window.matchMedia('(max-width: 767.98px)').matches;
      const tid = 'hsModalT' + (++modalSeq);
      const search = h('input', { type: 'search', class: 'form-control', id: 'hsChSearch', 'aria-label': 'Sök block', placeholder: 'Sök, till exempel "nyheter" eller "bild"', autocomplete: 'off' });
      const list = h('div', { class: 'hs-chooser-list mt-3', id: 'hsChList' });
      const addBtn = h('button', { type: 'button', class: 'btn btn-primary', disabled: true }, 'Lägg till');
      const zoneSel = ctx.zone ? h('select', { class: 'form-select form-select-sm w-auto', id: 'hsChZone', 'aria-label': 'Plats på sidan' }, ['full', 'main', 'side'].map(z => h('option', { value: z }, ZONE_LABEL[z]))) : null;
      const unavailable = t => blockUnavailable(t);

      const chosenTypes = () => !sel ? [] : sel.kind === 'task' ? blockTasks().find(t => t.key === sel.key).blocks : [sel.key];
      const syncBtn = () => {
        const ts = chosenTypes();
        addBtn.disabled = !ts.length;
        addBtn.textContent = ts.some(t => typeInfo(t).needsImage) ? 'Nästa: välj bild' : 'Lägg till';
      };
      const pick = (kind, key) => {
        sel = { kind, key };
        list.querySelectorAll('label.hs-choice').forEach(l => l.classList.toggle('active', l.dataset.pick === kind + ':' + key));
        syncBtn();
      };
      const choice = (kind, key, icon, label, desc, why, note) => {
        const did = 'hsChD' + kind + key;
        const input = h('input', { type: 'radio', name: 'hsch', value: kind + ':' + key, 'aria-disabled': why ? 'true' : null, 'aria-describedby': why ? did : null, checked: sel && sel.kind === kind && sel.key === key,
          onchange: e => { if (why) { e.target.checked = false; return; } pick(kind, key); } });
        return h('label', { class: 'hs-choice' + (why ? ' hs-choice-off' : '') + (sel && sel.kind === kind && sel.key === key ? ' active' : ''), 'data-pick': kind + ':' + key, 'data-add-block': kind === 'block' ? key : null, 'data-add-task': kind === 'task' ? key : null },
          input, h('i', { class: 'bi ' + icon + ' me-1', 'aria-hidden': 'true' }), h('strong', {}, label),
          h('span', { class: 'hs-sub' + (why ? ' fst-italic' : ''), id: did }, why || desc), !why && note ? h('span', { class: 'hs-sub' }, note) : null);
      };
      const paint = () => {
        const q = search.value.trim().toLowerCase();
        const match = (...s) => !q || s.join(' ').toLowerCase().includes(q);
        const nodes = [];
        const ts = tasks.filter(t => match(t.title, t.description));
        if (ts.length) nodes.push(h('section', { class: 'mb-3', 'aria-labelledby': 'hsChTasks' }, h('h3', { class: 'h6 text-muted mb-2', id: 'hsChTasks' }, 'Vad vill du göra?'),
          ts.map(t => choice('task', t.key, t.icon, t.title, t.description, '', t.note))));
        const groups = blockGroups().map((title, gi) => {
          const items = types.filter(t => t.group === gi && match(t.label, t.description, title));
          if (!items.length) return null;
          const id = 'hsChG' + gi;
          return h('details', { class: 'mb-2 hs-chooser-group', open: (!phone || q) ? true : null }, h('summary', { class: 'fw-semibold py-1' }, h('h3', { class: 'h6 d-inline mb-0', id }, title)),
            h('div', { class: 'mt-1', role: 'group', 'aria-labelledby': id }, items.map(t => choice('block', t.type, t.icon, t.label, t.description, unavailable(t)))));
        }).filter(Boolean);
        if (groups.length) nodes.push(h('section', { 'aria-labelledby': 'hsChAll' }, h('h3', { class: 'h6 text-muted mb-2', id: 'hsChAll' }, ts.length || !q ? 'Alla block' : 'Block'), groups));
        if (!nodes.length) nodes.push(h('p', { class: 'text-muted' }, 'Inget block matchar "' + search.value.trim() + '".'));
        list.replaceChildren(...nodes);
      };
      search.addEventListener('input', paint);

      const finish = () => { const ts = chosenTypes(); if (!ts.length) return; result = { types: ts, zone: zoneSel ? zoneSel.value : null }; bs.hide(); };
      addBtn.addEventListener('click', finish);
      search.addEventListener('keydown', e => {
        if (e.key !== 'Enter') return;
        e.preventDefault();
        if (!sel) { const only = [...list.querySelectorAll('label.hs-choice:not(.hs-choice-off)')]; if (only.length === 1) { const [k, v] = only[0].dataset.pick.split(':'); pick(k, v); } }
        finish();
      });

      const el = h('div', { class: 'modal fade', tabindex: '-1', 'aria-labelledby': tid, id: 'hsChooser' },
        h('div', { class: 'modal-dialog modal-dialog-centered modal-dialog-scrollable modal-lg modal-fullscreen-sm-down' }, h('div', { class: 'modal-content' },
          h('div', { class: 'modal-header' }, h('div', {}, h('h2', { class: 'modal-title h5', id: tid }, 'Lägg till på sidan'), h('div', { class: 'small text-muted', id: 'hsChWhere' }, ctx.subtitle)),
            h('button', { type: 'button', class: 'btn-close', 'data-bs-dismiss': 'modal', 'aria-label': 'Stäng' })),
          h('div', { class: 'modal-body' }, search,
            zoneSel ? h('div', { class: 'd-flex align-items-center gap-2 mt-2 small' }, h('label', { for: 'hsChZone' }, 'Plats på sidan'), zoneSel) : null, list),
          h('div', { class: 'modal-footer' }, h('button', { type: 'button', class: 'btn btn-secondary', 'data-bs-dismiss': 'modal' }, 'Avbryt'), addBtn))));
      document.body.append(el);
      const bs = new bootstrap.Modal(el);
      el.addEventListener('shown.bs.modal', () => search.focus());
      el.addEventListener('hidden.bs.modal', () => { el.remove(); resolve(result); });   // svaret först när dialogen är helt stängd, så att fokus inte rycks tillbaka av Bootstrap
      paint();
      bs.show();
    });
  }

  // Närmaste granne åt det håll i samma grupp (en slots före/efter är två egna listor); fri sida: bara nästa.
  function neighbour(i, d) {
    for (let j = i + d; j >= 0 && j < S.blocks.length; j += d) if (S.kind !== 'slots' || S.blocks[j].pos === S.blocks[i].pos) return j;
    return -1;
  }
  const indexOfId = id => S.blocks.findIndex(b => b.id === id);
  /** Flyttar blocket inom sin grupp (hela sidan, eller före/efter på en systemsida): 'up' | 'down' | 'first' | 'last'. Ritar om, säger var det hamnade och behåller fokus på kortets Åtgärder. */
  function moveBlock(id, how) {
    const i = indexOfId(id); if (i < 0) return;
    const grp = S.blocks.map((b, k) => [b, k]).filter(([b]) => S.kind !== 'slots' || b.pos === S.blocks[i].pos);
    const at = grp.findIndex(([b]) => b.id === id);
    const to = how === 'up' ? at - 1 : how === 'down' ? at + 1 : how === 'first' ? 0 : grp.length - 1;
    if (to < 0 || to >= grp.length || to === at) return;
    const order = grp.map(([b]) => b); const [x] = order.splice(at, 1); order.splice(to, 0, x);
    pushUndo('flytt av ' + blockName(x));
    grp.forEach(([, k], n) => { S.blocks[k] = order[n]; });
    S.justAdded = null; markDirty(); drawBlocks();
    const now = grp.length > 1 ? to + 1 : 1;
    toast('Flyttade ' + blockTitle(x) + ', nu nummer ' + now + ' av ' + grp.length + '.', true);
    afterModals(() => { const t = body.querySelector('[data-block-id="' + id + '"] .hs-menu-toggle'); if (t) { t.scrollIntoView({ block: 'nearest', behavior: reducedMotion() ? 'auto' : 'smooth' }); t.focus({ preventScroll: true }); } });
  }
  /** Tar bort blocket direkt (ingen spärrfråga: Ångra finns i fältet, i Åtgärder och i meddelandet). Fokus går till nästa kort, annars platsknappen där det satt. */
  function removeBlock(id) {
    const i = indexOfId(id); if (i < 0) return;
    const b = S.blocks[i], label = blockTitle(b);
    pushUndo('borttagning av ' + blockName(b));
    S.blocks.splice(i, 1);
    if (S.openId === id) S.openId = null;
    S.justAdded = null; markDirty(); drawBlocks();
    toast('Tog bort ' + label + '.', true);
    afterModals(() => {
      const next = S.blocks[i] || null;
      const t = next ? body.querySelector('[data-block-id="' + next.id + '"] .hs-type') : body.querySelector('[data-insert-at="' + Math.max(0, i) + '"], #hsEmpty button');
      if (t) t.focus({ preventScroll: true });
    });
  }
  function toggleOpen(id) {
    S.justAdded = null;
    const wasOpen = S.openId === id;
    S.openId = wasOpen ? null : id;
    S.focusAfterRte = wasOpen ? null : id;
    drawBlocks();
    if (!wasOpen) revealCard(id, true);
    else afterModals(() => { const t = body.querySelector('[data-block-id="' + id + '"] .hs-open-toggle'); if (t) t.focus({ preventScroll: true }); });
  }

  // ── Ångra och meddelanden ───────────────────────────────────────────────────
  // Ångra gäller blocken (lägga till, ta bort, flytta, mallar): en lokal logg med högst 30 steg som töms när redigeraren lämnas. Texten inne i ett textblock ångras av textredigeraren själv.
  function pushUndo(label) {
    S.undo.push({ label, blocks: JSON.parse(JSON.stringify(S.blocks)), openId: S.openId });
    if (S.undo.length > 30) S.undo.shift();
    paintUndo();
  }
  function undoLast() {
    const u = S.undo.pop(); if (!u) return;
    clearEditors();
    S.blocks = u.blocks; S.openId = S.blocks.some(b => b.id === u.openId) ? u.openId : null; S.justAdded = null;
    for (const b of S.blocks) if (b.type !== 'panel') b.visibility = Math.max(b.visibility, minVisFor(b.type));   // sidan kan ha blivit strängare sedan dess
    markDirty(); drawBlocks(); paintUndo();
    toast('Ångrade ' + u.label + '.', false);
  }
  function paintUndo() {
    const b = document.getElementById('hsUndo'); if (!b) return;
    const last = S.undo[S.undo.length - 1];
    b.disabled = !last;
    b.title = last ? 'Ångra ' + last.label : 'Det finns inget att ångra';
    b.setAttribute('aria-label', last ? 'Ångra ' + last.label : 'Ångra (inget att ångra)');
  }
  /** Ett kort meddelande i det klibbiga fältet (en live-region som finns i sidan innan texten byts, så att en skärmläsare läser upp den). Med Ångra-knapp när något gått att ångra. */
  function toast(text, withUndo) {
    const el = document.getElementById('hsToast'); if (!el) return;
    clearTimeout(S.toastTimer);
    el.replaceChildren(h('span', { class: 'hs-toast-text' }, text),
      withUndo && S.undo.length ? h('button', { type: 'button', class: 'btn btn-sm btn-outline-primary ms-2', id: 'hsToastUndo', onclick: () => undoLast() }, 'Ångra') : null);
    el.classList.add('on');
    S.toastTimer = setTimeout(() => { el.classList.remove('on'); el.replaceChildren(); }, 12000);
  }

  /** Listan: platsknapp, kort, platsknapp, kort … (ett kort är stängt tills man väljer Ändra; högst ett är öppet). */
  function cardNodes(filterPos) {
    const nodes = [], items = S.blocks.map((b, i) => [b, i]).filter(([b]) => !filterPos || b.pos === filterPos);
    const groupIndex = k => k < items.length ? items[k][1] : items.length ? items[items.length - 1][1] + 1 : (filterPos === 'before' ? 0 : S.blocks.length);
    nodes.push(insertSlot({ index: groupIndex(0), pos: filterPos }));
    items.forEach(([b], k) => {
      nodes.push(b.type === 'panel' ? panelCard(b) : blockCard(b));
      nodes.push(insertSlot({ index: groupIndex(k + 1), pos: filterPos, after: b }));
    });
    return nodes;
  }

  function drawBlocks() {
    pruneEditors(S.openId); S.refreshWho && S.refreshWho();   // blocken (och deras nivåer) styr tabellen i Vem ser sidan?; bara det öppna blockets textredigerare lever kvar
    const el = document.getElementById('hsBlocks'); if (!el) return;
    if (S.kind === 'slots') {
      const group = (pos, title, help) => {
        const has = S.blocks.some(b => b.pos === pos);
        return h('section', { class: 'mb-4', 'aria-label': title },
          h('h2', { class: 'h5' }, title), h('p', { class: 'text-muted small' }, help),
          ...(has ? cardNodes(pos) : [h('div', { class: 'hs-card p-3 text-center text-muted mb-2' }, 'Ingen text här än.'), insertSlot({ index: pos === 'before' ? 0 : S.blocks.length, pos })]));
      };
      el.replaceChildren(group('before', 'Före sidans innehåll', 'Visas överst på sidan.'),
        h('div', { class: 'hs-card hs-removed p-2 px-3 mb-4 text-muted small' }, h('i', { class: 'bi bi-layout-text-window me-2', 'aria-hidden': 'true' }), 'Sidans vanliga innehåll visas här. Det ändras inte.'),
        group('after', 'Efter sidans innehåll', 'Visas längst ner på sidan.'));
    } else if (S.kind === 'converted') {
      // En omgjord sida: paneler (kort med Åtgärder) och egna block i EN lista; borttagna paneler som grå rader med Lägg tillbaka.
      el.replaceChildren(...cardNodes(), ...removedPanels().map(removedRow));
    } else {
      el.replaceChildren(...(S.blocks.length ? cardNodes() : [emptyState()]));
    }
    // CKEditor skapas först när elementen sitter i dokumentet, och bara en gång per öppet block (det öppna blockets innehåll återanvänds).
    el.querySelectorAll('[data-rte]:not([data-rte-init])').forEach(node => { node.dataset.rteInit = '1'; initRte(node, S.blocks.find(b => b.id === node.dataset.rte)); });
    setBarHeight();
  }
  /** Det klibbiga fältets höjd (för `scroll-margin-top`): ett block som rullas fram får aldrig hamna under fältet, inte heller på telefon där fältet har flera rader. */
  function setBarHeight() { const bar = root.querySelector('.hs-bar'); if (bar) { root.style.setProperty('--hs-bar-h', bar.offsetHeight + 'px'); document.documentElement.style.scrollPaddingTop = (bar.offsetHeight + 12) + 'px'; } }
  window.addEventListener('resize', () => { if (root.querySelector('.hs-bar')) setBarHeight(); });

  /** Sidan är tom: tre snabbstarter, alla block, och färdiga sidor (som annars bara nås i guiden Ny sida). */
  function emptyState() {
    const quick = ['text', 'image', 'events'].filter(t => blockTypes().some(x => x.type === t));
    return h('div', { class: 'hs-card p-4 mb-3', id: 'hsEmpty' },
      h('h2', { class: 'h5' }, 'Sidan är tom'),
      h('p', { class: 'text-muted' }, 'Börja med ett block. Blocken visas i den ordning ni lägger dem, och ni kan lägga till fler var som helst på sidan.'),
      h('div', { class: 'd-flex flex-wrap gap-2 mb-3' },
        quick.map(t => h('button', { type: 'button', class: 'btn btn-outline-primary', 'data-quick': t, onclick: () => addBlocks([t], { index: 0 }) }, h('i', { class: 'bi ' + typeInfo(t).icon + ' me-1', 'aria-hidden': 'true' }), typeInfo(t).label)),
        h('button', { type: 'button', class: 'btn btn-primary', id: 'hsEmptyMore', onclick: e => startAdd({ index: 0 }, e.currentTarget) }, 'Fler block …')),
      h('p', { class: 'mb-0 small' }, 'Eller ', h('button', { type: 'button', class: 'btn btn-link p-0 align-baseline', id: 'hsEmptyTemplate', onclick: () => startTemplate() }, 'börja från en färdig sida'), '.'));
  }

  // ── Förhandsgranska utkastet ────────────────────────────────────────────────
  // Sparar utkastet, ber servern rita det för en vald besökare (samma renderare och åtkomstregler som besökarna möter) och visar det i en iframe utan skript och utan formulär.
  // Aldrig ensam väg: allt går att göra och läsa i redigeraren. Länkar i förhandsvisningen är döda (ingen navigering härifrån).
  async function openPreview() {
    if (S.dirty && !await saveNow()) { toast('Utkastet kunde inte sparas, så det går inte att förhandsgranska än.', false); return; }
    const theme = document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'dark' : 'light';
    let personaKey = S.visibility === 0 ? 'anon' : 'member', width = 'wide';
    const tid = 'hsModalT' + (++modalSeq);
    const who = h('select', { class: 'form-select form-select-sm w-auto', id: 'hsPvWho', 'aria-label': 'Se sidan som' });
    const notice = h('div', { class: 'small text-muted', id: 'hsPvNotice', role: 'status', 'aria-live': 'polite' });
    const frameWrap = h('div', { class: 'hs-pv-wrap' });
    const frame = h('iframe', { class: 'hs-pv-frame', id: 'hsPvFrame', title: 'Förhandsgranskning av sidan', sandbox: 'allow-same-origin', 'data-preview': '1' });
    const msg = h('div', { class: 'hs-pv-msg alert alert-secondary', hidden: true });
    const widthBtn = (key, label, icon) => h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary' + (key === width ? ' active' : ''), 'data-pv-width': key, 'aria-pressed': String(key === width),
      onclick: () => { width = key; paintWidth(); } }, h('i', { class: 'bi ' + icon + ' me-1', 'aria-hidden': 'true' }), label);
    const wBtns = [widthBtn('wide', 'Dator', 'bi-display'), widthBtn('narrow', 'Telefon', 'bi-phone')];
    const paintWidth = () => { frameWrap.classList.toggle('narrow', width === 'narrow'); wBtns.forEach(b => { const on = b.dataset.pvWidth === width; b.classList.toggle('active', on); b.setAttribute('aria-pressed', String(on)); }); };
    const openLive = h('a', { class: 'btn btn-link btn-sm', href: (S.page && S.page.url) || '#', target: '_blank', rel: 'noopener', hidden: !(S.published && S.page && S.page.url) }, 'Öppna den publicerade sidan i en ny flik');
    let seq = 0;
    async function show() {
      const mine = ++seq;
      notice.textContent = 'Ritar förhandsgranskningen …';
      const r = await api('POST', 'RenderPreview', { pageId: S.page.id, persona: personaKey, theme });
      if (mine !== seq) return;   // ett nyare val har tagit över
      if (!r.success) { frame.hidden = true; msg.hidden = false; msg.textContent = r.message || 'Förhandsgranskningen gick inte att rita.'; notice.textContent = ''; return; }
      if (!who.options.length) r.personas.forEach(p => who.append(h('option', { value: p.key }, p.label)));
      who.value = r.persona;
      const base = r.basis === 'draft' ? 'Det sparade utkastet.' : 'Sidan har inget utkast, så det här är den publicerade versionen.';
      notice.textContent = r.notice + ' ' + base + (r.hiddenPersonal ? ' ' + r.hiddenPersonal + (r.hiddenPersonal === 1 ? ' block med personuppgifter visas inte i stödläge.' : ' block med personuppgifter visas inte i stödläge.') : '');
      if (!r.document) { frame.srcdoc = ''; frame.hidden = true; msg.hidden = false; msg.textContent = r.notice; return; }   // tömmer också ramen: den förra besökarens innehåll får inte ligga kvar dolt
      msg.hidden = true; frame.hidden = false;
      frame.srcdoc = r.document;
    }
    frame.addEventListener('load', () => {   // inga länkar leder någonstans härifrån
      try { frame.contentDocument.addEventListener('click', e => { if (e.target.closest && e.target.closest('a')) e.preventDefault(); }); } catch { /* ingen åtkomst: sandlådan stänger ändå navigeringen */ }
    });
    who.addEventListener('change', () => { personaKey = who.value; show(); });
    frameWrap.append(frame);
    const el = h('div', { class: 'modal fade', tabindex: '-1', 'aria-labelledby': tid, id: 'hsPvModal' },
      h('div', { class: 'modal-dialog modal-dialog-centered modal-xl modal-fullscreen-md-down modal-dialog-scrollable' }, h('div', { class: 'modal-content' },
        h('div', { class: 'modal-header' }, h('h2', { class: 'modal-title h5', id: tid }, 'Förhandsgranska utkastet'), h('button', { type: 'button', class: 'btn-close', 'data-bs-dismiss': 'modal', 'aria-label': 'Stäng' })),
        h('div', { class: 'modal-body' },
          h('div', { class: 'd-flex flex-wrap align-items-center gap-2 mb-2' }, h('label', { for: 'hsPvWho', class: 'small fw-semibold' }, 'Se sidan som'), who,
            h('span', { class: 'btn-group ms-auto', role: 'group', 'aria-label': 'Bredd' }, wBtns)),
          notice, msg, frameWrap),
        h('div', { class: 'modal-footer' }, openLive, h('button', { type: 'button', class: 'btn btn-primary', 'data-bs-dismiss': 'modal' }, 'Stäng')))));
    document.body.append(el);
    const bs = new bootstrap.Modal(el);
    const opener = document.activeElement;   // Bootstrap lämnar bara tillbaka fokus för dialoger som öppnas med data-attribut: här görs det själv
    el.addEventListener('hidden.bs.modal', () => { el.remove(); if (opener && opener.isConnected && opener !== document.body) opener.focus(); });
    paintWidth();
    bs.show();
    show();
  }

  /** Lägger in en färdig sidas block i en tom sida (samma mallar som guiden Ny sida). */
  async function startTemplate() {
    let chosen = 'welcome';
    const opts = readyTemplates().map(([k, t, s]) => h('label', { class: 'hs-choice' + (k === chosen ? ' active' : ''), 'data-template': k },
      h('input', { type: 'radio', name: 'hsTpl', checked: k === chosen, onchange: e => { chosen = k; e.target.closest('.modal').querySelectorAll('label.hs-choice').forEach(l => l.classList.toggle('active', l.dataset.template === k)); } }),
      h('strong', {}, t), h('span', { class: 'hs-sub' }, s)));
    const ok = await modal('Börja från en färdig sida', [h('p', { class: 'text-muted small' }, 'Blocken läggs in på sidan och går att ändra, flytta och ta bort.'), ...opts],
      [{ label: 'Avbryt', value: false }, { label: 'Använd den här', value: true, cls: 'btn-primary' }], true);
    if (ok !== true) return;
    const blocks = templateBlocks(chosen, S.visibility);
    if (!blocks.length) return;
    pushUndo('den färdiga sidan');
    S.blocks.push(...blocks);
    S.justAdded = blocks.map(b => b.id); S.openId = null;
    markDirty(); drawBlocks();
    toast('Lade in ' + blocks.length + (blocks.length === 1 ? ' block' : ' block') + ' från den färdiga sidan.', true);
    revealCard(blocks[0].id, true);
  }

  // ── Omgjorda sidor: panelkort ────────────────────────────────────────────────
  const ZONE_LABEL = { main: 'Huvudkolumnen', side: 'Sidokolumnen', full: 'Hela bredden' };
  const catOf = key => (S.panelCat || []).find(c => c.key === key) || { key, name: key, description: '', zone: 'full', mustKeep: true, systemOnly: false, level: 0, personalData: false };
  /** Panelerna som får finnas på sidan men inte ligger där nu (och som ägaren kan lägga tillbaka). */
  function removedPanels() {
    const present = new Set(S.blocks.filter(b => b.type === 'panel').map(b => b.data.key));
    return (S.panelCat || []).filter(c => !c.systemOnly && !present.has(c.key));
  }
  function setZone(b, zone) {
    pushUndo('flytt av ' + blockName(b));
    if (zone === 'full') delete b.data.zone; else b.data.zone = zone;   // "Hela bredden" lagras aldrig (ingen zon)
    markDirty(); drawBlocks();
    toast(blockName(b) + ' ligger nu i ' + ZONE_LABEL[zone].toLowerCase() + '.', true);
  }
  function restorePanel(c) {
    // Tillbaka på sin vanliga plats: före den första panel som ligger efter den i standardordningen, annars sist.
    const order = (S.panelCat || []).map(x => x.key), mine = order.indexOf(c.key);
    let at = S.blocks.findIndex(b => b.type === 'panel' && order.indexOf(b.data.key) > mine);
    if (at < 0) at = S.blocks.length;
    pushUndo('att ' + c.name + ' lades tillbaka');
    const nb = { id: newId(), type: 'panel', visibility: Math.max(c.level, S.visibility), data: { key: c.key, zone: c.zone } };
    S.blocks.splice(at, 0, nb);
    S.justAdded = [nb.id];
    markDirty(); drawBlocks();
    toast('Lade tillbaka ' + c.name + '.', true);
    revealCard(nb.id, true);
  }
  function removedRow(c) {
    return h('div', { class: 'hs-card hs-removed p-2 px-3 mb-3 d-flex align-items-center gap-2 flex-wrap text-muted' },
      h('span', { class: 'me-auto' }, c.name + ' är borttagen.'),
      h('button', { class: 'btn btn-sm btn-outline-secondary', onclick: () => restorePanel(c) }, 'Lägg tillbaka'));
  }
  /** Blockets namn i texter: en panels eget namn, annars typens namn ur registret. */
  const blockName = b => b.type === 'panel' ? catOf(b.data.key).name : typeInfo(b.type).label;
  const pageLevelReason = () => 'Sidan är bara öppen för ' + visLabel(S.visibility).toLowerCase() + ', så blocket kan inte vara öppnare.';

  // ── Kort: panel och block har samma huvud (namn + sammanfattning, märken, Åtgärder) ─────────────────────────
  function cardHead(b, i, o) {
    return h('div', { class: 'hs-block-head' },
      h('div', { class: 'hs-block-title' }, o.thumb || h('i', { class: 'bi ' + o.icon + ' hs-block-icon', 'aria-hidden': 'true' }),
        h('div', { class: 'hs-block-text' }, h('span', { class: 'hs-type', tabindex: '-1' }, o.name), h('div', { class: 'hs-summary' + (o.summaryMuted ? ' text-muted fst-italic' : ' text-muted') }, o.summary))),
      h('div', { class: 'hs-block-actions' }, o.open || null, o.menu),
      h('div', { class: 'hs-block-marks' }, o.marks));
  }

  function panelCard(b) {
    const c = catOf(b.data.key), isHome = S.pageKey === 'home', i = indexOfId(b.id);
    // Aldrig öppnare än panelen är idag, än sidan, och aldrig "Alla" för personuppgifter (servern prövar samma sak).
    const min = Math.max(c.level, S.visibility, c.personalData ? 1 : 0);
    const marks = [];
    if (c.systemOnly) marks.push(stateMark('off', 'Visas bara för den som inte är inloggad'));
    else marks.push(levelPicker(b, c.name, {
      min,
      // "Visa publikt": en panel som får visas för alla kan väljas som Alla även när sidan inte är det än (då öppnas sidan också, efter en bekräftelse).
      extraAllowed: v => v === 0 && c.publicAllowed,
      reason: v => c.personalData && v < 1 ? 'Innehåller personuppgifter och kan inte visas för alla.' : v < c.level ? '"' + c.name + '" är inte öppnare än så här idag.' : pageLevelReason(),
      onPick: async v => {
        if (v === 0) {
          if (!await confirmPublic(c)) return false;
          b.visibility = 0;
          if (S.visibility > 0) { S.visibility = 0; markDirty(); renderEditor(); return false; }   // sidan måste själv vara öppen för alla, annars syns panelen inte (hela vyn ritas om)
        } else b.visibility = v;
        markDirty(); S.refreshWho && S.refreshWho();
        toast(c.name + ' syns nu för ' + visLabel(v).toLowerCase() + '.', false);
        return true;
      },
    }));
    marks.push(stateMark('off', 'Hämtas automatiskt', 'Innehållet hämtas vid varje besök.'));
    if (c.personalData) marks.push(stateMark('off', 'Personuppgifter', 'Visar personuppgifter och kan aldrig vara öppen för alla.'));
    if (isHome) marks.push(stateMark('own', ZONE_LABEL[b.data.zone] || ZONE_LABEL.full));
    const menu = rowMenu([
      ['Flytta upp', () => moveBlock(b.id, 'up'), { disabled: neighbour(i, -1) < 0 }],
      ['Flytta ner', () => moveBlock(b.id, 'down'), { disabled: neighbour(i, 1) < 0 }],
      ['Flytta först', () => moveBlock(b.id, 'first'), { disabled: neighbour(i, -1) < 0 }],
      ['Flytta sist', () => moveBlock(b.id, 'last'), { disabled: neighbour(i, 1) < 0 }],
      ...(isHome ? ['full', 'main', 'side'].filter(z => z !== (b.data.zone || 'full')).map(z => ['Flytta till ' + ZONE_LABEL[z].toLowerCase(), () => setZone(b, z)]) : []),
      '-', ['Lägg till block under', () => startAdd({ index: i + 1, after: b }, () => body.querySelector('[data-block-id="' + b.id + '"] .hs-menu-toggle'))],
      c.mustKeep ? null : '-', c.mustKeep ? null : ['Ta bort', () => removeBlock(b.id), { danger: true }]], 'Åtgärder för ' + c.name + ', ' + blockNo(i));
    const hl = S.justAdded && S.justAdded.includes(b.id);
    return h('div', { class: 'hs-block hs-panelcard' + (hl ? ' hs-new' : ''), 'data-block-id': b.id, 'data-block-type': 'panel', 'data-panel-key': b.data.key },
      cardHead(b, i, { icon: 'bi-grid-1x2', name: c.name, summary: (c.description || '') + (c.mustKeep ? ' Kan flyttas men inte tas bort.' : ''), marks, menu }));
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

  // Signaturen (avatar, namn, roll) i texten: se site-signature.js. Man skriver bara under som sig själv; servern prövar det vid sparning.
  const SIG = window.HpskSignature ? window.HpskSignature.create({ get: ids => api('GET', 'Signatures', { ids: ids.join(',') }) }) : null;

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
      extraPlugins: SIG ? [SiteImageWidth, SIG.plugin] : [SiteImageWidth],
    }).then(ed => {
      ed.setData(block.data.html || '');
      const sigUi = SIG ? SIG.controls(h) : null;
      node.parentNode.insertBefore(h('div', { class: 'mb-2 d-flex flex-wrap align-items-center gap-1' }, h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', onclick: () => insertImage(ed) },
        h('i', { class: 'bi bi-image me-1', 'aria-hidden': 'true' }), 'Infoga bild'), sigUi && sigUi.node, widthBar(() => ed)), node);
      if (sigUi) sigUi.bind(ed, block.data.html || '');
      ed.model.document.on('change:data', () => { block.data.html = ed.getData(); markDirty(); });
      ed._blockId = block.id;   // pruneEditors behåller det öppna blockets textredigerare när listan ritas om
      S.editors.push(ed);
      if (S.focusAfterRte === block.id) { S.focusAfterRte = null; ed.editing.view.focus(); }   // ett nytt eller öppnat textblock får fokus först när textredigeraren är klar
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

  // ── Blockkortet: stängt visar det vad det är (namn, sammanfattning, märken); Ändra öppnar fälten. ────────────────────
  const plainText = html => { const d = new DOMParser().parseFromString(html || '', 'text/html'); return (d.body.textContent || '').replace(/\s+/g, ' ').trim(); };
  const plural = (n, one, many) => n + ' ' + (n === 1 ? one : many);
  /** Det som skrivs ut på kortet: texten i ett textblock, rubriken, antalet länkar/dokument/bilder, annars vad blocket visar. */
  function summaryOf(b) {
    const d = b.data || {};
    switch (b.type) {
      case 'text': return plainText(d.html) || (/<hpsk-signatur\b/i.test(d.html || '') ? 'Signatur' : '');
      case 'heading': return (d.text || '').trim();
      case 'image': return String(d.alt || d.caption || '').trim();
      case 'gallery': return plural((d.items || []).length, 'bild', 'bilder') + (d.title ? ' · ' + d.title : '');
      case 'links': return plural((d.items || []).filter(it => it.label || it.url).length, 'länk', 'länkar');
      case 'documents': return plural((d.refs || []).length, 'dokument', 'dokument') + (d.title ? ' · ' + d.title : '');
      case 'signup': return d.title || 'Anmälan';
      case 'pagelink': { const p = (S.st.systemPages || []).find(x => x.key === d.key); return d.label || (p ? 'Knapp till ' + p.label : 'Knapp till en sida'); }
      case 'events': case 'news': case 'competitions': case 'staff-seeking': return 'Visar upp till ' + (d.count || 5) + (d.type === 'Möte' ? ' · bara möten' : '');
      default: return d.title || '';
    }
  }
  const cardSummary = b => {
    const s = summaryOf(b);
    if (b.type === 'text') return { text: s ? (s.length > 140 ? s.slice(0, 140) + '…' : s) : 'Ingen text än.', muted: !s };
    if (b.type === 'heading') return { text: s || 'Ingen rubrik än.', muted: !s };
    if (b.type === 'image') return { text: s || 'Bilden saknar beskrivning.', muted: !s };
    if (['gallery', 'links', 'documents', 'signup', 'pagelink', 'events', 'news', 'competitions', 'staff-seeking'].includes(b.type)) {
      const info = typeInfo(b.type);
      return { text: s + (['events', 'news', 'competitions', 'staff-seeking'].includes(b.type) ? '. ' + info.description : ''), muted: false };
    }
    return { text: typeInfo(b.type).description, muted: false };
  };
  /** Har blocket inget innehåll än? Då visas det inte för besökarna förrän det har något (märket "Tomt"). */
  function isEmptyBlock(b) {
    const d = b.data || {};
    switch (b.type) {
      case 'text': return !plainText(d.html) && !/<img\b/i.test(d.html || '') && !/<hpsk-signatur\b/i.test(d.html || '');
      case 'heading': return !String(d.text || '').trim();
      case 'links': return !(d.items || []).some(it => String(it.label || '').trim() && String(it.url || '').trim());
      case 'documents': return !(d.refs || []).length;
      case 'gallery': return !(d.items || []).length;
      default: return false;
    }
  }

  const emptyMark = () => h('span', { class: 'hs-state wait', 'data-mark-empty': '1', title: 'Blocket har inget innehåll än. Det sparas och visas för besökarna först när det har något.' }, 'Tomt');
  /** Medan man skriver i det öppna kortet följer sammanfattningen och märket "Tomt" med (ingen omritning: textredigerare och fokus lever kvar). */
  function paintCardLive() {
    const b = S.openId && S.blocks.find(x => x.id === S.openId); if (!b || b.type === 'panel') return;
    const card = body.querySelector('[data-block-id="' + b.id + '"]'); if (!card) return;
    const sum = cardSummary(b), el = card.querySelector('.hs-summary');
    if (el) { el.textContent = sum.text; el.classList.toggle('fst-italic', !!sum.muted); }
    const marks = card.querySelector('.hs-block-marks'), has = marks && marks.querySelector('[data-mark-empty]');
    const empty = isEmptyBlock(b) || savableBlock(b) === null;
    if (marks && empty && !has) marks.querySelector('.dropdown') ? marks.querySelector('.dropdown').after(emptyMark()) : marks.append(emptyMark());
    else if (!empty && has) has.remove();
  }

  function blockCard(b) {
    const info = typeInfo(b.type), i = indexOfId(b.id), open = S.openId === b.id, min = minVisFor(b.type);
    const floor = ((S.st && S.st.blockFloors) || {})[b.type] || 0;
    const isHome = S.kind === 'converted' && S.pageKey === 'home';
    const marks = [levelPicker(b, info.label, {
      min,
      reason: v => v < floor ? 'Det här blocket visar personuppgifter och kan inte vara öppnare än ' + visLabel(floor).toLowerCase() + '.' : pageLevelReason(),
      onPick: v => { b.visibility = v; markDirty(); S.refreshWho && S.refreshWho(); toast(info.label + ' syns nu för ' + visLabel(v).toLowerCase() + '.', false); return true; },
    })];
    if (isEmptyBlock(b) || savableBlock(b) === null) marks.push(emptyMark());
    if (floor > 0) marks.push(stateMark('off', 'Personuppgifter', 'Visar personuppgifter och kan aldrig vara öppet för alla.'));
    if (info.live) marks.push(stateMark('off', 'Hämtas automatiskt', 'Innehållet hämtas vid varje besök.'));
    if (isHome) marks.push(stateMark('own', ZONE_LABEL[b.data.zone] || ZONE_LABEL.full));
    const addUnder = () => startAdd({ index: i + 1, pos: b.pos, after: b }, () => body.querySelector('[data-block-id="' + b.id + '"] .hs-menu-toggle'));
    const menu = rowMenu([
      ['Flytta upp', () => moveBlock(b.id, 'up'), { disabled: neighbour(i, -1) < 0 }],
      ['Flytta ner', () => moveBlock(b.id, 'down'), { disabled: neighbour(i, 1) < 0 }],
      ['Flytta först', () => moveBlock(b.id, 'first'), { disabled: neighbour(i, -1) < 0 }],
      ['Flytta sist', () => moveBlock(b.id, 'last'), { disabled: neighbour(i, 1) < 0 }],
      ...(isHome ? ['full', 'main', 'side'].filter(z => z !== (b.data.zone || 'full')).map(z => ['Flytta till ' + ZONE_LABEL[z].toLowerCase(), () => setZone(b, z)]) : []),
      '-', ['Lägg till block under', addUnder],
      '-', ['Ta bort', () => removeBlock(b.id), { danger: true }]], 'Åtgärder för ' + blockTitle(b) + ', ' + blockNo(i));
    const openBtn = h('button', { type: 'button', class: 'btn btn-sm hs-open-toggle ' + (open ? 'btn-primary' : 'btn-outline-primary'), 'aria-expanded': String(open), 'aria-controls': 'hsBody' + b.id,
      'aria-label': (open ? 'Klar med ' : 'Ändra ') + blockTitle(b) + ', ' + blockNo(i), onclick: () => toggleOpen(b.id) }, open ? 'Klar' : 'Ändra');
    const sum = cardSummary(b);
    const thumb = b.type === 'image' && b.data.ref ? h('img', { class: 'hs-thumb', src: imgUrl(b.data.ref) + '&w=320', alt: '', loading: 'lazy' }) : null;
    let bodyEl = null;
    if (open) {
      bodyEl = S.bodyCache.get(b.id);   // det öppna blockets innehåll (och dess textredigerare) återanvänds när listan ritas om: fokus och markör finns kvar
      if (!bodyEl) { bodyEl = h('div', { class: 'hs-block-body', id: 'hsBody' + b.id }, blockBody(b)); S.bodyCache.set(b.id, bodyEl); }
    }
    const hl = S.justAdded && S.justAdded.includes(b.id);
    return h('div', { class: 'hs-block' + (open ? ' open' : '') + (hl ? ' hs-new' : ''), 'data-block-id': b.id, 'data-block-type': b.type },
      cardHead(b, i, { icon: info.icon, thumb, name: info.label, summary: sum.text, summaryMuted: sum.muted, marks, menu, open: openBtn }), bodyEl);
  }

  // Etiketten kopplas till fältet (for/id), så att en skärmläsare säger vad fältet är och ett klick på etiketten går till fältet.
  let fieldSeq = 0;
  function field(label, input, help) {
    if (input && input.nodeType === 1 && /^(INPUT|SELECT|TEXTAREA)$/.test(input.tagName) && !input.id) input.id = 'hsf' + (++fieldSeq);
    return h('div', { class: 'mb-2' }, h('label', { class: 'form-label small fw-semibold mb-1', for: input && input.nodeType === 1 ? input.id : null }, label), input, help ? h('div', { class: 'form-text' }, help) : null);
  }
  // Datumfält: sajtens vanliga svenska datumväljare (flatpickr + planering-dates.js: skriv "16 maj 2027" eller välj i kalendern). Värdet lagras alltid som åååå-mm-dd;
  // ett datum som inte går att läsa lagras som det står, så att servern avvisar det i stället för att tyst tappa det.
  function dateInput(obj, key) {
    const inp = h('input', { class: 'form-control', type: 'text', value: obj[key] || '', autocomplete: 'off' });
    const set = () => { const v = window.hpskPlParseDate ? window.hpskPlParseDate(inp.value) : inp.value; obj[key] = v === null ? inp.value.trim() : v; markDirty(); };
    inp.addEventListener('input', set); inp.addEventListener('change', set);
    if (window.hpskPlDatePicker) { window.hpskPlDatePicker(inp); S.editors.push({ destroy: () => { if (inp._flatpickr) inp._flatpickr.destroy(); } }); }
    return inp;
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
      case 'about': return [field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        h('p', { class: 'text-muted small mb-0' }, 'Visar ' + (IS_REGION ? 'kretsens' : 'klubbens') + ' presentationstext. Texten skriver ni under ' + (IS_REGION ? 'Kretsens' : 'Klubbens') + ' uppgifter och den hämtas vid varje besök, så ändrar ni den där ändras den här också.')];
      case 'news': return [field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        field('Så många nyheter', bind(h('select', { class: 'form-select w-auto' }, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map(n => h('option', { value: n, selected: n === (d.count || 5) ? true : null }, n))), d, 'count', v => parseInt(v, 10)),
          'Visar de senaste nyheterna ni skrivit under Nyheter (menyn), med en länk till alla. Varje nyhet har sin egen nivå (vem som får läsa den); den som inte får läsa en nyhet ser den inte i listan.'),
        h('div', { class: 'form-check' },
          (() => { const c = h('input', { type: 'checkbox', class: 'form-check-input', id: 'hsNwEx' + b.id, checked: d.showExcerpt !== false }); c.addEventListener('change', () => { d.showExcerpt = c.checked; markDirty(); }); return c; })(),
          h('label', { class: 'form-check-label', for: 'hsNwEx' + b.id }, 'Visa en kort sammanfattning under rubriken')),
        h('div', { class: 'form-check' },
          (() => { const c = h('input', { type: 'checkbox', class: 'form-check-input', id: 'hsNwIm' + b.id, checked: d.showImage === true }); c.addEventListener('change', () => { d.showImage = c.checked; markDirty(); }); return c; })(),
          h('label', { class: 'form-check-label', for: 'hsNwIm' + b.id }, 'Visa omslagsbilden'))];
      case 'events': return [field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        field('Så många händelser', bind(h('select', { class: 'form-select w-auto' }, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map(n => h('option', { value: n, selected: n === (d.count || 5) ? true : null }, n))), d, 'count', v => parseInt(v, 10)),
          'Visar de närmaste kommande händelserna i kalendern.'),
        field('Vilka händelser?', bind(h('select', { class: 'form-select w-auto' }, h('option', { value: '', selected: !d.type ? true : null }, 'Alla händelser'), h('option', { value: 'Möte', selected: d.type === 'Möte' ? true : null }, 'Bara möten')), d, 'type', v => v || undefined))];
      case 'competitions': return [field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        field('Så många tävlingar', bind(h('select', { class: 'form-select w-auto' }, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map(n => h('option', { value: n, selected: n === (d.count || 5) ? true : null }, n))), d, 'count', v => parseInt(v, 10)),
          IS_REGION ? 'Visar de närmaste kommande tävlingarna i kretsen. Klubbinterna tävlingar visas aldrig här.' : 'Visar de närmaste kommande tävlingarna där klubben är arrangör. Klubbinterna tävlingar ser bara klubbens medlemmar.')];
      case 'staff-seeking': return [field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        field('Så många tävlingar', bind(h('select', { class: 'form-select w-auto' }, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map(n => h('option', { value: n, selected: n === (d.count || 5) ? true : null }, n))), d, 'count', v => parseInt(v, 10)),
          'Visar kommande tävlingar som söker funktionärer bland ' + (IS_REGION ? 'kretsens klubbar' : 'klubbens medlemmar') + ', även när en annan klubb i kretsen arrangerar. Varje tävling länkar till anmälan. Finns ingen visas inget för besökaren.')];
      case 'records': case 'ranges': case 'champions': case 'stats': return [field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        h('p', { class: 'text-muted small mb-0' }, b.type === 'records' ? 'Visar ' + (IS_REGION ? 'kretsens' : 'klubbens') + ' gällande rekord per gren och klass. Listan hämtas vid varje besök.'
          : b.type === 'champions' ? 'Visar senaste årets mästare per gren och klass, som de är inmatade under ' + (IS_REGION ? 'kretsens' : 'klubbens') + ' Mästare. Mästarna är personuppgifter, så blocket kan aldrig visas för alla.'
          : b.type === 'stats' ? (IS_REGION ? 'Visar antal klubbar, skjutbanor, serier och kommande tävlingar i kretsen.' : 'Visar antal medlemmar, skjutbanor och kommande tävlingar. Bara siffror, inga namn. Klubbinterna tävlingar räknas inte med.') + ' Siffrorna hämtas vid varje besök.'
          : (IS_REGION ? 'Listar skjutbanorna som kretsens klubbar använder.' : 'Listar klubbens skjutbanor (namn och ort), utan karta.') + ' Banor som är markerade som begränsade visas aldrig här.')];
      case 'pagelink': return [field('Vilken sida?', bind(h('select', { class: 'form-select' }, S.st.systemPages.map(p => h('option', { value: p.key, selected: p.key === d.key ? true : null }, p.label))), d, 'key')),
        field('Text på knappen (valfri)', bind(h('input', { class: 'form-control', maxlength: '80', value: d.label || '' }), d, 'label'))];
      case 'image': return imageEditor(b);
      case 'gallery': return galleryEditor(b);
      case 'signup': return signupEditor(b);
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

  // Galleriet: bilderna i ordning, var och en med beskrivning (läses upp) och bildtext (visas under bilden). Flytta med pilarna, ta bort, lägg till fler ur biblioteket.
  function galleryEditor(b) {
    const d = b.data;
    if (!Array.isArray(d.items)) d.items = [];
    const wrap = h('div');
    const draw = () => {
      const move = (i, dir) => { const j = i + dir; if (j < 0 || j >= d.items.length) return; [d.items[i], d.items[j]] = [d.items[j], d.items[i]]; markDirty(); draw(); };
      wrap.replaceChildren(
        field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        d.items.length ? h('div', { class: 'row g-3 mb-3' }, d.items.map((it, i) => h('div', { class: 'col-12 col-md-6' }, h('div', { class: 'hs-card p-2 h-100' },
          h('img', { class: 'hs-imgprev mb-2', src: imgUrl(it.ref) + '&w=320', alt: it.alt || it.caption || '', loading: 'lazy',
            onerror: e => e.target.replaceWith(h('p', { class: 'text-danger small' }, 'Bilden finns inte längre. Ta bort den ur galleriet.')) }),
          field('Beskriv bilden', bind(h('input', { class: 'form-control form-control-sm', maxlength: '200', value: it.alt || '', placeholder: 'Till exempel: Skyttar vid banan' }), it, 'alt')),
          field('Bildtext (valfri)', bind(h('input', { class: 'form-control form-control-sm', maxlength: '200', value: it.caption || '' }), it, 'caption')),
          h('div', { class: 'd-flex gap-1' },
            h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', disabled: i === 0, 'aria-label': 'Flytta bilden åt vänster', onclick: () => move(i, -1) }, h('i', { class: 'bi bi-arrow-left' })),
            h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', disabled: i === d.items.length - 1, 'aria-label': 'Flytta bilden åt höger', onclick: () => move(i, 1) }, h('i', { class: 'bi bi-arrow-right' })),
            h('button', { type: 'button', class: 'btn btn-sm btn-outline-danger ms-auto', onclick: () => { d.items.splice(i, 1); markDirty(); draw(); } }, 'Ta bort ur galleriet')))))) : h('p', { class: 'text-muted' }, 'Galleriet har inga bilder än.'),
        h('button', { type: 'button', class: 'btn btn-sm btn-outline-primary', disabled: d.items.length >= 24, onclick: async () => {
          const fs = await pickImage(null, { multi: true }); if (!fs || !fs.length) return;
          for (const f of fs) if (d.items.length < 24 && !d.items.some(x => x.ref === 'file:' + f.id)) d.items.push({ ref: 'file:' + f.id, alt: '', caption: '' });
          markDirty(); draw();
        } }, '＋ Lägg till bilder'),
        h('p', { class: 'text-muted small mt-2 mb-0' }, d.items.length + ' av högst 24 bilder. Besökaren ser miniatyrer; ett klick öppnar bilden i full storlek i en ny flik.'));
    };
    draw(); return wrap;
  }

  // Anmälan/ansökan: vad formuläret frågar efter, vilka val besökaren har och när det är öppet. Namn och e-post krävs alltid.
  function signupEditor(b) {
    const d = b.data;
    if (!Array.isArray(d.choices)) d.choices = [];
    const wrap = h('div');
    const flag = (key, label, help) => {
      const cb = h('input', { type: 'checkbox', class: 'form-check-input', id: 'hsSg' + b.id + key, checked: d[key] === true });
      cb.addEventListener('change', () => { if (cb.checked) d[key] = true; else delete d[key]; markDirty(); });
      return h('div', { class: 'form-check mb-1' }, cb, h('label', { class: 'form-check-label', for: 'hsSg' + b.id + key }, label), help ? h('div', { class: 'form-text mt-0 mb-1' }, help) : null);
    };
    const draw = () => {
      wrap.replaceChildren(
        field('Rubrik', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        field('Text ovanför formuläret (valfri)', bind(h('input', { class: 'form-control', maxlength: '600', value: d.intro || '' }), d, 'intro'), 'Till exempel vad kursen innehåller och vad den kostar.'),
        h('p', { class: 'form-label small fw-semibold mb-1 mt-3' }, 'Vad gäller anmälan? (valfritt)'),
        ...d.choices.map((c, i) => h('div', { class: 'd-flex gap-2 mb-2' },
          bind(h('input', { class: 'form-control form-control-sm', maxlength: '80', value: c, 'aria-label': 'Val ' + (i + 1), placeholder: 'Till exempel: Grundkurs pistol' }), d.choices, i),
          h('button', { type: 'button', class: 'btn btn-sm btn-outline-danger', 'aria-label': 'Ta bort valet ' + (c || (i + 1)), onclick: () => { d.choices.splice(i, 1); markDirty(); draw(); } }, 'Ta bort'))),
        h('button', { type: 'button', class: 'btn btn-sm btn-outline-primary', disabled: d.choices.length >= 10, onclick: () => { d.choices.push(''); markDirty(); draw(); } }, '＋ Lägg till ett val'),
        h('p', { class: 'text-muted small mt-1' }, 'Besökaren väljer ett av valen. Lämna tomt om ni inte behöver val. Exempel: "Grundkurs pistol" och "Jag är skytt sedan tidigare och vill bli medlem".'),
        h('p', { class: 'form-label small fw-semibold mb-1 mt-3' }, 'Vad ska besökaren fylla i?'),
        h('p', { class: 'text-muted small mb-2' }, 'Namn och e-post krävs alltid. Det ni kryssar i nedan krävs också, utom kön och meddelande som är frivilliga.'),
        flag('phone', 'Mobilnummer'), flag('address', 'Adress (adress, postnummer och ort)'), flag('gender', 'Kön (frivilligt)'), flag('message', 'Meddelande (frivilligt)'),
        flag('pnr', 'Personnummer', 'Personnummer är en känslig personuppgift. Be bara om det om ni behöver det för att registrera personen. Det tas bort automatiskt 30 dagar efter att ni har avgjort anmälan.'),
        h('div', { class: 'row g-2 mt-2' },
          h('div', { class: 'col-12 col-md-6' }, field('Öppnar (valfritt)', dateInput(d, 'opens'), 'Tomt = öppen direkt.')),
          h('div', { class: 'col-12 col-md-6' }, field('Stänger (valfritt)', dateInput(d, 'closes'), 'Sista dagen som går att anmäla sig. Tomt = alltid öppen.'))),
        field('Svar efter anmälan (valfritt)', bind(h('input', { class: 'form-control', maxlength: '300', value: d.thanks || '', placeholder: 'Tack! Vi har tagit emot din anmälan och hör av oss.' }), d, 'thanks')),
        h('div', { class: 'alert alert-info small mt-3 mb-0' }, 'Anmälningarna kommer in under ', h('strong', {}, 'Anmälningar'), ' i Hemsidan. Där godkänner, köar eller nekar ni dem. Formuläret kan fyllas i av alla som får se blocket, även den som inte är inloggad om blocket visas för alla.'));
    };
    draw(); return wrap;
  }

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
  function pickImage(currentRef, opts) {
    const multi = !!(opts && opts.multi);   // flerval: svarar med en lista bilder (för galleriet)
    return new Promise(async resolve => {
      await loadFiles();
      let chosen = null, done = false;
      const picked = new Map();
      const isSel = f => multi ? picked.has(f.id) : !!(chosen && chosen.id === f.id);
      const msg = h('div'), info = h('p', { class: 'small text-muted mb-2' }), grid = h('div', { class: 'hs-imggrid' });
      const useBtn = h('button', { type: 'button', class: 'btn btn-primary', disabled: true }, 'Använd bilden');
      const fileInp = h('input', { type: 'file', accept: 'image/jpeg,image/png,image/gif,image/webp', class: 'd-none', 'aria-label': 'Välj en bild att ladda upp', multiple: multi ? true : null });
      const upBtn = h('button', { type: 'button', class: 'btn btn-outline-primary', onclick: () => fileInp.click() }, h('i', { class: 'bi bi-upload me-1' }), 'Ladda upp en bild');
      const show = (cls, text) => msg.replaceChildren(text ? h('div', { class: 'alert alert-' + cls + ' py-2' }, text) : '');

      // Album och sökning i väljaren: filtret är bara ett urval, valda bilder följer med mellan album. "Markera hela albumet" kopierar bilderna (albumet följs inte efteråt).
      let pAlbum = 'all', pQ = '';
      const pAlbums = (S.files.albums || []);
      const albumSel = h('select', { class: 'form-select w-auto', 'aria-label': 'Visa album', id: 'hsPickAlbum' },
        h('option', { value: 'all' }, 'Alla bilder'), h('option', { value: 'none' }, 'Utan album'), pAlbums.map(a => h('option', { value: String(a.id) }, a.name + ' (' + a.fileIds.length + ')')));
      const searchInp = h('input', { type: 'search', class: 'form-control', placeholder: 'Sök på namn, beskrivning eller album', 'aria-label': 'Sök bland bilderna', id: 'hsPickSearch' });
      const wholeBtn = h('button', { type: 'button', class: 'btn btn-outline-primary btn-sm d-none', id: 'hsPickWhole' }, 'Markera hela albumet');
      const filtered = () => {
        const all = S.files.files || [], albs = S.files.albums || [];
        const inAlb = new Set(); albs.forEach(a => a.fileIds.forEach(i => inAlb.add(i)));
        const names = new Map(); albs.forEach(a => a.fileIds.forEach(i => { if (!names.has(i)) names.set(i, []); names.get(i).push(a.name); }));
        let list = all;
        if (pAlbum === 'none') list = all.filter(x => !inAlb.has(x.id));
        else if (pAlbum !== 'all') { const a = albs.find(z => String(z.id) === pAlbum); const m = new Map(all.map(x => [x.id, x])); list = a ? a.fileIds.map(i => m.get(i)).filter(Boolean) : []; }
        const q = pQ.trim().toLowerCase();
        if (q) list = list.filter(x => (x.name + ' ' + (x.description || '') + ' ' + (names.get(x.id) || []).join(' ')).toLowerCase().includes(q));
        return list;
      };
      albumSel.addEventListener('change', () => { pAlbum = albumSel.value; paint(); });
      searchInp.addEventListener('input', () => { pQ = searchInp.value; paint(); });
      wholeBtn.addEventListener('click', () => {
        const room = 24; let added = 0;
        for (const x of filtered()) { if (picked.size >= room) break; if (!picked.has(x.id)) { picked.set(x.id, x); added++; } }
        show('info', picked.size >= room ? 'Galleriet rymmer högst 24 bilder. De första är valda.' : added + ' bilder är valda.'); paint();
      });

      const paint = () => {
        const fs = filtered();
        const total = (S.files.files || []).length;
        wholeBtn.classList.toggle('d-none', !(multi && pAlbum !== 'all' && pAlbum !== 'none' && fs.length));
        info.textContent = S.files.failed ? '' : (fs.length === total ? total + ' bilder' : 'Visar ' + fs.length + ' av ' + total + ' bilder') + ', ' + fmtSize(S.files.bytes || 0) + ' av ' + fmtSize(S.files.maxBytes || 0) + ' används.';
        if (S.files.failed) show('danger', S.files.failed);
        grid.replaceChildren(...(fs.length ? fs.map(f => h('div', { class: 'hs-imgtile' + (isSel(f) ? ' sel' : '') },
          h('button', { type: 'button', class: 'hs-imgpick', 'aria-pressed': isSel(f) ? 'true' : 'false', 'aria-label': 'Välj ' + f.name, onclick: () => { if (multi) { if (picked.has(f.id)) picked.delete(f.id); else picked.set(f.id, f); } else chosen = f; paint(); } },
            h('img', { src: f.url + '&w=320', alt: '', loading: 'lazy' }), h('span', { class: 'hs-imgname d-block', title: f.name }, f.name)),
          h('span', { class: 'small text-muted' }, f.usedBy > 0 ? 'Används på ' + f.usedBy + (f.usedBy === 1 ? ' sida' : ' sidor') : 'Används inte'),
          f.usedBy === 0 ? h('button', { type: 'button', class: 'btn btn-sm btn-outline-danger', onclick: async () => {
            const r = await api('POST', 'DeleteFile', { fileId: f.id });
            if (!r.success) { show('danger', r.message || 'Det gick inte att ta bort bilden.'); return; }
            if (chosen && chosen.id === f.id) chosen = null;
            picked.delete(f.id);
            await loadFiles(); show('', ''); paint();
          } }, 'Ta bort') : null))
          : [h('p', { class: 'text-muted' }, total ? 'Inga bilder matchar urvalet.' : 'Ni har inga bilder än. Ladda upp den första.')]));
        useBtn.disabled = multi ? picked.size === 0 : !chosen;
        if (multi) useBtn.textContent = picked.size ? 'Lägg till ' + picked.size + (picked.size === 1 ? ' bild' : ' bilder') : 'Lägg till bilder';
      };
      fileInp.addEventListener('change', async () => {
        const list = [...fileInp.files]; fileInp.value = ''; if (!list.length) return;
        upBtn.disabled = true; let okCount = 0, lastErr = null;
        for (let i = 0; i < list.length; i++) {
          show('info', list.length > 1 ? 'Laddar upp ' + (i + 1) + ' av ' + list.length + ' …' : 'Laddar upp …');
          const r = await uploadImage(list[i]);
          if (!r.success) { lastErr = list[i].name + ': ' + (r.message || 'Det gick inte att ladda upp bilden.'); if (r.code === 'quota' || r.code === 'rate') break; continue; }
          okCount++;
          await loadFiles();
          const nf = (S.files.files || []).find(x => x.id === r.id) || null;
          if (multi) { if (nf) picked.set(nf.id, nf); } else chosen = nf;
        }
        upBtn.disabled = false;
        if (lastErr) show('danger', (okCount ? okCount + ' uppladdade. ' : '') + lastErr); else show('success', okCount === 1 ? 'Bilden är uppladdad.' : okCount + ' bilder är uppladdade.');
        paint();
      });

      const el = h('div', { class: 'modal fade', tabindex: '-1' },
        h('div', { class: 'modal-dialog modal-dialog-centered modal-lg modal-dialog-scrollable' }, h('div', { class: 'modal-content' },
          h('div', { class: 'modal-header' }, h('h2', { class: 'modal-title h5' }, multi ? 'Välj bilder' : 'Välj bild'), h('button', { type: 'button', class: 'btn-close', 'data-bs-dismiss': 'modal', 'aria-label': 'Stäng' })),
          h('div', { class: 'modal-body' },
            h('p', { class: 'text-muted small' }, 'Bilden skrivs om när den laddas upp: platsdata och annan dold information tas bort och en mycket stor bild förminskas. Lägg inte upp bilder där någon kan kännas igen utan att ha gett sitt tillstånd.'),
            h('div', { class: 'mb-3 d-flex flex-wrap gap-2 align-items-center' }, upBtn, fileInp,
              (S.files.albums || []).length ? albumSel : null, h('div', { class: 'flex-grow-1', style: 'min-width:10rem' }, searchInp), wholeBtn), msg, info, grid),
          h('div', { class: 'modal-footer' }, h('button', { type: 'button', class: 'btn btn-secondary', 'data-bs-dismiss': 'modal' }, 'Avbryt'), useBtn))));
      document.body.append(el);
      const bsm = new bootstrap.Modal(el);
      useBtn.addEventListener('click', () => { done = true; bsm.hide(); resolve(multi ? [...picked.values()] : chosen); });
      el.addEventListener('hidden.bs.modal', () => { el.remove(); if (!done) resolve(null); });
      if (currentRef) chosen = (S.files.files || []).find(x => 'file:' + x.id === currentRef) || null;
      paint(); bsm.show();
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
