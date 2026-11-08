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

  const VIS = [
    { v: 0, t: 'Alla', s: 'Alla, även den som inte är inloggad' },
    { v: 1, t: 'Inloggade', s: 'Alla som är inloggade på pistol.nu' },
    { v: 2, t: 'Medlemmar', s: 'Bara aktiva medlemmar i klubben' },
    { v: 3, t: 'Styrelsen', s: 'Bara styrelsen' },
    { v: 4, t: 'Administratörer', s: 'Bara klubbens administratörer' },
  ];
  const visOptions = () => VIS.filter(o => !(OWNER.type === 'Region' && o.v === 2));   // kretsens medlemssynlighet kommer med kretsstegen (2d)
  const visLabel = v => (VIS.find(o => o.v === v) || VIS[4]).t;
  const CONSEQUENCE = {
    0: 'Sidan kan hittas och läsas av vem som helst på internet. Skriv inga personuppgifter.',
    1: 'Den som har ett konto på pistol.nu kan läsa sidan.',
    2: 'Bara medlemmar i klubben kan läsa sidan. Andra som öppnar länken ser ingenting.',
    3: 'Bara styrelsen kan läsa sidan. Andra får beskedet att sidan inte finns.',
    4: 'Bara klubbens administratörer kan läsa sidan. Andra får beskedet att sidan inte finns.',
  };
  const BLOCK_TYPES = [
    { type: 'text', label: 'Text', icon: 'bi-text-paragraph' },
    { type: 'heading', label: 'Rubrik', icon: 'bi-type-h2' },
    { type: 'links', label: 'Länkar', icon: 'bi-link-45deg' },
    { type: 'documents', label: 'Dokument', icon: 'bi-file-earmark-text' },
    { type: 'contact', label: 'Kontaktuppgifter', icon: 'bi-person-lines-fill' },
    { type: 'events', label: 'Kommande händelser', icon: 'bi-calendar-event' },
    { type: 'pagelink', label: 'Länk till en sida', icon: 'bi-box-arrow-up-right' },
  ];
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
  function view(nodes) { clearEditors(); clearTimeout(S.timer); body.replaceChildren(...[].concat(nodes).filter(Boolean)); }
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

  async function load() {
    view(h('p', { class: 'text-muted' }, 'Laddar …'));
    const st = await api('GET', 'State');
    if (!st.success && st.code === 'support') return renderSupportStart();
    if (!st.success) { view([header(), alertBox('warning', st.message || 'Hemsidan kan inte öppnas.')]); return; }
    S.st = st;
    if (!st.termsAccepted) return renderTerms();
    renderList();
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

  function renderList() {
    const st = S.st;
    const rows = st.pages.map(p => h('tr', {},
      h('td', {}, h('a', { href: '#', class: 'fw-semibold text-decoration-none', onclick: e => { e.preventDefault(); openEditor(p.id); } }, p.title)),
      h('td', {}, visBadge(p.visibility)), h('td', {}, statusBadge(p)),
      h('td', { class: 'text-end' }, h('div', { class: 'dropdown' },
        h('button', { class: 'btn btn-sm btn-outline-secondary dropdown-toggle', 'data-bs-toggle': 'dropdown', type: 'button' }, 'Åtgärder'),
        h('ul', { class: 'dropdown-menu dropdown-menu-end' },
          h('li', {}, h('a', { class: 'dropdown-item', href: '#', onclick: e => { e.preventDefault(); openEditor(p.id); } }, 'Ändra')),
          p.status !== 'Utkast' && p.url ? h('li', {}, h('a', { class: 'dropdown-item', href: p.url, target: '_blank', rel: 'noopener' }, 'Se sidan')) : null,
          p.status !== 'Utkast' && p.url ? h('li', {}, h('a', { class: 'dropdown-item', href: '#', onclick: e => { e.preventDefault(); S.visibility = p.visibility; copyLink(p.url); } }, 'Kopiera länk')) : null,
          h('li', {}, h('hr', { class: 'dropdown-divider' })),
          h('li', {}, h('a', { class: 'dropdown-item text-danger', href: '#', onclick: async e => {
            e.preventDefault();
            if (!await confirmDialog('Radera sidan?', 'Sidan "' + p.title + '" tas bort från sajten. Ni kan ångra det i 30 dagar.', 'Radera', true)) return;
            const r = await api('POST', 'DeletePage', { pageId: p.id, expectedRevision: p.revision });
            if (!r.success) { S.banner = { cls: 'danger', text: r.message }; } else S.banner = { cls: 'success', text: 'Sidan är raderad. Du kan ångra under "Raderade sidor".' };
            await reloadState();
          } }, 'Radera')))))));
    view([header('Egna sidor för ' + OWNER.name), h('div', { id: 'hsBanner' }),
      h('div', { class: 'd-flex flex-wrap gap-2 mb-3 align-items-center' },
        h('button', { class: 'btn btn-primary', disabled: st.pages.length >= st.maxPages, onclick: () => renderNew() }, h('i', { class: 'bi bi-plus-lg me-1' }), 'Ny sida'),
        st.trash.length ? h('button', { class: 'btn btn-link', onclick: renderTrash }, 'Raderade sidor (' + st.trash.length + ')') : null,
        h('span', { class: 'text-muted small ms-auto' }, st.pages.length + ' av högst ' + st.maxPages + ' sidor')),
      st.pages.length
        ? h('table', { class: 'table align-middle hs-list' }, h('thead', {}, h('tr', {}, h('th', {}, 'Sida'), h('th', {}, 'Vem ser den'), h('th', {}, 'Status'), h('th', {}))), h('tbody', {}, rows))
        : h('div', { class: 'hs-card p-4 text-center' }, h('p', { class: 'mb-2' }, 'Ni har inga egna sidor än.'), h('p', { class: 'text-muted mb-3' }, 'Skapa en sida för till exempel banregler, klubbens historia eller hitta hit.')),
      h('p', { class: 'text-muted small mt-3' }, 'Klubbens vanliga sidor (Hem, Om klubben, Banor, Kalender m.fl.) hanteras som förut och finns kvar.'),
      slotsCard(), auditCard()]);
    renderBanner();
  }

  // Egen text före/efter klubbens vanliga sidor (Hem, Om klubben, Banor …): en mallkopia per systemsida. Systemsidan är exakt som förut utan den.
  function slotsCard() {
    const slots = (S.st && S.st.slots) || [];
    if (!slots.length) return null;
    const rows = slots.map(x => h('tr', {},
      h('td', {}, h('strong', {}, x.label)),
      h('td', {}, x.page ? [visBadge(x.page.visibility), ' ', statusBadge({ status: x.page.status })] : h('span', { class: 'text-muted' }, 'Ingen egen text')),
      h('td', { class: 'text-end' }, h('button', { class: 'btn btn-sm btn-outline-secondary', onclick: () => x.page ? openEditor(x.page.id) : chooseSlotVisibility(x) }, x.page ? 'Ändra' : 'Lägg till egen text'))));
    return h('details', { class: 'hs-card p-3 mt-4' }, h('summary', { class: 'fw-semibold' }, 'Egen text på de vanliga sidorna'),
      h('p', { class: 'text-muted small mt-2' }, 'Skriv egen text före eller efter innehållet på Hem, Om klubben, Banor och de andra vanliga sidorna. Sidorna är som förut tills ni lägger till något.'),
      h('table', { class: 'table align-middle hs-list mb-0' }, h('tbody', {}, rows)));
  }

  async function chooseSlotVisibility(x) {
    let chosen = Math.max(x.minVisibility, OWNER.type === 'Region' ? 1 : 2);        // förval: medlemmar (inloggade för en krets), aldrig öppnare än sidan tillåter
    const box = h('div');
    const draw = () => box.replaceChildren(h('div', {}, visOptions().filter(o => o.v >= x.minVisibility && o.v <= 3).map(o => h('label', { class: 'hs-choice ' + (chosen === o.v ? 'active' : '') },
      h('input', { type: 'radio', name: 'slotvis', checked: chosen === o.v, onchange: () => { chosen = o.v; draw(); } }), h('strong', {}, o.t), h('span', { class: 'hs-sub' }, o.s))),
      h('p', { class: 'text-muted small mt-2 mb-0' }, 'Texten syns bara för dem som får se sidan, och aldrig för fler än du väljer här. ' + CONSEQUENCE[chosen])));
    draw();
    const go = await modal('Vem ska se egen text på "' + x.label + '"?', box, [{ label: 'Avbryt', value: false }, { label: 'Fortsätt', value: true, cls: 'btn-primary' }]);
    if (go !== true) return;
    if (chosen === 0 && !await confirmPublic()) return;
    const r = await api('POST', 'CreateSlotPage', { templateKey: x.key, visibility: chosen });
    if (!r.success) { S.banner = { cls: 'danger', text: r.message || 'Det gick inte att skapa.' }; renderList(); return; }
    await reloadState();
    openEditor(r.pageId);
  }

  // "Vad som hänt": vem gjorde vad och när (senaste 100). Bara föreningens egna administratörer läser loggen, så den visas inte i stödläge.
  function auditCard() {
    if (S.st && S.st.support) return null;
    const out = h('div', { class: 'mt-2' });
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
    const box = h('details', { class: 'hs-card p-3 mt-4' }, h('summary', { class: 'fw-semibold' }, 'Vad som hänt'), out);
    box.addEventListener('toggle', () => { if (box.open && !box.dataset.loaded) { box.dataset.loaded = '1'; load(); } });
    return box;
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
    const rows = S.st.trash.map(p => h('tr', {}, h('td', {}, p.title), h('td', {}, visBadge(p.visibility)),
      h('td', { class: 'text-end' }, h('button', { class: 'btn btn-sm btn-outline-primary', onclick: async () => {
        const r = await api('POST', 'RestorePage', { pageId: p.id, expectedRevision: p.revision });
        S.banner = r.success ? { cls: 'success', text: 'Sidan "' + p.title + '" är tillbaka.' } : { cls: 'danger', text: r.message };
        await reloadState();
      } }, 'Ångra radering'))));
    view([header('Raderade sidor'), h('p', {}, h('a', { href: '#', onclick: e => { e.preventDefault(); renderList(); } }, '← Tillbaka till sidorna')),
      h('p', { class: 'text-muted' }, 'En raderad sida kan ångras i 30 dagar.'),
      h('table', { class: 'table align-middle hs-list' }, h('tbody', {}, rows))]);
  }

  // ── Ny sida i tre steg ──────────────────────────────────────────────────────
  function renderNew(w) {
    w = w || { step: 1, title: '', template: 'text', visibility: OWNER.type === 'Region' ? 1 : 2 };
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
        S.hint = w.template === 'docs' ? 'Lägg till dokumenten med knappen "Dokument" här under.' : null;
        if (blocks.length) {
          const save = await api('POST', 'SavePage', { pageId: create.pageId, expectedRevision: create.revision, title: w.title.trim(), blocksJson: JSON.stringify({ schema: 1, blocks }), visibility: w.visibility });
          if (!save.success) { err.replaceChildren(alertBox('danger', save.message || 'Sidan skapades men mallen kunde inte läggas in.')); }
        }
        await reloadState();
        openEditor(create.pageId);
      };
    }
    const nextBtn = h('button', { class: 'btn btn-primary', onclick: () => next() }, w.step === 3 ? 'Skapa sidan' : 'Nästa');
    view([header('Ny sida'), h('ol', { class: 'list-inline mb-3' }, step(1, 'Namn'), step(2, 'Mall'), step(3, 'Vem ser den')),
      h('div', { class: 'hs-card p-3 p-md-4', style: 'max-width:46rem' }, content,
        h('div', { class: 'd-flex gap-2 mt-3' }, back ? h('button', { class: 'btn btn-outline-secondary', onclick: back }, 'Tillbaka') : h('button', { class: 'btn btn-outline-secondary', onclick: renderList }, 'Avbryt'), nextBtn))]);
  }

  function templateBlocks(tpl, vis) {
    const text = () => ({ id: newId(), type: 'text', visibility: vis, data: { html: '' } });
    if (tpl === 'empty') return [];
    if (tpl === 'contact') return [text(), { id: newId(), type: 'contact', visibility: Math.max(vis, 1), data: { title: 'Kontakt' } }];
    return [text()];
  }

  function visibilityChoices(current, onPick) {
    return h('div', {}, visOptions().map(o => h('label', { class: 'hs-choice ' + (current === o.v ? 'active' : '') },
      h('input', { type: 'radio', name: 'vis', checked: current === o.v, onchange: () => onPick(o.v) }), h('strong', {}, o.t), h('span', { class: 'hs-sub' }, o.s))),
      h('p', { class: 'text-muted small mt-2 mb-0' }, CONSEQUENCE[current]));
  }

  async function confirmPublic() {
    const ok = h('input', { type: 'checkbox', class: 'form-check-input', id: 'hsPubOk' });
    const res = await modal('Synlig för alla?', [
      h('p', {}, 'Alla som besöker pistol.nu kan läsa den här sidan, även utan inloggning. Den kan också hittas via sökmotorer.'),
      h('p', { class: 'fw-semibold' }, 'Skriv inga personuppgifter: inga namn på enskilda medlemmar, inga personnummer, telefonnummer eller adresser till privatpersoner.'),
      h('div', { class: 'form-check' }, ok, h('label', { class: 'form-check-label', for: 'hsPubOk' }, 'Jag har kontrollerat att sidan inte innehåller personuppgifter.'))],
      [{ label: 'Avbryt', value: false }, { label: 'Ja, gör den synlig för alla', value: true, cls: 'btn-primary' }]);
    return res === true && ok.checked;
  }

  // ── Redigeraren ─────────────────────────────────────────────────────────────
  async function openEditor(pageId) {
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
    Object.assign(S, { kind: r.kind || 'free', minVis: r.minVisibility || 0, templateKey: r.templateKey || null });
    Object.assign(S, { page: r, blocks, title: r.title, visibility: r.visibility, revision: r.revision, published: r.published, hasUnpublished: r.hasUnpublishedChanges,
      dirty: false, editVersion: 0, banner: null, statusText: 'Sparat', statusErr: false });
    if (!S.docs) { const d = await api('GET', 'Documents'); S.docs = d.success ? d.documents : []; }
    if (!S.personas) { const pr = await api('GET', 'PreviewPersonas'); S.personas = pr.success ? pr.personas : []; }
    renderEditor();
  }

  function markDirty() {
    S.dirty = true; S.editVersion++; S.statusText = 'Inte sparat'; S.statusErr = false; paintStatus();
    clearTimeout(S.timer); S.timer = setTimeout(() => saveNow(), SAVE_IDLE_MS);
  }
  function paintStatus() {
    const el = document.getElementById('hsStatus'); if (el) { el.textContent = S.statusText; el.className = 'hs-status' + (S.statusErr ? ' err' : ''); }
    const pub = document.getElementById('hsPublish'); if (pub) pub.disabled = S.saving || !(S.dirty || S.hasUnpublished);
    const dis = document.getElementById('hsDiscard'); if (dis) dis.hidden = !(S.published && (S.dirty || S.hasUnpublished));
  }

  function setBanner(cls, text, extra) { S.banner = { cls, text, extra }; paintEditorBanner(); }
  function paintEditorBanner() {
    const el = document.getElementById('hsBanner'); if (!el) return;
    const kids = [];
    if (S.banner) kids.push(h('div', { class: 'alert alert-' + S.banner.cls + ' d-flex flex-wrap gap-2 align-items-center' }, h('span', { class: 'me-auto' }, S.banner.text), S.banner.extra || null));
    else if (S.published && (S.dirty || S.hasUnpublished)) kids.push(h('div', { class: 'alert alert-info' }, 'Du har ändringar som inte är publicerade. Besökarna ser fortfarande den publicerade versionen.'));
    else if (!S.published) kids.push(h('div', { class: 'alert alert-secondary' }, 'Sidan är inte publicerad än. Ingen annan kan se den förrän du publicerar.'));
    if (S.hint) kids.push(h('div', { class: 'alert alert-light border' }, S.hint));
    el.replaceChildren(...kids);
  }

  function collectJson() {
    const plain = b => ({ id: b.id, type: b.type, visibility: b.visibility, data: b.data });
    if (S.kind === 'slots') return JSON.stringify({ schema: 1, slots: { before: S.blocks.filter(b => b.pos === 'before').map(plain), after: S.blocks.filter(b => b.pos === 'after').map(plain) } });
    return JSON.stringify({ schema: 1, blocks: S.blocks.map(plain) });
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
    if (S.dirty && !await saveNow()) return false;
    S.saving = true; paintStatus();
    const r = await api('POST', 'PublishPage', { pageId: S.page.id, expectedRevision: S.revision, allowPersonalNumber: S.ack.size > 0 ? 'true' : 'false' });
    S.saving = false;
    if (r.success) {
      S.revision = r.revision; S.published = true; S.hasUnpublished = false; S.dirty = false; S.statusText = 'Publicerat kl. ' + clock(); S.statusErr = false;
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
    const slots = S.kind === 'slots';
    const titleInp = h('input', { class: 'form-control form-control-lg', maxlength: '120', value: S.title, 'aria-label': 'Sidans rubrik' });
    titleInp.addEventListener('input', () => { S.title = titleInp.value; markDirty(); });
    const visSel = h('select', { class: 'form-select', 'aria-label': 'Vem ser sidan' }, visOptions().filter(o => !slots || o.v >= S.minVis).map(o => h('option', { value: o.v, selected: o.v === S.visibility ? true : null }, o.s)));
    visSel.addEventListener('change', async () => {
      const v = parseInt(visSel.value, 10);
      if (v === 0 && !await confirmPublic()) { visSel.value = S.visibility; return; }
      setPageVisibility(v);
    });
    const consequence = h('div', { class: 'text-muted small mt-1' }, CONSEQUENCE[S.visibility]);
    const blocksEl = h('div', { id: 'hsBlocks' });

    const bar = h('div', { class: 'hs-bar d-flex flex-wrap gap-2 align-items-center' },
      h('button', { class: 'btn btn-outline-secondary', onclick: async () => { if (S.dirty) await saveNow(); load(); } }, h('i', { class: 'bi bi-arrow-left me-1' }), 'Alla sidor'),
      h('span', { id: 'hsStatus', class: 'hs-status ms-auto' }, S.statusText),
      h('button', { id: 'hsDiscard', class: 'btn btn-outline-secondary', hidden: true, onclick: async () => {
        if (!await confirmDialog('Kasta ändringarna?', 'Ändringarna sedan senaste publiceringen försvinner. Den publicerade sidan påverkas inte.', 'Kasta ändringarna', true)) return;
        clearTimeout(S.timer); S.dirty = false;
        const r = await api('POST', 'DiscardDraft', { pageId: S.page.id, expectedRevision: S.revision });
        if (!r.success) { setBanner('danger', r.message || 'Det gick inte att kasta ändringarna.'); return; }
        openEditor(S.page.id);
      } }, 'Kasta ändringarna'),
      h('button', { id: 'hsPublish', class: 'btn btn-primary', onclick: () => publishNow() }, 'Publicera ändringar'));

    view([bar, supportBanner(), h('div', { id: 'hsBanner' }),
      slots
        ? h('div', { class: 'mb-3' }, h('h1', { class: 'h3 mb-1' }, 'Egen text på "' + S.title + '"'),
            h('p', { class: 'text-muted mb-0' }, 'Texten visas före och efter sidans vanliga innehåll. Sidans eget innehåll ändras inte.'))
        : h('div', { class: 'mb-3' }, h('label', { class: 'form-label fw-semibold' }, 'Rubrik'), titleInp),
      h('div', { class: 'mb-4' }, h('label', { class: 'form-label fw-semibold' }, slots ? 'Vem ser texten?' : 'Vem ser sidan?'), visSel, consequence,
        !slots && S.page.url ? h('div', { class: 'small mt-1 text-muted' }, 'Adress: ', h('code', {}, S.page.url)) : null),
      slots ? null : menuToggle(), slots ? null : previewRow(),
      blocksEl, slots ? null : addBlockBar(), moreCard()]);
    drawBlocks();
    paintStatus(); paintEditorBanner();
  }

  // "Se som": förhandsvisar sidan för en vald fast tittare. Servern renderar (samma renderare och regler som besökarna), svaret läggs i en sandlådad iframe.
  function previewRow() {
    const sel = h('select', { class: 'form-select w-auto', 'aria-label': 'Se som' }, (S.personas || []).map(x => h('option', { value: x.key }, x.label)));
    return h('div', { class: 'd-flex flex-wrap align-items-center gap-2 mb-4' },
      h('span', { class: 'fw-semibold' }, 'Se sidan som'), sel,
      h('button', { class: 'btn btn-outline-secondary', onclick: async e => {
        e.target.disabled = true;
        if (S.dirty) await saveNow();                                   // förhandsvisningen läser det sparade utkastet
        const r = await api('POST', 'Preview', { pageId: S.page.id, persona: sel.value });
        e.target.disabled = false;
        if (!r.success) { setBanner('danger', r.message || 'Förhandsvisningen gick inte att göra.'); return; }
        const kids = [alertBox(r.decision === 'Serve' ? 'info' : 'warning', r.notice)];
        if (r.document) kids.push(h('iframe', { sandbox: '', srcdoc: r.document, title: 'Förhandsvisning', style: 'width:100%;height:55vh;border:1px solid var(--bs-border-color);border-radius:.5rem;background:#fff' }));
        modal('Se sidan som: ' + sel.options[sel.selectedIndex].text, kids, [{ label: 'Stäng', value: true, cls: 'btn-primary' }], true);
      } }, h('i', { class: 'bi bi-eye me-1' }), 'Förhandsvisa'));
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
        ...(S.kind === 'slots' ? [] : [h('h2', { class: 'h6 mt-4' }, 'Adress (avancerat)'),
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
    return h('div', { class: 'form-check mb-4' }, cb, h('label', { class: 'form-check-label', for: 'hsInMenu' }, 'Visa sidan i menyn'),
      h('div', { class: 'form-text' }, 'Länken syns i menyn när sidan är publicerad, och bara för dem som får läsa sidan.'));
  }

  function addBlockBar() {
    return h('div', { class: 'hs-card p-3' }, h('p', { class: 'fw-semibold mb-2' }, 'Lägg till'),
      h('div', { class: 'd-flex flex-wrap gap-2' }, BLOCK_TYPES.map(t => h('button', { class: 'btn btn-outline-primary', onclick: () => addBlock(t.type) }, h('i', { class: 'bi ' + t.icon + ' me-1' }), t.label))));
  }

  function addBlock(type, pos) {
    const data = type === 'text' ? { html: '' } : type === 'heading' ? { text: '', level: 2 } : type === 'links' ? { items: [{ label: '', url: '' }] }
      : type === 'documents' ? { title: '', refs: [] } : type === 'events' ? { title: 'Kommande händelser', count: 5 } : type === 'pagelink' ? { key: (S.st.systemPages[0] || {}).key, label: '' } : { title: 'Kontakt' };
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
    clearEditors();
    const el = document.getElementById('hsBlocks'); if (!el) return;
    if (S.kind === 'slots') {
      const group = (pos, title, help) => {
        const items = S.blocks.map((b, i) => [b, i]).filter(([b]) => b.pos === pos);
        return h('section', { class: 'mb-4', dataset: undefined },
          h('h2', { class: 'h5' }, title), h('p', { class: 'text-muted small' }, help),
          ...(items.length ? items.map(([b, i]) => blockCard(b, i)) : [h('div', { class: 'hs-card p-3 text-center text-muted mb-3' }, 'Ingen text här än.')]),
          h('div', { class: 'd-flex flex-wrap gap-2' }, ['text', 'links'].map(t => h('button', { class: 'btn btn-outline-primary', onclick: () => addBlock(t, pos) }, h('i', { class: 'bi ' + typeInfo(t).icon + ' me-1' }), typeInfo(t).label))));
      };
      el.replaceChildren(group('before', 'Före sidans innehåll', 'Visas överst på sidan.'), group('after', 'Efter sidans innehåll', 'Visas längst ner på sidan.'));
      el.querySelectorAll('[data-rte]').forEach(node => initRte(node, S.blocks.find(b => b.id === node.dataset.rte)));
      return;
    }
    el.replaceChildren(...(S.blocks.length ? S.blocks.map(blockCard) : [h('div', { class: 'hs-card p-4 text-center text-muted mb-3' }, 'Sidan har inget innehåll än. Lägg till ett block här under.')]));
    // CKEditor skapas först när elementen sitter i dokumentet.
    el.querySelectorAll('[data-rte]').forEach(node => initRte(node, S.blocks.find(b => b.id === node.dataset.rte)));
  }

  function initRte(node, block) {
    if (!window.ClassicEditor || !block) { node.append(h('p', { class: 'text-danger' }, 'Textredigeraren kunde inte laddas. Ladda om sidan.')); return; }
    ClassicEditor.create(node, {
      toolbar: ['heading', '|', 'bold', 'italic', 'link', 'bulletedList', 'numberedList', 'blockQuote', 'insertTable', '|', 'undo', 'redo'],
      heading: { options: [{ model: 'paragraph', title: 'Vanlig text', class: 'ck-heading_paragraph' }, { model: 'heading2', view: 'h2', title: 'Rubrik', class: 'ck-heading_heading2' },
                           { model: 'heading3', view: 'h3', title: 'Underrubrik', class: 'ck-heading_heading3' }] },
      removePlugins: ['Image', 'ImageCaption', 'ImageStyle', 'ImageToolbar', 'ImageUpload', 'ImageResize', 'EasyImage', 'CKFinder', 'CKFinderUploadAdapter', 'CKBox', 'CKBoxUI', 'CKBoxEditing', 'CKBoxUtils', 'CKBoxImageEdit', 'MediaEmbed'],
    }).then(ed => {
      ed.setData(block.data.html || '');
      ed.model.document.on('change:data', () => { block.data.html = ed.getData(); markDirty(); });
      S.editors.push(ed);
    }).catch(err => { console.error('CKEditor kunde inte startas:', err && (err.message || err)); node.append(h('p', { class: 'text-danger' }, 'Textredigeraren kunde inte startas.')); });
  }

  function blockCard(b, i) {
    const info = typeInfo(b.type), min = minVisFor(b.type);
    const visSel = h('select', { class: 'form-select form-select-sm w-auto', 'aria-label': 'Vem ser blocket' },
      visOptions().filter(o => o.v >= min).map(o => h('option', { value: o.v, selected: o.v === b.visibility ? true : null }, o.t)));
    visSel.addEventListener('change', () => { b.visibility = parseInt(visSel.value, 10); markDirty(); });
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

  function field(label, input, help) { return h('div', { class: 'mb-2' }, h('label', { class: 'form-label small fw-semibold mb-1' }, label), input, help ? h('div', { class: 'form-text' }, help) : null); }
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
      case 'events': return [field('Rubrik (valfri)', bind(h('input', { class: 'form-control', maxlength: '120', value: d.title || '' }), d, 'title')),
        field('Så många händelser', bind(h('select', { class: 'form-select w-auto' }, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map(n => h('option', { value: n, selected: n === (d.count || 5) ? true : null }, n))), d, 'count', v => parseInt(v, 10)),
          'Visar de närmaste kommande händelserna i kalendern.')];
      case 'pagelink': return [field('Vilken sida?', bind(h('select', { class: 'form-select' }, S.st.systemPages.map(p => h('option', { value: p.key, selected: p.key === d.key ? true : null }, p.label))), d, 'key')),
        field('Text på knappen (valfri)', bind(h('input', { class: 'form-control', maxlength: '80', value: d.label || '' }), d, 'label'))];
      default: return h('p', { class: 'text-danger' }, 'Okänt block.');
    }
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
