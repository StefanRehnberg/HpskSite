/* Signaturen i textrutan (Hemsidans textblock och nyhetsskrivningen). Delas av hemsidan.js och site-news-edit.js, så att de beter sig likadant.

   I texten lagras bara ett medlemsnummer: <hpsk-signatur member="123"></hpsk-signatur>. Namn, bild och roll hämtas när sidan visas (SiteSignatures på
   servern), så de följer med när personen byter bild eller får en ny roll. Här i redigeraren visas signaturen som en ruta (avatar, namn, roll) som går att
   markera och ta bort men inte skriva i. Man kan bara skriva under som sig själv: servern tar bort andras nummer vid sparning.

   Användning:
     const sig = HpskSignature.create({ get: ids => Promise<{ success, me, reason, items }> });
     ClassicEditor.create(node, { extraPlugins: [sig.plugin] }).then(ed => { ...; const c = sig.controls(h); c.bind(ed, html); });
   c.node är knappen Infoga signatur med en hjälprad. Inga prompt/confirm/alert. */
(function (global) {
  'use strict';
  const TAG = 'hpsk-signatur';
  const ID_RX = /<hpsk-signatur member="(\d{1,9})"/g;

  const idsIn = html => { const out = new Set(); let m; ID_RX.lastIndex = 0; while ((m = ID_RX.exec(html || ''))) out.add(parseInt(m[1], 10)); return [...out]; };
  const safePicture = u => typeof u === 'string' && u.length <= 500 && ((u.startsWith('/') && !u.startsWith('//')) || /^https:\/\//i.test(u));

  /** Fyller ett element med samma uppbyggnad som servern ritar (.hs-sig), eller en dämpad rad när medlemmen inte får visas. Bara textNode/DOM, aldrig innerHTML. */
  function fill(el, info) {
    el.textContent = '';
    if (!info) {
      const p = document.createElement('span');
      p.className = 'hs-sig-missing';
      p.textContent = info === undefined ? 'Hämtar signaturen…' : 'Signaturen visas inte (personen hör inte till föreningen).';
      el.append(p);
      return;
    }
    const wrap = document.createElement('div'); wrap.className = 'hs-sig';
    const av = document.createElement('span'); av.className = 'hs-sig-av'; av.setAttribute('aria-hidden', 'true');
    av.append(document.createTextNode(info.initials || '?'));
    if (safePicture(info.picture)) {
      const img = document.createElement('img'); img.alt = ''; img.src = info.picture;
      img.addEventListener('error', () => img.remove());
      av.append(img);
    }
    const who = document.createElement('span'); who.className = 'hs-sig-who';
    const nm = document.createElement('span'); nm.className = 'hs-sig-name'; nm.textContent = info.name || '';
    who.append(nm);
    if (info.role) { const rl = document.createElement('span'); rl.className = 'hs-sig-role'; rl.textContent = info.role; who.append(rl); }
    wrap.append(av, who);
    el.append(wrap);
  }

  /**
   * Gör ett element till en markerbar ruta för CKEditors Widget-tillägg. Det färdiga bygget (CDN) exponerar inte toWidget(), så de egenskaper den sätter
   * sätts här: "widget", etiketten (en lista som tillägget fyller på), inget fyllnadstecken, och hur markeringen ritas. Utan dem kastar tillägget fel vid inläsning.
   */
  function asWidget(writer, el, label) {
    writer.setCustomProperty('widget', true, el);
    writer.setCustomProperty('widgetLabel', label ? [label] : [], el);
    writer.setCustomProperty('addHighlight', (element, descriptor, w) => w.addClass(descriptor.classes, element), el);
    writer.setCustomProperty('removeHighlight', (element, descriptor, w) => w.removeClass(descriptor.classes, element), el);
    el.getFillerOffset = () => null;
    writer.addClass('ck-widget', el);
    writer.setAttribute('contenteditable', 'false', el);
  }

  function create(opts) {
    const cache = new Map();   // medlemsnummer -> info, eller null när medlemmen inte får visas
    const state = { me: null, reason: '', loaded: false, editors: new Set() };

    async function load(ids) {
      const need = ids.filter(i => !cache.has(i));
      let r = null;
      try { r = await opts.get(need); } catch { r = null; }
      if (!r || !r.success) return false;
      state.me = r.me || null; state.reason = r.reason || ''; state.loaded = true;
      if (r.me) cache.set(r.me.memberId, r.me);
      for (const it of (r.items || [])) cache.set(it.memberId, it);
      for (const i of need) if (!cache.has(i)) cache.set(i, null);
      return true;
    }

    function repaint(ed) {
      const root = ed.editing.view.getDomRoot();
      if (!root) return;
      root.querySelectorAll('.hs-sig-edit').forEach(box => {
        const body = box.querySelector('.hs-sig-edit-body');
        const id = parseInt(box.getAttribute('data-sig-member'), 10);
        if (body) fill(body, cache.has(id) ? cache.get(id) : undefined);
      });
    }

    class SiteSignature {
      constructor(editor) { this.editor = editor; }
      init() {
        const ed = this.editor;
        ed.model.schema.register('siteSignature', { inheritAllFrom: '$blockObject', allowAttributes: ['memberId'] });
        // Texten → modellen: bara ett giltigt nummer blir en signatur.
        ed.conversion.for('upcast').elementToElement({
          view: { name: TAG },
          model: (view, { writer }) => { const id = parseInt(view.getAttribute('member'), 10); return id > 0 ? writer.createElement('siteSignature', { memberId: id }) : null; },
        });
        // Modellen → sparad HTML: bara elementet och numret.
        ed.conversion.for('dataDowncast').elementToElement({
          model: 'siteSignature',
          // Ett tomt element (inget fyllnadstecken inuti): <hpsk-signatur member="123"></hpsk-signatur>.
          view: (m, { writer }) => writer.createEmptyElement(TAG, { member: String(m.getAttribute('memberId')) }),
        });
        // Modellen → rutan i redigeraren: en markerbar, ej skrivbar ruta (samma markering som en bild: Widget-tillägget känner igen egenskapen "widget").
        ed.conversion.for('editingDowncast').elementToElement({
          model: 'siteSignature',
          view: (m, { writer }) => {
            const id = m.getAttribute('memberId');
            const box = writer.createContainerElement('div', { class: 'hs-sig-edit', 'data-sig-member': String(id) });
            const ui = writer.createUIElement('div', { class: 'hs-sig-edit-body' }, function (domDocument) {
              const el = this.toDomElement(domDocument);
              fill(el, cache.has(id) ? cache.get(id) : undefined);
              return el;
            });
            writer.insert(writer.createPositionAt(box, 0), ui);
            asWidget(writer, box, 'Signatur');
            return box;
          },
        });
      }
    }

    /** Knappen Infoga signatur med sin hjälprad. `h` är sidans egen elementbyggare. bind() kopplar den till textrutan och hämtar vem man är. */
    function controls(h) {
      const btn = h('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', 'data-sig-insert': '1' },
        h('i', { class: 'bi bi-person-badge me-1', 'aria-hidden': 'true' }), 'Infoga signatur');
      btn.disabled = true;
      const hint = h('span', { class: 'small text-muted ms-1', role: 'status' });
      const node = h('span', { class: 'd-inline-flex align-items-center' }, btn, hint);
      function bind(ed, html) {
        state.editors.add(ed);
        load(idsIn(html)).then(ok => {
          if (!ok) { hint.textContent = 'Signaturen kunde inte hämtas.'; return; }
          repaint(ed);
          if (state.me) { btn.disabled = false; btn.title = 'Lägger in ditt namn, din bild och din roll'; }
          else hint.textContent = state.reason;
        });
        btn.addEventListener('click', () => {
          if (!state.me) return;
          cache.set(state.me.memberId, state.me);
          ed.model.change(w => { ed.model.insertObject(w.createElement('siteSignature', { memberId: state.me.memberId }), null, null, { setSelection: 'after' }); });
          ed.editing.view.focus();
        });
      }
      return { node, bind };
    }

    return { plugin: SiteSignature, controls, _fill: fill };
  }

  global.HpskSignature = { create, idsIn };
})(window);
