/* Skrivytan för föreningens nyheter (/{ägare}/nyheter/skriv/). Ritas ur SiteNews.cshtml; all data går via SiteNewsController, som prövar ägaren och rätten vid varje anrop.
   Textrutan och bildhanteringen beter sig som textblocket i Hemsidan: samma bildväljare (bibliotek, album, sökning, uppladdning), samma beskrivningsdialog, samma
   placering (vänster/mitten/höger) och storlek (25/50/75 %/hela bredden). Inga prompt/confirm/alert. */
(function () {
  'use strict';
  const root = document.getElementById('newsEditor');
  if (!root) return;
  const ownerType = root.dataset.ownerType, ownerId = root.dataset.ownerId, archiveUrl = root.dataset.archiveUrl;
  const canDelete = root.dataset.canDelete === '1', isRegion = root.dataset.isRegion === '1';
  const tokenEl = root.querySelector('input[name=__RequestVerificationToken]');
  const S = '/umbraco/surface/SiteNews/';
  const st = { id: 0, revision: 0, status: 0, coverId: null, editor: null, dirty: false, slugUrl: null, files: { files: [], albums: [] } };

  function h(tag, attrs, ...kids) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v === null || v === undefined || v === false) continue;
      if (k === 'class') e.className = v; else if (k.startsWith('on')) e.addEventListener(k.slice(2), v); else e.setAttribute(k, v === true ? '' : v);
    }
    for (const k of kids.flat()) if (k !== null && k !== undefined && k !== false) e.append(k.nodeType ? k : document.createTextNode(k));
    return e;
  }
  async function post(action, data) {
    const fd = new FormData();
    fd.append('ownerType', ownerType); fd.append('ownerId', ownerId);
    for (const [k, v] of Object.entries(data || {})) if (v !== null && v !== undefined) fd.append(k, v);
    const r = await fetch(S + action, { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': tokenEl.value }, body: fd });
    return r.json();
  }
  async function get(action, q) {
    const r = await fetch(S + action + '?' + new URLSearchParams(Object.assign({ ownerType, ownerId }, q || {})), { credentials: 'same-origin' });
    return r.json();
  }
  const fmtSize = b => b >= 1048576 ? (b / 1048576).toLocaleString('sv-SE', { maximumFractionDigits: 1 }) + ' MB' : Math.max(1, Math.round(b / 1024)) + ' kB';

  // En Bootstrap-dialog: svarar med värdet på den knapp som trycktes (null vid stängning). Samma uppbyggnad som Hemsidans dialoger.
  function modal(title, bodyNodes, buttons) {
    return new Promise(resolve => {
      let done = false;
      const btns = buttons.map(b => h('button', { type: 'button', class: 'btn ' + (b.cls || 'btn-secondary'), onclick: () => { done = true; bsm.hide(); resolve(b.value); } }, b.label));
      const el = h('div', { class: 'modal fade', tabindex: '-1' },
        h('div', { class: 'modal-dialog modal-dialog-centered' }, h('div', { class: 'modal-content' },
          h('div', { class: 'modal-header' }, h('h2', { class: 'modal-title h5' }, title), h('button', { type: 'button', class: 'btn-close', 'data-bs-dismiss': 'modal', 'aria-label': 'Stäng' })),
          h('div', { class: 'modal-body' }, bodyNodes), h('div', { class: 'modal-footer' }, btns))));
      document.body.append(el);
      const bsm = new bootstrap.Modal(el);
      el.addEventListener('hidden.bs.modal', () => { el.remove(); if (!done) resolve(null); });
      bsm.show();
    });
  }

  async function loadFiles() {
    const r = await get('Files');
    st.files = r.success ? r : { files: [], albums: [], count: 0, bytes: 0, maxBytes: 0, failed: r.message || 'Det gick inte att hämta bilderna.' };
    return st.files;
  }

  async function uploadImage(file) {
    const fd = new FormData();
    fd.append('upload', file); fd.append('ownerType', ownerType); fd.append('ownerId', ownerId);
    try {
      const r = await fetch(S + 'UploadImage', { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': tokenEl.value }, body: fd });
      if (r.status === 413) return { success: false, message: 'Bilden är för stor (högst 20 MB).' };
      return await r.json();
    } catch { return { success: false, message: 'Det gick inte att nå servern. Kontrollera uppkopplingen och försök igen.' }; }
  }

  // Bildväljaren: ladda upp en ny bild eller välj en av föreningens (album och sökning). Svarar med den valda filen, eller null.
  function pickImage(currentId) {
    return new Promise(async resolve => {
      await loadFiles();
      let chosen = null, done = false;
      const msg = h('div'), info = h('p', { class: 'small text-muted mb-2' }), grid = h('div', { class: 'hs-imggrid' });
      const useBtn = h('button', { type: 'button', class: 'btn btn-primary', disabled: true }, 'Använd bilden');
      const fileInp = h('input', { type: 'file', accept: 'image/jpeg,image/png,image/gif,image/webp', class: 'd-none', 'aria-label': 'Välj en bild att ladda upp' });
      const upBtn = h('button', { type: 'button', class: 'btn btn-outline-primary', onclick: () => fileInp.click() }, h('i', { class: 'bi bi-upload me-1' }), 'Ladda upp en bild');
      const show = (cls, text) => msg.replaceChildren(text ? h('div', { class: 'alert alert-' + cls + ' py-2' }, text) : '');
      let pAlbum = 'all', pQ = '';
      const albums = st.files.albums || [];
      const albumSel = h('select', { class: 'form-select w-auto', 'aria-label': 'Visa album' },
        h('option', { value: 'all' }, 'Alla bilder'), h('option', { value: 'none' }, 'Utan album'), albums.map(a => h('option', { value: String(a.id) }, a.name + ' (' + a.fileIds.length + ')')));
      const searchInp = h('input', { type: 'search', class: 'form-control', placeholder: 'Sök på namn, beskrivning eller album', 'aria-label': 'Sök bland bilderna' });
      const filtered = () => {
        const all = st.files.files || [], albs = st.files.albums || [];
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
      const paint = () => {
        const fs = filtered(), total = (st.files.files || []).length;
        info.textContent = st.files.failed ? '' : (fs.length === total ? total + ' bilder' : 'Visar ' + fs.length + ' av ' + total + ' bilder') + ', ' + fmtSize(st.files.bytes || 0) + ' av ' + fmtSize(st.files.maxBytes || 0) + ' används.';
        if (st.files.failed) show('danger', st.files.failed);
        grid.replaceChildren(...(fs.length ? fs.map(f => h('div', { class: 'hs-imgtile' + (chosen && chosen.id === f.id ? ' sel' : '') },
          h('button', { type: 'button', class: 'hs-imgpick', 'aria-pressed': chosen && chosen.id === f.id ? 'true' : 'false', 'aria-label': 'Välj ' + f.name, onclick: () => { chosen = f; paint(); } },
            h('img', { src: f.url + '&w=320', alt: '', loading: 'lazy' }), h('span', { class: 'hs-imgname d-block', title: f.name }, f.name)),
          h('span', { class: 'small text-muted' }, f.usedBy > 0 ? 'Används på ' + f.usedBy + (f.usedBy === 1 ? ' sida' : ' sidor') : 'Används inte')))
          : [h('p', { class: 'text-muted' }, total ? 'Inga bilder matchar urvalet.' : 'Ni har inga bilder än. Ladda upp den första.')]));
        useBtn.disabled = !chosen;
      };
      fileInp.addEventListener('change', async () => {
        const f = fileInp.files[0]; fileInp.value = ''; if (!f) return;
        upBtn.disabled = true; show('info', 'Laddar upp …');
        const r = await uploadImage(f);
        upBtn.disabled = false;
        if (!r.success) { show('danger', r.message || 'Det gick inte att ladda upp bilden.'); return; }
        await loadFiles();
        chosen = (st.files.files || []).find(x => x.id === r.id) || null;
        show('success', 'Bilden är uppladdad.'); paint();
      });
      const el = h('div', { class: 'modal fade', tabindex: '-1' },
        h('div', { class: 'modal-dialog modal-dialog-centered modal-lg modal-dialog-scrollable' }, h('div', { class: 'modal-content' },
          h('div', { class: 'modal-header' }, h('h2', { class: 'modal-title h5' }, 'Välj bild'), h('button', { type: 'button', class: 'btn-close', 'data-bs-dismiss': 'modal', 'aria-label': 'Stäng' })),
          h('div', { class: 'modal-body' },
            h('p', { class: 'text-muted small' }, 'Bilden skrivs om när den laddas upp: platsdata och annan dold information tas bort och en mycket stor bild förminskas. Lägg inte upp bilder där någon kan kännas igen utan att ha gett sitt tillstånd.'),
            h('div', { class: 'mb-3 d-flex flex-wrap gap-2 align-items-center' }, upBtn, fileInp, albums.length ? albumSel : null, h('div', { class: 'flex-grow-1', style: 'min-width:10rem' }, searchInp)), msg, info, grid),
          h('div', { class: 'modal-footer' }, h('button', { type: 'button', class: 'btn btn-secondary', 'data-bs-dismiss': 'modal' }, 'Avbryt'), useBtn))));
      document.body.append(el);
      const bsm = new bootstrap.Modal(el);
      useBtn.addEventListener('click', () => { done = true; bsm.hide(); resolve(chosen); });
      el.addEventListener('hidden.bs.modal', () => { el.remove(); if (!done) resolve(null); });
      if (currentId) chosen = (st.files.files || []).find(x => x.id === currentId) || null;
      paint(); bsm.show();
    });
  }

  // Bildens bredd i procent: ett eget attribut på bildblocket som skrivs som style="width:N%" på bildens ram (figure) och läses tillbaka därifrån.
  // Servern släpper bara igenom just det (SiteHtmlSanitizer). Samma plugin som textblocket i Hemsidan.
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
  function widthBar() {
    const hint = h('span', { class: 'small text-muted ms-2', role: 'status' });
    const set = (value, label) => h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', 'data-img-width': value || 'full', onclick: () => {
      const ed = st.editor; const el = ed && ed.model.document.selection.getSelectedElement();
      if (!el || el.name !== 'imageBlock') { hint.textContent = 'Klicka först på bilden du vill ändra.'; return; }
      hint.textContent = '';
      ed.model.change(w => { if (value) w.setAttribute('siteWidth', value, el); else w.removeAttribute('siteWidth', el); });
      ed.editing.view.focus();
    } }, label);
    return h('span', { class: 'd-inline-flex align-items-center gap-1 ms-2', role: 'group', 'aria-label': 'Bildens storlek' },
      h('span', { class: 'small text-muted' }, 'Bildens storlek:'), set('25%', '25 %'), set('50%', '50 %'), set('75%', '75 %'), set(null, 'Hela bredden'), hint);
  }

  // Infoga en av föreningens bilder i texten. Beskrivningen (eller "bara dekoration") krävs.
  async function insertImage() {
    const ed = st.editor; if (!ed) return;
    const f = await pickImage(); if (!f) return;
    let err = '';
    for (;;) {
      const alt = h('input', { class: 'form-control', maxlength: '200', placeholder: 'Till exempel: Skyttar på 25-metersbanan under årsmötet', 'aria-label': 'Beskriv bilden' });
      const deco = h('input', { type: 'checkbox', class: 'form-check-input', id: 'nwInlineDeco' });
      deco.addEventListener('change', () => { alt.disabled = deco.checked; });
      const r = await modal('Beskriv bilden', [
        err ? h('div', { class: 'alert alert-danger py-2' }, err) : null,
        h('p', { class: 'small text-muted' }, 'Läses upp för den som inte ser bilden. Skriv vad bilden visar, inte "bild på…".'), alt,
        h('div', { class: 'form-check mt-2' }, deco, h('label', { class: 'form-check-label', for: 'nwInlineDeco' }, 'Bara dekoration, ingen beskrivning behövs'))],
        [{ label: 'Avbryt', value: false }, { label: 'Infoga bilden', value: true, cls: 'btn-primary' }]);
      if (r !== true) return;
      const text = alt.value.trim();
      if (!deco.checked && !text) { err = 'Beskriv bilden, eller välj att den bara är dekoration.'; continue; }
      ed.model.change(w => ed.model.insertContent(w.createElement('imageBlock', { src: f.url, alt: deco.checked ? '' : text })));
      ed.editing.view.focus();
      return;
    }
  }

  const title = h('input', { class: 'form-control form-control-lg', maxlength: '200', id: 'nwTitle', 'aria-describedby': 'nwTitleHelp' });
  const bodyHost = h('div', { id: 'nwBody' });
  const excerpt = h('textarea', { class: 'form-control', rows: '2', maxlength: '400', id: 'nwExcerpt', 'aria-describedby': 'nwExcerptHelp' });
  const alt = h('input', { class: 'form-control', maxlength: '200', id: 'nwCoverAlt', placeholder: 'Beskriv bilden för den som inte ser den' });
  const coverBox = h('div', { class: 'd-flex align-items-start gap-3 flex-wrap' });
  const vis = h('select', { class: 'form-select w-auto', id: 'nwVis' },
    [[0, 'Alla, även utan inloggning'], [1, 'Alla inloggade'], [2, isRegion ? 'Medlemmar i kretsens klubbar' : 'Medlemmar i klubben'], [3, 'Styrelsen'], [4, 'Bara administratören']]
      .map(([v, t]) => h('option', { value: v, selected: v === 2 ? true : null }, t)));
  // Datumfält: sajtens vanliga svenska datumväljare (flatpickr + planering-dates.js: skriv "16 maj 2027" eller välj i kalendern). Värdet är alltid åååå-mm-dd eller fältet är rött.
  const date = h('input', { type: 'text', class: 'form-control', style: 'max-width:14rem', id: 'nwDate', autocomplete: 'off' });
  if (window.hpskPlDatePicker) window.hpskPlDatePicker(date);
  function setDate(v) { if (date._flatpickr) date._flatpickr.setDate(v || null, false); else date.value = v || ''; }
  // Datumet att skicka: tomt, åååå-mm-dd, eller null när texten inte går att läsa (då sparas ingenting).
  function dateValue() { const v = date.value.trim(); if (!v) return ''; return window.hpskPlParseDate ? window.hpskPlParseDate(v) : v; }
  const msg = h('div', { class: 'alert d-none', role: 'status', 'aria-live': 'polite' });
  const actions = h('div', { class: 'd-flex flex-wrap gap-2 align-items-center' });
  const statusBadge = h('span', { class: 'badge text-bg-secondary' }, 'Nytt utkast');

  function say(text, kind) { msg.className = 'alert alert-' + (kind || 'info'); msg.textContent = text; if (kind === 'success' && st.slugUrl) msg.append(' ', h('a', { href: st.slugUrl }, 'Visa nyheten')); }
  function clearMsg() { msg.className = 'alert d-none'; msg.textContent = ''; }
  function field(label, id, control, help) {
    return h('div', { class: 'mb-3' }, h('label', { class: 'form-label fw-semibold', for: id }, label), control, help ? h('div', { class: 'form-text', id: id + 'Help' }, help) : null);
  }

  async function chooseCover() {
    const f = await pickImage(st.coverId); if (!f) return;
    st.coverId = f.id; renderCover(); st.dirty = true;
  }
  function renderCover() {
    coverBox.replaceChildren();
    if (st.coverId) {
      coverBox.append(h('img', { src: '/umbraco/surface/SiteFile/Image?id=' + st.coverId + '&w=320', width: '160', class: 'rounded', alt: '', role: 'presentation' }),
        h('div', {}, h('button', { type: 'button', class: 'btn btn-outline-secondary btn-sm me-2', onclick: chooseCover }, 'Byt bild'),
          h('button', { type: 'button', class: 'btn btn-outline-secondary btn-sm', onclick: () => { st.coverId = null; alt.value = ''; renderCover(); st.dirty = true; } }, 'Ta bort bilden')));
    } else coverBox.append(h('button', { type: 'button', class: 'btn btn-outline-primary', onclick: chooseCover }, h('i', { class: 'bi bi-image me-1', 'aria-hidden': 'true' }), 'Välj omslagsbild'));
  }

  function formData() {
    return { title: title.value, bodyHtml: st.editor ? st.editor.getData() : '', excerpt: excerpt.value, coverFileId: st.coverId || '', coverAlt: alt.value, visibility: vis.value, publishedDate: dateValue() || '' };
  }

  async function save(publish) {
    clearMsg();
    const d = formData();
    if (!d.title.trim()) { say('Skriv en rubrik.', 'danger'); title.focus(); return; }
    if (st.coverId && !d.coverAlt.trim()) { say('Beskriv omslagsbilden, så att den går att läsa upp.', 'danger'); alt.focus(); return; }
    if (dateValue() === null) { say('Datumet går inte att läsa. Skriv till exempel 16 maj 2027, eller välj i kalendern.', 'danger'); date.focus(); return; }
    setBusy(true);
    try {
      const j = await post('Save', Object.assign({ id: st.id, revision: st.revision }, d));
      if (!j.success) { say(j.message || 'Det gick inte att spara.', 'danger'); return; }
      apply(j.post);
      if (publish !== undefined) {
        const p = await post('SetPublished', { id: st.id, revision: st.revision, publish: publish ? 'true' : 'false', publishedDate: dateValue() || '' });
        if (!p.success) { say(p.message || 'Det gick inte att publicera.', 'danger'); return; }
        apply(p.post);
        say(publish ? 'Nyheten är publicerad.' : 'Nyheten är indragen och är nu ett utkast.', 'success');
      } else say('Sparat.', 'success');
      st.dirty = false;
      history.replaceState(null, '', location.pathname + '?id=' + st.id);
    } finally { setBusy(false); }
  }

  function setBusy(b) { actions.querySelectorAll('button').forEach(x => x.disabled = b); }

  function apply(p) {
    if (!p) return;
    st.id = p.id; st.revision = p.revision; st.status = p.status; st.slugUrl = p.url;
    renderActions();
    refreshMail();
  }

  // Mejla till medlemmarna: ett uttryckligt val efter publiceringen, högst en gång per nyhet. Knappen frågar en gång till (två klick), aldrig en dialog.
  const mailBox = h('div', { class: 'card card-body mt-3 d-none', id: 'nwMail' });
  async function refreshMail() {
    mailBox.replaceChildren();
    if (!st.id || st.status !== 1) { mailBox.classList.add('d-none'); return; }
    let j; try { j = await get('MailPreview', { id: st.id }); } catch (e) { mailBox.classList.add('d-none'); return; }
    if (!j.success) { mailBox.classList.add('d-none'); return; }
    mailBox.classList.remove('d-none');
    if (j.mailedDate) { mailBox.append(h('strong', {}, 'Mejlad till medlemmarna'), h('span', { class: 'text-muted' }, ' · ' + String(j.mailedDate).slice(0, 10) + (j.mailedCount != null ? ' · ' + j.mailedCount + ' mottagare' : ''))); return; }
    if (!j.canMail) { mailBox.append(h('strong', {}, 'Mejla till medlemmarna'), h('div', { class: 'text-muted small' }, j.reason || 'Nyheten går inte att mejla just nu.')); return; }
    const btn = h('button', { type: 'button', class: 'btn btn-outline-primary' }, h('i', { class: 'bi bi-envelope me-1', 'aria-hidden': 'true' }), 'Mejla till ' + j.count + ' medlemmar');
    let armed = false;
    btn.addEventListener('click', async () => {
      if (!armed) { armed = true; btn.textContent = 'Klicka igen för att skicka till ' + j.count + ' medlemmar'; btn.classList.replace('btn-outline-primary', 'btn-primary'); setTimeout(() => { armed = false; btn.textContent = 'Mejla till ' + j.count + ' medlemmar'; btn.classList.replace('btn-primary', 'btn-outline-primary'); }, 6000); return; }
      btn.disabled = true; btn.textContent = 'Skickar …';
      const r = await post('MailMembers', { id: st.id });
      if (r.success) say('Nyheten mejlades till ' + r.sent + ' medlemmar' + (r.failed ? ' (' + r.failed + ' gick inte att nå)' : '') + '.', 'success');
      else say(r.message || 'Det gick inte att mejla.', 'danger');
      refreshMail();
    });
    mailBox.append(h('strong', {}, 'Mejla till medlemmarna'),
      h('div', { class: 'text-muted small mb-2' }, 'Skickar rubriken, sammanfattningen och en länk till ' + j.count + ' medlemmar. Det går bara att göra en gång per nyhet.'), btn);
  }

  function renderActions() {
    statusBadge.className = 'badge ' + (st.status === 1 ? 'text-bg-success' : 'text-bg-secondary');
    statusBadge.textContent = st.id === 0 ? 'Nytt utkast' : st.status === 1 ? 'Publicerad' : 'Utkast';
    actions.replaceChildren();
    if (st.status === 1) {
      actions.append(h('button', { type: 'button', class: 'btn btn-primary', onclick: () => save() }, 'Spara ändringar'),
        h('button', { type: 'button', class: 'btn btn-outline-secondary', onclick: () => save(false) }, 'Dra tillbaka (gör till utkast)'));
    } else {
      actions.append(h('button', { type: 'button', class: 'btn btn-primary', onclick: () => save(true) }, h('i', { class: 'bi bi-send me-1', 'aria-hidden': 'true' }), 'Publicera'),
        h('button', { type: 'button', class: 'btn btn-outline-secondary', onclick: () => save() }, 'Spara utkast'));
    }
    if (st.id) actions.append(h('a', { class: 'btn btn-link', href: st.slugUrl || archiveUrl }, 'Visa'));
    if (st.id && canDelete) {
      const del = h('button', { type: 'button', class: 'btn btn-outline-danger ms-auto' }, 'Ta bort');
      let armed = false;
      del.addEventListener('click', async () => {
        if (!armed) { armed = true; del.textContent = 'Klicka igen för att ta bort'; del.classList.replace('btn-outline-danger', 'btn-danger'); setTimeout(() => { armed = false; del.textContent = 'Ta bort'; del.classList.replace('btn-danger', 'btn-outline-danger'); }, 5000); return; }
        const j = await post('Delete', { id: st.id });
        if (j.success) location.href = j.url; else say(j.message || 'Det gick inte att ta bort.', 'danger');
      });
      actions.append(del);
    }
  }

  async function init() {
    root.querySelectorAll('p.text-muted').forEach(x => x.remove());
    const id = parseInt(new URLSearchParams(location.search).get('id') || '0', 10) || 0;
    let existing = null;
    if (id) {
      const j = await get('Get', { id });
      if (!j.success) { root.append(h('div', { class: 'alert alert-danger' }, j.message || 'Nyheten kunde inte hämtas.')); return; }
      existing = j.post;
    }
    root.append(
      h('div', { class: 'mb-3' }, statusBadge),
      field('Rubrik', 'nwTitle', title, 'Det som står överst och i listan.'),
      h('div', { class: 'mb-3' }, h('label', { class: 'form-label fw-semibold' }, 'Text'),
        h('div', { class: 'mb-2 d-flex flex-wrap align-items-center gap-1' },
          h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', onclick: insertImage }, h('i', { class: 'bi bi-image me-1', 'aria-hidden': 'true' }), 'Infoga bild'), widthBar()),
        bodyHost),
      field('Kort sammanfattning (valfri)', 'nwExcerpt', excerpt, 'Visas i listan. Lämnar du den tom tas början av texten.'),
      h('div', { class: 'mb-3' }, h('div', { class: 'form-label fw-semibold' }, 'Omslagsbild (valfri)'), coverBox, h('div', { class: 'mt-2' }, h('label', { class: 'form-label small', for: 'nwCoverAlt' }, 'Beskrivning av omslagsbilden'), alt)),
      field('Vem får läsa nyheten?', 'nwVis', vis, 'Nyheten syns bara för dem på den nivån och över. Välj "Alla" bara för sådant som är offentligt.'),
      field('Datum som visas (valfritt)', 'nwDate', date, 'Tomt = dagen du publicerar.'),
      msg, actions, mailBox);
    renderCover();
    if (existing) {
      title.value = existing.title || ''; excerpt.value = existing.excerpt || ''; alt.value = existing.coverAlt || ''; vis.value = String(existing.visibility);
      st.coverId = existing.coverFileId || null; renderCover();
      if (existing.publishedDate) setDate(String(existing.publishedDate).slice(0, 10));
      apply(existing);
    } else renderActions();
    if (!window.ClassicEditor) { bodyHost.append(h('p', { class: 'text-danger' }, 'Textredigeraren kunde inte laddas. Ladda om sidan.')); return; }
    ClassicEditor.create(bodyHost, {
      toolbar: ['heading', '|', 'bold', 'italic', 'link', 'bulletedList', 'numberedList', 'blockQuote', 'insertTable', '|', 'undo', 'redo'],
      heading: { options: [{ model: 'paragraph', title: 'Vanlig text', class: 'ck-heading_paragraph' }, { model: 'heading2', view: 'h2', title: 'Rubrik', class: 'ck-heading_heading2' },
                           { model: 'heading3', view: 'h3', title: 'Underrubrik', class: 'ck-heading_heading3' }] },
      // Bilder: bara ur föreningens egna bilder (knappen Infoga bild). Placering och beskrivning väljs på bilden, storleken med knapparna ovanför textrutan.
      removePlugins: ['ImageCaption', 'ImageUpload', 'EasyImage', 'CKFinder', 'CKFinderUploadAdapter', 'CKBox', 'CKBoxUI', 'CKBoxEditing', 'CKBoxUtils', 'CKBoxImageEdit', 'MediaEmbed'],
      image: { toolbar: ['imageStyle:alignLeft', 'imageStyle:alignCenter', 'imageStyle:alignRight', '|', 'imageTextAlternative'], styles: { options: ['alignLeft', 'alignCenter', 'alignRight'] } },
      extraPlugins: [SiteImageWidth],
    }).then(ed => {
      st.editor = ed;
      ed.setData(existing ? (existing.bodyHtml || '') : '');
      ed.model.document.on('change:data', () => { st.dirty = true; });
    }).catch(e => { console.error('CKEditor kunde inte startas:', e && (e.message || e)); bodyHost.append(h('p', { class: 'text-danger' }, 'Textredigeraren kunde inte startas.')); });
    window.addEventListener('beforeunload', ev => { if (st.dirty) { ev.preventDefault(); ev.returnValue = ''; } });
  }
  init();
})();
