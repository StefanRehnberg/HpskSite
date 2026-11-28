/* /pqt — gilla, kommentarer och intresseanmälan för märken.
   Skriptet ritar inget om vem som får göra vad: kommentarsdelen hämtas som färdig HTML (GET /pqt/kommentarer) efter varje åtgärd, och servern (PqtController) prövar allt
   en gång till. Felmeddelanden kommer från servern och visas som de är. */
(function () {
  'use strict';
  var root = document.getElementById('pqt-root');
  if (!root) return;

  function token() {
    var el = root.querySelector('input[name="__RequestVerificationToken"]');
    return el ? el.value : '';
  }

  /** POST med formulärfält. Ger alltid ett objekt {success, message, ...}; nätverksfel blir ett vanligt felmeddelande. */
  async function post(url, fields) {
    var fd = new FormData();
    Object.keys(fields || {}).forEach(function (k) { fd.append(k, fields[k]); });
    try {
      var r = await fetch(url, { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': token() }, body: fd });
      if (!r.ok) return { success: false, message: 'Något gick fel (' + r.status + '). Ladda om sidan och försök igen.' };
      return await r.json();
    } catch (e) {
      return { success: false, message: 'Det gick inte att nå servern. Kontrollera uppkopplingen och försök igen.' };
    }
  }

  function say(el, text, kind) {
    if (!el) return;
    el.textContent = text || '';
    el.classList.toggle('is-error', kind === 'error');
    el.classList.toggle('is-ok', kind === 'ok');
  }

  async function refreshComments() {
    var box = document.getElementById('pqt-comments');
    if (!box) return;
    try {
      var r = await fetch('/pqt/kommentarer', { credentials: 'same-origin' });
      if (r.ok) box.innerHTML = await r.text();
    } catch (e) { /* kvar som den var */ }
  }

  // ── Gilla ───────────────────────────────────────────────────────────────
  var likeBtn = document.getElementById('pqt-like');
  var likeBar = document.getElementById('pqt-like-bar');
  if (likeBtn) {
    likeBtn.addEventListener('click', async function () {
      var msg = document.getElementById('pqt-like-msg');
      if (likeBar.getAttribute('data-logged-in') !== '1') { say(msg, 'Logga in för att gilla.', 'error'); return; }
      var liked = likeBar.getAttribute('data-liked') === '1';
      likeBtn.disabled = true;
      var res = await post(liked ? '/pqt/ogilla' : '/pqt/gilla');
      likeBtn.disabled = false;
      if (!res.success) { say(msg, res.message, 'error'); return; }
      likeBar.setAttribute('data-liked', res.liked ? '1' : '0');
      likeBtn.setAttribute('aria-pressed', res.liked ? 'true' : 'false');
      likeBtn.classList.toggle('btn-primary', res.liked);
      likeBtn.classList.toggle('btn-outline-primary', !res.liked);
      likeBtn.querySelector('i').className = 'bi ' + (res.liked ? 'bi-heart-fill' : 'bi-heart');
      document.getElementById('pqt-like-label').textContent = res.liked ? 'Du gillar PQT' : 'Gilla';
      document.getElementById('pqt-like-count').textContent = res.count;
      say(msg, '');
    });
  }

  // ── Kommentarer ─────────────────────────────────────────────────────────
  var commentsBox = document.getElementById('pqt-comments');

  async function addComment() {
    var ta = document.getElementById('pqt-new-body');
    var msg = document.getElementById('pqt-comment-msg');
    var btn = document.getElementById('pqt-add-comment');
    btn.disabled = true;
    var res = await post('/pqt/kommentar', { body: ta.value });
    btn.disabled = false;
    if (!res.success) { say(msg, res.message, 'error'); return; }
    var pending = res.pending;
    await refreshComments();
    var m = document.getElementById('pqt-comment-msg');
    say(m, pending ? 'Tack. Din kommentar syns för andra när den har godkänts.' : 'Kommentaren är publicerad.', 'ok');
  }

  function openEdit(li) {
    var p = li.querySelector('[data-body]');
    if (!p || li.querySelector('.pqt-inline')) return;
    var form = document.createElement('div');
    form.className = 'pqt-inline';
    var ta = document.createElement('textarea');
    ta.className = 'form-control';
    ta.rows = 3;
    ta.maxLength = 2000;
    ta.value = p.textContent;
    ta.setAttribute('aria-label', 'Ändra kommentaren');
    var row = document.createElement('div');
    row.className = 'd-flex flex-wrap gap-2 align-items-center';
    var save = document.createElement('button'); save.type = 'button'; save.className = 'btn btn-primary btn-sm'; save.textContent = 'Spara';
    var cancel = document.createElement('button'); cancel.type = 'button'; cancel.className = 'btn btn-outline-secondary btn-sm'; cancel.textContent = 'Avbryt';
    var msg = document.createElement('span'); msg.className = 'pqt-msg'; msg.setAttribute('role', 'status');
    row.append(save, cancel, msg);
    form.append(ta, row);
    p.hidden = true;
    p.after(form);
    ta.focus();
    cancel.addEventListener('click', function () { form.remove(); p.hidden = false; });
    save.addEventListener('click', async function () {
      save.disabled = true;
      var res = await post('/pqt/kommentar/andra', { commentId: li.getAttribute('data-comment-id'), body: ta.value });
      save.disabled = false;
      if (!res.success) { say(msg, res.message, 'error'); return; }
      await refreshComments();
    });
  }

  function openReport(li) {
    if (li.querySelector('.pqt-inline')) return;
    var form = document.createElement('div');
    form.className = 'pqt-inline';
    var input = document.createElement('input');
    input.type = 'text'; input.className = 'form-control form-control-sm'; input.maxLength = 300;
    input.placeholder = 'Varför anmäler du kommentaren? (valfritt)';
    input.setAttribute('aria-label', 'Varför anmäler du kommentaren?');
    var row = document.createElement('div');
    row.className = 'd-flex flex-wrap gap-2 align-items-center';
    var send = document.createElement('button'); send.type = 'button'; send.className = 'btn btn-danger btn-sm'; send.textContent = 'Skicka anmälan';
    var cancel = document.createElement('button'); cancel.type = 'button'; cancel.className = 'btn btn-outline-secondary btn-sm'; cancel.textContent = 'Avbryt';
    var msg = document.createElement('span'); msg.className = 'pqt-msg'; msg.setAttribute('role', 'status');
    row.append(send, cancel, msg);
    form.append(input, row);
    li.append(form);
    input.focus();
    cancel.addEventListener('click', function () { form.remove(); });
    send.addEventListener('click', async function () {
      send.disabled = true;
      var res = await post('/pqt/kommentar/anmal', { commentId: li.getAttribute('data-comment-id'), reason: input.value });
      send.disabled = false;
      if (!res.success) { say(msg, res.message, 'error'); return; }
      say(msg, 'Tack. Administratören har fått din anmälan.', 'ok');
      send.hidden = true; cancel.hidden = true;
    });
  }

  if (commentsBox) {
    commentsBox.addEventListener('click', async function (ev) {
      var t = ev.target;
      if (t.id === 'pqt-add-comment') { addComment(); return; }
      var btn = t.closest ? t.closest('button[data-act]') : null;
      if (!btn) return;
      var li = btn.closest('li[data-comment-id]');
      if (!li) return;
      var id = li.getAttribute('data-comment-id');
      var act = btn.getAttribute('data-act');
      if (act === 'edit') { openEdit(li); return; }
      if (act === 'report') { openReport(li); return; }
      if (act === 'delete' && !window.confirm('Ta bort kommentaren?')) return;
      var url = { delete: '/pqt/kommentar/ta-bort', restore: '/pqt/kommentar/aterstall', approve: '/pqt/kommentar/godkann', handled: '/pqt/kommentar/hanterad' }[act];
      if (!url) return;
      btn.disabled = true;
      var res = await post(url, { commentId: id });
      if (!res.success) {
        btn.disabled = false;
        var m = li.querySelector('.pqt-msg');
        if (!m) { m = document.createElement('span'); m.className = 'pqt-msg'; m.setAttribute('role', 'status'); li.append(m); }
        say(m, res.message, 'error');
        return;
      }
      await refreshComments();
    });
  }

  // ── Intresseanmälan för märken ──────────────────────────────────────────
  var form = document.getElementById('pqt-interest-form');
  if (form) {
    form.addEventListener('submit', async function (ev) {
      ev.preventDefault();
      var msg = document.getElementById('pqt-interest-msg');
      var btn = document.getElementById('pqt-interest-save');
      btn.disabled = true;
      var res = await post('/pqt/intresse', {
        marksman: document.getElementById('pqt-i-marksman').value || '0',
        sharpshooter: document.getElementById('pqt-i-sharpshooter').value || '0',
        expert: document.getElementById('pqt-i-expert').value || '0',
        clubOrEvent: document.getElementById('pqt-i-club').value,
        note: document.getElementById('pqt-i-note').value
      });
      btn.disabled = false;
      if (!res.success) { say(msg, res.message, 'error'); return; }
      say(msg, 'Tack. Din anmälan är sparad.', 'ok');
      window.location.hash = 'pqt-interest';
      window.location.reload();
    });
  }
  var del = document.getElementById('pqt-interest-delete');
  if (del) {
    del.addEventListener('click', async function () {
      if (!window.confirm('Ta bort din intresseanmälan?')) return;
      var res = await post('/pqt/intresse/ta-bort');
      if (!res.success) { var msg = document.getElementById('pqt-interest-msg'); say(msg, res.message, 'error'); return; }
      window.location.hash = 'pqt-interest';
      window.location.reload();
    });
  }
})();
