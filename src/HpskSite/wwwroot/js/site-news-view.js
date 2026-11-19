/* Nyhetens sida (/{ägare}/nyheter/{adress}/): reaktioner, bildvisning (lightbox) och "ta bort min bild". All data går via SiteNewsReactionsController / SiteNewsController, som prövar ägaren,
   inloggningen och läsrätten vid varje anrop — den här filen avgör inget om behörighet, den visar bara. Inga prompt/confirm/alert. */
(function () {
  'use strict';
  const S = '/umbraco/surface/SiteNewsReactions/';
  const tokenEl = () => document.querySelector('input[name=__RequestVerificationToken]');

  async function post(action, data, controller) {
    const fd = new FormData();
    for (const [k, v] of Object.entries(data || {})) if (v !== null && v !== undefined) fd.append(k, v);
    const t = tokenEl();
    try {
      const r = await fetch((controller || S) + action, { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': t ? t.value : '' }, body: fd });
      return await r.json();
    } catch { return { success: false, message: 'Det gick inte att nå servern. Kontrollera uppkopplingen och försök igen.' }; }
  }

  // ───── Reaktioner ─────
  document.querySelectorAll('[data-sn-react]').forEach(root => {
    const ids = { ownerType: root.dataset.ownerType, ownerId: root.dataset.ownerId, postId: root.dataset.postId };
    const picker = root.querySelector('[data-sn-picker]'), moreBtn = root.querySelector('[data-sn-more]'), err = root.querySelector('[data-sn-error]');
    const counts = root.querySelector('[data-sn-counts]');
    const labels = { heart: ['❤️', 'Hjärta'], thumbs: ['👍', 'Tumme upp'], fire: ['🔥', 'Eld'], sad: ['😢', 'Ledsen'], target: ['🎯', 'Prickskytt'] };
    let busy = false;

    function showErr(m) { if (!err) return; err.textContent = m || ''; err.classList.toggle('d-none', !m); }
    function closePicker() { if (picker) { picker.hidden = true; moreBtn.setAttribute('aria-expanded', 'false'); } }

    function paint(state) {
      root.dataset.mine = state.mine || '';
      const heart = root.querySelector('.sn-heart');
      if (heart) {
        const on = state.mine === 'heart';
        heart.setAttribute('aria-pressed', on ? 'true' : 'false');
        heart.classList.toggle('btn-primary', on); heart.classList.toggle('btn-outline-secondary', !on);
        heart.querySelector('[data-sn-heart-label]').textContent = on ? 'Du gillar' : 'Gilla';
      }
      root.querySelectorAll('.sn-emoji').forEach(b => b.setAttribute('aria-pressed', state.mine === b.dataset.snKind ? 'true' : 'false'));
      counts.replaceChildren();
      for (const k of Object.keys(labels)) {
        const n = (state.counts || {})[k] || 0; if (!n) continue;
        const el = document.createElement('span');
        el.className = 'badge rounded-pill text-bg-light border'; el.dataset.snCount = k; el.title = labels[k][1];
        const em = document.createElement('span'); em.setAttribute('aria-hidden', 'true'); em.textContent = labels[k][0];
        const vh = document.createElement('span'); vh.className = 'visually-hidden'; vh.textContent = ' ' + labels[k][1];
        el.append(em, ' ' + n, vh);
        counts.append(el);
      }
    }

    async function choose(kind) {
      if (busy) return; busy = true; showErr('');
      // Samma emoji igen = ta bort (som i Träningsmatcherna). Servern har två explicita skrivningar, aldrig en växling.
      const same = root.dataset.mine === kind;
      const r = await post(same ? 'Clear' : 'React', Object.assign({}, ids, same ? {} : { kind }));
      busy = false;
      if (!r.success) { showErr(r.message || 'Det gick inte att spara din reaktion.'); return; }
      paint(r); closePicker();
    }

    root.querySelectorAll('[data-sn-kind]').forEach(b => b.addEventListener('click', () => choose(b.dataset.snKind)));
    if (moreBtn) {
      moreBtn.addEventListener('click', e => { e.stopPropagation(); const open = picker.hidden; picker.hidden = !open; moreBtn.setAttribute('aria-expanded', open ? 'true' : 'false'); if (open) { const f = picker.querySelector('button'); if (f) f.focus(); } });
      document.addEventListener('click', e => { if (picker && !picker.hidden && !picker.contains(e.target)) closePicker(); });
      document.addEventListener('keydown', e => { if (e.key === 'Escape' && picker && !picker.hidden) { closePicker(); moreBtn.focus(); } });
    }
  });

  // ───── Lightbox ─────
  document.querySelectorAll('[data-sn-gallery]').forEach(gal => {
    let images = [];
    try { images = JSON.parse(gal.querySelector('[data-sn-images]').textContent) || []; } catch { images = []; }
    if (!images.length) return;
    const ids = { ownerType: gal.dataset.ownerType, ownerId: gal.dataset.ownerId, postId: gal.dataset.postId };
    const canRequest = gal.dataset.canRequest === '1';

    function open(index, opener) {
      let i = index; const prevFocus = opener || document.activeElement;
      const el = document.createElement('div');
      el.className = 'sn-lb'; el.setAttribute('role', 'dialog'); el.setAttribute('aria-modal', 'true'); el.setAttribute('aria-label', 'Bildvisning');
      el.innerHTML = '<div class="sn-lb-bar"><span class="sn-lb-count" aria-live="polite"></span><button type="button" class="sn-lb-btn sn-lb-close" aria-label="Stäng bildvisningen">✕</button></div>' +
        '<div class="sn-lb-stage"><button type="button" class="sn-lb-btn sn-lb-prev" aria-label="Föregående bild">‹</button><img class="sn-lb-img" alt="">' +
        '<button type="button" class="sn-lb-btn sn-lb-next" aria-label="Nästa bild">›</button></div><div class="sn-lb-foot"><div class="sn-lb-cap"></div><div class="sn-lb-req"></div></div>';
      document.body.append(el); document.body.classList.add('sn-lb-open');
      const img = el.querySelector('.sn-lb-img'), cap = el.querySelector('.sn-lb-cap'), count = el.querySelector('.sn-lb-count'), reqBox = el.querySelector('.sn-lb-req');
      const closeBtn = el.querySelector('.sn-lb-close'), prev = el.querySelector('.sn-lb-prev'), next = el.querySelector('.sn-lb-next');

      function show() {
        const im = images[i];
        img.src = im.url; img.alt = im.alt || '';
        cap.textContent = im.caption || '';
        count.textContent = (i + 1) + ' av ' + images.length;
        prev.hidden = next.hidden = images.length < 2;
        reqBox.replaceChildren();
        if (canRequest && im.people) {
          const b = document.createElement('button'); b.type = 'button'; b.className = 'btn btn-link btn-sm'; b.textContent = 'Jag syns på den här bilden och vill att den tas bort';
          b.addEventListener('click', () => requestForm(im));
          reqBox.append(b);
        }
        const n = images[(i + 1) % images.length]; if (n && images.length > 1) new Image().src = n.url;   // förladda nästa
      }
      function requestForm(im) {
        reqBox.replaceChildren();
        const wrap = document.createElement('div'); wrap.className = 'd-flex flex-wrap gap-2 justify-content-center align-items-center';
        const inp = document.createElement('input'); inp.type = 'text'; inp.maxLength = 300; inp.className = 'form-control form-control-sm w-auto'; inp.style.minWidth = '16rem'; inp.placeholder = 'Valfritt: skriv gärna varför'; inp.setAttribute('aria-label', 'Meddelande till skribenterna');
        const send = document.createElement('button'); send.type = 'button'; send.className = 'btn btn-light btn-sm'; send.textContent = 'Skicka förfrågan';
        const msg = document.createElement('span'); msg.setAttribute('role', 'status');
        send.addEventListener('click', async () => {
          send.disabled = true;
          const r = await post('RequestImageRemoval', Object.assign({}, ids, { fileId: im.fileId, message: inp.value }));
          msg.textContent = r.success ? 'Förfrågan är skickad. Skribenterna ser den på nyhetens sida.' : (r.message || 'Det gick inte att skicka förfrågan.');
          if (r.success) { inp.remove(); send.remove(); } else send.disabled = false;
        });
        wrap.append(inp, send, msg); reqBox.append(wrap); inp.focus();
      }
      function step(d) { i = (i + d + images.length) % images.length; show(); }
      function close() { document.removeEventListener('keydown', onKey, true); el.remove(); document.body.classList.remove('sn-lb-open'); if (prevFocus && prevFocus.focus) prevFocus.focus(); }
      function onKey(e) {
        if (e.key === 'Escape') { e.preventDefault(); close(); return; }
        if (e.target && e.target.tagName === 'INPUT') return;   // piltangenterna hör till textfältet när man skriver
        if (e.key === 'ArrowLeft') { e.preventDefault(); step(-1); }
        else if (e.key === 'ArrowRight') { e.preventDefault(); step(1); }
        else if (e.key === 'Tab') {   // fokusfälla: Tab stannar i bildvisningen
          const f = [...el.querySelectorAll('button:not([hidden]), input')].filter(x => !x.disabled);
          if (!f.length) return; const first = f[0], last = f[f.length - 1];
          if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
          else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
          else if (!el.contains(document.activeElement)) { e.preventDefault(); first.focus(); }
        }
      }
      let x0 = null;
      el.addEventListener('touchstart', e => { x0 = e.changedTouches[0].clientX; }, { passive: true });
      el.addEventListener('touchend', e => { if (x0 === null) return; const dx = e.changedTouches[0].clientX - x0; x0 = null; if (Math.abs(dx) > 50 && images.length > 1) step(dx < 0 ? 1 : -1); });
      el.addEventListener('click', e => { if (e.target === el || e.target.classList.contains('sn-lb-stage')) close(); });
      closeBtn.addEventListener('click', close); prev.addEventListener('click', () => step(-1)); next.addEventListener('click', () => step(1));
      document.addEventListener('keydown', onKey, true);
      show(); closeBtn.focus();
    }
    gal.querySelectorAll('[data-sn-index]').forEach(b => b.addEventListener('click', () => open(parseInt(b.dataset.snIndex, 10) || 0, b)));
  });

  // ───── Kommentarer ─────
  // All data går via SiteNewsCommentsController (prövar ägaren, medlemskapet och läsrätten vid varje anrop). Efter en lyckad skrivning laddas sidan om vid #kommentarer — då ritar
  // servern listan, och den här filen behöver aldrig bygga kommentarer av text. Inga prompt/confirm/alert.
  document.querySelectorAll('[data-sn-comments]').forEach(root => {
    const C = '/umbraco/surface/SiteNewsComments/';
    const ids = { ownerType: root.dataset.ownerType, ownerId: root.dataset.ownerId, postId: root.dataset.postId };
    const listErr = root.querySelector('[data-sn-c-listerror]');
    const reload = () => { location.hash = 'kommentarer'; location.reload(); };
    const showListErr = m => { if (!listErr) return; listErr.textContent = m || ''; listErr.classList.toggle('d-none', !m); };

    const form = root.querySelector('[data-sn-c-form]');
    if (form) {
      const ta = form.querySelector('[data-sn-c-body]'), count = form.querySelector('[data-sn-c-count]'), err = form.querySelector('[data-sn-c-error]'), send = form.querySelector('[data-sn-c-send]');
      ta.addEventListener('input', () => { count.textContent = ta.value.length; });
      form.addEventListener('submit', async e => {
        e.preventDefault(); err.classList.add('d-none');
        send.disabled = true;
        const r = await post('Add', Object.assign({}, ids, { body: ta.value }), C);
        if (r.success) { reload(); return; }
        send.disabled = false; err.textContent = r.message || 'Det gick inte att skicka kommentaren.'; err.classList.remove('d-none');
      });
    }

    root.querySelectorAll('[data-sn-comment]').forEach(li => {
      const commentId = li.dataset.snComment, slot = li.querySelector('[data-sn-c-slot]');
      const act = (action, extra) => post(action, Object.assign({}, ids, { commentId }, extra || {}), C);
      const on = (sel, fn) => { const b = li.querySelector(sel); if (b) b.addEventListener('click', fn); };
      const msg = (text, ok) => { slot.replaceChildren(); const s = document.createElement('span'); s.className = ok ? 'text-success' : 'text-danger'; s.setAttribute('role', 'status'); s.textContent = text; slot.append(s); };

      // Ändra (egen kommentar, 15 minuter): textrutan fylls med den nuvarande texten; servern prövar fönstret.
      on('[data-sn-c-edit]', () => {
        const textEl = li.querySelector('[data-sn-c-text]'); slot.replaceChildren();
        const ta = document.createElement('textarea'); ta.className = 'form-control form-control-sm'; ta.rows = 3; ta.maxLength = 2000; ta.value = textEl.textContent; ta.setAttribute('aria-label', 'Ändra din kommentar');
        const save = document.createElement('button'); save.type = 'button'; save.className = 'btn btn-primary btn-sm me-2 mt-1'; save.textContent = 'Spara';
        const cancel = document.createElement('button'); cancel.type = 'button'; cancel.className = 'btn btn-outline-secondary btn-sm mt-1'; cancel.textContent = 'Avbryt';
        cancel.addEventListener('click', () => slot.replaceChildren());
        save.addEventListener('click', async () => {
          save.disabled = true;
          const r = await act('Edit', { body: ta.value });
          if (r.success) reload(); else { save.disabled = false; msg(r.message || 'Det gick inte att spara.', false); slot.prepend(ta); }
        });
        slot.append(ta, save, cancel); ta.focus();
      });

      // Ta bort: två klick i stället för en dialog.
      const del = li.querySelector('[data-sn-c-delete]');
      if (del) {
        let armed = false, timer = null;
        del.addEventListener('click', async () => {
          if (!armed) { armed = true; del.textContent = 'Klicka igen för att ta bort'; timer = setTimeout(() => { armed = false; del.textContent = 'Ta bort'; }, 5000); return; }
          clearTimeout(timer); del.disabled = true;
          const r = await act('Delete');
          if (r.success) reload(); else { del.disabled = false; armed = false; del.textContent = 'Ta bort'; showListErr(r.message || 'Det gick inte att ta bort kommentaren.'); }
        });
      }

      for (const [sel, action] of [['[data-sn-c-approve]', 'Approve'], ['[data-sn-c-restore]', 'Restore'], ['[data-sn-c-handle]', 'HandleReport']])
        on(sel, async e => { e.target.disabled = true; const r = await act(action); if (r.success) reload(); else { e.target.disabled = false; showListErr(r.message || 'Det gick inte.'); } });

      // Anmäl: ett valfritt skäl, ett klick till. Anmälaren syns aldrig för den som anmäls (och mejlet till administratörerna avslöjar inte heller vem).
      on('[data-sn-c-report]', e => {
        const opener = e.target; slot.replaceChildren();
        const inp = document.createElement('input'); inp.type = 'text'; inp.maxLength = 300; inp.className = 'form-control form-control-sm d-inline-block w-auto'; inp.style.minWidth = '16rem'; inp.placeholder = 'Valfritt: skriv gärna varför'; inp.setAttribute('aria-label', 'Varför anmäler du kommentaren?');
        const send = document.createElement('button'); send.type = 'button'; send.className = 'btn btn-outline-danger btn-sm ms-2'; send.textContent = 'Skicka anmälan';
        const cancel = document.createElement('button'); cancel.type = 'button'; cancel.className = 'btn btn-link btn-sm'; cancel.textContent = 'Avbryt';
        cancel.addEventListener('click', () => { slot.replaceChildren(); opener.focus(); });
        send.addEventListener('click', async () => {
          send.disabled = true;
          const r = await act('Report', { reason: inp.value });
          if (r.success) { msg('Tack. Klubbens administratörer har fått din anmälan.', true); opener.remove(); } else { send.disabled = false; msg(r.message || 'Det gick inte att skicka anmälan.', false); slot.prepend(inp); slot.append(send, cancel); }
        });
        slot.append(inp, send, cancel); inp.focus();
      });
    });
  });

  // ───── Skribenterna: markera en förfrågan som hanterad ─────
  document.querySelectorAll('[data-sn-requests]').forEach(box => {
    box.querySelectorAll('[data-sn-handled]').forEach(btn => btn.addEventListener('click', async () => {
      btn.disabled = true;
      const r = await post('HandleImageRequest', { ownerType: box.dataset.ownerType, ownerId: box.dataset.ownerId, requestId: btn.dataset.snHandled }, '/umbraco/surface/SiteNews/');
      if (r.success) { const row = box.querySelector('[data-sn-request="' + btn.dataset.snHandled + '"]'); if (row) row.remove(); if (!box.querySelector('[data-sn-request]')) box.remove(); }
      else { btn.disabled = false; btn.textContent = r.message || 'Det gick inte'; }
    }));
  });
})();
