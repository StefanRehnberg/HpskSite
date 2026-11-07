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
    dirty: false, editVersion: 0, saving: false, timer: null, ack: new Set(), editors: [], docs: null, banner: null, statusText: '', statusErr: false, hint: null,
  };

  // ── Dialoger (bootstrap-modal, aldrig alert/confirm) ────────────────────────
  function modal(title, bodyNodes, buttons) {
    return new Promise(resolve => {
      const el = h('div', { class: 'modal fade', tabindex: '-1' },
        h('div', { class: 'modal-dialog modal-dialog-centered' }, h('div', { class: 'modal-content' },
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
  function view(nodes) { clearEditors(); clearTimeout(S.timer); body.replaceChildren(...[].concat(nodes)); }
  const header = (sub) => h('div', { class: 'mb-3' },
    h('p', { class: 'mb-1' }, h('a', { href: OWNER.url, class: 'text-decoration-none' }, h('i', { class: 'bi bi-arrow-left me-1' }), OWNER.name)),
    h('h1', { class: 'h3 mb-1' }, 'Hemsidan'), sub ? h('p', { class: 'text-muted mb-0' }, sub) : null);
  const alertBox = (cls, ...kids) => h('div', { class: 'alert alert-' + cls }, kids);

  async function load() {
    view(h('p', { class: 'text-muted' }, 'Laddar …'));
    const st = await api('GET', 'State');
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
      h('p', {}, 'Här kan ' + OWNER.name + ' skriva egna sidor som visas på pistol.nu. Läs det här först:'),
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
      h('p', { class: 'text-muted small mt-3' }, 'Klubbens vanliga sidor (Hem, Om klubben, Banor, Kalender m.fl.) hanteras som förut och finns kvar.')]);
    renderBanner();
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
    if (!parsed || !Array.isArray(parsed.blocks)) { view([header(), alertBox('danger', 'Sidans innehåll kunde inte läsas.')]); return; }
    Object.assign(S, { page: r, blocks: parsed.blocks, title: r.title, visibility: r.visibility, revision: r.revision, published: r.published, hasUnpublished: r.hasUnpublishedChanges,
      dirty: false, editVersion: 0, banner: null, statusText: 'Sparat', statusErr: false });
    if (!S.docs) { const d = await api('GET', 'Documents'); S.docs = d.success ? d.documents : []; }
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
    return JSON.stringify({ schema: 1, blocks: S.blocks.map(b => ({ id: b.id, type: b.type, visibility: b.visibility, data: b.data })) });
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
    const titleInp = h('input', { class: 'form-control form-control-lg', maxlength: '120', value: S.title, 'aria-label': 'Sidans rubrik' });
    titleInp.addEventListener('input', () => { S.title = titleInp.value; markDirty(); });
    const visSel = h('select', { class: 'form-select', 'aria-label': 'Vem ser sidan' }, visOptions().map(o => h('option', { value: o.v, selected: o.v === S.visibility ? true : null }, o.s)));
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

    view([bar, h('div', { id: 'hsBanner' }),
      h('div', { class: 'mb-3' }, h('label', { class: 'form-label fw-semibold' }, 'Rubrik'), titleInp),
      h('div', { class: 'mb-4' }, h('label', { class: 'form-label fw-semibold' }, 'Vem ser sidan?'), visSel, consequence,
        S.page.url ? h('div', { class: 'small mt-1 text-muted' }, 'Adress: ', h('code', {}, S.page.url)) : null),
      blocksEl, addBlockBar()]);
    drawBlocks();
    paintStatus(); paintEditorBanner();
  }

  function addBlockBar() {
    return h('div', { class: 'hs-card p-3' }, h('p', { class: 'fw-semibold mb-2' }, 'Lägg till'),
      h('div', { class: 'd-flex flex-wrap gap-2' }, BLOCK_TYPES.map(t => h('button', { class: 'btn btn-outline-primary', onclick: () => addBlock(t.type) }, h('i', { class: 'bi ' + t.icon + ' me-1' }), t.label))));
  }

  function addBlock(type) {
    const data = type === 'text' ? { html: '' } : type === 'heading' ? { text: '', level: 2 } : type === 'links' ? { items: [{ label: '', url: '' }] }
      : type === 'documents' ? { title: '', refs: [] } : type === 'events' ? { title: 'Kommande händelser', count: 5 } : type === 'pagelink' ? { key: (S.st.systemPages[0] || {}).key, label: '' } : { title: 'Kontakt' };
    S.blocks.push({ id: newId(), type, visibility: minVisFor(type), data });
    S.hint = null; markDirty(); drawBlocks();
  }

  function move(i, d) { const j = i + d; if (j < 0 || j >= S.blocks.length) return; [S.blocks[i], S.blocks[j]] = [S.blocks[j], S.blocks[i]]; markDirty(); drawBlocks(); }

  function drawBlocks() {
    clearEditors();
    const el = document.getElementById('hsBlocks'); if (!el) return;
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
      h('button', { class: 'btn btn-sm btn-outline-secondary', disabled: i === 0, 'aria-label': 'Flytta upp', onclick: () => move(i, -1) }, h('i', { class: 'bi bi-arrow-up' })),
      h('button', { class: 'btn btn-sm btn-outline-secondary', disabled: i === S.blocks.length - 1, 'aria-label': 'Flytta ner', onclick: () => move(i, 1) }, h('i', { class: 'bi bi-arrow-down' })),
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
