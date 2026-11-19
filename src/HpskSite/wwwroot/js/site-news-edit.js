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
  const MAX_GALLERY = 12;   // samma tak som servern (SitePostRules.MaxGallery); servern prövar det igen
  const st = { id: 0, revision: 0, status: 0, gallery: [], editor: null, dirty: false, slugUrl: null, files: { files: [], albums: [] } };

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
  // Signaturen (avatar, namn, roll) i texten: se site-signature.js. Man skriver bara under som sig själv; servern prövar det vid sparning.
  const SIG = window.HpskSignature ? window.HpskSignature.create({ get: ids => get('Signatures', { ids: ids.join(',') }) }) : null;
  const sigUi = SIG ? SIG.controls(h) : null;
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

  // Förminskar en stor kamerabild i webbläsaren före uppladdning (längsta sida högst 2400 px, som servern gör): en 48-megapixelbild från en ny telefon avvisas annars av servern
  // med ett fel som en lekman inte kan åtgärda, och 20 bilder à 8 MB tar lång tid över mobilnät. canvas ger bilden rätt orientering (imageOrientation) och ingen EXIF; allt annat
  // (gif, redan små bilder, filer webbläsaren inte kan läsa) skickas orört — servern gör sin egen bearbetning oavsett (orientering, metadata bort, tak).
  async function prepareImage(file) {
    if (!/^image\/(jpeg|png|webp)$/.test(file.type) || !window.createImageBitmap) return file;
    try {
      const bmp = await createImageBitmap(file, { imageOrientation: 'from-image' });
      const longest = Math.max(bmp.width, bmp.height);
      if (longest <= 2400 && file.size <= 8 * 1048576) { if (bmp.close) bmp.close(); return file; }
      const f = Math.min(1, 2400 / longest);
      const c = document.createElement('canvas'); c.width = Math.max(1, Math.round(bmp.width * f)); c.height = Math.max(1, Math.round(bmp.height * f));
      c.getContext('2d').drawImage(bmp, 0, 0, c.width, c.height);
      if (bmp.close) bmp.close();
      const type = file.type === 'image/png' ? 'image/png' : 'image/jpeg';
      const blob = await new Promise(res => c.toBlob(res, type, 0.85));
      if (!blob) return file;
      return new File([blob], file.name.replace(/\.(png|jpe?g|webp)$/i, type === 'image/png' ? '.png' : '.jpg'), { type });
    } catch { return file; }
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
  const galleryBox = h('div', { id: 'nwGallery' });
  const galleryCount = h('span', { class: 'small text-muted', id: 'nwGalleryCount' });
  const queueBox = h('div', { class: 'small mt-2', role: 'status', 'aria-live': 'polite' });
  const consent = h('input', { type: 'checkbox', class: 'form-check-input', id: 'nwConsent' });
  const consentWrap = h('div', { class: 'alert alert-info py-2 mt-2 d-none', id: 'nwConsentWrap' },
    h('div', { class: 'form-check' }, consent,
      h('label', { class: 'form-check-label', for: 'nwConsent' }, 'Jag bekräftar att de som syns på bilderna har gett sitt samtycke till att bilderna publiceras. För barn under 18 år är det vårdnadshavarens samtycke som gäller.')),
    h('div', { class: 'small mt-1', id: 'nwConsentHint' }));
  const vis = h('select', { class: 'form-select w-auto', id: 'nwVis' },
    [[0, 'Alla, även utan inloggning'], [1, 'Alla inloggade'], [2, isRegion ? 'Medlemmar i kretsens klubbar' : 'Medlemmar i klubben'], [3, 'Styrelsen'], [4, 'Bara administratören']]
      .map(([v, t]) => h('option', { value: v, selected: v === 2 ? true : null }, t)));
  // Fäst överst och kommentarsläge (sparas med ett eget anrop, SetPostOptions, som aldrig rör Revision). Kommentarsvalet visas bara när föreningen slagit på kommentarer.
  const commentsEnabled = root.dataset.commentsEnabled === '1';
  const pinned = h('input', { type: 'checkbox', class: 'form-check-input', id: 'nwPinned' });
  const cmode = h('select', { class: 'form-select w-auto', id: 'nwComments' },
    [[0, 'Av'], [1, 'På'], [2, 'Stängd (går att läsa, inga nya)']].map(([v, t]) => h('option', { value: v }, t)));
  async function saveOptions() {
    const j = await post('SetPostOptions', { id: st.id, commentsMode: commentsEnabled ? cmode.value : (st.commentsMode || 0), pinned: pinned.checked ? 'true' : 'false' });
    if (j.success) st.commentsMode = parseInt(commentsEnabled ? cmode.value : (st.commentsMode || 0), 10);
    return j;
  }
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

  // ───── Galleriet ─────
  // Den första bilden är den stora och blir också listans omslag (en skrivväg: servern sätter omslaget ur galleriets första bild). Ordningen ändras med knappar, inte dra-och-släpp
  // (fungerar på telefon och för den som inte använder mus). Beskrivning krävs bara för huvudbilden; resten är valfri men uppmuntras.
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const thumbOf = id => '/umbraco/surface/SiteFile/Image?id=' + id + '&w=320';
  const needsConsent = () => st.gallery.some(x => x.showsPeople && !x.consented);

  function move(i, to) {
    if (to < 0 || to >= st.gallery.length) return;
    const [it] = st.gallery.splice(i, 1); st.gallery.splice(to, 0, it);
    st.dirty = true; renderGallery();
  }

  function refreshConsent() {
    const need = needsConsent();
    consentWrap.classList.toggle('d-none', !need);
    const hint = consentWrap.querySelector('#nwConsentHint');
    // Bilder på personer syns för alla som får läsa nyheten: påminn när nivån är bredare än föreningens medlemmar.
    hint.textContent = (need && (vis.value === '0' || vis.value === '1')) ? 'Nyheten är läsbar för fler än föreningens medlemmar. Överväg att välja "Medlemmar i klubben" under "Vem får läsa nyheten?" så länge bilderna visar personer.' : '';
  }

  function renderGallery() {
    const list = st.gallery;
    galleryCount.textContent = list.length + ' av ' + MAX_GALLERY + ' bilder';
    galleryBox.replaceChildren(...(list.length ? list.map((it, i) => {
      const altInp = h('input', { class: 'form-control form-control-sm', maxlength: '200', value: it.alt || '', 'aria-label': 'Beskrivning av bild ' + (i + 1), 'data-gallery-alt': String(i),
        placeholder: 'Beskriv bilden för den som inte ser den (valfritt)' });
      altInp.addEventListener('input', () => { it.alt = altInp.value; st.dirty = true; });
      const capInp = h('input', { class: 'form-control form-control-sm', maxlength: '200', value: it.caption || '', 'aria-label': 'Bildtext för bild ' + (i + 1), placeholder: 'Bildtext som visas vid bilden (valfritt)' });
      capInp.addEventListener('input', () => { it.caption = capInp.value; st.dirty = true; });
      const ppl = h('input', { type: 'checkbox', class: 'form-check-input', id: 'nwPpl' + i, checked: it.showsPeople ? true : null });
      ppl.addEventListener('change', () => { it.showsPeople = ppl.checked; st.dirty = true; refreshConsent(); });
      const btn = (label, icon, onclick, disabled) => h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', onclick, disabled: disabled ? true : null, 'aria-label': label + ' (bild ' + (i + 1) + ')' }, h('i', { class: 'bi ' + icon, 'aria-hidden': 'true' }), ' ', label);
      return h('div', { class: 'd-flex flex-wrap gap-3 border rounded p-2 mb-2', 'data-gallery-item': String(i) },
        h('div', { class: 'flex-shrink-0' }, h('img', { src: it.thumb || thumbOf(it.fileId), width: '96', class: 'rounded d-block', alt: '', style: 'height:auto' }),
          i === 0 ? h('span', { class: 'badge text-bg-primary mt-1' }, 'Huvudbild') : h('span', { class: 'small text-muted' }, 'Bild ' + (i + 1))),
        h('div', { class: 'flex-grow-1', style: 'min-width:14rem' }, h('div', { class: 'mb-1' }, altInp), h('div', { class: 'mb-1' }, capInp),
          h('div', { class: 'form-check' }, ppl, h('label', { class: 'form-check-label small', for: 'nwPpl' + i }, 'Bilden visar personer')),
          h('div', { class: 'd-flex flex-wrap gap-1 mt-2' },
            btn('Flytta upp', 'bi-arrow-up', () => move(i, i - 1), i === 0), btn('Flytta ner', 'bi-arrow-down', () => move(i, i + 1), i === list.length - 1),
            i > 0 ? btn('Gör till huvudbild', 'bi-star', () => move(i, 0)) : null,
            btn('Ta bort', 'bi-x-lg', () => { st.gallery.splice(i, 1); st.dirty = true; renderGallery(); }))));
    }) : [h('p', { class: 'text-muted small mb-2' }, 'Inga bilder ännu. Ladda upp bilder eller välj ur föreningens bilder.')]));
    refreshConsent();
  }

  // Laddar upp flera bilder: förminskas i webbläsaren, två åt gången (telefonen och servern ska inte svälja tjugo foton på en gång), ett fel per fil — de som gick bra behålls.
  // Bilderna läggs i galleriet i den ordning de valdes (inte i den ordning de blev klara).
  async function uploadMany(files) {
    let list = [...files]; if (!list.length) return;
    const room = MAX_GALLERY - st.gallery.length;
    queueBox.replaceChildren();
    if (room <= 0) { queueBox.append(h('div', { class: 'text-danger' }, 'Galleriet är fullt (' + MAX_GALLERY + ' bilder). Ta bort en bild först.')); return; }
    if (list.length > room) { queueBox.append(h('div', { class: 'text-warning-emphasis' }, 'Bara ' + room + ' av ' + list.length + ' bilder får plats (högst ' + MAX_GALLERY + '). De första ' + room + ' laddas upp.')); list = list.slice(0, room); }
    const rows = list.map(f => { const s = h('span', {}, 'Väntar …'); queueBox.append(h('div', {}, h('span', { class: 'text-muted' }, f.name + ': '), s)); return s; });
    const results = new Array(list.length);
    let next = 0;
    async function one(i) {
      rows[i].textContent = 'Förbereder …';
      const f = await prepareImage(list[i]);
      rows[i].textContent = 'Laddar upp …';
      let r = await uploadImage(f);
      if (!r.success && r.code === 'busy') { rows[i].textContent = 'Väntar på servern …'; await sleep(3000); r = await uploadImage(f); }
      if (!r.success) { rows[i].className = 'text-danger'; rows[i].textContent = r.message || 'Det gick inte att ladda upp bilden.'; return; }
      rows[i].className = 'text-success'; rows[i].textContent = 'Klar'; results[i] = r;
    }
    async function worker() { while (next < list.length) { const i = next++; await one(i); } }
    await Promise.all([worker(), worker()]);
    for (const r of results) {
      if (!r || st.gallery.length >= MAX_GALLERY || st.gallery.some(g => g.fileId === r.id)) continue;
      st.gallery.push({ fileId: r.id, alt: '', caption: '', showsPeople: false, consented: false, thumb: r.thumb });
    }
    st.dirty = true; renderGallery();
  }

  async function addFromLibrary() {
    if (st.gallery.length >= MAX_GALLERY) { queueBox.replaceChildren(h('div', { class: 'text-danger' }, 'Galleriet är fullt (' + MAX_GALLERY + ' bilder).')); return; }
    const f = await pickImage(); if (!f) return;
    queueBox.replaceChildren();
    if (st.gallery.some(g => g.fileId === f.id)) { queueBox.append(h('div', { class: 'text-danger' }, 'Den bilden ligger redan i galleriet.')); return; }
    st.gallery.push({ fileId: f.id, alt: '', caption: '', showsPeople: false, consented: false, thumb: f.url + '&w=320' });
    st.dirty = true; renderGallery();
  }

  function galleryCard() {
    const fileInp = h('input', { type: 'file', multiple: true, accept: 'image/jpeg,image/png,image/gif,image/webp', class: 'd-none', 'aria-label': 'Välj bilder att ladda upp' });
    fileInp.addEventListener('change', async () => { const fs = [...fileInp.files]; fileInp.value = ''; await uploadMany(fs); });
    return h('div', { class: 'mb-3' },
      h('div', { class: 'd-flex flex-wrap align-items-baseline gap-2 mb-1' }, h('div', { class: 'form-label fw-semibold mb-0' }, 'Bilder'), galleryCount),
      h('div', { class: 'form-text mb-2' }, 'Den första bilden är den stora och syns också i listan. Visa flera bilder på en gång: en stor och två små, och "+N" när det finns fler. Platsdata och annan dold information tas bort från bilderna när de laddas upp.'),
      galleryBox,
      h('div', { class: 'd-flex flex-wrap gap-2' },
        h('button', { type: 'button', class: 'btn btn-outline-primary', onclick: () => fileInp.click() }, h('i', { class: 'bi bi-upload me-1', 'aria-hidden': 'true' }), 'Ladda upp bilder'), fileInp,
        h('button', { type: 'button', class: 'btn btn-outline-secondary', onclick: addFromLibrary }, h('i', { class: 'bi bi-images me-1', 'aria-hidden': 'true' }), 'Välj ur föreningens bilder')),
      queueBox, consentWrap);
  }

  function formData() {
    return {
      title: title.value, bodyHtml: st.editor ? st.editor.getData() : '', excerpt: excerpt.value, visibility: vis.value, publishedDate: dateValue() || '',
      gallery: JSON.stringify(st.gallery.map(g => ({ fileId: g.fileId, alt: g.alt || '', caption: g.caption || '', showsPeople: !!g.showsPeople }))),
      consent: consent.checked ? 'true' : 'false',
    };
  }

  async function save(publish) {
    clearMsg();
    const d = formData();
    if (!d.title.trim()) { say('Skriv en rubrik.', 'danger'); title.focus(); return; }
    if (needsConsent() && !consent.checked) { say('Bekräfta att de som syns på bilderna har gett sitt samtycke.', 'danger'); consent.focus(); return; }
    if (dateValue() === null) { say('Datumet går inte att läsa. Skriv till exempel 16 maj 2027, eller välj i kalendern.', 'danger'); date.focus(); return; }
    setBusy(true);
    try {
      const j = await post('Save', Object.assign({ id: st.id, revision: st.revision }, d));
      if (!j.success) { say(j.message || 'Det gick inte att spara.', 'danger'); return; }
      apply(j.post);
      const o = await saveOptions();
      if (!o.success) { say(o.message || 'Nyheten sparades men fästningen och kommentarsläget gick inte att spara.', 'danger'); return; }
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
    // Servern är sanningen för galleriet efter en sparning (samtycket får sin tid och person där).
    if (Array.isArray(p.gallery)) { st.gallery = p.gallery.map(g => ({ fileId: g.fileId, alt: g.alt || '', caption: g.caption || '', showsPeople: !!g.showsPeople, consented: !!g.consented, thumb: g.thumb })); renderGallery(); }
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
          h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', onclick: insertImage }, h('i', { class: 'bi bi-image me-1', 'aria-hidden': 'true' }), 'Infoga bild'), sigUi && sigUi.node, widthBar()),
        bodyHost),
      field('Kort sammanfattning (valfri)', 'nwExcerpt', excerpt, 'Visas i listan. Lämnar du den tom tas början av texten.'),
      galleryCard(),
      field('Vem får läsa nyheten?', 'nwVis', vis, 'Nyheten syns bara för dem på den nivån och över. Välj "Alla" bara för sådant som är offentligt.'),
      field('Datum som visas (valfritt)', 'nwDate', date, 'Tomt = dagen du publicerar.'),
      h('div', { class: 'mb-3 form-check' }, pinned, h('label', { class: 'form-check-label', for: 'nwPinned' }, 'Fäst överst i listan'), h('div', { class: 'form-text' }, 'En fäst nyhet står först i arkivet tills du tar bort fästningen.')),
      commentsEnabled ? field('Kommentarer', 'nwComments', cmode, 'På: klubbens medlemmar kan kommentera. Stängd: kommentarerna går att läsa men inga nya kan skrivas. Klubbens styrelse ansvarar för kommentarerna.') : null,
      msg, actions, mailBox);
    renderGallery();
    vis.addEventListener('change', refreshConsent);
    if (existing) {
      title.value = existing.title || ''; excerpt.value = existing.excerpt || ''; vis.value = String(existing.visibility);
      pinned.checked = !!existing.isPinned; st.commentsMode = existing.commentsMode || 0; cmode.value = String(st.commentsMode);
      st.gallery = (existing.gallery || []).map(g => ({ fileId: g.fileId, alt: g.alt || '', caption: g.caption || '', showsPeople: !!g.showsPeople, consented: !!g.consented, thumb: g.thumb }));
      renderGallery();
      if (existing.publishedDate) setDate(String(existing.publishedDate).slice(0, 10));
      apply(existing);
    } else {
      // En NY nyhet får kommentarer På som förval när klubben slagit på dem (skribenten kan ändra till Av eller Stängd). Äldre nyheter lämnas som de är.
      if (commentsEnabled) cmode.value = '1';
      renderActions();
    }
    if (!window.ClassicEditor) { bodyHost.append(h('p', { class: 'text-danger' }, 'Textredigeraren kunde inte laddas. Ladda om sidan.')); return; }
    ClassicEditor.create(bodyHost, {
      toolbar: ['heading', '|', 'bold', 'italic', 'link', 'bulletedList', 'numberedList', 'blockQuote', 'insertTable', '|', 'undo', 'redo'],
      heading: { options: [{ model: 'paragraph', title: 'Vanlig text', class: 'ck-heading_paragraph' }, { model: 'heading2', view: 'h2', title: 'Rubrik', class: 'ck-heading_heading2' },
                           { model: 'heading3', view: 'h3', title: 'Underrubrik', class: 'ck-heading_heading3' }] },
      // Bilder: bara ur föreningens egna bilder (knappen Infoga bild). Placering och beskrivning väljs på bilden, storleken med knapparna ovanför textrutan.
      removePlugins: ['ImageCaption', 'ImageUpload', 'EasyImage', 'CKFinder', 'CKFinderUploadAdapter', 'CKBox', 'CKBoxUI', 'CKBoxEditing', 'CKBoxUtils', 'CKBoxImageEdit', 'MediaEmbed'],
      image: { toolbar: ['imageStyle:alignLeft', 'imageStyle:alignCenter', 'imageStyle:alignRight', '|', 'imageTextAlternative'], styles: { options: ['alignLeft', 'alignCenter', 'alignRight'] } },
      extraPlugins: SIG ? [SiteImageWidth, SIG.plugin] : [SiteImageWidth],
    }).then(ed => {
      st.editor = ed;
      ed.setData(existing ? (existing.bodyHtml || '') : '');
      if (sigUi) sigUi.bind(ed, existing ? (existing.bodyHtml || '') : '');
      ed.model.document.on('change:data', () => { st.dirty = true; });
    }).catch(e => { console.error('CKEditor kunde inte startas:', e && (e.message || e)); bodyHost.append(h('p', { class: 'text-danger' }, 'Textredigeraren kunde inte startas.')); });
    window.addEventListener('beforeunload', ev => { if (st.dirty) { ev.preventDefault(); ev.returnValue = ''; } });
  }
  init();
})();
