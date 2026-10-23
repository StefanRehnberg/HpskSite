/* =====================================================================================
   Tävlingsplanering, P1 (2026-10-21) — sidan /tavlingsplanering/{id} och länkläget
   /tavlingsplanering/svar?t=… (samma sida, varje anrop bär länken).

   Skalet är sajtens förlaga för en egen sida (Ekonomin, Min kurs, Styrelsen): rälsen = platser,
   blå Åtgärder = handlingar för delen man står på, grå radmeny per rad, dialoger i stället för
   confirm/alert/prompt, data-attribut i stället för onclick med text. Delen står i adressen (#vy=).

   ⚠️ All text från servern går genom esc(). Inget användarinnehåll i ett attribut som kod.
   ⚠️ Pollningen var 10:e sekund läser bara en räknare (Version); först när den ändrats hämtas
      delen om — och skrivrutan ritas aldrig om under fingrarna på den som skriver.
   ===================================================================================== */
(function () {
    'use strict';
    const CFG = window.PL_CONFIG || {};
    const ID = CFG.id;
    const TOKEN = CFG.token || '';
    const $ = id => document.getElementById(id);

    // ---------------------------------------------------------------- anrop
    function token() { const el = document.querySelector('input[name="__RequestVerificationToken"]'); return el ? el.value : ''; }
    async function req(url, opts) {
        let r;
        try { r = await fetch(url, opts); }
        catch (e) { throw new Error('Det gick inte att nå pistol.nu. Kontrollera uppkopplingen och försök igen.'); }
        // hpskRequestFailureMessage ger { msg, reload } — texten är .msg.
        if (!r.ok) throw new Error(window.hpskRequestFailureMessage ? window.hpskRequestFailureMessage(r.status).msg : 'Något gick fel (' + r.status + ').');
        return r.json();
    }
    function withT(params) { const p = Object.assign({}, params || {}); if (TOKEN) p.t = TOKEN; return p; }
    function get(action, qs) {
        return req('/umbraco/surface/Planning/' + action + '?' + new URLSearchParams(withT(Object.assign({ id: ID }, qs || {}))).toString());
    }
    function post(action, params) {
        return req('/umbraco/surface/Planning/' + action, {
            method: 'POST',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'RequestVerificationToken': token() },
            body: new URLSearchParams(withT(Object.assign({ id: ID }, params || {}))).toString()
        });
    }

    // ---------------------------------------------------------------- små hjälpare
    function esc(s) {
        if (s === null || s === undefined) return '';
        const m = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' };
        return s.toString().replace(/[&<>"']/g, c => m[c]);
    }
    function parseDT(s) { return s ? new Date(s.replace(' ', 'T')) : null; }
    function fmtDT(s, withDay) {
        const d = parseDT(s); if (!d || isNaN(d)) return s || '';
        return d.toLocaleString('sv-SE', withDay === false
            ? { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }
            : { weekday: 'long', day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' });
    }
    function fmtShort(s) {
        const d = parseDT(s); if (!d || isNaN(d)) return s || '';
        const today = new Date(); const y = new Date(); y.setDate(today.getDate() - 1);
        const hm = d.toLocaleTimeString('sv-SE', { hour: '2-digit', minute: '2-digit' });
        if (d.toDateString() === today.toDateString()) return 'i dag ' + hm;
        if (d.toDateString() === y.toDateString()) return 'i går ' + hm;
        return d.toLocaleString('sv-SE', { weekday: 'short', day: 'numeric', month: 'short' }) + ' ' + hm;
    }
    function span(a, b) {
        const da = parseDT(a), db = parseDT(b);
        if (!da || !db) return fmtDT(a);
        const sameDay = da.toDateString() === db.toDateString();
        return fmtDT(a) + '–' + (sameDay ? db.toLocaleTimeString('sv-SE', { hour: '2-digit', minute: '2-digit' }) : fmtDT(b));
    }
    function spinner() { return '<div class="text-center text-muted py-4"><div class="spinner-border"></div><div class="mt-2">Hämtar…</div></div>'; }

    // ---------------------------------------------------------------- skalet
    let actionFns = [], rowFns = [];
    function setHead(title, sub) { $('plTitle').textContent = title || ''; $('plSub').innerHTML = sub || ''; }
    function setActions(items) {
        const wrap = $('plActions'), list = $('plActionsList');
        actionFns = [];
        const real = (items || []).filter(Boolean);
        if (!real.some(i => i.fn)) { wrap.classList.add('d-none'); list.innerHTML = ''; return; }
        list.innerHTML = real.map(i => {
            if (i.header) return `<li><h6 class="dropdown-header">${esc(i.header)}</h6></li>`;
            if (i.divider) return '<li><hr class="dropdown-divider"></li>';
            actionFns.push(i.fn);
            return `<li><button type="button" class="dropdown-item${i.danger ? ' text-danger' : ''}" data-pl-action="${actionFns.length - 1}">`
                + (i.icon ? `<i class="bi ${i.icon} me-2${i.danger ? '' : ' text-secondary'}"></i>` : '') + `${esc(i.label)}</button></li>`;
        }).join('');
        wrap.classList.remove('d-none');
    }
    $('plActionsList')?.addEventListener('click', e => {
        const b = e.target.closest('[data-pl-action]'); if (!b) return;
        const fn = actionFns[parseInt(b.dataset.plAction, 10)]; if (fn) fn();
    });
    function rowMenu(items) {
        const real = (items || []).filter(Boolean);
        if (!real.some(i => i.fn)) return '';
        const html = real.map(i => {
            if (i.divider) return '<li><hr class="dropdown-divider"></li>';
            rowFns.push(i.fn);
            return `<li><button type="button" class="dropdown-item${i.danger ? ' text-danger' : ''}" data-pl-row="${rowFns.length - 1}">${esc(i.label)}</button></li>`;
        }).join('');
        return `<div class="dropdown pl-noprint d-inline-block">
            <button class="btn btn-sm btn-outline-secondary dropdown-toggle" type="button" data-bs-toggle="dropdown"
                    data-bs-popper-config='{"strategy":"fixed"}' aria-expanded="false">Åtgärder</button>
            <ul class="dropdown-menu dropdown-menu-end">${html}</ul></div>`;
    }
    document.addEventListener('click', e => {
        const b = e.target.closest('[data-pl-row]'); if (!b) return;
        const fn = rowFns[parseInt(b.dataset.plRow, 10)]; if (fn) fn();
    });

    function showMsg(text, kind) {
        const el = $('plMsg'); if (!el) return;
        el.innerHTML = text ? `<div class="alert alert-${kind || 'success'} alert-dismissible fade show" role="alert">${text}
            <button type="button" class="btn-close" data-bs-dismiss="alert" aria-label="Stäng"></button></div>` : '';
        if (text) el.scrollIntoView({ block: 'nearest' });
    }
    function failMsg(r, fallback) { showMsg(esc((r && r.message) || fallback || 'Det gick inte.'), 'danger'); }

    function plConfirm(title, text, okLabel, danger) {
        return new Promise(resolve => {
            const m = $('plConfirmModal');
            $('plConfirmTitle').textContent = title;
            $('plConfirmText').textContent = text || '';
            const ok = $('plConfirmOk');
            ok.textContent = okLabel || 'OK';
            ok.className = 'btn ' + (danger ? 'btn-danger' : 'btn-primary');
            let answered = false;
            const modal = bootstrap.Modal.getOrCreateInstance(m);
            ok.onclick = () => { answered = true; modal.hide(); };
            m.addEventListener('hidden.bs.modal', function h() { m.removeEventListener('hidden.bs.modal', h); resolve(answered); });
            modal.show();
        });
    }
    function formModal({ title, html, okLabel, okClass, onOpen, onOk, wide, cancelLabel }) {
        const m = $('plFormModal');
        $('plFormTitle').textContent = title;
        $('plFormCancel').textContent = cancelLabel || 'Avbryt';
        // Ny kropp varje gång: lyssnare från förra dialogen får inte leva kvar (bytet medlem ⇄ e-post).
        const oldBody = $('plFormBody'), fresh = oldBody.cloneNode(false);
        oldBody.replaceWith(fresh);
        fresh.innerHTML = html;
        const wasOpen = m.classList.contains('show');
        const err = $('plFormErr'); err.classList.add('d-none'); err.textContent = '';
        const ok = $('plFormOk');
        ok.textContent = okLabel || 'Spara';
        ok.className = 'btn ' + (okClass || 'btn-primary');
        ok.classList.toggle('d-none', !onOk);
        ok.disabled = false;
        m.querySelector('.modal-dialog').classList.toggle('modal-lg', wide !== false);
        const modal = bootstrap.Modal.getOrCreateInstance(m);
        ok.onclick = async () => {
            ok.disabled = true;
            try {
                const res = await onOk($('plFormBody'));
                if (res === true) { modal.hide(); return; }
                if (typeof res === 'string') { err.textContent = res; err.classList.remove('d-none'); }
            } catch (e) {
                err.textContent = (e && e.message) || 'Det gick inte att spara.'; err.classList.remove('d-none');
            } finally { ok.disabled = false; }
        };
        if (wasOpen) { if (onOpen) onOpen(fresh); return modal; }
        modal.show();
        if (onOpen) m.addEventListener('shown.bs.modal', function h() { m.removeEventListener('shown.bs.modal', h); onOpen($('plFormBody')); });
        return modal;
    }
    function datePicker(el) {
        if (!el || !window.flatpickr) return;
        window.flatpickr(el, { locale: 'sv', dateFormat: 'Y-m-d', allowInput: true });
    }

    // ---------------------------------------------------------------- läget
    let S = null;          // Get-svaret
    let MSG = null;        // Messages-svaret
    let MEET = null;       // Meetings-svaret
    let version = 0;
    let view = '';
    let replyTo = CFG.reply || 0;
    let pplQuery = '', pplFilter = '';   // Deltagarlistans sök och filter (lever över omritningar)

    // Nivåns färg — samma överallt där en nivå visas.
    const LEVEL_RANK = { Owner: 4, Editor: 3, Participant: 2, Follower: 1 };
    const LEVEL_BADGE = { Owner: 'text-bg-primary', Editor: 'text-bg-info', Participant: 'text-bg-success', Follower: 'text-bg-secondary' };
    const levelBadge = p => `<span class="badge rounded-pill ${LEVEL_BADGE[p.access] || 'text-bg-secondary'}">${esc(p.accessLabel || p.access)}</span>`;
    let pendingMentions = new Map(); // participantId -> name

    const VIEWS = [
        { key: 'oversikt', label: 'Översikt' },
        { key: 'meddelanden', label: 'Meddelanden', need: s => s.me.canReadMessages },
        { key: 'moten', label: 'Möten' },
        { key: 'deltagare', label: 'Deltagare' },
        { key: 'notiser', label: 'Mina notiser', need: s => s.me.isParticipant },
        { key: 'installningar', label: 'Inställningar', need: s => s.me.canEdit },
    ];

    function hashView() {
        const h = new URLSearchParams((location.hash || '').replace(/^#/, ''));
        return h.get('vy') || '';
    }
    function go(v) {
        if (location.hash !== '#vy=' + v) history.pushState(null, '', '#vy=' + v);
        render(v);
    }
    window.addEventListener('hashchange', () => render(hashView() || 'oversikt'));
    window.addEventListener('popstate', () => render(hashView() || 'oversikt'));
    document.addEventListener('click', e => {
        const a = e.target.closest('[data-pl-go]'); if (!a) return;
        e.preventDefault(); go(a.dataset.plGo);
    });

    function unreadCount() {
        if (!MSG) return 0;
        return MSG.messages.filter(m => m.id > MSG.myLastRead && !m.mine && !m.deleted && m.kind === 'Message').length;
    }
    function votesOwed() {
        if (!MEET || !S || !S.me.canUseMeetings) return 0;
        return MEET.meetings.filter(m => m.status === 'Proposed' && m.options.every(o => !o.mine)).length;
    }

    function renderRail() {
        const s = S;
        $('plOrg').textContent = s.planning.name;
        $('plRole').textContent = s.me.accessLabel || '';
        if (TOKEN) { const b = $('plBack'); b.textContent = '← Till startsidan'; b.href = '/'; }
        const counts = { meddelanden: unreadCount(), moten: votesOwed(), deltagare: s.me.pendingRequests || 0 };
        $('plRail').innerHTML = VIEWS.filter(v => !v.need || v.need(s)).map(v =>
            `<a href="#vy=${v.key}" class="pl-nav${v.key === view ? ' on' : ''}" data-pl-go="${v.key}">${esc(v.label)}`
            + (counts[v.key] ? `<span class="pl-count" title="${counts[v.key]} väntar">${counts[v.key]}</span>` : '') + '</a>').join('');
    }

    async function loadAll() {
        const [s, m] = await Promise.all([get('Get'), get('Meetings')]);
        if (!s.success) { renderMissing(s.message); return false; }
        S = s; MEET = m.success ? m : { meetings: [] };
        version = s.version;
        if (S.me.canReadMessages) {
            const mm = await get('Messages');
            MSG = mm.success ? mm : null;
        }
        return true;
    }
    function renderMissing(msg) {
        setHead('Planeringen finns inte', '');
        setActions([]);
        $('plRail').innerHTML = '';
        $('plBody').innerHTML = `<div class="pl-card"><p class="mb-1">${esc(msg || 'Planeringen finns inte, eller så är du inte med i den.')}</p>
            <p class="pl-help mb-0">Har någon bjudit in dig? Öppna länken i inbjudan, eller hör med den som skapade planeringen.</p></div>`;
    }

    function render(v) {
        if (!S) return;
        const allowed = VIEWS.filter(x => !x.need || x.need(S)).map(x => x.key);
        view = allowed.includes(v) ? v : 'oversikt';
        rowFns = [];
        renderRail();
        ({ oversikt: viewOverview, meddelanden: viewMessages, moten: viewMeetings, deltagare: viewPeople,
           notiser: viewNotify, installningar: viewSettings })[view]();
    }

    function subLine() {
        const p = S.planning;
        const parts = [];
        if (p.targetText) parts.push(esc(p.targetText));
        else if (p.targetStart) parts.push(esc(p.targetStart) + (p.targetEnd && p.targetEnd !== p.targetStart ? '–' + esc(p.targetEnd) : ''));
        if (p.status === 'Archived') parts.push('<span class="pl-state off">Arkiverad</span>');
        return parts.join(' · ');
    }

    // ================================================================ Översikt
    function viewOverview() {
        setHead(S.planning.name, subLine());
        setActions(S.me.isParticipant ? [
            S.me.canInvite && { label: 'Bjud in…', icon: 'bi-person-plus', fn: openInvite },
            S.me.canUseMeetings && { label: 'Föreslå ett möte…', icon: 'bi-calendar-plus', fn: openNewMeeting },
            S.me.canWrite && { label: 'Skriv ett meddelande', icon: 'bi-chat-left-text', fn: () => go('meddelanden') },
        ] : []);
        const p = S;
        const html = [];
        if (TOKEN && p.me.isGuest) {
            html.push(`<div class="pl-card"><p class="mb-1"><b>Du är inne via länken i ditt mejl.</b> Du behöver inget konto.</p>
                <p class="pl-help mb-0">Spara gärna mejlet — varje nytt mejl från planeringen har en ny länk.</p></div>`);
        }
        if (p.me.access === 'Follower') {
            html.push(`<div class="pl-card"><p class="mb-0">Du <b>följer</b> planeringen: du läser allt, även meddelandena, men skriver inte och svarar inte på mötestider.
                Vill du vara med och bestämma — be den sammankallande ändra din nivå till <i>Deltar</i>.</p></div>`);
        }
        // Bara en sammankallande: gruppen står utan ledning om hen blir sjuk eller slutar.
        if (p.me.canManage && p.participants.filter(x => x.access === 'Owner').length === 1 && p.participants.length > 1) {
            html.push(`<div class="alert alert-warning d-flex gap-2 align-items-start"><i class="bi bi-exclamation-triangle mt-1"></i>
                <div>Du är ensam <b>sammankallande</b>. Utse gärna en till, så att gruppen inte står utan om du blir sjuk eller slutar.
                <a href="#vy=deltagare" data-pl-go="deltagare">Gå till Deltagare</a></div></div>`);
        }
        // Nästa steg
        const steps = [];
        if (MEET) MEET.meetings.filter(m => m.status === 'Proposed' && p.me.canUseMeetings && m.options.every(o => !o.mine))
            .forEach(m => steps.push(`<div class="pl-next warn"><div class="t"><b>Svara på tiderna:</b> ${esc(m.title)}</div>
                <button class="btn btn-primary" data-pl-meet="${m.id}">Svara</button></div>`));
        const unread = unreadCount();
        if (unread) steps.push(`<div class="pl-next"><div class="t"><b>${unread === 1 ? 'Ett nytt meddelande' : unread + ' nya meddelanden'}</b></div>
            <a class="btn btn-primary" href="#vy=meddelanden" data-pl-go="meddelanden">Läs</a></div>`);
        const upcoming = (MEET ? MEET.meetings : []).filter(m => m.status === 'Scheduled' && parseDT(m.endsAt || m.startsAt) >= new Date())
            .sort((a, b) => parseDT(a.startsAt) - parseDT(b.startsAt));
        if (upcoming.length) {
            const m = upcoming[0];
            steps.push(`<div class="pl-next"><div class="t"><b>Nästa möte:</b> ${esc(m.title)} — ${esc(span(m.startsAt, m.endsAt))}</div>
                ${m.onlineUrl ? `<a class="btn btn-outline-primary" href="${esc(m.onlineUrl)}" target="_blank" rel="noopener">Gå med</a>` : ''}
                <a class="btn btn-outline-primary" href="#vy=moten" data-pl-go="moten">Visa</a></div>`);
        }
        html.push(`<div class="pl-card"><h2>Nästa steg</h2>${steps.join('') || '<p class="pl-help mb-0">Inget väntar på dig just nu.</p>'}</div>`);

        // Kom igång — för en ny planering
        if (p.me.isParticipant && p.participants.length <= 1 && (!MEET || MEET.meetings.length === 0)) {
            html.push(`<div class="pl-card"><h2>Kom igång</h2>
                <ol class="mb-0">
                    <li class="mb-2">${p.me.canInvite ? '<button class="btn btn-link p-0 align-baseline" data-pl-invite="1">Bjud in</button> de som ska vara med' : 'Be den som skapade planeringen bjuda in de andra'} — sök på namnet bland alla medlemmar. Ingen behöver känna till någons e-postadress.</li>
                    <li class="mb-2"><button class="btn btn-link p-0 align-baseline" data-pl-newmeet="1">Föreslå ett möte</button> med några tider — alla svarar vilka som passar, direkt ur mejlet.</li>
                    <li>Skriv det första meddelandet under <a href="#vy=meddelanden" data-pl-go="meddelanden">Meddelanden</a>. Alla får ett mejl på kvällen med det som är nytt.</li>
                </ol></div>`);
        }
        // Senaste meddelandena
        if (MSG) {
            const last = MSG.messages.filter(m => m.kind === 'Message' && !m.deleted).slice(-3);
            if (last.length) html.push(`<div class="pl-card"><div class="pl-cardh"><h2>Senaste meddelandena</h2>
                <a href="#vy=meddelanden" data-pl-go="meddelanden">Alla meddelanden</a></div>
                ${last.map(m => `<div class="pl-msg"><span class="who">${esc(m.author)}</span> <span class="when">${esc(fmtShort(m.createdAt))}</span>
                <div class="body">${renderBody(m)}</div></div>`).join('')}</div>`);
        }
        // Med i planeringen
        html.push(`<div class="pl-card"><div class="pl-cardh"><h2>Med i planeringen (${p.participants.length})</h2>
            <a href="#vy=deltagare" data-pl-go="deltagare">Visa alla</a></div>
            <div>${p.participants.slice(0, 8).map(x => esc(x.name) + (x.represents ? ` <span class="pl-help">(${esc(x.represents)})</span>` : '')).join(', ')}${p.participants.length > 8 ? ' …' : ''}</div></div>`);
        $('plBody').innerHTML = html.join('');
    }
    document.addEventListener('click', e => {
        const b = e.target.closest('[data-pl-meet]'); if (!b) return;
        e.preventDefault(); CFG.startMeeting = parseInt(b.dataset.plMeet, 10); go('moten');
    });
    document.addEventListener('click', e => { if (e.target.closest('[data-pl-invite]')) { e.preventDefault(); openInvite(); } });
    document.addEventListener('click', e => { if (e.target.closest('[data-pl-newmeet]')) { e.preventDefault(); openNewMeeting(); } });

    // ================================================================ Meddelanden
    function renderBody(m) {
        let html = esc(m.body);
        // Omnämnandena är data; namnet i texten markeras bara visuellt.
        (m.mentions || []).forEach(pid => {
            const p = S.participants.find(x => x.id === pid);
            if (!p) return;
            const n = esc('@' + p.name);
            html = html.split(n).join(`<span class="pl-mention">${n}</span>`);
        });
        return html;
    }
    function seenText(m) {
        if (!MSG || !m.mine) return '';
        const others = MSG.reads.filter(r => r.participantId !== m.authorId);
        const seen = others.filter(r => r.lastRead >= m.id).length;
        return `Sett av ${seen} av ${others.length}`;
    }
    function msgHtml(m, isReply) {
        if (m.kind === 'System') return `<div class="pl-sys">${esc(m.systemText)} <span class="when">· ${esc(fmtShort(m.createdAt))}</span></div>`;
        const unread = m.id > MSG.myLastRead && !m.mine && !m.deleted;
        const menu = m.mine && !m.deleted ? rowMenu([
            { label: 'Ändra', fn: () => editMessage(m) },
            { divider: true },
            { label: 'Ta bort', danger: true, fn: () => deleteMessage(m) },
        ]) : '';
        return `<div class="pl-msg${unread ? ' unread' : ''}" data-msg-id="${m.id}">
            <div class="d-flex justify-content-between gap-2"><div><span class="who">${esc(m.author)}</span>
            <span class="when"> · ${esc(fmtShort(m.createdAt))}${m.edited ? ' · ändrat' : ''}${m.viaEmail ? ' · via mejl' : ''}</span></div>${menu}</div>
            <div class="body">${m.deleted ? '<i class="text-muted">Meddelandet togs bort.</i>' : renderBody(m)}</div>
            <div class="meta">${!isReply && !m.deleted && S.me.canWrite ? `<button class="btn btn-link btn-sm p-0" data-pl-reply="${m.id}">Svara</button>` : ''}
            ${m.mine ? `<span>${esc(seenText(m))}</span>` : ''}</div></div>`;
    }
    function threadHtml() {
        const all = MSG.messages;
        const replies = new Map();
        all.filter(m => m.replyTo).forEach(m => { if (!replies.has(m.replyTo)) replies.set(m.replyTo, []); replies.get(m.replyTo).push(m); });
        const top = all.filter(m => !m.replyTo);
        if (!top.length) return '<p class="pl-help">Inga meddelanden ännu. Skriv det första nedan — alla som är med får det i kvällens mejl.</p>';
        return top.map(m => msgHtml(m, false) + ((replies.get(m.id) || []).length
            ? `<div class="pl-reply">${replies.get(m.id).map(r => msgHtml(r, true)).join('')}</div>` : '')).join('');
    }
    function composerHtml() {
        if (!S.me.canWrite) return `<p class="pl-help mt-3 mb-0">Du följer planeringen och läser meddelandena, men skriver inte här.</p>`;
        const target = replyTo ? MSG.messages.find(m => m.id === replyTo) : null;
        return `<div class="pl-compose pl-noprint" id="plCompose">
            ${target ? `<div class="small mb-1">Svarar på <b>${esc(target.author)}</b>: <i>${esc((target.body || '').slice(0, 80))}</i>
                <button class="btn btn-link btn-sm p-0 ms-2" id="plReplyCancel">Avbryt svaret</button></div>` : ''}
            <label class="form-label fw-semibold" for="plText">${target ? 'Ditt svar' : 'Nytt meddelande'}</label>
            <textarea id="plText" class="form-control" rows="3" maxlength="4000" placeholder="Skriv här… (Enter ger ny rad)"></textarea>
            <div class="d-flex gap-2 mt-2 flex-wrap align-items-center">
                <button class="btn btn-primary" id="plSend"><i class="bi bi-send me-1"></i>Skicka</button>
                <div class="dropdown">
                    <button class="btn btn-outline-secondary dropdown-toggle" data-bs-toggle="dropdown" aria-expanded="false" type="button">Nämn någon</button>
                    <ul class="dropdown-menu" style="max-height:300px;overflow:auto">
                        ${S.participants.filter(p => !p.isMe).map(p => `<li><button class="dropdown-item" type="button" data-pl-mention="${p.id}">${esc(p.name)}</button></li>`).join('') || '<li><span class="dropdown-item-text text-muted">Ingen annan är med ännu</span></li>'}
                    </ul>
                </div>
                <span class="pl-help small" id="plMentionNote">${pendingMentions.size ? 'Nämner: ' + esc([...pendingMentions.values()].join(', ')) : 'Den du nämner får en notis.'}</span>
            </div></div>`;
    }
    function viewMessages() {
        setHead('Meddelanden', esc(S.planning.name));
        setActions([]);
        if (!MSG) { $('plBody').innerHTML = spinner(); return; }
        $('plBody').innerHTML = `<div class="pl-card"><div class="pl-msgs" id="plThread">${threadHtml()}</div>${composerHtml()}</div>`;
        wireComposer();
        if (replyTo) setTimeout(() => $('plText')?.focus(), 50);
        markRead();
    }
    function refreshThreadOnly() {
        const t = $('plThread');
        if (t) { rowFns = []; t.innerHTML = threadHtml(); }
        renderRail();
        markRead();
    }
    function wireComposer() {
        if (!$('plSend')) return;
        $('plSend').onclick = send;
        const c = $('plReplyCancel'); if (c) c.onclick = () => { replyTo = 0; viewMessages(); };
    }
    document.addEventListener('click', e => {
        const b = e.target.closest('[data-pl-reply]'); if (!b) return;
        replyTo = parseInt(b.dataset.plReply, 10);
        const text = $('plText') ? $('plText').value : '';
        viewMessages();
        if (text && $('plText')) $('plText').value = text;
        $('plCompose')?.scrollIntoView({ block: 'nearest' });
    });
    document.addEventListener('click', e => {
        const b = e.target.closest('[data-pl-mention]'); if (!b) return;
        const pid = parseInt(b.dataset.plMention, 10);
        const p = S.participants.find(x => x.id === pid); if (!p) return;
        pendingMentions.set(pid, p.name);
        const ta = $('plText');
        if (ta) {
            const ins = '@' + p.name + ' ';
            const pos = ta.selectionStart ?? ta.value.length;
            ta.value = ta.value.slice(0, pos) + ins + ta.value.slice(pos);
            ta.focus(); ta.selectionStart = ta.selectionEnd = pos + ins.length;
        }
        const n = $('plMentionNote'); if (n) n.textContent = 'Nämner: ' + [...pendingMentions.values()].join(', ');
    });
    async function send() {
        const ta = $('plText'); const body = (ta.value || '').trim();
        if (!body) { ta.focus(); return; }
        // Nämnda vars namn tagits bort ur texten nämns inte.
        const mentions = [...pendingMentions.entries()].filter(([, n]) => body.includes('@' + n)).map(([id]) => id);
        const btn = $('plSend'); btn.disabled = true;
        try {
            const r = await post('Post', { body, replyTo: replyTo || '', mentions: mentions.join(',') });
            if (!r.success) { failMsg(r); return; }
            replyTo = 0; pendingMentions = new Map();
            showMsg('');
            await reloadMessages();
            viewMessages();
            $('plThread')?.lastElementChild?.scrollIntoView({ block: 'nearest' });
        } catch (e) { failMsg(e); }
        finally { if ($('plSend')) $('plSend').disabled = false; }
    }
    async function reloadMessages() { const mm = await get('Messages'); if (mm.success) MSG = mm; }
    function editMessage(m) {
        formModal({
            title: 'Ändra meddelandet', okLabel: 'Spara',
            html: `<textarea id="plEdit" class="form-control" rows="5" maxlength="4000">${esc(m.body)}</textarea>`,
            onOk: async body => {
                const r = await post('EditMessage', { messageId: m.id, body: body.querySelector('#plEdit').value });
                if (!r.success) return r.message;
                await reloadMessages(); refreshThreadOnly(); return true;
            }
        });
    }
    async function deleteMessage(m) {
        if (!await plConfirm('Ta bort meddelandet?', 'Det står "Meddelandet togs bort" i stället, så att svaren håller ihop.', 'Ta bort', true)) return;
        const r = await post('DeleteMessage', { messageId: m.id });
        if (!r.success) { failMsg(r); return; }
        await reloadMessages(); refreshThreadOnly();
    }
    let lastMarked = 0;
    async function markRead() {
        if (view !== 'meddelanden' || document.visibilityState !== 'visible' || !MSG || !MSG.messages.length) return;
        const max = Math.max(...MSG.messages.map(m => m.id));
        if (max <= MSG.myLastRead || max <= lastMarked) return;
        lastMarked = max;
        try { await post('MarkRead', { upTo: max }); MSG.myLastRead = max; renderRail(); } catch (e) { /* nästa poll försöker igen */ }
    }

    // ================================================================ Möten
    function meetingState(m) {
        if (m.status === 'Cancelled') return '<span class="pl-state off">Inställt</span>';
        if (m.status === 'Scheduled') return parseDT(m.endsAt || m.startsAt) < new Date()
            ? '<span class="pl-state off">Genomfört</span>' : '<span class="pl-state ok">Bokat</span>';
        return '<span class="pl-state wait">Tider föreslagna</span>';
    }
    const ANS = [['Yes', 'Passar', 'btn-success'], ['Maybe', 'Kanske', 'btn-warning'], ['No', 'Passar inte', 'btn-danger']];
    function optionRow(m, o) {
        const yes = o.votes.filter(v => v.answer === 'Yes').length, maybe = o.votes.filter(v => v.answer === 'Maybe').length,
              no = o.votes.filter(v => v.answer === 'No').length;
        const canUse = MEET.canUse;
        const who = o.votes.map(v => {
            const p = S.participants.find(x => x.id === v.participantId);
            return p ? esc(p.name) + ' ' + (v.answer === 'Yes' ? '✓' : v.answer === 'Maybe' ? '?' : '✗') : '';
        }).filter(Boolean).join(', ');
        return `<div class="pl-opt${m.bestOptionId === o.id ? ' best' : ''}">
            <div><b>${esc(span(o.startsAt, o.endsAt))}</b>${m.bestOptionId === o.id ? ' <span class="pl-state ok">Passar flest</span>' : ''}
                <div class="tally">✓ ${yes} · ? ${maybe} · ✗ ${no}${who ? ' — ' + who : ''}</div></div>
            ${canUse ? `<div class="btn-group pl-ans" role="group" aria-label="Ditt svar">${ANS.map(([k, l, c]) =>
                `<button type="button" class="btn btn-sm ${o.mine === k ? c : 'btn-outline-secondary'}" data-pl-vote="${m.id}:${o.id}:${k}" aria-pressed="${o.mine === k}">${l}</button>`).join('')}</div>` : '<div></div>'}
            ${canUse ? `<div><button type="button" class="btn btn-sm btn-outline-primary" data-pl-book="${m.id}:${o.id}">Boka</button></div>` : '<div></div>'}
        </div>`;
    }
    function meetingCard(m) {
        const canUse = MEET.canUse;
        const menu = m.canEdit && m.status !== 'Cancelled' ? rowMenu([
            { label: 'Ändra mötet…', fn: () => openEditMeeting(m) },
            m.status === 'Proposed' && { label: 'Lägg till en tid…', fn: () => openAddOption(m) },
            m.status === 'Scheduled' && { label: 'Flytta till en annan tid…', fn: () => openBookOwn(m) },
            { divider: true },
            m.status === 'Scheduled'
                ? { label: 'Ställ in mötet', danger: true, fn: () => cancelMeeting(m) }
                : { label: 'Ta bort mötet', danger: true, fn: () => deleteMeeting(m) },
        ]) : '';
        const h = [`<div class="pl-card" id="plMeet${m.id}"><div class="pl-cardh"><h2>${esc(m.title)}</h2><div class="d-flex gap-2 align-items-center">${meetingState(m)}${menu}</div></div>`];
        if (m.status === 'Proposed') {
            h.push(`<p class="pl-help">${esc(m.createdBy)} föreslår de här tiderna. ${canUse ? 'Tryck på det som gäller för dig för varje tid.' : ''}
                ${m.voteDeadline ? 'Svara senast ' + esc(m.voteDeadline) + '.' : ''}</p>`);
            h.push(m.options.map(o => optionRow(m, o)).join(''));
            if (m.notAnswered && m.notAnswered.length) {
                h.push(`<div class="mt-3 pl-help">Har inte svarat: ${m.notAnswered.map(p => esc(p.name)).join(', ')}
                    ${canUse ? `<button class="btn btn-link btn-sm p-0 ms-2 align-baseline" data-pl-remind="${m.id}">Påminn dem</button>` : ''}</div>`);
            }
            if (canUse && m.bestOptionId) {
                const b = m.options.find(o => o.id === m.bestOptionId);
                h.push(`<div class="pl-next mt-3"><div class="t">Flest kan <b>${esc(span(b.startsAt, b.endsAt))}</b>. Den som bokar bestämmer — alla får ett mejl med tiden och länken.</div>
                    <button class="btn btn-primary" data-pl-book="${m.id}:${b.id}">Boka den tiden</button></div>`);
            }
        } else {
            if (m.startsAt) h.push(`<p class="mb-1" style="font-size:1.15rem"><b>${esc(span(m.startsAt, m.endsAt))}</b></p>`);
            if (m.place) h.push(`<p class="mb-1"><i class="bi bi-geo-alt me-1"></i>${esc(m.place)}</p>`);
            if (m.status === 'Scheduled') {
                const acts = [];
                if (m.onlineUrl) acts.push(`<a class="btn btn-primary" href="${esc(m.onlineUrl)}" target="_blank" rel="noopener"><i class="bi bi-camera-video me-1"></i>Gå med i mötet</a>`);
                if (S.me.icsUrl) acts.push(`<a class="btn btn-outline-secondary" href="${esc(S.me.icsUrl)}"><i class="bi bi-calendar-plus me-1"></i>Lägg i kalendern</a>`);
                if (acts.length) h.push(`<div class="d-flex flex-wrap gap-2 my-2">${acts.join('')}</div>`);
                if (m.bookedBy) h.push(`<p class="pl-help mb-2">Bokat av ${esc(m.bookedBy)}.</p>`);
            }
        }
        if (m.agenda) h.push(`<div class="mt-2"><div class="fw-semibold">Dagordning</div><div style="white-space:pre-wrap">${esc(m.agenda)}</div></div>`);
        if (m.status === 'Scheduled' && canUse) {
            h.push(`<div class="mt-3"><label class="form-label fw-semibold" for="plNotes${m.id}">Anteckningar från mötet</label>
                <textarea class="form-control" id="plNotes${m.id}" rows="3" data-pl-notes="${m.id}" placeholder="Vad bestämdes? Vem gör vad?">${esc(m.notes || '')}</textarea>
                <div class="pl-help small mt-1" id="plNotesSaved${m.id}">Sparas när du lämnar rutan.</div></div>`);
        } else if (m.notes) {
            h.push(`<div class="mt-2"><div class="fw-semibold">Anteckningar</div><div style="white-space:pre-wrap">${esc(m.notes)}</div></div>`);
        }
        h.push('</div>');
        return h.join('');
    }
    function viewMeetings() {
        setHead('Möten', esc(S.planning.name));
        setActions(MEET.canUse ? [{ label: 'Föreslå ett möte…', icon: 'bi-calendar-plus', fn: openNewMeeting }] : []);
        const list = MEET.meetings.slice();
        const order = m => m.status === 'Proposed' ? 0 : m.status === 'Scheduled' && parseDT(m.endsAt || m.startsAt) >= new Date() ? 1 : 2;
        list.sort((a, b) => order(a) - order(b) || (parseDT(a.startsAt) || 0) - (parseDT(b.startsAt) || 0));
        const html = [];
        if (!list.length) html.push(`<div class="pl-card"><p class="mb-2">Inga möten ännu.</p>
            ${MEET.canUse ? '<button class="btn btn-primary" data-pl-newmeet="1"><i class="bi bi-calendar-plus me-1"></i>Föreslå ett möte</button>' : ''}
            <p class="pl-help mt-2 mb-0">Föreslå några tider — alla svarar vilka som passar, direkt ur mejlet, och sedan bokar ni den tid som passar flest.</p></div>`);
        list.forEach(m => html.push(meetingCard(m)));
        if (S.me.icsUrl) html.push(`<p class="pl-help pl-noprint">Planeringens bokade möten kan ligga i din egen kalender och följa med om de flyttas:
            <a href="${esc(S.me.icsUrl)}">lägg till kalendern</a>.</p>`);
        $('plBody').innerHTML = html.join('');
        if (CFG.startMeeting) { $('plMeet' + CFG.startMeeting)?.scrollIntoView({ block: 'start' }); CFG.startMeeting = 0; }
    }
    document.addEventListener('click', async e => {
        const b = e.target.closest('[data-pl-vote]'); if (!b) return;
        const [mid, oid, ans] = b.dataset.plVote.split(':');
        b.disabled = true;
        try {
            const r = await post('Vote', { meetingId: mid, answers: oid + ':' + ans });
            if (!r.success) { failMsg(r); return; }
            await reloadMeetings(); viewMeetings();
            showMsg('Ditt svar är sparat.', 'success');
        } catch (er) { failMsg(er); } finally { b.disabled = false; }
    });
    document.addEventListener('click', async e => {
        const b = e.target.closest('[data-pl-book]'); if (!b) return;
        const [mid, oid] = b.dataset.plBook.split(':').map(Number);
        const m = MEET.meetings.find(x => x.id === mid); const o = m && m.options.find(x => x.id === oid);
        if (!m || !o) return;
        const again = m.status === 'Scheduled';
        if (!await plConfirm(again ? 'Flytta mötet?' : 'Boka mötet?',
            `${m.title}\n${span(o.startsAt, o.endsAt)}\n\nAlla som är med får ett mejl med tiden${m.onlineUrl ? ', länken' : ''} och en knapp för kalendern.`, again ? 'Flytta' : 'Boka')) return;
        const r = await post('Book', { meetingId: mid, optionId: oid });
        if (!r.success) { failMsg(r); return; }
        await reloadMeetings(); viewMeetings(); showMsg('Mötet är bokat och alla har fått ett mejl.', 'success');
    });
    document.addEventListener('click', async e => {
        const b = e.target.closest('[data-pl-remind]'); if (!b) return;
        b.disabled = true;
        try {
            const r = await post('RemindNonVoters', { meetingId: b.dataset.plRemind });
            if (!r.success) { failMsg(r); return; }
            showMsg(r.reached ? `Påminnelsen gick till ${r.reached} ${r.reached === 1 ? 'person' : 'personer'}.`
                : 'Ingen påminnelse gick iväg — de har redan fått en i dag, har stängt av notiserna, eller så kunde mejlet inte skickas.', r.reached ? 'success' : 'warning');
        } catch (er) { failMsg(er); } finally { b.disabled = false; }
    });
    document.addEventListener('focusout', async e => {
        const ta = e.target.closest('[data-pl-notes]'); if (!ta) return;
        const m = MEET.meetings.find(x => x.id === parseInt(ta.dataset.plNotes, 10)); if (!m) return;
        if ((m.notes || '') === ta.value) return;
        const r = await post('UpdateMeeting', { meetingId: m.id, title: m.title, place: m.place || '', onlineUrl: m.onlineUrl || '',
            agenda: m.agenda || '', notes: ta.value, voteDeadline: m.voteDeadline || '' });
        const note = $('plNotesSaved' + m.id);
        if (r.success) { m.notes = ta.value; if (note) note.textContent = 'Sparat.'; }
        else if (note) note.textContent = r.message || 'Det gick inte att spara.';
    });
    async function reloadMeetings() { const m = await get('Meetings'); if (m.success) MEET = m; }

    function optRowHtml(i, d, a, b) {
        return `<div class="row g-2 align-items-end mb-2" data-opt-row="${i}">
            <div class="col-sm-5"><label class="form-label small mb-0">Dag</label><input type="text" class="form-control" data-opt-date value="${esc(d || '')}" placeholder="åååå-mm-dd"></div>
            <div class="col-5 col-sm-3"><label class="form-label small mb-0">Från</label><input type="text" class="form-control" data-opt-from data-hpsk-time inputmode="decimal" value="${esc(a || '19:00')}"></div>
            <div class="col-5 col-sm-3"><label class="form-label small mb-0">Till</label><input type="text" class="form-control" data-opt-to data-hpsk-time inputmode="decimal" value="${esc(b || '20:00')}"></div>
            <div class="col-2 col-sm-1"><button type="button" class="btn btn-outline-secondary" data-opt-del title="Ta bort tiden" aria-label="Ta bort tiden">✕</button></div></div>`;
    }
    function readOptions(body) {
        const out = []; let err = null;
        body.querySelectorAll('[data-opt-row]').forEach(row => {
            const d = row.querySelector('[data-opt-date]').value.trim();
            const f = window.hpskNormalizeTime ? window.hpskNormalizeTime(row.querySelector('[data-opt-from]').value) : row.querySelector('[data-opt-from]').value.trim();
            const t = window.hpskNormalizeTime ? window.hpskNormalizeTime(row.querySelector('[data-opt-to]').value) : row.querySelector('[data-opt-to]').value.trim();
            if (!d && !f) return;
            if (!/^\d{4}-\d{2}-\d{2}$/.test(d)) { err = 'Välj en dag för varje tid.'; return; }
            if (!f || !t) { err = 'Skriv klockslag, till exempel 19 eller 19:00.'; return; }
            out.push(`${d} ${f}|${d} ${t}`);
        });
        return { options: out, err };
    }
    function openNewMeeting() {
        if (!MEET.canUse) return;
        formModal({
            title: 'Föreslå ett möte', okLabel: 'Skicka förslaget',
            html: `<div class="mb-3"><label class="form-label fw-semibold" for="nmTitle">Vad gäller mötet?</label>
                    <input id="nmTitle" class="form-control" maxlength="200" placeholder="Till exempel: Första träffen om SM 2028"></div>
                <div class="mb-2 fw-semibold">Föreslå några tider</div>
                <div class="pl-help mb-2">Alla som är med får ett mejl och svarar <i>Passar / Kanske / Passar inte</i> för varje tid — utan att logga in.</div>
                <div id="nmOpts">${optRowHtml(0)}${optRowHtml(1)}${optRowHtml(2)}</div>
                <button type="button" class="btn btn-outline-secondary btn-sm mb-3" id="nmAdd">+ Ytterligare en tid</button>
                <div class="form-check mb-3"><input class="form-check-input" type="checkbox" id="nmBook">
                    <label class="form-check-label" for="nmBook">Tiden är redan bestämd — boka den första tiden direkt (ingen röstning)</label></div>
                <div class="row g-2 mb-3"><div class="col-sm-6"><label class="form-label" for="nmDeadline">Svara senast (valfritt)</label>
                    <input id="nmDeadline" class="form-control" placeholder="åååå-mm-dd"></div></div>
                <div class="mb-3"><label class="form-label" for="nmUrl">Länk till onlinemötet (valfritt)</label>
                    <input id="nmUrl" class="form-control" placeholder="https://teams.microsoft.com/… eller https://zoom.us/…" inputmode="url"></div>
                <div class="mb-3"><label class="form-label" for="nmPlace">Plats (valfritt)</label><input id="nmPlace" class="form-control" maxlength="200" placeholder="Om ni ses på riktigt"></div>
                <div class="mb-1"><label class="form-label" for="nmAgenda">Dagordning (valfritt)</label><textarea id="nmAgenda" class="form-control" rows="3"></textarea></div>`,
            onOpen: body => {
                body.querySelectorAll('[data-opt-date]').forEach(datePicker);
                datePicker(body.querySelector('#nmDeadline'));
                body.querySelector('#nmAdd').onclick = () => {
                    const n = body.querySelectorAll('[data-opt-row]').length;
                    if (n >= 8) return;
                    body.querySelector('#nmOpts').insertAdjacentHTML('beforeend', optRowHtml(n));
                    datePicker(body.querySelector(`[data-opt-row="${n}"] [data-opt-date]`));
                };
                body.addEventListener('click', ev => { const d = ev.target.closest('[data-opt-del]'); if (d) d.closest('[data-opt-row]').remove(); });
                body.querySelector('#nmTitle').focus();
            },
            onOk: async body => {
                const title = body.querySelector('#nmTitle').value.trim();
                if (!title) return 'Skriv vad mötet gäller.';
                const { options, err } = readOptions(body);
                if (err) return err;
                if (!options.length) return 'Föreslå minst en tid.';
                const book = body.querySelector('#nmBook').checked;
                if (book && options.length !== 1) return 'Ska tiden bokas direkt, låt bara en tid stå kvar.';
                const r = await post('CreateMeeting', {
                    title, options: options.join(';'), bookNow: book ? '1' : '0',
                    voteDeadline: body.querySelector('#nmDeadline').value.trim(), onlineUrl: body.querySelector('#nmUrl').value.trim(),
                    place: body.querySelector('#nmPlace').value.trim(), agenda: body.querySelector('#nmAgenda').value
                });
                if (!r.success) return r.message;
                await reloadMeetings(); CFG.startMeeting = r.meetingId; go('moten');
                showMsg(r.booked ? 'Mötet är bokat. Alla har fått ett mejl med tiden.' : 'Förslaget är skickat. Alla har fått ett mejl och kan svara direkt.', 'success');
                return true;
            }
        });
    }
    function openAddOption(m) {
        formModal({
            title: 'Lägg till en tid', okLabel: 'Lägg till',
            html: `<div id="aoOpts">${optRowHtml(0)}</div>`,
            onOpen: body => body.querySelectorAll('[data-opt-date]').forEach(datePicker),
            onOk: async body => {
                const { options, err } = readOptions(body);
                if (err) return err; if (!options.length) return 'Välj en tid.';
                const [a, b] = options[0].split('|');
                const r = await post('AddOption', { meetingId: m.id, start: a, end: b });
                if (!r.success) return r.message;
                await reloadMeetings(); viewMeetings(); return true;
            }
        });
    }
    function openBookOwn(m) {
        formModal({
            title: 'Flytta mötet', okLabel: 'Flytta',
            html: `<p class="pl-help">Alla får ett mejl om den nya tiden, och kalendrarna flyttar mötet.</p><div>${optRowHtml(0)}</div>`,
            onOpen: body => body.querySelectorAll('[data-opt-date]').forEach(datePicker),
            onOk: async body => {
                const { options, err } = readOptions(body);
                if (err) return err; if (!options.length) return 'Välj en tid.';
                const [a, b] = options[0].split('|');
                const r = await post('Book', { meetingId: m.id, start: a, end: b });
                if (!r.success) return r.message;
                await reloadMeetings(); viewMeetings(); showMsg('Mötet är flyttat och alla har fått ett mejl.', 'success'); return true;
            }
        });
    }
    function openEditMeeting(m) {
        formModal({
            title: 'Ändra mötet', okLabel: 'Spara',
            html: `<div class="mb-3"><label class="form-label" for="emTitle">Rubrik</label><input id="emTitle" class="form-control" maxlength="200" value="${esc(m.title)}"></div>
                <div class="mb-3"><label class="form-label" for="emUrl">Länk till onlinemötet</label><input id="emUrl" class="form-control" value="${esc(m.onlineUrl || '')}" placeholder="https://…"></div>
                <div class="mb-3"><label class="form-label" for="emPlace">Plats</label><input id="emPlace" class="form-control" maxlength="200" value="${esc(m.place || '')}"></div>
                ${m.status === 'Proposed' ? `<div class="mb-3"><label class="form-label" for="emDeadline">Svara senast</label><input id="emDeadline" class="form-control" value="${esc(m.voteDeadline || '')}"></div>` : ''}
                <div class="mb-1"><label class="form-label" for="emAgenda">Dagordning</label><textarea id="emAgenda" class="form-control" rows="4">${esc(m.agenda || '')}</textarea></div>`,
            onOpen: body => datePicker(body.querySelector('#emDeadline')),
            onOk: async body => {
                const r = await post('UpdateMeeting', {
                    meetingId: m.id, title: body.querySelector('#emTitle').value, onlineUrl: body.querySelector('#emUrl').value.trim(),
                    place: body.querySelector('#emPlace').value, agenda: body.querySelector('#emAgenda').value, notes: m.notes || '',
                    voteDeadline: body.querySelector('#emDeadline') ? body.querySelector('#emDeadline').value : (m.voteDeadline || '')
                });
                if (!r.success) return r.message;
                await reloadMeetings(); viewMeetings(); return true;
            }
        });
    }
    async function cancelMeeting(m) {
        if (!await plConfirm('Ställ in mötet?', `${m.title}\n${span(m.startsAt, m.endsAt)}\n\nAlla får ett mejl om att mötet är inställt, och det stryks i kalendrarna.`, 'Ställ in', true)) return;
        const r = await post('CancelMeeting', { meetingId: m.id });
        if (!r.success) { failMsg(r); return; }
        await reloadMeetings(); viewMeetings();
    }
    async function deleteMeeting(m) {
        if (!await plConfirm('Ta bort mötet?', `${m.title}\n\nTiderna och svaren tas bort.`, 'Ta bort', true)) return;
        const r = await post('DeleteMeeting', { meetingId: m.id });
        if (!r.success) { failMsg(r); return; }
        await reloadMeetings(); viewMeetings();
    }

    // ================================================================ Deltagare
    // Nivåerna och deras förklaringar kommer från servern (PlanningAccessLevel.Help) — en text, ett ställe.
    function accessPicker(cur, name) {
        const opts = (S.accessLevels || []).filter(l => l.canGrant || l.value === cur);
        return `<div role="radiogroup" aria-label="Nivå i planeringen">${opts.map(l => `<label class="pl-radio">
            <input type="radio" name="${name}" value="${l.value}" class="form-check-input me-2" data-pl-level="1"${l.value === cur ? ' checked' : ''}${l.canGrant ? '' : ' disabled'}>
            <b>${esc(l.label)}</b>${l.value === 'Participant' ? ' <span class="pl-help small">(det vanliga)</span>' : ''}
            <div class="pl-help small ms-4">${esc(l.help)}</div></label>`).join('')}
            <div class="alert alert-warning small py-2 mt-2${cur === 'Owner' ? '' : ' d-none'}" data-pl-owner-warn="1">
                <i class="bi bi-exclamation-triangle me-1"></i>En sammankallande kan ta bort deltagare och hela planeringen. Ge det bara till den som ska leda gruppen.</div></div>`;
    }
    function readAccess(body, name, fallback) {
        const r = body.querySelector(`input[name="${name}"]:checked`);
        return r ? r.value : fallback;
    }
    document.addEventListener('change', e => {
        const r = e.target.closest('[data-pl-level]');
        if (r) {
            const warn = r.closest('[role="radiogroup"]')?.querySelector('[data-pl-owner-warn]');
            if (warn) warn.classList.toggle('d-none', r.value !== 'Owner');
        }
        const s = e.target.closest('[data-pl-rep]');
        if (s) {
            const w = document.getElementById(s.dataset.plRep + 'RepTextWrap');
            if (w) w.classList.toggle('d-none', s.value !== 't');
            if (s.value === 't') document.getElementById(s.dataset.plRep + 'RepText')?.focus();
        }
    });

    /** Vad personen företräder: en klubb, en krets eller egen text ("Förbundet", "Markägaren"). */
    function repPicker(prefix, cur, clubs, offerOwn) {
        const regions = S.regions || [];
        const sel = cur.clubId ? 'c-' + cur.clubId : cur.regionId ? 'r-' + cur.regionId : cur.text ? 't' : (offerOwn ? 'own' : '');
        const clubOpts = (clubs || []).map(c => `<option value="c-${c.id}"${sel === 'c-' + c.id ? ' selected' : ''}>${esc(c.name)}</option>`).join('');
        return `<div class="row g-2"><div class="col-sm-6"><select class="form-select" id="${prefix}Rep" data-pl-rep="${prefix}">
            ${offerOwn ? `<option value="own"${sel === 'own' ? ' selected' : ''}>Hens egen klubb</option>` : ''}
            <option value="none"${sel === '' ? ' selected' : ''}>Inget särskilt</option>
            ${clubOpts ? `<optgroup label="Klubb">${clubOpts}</optgroup>` : ''}
            ${regions.length ? `<optgroup label="Krets">${regions.map(r => `<option value="r-${r.id}"${sel === 'r-' + r.id ? ' selected' : ''}>${esc(r.name)}</option>`).join('')}</optgroup>` : ''}
            <option value="t"${sel === 't' ? ' selected' : ''}>Något annat — skriv själv</option></select></div>
            <div class="col-sm-6${sel === 't' ? '' : ' d-none'}" id="${prefix}RepTextWrap">
                <input class="form-control" id="${prefix}RepText" maxlength="200" value="${esc(cur.text || '')}" placeholder="Till exempel Förbundet eller Markägaren" aria-label="Vad personen företräder"></div></div>`;
    }
    /** Fälten till servern. "own" = skicka ingenting (servern tar hens klubb); "none" = uttryckligen inget. */
    function readRep(body, prefix) {
        const v = body.querySelector('#' + prefix + 'Rep').value;
        if (v === 'own') return {};
        if (v.startsWith('c-')) return { representsClubId: v.slice(2) };
        if (v.startsWith('r-')) return { representsRegionId: v.slice(2) };
        if (v === 't') return { representsText: (body.querySelector('#' + prefix + 'RepText').value || '').trim() };
        return { representsText: '', representsNothing: '1' };
    }

    function viewPeople() {
        setHead('Deltagare', esc(S.planning.name));
        setActions([
            S.me.canInvite && { label: 'Bjud in…', icon: 'bi-person-plus', fn: openInvite },
            S.me.isParticipant && !TOKEN && { divider: true },
            S.me.isParticipant && !TOKEN && { label: 'Lämna planeringen', icon: 'bi-box-arrow-right', danger: true, fn: leave },
        ]);
        const conveners = S.participants.filter(p => p.access === 'Owner');
        // En rad per deltagare, som de andra listorna. Filtret "Företräder" visar om alla kretsar har någon med.
        const reps = [...new Set(S.participants.map(p => p.represents || ''))].sort((a, b) => (a === '') - (b === '') || a.localeCompare(b, 'sv'));
        if (pplFilter.startsWith('r:') && !reps.includes(pplFilter.slice(2))) pplFilter = '';
        const rowFor = p => {
            const mgr = S.me.canManage;
            const menu = p.isMe ? rowMenu([{ label: 'Ändra vad jag företräder…', fn: () => openEditParticipant(p) }]) : rowMenu([
                S.me.isParticipant && { label: 'Kontaktuppgifter…', fn: () => openContact(p) },
                mgr && S.me.isParticipant && { divider: true },
                mgr && { label: 'Ändra nivå och vad hen företräder…', fn: () => openEditParticipant(p) },
                mgr && { label: 'Byt ut mot någon annan…', fn: () => openReplace(p) },
                mgr && p.isGuest && { label: 'Skicka inbjudan igen', fn: () => resend(p) },
                mgr && { divider: true },
                mgr && { label: 'Ta bort ur planeringen', danger: true, fn: () => removePerson(p) },
            ]);
            const guest = p.isGuest ? ` <span class="badge rounded-pill text-bg-light border" title="Har inget konto — deltar via länkarna i mejlen">Gäst</span>` : '';
            const seen = p.lastSeen ? esc(fmtShort(p.lastSeen)) : (p.inviteSent ? 'Inbjuden ' + esc(fmtShort(p.inviteSent)) + ', har inte öppnat än' : 'Har inte öppnat än');
            const rep = p.represents ? esc(p.represents) : '<span class="pl-help">—</span>';
            return `<tr><td><b>${esc(p.name)}</b>${p.isMe ? ' (du)' : ''}${guest}
                    ${p.guestEmail ? `<div class="pl-help small">${esc(p.guestEmail)}</div>` : ''}
                    <div class="d-sm-none mt-1">${levelBadge(p)}</div>
                    <div class="d-md-none small mt-1">${p.represents ? 'Företräder ' + esc(p.represents) + ' · ' : ''}${p.lastSeen ? 'Senast inne ' : ''}${seen}</div></td>
                <td class="d-none d-md-table-cell">${rep}</td>
                <td class="d-none d-sm-table-cell">${levelBadge(p)}</td>
                <td class="d-none d-md-table-cell small">${seen}</td>
                <td class="text-end">${menu}</td></tr>`;
        };
        const shown = () => {
            const q = pplQuery.trim().toLowerCase();
            return S.participants.filter(p =>
                (!q || [p.name, p.represents, p.guestEmail].some(v => (v || '').toLowerCase().includes(q)))
                && (!pplFilter || (pplFilter === 'guest' ? p.isGuest : pplFilter.startsWith('r:') ? (p.represents || '') === pplFilter.slice(2) : p.access === pplFilter)))
                .sort((a, b) => (LEVEL_RANK[b.access] || 0) - (LEVEL_RANK[a.access] || 0) || a.name.localeCompare(b.name, 'sv'));
        };
        const renderRows = () => {
            const rows = shown();
            $('plPplRows').innerHTML = rows.length ? rows.map(rowFor).join('')
                : '<tr><td colspan="5" class="pl-help">Ingen deltagare matchar.</td></tr>';
            $('plPplCount').textContent = rows.length === S.participants.length
                ? `${rows.length} deltagare`
                : `${rows.length} av ${S.participants.length}`;
        };
        const count = f => S.participants.filter(f).length;
        const levelOpts = (S.accessLevels || []).slice().reverse()
            .map(l => [l.value, l.label, count(p => p.access === l.value)]).filter(o => o[2] > 0);
        const opt = (v, label) => `<option value="${esc(v)}"${v === pplFilter ? ' selected' : ''}>${esc(label)}</option>`;
        const filter = `<select class="form-select form-select-sm" id="plPplFilter" aria-label="Visa" style="max-width:18rem">
                ${opt('', `Alla (${S.participants.length})`)}
                <optgroup label="Nivå">${levelOpts.map(o => opt(o[0], `${o[1]} (${o[2]})`)).join('')}</optgroup>
                ${count(p => p.isGuest) ? opt('guest', `Gäster utan konto (${count(p => p.isGuest)})`) : ''}
                ${reps.length > 1 ? `<optgroup label="Företräder">${reps.map(r => opt('r:' + r, `${r || 'Inget särskilt'} (${count(p => (p.represents || '') === r)})`)).join('')}</optgroup>` : ''}
            </select>`;
        const list = `<div class="d-flex flex-wrap gap-2 align-items-center mb-2">
                <input type="search" class="form-control form-control-sm" id="plPplQ" placeholder="Sök på namn eller vad man företräder" style="max-width:20rem" value="${esc(pplQuery)}">
                ${filter}<span class="ms-auto pl-help small" id="plPplCount"></span></div>
            <div class="table-responsive"><table class="table align-middle mb-0 pl-ppl"><thead><tr>
                <th>Namn</th><th class="d-none d-md-table-cell">Företräder</th><th class="d-none d-sm-table-cell">Nivå</th><th class="d-none d-md-table-cell">Senast inne</th><th></th>
            </tr></thead><tbody id="plPplRows"></tbody></table></div>`;
        const warn = S.me.canManage && conveners.length === 1 && S.participants.length > 1
            ? `<div class="alert alert-warning d-flex gap-2 align-items-start"><i class="bi bi-exclamation-triangle mt-1"></i>
                <div>Det finns bara en <b>sammankallande</b>${conveners[0].isMe ? ' — du' : ''}. Utse gärna en till, så att gruppen inte står utan om hen blir sjuk eller slutar:
                välj <i>Ändra nivå</i> på personen.</div></div>` : '';
        $('plBody').innerHTML = `<div id="plReqs"></div>${warn}<div class="pl-card"><p class="pl-help">Du bjuder in <b>medlemmar</b> genom att söka på namnet — ingen behöver känna till någon annans e-postadress.
            Den som inte är skytt (t.ex. någon från kommunen eller markägaren) bjuds in med e-post och deltar via länkarna i mejlen.</p>${list}</div>
            <div class="pl-card"><h2>Nivåerna</h2><ul class="mb-0">${(S.accessLevels || []).slice().reverse().map(l =>
                `<li class="mb-1">${levelBadge({ access: l.value, accessLabel: l.label })} — ${esc(l.help)}</li>`).join('')}</ul></div>`;
        renderRows();
        $('plPplQ').addEventListener('input', e => { pplQuery = e.target.value; renderRows(); });
        $('plPplFilter').addEventListener('change', e => { pplFilter = e.target.value; renderRows(); });
        loadRequests();
    }
    // Kontaktuppgifter hämtas när man frågar — de ligger aldrig i planeringens data.
    async function openContact(p) {
        const r = await get('ContactInfo', { participantId: p.id });
        if (!r.success) { showMsg(esc(r.message || 'Det gick inte att hämta kontaktuppgifterna.'), 'danger'); return; }
        const tel = (r.phone || '').replace(/[^\d+]/g, '');
        const line = (icon, label, value, href) => `<div class="d-flex gap-3 align-items-start py-2 border-top">
                <i class="bi ${icon} fs-5 text-body-secondary"></i><div class="flex-grow-1"><div class="small text-body-secondary">${label}</div>
                ${value ? `<a href="${esc(href)}" class="fs-5 text-break">${esc(value)}</a>` : '<span class="pl-help">Saknas</span>'}</div></div>`;
        formModal({
            title: 'Kontaktuppgifter', wide: false, cancelLabel: 'Stäng',
            html: `<div class="mb-2"><b class="fs-5">${esc(r.name)}</b>${r.isGuest ? ' <span class="badge rounded-pill text-bg-light border">Gäst</span>' : ''}
                    ${r.club ? `<div class="pl-help">${esc(r.club)}</div>` : ''}${p.represents ? `<div class="pl-help">Företräder ${esc(p.represents)}</div>` : ''}</div>
                ${line('bi-envelope', 'E-post', r.email, 'mailto:' + r.email)}
                ${r.isGuest ? '' : line('bi-telephone', 'Telefon', r.phone, 'tel:' + tel)}
                <p class="pl-help small mt-2 mb-0">Uppgifterna är till för planeringen — dela dem inte vidare.</p>`,
        });
    }

    // ================================================================ Bjud in — guiden (Stefan 2026-10-23)
    // Steg 1: en medlem eller någon utan konto. Medlem: sök → Företräder med HENS klubbar och kretsar + nivå,
    // och hen är med direkt. E-post: namn och adress — hen får en inbjudan att SVARA, och den sammankallande
    // väljer nivå och Företräder när svaret kommit. En länk i ett mejl ger aldrig tillgång i sig.
    function memberRepPicker(prefix, o) {
        const clubs = o.clubs || [], regions = o.regions || [];
        const first = o.primaryClubId ? 'c-' + o.primaryClubId : clubs.length ? 'c-' + clubs[0].id : 'none';
        const opt = (v, label) => `<option value="${v}"${v === first ? ' selected' : ''}>${esc(label)}</option>`;
        return `<div class="row g-2"><div class="col-sm-6"><select class="form-select" id="${prefix}Rep" data-pl-rep="${prefix}">
            ${clubs.length ? `<optgroup label="Hens klubb">${clubs.map(c => opt('c-' + c.id, c.name)).join('')}</optgroup>` : ''}
            ${regions.length ? `<optgroup label="Hens krets">${regions.map(r => opt('r-' + r.id, r.name)).join('')}</optgroup>` : ''}
            ${opt('none', 'Inget särskilt')}
            <option value="t">Något annat — skriv själv</option></select></div>
            <div class="col-sm-6 d-none" id="${prefix}RepTextWrap">
                <input class="form-control" id="${prefix}RepText" maxlength="200" placeholder="Till exempel Förbundet" aria-label="Vad personen företräder"></div></div>`;
    }
    function openInvite() {
        if (!S.me.canInvite) return;
        let timer = null, hits = [], chosen = null;
        const err = m => { const e = $('plFormErr'); e.textContent = m; e.classList.toggle('d-none', !m); };
        const step1 = () => `<p class="mb-3">Vem vill du bjuda in?</p>
            <div class="d-grid gap-2">
                <button type="button" class="btn btn-outline-secondary text-start p-3" data-iv-go="member">
                    <b><i class="bi bi-person me-2"></i>En medlem på pistol.nu</b>
                    <div class="small text-body-secondary ms-4">Sök på namnet. Hen är med direkt och deltar med sitt eget konto.</div></button>
                <button type="button" class="btn btn-outline-secondary text-start p-3" data-iv-go="email">
                    <b><i class="bi bi-envelope me-2"></i>Någon utan konto (e-post)</b>
                    <div class="small text-body-secondary ms-4">Till exempel någon från kommunen eller markägaren. Hen får ett mejl och svarar om hen vill vara med. Du släpper in hen när svaret kommit.</div></button>
            </div>
            <p class="pl-help small mt-3 mb-0">Den som bjuds in får ett mejl med ditt namn. Högst ${S.inviteCap || 30} inbjudningar per dygn och planering.</p>`;
        const stepSearch = () => `<label class="form-label fw-semibold" for="ivQ">Sök på namn</label>
            <input id="ivQ" class="form-control mb-2" placeholder="Till exempel Anna Svensson" autocomplete="off">
            <div id="ivHits" class="pl-help">Skriv minst två bokstäver.</div>
            <button type="button" class="btn btn-link px-0 mt-2" data-iv-go="start">← Tillbaka</button>`;
        const stepMember = o => `<p>Bjud in <b>${esc(chosen.name)}</b>${chosen.club ? ' <span class="pl-help">(' + esc(chosen.club) + ')</span>' : ''}.</p>
            <div class="mb-3"><label class="form-label fw-semibold" for="ivRep">Företräder</label>${memberRepPicker('iv', o)}</div>
            <div class="mb-3"><div class="form-label fw-semibold">Nivå i planeringen</div>${accessPicker('Participant', 'ivAccess')}</div>
            <div class="d-flex gap-2"><button type="button" class="btn btn-outline-secondary" data-iv-go="member">Tillbaka</button>
                <button type="button" class="btn btn-primary" data-iv-send="member">Bjud in</button></div>`;
        const stepEmail = () => `<div class="mb-3"><label class="form-label" for="ieName">Namn</label><input id="ieName" class="form-control" maxlength="200" autocomplete="off"></div>
            <div class="mb-3"><label class="form-label" for="ieEmail">E-post</label><input id="ieEmail" class="form-control" type="email" inputmode="email" autocomplete="off"></div>
            <div class="mb-3"><label class="form-label" for="ieMsg">Några rader till hen (valfritt)</label>
                <textarea id="ieMsg" class="form-control" rows="3" maxlength="1000" placeholder="Till exempel varför ni vill ha med hen"></textarea></div>
            <p class="pl-help small">Hen får ett mejl och svarar om hen vill vara med, och kan skriva vem hen företräder. Du väljer nivå och godkänner under <b>Deltagare</b> när svaret kommit — först då kommer hen in.</p>
            <div class="d-flex gap-2"><button type="button" class="btn btn-outline-secondary" data-iv-go="start">Tillbaka</button>
                <button type="button" class="btn btn-primary" data-iv-send="email">Skicka inbjudan</button></div>`;
        const done = html => `${html}<div class="d-flex gap-2 mt-3"><button type="button" class="btn btn-outline-secondary" data-iv-go="start">Bjud in fler</button>
            <button type="button" class="btn btn-primary" data-bs-dismiss="modal">Klar</button></div>`;
        formModal({
            title: 'Bjud in', okLabel: null, wide: true, html: step1(),
            onOpen: body => {
                const show = html => { err(''); body.innerHTML = html; };
                body.addEventListener('input', ev => {
                    if (ev.target.id !== 'ivQ') return;
                    clearTimeout(timer);
                    timer = setTimeout(async () => {
                        const v = ev.target.value.trim();
                        const box = body.querySelector('#ivHits'); if (!box) return;
                        if (v.length < 2) { box.textContent = 'Skriv minst två bokstäver.'; return; }
                        const r = await get('SearchMembers', { q: v });
                        if (!r.success) { box.textContent = r.message || 'Sökningen gick inte.'; return; }
                        hits = r.members;
                        box.innerHTML = hits.length ? hits.map((m, i) =>
                            `<div class="pl-person"><div><b>${esc(m.name)}</b><div class="small">${esc(m.club)}</div></div>
                            ${m.alreadyIn ? '<span class="pl-state ok">Är med</span>' : `<button type="button" class="btn btn-outline-primary btn-sm" data-iv-pick="${i}">Välj</button>`}</div>`).join('')
                            : 'Ingen medlem hittades. Saknar personen konto: gå tillbaka och välj <i>Någon utan konto</i>.';
                    }, 250);
                });
                body.addEventListener('click', async ev => {
                    const go = ev.target.closest('[data-iv-go]');
                    if (go) {
                        const s = go.dataset.ivGo;
                        show(s === 'member' ? stepSearch() : s === 'email' ? stepEmail() : step1());
                        (body.querySelector('#ivQ') || body.querySelector('#ieName'))?.focus();
                        return;
                    }
                    const pick = ev.target.closest('[data-iv-pick]');
                    if (pick) {
                        chosen = hits[parseInt(pick.dataset.ivPick, 10)];
                        const o = await get('RepresentOptions', { memberId: chosen.memberId });
                        if (!o.success) { err(o.message || 'Det gick inte.'); return; }
                        show(stepMember(o));
                        return;
                    }
                    const send = ev.target.closest('[data-iv-send]'); if (!send) return;
                    send.disabled = true;
                    try {
                        if (send.dataset.ivSend === 'member') {
                            const r = await post('AddMember', Object.assign({ memberId: chosen.memberId, access: readAccess(body, 'ivAccess', 'Participant') }, readRep(body, 'iv')));
                            if (!r.success) { err(r.message); return; }
                            await reloadPeople(); if (view === 'deltagare') viewPeople();
                            show(done(`<div class="alert alert-success mb-0">${esc(chosen.name)} är med${r.mailed ? ' och har fått ett mejl' : ''}.${r.alreadyIn ? ' (Hen var redan med.)' : ''}</div>`));
                        } else {
                            const name = body.querySelector('#ieName').value.trim();
                            const r = await post('InviteByEmail', { name, email: body.querySelector('#ieEmail').value, message: body.querySelector('#ieMsg').value });
                            if (!r.success) { err(r.message); return; }
                            if (view === 'deltagare') loadRequests();
                            show(done(`<div class="alert alert-${r.mailed ? 'success' : 'warning'} mb-0">${r.mailed
                                ? `Inbjudan till ${esc(name)} är skickad. När hen svarat ja ser du det under <b>Deltagare → Väntar på dig</b>.`
                                : `Inbjudan är sparad, men mejlet till ${esc(name)} kunde inte skickas. Kontrollera adressen.`}
                                ${r.supportLink ? `<div class="small mt-2">Länken (för support): <a href="${esc(r.supportLink)}" target="_blank" rel="noopener">svara på inbjudan</a></div>` : ''}</div>`));
                        }
                    } finally { send.disabled = false; }
                });
            }
        });
    }

    // ================================================================ Förfrågningar — väntar på dig
    async function loadRequests() {
        const box = $('plReqs'); if (!box || !(S.me.canInvite || S.me.canManage)) return;
        const r = await get('Requests');
        if (!r.success || !$('plReqs')) return;
        const pending = r.requests.filter(x => x.status === 'Pending'), sent = r.requests.filter(x => x.status === 'Sent');
        REQS = r.requests;
        const pend = pending.map(x => `<div class="pl-person"><div>
                <div><b>${esc(x.name)}</b> ${x.club ? '<span class="pl-help small">(' + esc(x.club) + ')</span>' : ''}</div>
                <div class="small">${x.kind === 'Invite' ? 'Har tackat ja till inbjudan' + (x.invitedBy ? ' från ' + esc(x.invitedBy) : '') : 'Ber om att få vara med'} · ${esc(fmtShort(x.respondedAt || x.createdAt))}</div>
                ${x.proposedRepresents ? `<div class="small">Företräder enligt hen själv: <b>${esc(x.proposedRepresents)}</b></div>` : ''}
                ${x.message && x.kind === 'Request' ? `<div class="small pl-help" style="white-space:pre-wrap">”${esc(x.message)}”</div>` : ''}</div>
                ${r.canDecide ? `<div class="d-flex gap-2 flex-wrap"><button class="btn btn-primary btn-sm" data-req-ok="${x.id}">Godkänn…</button>
                    <button class="btn btn-outline-secondary btn-sm" data-req-no="${x.id}">Avböj…</button></div>` : ''}</div>`).join('');
        const sentHtml = sent.map(x => `<div class="pl-person"><div><div><b>${esc(x.name)}</b> <span class="pl-help small">${esc(x.email || '')}</span></div>
                <div class="small pl-help">Inbjuden ${esc(fmtShort(x.createdAt))}${x.invitedBy ? ' av ' + esc(x.invitedBy) : ''} — har inte svarat än</div></div>
                <div>${rowMenu([{ label: 'Dra tillbaka inbjudan', danger: true, fn: () => withdrawInvite(x) }])}</div></div>`).join('');
        box.innerHTML = (pending.length ? `<div class="pl-card border-warning"><h2>Väntar på ${r.canDecide ? 'dig' : 'den sammankallande'} (${pending.length})</h2>
                <p class="pl-help">De här kommer inte in förrän de är godkända. När du godkänner väljer du nivå och vad personen företräder.${r.support ? ' <b>Du godkänner som sajtens administratör (support).</b>' : ''}</p>${pend}</div>` : '')
            + (sent.length ? `<div class="pl-card"><h2>Inbjudna som inte svarat (${sent.length})</h2>${sentHtml}</div>` : '');
    }
    let REQS = [];
    document.addEventListener('click', e => {
        const ok = e.target.closest('[data-req-ok]'); if (ok) { openApprove(REQS.find(x => x.id === +ok.dataset.reqOk)); return; }
        const no = e.target.closest('[data-req-no]'); if (no) openReject(REQS.find(x => x.id === +no.dataset.reqNo));
    });
    function openApprove(x) {
        if (!x) return;
        const member = x.kind === 'Request';
        formModal({
            title: 'Släpp in ' + x.name, okLabel: 'Godkänn',
            html: `<p>${member ? `${esc(x.name)} ber om att få vara med.` : `${esc(x.name)} har tackat ja till inbjudan.`} Hen får ett mejl när du har godkänt.</p>
                <div class="mb-3"><label class="form-label fw-semibold" for="apRep">Företräder</label>
                ${member ? memberRepPicker('ap', { clubs: x.clubChoices, regions: x.regionChoices }) : repPicker('ap', { text: x.proposedRepresents }, [], false)}
                ${!member && x.proposedRepresents ? `<div class="form-text">Hen föreslog själv: ${esc(x.proposedRepresents)}</div>` : ''}</div>
                <div class="mb-1"><div class="form-label fw-semibold">Nivå i planeringen</div>${accessPicker('Participant', 'apAccess')}</div>`,
            onOk: async body => {
                const r = await post('ApproveRequest', Object.assign({ requestId: x.id, access: readAccess(body, 'apAccess', 'Participant') }, readRep(body, 'ap')));
                if (!r.success) return r.message;
                await reloadPeople(); viewPeople();
                showMsg(`${esc(x.name)} är med${r.mailed ? ' och har fått ett mejl' : ', men mejlet kunde inte skickas'}.`, r.mailed ? 'success' : 'warning');
                return true;
            }
        });
    }
    function openReject(x) {
        if (!x) return;
        formModal({
            title: 'Avböj ' + x.name, okLabel: 'Avböj', okClass: 'btn-danger', wide: false,
            html: `<p>${esc(x.name)} kommer inte in i planeringen och får ett mejl om det.</p>
                <label class="form-label" for="rjNote">Skäl (valfritt, står i mejlet)</label>
                <textarea id="rjNote" class="form-control" rows="3" maxlength="500"></textarea>`,
            onOk: async body => {
                const r = await post('RejectRequest', { requestId: x.id, note: body.querySelector('#rjNote').value });
                if (!r.success) return r.message;
                await reloadPeople(); viewPeople();
                return true;
            }
        });
    }
    async function withdrawInvite(x) {
        if (!await plConfirm('Dra tillbaka inbjudan?', `Länken i mejlet till ${x.name} slutar fungera.`, 'Dra tillbaka', true)) return;
        const r = await post('WithdrawInvite', { requestId: x.id });
        if (!r.success) { failMsg(r); return; }
        loadRequests();
    }
    function openEditParticipant(p) {
        const levelEditable = S.me.canManage && !p.isMe;
        formModal({
            title: p.isMe ? 'Vad jag företräder' : 'Ändra ' + p.name, okLabel: 'Spara',
            html: `${levelEditable ? `<div class="mb-3"><div class="form-label fw-semibold">Nivå i planeringen</div>${accessPicker(p.access, 'epAccess')}</div>` : ''}
                <div class="mb-1"><label class="form-label fw-semibold" for="epRep">Företräder</label>
                ${repPicker('ep', { clubId: p.representsClubId, regionId: p.representsRegionId, text: p.representsText }, p.clubChoices, false)}</div>
                <p class="pl-help small mt-2 mb-0">Syns på deltagarlistan, så att alla ser vem som företräder vilken klubb, krets eller annan part.</p>`,
            onOk: async body => {
                const rep = readRep(body, 'ep');
                const r = await post('UpdateParticipant', {
                    participantId: p.id,
                    access: levelEditable ? readAccess(body, 'epAccess', p.access) : p.access,
                    representsClubId: rep.representsClubId || '', representsRegionId: rep.representsRegionId || '', representsText: rep.representsText || '',
                });
                if (!r.success) return r.message;
                await reloadPeople(); viewPeople(); return true;
            }
        });
    }
    /** Byt ut: den nya får samma nivå och företräder samma; den gamla lämnar, hens meddelanden står kvar. */
    function openReplace(p) {
        let timer = null, chosen = 0, chosenName = '';
        formModal({
            title: 'Byt ut ' + p.name, okLabel: 'Byt ut', wide: true,
            html: `<p>Den nya personen får samma nivå (<b>${esc(p.accessLabel)}</b>)${p.represents ? ` och företräder samma (<b>${esc(p.represents)}</b>)` : ''}.
                ${esc(p.name)} lämnar planeringen; hens meddelanden står kvar. Den nya får ett mejl med inbjudan.</p>
                <div class="mb-2"><label class="pl-radio d-inline-block me-3"><input type="radio" name="rpMode" value="member" class="form-check-input me-2" checked> En medlem</label>
                    <label class="pl-radio d-inline-block"><input type="radio" name="rpMode" value="email" class="form-check-input me-2"> Någon utan konto (e-post)</label></div>
                <div id="rpMember"><label class="form-label" for="rpQ">Sök på namn</label>
                    <input id="rpQ" class="form-control mb-2" placeholder="Till exempel Anna Svensson" autocomplete="off">
                    <div id="rpHits" class="pl-help">Skriv minst två bokstäver.</div></div>
                <div id="rpEmail" class="d-none">
                    <div class="mb-2"><label class="form-label" for="rpName">Namn</label><input id="rpName" class="form-control" maxlength="200" autocomplete="off"></div>
                    <div class="mb-1"><label class="form-label" for="rpMail">E-post</label><input id="rpMail" class="form-control" type="email" inputmode="email" autocomplete="off"></div></div>`,
            onOpen: body => {
                body.addEventListener('change', ev => {
                    if (!ev.target.matches('input[name="rpMode"]')) return;
                    const email = ev.target.value === 'email';
                    body.querySelector('#rpMember').classList.toggle('d-none', email);
                    body.querySelector('#rpEmail').classList.toggle('d-none', !email);
                });
                const q = body.querySelector('#rpQ'); q.focus();
                q.addEventListener('input', () => {
                    clearTimeout(timer);
                    timer = setTimeout(async () => {
                        const v = q.value.trim();
                        if (v.length < 2) { body.querySelector('#rpHits').textContent = 'Skriv minst två bokstäver.'; return; }
                        const r = await get('SearchMembers', { q: v });
                        if (!r.success) { body.querySelector('#rpHits').textContent = r.message || 'Sökningen gick inte.'; return; }
                        body.querySelector('#rpHits').innerHTML = r.members.length ? r.members.map((m, i) =>
                            `<div class="pl-person"><div><b>${esc(m.name)}</b><div class="small">${esc(m.club)}</div></div>
                            ${m.alreadyIn ? '<span class="pl-state ok">Är med</span>' : `<button type="button" class="btn btn-outline-primary btn-sm" data-rp-pick="${m.memberId}" data-rp-i="${i}">Välj</button>`}</div>`).join('')
                            : 'Ingen medlem hittades. Saknar personen konto: välj "Någon utan konto".';
                        body._rpHits = r.members;
                    }, 250);
                });
                body.addEventListener('click', ev => {
                    const b = ev.target.closest('[data-rp-pick]'); if (!b) return;
                    chosen = parseInt(b.dataset.rpPick, 10);
                    chosenName = (body._rpHits || [])[parseInt(b.dataset.rpI, 10)]?.name || '';
                    body.querySelectorAll('[data-rp-pick]').forEach(x => { x.classList.remove('btn-primary'); x.classList.add('btn-outline-primary'); x.textContent = 'Välj'; });
                    b.classList.remove('btn-outline-primary'); b.classList.add('btn-primary'); b.textContent = 'Vald';
                });
            },
            onOk: async body => {
                const email = body.querySelector('input[name="rpMode"]:checked').value === 'email';
                if (!email && !chosen) return 'Sök fram och välj den som ska ta över.';
                const r = await post('ReplaceParticipant', email
                    ? { participantId: p.id, name: body.querySelector('#rpName').value, email: body.querySelector('#rpMail').value }
                    : { participantId: p.id, memberId: chosen });
                if (!r.success) return r.message;
                await reloadPeople(); viewPeople();
                showMsg(`${esc(email ? body.querySelector('#rpName').value : chosenName)} har ersatt ${esc(p.name)}.`
                    + (r.mailed ? ' Ett mejl med inbjudan är skickat.' : ' Men mejlet kunde inte skickas — skicka inbjudan igen under Deltagare.'), r.mailed ? 'success' : 'warning');
                return true;
            }
        });
    }
    async function resend(p) {
        const r = await post('ResendInvite', { participantId: p.id });
        if (!r.success) { failMsg(r); return; }
        showMsg('Inbjudan är skickad igen.', 'success');
    }
    async function removePerson(p) {
        if (!await plConfirm('Ta bort ' + p.name + '?', 'Hen får inga fler mejl och länkarna i hens mejl slutar fungera. Hens meddelanden står kvar.', 'Ta bort', true)) return;
        const r = await post('RemoveParticipant', { participantId: p.id });
        if (!r.success) { failMsg(r); return; }
        await reloadPeople(); viewPeople();
    }
    async function leave() {
        if (!await plConfirm('Lämna planeringen?', 'Du får inga fler mejl från planeringen. Dina meddelanden står kvar.', 'Lämna', true)) return;
        const r = await post('RemoveParticipant', { participantId: S.me.participantId });
        if (!r.success) { failMsg(r); return; }
        location.href = TOKEN ? '/' : '/tavlingsplanering';
    }
    async function reloadPeople() { const s = await get('Get'); if (s.success) { S = s; version = s.version; } }

    // ================================================================ Mina notiser
    function viewNotify() {
        setHead('Mina notiser', esc(S.planning.name));
        setActions([]);
        const cur = S.me.notifyMode;
        $('plBody').innerHTML = `<div class="pl-card"><h2>Hur vill du få veta vad som händer?</h2>
            <p class="pl-help">Gäller bara den här planeringen. Du kan ändra när som helst.</p>
            ${S.notifyModes.map(n => `<label class="pl-radio"><input type="radio" name="plNotify" value="${n.value}" class="form-check-input me-2"${n.value === cur ? ' checked' : ''}>
                <b>${esc(n.label)}</b>${n.value === 'Daily' ? ' <span class="pl-help small">(förval)</span>' : ''}<div class="pl-help small ms-4">${esc(n.help)}</div></label>`).join('')}
            <div id="plNotifySaved" class="pl-help small"></div></div>
            ${S.me.icsUrl ? `<div class="pl-card"><h2>Mötena i din kalender</h2>
                <p>Lägg planeringens bokade möten i din egen kalender (telefonen, Outlook, Google). Flyttas ett möte flyttas det i kalendern också.</p>
                <a class="btn btn-outline-secondary" href="${esc(S.me.icsUrl)}"><i class="bi bi-calendar-plus me-1"></i>Lägg till kalendern</a>
                <div class="mt-2"><label class="form-label small" for="plIcsUrl">Eller kopiera adressen och prenumerera på den i din kalender:</label>
                <input id="plIcsUrl" class="form-control form-control-sm" readonly value="${esc(S.me.icsUrl)}"></div></div>` : ''}
            ${!S.me.isGuest ? `<div class="pl-card"><h2>Notiser i telefonen</h2><p class="pl-help">Notiser i telefonen kräver att du slagit på notiser för pistol.nu på den telefon eller dator du vill få dem på.
                ${S.me.pushConfigured ? '' : 'Notiser i telefonen är inte påslagna på den här sajten ännu — du får mejl.'}</p>
                <a class="btn btn-outline-primary" href="/user-profile-page#notiser"><i class="bi bi-bell me-1"></i>Slå på notiser på Min sida</a></div>` : ''}`;
        $('plBody').querySelectorAll('input[name="plNotify"]').forEach(r => r.addEventListener('change', async () => {
            const res = await post('SetMyNotify', { mode: r.value });
            if (!res.success) { failMsg(res); return; }
            S.me.notifyMode = r.value;
            $('plNotifySaved').textContent = 'Sparat: ' + res.label + '.';
        }));
        $('plIcsUrl')?.addEventListener('focus', e => e.target.select());
    }

    // ================================================================ Inställningar
    function viewSettings() {
        setHead('Inställningar', esc(S.planning.name));
        const p = S.planning;
        setActions(S.me.canManage ? [
            { label: p.status === 'Archived' ? 'Återuppta planeringen' : 'Arkivera planeringen', icon: 'bi-archive', fn: toggleArchive },
            { divider: true },
            { label: 'Ta bort planeringen…', icon: 'bi-trash', danger: true, fn: removePlanning },
        ] : []);
        $('plBody').innerHTML = `<div class="pl-card" style="max-width:720px">
            <div class="mb-3"><label class="form-label fw-semibold" for="stName">Namn</label><input id="stName" class="form-control" maxlength="200" value="${esc(p.name)}"></div>
            <div class="mb-3"><label class="form-label fw-semibold" for="stTarget">Ungefär när och var</label>
                <input id="stTarget" class="form-control" maxlength="200" value="${esc(p.targetText || '')}" placeholder="Till exempel: augusti 2028, Skillingaryd?"></div>
            <div class="row g-2 mb-3"><div class="col-sm-6"><label class="form-label" for="stStart">Första dagen (om den är bestämd)</label><input id="stStart" class="form-control" value="${esc(p.targetStart || '')}"></div>
                <div class="col-sm-6"><label class="form-label" for="stEnd">Sista dagen</label><input id="stEnd" class="form-control" value="${esc(p.targetEnd || '')}"></div></div>
            ${S.me.canManage ? `<div class="form-check form-switch mb-3"><input class="form-check-input" type="checkbox" role="switch" id="stListed"${p.listed ? ' checked' : ''}>
                <label class="form-check-label fw-semibold" for="stListed">Visa planeringen för alla medlemmar, så att de kan be om att få vara med</label>
                <div class="pl-help small">Då ser alla inloggade medlemmar namnet, ungefär när och var, och vem som är sammankallande — men inget av innehållet.
                    Den som ber om att få vara med kommer in först när du har godkänt. Avstängt: bara de inbjudna vet att planeringen finns.</div></div>` : ''}
            <button class="btn btn-primary" id="stSave">Spara</button> <span id="stSaved" class="pl-help ms-2"></span></div>`;
        datePicker($('stStart')); datePicker($('stEnd'));
        $('stSave').onclick = async () => {
            const params = { name: $('stName').value, targetText: $('stTarget').value, targetStart: $('stStart').value.trim(), targetEnd: $('stEnd').value.trim() };
            if ($('stListed')) params.listed = $('stListed').checked ? '1' : '0';
            const r = await post('Update', params);
            if (!r.success) { failMsg(r); return; }
            await reloadPeople(); renderRail(); setHead('Inställningar', esc(S.planning.name)); $('stSaved').textContent = 'Sparat.';
        };
    }
    async function toggleArchive() {
        const arch = S.planning.status !== 'Archived';
        if (arch && !await plConfirm('Arkivera planeringen?', 'Den finns kvar och går att läsa, men inga påminnelser och inga kvällsmejl går ut.', 'Arkivera')) return;
        const r = await post('SetStatus', { status: arch ? 'Archived' : 'Active' });
        if (!r.success) { failMsg(r); return; }
        await reloadPeople(); render('installningar');
    }
    async function removePlanning() {
        if (!await plConfirm('Ta bort planeringen?', 'Ingen kommer längre åt den, länkarna i mejlen slutar fungera och inga fler mejl går ut.', 'Ta bort', true)) return;
        const r = await post('Delete', {});
        if (!r.success) { failMsg(r); return; }
        location.href = '/tavlingsplanering';
    }

    // ================================================================ Pollningen
    let polling = false;
    async function poll() {
        if (polling || document.visibilityState !== 'visible' || !S) return;
        polling = true;
        try {
            const v = await get('Version');
            if (!v.ok) { clearInterval(timer); renderMissing(); return; }
            if (v.version !== version) {
                version = v.version;
                const typing = view === 'meddelanden' && $('plText') && document.activeElement === $('plText');
                await Promise.all([reloadPeople(), reloadMeetings(), S.me.canReadMessages ? reloadMessages() : Promise.resolve()]);
                version = S.version;
                if (view === 'meddelanden' && $('plThread')) refreshThreadOnly();          // skrivrutan rörs inte
                else if (view === 'moten' && !document.querySelector('[data-pl-notes]:focus')) viewMeetings();
                else if (!typing && view !== 'installningar' && !document.querySelector('.modal.show')) render(view);
                else renderRail();
            }
        } catch (e) { /* tyst: nästa varv försöker igen */ }
        finally { polling = false; }
    }
    const timer = setInterval(poll, 10000);
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') { poll(); markRead(); } });

    // ================================================================ Start
    (async () => {
        try {
            if (!await loadAll()) return;
            render(hashView() || CFG.startView || 'oversikt');
            if (CFG.startView && !hashView()) history.replaceState(null, '', '#vy=' + view);
            if (CFG.leave && S.me.isParticipant) leave(); // "Lämna planeringen" i inbjudningsmejlet
        } catch (e) {
            $('plBody').innerHTML = `<div class="pl-card"><p class="text-danger mb-0">${esc(e.message || 'Sidan kunde inte laddas.')}</p></div>`;
        }
    })();
})();
