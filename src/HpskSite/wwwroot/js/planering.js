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
    // Dagsprogrammets sida har samma ram och vänstermeny (Stefan 2026-10-23), men sitt eget innehåll: där
    // ritas bara rälsen, och dess poster leder tillbaka till planeringens sida. (Bemanningen är sedan
    // 2026-10-24 en del i den här sidan, #vy=bemanning — planering-bemanning.js.)
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
            m.addEventListener('hidden.bs.modal', function h() { m.removeEventListener('hidden.bs.modal', h); m.style.zIndex = ''; resolve(answered); });
            // Öppnas rutan ovanpå en annan dialog (Lägg in flera) hamnar den annars bakom den och ser ut som att sidan hängt sig: lägg den överst, med egen fond.
            m.style.zIndex = '1080';
            m.addEventListener('shown.bs.modal', () => { const bd = document.querySelectorAll('.modal-backdrop'); if (bd.length) bd[bd.length - 1].style.zIndex = '1075'; }, { once: true });
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
        // Fokus tillbaka dit man kom ifrån när dialogen stängs (annars hamnar tangentbordsanvändaren överst på sidan); är utlösaren borta (ett menyval) går fokus till Åtgärder.
        const opener = document.activeElement;
        m.addEventListener('hidden.bs.modal', function back() {
            m.removeEventListener('hidden.bs.modal', back);
            const t = opener && opener.isConnected && opener !== document.body && opener.getClientRects().length ? opener : document.querySelector('#plActions [data-bs-toggle="dropdown"], #plActions button');
            if (t && typeof t.focus === 'function') t.focus();
        });
        modal.show();
        if (onOpen) m.addEventListener('shown.bs.modal', function h() { m.removeEventListener('shown.bs.modal', h); onOpen($('plFormBody')); });
        return modal;
    }
    // Omgång 3 (K8): den gemensamma datumtolkningen (planering-dates.js) — "16 maj 2027" blir aldrig 1 januari.
    function datePicker(el) {
        if (!el) return;
        if (window.hpskPlDatePicker) window.hpskPlDatePicker(el);
        else if (window.flatpickr) window.flatpickr(el, { locale: 'sv', dateFormat: 'Y-m-d', allowInput: true });
    }
    // Fältets datum som åååå-mm-dd; en text som inte går att läsa skickas som den är, så att servern säger ifrån.
    function dateValue(el) {
        const v = ((el && el.value) || '').trim();
        const p = window.hpskPlParseDate ? window.hpskPlParseDate(v) : v;
        return p === null ? v : p;
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
    // Omgång 2 steg 4: OMRÅDENA ersätter delarna. Ett område grupperar uppgifter och meddelanden; filtret '' = alla,
    // '0' = utan område (hela planeringen), annars områdets id. S.areas och TASKS.areas har samma form (AreasJson).
    let tkArea = '', msgArea = '';
    function areas() { return (TASKS && TASKS.areas) || (S && S.areas) || []; }
    function areaChip(id) {
        const a = id ? areas().find(x => x.id === id) : null;
        return a ? ` <span class="pl-role" title="Området"><i class="bi bi-folder2 me-1" aria-hidden="true"></i>${esc(a.name)}</span>` : '';
    }
    function areaFilterOptions(cur, allLabel) {
        const o = (v, l) => `<option value="${v}"${String(cur) === String(v) ? ' selected' : ''}>${esc(l)}</option>`;
        return o('', allLabel || 'Alla områden') + o('0', 'Utan område') + areas().map(a => o(a.id, a.name)).join('');
    }
    function areaMatches(areaId, f) { return f === '' || (f === '0' ? !areaId : areaId === parseInt(f, 10)); }
    let view = '';
    let replyTo = CFG.reply || 0;
    let pplQuery = '', pplFilter = '';   // Deltagarlistans sök och filter (lever över omritningar)

    // Nivåns färg — samma överallt där en nivå visas.
    const LEVEL_RANK = { Owner: 4, Editor: 3, Participant: 2, Follower: 1 };
    const LEVEL_BADGE = { Owner: 'text-bg-primary', Editor: 'text-bg-info', Participant: 'text-bg-success', Follower: 'text-bg-secondary' };
    const levelBadge = p => `<span class="badge rounded-pill ${LEVEL_BADGE[p.access] || 'text-bg-secondary'}">${esc(p.accessLabel || p.access)}</span>`;
    let pendingMentions = new Map(); // participantId -> name

    // Stefan 2026-10-25: rälsen grupperas — Översikt, tre grupper, och Inställningar sist (med Dina notiser).
    const VIEWS = [
        { key: 'oversikt', label: 'Översikt' },
        { key: 'meddelanden', label: 'Meddelanden', group: 'Samarbete', need: s => s.me.canReadMessages },
        { key: 'moten', label: 'Möten', group: 'Samarbete' },
        // Omgång 3 steg 10: Förslag och beslut syns när det finns något, eller när delen slagits på under Åtgärder.
        // hashOk: en länk (#vy=beslut) öppnar delen även när rälsen döljer den — sidans läge kan vara äldre än förslaget.
        { key: 'beslut', label: 'Förslag och beslut', group: 'Samarbete', need: s => !s.planning.parts || s.planning.parts.decisions, hashOk: true },
        { key: 'deltagare', label: 'Deltagare', group: 'Samarbete' },
        { key: 'uppgifter', label: 'Uppgifter', group: 'Arbetet' },
        { key: 'dokument', label: 'Dokument', group: 'Arbetet' },
        // Omgång 2: vägen till tävlingen — ansökan till kretsen, tävlingen som skapas, och kopplingen. Den som deltar
        // utan att kunna ändra ser den när det finns något att läsa.
        // Stefan 2026-10-25: en huvudplanering med delplaneringar är paraplyet — tävlingarna ligger i delarna, så den har
        // ingen sida Tävlingen (utom när den själv redan har en tävling eller en ansökan, så att inget försvinner ur sikte).
        { key: 'tavling', label: 'Tävlingen', group: 'Tävlingen', need: s => !TOKEN && (!isUmbrella(s) || hasOwnCompetition(s))
            && (s.me.canEdit || hasOwnCompetition(s)) },
        // Stationerna (Stefan 2026-10-24): fältskyttets stationer — föreningarna, delarna och helheten — på en egen plats.
        { key: 'stationer', label: 'Stationerna', group: 'Tävlingen', need: s => !TOKEN && stationsWanted(s) },
        // Omgång 2 (2026-10-24): bemanningen är en del i planeringen, inte en egen sida (planering-bemanning.js).
        // Omgång 3 (K8): platsen syns för alla som är med — den som inte kan ändra får veta vem som sköter bemanningen
        // och sina egna uppdrag, i stället för att delen tyst saknas.
        { key: 'bemanning', label: 'Bemanning', group: 'Tävlingen', need: s => !TOKEN && (s.me.canEdit || s.me.isParticipant) },
        // Dagsprogrammet hör till tävlingen och är fortfarande en egen sida — finns när planeringen är kopplad.
        { key: 'dagsprogram', label: 'Dagsprogram', group: 'Tävlingen', href: s => s.planning.agendaUrl, need: s => s.me.canEdit && !TOKEN && !!s.planning.agendaUrl },
        // Dina notiser ligger överst i Inställningar — alla som är med har den, den som kan ändra även planeringens.
        { key: 'installningar', label: 'Inställningar', need: s => s.me.isParticipant || s.me.canEdit },
    ];
    // Mejlens länk "Ändra hur du får notiser" (#vy=notiser) öppnar Inställningar vid Dina notiser.
    const VIEW_ALIAS = { notiser: 'installningar' };
    // Paraplyet: en huvudplanering med delplaneringar (familjen finns och planeringen har ingen förälder).
    function isUmbrella(s) { s = s || S; return !!(s.planning.family && !s.planning.parent); }
    function hasOwnCompetition(s) { s = s || S; return !!s.planning.competition || !!(s.planning.application && s.planning.application.id); }

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
        // Den som bara läser (huvudplaneringens sammankallande) har inget läst-läge här — inget är "nytt" för hen.
        if (!MSG || !S || !S.me.isParticipant) return 0;
        return MSG.messages.filter(m => m.id > MSG.myLastRead && !m.mine && !m.deleted && m.kind === 'Message').length;
    }
    function votesOwed() {
        if (!MEET || !S || !S.me.canUseMeetings) return 0;
        return MEET.meetings.filter(m => m.status === 'Proposed' && m.options.every(o => !o.mine)).length;
    }

    let railHere = null;   // platsen rälsen senast ritades för (telefonens meny fälls ihop när den byts)
    function renderRail() {
        const s = S;
        $('plOrg').textContent = s.planning.name;
        $('plRole').textContent = s.me.accessLabel || '';
        if (TOKEN) { const b = $('plBack'); b.textContent = '← Till startsidan'; b.href = '/'; }
        const counts = { meddelanden: unreadCount(), moten: votesOwed(), uppgifter: myOpenTasks(), beslut: decisionsOwed(), deltagare: s.me.pendingRequests || 0 };
        const here = RAIL_ONLY || (view === 'mote' ? 'moten' : view);
        const base = RAIL_ONLY ? '/tavlingsplanering/' + ID : '';
        const link = v => v.href
            // En egen sida (dagsprogrammet): en vanlig länk, i samma flik.
            ? `<a href="${esc(v.href(s))}" class="pl-nav${v.key === here ? ' on' : ''}">${esc(v.label)}</a>`
            : `<a href="${base}#vy=${v.key}" class="pl-nav${v.key === here ? ' on' : ''}" data-pl-go="${v.key}">${esc(v.label)}`
                + (counts[v.key] ? `<span class="pl-count" title="${counts[v.key]} väntar">${counts[v.key]}</span>` : '') + '</a>';
        // En grupp visas bara med det man har; utan något i den syns inte heller rubriken.
        const shown = VIEWS.filter(v => !v.need || v.need(s));
        const html = [];
        shown.forEach((v, i) => {
            if (!v.group) { html.push((v.key === 'installningar' && i > 0 ? '<div class="pl-railsep"></div>' : '') + link(v)); return; }
            if (shown[i - 1] && shown[i - 1].group === v.group) return;
            html.push(`<div class="pl-railgrp"><div class="pl-railh">${esc(v.group)}</div><div class="pl-railitems">${shown.filter(x => x.group === v.group).map(link).join('')}</div></div>`);
        });
        // Expertgruppen N7: på telefon tog rälsen hela första skärmen. Där fälls den ihop bakom en knapp som säger var man
        // är och hur mycket som väntar; datorn visar den som förut (knappen döljs i CSS).
        const cur = VIEWS.find(v => v.key === here);
        const waiting = Object.values(counts).reduce((a, b) => a + (b || 0), 0);
        // Ett val fäller ihop menyn — men pollningens omritning (samma plats) låter en utfälld meny stå kvar.
        const navEl = $('plRail').closest('.pl-rail');
        if (railHere !== here) navEl?.classList.remove('open');
        railHere = here;
        const isOpen = !!navEl?.classList.contains('open');
        $('plRail').innerHTML = `<button type="button" class="pl-railtoggle" data-rail-toggle aria-expanded="${isOpen}" aria-controls="plRailNav">
                <span><i class="bi bi-list me-2" aria-hidden="true"></i>Meny · <b>${esc(cur ? cur.label : 'Översikt')}</b></span>
                <span>${waiting ? `<span class="pl-count me-2" title="${waiting} väntar">${waiting}</span>` : ''}<i class="bi bi-chevron-down" aria-hidden="true"></i></span></button>
            <div class="pl-railnav" id="plRailNav">${html.join('')}</div>`;
    }
    document.addEventListener('click', e => {
        const b = e.target.closest('[data-rail-toggle]'); if (!b) return;
        const nav = b.closest('.pl-rail');
        const open = !nav.classList.contains('open');
        nav.classList.toggle('open', open);
        b.setAttribute('aria-expanded', open ? 'true' : 'false');
    });

    // Omgång 3 steg 10 (progressivt avslöjande): Åtgärder slår på en del som inte visas än — och av igen så länge den är tom.
    function partActions() {
        const pt = S.planning.parts;
        if (!pt || !S.me.canEdit || TOKEN) return [];
        const out = [];
        if (!pt.decisions) out.push({ label: 'Visa Förslag och beslut', icon: 'bi-plus-lg', fn: () => setPart('beslut', true, 'beslut') });
        else if (pt.decisionsOn && !pt.decisionsHasContent) out.push({ label: 'Dölj Förslag och beslut (tomt)', icon: 'bi-eye-slash', fn: () => setPart('beslut', false) });
        return out;
    }
    async function setPart(part, on, then) {
        const r = await post('SetPart', { part, on: on ? '1' : '0' });
        if (!r.success) { failMsg(r); return; }
        await reloadPeople();
        if (then) go(then); else { renderRail(); render(view); }
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
        const allowed = VIEWS.filter(x => !x.href && (!x.need || x.need(S) || x.hashOk)).map(x => x.key).concat(['mote']);
        v = VIEW_ALIAS[v] || v;
        view = allowed.includes(v) ? v : 'oversikt';
        if (view === 'mote' && !MEET.meetings.some(m => m.id === parseInt(hashParams().get('id'), 10))) view = 'moten';
        if (view !== 'bemanning' && window.PlaneringBemanning) window.PlaneringBemanning.leave();
        rowFns = [];
        renderRail();
        ({ oversikt: viewOverview, meddelanden: viewMessages, moten: viewMeetings, mote: () => viewMeeting(parseInt(hashParams().get('id'), 10)),
           uppgifter: viewTasks, beslut: viewDecisions, dokument: viewDocuments, deltagare: viewPeople, installningar: viewSettings,
           tavling: viewCompetition, stationer: viewStations,
           bemanning: () => S.me.canEdit ? window.PlaneringBemanning.render(STAFF_API) : viewStaffingInfo() })[view]();
    }

    // Omgång 3 (K8): Bemanning för den som inte kan ändra — vem som sköter den och mina egna uppdrag.
    function viewStaffingInfo() {
        setHead('Bemanning', esc(S.planning.name));
        setActions([]);
        const leads = (S.participants || []).filter(p => p.access === 'Owner' || p.access === 'Editor');
        const me = (S.participants || []).find(p => p.isMe);
        const mine = me && me.duties && me.duties.length ? me.duties : [];
        $('plBody').innerHTML = `<div class="pl-card"><h2>Vem sköter bemanningen?</h2>
                <p class="mb-2">Bemanningen — funktionärerna och deras pass — sköts av ${leads.length
                    ? leads.map(p => `<b>${esc(p.name)}</b>`).join(', ').replace(/, ([^,]*)$/, ' och $1')
                    : 'den som kan ändra planeringen'}. Hör av dig till ${leads.length === 1 ? 'hen' : 'någon av dem'} om du vill hjälpa till eller ändra något.</p>
                <p class="pl-help small mb-0">Du kan se bemanningen nedan. Den som kan ändra planeringen lägger in och ändrar.</p></div>
            <div class="pl-card" id="stfOverview"><h2>Hela bemanningen</h2><p class="pl-help mb-0">Hämtar…</p></div>
            <div class="pl-card"><h2>Dina uppdrag</h2>
                ${mine.length ? `<ul class="mb-2">${mine.map(d => `<li>${esc(d)}</li>`).join('')}</ul>` : '<p class="mb-2">Du har inga uppdrag i bemanningen.</p>'}
                <a class="btn btn-outline-secondary btn-sm" href="/mina-uppdrag">Mina uppdrag</a></div>`;
        loadStaffingOverview();
    }

    // Planera dagen: den som är med men inte får ändra ser bemanningen skrivskyddat — roll, dag, tid, namn och förening, inget mer.
    async function loadStaffingOverview() {
        const box = $('stfOverview'); if (!box) return;
        let r;
        try { r = await req('/umbraco/surface/Staffing/GetStaffingOverview?planningId=' + ID + '&competitionId=0', { headers: { Accept: 'application/json' } }); } catch (e) { r = null; }
        if (!$('stfOverview')) return;
        if (!r || !r.success) { box.innerHTML = '<h2>Hela bemanningen</h2><p class="pl-help mb-0">Bemanningen kunde inte läsas just nu.</p>'; return; }
        const cols = r.columns || [], rows = r.rows || [];
        if (!rows.length) { box.innerHTML = '<h2>Hela bemanningen</h2><p class="mb-0">Ingen är inlagd än.</p>'; return; }
        const cell = (row, c) => {
            const list = (row.cells || {})[c.key] || [];
            const need = (row.needed || {})[c.key] || 0, filled = (row.filled || {})[c.key] || 0;
            return list.map(e => `<div>${e.responsible ? '<i class="bi bi-star-fill text-warning me-1" aria-hidden="true"></i>' : ''}<span class="fw-semibold">${esc(e.name)}</span>${e.club ? ` <span class="text-body-secondary small">${esc(e.club)}</span>` : ''}${e.time ? ` <span class="badge text-bg-light border fw-normal">${esc(e.time)}</span>` : ''}</div>`).join('')
                + (need > filled ? `<div class="small text-danger">saknas ${need - filled}</div>` : '');
        };
        box.innerHTML = `<h2>Hela bemanningen</h2>${r.missing ? `<p class="small text-danger mb-2">${r.missing} ${r.missing === 1 ? 'plats saknas' : 'platser saknas'}.</p>` : ''}
            <div class="table-responsive"><table class="table table-sm align-top mb-0" id="stfOverviewTable"><thead><tr><th scope="col">Roll</th>${cols.map(c => `<th scope="col">${esc(c.label)}${c.dayLabel ? `<div class="small fw-normal text-body-secondary">${esc(c.dayLabel)}</div>` : ''}</th>`).join('')}</tr></thead>
            <tbody>${rows.map(row => `<tr><th scope="row">${esc(row.roleName)}${row.scopeLabel ? `<div class="small fw-normal text-body-secondary">${esc(row.scopeLabel)}</div>` : ''}</th>${cols.map(c => `<td>${cell(row, c)}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`;
    }

    // Det bemanningens del (planering-bemanning.js) får av skalet: samma anrop, dialoger och meddelanden.
    const STAFF_API = {
        ID, req, token, esc, spinner, setHead, setActions, rowMenu, formModal, plConfirm, showMsg, failMsg, datePicker,
        S: () => S, isActive: () => view === 'bemanning', go,
    };

    function subLine() {
        const p = S.planning;
        const parts = [];
        if (p.targetText) parts.push(esc(p.targetText));
        else if (p.targetStart) parts.push(esc(p.targetStart) + (p.targetEnd && p.targetEnd !== p.targetStart ? '–' + esc(p.targetEnd) : ''));
        if (p.status === 'Archived') parts.push('<span class="pl-state off">Arkiverad</span>');
        // En delplanering säger vilken planering den är en del av, och vem som sköter den.
        if (p.parent) parts.push('Del av ' + (p.parent.url ? `<a href="${esc(p.parent.url)}">${esc(p.parent.name)}</a>` : esc(p.parent.name)));
        if (p.parent && p.parent.host) parts.push('Sköts av ' + esc(p.parent.host));
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
            // Omgång 3 steg 10: delar som inte visas förrän de behövs.
            ...partActions(),
            // Stefan 2026-10-25: en delplanering skapas HÄR, direkt — för en gren, en tävling eller en förening som sköter en del.
            S.me.canCreateSub && !TOKEN && { label: 'Ny delplanering…', icon: 'bi-diagram-3', fn: openNewSub },
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
        if (p.me.isParentReader) {
            html.push(`<div class="pl-card" id="plParentReader"><p class="mb-0"><i class="bi bi-eye me-1"></i>Du <b>läser</b> den här planeringen eftersom du kan ändra i
                ${p.planning.parent && p.planning.parent.url ? `<a href="${esc(p.planning.parent.url)}">${esc(p.planning.parent.name)}</a>` : 'huvudplaneringen'}.
                Du är inte med i den: du skriver inget här och får inga notiser härifrån. Vill du vara med — be den sammankallande bjuda in dig.</p></div>`);
        }
        // Bara en sammankallande: gruppen står utan ledning om hen blir sjuk eller slutar.
        if (p.me.canManage && p.participants.filter(x => x.access === 'Owner').length === 1 && p.participants.length > 1) {
            html.push(`<div class="alert alert-warning d-flex gap-2 align-items-start"><i class="bi bi-exclamation-triangle mt-1"></i>
                <div>Du är ensam <b>sammankallande</b>. Utse gärna en till, så att gruppen inte står utan om du blir sjuk eller slutar.
                <a href="#vy=deltagare" data-pl-go="deltagare">Gå till Deltagare</a></div></div>`);
        }
        // Nästa steg
        const steps = [];
        // Omgång 3 (K8): den som väntar på att bli godkänd står först — annars sa sidan "Inget väntar på dig".
        if (p.me.pendingRequests) steps.push(`<div class="pl-next warn"><div class="t"><b>${p.me.pendingRequests === 1 ? 'En väntar' : p.me.pendingRequests + ' väntar'} på att du godkänner ${p.me.pendingRequests === 1 ? 'hen' : 'dem'}</b> — under Deltagare.</div>
            <a class="btn btn-primary" href="#vy=deltagare" data-pl-go="deltagare">Visa</a></div>`);
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
            <a class="btn btn-primary" href="#vy=uppgifter" data-pl-go="uppgifter" data-pl-filter="mine">Dina uppgifter</a></div>`);
        const late = TASKS ? TASKS.tasks.filter(t => t.overdue).length : 0;
        if (late && p.me.canEdit) steps.push(`<div class="pl-next warn"><div class="t"><b>${late === 1 ? 'En uppgift är försenad' : `${late} uppgifter är försenade`}</b></div>
            <a class="btn btn-outline-primary" href="#vy=uppgifter" data-pl-go="uppgifter" data-pl-filter="overdue">Visa</a></div>`);
        if (TASKS && !TASKS.tasks.length && p.planning.competition && p.me.canEdit)
            steps.push(`<div class="pl-next"><div class="t"><b>Lägg in förberedelserna</b> — börja med en mall för tävlingens storlek.</div>
                <a class="btn btn-primary" href="#vy=uppgifter" data-pl-go="uppgifter">Uppgifter</a></div>`);
        // Tävlingen (omgång 2): det som väntar på vägen till tävlingen — själva arbetet görs i rälsposten Tävlingen.
        const compStep = competitionNextStep();
        if (compStep) steps.push(`<div class="pl-next${compStep.warn ? ' warn' : ''}" id="plCompNext"><div class="t"><b>Tävlingen:</b> ${compStep.text}</div>
            <a class="btn ${compStep.warn ? 'btn-primary' : 'btn-outline-primary'}" href="#vy=tavling" data-pl-go="tavling">${esc(compStep.button || 'Tävlingen')}</a></div>`);
        // Bemanningen: persontesterna fick "Lägg in förberedelserna" som enda nästa steg och hittade aldrig Bemanning (långt ner i menyn). "Dagens plan" i Bemanning
        // visar vad som saknas; den här raden pekar dit. Bara den som får ändra.
        if (p.me.canEdit) steps.push(`<div class="pl-next" id="plStaffNext"><div class="t"><b>Bemanning:</b> vilka som behövs, vilka som står var och vad som saknas.</div>
            <a class="btn btn-outline-primary" href="#vy=bemanning" data-pl-go="bemanning">Dagens plan</a></div>`);
        const upcoming = (MEET ? MEET.meetings : []).filter(m => m.status === 'Scheduled' && parseDT(m.endsAt || m.startsAt) >= new Date())
            .sort((a, b) => parseDT(a.startsAt) - parseDT(b.startsAt));
        if (upcoming.length) {
            const m = upcoming[0];
            steps.push(`<div class="pl-next"><div class="t"><b>Nästa möte:</b> ${esc(m.title)} — ${esc(span(m.startsAt, m.endsAt))}</div>
                ${m.onlineUrl ? `<a class="btn btn-outline-primary" href="${esc(m.onlineUrl)}" target="_blank" rel="noopener">Gå med</a>` : ''}
                <a class="btn btn-outline-primary" href="#vy=mote&id=${m.id}" data-pl-open-meeting="${m.id}">Visa</a></div>`);
        }
        // Planeringstestet k2 (S4 T4-8): "Inget väntar på dig" lovade för mycket när Kjell inte hade gjort något än.
        html.push(`<div class="pl-card"><h2>Nästa steg</h2>${steps.join('') || '<p class="pl-help mb-0">Inget att svara på just nu.</p>'}</div>`);
        // N7: delplaneringarna före tävlingarna och veckoprogrammet, som kan bli långt — kortet ska synas utan att rulla.
        html.push(subPlanningsCardHtml());
        html.push(familyCompetitionsCardHtml());
        html.push(parentDecisionsCardHtml());
        html.push('<div id="plStationsSum"></div>');

        // Kom igång — står kvar för den sammankallande tills de tre stegen är gjorda, och bockar av dem (k2: den försvann
        // vid första inbjudan, och Kjell visste inte vad han skulle göra sedan).
        const kgInvited = p.participants.length > 1;
        const kgMeeting = !!(MEET && MEET.meetings.length);
        const kgMessage = !!(MSG && MSG.messages.some(m => m.kind === 'Message' && !m.deleted));
        const kgShow = p.me.isParticipant && (p.participants.length <= 1 && !kgMeeting || p.me.canManage && !(kgInvited && kgMeeting && kgMessage));
        if (kgShow) {
            const kg = (done, html) => `<li class="mb-2">${done ? '<span class="text-success me-1" aria-label="Gjort">✓</span><span class="pl-help">' + html + '</span>' : html}</li>`;
            html.push(`<div class="pl-card" id="plGetStarted"><h2>Kom igång</h2>
                <ol class="mb-0">
                    ${kg(kgInvited, `${p.me.canInvite ? '<button class="btn btn-link p-0 align-baseline" data-pl-invite="1">Bjud in</button> de som ska vara med' : 'Be den som skapade planeringen bjuda in de andra'} — sök på namnet bland alla medlemmar. Ingen behöver känna till någons e-postadress.`)}
                    ${kg(kgMeeting, '<button class="btn btn-link p-0 align-baseline" data-pl-newmeet="1">Föreslå ett möte</button> med några tider — alla svarar vilka som passar, direkt ur mejlet.')}
                    ${kg(kgMessage, 'Skriv det första meddelandet under <a href="#vy=meddelanden" data-pl-go="meddelanden">Meddelanden</a>. Alla får ett mejl på kvällen med det som är nytt.')}
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
        loadStationsSummary();
    }
    document.addEventListener('click', e => {
        const b = e.target.closest('[data-pl-meet]'); if (!b) return;
        e.preventDefault(); goMeeting(parseInt(b.dataset.plMeet, 10));
    });
    document.addEventListener('click', e => { if (e.target.closest('[data-pl-invite]')) { e.preventDefault(); openInvite(); } });
    // Planeringstestet k2 (S1, S2, S4): "Med i planeringen (1)" stod kvar efter inbjudan — Översikt ritas om när en dialog
    // stängs, så att listan, Kom igång och Nästa steg stämmer.
    $('plFormModal')?.addEventListener('hidden.bs.modal', () => { if (view === 'oversikt' && S && !RAIL_ONLY) render('oversikt'); });
    document.addEventListener('click', e => { if (e.target.closest('[data-pl-newmeet]')) { e.preventDefault(); openNewMeeting(); } });

    // ================================================================ Tävlingen (omgång 2)
    // Vägen från planering till tävling på ett ställe: ansökan till kretsen (klubbens vanliga ansökan — läst,
    // ändrad, inskickad och utskriven här, med klubbens fullständiga dialog), tävlingen som skapas ur den eller
    // direkt (en föreningstävling söks inte), och kopplingen. Ändra, skicka in, svara och skapa kräver klubbens
    // rätt och prövas på servern; den som bara deltar läser.
    let APP = null; // CompetitionApplication/GetApplication: { application, events, canClub }
    const APP_BASE = '/umbraco/surface/CompetitionApplication/';
    function appPost(action, params) {
        return req(APP_BASE + action, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'RequestVerificationToken': token() },
            body: new URLSearchParams(params).toString() });
    }
    const APP_AT_KRETS = ['Inskickad', 'Komplettering', 'HosForbundet'];

    // Raden under Nästa steg på Översikt — null när inget väntar.
    function competitionNextStep() {
        const p = S.planning, a = p.application;
        // Omgång 3 (K8): steget är den sammankallandes — för den som deltar var det en uppmaning hen inte kan följa.
        if (TOKEN || p.competition || !S.me.canManage) return null;
        if (isUmbrella() && !hasOwnCompetition()) return null;   // tävlingarna ligger i delplaneringarna
        if (a && a.id > 0) {
            if (a.status === 'Utkast') return S.me.canEdit ? { text: 'utkastet till ansökan är inte inskickat till kretsen.', button: 'Visa ansökan' } : null;
            if (a.status === 'Komplettering') return { warn: true, text: 'kretsen ber om mer i ansökan.', button: 'Visa ansökan' };
            if (a.status === 'Beviljad') return { warn: true, text: 'kretsen har sagt ja — nu kan tävlingen skapas.', button: 'Skapa tävlingen' };
            if (a.status === 'Avslagen') return { text: 'kretsen sa nej till ansökan.', button: 'Visa ansökan' };
            return null; // hos kretsen eller förbundet: inget att göra än
        }
        if (a && a.id === 0) return { text: 'ska den sökas hos kretsen, eller är det en föreningstävling?', button: 'Tävlingen' };
        return null;
    }

    function competitionSteps() {
        const p = S.planning, a = p.application, st = a && a.id > 0 ? a.status : '';
        const linked = !!p.competition, direct = linked && !st;
        const sent = st && st !== 'Utkast';
        const steps = [
            { t: direct ? 'Ingen ansökan behövdes' : 'Ansökan till kretsen', s: direct ? 'skip' : (sent || linked ? 'done' : 'now') },
            { t: st === 'Avslagen' ? 'Kretsen sa nej' : 'Kretsen säger ja', s: direct ? 'skip' : (st === 'Beviljad' || linked ? 'done' : (APP_AT_KRETS.includes(st) || st === 'Avslagen' ? 'now' : '')) },
            { t: 'Tävlingen skapas', s: linked ? 'done' : (st === 'Beviljad' ? 'now' : '') },
            { t: 'Planeringen kopplas', s: linked ? 'done' : '' },
        ];
        return `<ol class="pl-steps mb-3" id="plCompSteps">${steps.map((x, i) => `<li class="${x.s}"${x.s === 'now' ? ' aria-current="step"' : ''}>
            <span class="n">${x.s === 'done' ? '<i class="bi bi-check-lg"></i>' : i + 1}</span><span>${esc(x.t)}</span></li>`).join('')}</ol>`;
    }

    async function viewCompetition() {
        const p = S.planning, sum = p.application;
        setHead('Tävlingen', esc(p.name));
        // Läst förut (samma ansökan): rita direkt och läs om i bakgrunden — pollningen ritar om vyn.
        const cached = APP && sum && APP.application.id === sum.id;
        if (cached || !(sum && sum.id > 0)) drawCompetition();
        else { setActions([]); $('plBody').innerHTML = spinner(); }
        if (!(sum && sum.id > 0)) { APP = null; return; }
        try {
            const r = await req(APP_BASE + 'GetApplication?asClub=true&id=' + sum.id);
            APP = r.success ? r : null;
        } catch (e) { APP = null; }
        if (view === 'tavling' && !document.querySelector('.modal.show')) drawCompetition();
    }

    function drawCompetition() {
        const p = S.planning, sum = p.application, comp = p.competition;
        const A = APP && sum && APP.application.id === sum.id ? APP.application : null;
        const club = !!(APP && APP.canClub && A);
        const direct = !comp && !A && sum && sum.id === 0 && S.me.canLinkCompetition;
        setActions([
            club && A.canSubmit && { label: 'Skicka in till kretsen', icon: 'bi-send', fn: submitApp },
            club && A.canReply && { label: 'Svara kretsen…', icon: 'bi-reply', fn: replyApp },
            club && A.canCreateCompetition && { label: 'Skapa tävlingen…', icon: 'bi-trophy', fn: createFromApp },
            club && A.canEdit && { label: 'Ändra ansökan…', icon: 'bi-pencil', fn: editApp },
            A && { label: 'Skriv ut ansökan', icon: 'bi-printer', fn: () => window.open(APP_BASE + 'PrintApplication?id=' + A.id, '_blank', 'noopener') },
            !comp && !A && sum && sum.id === 0 && { label: 'Sök om tävlingen hos kretsen…', icon: 'bi-send', fn: newApp },
            direct && { label: 'Skapa en föreningstävling…', icon: 'bi-plus-circle', fn: createDirect },
            // Utan ansökan, eller när tävlingen redan skapats ur den (och planeringen kopplats loss).
            !comp && (!A || A.competitionId > 0) && S.me.canLinkCompetition && { label: 'Koppla till en befintlig tävling…', icon: 'bi-link-45deg', fn: linkExisting },
            comp && comp.manageUrl && { label: 'Öppna tävlingshanteringen', icon: 'bi-box-arrow-up-right', fn: () => { location.href = comp.manageUrl; } },
            ((club && (A.canDelete || A.canWithdraw)) || (comp && S.me.canLinkCompetition)) && { divider: true },
            club && A.canDelete && { label: 'Ta bort utkastet…', icon: 'bi-trash', danger: true, fn: deleteApp },
            club && !A.canDelete && A.canWithdraw && { label: 'Återkalla ansökan…', icon: 'bi-x-circle', danger: true, fn: withdrawApp },
            comp && S.me.canLinkCompetition && { label: 'Koppla loss från tävlingen…', icon: 'bi-link', danger: true, fn: unlinkCompetition },
        ]);

        const html = [`<div class="pl-card" id="plCompWay"><h2>Vägen till tävlingen</h2>${competitionSteps()}${competitionNow(A, club, direct)}</div>`];
        if (comp) html.push(`<div class="pl-card" id="plCompCard"><h2>Tävlingen</h2>
            <p class="mb-1">${comp.url ? `<a href="${esc(comp.url)}"><b>${esc(comp.name)}</b></a>` : `<b>${esc(comp.name)}</b>`}${comp.date ? ' · ' + esc(fmtDay(comp.date)) + (comp.endDate && comp.endDate !== comp.date ? '–' + esc(fmtDay(comp.endDate)) : '') : ''}</p>
            <p class="pl-help small mb-2">Den som har tillgång till tävlingen — tävlingsledning, klubbens administratörer och skjutledare, kretsens administratörer — kommer in i planeringen med nivån <i>Kan ändra</i> utan att bjudas in.</p>
            <div class="d-flex gap-2 flex-wrap">${comp.manageUrl ? `<a class="btn btn-outline-primary btn-sm" href="${esc(comp.manageUrl)}">Tävlingshanteringen</a>` : ''}
                ${p.agendaUrl && S.me.canEdit ? `<a class="btn btn-outline-primary btn-sm" href="${esc(p.agendaUrl)}">Dagsprogrammet</a>` : ''}</div></div>`);
        if (A) html.push(applicationCard(A), applicationHistory());
        html.push(staffsForCardHtml());
        $('plBody').innerHTML = html.join('');
        const sf = $('plStaffsFor');
        if (sf) sf.addEventListener('change', async () => {
            const r = await post('SetStaffsFor', { competitionId: sf.value });
            if (!r.success) { failMsg(r); return; }
            await reloadPeople(); drawCompetition();
            showMsg('Bemanningen gäller nu ' + esc(sf.selectedOptions[0] ? sf.selectedOptions[0].textContent : '') + '.', 'success');
        });
    }

    // Omgång 3 ("Bemannar för"): en planering utan egen koppling i en familj väljer vilken av familjens tävlingar dess
    // bemanning gäller — funktionärerna får behörighet och syns i den tävlingens upprop. Förval: huvudplaneringens.
    function staffsForCardHtml() {
        const f = S.planning.family;
        if (!f || f.ownLink || !(f.competitions || []).length) return '';
        const parentComp = f.competitions.find(c => c.competitionId === f.parentCompetitionId);
        const current = f.competitions.find(c => c.competitionId === f.staffingCompetitionId);
        const label = current ? current.name : 'ingen tävling';
        if (!S.me.canManage) return `<div class="pl-card" id="plStaffsForCard"><h2>Bemanningen</h2>
            <p class="mb-0">Bemanningen i den här planeringen gäller <b>${esc(label)}</b>.</p></div>`;
        const dflt = parentComp ? `Huvudplaneringens tävling (${parentComp.name})` : 'Ingen tävling';
        return `<div class="pl-card" id="plStaffsForCard"><h2>Bemanningen</h2>
            <label class="form-label" for="plStaffsFor">Vilken tävling gäller bemanningen här?</label>
            <select id="plStaffsFor" class="form-select" style="max-width:32rem">
                <option value="0"${f.staffsFor ? '' : ' selected'}>${esc(dflt)}</option>
                ${f.competitions.filter(c => c.competitionId !== f.parentCompetitionId).map(c => `<option value="${c.competitionId}"${f.staffsFor === c.competitionId ? ' selected' : ''}>${esc(c.name)}</option>`).join('')}</select>
            <p class="pl-help small mt-2 mb-0">Funktionärerna du lägger in här får tillgång till den tävlingen och syns i dess upprop. Planeringen kopplas inte till tävlingen — den är kopplad till ${esc(f.competitions.find(c => c.competitionId === (f.staffsFor || f.parentCompetitionId))?.planningName || 'en annan planering i familjen')}.</p></div>`;
    }

    // Vad som gäller just nu, i ord, med knappen för steget man står på (resten under Åtgärder).
    function competitionNow(A, club, direct) {
        const p = S.planning, sum = p.application, comp = p.competition;
        const btn = (id, label, cls) => `<button class="btn ${cls || 'btn-primary'}" id="${id}">${esc(label)}</button>`;
        if (comp) return `<p class="mb-0">Planeringen är kopplad till tävlingen. Bemanningen, dagsprogrammet och förberedelserna hör nu till den.</p>`;
        if (A) {
            const who = club ? '' : `<p class="pl-help small mb-0 mt-1">Ansökan ändras och skickas in av någon som administrerar ${esc(A.clubName)}.</p>`;
            switch (A.status) {
                case 'Utkast': return `<div class="pl-next"><div class="t"><b>Utkastet är inte inskickat.</b> Gå igenom det och skicka in det till ${esc(A.regionName || 'kretsen')}.</div>
                    ${club ? btn('plAppEdit', 'Ändra', 'btn-outline-primary') + btn('plAppSubmit', 'Skicka in till kretsen') : ''}</div>${who}`;
                case 'Inskickad': return `<p class="mb-0">Ansökan ligger hos ${esc(A.regionName || 'kretsen')}. Ni får ett mejl när kretsen har svarat.</p>`;
                case 'Komplettering': return `<div class="pl-next warn"><div class="t"><b>Kretsen ber om mer:</b> ${esc(A.completionRequest || '')}</div>
                    ${club ? btn('plAppReply', 'Svara kretsen') : ''}</div>${who}`;
                case 'HosForbundet': return `<p class="mb-0">Kretsen har skickat ansökan vidare till Förbundet, som beslutar. Kretsen registrerar beslutet här när det kommer.</p>`;
                case 'Beviljad': return A.canCreateCompetition || !club
                    ? `<div class="pl-next warn"><div class="t"><b>Kretsen har sagt ja.</b> Skapa tävlingen — namn, datum och det ansökan angav fylls i, och planeringen kopplas till tävlingen.</div>
                        ${club ? btn('plAppCreate', 'Skapa tävlingen') : ''}</div>${club ? '' : `<p class="pl-help small mb-0 mt-1">Tävlingen skapas av någon som administrerar ${esc(A.clubName)}.</p>`}`
                    : `<p class="mb-2">Kretsen har sagt ja, och tävlingen är skapad${A.competitionUrl ? ` — <a href="${esc(A.competitionUrl)}">öppna den</a>` : ''}. Planeringen är inte kopplad till den.</p>
                        ${S.me.canLinkCompetition ? btn('plCompLink', 'Koppla till en befintlig tävling…', 'btn-outline-primary') : ''}`;
                case 'Avslagen': return `<p class="mb-0">Kretsen sa nej till ansökan.${A.decisionText ? ' ' + esc(A.decisionText) : ''}</p>`;
                default: return '';
            }
        }
        if (sum && sum.id > 0) return `<p class="mb-0"><b>${esc(sum.name)}</b> <span class="pl-state wait">${esc(sum.statusLabel)}</span></p>
            <p class="pl-help small mb-0 mt-1">Ansökan kunde inte läsas just nu. Ladda om sidan om en stund.</p>`;
        if (sum && sum.id === 0) return `<p class="mb-2">En <b>kretstävling, landsdelstävling eller nationell tävling</b> söks hos kretsen. En <b>föreningstävling</b> söks inte — den skapas direkt.</p>
            <div class="d-flex gap-2 flex-wrap">${btn('plAppNew', 'Sök om tävlingen hos kretsen…')}
                ${direct ? btn('plCompDirect', 'Skapa en föreningstävling…', 'btn-outline-primary') : ''}
                ${S.me.canLinkCompetition ? btn('plCompLink', 'Koppla till en befintlig tävling…', 'btn-outline-secondary') : ''}</div>`;
        if (S.me.canLinkCompetition) return `<p class="mb-2">Finns tävlingen redan kan du koppla planeringen till den. Ansökan till kretsen och en ny tävling görs av någon som administrerar klubben som arrangerar — be den personen vara med i planeringen och företräda klubben.</p>
            ${btn('plCompLink', 'Koppla till en befintlig tävling…', 'btn-outline-primary')}`;
        return `<p class="mb-0">${S.me.canEdit ? 'Ansökan till kretsen och tävlingen görs av någon som administrerar klubben som arrangerar, och planeringen kopplas av den sammankallande.' : 'Här syns ansökan och tävlingen när de finns.'}</p>`;
    }
    document.addEventListener('click', e => {
        const b = e.target.closest('#plAppEdit, #plAppSubmit, #plAppReply, #plAppCreate, #plAppNew, #plCompDirect, #plCompLink'); if (!b) return;
        ({ plAppEdit: editApp, plAppSubmit: submitApp, plAppReply: replyApp, plAppCreate: createFromApp, plAppNew: newApp,
           plCompDirect: createDirect, plCompLink: linkExisting })[b.id]();
    });

    function applicationCard(A) {
        const cls = A.status === 'Beviljad' ? 'ok' : (A.status === 'Avslagen' || A.status === 'Aterkallad' ? 'off' : (A.status === 'Utkast' ? 'blue' : 'wait'));
        const day = s => s ? fmtDay(s) : '';
        const rows = [
            ['Tävlingen', A.name],
            ['Söker', `${A.clubName}${A.regionName ? ' hos ' + A.regionName : ''}`],
            ['Söks som', A.levelLabel],
            ['Gren', A.disciplineLabel],
            ['Datum', day(A.competitionDate) + (A.endDate && A.endDate !== A.competitionDate ? ' – ' + day(A.endDate) : '')
                + (A.grantedDate && A.grantedDate !== A.competitionDate ? ` (Förbundet har beviljat ${day(A.grantedDate)})` : '')],
            A.reserveDate && ['Reservdatum', day(A.reserveDate)],
            ['Plats', [A.rangeName, A.place].filter(Boolean).filter((v, i, all) => all.indexOf(v) === i).join(' · ') || 'Ej angivet'],
            A.numberOfSeries && ['Antal ' + (A.seriesWord || 'serier'), String(A.numberOfSeries)],
            ['Mästerskap', A.championshipScope || 'Nej'],
            ['Standardmedaljer', A.awardsStandardMedals === true ? 'Ja' : A.awardsStandardMedals === false ? 'Nej' : 'Ej angivet'],
            A.classes && ['Preliminära klasser', A.classes],
            ['Kontaktperson', [A.contactName, A.contactEmail, A.contactPhone].filter(Boolean).join(' · ')],
            A.note && ['Meddelande till kretsen', A.note],
            A.completionRequest && ['Kretsen bad om', A.completionRequest],
            A.completionReply && ['Klubbens svar', A.completionReply],
            A.kretsOpinion && ['Kretsens yttrande', A.kretsOpinion + (A.kretsOpinionText ? ': ' + A.kretsOpinionText : '')],
            A.decisionText && ['Beslut', A.decisionText + (A.deciderName ? ` (${A.deciderName}${A.decidedAt ? ', ' + day(A.decidedAt) : ''})` : '')],
        ].filter(Boolean);
        return `<div class="pl-card" id="plAppCard"><div class="pl-cardh"><h2>Ansökan till kretsen</h2><span class="pl-state ${cls}">${esc(A.statusLabel)}</span></div>
            <dl class="pl-kv">${rows.map(r => `<dt>${esc(r[0])}</dt><dd>${esc(r[1])}</dd>`).join('')}</dl>
            ${A.lateNote ? `<div class="alert alert-warning py-2 mt-3 mb-0 small">${esc(A.lateNote)}</div>` : ''}</div>`;
    }
    function applicationHistory() {
        const ev = (APP && APP.events) || [];
        if (!ev.length) return '';
        return `<div class="pl-card" id="plAppHistory"><h2>Historik</h2>
            ${ev.slice().reverse().map(x => `<div class="d-flex gap-3 py-1 border-top flex-wrap"><div class="pl-help small text-nowrap" style="min-width:8.5rem">${esc(x.at)}</div>
                <div class="flex-grow-1"><b>${esc(x.kindLabel)}</b>${x.byName ? ' · ' + esc(x.byName) : ''}${x.text ? `<div class="small" style="white-space:pre-wrap">${esc(x.text)}</div>` : ''}</div></div>`).join('')}</div>`;
    }

    // Efter en ändring i ansökan: planeringens sammanfattning och ansökan läses om.
    async function afterAppChange(msg, kind) {
        await reloadPeople();
        if (view === 'tavling') await viewCompetition(); else render(view);
        if (msg) showMsg(esc(msg), kind || 'success');
    }
    function needDialog() {
        if (typeof window.hpskAppOpenForm === 'function') return true;
        showMsg('Ansökans dialog finns inte på sidan. Ladda om sidan och försök igen.', 'danger');
        return false;
    }
    function newApp() {
        const p = S.planning, sum = p.application;
        if (!sum || sum.id !== 0 || !needDialog()) return;
        window.hpskAppOpenForm({
            clubId: sum.clubs[0].id, clubs: sum.clubs, planningId: ID,
            prefill: { name: p.name, discipline: p.disciplineKey || '', date: p.targetStart || '',
                endDate: p.targetEnd && p.targetEnd !== p.targetStart ? p.targetEnd : '', place: p.targetText || '' },
            onSaved: r => afterAppChange(r.message),
        });
    }
    function editApp() {
        const A = APP && APP.application;
        if (!A || !needDialog()) return;
        window.hpskAppOpenForm({ clubId: A.clubId, app: A, onSaved: r => afterAppChange(r.message) });
    }
    async function submitApp() {
        const A = APP && APP.application; if (!A) return;
        if (!await plConfirm('Skicka in ansökan?', `Ansökan om ${A.name} skickas till ${A.regionName || 'kretsen'}, som får ett mejl. Efter det ändrar du den bara om kretsen ber om mer — eller så återkallar du den.`, 'Skicka in')) return;
        const r = await appPost('SubmitApplication', { id: A.id });
        if (!r.success) { failMsg(r); return; }
        await afterAppChange(r.message);
    }
    function replyApp() {
        const A = APP && APP.application; if (!A) return;
        formModal({
            title: 'Svara kretsen', okLabel: 'Skicka svaret',
            html: `${A.completionRequest ? `<p class="mb-2"><b>Kretsen ber om:</b> ${esc(A.completionRequest)}</p>` : ''}
                <label class="form-label" for="plAppReplyText">Ditt svar</label>
                <textarea id="plAppReplyText" class="form-control" rows="4" maxlength="4000"></textarea>
                <p class="pl-help small mt-2 mb-0">Behöver något i själva ansökan ändras — gör det med <i>Ändra ansökan</i> först.</p>`,
            onOpen: body => body.querySelector('#plAppReplyText').focus(),
            onOk: async body => {
                const text = body.querySelector('#plAppReplyText').value.trim();
                if (!text) return 'Skriv svaret till kretsen.';
                const r = await appPost('ReplyApplication', { id: A.id, text });
                if (!r.success) return r.message || 'Svaret kunde inte skickas.';
                afterAppChange(r.message);
                return true;
            }
        });
    }
    function withdrawApp() {
        const A = APP && APP.application; if (!A) return;
        formModal({
            title: 'Återkalla ansökan', okLabel: 'Återkalla', okClass: 'btn-danger',
            html: `<p>Ansökan om <b>${esc(A.name)}</b> återkallas. Kretsen ser att den är återkallad, och den kan inte skickas in igen — en ny ansökan kan göras härifrån.</p>
                <label class="form-label" for="plAppWithdrawWhy">Skäl (valfritt)</label>
                <input id="plAppWithdrawWhy" class="form-control" maxlength="500">`,
            onOk: async body => {
                const r = await appPost('WithdrawApplication', { id: A.id, reason: body.querySelector('#plAppWithdrawWhy').value.trim() });
                if (!r.success) return r.message || 'Ansökan kunde inte återkallas.';
                APP = null;
                afterAppChange(r.message);
                return true;
            }
        });
    }
    async function deleteApp() {
        const A = APP && APP.application; if (!A) return;
        if (!await plConfirm('Ta bort utkastet?', `Utkastet till ansökan om ${A.name} tas bort. Det har aldrig skickats in, så kretsen har inte sett det.`, 'Ta bort', true)) return;
        const r = await appPost('DeleteApplication', { id: A.id });
        if (!r.success) { failMsg(r); return; }
        APP = null;
        await afterAppChange(r.message);
    }

    // Guiden (CompetitionWizardModal) visas i planeringen; när tävlingen är skapad säger planeringen det i stället för
    // guidens alert() (onCompetitionSaved returnerar true).
    function openWizard() {
        const el = document.getElementById('competitionWizardModal');
        if (!el || typeof window.hpskWizardFromPlanning !== 'function') { showMsg('Tävlingsguiden finns inte på sidan. Ladda om sidan och försök igen.', 'danger'); return false; }
        const modal = window.competitionWizardModalInstance || new bootstrap.Modal(el);
        window.competitionWizardModalInstance = modal;
        modal.show();
        if (typeof window.resetWizard === 'function') window.resetWizard();
        return true;
    }
    function createFromApp() {
        const A = APP && APP.application; if (!A) return;
        if (typeof window.hpskAppCreateCompetition !== 'function') { needDialog(); return; }
        window.hpskAppCreateCompetition(A.id, data => {
            if (!data || data.success === false) { failMsg(data, 'Tävlingen kunde inte skapas.'); return true; }
            // Servern kopplar tävlingen till planeringen i samma anrop (ansökan bär planeringens id).
            afterAppChange('Tävlingen är skapad och planeringen är kopplad till den.' + (data.applicationNote ? ' ' + data.applicationNote : ''));
            return true;
        });
    }
    function createDirect() {
        const p = S.planning, sum = p.application;
        if (!sum || sum.id !== 0 || !openWizard()) return;
        window.hpskWizardFromPlanning({ name: p.name, discipline: p.disciplineKey || '', date: p.targetStart || '',
            endDate: p.targetEnd && p.targetEnd !== p.targetStart ? p.targetEnd : '', venue: p.targetText || '', clubId: sum.clubs[0].id });
        window.onCompetitionSaved = data => {
            const cid = parseInt(data && data.data && data.data.id, 10) || 0;
            linkAfterCreate(cid);
            return true;
        };
    }
    async function linkAfterCreate(cid) {
        if (!cid) { showMsg('Tävlingen är skapad, men dess nummer kom inte tillbaka. Koppla planeringen under Åtgärder → Koppla till en befintlig tävling.', 'warning'); return; }
        const r = await post('LinkCompetition', { competitionId: cid });
        if (!r.success) { await afterAppChange(); showMsg('Tävlingen är skapad, men planeringen kunde inte kopplas: ' + esc(r.message || '') + ' Koppla den under Åtgärder → Koppla till en befintlig tävling.', 'warning'); return; }
        await afterAppChange('Tävlingen är skapad och planeringen är kopplad till den.' + (r.courseNote ? ' ' + esc(r.courseNote) : ''));
    }
    async function linkExisting() {
        const r = await get('LinkableCompetitions');
        if (!r.success) { failMsg(r); return; }
        const list = r.competitions || [];
        formModal({
            title: 'Koppla till en befintlig tävling', okLabel: list.length ? 'Koppla' : 'Stäng',
            html: list.length
                ? `<p class="pl-help small">Kommande tävlingar som klubbarna eller kretsarna som någon i planeringen företräder arrangerar, eller där du är tävlingsansvarig — som du får redigera och som inte redan har en planering, närmast planeringens datum först.</p>
                   <div class="list-group" id="plLinkList">${list.map((c, i) => `<label class="list-group-item d-flex gap-2 align-items-start">
                       <input class="form-check-input mt-1" type="radio" name="plLinkComp" value="${c.id}"${i === 0 ? ' checked' : ''}>
                       <span><b>${esc(c.name)}</b><span class="d-block pl-help small">${esc([c.date ? fmtDay(c.date) : '', c.club].filter(Boolean).join(' · '))}</span></span></label>`).join('')}</div>
                   <p class="pl-help small mt-2 mb-0">Bemanningen får tävlingens dagar, och de som har tillgång till tävlingen kommer in i planeringen.</p>`
                : `<p class="mb-0">Det finns ingen kommande tävling att koppla till: ingen som klubbarna eller kretsarna som någon här företräder arrangerar (eller där du är tävlingsansvarig) som du får redigera och som saknar planering.</p>`,
            onOk: async body => {
                const pick = body.querySelector('input[name="plLinkComp"]:checked');
                if (!pick) return list.length ? 'Välj en tävling.' : true;
                const x = await post('LinkCompetition', { competitionId: pick.value });
                if (!x.success) return x.message || 'Planeringen kunde inte kopplas.';
                afterAppChange('Planeringen är kopplad till tävlingen.' + (x.courseNote ? ' ' + esc(x.courseNote) : ''));
                return true;
            }
        });
    }
    async function unlinkCompetition() {
        if (!await plConfirm('Koppla loss från tävlingen?', 'Uppgifterna står kvar. De som bara var med genom tävlingen kommer inte längre in, och förberedelserna syns inte längre på tävlingens sida.', 'Koppla loss', true)) return;
        const r = await post('UnlinkCompetition', {});
        if (!r.success) { failMsg(r); return; }
        await afterAppChange('Planeringen är inte längre kopplad till tävlingen.');
    }

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
        const canArea = !isReply && !m.deleted && areas().length > 0 && (m.mine || S.me.canEdit);
        const menu = (m.mine && !m.deleted) || canArea ? rowMenu([
            m.mine && !m.deleted && { label: 'Ändra', fn: () => editMessage(m) },
            m.mine && !m.deleted && canAddDocs() && { label: 'Bifoga dokument…', fn: () => pickDocuments({ attach: { kind: 'Message', id: m.id } }) },
            canArea && { label: 'Gäller området…', fn: () => openMessageArea(m) },
            m.mine && !m.deleted && { divider: true },
            m.mine && !m.deleted && { label: 'Ta bort', danger: true, fn: () => deleteMessage(m) },
        ]) : '';
        return `<div class="pl-msg${unread ? ' unread' : ''}" data-msg-id="${m.id}">
            <div class="d-flex justify-content-between gap-2"><div><span class="who">${esc(m.author)}</span>
            <span class="when"> · ${esc(fmtShort(m.createdAt))}${m.edited ? ' · ändrat' : ''}${m.viaEmail ? ' · via mejl' : ''}</span>${isReply ? '' : areaChip(m.areaId)}</div>${menu}</div>
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
        const top = all.filter(m => !m.replyTo && (m.kind !== 'Message' ? msgArea === '' : areaMatches(m.areaId, msgArea))).sort((a, b) => last(b) - last(a));
        if (!top.length) return msgArea !== '' ? '<p class="pl-help mt-3">Inga meddelanden här.</p>'
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
                ${!target && areas().length ? `<div><select id="plArea" class="form-select" style="max-width:15rem" aria-label="Meddelandet gäller">
                    <option value="">Gäller hela planeringen</option>${areas().map(a => `<option value="${a.id}"${String(a.id) === msgArea ? ' selected' : ''}>Gäller ${esc(a.name)}</option>`).join('')}</select></div>` : ''}
                <span class="pl-help small" id="plMentionNote">${pendingMentions.size ? 'Nämner: ' + esc([...pendingMentions.values()].join(', ')) : 'Den du nämner får ett mejl direkt.'}</span>
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
        const filter = areas().length ? `<div class="d-flex align-items-center gap-2 mb-2 pl-noprint"><label class="pl-help small" for="plMsgArea">Visa</label>
            <select id="plMsgArea" class="form-select form-select-sm" style="max-width:16rem">${areaFilterOptions(msgArea, 'Alla meddelanden')}</select></div>` : '';
        $('plBody').innerHTML = `<div class="pl-card">${replyTo ? '' : composerHtml()}${filter}<div class="pl-msgs" id="plThread">${threadHtml()}</div></div>`;
        $('plMsgArea')?.addEventListener('change', e => { msgArea = e.target.value; refreshThreadOnly(); });
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
                areaId: !replyTo && $('plArea') ? $('plArea').value : '' });
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
        // Omgång 3: via mejllänken beslutar man bara om sin egen uppgift (servern prövar detsamma).
        const decides = m.canDecideTasks || (!!TOKEN && m.isCurrent && t.assigneeId != null && t.assigneeId === S.me.participantId);
        if (t.status === 'Done') {
            return `<div class="pl-tkdone"><span><b>${esc(t.title)}</b>${t.assignee ? ' · ' + esc(t.assignee) : ''}</span>
                <span class="small">Utförd — bestämt på mötet${decides ? ` · <button type="button" class="btn btn-link btn-sm p-0 align-baseline pl-tkundo" data-tk-undo="${t.id}" data-tk-meeting="${m.id}">Ångra</button>` : ''}</span></div>`;
        }
        const ready = t.status === 'ReadyForReview';
        let side = '';
        if (decides && m.isCurrent) {
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
            // Omgång 3 (K8): en rad utan dag hoppas över — raderna har förifyllda klockslag, så "både dag och tid tomma"
            // hände aldrig och dialogen vägrade boka.
            if (row.hidden || !d) return;
            const day = window.hpskPlParseDate ? window.hpskPlParseDate(d) : d;
            if (!day || !/^\d{4}-\d{2}-\d{2}$/.test(day)) { err = 'Datumet går inte att läsa. Skriv till exempel 16 maj 2027.'; return; }
            if (!f || !t) { err = 'Skriv klockslag, till exempel 19 eller 19:00.'; return; }
            out.push(`${day} ${f}|${day} ${t}`);
        });
        return { options: out, err };
    }
    function openNewMeeting() {
        if (!MEET.canUse) return;
        formModal({
            title: 'Föreslå ett möte', okLabel: 'Skicka förslaget',
            html: `<div class="mb-3"><label class="form-label fw-semibold" for="nmTitle">Vad gäller mötet?</label>
                    <input id="nmTitle" class="form-control" maxlength="200" placeholder="Till exempel: Första träffen om SM 2028"></div>
                <div class="mb-3">
                    <label class="pl-radio"><input class="form-check-input me-2" type="radio" name="nmMode" value="ask" checked>
                        <b>Fråga vilka tider som passar</b><div class="pl-help small ms-4">Alla som är med får ett mejl och svarar <i>Passar / Kanske / Passar inte</i> för varje tid — utan att logga in.</div></label>
                    <label class="pl-radio"><input class="form-check-input me-2" type="radio" name="nmMode" value="book" id="nmBook">
                        <b>Tiden är bestämd — boka mötet</b><div class="pl-help small ms-4">Ingen röstning. Alla får ett mejl med tiden.</div></label></div>
                <div class="mb-2 fw-semibold" id="nmOptsHead">Föreslå några tider</div>
                <div id="nmOpts">${optRowHtml(0)}${optRowHtml(1)}${optRowHtml(2)}</div>
                <button type="button" class="btn btn-outline-secondary btn-sm mb-3" id="nmAdd">+ Ytterligare en tid</button>
                <div class="row g-2 mb-3" id="nmDeadlineRow"><div class="col-sm-6"><label class="form-label" for="nmDeadline">Svara senast (valfritt)</label>
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
                // "Tiden är bestämd": en tid, ingen svarsfrist — de andra raderna göms (och läses inte).
                const syncMode = () => {
                    const book = body.querySelector('#nmBook').checked;
                    body.querySelectorAll('[data-opt-row]').forEach((r, i) => { r.hidden = book && i > 0; });
                    body.querySelector('#nmAdd').hidden = book;
                    body.querySelector('#nmDeadlineRow').hidden = book;
                    body.querySelector('#nmOptsHead').textContent = book ? 'Tiden' : 'Föreslå några tider';
                    const ok = $('plFormOk'); if (ok) ok.textContent = book ? 'Boka mötet' : 'Skicka förslaget';
                };
                body.querySelectorAll('input[name="nmMode"]').forEach(r => r.addEventListener('change', syncMode));
                body.querySelector('#nmTitle').focus();
            },
            onOk: async body => {
                const title = body.querySelector('#nmTitle').value.trim();
                if (!title) return 'Skriv vad mötet gäller.';
                const { options, err } = readOptions(body);
                if (err) return err;
                const book = body.querySelector('#nmBook').checked;
                if (!options.length) return book ? 'Välj dag för mötet.' : 'Föreslå minst en tid.';
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
            TASKS.canManageAreas && TASKS.stationCount > 0 && { label: `En uppgift per station (${TASKS.stationCount})`, icon: 'bi-signpost-split', fn: seedStations },
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
                </select>${(TASKS.areas || []).length ? `<select class="form-select form-select-sm" id="tkArea" aria-label="Område" style="max-width:14rem">${areaFilterOptions(tkArea)}</select>` : ''}<span class="ms-auto pl-help small" id="tkCount"></span></div>
                <div class="pl-card p-0"><div class="table-responsive"><table class="table align-middle mb-0 pl-tktable">
                <thead><tr><th class="ps-3">Uppgift</th><th class="d-none d-md-table-cell">Vem</th><th class="d-none d-md-table-cell">Senast</th>
                    <th class="d-none d-sm-table-cell">Läge</th><th class="d-none d-lg-table-cell">Senaste raden</th><th class="pl-noprint"></th></tr></thead>
                <tbody id="tkRows"></tbody></table></div></div>`);
        }
        if (TASKS.canManageAreas && !(TASKS.areas || []).length)
            html.push(`<div class="pl-card mt-3" id="tkAreasHint"><h2>Områden</h2>
                <p class="mb-2">Dela upp arbetet i <b>områden</b> med var sin ansvarig — till exempel Servering, Materiel och Banor. Uppgifterna grupperas efter dem,
                    och den ansvarige kan ta och ändra områdets uppgifter.</p>
                <button type="button" class="btn btn-outline-primary btn-sm" data-area-new="1"><i class="bi bi-folder-plus me-1"></i>Nytt område…</button>
                <p class="pl-help small mb-0 mt-2">En del med egna deltagare, möten eller en egen tävling — till exempel en gren på en SM-vecka — är en <b>delplanering</b>. Den skapas under Översikt.</p></div>`);
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
        $('tkArea')?.addEventListener('change', e => { tkArea = e.target.value; renderTaskRows(); });
    }
    document.addEventListener('click', e => { if (e.target.closest('[data-area-new]')) { e.preventDefault(); openArea(null); } });

    function taskMatches(t, q) {
        const f = tkFilter;
        const okF = f === 'all' || (f === 'open' && t.status !== 'Done') || (f === 'mine' && t.mine && t.status !== 'Done')
            || (f === 'help' && t.status === 'NeedsHelp') || (f === 'ready' && t.status === 'ReadyForReview')
            || (f === 'overdue' && t.overdue) || (f === 'done' && t.status === 'Done') || (f === 'milestones' && t.isMilestone);
        const area = t.areaId ? (areaById(t.areaId) || {}).name : '';
        return okF && areaMatches(t.areaId, tkArea) && (!q || [t.title, t.assignee, t.report, area].some(v => (v || '').toLowerCase().includes(q)));
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
        if (t.scopeType) extra.push(t.stationUrl ? `<a href="${esc(t.stationUrl)}" data-station-link="${esc(t.scopeKey || '')}"><i class="bi bi-pencil-square me-1" aria-hidden="true"></i>Öppna station ${esc(t.scopeKey || '')} i konfiguratorn</a>` : esc(t.scopeType + ' ' + (t.scopeKey || '')));
        if (t.estimatedCost != null) extra.push(esc(kr(t.estimatedCost)));
        const docs = refsOn('Task', t.id).length;
        if (t.comments || docs) extra.push(`<button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-tk-thread="${t.id}">`
            + [t.comments ? `${t.comments} kommentar${t.comments === 1 ? '' : 'er'}` : '', docs ? `${docs} dokument` : ''].filter(Boolean).join(', ') + '</button>');
        // Bara på EGNA uppgifter — på varje rad blev det brus. Övriga bockas av via radens Åtgärder.
        const quick = decide && t.canDecide && t.mine ?`<button type="button" class="btn btn-sm btn-outline-success ms-2 pl-noprint" data-tk-quickdone="${t.id}" title="Markera som utförd">Klar ✓</button>` : '';
        const flag = t.isMilestone ? '<i class="bi bi-flag-fill text-primary me-1" aria-hidden="true"></i><span class="visually-hidden">Milstolpe: </span>' : '';
        const sent = t.sentToForbundetAt ? `<div class="small text-success">Skickad till förbundet ${esc(fmtShort(t.sentToForbundetAt))}${t.sentToForbundetBy ? ' — ' + esc(t.sentToForbundetBy) : ''}</div>`
            : (t.isMilestone && t.sendToForbundet ? '<div class="small text-body-secondary">Skickas till förbundet</div>' : '');
        return `<tr class="${t.mine && open ? 'pl-tkmine' : ''}${open ? '' : ' pl-tkdonerow'}"><td class="ps-3">${flag}<b>${esc(t.title)}</b>${quick}${sent}<div class="pl-help small">${origin}${extra.length ? ' · ' + extra.join(' · ') : ''}</div>${done}
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
        const searching = q || tkFilter !== 'open' || tkArea !== '';
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
                    S.me.canMoveAreas && familyTargets().length && { label: 'Flytta till en annan planering…', fn: () => openMoveArea(g.area) },
                    { divider: true },
                    { label: 'Ta bort området…', danger: true, fn: () => deleteArea(g.area) },
                ]) : (TASKS.canCreate && (TASKS.myAreas || []).includes(g.area.id) ? rowMenu([
                    { label: 'Ny uppgift i området…', fn: () => openTaskEdit(null, g.area.id) },
                ]) : '');
                const mineLead = (TASKS.myAreas || []).includes(g.area.id);
                parts.push(`<tr class="pl-areah"><td colspan="6" class="ps-3"><div class="d-flex flex-wrap align-items-center gap-2">
                    <b>${esc(g.area.name)}</b>${g.area.responsible ? `<span class="pl-help small">Ansvarig: ${esc(g.area.responsible)}${mineLead ? ' (du)' : ''}</span>` : ''}
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
    // Vem: en deltagare. Omgång 3 (Stefan 2026-10-24): en uppgift går bara till någon som är med — den som ska ha den
    // bjuds in först. Ett fritt namn från förr (t.ex. inläst ur tävlingens gamla lista) står kvar tills någon ändrar det.
    function whoField(t) {
        const free = t && !t.assigneeId && t.assigneeName;
        return `<label class="form-label" for="teWho">Vem gör det?</label>
            <select id="teWho" class="form-select">${peopleOptions(t ? t.assigneeId : null)}
                ${free ? `<option value="__name" selected>${esc(t.assigneeName)} (inte med i planeringen)</option>` : ''}</select>
            <input type="hidden" id="teName" value="${esc(free ? t.assigneeName : '')}">
            <div class="pl-help small mt-1">Bara de som är med i planeringen kan få en uppgift. Saknas någon — bjud in hen först under <i>Deltagare</i>.</div>`;
    }
    function wireWho() { }
    function whoParams(body) {
        const v = body.querySelector('#teWho').value;
        if (v === '__name') return { assignee: '', assigneeMemberId: '', assigneeName: body.querySelector('#teName').value.trim() };
        return { assignee: v, assigneeMemberId: '', assigneeName: '' };
    }
    // preset: { scopeType, scopeKey } för en ny uppgift som gäller en station (Stationerna-vyn).
    function openTaskEdit(t, presetArea, preset) {
        const c = competition();
        const days = t ? t.dueDaysBefore != null : false;
        const showDays = canDaysBefore() || days;
        const scopeNow = t ? t.scopeType : preset ? preset.scopeType : '';
        const showScope = !!c || !!scopeNow;
        const scopeOpts = '<option value="">—</option>' + (TASKS.scopeTypes || []).map(s => `<option${scopeNow === s ? ' selected' : ''}>${esc(s)}</option>`).join('');
        formModal({
            title: t ? 'Ändra uppgiften' : 'Ny uppgift', okLabel: t ? 'Spara' : 'Lägg till', wide: false,
            html: `<div class="mb-3"><label class="form-label fw-semibold" for="teTitle">Vad ska göras?</label>
                    <input id="teTitle" class="form-control" maxlength="300" value="${esc(t ? t.title : '')}"></div>
                ${(TASKS.areas || []).length ? `<div class="mb-3"><label class="form-label" for="teArea">Område</label><select id="teArea" class="form-select">${areaOptions(t ? t.areaId : presetArea || (parseInt(tkArea, 10) || null))}</select></div>` : ''}
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
                <details class="mb-1"${(t && (t.description || t.scopeType || t.estimatedCost != null)) || preset ? ' open' : ''}><summary class="mb-2">Mer om uppgiften</summary>
                    <div class="mb-3"><label class="form-label" for="teDesc">Beskrivning</label><textarea id="teDesc" class="form-control" rows="3" maxlength="4000">${esc(t ? t.description || '' : '')}</textarea></div>
                    ${showScope ? `<div class="row g-2 mb-3"><div class="col-6"><label class="form-label" for="teScope">Gäller</label><select id="teScope" class="form-select">${scopeOpts}</select></div>
                        <div class="col-6"><label class="form-label" for="teScopeKey">Nummer</label><input id="teScopeKey" class="form-control" maxlength="100" value="${esc(t ? t.scopeKey || '' : preset ? preset.scopeKey || '' : '')}" placeholder="t.ex. 3"></div></div>` : ''}
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
                }, w);
                if (daysMode && p.dueDaysBefore === '') return 'Skriv hur många dagar före.';
                const r = t ? await post('UpdateTask', Object.assign({ taskId: t.id }, p)) : await post('CreateTask', p);
                if (!r.success) return r.message;
                await reloadTasks();
                rerender();
                if (r.mailed) showMsg('Den som fick uppgiften har fått ett mejl.', 'success');
                else if (r.mailQueued) showMsg('Den som fick uppgiften har redan fått ett mejl i dag — hen får ett samlat mejl om sina nya uppgifter om en stund.', 'success');
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
    // Stefan 2026-10-25 (Expertgruppen): ett område är ett ANSVARSOMRÅDE — ett namn och en ansvarig. Det hör hemma här under
    // Uppgifter och skapas bara här (och av mallen). En del med egna deltagare, möten eller en egen tävling är en delplanering
    // (Översikt → Ny delplanering), aldrig ett område.
    function openArea(a) {
        formModal({
            title: a ? 'Ändra området' : 'Nytt område', okLabel: a ? 'Spara' : 'Lägg till', wide: false,
            html: `<div class="mb-3"><label class="form-label fw-semibold" for="arName">Områdets namn</label>
                    <input id="arName" class="form-control" maxlength="200" value="${esc(a ? a.name : '')}" placeholder="Till exempel Servering, Materiel eller Banor"></div>
                <div class="mb-1"><label class="form-label" for="arResp">Ansvarig (valfritt)</label>
                    <select id="arResp" class="form-select">${peopleOptions(a ? a.responsibleId : null)}<option value="__name"${a && !a.responsibleId && a.responsibleName ? ' selected' : ''}>Skriv ett namn…</option></select>
                    <input id="arRespName" class="form-control mt-2${a && !a.responsibleId && a.responsibleName ? '' : ' d-none'}" maxlength="200" value="${esc(a && !a.responsibleId ? a.responsibleName || '' : '')}">
                    <div class="pl-help small mt-1">Den ansvarige får ta, ändra och sätta läge på områdets uppgifter, och får veta när någon behöver hjälp eller är klar att pröva.</div></div>`,
            onOpen: body => {
                body.querySelector('#arResp').addEventListener('change', () =>
                    body.querySelector('#arRespName').classList.toggle('d-none', body.querySelector('#arResp').value !== '__name'));
                body.querySelector('#arName').focus();
            },
            onOk: async body => {
                const v = body.querySelector('#arResp').value;
                const r = await post('SaveArea', { areaId: a ? a.id : 0, name: body.querySelector('#arName').value,
                    responsibleId: v === '__name' ? '' : v, responsibleName: v === '__name' ? body.querySelector('#arRespName').value : '' });
                if (!r.success) return r.message;
                await Promise.all([reloadTasks(), reloadPeople()]); rerender(); return true;
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
        if (!await plConfirm('Ta bort området?', `${a.name}\n\n${n ? `De ${n} uppgifterna står kvar, utan område.` : 'Området har inga uppgifter.'} Meddelandena och dokumentkategorierna i området står kvar och gäller då hela planeringen.`, 'Ta bort', true)) return;
        const r = await post('DeleteArea', { areaId: a.id });
        if (!r.success) { failMsg(r); return; }
        if (tkArea === String(a.id)) tkArea = '';
        if (msgArea === String(a.id)) msgArea = '';
        await Promise.all([reloadTasks(), reloadPeople(), S.me.canReadMessages ? reloadMessages() : Promise.resolve(), reloadDocs()]); rerender();
    }

    // ---------------------------------------------------------------- mall, checklista, stationer, inklistring
    // Stefan 2026-10-25: mallen fyller inte i allt. Steg 1 = vilken mall (gren och sorts tävling), steg 2 = mallens
    // uppgifter med en ruta var — användaren bockar ur det hen inte vill ha. Båda stegen i samma dialog (formModal ritar
    // om innehållet när dialogen redan är öppen); "Tillbaka" behåller valen i steg 1.
    function openTemplate(prev) {
        const c = competition();
        // Omgång 3 (K8, K10): grenen förvald efter planeringens egen (stod alltid på Precision), och flera grenar på en gång
        // (en SM-vecka med fält och precision) — grenens egna områden får då grenens namn.
        const dk = (S.planning.disciplineKey || '').toLowerCase();
        const own = /falt/.test(dk) ? 'Faltskytte' : /spring/.test(dk) ? 'Springskytte' : /milsnabb/.test(dk) ? 'Milsnabb' : 'Precision';
        const wantDisc = v => prev && prev.discs ? prev.discs.indexOf(v) >= 0 : v === own;
        const discOpt = c ? '' : `<div class="mb-3"><div class="form-label">Gren — välj en eller flera</div>
            ${[['Precision', 'Precision'], ['Milsnabb', 'Milsnabb'], ['Faltskytte', 'Fältskytte'], ['Springskytte', 'Springskytte']].map(([v, l]) =>
                `<div class="form-check form-check-inline"><input class="form-check-input" type="checkbox" data-tm-disc value="${v}" id="tmD${v}"${wantDisc(v) ? ' checked' : ''}>
                    <label class="form-check-label" for="tmD${v}">${l}</label></div>`).join('')}
            <div class="pl-help small">Med flera grenar kommer de gemensamma områdena en gång. Grenens egna områden hamnar i grenens delplanering om det finns en,
                annars här med grenens namn, till exempel <i>Materiel – Fält</i>.</div></div>`;
        const sizes = [['klubb', 'Klubbtävling', 'Ingen ansökan.'],
            ['krets', 'Kretstävling', 'Ansökan till kretsstyrelsen.'],
            ['nationell', 'Nationell tävling', 'Ansökan via kretsen före 1 oktober året före; inbjudan till förbundet för kännedom.'],
            ['ldm', 'Landsdelsmästerskap', 'Ansökan via kretsen före 1 oktober året före; inbjudan till förbundet för godkännande.'],
            ['sm', 'Svenskt mästerskap', 'Förbundets uppdrag — ingen ansökan; inbjudan för godkännande och SM-annonsen.']];
        const wantSize = (v, i) => prev && prev.size ? prev.size === v : i === 0;
        formModal({
            title: 'Fyll i från en mall — steg 1 av 2', okLabel: 'Visa uppgifterna', wide: false,
            html: `<p>Mallen föreslår de vanliga områdena och uppgifterna för den sortens tävling${c ? ' i tävlingens gren' : ''}, enligt Skjuthandboken.
                    I nästa steg ser du uppgifterna och väljer vilka som ska läggas in.</p>
                ${discOpt}
                <div class="form-label">Vilken sorts tävling?</div>
                <div class="mb-1">${sizes.map(([v, l, h], i) =>
                    `<div class="form-check"><input class="form-check-input" type="radio" name="tmSize" id="tm${v}" value="${v}"${wantSize(v, i) ? ' checked' : ''}><label class="form-check-label" for="tm${v}">${esc(l)} <span class="pl-help small">— ${esc(h)}</span></label></div>`).join('')}</div>`,
            onOk: async body => {
                const size = body.querySelector('[name=tmSize]:checked').value;
                const discs = [...body.querySelectorAll('[data-tm-disc]:checked')].map(x => x.value);
                if (!c && !discs.length) return 'Välj minst en gren.';
                const r = await get('PreviewTemplate', { size, discipline: discs.join(',') });
                if (!r.success) return r.message;
                openTemplatePick({ size, discs, label: (sizes.find(x => x[0] === size) || [, size])[1] }, r.areas || []);
                // Inget returvärde: dialogen står kvar och visar steg 2.
            }
        });
    }
    function openTemplatePick(choice, areas) {
        const all = areas.reduce((n, a) => n + a.items.filter(i => !i.exists).length, 0);
        const areaHtml = (a, ai) => {
            const fresh = a.items.filter(i => !i.exists);
            return `<div class="mb-3" data-tm-area="${ai}">
                <div class="form-check border-bottom pb-1 mb-1">
                    <input class="form-check-input" type="checkbox" id="tmA${ai}" data-tm-areabox="${ai}"${fresh.length ? ' checked' : ' disabled'}>
                    <label class="form-check-label fw-semibold" for="tmA${ai}">${esc(a.area)}</label>
                    ${a.subName ? `<span class="badge text-bg-primary-subtle text-primary-emphasis border ms-1" data-tm-sub>i delplaneringen ${esc(a.subName)}</span>` : ''}
                    ${a.exists ? '<span class="badge text-bg-light border ms-1">finns redan</span>' : ''}
                </div>
                ${a.items.map(i => `<div class="form-check ms-3">
                    <input class="form-check-input" type="checkbox" id="tmI${esc(i.key).replace(':', '_')}" data-tm-item="${esc(i.key)}" data-tm-of="${ai}"${i.exists ? ' disabled' : ' checked'}>
                    <label class="form-check-label${i.exists ? ' text-body-secondary' : ''}" for="tmI${esc(i.key).replace(':', '_')}">${esc(i.title)}${i.milestone ? ' <span class="badge text-bg-info">milstolpe</span>' : ''}
                        <span class="pl-help small">${i.exists ? '— finns redan, läggs inte in igen' : i.due ? '— senast ' + esc(fmtDay(i.due)) : ''}</span></label>
                </div>`).join('')}
            </div>`;
        };
        formModal({
            title: 'Fyll i från en mall — steg 2 av 2', okLabel: 'Lägg till valda', wide: true,
            html: `<p class="mb-2">${esc(choice.label)}${choice.discs && choice.discs.length ? ' · ' + esc(choice.discs.map(d => d === 'Faltskytte' ? 'Fältskytte' : d).join(', ')) : ''}.
                    Bocka ur de uppgifter ni inte vill ha. Sista dagarna räknas ${esc(daysWord())}.
                    Ett område läggs bara till när minst en av dess uppgifter är vald.</p>
                <div class="d-flex flex-wrap align-items-center gap-2 mb-2">
                    <button type="button" class="btn btn-sm btn-outline-secondary" data-tm-back><i class="bi bi-arrow-left me-1" aria-hidden="true"></i>Tillbaka</button>
                    <button type="button" class="btn btn-sm btn-outline-secondary" data-tm-all="1">Välj alla</button>
                    <button type="button" class="btn btn-sm btn-outline-secondary" data-tm-all="0">Välj ingen</button>
                    <span class="ms-auto small" id="tmCount" aria-live="polite"></span>
                </div>
                ${all ? '' : '<div class="alert alert-info py-2">Alla mallens uppgifter finns redan i planeringen.</div>'}
                <div style="max-height:55vh;overflow:auto" class="border rounded p-2">${areas.map(areaHtml).join('')}</div>`,
            onOpen: body => {
                const items = () => [...body.querySelectorAll('[data-tm-item]:not(:disabled)')];
                const sync = () => {
                    body.querySelectorAll('[data-tm-areabox]').forEach(box => {
                        const mine = items().filter(x => x.getAttribute('data-tm-of') === box.getAttribute('data-tm-areabox'));
                        const on = mine.filter(x => x.checked).length;
                        box.checked = mine.length > 0 && on === mine.length;
                        box.indeterminate = on > 0 && on < mine.length;
                    });
                    const on = items().filter(x => x.checked).length;
                    body.querySelector('#tmCount').textContent = `${on} av ${all} uppgifter valda`;
                };
                body.addEventListener('change', e => {
                    const box = e.target.closest('[data-tm-areabox]');
                    if (box) items().filter(x => x.getAttribute('data-tm-of') === box.getAttribute('data-tm-areabox')).forEach(x => { x.checked = box.checked; });
                    sync();
                });
                body.addEventListener('click', e => {
                    const b = e.target.closest('[data-tm-all],[data-tm-back]');
                    if (!b) return;
                    if (b.hasAttribute('data-tm-back')) { openTemplate({ size: choice.size, discs: choice.discs }); return; }
                    items().forEach(x => { x.checked = b.getAttribute('data-tm-all') === '1'; });
                    sync();
                });
                sync();
            },
            onOk: async body => {
                const keys = [...body.querySelectorAll('[data-tm-item]:checked:not(:disabled)')].map(x => x.getAttribute('data-tm-item'));
                if (!keys.length) return 'Välj minst en uppgift, eller tryck Avbryt.';
                const r = await post('SeedTemplate', { size: choice.size, discipline: (choice.discs || []).join(','), items: keys.join('\n') });
                if (!r.success) return r.message;
                await reloadTasks(); rerender();
                const subs = (r.inSubs || []).map(x => `${x.tasks} i ${x.name}`).join(', ');
                showMsg(r.tasks ? esc(`${r.areas ? r.areas + ' nya områden och ' : ''}${r.tasks} uppgifter lades till${subs ? ' — varav ' + subs : ''}.`) : 'Ingenting lades till.', r.tasks ? 'success' : 'info');
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
        const n = TASKS.stationCount || 0;
        if (!await plConfirm('En uppgift per station?', `"Bygg station 1" … "Bygg station ${n}" läggs i området Bygga banan, och den som är ansvarig för en station får dess uppgift. Milstolpen "Banläggaren godkänner stationerna" läggs till. Stationer som redan har en uppgift hoppas över.`, 'Lägg till')) return;
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
            onOpen: body => {
                body.querySelector('#psText').addEventListener('input', () => { ready = false; $('plFormOk').textContent = 'Visa hur det blir'; body.querySelector('#psPreview').innerHTML = ''; });
            },
            onOk: async body => {
                const params = { text: body.querySelector('#psText').value, areaId: body.querySelector('#psArea') ? body.querySelector('#psArea').value : '' };
                if (!ready) {
                    const r = await post('PasteTasks', params);
                    body.querySelector('#psPreview').innerHTML = (r.rows || []).length ? `<table class="table table-sm"><thead><tr><th>Uppgift</th><th>Vem</th><th>Sista dag</th></tr></thead><tbody>${r.rows.map(x =>
                        `<tr class="${x.error ? 'table-danger' : ''}"><td>${esc(x.title)}${x.error ? `<div class="small text-danger">${esc(x.error)}</div>` : ''}</td>
                        <td>${esc(x.who || '')}${x.who && !x.matched ? ' <span class="pl-help small">(inte med i planeringen — läggs in utan ansvarig)</span>' : ''}</td><td>${esc(x.due || '')}</td></tr>`).join('')}</tbody></table>` : '';
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
    function canAddDocs() { return !!(DOCS && DOCS.canAdd); }
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
                ${ref.canDetach ? `<button type="button" class="pl-noprint" data-doc-detach="${ref.refId}" title="Ta bort bifogningen" aria-label="Ta bort bifogningen av ${esc(doc.title)}">✕</button>` : ''}</span>`).join('')}
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
        if (r.kind === 'Decision') return `Förslag: ${t}`;
        return t;
    }
    function gotoRef(code) {
        const [kind, id, meetingId] = code.split(':').map(x => parseInt(x, 10) || x);
        if (kind === 'Meeting' || kind === 'Topic') { if (meetingId) goMeeting(meetingId); return; }
        if (kind === 'Message') { go('meddelanden'); setTimeout(() => document.querySelector(`[data-msg-id="${id}"]`)?.scrollIntoView({ block: 'center' }), 60); return; }
        if (kind === 'Task') { const t = findTask(id); if (t) openThread(t); else go('uppgifter'); return; }
        if (kind === 'Area') go('uppgifter');
        if (kind === 'Decision') { go('beslut'); setTimeout(() => document.querySelector(`[data-dec-id="${id}"]`)?.scrollIntoView({ block: 'center' }), 60); }
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
                { label: 'Ändra namn och område…', fn: () => openCategory(g.cat) },
                gi > 0 && { label: 'Flytta upp', fn: () => moveCategory(g.cat, -1) },
                gi < cats.length - 1 && { label: 'Flytta ner', fn: () => moveCategory(g.cat, 1) },
                { divider: true },
                { label: 'Ta bort kategorin…', danger: true, fn: () => deleteCategory(g.cat) },
            ]) : '';
            const title = g.cat ? g.cat.name : (cats.length ? 'Utan kategori' : 'Dokument');
            return `<div class="pl-card mb-3"><div class="pl-cardh"><h2><i class="bi bi-folder2 me-2 text-secondary" aria-hidden="true"></i>${esc(title)}
                    <span class="pl-help small fw-normal">${g.docs.length}</span>${g.cat ? areaChip(g.cat.areaId) : ''}</h2>${menu}</div>
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
            d.canEdit && { label: 'Ändra namn, beskrivning och kategori…', fn: () => openDocEdit(d) },
            canAddDocs() && { label: 'Bifoga på ett möte eller en uppgift…', fn: () => openAttachFromDoc(d) },
            d.canRemove && { divider: true },
            d.canRemove && { label: 'Ta bort dokumentet…', danger: true, fn: () => removeDoc(d) },
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
    // En kategori kan höra till ett område — flyttas området till en annan planering i familjen följer kategorin med.
    function openCategory(c) {
        const withArea = areas().length > 0;
        formModal({
            title: c ? 'Ändra kategorin' : 'Ny kategori', okLabel: c ? 'Spara' : 'Lägg till', wide: false,
            html: `<label class="form-label" for="dcCatName">Vad heter kategorin?</label>
                <input id="dcCatName" class="form-control" maxlength="200" value="${esc(c ? c.name : '')}" placeholder="Till exempel Inbjudan, Banor eller Avtal">
                ${withArea ? `<div class="mt-3"><label class="form-label" for="dcCatArea">Hör till området (valfritt)</label>
                    <select id="dcCatArea" class="form-select"><option value="0">Hela planeringen</option>${areas().map(a => `<option value="${a.id}"${c && c.areaId === a.id ? ' selected' : ''}>${esc(a.name)}</option>`).join('')}</select>
                    <div class="pl-help small mt-1">Flyttas området till en annan planering i familjen följer kategorin och dess dokument med.</div></div>` : ''}`,
            onOpen: body => { body.querySelector('#dcCatName').focus(); },
            onOk: async body => {
                const r = await post('SaveDocumentCategory', Object.assign({ categoryId: c ? c.id : '', name: body.querySelector('#dcCatName').value },
                    body.querySelector('#dcCatArea') ? { areaId: body.querySelector('#dcCatArea').value } : {}));
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
                // onPick som svarar false har själv öppnat nästa dialog i samma fönster — stäng det inte då.
                if (opts.onPick) return opts.onPick(ids) === false ? false : true;
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
        const decs = (DEC ? DEC.decisions : []).filter(x => x.canAttach);
        const opt = (k, id, label) => has.has(k + ':' + id) ? '' : `<option value="${k}:${id}">${esc(label)}</option>`;
        const groups = [
            meetings.length && `<optgroup label="Möten">${meetings.map(m => opt('Meeting', m.id, m.title + (m.startsAt ? ' · ' + fmtDay(m.startsAt) : ''))).join('')}</optgroup>`,
            tasks.length && `<optgroup label="Uppgifter">${tasks.map(t => opt('Task', t.id, t.title)).join('')}</optgroup>`,
            areas.length && `<optgroup label="Områden">${areas.map(a => opt('Area', a.id, a.name)).join('')}</optgroup>`,
            decs.length && `<optgroup label="Förslag">${decs.map(x => opt('Decision', x.id, x.title)).join('')}</optgroup>`,
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
                else if (r.mailQueued) showMsg('Uppgiften är tillagd. Den som fick den får ett samlat mejl om sina nya uppgifter om en stund.', 'success');
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
                ${r.pending ? `<p class="mb-0">${esc(r.name)} är inbjuden men har inte öppnat planeringen än. E-post och telefon visas när hen har varit inne.</p>` : `
                ${line('bi-envelope', 'E-post', r.email, 'mailto:' + r.email)}
                ${r.isGuest ? '' : line('bi-telephone', 'Telefon', r.phone, 'tel:' + tel)}
                <p class="pl-help small mt-2 mb-0">Uppgifterna är till för planeringen — dela dem inte vidare.</p>`}`,
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
            html: `<p>Den nya personen får samma nivå (<b>${esc(p.accessLabel)}</b>)${p.represents ? ` och företräder samma (<b>${esc(p.represents)}</b>)` : ''}
                och tar över de öppna uppgifterna, ansvaret för områden och rollerna från ${esc(p.name)}.
                ${esc(p.name)} lämnar planeringen; hens meddelanden och avslutade uppgifter står kvar. Den nya får ett mejl med inbjudan.</p>
                <div id="rpFamily"></div>
                <div class="mb-2"><label class="pl-radio d-inline-block me-3"><input type="radio" name="rpMode" value="member" class="form-check-input me-2" checked> En medlem</label>
                    <label class="pl-radio d-inline-block"><input type="radio" name="rpMode" value="email" class="form-check-input me-2"> Någon utan konto (e-post)</label></div>
                <div id="rpMember"><label class="form-label" for="rpQ">Sök på namn</label>
                    <input id="rpQ" class="form-control mb-2" placeholder="Till exempel Anna Svensson" autocomplete="off">
                    <div id="rpHits" class="pl-help">Skriv minst två bokstäver.</div></div>
                <div id="rpEmail" class="d-none">
                    <div class="mb-2"><label class="form-label" for="rpName">Namn</label><input id="rpName" class="form-control" maxlength="200" autocomplete="off"></div>
                    <div class="mb-1"><label class="form-label" for="rpMail">E-post</label><input id="rpMail" class="form-control" type="email" inputmode="email" autocomplete="off"></div></div>`,
            onOpen: body => {
                // Omgång 3: samma person i familjens andra planeringar — byt där också (förvalt där du får).
                get('ReplaceFamily', { participantId: p.id }).then(f => {
                    if (!f.success || !(f.plannings || []).length) return;
                    body.querySelector('#rpFamily').innerHTML = `<div class="pl-card p-2 mb-3"><b>${esc(p.name)} är också med i</b>
                        ${f.plannings.map(x => `<div class="form-check mt-1"><input class="form-check-input" type="checkbox" id="rpF${x.planningId}" data-rp-family="${x.planningId}"${x.canReplace ? ' checked' : ' disabled'}>
                            <label class="form-check-label" for="rpF${x.planningId}">${esc(x.name)} <span class="pl-help small">(${esc(x.access)})</span>
                            ${x.canReplace ? '' : '<span class="pl-help small"> — byts ut av den planeringens sammankallande</span>'}</label></div>`).join('')}
                        <div class="pl-help small mt-1">Byt ut där också. Den nya får den nivå ${esc(p.name)} har i varje planering.</div></div>`;
                });
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
                const alsoIn = [...body.querySelectorAll('[data-rp-family]:checked')].map(c => c.dataset.rpFamily).join(',');
                const r = await post('ReplaceParticipant', Object.assign(email
                    ? { participantId: p.id, name: body.querySelector('#rpName').value, email: body.querySelector('#rpMail').value }
                    : { participantId: p.id, memberId: chosen }, { alsoIn }));
                if (!r.success) return r.message;
                await reloadPeople(); viewPeople();
                const fam = (r.replacedIn || []).length ? ` Också i ${esc(r.replacedIn.join(', '))}.` : '';
                const notFam = (r.notReplacedIn || []).length ? ` Inte i ${esc(r.notReplacedIn.join(', '))}.` : '';
                showMsg(`${esc(email ? body.querySelector('#rpName').value : chosenName)} har ersatt ${esc(p.name)}.${fam}${notFam}`
                    + (r.mailed ? ' Ett mejl med inbjudan är skickat.' : ' Men mejlet kunde inte skickas — skicka inbjudan igen under Deltagare.'),
                    r.mailed && !notFam ? 'success' : 'warning');
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

    // Stefan B11: vem som sköter en delplanering — en klubb, en krets eller fri text.
    async function openSubHost() {
        const o = await get('HostOptions');
        if (!o.success) { failMsg(o); return; }
        const par = S.planning.parent || {};
        const cur = par.hostClubId ? '0:' + par.hostClubId : par.hostRegionId ? '1:' + par.hostRegionId : (par.hostText ? 'text' : '');
        formModal({
            title: 'Vem sköter delplaneringen?', okLabel: 'Spara', wide: false,
            html: `<select id="shHost" class="form-select"><option value="">—</option>${o.hosts.map(h => `<option value="${h.ownerType}:${h.ownerId}"${cur === h.ownerType + ':' + h.ownerId ? ' selected' : ''}>${esc(h.name)}</option>`).join('')}
                    <option value="text"${cur === 'text' ? ' selected' : ''}>Annan (skriv)</option></select>
                <input id="shText" class="form-control mt-2${cur === 'text' ? '' : ' d-none'}" maxlength="200" value="${esc(par.hostText || '')}">
                <div class="pl-help small mt-1">Den klubb eller krets som står för delen. Klubbarna och kretsarna som någon i planeringen företräder.</div>`,
            onOpen: body => body.querySelector('#shHost').addEventListener('change', e =>
                body.querySelector('#shText').classList.toggle('d-none', e.target.value !== 'text')),
            onOk: async body => {
                const v = body.querySelector('#shHost').value;
                const [ht, hid] = v && v !== 'text' ? v.split(':') : ['', ''];
                const r = await post('SetSubHost', { hostType: ht, hostId: hid, hostText: v === 'text' ? body.querySelector('#shText').value : '' });
                if (!r.success) return r.message;
                await reloadPeople(); rerender(); return true;
            },
        });
    }
    document.addEventListener('click', e => { if (e.target.closest('[data-sub-host]')) { e.preventDefault(); openSubHost(); } });

    // Kopiera planeringen (P3): formen följer med, aldrig personerna eller innehållet.
    function openCopyPlanning() {
        const p = S.planning;
        const subs = (p.subPlannings || []).length;
        let chosen = 0, chosenName = '', timer = 0;
        formModal({
            title: 'Kopiera planeringen', okLabel: 'Kopiera', wide: true,
            html: `<p class="mb-2">Kopian får samma <b>områden, uppgifter och milstolpar</b> och bemanningens <b>roller, dagar och behov</b>.
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
                </div>
                ${subs ? `<div class="form-check mt-3"><input class="form-check-input" type="checkbox" id="cpSubs" checked>
                    <label class="form-check-label fw-semibold" for="cpSubs">Ta med delplaneringarna (${subs})</label>
                    <div class="pl-help small">Varje delplanering kopieras med sina områden och uppgifter, med samma ledning som kopian, och flyttas lika många dagar.</div></div>` : ''}`,
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
                    name: body.querySelector('#cpName').value.trim(), targetStart: dateValue(body.querySelector('#cpStart')),
                    targetEnd: dateValue(body.querySelector('#cpEnd')), recipientMemberId: other ? chosen : 0,
                    includeMe: other && !body.querySelector('#cpIncludeMe').checked ? '0' : '1',
                    includeSubs: body.querySelector('#cpSubs') && !body.querySelector('#cpSubs').checked ? '0' : '1',
                });
                if (!r.success) return r.message || 'Det gick inte att kopiera.';
                const lost = [];
                if (r.datesDropped) lost.push(`${r.datesDropped} fasta datum följde inte med (ingen första dag att räkna från)`);
                if (r.daysDropped) lost.push(`${r.daysDropped} av bemanningens dagar följde inte med`);
                const what = `${r.tasks} uppgifter i ${r.areas} områden, ${r.roles} roller, ${r.days} dagar och ${r.needs} behov`
                    + (r.subPlannings ? ` — och ${r.subPlannings === 1 ? 'en delplanering' : r.subPlannings + ' delplaneringar'}` : '');
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

    // ================================================================ Dina notiser (överst i Inställningar sedan 2026-10-25)
    function notifyHtml() {
        const cur = S.me.notifyMode;
        return `<div id="plNotifySec"><h2 class="h5 fw-bold mb-2">Dina notiser</h2><div class="pl-card" style="max-width:720px"><h2>Hur vill du få veta vad som händer?</h2>
            <p class="pl-help">Gäller bara den här planeringen. Du kan ändra när som helst.</p>
            ${S.notifyModes.map(n => `<label class="pl-radio"><input type="radio" name="plNotify" value="${n.value}" class="form-check-input me-2"${n.value === cur ? ' checked' : ''}>
                <b>${esc(n.label)}</b>${n.value === 'Daily' ? ' <span class="pl-help small">(förval)</span>' : ''}<div class="pl-help small ms-4">${esc(n.help)}</div></label>`).join('')}
            <div id="plNotifySaved" class="pl-help small"></div></div>
            ${S.me.icsUrl ? `<div class="pl-card" style="max-width:720px"><h2>Mötena i din kalender</h2>
                <p>Lägg planeringens bokade möten i din egen kalender (telefonen, Outlook, Google). Flyttas ett möte flyttas det i kalendern också.</p>
                <a class="btn btn-outline-secondary" href="${esc(S.me.icsUrl)}"><i class="bi bi-calendar-plus me-1"></i>Lägg till kalendern</a>
                <div class="mt-2"><label class="form-label small" for="plIcsUrl">Eller kopiera adressen och prenumerera på den i din kalender:</label>
                <input id="plIcsUrl" class="form-control form-control-sm" readonly value="${esc(S.me.icsUrl)}"></div></div>` : ''}
            ${!S.me.isGuest ? `<div class="pl-card" style="max-width:720px"><h2>Notiser i telefonen</h2><p class="pl-help">Notiser i telefonen kräver att du slagit på notiser för pistol.nu på den telefon eller dator du vill få dem på.
                ${S.me.pushConfigured ? '' : 'Notiser i telefonen är inte påslagna på den här sajten ännu — du får mejl.'}</p>
                <a class="btn btn-outline-primary" href="/user-profile-page#notiser"><i class="bi bi-bell me-1"></i>Slå på notiser på Min sida</a></div>` : ''}</div>`;
    }
    function bindNotify() {
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
        const mine = S.me.isParticipant ? notifyHtml() : '';
        if (!S.me.canEdit) {
            $('plBody').innerHTML = mine;
            bindNotify();
            return;
        }
        $('plBody').innerHTML = mine + `${mine ? '<h2 class="h5 fw-bold mb-2 mt-4">Planeringen</h2>' : ''}<div class="pl-card" style="max-width:720px">
            <div class="mb-3"><label class="form-label fw-semibold" for="stName">Namn</label><input id="stName" class="form-control" maxlength="200" value="${esc(p.name)}"></div>
            <div class="mb-3"><label class="form-label fw-semibold" for="stTarget">Ungefär när och var</label>
                <input id="stTarget" class="form-control" maxlength="200" value="${esc(p.targetText || '')}" placeholder="Till exempel: augusti 2028, Skillingaryd?"></div>
            <div class="row g-2 mb-3"><div class="col-sm-6"><label class="form-label" for="stStart">Första dagen (om den är bestämd)</label><input id="stStart" class="form-control" value="${esc(p.targetStart || '')}"></div>
                <div class="col-sm-6"><label class="form-label" for="stEnd">Sista dagen</label><input id="stEnd" class="form-control" value="${esc(p.targetEnd || '')}"></div></div>
            <div class="mb-3"><label class="form-label fw-semibold" for="stGroup">Gruppens namn i besluten (valfritt)</label>
                <input id="stGroup" class="form-control" maxlength="100" value="${esc(p.groupName || '')}" placeholder="Till exempel: SSM-kommittén">
                <div class="pl-help small">Är gruppen en kommitté — till exempel landsdelens SM-kommitté — står namnet i förslagen och besluten:
                    <i>beslutat av SSM-kommittén</i>. Tomt: <i>gruppens beslut</i>.</div></div>
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
            ${p.parent && S.me.canManage ? `<div class="pl-card" style="max-width:720px" id="stSubHost"><h2>Delplaneringen</h2>
                <p class="mb-2">Del av <b>${esc(p.parent.name)}</b>. Sköts av: <b>${esc(p.parent.host || '—')}</b></p>
                <button type="button" class="btn btn-outline-primary btn-sm" data-sub-host="1">Ändra vem som sköter delplaneringen…</button></div>` : ''}
            ${S.me.isSiteAdmin ? `<div class="pl-card" style="max-width:720px"><h2>Förbundets adress</h2>
                <p class="pl-help small">Gäller alla planeringar: dit går milstolpar som skickas till förbundet (till exempel inbjudan i tid till tidningen). Syns bara för sajtadministratörer.</p>
                <div class="d-flex gap-2 flex-wrap"><input id="stForbundet" type="email" class="form-control" style="max-width:320px" maxlength="256" value="${esc((TASKS && TASKS.forbundetEmail) || '')}" placeholder="namn@pistolskytteforbundet.se">
                <button class="btn btn-outline-primary" id="stForbundetSave">Spara adressen</button></div><span id="stForbundetSaved" class="pl-help small"></span></div>` : ''}`;
        bindNotify();
        datePicker($('stStart')); datePicker($('stEnd'));
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
        $('stSave').onclick = async () => {
            const params = { name: $('stName').value, targetText: $('stTarget').value, targetStart: dateValue($('stStart')), targetEnd: dateValue($('stEnd')),
                groupName: $('stGroup').value.trim(), groupNameSet: '1' };
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

    // ================================================================ Förslag och beslut (P3, omgjort 2026-10-24)
    // Den som lägger fram ett förslag väljer vem som beslutar: GRUPPEN (de som deltar svarar Ja eller Nej, och den
    // som kan ändra planeringen registrerar beslutet) eller EN STYRELSE i en klubb eller krets som någon här
    // företräder (förslaget läggs i styrelsens ärendekö med sina bilagor; gruppen svarar inte, utan skriver i
    // meddelandena). Aldrig båda. Styrelsens läge läses ur deras ärendekö.
    async function reloadDecisions() { const r = await get('Decisions'); if (r.success) DEC = r; }
    function findDecision(id) { return DEC.decisions.find(d => d.id === id); }
    const isBoardDec = d => d.deciderKind === 'Board';

    function decisionAnswers(d) {
        if (isBoardDec(d)) return '';
        const yes = d.responses.filter(r => r.answer === 'Yes'), no = d.responses.filter(r => r.answer === 'No');
        const who = r => esc(r.name) + (r.represents ? ` <span class="pl-help">(${esc(r.represents)})</span>` : '')
            + (r.beforeEdit ? ' <span class="pl-help small">· svarade före ändringen</span>' : '')
            + (r.comment ? `<div class="pl-help small ms-3" style="white-space:pre-wrap;">${esc(r.comment)}</div>` : '');
        const list = (rows, label, icon) => rows.length
            ? `<div class="mb-1"><i class="bi ${icon} me-1" aria-hidden="true"></i><b>${label} (${rows.length})</b><ul class="mb-1 ps-4">${rows.map(r => `<li>${who(r)}</li>`).join('')}</ul></div>` : '';
        return list(yes, 'Ja', 'bi-check-circle text-success') + list(no, 'Nej', 'bi-x-circle text-danger')
            + (d.waiting && d.waiting.length ? `<div class="pl-help small">Väntar på svar från ${esc(d.waiting.join(', '))}.</div>` : '')
            + (!d.responses.length && !(d.waiting || []).length ? '<div class="pl-help small">Inga svar ännu.</div>' : '');
    }

    function boardBlock(d) {
        const b = d.board; if (!b) return '';
        const tone = b.state === 'decided' || b.state === 'handled' ? 'success'
            : b.state === 'unsent' ? 'warning' : b.state === 'rejected' || b.state === 'gone' || b.state === 'withdrawn' ? 'secondary' : 'info';
        const sent = b.sentAt ? ` <span class="pl-help small">· skickat ${esc(fmtShort(b.sentAt))}${b.sentBy ? ' av ' + esc(b.sentBy) : ''}</span>` : '';
        const send = b.state === 'unsent' && d.canSendToBoard
            ? `<div class="mt-2"><button class="btn btn-sm btn-primary" data-dec-send="${d.id}"><i class="bi bi-send me-1"></i>Skicka till styrelsen</button></div>` : '';
        const decision = b.state === 'decided'
            ? `<div class="mt-1"><b>${esc(b.outcome || '')}</b>${b.decision ? ` — <span style="white-space:pre-wrap;">${esc(b.decision)}</span>` : ''}</div>`
            : b.decision ? `<div class="mt-1"><b>Styrelsens beslut:</b> <span style="white-space:pre-wrap;">${esc(b.decision)}</span></div>` : '';
        return `<div class="alert alert-${tone} py-2 mb-2"><div><i class="bi bi-people me-1" aria-hidden="true"></i><b>Styrelsen i ${esc(b.org)}</b>${sent}</div>
            <div>${esc(b.label)}</div>${decision}${send}</div>`;
    }

    function decisionState(d) {
        if (d.status === 'Decided') return `<span class="pl-state ${d.outcome === 'Rejected' ? 'off' : 'ok'}">${esc(d.outcomeLabel || 'Avgjort')}</span>`;
        if (d.status === 'Withdrawn') return '<span class="pl-state off">Tillbakadraget</span>';
        if (isBoardDec(d)) return d.board && d.board.state === 'unsent' ? '<span class="pl-state wait">Inte skickat</span>' : '<span class="pl-state blue">Hos styrelsen</span>';
        return '<span class="pl-state blue">Öppet</span>';
    }

    function decisionCard(d) {
        const board = isBoardDec(d);
        const resend = board && d.canSendToBoard && d.board && d.board.state !== 'unsent';
        const menu = rowMenu([
            d.canEdit && { label: 'Ändra förslaget…', fn: () => openDecisionEdit(d) },
            d.canAttach && canAddDocs() && { label: 'Bifoga dokument…', fn: () => pickDocuments({ attach: { kind: 'Decision', id: d.id } }) },
            resend && { label: 'Skicka till styrelsen igen', fn: () => sendDecision(d) },
            d.canDecide && { label: 'Registrera beslutet…', fn: () => openDecide(d) },
            d.canReopen && { label: 'Ångra beslutet', fn: () => reopenDecision(d) },
            d.canWithdraw && { divider: true },
            d.canWithdraw && { label: 'Dra tillbaka förslaget…', danger: true, fn: () => withdrawDecision(d) },
        ]);
        const who = board ? `Styrelsen i ${esc(d.board ? d.board.org : '')} beslutar` : (S.planning.groupName ? esc(S.planning.groupName) + ' beslutar' : 'Gruppen beslutar');
        const decided = !board && d.status === 'Decided' ? `<div class="mb-2"><b>${esc(d.outcomeLabel)}</b> — ${d.decidedGroupName ? 'beslutat av ' + esc(d.decidedGroupName) : 'gruppens beslut'}
            <span class="pl-help small">· ${esc(fmtShort(d.decidedAt))}${d.decidedBy ? ', registrerat av ' + esc(d.decidedBy) : ''}</span>
            ${d.decisionNote ? `<div style="white-space:pre-wrap;">${esc(d.decisionNote)}</div>` : ''}</div>` : '';
        let answer = '';
        if (d.canRespond) {
            const m = d.mine;
            answer = `<div class="pl-noprint border-top pt-2 mt-2"><div class="fw-semibold mb-1">Ditt svar</div>
                ${m && m.beforeEdit ? '<div class="alert alert-warning py-1 px-2 small mb-2">Förslaget ändrades efter att du svarade. Svara gärna igen.</div>' : ''}
                <input type="text" class="form-control form-control-sm mb-2" id="decC${d.id}" maxlength="1000" placeholder="Kommentar (valfri)" value="${esc(m ? m.comment || '' : '')}" aria-label="Kommentar till ditt svar">
                <div class="d-flex gap-2 flex-wrap">
                    <button class="btn btn-sm ${m && m.answer === 'Yes' ? 'btn-success' : 'btn-outline-success'}" data-dec-answer="${d.id}" data-answer="Yes"><i class="bi bi-check-lg me-1"></i>Ja</button>
                    <button class="btn btn-sm ${m && m.answer === 'No' ? 'btn-danger' : 'btn-outline-danger'}" data-dec-answer="${d.id}" data-answer="No"><i class="bi bi-x-lg me-1"></i>Nej</button>
                    ${m ? `<span class="pl-help small align-self-center">Du har svarat ${m.answer === 'No' ? 'Nej' : 'Ja'}. Tryck igen för att ändra.</span>` : ''}
                </div></div>`;
        } else if (board && d.status === 'Open') {
            answer = '<div class="pl-help small border-top pt-2 mt-2">Styrelsen beslutar. Vill du säga något om förslaget skriver du det under Meddelanden.</div>';
        }
        return `<div class="pl-card" data-dec-id="${d.id}">
            <div class="d-flex justify-content-between gap-2 align-items-start"><div><h2 class="mb-1">${esc(d.title)}</h2>
                <div class="pl-help small">${decisionState(d)} ${who} · föreslaget av ${esc(d.proposedBy)} ${esc(fmtShort(d.proposedAt))}${d.editedAt ? ' · ändrat ' + esc(fmtShort(d.editedAt)) : ''}</div></div>${menu}</div>
            ${d.body ? `<div class="mt-2 mb-2" style="white-space:pre-wrap;">${esc(d.body)}</div>` : ''}
            ${docChips('Decision', d.id)}
            ${decided}${boardBlock(d)}
            <div class="mt-2">${decisionAnswers(d)}</div>${answer}</div>`;
    }

    function viewDecisions() {
        setHead('Förslag och beslut', esc(S.planning.name));
        setActions([DEC.canPropose && { label: 'Nytt förslag…', icon: 'bi-lightbulb', fn: () => openDecisionEdit(null) }]);
        const all = DEC.decisions;
        const open = all.filter(d => d.status === 'Open'), decided = all.filter(d => d.status === 'Decided'), withdrawn = all.filter(d => d.status === 'Withdrawn');
        const html = [`<p class="pl-help">Lägg fram ett förslag och välj vem som beslutar: <b>ni i planeringen</b> (alla som deltar svarar Ja eller Nej)
            eller <b>styrelsen</b> i en klubb eller krets som någon här företräder. Dokument kan bifogas och följer med till styrelsen.</p>`];
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
    document.addEventListener('click', e => {
        const s = e.target.closest('[data-dec-send]'); if (!s) return;
        e.preventDefault();
        const d = findDecision(parseInt(s.dataset.decSend, 10)); if (d) sendDecision(d);
    });
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

    // Nytt förslag: rubrik, bakgrund, vem som beslutar och bilagor. Bilagorna väljs i dokumentväljaren, som
    // öppnas i samma dialogfönster — utkastet (draft) bär det som redan skrivits tillbaka hit.
    function openDecisionEdit(d, draft) {
        if (d) return openDecisionChange(d);
        draft = draft || { title: '', body: '', decider: 'Group', target: 0, docIds: [] };
        const targets = DEC.boardTargets || [];
        const canBoard = DEC.canSendToBoard && targets.length > 0;
        const docs = draft.docIds.map(id => DOCS && DOCS.documents.find(x => x.id === id)).filter(Boolean);
        const read = body => ({
            title: body.querySelector('#deTitle').value, body: body.querySelector('#deBody').value,
            decider: canBoard && body.querySelector('#deByBoard').checked ? 'Board' : 'Group',
            target: canBoard ? parseInt(body.querySelector('#deTarget').value, 10) || 0 : 0, docIds: draft.docIds.slice(),
        });
        formModal({
            title: 'Nytt förslag', okLabel: 'Lägg fram förslaget', wide: false,
            html: `<label class="form-label" for="deTitle">Förslaget</label>
                <input id="deTitle" class="form-control mb-1" maxlength="300" value="${esc(draft.title)}" placeholder="Till exempel: Varbergs PK bemannar station 3–5">
                <div class="pl-help small mb-3">En mening som går att svara Ja eller Nej på.</div>
                <label class="form-label" for="deBody">Bakgrund (valfri)</label>
                <textarea id="deBody" class="form-control mb-3" rows="4" maxlength="4000">${esc(draft.body)}</textarea>
                <div class="fw-semibold mb-1">Vem beslutar?</div>
                <div class="form-check"><input class="form-check-input" type="radio" name="deBy" id="deByGroup" value="Group"${draft.decider !== 'Board' ? ' checked' : ''}>
                    <label class="form-check-label" for="deByGroup">${S.planning.groupName ? 'Vi i ' + esc(S.planning.groupName) : 'Vi i planeringen'} <span class="pl-help small">— alla som deltar svarar Ja eller Nej</span></label></div>
                ${canBoard ? `<div class="form-check"><input class="form-check-input" type="radio" name="deBy" id="deByBoard" value="Board"${draft.decider === 'Board' ? ' checked' : ''}>
                    <label class="form-check-label" for="deByBoard">En styrelse <span class="pl-help small">— förslaget skickas till styrelsen, gruppen svarar inte</span></label></div>
                    <div id="deBoardBox" class="ms-4 mt-1${draft.decider === 'Board' ? '' : ' d-none'}">
                        <select id="deTarget" class="form-select" aria-label="Vilken styrelse">${targets.map((t, i) => `<option value="${i}"${i === draft.target ? ' selected' : ''}>Styrelsen i ${esc(t.name)}</option>`).join('')}</select>
                        <div class="pl-help small mt-1">Klubbarna och kretsarna som någon i planeringen företräder. Sekreteraren och ordföranden får ett mejl;
                            styrelsen beslutar direkt eller tar upp det på ett möte.</div></div>`
                    : `<p class="pl-help small mb-0">${DEC.canSendToBoard ? 'Ingen i planeringen företräder en klubb eller krets än, så förslaget kan inte skickas till en styrelse.'
                        : 'Den som kan ändra planeringen kan också lägga fram förslag som en styrelse beslutar om.'}</p>`}
                <div class="fw-semibold mt-3 mb-1">Bilagor</div>
                <div id="deDocs">${docs.length ? docs.map(x => `<div class="d-flex align-items-center gap-2"><i class="bi ${docIcon(x)} text-secondary" aria-hidden="true"></i>${esc(x.title)}
                    <button type="button" class="btn btn-link btn-sm text-danger p-0 ms-auto" data-de-undoc="${x.id}" aria-label="Ta bort ${esc(x.title)} ur bilagorna">Ta bort</button></div>`).join('')
                    : '<div class="pl-help small">Inga bilagor.</div>'}</div>
                ${canAddDocs() ? '<button type="button" class="btn btn-sm btn-outline-secondary mt-2" id="deAddDocs"><i class="bi bi-paperclip me-1" aria-hidden="true"></i>Välj eller lägg till dokument…</button>' : ''}`,
            onOpen: body => {
                body.querySelector('#deTitle').focus();
                body.querySelectorAll('input[name="deBy"]').forEach(r => r.addEventListener('change', () =>
                    body.querySelector('#deBoardBox')?.classList.toggle('d-none', !body.querySelector('#deByBoard')?.checked)));
                body.querySelector('#deAddDocs')?.addEventListener('click', () => {
                    const cur = read(body);
                    pickDocuments({ preselected: cur.docIds, onPick: ids => { openDecisionEdit(null, Object.assign(cur, { docIds: ids })); return false; } });
                });
                body.addEventListener('click', e => {
                    const u = e.target.closest('[data-de-undoc]'); if (!u) return;
                    const cur = read(body);
                    cur.docIds = cur.docIds.filter(id => id !== parseInt(u.dataset.deUndoc, 10));
                    openDecisionEdit(null, cur);
                });
            },
            onOk: async body => {
                const p = read(body);
                if (!p.title.trim()) return 'Skriv vad förslaget gäller.';
                const t = p.decider === 'Board' ? targets[p.target] : null;
                if (p.decider === 'Board' && !t) return 'Välj en styrelse.';
                const r = await post('CreateDecision', { title: p.title.trim(), body: p.body, decider: p.decider,
                    ownerType: t ? t.ownerType : '', ownerId: t ? t.ownerId : '' });
                if (!r.success) return r.message || 'Det gick inte att lägga fram förslaget.';
                let note = '';
                if (p.docIds.length) {
                    const a = await post('AttachDocuments', { documentIds: p.docIds.join(','), kind: 'Decision', targetId: r.decisionId });
                    if (!a.success) note = ' Bilagorna kunde inte bifogas: ' + esc(a.message || '') + '.';
                }
                if (p.decider === 'Board') {
                    const s = await post('SendDecisionToBoard', { decisionId: r.decisionId });
                    await reloadDecisions(); await reloadDocs(); await reloadMessages(); go('beslut');
                    if (!s.success) { showMsg('Förslaget är sparat men kunde inte skickas: ' + esc(s.message || '') + ' Tryck Skicka till styrelsen på förslaget.' + note, 'warning'); return true; }
                    showMsg(sentMessage(s) + note, s.notified && !note && !(s.skipped || []).length ? 'success' : 'warning');
                    return true;
                }
                await reloadDecisions(); await reloadDocs(); await reloadMessages(); go('beslut');
                if (note) showMsg('Förslaget är framlagt.' + note, 'warning');
                return true;
            }
        });
    }
    function sentMessage(s) {
        return `Skickat till styrelsen i ${esc(s.org)}` + (s.attachments ? ` med ${s.attachments === 1 ? 'en bilaga' : s.attachments + ' bilagor'}` : '') + '.'
            + ((s.skipped || []).length ? ` Följande kunde inte följa med: ${esc(s.skipped.join(', '))}.` : '')
            + (s.notified ? '' : ' Ingen i styrelsen kunde nås med e-post — hör av dig till dem.');
    }

    // Ändra ett befintligt förslag: rubrik och bakgrund. Vem som beslutar ändras inte — dra tillbaka och lägg fram igen.
    function openDecisionChange(d) {
        const answered = d.responses.length;
        formModal({
            title: 'Ändra förslaget', okLabel: 'Spara', wide: false,
            html: `<label class="form-label" for="deTitle">Förslaget</label>
                <input id="deTitle" class="form-control mb-3" maxlength="300" value="${esc(d.title)}">
                <label class="form-label" for="deBody">Bakgrund (valfri)</label>
                <textarea id="deBody" class="form-control" rows="5" maxlength="4000">${esc(d.body || '')}</textarea>
                ${answered ? `<div class="alert alert-warning small mt-3 mb-0">${answered === 1 ? 'En har' : answered + ' har'} redan svarat. Svaren står kvar men märks som givna före ändringen.</div>` : ''}
                <p class="pl-help small mt-3 mb-0">${isBoardDec(d) ? 'Styrelsen beslutar om förslaget.' : (S.planning.groupName ? esc(S.planning.groupName) + ' beslutar' : 'Gruppen beslutar') + ' om förslaget.'}
                    Vill du ändra det, dra tillbaka förslaget och lägg fram det igen.</p>`,
            onOpen: body => body.querySelector('#deTitle').focus(),
            onOk: async body => {
                const p = { title: body.querySelector('#deTitle').value.trim(), body: body.querySelector('#deBody').value };
                if (!p.title) return 'Skriv vad förslaget gäller.';
                const r = await post('UpdateDecision', Object.assign({ decisionId: d.id }, p));
                if (!r.success) return r.message || 'Det gick inte att spara.';
                await reloadDecisions(); await reloadMessages(); go('beslut');
                return true;
            }
        });
    }

    async function sendDecision(d) {
        const org = d.board ? d.board.org : '';
        if (!await plConfirm('Skicka till styrelsen?', `"${d.title}" läggs i ärendekön hos styrelsen i ${org}, med bilagorna. Sekreteraren och ordföranden får ett mejl.`, 'Skicka')) return;
        const s = await post('SendDecisionToBoard', { decisionId: d.id });
        if (!s.success) { failMsg(s); return; }
        await reloadDecisions(); await reloadMessages(); render(view);
        showMsg(sentMessage(s), s.notified && !(s.skipped || []).length ? 'success' : 'warning');
    }

    // Gruppens beslut. Ett förslag till en styrelse registreras inte här — styrelsens beslut syns av sig själv.
    function openDecide(d) {
        const yes = d.responses.filter(r => r.answer === 'Yes').length, no = d.responses.filter(r => r.answer === 'No').length;
        formModal({
            title: 'Registrera beslutet', okLabel: 'Registrera', wide: false,
            html: `<p class="mb-1"><b>${esc(d.title)}</b></p>
                <p class="pl-help small mb-3">Svaren: ${yes} Ja, ${no} Nej${(d.waiting || []).length ? `, ${d.waiting.length} har inte svarat` : ''}.</p>
                <div class="mb-3"><div class="form-check"><input class="form-check-input" type="radio" name="dcOut" id="dcOutA" value="Approved" checked><label class="form-check-label" for="dcOutA">Beslutat</label></div>
                    <div class="form-check"><input class="form-check-input" type="radio" name="dcOut" id="dcOutR" value="Rejected"><label class="form-check-label" for="dcOutR">Avslaget</label></div></div>
                <label class="form-label" for="dcNote">Beslutet (valfritt)</label>
                <textarea id="dcNote" class="form-control" rows="4" maxlength="4000"></textarea>
                <p class="pl-help small mt-2 mb-0">Gruppen ser beslutet här och i meddelandena. Ett beslut kan ångras.</p>`,
            onOk: async body => {
                const r = await post('DecideDecision', { decisionId: d.id, outcome: body.querySelector('input[name="dcOut"]:checked').value,
                    note: body.querySelector('#dcNote').value });
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
        if (!await plConfirm('Dra tillbaka förslaget?', `"${d.title}" står kvar under Tillbakadragna förslag, men går inte längre att ${isBoardDec(d) ? 'skicka' : 'svara på'}.`, 'Dra tillbaka', true)) return;
        const r = await post('WithdrawDecision', { decisionId: d.id });
        if (!r.success) { failMsg(r); return; }
        await reloadDecisions(); await reloadMessages(); render(view);
    }

    // ================================================================ Stationerna (Stefan 2026-10-24, omgjord 2026-10-25)
    // En egen plats i rälsen för fältskytte. Stefan 2026-10-25:
    // - stationerna har EN plats — den planering i familjen som är fältskytte (en SM-veckas Fält-delplanering);
    // - de är EN fältkonfiguration i planeringens projekt i Fältkonfiguratorn — inga delar, ingen ihopsättning;
    // - varje station kan ha en ANSVARIG (en person, klubb eller krets) som utformar just den och ändrar bara den;
    // - banläggaren godkänner station för station, sedan helheten.
    const FALT_KEYS = ['Faltskytte', 'MagnumFalt'];
    function stationsWanted(s) {
        s = s || S;
        const o = s.planning.stationsOwner;
        if (o && !o.here) return false; // stationerna sköts i en annan planering i familjen
        // Planeringstestet k2: utan gren fanns ingen väg dit — den som kan ändra ser platsen även utan gren.
        return FALT_KEYS.includes(s.planning.disciplineKey) || (!s.planning.disciplineKey && s.me.canEdit);
    }
    let ST = null;
    const ST_APPROVAL = { Pending: ['Väntar på godkännande', 'draft'], Approved: ['Godkänd', 'live'], Returned: ['Tillbaka', 'draft'] };
    const stPill = ([lbl, tone], title) => `<span class="hpsk-state hpsk-state--${tone}"${title ? ` title="${esc(title)}"` : ''}><span class="hpsk-state-dot"></span>${esc(lbl)}</span>`;
    const stDash = '<span class="pl-help">—</span>';

    async function viewStations() {
        setHead('Stationerna', esc(S.planning.name));
        setActions([]);
        if (!ST) $('plBody').innerHTML = spinner();
        else drawStations();
        await reloadStations();
    }
    async function reloadStations() {
        const r = await get('Stations');
        if (view !== 'stationer') { if (r.success) ST = r; return; }
        if (!r.success) { $('plBody').innerHTML = `<div class="alert alert-danger">${esc(r.message || 'Stationerna kunde inte hämtas.')}</div>`; return; }
        ST = r; drawStations();
    }
    async function afterStationChange() {
        await Promise.all([reloadPeople(), reloadTasks()]);
        renderRail();
        await reloadStations();
    }

    function drawStations() {
        const r = ST, c = r.course, edit = S.me.canEdit && !TOKEN;
        const rows = r.stations || [], fa = r.responsibles || [];
        const comp = c && c.competition;
        const canAttach = comp && comp.canAttach && edit && (!comp.attached || comp.stale) && c.builtStations > 0;
        const whole = edit && !r.wholeElsewhere;
        setActions(edit && !c ? [
            rows.length && TASKS && TASKS.canManageAreas && { label: 'En uppgift per station', icon: 'bi-signpost-split', fn: seedStations },
        ] : edit && c ? [
            c.canView && { label: 'Öppna stationerna i konfiguratorn', icon: 'bi-box-arrow-up-right', fn: () => { location.href = c.url; } },
            whole && { label: 'Ge stationer en ansvarig…', icon: 'bi-person-check', fn: () => openResponsible(null) },
            whole && { label: 'Ändra antalet stationer…', icon: 'bi-123', fn: openStationCount },
            TASKS && TASKS.canManageAreas && { label: 'En uppgift per station', icon: 'bi-signpost-split', fn: seedStations },
            (c.canEdit || canAttach) && { divider: true },
            c.canEdit && { label: 'Begär godkännande av helheten…', icon: 'bi-patch-check', fn: () => { location.href = c.url; } },
            canAttach && { label: comp.attached ? 'Uppdatera tävlingens stationer' : 'Anslut stationerna till tävlingen', icon: 'bi-box-arrow-in-right', fn: attachStations },
        ] : []);
        rowFns = [];
        if (!c) {
            $('plBody').innerHTML = stationsEmptyHtml(r, rows) + (rows.length ? (fa.length ? responsiblesHtml(fa) : '') + stationRowsHtml(rows, edit, whole) : '');
            return;
        }
        $('plBody').innerHTML = `<p class="pl-help mb-3">Stationerna utformas i konfiguratorn${c.projectUrl ? ` — i projektet <a href="${esc(c.projectUrl)}">${esc(S.planning.name)}</a>` : ''}.
                Varje station kan ha en ansvarig — en person, en klubb eller en krets — som utformar och ändrar just den.
                En banläggare godkänner station för station, sedan helheten.</p>
            ${stepsHtml(r, rows, whole)}${fa.length ? responsiblesHtml(fa) : ''}${stationRowsHtml(rows, edit, whole)}`;
    }

    function stationsEmptyHtml(r, rows) {
        const suggested = Math.max(rows.length ? rows[rows.length - 1].number : 0, r.stationCount || 0, (r.competition && r.competition.stationCount) || 0) || 8;
        if (!r.canCreateCourse) return `<div class="pl-card" id="plStEmpty"><h2>Stationerna är inte skapade än</h2>
            <p class="mb-0">${r.wholeElsewhere ? esc(r.wholeElsewhere) : 'Den som kan ändra planeringen skapar stationerna här.'}</p></div>`;
        return `<div class="pl-card" id="plStEmpty"><h2>Skapa stationerna</h2>
            ${!S.planning.disciplineKey ? '<p class="mb-2"><b>Stationerna behövs i fältskytte.</b> Har tävlingen stationer — en fälttävling, eller fältet i en SM-vecka — börjar ni här. Annars kan du lämna den här delen.</p>' : ''}
            <p>Börja med hur många stationer tävlingen har. De hamnar i Fältkonfiguratorn, i ett projekt med planeringens namn.
                ${r.competition && r.competition.falt ? `Stationerna börjar som en kopia av tävlingens fältkonfiguration (${esc(r.competition.name)}).` : ''}</p>
            <div class="d-flex flex-wrap gap-2 align-items-end"><div><label class="form-label" for="plStCount">Antal stationer</label>
                <input id="plStCount" class="form-control" style="max-width:8rem" inputmode="numeric" value="${suggested}"></div>
                <button type="button" class="btn btn-primary" data-st-create="1">Skapa stationerna</button></div></div>`;
    }

    // Läget: Utforma · Godkänn · Tävlingen.
    function stepsHtml(r, rows, edit) {
        const c = r.course, comp = c.competition;
        const total = r.stationCount || rows.length;
        const built = rows.filter(s => s.built).length, approved = c.approvedStations || 0;
        const wholeText = c.status === 'Approved' ? 'Helheten är godkänd' : c.status === 'PendingApproval' ? 'Helheten väntar på godkännande'
            : approved === total && total > 0 ? 'Begär godkännande av helheten i konfiguratorn' : 'Helheten godkänns när alla stationer är godkända';
        const steps = [
            { label: 'Utforma', done: total > 0 && built === total, big: `${built} av ${total} utformade`, small: 'i konfiguratorn',
                extra: c.canView ? `<a class="btn btn-outline-primary btn-sm mt-1" style="justify-self:start" href="${esc(c.url)}">Öppna i konfiguratorn</a>` : '' },
            { label: 'Godkänn', done: c.status === 'Approved', big: `${approved} av ${total} godkända`, small: wholeText },
            comp
                ? { label: 'Tävlingen', done: !!(comp.attached && !comp.stale),
                    big: comp.attached ? (comp.stale ? 'Tävlingens kopia är äldre' : 'Anslutna till tävlingen') : comp.hasOther ? 'Tävlingen har en annan fältkonfiguration' : 'Inte anslutna än',
                    small: esc(r.competition.name),
                    extra: comp.canAttach && edit && (!comp.attached || comp.stale) && c.builtStations > 0
                        ? `<button type="button" class="btn btn-outline-primary btn-sm mt-1" style="justify-self:start" data-st-attach="1">${comp.attached ? 'Uppdatera tävlingens stationer' : 'Anslut till tävlingen'}</button>` : '' }
                : { label: 'Tävlingen', done: false, big: 'Ingen fälttävling kopplad', small: 'Stationerna följer med när planeringen kopplas' },
        ];
        const now = steps.findIndex(s => !s.done);
        return `<div class="pl-card" id="plStSteps"><h2>Läget</h2><div class="pl-steps pl-steps-3">${steps.map((s, i) =>
            `<div class="pl-step${s.done ? ' done' : i === now ? ' now' : ''}" data-st-step="${esc(s.label)}"${i === now ? ' aria-current="step"' : ''}>
                <span class="n">${i + 1} · ${esc(s.label)}</span><b>${s.big}</b><span class="small">${s.small}</span>${s.extra || ''}</div>`).join('')}</div>
            <div id="plStResult" class="mt-2"></div></div>`;
    }

    // De ansvariga: vem som utformar vilka stationer (Stefan 2026-10-25: en egen plats, inga områden).
    const ST_KIND = { Person: 'person i planeringen', Club: 'klubb', Region: 'krets', Name: '' };
    function responsiblesHtml(fa) {
        const body = fa.map(a => {
            const menu = a.editable ? rowMenu([
                { label: 'Välj stationer…', fn: () => openResponsible(a) },
                { divider: true },
                { label: 'Ta bort som ansvarig', danger: true, fn: () => removeResponsible(a) },
            ]) : '';
            const kind = ST_KIND[a.kind] || '';
            return `<tr data-st-resp="${esc(a.key)}">
                <td class="ps-3"><b>${esc(a.name)}</b>${kind ? `<div class="pl-help small">${esc(kind)}</div>` : ''}
                    ${a.left ? '<div class="small text-danger">Har lämnat planeringen — kan inte ändra stationerna.</div>' : ''}</td>
                <td><div class="pl-chips">${a.stations.map(n => `<span class="pl-chip">${n}</span>`).join('')}</div></td>
                <td class="d-none d-md-table-cell">${a.contact ? esc(a.contact) : stDash}</td>
                <td class="text-end">${menu}</td></tr>`;
        }).join('');
        return `<div class="pl-card p-0" id="plStAreas"><div class="pl-cardh px-3 pt-3 d-block"><h2>Ansvariga</h2>
                <p class="pl-help small mb-2">Den som är ansvarig för en station utformar den och kan ändra just den i konfiguratorn. För en klubb, en krets eller ett namn är det kontakten som gör det.</p></div>
            <div class="table-responsive"><table class="table align-middle mb-0">
                <thead><tr><th class="ps-3">Ansvarig</th><th>Stationer</th><th class="d-none d-md-table-cell">Kontakt</th><th></th></tr></thead>
                <tbody>${body}</tbody></table></div></div>`;
    }
    async function removeResponsible(a) {
        if (!await plConfirm(`Ta bort ${a.name} som ansvarig?`, `Station ${a.stations.join(', ')} blir utan ansvarig. Det som är utformat i konfiguratorn står kvar.`, 'Ta bort')) return;
        const r = await post('ClearStationResponsible', { stations: a.stations.join(',') });
        if (!r.success) { failMsg(r); return; }
        await afterStationChange();
    }

    function approvalHtml(s) {
        if (!s.built && !s.approval) return stDash;
        if (!s.approval) return '<span class="pl-help">Utkast</span>';
        return stPill(ST_APPROVAL[s.approval] || ST_APPROVAL.Pending, s.approvalComment) + (s.approval === 'Returned' && s.approvalComment ? `<div class="pl-help small">${esc(s.approvalComment)}</div>` : '');
    }
    function stationRowsHtml(rows, edit, whole) {
        const body = rows.map(s => {
            const builtHtml = s.built ? '<span class="text-success">Utformad</span>' : '<span class="pl-help">Inte än</span>';
            const whoHtml = s.responsible ? esc(s.responsible) : stDash;
            const menu = rowMenu([
                s.url && { label: 'Öppna stationen i konfiguratorn', fn: () => { location.href = s.url; } },
                whole && ST.course && { label: s.responsible ? 'Byt ansvarig…' : 'Ge stationen en ansvarig…', fn: () => openGiveStation(s) },
                TASKS && TASKS.canCreate && !TOKEN && { label: 'Lägg till en uppgift för stationen…',
                    fn: () => openTaskEdit(null, null, { scopeType: 'Station', scopeKey: String(s.number) }) },
            ]);
            return `<tr data-station="${s.number}"><td class="ps-3"><b>${s.number}</b>
                    <div class="d-md-none small mt-1">${s.responsible ? esc(s.responsible) + ' · ' : ''}${builtHtml}${s.approval ? ' · ' + approvalHtml(s) : ''}</div></td>
                <td class="d-none d-md-table-cell">${whoHtml}</td>
                <td class="d-none d-md-table-cell">${builtHtml}</td>
                <td class="d-none d-md-table-cell">${approvalHtml(s)}</td>
                <td class="d-none d-md-table-cell">${s.openTasks ? s.openTasks + ' öppna' : stDash}</td>
                <td class="text-end">${menu}</td></tr>`;
        }).join('');
        return `<div class="pl-card p-0" id="plStRows"><div class="pl-cardh px-3 pt-3 d-block"><h2>Stationerna</h2>
                <p class="pl-help small mb-2">En rad per station: vem som är ansvarig för att utforma den, om den är utformad och godkänd, och de öppna uppgifterna.</p></div>
            <div class="table-responsive"><table class="table align-middle mb-0">
            <thead><tr><th class="ps-3">Station</th><th class="d-none d-md-table-cell">Ansvarig</th><th class="d-none d-md-table-cell">Utformad</th>
                <th class="d-none d-md-table-cell">Godkänd</th><th class="d-none d-md-table-cell">Uppgifter</th><th></th></tr></thead><tbody>${body}</tbody></table></div></div>`;
    }

    // ---------------------------------------------------------------- dialogerna
    // Rutnätet: en ruta per station. En station som någon annan är ansvarig för byter ansvarig.
    function stationGridHtml(key, chosen) {
        return `<div class="pl-stgrid" id="stGrid">${(ST.stations || []).map(s => {
            const mine = chosen.has(s.number);
            return `<button type="button" class="pl-st${mine ? ' on' : ''}" data-st-n="${s.number}" aria-pressed="${mine}"
                title="${esc(s.responsible ? 'Ansvarig: ' + s.responsible : 'Ingen ansvarig än')}"><b>${s.number}</b><span>${esc(stGridLabel(s, key, mine))}</span></button>`;
        }).join('')}</div>`;
    }
    function stGridLabel(s, key, mine) {
        const other = !!s.responsibleKey && s.responsibleKey !== key;
        if (mine) return other ? 'byter ansvarig' : 'vald';
        return other ? s.responsible : 'ingen än';
    }
    function wireStationGrid(body, key, chosen) {
        body.querySelector('#stGrid').addEventListener('click', e => {
            const b = e.target.closest('[data-st-n]'); if (!b || b.disabled) return;
            const n = parseInt(b.dataset.stN, 10), s = ST.stations.find(x => x.number === n);
            if (chosen.has(n)) chosen.delete(n); else chosen.add(n);
            const mine = chosen.has(n);
            b.classList.toggle('on', mine); b.setAttribute('aria-pressed', mine);
            b.querySelector('span').textContent = stGridLabel(s, key, mine);
        });
    }
    // "Ge stationer en ansvarig" — en person i planeringen, en klubb eller krets, eller ett namn. Inget område skapas.
    // a = en befintlig ansvarig (Välj stationer…): bara stationerna ändras.
    async function openResponsible(a, presetStation) {
        const before = a ? a.stations.slice() : [];
        const chosen = new Set(a ? a.stations : presetStation ? [presetStation] : []);
        let hosts = [];
        if (!a) {
            const o = await get('HostOptions');
            if (!o.success) { failMsg(o); return; }
            hosts = o.hosts;
        }
        const people = (S.participants || []).slice().sort((x, y) => x.name.localeCompare(y.name, 'sv'));
        const whoOf = x => x.kind === 'Person' ? 'p:' + x.participantId : x.kind === 'Club' ? 'h:0:' + x.hostId : x.kind === 'Region' ? 'h:1:' + x.hostId : 'text';
        formModal({
            title: a ? `${a.name}: stationer` : 'Ge stationer en ansvarig', okLabel: a ? 'Spara' : 'Lägg till', wide: false,
            html: (a ? '' : `<div class="mb-3"><label class="form-label fw-semibold" for="saWho">Ansvarig</label>
                    <select id="saWho" class="form-select"><option value="">Välj…</option>
                        ${people.length ? `<optgroup label="Någon i planeringen">${people.map(p => `<option value="p:${p.id}">${esc(p.name)}</option>`).join('')}</optgroup>` : ''}
                        ${hosts.length ? `<optgroup label="En klubb eller krets">${hosts.map(h => `<option value="h:${h.ownerType}:${h.ownerId}">${esc(h.name)}</option>`).join('')}</optgroup>` : ''}
                        <option value="text">Någon annan (skriv namnet)</option></select>
                    <input id="saWhoText" class="form-control mt-2 d-none" maxlength="200" placeholder="Till exempel Hallands krets eller Kalle Svensson">
                    <div class="pl-help small mt-1">Den ansvarige utformar stationerna och kan ändra just dem i konfiguratorn.</div></div>
                <div class="mb-3 d-none" id="saRespBox"><label class="form-label" for="saResp">Kontakt</label>
                    <select id="saResp" class="form-select">${peopleOptions(null)}</select>
                    <div class="pl-help small mt-1">Den i planeringen som utformar stationerna åt klubben, kretsen eller den du skrev — hen kan ändra dem i konfiguratorn.</div></div>`)
                + `<div class="form-label fw-semibold mb-1">Stationer</div>
                <div class="pl-help small mb-2">Tryck på en station för att ta den eller släppa den. En station som någon annan är ansvarig för byter ansvarig.</div>
                ${stationGridHtml(a ? a.key : '', chosen)}`,
            onOpen: body => {
                wireStationGrid(body, a ? a.key : '', chosen);
                const who = body.querySelector('#saWho');
                who?.addEventListener('change', () => {
                    body.querySelector('#saWhoText').classList.toggle('d-none', who.value !== 'text');
                    body.querySelector('#saRespBox').classList.toggle('d-none', !(who.value === 'text' || who.value.startsWith('h:')));
                });
            },
            onOk: async body => {
                let params;
                if (a) {
                    params = { who: whoOf(a), name: a.kind === 'Name' ? a.name : '', contactId: a.kind === 'Person' ? '' : (a.contactId || '') };
                } else {
                    const who = body.querySelector('#saWho').value, text = body.querySelector('#saWhoText').value.trim();
                    if (!who) return 'Välj vem som är ansvarig.';
                    if (who === 'text' && !text) return 'Skriv namnet.';
                    params = { who, name: who === 'text' ? text : '', contactId: who.startsWith('p:') ? '' : body.querySelector('#saResp').value };
                }
                if (!chosen.size && !a) return 'Välj minst en station.';
                const list = [...chosen].sort((x, y) => x - y);
                const r = !list.length
                    ? await post('ClearStationResponsible', { stations: before.join(',') })
                    : await post('SetStationResponsible', Object.assign(params, { stations: list.join(','), release: before.filter(n => !chosen.has(n)).join(',') }));
                if (!r.success) return r.message;
                await afterStationChange();
                return true;
            },
        });
    }
    function openGiveStation(s) {
        const opts = (ST.responsibles || []).filter(a => a.editable && a.key !== s.responsibleKey);
        formModal({
            title: `Station ${s.number}`, okLabel: 'Spara', wide: false,
            html: `<label class="form-label fw-semibold" for="gsArea">Vem är ansvarig för stationen?</label>
                <select id="gsArea" class="form-select">${opts.map(a => `<option value="${esc(a.key)}">${esc(a.name)}</option>`).join('')}
                    <option value="__new">＋ Någon ny…</option>
                    ${s.responsible ? '<option value="__none">Ingen ansvarig</option>' : ''}</select>
                ${s.responsible ? `<div class="pl-help small mt-1">I dag: ${esc(s.responsible)}.</div>` : ''}`,
            onOk: async body => {
                const v = body.querySelector('#gsArea').value;
                if (v === '__new') { openResponsible(null, s.number); return; }
                let r;
                if (v === '__none') r = await post('ClearStationResponsible', { stations: String(s.number) });
                else {
                    const to = opts.find(a => a.key === v);
                    r = await post('SetStationResponsible', {
                        who: to.kind === 'Person' ? 'p:' + to.participantId : to.kind === 'Club' ? 'h:0:' + to.hostId : to.kind === 'Region' ? 'h:1:' + to.hostId : 'text',
                        name: to.kind === 'Name' ? to.name : '', contactId: to.kind === 'Person' ? '' : (to.contactId || ''),
                        stations: String(s.number), release: '' });
                }
                if (!r.success) return r.message;
                await afterStationChange();
                return true;
            },
        });
    }
    function openStationCount() {
        formModal({
            title: 'Antal stationer', okLabel: 'Spara', wide: false,
            html: `<label class="form-label fw-semibold" for="scCount">Hur många stationer har tävlingen?</label>
                <input id="scCount" class="form-control" style="max-width:8rem" inputmode="numeric" value="${ST.stationCount || ''}">
                <div class="pl-help small mt-1">Färre stationer går bara när ingen station över gränsen är utformad eller har en ansvarig.</div>`,
            onOpen: body => body.querySelector('#scCount').focus(),
            onOk: async body => {
                const n = parseInt(body.querySelector('#scCount').value, 10);
                if (!(n > 0)) return 'Skriv antalet stationer.';
                const r = await post('SetStationCount', { stationCount: n });
                if (!r.success) return r.message;
                await reloadStations(); return true;
            },
        });
    }
    async function attachStations() {
        const r = await post('AttachCourseToCompetition', {});
        if (!r.success) { failMsg(r, 'Stationerna kunde inte anslutas.'); return; }
        await reloadStations();
        showMsg(`Stationerna är anslutna till ${esc(r.competition || 'tävlingen')}.`, 'success');
    }
    document.addEventListener('click', async e => {
        const b = e.target.closest('[data-st-create],[data-st-attach]'); if (!b) return;
        e.preventDefault(); if (b.dataset.busy) return; b.dataset.busy = '1';
        try {
            if (b.dataset.stCreate) {
                const n = parseInt(($('plStCount') || {}).value, 10);
                const r = await post('CreateCourseConfig', { stationCount: n > 0 ? n : '' });
                if (!r.success) { failMsg(r, 'Stationerna kunde inte skapas.'); return; }
                await reloadStations();
            } else if (b.dataset.stAttach) await attachStations();
        } finally { delete b.dataset.busy; }
    });

    // Översikt: en rad — eller, när stationerna sköts i en annan planering i familjen, en hänvisning dit.
    async function loadStationsSummary() {
        const w0 = $('plStationsSum');
        if (!w0 || TOKEN) return;
        const o = S.planning.stationsOwner;
        if (o && !o.here && o.name) {
            w0.innerHTML = `<div class="pl-card" id="plStationsSummary"><div class="d-flex flex-wrap gap-2 align-items-center justify-content-between">
                <div><b>Stationerna</b> · sköts i ${o.isSub ? 'delplaneringen' : 'huvudplaneringen'} ${esc(o.name)}</div>
                <a class="btn btn-outline-primary btn-sm" href="${esc(o.url)}">Till stationerna</a></div></div>`;
            return;
        }
        if (!stationsWanted()) return;
        const r = await get('Stations');
        const w = $('plStationsSum');
        if (!r.success || !w) return;
        ST = r;
        const rows = r.stations || [];
        let text;
        if (!r.course) {
            if (!r.canCreateCourse && !rows.length) return;
            text = 'inte skapade än';
        } else {
            const total = r.stationCount || rows.length, built = rows.filter(s => s.built).length;
            text = `${built} av ${total} utformade · ${r.course.approvedStations || 0} av ${total} godkända`
                + (r.course.status === 'Approved' ? ' · helheten godkänd' : '');
        }
        w.innerHTML = `<div class="pl-card" id="plStationsSummary"><div class="d-flex flex-wrap gap-2 align-items-center justify-content-between">
            <div><b>Stationerna</b> · ${text}</div>
            <a class="btn btn-outline-primary btn-sm" href="#vy=stationer" data-pl-go="stationer">Till Stationerna</a></div></div>`;
    }

    // Den sammankallande är alltid med (delegerat: dialogens kropp byts ut varje gång).
    document.addEventListener('change', e => {
        if (e.target.id !== 'spConv') return;
        const cb = document.getElementById('spP' + e.target.value); if (cb) cb.checked = true;
    });

    // Översikt: delplaneringarnas läge (huvudplaneringen) och huvudplaneringen (delplaneringen).
    // Omgång 3 (Stefan 2026-10-24: familjen är veckan): tävlingarna i familjen — en per delplanering — på ett ställe.
    function familyCompetitionsCardHtml() {
        const f = S.planning.family;
        const comps = (f && f.competitions) || [], week = (f && f.week) || [];
        // Stefan 2026-10-25: kortet stod tomt när familjen inte hade någon kopplad tävling och delarna saknade datum.
        // Det visas bara när det finns en tävling eller ett veckoprogram att visa.
        const program = f ? weekProgramHtml(week) : '';
        if (!f || (!comps.length && !program)) return '';
        if (!comps.length) return `<div class="pl-card" id="plFamilyComps"><h2 id="plWeekProgram">Veckoprogrammet</h2>${program.replace(/^<h3[^>]*>[^<]*<\/h3>/, '')}</div>`;
        const shooting = new Map(week.map(w => [w.planningId, w.intents || 0]));
        const list = comps.length ? `<ul class="list-unstyled mb-0">${comps.map(c => `<li class="py-1 d-flex flex-wrap gap-2 align-items-baseline">
                ${c.url ? `<a href="${esc(c.url)}"><b>${esc(c.name)}</b></a>` : `<b>${esc(c.name)}</b>`}
                ${c.date ? `<span class="pl-help small">${esc(fmtDay(c.date))}${c.endDate && c.endDate !== c.date ? '–' + esc(fmtDay(c.endDate)) : ''}</span>` : ''}
                <span class="pl-help small">· ${esc(c.planningName)}</span>
                ${shooting.get(c.planningId) ? `<span class="pl-help small">· ${shooting.get(c.planningId)} av funktionärerna tänker tävla</span>` : ''}</li>`).join('')}</ul>` : '';
        return `<div class="pl-card" id="plFamilyComps"><h2>${comps.length === 1 ? 'Tävlingen' : 'Tävlingarna'}</h2>${list}${program}</div>`;
    }
    // Omgång 3 steg 9: veckoprogrammet — dag för dag vilka av veckans tävlingar som pågår, och hur många av funktionärerna
    // som tänker tävla. Ett underlag för att lägga uppdragen när folk inte skjuter.
    function weekProgramHtml(week) {
        const dated = week.filter(w => w.from);
        if (!dated.length) return '';
        const days = new Map();
        dated.forEach(w => {
            const d = new Date(w.from + 'T00:00:00'), end = new Date((w.to || w.from) + 'T00:00:00');
            for (let n = 0; d <= end && n < 31; n++, d.setDate(d.getDate() + 1)) {
                const k = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
                (days.get(k) || days.set(k, []).get(k)).push(w);
            }
        });
        const keys = [...days.keys()].sort();
        return `<h3 class="h6 mt-3 mb-1" id="plWeekProgram">Veckoprogrammet</h3><ul class="list-unstyled mb-0 small">${keys.map(k =>
            `<li class="py-1" data-week-day="${esc(k)}"><b>${esc(fmtDay(k))}</b> · ${days.get(k).map(w => esc(w.name) + (w.intents ? ` <span class="pl-help">(${w.intents} funktionärer tänker tävla)</span>` : '')).join(', ')}</li>`).join('')}</ul>`;
    }

    function subPlanningsCardHtml() {
        const list = S.planning.subPlannings || [];
        if (!list.length) return '';
        return `<div class="pl-card p-0" id="plSubPlannings"><div class="pl-cardh px-3 pt-3"><h2>Delplaneringar</h2>
                ${S.me.canCreateSub && !TOKEN ? '<button type="button" class="btn btn-outline-primary btn-sm" data-sub-new="1"><i class="bi bi-plus-lg me-1"></i>Ny delplanering…</button>' : ''}</div>
            <p class="pl-help small px-3 mb-2">De delar som planeras för sig — en gren, en tävling eller en förening som sköter en del. Du som är sammankallande här är det i dem också${S.me.canEdit && !S.me.canManage ? ', och du som kan ändra här kan läsa dem' : ''}.</p>
            <div class="table-responsive"><table class="table align-middle mb-0">
            <thead><tr><th class="ps-3">Planering</th><th class="d-none d-md-table-cell">Sammankallande</th><th>Uppgifter</th><th class="d-none d-md-table-cell">Nästa</th></tr></thead>
            <tbody>${list.map(s => {
                const next = [s.nextMilestone ? `<i class="bi bi-flag-fill text-primary"></i> ${esc(s.nextMilestone)}${s.nextMilestoneDue ? ' · ' + esc(fmtDay(s.nextMilestoneDue)) : ''}` : '',
                    s.nextMeeting ? `<i class="bi bi-calendar-event"></i> ${esc(s.nextMeeting)} · ${esc(fmtShort(s.nextMeetingAt))}` : ''].filter(Boolean).join('<br>');
                const sub = [s.discipline, s.host ? 'sköts av ' + s.host : ''].filter(Boolean).join(' · ');
                return `<tr data-sub-row="${s.id}"><td class="ps-3">${s.url ? `<a href="${esc(s.url)}"><b>${esc(s.name)}</b></a>` : `<b>${esc(s.name)}</b>`}
                        ${s.status === 'Archived' ? ' <span class="pl-state off">Arkiverad</span>' : ''}
                        ${sub ? `<div class="pl-help small">${esc(sub)}</div>` : ''}
                        <div class="pl-help small d-md-none">${esc(s.conveners)}</div></td>
                    <td class="d-none d-md-table-cell">${esc(s.conveners || '—')}</td>
                    <td>${s.openTasks} öppna <span class="pl-help small">av ${s.tasks}</span>${s.overdue ? `<div class="small text-danger">${s.overdue} försenade</div>` : ''}</td>
                    <td class="d-none d-md-table-cell small">${next || '<span class="pl-help">—</span>'}</td></tr>`;
            }).join('')}</tbody></table></div></div>`;
    }
    document.addEventListener('click', e => { if (e.target.closest('[data-sub-new]')) { e.preventDefault(); openNewSub(); } });

    // Stefan 2026-10-25 (B3): delplaneringens folk läser huvudplaneringens fattade beslut.
    function parentDecisionsCardHtml() {
        const list = S.planning.parentDecisions || [];
        const parent = S.planning.parent;
        if (!list.length || !parent) return '';
        return `<div class="pl-card" id="plParentDecisions"><h2>Beslut i ${esc(parent.name)}</h2>
            <p class="pl-help small">Det huvudplaneringen har bestämt, som gäller er del också.</p>
            ${list.map(d => `<div class="py-2 border-top"><b>${esc(d.title)}</b> <span class="pl-help small">· ${esc(fmtShort(d.decidedAt))}</span>
                ${d.body ? `<div class="small mt-1" style="white-space:pre-wrap">${esc(d.body)}</div>` : ''}</div>`).join('')}</div>`;
    }

    // ================================================================ Ny delplanering (Stefan 2026-10-25, Expertgruppen)
    // En delplanering skapas DIREKT, utan att först skapa ett område. Den får egna deltagare, möten, uppgifter, bemanning och
    // tävlingskoppling. Valda områden flyttar dit med sina uppgifter, trådar och dokumentkategorier.
    async function openNewSub() {
        const o = await get('HostOptions');
        const hosts = o.success ? o.hosts : [];
        const conv = (S.participants || []).filter(p => !p.isGuest).slice().sort((a, b) => a.name.localeCompare(b.name, 'sv'));
        const me = conv.find(p => p.isMe);
        const discOpts = (S.planning.disciplines || []).map(d => `<option value="${esc(d.id)}">${esc(d.name)}</option>`).join('');
        const areasList = areas();
        const count = id => (TASKS ? TASKS.tasks.filter(t => t.areaId === id).length : 0);
        formModal({
            title: 'Ny delplanering', okLabel: 'Skapa delplaneringen', wide: true,
            html: `<p class="mb-3">En delplanering är en egen planering under <b>${esc(S.planning.name)}</b> — för en gren, en tävling eller en förening som sköter en del.
                    Den får egna deltagare, möten, uppgifter och bemanning. Du som är sammankallande här är sammankallande där också.</p>
                <div class="row g-3">
                    <div class="col-md-7"><label class="form-label fw-semibold" for="nsName">Namn</label>
                        <input id="nsName" class="form-control" maxlength="200" placeholder="Till exempel Fält — ${esc(S.planning.name)}"></div>
                    <div class="col-md-5"><label class="form-label" for="nsDisc">Gren (valfritt)</label>
                        <select id="nsDisc" class="form-select"><option value="">Ingen gren</option>${discOpts}</select></div>
                    <div class="col-md-6"><label class="form-label" for="nsConv">Sammankallande</label>
                        <select id="nsConv" class="form-select">${conv.map(p => `<option value="${p.id}"${p.isMe ? ' selected' : ''}>${esc(p.name)}${p.represents ? ' (' + esc(p.represents) + ')' : ''}</option>`).join('')}</select>
                        <div class="pl-help small mt-1">Den som leder delen. Saknas hen — bjud in hen här först.</div></div>
                    <div class="col-md-6"><label class="form-label" for="nsHost">Sköts av (valfritt)</label>
                        <select id="nsHost" class="form-select"><option value="">—</option>${hosts.map(h => `<option value="${h.ownerType}:${h.ownerId}">${esc(h.name)}</option>`).join('')}<option value="text">Annan (skriv)</option></select>
                        <input id="nsHostText" class="form-control mt-2 d-none" maxlength="200" placeholder="Till exempel Hallands krets">
                        <div class="pl-help small mt-1">Den klubb eller krets som står för delen.</div></div>
                    <div class="col-sm-6"><label class="form-label" for="nsStart">Första dagen</label><input id="nsStart" class="form-control" value="${esc(S.planning.targetStart || '')}"></div>
                    <div class="col-sm-6"><label class="form-label" for="nsEnd">Sista dagen</label><input id="nsEnd" class="form-control" value="${esc(S.planning.targetEnd || '')}"></div>
                </div>
                ${areasList.length ? `<h3 class="h6 mt-4 mb-1">Ta med områden (valfritt)</h3>
                    <div class="pl-help small mb-2">Ett område du kryssar i flyttar till delplaneringen med sina uppgifter, trådar och dokumentkategorier. De som har dess uppgifter blir med där.</div>
                    <div id="nsAreas" class="border rounded p-2" style="max-height:200px;overflow:auto">${areasList.map(a => `<div class="form-check">
                        <input class="form-check-input" type="checkbox" id="nsA${a.id}" value="${a.id}"><label class="form-check-label" for="nsA${a.id}">${esc(a.name)}
                        <span class="pl-help small">· ${count(a.id)} uppgifter${a.responsible ? ' · ' + esc(a.responsible) : ''}</span></label></div>`).join('')}</div>
                    <div id="nsPreview" class="pl-help small mt-2"></div>` : ''}`,
            onOpen: body => {
                datePicker(body.querySelector('#nsStart')); datePicker(body.querySelector('#nsEnd'));
                const host = body.querySelector('#nsHost');
                host.addEventListener('change', () => body.querySelector('#nsHostText').classList.toggle('d-none', host.value !== 'text'));
                body.querySelector('#nsAreas')?.addEventListener('change', async () => {
                    const ids = [...body.querySelectorAll('#nsAreas input:checked')].map(i => i.value);
                    const pv = body.querySelector('#nsPreview');
                    if (!ids.length) { pv.textContent = ''; return; }
                    const r = await get('MovePreview', { targetId: 0, areas: ids.join(',') });
                    pv.innerHTML = r.success ? esc(moveSummary(r)) : esc(r.message || '');
                });
                body.querySelector('#nsName').focus();
            },
            onOk: async body => {
                const name = body.querySelector('#nsName').value.trim();
                if (!name) return 'Ge delplaneringen ett namn.';
                const host = body.querySelector('#nsHost').value;
                const [ht, hid] = host && host !== 'text' ? host.split(':') : ['', ''];
                const ids = [...body.querySelectorAll('#nsAreas input:checked')].map(i => i.value);
                const res = await post('CreateSub', { name, discipline: body.querySelector('#nsDisc').value,
                    convenerParticipantId: body.querySelector('#nsConv').value,
                    targetStart: dateValue(body.querySelector('#nsStart')), targetEnd: dateValue(body.querySelector('#nsEnd')),
                    hostType: ht, hostId: hid, hostText: host === 'text' ? body.querySelector('#nsHostText').value : '', areas: ids.join(',') });
                if (!res.success) return res.message || 'Det gick inte att skapa delplaneringen.';
                ids.forEach(id => { if (String(tkArea) === String(id)) tkArea = ''; if (String(msgArea) === String(id)) msgArea = ''; });
                await loadAll(); render(view);
                const moved = res.areas ? ` ${res.areas === 1 ? 'Ett område' : res.areas + ' områden'} flyttade dit med ${res.tasks} uppgifter.` : '';
                const det = res.detached ? ` ${res.detached === 1 ? 'En bifogning' : res.detached + ' bifogningar'} mellan något som flyttade och något som stannade släpptes.` : '';
                showMsg(`Delplaneringen <b>${esc(res.name)}</b> är skapad.${esc(moved)}${esc(det)} <a href="${esc(res.url)}">Öppna den</a>`);
                return true;
            },
        });
    }
    function moveSummary(r) {
        const parts = [`${r.tasks} ${r.tasks === 1 ? 'uppgift' : 'uppgifter'}`, `${r.threads} ${r.threads === 1 ? 'tråd' : 'trådar'}`, `${r.documents} ${r.documents === 1 ? 'dokument' : 'dokument'}`];
        let t = 'Flyttar: ' + parts.join(', ') + '.';
        if (r.joining && r.joining.length) t += ` Blir med där: ${r.joining.join(', ')}.`;
        if (r.detached) t += ` ${r.detached === 1 ? 'En bifogning' : r.detached + ' bifogningar'} mellan något som flyttar och något som stannar släpps.`;
        return t;
    }

    // ================================================================ Flytta ett område inom familjen (åt båda hållen)
    function familyTargets() { const f = S.planning.family; return f && f.plannings ? f.plannings : []; }
    function openMoveArea(a) {
        const targets = familyTargets();
        formModal({
            title: `Flytta ${a.name}`, okLabel: 'Flytta området', wide: false,
            html: `<p class="mb-2">Området flyttar med sina uppgifter, trådar och dokumentkategorier till den planering du väljer. Det går att flytta tillbaka.</p>
                <label class="form-label fw-semibold" for="mvTarget">Till</label>
                <select id="mvTarget" class="form-select">${targets.map(t => `<option value="${t.id}">${esc(t.name)}${t.isParent ? ' (huvudplaneringen)' : ''}</option>`).join('')}</select>
                <div id="mvPreview" class="pl-help small mt-2"></div>`,
            onOpen: body => {
                const sel = body.querySelector('#mvTarget');
                const preview = async () => {
                    const r = await get('MovePreview', { targetId: sel.value, areas: String(a.id) });
                    body.querySelector('#mvPreview').innerHTML = r.success ? esc(moveSummary(r)) : esc(r.message || '');
                };
                sel.addEventListener('change', preview); preview();
            },
            onOk: async body => {
                const res = await post('MoveAreas', { targetId: body.querySelector('#mvTarget').value, areas: String(a.id) });
                if (!res.success) return res.message || 'Det gick inte att flytta området.';
                if (String(tkArea) === String(a.id)) tkArea = '';
                if (String(msgArea) === String(a.id)) msgArea = '';
                await loadAll(); render(view);
                showMsg(`${esc(a.name)} flyttade till <a href="${esc(res.url)}">${esc(res.target)}</a>: ${res.tasks} uppgifter och ${res.threads} trådar.`
                    + (res.joined && res.joined.length ? ` ${esc(res.joined.join(', '))} är nu med där.` : ''));
                return true;
            },
        });
    }

    function openMessageArea(m) {
        formModal({
            title: 'Vilket område gäller meddelandet?', okLabel: 'Spara', wide: false,
            html: `<select id="maArea" class="form-select" aria-label="Område"><option value="0">Hela planeringen</option>${areas().map(a => `<option value="${a.id}"${m.areaId === a.id ? ' selected' : ''}>${esc(a.name)}</option>`).join('')}</select>
                <p class="pl-help small mt-2 mb-0">Gäller hela tråden, även svaren.</p>`,
            onOk: async body => {
                const r = await post('SetMessageArea', { messageId: m.id, areaId: body.querySelector('#maArea').value });
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
                // Bemanningen läser om sig själv (egen pollning) — en omritning här hade stängt rutans inmatning.
                else if (!typing && !editing && view !== 'installningar' && view !== 'bemanning' && !document.querySelector('.modal.show')) render(view);
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
