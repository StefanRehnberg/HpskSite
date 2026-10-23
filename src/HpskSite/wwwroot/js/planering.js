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
    // Bemanningens och dagsprogrammets sidor har samma ram och vänstermeny (Stefan 2026-10-23), men sitt
    // eget innehåll: där ritas bara rälsen, och dess poster leder tillbaka till planeringens sida.
    const RAIL_ONLY = CFG.railOnly || '';
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
    let TASKS = null;      // Tasks-svaret
    let DOCS = null;       // Documents-svaret (arkivet och bifogningarna)
    let DEC = null;        // Decisions-svaret (förslag och beslut)
    let pendingDocs = new Map(); // dokument som bifogas i meddelandet som skrivs: id -> namn
    let version = 0;
    let showPastMeetings = false;        // Möteslistans "Visa tidigare möten"
    let tkQuery = '', tkFilter = '';     // Uppgifternas sök och filter (lever över omritningar)
    let tkPart = '', msgPart = '';       // P3 steg D: filtret på del ('' = alla, '0' = hela planeringen, annars delens id)
    function parts() { return (S && S.parts) || []; }
    function partById(id) { return parts().find(p => p.id === id); }
    function partChip(id) {
        const p = id ? partById(id) : null;
        return p ? ` <span class="pl-role" title="Delen"><i class="bi bi-diagram-3 me-1" aria-hidden="true"></i>${esc(p.name)}</span>` : '';
    }
    function partFilterOptions(cur) {
        const o = (v, l) => `<option value="${v}"${String(cur) === String(v) ? ' selected' : ''}>${esc(l)}</option>`;
        return o('', 'Alla delar') + o('0', 'Hela planeringen') + parts().map(p => o(p.id, p.name)).join('');
    }
    function partMatches(partId, f) { return f === '' || (f === '0' ? !partId : partId === parseInt(f, 10)); }
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
        { key: 'uppgifter', label: 'Uppgifter' },
        { key: 'beslut', label: 'Förslag och beslut' },
        { key: 'dokument', label: 'Dokument' },
        { key: 'deltagare', label: 'Deltagare' },
        { key: 'delar', label: 'Delar', need: s => s.me.canEdit || (s.parts || []).length > 0 },
        { key: 'notiser', label: 'Mina notiser', need: s => s.me.isParticipant },
        { key: 'installningar', label: 'Inställningar', need: s => s.me.canEdit },
    ];

    function hashParams() { return new URLSearchParams((location.hash || '').replace(/^#/, '')); }
    function hashView() { return hashParams().get('vy') || ''; }
    function go(v) {
        if (location.hash !== '#vy=' + v) history.pushState(null, '', '#vy=' + v);
        render(v);
    }
    // Ett möte har en egen sida (#vy=mote&id=N) — rälsen visar Möten.
    function goMeeting(id) {
        const h = '#vy=mote&id=' + id;
        if (location.hash !== h) history.pushState(null, '', h);
        render('mote');
    }
    document.addEventListener('click', e => {
        const a = e.target.closest('[data-pl-open-meeting]'); if (!a) return;
        if (e.target.closest('.dropdown')) return; // radens Åtgärder öppnar sin meny, inte mötet
        e.preventDefault(); goMeeting(parseInt(a.dataset.plOpenMeeting, 10));
    });
    window.addEventListener('hashchange', () => { if (!RAIL_ONLY) render(hashView() || 'oversikt'); });
    window.addEventListener('popstate', () => { if (!RAIL_ONLY) render(hashView() || 'oversikt'); });
    document.addEventListener('click', e => {
        if (RAIL_ONLY) return; // vanliga länkar till planeringens sida
        const a = e.target.closest('[data-pl-go]'); if (!a) return;
        e.preventDefault();
        if (a.dataset.plFilter) tkFilter = a.dataset.plFilter;
        go(a.dataset.plGo);
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
        const counts = { meddelanden: unreadCount(), moten: votesOwed(), uppgifter: myOpenTasks(), beslut: decisionsOwed(), deltagare: s.me.pendingRequests || 0 };
        const here = RAIL_ONLY || (view === 'mote' ? 'moten' : view);
        const base = RAIL_ONLY ? '/tavlingsplanering/' + ID : '';
        $('plRail').innerHTML = VIEWS.filter(v => !v.need || v.need(s)).map(v =>
            `<a href="${base}#vy=${v.key}" class="pl-nav${v.key === here ? ' on' : ''}" data-pl-go="${v.key}">${esc(v.label)}`
            + (counts[v.key] ? `<span class="pl-count" title="${counts[v.key]} väntar">${counts[v.key]}</span>` : '') + '</a>').join('')
            // P2b: bemanningen är ett eget rutnät på en egen sida (det behöver hela bredden).
            // Öppnas i samma flik (en egen sida som behöver hela bredden), därför ingen ny-flik-ikon.
            + (s.me.canEdit && s.planning.staffingUrl ? `<a href="${esc(s.planning.staffingUrl)}" class="pl-nav${here === 'bemanning' ? ' on' : ''}">Bemanning</a>` : '')
            // Dagsprogrammet hör till tävlingen och finns här när planeringen är kopplad (Stefan 2026-10-23).
            + (s.me.canEdit && s.planning.agendaUrl ? `<a href="${esc(s.planning.agendaUrl)}" class="pl-nav${here === 'dagsprogram' ? ' on' : ''}">Dagsprogram</a>` : '');
    }

    function myOpenTasks() {
        if (!TASKS) return S ? (S.me.openTasks || 0) : 0;
        return TASKS.tasks.filter(t => t.mine && t.status !== 'Done').length;
    }

    // Öppna förslag jag ska svara på — siffran i rälsen och ett steg på Översikt.
    function decisionsOwed() {
        if (!DEC) return 0;
        return DEC.decisions.filter(d => d.status === 'Open' && d.canRespond && !d.mine).length;
    }

    async function loadAll() {
        const [s, m, tk, dc, de] = await Promise.all([get('Get'), get('Meetings'), get('Tasks'), get('Documents'), get('Decisions')]);
        if (!s.success) { renderMissing(s.message); return false; }
        S = s; MEET = m.success ? m : { meetings: [] };
        TASKS = tk.success ? tk : { tasks: [], statuses: [], canCreate: false, canDecide: false };
        DOCS = dc.success ? dc : null;
        DEC = de.success ? de : { decisions: [], boardTargets: [], canPropose: false, canDecide: false };
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
        const allowed = VIEWS.filter(x => !x.need || x.need(S)).map(x => x.key).concat(['mote']);
        view = allowed.includes(v) ? v : 'oversikt';
        if (view === 'mote' && !MEET.meetings.some(m => m.id === parseInt(hashParams().get('id'), 10))) view = 'moten';
        rowFns = [];
        renderRail();
        ({ oversikt: viewOverview, meddelanden: viewMessages, moten: viewMeetings, mote: () => viewMeeting(parseInt(hashParams().get('id'), 10)),
           uppgifter: viewTasks, beslut: viewDecisions, delar: viewParts, dokument: viewDocuments, deltagare: viewPeople, notiser: viewNotify, installningar: viewSettings })[view]();
    }

    function subLine() {
        const p = S.planning;
        const parts = [];
        if (p.targetText) parts.push(esc(p.targetText));
        else if (p.targetStart) parts.push(esc(p.targetStart) + (p.targetEnd && p.targetEnd !== p.targetStart ? '–' + esc(p.targetEnd) : ''));
        if (p.status === 'Archived') parts.push('<span class="pl-state off">Arkiverad</span>');
        if (p.competition) parts.push(`Tävlingen <a href="${esc(p.competition.url)}">${esc(p.competition.name)}</a>`
            + (TOKEN || !p.competition.manageUrl ? '' : ` · <a href="${esc(p.competition.manageUrl)}">Tävlingshanteringen</a>`));
        return parts.join(' · ');
    }

    // ================================================================ Översikt
    function viewOverview() {
        setHead(S.planning.name, subLine());
        setActions(S.me.isParticipant ? [
            S.me.canInvite && { label: 'Bjud in…', icon: 'bi-person-plus', fn: openInvite },
            S.me.canUseMeetings && { label: 'Föreslå ett möte…', icon: 'bi-calendar-plus', fn: openNewMeeting },
            S.me.canWrite && { label: 'Skriv ett meddelande', icon: 'bi-chat-left-text', fn: () => go('meddelanden') },
            // Hela planen på papper: bemanning, förberedelser och (när planeringen är kopplad) dagsprogrammet.
            S.me.canEdit && !TOKEN && { label: 'Skriv ut hela planen', icon: 'bi-printer',
                fn: () => window.open('/tavlingsplanering/blad?p=' + S.planning.id + '&vy=allt', '_blank', 'noopener') },
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
        (DEC ? DEC.decisions : []).filter(d => d.status === 'Open' && d.canRespond && !d.mine)
            .forEach(d => steps.push(`<div class="pl-next warn"><div class="t"><b>Svara på förslaget:</b> ${esc(d.title)}</div>
                <a class="btn btn-primary" href="#vy=beslut" data-pl-go="beslut">Svara</a></div>`));
        const myTasks = myOpenTasks();
        if (myTasks) steps.push(`<div class="pl-next"><div class="t"><b>${myTasks === 1 ? 'Du har en uppgift' : `Du har ${myTasks} uppgifter`}</b> — berätta hur det går.</div>
            <a class="btn btn-primary" href="#vy=uppgifter" data-pl-go="uppgifter">Dina uppgifter</a></div>`);
        const late = TASKS ? TASKS.tasks.filter(t => t.overdue).length : 0;
        if (late && p.me.canEdit) steps.push(`<div class="pl-next warn"><div class="t"><b>${late === 1 ? 'En uppgift är försenad' : `${late} uppgifter är försenade`}</b></div>
            <a class="btn btn-outline-primary" href="#vy=uppgifter" data-pl-go="uppgifter" data-pl-filter="overdue">Visa</a></div>`);
        if (TASKS && !TASKS.tasks.length && p.planning.competition && p.me.canEdit)
            steps.push(`<div class="pl-next"><div class="t"><b>Lägg in förberedelserna</b> — börja med en mall för tävlingens storlek.</div>
                <a class="btn btn-primary" href="#vy=uppgifter" data-pl-go="uppgifter">Uppgifter</a></div>`);
        const upcoming = (MEET ? MEET.meetings : []).filter(m => m.status === 'Scheduled' && parseDT(m.endsAt || m.startsAt) >= new Date())
            .sort((a, b) => parseDT(a.startsAt) - parseDT(b.startsAt));
        if (upcoming.length) {
            const m = upcoming[0];
            steps.push(`<div class="pl-next"><div class="t"><b>Nästa möte:</b> ${esc(m.title)} — ${esc(span(m.startsAt, m.endsAt))}</div>
                ${m.onlineUrl ? `<a class="btn btn-outline-primary" href="${esc(m.onlineUrl)}" target="_blank" rel="noopener">Gå med</a>` : ''}
                <a class="btn btn-outline-primary" href="#vy=mote&id=${m.id}" data-pl-open-meeting="${m.id}">Visa</a></div>`);
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
        // Milstolparna: de viktiga datumen, först de som är kvar.
        const ms = ((TASKS && TASKS.tasks) || []).filter(t => t.isMilestone)
            .sort((a, b) => ((a.status === 'Done') - (b.status === 'Done')) || String(a.due || '9999').localeCompare(String(b.due || '9999')));
        if (ms.length) html.push(`<div class="pl-card"><div class="pl-cardh"><h2>Milstolpar</h2>
                <a href="#vy=uppgifter" data-pl-go="uppgifter" data-pl-filter="milestones">Alla milstolpar</a></div>
            ${ms.slice(0, 6).map(t => `<div class="d-flex gap-2 py-1 border-top align-items-baseline">
                <i class="bi ${t.status === 'Done' ? 'bi-check-circle-fill text-success' : 'bi-flag-fill text-primary'}" aria-hidden="true"></i>
                <div class="flex-grow-1">${esc(t.title)}${t.sentToForbundetAt ? ' <span class="pl-help small">· skickad till förbundet</span>' : ''}</div>
                <div class="text-nowrap small${t.overdue ? ' text-danger' : ' pl-help'}">${t.due ? esc(fmtDay(t.due)) : 'inget datum'}${t.overdue ? ' · försenad' : ''}</div></div>`).join('')}</div>`);
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
        e.preventDefault(); goMeeting(parseInt(b.dataset.plMeet, 10));
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
        const canPart = !isReply && !m.deleted && parts().length > 0 && (m.mine || S.me.canEdit);
        const menu = (m.mine && !m.deleted) || canPart ? rowMenu([
            m.mine && !m.deleted && { label: 'Ändra', fn: () => editMessage(m) },
            m.mine && !m.deleted && canAddDocs() && { label: 'Bifoga dokument…', fn: () => pickDocuments({ attach: { kind: 'Message', id: m.id } }) },
            canPart && { label: 'Gäller delen…', fn: () => openMessagePart(m) },
            m.mine && !m.deleted && { divider: true },
            m.mine && !m.deleted && { label: 'Ta bort', danger: true, fn: () => deleteMessage(m) },
        ]) : '';
        return `<div class="pl-msg${unread ? ' unread' : ''}" data-msg-id="${m.id}">
            <div class="d-flex justify-content-between gap-2"><div><span class="who">${esc(m.author)}</span>
            <span class="when"> · ${esc(fmtShort(m.createdAt))}${m.edited ? ' · ändrat' : ''}${m.viaEmail ? ' · via mejl' : ''}</span>${isReply ? '' : partChip(m.partId)}</div>${menu}</div>
            <div class="body">${m.deleted ? '<i class="text-muted">Meddelandet togs bort.</i>' : renderBody(m)}</div>
            ${m.deleted ? '' : docChips('Message', m.id)}
            <div class="meta">${!isReply && !m.deleted && S.me.canWrite ? `<button class="btn btn-link btn-sm p-0" data-pl-reply="${m.id}">Svara</button>` : ''}
            ${m.mine ? `<span>${esc(seenText(m))}</span>` : ''}</div></div>`;
    }
    function threadHtml() {
        const all = MSG.messages;
        const replies = new Map();
        all.filter(m => m.replyTo).forEach(m => { if (!replies.has(m.replyTo)) replies.set(m.replyTo, []); replies.get(m.replyTo).push(m); });
        // Trådarna i ordning efter senaste händelsen (ett nytt svar lyfter tråden), svaren i tidsordning.
        const last = m => Math.max(m.id, ...(replies.get(m.id) || []).map(r => r.id));
        const top = all.filter(m => !m.replyTo && (m.kind !== 'Message' ? msgPart === '' : partMatches(m.partId, msgPart))).sort((a, b) => last(b) - last(a));
        if (!top.length) return msgPart !== '' ? '<p class="pl-help mt-3">Inga meddelanden i den delen.</p>'
            : '<p class="pl-help mt-3">Inga meddelanden ännu. Skriv det första ovan — alla som är med får det i kvällens mejl.</p>';
        // Den man svarar på visas alltid, även om den ligger bland de äldre.
        const shown = top.filter((m, i) => i < msgShown || m.id === replyTo);
        const older = top.length - shown.length;
        return shown.map(m => msgHtml(m, false) + ((replies.get(m.id) || []).length
            ? `<div class="pl-reply">${replies.get(m.id).map(r => msgHtml(r, true)).join('')}</div>` : '')
            + (replyTo === m.id ? `<div class="pl-reply">${composerHtml()}</div>` : '')).join('')
            + (older > 0 ? `<div class="text-center mt-2"><button type="button" class="btn btn-outline-secondary" data-pl-older>Visa äldre meddelanden (${older})</button></div>` : '');
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
                ${canAddDocs() ? '<button class="btn btn-outline-secondary" type="button" id="plAttach"><i class="bi bi-paperclip me-1"></i>Bifoga dokument</button>' : ''}
                ${!target && parts().length ? `<select id="plPart" class="form-select" style="max-width:15rem" aria-label="Meddelandet gäller">
                    <option value="">Gäller hela planeringen</option>${parts().map(p => `<option value="${p.id}"${String(p.id) === msgPart ? ' selected' : ''}>Gäller ${esc(p.name)}</option>`).join('')}</select>` : ''}
                <span class="pl-help small" id="plMentionNote">${pendingMentions.size ? 'Nämner: ' + esc([...pendingMentions.values()].join(', ')) : 'Den du nämner får en notis.'}</span>
            </div><div id="plPendingDocs">${pendingDocsHtml()}</div></div>`;
    }
    function pendingDocsHtml() {
        if (!pendingDocs.size) return '';
        return `<div class="pl-docs mt-2" aria-label="Bifogas i meddelandet">${[...pendingDocs.entries()].map(([id, title]) => `<span class="pl-doc">
            <i class="bi bi-paperclip" aria-hidden="true"></i>${esc(title)}
            <button type="button" data-pending-doc="${id}" title="Ta bort ur meddelandet" aria-label="Bifoga inte ${esc(title)}">✕</button></span>`).join('')}</div>`;
    }
    function viewMessages() {
        setHead('Meddelanden', esc(S.planning.name));
        setActions([]);
        if (!MSG) { $('plBody').innerHTML = spinner(); return; }
        // Skrivrutan först och det senaste överst (Stefan 2026-10-23): med många meddelanden skulle man
        // annars behöva rulla förbi alla för att skriva. Svarar man skrivs svaret under tråden.
        const filter = parts().length ? `<div class="d-flex align-items-center gap-2 mb-2 pl-noprint"><label class="pl-help small" for="plMsgPart">Visa</label>
            <select id="plMsgPart" class="form-select form-select-sm" style="max-width:16rem">${partFilterOptions(msgPart)}</select></div>` : '';
        $('plBody').innerHTML = `<div class="pl-card">${replyTo ? '' : composerHtml()}${filter}<div class="pl-msgs" id="plThread">${threadHtml()}</div></div>`;
        $('plMsgPart')?.addEventListener('change', e => { msgPart = e.target.value; refreshThreadOnly(); });
        wireComposer();
        if (replyTo) setTimeout(() => { $('plCompose')?.scrollIntoView({ block: 'center' }); $('plText')?.focus(); }, 50);
        markRead();
    }
    const MSG_PAGE = 15;
    let msgShown = MSG_PAGE;
    document.addEventListener('click', e => {
        if (!e.target.closest('[data-pl-older]')) return;
        msgShown += MSG_PAGE * 2;
        const t = $('plThread'); if (t) { rowFns = []; t.innerHTML = threadHtml(); }
    });
    function refreshThreadOnly() {
        const t = $('plThread');
        if (t) { rowFns = []; t.innerHTML = threadHtml(); }
        renderRail();
        markRead();
    }
    function wireComposer() {
        if (!$('plSend')) return;
        $('plSend').onclick = send;
        const at = $('plAttach');
        if (at) at.onclick = () => pickDocuments({
            preselected: [...pendingDocs.keys()],
            onPick: ids => { ids.forEach(id => { const d = docById(id); if (d) pendingDocs.set(id, d.title); }); $('plPendingDocs').innerHTML = pendingDocsHtml(); },
        });
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
            const r = await post('Post', { body, replyTo: replyTo || '', mentions: mentions.join(','), documents: [...pendingDocs.keys()].join(','),
                partId: !replyTo && $('plPart') ? $('plPart').value : '' });
            if (!r.success) { failMsg(r); return; }
            replyTo = 0; pendingMentions = new Map(); pendingDocs = new Map();
            showMsg(r.docWarning ? esc(r.docWarning) : '', 'warning');
            await reloadDocs();
            await reloadMessages();
            viewMessages();
            $('plThread')?.firstElementChild?.scrollIntoView({ block: 'nearest' });
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
    // Möten: en lista i styrelsens form, och ett möte = en AVSTÄMNING på en egen sida (Stefan 2026-10-23,
    // skissen https://claude.ai/artifact/ThReUw38FM6kAi7R7JnU1T). ⚠️ Inga faser, ingen kallelse, ingen
    // närvaro, inget protokoll att justera — ett planeringsmöte är informellt.
    function rerender() { render(view); }
    function isPastMeeting(m) { return m.status === 'Cancelled' || (m.status === 'Scheduled' && parseDT(m.endsAt || m.startsAt) < new Date()); }
    function fmtDay(s) {
        const p = /^(\d{4})-(\d{2})-(\d{2})/.exec(s || ''); if (!p) return s || '';
        // Året skrivs ut när det inte är i år — en milstolpe 1 oktober nästa år får inte läsas som i höst.
        const d = new Date(+p[1], +p[2] - 1, +p[3]);
        return d.toLocaleDateString('sv-SE', d.getFullYear() === new Date().getFullYear()
            ? { day: 'numeric', month: 'short' } : { day: 'numeric', month: 'short', year: 'numeric' });
    }
    function firstName(n) { return (n || '').split(' ')[0]; }
    function meetingMenuItems(m, onPage) {
        if (m.status === 'Cancelled') return [!onPage && { label: 'Öppna mötet', fn: () => goMeeting(m.id) }];
        return [
            !onPage && { label: 'Öppna mötet', fn: () => goMeeting(m.id) },
            m.status === 'Scheduled' && S.me.icsUrl && { label: 'Lägg i min kalender', icon: 'bi-calendar-plus', fn: () => { location.href = S.me.icsUrl; } },
            m.canEdit && { label: 'Ändra mötet…', icon: 'bi-pencil', fn: () => openEditMeeting(m) },
            m.canEdit && m.status === 'Proposed' && { label: 'Lägg till en tid…', icon: 'bi-plus-lg', fn: () => openAddOption(m) },
            m.canEdit && m.status === 'Scheduled' && { label: 'Flytta till en annan tid…', icon: 'bi-arrow-left-right', fn: () => openBookOwn(m) },
            m.canEdit && { divider: true },
            m.canEdit && (m.status === 'Scheduled'
                ? { label: 'Ställ in mötet…', icon: 'bi-x-circle', danger: true, fn: () => cancelMeeting(m) }
                : { label: 'Ta bort mötet…', icon: 'bi-trash', danger: true, fn: () => deleteMeeting(m) }),
        ];
    }
    function openTaskCounts() {
        const open = TASKS.tasks.filter(t => t.status !== 'Done');
        return { open: open.length, ready: open.filter(t => t.status === 'ReadyForReview').length };
    }
    function viewMeetings() {
        if (CFG.startMeeting) { // länken ur ett mejl pekar på ett möte
            const id = CFG.startMeeting; CFG.startMeeting = 0;
            if (MEET.meetings.some(m => m.id === id)) { goMeeting(id); return; }
        }
        setHead('Möten', esc(S.planning.name));
        setActions(MEET.canUse ? [{ label: 'Föreslå ett möte…', icon: 'bi-calendar-plus', fn: openNewMeeting }] : []);
        const all = MEET.meetings.slice();
        const coming = all.filter(m => !isPastMeeting(m))
            .sort((a, b) => (a.status === 'Proposed' ? 0 : 1) - (b.status === 'Proposed' ? 0 : 1) || (parseDT(a.startsAt) || 0) - (parseDT(b.startsAt) || 0));
        const past = all.filter(isPastMeeting).sort((a, b) => (parseDT(b.startsAt) || 0) - (parseDT(a.startsAt) || 0));
        const shown = showPastMeetings ? coming.concat(past) : coming;
        const tc = openTaskCounts();
        const nextBooked = coming.find(m => m.status === 'Scheduled');
        const html = [`<p class="pl-help">Planeringens möten är avstämningar: vad ska vi prata om, hur går det med uppgifterna, vem gör vad nu. Ingen kallelse och inget protokoll att justera.</p>`];
        if (!all.length) {
            html.push(`<div class="pl-card"><p class="mb-2">Inga möten ännu.</p>
                ${MEET.canUse ? '<button class="btn btn-primary" data-pl-newmeet="1"><i class="bi bi-calendar-plus me-1"></i>Föreslå ett möte</button>' : ''}
                <p class="pl-help mt-2 mb-0">Föreslå några tider — alla svarar vilka som passar, direkt ur mejlet, och sedan bokar ni den tid som passar flest.</p></div>`);
        } else {
            html.push(`<div class="d-flex flex-wrap gap-3 align-items-center mb-2 pl-help">
                ${past.length ? `<label class="d-flex align-items-center gap-2 mb-0"><input type="checkbox" class="form-check-input" id="plPastToggle"${showPastMeetings ? ' checked' : ''}> Visa tidigare möten (${past.length})</label>` : ''}
                <span class="ms-auto">${shown.length === all.length ? `${all.length} ${all.length === 1 ? 'möte' : 'möten'}` : `${shown.length} av ${all.length} möten`}</span></div>`);
            if (!shown.length) html.push(`<div class="pl-card"><p class="pl-help mb-0">Inga kommande möten. Kryssa i <i>Visa tidigare möten</i> för att se dem${MEET.canUse ? ', eller föreslå ett nytt under Åtgärder' : ''}.</p></div>`);
            else {
                const rows = shown.map(m => {
                    const answered = m.answeredCount || 0, waiting = (m.notAnswered || []).length;
                    const state = meetingState(m) + (m.status === 'Proposed' ? `<div class="pl-help small mt-1">${answered} av ${answered + waiting} har svarat</div>` : '');
                    const topics = (m.topics || []).length;
                    const sub = [m.onlineUrl ? 'Online' : '', m.place || '', topics ? `${topics} ${topics === 1 ? 'sak' : 'saker'} att prata om` : '']
                        .filter(Boolean).map(esc).join(' · ');
                    const when = m.status === 'Proposed' ? '<span class="pl-help">Tiden inte bestämd</span>' : esc(fmtShort(m.startsAt));
                    const tasksCell = (m === nextBooked || m.isCurrent) && tc.open
                        ? `<b>${tc.open} öppna</b>${tc.ready ? `<div class="pl-help small">${tc.ready} klar att pröva</div>` : ''}` : '<span class="pl-help">—</span>';
                    return `<tr class="pl-rowlink${isPastMeeting(m) ? ' pl-past' : ''}" data-pl-open-meeting="${m.id}">
                        <td class="d-none d-sm-table-cell text-nowrap">${when}</td>
                        <td><a href="#vy=mote&id=${m.id}" class="fw-semibold text-decoration-none" data-pl-open-meeting="${m.id}">${esc(m.title)}</a>
                            <div class="d-sm-none small">${when}</div>${sub ? `<div class="pl-help small">${sub}</div>` : ''}</td>
                        <td>${state}</td>
                        <td class="d-none d-md-table-cell">${tasksCell}</td>
                        <td class="text-end">${rowMenu(meetingMenuItems(m, false))}</td></tr>`;
                }).join('');
                html.push(`<div class="pl-card p-0"><div class="table-responsive"><table class="table align-middle mb-0 pl-mtg">
                    <thead><tr><th class="d-none d-sm-table-cell ps-3">När</th><th>Möte</th><th>Läge</th><th class="d-none d-md-table-cell">Uppgifter</th><th></th></tr></thead>
                    <tbody>${rows}</tbody></table></div></div>`);
            }
        }
        if (S.me.icsUrl) html.push(`<p class="pl-help pl-noprint mt-3">Planeringens bokade möten kan ligga i din egen kalender och följa med om de flyttas:
            <a href="${esc(S.me.icsUrl)}">lägg till kalendern</a>.</p>`);
        $('plBody').innerHTML = html.join('');
        $('plPastToggle')?.addEventListener('change', e => { showPastMeetings = e.target.checked; viewMeetings(); });
    }

    function proposalHtml(m) {
        const canUse = MEET.canUse;
        const h = [`<div class="pl-card"><h2>Vilka tider passar?</h2>
            <p class="pl-help">${esc(m.createdBy)} föreslår de här tiderna. ${canUse ? 'Tryck på det som gäller för dig för varje tid.' : ''}
            ${m.voteDeadline ? 'Svara senast ' + esc(m.voteDeadline) + '.' : ''}</p>`];
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
        h.push('</div>');
        return h.join('');
    }

    function tasksForMeeting(m) {
        const order = { ReadyForReview: 0, NeedsHelp: 1, InProgress: 2, NotStarted: 3 };
        return TASKS.tasks.filter(t => t.status !== 'Done' || t.doneAtMeetingId === m.id)
            .sort((a, b) => (a.status === 'Done') - (b.status === 'Done') || (order[a.status] ?? 9) - (order[b.status] ?? 9)
                || (a.due || '9999').localeCompare(b.due || '9999') || a.id - b.id);
    }
    function meetingTaskRow(m, t) {
        if (t.status === 'Done') {
            return `<div class="pl-tkdone"><span><b>${esc(t.title)}</b>${t.assignee ? ' · ' + esc(t.assignee) : ''}</span>
                <span class="small">Utförd — bestämt på mötet${m.canDecideTasks ? ` · <button type="button" class="btn btn-link btn-sm p-0 align-baseline pl-tkundo" data-tk-undo="${t.id}" data-tk-meeting="${m.id}">Ångra</button>` : ''}</span></div>`;
        }
        const ready = t.status === 'ReadyForReview';
        let side = '';
        if (m.canDecideTasks && m.isCurrent) {
            side = `<div class="d-flex gap-2 flex-shrink-0">
                <button type="button" class="btn ${ready ? 'btn-success' : 'btn-outline-success'}" data-tk-done="${t.id}" data-tk-meeting="${m.id}">Klar ✓</button>
                <button type="button" class="btn btn-outline-secondary" data-tk-continue="${t.id}" data-tk-meeting="${m.id}">Fortsätter</button></div>`;
        } else if (t.mine && t.canReport) {
            side = `<div class="flex-shrink-0"><button type="button" class="btn btn-outline-primary btn-sm" data-tk-report="${t.id}">Berätta hur det går…</button></div>`;
        }
        return `<div class="pl-tkrow"><div class="flex-grow-1" style="min-width:0">
                <div class="d-flex gap-2 align-items-center flex-wrap"><b>${esc(t.title)}</b>${tkChip(t)}</div>
                <div class="pl-help small">${esc(t.assignee || 'Ingen ansvarig än')}${t.due ? ' · senast ' + esc(fmtDay(t.due)) : ''}${t.overdue ? ' · <span class="text-danger">försenad</span>' : ''}</div>
                ${t.report ? `<div class="mt-1">”${esc(t.report)}” <span class="pl-help small">— ${esc(firstName(t.reportBy))}, ${esc(fmtShort(t.reportAt))}</span></div>`
                    : '<div class="mt-1 pl-help small">Inget skrivet än.</div>'}
            </div>${side}</div>`;
    }

    function viewMeeting(id) {
        const m = MEET.meetings.find(x => x.id === id);
        const meta = [];
        if (m.status === 'Proposed') meta.push('Tiden inte bestämd');
        else if (m.startsAt) meta.push(esc(span(m.startsAt, m.endsAt)));
        if (m.place) meta.push(esc(m.place)); else if (m.onlineUrl) meta.push('Online');
        setHead(m.title, meta.join(' · ') + ' ' + meetingState(m));
        setActions(meetingMenuItems(m, true));
        const h = [`<a href="#vy=moten" data-pl-go="moten" class="d-inline-block mb-2 small">← Alla möten</a>`];
        if (m.status === 'Cancelled') h.push('<div class="alert alert-secondary">Mötet är inställt.</div>');
        if (m.status === 'Scheduled') {
            const acts = [];
            if (m.onlineUrl) acts.push(`<a class="btn btn-primary" href="${esc(m.onlineUrl)}" target="_blank" rel="noopener"><i class="bi bi-camera-video me-1"></i>Anslut till mötet</a>`);
            if (S.me.icsUrl) acts.push(`<a class="btn btn-outline-secondary" href="${esc(S.me.icsUrl)}"><i class="bi bi-calendar-plus me-1"></i>Lägg i kalendern</a>`);
            if (acts.length || m.bookedBy) h.push(`<div class="d-flex flex-wrap gap-2 align-items-center mb-3">${acts.join('')}${m.bookedBy ? `<span class="pl-help small">Bokat av ${esc(m.bookedBy)}.</span>` : ''}</div>`);
        }
        if (m.status === 'Proposed') h.push(proposalHtml(m));

        // Att prata om
        const canTalk = MEET.canUse && m.status !== 'Cancelled';
        const topics = m.topics || [];
        h.push(`<div class="pl-card"><h2>Att prata om</h2>
            <p class="pl-help">Alla i planeringen kan lägga till något före mötet. Ingen dagordning att godkänna.</p>
            ${topics.length ? `<div class="pl-topics">${topics.map(tp => `<div class="pl-topic"><span>${esc(tp.text)}${docChips('Topic', tp.id)}</span>
                <span class="d-flex align-items-center gap-2 flex-shrink-0"><span class="pl-help small">${esc(tp.by)}</span>
                ${canTalk && canAddDocs() ? `<button type="button" class="btn btn-sm btn-link text-secondary p-0 pl-noprint" data-doc-attach="Topic:${tp.id}" title="Bifoga dokument till punkten" aria-label="Bifoga dokument till punkten"><i class="bi bi-paperclip"></i></button>` : ''}
                ${tp.canRemove ? `<button type="button" class="btn btn-sm btn-link text-secondary p-0" data-topic-del="${tp.id}" title="Ta bort punkten" aria-label="Ta bort punkten">✕</button>` : ''}</span></div>`).join('')}</div>`
                : '<p class="pl-help mb-2">Inget ännu.</p>'}
            ${canTalk ? `<div class="d-flex gap-2 mt-2 flex-wrap"><input type="text" class="form-control" style="flex:1 1 260px" id="plTopic${m.id}" maxlength="500"
                placeholder="Något du vill ta upp…" aria-label="Något att prata om" data-topic-input="${m.id}">
                <button type="button" class="btn btn-outline-primary" data-topic-add="${m.id}">Lägg till</button></div>` : ''}
            ${m.agenda ? `<div class="mt-3"><div class="fw-semibold">Dagordning</div><div style="white-space:pre-wrap">${esc(m.agenda)}</div></div>` : ''}
            </div>`);

        // Underlaget: dokument till mötet
        const meetDocs = docChips('Meeting', m.id);
        h.push(`<div class="pl-card"><div class="pl-cardh"><h2>Underlag till mötet</h2>${m.status !== 'Cancelled' ? attachBtn('Meeting', m.id) : ''}</div>
            ${meetDocs || '<p class="pl-help mb-0">Inga dokument bifogade. Bifoga det ni ska titta på — en karta, en offert, ett förslag till inbjudan.</p>'}</div>`);

        // Hur går det med uppgifterna?
        const list = tasksForMeeting(m);
        let lead;
        if (m.status === 'Cancelled') lead = 'Mötet är inställt — uppgifterna tas upp på nästa möte.';
        else if (m.isCurrent && m.canDecideTasks) lead = 'Kommer med på varje möte av sig själv. Var och en har skrivit hur det går — här bestämmer ni om en uppgift är klar eller fortsätter.';
        else if (isPastMeeting(m)) lead = 'Mötet är avslutat. Uppgifterna som fortfarande är öppna kommer upp på nästa möte.';
        else lead = 'Kommer med på varje möte av sig själv. Under mötet trycker ni <i>Klar ✓</i> eller <i>Fortsätter</i> här. Fram till dess säger var och en själv hur det går under Uppgifter.';
        h.push(`<div class="pl-card"><h2>Hur går det med uppgifterna?</h2><p class="pl-help">${lead}</p>
            ${list.length ? list.map(t => meetingTaskRow(m, t)).join('') : '<p class="pl-help mb-0">Inga öppna uppgifter.</p>'}</div>`);

        // Anteckningar och vem gör vad
        const fromHere = TASKS.tasks.filter(t => t.fromMeetingId === m.id);
        const notesHtml = m.status === 'Scheduled' && MEET.canUse
            ? `<label class="visually-hidden" for="plNotes${m.id}">Anteckningar</label>
               <textarea class="form-control" id="plNotes${m.id}" rows="5" data-pl-notes="${m.id}" placeholder="Vad pratade ni om? Vad bestämdes?">${esc(m.notes || '')}</textarea>
               <div class="pl-help small mt-1" id="plNotesSaved${m.id}">Sparas när du lämnar rutan.</div>`
            : (m.notes ? `<div style="white-space:pre-wrap">${esc(m.notes)}</div>` : '<p class="pl-help mb-0">Inga anteckningar.</p>');
        const people = S.participants.slice().sort((a, b) => a.name.localeCompare(b.name, 'sv'));
        const addForm = TASKS.canCreate && m.status !== 'Cancelled' ? `<div class="row g-2 mt-1 align-items-end">
                <div class="col-md-5"><label class="form-label small mb-0" for="ntTitle${m.id}">Vad ska göras?</label>
                    <input type="text" class="form-control" id="ntTitle${m.id}" maxlength="300" data-nt-title="${m.id}"></div>
                <div class="col-md-3 col-7"><label class="form-label small mb-0" for="ntWho${m.id}">Vem?</label>
                    <select class="form-select" id="ntWho${m.id}"><option value="">Ingen ännu</option>${people.map(p => `<option value="${p.id}">${esc(p.name)}</option>`).join('')}</select></div>
                <div class="col-md-2 col-5"><label class="form-label small mb-0" for="ntDue${m.id}">Senast</label>
                    <input type="text" class="form-control" id="ntDue${m.id}" placeholder="valfritt" data-nt-due="${m.id}"></div>
                <div class="col-md-2"><button type="button" class="btn btn-primary w-100" data-tk-add="${m.id}">+ Lägg till</button></div></div>
                <p class="pl-help small mt-2 mb-0">Den som får en uppgift får ett mejl. Uppgiften hamnar under Uppgifter och kommer upp på nästa möte.</p>` : '';
        h.push(`<div class="pl-card"><h2>Anteckningar och vem gör vad</h2>
            ${m.status === 'Scheduled' && MEET.canUse ? '<p class="pl-help">Skriv fritt. Anteckningarna sparas av sig själva och syns för alla i planeringen.</p>' : ''}
            ${notesHtml}
            ${fromHere.length || addForm ? '<div class="fw-semibold mt-3">Nya uppgifter från mötet</div>' : ''}
            ${fromHere.map(t => `<div class="pl-tknew"><span><b>${esc(t.title)}</b>${t.assignee ? ' · ' + esc(t.assignee) : ''}${t.due ? ' · senast ' + esc(fmtDay(t.due)) : ''}</span>${tkChip(t)}</div>`).join('')}
            ${addForm}</div>`);
        $('plBody').innerHTML = `<div id="plMeet${m.id}">${h.join('')}</div>`;
        const due = $('ntDue' + m.id); if (due) datePicker(due);
    }
    document.addEventListener('click', async e => {
        const b = e.target.closest('[data-pl-vote]'); if (!b) return;
        const [mid, oid, ans] = b.dataset.plVote.split(':');
        b.disabled = true;
        try {
            const r = await post('Vote', { meetingId: mid, answers: oid + ':' + ans });
            if (!r.success) { failMsg(r); return; }
            await reloadMeetings(); rerender();
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
        await reloadMeetings(); rerender(); showMsg('Mötet är bokat och alla har fått ett mejl.', 'success');
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
    // Anteckningarna sparas när rutan lämnas — och innan en knapp på mötessidan ritar om den.
    // En pågående sparning delas, så att rutan aldrig sparas två gånger samtidigt.
    const notesSaving = new Map();
    function saveNotes(ta) {
        const m = MEET.meetings.find(x => x.id === parseInt(ta.dataset.plNotes, 10)); if (!m) return Promise.resolve();
        const value = ta.value;
        if ((m.notes || '') === value) return notesSaving.get(m.id) || Promise.resolve();
        const prev = notesSaving.get(m.id);
        if (prev && prev.value === value) return prev;
        const p = (async () => {
            const r = await post('UpdateMeeting', { meetingId: m.id, title: m.title, place: m.place || '', onlineUrl: m.onlineUrl || '',
                agenda: m.agenda || '', notes: value, voteDeadline: m.voteDeadline || '' });
            const note = $('plNotesSaved' + m.id);
            if (r.success) { m.notes = value; if (note) note.textContent = 'Sparat.'; }
            else if (note) note.textContent = r.message || 'Det gick inte att spara.';
        })().finally(() => { if (notesSaving.get(m.id) === p) notesSaving.delete(m.id); });
        p.value = value;
        notesSaving.set(m.id, p);
        return p;
    }
    async function flushNotes() {
        const ta = $('plBody').querySelector('[data-pl-notes]');
        if (ta) { try { await saveNotes(ta); } catch (e) { /* visas under rutan */ } }
    }
    document.addEventListener('focusout', e => {
        const ta = e.target.closest('[data-pl-notes]'); if (ta) saveNotes(ta);
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
                await reloadMeetings(); goMeeting(r.meetingId);
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
                await reloadMeetings(); rerender(); return true;
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
                await reloadMeetings(); rerender(); showMsg('Mötet är flyttat och alla har fått ett mejl.', 'success'); return true;
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
                await reloadMeetings(); rerender(); return true;
            }
        });
    }
    async function cancelMeeting(m) {
        if (!await plConfirm('Ställ in mötet?', `${m.title}\n${span(m.startsAt, m.endsAt)}\n\nAlla får ett mejl om att mötet är inställt, och det stryks i kalendrarna.`, 'Ställ in', true)) return;
        const r = await post('CancelMeeting', { meetingId: m.id });
        if (!r.success) { failMsg(r); return; }
        await reloadMeetings(); rerender();
    }
    async function deleteMeeting(m) {
        if (!await plConfirm('Ta bort mötet?', `${m.title}\n\nTiderna och svaren tas bort.`, 'Ta bort', true)) return;
        const r = await post('DeleteMeeting', { meetingId: m.id });
        if (!r.success) { failMsg(r); return; }
        await reloadMeetings(); rerender();
    }

    // ================================================================ Uppgifter
    // Lägena: Ej påbörjad → Pågår → Behöver hjälp → Klar att pröva → Utförd. Den som har uppgiften sätter de
    // fyra första; Utförd bestäms på ett möte, eller av den sammankallande (servern prövar båda).
    // P2 (2026-10-23): tävlingens förberedelser bor här — områden, mallar, kretsens checklista, stationer,
    // dokument och kommentarer. I tävlingens egen planering ("Utförd beslutas på mötena" av) bockar den
    // ansvarige och Kan ändra av direkt.
    const TK_CLASS = { NotStarted: 'ns', InProgress: 'ip', NeedsHelp: 'nh', ReadyForReview: 'rr', Done: 'dn' };
    function tkChip(t) { return `<span class="pl-tk pl-tk-${TK_CLASS[t.status] || 'ns'}">${esc(t.statusLabel)}</span>`; }
    async function reloadTasks() { const r = await get('Tasks'); if (r.success) TASKS = r; }
    function findTask(id) { return TASKS.tasks.find(t => t.id === id); }
    function areaById(id) { return (TASKS.areas || []).find(a => a.id === id); }
    function competition() { return S && S.planning ? S.planning.competition : null; }
    function canDaysBefore() { return !!(competition() || S.planning.targetStart); }
    function daysWord() { return TASKS.anchorIsCompetition ? 'före tävlingen' : 'före planeringens första dag'; }
    function dueText(t) {
        if (t.dueDaysBefore != null) return (t.due ? fmtDay(t.due) + ' ' : '') + `<span class="pl-help small">(${t.dueDaysBefore} d ${esc(daysWord())})</span>`;
        return t.due ? esc(fmtDay(t.due)) : '';
    }
    function kr(v) { return v == null ? '' : Number(v).toLocaleString('sv-SE') + ' kr'; }
    function postForm(action, fd) {
        fd.append('id', ID);
        if (TOKEN) fd.append('t', TOKEN);
        return req('/umbraco/surface/Planning/' + action, { method: 'POST', headers: { 'RequestVerificationToken': token() }, body: fd });
    }

    function viewTasks() {
        setHead('Uppgifter', esc(S.planning.name));
        const c = competition(), all = TASKS.tasks;
        const overdue = all.filter(t => t.overdue).length;
        const fill = TASKS.canManageAreas || (TASKS.canCreate && !TOKEN);
        setActions([
            TASKS.canCreate && { label: 'Ny uppgift…', icon: 'bi-plus-lg', fn: () => openTaskEdit(null) },
            TASKS.canManageAreas && { label: 'Nytt område…', icon: 'bi-folder-plus', fn: () => openArea(null) },
            fill && { header: 'Fyll i' },
            TASKS.canManageAreas && { label: 'Fyll i från en mall…', icon: 'bi-magic', fn: openTemplate },
            TASKS.canManageAreas && c && { label: 'Kretsens checklista…', icon: 'bi-list-check', fn: openKretsChecklist },
            TASKS.canManageAreas && c && c.isFalt && c.stationCount > 0 && { label: `En uppgift per station (${c.stationCount})`, icon: 'bi-signpost-split', fn: seedStations },
            TASKS.canCreate && !TOKEN && { label: 'Klistra in från ett kalkylark…', icon: 'bi-clipboard', fn: () => openPaste(null) },
            { header: 'Dela och följ upp' },
            TASKS.canManageAreas && overdue > 0 && { label: `Påminn dem med försenade uppgifter (${overdue})`, icon: 'bi-bell', fn: remindOverdue },
            !TOKEN && c && { label: c.isFalt ? 'Materiellista för stationerna' : 'Materielberäkning…', icon: 'bi-box-seam',
                fn: () => c.isFalt ? window.open('/faltskytte/materiel/' + c.id, '_blank', 'noopener') : openMateriel(c) },
            !TOKEN && { label: 'Hämta som kalkylark (CSV)', icon: 'bi-filetype-csv', fn: () => { location.href = '/umbraco/surface/Planning/ExportTasks?id=' + ID; } },
            { label: 'Skriv ut', icon: 'bi-printer', fn: () => window.print() },
        ]);
        const n = {
            open: all.filter(t => t.status !== 'Done').length,
            mine: all.filter(t => t.mine && t.status !== 'Done').length,
            help: all.filter(t => t.status === 'NeedsHelp').length,
            ready: all.filter(t => t.status === 'ReadyForReview').length,
            overdue,
            done: all.filter(t => t.status === 'Done').length,
            milestones: all.filter(t => t.isMilestone).length,
        };
        if (!tkFilter) tkFilter = TOKEN && n.mine ? 'mine' : 'open';
        const html = [];
        if (c) html.push(`<div class="pl-card pl-comp d-flex flex-wrap gap-2 align-items-center">
            <i class="bi bi-trophy text-secondary"></i><div class="flex-grow-1">Tävlingen <a href="${esc(c.url)}">${esc(c.name)}</a>${c.date ? ' · ' + esc(fmtDay(c.date)) : ''}</div>
            ${TOKEN || !c.manageUrl ? '' : `<a class="btn btn-sm btn-outline-secondary" href="${esc(c.manageUrl)}">Tävlingshanteringen</a>`}</div>`);
        html.push(`<p class="pl-help">Vem gör vad, och till när. ${TASKS.doneAtMeetingsOnly
            ? 'Den som har uppgiften ändrar själv hur långt hen kommit; om den är utförd bestämmer ni på ett möte, eller den sammankallande.'
            : 'Den som har uppgiften ändrar själv hur långt hen kommit och bockar av den när den är klar.'}</p>`);
        if (!all.length) {
            html.push(`<div class="pl-card"><p class="mb-2"><b>Inga uppgifter ännu.</b></p>
                ${TASKS.canManageAreas ? `<p class="mb-3">Börja med en färdig lista för tävlingens storlek — den går att ändra efteråt.</p>
                    <button class="btn btn-primary" data-tk-template="1"><i class="bi bi-magic me-1"></i>Fyll i från en mall…</button>` : ''}
                <p class="pl-help mb-0 mt-3">Uppgifter fördelas också på mötena: under <i>Anteckningar och vem gör vad</i> skriver ni vad som ska göras och vem som gör det.</p></div>`);
        } else {
            const opt = (v, label) => `<option value="${v}"${v === tkFilter ? ' selected' : ''}>${esc(label)}</option>`;
            html.push(`<div class="d-flex flex-wrap gap-2 align-items-center mb-2 pl-noprint">
                <input type="search" class="form-control form-control-sm" id="tkQ" placeholder="Sök på uppgift eller namn" style="max-width:20rem" value="${esc(tkQuery)}">
                <select class="form-select form-select-sm" id="tkFilter" aria-label="Visa" style="max-width:16rem">
                    ${opt('open', `Öppna (${n.open})`)}${opt('mine', `Mina (${n.mine})`)}${opt('overdue', `Försenade (${n.overdue})`)}
                    ${opt('help', `Behöver hjälp (${n.help})`)}${opt('ready', `Klar att pröva (${n.ready})`)}${n.milestones ? opt('milestones', `Milstolpar (${n.milestones})`) : ''}${opt('done', `Utförda (${n.done})`)}${opt('all', `Alla (${all.length})`)}
                </select>${parts().length ? `<select class="form-select form-select-sm" id="tkPart" aria-label="Del" style="max-width:14rem">${partFilterOptions(tkPart)}</select>` : ''}<span class="ms-auto pl-help small" id="tkCount"></span></div>
                <div class="pl-card p-0"><div class="table-responsive"><table class="table align-middle mb-0 pl-tktable">
                <thead><tr><th class="ps-3">Uppgift</th><th class="d-none d-md-table-cell">Vem</th><th class="d-none d-md-table-cell">Senast</th>
                    <th class="d-none d-sm-table-cell">Läge</th><th class="d-none d-lg-table-cell">Senaste raden</th><th class="pl-noprint"></th></tr></thead>
                <tbody id="tkRows"></tbody></table></div></div>`);
        }
        html.push(`<div class="pl-card mt-3"><h2>Lägena</h2><div class="d-flex gap-2 flex-wrap align-items-center">
            ${['NotStarted', 'InProgress', 'NeedsHelp', 'ReadyForReview', 'Done'].map(s => tkChip({ status: s, statusLabel: (TASKS.statuses.find(x => x.value === s) || {}).label || s })).join(' → ')}</div>
            <p class="pl-help mb-0 mt-2">${TASKS.doneAtMeetingsOnly
                ? 'Den som har uppgiften sätter de fyra första. Utförd sätts på ett möte eller av den sammankallande.'
                : 'Den som har uppgiften sätter läget och bockar av den som utförd. Den sammankallande kan ändra det under Inställningar.'}</p></div>`);
        $('plBody').innerHTML = html.join('');
        if (!all.length) return;
        renderTaskRows();
        $('tkQ').addEventListener('input', e => { tkQuery = e.target.value; renderTaskRows(); });
        $('tkFilter').addEventListener('change', e => { tkFilter = e.target.value; renderTaskRows(); });
        $('tkPart')?.addEventListener('change', e => { tkPart = e.target.value; renderTaskRows(); });
    }

    function taskMatches(t, q) {
        const f = tkFilter;
        const okF = f === 'all' || (f === 'open' && t.status !== 'Done') || (f === 'mine' && t.mine && t.status !== 'Done')
            || (f === 'help' && t.status === 'NeedsHelp') || (f === 'ready' && t.status === 'ReadyForReview')
            || (f === 'overdue' && t.overdue) || (f === 'done' && t.status === 'Done') || (f === 'milestones' && t.isMilestone);
        const area = t.areaId ? (areaById(t.areaId) || {}).name : '';
        const part = t.partId ? (partById(t.partId) || {}).name : '';
        return okF && partMatches(t.partId, tkPart) && (!q || [t.title, t.assignee, t.report, area, part].some(v => (v || '').toLowerCase().includes(q)));
    }
    function taskRow(t) {
        const open = t.status !== 'Done';
        const decide = open && (t.canDecide || TASKS.canDecide);
        const menu = rowMenu([
            t.canReport && open && { label: 'Ändra läge och skriv hur det går…', fn: () => openReport(t) },
            t.canDecide && open && { label: 'Utförd ✓', fn: () => markDone(t) },
            t.canEdit && { label: 'Ändra uppgiften…', fn: () => openTaskEdit(t) },
            { label: `Kommentarer och dokument${t.comments ? ' (' + t.comments + ')' : ''}…`, fn: () => openThread(t) },
            !t.canDecide && TASKS.canDecide && open && { label: 'Markera som utförd…', fn: () => decideOutsideMeeting(t) },
            (t.canDecide || TASKS.canDecide) && !open && { label: 'Ångra utförd', fn: () => undoDone(t, 0) },
            t.isMilestone && t.sendToForbundet && TASKS.canSendToForbundet && { label: t.sentToForbundetAt ? 'Skicka till förbundet igen…' : 'Skicka till förbundet…', fn: () => openSendForbundet(t) },
            { label: 'Visa historiken', fn: () => openHistory(t) },
            t.canEdit && { divider: true },
            t.canEdit && { label: 'Ta bort…', danger: true, fn: () => removeTask(t) },
        ]);
        const origin = t.fromMeeting ? `Från mötet ${esc(t.fromMeeting)}` : t.source === 'Imported' ? 'Från tävlingens förberedelser' : t.source === 'Template' ? 'Från mallen' : t.source === 'KretsChecklist' ? 'Kretsens checklista' : `Tillagd av ${esc(t.createdBy)}`;
        const done = !open ? `<div class="small text-success">Utförd ${esc(fmtShort(t.doneAt))}${t.doneAtMeeting ? ' på mötet ' + esc(t.doneAtMeeting) : t.doneBy ? ' — ' + esc(t.doneBy) : ''}</div>` : '';
        const extra = [];
        if (t.scopeType) extra.push(t.stationUrl ? `<a href="${esc(t.stationUrl)}">${esc(t.scopeType)} ${esc(t.scopeKey || '')}</a>` : esc(t.scopeType + ' ' + (t.scopeKey || '')));
        if (t.estimatedCost != null) extra.push(esc(kr(t.estimatedCost)));
        const docs = refsOn('Task', t.id).length;
        if (t.comments || docs) extra.push(`<button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-tk-thread="${t.id}">`
            + [t.comments ? `${t.comments} kommentar${t.comments === 1 ? '' : 'er'}` : '', docs ? `${docs} dokument` : ''].filter(Boolean).join(', ') + '</button>');
        // Bara på EGNA uppgifter — på varje rad blev det brus. Övriga bockas av via radens Åtgärder.
        const quick = decide && t.canDecide && t.mine ?`<button type="button" class="btn btn-sm btn-outline-success ms-2 pl-noprint" data-tk-quickdone="${t.id}" title="Markera som utförd">Klar ✓</button>` : '';
        const flag = t.isMilestone ? '<i class="bi bi-flag-fill text-primary me-1" aria-hidden="true"></i><span class="visually-hidden">Milstolpe: </span>' : '';
        const sent = t.sentToForbundetAt ? `<div class="small text-success">Skickad till förbundet ${esc(fmtShort(t.sentToForbundetAt))}${t.sentToForbundetBy ? ' — ' + esc(t.sentToForbundetBy) : ''}</div>`
            : (t.isMilestone && t.sendToForbundet ? '<div class="small text-body-secondary">Skickas till förbundet</div>' : '');
        return `<tr class="${t.mine && open ? 'pl-tkmine' : ''}${open ? '' : ' pl-tkdonerow'}"><td class="ps-3">${flag}<b>${esc(t.title)}</b>${partChip(t.partId)}${quick}${sent}<div class="pl-help small">${origin}${extra.length ? ' · ' + extra.join(' · ') : ''}</div>${done}
                <div class="d-sm-none mt-1">${tkChip(t)}</div>
                <div class="d-md-none small mt-1">${esc(t.assignee || 'Ingen ansvarig än')}${t.due ? ' · senast ' + esc(fmtDay(t.due)) : ''}${t.overdue ? ' <span class="text-danger">försenad</span>' : ''}</div></td>
            <td class="d-none d-md-table-cell">${t.assignee ? esc(t.assignee) : '<span class="pl-help">Ingen än</span>'}</td>
            <td class="d-none d-md-table-cell text-nowrap">${t.due || t.dueDaysBefore != null ? dueText(t) + (t.overdue ? ' <span class="text-danger small">försenad</span>' : '') : '<span class="pl-help">—</span>'}</td>
            <td class="d-none d-sm-table-cell">${tkChip(t)}</td>
            <td class="d-none d-lg-table-cell small">${t.report ? esc(t.report.length > 60 ? t.report.slice(0, 60) + '…' : t.report) : '<span class="pl-help">—</span>'}</td>
            <td class="text-end pl-noprint">${menu}</td></tr>`;
    }
    function renderTaskRows() {
        const q = tkQuery.trim().toLowerCase();
        const order = { ReadyForReview: 0, NeedsHelp: 1, InProgress: 2, NotStarted: 3 };
        const sortRows = rows => rows.sort((a, b) => (a.status === 'Done') - (b.status === 'Done') || a.sortOrder - b.sortOrder
            || (order[a.status] ?? 9) - (order[b.status] ?? 9) || (a.due || '9999').localeCompare(b.due || '9999') || a.id - b.id);
        const rows = TASKS.tasks.filter(t => taskMatches(t, q));
        rowFns = [];
        const parts = [];
        const areas = TASKS.areas || [];
        const groups = areas.map(a => ({ area: a, rows: sortRows(rows.filter(t => t.areaId === a.id)) }));
        groups.push({ area: null, rows: sortRows(rows.filter(t => !t.areaId || !areaById(t.areaId))) });
        const searching = q || tkFilter !== 'open' || tkPart !== '';
        groups.forEach(g => {
            if (g.area) {
                const allIn = TASKS.tasks.filter(t => t.areaId === g.area.id);
                if (searching && !g.rows.length) return;
                const openN = allIn.filter(t => t.status !== 'Done').length;
                const menu = TASKS.canManageAreas ? rowMenu([
                    TASKS.canCreate && { label: 'Ny uppgift i området…', fn: () => openTaskEdit(null, g.area.id) },
                    { label: 'Ändra området…', fn: () => openArea(g.area) },
                    { label: 'Flytta upp', fn: () => moveArea(g.area, -1) },
                    { label: 'Flytta ner', fn: () => moveArea(g.area, 1) },
                    canAddDocs() && { label: 'Bifoga dokument…', fn: () => pickDocuments({ attach: { kind: 'Area', id: g.area.id } }) },
                    { divider: true },
                    { label: 'Ta bort området…', danger: true, fn: () => deleteArea(g.area) },
                ]) : '';
                parts.push(`<tr class="pl-areah"><td colspan="6" class="ps-3"><div class="d-flex flex-wrap align-items-center gap-2">
                    <b>${esc(g.area.name)}</b>${g.area.responsible ? `<span class="pl-help small">Ansvarig: ${esc(g.area.responsible)}</span>` : ''}
                    <span class="ms-auto pl-help small">${openN === 0 && allIn.length ? 'allt utfört' : openN + ' öppna'}</span>${menu}</div>${docChips('Area', g.area.id)}</td></tr>`);
                parts.push(g.rows.length ? g.rows.map(taskRow).join('') : `<tr><td colspan="6" class="pl-help ps-4 small">Inga uppgifter i området ännu.</td></tr>`);
            } else if (g.rows.length) {
                if (areas.length) parts.push(`<tr class="pl-areah"><td colspan="6" class="ps-3"><b>Utan område</b></td></tr>`);
                parts.push(g.rows.map(taskRow).join(''));
            }
        });
        $('tkRows').innerHTML = parts.join('') || '<tr><td colspan="6" class="pl-help ps-3">Ingen uppgift matchar.</td></tr>';
        $('tkCount').textContent = rows.length === 1 ? '1 uppgift' : `${rows.length} uppgifter`;
    }

    function peopleOptions(cur) {
        return '<option value="">Ingen ännu</option>' + S.participants.slice().sort((a, b) => a.name.localeCompare(b.name, 'sv'))
            .map(p => `<option value="${p.id}"${p.id === cur ? ' selected' : ''}>${esc(p.name)}</option>`).join('');
    }
    function areaOptions(cur) {
        return '<option value="">Utan område</option>' + (TASKS.areas || [])
            .map(a => `<option value="${a.id}"${a.id === cur ? ' selected' : ''}>${esc(a.name)}</option>`).join('');
    }
    // Vem: en deltagare, en medlem som inte är med (läggs till som Deltar), eller ett fritt namn.
    function whoField(t) {
        const free = t && !t.assigneeId && t.assigneeName;
        return `<label class="form-label" for="teWho">Vem gör det?</label>
            <select id="teWho" class="form-select">${peopleOptions(t ? t.assigneeId : null)}
                ${TOKEN ? '' : '<option value="__member">Någon som inte är med i planeringen…</option>'}
                <option value="__name"${free ? ' selected' : ''}>Skriv ett namn (t.ex. en sektion eller ett företag)…</option></select>
            <div id="teMemberBox" class="mt-2 d-none"><input id="teMemberQ" class="form-control" placeholder="Sök på medlemmens namn" autocomplete="off">
                <div id="teMemberHits" class="list-group mt-1"></div><input type="hidden" id="teMemberId">
                <div class="pl-help small mt-1">Den du väljer läggs till i planeringen som <i>Deltar</i> och får ett mejl om uppgiften.</div></div>
            <div id="teNameBox" class="mt-2${free ? '' : ' d-none'}"><input id="teName" class="form-control" maxlength="200" value="${esc(free ? t.assigneeName : '')}" placeholder="Till exempel Ungdomssektionen"></div>`;
    }
    function wireWho(body) {
        const sel = body.querySelector('#teWho');
        const sync = () => {
            body.querySelector('#teMemberBox').classList.toggle('d-none', sel.value !== '__member');
            body.querySelector('#teNameBox').classList.toggle('d-none', sel.value !== '__name');
            if (sel.value === '__member') body.querySelector('#teMemberQ').focus();
        };
        sel.addEventListener('change', sync);
        let seq = 0;
        body.querySelector('#teMemberQ')?.addEventListener('input', async e => {
            const q = e.target.value.trim(), my = ++seq;
            const hits = body.querySelector('#teMemberHits');
            if (q.length < 2) { hits.innerHTML = ''; return; }
            const r = await get('SearchMembers', { q });
            if (my !== seq || !r.success) return;
            hits.innerHTML = r.members.length ? r.members.map(m => `<button type="button" class="list-group-item list-group-item-action" data-pick-member="${m.memberId}">
                ${esc(m.name)}${m.club ? ` <span class="pl-help small">${esc(m.club)}</span>` : ''}${m.alreadyIn ? ' <span class="badge text-bg-light border">med</span>' : ''}</button>`).join('')
                : '<div class="pl-help small p-2">Ingen träff.</div>';
        });
        body.addEventListener('click', e => {
            const b = e.target.closest('[data-pick-member]'); if (!b) return;
            body.querySelector('#teMemberId').value = b.dataset.pickMember;
            body.querySelectorAll('[data-pick-member]').forEach(x => x.classList.toggle('active', x === b));
        });
    }
    function whoParams(body) {
        const v = body.querySelector('#teWho').value;
        if (v === '__member') return { assignee: '', assigneeMemberId: body.querySelector('#teMemberId').value, assigneeName: '' };
        if (v === '__name') return { assignee: '', assigneeMemberId: '', assigneeName: body.querySelector('#teName').value.trim() };
        return { assignee: v, assigneeMemberId: '', assigneeName: '' };
    }
    function openTaskEdit(t, presetArea) {
        const c = competition();
        const days = t ? t.dueDaysBefore != null : false;
        const showDays = canDaysBefore() || days;
        const showScope = !!c || (t && t.scopeType);
        const scopeOpts = '<option value="">—</option>' + (TASKS.scopeTypes || []).map(s => `<option${t && t.scopeType === s ? ' selected' : ''}>${esc(s)}</option>`).join('');
        formModal({
            title: t ? 'Ändra uppgiften' : 'Ny uppgift', okLabel: t ? 'Spara' : 'Lägg till', wide: false,
            html: `<div class="mb-3"><label class="form-label fw-semibold" for="teTitle">Vad ska göras?</label>
                    <input id="teTitle" class="form-control" maxlength="300" value="${esc(t ? t.title : '')}"></div>
                ${(TASKS.areas || []).length ? `<div class="mb-3"><label class="form-label" for="teArea">Område</label><select id="teArea" class="form-select">${areaOptions(t ? t.areaId : presetArea || null)}</select></div>` : ''}
                ${parts().length ? `<div class="mb-3"><label class="form-label" for="tePart">Del</label><select id="tePart" class="form-select">
                    <option value="0">Hela planeringen</option>${parts().map(p => `<option value="${p.id}"${(t ? t.partId === p.id : String(p.id) === tkPart) ? ' selected' : ''}>${esc(p.name)}</option>`).join('')}</select></div>` : ''}
                <div class="mb-3">${whoField(t)}</div>
                <div class="mb-3"><div class="form-label">Klart senast (valfritt)</div>
                    ${showDays ? `<div class="btn-group mb-2" role="group" aria-label="Sista dag som">
                        <input type="radio" class="btn-check" name="teDueMode" id="teDueDate" value="date"${days ? '' : ' checked'}><label class="btn btn-outline-secondary btn-sm" for="teDueDate">Ett datum</label>
                        <input type="radio" class="btn-check" name="teDueMode" id="teDueDays" value="days"${days ? ' checked' : ''}><label class="btn btn-outline-secondary btn-sm" for="teDueDays">Dagar ${esc(daysWord())}</label></div>` : ''}
                    <div id="teDueDateBox"${days ? ' class="d-none"' : ''}><input id="teDue" class="form-control" value="${esc(t && !days ? t.due : '')}" placeholder="åååå-mm-dd"></div>
                    <div id="teDueDaysBox" class="${days ? '' : 'd-none'}"><div class="input-group" style="max-width:16rem"><input id="teDays" type="number" min="0" max="1000" class="form-control" value="${days ? t.dueDaysBefore : ''}">
                        <span class="input-group-text">dagar ${esc(daysWord())}</span></div>
                        <div class="pl-help small mt-1">${TASKS.anchor ? 'Datumet följer med om dagen flyttas.' : 'Datumet räknas fram när dagen är bestämd.'}</div></div></div>
                <div class="mb-3"><div class="form-check"><input class="form-check-input" type="checkbox" id="teMilestone"${t && t.isMilestone ? ' checked' : ''}>
                    <label class="form-check-label" for="teMilestone"><b>Milstolpe</b> — ett viktigt datum, till exempel en frist hos förbundet</label></div>
                    <div class="form-check ms-4${t && t.isMilestone ? '' : ' d-none'}" id="teForbundetBox"><input class="form-check-input" type="checkbox" id="teForbundet"${t && t.sendToForbundet ? ' checked' : ''}>
                    <label class="form-check-label" for="teForbundet">Skickas till förbundet, med dokument ur planeringens arkiv</label></div></div>
                <details class="mb-1"${t && (t.description || t.scopeType || t.estimatedCost != null) ? ' open' : ''}><summary class="mb-2">Mer om uppgiften</summary>
                    <div class="mb-3"><label class="form-label" for="teDesc">Beskrivning</label><textarea id="teDesc" class="form-control" rows="3" maxlength="4000">${esc(t ? t.description || '' : '')}</textarea></div>
                    ${showScope ? `<div class="row g-2 mb-3"><div class="col-6"><label class="form-label" for="teScope">Gäller</label><select id="teScope" class="form-select">${scopeOpts}</select></div>
                        <div class="col-6"><label class="form-label" for="teScopeKey">Nummer</label><input id="teScopeKey" class="form-control" maxlength="100" value="${esc(t ? t.scopeKey || '' : '')}" placeholder="t.ex. 3"></div></div>` : ''}
                    <div class="mb-1"><label class="form-label" for="teCost">Uppskattning (kr)</label><input id="teCost" class="form-control" inputmode="decimal" style="max-width:12rem" value="${t && t.estimatedCost != null ? esc(t.estimatedCost) : ''}">
                        <div class="pl-help small">Vad ni räknar med att det kostar. Utfallet kommer ur bokföringen.</div></div></details>
                ${t ? '' : '<p class="pl-help small mt-2 mb-0">Den som får uppgiften får ett mejl.</p>'}`,
            onOpen: body => {
                datePicker(body.querySelector('#teDue'));
                wireWho(body);
                body.querySelectorAll('[name=teDueMode]').forEach(r => r.addEventListener('change', () => {
                    const d = body.querySelector('#teDueDays').checked;
                    body.querySelector('#teDueDateBox').classList.toggle('d-none', d);
                    body.querySelector('#teDueDaysBox').classList.toggle('d-none', !d);
                }));
                body.querySelector('#teMilestone').addEventListener('change', e => body.querySelector('#teForbundetBox').classList.toggle('d-none', !e.target.checked));
                body.querySelector('#teTitle').focus();
            },
            onOk: async body => {
                const daysMode = !!body.querySelector('#teDueDays') && body.querySelector('#teDueDays').checked;
                const milestone = body.querySelector('#teMilestone').checked;
                const w = whoParams(body);
                if (body.querySelector('#teWho').value === '__member' && !w.assigneeMemberId) return 'Sök fram och välj medlemmen först.';
                const p = Object.assign({
                    title: body.querySelector('#teTitle').value,
                    due: daysMode ? '' : body.querySelector('#teDue').value.trim(),
                    dueDaysBefore: daysMode ? body.querySelector('#teDays').value.trim() : '',
                    areaId: body.querySelector('#teArea') ? body.querySelector('#teArea').value : (t ? t.areaId || '' : presetArea || ''),
                    description: body.querySelector('#teDesc').value,
                    scopeType: body.querySelector('#teScope') ? body.querySelector('#teScope').value : (t ? t.scopeType || '' : ''),
                    scopeKey: body.querySelector('#teScopeKey') ? body.querySelector('#teScopeKey').value : (t ? t.scopeKey || '' : ''),
                    estimatedCost: body.querySelector('#teCost').value.trim(),
                    isMilestone: milestone ? '1' : '0',
                    sendToForbundet: milestone && body.querySelector('#teForbundet').checked ? '1' : '0',
                }, w, body.querySelector('#tePart') ? { partId: body.querySelector('#tePart').value } : {});
                if (daysMode && p.dueDaysBefore === '') return 'Skriv hur många dagar före.';
                const r = t ? await post('UpdateTask', Object.assign({ taskId: t.id }, p)) : await post('CreateTask', p);
                if (!r.success) return r.message;
                await Promise.all([reloadTasks(), w.assigneeMemberId ? reloadPeople() : Promise.resolve()]);
                rerender();
                if (r.mailed) showMsg('Den som fick uppgiften har fått ett mejl.', 'success');
                return true;
            }
        });
    }
    // Milstolpen till förbundet (P3): valda dokument ur arkivet bifogas, svarsadressen är den som skickar.
    function openSendForbundet(t) {
        const to = TASKS.forbundetEmail || '';
        const docs = (DOCS && DOCS.documents) || [];
        if (!to) {
            formModal({ title: 'Skicka till förbundet', wide: false, cancelLabel: 'Stäng',
                html: `<p>Förbundets adress är inte ifylld än. En sajtadministratör fyller i den under planeringens <b>Inställningar</b>.</p>` });
            return;
        }
        formModal({
            title: 'Skicka till förbundet', okLabel: 'Skicka', wide: false,
            html: `<p class="mb-2"><b>${esc(t.title)}</b></p>
                <p class="pl-help small">Mejlet går till <b>${esc(to)}</b>. Förbundet svarar dig. Milstolpen bockas av när mejlet har gått iväg.</p>
                <div class="fw-semibold mb-1">Bifoga dokument</div>
                ${docs.length ? `<div class="mb-3" style="max-height:240px;overflow:auto">${docs.map((d, i) => `<div class="form-check">
                    <input class="form-check-input sf-doc" type="checkbox" id="sfDoc${i}" data-sf-doc="${d.id}">
                    <label class="form-check-label" for="sfDoc${i}">${esc(d.title)}${d.fileName ? ` <span class="pl-help small">${esc(d.fileName)}</span>` : ''}</label></div>`).join('')}</div>`
                    : '<p class="pl-help small">Planeringens arkiv är tomt. Lägg dokumentet under <b>Dokument</b> först.</p>'}
                <label class="form-label" for="sfMsg">Meddelande (valfritt)</label>
                <textarea id="sfMsg" class="form-control" rows="4" maxlength="2000"></textarea>`,
            onOk: async body => {
                const ids = [...body.querySelectorAll('.sf-doc:checked')].map(x => x.dataset.sfDoc);
                const r = await post('SendToForbundet', { taskId: t.id, documentIds: ids.join(','), message: body.querySelector('#sfMsg').value });
                if (!r.success) return r.message;
                await reloadTasks(); await reloadMessages(); rerender();
                showMsg('Skickat till förbundet (' + esc(r.to) + ').', 'success');
                return true;
            }
        });
    }
    function openReport(t) {
        const opts = (TASKS.statuses || []).filter(s => s.assignee || (s.value === 'Done' && t.canDecide));
        let chosen = t.status;
        formModal({
            title: t.title, okLabel: 'Spara', wide: false,
            html: `<p class="pl-help">${t.due ? 'Senast ' + esc(fmtDay(t.due)) + (t.fromMeeting ? ' · ' : '') : ''}${t.fromMeeting ? 'Från mötet ' + esc(t.fromMeeting) : ''}</p>
                <div class="fw-semibold mb-2">Hur långt har du kommit?</div>
                <div class="pl-statgrid" role="group" aria-label="Läge">${opts.map(s =>
                    `<button type="button" class="btn pl-statbtn${s.value === chosen ? ' on' : ''}" data-st="${s.value}" aria-pressed="${s.value === chosen}">${s.value === chosen ? '✓ ' : ''}${esc(s.label)}</button>`).join('')}</div>
                <label class="form-label fw-semibold mt-3" for="rpText">Hur går det? (valfritt)</label>
                <textarea id="rpText" class="form-control" rows="3" maxlength="2000">${esc(t.report || '')}</textarea>
                <p class="pl-help small mt-2 mb-0">${t.canDecide ? 'Är den klar trycker du Utförd.' : 'Om den är utförd bestämmer ni på mötet.'}</p>`,
            onOpen: body => body.addEventListener('click', e => {
                const b = e.target.closest('[data-st]'); if (!b) return;
                chosen = b.dataset.st;
                body.querySelectorAll('[data-st]').forEach(x => {
                    const on = x.dataset.st === chosen;
                    x.classList.toggle('on', on); x.setAttribute('aria-pressed', on);
                    const lbl = (opts.find(s => s.value === x.dataset.st) || {}).label || '';
                    x.textContent = (on ? '✓ ' : '') + lbl;
                });
            }),
            onOk: async body => {
                const text = body.querySelector('#rpText').value.trim();
                const report = text === (t.report || '').trim() ? '' : text;
                const status = chosen !== t.status ? chosen : '';
                if (!report && !status) return true;
                const r = await post('ReportTask', { taskId: t.id, status, report });
                if (!r.success) return r.message;
                await reloadTasks(); rerender(); showMsg('Sparat.', 'success');
                return true;
            }
        });
    }
    async function markDone(t) {
        const r = await post('DecideTask', { taskId: t.id, decision: 'done' });
        if (!r.success) { failMsg(r); return; }
        await reloadTasks(); rerender();
    }
    async function decideOutsideMeeting(t) {
        if (!await plConfirm('Markera som utförd?', `${t.title}\n\nDet står att du beslutade det, inte ett möte.`, 'Utförd')) return;
        await markDone(t);
    }
    async function undoDone(t, meetingId) {
        const r = await post('DecideTask', { taskId: t.id, decision: 'undo', meetingId: meetingId || '' });
        if (!r.success) { failMsg(r); return; }
        await reloadTasks(); rerender();
    }
    async function removeTask(t) {
        if (!await plConfirm('Ta bort uppgiften?', t.title, 'Ta bort', true)) return;
        const r = await post('RemoveTask', { taskId: t.id });
        if (!r.success) { failMsg(r); return; }
        await reloadTasks(); rerender();
    }
    const TK_EVENT = {
        Created: e => e.note ? 'lade till uppgiften — ' + e.note : 'lade till uppgiften', Edited: e => e.note || 'ändrade uppgiften',
        Status: e => `ändrade läget till ${e.newStatus}` + (e.note ? `: ”${e.note}”` : ''),
        Report: e => `skrev: ”${e.note}”`, Done: e => 'Utförd' + (e.meeting ? ` — på mötet ${e.meeting}` : ''),
        Undone: e => 'ångrade Utförd', Continued: e => 'Fortsätter' + (e.meeting ? ` (mötet ${e.meeting})` : '') + (e.note ? ` — ${e.note}` : ''),
        Removed: e => 'tog bort uppgiften', Comment: e => `kommenterade: ”${e.note}”`,
    };
    async function openHistory(t) {
        const r = await get('TaskHistory', { taskId: t.id });
        if (!r.success) { failMsg(r); return; }
        formModal({
            title: t.title, wide: false, cancelLabel: 'Stäng',
            html: r.events.length ? `<ul class="list-unstyled mb-0">${r.events.map(e =>
                `<li class="py-2 border-top"><div class="pl-help small">${esc(fmtShort(e.at))} · ${esc(e.who)}${e.viaEmail ? ' (ur mejlet)' : ''}</div>
                 <div>${esc((TK_EVENT[e.kind] || (() => e.kind))(e))}</div></li>`).join('')}</ul>` : '<p class="pl-help mb-0">Ingen historik.</p>',
        });
    }
    // Kommentarer och dokument på en uppgift. Dialogen ritas om på plats när något läggs till.
    async function openThread(t, keepOpen) {
        const r = await get('TaskHistory', { taskId: t.id });
        if (!r.success) { failMsg(r); return; }
        const fresh = findTask(t.id) || t;
        const comments = r.events.filter(e => e.kind === 'Comment' || e.kind === 'Report');
        const canComment = S.me.canWrite || fresh.mine;
        formModal({
            title: fresh.title, wide: false, cancelLabel: 'Stäng',
            html: `${fresh.description ? `<p style="white-space:pre-wrap">${esc(fresh.description)}</p>` : ''}
                <h3 class="h6">Kommentarer</h3>
                ${comments.length ? `<ul class="list-unstyled">${comments.map(e => `<li class="py-2 border-top"><div class="pl-help small">${esc(fmtShort(e.at))} · ${esc(e.who)}</div>
                    <div style="white-space:pre-wrap">${esc(e.note)}</div></li>`).join('')}</ul>` : '<p class="pl-help">Inga kommentarer ännu.</p>'}
                ${canComment ? `<label class="form-label" for="thText">Skriv en kommentar</label><textarea id="thText" class="form-control" rows="3" maxlength="4000"></textarea>
                    <button type="button" class="btn btn-primary btn-sm mt-2" id="thSend">Skicka</button>` : ''}
                <h3 class="h6 mt-4">Dokument</h3>
                ${docChips('Task', t.id) || '<p class="pl-help">Inga dokument bifogade.</p>'}
                ${canAddDocs() ? `<button type="button" class="btn btn-outline-secondary btn-sm mt-2" data-tk-doc-pick="${t.id}"><i class="bi bi-paperclip me-1"></i>Bifoga dokument…</button>` : ''}`,
            onOpen: body => {
                body.querySelector('#thSend')?.addEventListener('click', async e => {
                    const text = body.querySelector('#thText').value.trim();
                    if (!text) { body.querySelector('#thText').focus(); return; }
                    e.target.disabled = true;
                    const res = await post('AddTaskComment', { taskId: t.id, text });
                    e.target.disabled = false;
                    if (!res.success) { failMsg(res); return; }
                    await reloadTasks(); rerender(); openThread(t, true);
                });
            },
        });
    }

    // ---------------------------------------------------------------- områden
    function openArea(a) {
        formModal({
            title: a ? 'Ändra området' : 'Nytt område', okLabel: a ? 'Spara' : 'Lägg till', wide: false,
            html: `<div class="mb-3"><label class="form-label fw-semibold" for="arName">Områdets namn</label>
                    <input id="arName" class="form-control" maxlength="200" value="${esc(a ? a.name : '')}" placeholder="Till exempel Materiel, Banor eller Servering"></div>
                <div class="mb-2"><label class="form-label" for="arResp">Ansvarig (valfritt)</label>
                    <select id="arResp" class="form-select">${peopleOptions(a ? a.responsibleId : null)}<option value="__name"${a && !a.responsibleId && a.responsibleName ? ' selected' : ''}>Skriv ett namn…</option></select>
                    <input id="arRespName" class="form-control mt-2${a && !a.responsibleId && a.responsibleName ? '' : ' d-none'}" maxlength="200" value="${esc(a && !a.responsibleId ? a.responsibleName || '' : '')}"></div>`,
            onOpen: body => {
                const sel = body.querySelector('#arResp');
                sel.addEventListener('change', () => body.querySelector('#arRespName').classList.toggle('d-none', sel.value !== '__name'));
                body.querySelector('#arName').focus();
            },
            onOk: async body => {
                const v = body.querySelector('#arResp').value;
                const r = await post('SaveArea', { areaId: a ? a.id : 0, name: body.querySelector('#arName').value,
                    responsibleId: v === '__name' ? '' : v, responsibleName: v === '__name' ? body.querySelector('#arRespName').value : '' });
                if (!r.success) return r.message;
                await reloadTasks(); rerender(); return true;
            }
        });
    }
    async function moveArea(a, dir) {
        const r = await post('MoveArea', { areaId: a.id, direction: dir });
        if (!r.success) { failMsg(r); return; }
        await reloadTasks(); rerender();
    }
    async function deleteArea(a) {
        const n = TASKS.tasks.filter(t => t.areaId === a.id).length;
        if (!await plConfirm('Ta bort området?', `${a.name}\n\n${n ? `De ${n} uppgifterna står kvar, utan område.` : 'Området har inga uppgifter.'}`, 'Ta bort', true)) return;
        const r = await post('DeleteArea', { areaId: a.id });
        if (!r.success) { failMsg(r); return; }
        await reloadTasks(); rerender();
    }

    // ---------------------------------------------------------------- mall, checklista, stationer, inklistring
    function openTemplate() {
        const c = competition();
        const discOpt = c ? '' : `<div class="mb-3"><label class="form-label" for="tmDisc">Gren</label><select id="tmDisc" class="form-select">
            <option value="Precision">Precision</option><option value="Faltskytte">Fältskytte</option><option value="Springskytte">Springskytte</option></select></div>`;
        formModal({
            title: 'Fyll i från en mall', okLabel: 'Fyll i', wide: false,
            html: `<p>Mallen lägger till de vanliga områdena och uppgifterna för en tävling av den storleken${c ? ' i tävlingens gren' : ''}.
                    Sista dagarna räknas ${esc(daysWord())}. Ett område som redan finns rörs inte.</p>
                ${discOpt}
                <div class="mb-1">${[['klubb', 'Klubbtävling'], ['krets', 'Kretstävling'], ['sm', 'Mästerskap (SM eller landsdel)']].map(([v, l], i) =>
                    `<div class="form-check"><input class="form-check-input" type="radio" name="tmSize" id="tm${v}" value="${v}"${i === 0 ? ' checked' : ''}><label class="form-check-label" for="tm${v}">${esc(l)}</label></div>`).join('')}</div>`,
            onOk: async body => {
                const size = body.querySelector('[name=tmSize]:checked').value;
                const r = await post('SeedTemplate', { size, discipline: body.querySelector('#tmDisc') ? body.querySelector('#tmDisc').value : '' });
                if (!r.success) return r.message;
                await reloadTasks(); rerender();
                showMsg(r.tasks ? `${r.areas} områden och ${r.tasks} uppgifter lades till.` : 'Mallens områden fanns redan — ingenting lades till.', r.tasks ? 'success' : 'info');
                return true;
            }
        });
    }
    async function openKretsChecklist() {
        const r = await get('KretsChecklist');
        if (!r.success) { failMsg(r); return; }
        if (!r.available) {
            formModal({ title: 'Kretsens checklista', wide: false, cancelLabel: 'Stäng',
                html: '<p class="mb-0">Kretsen har ingen checklista för den här tävlingen.</p>' });
            return;
        }
        const items = r.pending || [];
        formModal({
            title: 'Kretsens checklista — ' + r.regionName, okLabel: 'Spara valen', wide: true,
            html: `<p>Det här vill kretsen att arrangören gör. Välj för varje punkt: lägg in den som en uppgift, eller <i>Behövs inte</i>.
                    ${r.added || r.dismissed ? `<span class="pl-help">Tidigare: ${r.added} inlagda, ${r.dismissed} bortvalda.</span>` : ''}</p>
                ${items.length ? `<div class="table-responsive"><table class="table align-middle"><tbody>${items.map(p => `<tr><td><b>${esc(p.title)}</b>
                    ${p.description ? `<div class="pl-help small">${esc(p.description)}</div>` : ''}${p.daysBefore != null ? `<div class="pl-help small">${p.daysBefore} dagar före tävlingen</div>` : ''}</td>
                    <td style="width:12rem"><select class="form-select form-select-sm" data-kc="${p.id}"><option value="add">Lägg in</option><option value="dismiss">Behövs inte</option><option value="">Bestäm senare</option></select></td></tr>`).join('')}</tbody></table></div>`
                : '<p class="pl-help mb-0">Alla kretsens punkter är redan hanterade.</p>'}`,
            onOk: items.length && r.canApply ? async body => {
                const add = [], dismiss = [];
                body.querySelectorAll('[data-kc]').forEach(s => { if (s.value === 'add') add.push(s.dataset.kc); else if (s.value === 'dismiss') dismiss.push(s.dataset.kc); });
                if (!add.length && !dismiss.length) return true;
                const res = await post('ApplyKretsChecklist', { add: add.join(','), dismiss: dismiss.join(',') });
                if (!res.success) return res.message;
                await reloadTasks(); rerender();
                showMsg(`${res.added} uppgifter lades till${res.dismissed ? `, ${res.dismissed} punkter valdes bort` : ''}.`, 'success');
                return true;
            } : null,
        });
    }
    async function seedStations() {
        const c = competition();
        if (!await plConfirm('En uppgift per station?', `"Bygg station 1" … "Bygg station ${c.stationCount}" läggs i området Stationsbygge. Stationer som redan har en uppgift hoppas över.`, 'Lägg till')) return;
        const r = await post('SeedStations', {});
        if (!r.success) { failMsg(r); return; }
        await reloadTasks(); rerender();
        showMsg(r.added ? `${r.added} uppgifter lades till.` : 'Alla stationer har redan en uppgift.', r.added ? 'success' : 'info');
    }
    function openPaste(presetArea) {
        let ready = false;
        formModal({
            title: 'Klistra in uppgifter', okLabel: 'Visa hur det blir', wide: true,
            html: `<p>Markera raderna i kalkylarket och klistra in dem här. En uppgift per rad: <b>uppgift</b>, <b>vem</b> och <b>sista dag</b> i var sin kolumn — de två sista får vara tomma.</p>
                ${(TASKS.areas || []).length ? `<div class="mb-2"><label class="form-label" for="psArea">Område</label><select id="psArea" class="form-select" style="max-width:20rem">${areaOptions(presetArea)}</select></div>` : ''}
                <textarea id="psText" class="form-control font-monospace" rows="8" placeholder="Beställ tavlor&#9;Kalle Andersson&#9;2027-05-01"></textarea>
                <div id="psPreview" class="mt-3"></div>`,
            onOpen: body => body.querySelector('#psText').addEventListener('input', () => { ready = false; $('plFormOk').textContent = 'Visa hur det blir'; body.querySelector('#psPreview').innerHTML = ''; }),
            onOk: async body => {
                const params = { text: body.querySelector('#psText').value, areaId: body.querySelector('#psArea') ? body.querySelector('#psArea').value : '' };
                if (!ready) {
                    const r = await post('PasteTasks', params);
                    body.querySelector('#psPreview').innerHTML = (r.rows || []).length ? `<table class="table table-sm"><thead><tr><th>Uppgift</th><th>Vem</th><th>Sista dag</th></tr></thead><tbody>${r.rows.map(x =>
                        `<tr class="${x.error ? 'table-danger' : ''}"><td>${esc(x.title)}${x.error ? `<div class="small text-danger">${esc(x.error)}</div>` : ''}</td>
                        <td>${esc(x.who || '')}${x.who && !x.matched ? ' <span class="pl-help small">(skrivs som namn)</span>' : ''}</td><td>${esc(x.due || '')}</td></tr>`).join('')}</tbody></table>` : '';
                    if (!r.success) return r.message;
                    ready = true; $('plFormOk').textContent = `Lägg in ${r.rows.length} uppgifter`;
                    return false;
                }
                const r = await post('PasteTasks', Object.assign({ apply: '1' }, params));
                if (!r.success) return r.message;
                await reloadTasks(); rerender(); showMsg(`${r.added} uppgifter lades till.`, 'success');
                return true;
            }
        });
    }

    // ================================================================ Dokument
    // Planeringens arkiv (2026-10-23): kategorier och dokument, och var de är bifogade. Ett dokument
    // bor i arkivet och kan bifogas i ett meddelande, på ett möte, på en punkt att prata om, på en
    // uppgift eller ett område. Alla som är med läser alla dokument.
    async function reloadDocs() { const r = await get('Documents'); if (r.success) DOCS = r; }
    function docById(id) { return DOCS ? DOCS.documents.find(d => d.id === id) : null; }
    function canAddDocs() { return !!(DOCS && DOCS.canAdd && !TOKEN); }
    function refsOn(kind, id) {
        const out = [];
        if (!DOCS) return out;
        DOCS.documents.forEach(d => d.refs.forEach(r => { if (r.kind === kind && r.targetId === id) out.push({ doc: d, ref: r }); }));
        return out;
    }
    function goneRefsOn(kind, id) { return DOCS ? DOCS.removedRefs.filter(r => r.kind === kind && r.targetId === id) : []; }
    function fmtSize(b) {
        if (!b) return '';
        if (b < 1024 * 1024) return Math.max(1, Math.round(b / 1024)) + ' kB';
        return (b / (1024 * 1024)).toLocaleString('sv-SE', { maximumFractionDigits: 1 }) + ' MB';
    }
    const DOC_ICON = { pdf: 'bi-file-earmark-pdf', doc: 'bi-file-earmark-word', docx: 'bi-file-earmark-word', xls: 'bi-file-earmark-excel',
        xlsx: 'bi-file-earmark-excel', csv: 'bi-file-earmark-spreadsheet', ppt: 'bi-file-earmark-ppt', pptx: 'bi-file-earmark-ppt',
        jpg: 'bi-file-earmark-image', jpeg: 'bi-file-earmark-image', png: 'bi-file-earmark-image', webp: 'bi-file-earmark-image',
        gif: 'bi-file-earmark-image', txt: 'bi-file-earmark-text' };
    function docExt(d) { const m = /\.([a-z0-9]+)$/i.exec(d.fileName || ''); return m ? m[1].toLowerCase() : ''; }
    function docIcon(d) { return d.isFile ? (DOC_ICON[docExt(d)] || 'bi-file-earmark') : 'bi-link-45deg'; }
    function docKindText(d) { return d.isFile ? (docExt(d).toUpperCase() || 'Fil') : 'Länk'; }
    function catName(id) { const c = DOCS && DOCS.categories.find(x => x.id === id); return c ? c.name : ''; }

    // Bifogade dokument som små brickor. ✕ tar bort bifogningen (aldrig dokumentet).
    function docChips(kind, id) {
        const items = refsOn(kind, id), gone = goneRefsOn(kind, id);
        if (!items.length && !gone.length) return '';
        return `<div class="pl-docs">${items.map(({ doc, ref }) => `<span class="pl-doc"><i class="bi ${docIcon(doc)}" aria-hidden="true"></i>
                <a href="${esc(doc.url)}" target="_blank" rel="noopener" title="${esc(doc.title)}">${esc(doc.title)}</a>
                ${ref.canDetach && !TOKEN ? `<button type="button" class="pl-noprint" data-doc-detach="${ref.refId}" title="Ta bort bifogningen" aria-label="Ta bort bifogningen av ${esc(doc.title)}">✕</button>` : ''}</span>`).join('')}
            ${gone.map(r => `<span class="pl-doc gone"><i class="bi bi-file-earmark-x" aria-hidden="true"></i>Borttaget dokument
                ${!TOKEN && DOCS.canManageCategories ? `<button type="button" class="pl-noprint" data-doc-detach="${r.refId}" aria-label="Ta bort raden">✕</button>` : ''}</span>`).join('')}</div>`;
    }
    function attachBtn(kind, id, label, small) {
        if (!canAddDocs()) return '';
        return `<button type="button" class="btn ${small ? 'btn-link btn-sm p-0' : 'btn-sm btn-outline-secondary'} pl-noprint" data-doc-attach="${kind}:${id}">
            <i class="bi bi-paperclip me-1" aria-hidden="true"></i>${esc(label || 'Bifoga dokument')}</button>`;
    }
    document.addEventListener('click', e => {
        const a = e.target.closest('[data-doc-attach]');
        if (a) { const [kind, id] = a.dataset.docAttach.split(':'); pickDocuments({ attach: { kind, id: parseInt(id, 10) } }); return; }
        const d = e.target.closest('[data-doc-detach]');
        if (d) { detachDoc(parseInt(d.dataset.docDetach, 10)); return; }
        const g = e.target.closest('[data-doc-goto]');
        if (g) { e.preventDefault(); gotoRef(g.dataset.docGoto); }
    });
    async function detachDoc(refId) {
        const r = await post('DetachDocument', { refId });
        if (!r.success) { failMsg(r); return; }
        await reloadDocs(); rerender();
    }
    async function afterDocChange() {
        await reloadDocs();
        if ($('plFormModal').classList.contains('show')) return; // en öppen dialog ritar om sig själv
        rerender();
    }

    // Var ett dokument är bifogat, i ord — och vart man kommer om man klickar.
    function refText(r) {
        const t = r.title || '';
        if (r.kind === 'Message') return `Meddelande från ${t || 'någon'}${r.at ? ', ' + fmtShort(r.at) : ''}`;
        if (r.kind === 'Meeting') return `Möte: ${t}`;
        if (r.kind === 'Topic') { const m = MEET && MEET.meetings.find(x => x.id === r.meetingId); return `Att prata om${m ? ' på ' + m.title : ''}: ${t}`; }
        if (r.kind === 'Task') return `Uppgift: ${t}`;
        if (r.kind === 'Area') return `Område: ${t}`;
        return t;
    }
    function gotoRef(code) {
        const [kind, id, meetingId] = code.split(':').map(x => parseInt(x, 10) || x);
        if (kind === 'Meeting' || kind === 'Topic') { if (meetingId) goMeeting(meetingId); return; }
        if (kind === 'Message') { go('meddelanden'); setTimeout(() => document.querySelector(`[data-msg-id="${id}"]`)?.scrollIntoView({ block: 'center' }), 60); return; }
        if (kind === 'Task') { const t = findTask(id); if (t) openThread(t); else go('uppgifter'); return; }
        if (kind === 'Area') go('uppgifter');
    }

    let docQuery = '', docCat = '';
    function viewDocuments() {
        setHead('Dokument', esc(S.planning.name));
        setActions([
            canAddDocs() && { label: 'Lägg till dokument eller länk…', icon: 'bi-upload', fn: () => openDocAdd({}) },
            !TOKEN && DOCS && DOCS.canManageCategories && { label: 'Ny kategori…', icon: 'bi-folder-plus', fn: () => openCategory(null) },
        ]);
        if (!DOCS) { $('plBody').innerHTML = spinner(); return; }
        const docs = DOCS.documents, cats = DOCS.categories;
        const html = [`<p class="pl-help">Planeringens dokument på ett ställe — inbjudan, kartor, avtal, offerter. Bifoga dem i ett meddelande,
            på ett möte eller på en uppgift. Alla som är med i planeringen kan läsa dem.</p>`];
        if (!docs.length) {
            html.push(`<div class="pl-card"><p class="mb-2"><b>Inga dokument ännu.</b></p>
                ${canAddDocs() ? `<button type="button" class="btn btn-primary" data-doc-add="1"><i class="bi bi-upload me-1"></i>Lägg till dokument eller länk…</button>` : ''}
                ${cats.length ? '' : `<p class="pl-help mb-0 mt-3">Många dokument? Dela upp dem i kategorier, till exempel <i>Inbjudan</i>, <i>Banor</i> och <i>Avtal</i>${DOCS.canManageCategories && !TOKEN ? ' — under Åtgärder.' : '.'}</p>`}</div>`);
        } else {
            const catOpts = `<option value="">Alla kategorier</option>${cats.map(c => `<option value="${c.id}"${String(c.id) === docCat ? ' selected' : ''}>${esc(c.name)}</option>`).join('')}`
                + (docs.some(d => !d.categoryId) && cats.length ? `<option value="none"${docCat === 'none' ? ' selected' : ''}>Utan kategori</option>` : '');
            html.push(`<div class="d-flex flex-wrap gap-2 align-items-center mb-2 pl-noprint">
                <input type="search" class="form-control form-control-sm" id="docQ" placeholder="Sök på namn eller beskrivning" style="max-width:20rem" value="${esc(docQuery)}" aria-label="Sök bland dokumenten">
                ${cats.length ? `<select class="form-select form-select-sm" id="docCat" style="max-width:16rem" aria-label="Kategori">${catOpts}</select>` : ''}
                <span class="ms-auto pl-help small" id="docCount"></span></div><div id="docGroups"></div>`);
        }
        if (DOCS.storage && DOCS.storage.usedBytes) html.push(`<p class="pl-help small mt-3 mb-0">Filerna tar ${esc(fmtSize(DOCS.storage.usedBytes))} av ${esc(fmtSize(DOCS.storage.limitBytes))}.</p>`);
        $('plBody').innerHTML = html.join('');
        if (!docs.length) return;
        renderDocGroups();
        $('docQ').addEventListener('input', e => { docQuery = e.target.value; renderDocGroups(); });
        $('docCat')?.addEventListener('change', e => { docCat = e.target.value; renderDocGroups(); });
    }
    function renderDocGroups() {
        const q = docQuery.trim().toLowerCase();
        const cats = DOCS.categories;
        const match = d => (!q || [d.title, d.description, d.fileName].some(v => (v || '').toLowerCase().includes(q)))
            && (!docCat || (docCat === 'none' ? !d.categoryId || !cats.some(c => c.id === d.categoryId) : String(d.categoryId) === docCat));
        const rows = DOCS.documents.filter(match);
        rowFns = [];
        const groups = cats.map(c => ({ cat: c, docs: rows.filter(d => d.categoryId === c.id) }));
        groups.push({ cat: null, docs: rows.filter(d => !d.categoryId || !cats.some(c => c.id === d.categoryId)) });
        const filtering = q || docCat;
        const parts = groups.map((g, gi) => {
            if (!g.docs.length && (filtering || !g.cat)) return '';
            const menu = g.cat && DOCS.canManageCategories && !TOKEN ? rowMenu([
                canAddDocs() && { label: 'Lägg till dokument i kategorin…', fn: () => openDocAdd({ categoryId: g.cat.id }) },
                { label: 'Byt namn…', fn: () => openCategory(g.cat) },
                gi > 0 && { label: 'Flytta upp', fn: () => moveCategory(g.cat, -1) },
                gi < cats.length - 1 && { label: 'Flytta ner', fn: () => moveCategory(g.cat, 1) },
                { divider: true },
                { label: 'Ta bort kategorin…', danger: true, fn: () => deleteCategory(g.cat) },
            ]) : '';
            const title = g.cat ? g.cat.name : (cats.length ? 'Utan kategori' : 'Dokument');
            return `<div class="pl-card mb-3"><div class="pl-cardh"><h2><i class="bi bi-folder2 me-2 text-secondary" aria-hidden="true"></i>${esc(title)}
                    <span class="pl-help small fw-normal">${g.docs.length}</span></h2>${menu}</div>
                ${g.docs.length ? g.docs.map(docRow).join('') : '<p class="pl-help mb-0">Inga dokument i kategorin ännu.</p>'}</div>`;
        }).join('');
        $('docGroups').innerHTML = parts || '<div class="pl-card"><p class="pl-help mb-0">Inget dokument matchar.</p></div>';
        $('docCount').textContent = rows.length === 1 ? '1 dokument' : `${rows.length} dokument`;
    }
    function docRow(d) {
        const meta = [docKindText(d), d.fileSize ? fmtSize(d.fileSize) : '', d.addedBy, d.addedAt ? fmtDay(d.addedAt) : ''].filter(Boolean);
        const where = d.refs.filter(r => !r.gone);
        const menu = rowMenu([
            { label: d.isFile ? 'Öppna filen' : 'Öppna länken', fn: () => window.open(d.url, '_blank', 'noopener') },
            d.canEdit && !TOKEN && { label: 'Ändra namn, beskrivning och kategori…', fn: () => openDocEdit(d) },
            canAddDocs() && { label: 'Bifoga på ett möte eller en uppgift…', fn: () => openAttachFromDoc(d) },
            d.canRemove && !TOKEN && { divider: true },
            d.canRemove && !TOKEN && { label: 'Ta bort dokumentet…', danger: true, fn: () => removeDoc(d) },
        ]);
        return `<div class="pl-docrow"><i class="bi ${docIcon(d)} fs-4 text-secondary" aria-hidden="true"></i>
            <div class="flex-grow-1 min-w-0"><a href="${esc(d.url)}" target="_blank" rel="noopener" class="fw-semibold">${esc(d.title)}</a>
                ${d.description ? `<div class="small" style="white-space:pre-wrap">${esc(d.description)}</div>` : ''}
                <div class="pl-help small">${meta.map(esc).join(' · ')}</div>
                ${where.length ? `<div class="small mt-1">Bifogat i: ${where.map(r => `<a href="#" data-doc-goto="${r.kind}:${r.targetId}:${r.meetingId || 0}">${esc(refText(r))}</a>`).join(' · ')}</div>` : ''}</div>
            <div class="flex-shrink-0">${menu}</div></div>`;
    }
    document.addEventListener('click', e => { if (e.target.closest('[data-doc-add]')) openDocAdd({}); });

    // ---------------------------------------------------------------- kategorier
    function openCategory(c) {
        formModal({
            title: c ? 'Byt namn på kategorin' : 'Ny kategori', okLabel: c ? 'Spara' : 'Lägg till', wide: false,
            html: `<label class="form-label" for="dcCatName">Vad heter kategorin?</label>
                <input id="dcCatName" class="form-control" maxlength="200" value="${esc(c ? c.name : '')}" placeholder="Till exempel Inbjudan, Banor eller Avtal">`,
            onOpen: body => body.querySelector('#dcCatName').focus(),
            onOk: async body => {
                const r = await post('SaveDocumentCategory', { categoryId: c ? c.id : '', name: body.querySelector('#dcCatName').value });
                if (!r.success) return r.message;
                await afterDocChange(); rerender(); return true;
            }
        });
    }
    async function moveCategory(c, dir) {
        const r = await post('MoveDocumentCategory', { categoryId: c.id, dir });
        if (!r.success) { failMsg(r); return; }
        await reloadDocs(); rerender();
    }
    async function deleteCategory(c) {
        if (!await plConfirm('Ta bort kategorin?', `${c.name}\n\nDokumenten i kategorin står kvar, utan kategori.`, 'Ta bort', true)) return;
        const r = await post('DeleteDocumentCategory', { categoryId: c.id });
        if (!r.success) { failMsg(r); return; }
        if (docCat === String(c.id)) docCat = '';
        await reloadDocs(); rerender();
    }
    function categorySelect(cur) {
        const cats = DOCS ? DOCS.categories : [];
        if (!cats.length) return '';
        return `<div class="mb-3"><label class="form-label" for="dcCat">Kategori</label><select id="dcCat" class="form-select">
            <option value="">Utan kategori</option>${cats.map(c => `<option value="${c.id}"${c.id === cur ? ' selected' : ''}>${esc(c.name)}</option>`).join('')}</select></div>`;
    }

    // ---------------------------------------------------------------- lägga till och ändra
    // opts: { categoryId, attach: {kind, id}, onAdded(docId) } — attach bifogar direkt, onAdded tar emot det nya dokumentet.
    function openDocAdd(opts) {
        opts = opts || {};
        const where = opts.attach ? `<p class="pl-help">Dokumentet läggs bland planeringens dokument och bifogas där du var.</p>` : '';
        formModal({
            title: 'Lägg till dokument eller länk', okLabel: 'Lägg till', wide: false,
            html: `${where}<div class="btn-group mb-3" role="group" aria-label="Fil eller länk">
                    <input type="radio" class="btn-check" name="dcKind" id="dcFile" value="file" checked><label class="btn btn-outline-secondary btn-sm" for="dcFile">Fil</label>
                    <input type="radio" class="btn-check" name="dcKind" id="dcLink" value="link"><label class="btn btn-outline-secondary btn-sm" for="dcLink">Länk</label></div>
                <div id="dcFileBox" class="mb-3"><label class="form-label" for="dcFileIn">Fil</label><input id="dcFileIn" type="file" class="form-control">
                    <div class="pl-help small mt-1">PDF, bild, Word, Excel, PowerPoint eller text, högst 25 MB.</div></div>
                <div id="dcLinkBox" class="mb-3 d-none"><label class="form-label" for="dcUrl">Länk</label><input id="dcUrl" class="form-control" placeholder="https://"></div>
                <div class="mb-3"><label class="form-label" for="dcTitle">Namn (valfritt)</label><input id="dcTitle" class="form-control" maxlength="200" placeholder="Till exempel Inbjudan eller Banritning"></div>
                <div class="mb-3"><label class="form-label" for="dcDesc">Beskrivning (valfri)</label><textarea id="dcDesc" class="form-control" rows="2" maxlength="1000"></textarea></div>
                ${categorySelect(opts.categoryId || null)}`,
            onOpen: body => body.querySelectorAll('[name=dcKind]').forEach(r => r.addEventListener('change', () => {
                const file = body.querySelector('#dcFile').checked;
                body.querySelector('#dcLinkBox').classList.toggle('d-none', file);
                body.querySelector('#dcFileBox').classList.toggle('d-none', !file);
            })),
            onOk: async body => {
                const title = body.querySelector('#dcTitle').value, description = body.querySelector('#dcDesc').value;
                const categoryId = body.querySelector('#dcCat') ? body.querySelector('#dcCat').value : '';
                let r;
                if (body.querySelector('#dcFile').checked) {
                    const f = body.querySelector('#dcFileIn').files[0];
                    if (!f) return 'Välj en fil.';
                    if (f.size > 25 * 1024 * 1024) return 'Filen är för stor (högst 25 MB).';
                    const fd = new FormData();
                    fd.append('file', f); fd.append('title', title); fd.append('description', description);
                    if (categoryId) fd.append('categoryId', categoryId);
                    r = await postForm('UploadDocument', fd);
                } else {
                    r = await post('AddDocumentLink', { title, description, categoryId, url: body.querySelector('#dcUrl').value.trim() });
                }
                if (!r.success) return r.message;
                if (opts.attach) {
                    const a = await post('AttachDocuments', { documentIds: r.documentId, kind: opts.attach.kind, targetId: opts.attach.id });
                    if (!a.success) { await reloadDocs(); return 'Dokumentet är tillagt, men kunde inte bifogas: ' + a.message; }
                }
                await reloadDocs();
                if (opts.onAdded) { opts.onAdded(r.documentId); return false; }
                rerender(); return true;
            }
        });
    }
    function openDocEdit(d) {
        formModal({
            title: 'Ändra dokumentet', okLabel: 'Spara', wide: false,
            html: `<div class="mb-3"><label class="form-label" for="dcTitle">Namn</label><input id="dcTitle" class="form-control" maxlength="200" value="${esc(d.title)}"></div>
                <div class="mb-3"><label class="form-label" for="dcDesc">Beskrivning (valfri)</label><textarea id="dcDesc" class="form-control" rows="3" maxlength="1000">${esc(d.description || '')}</textarea></div>
                ${categorySelect(d.categoryId)}
                <p class="pl-help small mb-0">Ska filen eller länken bytas? Lägg till den nya och ta bort den gamla.</p>`,
            onOk: async body => {
                const r = await post('UpdateDocument', { documentId: d.id, title: body.querySelector('#dcTitle').value,
                    description: body.querySelector('#dcDesc').value, categoryId: body.querySelector('#dcCat') ? body.querySelector('#dcCat').value : '' });
                if (!r.success) return r.message;
                await reloadDocs(); rerender(); return true;
            }
        });
    }
    async function removeDoc(d) {
        const n = d.refs.filter(r => !r.gone).length;
        const text = d.title + (n ? `\n\nDet är bifogat på ${n === 1 ? 'ett ställe' : n + ' ställen'}. Där står det sedan att dokumentet är borttaget.` : '');
        if (!await plConfirm('Ta bort dokumentet?', text, 'Ta bort', true)) return;
        const r = await post('RemoveDocument', { documentId: d.id });
        if (!r.success) { failMsg(r); return; }
        await reloadDocs(); rerender();
    }

    // ---------------------------------------------------------------- välja dokument
    // opts: { attach: {kind, id} } bifogar de valda; { onPick(ids), preselected: [ids] } lämnar dem till anroparen.
    function pickDocuments(opts) {
        opts = opts || {};
        const already = opts.attach ? new Set(refsOn(opts.attach.kind, opts.attach.id).map(x => x.doc.id)) : new Set();
        const chosen = new Set(opts.preselected || []);
        const draw = body => {
            const q = (body.querySelector('#pkQ').value || '').trim().toLowerCase();
            const cats = DOCS.categories;
            const docs = DOCS.documents.filter(d => !q || [d.title, d.description, d.fileName].some(v => (v || '').toLowerCase().includes(q)));
            const groups = cats.map(c => ({ name: c.name, docs: docs.filter(d => d.categoryId === c.id) }))
                .concat([{ name: cats.length ? 'Utan kategori' : '', docs: docs.filter(d => !d.categoryId || !cats.some(c => c.id === d.categoryId)) }]);
            const html = groups.filter(g => g.docs.length).map(g => `${g.name ? `<div class="fw-semibold small mt-2">${esc(g.name)}</div>` : ''}
                ${g.docs.map(d => {
                    const has = already.has(d.id);
                    return `<label class="d-flex gap-2 align-items-start py-1"><input type="checkbox" class="form-check-input mt-1" data-pk="${d.id}"${has || chosen.has(d.id) ? ' checked' : ''}${has ? ' disabled' : ''}>
                        <span><i class="bi ${docIcon(d)} text-secondary me-1" aria-hidden="true"></i>${esc(d.title)}${has ? ' <span class="pl-help small">(redan bifogat)</span>' : ''}
                        ${d.description ? `<span class="d-block pl-help small">${esc(d.description.length > 80 ? d.description.slice(0, 80) + '…' : d.description)}</span>` : ''}</span></label>`;
                }).join('')}`).join('');
            body.querySelector('#pkList').innerHTML = html || `<p class="pl-help mb-0">${DOCS.documents.length ? 'Inget dokument matchar.' : 'Planeringen har inga dokument ännu.'}</p>`;
        };
        formModal({
            title: 'Bifoga dokument', okLabel: 'Bifoga', wide: false,
            html: `<p class="pl-help">Välj bland planeringens dokument${canAddDocs() ? ', eller lägg till ett nytt' : ''}.</p>
                ${canAddDocs() ? `<button type="button" class="btn btn-outline-primary btn-sm mb-3" id="pkNew"><i class="bi bi-upload me-1"></i>Lägg till ett nytt dokument eller en länk…</button>` : ''}
                <input type="search" class="form-control form-control-sm mb-2" id="pkQ" placeholder="Sök" aria-label="Sök bland dokumenten">
                <div id="pkList" style="max-height:50vh;overflow:auto"></div>`,
            onOpen: body => {
                draw(body);
                body.querySelector('#pkQ').addEventListener('input', () => draw(body));
                body.addEventListener('change', e => {
                    const cb = e.target.closest('[data-pk]'); if (!cb) return;
                    const id = parseInt(cb.dataset.pk, 10);
                    if (cb.checked) chosen.add(id); else chosen.delete(id);
                });
                body.querySelector('#pkNew')?.addEventListener('click', () => openDocAdd({
                    // Det nya dokumentet väljs, och väljaren kommer tillbaka.
                    onAdded: id => { chosen.add(id); pickDocuments(Object.assign({}, opts, { preselected: [...chosen] })); },
                }));
            },
            onOk: async () => {
                const ids = [...chosen].filter(id => !already.has(id));
                if (!ids.length) return 'Välj minst ett dokument.';
                if (opts.onPick) { opts.onPick(ids); return true; }
                const r = await post('AttachDocuments', { documentIds: ids.join(','), kind: opts.attach.kind, targetId: opts.attach.id });
                if (!r.success) return r.message;
                await reloadDocs();
                if (opts.attach.kind === 'Task' && opts.reopenThread) { const t = findTask(opts.attach.id); if (t) { openThread(t, true); return false; } }
                rerender(); return true;
            }
        });
    }
    // Från arkivet: bifoga dokumentet på ett möte, en punkt eller en uppgift.
    function openAttachFromDoc(d) {
        const has = new Set(d.refs.filter(r => !r.gone).map(r => r.kind + ':' + r.targetId));
        const meetings = (MEET ? MEET.meetings : []).filter(m => m.status !== 'Cancelled');
        const tasks = (TASKS ? TASKS.tasks : []).filter(t => t.status !== 'Done');
        const areas = TASKS ? TASKS.areas || [] : [];
        const opt = (k, id, label) => has.has(k + ':' + id) ? '' : `<option value="${k}:${id}">${esc(label)}</option>`;
        const groups = [
            meetings.length && `<optgroup label="Möten">${meetings.map(m => opt('Meeting', m.id, m.title + (m.startsAt ? ' · ' + fmtDay(m.startsAt) : ''))).join('')}</optgroup>`,
            tasks.length && `<optgroup label="Uppgifter">${tasks.map(t => opt('Task', t.id, t.title)).join('')}</optgroup>`,
            areas.length && `<optgroup label="Områden">${areas.map(a => opt('Area', a.id, a.name)).join('')}</optgroup>`,
        ].filter(Boolean).join('');
        formModal({
            title: 'Bifoga dokumentet', okLabel: 'Bifoga', wide: false,
            html: `<p class="mb-2"><b>${esc(d.title)}</b></p>${groups
                ? `<label class="form-label" for="afTarget">Var ska det bifogas?</label><select id="afTarget" class="form-select">${groups}</select>
                   <p class="pl-help small mt-2 mb-0">Till ett meddelande bifogar du när du skriver det, under Meddelanden.</p>`
                : '<p class="pl-help mb-0">Det finns inga möten eller öppna uppgifter att bifoga till ännu.</p>'}`,
            onOk: groups ? async body => {
                const [kind, id] = body.querySelector('#afTarget').value.split(':');
                if (!kind) return 'Välj var det ska bifogas.';
                const r = await post('AttachDocuments', { documentIds: d.id, kind, targetId: id });
                if (!r.success) return r.message;
                await reloadDocs(); rerender(); return true;
            } : null,
        });
    }

    // Materielberäkningen räknas ur tävlingens anmälningar (bemanningens tjänst, tävlingens behörighet).
    async function openMateriel(c) {
        let html;
        try {
            const r = await req('/umbraco/surface/Staffing/GetMaterielEstimate?competitionId=' + c.id, { headers: { 'Accept': 'application/json' } });
            if (!r.success) html = `<div class="alert alert-warning mb-0">${esc(r.message || 'Beräkningen kunde inte göras.')}</div>`;
            else html = `<p class="pl-help">En <b>uppskattning</b> utifrån ${r.participantCount} deltagare, ${r.startCount} starter och ${r.classCount} klasser${r.series > 0 ? `, ${r.series} serier` : ''}. Justera innan ni beställer.</p>
                <div class="table-responsive"><table class="table table-sm"><thead><tr><th>Kategori</th><th>Materiel</th><th class="text-end">Antal</th><th>Underlag</th></tr></thead><tbody>
                ${(r.rows || []).map(x => `<tr><td>${esc(x.category)}</td><td>${esc(x.item)}</td><td class="text-end">${x.quantity != null ? esc(x.quantity + (x.unit ? ' ' + x.unit : '')) : '<span class="pl-help">efter behov</span>'}</td>
                    <td class="pl-help small">${esc(x.basis)}</td></tr>`).join('') || '<tr><td colspan="4" class="pl-help">Inga poster.</td></tr>'}</tbody></table></div>`;
        } catch (e) { html = `<div class="alert alert-warning mb-0">${esc(e.message)}</div>`; }
        formModal({ title: 'Materielberäkning', wide: true, cancelLabel: 'Stäng', html });
    }
    async function remindOverdue() {
        const people = new Set(TASKS.tasks.filter(t => t.overdue && t.assigneeId).map(t => t.assigneeId)).size;
        if (!people) { showMsg('Ingen av de försenade uppgifterna har en ansvarig i planeringen att påminna.', 'info'); return; }
        if (!await plConfirm('Påminn om försenade uppgifter?', `${people === 1 ? 'En person' : people + ' personer'} får ett mejl med sina försenade uppgifter. Ett mejl per person och dag.`, 'Skicka')) return;
        const r = await post('RemindOverdue', {});
        if (!r.success) { failMsg(r); return; }
        showMsg(r.sent === r.people ? `${r.sent} ${r.sent === 1 ? 'person har' : 'personer har'} fått en påminnelse.`
            : `${r.sent} av ${r.people} fick en påminnelse. Inte nådda: ${esc((r.failed || []).join(', ')) || 'de som redan påmints i dag eller stängt av notiserna'}.`, r.sent ? 'success' : 'warning');
    }
    document.addEventListener('click', e => {
        const tq = e.target.closest('[data-tk-quickdone]');
        if (tq) { const t = findTask(parseInt(tq.dataset.tkQuickdone, 10)); if (t) { tq.disabled = true; markDone(t); } return; }
        const th = e.target.closest('[data-tk-thread]');
        if (th) { const t = findTask(parseInt(th.dataset.tkThread, 10)); if (t) openThread(t); return; }
        if (e.target.closest('[data-tk-template]')) { openTemplate(); return; }
        const dp = e.target.closest('[data-tk-doc-pick]');
        if (dp) pickDocuments({ attach: { kind: 'Task', id: parseInt(dp.dataset.tkDocPick, 10) }, reopenThread: true });
    });

    // Mötessidans knappar: Klar ✓ / Fortsätter / Ångra / Berätta hur det går / Lägg till / punkterna.
    document.addEventListener('click', async e => {
        const b = e.target.closest('[data-tk-done],[data-tk-continue],[data-tk-undo],[data-tk-report],[data-tk-add],[data-topic-add],[data-topic-del]');
        if (!b) return;
        const meetingId = parseInt(b.dataset.tkMeeting || b.dataset.tkAdd || b.dataset.topicAdd || '0', 10);
        try {
            // Sidan ritas om efter varje knapp — det som just skrivits i anteckningarna sparas först.
            await flushNotes();
            if (b.dataset.tkDone) {
                b.disabled = true;
                const r = await post('DecideTask', { taskId: b.dataset.tkDone, decision: 'done', meetingId });
                if (!r.success) { failMsg(r); b.disabled = false; return; }
                await reloadTasks(); rerender();
            } else if (b.dataset.tkContinue) {
                const t = findTask(parseInt(b.dataset.tkContinue, 10)); if (!t) return;
                formModal({
                    title: 'Fortsätter: ' + t.title, okLabel: 'Fortsätter', wide: false,
                    html: `<p>Uppgiften är inte klar och står kvar till nästa möte.</p>
                        <label class="form-label" for="ctDue">Ny sista dag (valfritt)</label>
                        <input id="ctDue" class="form-control" value="${esc(t.due || '')}" placeholder="åååå-mm-dd">`,
                    onOpen: body => datePicker(body.querySelector('#ctDue')),
                    onOk: async body => {
                        const nd = body.querySelector('#ctDue').value.trim();
                        const r = await post('DecideTask', { taskId: t.id, decision: 'continue', meetingId, newDue: nd !== (t.due || '') ? nd : '' });
                        if (!r.success) return r.message;
                        await reloadTasks(); rerender(); return true;
                    }
                });
            } else if (b.dataset.tkUndo) {
                const t = findTask(parseInt(b.dataset.tkUndo, 10)); if (t) await undoDone(t, meetingId);
            } else if (b.dataset.tkReport) {
                const t = findTask(parseInt(b.dataset.tkReport, 10)); if (t) openReport(t);
            } else if (b.dataset.tkAdd) {
                const title = $('ntTitle' + meetingId).value.trim();
                if (!title) { $('ntTitle' + meetingId).focus(); return; }
                b.disabled = true;
                const r = await post('CreateTask', { title, assignee: $('ntWho' + meetingId).value, due: $('ntDue' + meetingId).value.trim(), meetingId });
                b.disabled = false;
                if (!r.success) { failMsg(r); return; }
                await reloadTasks(); rerender();
                $('ntTitle' + meetingId)?.focus();
                if (r.mailed) showMsg('Uppgiften är tillagd och den som fick den har fått ett mejl.', 'success');
            } else if (b.dataset.topicAdd) {
                const inp = $('plTopic' + meetingId); const text = inp ? inp.value.trim() : '';
                if (!text) { inp?.focus(); return; }
                b.disabled = true;
                const r = await post('AddTopic', { meetingId, text });
                b.disabled = false;
                if (!r.success) { failMsg(r); return; }
                await reloadMeetings(); rerender(); $('plTopic' + meetingId)?.focus();
            } else if (b.dataset.topicDel) {
                const r = await post('RemoveTopic', { topicId: b.dataset.topicDel });
                if (!r.success) { failMsg(r); return; }
                await reloadMeetings(); rerender();
            }
        } catch (er) { failMsg(er); b.disabled = false; }
    });
    // Enter i "Något att prata om" och "Vad ska göras?" lägger till.
    document.addEventListener('keydown', e => {
        if (e.key !== 'Enter') return;
        const ti = e.target.closest('[data-topic-input]');
        if (ti) { e.preventDefault(); document.querySelector(`[data-topic-add="${ti.dataset.topicInput}"]`)?.click(); return; }
        const nt = e.target.closest('[data-nt-title]');
        if (nt) { e.preventDefault(); document.querySelector(`[data-tk-add="${nt.dataset.ntTitle}"]`)?.click(); }
    });

    // ================================================================ Roller
    function openRoles(p) {
        let roles = (p.roles || []).slice();
        const sugg = S.roleSuggestions || [];
        const chips = () => roles.length
            ? roles.map((r, i) => `<span class="pl-role">${esc(r)} <button type="button" class="btn btn-link btn-sm p-0 text-reset" data-role-del="${i}" aria-label="Ta bort ${esc(r)}">✕</button></span>`).join(' ')
            : '<span class="pl-help">Inga roller än.</span>';
        formModal({
            title: 'Roller: ' + p.name, okLabel: 'Spara', wide: false,
            html: `<p class="pl-help">En roll säger vad personen ansvarar för — den ger inga rättigheter (det gör nivån). En person kan ha flera.</p>
                <div id="rlChips" class="d-flex gap-2 flex-wrap mb-3">${chips()}</div>
                <div class="d-flex gap-2"><input type="text" class="form-control" id="rlNew" list="rlList" maxlength="100" placeholder="Välj eller skriv en roll — t.ex. Kassör" aria-label="Ny roll">
                    <button type="button" class="btn btn-outline-primary" id="rlAdd">Lägg till</button></div>
                <datalist id="rlList">${sugg.map(s => `<option value="${esc(s)}"></option>`).join('')}</datalist>
                <p class="pl-help small mt-2 mb-0">Förslagen kommer från samma lista som bemanningens roller, så "Banansvarig" betyder samma sak i planeringen och på tävlingen.</p>`,
            onOpen: body => {
                const add = () => {
                    const v = body.querySelector('#rlNew').value.trim();
                    if (v && !roles.some(r => r.toLowerCase() === v.toLowerCase())) roles.push(v);
                    body.querySelector('#rlNew').value = ''; body.querySelector('#rlChips').innerHTML = chips();
                    body.querySelector('#rlNew').focus();
                };
                body.querySelector('#rlAdd').onclick = add;
                body.querySelector('#rlNew').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); add(); } });
                body.addEventListener('click', e => {
                    const d = e.target.closest('[data-role-del]'); if (!d) return;
                    roles.splice(parseInt(d.dataset.roleDel, 10), 1); body.querySelector('#rlChips').innerHTML = chips();
                });
                body.querySelector('#rlNew').focus();
            },
            onOk: async body => {
                const pending = body.querySelector('#rlNew').value.trim();
                if (pending && !roles.some(r => r.toLowerCase() === pending.toLowerCase())) roles.push(pending);
                const r = await post('SetRoles', { participantId: p.id, roles: roles.join('\n') });
                if (!r.success) return r.message;
                await reloadPeople(); rerender(); return true;
            }
        });
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
            const menu = p.isMe ? rowMenu([{ label: 'Ändra vad jag företräder…', fn: () => openEditParticipant(p) },
                    S.me.canManageRoles && { label: 'Roller…', fn: () => openRoles(p) }]) : rowMenu([
                S.me.isParticipant && { label: 'Kontaktuppgifter…', fn: () => openContact(p) },
                mgr && S.me.isParticipant && { divider: true },
                S.me.canManageRoles && { label: 'Roller…', fn: () => openRoles(p) },
                mgr && { label: 'Ändra nivå och vad hen företräder…', fn: () => openEditParticipant(p) },
                mgr && { label: 'Byt ut mot någon annan…', fn: () => openReplace(p) },
                mgr && !TOKEN && !p.isGuest && p.access !== 'Owner' && { label: 'Lämna över ledningen till ' + p.name + '…', fn: () => handOver(p) },
                mgr && p.isGuest && { label: 'Skicka inbjudan igen', fn: () => resend(p) },
                mgr && { divider: true },
                mgr && { label: 'Ta bort ur planeringen', danger: true, fn: () => removePerson(p) },
            ]);
            const guest = (p.isGuest ? ` <span class="badge rounded-pill text-bg-light border" title="Har inget konto — deltar via länkarna i mejlen">Gäst</span>` : '')
                + (p.viaCompetition ? ` <span class="badge rounded-pill text-bg-light border" title="Har tillgång till tävlingen och kom in utan inbjudan. Ändrar du personens nivå blir hen en vanlig deltagare.">via tävlingen</span>` : '');
            const seen = p.lastSeen ? esc(fmtShort(p.lastSeen)) : (p.inviteSent ? 'Inbjuden ' + esc(fmtShort(p.inviteSent)) + ', har inte öppnat än' : 'Har inte öppnat än');
            const rep = p.represents ? esc(p.represents) : '<span class="pl-help">—</span>';
            const roleChips = (p.roles || []).map(r => `<span class="pl-role">${esc(r)}</span>`).join(' ');
            // Uppdragen i bemanningen — vad personen gör under själva tävlingen.
            const duties = (p.duties || []).length
                ? `<div class="small mt-1"><span class="text-body-secondary">Under tävlingen:</span> ${(p.duties || []).map(esc).join(' · ')}</div>` : '';
            const tasks = p.openTasks ? `<a href="#vy=uppgifter" data-pl-go="uppgifter" class="small">${p.openTasks === 1 ? '1 öppen uppgift' : p.openTasks + ' öppna uppgifter'}</a>` : '';
            return `<tr><td><b>${esc(p.name)}</b>${p.isMe ? ' (du)' : ''}${guest}
                    ${p.guestEmail ? `<div class="pl-help small">${esc(p.guestEmail)}</div>` : ''}
                    <div class="d-sm-none mt-1">${levelBadge(p)}</div>
                    ${roleChips ? `<div class="d-lg-none mt-1 d-flex gap-1 flex-wrap">${roleChips}</div>` : ''}
                    <div class="d-md-none small mt-1">${p.represents ? 'Företräder ' + esc(p.represents) + ' · ' : ''}${p.lastSeen ? 'Senast inne ' : ''}${seen}</div>
                    ${duties}${tasks ? `<div class="mt-1">${tasks}</div>` : ''}</td>
                <td class="d-none d-md-table-cell">${rep}</td>
                <td class="d-none d-sm-table-cell">${levelBadge(p)}</td>
                <td class="d-none d-lg-table-cell">${roleChips ? `<div class="d-flex gap-1 flex-wrap">${roleChips}</div>` : '<span class="pl-help">—</span>'}</td>
                <td class="d-none d-md-table-cell small">${seen}</td>
                <td class="text-end">${menu}</td></tr>`;
        };
        const shown = () => {
            const q = pplQuery.trim().toLowerCase();
            return S.participants.filter(p =>
                (!q || [p.name, p.represents, p.guestEmail, (p.roles || []).join(' '), (p.duties || []).join(' ')].some(v => (v || '').toLowerCase().includes(q)))
                && (!pplFilter || (pplFilter === 'guest' ? p.isGuest : pplFilter.startsWith('r:') ? (p.represents || '') === pplFilter.slice(2) : p.access === pplFilter)))
                .sort((a, b) => (LEVEL_RANK[b.access] || 0) - (LEVEL_RANK[a.access] || 0) || a.name.localeCompare(b.name, 'sv'));
        };
        const renderRows = () => {
            const rows = shown();
            $('plPplRows').innerHTML = rows.length ? rows.map(rowFor).join('')
                : '<tr><td colspan="6" class="pl-help">Ingen deltagare matchar.</td></tr>';
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
                <input type="search" class="form-control form-control-sm" id="plPplQ" placeholder="Sök på namn, roll eller vad man företräder" style="max-width:20rem" value="${esc(pplQuery)}">
                ${filter}<span class="ms-auto pl-help small" id="plPplCount"></span></div>
            <div class="table-responsive"><table class="table align-middle mb-0 pl-ppl"><thead><tr>
                <th>Namn</th><th class="d-none d-md-table-cell">Företräder</th><th class="d-none d-sm-table-cell">Nivå</th><th class="d-none d-lg-table-cell">Roller</th><th class="d-none d-md-table-cell">Senast inne</th><th></th>
            </tr></thead><tbody id="plPplRows"></tbody></table></div>`;
        const warn = S.me.canManage && conveners.length === 1 && S.participants.length > 1
            ? `<div class="alert alert-warning d-flex gap-2 align-items-start"><i class="bi bi-exclamation-triangle mt-1"></i>
                <div>Det finns bara en <b>sammankallande</b>${conveners[0].isMe ? ' — du' : ''}. Utse gärna en till, så att gruppen inte står utan om hen blir sjuk eller slutar:
                välj <i>Ändra nivå</i> på personen.</div></div>` : '';
        $('plBody').innerHTML = `<div id="plReqs"></div>${warn}<div class="pl-card"><p class="pl-help">Du bjuder in <b>medlemmar</b> genom att söka på namnet — ingen behöver känna till någon annans e-postadress.
            Den som inte är skytt (t.ex. någon från kommunen eller markägaren) bjuds in med e-post och deltar via länkarna i mejlen.
            <b>Nivån</b> styr vad man får göra på sidan; <b>rollerna</b> säger vad man ansvarar för och ger inga rättigheter.</p>${list}</div>
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
    // Lämna över (P3): den andra blir sammankallande, jag blir Kan ändra — i ett steg.
    async function handOver(p) {
        if (!await plConfirm('Lämna över ledningen?', `${p.name} blir sammankallande. Du blir Kan ändra och är kvar i planeringen, men kan inte längre arkivera, ta bort eller ändra andras nivå.`, 'Lämna över')) return;
        const r = await post('HandOver', { participantId: p.id });
        if (!r.success) { failMsg(r); return; }
        await reloadPeople(); render(view);
        showMsg(`${esc(p.name)} är nu sammankallande.`, 'success');
    }

    // Kopiera planeringen (P3): formen följer med, aldrig personerna eller innehållet.
    function openCopyPlanning() {
        const p = S.planning;
        let chosen = 0, chosenName = '', timer = 0;
        formModal({
            title: 'Kopiera planeringen', okLabel: 'Kopiera', wide: true,
            html: `<p class="mb-2">Kopian får samma <b>områden, uppgifter och milstolpar, delar</b> och bemanningens <b>roller, dagar och behov</b>.
                    Den får <b>inte</b> med deltagarna, meddelandena, mötena, dokumenten, förslagen eller vem som gör vad.</p>
                <label class="form-label" for="cpName">Namn på kopian</label>
                <input id="cpName" class="form-control mb-3" maxlength="200" value="${esc((p.name.length > 190 ? p.name.slice(0, 190) : p.name) + ' (kopia)')}">
                <div class="row g-2 mb-1"><div class="col-sm-6"><label class="form-label" for="cpStart">Första dagen</label><input id="cpStart" class="form-control" placeholder="ÅÅÅÅ-MM-DD"></div>
                    <div class="col-sm-6"><label class="form-label" for="cpEnd">Sista dagen</label><input id="cpEnd" class="form-control" placeholder="ÅÅÅÅ-MM-DD"></div></div>
                <div class="pl-help small mb-3">Datumen flyttas med till kopians första dag: bemanningens dagar behåller veckodagen, och en frist ett år senare behåller sitt datum (1 oktober blir 1 oktober). Utan första dag följer fasta datum och dagar inte med — "N dagar före" följer alltid med.</div>
                <div class="fw-semibold mb-1">Vem ska leda kopian?</div>
                <div class="form-check"><input class="form-check-input" type="radio" name="cpWho" id="cpMe" value="me" checked><label class="form-check-label" for="cpMe">Jag</label></div>
                <div class="form-check mb-2"><input class="form-check-input" type="radio" name="cpWho" id="cpOther" value="other"><label class="form-check-label" for="cpOther">En annan medlem — hen blir sammankallande och får ett mejl</label></div>
                <div id="cpOtherBox" class="d-none ms-4">
                    <input id="cpQ" class="form-control mb-2" placeholder="Sök på namn" autocomplete="off">
                    <div id="cpHits" class="pl-help small mb-2">Skriv minst två bokstäver.</div>
                    <div class="form-check"><input class="form-check-input" type="checkbox" id="cpIncludeMe" checked><label class="form-check-label" for="cpIncludeMe">Jag är med i kopian som Kan ändra</label></div>
                </div>`,
            onOpen: body => {
                datePicker(body.querySelector('#cpStart')); datePicker(body.querySelector('#cpEnd'));
                body.querySelectorAll('input[name="cpWho"]').forEach(r => r.addEventListener('change', () =>
                    body.querySelector('#cpOtherBox').classList.toggle('d-none', !body.querySelector('#cpOther').checked)));
                const q = body.querySelector('#cpQ');
                q.addEventListener('input', () => {
                    clearTimeout(timer);
                    timer = setTimeout(async () => {
                        const v = q.value.trim();
                        if (v.length < 2) { body.querySelector('#cpHits').textContent = 'Skriv minst två bokstäver.'; return; }
                        const r = await get('SearchMembers', { q: v });
                        if (!r.success) { body.querySelector('#cpHits').textContent = r.message || 'Sökningen gick inte.'; return; }
                        body._cpHits = r.members;
                        body.querySelector('#cpHits').innerHTML = r.members.length ? r.members.map((m, i) =>
                            `<div class="pl-person"><div><b>${esc(m.name)}</b><div class="small">${esc(m.club)}</div></div>
                            <button type="button" class="btn btn-outline-primary btn-sm" data-cp-pick="${m.memberId}" data-cp-i="${i}">Välj</button></div>`).join('')
                            : 'Ingen medlem hittades.';
                    }, 250);
                });
                body.addEventListener('click', ev => {
                    const b = ev.target.closest('[data-cp-pick]'); if (!b) return;
                    chosen = parseInt(b.dataset.cpPick, 10);
                    chosenName = (body._cpHits || [])[parseInt(b.dataset.cpI, 10)]?.name || '';
                    body.querySelectorAll('[data-cp-pick]').forEach(x => { x.classList.remove('btn-primary'); x.classList.add('btn-outline-primary'); x.textContent = 'Välj'; });
                    b.classList.remove('btn-outline-primary'); b.classList.add('btn-primary'); b.textContent = 'Vald';
                });
            },
            onOk: async body => {
                const other = body.querySelector('#cpOther').checked;
                if (other && !chosen) return 'Sök fram och välj den som ska leda kopian.';
                const r = await post('CopyPlanning', {
                    name: body.querySelector('#cpName').value.trim(), targetStart: body.querySelector('#cpStart').value.trim(),
                    targetEnd: body.querySelector('#cpEnd').value.trim(), recipientMemberId: other ? chosen : 0,
                    includeMe: other && !body.querySelector('#cpIncludeMe').checked ? '0' : '1',
                });
                if (!r.success) return r.message || 'Det gick inte att kopiera.';
                const lost = [];
                if (r.datesDropped) lost.push(`${r.datesDropped} fasta datum följde inte med (ingen första dag att räkna från)`);
                if (r.daysDropped) lost.push(`${r.daysDropped} av bemanningens dagar följde inte med`);
                const what = `${r.tasks} uppgifter i ${r.areas} områden, ${r.roles} roller, ${r.days} dagar och ${r.needs} behov`;
                showMsg(`Kopian <a href="${esc(r.url)}">${esc(body.querySelector('#cpName').value.trim())}</a> är skapad med ${what}.`
                    + (lost.length ? ' ' + esc(lost.join('; ')) + '.' : '')
                    + (r.toSomeoneElse ? ` ${esc(r.recipient)} är sammankallande` + (r.mailed ? ' och har fått ett mejl.' : ' — men mejlet kunde inte skickas, säg till hen.') : ''),
                    lost.length || (r.toSomeoneElse && !r.mailed) ? 'warning' : 'success');
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
            !TOKEN && { label: 'Kopiera planeringen…', icon: 'bi-copy', fn: openCopyPlanning },
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
            <button class="btn btn-primary" id="stSave">Spara</button> <span id="stSaved" class="pl-help ms-2"></span></div>
            ${S.me.canManage ? `<div class="pl-card" style="max-width:720px"><h2>Uppgifterna</h2>
                <div class="form-check form-switch"><input class="form-check-input" type="checkbox" role="switch" id="stDoneMeet"${p.doneAtMeetingsOnly ? ' checked' : ''}>
                <label class="form-check-label fw-semibold" for="stDoneMeet">Om en uppgift är utförd beslutas på mötena</label>
                <div class="pl-help small">På: den som har uppgiften säger <i>Klar att pröva</i>, och mötet (eller du som sammankallande) bestämmer att den är utförd.
                    Av: den som har uppgiften, och den som kan ändra planeringen, bockar av den direkt — bra när en person planerar en klubbtävling ensam.</div></div>
                <span id="stDoneSaved" class="pl-help small"></span></div>` : ''}
            <div class="pl-card" style="max-width:720px"><h2>Gren</h2>
                ${p.competition
                    ? `<p class="mb-0">Bemanningens roller följer tävlingens gren: <strong>${esc((p.disciplines.find(d => d.id === p.competition.typeId) || {}).name || p.competition.typeId || '')}</strong>.</p>`
                    : `<label class="form-label" for="stDisc">Vilken gren gäller planeringen?</label>
                       <select id="stDisc" class="form-select" style="max-width:320px"><option value="">Inte bestämt än</option>
                       ${p.disciplines.map(d => `<option value="${esc(d.id)}"${d.id === p.disciplineKey ? ' selected' : ''}>${esc(d.name)}</option>`).join('')}</select>
                       <div class="pl-help small mt-1">Styr vilka roller bemanningen föreslår (till exempel stationschef i fältskytte). När planeringen kopplas till en tävling gäller tävlingens gren.</div>
                       <span id="stDiscSaved" class="pl-help small"></span>`}</div>
            ${S.me.isSiteAdmin ? `<div class="pl-card" style="max-width:720px"><h2>Förbundets adress</h2>
                <p class="pl-help small">Gäller alla planeringar: dit går milstolpar som skickas till förbundet (till exempel inbjudan i tid till tidningen). Syns bara för sajtadministratörer.</p>
                <div class="d-flex gap-2 flex-wrap"><input id="stForbundet" type="email" class="form-control" style="max-width:320px" maxlength="256" value="${esc((TASKS && TASKS.forbundetEmail) || '')}" placeholder="namn@pistolskytteforbundet.se">
                <button class="btn btn-outline-primary" id="stForbundetSave">Spara adressen</button></div><span id="stForbundetSaved" class="pl-help small"></span></div>` : ''}
            ${p.competition ? `<div class="pl-card" style="max-width:720px"><h2>Tävlingen</h2>
                <p class="mb-2">Planeringen hör till <a href="${esc(p.competition.url)}">${esc(p.competition.name)}</a>. Den som har tillgång till tävlingen — tävlingsledning,
                    klubbens administratörer och skjutledare, kretsens administratörer — kommer in med nivån <i>Kan ändra</i> utan att bjudas in.</p>
                ${S.me.canLinkCompetition ? '<button class="btn btn-outline-danger btn-sm" id="stUnlink">Koppla loss från tävlingen…</button>' : ''}</div>` : ''}`;
        datePicker($('stStart')); datePicker($('stEnd'));
        $('stDisc')?.addEventListener('change', async e => {
            const r = await post('SetDiscipline', { value: e.target.value });
            if (!r.success) { failMsg(r); e.target.value = S.planning.disciplineKey || ''; return; }
            S.planning.disciplineKey = e.target.value; $('stDiscSaved').textContent = 'Sparat.';
        });
        $('stDoneMeet')?.addEventListener('change', async e => {
            const r = await post('SetDoneAtMeetingsOnly', { value: e.target.checked ? '1' : '0' });
            if (!r.success) { failMsg(r); e.target.checked = !e.target.checked; return; }
            S.planning.doneAtMeetingsOnly = e.target.checked;
            await reloadTasks(); $('stDoneSaved').textContent = 'Sparat.';
        });
        $('stForbundetSave')?.addEventListener('click', async () => {
            const r = await post('SetForbundetEmail', { email: $('stForbundet').value.trim() });
            if (!r.success) { failMsg(r); return; }
            await reloadTasks(); $('stForbundetSaved').textContent = r.email ? 'Sparat.' : 'Adressen är borttagen.';
        });
        $('stUnlink')?.addEventListener('click', async () => {
            if (!await plConfirm('Koppla loss från tävlingen?', 'Uppgifterna står kvar. De som bara var med genom tävlingen kommer inte längre in, och förberedelserna syns inte längre på tävlingens sida.', 'Koppla loss', true)) return;
            const r = await post('UnlinkCompetition', {});
            if (!r.success) { failMsg(r); return; }
            await reloadPeople(); render('installningar');
        });
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

    // ================================================================ Förslag och beslut (P3)
    // Ett förslag läggs fram, de som deltar svarar Vi kan / Vi kan inte, och beslutet registreras här —
    // som arbetsgruppens beslut eller som en styrelses. Ett förslag kan skickas till styrelsen i en klubb
    // eller krets som någon i planeringen företräder; styrelsens läge läses ur deras ärendekö.
    async function reloadDecisions() { const r = await get('Decisions'); if (r.success) DEC = r; }
    function findDecision(id) { return DEC.decisions.find(d => d.id === id); }

    function decisionAnswers(d) {
        const can = d.responses.filter(r => r.answer === 'Can'), cannot = d.responses.filter(r => r.answer === 'Cannot');
        const who = r => esc(r.name) + (r.represents ? ` <span class="pl-help">(${esc(r.represents)})</span>` : '')
            + (r.beforeEdit ? ' <span class="pl-help small">· svarade före ändringen</span>' : '')
            + (r.comment ? `<div class="pl-help small ms-3" style="white-space:pre-wrap;">${esc(r.comment)}</div>` : '');
        const list = (rows, label, icon) => rows.length
            ? `<div class="mb-1"><i class="bi ${icon} me-1" aria-hidden="true"></i><b>${label} (${rows.length})</b><ul class="mb-1 ps-4">${rows.map(r => `<li>${who(r)}</li>`).join('')}</ul></div>` : '';
        return list(can, 'Vi kan', 'bi-check-circle text-success') + list(cannot, 'Vi kan inte', 'bi-x-circle text-danger')
            + (d.waiting && d.waiting.length ? `<div class="pl-help small">Väntar på svar från ${esc(d.waiting.join(', '))}.</div>` : '')
            + (!d.responses.length && !(d.waiting || []).length ? '<div class="pl-help small">Inga svar ännu.</div>' : '');
    }

    function boardBlock(d) {
        const b = d.board; if (!b) return '';
        const tone = b.state === 'handled' ? 'success' : b.state === 'rejected' || b.state === 'gone' || b.state === 'withdrawn' ? 'secondary' : 'info';
        return `<div class="alert alert-${tone} py-2 mb-2"><div><i class="bi bi-people me-1" aria-hidden="true"></i><b>Styrelsen i ${esc(b.org)}</b>
            <span class="pl-help small">· skickat ${esc(fmtShort(b.sentAt))}${b.sentBy ? ' av ' + esc(b.sentBy) : ''}</span></div>
            <div>${esc(b.label)}</div>${b.decision ? `<div class="mt-1"><b>Styrelsens beslut:</b> <span style="white-space:pre-wrap;">${esc(b.decision)}</span></div>` : ''}</div>`;
    }

    function decisionCard(d) {
        const open = d.status === 'Open';
        const menu = rowMenu([
            d.canEdit && { label: 'Ändra förslaget…', fn: () => openDecisionEdit(d) },
            d.canSendToBoard && { label: d.board ? 'Skicka till en styrelse igen…' : 'Skicka till en styrelse…', fn: () => openSendToBoard(d) },
            d.canDecide && { label: 'Registrera beslutet…', fn: () => openDecide(d) },
            d.canReopen && { label: 'Ångra beslutet', fn: () => reopenDecision(d) },
            d.canWithdraw && { divider: true },
            d.canWithdraw && { label: 'Dra tillbaka förslaget…', danger: true, fn: () => withdrawDecision(d) },
        ]);
        const state = open ? '<span class="pl-state blue">Öppet</span>'
            : d.status === 'Decided' ? `<span class="pl-state ${d.outcome === 'Rejected' ? 'off' : 'ok'}">${esc(d.outcomeLabel)}</span>`
            : '<span class="pl-state off">Tillbakadraget</span>';
        const decided = d.status === 'Decided' ? `<div class="mb-2"><b>${esc(d.outcomeLabel)}</b> — ${d.decidedByKind === 'Board' ? 'beslutat av ' + esc(d.decidedBoardName) : 'arbetsgruppens beslut'}
            <span class="pl-help small">· ${esc(fmtShort(d.decidedAt))}${d.decidedBy ? ', registrerat av ' + esc(d.decidedBy) : ''}</span>
            ${d.decisionNote ? `<div style="white-space:pre-wrap;">${esc(d.decisionNote)}</div>` : ''}</div>` : '';
        let answer = '';
        if (d.canRespond) {
            const m = d.mine;
            answer = `<div class="pl-noprint border-top pt-2 mt-2"><div class="fw-semibold mb-1">Ditt svar</div>
                ${m && m.beforeEdit ? '<div class="alert alert-warning py-1 px-2 small mb-2">Förslaget ändrades efter att du svarade. Svara gärna igen.</div>' : ''}
                <input type="text" class="form-control form-control-sm mb-2" id="decC${d.id}" maxlength="1000" placeholder="Kommentar (valfri)" value="${esc(m ? m.comment || '' : '')}" aria-label="Kommentar till ditt svar">
                <div class="d-flex gap-2 flex-wrap">
                    <button class="btn btn-sm ${m && m.answer === 'Can' ? 'btn-success' : 'btn-outline-success'}" data-dec-answer="${d.id}" data-answer="Can"><i class="bi bi-check-lg me-1"></i>Vi kan</button>
                    <button class="btn btn-sm ${m && m.answer === 'Cannot' ? 'btn-danger' : 'btn-outline-danger'}" data-dec-answer="${d.id}" data-answer="Cannot"><i class="bi bi-x-lg me-1"></i>Vi kan inte</button>
                    ${m ? `<span class="pl-help small align-self-center">Du har svarat ${m.answer === 'Can' ? 'Vi kan' : 'Vi kan inte'}. Tryck igen för att ändra.</span>` : ''}
                </div></div>`;
        }
        return `<div class="pl-card" data-dec-id="${d.id}">
            <div class="d-flex justify-content-between gap-2 align-items-start"><div><h2 class="mb-1">${esc(d.title)}</h2>
                <div class="pl-help small">${state} Föreslaget av ${esc(d.proposedBy)} ${esc(fmtShort(d.proposedAt))}${d.editedAt ? ' · ändrat ' + esc(fmtShort(d.editedAt)) : ''}</div></div>${menu}</div>
            ${d.body ? `<div class="mt-2 mb-2" style="white-space:pre-wrap;">${esc(d.body)}</div>` : ''}
            ${decided}${boardBlock(d)}
            <div class="mt-2">${decisionAnswers(d)}</div>${answer}</div>`;
    }

    function viewDecisions() {
        setHead('Förslag och beslut', esc(S.planning.name));
        setActions([DEC.canPropose && { label: 'Nytt förslag…', icon: 'bi-lightbulb', fn: () => openDecisionEdit(null) }]);
        const all = DEC.decisions;
        const open = all.filter(d => d.status === 'Open'), decided = all.filter(d => d.status === 'Decided'), withdrawn = all.filter(d => d.status === 'Withdrawn');
        const html = [`<p class="pl-help">Lägg fram ett förslag. De som deltar svarar <i>Vi kan</i> eller <i>Vi kan inte</i>, och beslutet registreras här —
            som arbetsgruppens beslut, eller som ett beslut av en styrelse. Ett förslag kan skickas till styrelsen i en klubb eller krets som någon i planeringen företräder.</p>`];
        if (!all.length) {
            html.push(`<div class="pl-card"><p class="mb-2"><b>Inga förslag ännu.</b></p>
                <p class="mb-${DEC.canPropose ? '3' : '0'}">Till exempel: "Varbergs PK bemannar station 3–5" eller "Vi använder Skrea skjutbana för fältet".</p>
                ${DEC.canPropose ? '<button class="btn btn-primary" data-dec-new="1"><i class="bi bi-lightbulb me-1"></i>Nytt förslag…</button>' : ''}</div>`);
        } else {
            if (open.length) html.push(`<h2 class="h6 mt-2">Öppna förslag (${open.length})</h2>` + open.map(decisionCard).join(''));
            if (decided.length) html.push(`<h2 class="h6 mt-3">Avgjorda (${decided.length})</h2>` + decided.map(decisionCard).join(''));
            if (withdrawn.length) html.push(`<details class="mt-3"><summary class="pl-help">Tillbakadragna förslag (${withdrawn.length})</summary>${withdrawn.map(decisionCard).join('')}</details>`);
        }
        $('plBody').innerHTML = html.join('');
    }
    document.addEventListener('click', e => { if (e.target.closest('[data-dec-new]')) { e.preventDefault(); openDecisionEdit(null); } });
    document.addEventListener('click', async e => {
        const b = e.target.closest('[data-dec-answer]'); if (!b) return;
        e.preventDefault();
        const id = parseInt(b.dataset.decAnswer, 10);
        const c = $('decC' + id);
        b.disabled = true;
        try {
            const r = await post('RespondDecision', { decisionId: id, answer: b.dataset.answer, comment: c ? c.value : '' });
            if (!r.success) { failMsg(r); return; }
            await reloadDecisions(); render(view);
        } catch (err) { showMsg(esc(err.message), 'danger'); }
        finally { b.disabled = false; }
    });

    function openDecisionEdit(d) {
        const answered = d ? d.responses.length : 0;
        formModal({
            title: d ? 'Ändra förslaget' : 'Nytt förslag', okLabel: d ? 'Spara' : 'Lägg fram förslaget', wide: false,
            html: `<label class="form-label" for="deTitle">Förslaget</label>
                <input id="deTitle" class="form-control mb-1" maxlength="300" value="${esc(d ? d.title : '')}" placeholder="Till exempel: Varbergs PK bemannar station 3–5">
                <div class="pl-help small mb-3">En mening som går att svara Vi kan eller Vi kan inte på.</div>
                <label class="form-label" for="deBody">Bakgrund (valfri)</label>
                <textarea id="deBody" class="form-control" rows="5" maxlength="4000">${esc(d ? d.body || '' : '')}</textarea>
                ${answered ? `<div class="alert alert-warning small mt-3 mb-0">${answered === 1 ? 'En har' : answered + ' har'} redan svarat. Svaren står kvar men märks som givna före ändringen.</div>`
                    : '<p class="pl-help small mt-3 mb-0">Gruppen ser förslaget i meddelandena och under Förslag och beslut.</p>'}`,
            onOpen: body => body.querySelector('#deTitle').focus(),
            onOk: async body => {
                const p = { title: body.querySelector('#deTitle').value.trim(), body: body.querySelector('#deBody').value };
                if (!p.title) return 'Skriv vad förslaget gäller.';
                const r = d ? await post('UpdateDecision', Object.assign({ decisionId: d.id }, p)) : await post('CreateDecision', p);
                if (!r.success) return r.message || 'Det gick inte att spara.';
                await reloadDecisions(); await reloadMessages(); go('beslut');
                return true;
            }
        });
    }

    function openSendToBoard(d) {
        const targets = DEC.boardTargets || [];
        formModal({
            title: 'Skicka till en styrelse', okLabel: 'Skicka', wide: false,
            html: `<p class="mb-2"><b>${esc(d.title)}</b></p>
                <label class="form-label" for="sbTarget">Styrelsen i</label>
                <select id="sbTarget" class="form-select mb-1">${targets.map((t, i) => `<option value="${i}">${esc(t.name)}</option>`).join('')}</select>
                <div class="pl-help small mb-3">Klubbarna och kretsarna som någon i planeringen företräder.</div>
                <label class="form-label" for="sbKind">Vad vill ni att styrelsen gör?</label>
                <select id="sbKind" class="form-select mb-3"><option value="Beslut">Fatta ett beslut</option><option value="Information">Ta del av det (information)</option><option value="Diskussion">Diskutera det</option></select>
                <p class="pl-help small mb-0">Förslaget läggs i styrelsens ärendekö, med bakgrunden och svaren hittills. Sekreteraren och ordföranden får ett mejl.
                    Du får besked när ärendet placerats på ett möte och när protokollet är justerat — och läget syns här.</p>`,
            onOk: async body => {
                const t = targets[parseInt(body.querySelector('#sbTarget').value, 10)];
                if (!t) return 'Välj en styrelse.';
                const r = await post('SendDecisionToBoard', { decisionId: d.id, ownerType: t.ownerType, ownerId: t.ownerId, kind: body.querySelector('#sbKind').value });
                if (!r.success) return r.message || 'Det gick inte att skicka.';
                await reloadDecisions(); await reloadMessages(); render(view);
                showMsg(`Skickat till styrelsen i ${esc(r.org)}.` + (r.notified ? '' : ' Ingen i styrelsen kunde nås med e-post — hör av dig till dem.'), r.notified ? 'success' : 'warning');
                return true;
            }
        });
    }

    function openDecide(d) {
        const targets = DEC.boardTargets || [];
        const b = d.board, handled = b && b.state === 'handled';
        formModal({
            title: 'Registrera beslutet', okLabel: 'Registrera', wide: false,
            html: `<p class="mb-2"><b>${esc(d.title)}</b></p>
                <div class="mb-3"><div class="form-check"><input class="form-check-input" type="radio" name="dcOut" id="dcOutA" value="Approved" checked><label class="form-check-label" for="dcOutA">Beslutat</label></div>
                    <div class="form-check"><input class="form-check-input" type="radio" name="dcOut" id="dcOutR" value="Rejected"><label class="form-check-label" for="dcOutR">Avslaget</label></div></div>
                <div class="fw-semibold mb-1">Vem beslutade?</div>
                <div class="mb-2"><div class="form-check"><input class="form-check-input" type="radio" name="dcBy" id="dcByG" value="Group"${handled ? '' : ' checked'}><label class="form-check-label" for="dcByG">Arbetsgruppen (planeringen)</label></div>
                    <div class="form-check"><input class="form-check-input" type="radio" name="dcBy" id="dcByB" value="Board"${handled ? ' checked' : ''}><label class="form-check-label" for="dcByB">En styrelse</label></div></div>
                <div id="dcBoardBox" class="mb-3 ms-4${handled ? '' : ' d-none'}">
                    <input id="dcBoard" class="form-control" list="dcBoardList" maxlength="300" placeholder="Till exempel: Styrelsen i Varbergs PK" value="${esc(b ? 'Styrelsen i ' + b.org : '')}">
                    <datalist id="dcBoardList">${targets.map(t => `<option value="${esc('Styrelsen i ' + t.name)}"></option>`).join('')}</datalist></div>
                <label class="form-label" for="dcNote">Beslutet (valfritt)</label>
                <textarea id="dcNote" class="form-control" rows="4" maxlength="4000">${esc(handled && b.decision ? b.decision : '')}</textarea>
                <p class="pl-help small mt-2 mb-0">Gruppen ser beslutet här och i meddelandena. Ett beslut kan ångras.</p>`,
            onOpen: body => body.querySelectorAll('input[name="dcBy"]').forEach(r => r.addEventListener('change', () =>
                body.querySelector('#dcBoardBox').classList.toggle('d-none', !body.querySelector('#dcByB').checked))),
            onOk: async body => {
                const maker = body.querySelector('input[name="dcBy"]:checked').value;
                const boardName = body.querySelector('#dcBoard').value.trim();
                if (maker === 'Board' && !boardName) return 'Skriv vilken styrelse som beslutade.';
                const r = await post('DecideDecision', { decisionId: d.id, outcome: body.querySelector('input[name="dcOut"]:checked').value,
                    maker, boardName, note: body.querySelector('#dcNote').value });
                if (!r.success) return r.message || 'Det gick inte att registrera beslutet.';
                await reloadDecisions(); await reloadMessages(); render(view);
                return true;
            }
        });
    }

    async function reopenDecision(d) {
        if (!await plConfirm('Ångra beslutet?', `"${d.title}" blir ett öppet förslag igen. Svaren står kvar.`, 'Ångra beslutet')) return;
        const r = await post('ReopenDecision', { decisionId: d.id });
        if (!r.success) { failMsg(r); return; }
        await reloadDecisions(); await reloadMessages(); render(view);
    }
    async function withdrawDecision(d) {
        if (!await plConfirm('Dra tillbaka förslaget?', `"${d.title}" står kvar under Tillbakadragna förslag, men går inte längre att svara på.`, 'Dra tillbaka', true)) return;
        const r = await post('WithdrawDecision', { decisionId: d.id });
        if (!r.success) { failMsg(r); return; }
        await reloadDecisions(); await reloadMessages(); render(view);
    }

    // ================================================================ Delar (P3 steg D)
    // Delarna är ORGANISATION: en gren eller ett ansvar, med en ansvarig och en värd. Uppgifter och meddelanden kan
    // märkas med en del. Bemanningen och kopplingen gäller fortfarande hela planeringen.
    function viewParts() {
        setHead('Delar', esc(S.planning.name));
        setActions([S.me.canEdit && !TOKEN && { label: 'Ny del…', icon: 'bi-plus-lg', fn: () => openPart(null) }]);
        const list = parts();
        const html = [`<p class="pl-help">Dela upp planeringen i <b>grenar</b> (precision, fält, springskytte …) och <b>ansvar</b> (sekretariat, kiosk, prisutdelning …).
            Varje del har en ansvarig och en värd — den klubb eller krets som står för den. Uppgifter och meddelanden kan märkas med en del och filtreras på den.</p>`];
        if (!list.length) {
            html.push(`<div class="pl-card"><p class="mb-2"><b>Inga delar ännu.</b></p>
                <p class="mb-${S.me.canEdit ? '3' : '0'}">En SM-vecka kan till exempel ha delarna Precision, Fält och Sekretariatet, med var sin arrangerande klubb.</p>
                ${S.me.canEdit && !TOKEN ? '<button class="btn btn-primary" data-part-new="1"><i class="bi bi-plus-lg me-1"></i>Ny del…</button>' : ''}</div>`);
        } else {
            html.push(`<div class="pl-card p-0"><div class="table-responsive"><table class="table align-middle mb-0">
                <thead><tr><th class="ps-3">Del</th><th class="d-none d-md-table-cell">Ansvarig</th><th class="d-none d-md-table-cell">Värd</th><th>Uppgifter</th><th class="pl-noprint"></th></tr></thead>
                <tbody>${list.map(p => {
                    const menu = S.me.canEdit && !TOKEN ? rowMenu([
                        { label: 'Ändra delen…', fn: () => openPart(p) },
                        { label: 'Visa delens uppgifter', fn: () => { tkPart = String(p.id); tkFilter = 'all'; go('uppgifter'); } },
                        S.me.canReadMessages && { label: 'Visa delens meddelanden', fn: () => { msgPart = String(p.id); go('meddelanden'); } },
                        { divider: true },
                        { label: 'Ta bort delen…', danger: true, fn: () => removePart(p) },
                    ]) : '';
                    const sub = [p.kindLabel + (p.discipline ? ': ' + p.discipline : ''), p.responsible ? 'Ansvarig: ' + p.responsible : '', p.host ? 'Värd: ' + p.host : ''].filter(Boolean);
                    return `<tr data-part-row="${p.id}"><td class="ps-3"><b>${esc(p.name)}</b><div class="pl-help small">${esc(sub[0])}</div>
                            <div class="pl-help small d-md-none">${esc(sub.slice(1).join(' · '))}</div>
                            ${p.description ? `<div class="small mt-1" style="white-space:pre-wrap;">${esc(p.description)}</div>` : ''}</td>
                        <td class="d-none d-md-table-cell">${esc(p.responsible || '—')}</td><td class="d-none d-md-table-cell">${esc(p.host || '—')}</td>
                        <td><a href="#vy=uppgifter" data-part-tasks="${p.id}">${p.openTasks} öppna</a><div class="pl-help small">av ${p.tasks}</div></td>
                        <td class="text-end pe-3">${menu}</td></tr>`;
                }).join('')}</tbody></table></div></div>
                <p class="pl-help small mt-2">Bemanningen och kopplingen till en tävling gäller hela planeringen.</p>`);
        }
        $('plBody').innerHTML = html.join('');
    }
    document.addEventListener('click', e => { if (e.target.closest('[data-part-new]')) { e.preventDefault(); openPart(null); } });
    document.addEventListener('click', e => {
        const a = e.target.closest('[data-part-tasks]'); if (!a) return;
        e.preventDefault(); tkPart = a.dataset.partTasks; tkFilter = 'open'; go('uppgifter');
    });

    async function openPart(p) {
        const o = await get('PartHostOptions');
        if (!o.success) { failMsg(o); return; }
        const hostVal = p ? (p.hostClubId ? '0:' + p.hostClubId : p.hostRegionId ? '1:' + p.hostRegionId : (p.hostText ? 'text' : '')) : '';
        const people = S.participants;
        formModal({
            title: p ? 'Ändra delen' : 'Ny del', okLabel: p ? 'Spara' : 'Lägg till', wide: false,
            html: `<label class="form-label" for="ptName">Namn</label>
                <input id="ptName" class="form-control mb-3" maxlength="200" value="${esc(p ? p.name : '')}" placeholder="Till exempel: Fält eller Sekretariatet">
                <div class="mb-3"><div class="form-check form-check-inline"><input class="form-check-input" type="radio" name="ptKind" id="ptKindD" value="Gren"${!p || p.kind === 'Gren' ? ' checked' : ''}><label class="form-check-label" for="ptKindD">En gren</label></div>
                    <div class="form-check form-check-inline"><input class="form-check-input" type="radio" name="ptKind" id="ptKindR" value="Ansvar"${p && p.kind === 'Ansvar' ? ' checked' : ''}><label class="form-check-label" for="ptKindR">Ett ansvar</label></div></div>
                <div id="ptDiscBox" class="mb-3${p && p.kind === 'Ansvar' ? ' d-none' : ''}"><label class="form-label" for="ptDisc">Gren</label>
                    <select id="ptDisc" class="form-select"><option value="">—</option>${o.disciplines.map(d => `<option value="${esc(d.id)}"${p && p.disciplineKey === d.id ? ' selected' : ''}>${esc(d.name)}</option>`).join('')}</select></div>
                <label class="form-label" for="ptResp">Ansvarig</label>
                <select id="ptResp" class="form-select mb-1"><option value="">Ingen än</option>${people.map(x => `<option value="${x.id}"${p && p.responsibleId === x.id ? ' selected' : ''}>${esc(x.name)}</option>`).join('')}
                    <option value="__text"${p && !p.responsibleId && p.responsibleName ? ' selected' : ''}>Någon som inte är med (skriv namnet)</option></select>
                <input id="ptRespName" class="form-control mb-3${p && !p.responsibleId && p.responsibleName ? '' : ' d-none'}" maxlength="200" value="${esc(p && !p.responsibleId ? p.responsibleName || '' : '')}" placeholder="Namn">
                <label class="form-label mt-2" for="ptHost">Värd</label>
                <select id="ptHost" class="form-select mb-1"><option value="">—</option>${o.hosts.map(h => `<option value="${h.ownerType}:${h.ownerId}"${hostVal === h.ownerType + ':' + h.ownerId ? ' selected' : ''}>${esc(h.name)}</option>`).join('')}
                    <option value="text"${hostVal === 'text' ? ' selected' : ''}>Annan (skriv)</option></select>
                <div class="pl-help small mb-1">Klubbarna och kretsarna som någon i planeringen företräder.</div>
                <input id="ptHostText" class="form-control mb-3${hostVal === 'text' ? '' : ' d-none'}" maxlength="200" value="${esc(p ? p.hostText || '' : '')}" placeholder="Till exempel: Förbundets kansli">
                <label class="form-label mt-2" for="ptDesc">Beskrivning (valfri)</label>
                <textarea id="ptDesc" class="form-control" rows="3" maxlength="2000">${esc(p ? p.description || '' : '')}</textarea>`,
            onOpen: body => {
                const sync = () => {
                    body.querySelector('#ptDiscBox').classList.toggle('d-none', !body.querySelector('#ptKindD').checked);
                    body.querySelector('#ptRespName').classList.toggle('d-none', body.querySelector('#ptResp').value !== '__text');
                    body.querySelector('#ptHostText').classList.toggle('d-none', body.querySelector('#ptHost').value !== 'text');
                };
                body.querySelectorAll('input[name="ptKind"], #ptResp, #ptHost').forEach(el => el.addEventListener('change', sync));
                body.querySelector('#ptName').focus();
            },
            onOk: async body => {
                const name = body.querySelector('#ptName').value.trim();
                if (!name) return 'Ge delen ett namn.';
                const resp = body.querySelector('#ptResp').value, host = body.querySelector('#ptHost').value;
                const [ht, hid] = host && host !== 'text' ? host.split(':') : ['', ''];
                const r = await post('SavePart', {
                    partId: p ? p.id : 0, name, kind: body.querySelector('input[name="ptKind"]:checked').value,
                    disciplineKey: body.querySelector('#ptDisc').value, description: body.querySelector('#ptDesc').value,
                    responsibleId: resp && resp !== '__text' ? resp : '', responsibleName: resp === '__text' ? body.querySelector('#ptRespName').value : '',
                    hostType: ht, hostId: hid, hostText: host === 'text' ? body.querySelector('#ptHostText').value : '',
                });
                if (!r.success) return r.message || 'Det gick inte att spara.';
                await reloadPeople(); render(view);
                return true;
            }
        });
    }
    async function removePart(p) {
        if (!await plConfirm('Ta bort delen?', `${p.name} tas bort. Dess uppgifter och meddelanden står kvar och gäller då hela planeringen.`, 'Ta bort', true)) return;
        const r = await post('RemovePart', { partId: p.id });
        if (!r.success) { failMsg(r); return; }
        if (tkPart === String(p.id)) tkPart = '';
        if (msgPart === String(p.id)) msgPart = '';
        await Promise.all([reloadPeople(), reloadTasks(), S.me.canReadMessages ? reloadMessages() : Promise.resolve()]);
        render(view);
    }
    function openMessagePart(m) {
        formModal({
            title: 'Vilken del gäller meddelandet?', okLabel: 'Spara', wide: false,
            html: `<select id="mpPart" class="form-select" aria-label="Del"><option value="0">Hela planeringen</option>${parts().map(p => `<option value="${p.id}"${m.partId === p.id ? ' selected' : ''}>${esc(p.name)}</option>`).join('')}</select>
                <p class="pl-help small mt-2 mb-0">Gäller hela tråden, även svaren.</p>`,
            onOk: async body => {
                const r = await post('SetMessagePart', { messageId: m.id, partId: body.querySelector('#mpPart').value });
                if (!r.success) return r.message || 'Det gick inte.';
                await reloadMessages(); refreshThreadOnly();
                return true;
            }
        });
    }

    // ================================================================ Pollningen
    let polling = false;
    async function poll() {
        if (RAIL_ONLY || polling || document.visibilityState !== 'visible' || !S) return;
        polling = true;
        try {
            const v = await get('Version');
            if (!v.ok) { clearInterval(timer); renderMissing(); return; }
            if (v.version !== version) {
                version = v.version;
                const typing = view === 'meddelanden' && $('plText') && document.activeElement === $('plText');
                await Promise.all([reloadPeople(), reloadMeetings(), reloadTasks(), reloadDocs(), reloadDecisions(), S.me.canReadMessages ? reloadMessages() : Promise.resolve()]);
                version = S.version;
                // Ett fält någon skriver i ritas aldrig om (anteckningar, punkter, nya uppgifter, sök).
                const editing = document.activeElement && $('plBody').contains(document.activeElement)
                    && /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement.tagName);
                if (view === 'meddelanden' && $('plThread')) refreshThreadOnly();          // skrivrutan rörs inte
                else if (!typing && !editing && view !== 'installningar' && !document.querySelector('.modal.show')) render(view);
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
            if (RAIL_ONLY) {
                const [s, m, tk, de] = await Promise.all([get('Get'), get('Meetings'), get('Tasks'), get('Decisions')]);
                if (!s.success) return;
                S = s; MEET = m.success ? m : { meetings: [] }; TASKS = tk.success ? tk : null; DEC = de.success ? de : null;
                if (S.me.canReadMessages) { const mm = await get('Messages'); MSG = mm.success ? mm : null; }
                renderRail();
                return;
            }
            if (!await loadAll()) return;
            render(hashView() || CFG.startView || 'oversikt');
            if (CFG.startView && !hashView()) history.replaceState(null, '', '#vy=' + view);
            if (CFG.leave && S.me.isParticipant) leave(); // "Lämna planeringen" i inbjudningsmejlet
        } catch (e) {
            $('plBody').innerHTML = `<div class="pl-card"><p class="text-danger mb-0">${esc(e.message || 'Sidan kunde inte laddas.')}</p></div>`;
        }
    })();
})();
