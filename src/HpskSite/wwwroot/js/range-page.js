/*
 * Banans egen sida — /skjutbana/{id} (Views/RangePage.cshtml), 2026-10-11.
 *
 * Delarna (rälsen): Översikt, Kalender, Banor och vallar, Klubbar och tider — för alla medlemmar —
 * och för banans förvaltare dessutom Tillstånd och dokument, Aktivitet, Incheckning och Förvaltare.
 * Delen står i adressen (#vy=…), så bakåtknappen, en omladdning och länkar i mejl landar rätt.
 * Blå Åtgärder bär handlingar för delen man står på; grå Åtgärder per rad.
 *
 * Regler som gäller hela filen (samma som /styrelse):
 *  - Ingen confirm()/alert()/prompt(): frågor går genom ask(), formulär genom formModal(), besked genom showMsg().
 *  - Ingen användartext i ett onclick-attribut: knappar bär index i data-attribut och läses av EN lyssnare.
 *  - Ett anrop som inte gick fram får aldrig vara tyst (request()).
 *  - Servern prövar behörigheten i varje endpoint; det som döljs här döljs för att det inte går att göra.
 */
(function () {
    'use strict';
    const app = document.getElementById('rpApp');
    if (!app) return;
    const RANGE_ID = parseInt(app.dataset.rangeId, 10) || 0;
    const $ = id => document.getElementById(id);
    const body = () => $('rpBody');

    const DAYS = { 1: 'Måndag', 2: 'Tisdag', 3: 'Onsdag', 4: 'Torsdag', 5: 'Fredag', 6: 'Lördag', 7: 'Söndag' };
    const DAYS_SHORT = { 1: 'Mån', 2: 'Tis', 3: 'Ons', 4: 'Tor', 5: 'Fre', 6: 'Lör', 7: 'Sön' };
    // Klubbens relation till banan i klartext. "Huvudman" finns i äldre data och betyder ägare.
    const RELATION = { Owner: 'Ägare', Huvudman: 'Ägare', PrimaryUser: 'Huvudanvändare', User: 'Användare', Tenant: 'Hyresgäst' };
    const RELATION_ORDER = ['Owner', 'Huvudman', 'PrimaryUser', 'User', 'Tenant'];
    const STATUS = { Active: ['Aktiv', 'ok'], Inactive: ['Inte i bruk', 'wait'], Decommissioned: ['Avvecklad', 'bad'], UnclaimedSeed: ['Ingen förvaltare ännu', 'wait'] };
    const PERMIT = { PoliceTillstand: 'Polistillstånd', EnvAnmalanC: 'Miljö – anmälan (C)', EnvTillstandB: 'Miljö – tillstånd (B)', Other: 'Annat tillstånd' };
    const DOC = { PoliceTillstand: 'Polistillstånd', Besiktningsprotokoll: 'Besiktningsprotokoll', Skjutbaneinstruktion: 'Skjutbaneinstruktion', EnvDecision: 'Miljöbeslut', Bullerutredning: 'Bullerutredning', Markundersokning: 'Markundersökning (bly)', Skotselplan: 'Skötselplan', Insurance: 'Försäkring', Other: 'Annat' };
    const SOURCE = { Competition: 'Tävling', TrainingLog: 'Träning', QrSelfReported: 'Incheckning (QR)', ManualBulk: 'Inlagd för hand', Estimated: 'Uppskattning' };
    const KIND_ICON = { competition: 'bi-trophy', training: 'bi-bullseye', event: 'bi-calendar-event', allocation: 'bi-clock', external: 'bi-person-badge' };

    let D = null;          // GetRange-svaret
    let todoCount = 0;

    // =====================================================================
    //  Anrop
    // =====================================================================
    function token() { return document.querySelector('input[name="__RequestVerificationToken"]')?.value || ''; }
    function request(url, opts) {
        return fetch(url, Object.assign({ credentials: 'same-origin' }, opts || {})).then(
            r => r.text().then(text => {
                let data = null;
                try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
                if (r.ok && data !== null) return data;
                throw failure(r.status, data);
            }),
            () => { throw failure(0, null); });
    }
    function failure(status, data) {
        const info = window.hpskRequestFailureMessage ? window.hpskRequestFailureMessage(status) : { msg: 'Anropet misslyckades.', reload: false };
        const msg = (data && data.message) ? data.message : info.msg;
        if (window.hpskReportRequestFailure) window.hpskReportRequestFailure(msg, info.reload);
        const err = new Error(msg); err.shown = true; return err;
    }
    const get = (path, qs) => request('/umbraco/surface/' + path + (qs ? '?' + new URLSearchParams(qs).toString() : ''));
    const post = (path, payload) => request('/umbraco/surface/' + path, {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token() },
        body: JSON.stringify(payload || {})
    });

    function esc(s) {
        if (s === null || s === undefined) return '';
        return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[c]));
    }
    const num = n => Number(n || 0).toLocaleString('sv-SE');
    function spinner() { return '<div class="text-center text-muted py-4"><div class="spinner-border"></div></div>'; }
    function errBox(m) { return `<div class="rp-card"><p class="text-danger mb-0">${esc(m)}</p></div>`; }
    function ymd(d) { const p = n => (n < 10 ? '0' : '') + n; return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()); }

    // =====================================================================
    //  Skalet: rubrik, Åtgärder, besked, dialoger
    // =====================================================================
    function setHead(title, sub) {
        $('rpTitle').textContent = title || '';
        $('rpSub').innerHTML = sub || '';
        document.title = (title && D ? title + ' – ' : '') + (D ? D.range.name : 'Skjutbana');
    }
    let actionFns = [];
    function setActions(items) {
        const wrap = $('rpActions'), list = $('rpActionsList');
        actionFns = [];
        const real = (items || []).filter(Boolean);
        if (!real.some(i => i.fn)) { wrap.classList.add('d-none'); list.innerHTML = ''; return; }
        list.innerHTML = real.map(i => {
            if (i.header) return `<li><h6 class="dropdown-header">${esc(i.header)}</h6></li>`;
            if (i.divider) return '<li><hr class="dropdown-divider"></li>';
            actionFns.push(i.fn);
            return `<li><button type="button" class="dropdown-item${i.danger ? ' text-danger' : ''}" data-rp-action="${actionFns.length - 1}">`
                + (i.icon ? `<i class="bi ${i.icon} me-2${i.danger ? '' : ' text-secondary'}"></i>` : '') + `${esc(i.label)}</button></li>`;
        }).join('');
        wrap.classList.remove('d-none');
    }
    $('rpActionsList').addEventListener('click', e => {
        const b = e.target.closest('[data-rp-action]'); if (!b) return;
        const fn = actionFns[parseInt(b.dataset.rpAction, 10)]; if (fn) fn();
    });

    let rowFns = [];
    function rowMenu(items) {
        const real = (items || []).filter(Boolean);
        if (!real.some(i => i.fn)) return '';
        const html = real.map(i => {
            if (i.divider) return '<li><hr class="dropdown-divider"></li>';
            rowFns.push(i.fn);
            return `<li><button type="button" class="dropdown-item${i.danger ? ' text-danger' : ''}" data-rp-row="${rowFns.length - 1}">${esc(i.label)}</button></li>`;
        }).join('');
        return `<div class="dropdown d-inline-block">
            <button class="btn btn-sm btn-outline-secondary dropdown-toggle" type="button" data-bs-toggle="dropdown"
                    data-bs-popper-config='{"strategy":"fixed"}' aria-expanded="false">Åtgärder</button>
            <ul class="dropdown-menu dropdown-menu-end">${html}</ul></div>`;
    }
    // Knappar i innehållet: data-rp-btn = index i samma lista som radmenyerna.
    function btn(label, fn, cls, icon) {
        rowFns.push(fn);
        return `<button type="button" class="btn ${cls || 'btn-outline-primary btn-sm'}" data-rp-row="${rowFns.length - 1}">`
            + (icon ? `<i class="bi ${icon} me-1" aria-hidden="true"></i>` : '') + esc(label) + '</button>';
    }
    document.addEventListener('click', e => {
        const b = e.target.closest('[data-rp-row]'); if (!b) return;
        const fn = rowFns[parseInt(b.dataset.rpRow, 10)]; if (fn) fn();
    });

    let msgKeep = false;
    function showMsg(html, kind, keep) {
        const el = $('rpMsg');
        el.innerHTML = html ? `<div class="alert alert-${kind || 'success'} alert-dismissible fade show" role="alert">${html}
            <button type="button" class="btn-close" data-bs-dismiss="alert" aria-label="Stäng"></button></div>` : '';
        msgKeep = !!keep;
        if (html) el.scrollIntoView({ block: 'nearest' });
    }
    function fail(r, fallback) { showMsg(esc((r && r.message) || fallback || 'Det gick inte.'), 'danger'); }

    function ask(title, text, okLabel, danger) {
        return new Promise(resolve => {
            const m = $('rpConfirmModal');
            $('rpConfirmTitle').textContent = title;
            $('rpConfirmText').textContent = text || '';
            const ok = $('rpConfirmOk');
            ok.textContent = okLabel || 'OK';
            ok.className = 'btn ' + (danger ? 'btn-danger' : 'btn-primary');
            let answered = false;
            const modal = bootstrap.Modal.getOrCreateInstance(m);
            ok.onclick = () => { answered = true; modal.hide(); };
            m.addEventListener('hidden.bs.modal', function h() { m.removeEventListener('hidden.bs.modal', h); resolve(answered); });
            modal.show();
        });
    }
    // Kalenderns "Ta bort bokningen" använder samma fråga.
    window.hpskRangeConfirm = ask;

    // onOk(bodyEl) returnerar ett löfte: true = stäng, en sträng = visa felet i dialogen.
    function formModal({ title, html, okLabel, okClass, onOpen, onOk, wide }) {
        const m = $('rpFormModal');
        $('rpFormTitle').textContent = title;
        $('rpFormBody').innerHTML = html;
        const err = $('rpFormErr'); err.classList.add('d-none'); err.textContent = '';
        const ok = $('rpFormOk');
        ok.textContent = okLabel || 'Spara';
        ok.className = 'btn ' + (okClass || 'btn-primary');
        ok.classList.toggle('d-none', !onOk);
        ok.disabled = false;
        m.querySelector('.modal-dialog').classList.toggle('modal-lg', !!wide);
        const modal = bootstrap.Modal.getOrCreateInstance(m);
        ok.onclick = async () => {
            ok.disabled = true;
            try {
                const res = await onOk($('rpFormBody'));
                if (res === true) { modal.hide(); return; }
                if (typeof res === 'string') { err.textContent = res; err.classList.remove('d-none'); }
            } catch (e) {
                err.textContent = (e && e.message) || 'Det gick inte att spara.'; err.classList.remove('d-none');
            } finally { ok.disabled = false; }
        };
        modal.show();
        if (onOpen) m.addEventListener('shown.bs.modal', function h() { m.removeEventListener('shown.bs.modal', h); onOpen($('rpFormBody')); });
    }
    function datePick(el) {
        if (el && typeof flatpickr !== 'undefined') flatpickr(el, { locale: 'sv', dateFormat: 'Y-m-d', allowInput: true });
    }
    const val = (root, sel) => (root.querySelector(sel)?.value || '').trim();
    const intOrNull = v => { const n = parseInt(v, 10); return isNaN(n) ? null : n; };

    // =====================================================================
    //  Rälsen och vägvalet
    // =====================================================================
    const cm = () => !!(D && D.canManage);
    function parts() {
        const list = [['oversikt', 'Översikt'], ['kalender', 'Kalender'], ['banor', 'Banor och vallar'], ['klubbar', 'Klubbar och tider']];
        if (cm()) list.push({ group: 'Förvaltning' }, ['tillstand', 'Tillstånd och dokument'], ['aktivitet', 'Aktivitet'], ['incheckning', 'Incheckning'], ['forvaltare', 'Förvaltare']);
        return list;
    }
    function renderRail(active) {
        $('rpRail').innerHTML = parts().map(p => {
            if (p.group) return `<div class="rp-group">${esc(p.group)}</div>`;
            const [k, label] = p;
            const n = k === 'oversikt' && todoCount ? `<span class="rp-count" title="Att göra">${todoCount}</span>` : '';
            return `<a href="#vy=${k}" class="rp-nav${k === active ? ' on' : ''}">${esc(label)}${n}</a>`;
        }).join('');
    }
    function parseHash() {
        const p = new URLSearchParams(location.hash.replace(/^#/, ''));
        let view = p.get('vy') || 'oversikt';
        const allowed = parts().filter(x => !x.group).map(x => x[0]);
        if (!allowed.includes(view)) view = 'oversikt';
        return { view, claim: p.get('ansprak') === '1' };
    }
    function go(view) {
        const h = '#vy=' + view;
        if (location.hash === h) route(); else location.hash = h;
    }
    window.addEventListener('hashchange', route);
    function route() {
        if (!D) return;
        const { view, claim } = parseHash();
        renderRail(view);
        if (!msgKeep) showMsg('');
        msgKeep = false;
        rowFns = [];
        setActions([]);
        const isCal = view === 'kalender';
        $('rpCalPart').classList.toggle('d-none', !isCal);
        body().classList.toggle('d-none', isCal);
        body().innerHTML = isCal ? '' : spinner();
        try { VIEWS[view](); } catch (e) { body().innerHTML = errBox('Delen kunde inte visas: ' + e.message); }
        if (claim && D.canClaim) { history.replaceState(null, '', '#vy=oversikt'); claimDialog(); }
    }

    function load() {
        return get('ShootingRange/GetRange', { id: RANGE_ID }).then(d => {
            if (!d.success) {
                $('rpName').textContent = 'Skjutbana';
                body().innerHTML = errBox(d.message || 'Skjutbanan kunde inte hämtas.');
                return false;
            }
            D = d;
            $('rpName').textContent = d.range.name;
            $('rpRole').textContent = roleText();
            return true;
        });
    }
    function reload(view) { return load().then(ok => { if (ok) { if (view) go(view); else route(); refreshTodo(); } }); }

    function myClubLinks() {
        const mine = new Set(D.myClubIds || []);
        return (D.clubs || []).filter(c => mine.has(c.clubId));
    }
    function roleText() {
        if (D.isSteward) return 'Förvaltare';
        const mine = myClubLinks();
        if (mine.length) return mine.map(c => c.clubName + ' · ' + (RELATION[c.relationType] || 'Användare').toLowerCase()).join(', ');
        if (D.isSiteAdmin) return 'Sajtadministratör';
        return 'Medlem';
    }
    function refreshTodo() {
        if (!cm()) { todoCount = 0; return; }
        get('ShootingRange/GetRangeTodo', { rangeId: RANGE_ID }).then(r => {
            todoCount = r.success ? (r.items || []).filter(t => t.severity !== 'info').length : 0;
            renderRail(parseHash().view);
        }).catch(() => { });
    }

    // =====================================================================
    //  ÖVERSIKT
    // =====================================================================
    function viewOversikt() {
        const r = D.range;
        const st = STATUS[r.status] || [r.status, ''];
        setHead(r.name, [r.city || r.municipality, r.county].filter(Boolean).map(esc).join(' · ') + ` <span class="rp-state ${st[1]} ms-1">${esc(st[0])}</span>`);
        setActions([
            D.canEdit ? { label: 'Ändra banans uppgifter…', icon: 'bi-pencil', fn: editRange } : null,
            D.canClaim ? { label: 'Gör anspråk på banan…', icon: 'bi-flag', fn: claimDialog } : null,
            D.canDelete ? { divider: true } : null,
            D.canDelete ? { label: 'Ta bort skjutbanan…', icon: 'bi-trash', danger: true, fn: deleteRange } : null
        ]);

        let h = '';
        if (r.status === 'UnclaimedSeed') {
            h += `<div class="rp-card" style="background:var(--bs-warning-bg-subtle);border-color:var(--bs-warning-border-subtle)">
                <h2>Ingen förvaltar banan ännu</h2>
                <p class="mb-2">Banan kommer från en karta över Sveriges skjutbanor. När en klubb gör anspråk på den blir du förvaltare och kan lägga in banor, klubbarnas tider, tillstånd och dokument.</p>
                ${D.canClaim ? btn('Gör anspråk på banan', claimDialog, 'btn-primary', 'bi-flag')
                    : '<p class="rp-help mb-0">Det är klubbens administratör som gör anspråk. Hör med din klubb om det här är er bana.</p>'}</div>`;
        }
        if (cm()) h += '<div id="rpTodo"></div>';

        // Hitta hit
        const pos = window.hpskRangeMap && window.hpskRangeMap.hasPos(r);
        const addr = window.hpskRangeMap ? window.hpskRangeMap.addressText(r) : '';
        h += `<div class="rp-card"><div class="rp-cardh"><h2>Hitta hit</h2>${pos ? `<a class="btn btn-outline-primary btn-sm" target="_blank" rel="noopener" href="${esc(window.hpskRangeMap.directionsUrl(r))}"><i class="bi bi-signpost-2 me-1"></i>Vägbeskrivning</a>` : ''}</div>
            ${addr ? `<div class="mb-2"><i class="bi bi-geo-alt-fill text-danger me-1"></i>${esc(addr)}</div>` : ''}
            ${pos ? '<div class="rp-map" id="rpMap"></div>'
                : (r.locationSensitivity === 'Restricted' && !cm()
                    ? '<p class="rp-help mb-0">Banans plats visas bara för banans förvaltare.</p>'
                    : `<p class="rp-help mb-0">Banan har ingen plats på kartan ännu.${D.canEdit ? ' Välj <strong>Ändra banans uppgifter</strong> under Åtgärder och klicka ut den.' : ''}</p>`)}
            ${r.description ? `<p class="mt-3 mb-0" style="white-space:pre-line">${esc(r.description)}</p>` : ''}</div>`;

        // Om banan
        const owner = r.huvudmanClubName || r.huvudmanName;
        const chef = [r.skjutbanechefName, r.skjutbanechefContact].filter(Boolean).join(' · ');
        const clubs = sortClubs(D.clubs || []);
        h += `<div class="rp-card"><h2>Om banan</h2><dl class="rp-facts row mb-0">
            <div class="col-sm-6"><dt>Ägare</dt><dd>${owner ? esc(owner) : '<span class="rp-help">Inte angivet</span>'}</dd></div>
            <div class="col-sm-6"><dt>Skjutbanechef</dt><dd>${chef ? esc(chef) : '<span class="rp-help">Inte angivet</span>'}</dd></div>
            <div class="col-sm-6"><dt>Klubbar på banan</dt><dd>${clubs.length ? clubs.map(c => esc(c.clubName) + ` <span class="rp-help">(${esc((RELATION[c.relationType] || c.relationType || '').toLowerCase())})</span>`).join('<br>') : '<span class="rp-help">Ingen klubb är kopplad</span>'}</dd></div>
            <div class="col-sm-6"><dt>Banor och vallar</dt><dd>${(D.sections || []).length ? (D.sections || []).map(s => esc(s.label)).join(', ') : '<span class="rp-help">Inga inlagda</span>'}</dd></div>
        </dl></div>`;

        h += '<div id="rpCheckIn"></div>';
        h += '<div class="rp-card" id="rpWeek"><h2>Den här veckan på banan</h2>' + spinner() + '</div>';
        body().innerHTML = h;

        if (pos) window.hpskRangeMap.render($('rpMap'), r);
        if (cm()) loadTodo();
        loadCheckIn();
        loadWeek();
    }

    function loadTodo() {
        get('ShootingRange/GetRangeTodo', { rangeId: RANGE_ID }).then(r => {
            const box = $('rpTodo'); if (!box) return;
            if (!r.success) { box.innerHTML = errBox(r.message || 'Listan kunde inte hämtas.'); return; }
            const items = r.items || [];
            todoCount = items.filter(t => t.severity !== 'info').length;
            renderRail('oversikt');
            if (!items.length) {
                box.innerHTML = '<div class="rp-card"><h2>Att göra</h2><p class="mb-0"><i class="bi bi-check-circle text-success me-1"></i>Inget väntar — tillstånd, tider och uppgifter är i ordning.</p></div>';
                return;
            }
            box.innerHTML = '<div class="rp-card"><h2>Att göra</h2>' + items.map(t => {
                const fn = t.goTo === 'redigera' ? editRange : () => go(t.goTo);
                return `<div class="rp-todo ${t.severity === 'info' ? '' : t.severity}">
                    <i class="bi ${t.severity === 'danger' ? 'bi-exclamation-octagon text-danger' : t.severity === 'warn' ? 'bi-exclamation-triangle text-warning-emphasis' : 'bi-info-circle text-secondary'}" aria-hidden="true"></i>
                    <div class="t">${esc(t.text)}</div>${btn(t.goTo === 'redigera' ? 'Fyll i' : 'Visa', fn, 'btn-outline-secondary btn-sm')}</div>`;
            }).join('') + '</div>';
        }).catch(() => { });
    }

    function loadCheckIn() {
        get('ShootingRange/CheckInStatus', { rangeId: RANGE_ID }).then(r => {
            const box = $('rpCheckIn'); if (!box || !r.success) return;
            if (r.checkedIn) {
                box.innerHTML = `<div class="rp-card" style="background:var(--bs-success-bg-subtle);border-color:var(--bs-success-border-subtle)">
                    <h2>Du är incheckad${r.since ? ' sedan ' + esc(r.since) : ''}</h2>
                    <div class="d-flex flex-wrap align-items-end gap-2">
                        <div><label class="form-label small mb-1" for="rpShots">Antal skott du sköt</label>
                        <input type="number" min="0" class="form-control" id="rpShots" style="max-width:10rem" inputmode="numeric"></div>
                        ${btn('Checka ut', () => checkOut(r.sessionId), 'btn-primary')}
                    </div></div>`;
            } else {
                box.innerHTML = `<div class="rp-card d-flex flex-wrap align-items-center gap-3 justify-content-between">
                    <div><h2 class="mb-1">Är du på banan nu?</h2><div class="rp-help">Checka in när du börjar och ut med antal skott när du är klar. Det hjälper banan att redovisa sin aktivitet.</div></div>
                    ${btn('Checka in', checkIn, 'btn-outline-primary', 'bi-box-arrow-in-right')}</div>`;
            }
        }).catch(() => { });
    }
    function checkIn() {
        post('ShootingRange/CheckIn', { id: RANGE_ID }).then(r => {
            if (!r.success) { fail(r); return; }
            showMsg('Du är incheckad. Checka ut här när du är klar.', 'success');
            loadCheckIn();
        }).catch(() => { });
    }
    function checkOut(sessionId) {
        const shots = parseInt($('rpShots')?.value, 10);
        if (isNaN(shots) || shots < 0) { showMsg('Skriv hur många skott du sköt (0 om du inte sköt).', 'warning'); $('rpShots')?.focus(); return; }
        post('ShootingRange/CheckOut', { sessionId, shotCount: shots }).then(r => {
            if (!r.success) { fail(r); return; }
            showMsg('Du är utcheckad. Tack!', 'success');
            loadCheckIn();
        }).catch(() => { });
    }

    function loadWeek() {
        const from = new Date(); from.setHours(0, 0, 0, 0);
        const to = new Date(from); to.setDate(to.getDate() + 6);
        get('RangeCalendarApi/GetCalendar', { rangeId: RANGE_ID, from: ymd(from), to: ymd(to) }).then(r => {
            const box = $('rpWeek'); if (!box) return;
            if (!r.success) { box.innerHTML = '<h2>Den här veckan på banan</h2>' + errBox(r.message || 'Kalendern kunde inte läsas.'); return; }
            const items = r.items || [];
            const link = `<a href="#vy=kalender" class="btn btn-outline-primary btn-sm">Hela kalendern</a>`;
            if (!items.length) {
                box.innerHTML = `<div class="rp-cardh"><h2>Den här veckan på banan</h2>${link}</div><p class="rp-help mb-0">Inget är inlagt de kommande sju dagarna.</p>`;
                return;
            }
            const byDay = {};
            items.forEach(i => (byDay[i.date] = byDay[i.date] || []).push(i));
            let h = '';
            Object.keys(byDay).sort().forEach(k => {
                const d = new Date(k + 'T00:00:00');
                h += `<div class="rp-week-day">${esc(d.toLocaleDateString('sv-SE', { weekday: 'long', day: 'numeric', month: 'short' }))}</div>`;
                h += byDay[k].map(i => {
                    const what = i.kind === 'allocation' ? 'Fast tid' + (i.note ? ' (' + esc(i.note) + ')' : '') : esc(i.title);
                    const tm = i.start ? i.start + (i.end ? '–' + i.end : '') : 'hela dagen';
                    return `<div class="rp-week-row${i.isCancelled ? ' rc-cancel' : ''}"><div class="tm">${esc(tm)}</div>
                        <div><i class="bi ${KIND_ICON[i.kind] || ''} text-muted me-1" aria-hidden="true"></i>${what}
                        <span class="rp-help">${esc([i.ownerName, i.sectionLabel].filter(Boolean).join(' · '))}</span>${i.isCancelled ? ' <span class="badge bg-danger">Inställd</span>' : ''}</div></div>`;
                }).join('');
            });
            box.innerHTML = `<div class="rp-cardh"><h2>Den här veckan på banan</h2>${link}</div>` + h;
        }).catch(() => { });
    }

    function editRange() { window.hpskRangeEdit.open(D.range, null, () => { showMsg('Banans uppgifter är sparade.', 'success', true); reload(); }); }

    function claimDialog() {
        get('ShootingRange/GetMyAdminClubs').then(r => {
            const clubs = (r.success && r.clubs) || [];
            formModal({
                title: 'Gör anspråk på banan',
                html: clubs.length
                    ? `<p>Välj klubben som använder banan. Du blir banans förvaltare och kan sedan lägga in banor, klubbarnas tider, tillstånd och dokument — och lägga till fler förvaltare.</p>
                       <label class="form-label" for="rpClaimClub">Klubb</label>
                       <select class="form-select" id="rpClaimClub">${clubs.map(c => `<option value="${c.clubId}">${esc(c.clubName)}</option>`).join('')}</select>`
                    : '<p class="mb-0">Du är inte administratör för någon klubb, och det är klubbens administratör som gör anspråk.</p>',
                okLabel: 'Gör anspråk',
                onOk: clubs.length ? async b => {
                    const res = await post('ShootingRange/ClaimRange', { rangeId: RANGE_ID, clubId: parseInt(val(b, '#rpClaimClub'), 10) });
                    if (!res.success) return res.message || 'Anspråket gick inte igenom.';
                    showMsg('Klart — du förvaltar nu banan.', 'success', true);
                    reload('oversikt');
                    return true;
                } : null
            });
        }).catch(() => { });
    }

    async function deleteRange() {
        if (!await ask('Ta bort skjutbanan?', `${D.range.name} tas bort med banor, klubbarnas tider, tillstånd, dokument och aktivitet. Träningar som legat på banan blir kvar men utan bana. Det går inte att ångra.`, 'Ta bort', true)) return;
        request('/umbraco/surface/ShootingRange/DeleteRange?id=' + RANGE_ID, { method: 'POST', headers: { 'RequestVerificationToken': token() } })
            .then(r => { if (!r.success) { fail(r); return; } location.href = '/skjutbanor'; }).catch(() => { });
    }

    // =====================================================================
    //  KALENDER
    // =====================================================================
    function viewKalender() {
        setHead('Kalender', 'Allt som klubbarna på banan har lagt in: tävlingar, träningar, händelser och fasta tider.');
        setActions([
            { label: 'Prenumerera i din kalender…', icon: 'bi-calendar-plus', fn: () => window.hpskRangeCalendar.openIcs() },
            cm() ? { divider: true } : null,
            cm() ? { header: 'Banans förvaltare' } : null,
            cm() ? { label: 'Lägg in en bokning för andra…', icon: 'bi-plus-circle', fn: () => window.hpskRangeCalendar.openExternal() } : null
        ]);
        if (window.hpskRangeCalendar) window.hpskRangeCalendar.start();
    }

    // =====================================================================
    //  BANOR OCH VALLAR
    // =====================================================================
    function viewBanor() {
        setHead('Banor och vallar', 'Banorna och vallarna på skjutbanan. Träningar, händelser och fasta tider kan läggas på en bestämd vall — då kan två klubbar skjuta samtidigt på olika vallar.');
        setActions([cm() ? { label: 'Lägg till bana eller vall…', icon: 'bi-plus-lg', fn: () => sectionDialog(null) } : null]);
        const s = D.sections || [];
        if (!s.length) {
            body().innerHTML = `<div class="rp-card"><p class="mb-0 rp-help">Inga banor eller vallar är inlagda.${cm() ? ' Välj <strong>Lägg till bana eller vall</strong> under Åtgärder.' : ''}</p></div>`;
            return;
        }
        body().innerHTML = `<div class="rp-card"><div class="table-responsive"><table class="table align-middle rp-table mb-0">
            <thead><tr><th>Bana / vall</th><th>Typ</th><th class="d-none d-md-table-cell">Avstånd</th><th class="d-none d-md-table-cell">Skjutplatser</th><th class="d-none d-lg-table-cell">Kulfång</th><th></th></tr></thead><tbody>
            ${s.map(x => `<tr>
                <td><div class="fw-semibold">${esc(x.label)}</div>
                    <div class="small rp-help d-md-none">${esc([x.distanceMeters ? x.distanceMeters + ' m' : '', x.firingPoints ? x.firingPoints + ' skjutplatser' : ''].filter(Boolean).join(' · '))}</div>
                    ${x.allowedWeaponsCalibers ? `<div class="small rp-help">Vapen: ${esc(x.allowedWeaponsCalibers)}</div>` : ''}
                    ${x.notes ? `<div class="small rp-help">${esc(x.notes)}</div>` : ''}</td>
                <td>${esc(x.banaType || '')}</td>
                <td class="d-none d-md-table-cell">${x.distanceMeters ? esc(x.distanceMeters) + ' m' : ''}</td>
                <td class="d-none d-md-table-cell">${x.firingPoints ? esc(x.firingPoints) : ''}</td>
                <td class="d-none d-lg-table-cell small">${esc(x.kulfangSpec || '')}</td>
                <td class="text-end">${cm() ? rowMenu([
                    { label: 'Ändra…', fn: () => sectionDialog(x) },
                    { divider: true },
                    { label: 'Ta bort…', danger: true, fn: () => deleteSection(x) }]) : ''}</td></tr>`).join('')}
            </tbody></table></div></div>`;
    }
    function sectionDialog(x) {
        const v = k => esc(x && x[k] != null ? x[k] : '');
        formModal({
            title: x ? 'Ändra ' + x.label : 'Ny bana eller vall', wide: true,
            html: `<div class="row g-3">
                <div class="col-md-6"><label class="form-label" for="sLabel">Namn *</label><input class="form-control" id="sLabel" value="${v('label')}" placeholder="Till exempel Vall A eller 25 m pistol" autocomplete="off"></div>
                <div class="col-md-6"><label class="form-label" for="sType">Typ</label><input class="form-control" id="sType" value="${v('banaType')}" placeholder="Pistol 25 m, Gevär 300 m …" autocomplete="off"></div>
                <div class="col-4"><label class="form-label" for="sDist">Avstånd (m)</label><input type="number" class="form-control" id="sDist" value="${v('distanceMeters')}"></div>
                <div class="col-4"><label class="form-label" for="sPts">Skjutplatser</label><input type="number" class="form-control" id="sPts" value="${v('firingPoints')}"></div>
                <div class="col-4"><label class="form-label" for="sDir">Riktning (°)</label><input type="number" class="form-control" id="sDir" value="${v('directionDegrees')}"></div>
                <div class="col-12"><label class="form-label" for="sWeap">Tillåtna vapen och kalibrar</label><input class="form-control" id="sWeap" value="${v('allowedWeaponsCalibers')}" autocomplete="off"></div>
                <div class="col-12"><label class="form-label" for="sKul">Kulfång</label><textarea class="form-control" id="sKul" rows="2">${v('kulfangSpec')}</textarea></div>
                <div class="col-12"><label class="form-label" for="sNotes">Anteckningar</label><textarea class="form-control" id="sNotes" rows="2">${v('notes')}</textarea></div></div>`,
            onOk: async b => {
                if (!val(b, '#sLabel')) return 'Skriv ett namn.';
                const r = await post('ShootingRange/SaveSection', {
                    id: x ? x.id : 0, rangeId: RANGE_ID, label: val(b, '#sLabel'), banaType: val(b, '#sType'),
                    distanceMeters: intOrNull(val(b, '#sDist')), firingPoints: intOrNull(val(b, '#sPts')), directionDegrees: intOrNull(val(b, '#sDir')),
                    allowedWeaponsCalibers: val(b, '#sWeap'), kulfangSpec: val(b, '#sKul'), notes: val(b, '#sNotes')
                });
                if (!r.success) return r.message || 'Det gick inte att spara.';
                reload('banor');
                return true;
            }
        });
    }
    async function deleteSection(x) {
        if (!await ask('Ta bort ' + x.label + '?', 'Träningar, händelser och fasta tider som ligger på vallen ligger sedan på hela banan.', 'Ta bort', true)) return;
        post('ShootingRange/DeleteSection', { id: x.id, rangeId: RANGE_ID }).then(r => { if (!r.success) fail(r); else reload('banor'); }).catch(() => { });
    }

    // =====================================================================
    //  KLUBBAR OCH TIDER
    // =====================================================================
    function sortClubs(clubs) {
        const mine = new Set(D.myClubIds || []);
        const rank = c => { const i = RELATION_ORDER.indexOf(c.relationType); return i < 0 ? 9 : i; };
        return clubs.slice().sort((a, b) => (mine.has(b.clubId) - mine.has(a.clubId)) || (rank(a) - rank(b)) || a.clubName.localeCompare(b.clubName, 'sv'));
    }
    function outside(a) {
        const wins = D.allowedWindows || [];
        if (!wins.length) return false;
        const t = s => { const p = String(s).split(':'); return (+p[0]) * 60 + (+p[1] || 0); };
        return !wins.some(w => w.day === a.dayOfWeek && t(w.start) <= t(a.startTime) && t(a.endTime) <= t(w.end));
    }
    function viewKlubbar() {
        setHead('Klubbar och tider', 'Klubbarna som använder banan och deras fasta tider varje vecka. Tiderna syns i banans kalender, så klubbarna ser när banan är upptagen.');
        setActions([cm() ? { label: 'Koppla en klubb till banan…', icon: 'bi-plus-lg', fn: linkDialog } : null]);
        const clubs = sortClubs(D.clubs || []);
        if (!clubs.length) {
            body().innerHTML = `<div class="rp-card"><p class="mb-0 rp-help">Ingen klubb är kopplad till banan.${cm() ? ' Välj <strong>Koppla en klubb till banan</strong> under Åtgärder.' : ''}</p></div>`;
            return;
        }
        const secLabel = id => (D.sections || []).find(s => s.id === id)?.label || 'Hela banan';
        const mine = new Set(D.myClubIds || []);
        body().innerHTML = clubs.map(c => {
            const allocs = (c.allocations || []).slice().sort((a, b) => a.dayOfWeek - b.dayOfWeek || a.startTime.localeCompare(b.startTime));
            const rows = allocs.map(a => `<tr>
                <td>${esc(DAYS[a.dayOfWeek])}</td><td class="text-nowrap">${esc(a.startTime)}–${esc(a.endTime)}</td>
                <td class="d-none d-sm-table-cell">${esc(secLabel(a.rangeSectionId))}</td>
                <td>${esc(a.note || '')}${cm() && outside(a) ? ' <span class="badge bg-warning-subtle text-warning-emphasis" title="Tiden ligger utanför skjuttiderna i banans tillstånd"><i class="bi bi-exclamation-triangle me-1"></i>Utanför tillståndet</span>' : ''}</td>
                <td class="text-end">${cm() ? rowMenu([
                    { label: 'Ändra tiden…', fn: () => allocDialog(c, a) },
                    { divider: true },
                    { label: 'Ta bort tiden…', danger: true, fn: () => deleteAlloc(c, a) }]) : ''}</td></tr>`).join('');
            return `<div class="rp-card"><div class="rp-cardh">
                <h2>${esc(c.clubName)} <span class="rp-state ms-1">${esc(RELATION[c.relationType] || c.relationType)}</span>${mine.has(c.clubId) ? ' <span class="badge bg-primary-subtle text-primary-emphasis ms-1">Din klubb</span>' : ''}</h2>
                ${cm() ? rowMenu([
                    { label: 'Lägg till en tid…', fn: () => allocDialog(c, null) },
                    { label: 'Ändra klubbens roll på banan…', fn: () => relationDialog(c) },
                    { divider: true },
                    { label: 'Ta bort klubben från banan…', danger: true, fn: () => removeLink(c) }]) : ''}</div>
                ${allocs.length ? `<div class="table-responsive"><table class="table align-middle rp-table mb-0"><thead><tr><th>Dag</th><th>Tid</th><th class="d-none d-sm-table-cell">Bana / vall</th><th>Notering</th><th></th></tr></thead><tbody>${rows}</tbody></table></div>`
                    : `<p class="rp-help mb-0">Inga fasta tider.${cm() ? ' ' : ''}</p>${cm() ? '<div class="mt-2">' + btn('Lägg till en tid', () => allocDialog(c, null), 'btn-outline-primary btn-sm', 'bi-plus-lg') + '</div>' : ''}`}
            </div>`;
        }).join('');
    }
    function relationOptions(selected) {
        return ['Owner', 'PrimaryUser', 'User', 'Tenant'].map(k => `<option value="${k}"${k === selected || (selected === 'Huvudman' && k === 'Owner') ? ' selected' : ''}>${esc(RELATION[k])}</option>`).join('');
    }
    const RELATION_HELP = '<div class="form-text">Ägare äger banan. Huvudanvändare använder den mest. Hyresgäst hyr tider av ägaren.</div>';
    function linkDialog() {
        get('ClubAdmin/GetClubsForRegistration').then(r => {
            const linked = new Set((D.clubs || []).map(c => c.clubId));
            const clubs = ((r.success && r.clubs) || []).filter(c => !linked.has(c.id));
            formModal({
                title: 'Koppla en klubb till banan',
                html: `<div class="mb-3"><label class="form-label" for="lClub">Klubb</label>
                    <input class="form-control mb-2" id="lFilter" placeholder="Sök klubb…" autocomplete="off">
                    <select class="form-select" id="lClub" size="8">${clubs.map(c => `<option value="${c.id}">${esc(c.name)}</option>`).join('')}</select></div>
                    <div><label class="form-label" for="lRel">Klubbens roll på banan</label><select class="form-select" id="lRel">${relationOptions('User')}</select>${RELATION_HELP}</div>`,
                onOpen: b => {
                    const f = b.querySelector('#lFilter'), sel = b.querySelector('#lClub');
                    f.addEventListener('input', () => {
                        const q = f.value.toLowerCase();
                        sel.innerHTML = clubs.filter(c => c.name.toLowerCase().includes(q)).map(c => `<option value="${c.id}">${esc(c.name)}</option>`).join('');
                    });
                    f.focus();
                },
                okLabel: 'Koppla klubben',
                onOk: async b => {
                    const clubId = parseInt(val(b, '#lClub'), 10);
                    if (!clubId) return 'Välj en klubb i listan.';
                    const res = await post('ShootingRange/AddClubLink', { rangeId: RANGE_ID, clubId, relationType: val(b, '#lRel') });
                    if (!res.success) return res.message || 'Klubben kunde inte kopplas.';
                    reload('klubbar');
                    return true;
                }
            });
        }).catch(() => { });
    }
    function relationDialog(c) {
        formModal({
            title: 'Klubbens roll: ' + c.clubName,
            html: `<label class="form-label" for="rRel">Roll på banan</label><select class="form-select" id="rRel">${relationOptions(c.relationType)}</select>${RELATION_HELP}`,
            onOk: async b => {
                const res = await post('ShootingRange/AddClubLink', { rangeId: RANGE_ID, clubId: c.clubId, relationType: val(b, '#rRel') });
                if (!res.success) return res.message || 'Det gick inte att spara.';
                reload('klubbar');
                return true;
            }
        });
    }
    async function removeLink(c) {
        if (!await ask('Ta bort ' + c.clubName + ' från banan?', 'Klubbens fasta tider på banan tas bort. Klubbens träningar och händelser ligger kvar.', 'Ta bort', true)) return;
        post('ShootingRange/RemoveClubLink', { linkId: c.linkId }).then(r => { if (!r.success) fail(r); else reload('klubbar'); }).catch(() => { });
    }
    function allocDialog(c, a) {
        const secs = D.sections || [];
        formModal({
            title: (a ? 'Ändra tiden' : 'Ny fast tid') + ' — ' + c.clubName,
            html: `<div class="row g-3">
                <div class="col-12"><label class="form-label" for="aDay">Veckodag</label><select class="form-select" id="aDay">${[1, 2, 3, 4, 5, 6, 7].map(d => `<option value="${d}"${a && a.dayOfWeek === d ? ' selected' : ''}>${DAYS[d]}</option>`).join('')}</select></div>
                <div class="col-6"><label class="form-label" for="aStart">Från</label><input class="form-control" id="aStart" placeholder="18:00" value="${esc(a ? a.startTime : '')}" autocomplete="off" inputmode="numeric"></div>
                <div class="col-6"><label class="form-label" for="aEnd">Till</label><input class="form-control" id="aEnd" placeholder="21:00" value="${esc(a ? a.endTime : '')}" autocomplete="off" inputmode="numeric"></div>
                ${secs.length ? `<div class="col-12"><label class="form-label" for="aSec">Bana / vall</label><select class="form-select" id="aSec"><option value="">Hela banan</option>${secs.map(s => `<option value="${s.id}"${a && a.rangeSectionId === s.id ? ' selected' : ''}>${esc(s.label)}</option>`).join('')}</select></div>` : ''}
                <div class="col-12"><label class="form-label" for="aNote">Notering (valfri)</label><input class="form-control" id="aNote" value="${esc(a ? a.note || '' : '')}" placeholder="Till exempel Juniorträning" autocomplete="off"></div></div>`,
            onOk: async b => {
                const t = s => /^\d{1,2}[:.]\d{2}$/.test(s) ? s.replace('.', ':').padStart(5, '0') : null;
                const start = t(val(b, '#aStart')), end = t(val(b, '#aEnd'));
                if (!start || !end) return 'Skriv tiderna som 18:00.';
                const sec = val(b, '#aSec');
                const res = await post('ShootingRange/AddAllocation', {
                    id: a ? a.id : 0, clubRangeLinkId: c.linkId, rangeSectionId: sec ? parseInt(sec, 10) : null,
                    dayOfWeek: parseInt(val(b, '#aDay'), 10), startTime: start, endTime: end, note: val(b, '#aNote')
                });
                if (!res.success) return res.message || 'Tiden kunde inte sparas.';
                reload('klubbar');
                return true;
            }
        });
    }
    async function deleteAlloc(c, a) {
        if (!await ask('Ta bort tiden?', `${c.clubName}: ${DAYS[a.dayOfWeek]} ${a.startTime}–${a.endTime} tas bort.`, 'Ta bort', true)) return;
        post('ShootingRange/DeleteAllocation', { id: a.id }).then(r => { if (!r.success) fail(r); else reload('klubbar'); }).catch(() => { });
    }

    // =====================================================================
    //  TILLSTÅND OCH DOKUMENT (förvaltare)
    // =====================================================================
    function expiry(dateStr) {
        if (!dateStr) return '<span class="rp-help">Inget slutdatum</span>';
        const days = Math.round((new Date(dateStr + 'T00:00:00') - new Date(new Date().toDateString())) / 86400000);
        if (days < 0) return `<span class="rp-state bad">Gick ut ${esc(dateStr)}</span>`;
        if (days <= 90) return `<span class="rp-state wait">Går ut ${esc(dateStr)}</span>`;
        return `<span class="rp-state ok">Gäller t.o.m. ${esc(dateStr)}</span>`;
    }
    function viewTillstand() {
        setHead('Tillstånd och dokument', 'Bara banans förvaltare ser den här delen. Banan påminner när ett tillstånd eller dokument snart går ut.');
        setActions([
            { label: 'Lägg till ett tillstånd…', icon: 'bi-plus-lg', fn: () => permitDialog(null) },
            { label: 'Ladda upp ett dokument…', icon: 'bi-cloud-upload', fn: docDialog }
        ]);
        get('ShootingRange/GetDossier', { rangeId: RANGE_ID }).then(d => {
            if (!d.success) { body().innerHTML = errBox(d.message || 'Uppgifterna kunde inte hämtas.'); return; }
            let h = '<div class="rp-card"><h2>Tillstånd</h2>';
            h += d.permits.length ? `<div class="table-responsive"><table class="table align-middle rp-table mb-0"><thead><tr><th>Tillstånd</th><th>Giltighet</th><th class="d-none d-md-table-cell">Skott per år</th><th></th></tr></thead><tbody>
                ${d.permits.map(p => `<tr><td><div class="fw-semibold">${esc(PERMIT[p.permitType] || p.permitType)}</div>
                    <div class="small rp-help">${esc([p.issuingAuthority, p.referenceNumber].filter(Boolean).join(' · '))}</div>
                    ${p.allowedWindows.length ? `<div class="small">Skjuttider: ${p.allowedWindows.map(w => esc(DAYS_SHORT[w.day] + ' ' + w.start + '–' + w.end)).join(', ')}</div>` : ''}
                    ${p.conditions ? `<div class="small rp-help">${esc(p.conditions)}</div>` : ''}</td>
                    <td>${expiry(p.expiryDate)}</td>
                    <td class="d-none d-md-table-cell">${p.maxShotsPerYear != null ? num(p.maxShotsPerYear) : '<span class="rp-help">Ingen gräns</span>'}</td>
                    <td class="text-end">${rowMenu([{ label: 'Ändra…', fn: () => permitDialog(p) }, { divider: true }, { label: 'Ta bort…', danger: true, fn: () => deletePermit(p) }])}</td></tr>`).join('')}
                </tbody></table></div>` : '<p class="rp-help mb-0">Inga tillstånd är inlagda. Välj <strong>Lägg till ett tillstånd</strong> under Åtgärder.</p>';
            h += '</div><div class="rp-card"><h2>Dokument</h2>';
            h += d.documents.length ? `<div class="table-responsive"><table class="table align-middle rp-table mb-0"><thead><tr><th>Dokument</th><th>Giltighet</th><th></th></tr></thead><tbody>
                ${d.documents.map(x => `<tr><td><a href="/umbraco/surface/ShootingRange/GetDocument?id=${x.id}" target="_blank" rel="noopener" class="fw-semibold">${esc(x.title)}</a>
                    <div class="small rp-help">${esc(DOC[x.docType] || x.docType)} · uppladdat ${esc(x.uploadedAt)}</div></td>
                    <td>${x.validUntil ? expiry(x.validUntil) : '<span class="rp-help">—</span>'}</td>
                    <td class="text-end">${rowMenu([{ label: 'Öppna', fn: () => window.open('/umbraco/surface/ShootingRange/GetDocument?id=' + x.id, '_blank') }, { divider: true }, { label: 'Ta bort…', danger: true, fn: () => deleteDoc(x) }])}</td></tr>`).join('')}
                </tbody></table></div>` : '<p class="rp-help mb-0">Inga dokument är uppladdade. Besiktningsprotokoll, skjutbaneinstruktion och miljöbeslut hör hemma här.</p>';
            body().innerHTML = h + '</div>';
        }).catch(() => body().innerHTML = errBox('Uppgifterna kunde inte hämtas.'));
    }
    function windowRow(w) {
        return `<div class="input-group input-group-sm mb-1 rp-win">
            <select class="form-select rp-w-day" aria-label="Veckodag">${[1, 2, 3, 4, 5, 6, 7].map(d => `<option value="${d}"${w && w.day === d ? ' selected' : ''}>${DAYS[d]}</option>`).join('')}</select>
            <input class="form-control rp-w-start" placeholder="09:00" aria-label="Från" value="${esc(w ? w.start : '')}">
            <input class="form-control rp-w-end" placeholder="21:00" aria-label="Till" value="${esc(w ? w.end : '')}">
            <button class="btn btn-outline-secondary rp-w-del" type="button" aria-label="Ta bort raden"><i class="bi bi-x-lg"></i></button></div>`;
    }
    function permitDialog(p) {
        const v = k => esc(p && p[k] != null ? p[k] : '');
        formModal({
            title: p ? 'Ändra tillståndet' : 'Nytt tillstånd', wide: true,
            html: `<div class="row g-3">
                <div class="col-md-6"><label class="form-label" for="pType">Slag</label><select class="form-select" id="pType">${Object.keys(PERMIT).map(k => `<option value="${k}"${(p ? p.permitType : 'PoliceTillstand') === k ? ' selected' : ''}>${PERMIT[k]}</option>`).join('')}</select></div>
                <div class="col-md-6"><label class="form-label" for="pAuth">Utfärdat av</label><input class="form-control" id="pAuth" value="${v('issuingAuthority')}" placeholder="Polismyndigheten region Syd" autocomplete="off"></div>
                <div class="col-md-4"><label class="form-label" for="pRef">Diarienummer</label><input class="form-control" id="pRef" value="${v('referenceNumber')}" autocomplete="off"></div>
                <div class="col-6 col-md-4"><label class="form-label" for="pIssued">Utfärdat</label><input class="form-control" id="pIssued" value="${v('issuedDate')}" autocomplete="off"></div>
                <div class="col-6 col-md-4"><label class="form-label" for="pExpiry">Gäller t.o.m.</label><input class="form-control" id="pExpiry" value="${v('expiryDate')}" autocomplete="off"></div>
                <div class="col-md-6"><label class="form-label" for="pMax">Högst antal skott per år</label><input type="number" class="form-control" id="pMax" value="${v('maxShotsPerYear')}" placeholder="50000"></div>
                <div class="col-12"><span class="form-label d-block">Tillåtna skjuttider</span><div id="pWins">${(p && p.allowedWindows || []).map(windowRow).join('')}</div>
                    <button type="button" class="btn btn-sm btn-outline-secondary mt-1" id="pAddWin"><i class="bi bi-plus-lg me-1"></i>Lägg till en skjuttid</button>
                    <div class="form-text">Klubbarnas fasta tider jämförs med skjuttiderna, och en tid utanför dem syns under Klubbar och tider.</div></div>
                <div class="col-12"><label class="form-label" for="pCond">Villkor och övrigt</label><textarea class="form-control" id="pCond" rows="2">${v('conditions')}</textarea></div></div>`,
            onOpen: b => {
                datePick(b.querySelector('#pIssued')); datePick(b.querySelector('#pExpiry'));
                b.querySelector('#pAddWin').addEventListener('click', () => b.querySelector('#pWins').insertAdjacentHTML('beforeend', windowRow(null)));
                b.querySelector('#pWins').addEventListener('click', e => { const x = e.target.closest('.rp-w-del'); if (x) x.closest('.rp-win').remove(); });
            },
            onOk: async b => {
                const wins = Array.from(b.querySelectorAll('.rp-win')).map(r => ({
                    day: parseInt(r.querySelector('.rp-w-day').value, 10), start: r.querySelector('.rp-w-start').value.trim(), end: r.querySelector('.rp-w-end').value.trim()
                })).filter(w => w.start && w.end);
                const res = await post('ShootingRange/SavePermit', {
                    id: p ? p.id : 0, rangeId: RANGE_ID, permitType: val(b, '#pType'), issuingAuthority: val(b, '#pAuth'),
                    referenceNumber: val(b, '#pRef'), issuedDate: val(b, '#pIssued'), expiryDate: val(b, '#pExpiry'),
                    maxShotsPerYear: intOrNull(val(b, '#pMax')), allowedWindows: wins, conditions: val(b, '#pCond')
                });
                if (!res.success) return res.message || 'Tillståndet kunde inte sparas.';
                reload('tillstand');
                return true;
            }
        });
    }
    async function deletePermit(p) {
        if (!await ask('Ta bort tillståndet?', `${PERMIT[p.permitType] || p.permitType}${p.referenceNumber ? ' (' + p.referenceNumber + ')' : ''} tas bort.`, 'Ta bort', true)) return;
        post('ShootingRange/DeletePermit', { id: p.id }).then(r => { if (!r.success) fail(r); else reload('tillstand'); }).catch(() => { });
    }
    function docDialog() {
        formModal({
            title: 'Ladda upp ett dokument',
            html: `<div class="row g-3">
                <div class="col-12"><label class="form-label" for="dType">Slag</label><select class="form-select" id="dType">${Object.keys(DOC).map(k => `<option value="${k}">${DOC[k]}</option>`).join('')}</select></div>
                <div class="col-12"><label class="form-label" for="dTitle">Rubrik</label><input class="form-control" id="dTitle" placeholder="Utan rubrik används filens namn" autocomplete="off"></div>
                <div class="col-6"><label class="form-label" for="dIssued">Utfärdat</label><input class="form-control" id="dIssued" autocomplete="off"></div>
                <div class="col-6"><label class="form-label" for="dValid">Gäller t.o.m.</label><input class="form-control" id="dValid" autocomplete="off"></div>
                <div class="col-12"><label class="form-label" for="dFile">Fil (pdf, bild eller Word, högst 25 MB)</label><input type="file" class="form-control" id="dFile" accept=".pdf,.jpg,.jpeg,.png,.webp,.doc,.docx"></div></div>`,
            okLabel: 'Ladda upp',
            onOpen: b => { datePick(b.querySelector('#dIssued')); datePick(b.querySelector('#dValid')); },
            onOk: async b => {
                const f = b.querySelector('#dFile').files[0];
                if (!f) return 'Välj en fil.';
                const fd = new FormData();
                fd.append('file', f); fd.append('__RequestVerificationToken', token());
                fd.append('rangeId', RANGE_ID); fd.append('docType', val(b, '#dType')); fd.append('title', val(b, '#dTitle'));
                fd.append('issuedDate', val(b, '#dIssued')); fd.append('validUntil', val(b, '#dValid'));
                const res = await request('/umbraco/surface/ShootingRange/UploadDocument', { method: 'POST', headers: { 'RequestVerificationToken': token() }, body: fd });
                if (!res.success) return res.message || 'Dokumentet kunde inte laddas upp.';
                reload('tillstand');
                return true;
            }
        });
    }
    async function deleteDoc(x) {
        if (!await ask('Ta bort dokumentet?', `${x.title} tas bort, och filen raderas.`, 'Ta bort', true)) return;
        post('ShootingRange/DeleteDocument', { id: x.id }).then(r => { if (!r.success) fail(r); else reload('tillstand'); }).catch(() => { });
    }

    // =====================================================================
    //  AKTIVITET (förvaltare)
    // =====================================================================
    let actYear = null;
    function viewAktivitet() {
        setHead('Aktivitet', 'Skott och skjutdagar på banan under året — underlaget till miljöförvaltningen. Tävlingar, träningar och incheckningar räknas in av sig själva.');
        setActions([
            { label: 'Registrera aktivitet…', icon: 'bi-plus-lg', fn: activityDialog },
            { label: 'Ladda ner året som CSV', icon: 'bi-download', fn: () => { location.href = `/umbraco/surface/ShootingRange/ExportActivity?rangeId=${RANGE_ID}&year=${actYear || new Date().getFullYear()}`; } }
        ]);
        get('ShootingRange/GetActivityReport', actYear ? { rangeId: RANGE_ID, year: actYear } : { rangeId: RANGE_ID }).then(d => {
            if (!d.success) { body().innerHTML = errBox(d.message || 'Aktiviteten kunde inte hämtas.'); return; }
            actYear = d.year;
            let cap;
            if (d.cap) {
                const pct = Math.min(100, Math.round(d.total / d.cap * 100));
                cap = `<div class="mb-1">${num(d.total)} av ${num(d.cap)} skott (${pct} %)</div>
                    <div class="progress mb-3" style="height:10px" role="progressbar" aria-valuenow="${pct}" aria-valuemin="0" aria-valuemax="100"><div class="progress-bar ${pct >= 100 ? 'bg-danger' : pct >= 80 ? 'bg-warning' : 'bg-success'}" style="width:${pct}%"></div></div>`;
            } else cap = `<div class="mb-3"><span class="fs-5 fw-semibold">${num(d.total)}</span> skott <span class="rp-help small">— ingen maxgräns är angiven i tillstånden</span></div>`;
            const src = Object.keys(d.bySource || {}).length
                ? Object.entries(d.bySource).map(([k, v]) => `<span class="badge bg-secondary-subtle text-secondary-emphasis me-1">${esc(SOURCE[k] || k)}: ${num(v)}</span>`).join('') : '';
            const maxH = Math.max(1, ...d.byHour);
            const hours = d.byHour.map((v, hr) => `<div title="kl ${hr}: ${v} skott" style="flex:1;display:flex;flex-direction:column;justify-content:flex-end;align-items:center">
                <div style="width:70%;background:var(--bs-info);height:${Math.round(v / maxH * 48)}px;border-radius:2px"></div>
                <small class="rp-help" style="font-size:.6rem">${hr % 6 === 0 ? hr : '&nbsp;'}</small></div>`).join('');
            let h = `<div class="rp-card"><div class="rp-cardh"><h2>År ${esc(d.year)}</h2>
                <select class="form-select form-select-sm" style="width:auto" id="rpActYear" aria-label="År">${d.years.map(y => `<option value="${y}"${y === d.year ? ' selected' : ''}>${y}</option>`).join('')}</select></div>
                ${cap}<div class="mb-2">${src}</div>
                <div class="small rp-help mb-1">${num(d.days)} skjutdagar · skott per timme på dygnet:</div><div class="rp-hours">${hours}</div></div>`;
            h += `<div class="rp-card"><h2>Registrerade pass</h2>`;
            h += d.sessions.length ? `<div class="table-responsive"><table class="table table-sm align-middle rp-table mb-0"><thead><tr><th>Datum</th><th>Tid</th><th>Skott</th><th>Källa</th><th></th></tr></thead><tbody>
                ${d.sessions.map(s => `<tr><td class="text-nowrap">${esc(s.date)}</td><td class="small text-nowrap">${esc((s.startTime || '') + (s.endTime ? '–' + s.endTime : ''))}</td>
                    <td>${num(s.shotCount)}${s.shooterCount > 1 ? ` <span class="rp-help small">(${s.shooterCount} skyttar)</span>` : ''}</td>
                    <td><span class="small">${esc(SOURCE[s.source] || s.source)}</span>${s.note ? `<div class="small rp-help">${esc(s.note)}</div>` : ''}</td>
                    <td class="text-end">${rowMenu([{ label: 'Ta bort…', danger: true, fn: () => deleteActivity(s) }])}</td></tr>`).join('')}
                </tbody></table></div>` : '<p class="rp-help mb-0">Inga pass är registrerade för året.</p>';
            body().innerHTML = h + '</div>';
            $('rpActYear').addEventListener('change', e => { actYear = parseInt(e.target.value, 10); route(); });
        }).catch(() => body().innerHTML = errBox('Aktiviteten kunde inte hämtas.'));
    }
    function activityDialog() {
        formModal({
            title: 'Registrera aktivitet',
            html: `<p class="rp-help">För det som inte räknas in av sig självt: gästskyttar, andra klubbar, evenemang.</p><div class="row g-3">
                <div class="col-12 col-sm-6"><label class="form-label" for="acDate">Datum</label><input class="form-control" id="acDate" value="${ymd(new Date())}" autocomplete="off"></div>
                <div class="col-6 col-sm-3"><label class="form-label" for="acStart">Från</label><input class="form-control" id="acStart" placeholder="09:00" autocomplete="off"></div>
                <div class="col-6 col-sm-3"><label class="form-label" for="acEnd">Till</label><input class="form-control" id="acEnd" placeholder="12:00" autocomplete="off"></div>
                <div class="col-6"><label class="form-label" for="acShots">Antal skott</label><input type="number" min="0" class="form-control" id="acShots"></div>
                <div class="col-6"><label class="form-label" for="acShooters">Antal skyttar</label><input type="number" min="1" class="form-control" id="acShooters" value="1"></div>
                <div class="col-12"><label class="form-label" for="acNote">Notering (valfri)</label><input class="form-control" id="acNote" placeholder="Till exempel gästskytte" autocomplete="off"></div></div>`,
            onOpen: b => datePick(b.querySelector('#acDate')),
            onOk: async b => {
                const shots = intOrNull(val(b, '#acShots'));
                if (shots == null || shots < 0) return 'Skriv antal skott.';
                const res = await post('ShootingRange/AddManualActivity', {
                    rangeId: RANGE_ID, date: val(b, '#acDate'), startTime: val(b, '#acStart'), endTime: val(b, '#acEnd'),
                    shotCount: shots, shooterCount: intOrNull(val(b, '#acShooters')) || 1, note: val(b, '#acNote')
                });
                if (!res.success) return res.message || 'Aktiviteten kunde inte sparas.';
                route();
                return true;
            }
        });
    }
    async function deleteActivity(s) {
        if (!await ask('Ta bort passet?', `${s.date}: ${s.shotCount} skott tas bort ur årets aktivitet.`, 'Ta bort', true)) return;
        post('ShootingRange/DeleteActivity', { id: s.id }).then(r => { if (!r.success) fail(r); else route(); }).catch(() => { });
    }

    // =====================================================================
    //  INCHECKNING (förvaltare)
    // =====================================================================
    function checkinUrl() { return location.origin + '/skjutbana/incheckning?r=' + RANGE_ID; }
    function viewIncheckning() {
        setHead('Incheckning', 'Sätt upp skylten vid banan. Skyttarna skannar QR-koden, checkar in när de börjar och ut med antal skott när de är klara — skotten räknas in i Aktivitet.');
        setActions([{ label: 'Skriv ut skylten…', icon: 'bi-printer', fn: printSign }]);
        const url = checkinUrl();
        const qr = '/umbraco/surface/Faltskytte/GenerateQrCode?url=' + encodeURIComponent(url);
        body().innerHTML = `<div class="rp-card"><div class="row g-4 align-items-center">
            <div class="col-sm-auto text-center"><img src="${qr}" alt="QR-kod till incheckningen" style="width:180px;height:180px" class="border rounded p-1 bg-white"></div>
            <div class="col"><h2>Skylten vid banan</h2><p class="mb-2">QR-koden leder till incheckningen för ${esc(D.range.name)}.</p>
                <div class="d-flex flex-wrap gap-2">${btn('Skriv ut skylten', printSign, 'btn-primary', 'bi-printer')}
                <a class="btn btn-outline-secondary" href="${esc(url)}" target="_blank" rel="noopener">Öppna incheckningen</a></div></div></div></div>
            <div class="rp-card"><h2>Glömd utcheckning</h2>
                <p class="rp-help">Den som glömmer att checka ut checkas ut av sig själv vid dygnets slut. Skriv hur många skott det ska räknas som.</p>
                <div class="d-flex flex-wrap align-items-end gap-2">
                    <div><label class="form-label" for="rpDefShots">Antal skott</label>
                    <input type="number" min="0" class="form-control" id="rpDefShots" style="max-width:10rem" value="${esc(D.range.defaultShotCount ?? '')}" placeholder="0"></div>
                    ${btn('Spara', saveDefaultShots, 'btn-outline-primary')}</div></div>`;
    }
    function saveDefaultShots() {
        const v = parseInt($('rpDefShots').value, 10);
        post('ShootingRange/SetRangeDefaultShotCount', { rangeId: RANGE_ID, defaultShotCount: !isNaN(v) && v > 0 ? v : null }).then(r => {
            if (!r.success) { fail(r); return; }
            D.range.defaultShotCount = r.defaultShotCount;
            showMsg('Sparat.', 'success');
        }).catch(() => { });
    }
    function printSign() {
        const r = D.range, url = checkinUrl();
        const qr = '/umbraco/surface/Faltskytte/GenerateQrCode?url=' + encodeURIComponent(url);
        const w = window.open('', '_blank');
        if (!w) { showMsg('Webbläsaren stoppade utskriftsfönstret. Tillåt popup-fönster för pistol.nu och försök igen.', 'warning'); return; }
        w.document.write(`<!doctype html><html lang="sv"><head><meta charset="utf-8"><title>Incheckning – ${esc(r.name)}<\/title>
            <style>@page { size: A4 portrait; margin: 18mm; } body { font-family: Arial, Helvetica, sans-serif; text-align: center; color: #111; }
            .name { font-size: 32px; font-weight: 700; margin: 0 0 4px; } .sub { font-size: 18px; color: #555; margin: 0 0 28px; }
            .lead { font-size: 26px; font-weight: 700; margin: 22px 0 12px; } .qr { width: 340px; height: 340px; }
            .steps { font-size: 18px; line-height: 1.5; margin: 14px auto 0; max-width: 460px; text-align: left; }
            .url { font-size: 13px; color: #666; margin-top: 24px; word-break: break-all; } .foot { font-size: 13px; color: #999; margin-top: 30px; }<\/style><\/head><body>
            <div class="name">${esc(r.name)}<\/div>${r.city ? `<div class="sub">${esc(r.city)}<\/div>` : ''}
            <div class="lead">Skanna för att checka in<\/div><img class="qr" src="${qr}" alt="QR">
            <ol class="steps"><li>Skanna QR-koden med mobilens kamera.<\/li><li>Logga in på pistol.nu om du inte redan är inloggad.<\/li>
            <li>Tryck <strong>Checka in<\/strong> när du börjar – och <strong>Checka ut<\/strong> med antal skott när du är klar.<\/li><\/ol>
            <div class="url">${esc(url)}<\/div><div class="foot">pistol.nu · Skjutbanor<\/div><\/body><\/html>`);
        w.document.close();
        const img = w.document.querySelector('img.qr');
        const goPrint = () => { try { w.focus(); w.print(); } catch (e) { } };
        if (img && !img.complete) { img.onload = goPrint; img.onerror = goPrint; } else setTimeout(goPrint, 300);
    }

    // =====================================================================
    //  FÖRVALTARE (förvaltare)
    // =====================================================================
    function viewForvaltare() {
        setHead('Förvaltare', 'Förvaltarna får ändra banans uppgifter, klubbarnas tider, tillstånd och dokument — oavsett vilken klubb de tillhör.');
        setActions([{ label: 'Lägg till en förvaltare…', icon: 'bi-person-plus', fn: stewardDialog }]);
        const sw = D.stewards || [];
        const last = sw.length <= 1;
        body().innerHTML = `<div class="rp-card">${sw.length ? `<div class="table-responsive"><table class="table align-middle rp-table mb-0"><thead><tr><th>Namn</th><th></th></tr></thead><tbody>
            ${sw.map(x => `<tr><td>${esc(x.memberName)}</td><td class="text-end">${last ? '<span class="small rp-help">Banans enda förvaltare</span>'
                : rowMenu([{ label: 'Ta bort som förvaltare…', danger: true, fn: () => removeSteward(x) }])}</td></tr>`).join('')}
            </tbody></table></div>` : '<p class="rp-help mb-0">Banan har ingen förvaltare.</p>'}</div>`;
    }
    function stewardDialog() {
        let timer = null, chosen = 0;
        formModal({
            title: 'Lägg till en förvaltare',
            html: `<label class="form-label" for="stQ">Sök medlem på namn eller e-post</label>
                <input class="form-control" id="stQ" autocomplete="off"><div class="list-group mt-2" id="stRes"></div>`,
            onOpen: b => {
                const q = b.querySelector('#stQ'), res = b.querySelector('#stRes');
                q.focus();
                q.addEventListener('input', () => {
                    clearTimeout(timer); chosen = 0;
                    timer = setTimeout(() => {
                        if (q.value.trim().length < 2) { res.innerHTML = ''; return; }
                        get('ShootingRange/SearchMembers', { query: q.value.trim() }).then(d => {
                            const list = (d.members || []);
                            res.innerHTML = list.length ? list.map(m => `<button type="button" class="list-group-item list-group-item-action" data-mid="${m.memberId}">${esc(m.memberName)}</button>`).join('')
                                : '<div class="small rp-help">Ingen medlem hittades.</div>';
                        }).catch(() => { });
                    }, 250);
                });
                res.addEventListener('click', e => {
                    const x = e.target.closest('[data-mid]'); if (!x) return;
                    chosen = parseInt(x.dataset.mid, 10);
                    res.querySelectorAll('.active').forEach(a => a.classList.remove('active'));
                    x.classList.add('active');
                });
            },
            okLabel: 'Lägg till',
            onOk: async () => {
                if (!chosen) return 'Välj en medlem i listan.';
                const r = await post('ShootingRange/AddSteward', { rangeId: RANGE_ID, memberId: chosen });
                if (!r.success) return r.message || 'Förvaltaren kunde inte läggas till.';
                reload('forvaltare');
                return true;
            }
        });
    }
    async function removeSteward(x) {
        if (!await ask('Ta bort ' + x.memberName + ' som förvaltare?', 'Personen kan inte längre ändra banans uppgifter, tider, tillstånd eller dokument.', 'Ta bort', true)) return;
        post('ShootingRange/RemoveSteward', { rangeId: RANGE_ID, memberId: x.memberId }).then(r => { if (!r.success) fail(r); else reload('forvaltare'); }).catch(() => { });
    }

    const VIEWS = {
        oversikt: viewOversikt, kalender: viewKalender, banor: viewBanor, klubbar: viewKlubbar,
        tillstand: viewTillstand, aktivitet: viewAktivitet, incheckning: viewIncheckning, forvaltare: viewForvaltare
    };

    // Glömda incheckningar stängs vid sidladdning (samma svep som katalogen och incheckningen gör).
    try {
        const k = 'hpsk_sweep_checkins', last = parseInt(localStorage.getItem(k) || '0', 10);
        if (Date.now() - last >= 5 * 60 * 1000) { localStorage.setItem(k, String(Date.now())); fetch('/umbraco/surface/ShootingRange/AutoCloseStaleCheckIns').catch(() => { }); }
    } catch (e) { }

    load().then(ok => { if (ok) { route(); refreshTodo(); } }).catch(() => body().innerHTML = errBox('Skjutbanan kunde inte hämtas.'));
})();
