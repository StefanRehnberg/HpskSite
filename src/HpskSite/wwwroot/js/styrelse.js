/*
 * /styrelse — styrelsearbete i Ekonomins skal (2026-10-08).
 *
 * Delarna (rälsen): Översikt, Möten (+ ett möte, mötesmallar), Ärenden, Motioner, Årshjul,
 * Styrelsen och mandat, Valberedning. Delen står i adressen (#vy=…&id=…), så bakåtknappen,
 * en omladdning och länkar i mejl landar rätt. Blå Åtgärder bär handlingar för delen man står på.
 *
 * Regler som gäller hela filen:
 *  - Ingen confirm()/alert()/prompt(). Frågor går genom svConfirm, formulär genom formModal,
 *    besked genom showMsg. En blockerande dialog har tidigare rapporterats som att "sidan frös".
 *  - Ingen användartext i ett onclick-attribut. Knappar bär ett id i data-attribut och läses av
 *    EN delegerad lyssnare (data-sv), eller får en riktig lyssnare efter att de ritats.
 *  - Ett anrop som inte gick fram får aldrig vara tyst (svRequest).
 */
(function () {
    'use strict';
    const C = window.SV_CONFIG || {};
    const OWNER_TYPE = C.ownerType, OWNER_ID = C.ownerId;
    const CAN_MANAGE = !!C.canManage, CAN_PLACE = !!C.canPlace, VALB_ONLY = !!C.valbOnly;
    const IS_CLUB = OWNER_TYPE === 0;
    const VALB_KEYS = ['Valberedning', 'ValberedningSammankallande'];
    const KRETS_UPPDRAG = C.kretsUppdrag || [];
    const POST_KEYS = [['Ordforande', 'Ordförande'], ['ViceOrdforande', 'Vice ordförande'], ['Sekreterare', 'Sekreterare'], ['Kassor', 'Kassör'], ['Ledamot', 'Ledamot'], ['Suppleant', 'Suppleant'], ['Revisor', 'Revisor'], ['Valberedning', 'Valberedning']];
    const NOM_STATUSES = ['Föreslagen', 'Tillfrågad', 'Tackat ja', 'Tackat nej'];
    const MEETING_TYPES = [['Styrelsemote', 'Ordinarie styrelsemöte'], ['Konstituerande', 'Konstituerande möte (efter årsmötet)'], ['Arsmote', 'Årsmöte'], ['ExtraArsmote', 'Extra årsmöte'], ['Custom', 'Eget möte']];

    const $ = id => document.getElementById(id);
    const body = () => $('svBody');

    // =====================================================================
    //  Anrop
    // =====================================================================
    function token() { return document.querySelector('input[name="__RequestVerificationToken"]')?.value || ''; }
    // ⚠️⚠️ Ett anrop som inte gick fram får ALDRIG vara tyst: ett felsvar (400 när säkerhetstoken
    // inte stämmer, 500, en inloggningssida i stället för JSON) visas i klartext och löftet avvisas.
    function svRequest(url, opts) {
        return fetch(url, opts).then(
            r => r.text().then(text => {
                let data = null;
                try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
                if (r.ok && data !== null) return data;
                throw svRequestError(r.status, data);
            }),
            () => { throw svRequestError(0, null); }
        );
    }
    function svRequestError(status, data) {
        const info = window.hpskRequestFailureMessage ? window.hpskRequestFailureMessage(status) : { msg: 'Anropet misslyckades.', reload: false };
        const msg = (data && data.message) ? data.message : info.msg;
        if (window.hpskReportRequestFailure) window.hpskReportRequestFailure(msg, info.reload);
        const err = new Error(msg); err.status = status; err.hpskShown = true;
        return err;
    }
    function post(ctrl, action, params) {
        return svRequest('/umbraco/surface/' + ctrl + '/' + action, {
            method: 'POST',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'RequestVerificationToken': token() },
            body: new URLSearchParams(params).toString()
        });
    }
    function get(ctrl, action, qs) {
        return svRequest('/umbraco/surface/' + ctrl + '/' + action + '?' + new URLSearchParams(qs || {}).toString());
    }
    function esc(s) {
        if (s === null || s === undefined) return '';
        const m = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' };
        return s.toString().replace(/[&<>"']/g, c => m[c]);
    }
    function flash(el) { if (!el) return; el.classList.add('show'); setTimeout(() => el.classList.remove('show'), 1800); }
    function spinner() { return '<div class="text-center text-muted py-4"><div class="spinner-border"></div><div class="mt-2">Hämtar…</div></div>'; }
    function errBox(msg) { return `<div class="sv-card"><p class="text-danger mb-0">${esc(msg)}</p></div>`; }
    const OWN = () => ({ ownerType: OWNER_TYPE, ownerId: OWNER_ID });
    function svDate(s) {
        // "2026-10-14 18:30" → "tis 14 okt 18:30"; "2026-10-14" → "14 okt 2026"
        if (!s) return '';
        const d = new Date(s.replace(' ', 'T'));
        if (isNaN(d)) return s;
        const hasTime = s.length > 10;
        const opts = hasTime ? { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }
            : { day: 'numeric', month: 'short', year: 'numeric' };
        return d.toLocaleString('sv-SE', opts);
    }

    // =====================================================================
    //  Skalets delar: rubrik, Åtgärder, besked, dialoger
    // =====================================================================
    function setHead(title, sub, crumb) {
        $('svTitle').textContent = title || '';
        $('svSub').innerHTML = sub || '';
        const c = $('svCrumb');
        if (crumb) { c.innerHTML = crumb; c.classList.remove('d-none'); } else { c.innerHTML = ''; c.classList.add('d-none'); }
        document.title = (title ? title + ' – ' : '') + 'Styrelsearbete';
    }
    let actionFns = [];
    // items: [{ header }, { divider }, { label, icon, fn, danger }]
    function setActions(items) {
        const wrap = $('svActions'), list = $('svActionsList');
        actionFns = [];
        const real = (items || []).filter(Boolean);
        if (!real.some(i => i.fn)) { wrap.classList.add('d-none'); list.innerHTML = ''; return; }
        list.innerHTML = real.map(i => {
            if (i.header) return `<li><h6 class="dropdown-header">${esc(i.header)}</h6></li>`;
            if (i.divider) return '<li><hr class="dropdown-divider"></li>';
            actionFns.push(i.fn);
            return `<li><button type="button" class="dropdown-item${i.danger ? ' text-danger' : ''}" data-sv-action="${actionFns.length - 1}">`
                + (i.icon ? `<i class="bi ${i.icon} me-2${i.danger ? '' : ' text-secondary'}"></i>` : '') + `${esc(i.label)}</button></li>`;
        }).join('');
        wrap.classList.remove('d-none');
    }
    $('svActionsList')?.addEventListener('click', e => {
        const b = e.target.closest('[data-sv-action]'); if (!b) return;
        const fn = actionFns[parseInt(b.dataset.svAction, 10)]; if (fn) fn();
    });

    let msgKeep = false;
    // Besked högst upp i innehållet. keep=true överlever nästa omritning (t.ex. efter en sparning
    // som ritar om delen) — annars försvinner beskedet i samma ögonblick det visas.
    function showMsg(text, kind, keep) {
        const el = $('svMsg'); if (!el) return;
        el.innerHTML = text ? `<div class="alert alert-${kind || 'success'} alert-dismissible fade show" role="alert">${text}
            <button type="button" class="btn-close" data-bs-dismiss="alert" aria-label="Stäng"></button></div>` : '';
        msgKeep = !!keep;
        if (text) el.scrollIntoView({ block: 'nearest' });
    }
    function failMsg(r, fallback) { showMsg(esc((r && r.message) || fallback || 'Det gick inte.'), 'danger'); }

    function svConfirm(title, text, okLabel, danger) {
        return new Promise(resolve => {
            const m = $('svConfirmModal');
            $('svConfirmTitle').textContent = title;
            $('svConfirmText').textContent = text || '';
            const ok = $('svConfirmOk');
            ok.textContent = okLabel || 'OK';
            ok.className = 'btn ' + (danger ? 'btn-danger' : 'btn-primary');
            let answered = false;
            const modal = bootstrap.Modal.getOrCreateInstance(m);
            const onOk = () => { answered = true; modal.hide(); };
            ok.onclick = onOk;
            m.addEventListener('hidden.bs.modal', function h() { m.removeEventListener('hidden.bs.modal', h); resolve(answered); });
            modal.show();
        });
    }

    // Ett formulär i en dialog. onOk(bodyEl) returnerar ett löfte: true = stäng, en sträng = visa felet.
    function formModal({ title, html, okLabel, okClass, onOpen, onOk, wide }) {
        const m = $('svFormModal');
        $('svFormTitle').textContent = title;
        $('svFormBody').innerHTML = html;
        const err = $('svFormErr'); err.classList.add('d-none'); err.textContent = '';
        const ok = $('svFormOk');
        ok.textContent = okLabel || 'Spara';
        ok.className = 'btn ' + (okClass || 'btn-primary');
        ok.classList.toggle('d-none', !onOk);
        ok.disabled = false;
        m.querySelector('.modal-dialog').classList.toggle('modal-lg', wide !== false);
        const modal = bootstrap.Modal.getOrCreateInstance(m);
        ok.onclick = async () => {
            ok.disabled = true;
            try {
                const res = await onOk($('svFormBody'));
                if (res === true) { modal.hide(); return; }
                if (typeof res === 'string') { err.textContent = res; err.classList.remove('d-none'); }
            } catch (e) {
                err.textContent = (e && e.message) || 'Det gick inte att spara.'; err.classList.remove('d-none');
            } finally { ok.disabled = false; }
        };
        modal.show();
        if (onOpen) m.addEventListener('shown.bs.modal', function h() { m.removeEventListener('shown.bs.modal', h); onOpen($('svFormBody')); });
        return modal;
    }
    function closeForm() { const m = $('svFormModal'); bootstrap.Modal.getOrCreateInstance(m).hide(); }

    function datePick(el, withTime, opts) {
        if (!el || typeof flatpickr === 'undefined') return null;
        return flatpickr(el, Object.assign({ locale: 'sv', enableTime: !!withTime, time_24hr: true, dateFormat: withTime ? 'Y-m-d H:i' : 'Y-m-d', allowInput: true }, opts || {}));
    }

    // Gemensam radmeny (grå Åtgärder per rad), byggd ur samma form som sidans meny.
    let rowFns = [];
    function rowMenu(items) {
        const real = (items || []).filter(Boolean);
        if (!real.some(i => i.fn)) return '';
        const html = real.map(i => {
            if (i.divider) return '<li><hr class="dropdown-divider"></li>';
            rowFns.push(i.fn);
            return `<li><button type="button" class="dropdown-item${i.danger ? ' text-danger' : ''}" data-sv-row="${rowFns.length - 1}">${esc(i.label)}</button></li>`;
        }).join('');
        return `<div class="dropdown sv-noprint d-inline-block">
            <button class="btn btn-sm btn-outline-secondary dropdown-toggle" type="button" data-bs-toggle="dropdown"
                    data-bs-popper-config='{"strategy":"fixed"}' aria-expanded="false">Åtgärder</button>
            <ul class="dropdown-menu dropdown-menu-end">${html}</ul></div>`;
    }
    document.addEventListener('click', e => {
        const b = e.target.closest('[data-sv-row]'); if (!b) return;
        const fn = rowFns[parseInt(b.dataset.svRow, 10)]; if (fn) fn();
    });
    // Knappar i innehållet: data-sv-go="vy" [data-id] [data-fas] — navigering utan onclick-text.
    document.addEventListener('click', e => {
        const b = e.target.closest('[data-sv-go]'); if (!b) return;
        e.preventDefault();
        go(b.dataset.svGo, b.dataset.id ? parseInt(b.dataset.id, 10) : 0, b.dataset.fas || '');
    });
    // "Anmäl ett ärende" står SYNLIG på Översikt och Ärenden (2026-10-08, efter Stefans test: i
    // Åtgärder-menyn hittade ingen ledamot den). Samma dialog som menyvalet.
    document.addEventListener('click', e => {
        const b = e.target.closest('[data-sv-new-issue]'); if (!b) return;
        e.preventDefault();
        issueDialog(null);
    });
    /** Ledamotens rad överst på Översikt och Ärenden — det enda en vanlig ledamot ofta vill göra här. */
    function newIssueBox(n) {
        if (VALB_ONLY) return '';
        return `<div class="sv-card d-flex flex-wrap align-items-center gap-3 justify-content-between">
            <div><h2 class="mb-1" style="font-size:1.2rem;font-weight:700;">Vill du ta upp något på ett styrelsemöte?</h2>
            <div class="sv-help">Anmäl ärendet här. Sekreteraren eller ordföranden lägger in det på dagordningen, och du får besked.${n ? ` Du har ${n} ärende${n === 1 ? '' : 'n'} som väntar.` : ''}</div></div>
            <button type="button" class="btn btn-primary" data-sv-new-issue><i class="bi bi-inbox me-1" aria-hidden="true"></i>Anmäl ett ärende</button></div>`;
    }

    function stateClass(state) {
        return ({ Waiting: 'wait', Placed: 'blue', Handled: 'ok', Withdrawn: 'off', Rejected: 'off',
                  Received: 'wait', OpinionReady: 'blue', OnAgenda: 'blue', Decided: 'ok',
                  Planerat: 'plan', 'Genomfört': 'blue', VantarJustering: 'wait', Justerat: 'ok' })[state] || 'plan';
    }
    function pill(state, label) { return `<span class="sv-state ${stateClass(state)}">${esc(label)}</span>`; }
    function meetingStatusLabel(s) { return ({ VantarJustering: 'Väntar på justering' })[s] || s; }

    // =====================================================================
    //  Rälsen och vägvalet
    // =====================================================================
    const RAIL = VALB_ONLY ? [['valberedning', 'Valberedning']]
        : [['oversikt', 'Översikt'], ['moten', 'Möten'], ['arenden', 'Ärenden'], ['motioner', 'Motioner'],
           ['arshjul', 'Årshjul'], ['styrelsen', 'Styrelsen och mandat'], ['valberedning', 'Valberedning']];
    const DEFAULT_VIEW = VALB_ONLY ? 'valberedning' : 'oversikt';
    const counts = { arenden: 0, motioner: 0 };

    function renderRail(active) {
        const parent = { mote: 'moten', mallar: 'moten' }[active] || active;
        $('svRailLinks').innerHTML = RAIL.map(([k, label]) => {
            const n = counts[k] ? `<span class="sv-count" title="Väntar på dig">${counts[k]}</span>` : '';
            return `<a href="#vy=${k}" class="sv-nav${k === parent ? ' on' : ''}" data-rail="${k}">${esc(label)}${n}</a>`;
        }).join('');
    }
    function refreshCounts() {
        if (VALB_ONLY) return Promise.resolve();
        return get('BoardWork', 'GetOverview', OWN()).then(r => {
            if (!r.success) return;
            counts.arenden = r.canPlace ? r.issuesToPlace : 0;
            counts.motioner = r.canPlace ? r.motionsWithoutOpinion : 0;
            renderRail(parseHash().view);
        }).catch(() => { });
    }

    function parseHash() {
        const p = new URLSearchParams(location.hash.replace(/^#/, ''));
        let view = p.get('vy') || '';
        const LEGACY = { styrelse: 'styrelsen', moten: 'moten', arshjul: 'arshjul', valberedning: 'valberedning' };
        if (!view && C.startTab && LEGACY[C.startTab]) view = LEGACY[C.startTab];
        if (!VIEWS[view] || (VALB_ONLY && view !== 'valberedning')) view = DEFAULT_VIEW;
        return { view, id: parseInt(p.get('id') || '0', 10) || 0, fas: p.get('fas') || '' };
    }
    function go(view, id, fas) {
        const h = '#vy=' + view + (id ? '&id=' + id : '') + (fas ? '&fas=' + fas : '');
        if (location.hash === h) route(); else location.hash = h;
    }
    window.addEventListener('hashchange', route);
    function route() {
        const { view, id, fas } = parseHash();
        renderRail(view);
        if (!msgKeep) showMsg('');
        msgKeep = false;
        rowFns = [];
        setActions([]);
        body().innerHTML = spinner();
        try { VIEWS[view](id, fas); } catch (e) { body().innerHTML = errBox('Delen kunde inte visas: ' + e.message); }
    }

    // =====================================================================
    //  ÖVERSIKT — "väntar något på mig?"
    // =====================================================================
    let ovMineOnly = true;
    function viewOversikt() {
        setHead('Översikt', esc(C.orgName));
        setActions([
            { label: 'Nytt möte…', icon: 'bi-plus-lg', fn: newMeetingDialog },
            { label: 'Anmäl ett ärende…', icon: 'bi-inbox', fn: () => issueDialog(null) },
            CAN_MANAGE ? { divider: true } : null,
            CAN_MANAGE ? { header: 'Inställningar' } : null,
            CAN_MANAGE ? { label: 'Mötesmallar…', icon: 'bi-list-check', fn: () => go('mallar') } : null
        ]);
        get('BoardWork', 'GetOverview', OWN()).then(r => {
            if (!r.success) { body().innerHTML = errBox(r.message || 'Översikten kunde inte hämtas.'); return; }
            counts.arenden = r.canPlace ? r.issuesToPlace : 0;
            counts.motioner = r.canPlace ? r.motionsWithoutOpinion : 0;
            renderRail('oversikt');
            body().innerHTML = renderOverview(r);
            const box = $('ovMine');
            if (box) box.addEventListener('change', () => { ovMineOnly = box.checked; $('ovActions').innerHTML = actionsTable(r.actions); });
            bindActionChecks();
        }).catch(() => body().innerHTML = errBox('Översikten kunde inte hämtas.'));
    }

    function renderOverview(r) {
        let h = '';
        // Nästa möte
        const n = r.nextMeeting;
        h += '<div class="sv-card"><div class="sv-cardh"><h2>Nästa möte</h2>' + (n ? pill(n.status, meetingStatusLabel(n.status)) : '') + '</div>';
        if (n) {
            const bits = [`Dagordning med ${n.agendaCount} punkter`];
            if (n.placedIssues) bits.push(`${n.placedIssues} anmälda ärenden`);
            if (n.placedMotions) bits.push(`${n.placedMotions} motioner`);
            h += `<div style="font-size:1.2rem;font-weight:700;">${esc(n.title)}</div>
                <div class="sv-help">${esc(svDate(n.meetingDate))}${n.location ? ' · ' + esc(n.location) : ''}</div>
                <ul class="mt-2 mb-3"><li>${bits.join(', ')}</li>
                <li>${n.kallelseSent ? 'Kallelsen skickades ' + esc(svDate(n.kallelseSentDate)) : 'Kallelsen är inte skickad'}</li>
                ${n.waitingForIt ? `<li>${n.waitingForIt} ärenden i kön är önskade till mötet</li>` : ''}</ul>
                <button type="button" class="btn btn-primary" data-sv-go="mote" data-id="${n.id}">Öppna mötet</button>`;
        } else {
            h += '<p class="sv-help mb-0">Inget kommande möte är inlagt. Välj <strong>Nytt möte</strong> under Åtgärder.</p>';
        }
        h += '</div>';

        h += newIssueBox((r.myIssues || []).filter(i => i.state === 'Waiting').length);

        // Väntar på dig
        const rows = [];
        if (r.canPlace && r.issuesToPlace)
            rows.push(todo(r.issuesToPlace, `ärende${r.issuesToPlace === 1 ? '' : 'n'} väntar på att placeras på ett möte`, 'arenden', 0, 'Placera'));
        (r.justering || []).forEach(j =>
            rows.push(todo('', `Protokoll att justera: <strong>${esc(j.title)}</strong> ${esc(svDate(j.meetingDate))}`, 'mote', j.id, 'Justera', 'efter')));
        if (r.canPlace && r.motionsWithoutOpinion)
            rows.push(todo(r.motionsWithoutOpinion, `motion${r.motionsWithoutOpinion === 1 ? '' : 'er'} till årsmötet saknar styrelsens yttrande`, 'motioner', 0, 'Visa'));
        if (r.canPlace && r.annualMeeting && !r.annualMeeting.motionDeadline)
            rows.push(todo('', `Årsmötet ${esc(svDate(r.annualMeeting.meetingDate))} har ingen sista dag för motioner`, 'motioner', 0, 'Sätt dag'));
        if (r.isAdmin && r.expiring && r.expiring.length)
            rows.push(todo(r.expiring.length, `mandat går ut inom 90 dagar (${r.expiring.map(x => esc(x.title)).join(', ')})`, 'styrelsen', 0, 'Visa'));
        (r.myIssues || []).filter(i => i.state === 'Placed' || i.state === 'Handled' || i.state === 'Rejected').slice(0, 3).forEach(i => {
            const where = i.placement ? ` — ${esc(i.placement.meetingTitle)} ${esc(svDate(i.placement.meetingDate))}, ${esc(i.placement.paragraph)}` : '';
            rows.push(`<div class="sv-todo done"><span class="n">✓</span><span class="t">Ditt ärende <strong>${esc(i.title)}</strong>: ${esc(i.stateLabel.toLowerCase())}${where}</span></div>`);
        });
        h += '<div class="sv-card"><div class="sv-cardh"><h2>Väntar på dig</h2></div>'
            + (rows.length ? rows.join('') : '<div class="sv-todo done"><span class="n">✓</span><span class="t">Inget väntar på dig just nu.</span></div>')
            + '</div>';

        // Uppgifter + årshjul
        h += `<div class="row g-3"><div class="col-12 col-xl-7"><div class="sv-card h-100">
            <div class="sv-cardh"><h2>Uppgifter</h2>
                <label class="sv-help d-flex align-items-center gap-2 mb-0"><input type="checkbox" class="form-check-input" id="ovMine" ${ovMineOnly ? 'checked' : ''}> Bara mina</label></div>
            <p class="sv-help mb-2">Uppgifter som styrelsen beslutat och som inte är klara, även från tidigare år.</p>
            <div id="ovActions">${actionsTable(r.actions)}</div></div></div>
            <div class="col-12 col-xl-5"><div class="sv-card h-100"><div class="sv-cardh"><h2>Årshjulet nu</h2>
                <a href="#vy=arshjul" class="small">Hela årshjulet</a></div>`;
        h += (r.wheel || []).length
            ? (r.wheel.map(w => `<div class="sv-person"><span>${esc(w.title)}</span><span class="${w.isOverdue ? 'text-danger' : 'sv-help'}">${w.targetDate ? esc(svDate(w.targetDate)) + (w.isOverdue ? ' (försenat)' : '') : ''}</span></div>`).join(''))
            : '<p class="sv-help mb-0">Inga öppna punkter i årets årshjul.</p>';
        h += '</div></div></div>';
        return h;
    }
    function todo(n, text, view, id, btn, fas) {
        return `<div class="sv-todo"><span class="n">${n === '' ? '<i class="bi bi-exclamation-circle" aria-hidden="true"></i>' : n}</span><span class="t">${text}</span>
            <button type="button" class="btn btn-sm btn-outline-secondary" data-sv-go="${view}" ${id ? `data-id="${id}"` : ''} ${fas ? `data-fas="${fas}"` : ''}>${esc(btn)}</button></div>`;
    }
    function actionsTable(actions) {
        const list = (actions || []).filter(a => !ovMineOnly || a.mine);
        if (!list.length) return `<p class="sv-help mb-0">${ovMineOnly ? 'Du har inga öppna uppgifter.' : 'Inga öppna uppgifter.'}</p>`;
        return list.map(a => `<div class="sv-person" id="svTask_${a.id}">
            <label class="d-flex align-items-center gap-2 mb-0" style="cursor:pointer;">
                <input type="checkbox" class="form-check-input sv-check" data-sv-done="${a.id}">
                <span class="svTaskText">${esc(a.description)}${a.assignedToName ? `<span class="sv-help"> – ${esc(a.assignedToName)}</span>` : ''}</span>
            </label>
            ${a.dueDate ? `<span class="${a.isOverdue ? 'text-danger' : 'sv-help'} text-nowrap">senast ${esc(svDate(a.dueDate))}</span>` : ''}</div>`).join('');
    }
    function bindActionChecks() {
        body().querySelectorAll('[data-sv-done]').forEach(cb => cb.addEventListener('change', () => {
            const id = parseInt(cb.dataset.svDone, 10), done = cb.checked;
            post('BoardMeeting', 'SetActionDone', { actionId: id, done }).then(r => {
                if (!r.success) { cb.checked = !done; failMsg(r); return; }
                const t = cb.closest('.sv-person')?.querySelector('.svTaskText, .svActText');
                if (t) { t.classList.toggle('text-decoration-line-through', done); t.classList.toggle('sv-help', done); }
            });
        }));
    }

    // =====================================================================
    //  MÖTEN — listan
    // =====================================================================
    let svMeetings = [], mtYear = null, mtShowDone = false;
    function viewMoten() {
        setHead('Möten', 'Styrelsens möten och årsmöten. Öppna ett möte för att förbereda det, föra protokoll och justera.');
        setActions([
            { label: 'Nytt möte…', icon: 'bi-plus-lg', fn: newMeetingDialog },
            CAN_MANAGE ? { label: 'Mötesmallar…', icon: 'bi-list-check', fn: () => go('mallar') } : null
        ]);
        get('BoardMeeting', 'GetMeetings', OWN()).then(r => {
            if (!r.success) { body().innerHTML = errBox(r.message || 'Mötena kunde inte hämtas.'); return; }
            svMeetings = r.data || [];
            renderMeetingList();
        }).catch(() => body().innerHTML = errBox('Mötena kunde inte hämtas.'));
    }
    function renderMeetingList() {
        const yrs = new Set(svMeetings.map(m => (m.meetingDate || '').slice(0, 4)).filter(Boolean));
        yrs.add(String(new Date().getFullYear()));
        const years = Array.from(yrs).sort().reverse();
        if (mtYear == null || !years.includes(mtYear)) mtYear = String(new Date().getFullYear());
        const ofYear = svMeetings.filter(m => (m.meetingDate || '').slice(0, 4) === mtYear);
        const done = ofYear.filter(m => m.status === 'Justerat');
        const shown = ofYear.filter(m => mtShowDone || m.status !== 'Justerat')
            .sort((a, b) => (a.meetingDate < b.meetingDate ? -1 : 1));

        let h = `<div class="d-flex flex-wrap gap-3 align-items-center mb-2 sv-help">
            <label class="d-flex align-items-center gap-2 mb-0"><input type="checkbox" class="form-check-input" id="mtDone" ${mtShowDone ? 'checked' : ''}> Visa justerade (${done.length})</label>
            <label class="d-flex align-items-center gap-2 mb-0">År <select id="mtYear" class="form-select form-select-sm" style="width:auto;">${years.map(y => `<option ${y === mtYear ? 'selected' : ''}>${y}</option>`).join('')}</select></label>
            <span>${shown.length} av ${ofYear.length} möten</span></div>`;
        if (!shown.length) {
            h += `<div class="sv-card"><p class="sv-help mb-0">${ofYear.length ? 'Alla möten i år är justerade. Kryssa i <em>Visa justerade</em> för att se dem.' : 'Inga möten ' + mtYear + '. Välj <strong>Nytt möte</strong> under Åtgärder.'}</p></div>`;
        } else {
            h += `<div class="sv-card p-0"><div class="table-responsive"><table class="table sv-table align-middle mb-0">
                <thead><tr><th class="ps-3">Datum</th><th>Möte</th><th>Läge</th><th class="text-end pe-3"></th></tr></thead><tbody>`;
            shown.forEach(m => {
                h += `<tr><td class="ps-3 text-nowrap">${esc(svDate(m.meetingDate))}</td>
                    <td><a href="#vy=mote&id=${m.id}" class="fw-semibold">${esc(m.title)}</a><div class="sv-help">${esc(m.typeLabel)}${m.location ? ' · ' + esc(m.location) : ''}</div></td>
                    <td>${pill(m.status, meetingStatusLabel(m.status))}${m.status === 'Planerat' && !m.kallelseSent && !m.isPast ? '<div class="sv-help small">Kallelse ej skickad</div>' : ''}</td>
                    <td class="text-end pe-3">${rowMenu([
                        { label: 'Öppna mötet', fn: () => go('mote', m.id) },
                        { label: 'Skriv ut dagordning', fn: () => window.open('/styrelse/dagordning/' + m.id, '_blank') },
                        { label: 'Skriv ut protokoll', fn: () => window.open('/styrelse/protokoll/' + m.id, '_blank') },
                        { divider: true },
                        { label: 'Ta bort mötet…', danger: true, fn: () => deleteMeeting(m.id, m.title) }
                    ])}</td></tr>`;
            });
            h += '</tbody></table></div></div>';
        }
        body().innerHTML = h;
        $('mtDone').addEventListener('change', e => { mtShowDone = e.target.checked; renderMeetingList(); });
        $('mtYear').addEventListener('change', e => { mtYear = e.target.value; renderMeetingList(); });
    }

    let svCreating = false;
    function newMeetingDialog() {
        const def = new Date().toISOString().slice(0, 10) + ' 18:30';
        formModal({
            title: 'Nytt möte', okLabel: 'Skapa mötet', wide: false,
            html: `<label class="sv-label" for="nmType">Typ av möte</label>
                <select id="nmType" class="form-select mb-1">${MEETING_TYPES.map(t => `<option value="${t[0]}">${esc(t[1])}</option>`).join('')}</select>
                <div class="sv-help mb-3">Dagordningen fylls i från ${IS_CLUB ? 'klubbens' : 'kretsens'} mall och styrelsen läggs till som närvarande. Allt går att ändra sedan.</div>
                <label class="sv-label" for="nmDate">Datum och tid</label>
                <input type="text" id="nmDate" class="form-control mb-3" value="${def}">
                <label class="sv-label" for="nmLoc">Plats (frivilligt)</label>
                <input type="text" id="nmLoc" class="form-control" placeholder="t.ex. Klubbstugan">
                <div id="nmDup" class="alert alert-warning mt-3 d-none"></div>`,
            onOpen: () => datePick($('nmDate'), true),
            onOk: () => createMeeting(false)
        });
    }
    function createMeeting(dup) {
        if (svCreating) return Promise.resolve(false);
        svCreating = true;
        const date = ($('nmDate').value || '').trim();
        return post('BoardMeeting', 'CreateMeeting', {
            ownerType: OWNER_TYPE, ownerId: OWNER_ID, meetingType: $('nmType').value, title: '',
            meetingDate: date, location: ($('nmLoc').value || '').trim(), createDuplicate: dup ? '1' : '0'
        }).then(r => {
            if (r.success) { closeForm(); go('mote', r.data.id, 'forbered'); return true; }
            if (r.duplicate) {
                // ⚠️ En andra tryckning skapade förr en full kopia. Fråga i dialogen, aldrig med confirm().
                const d = r.duplicate, box = $('nmDup');
                box.innerHTML = `Det finns redan ett möte av den typen samma dag: <strong>${esc(d.title)}</strong> (${esc(svDate(d.meetingDate))}).
                    <div class="mt-2 d-flex gap-2 flex-wrap"><button type="button" class="btn btn-sm btn-primary" id="nmOpen">Öppna det mötet</button>
                    <button type="button" class="btn btn-sm btn-outline-secondary" id="nmDupYes">Skapa ett till ändå</button></div>`;
                box.classList.remove('d-none');
                $('nmOpen').onclick = () => { closeForm(); go('mote', d.id); };
                $('nmDupYes').onclick = () => { svCreating = false; createMeeting(true); };
                return false;
            }
            return r.message || 'Mötet kunde inte skapas.';
        }).finally(() => { svCreating = false; });
    }

    async function deleteMeeting(id, title) {
        const ok = await svConfirm('Ta bort mötet?', `${title || 'Mötet'} tas bort med dagordning, närvaro och anteckningar. Ärenden och motioner som ligger på mötet går tillbaka till kön.\n\nDet går inte att ångra.`, 'Ta bort mötet', true);
        if (!ok) return;
        post('BoardMeeting', 'DeleteMeeting', { meetingId: id }).then(r => {
            if (!r.success) { failMsg(r, 'Mötet kunde inte tas bort.'); return; }
            showMsg('Mötet är borttaget.', 'success', true);
            go('moten');
        });
    }

    // =====================================================================
    //  ETT MÖTE — livscykeln och tre faser
    // =====================================================================
    let svCatalog = [], svMeetingTypes = [], svDocs = null;
    const phaseByMeeting = {};
    let cur = null;   // { m, d } — mötet som visas

    function viewMote(id, fas) {
        if (!id) { go('moten'); return; }
        loadMeeting(id, fas, true);
    }
    function reloadMeeting() { if (cur) loadMeeting(cur.m.id, '', false); }
    function loadMeeting(id, fas, scroll) {
        get('BoardMeeting', 'GetMeetingDetail', { meetingId: id }).then(d => {
            if (!d.success) { setHead('Mötet', ''); body().innerHTML = errBox(d.message || 'Mötet kunde inte öppnas.'); return; }
            if (fas) phaseByMeeting[id] = fas;
            renderMeeting(d, scroll);
        }).catch(() => body().innerHTML = errBox('Mötet kunde inte öppnas.'));
    }
    window.openMeeting = function (id) { go('mote', id); };

    function meetingStep(m) {
        if (m.status === 'Justerat') return 4;
        if (m.status === 'VantarJustering') return 3;
        if (m.status === 'Genomfört' || m.isPast) return 2;
        if (m.kallelseSentDate) return 1;
        return 0;
    }
    function defaultPhase(m) {
        if (m.status === 'Justerat' || m.status === 'VantarJustering') return 'efter';
        if (m.isPast || m.isToday) return 'under';
        return 'forbered';
    }

    function renderMeeting(d, scroll) {
        const m = d.meeting, j = d.justering || {};
        cur = { m, d };
        const locked = m.status === 'Justerat' || m.status === 'VantarJustering';
        const phase = phaseByMeeting[m.id] || defaultPhase(m);
        phaseByMeeting[m.id] = phase;
        window._mId = m.id; window._mAgenda = d.agenda || [];
        window._mYear = parseInt((m.meetingDate || '').slice(0, 4), 10) || new Date().getFullYear();

        setHead(m.title, `${esc(m.typeLabel)} · ${esc(svDate(m.meetingDate))}${m.location ? ' · ' + esc(m.location) : ''}`,
            '<a href="#vy=moten">← Alla möten</a>');
        setActions([
            !locked ? { label: 'Ändra datum, tid och plats…', icon: 'bi-pencil', fn: editMeetingDialog } : null,
            !locked ? { label: m.kallelseSentDate ? 'Skicka kallelsen igen…' : 'Skicka kallelse…', icon: 'bi-envelope-paper', fn: () => sendKallelse(m.id) } : null,
            m.isAnnual && CAN_PLACE && !locked ? { label: 'Sista dag för motioner…', icon: 'bi-calendar-check', fn: () => deadlineDialog(m) } : null,
            { divider: true }, { header: 'Utskrift' },
            { label: 'Dagordning', icon: 'bi-card-list', fn: () => window.open('/styrelse/dagordning/' + m.id, '_blank') },
            { label: 'Protokoll', icon: 'bi-printer', fn: () => window.open('/styrelse/protokoll/' + m.id, '_blank') },
            { divider: true },
            !locked ? { label: 'Skicka protokollet för justering…', icon: 'bi-send', fn: () => sendForJustering(m.id) } : null,
            locked ? { label: 'Återöppna protokollet för redigering…', icon: 'bi-arrow-counterclockwise', fn: () => reopenJustering(m.id) } : null,
            { divider: true },
            { label: 'Ta bort mötet…', danger: true, fn: () => deleteMeeting(m.id, m.title) }
        ]);

        const step = meetingStep(m);
        const steps = ['Planerat', 'Kallelse skickad', 'Genomfört', 'Väntar på justering', 'Justerat']
            .map((s, i) => `<div class="${i < step ? 'done' : i === step ? 'now' : ''}">${s}</div>`).join('');
        let h = `<div class="sv-steps" aria-label="Mötets läge">${steps}</div>`;
        h += nextStepBox(m, j, d);
        h += `<div class="btn-group sv-phases mb-3 sv-noprint" role="group" aria-label="Fas">
            ${[['forbered', 'Förbered'], ['under', 'Under mötet'], ['efter', 'Efter mötet']].map(([k, l]) =>
                `<button type="button" class="btn ${phase === k ? 'btn-primary' : 'btn-outline-primary'}" data-phase="${k}">${l}</button>`).join('')}</div>`;
        h += '<div id="mtPhase">';
        if (phase === 'forbered') h += phasePrepare(m, d, locked);
        else if (phase === 'under') h += phaseRun(m, d, locked);
        else h += phaseAfter(m, d, j, locked);
        h += '</div>';
        body().innerHTML = h;

        body().querySelectorAll('[data-phase]').forEach(b => b.addEventListener('click', () => {
            phaseByMeeting[m.id] = b.dataset.phase; renderMeeting(d, false);
        }));
        bindMeeting(m, d, locked);
        if (scroll) window.scrollTo({ top: 0 });
        if (window._agFocus) {
            const f = document.querySelector(`[data-ag="${window._agFocus}"]`);
            window._agFocus = null;
            if (f) f.scrollIntoView({ block: 'center' });
        }
    }

    function nextStepBox(m, j, d) {
        let t = '', btn = '';
        if (m.status === 'Justerat') t = `Protokollet är justerat${m.justifiedDate ? ' ' + esc(svDate(m.justifiedDate)) : ''}. Det finns i utskriften och för revisorn.`;
        else if (m.status === 'VantarJustering') {
            t = `Protokollet väntar på justering (${j.approvedCount || 0} av ${j.totalSigners || 0} har godkänt).`;
            if (j.canApprove) btn = `<button type="button" class="btn btn-primary btn-sm" data-act="approve">Godkänn protokollet</button>`;
        }
        else if (m.isPast) t = 'Mötet är hållet. Skriv klart protokollet under <em>Under mötet</em> och skicka det sedan för justering.';
        else if (m.isToday) t = 'Mötet är i dag. Markera närvaron och skriv besluten under <em>Under mötet</em>.';
        else if (!m.kallelseSentDate) {
            t = 'Gör klart dagordningen och skicka kallelsen.';
            if ((d.waitingIssues || []).length) t += ` ${d.waitingIssues.length} ärenden i kön är önskade till mötet.`;
            btn = `<button type="button" class="btn btn-primary btn-sm" data-act="kallelse">Skicka kallelse…</button>`;
        }
        else t = `Kallelsen är skickad. Under mötet förs protokollet under <em>Under mötet</em>.`;
        return `<div class="sv-next sv-noprint"><span class="t"><strong>Nästa steg:</strong> ${t}</span>${btn}</div>`;
    }

    // ---- Förbered: dagordningen, kön, kallelsen, motionerna ----
    function phasePrepare(m, d, locked) {
        let h = '';
        const waiting = d.waitingIssues || [];
        if (!locked && d.canPlace && waiting.length) {
            h += `<div class="sv-card sv-attn"><div class="sv-cardh"><h2>Ärenden i kön till det här mötet (${waiting.length})</h2></div>`;
            waiting.forEach(w => {
                h += `<div class="sv-person"><span><strong>${esc(w.title)}</strong><span class="sv-help d-block">${esc(w.kindLabel)} · anmält av ${esc(w.submittedBy)}</span></span>
                    <button type="button" class="btn btn-sm btn-primary" data-place-issue="${w.id}">Placera…</button></div>`;
            });
            h += '</div>';
        }
        h += `<div class="sv-card"><div class="sv-cardh"><h2>Dagordning</h2>${locked ? '' : '<span id="agSaved" class="sv-saved"><i class="bi bi-check-lg"></i> Sparat</span>'}</div>`;
        h += agendaList(d, 'prep', locked);
        if (!locked) {
            h += `<div class="mt-3 sv-noprint"><label class="sv-label" for="newAgendaType">Lägg till en punkt</label>
                <div class="row g-2" style="max-width:680px;">
                    <div class="col-12 col-md-7"><select id="newAgendaType" class="form-select">${agendaCatalogOptions()}</select></div>
                    <div class="col-12 col-md-5 d-none" id="newAgendaCustomWrap"><input type="text" id="newAgendaHeading" class="form-control" placeholder="Rubrik på punkten"></div>
                    <div class="col-12"><button type="button" class="btn btn-outline-primary" data-act="addAgenda"><i class="bi bi-plus-lg me-1"></i>Lägg till punkt</button></div>
                </div></div>`;
        }
        h += '</div>';

        if (m.isAnnual) {
            h += `<div class="sv-card"><div class="sv-cardh"><h2>Motioner till årsmötet</h2></div>
                <p class="mb-2">Sista dag för motioner: <strong>${m.motionDeadline ? esc(svDate(m.motionDeadline)) : 'inte satt'}</strong>.
                ${CAN_PLACE && !locked ? '<button type="button" class="btn btn-link p-0 align-baseline" data-act="deadline">Ändra</button>' : ''}</p>
                <p class="sv-help mb-0">Motionerna, styrelsens yttranden och vägen till dagordningen finns under <a href="#vy=motioner">Motioner</a>.
                Medlemmarna lämnar motioner på <a href="/motion?${IS_CLUB ? 'klubb' : 'krets'}=${OWNER_ID}" target="_blank" rel="noopener">motionssidan</a>.</p></div>`;
        }

        h += `<div class="sv-card sv-noprint"><div class="sv-cardh"><h2>Kallelse</h2>${m.kallelseSentDate ? pill('Justerat', 'Skickad ' + svDate(m.kallelseSentDate)) : pill('Planerat', 'Inte skickad')}</div>`;
        if (m.kallelseSentDate) h += `<p class="text-success mb-2"><i class="bi bi-check-circle me-1"></i>Skickad ${esc(svDate(m.kallelseSentDate))}${m.kallelseRecipientCount != null ? ' till ' + m.kallelseRecipientCount + ' mottagare' : ''}.</p>`;
        if (!locked) {
            const isArs = m.meetingType === 'Arsmote' || m.meetingType === 'ExtraArsmote';
            h += `<p class="sv-help">Kallelsen skickas med dagordningen via e-post. ${isArs ? 'För årsmöte går den till <strong>alla medlemmar</strong>.' : 'Den går till <strong>styrelsen</strong>.'}</p>
                <label class="sv-label" for="kallelseMsg">Eget meddelande (frivilligt)</label>
                <textarea id="kallelseMsg" class="form-control mb-2" rows="2"></textarea>
                <button type="button" class="btn btn-primary" data-act="kallelse">${m.kallelseSentDate ? 'Skicka kallelsen igen…' : 'Skicka kallelse…'}</button>`;
        }
        h += '</div>';
        return h;
    }

    // ---- Under mötet: närvaron och protokollet ----
    function phaseRun(m, d, locked) {
        const q = d.quorum;
        let h = `<div id="svQuorum" class="sv-card ${q.isMet ? 'sv-quorum-ok' : 'sv-quorum-no'}" data-required="${q.required}" data-total="${q.total}">
            <h2>Närvaro</h2><p id="svQuorumMsg" class="mb-2">${quorumMsgHtml(q)}</p>`;
        d.attendees.forEach(a => {
            const roleTxt = a.roleTitle ? `<span class="sv-help d-block">${esc(a.roleTitle)}</span>` : '';
            if (locked) h += `<div class="sv-person"><span>${esc(a.memberName || '-')} ${roleTxt}</span><span>${esc(a.attendanceStatus)}</span></div>`;
            else h += `<div class="sv-person"><label class="d-flex align-items-center gap-2 mb-0" style="cursor:pointer;">
                    <input type="checkbox" class="form-check-input sv-check svAttChk" data-att="${a.id}" ${a.attendanceStatus === 'Närvarande' ? 'checked' : ''}>
                    <span>${esc(a.memberName || '-')}${roleTxt}</span></label>
                    <span class="sv-help" id="svAttStatus_${a.id}">${a.attendanceStatus === 'Närvarande' ? 'Närvarande' : 'Frånvarande'}</span></div>`;
        });
        if (!locked && CAN_MANAGE) h += `<button type="button" class="btn btn-link p-0 mt-2 sv-noprint" data-act="syncBoard">Saknas någon? Hämta styrelsen igen</button>`;
        h += '</div>';
        h += `<div class="sv-card"><div class="sv-cardh"><h2>Protokoll</h2></div>
            <p class="sv-help">Skriv vad som sades och vad som beslutades under varje punkt. Allt sparas när du lämnar fältet.</p>
            ${agendaList(d, 'run', locked)}</div>`;
        return h;
    }

    // ---- Efter mötet: justering och uppgifter ----
    function phaseAfter(m, d, j, locked) {
        let h = justeringCard(m, j, d.agenda || []);
        h += '<div class="sv-card"><div class="sv-cardh"><h2>Uppgifter från mötet</h2></div>';
        if (d.actions.length) {
            d.actions.forEach(a => {
                const who = a.assignedToName ? ` – ${esc(a.assignedToName)}` : '';
                const due = a.dueDate ? `<span class="${a.isOverdue ? 'text-danger' : 'sv-help'}"> (senast ${esc(svDate(a.dueDate))})</span>` : '';
                h += `<div class="sv-person"><label class="d-flex align-items-center gap-2 mb-0" style="cursor:pointer;">
                    <input type="checkbox" class="form-check-input sv-check" data-sv-done="${a.id}" ${a.isDone ? 'checked' : ''}>
                    <span class="svActText ${a.isDone ? 'text-decoration-line-through sv-help' : ''}">${esc(a.description)}<span class="sv-help">${who}</span>${due}</span></label>
                    ${locked ? '' : rowMenu([{ label: 'Ta bort uppgiften…', danger: true, fn: () => removeAction(a.id) }])}</div>`;
            });
        } else h += '<p class="sv-help">Inga uppgifter från mötet.</p>';
        const opts = '<option value="">Ingen särskild person</option>' + d.attendees.map(a => `<option value="${a.memberId}">${esc(a.memberName || '')}</option>`).join('');
        h += `<div class="row g-2 mt-2" style="max-width:680px;">
            <div class="col-12"><label class="sv-label" for="actDesc">Ny uppgift</label><input type="text" id="actDesc" class="form-control" placeholder="Vad ska göras?"></div>
            <div class="col-12 col-md-6"><label class="sv-help" for="actWho">Vem ansvarar?</label><select id="actWho" class="form-select">${opts}</select></div>
            <div class="col-12 col-md-6"><label class="sv-help" for="actDue">Klart senast (frivilligt)</label><input type="text" id="actDue" class="form-control"></div>
            <div class="col-12"><button type="button" class="btn btn-outline-primary" data-act="addAction"><i class="bi bi-plus-lg me-1"></i>Lägg till uppgift</button></div></div></div>`;
        return h;
    }

    function bindMeeting(m, d, locked) {
        const B = body();
        B.querySelectorAll('[data-act]').forEach(btn => btn.addEventListener('click', () => {
            const a = btn.dataset.act;
            if (a === 'kallelse') sendKallelse(m.id);
            else if (a === 'approve') approveProtokoll(m.id);
            else if (a === 'addAgenda') addAgenda(m.id);
            else if (a === 'deadline') deadlineDialog(m);
            else if (a === 'syncBoard') post('BoardMeeting', 'SyncAttendees', { meetingId: m.id }).then(r => { if (r.success) reloadMeeting(); });
            else if (a === 'addAction') addAction(m.id);
            else if (a === 'sendJust') sendForJustering(m.id);
            else if (a === 'qr') showJusteringQr(m.id);
            else if (a === 'mailJust') sendJusteringMail(m.id);
            else if (a === 'reopen') reopenJustering(m.id);
        }));
        B.querySelectorAll('[data-place-issue]').forEach(btn => btn.addEventListener('click', () => placeIssueDialog(parseInt(btn.dataset.placeIssue, 10), m.id)));
        const sel = $('newAgendaType');
        if (sel) sel.addEventListener('change', () => $('newAgendaCustomWrap').classList.toggle('d-none', sel.value !== 'ovrigt'));
        B.querySelectorAll('.svAttChk').forEach(cb => cb.addEventListener('change', () => togglePresent(parseInt(cb.dataset.att, 10), cb.checked)));
        B.querySelectorAll('[data-ag-field]').forEach(f => f.addEventListener('change', () => saveAgenda(parseInt(f.dataset.agId, 10))));
        B.querySelectorAll('[data-ag-move]').forEach(btn => btn.addEventListener('click', () => moveAgenda(parseInt(btn.dataset.agId, 10), parseInt(btn.dataset.agMove, 10))));
        B.querySelectorAll('.svElectChk').forEach(cb => cb.addEventListener('change', ev => saveElection(ev, parseInt(cb.dataset.item, 10), parseInt(cb.dataset.count, 10))));
        B.querySelectorAll('[data-el-search]').forEach(inp => inp.addEventListener('input', () => elSearch(parseInt(inp.dataset.elSearch, 10), inp.value)));
        B.querySelectorAll('[data-el-remove]').forEach(a => a.addEventListener('click', ev => { ev.preventDefault(); elRemove(parseInt(a.dataset.item, 10), parseInt(a.dataset.elRemove, 10)); }));
        B.querySelectorAll('[data-aw-load]').forEach(btn => btn.addEventListener('click', () => loadAwards(parseInt(btn.dataset.awLoad, 10), null, true)));
        B.querySelectorAll('[data-aw-auto]').forEach(el => loadAwards(parseInt(el.dataset.awAuto, 10), null, false));
        bindActionChecks();
        datePick($('actDue'), false);
    }

    // ---- Dagordningen: huvudpunkter och underpunkter ----
    function agendaList(d, mode, locked) {
        const items = d.agenda || [];
        const tops = items.filter(i => !i.isSub);
        if (!items.length) return '<p class="sv-help mb-0">Dagordningen är tom.</p>';
        let h = '';
        tops.forEach((it, idx) => {
            const subs = items.filter(s => s.isSub && s.parentItemId === it.id);
            h += `<div class="sv-ag" data-ag="${it.id}">` + agendaItem(it, mode, locked, d, idx, tops.length);
            subs.forEach((s, sidx) => { h += `<div class="sv-sub-ag" data-ag="${s.id}">` + agendaItem(s, mode, locked, d, sidx, subs.length) + '</div>'; });
            h += '</div>';
        });
        return h;
    }

    function itemSource(it) {
        if (it.issue) {
            const src = it.issue.sourceKind === 'Motion' ? 'Från motionerna' : it.issue.sourceKind === 'Skrivelse' ? 'Inkommen skrivelse' : 'Anmält av ' + esc(it.issue.submittedBy);
            return `<div class="sv-src">${src} · ${esc(it.issue.kindLabel)}</div>${it.issue.body ? `<div class="sv-src" style="white-space:pre-wrap;">${esc(it.issue.body)}</div>` : ''}`;
        }
        if (it.motion) {
            const p = (it.motion.proposals || []).map(x => `<li>${esc(x)}</li>`).join('');
            return `<div class="sv-src">Motion ${esc(it.motion.number)} · motionär ${esc(it.motion.motionerName)}</div>
                ${p ? `<ul class="sv-src mb-1">${p}</ul>` : ''}
                ${it.motion.boardProposalLabel ? `<div class="sv-src">Styrelsens förslag: <strong>${esc(it.motion.boardProposalLabel)}</strong></div>` : ''}`;
        }
        return '';
    }

    function agendaItem(it, mode, locked, d, pos, siblings) {
        const links = (d.links || []).filter(l => l.agendaItemId === it.id);
        const linksHtml = links.map(l => linkChip(l, locked)).join('');
        const type = it.itemType || 'text';
        const typeTag = type === 'election' ? '<span class="sv-state plan ms-2">Personval</span>' : type === 'awards' ? '<span class="sv-state plan ms-2">Utdelning</span>' : type === 'note' ? '' : '';
        const no = `<span class="sv-no">${esc(it.paragraph)}</span>`;

        if (locked || mode === 'prep') {
            // Läsläge (låst) eller förberedelse: rubriken, källan och ordningen — inga protokollfält.
            let head;
            if (!locked) {
                head = `<div class="d-flex align-items-center gap-2">${no}
                    <input type="text" class="form-control" style="font-weight:600;" value="${esc(it.heading)}" id="ag_h_${it.id}" data-ag-field="1" data-ag-id="${it.id}" aria-label="Rubrik">
                    <span class="d-flex gap-1 sv-noprint">
                        <button type="button" class="btn btn-sm btn-outline-secondary" data-ag-move="-1" data-ag-id="${it.id}" ${pos === 0 ? 'disabled' : ''} title="Flytta upp" aria-label="Flytta upp"><i class="bi bi-arrow-up"></i></button>
                        <button type="button" class="btn btn-sm btn-outline-secondary" data-ag-move="1" data-ag-id="${it.id}" ${pos === siblings - 1 ? 'disabled' : ''} title="Flytta ner" aria-label="Flytta ner"><i class="bi bi-arrow-down"></i></button>
                        ${rowMenu([
                            { label: 'Bifoga dokument…', fn: () => attachDoc(it.id) },
                            { label: 'Bifoga tidigare möte…', fn: () => attachMeeting(it.id) },
                            { label: 'Bifoga valberedningens förslag…', fn: () => attachValforslag(it.id) },
                            { label: 'Bifoga länk…', fn: () => attachUrl(it.id) },
                            { divider: true },
                            { label: it.issueId || it.motionId ? 'Ta bort från mötet (tillbaka till kön)…' : 'Ta bort punkten…', danger: true, fn: () => removeAgenda(it) }
                        ])}
                    </span></div>`;
            } else {
                head = `<div class="d-flex align-items-center gap-2" style="font-weight:600;">${no}<span>${esc(it.heading)}</span>${typeTag}</div>`;
            }
            let extra = itemSource(it);
            if (locked) {
                if (type === 'election') extra += (it.electedNames || []).filter(Boolean).length ? `<div class="mt-1">Valda: <strong>${it.electedNames.filter(Boolean).map(esc).join(', ')}</strong></div>` : '';
                else if (type === 'awards') {
                    const s = it.awardsSummary;
                    extra += s ? `<div class="mt-1">Utdelning ${s.year}: <strong>${s.receivedCount}</strong> mottog${s.absentCount ? `, ${s.absentCount} ej närvarande` : ''}${s.laterCount ? `, ${s.laterCount} delas ut senare` : ''}.</div>` : '';
                } else {
                    if (it.discussion) extra += `<div class="mt-1" style="white-space:pre-wrap;">${esc(it.discussion)}</div>`;
                    if (type !== 'note' && it.decision) extra += `<div class="mt-1"><strong>Beslut:</strong> <span style="white-space:pre-wrap;">${esc(it.decision)}</span></div>`;
                    if (IS_CLUB && type !== 'note' && it.decision && !it.motionId) extra += `<div class="mt-1 sv-noprint"><button type="button" class="btn btn-link btn-sm p-0" data-krets-from="${it.id}">Gör beslutet till en motion till kretsen…</button></div>`;
                }
            } else if (type !== 'text') {
                extra = typeTag.replace('ms-2', '') + extra;
            }
            return head + (extra ? `<div class="mt-1" style="margin-left:${locked ? '2.6rem' : '0'}">${extra}</div>` : '') + (linksHtml ? `<div class="mt-1">${linksHtml}</div>` : '');
        }

        // Under mötet: protokollfälten.
        let fields;
        const electedIds = it.electedMemberIds || [], electedNames = it.electedNames || [];
        if (type === 'election' && it.electionSource === 'members') {
            const count = it.electionCount || 1;
            const chips = electedIds.map((id, i) => `<span class="badge bg-primary me-1 mb-1" style="font-size:.95rem;">${esc(electedNames[i] || ('#' + id))}
                <a href="#" data-el-remove="${id}" data-item="${it.id}" style="color:#fff;text-decoration:none;" aria-label="Ta bort">✕</a></span>`).join('');
            fields = `<label class="sv-help">Välj ${count === 1 ? 'en person' : count + ' personer'} bland medlemmarna</label>
                <div class="mb-1">${chips || '<span class="sv-help">Ingen vald ännu.</span>'}</div>
                <input type="text" class="form-control" data-el-search="${it.id}" placeholder="Sök medlem…" autocomplete="off" style="max-width:420px;">
                <div id="elres_${it.id}" class="list-group mt-1" style="max-width:420px;"></div>`;
        } else if (type === 'election') {
            const count = it.electionCount || 1;
            const present = d.attendees.filter(a => a.attendanceStatus === 'Närvarande');
            fields = `<label class="sv-help">Välj ${count === 1 ? 'en person' : count + ' personer'} bland de närvarande</label><div class="mb-1">`
                + (present.length ? present.map(a => `<label class="d-flex align-items-center gap-2 mb-1" style="cursor:pointer;">
                    <input type="checkbox" class="form-check-input sv-check svElectChk svElect_${it.id}" data-item="${it.id}" data-count="${count}" value="${a.memberId}" ${electedIds.includes(a.memberId) ? 'checked' : ''}>
                    <span>${esc(a.memberName || '-')}${a.isChairman ? ' (ordförande)' : a.isSecretary ? ' (sekreterare)' : ''}</span></label>`).join('')
                    : '<p class="sv-help mb-0">Inga närvarande att välja — markera närvaron först.</p>') + '</div>';
        } else if (type === 'awards') {
            const s = it.awardsSummary;
            const yr = s ? s.year : (window._mYear || new Date().getFullYear()) - 1;
            fields = `<div class="d-flex gap-2 flex-wrap align-items-center mb-2 sv-noprint">
                    <label class="sv-help mb-0" for="aw_year_${it.id}">Verksamhetsår</label>
                    <input type="number" class="form-control form-control-sm" style="width:100px;" id="aw_year_${it.id}" value="${yr}">
                    <button type="button" class="btn btn-sm btn-outline-primary" data-aw-load="${it.id}"><i class="bi bi-arrow-repeat me-1"></i>${s ? 'Hämta om listan' : 'Hämta årets lista'}</button>
                    <span class="sv-help">Årsmötet delar ut föregående års märken.</span></div>
                <div id="aw_body_${it.id}" ${s ? `data-aw-auto="${it.id}"` : ''}>${s ? '' : '<p class="sv-help mb-0">Ingen lista hämtad än.</p>'}</div>`;
        } else {
            fields = `<label class="sv-help" for="ag_disc_${it.id}">Anteckningar</label>
                <textarea class="form-control mb-2" rows="2" id="ag_disc_${it.id}" data-ag-field="1" data-ag-id="${it.id}" placeholder="Vad sades under punkten?">${esc(it.discussion || '')}</textarea>`;
            if (type !== 'note') fields += `<label class="sv-help" for="ag_dec_${it.id}">Beslut</label>
                <textarea class="form-control" rows="2" id="ag_dec_${it.id}" data-ag-field="1" data-ag-id="${it.id}" placeholder="Vad beslutade ${it.motionId ? 'årsmötet' : 'styrelsen'}?">${esc(it.decision || '')}</textarea>`;
        }
        return `<div class="d-flex align-items-center gap-2" style="font-weight:600;">${no}<span>${esc(it.heading)}</span>
                <span id="ag_saved_${it.id}" class="sv-saved ms-auto"><i class="bi bi-check-lg"></i> Sparat</span></div>
            <input type="hidden" id="ag_h_${it.id}" value="${esc(it.heading)}">
            <div style="margin-left:2.6rem;">${itemSource(it)}${fields}${linksHtml ? `<div class="mt-1">${linksHtml}</div>` : ''}</div>`;
    }

    function agendaCatalogOptions() {
        let o = '<option value="">Välj punkt att lägga till…</option>';
        (svCatalog || []).forEach(c => {
            const tag = c.itemType === 'election' ? ' (personval)' : '';
            o += `<option value="${esc(c.key)}" data-heading="${esc(c.heading)}" data-type="${esc(c.itemType)}" data-role="${esc(c.electionRole || '')}" data-count="${c.electionCount || 1}" data-source="${esc(c.electionSource || 'attendees')}">${esc(c.heading)}${tag}</option>`;
        });
        return o;
    }
    function addAgenda(mid) {
        const sel = $('newAgendaType'); const key = sel.value; if (!key) { showMsg('Välj vilken punkt som ska läggas till.', 'warning'); return; }
        const opt = sel.options[sel.selectedIndex];
        let heading = opt.dataset.heading || opt.textContent;
        if (key === 'ovrigt') { heading = $('newAgendaHeading').value.trim(); if (!heading) { showMsg('Skriv en rubrik på punkten.', 'warning'); return; } }
        post('BoardMeeting', 'AddAgendaItem', {
            meetingId: mid, heading, itemType: opt.dataset.type || 'text', electionRole: opt.dataset.role || '',
            electionCount: parseInt(opt.dataset.count || '1', 10), electionSource: opt.dataset.source || 'attendees'
        }).then(r => { if (r.success) { window._agFocus = r.data && r.data.id; reloadMeeting(); } else failMsg(r); });
    }
    // ⚠️ UpdateAgendaItem skriver ALLA tre fälten. I förberedelsen finns bara rubriken på skärmen, så
    // anteckningar och beslut måste skickas med ur det inlästa mötet — annars tömmer en rättad
    // rubrik det som redan skrivits i protokollet.
    function saveAgenda(id) {
        const it = ((cur && cur.d.agenda) || []).find(a => a.id === id) || {};
        const h = $('ag_h_' + id), disc = $('ag_disc_' + id), dec = $('ag_dec_' + id);
        const p = {
            agendaItemId: id,
            heading: h ? h.value : (it.heading || ''),
            discussion: disc ? disc.value : (it.discussion || ''),
            decision: dec ? dec.value : (it.decision || '')
        };
        post('BoardMeeting', 'UpdateAgendaItem', p).then(r => {
            if (!r.success) { failMsg(r, 'Punkten kunde inte sparas.'); return; }
            it.heading = p.heading; it.discussion = p.discussion; it.decision = p.decision;
            flash($('ag_saved_' + id) || $('agSaved'));
        });
    }
    function moveAgenda(id, dir) {
        post('BoardMeeting', 'MoveAgendaItem', { agendaItemId: id, direction: dir })
            .then(r => { if (r.success) { window._agFocus = id; reloadMeeting(); } });
    }
    async function removeAgenda(it) {
        const back = it.issueId || it.motionId;
        const ok = await svConfirm(back ? 'Ta bort från mötet?' : 'Ta bort punkten?',
            back ? `${it.heading} tas bort från dagordningen och går tillbaka till kön, där den kan placeras på ett annat möte.`
                 : `${it.heading} tas bort från dagordningen${(cur.d.agenda || []).some(s => s.parentItemId === it.id) ? ', tillsammans med underpunkterna. Ärenden och motioner i dem går tillbaka till kön' : ''}.`,
            'Ta bort', true);
        if (!ok) return;
        post('BoardMeeting', 'RemoveAgendaItem', { agendaItemId: it.id }).then(r => { if (r.success) { reloadMeeting(); refreshCounts(); } else failMsg(r); });
    }

    function editMeetingDialog() {
        const m = cur.m;
        formModal({
            title: 'Ändra mötet', wide: false,
            html: `<label class="sv-label" for="emTitle">Rubrik</label><input type="text" id="emTitle" class="form-control mb-3" value="${esc(m.title)}">
                <label class="sv-label" for="emDate">Datum och tid</label><input type="text" id="emDate" class="form-control mb-3" value="${esc(m.meetingDate)}">
                <label class="sv-label" for="emLoc">Plats</label><input type="text" id="emLoc" class="form-control" value="${esc(m.location || '')}">`,
            onOpen: () => datePick($('emDate'), true),
            onOk: () => post('BoardMeeting', 'UpdateMeeting', {
                meetingId: m.id, meetingType: m.meetingType || '', title: $('emTitle').value.trim() || m.title,
                meetingDate: $('emDate').value.trim(), location: $('emLoc').value.trim(), notes: ''
            }).then(r => { if (!r.success) return r.message || 'Mötet kunde inte sparas.'; showMsg('Mötet är ändrat.', 'success', true); reloadMeeting(); return true; })
        });
    }

    function deadlineDialog(m) {
        formModal({
            title: 'Sista dag för motioner', wide: false, okLabel: 'Spara',
            html: `<p class="sv-help">Gäller årsmötet ${esc(svDate(m.meetingDate))}. Dagen räknas med. En motion som kommer senare tas emot och märks <em>inkom efter sista dag</em>; den stoppas inte.</p>
                <label class="sv-label" for="dlDate">Sista dag</label><input type="text" id="dlDate" class="form-control" value="${esc(m.motionDeadline || '')}" placeholder="Ingen sista dag">`,
            onOpen: () => datePick($('dlDate'), false, { maxDate: (m.meetingDate || '').slice(0, 10) }),
            onOk: () => post('BoardWork', 'SetMotionDeadline', { meetingId: m.id, deadline: $('dlDate').value.trim() })
                .then(r => {
                    if (!r.success) return r.message || 'Datumet kunde inte sparas.';
                    showMsg('Sista dag för motioner är sparad.', 'success', true);
                    if (parseHash().view === 'mote') reloadMeeting(); else route();
                    refreshCounts();
                    return true;
                })
        });
    }

    // ---- Närvaro och val ----
    function togglePresent(id, present) {
        const lbl = $('svAttStatus_' + id);
        post('BoardMeeting', 'SetAttendance', { attendeeId: id, attendanceStatus: present ? 'Närvarande' : 'Frånvarande' })
            .then(r => { if (r.success) { if (lbl) lbl.textContent = present ? 'Närvarande' : 'Frånvarande'; updateQuorum(); } });
    }
    function updateQuorum() {
        const card = $('svQuorum'); if (!card) return;
        const req = parseInt(card.dataset.required, 10) || 0, total = parseInt(card.dataset.total, 10) || 0;
        const present = card.querySelectorAll('.svAttChk:checked').length;
        const met = present >= req && req > 0;
        card.classList.toggle('sv-quorum-ok', met); card.classList.toggle('sv-quorum-no', !met);
        $('svQuorumMsg').innerHTML = quorumMsgHtml({ isMet: met, present, total, required: req });
    }
    function quorumMsgHtml(q) {
        return (q.isMet ? '<i class="bi bi-check-circle-fill text-success me-1"></i><strong>Styrelsen är beslutsför.</strong>'
            : '<i class="bi bi-exclamation-triangle-fill text-warning me-1"></i><strong>Styrelsen är inte beslutsför.</strong>')
            + `<span class="sv-help d-block">${q.present} av ${q.total} närvarande (minst ${q.required} krävs).</span>`;
    }
    function saveElection(ev, itemId, count) {
        const boxes = Array.from(document.querySelectorAll('.svElect_' + itemId));
        if (count === 1 && ev.target.checked) boxes.forEach(b => { if (b !== ev.target) b.checked = false; });
        else if (boxes.filter(b => b.checked).length > count) {
            ev.target.checked = false;
            showMsg('Välj högst ' + count + (count > 1 ? ' personer' : ' person') + '.', 'warning');
        }
        const ids = boxes.filter(b => b.checked).map(b => b.value);
        post('BoardMeeting', 'SaveAgendaElection', { agendaItemId: itemId, memberIds: ids.join(',') })
            .then(r => { if (r.success) flash($('ag_saved_' + itemId)); });
    }
    let elTimer = null;
    function elSearch(itemId, q) {
        clearTimeout(elTimer);
        const box = $('elres_' + itemId); if (!box) return;
        q = (q || '').trim();
        if (q.length < 2) { box.innerHTML = ''; return; }
        elTimer = setTimeout(() => {
            get('BoardMeeting', 'GetOwnerMembers', { meetingId: window._mId, q }).then(r => {
                if (!r.success) { box.innerHTML = ''; return; }
                box.innerHTML = (r.data || []).map(mm => `<button type="button" class="list-group-item list-group-item-action" data-el-add="${mm.id}">${esc(mm.name)}</button>`).join('')
                    || '<div class="sv-help px-2">Inga träffar.</div>';
                box.querySelectorAll('[data-el-add]').forEach(b => b.addEventListener('click', () => elAdd(itemId, parseInt(b.dataset.elAdd, 10))));
            });
        }, 300);
    }
    function elItem(itemId) { return (window._mAgenda || []).find(a => a.id === itemId); }
    function elAdd(itemId, memberId) {
        const it = elItem(itemId); if (!it) return;
        const curIds = (it.electedMemberIds || []).map(Number);
        if (curIds.includes(memberId)) return;
        const count = it.electionCount || 1;
        if (curIds.length >= count) { showMsg('Välj högst ' + count + (count > 1 ? ' personer' : ' person') + '.', 'warning'); return; }
        post('BoardMeeting', 'SaveAgendaElection', { agendaItemId: itemId, memberIds: curIds.concat(memberId).join(',') })
            .then(r => { if (r.success) reloadMeeting(); });
    }
    function elRemove(itemId, memberId) {
        const it = elItem(itemId); if (!it) return;
        const ids = (it.electedMemberIds || []).map(Number).filter(x => x !== memberId);
        post('BoardMeeting', 'SaveAgendaElection', { agendaItemId: itemId, memberIds: ids.join(',') })
            .then(r => { if (r.success) reloadMeeting(); });
    }

    // ---- Utdelning av märken och medaljer (ItemType "awards") ----
    // ⚠️ Statusen sätts PER RAD mot servern — två sekreterare kan ha punkten öppen samtidigt.
    function loadAwards(itemId, year, refresh) {
        const box = $('aw_body_' + itemId); if (!box) return;
        const yEl = $('aw_year_' + itemId);
        const y = year || (yEl ? yEl.value : '') || '';
        if (refresh) box.innerHTML = '<p class="sv-help mb-0">Hämtar listan…</p>';
        get('BoardMeeting', 'GetAgendaAwards', { agendaItemId: itemId, year: y, refresh: !!refresh }).then(r => {
            if (!r.success) { box.innerHTML = `<p class="text-danger mb-0">${esc(r.message || 'Listan kunde inte hämtas.')}</p>`; return; }
            if (r.supported === false) { box.innerHTML = `<p class="sv-help mb-0">${esc(r.message)}</p>`; return; }
            renderAwards(itemId, r);
        }).catch(() => { box.innerHTML = '<p class="text-danger mb-0">Fel vid inläsning.</p>'; });
    }
    function renderAwards(itemId, r) {
        const box = $('aw_body_' + itemId);
        const a = r.awards;
        if (!a || !a.rows || !a.rows.length) {
            let empty = '';
            (a && a.notes ? a.notes : []).forEach(n => { empty += `<div class="alert alert-warning py-2 px-3 small mb-2">${esc(n)}</div>`; });
            box.innerHTML = empty + `<p class="sv-help mb-0">Inga märken eller medaljer att dela ut för ${esc(a ? a.year : '')}.</p>`;
            return;
        }
        const locked = !!r.locked;
        let h = '';
        (a.notes || []).forEach(n => { h += `<div class="alert alert-warning py-2 px-3 small mb-2">${esc(n)}</div>`; });
        h += `<div class="mb-2 sv-help">Hämtad ${esc(a.capturedAt)} · ${a.recipientCount} mottagare · ${a.orderableCount} föremål${a.uncalledCount ? ` · <strong>${a.uncalledCount} inte upplast</strong>` : ''}</div>
            <div class="table-responsive"><table class="table table-sm align-middle mb-0"><tbody>`;
        let lastMember = null;
        a.rows.forEach((row, idx) => {
            const newMember = row.memberId !== lastMember; lastMember = row.memberId;
            h += `<tr${newMember ? ' style="border-top:2px solid var(--bs-border-color);"' : ''}>
                <td style="min-width:150px;">${newMember ? `<strong>${esc(row.name)}</strong>` : ''}</td>
                <td>${esc(row.item)}<div class="sv-help">${esc(row.group)}${row.detail ? ' · ' + esc(row.detail) : ''}</div>
                    ${row.orderable ? '' : '<span class="badge bg-secondary-subtle text-secondary">inget märke</span> '}
                    ${row.unverified ? '<span class="badge bg-warning-subtle text-warning-emphasis">ej granskad</span> ' : ''}
                    ${row.noLongerInLedger ? '<span class="badge bg-danger-subtle text-danger">finns inte längre i liggaren</span>' : ''}</td>
                <td class="text-end" style="white-space:nowrap;">`;
            if (locked) h += `<span class="badge ${row.status === 'Mottaget' ? 'bg-success' : row.status ? 'bg-secondary' : 'bg-light text-muted border'}">${esc(row.statusLabel)}</span>`;
            else h += '<span class="btn-group btn-group-sm sv-noprint">'
                + [['Mottaget', 'Mottaget', 'btn-success'], ['Franvarande', 'Ej närv.', 'btn-secondary'], ['Senare', 'Senare', 'btn-warning']].map(([v, l, cls]) => {
                    const on = (row.status || null) === v;
                    return `<button type="button" class="btn btn-sm ${on ? cls : 'btn-outline-secondary'}" data-aw-set="${idx}" data-aw-val="${on ? '' : v}">${esc(l)}</button>`;
                }).join('') + '</span>';
            h += '</td></tr>';
        });
        box.innerHTML = h + '</tbody></table></div>';
        box.querySelectorAll('[data-aw-set]').forEach(b => b.addEventListener('click', () => {
            const row = a.rows[parseInt(b.dataset.awSet, 10)]; if (!row) return;
            post('BoardMeeting', 'SetAgendaAwardStatus', { agendaItemId: itemId, memberId: row.memberId, group: row.group, item: row.item, status: b.dataset.awVal || '' })
                .then(rr => { if (!rr.success) { failMsg(rr, 'Kunde inte spara.'); return; } flash($('ag_saved_' + itemId)); loadAwards(itemId, null, false); });
        }));
    }

    // ---- Bilagor ----
    function linkChip(l, locked) {
        const icon = l.kind === 'document' ? 'bi-file-earmark-text' : l.kind === 'meeting' ? 'bi-calendar-event' : l.kind === 'valforslag' ? 'bi-person-check' : 'bi-link-45deg';
        let href = '#', extra = 'target="_blank" rel="noopener"';
        if (l.kind === 'document') href = '/umbraco/surface/Document/DownloadDocument?id=' + l.refId;
        else if (l.kind === 'meeting') { href = '#vy=mote&id=' + l.refId; extra = ''; }
        else if (l.kind === 'valforslag') href = `/styrelse/valforslag?type=${OWNER_TYPE}&id=${OWNER_ID}&year=${l.refId}`;
        else href = l.url || '#';
        const rm = locked ? '' : ` <a href="#" class="text-danger ms-1 sv-noprint" title="Ta bort bilagan" data-link-rm="${l.id}">✕</a>`;
        return `<div class="sv-help" style="margin:.1rem 0;"><i class="bi ${icon} me-1"></i><a href="${esc(href)}" ${extra}>${esc(l.label)}</a>${rm}</div>`;
    }
    document.addEventListener('click', e => {
        const a = e.target.closest('[data-link-rm]'); if (!a) return;
        e.preventDefault();
        post('BoardMeeting', 'RemoveAgendaLink', { linkId: parseInt(a.dataset.linkRm, 10) }).then(r => { if (r.success) reloadMeeting(); });
    });
    function chooseDialog(title, options, cb) {
        if (!options.length) { showMsg('Det finns inget att välja.', 'warning'); return; }
        formModal({
            title, wide: false,
            html: '<div class="list-group">' + options.map((o, i) => `<button type="button" class="list-group-item list-group-item-action" data-choose="${i}">${esc(o.label)}</button>`).join('') + '</div>',
            onOpen: el => el.querySelectorAll('[data-choose]').forEach(b => b.addEventListener('click', () => { closeForm(); cb(options[parseInt(b.dataset.choose, 10)]); }))
        });
    }
    function loadDocs() {
        if (svDocs) return Promise.resolve(svDocs);
        return get('Document', 'GetPublicDocuments', OWN()).then(r => { svDocs = (r.success && r.data) ? r.data : []; return svDocs; });
    }
    function attachDoc(itemId) {
        loadDocs().then(docs => {
            if (!docs.length) { showMsg(`Det finns inga dokument i ${IS_CLUB ? 'klubbens' : 'kretsens'} arkiv ännu. Lägg upp dem under fliken Dokument först.`, 'warning'); return; }
            chooseDialog('Välj dokument att bifoga', docs.map(d => ({ label: d.title + (d.fileName ? ' (' + d.fileName + ')' : ''), v: d })), o =>
                post('BoardMeeting', 'AddAgendaLink', { agendaItemId: itemId, kind: 'document', refId: o.v.id, url: '', label: o.v.title }).then(r => { if (r.success) reloadMeeting(); }));
        });
    }
    function attachMeeting(itemId) {
        get('BoardMeeting', 'GetMeetings', OWN()).then(r => {
            const opts = ((r.success && r.data) || []).filter(m => m.id !== window._mId).map(m => ({ label: m.title + ' (' + svDate(m.meetingDate) + ')', v: m }));
            if (!opts.length) { showMsg('Det finns inga andra möten att länka till.', 'warning'); return; }
            chooseDialog('Välj möte att länka', opts, o =>
                post('BoardMeeting', 'AddAgendaLink', { agendaItemId: itemId, kind: 'meeting', refId: o.v.id, url: '', label: o.v.title }).then(rr => { if (rr.success) reloadMeeting(); }));
        });
    }
    function attachValforslag(itemId) {
        const y = window._mYear || new Date().getFullYear();
        chooseDialog('Vilket års förslag?', [y, y - 1, y + 1].map(yr => ({ label: 'Valberedningens förslag ' + yr, v: yr })), o =>
            post('BoardMeeting', 'AddAgendaLink', { agendaItemId: itemId, kind: 'valforslag', refId: o.v, url: '', label: 'Valberedningens förslag ' + o.v }).then(r => { if (r.success) reloadMeeting(); }));
    }
    function attachUrl(itemId) {
        formModal({
            title: 'Bifoga länk', wide: false, okLabel: 'Bifoga',
            html: `<label class="sv-label" for="luLabel">Vad heter länken?</label><input type="text" id="luLabel" class="form-control mb-3" placeholder="t.ex. Verksamhetsberättelse 2025">
                <label class="sv-label" for="luUrl">Webbadress</label><input type="url" id="luUrl" class="form-control" placeholder="https://…">`,
            onOk: () => {
                const label = $('luLabel').value.trim(), url = $('luUrl').value.trim();
                if (!label || !url) return Promise.resolve('Fyll i både namn och webbadress.');
                return post('BoardMeeting', 'AddAgendaLink', { agendaItemId: itemId, kind: 'url', refId: '', url, label })
                    .then(r => { if (!r.success) return r.message || 'Länken kunde inte sparas.'; reloadMeeting(); return true; });
            }
        });
    }

    // ---- Kallelse ----
    function sendKallelse(mid) {
        const msg = $('kallelseMsg') ? $('kallelseMsg').value : '';
        get('BoardKallelse', 'GetKallelsePreview', { meetingId: mid }).then(async p => {
            if (!p.success) { failMsg(p, 'Kallelsen kunde inte förberedas.'); return; }
            if (p.count === 0) { showMsg('Inga mottagare med e-postadress hittades (' + esc(p.audience) + ').', 'warning'); return; }
            if (p.tooMany) { showMsg('För många mottagare (' + p.count + ') för ett direktutskick. Använd klubbens utskick under Medlemmar.', 'warning'); return; }
            if (!await svConfirm('Skicka kallelsen?', 'Kallelsen med dagordningen skickas till ' + p.count + ' mottagare (' + p.audience + ').', 'Skicka')) return;
            post('BoardKallelse', 'SendKallelse', { meetingId: mid, message: msg }).then(r => {
                if (r.success) { showMsg('Kallelsen skickades till ' + r.sent + ' mottagare' + (r.failed ? ' (' + r.failed + ' misslyckades)' : '') + '.', r.failed ? 'warning' : 'success', true); reloadMeeting(); }
                else failMsg(r, 'Utskicket misslyckades.');
            });
        });
    }

    // ---- Uppgifter ----
    function addAction(mid) {
        const desc = $('actDesc').value.trim(); if (!desc) { showMsg('Skriv vad som ska göras.', 'warning'); return; }
        post('BoardMeeting', 'AddAction', { ownerType: OWNER_TYPE, ownerId: OWNER_ID, meetingId: mid, agendaItemId: '', description: desc, assignedToMemberId: $('actWho').value || '', dueDate: $('actDue').value || '' })
            .then(r => { if (r.success) reloadMeeting(); else failMsg(r, 'Uppgiften kunde inte läggas till.'); });
    }
    async function removeAction(id) {
        if (!await svConfirm('Ta bort uppgiften?', 'Uppgiften tas bort från mötet och från uppgiftslistan.', 'Ta bort', true)) return;
        post('BoardMeeting', 'RemoveAction', { actionId: id }).then(r => { if (r.success) reloadMeeting(); });
    }

    // ---- Justering ----
    function justeringCard(m, j, agenda) {
        const signers = j.signers || [];
        const signerList = signers.map(s => `<div class="sv-person"><span><strong>${esc(s.memberName || '-')}</strong> <span class="sv-help">${esc(s.role)}</span></span>
            <span>${s.approved ? `<span class="text-success"><i class="bi bi-check-circle-fill me-1"></i>Justerat${s.approvedDate ? ' ' + esc(svDate(s.approvedDate)) : ''}${s.via === 'qr' ? ' (QR)' : ''}</span>` : '<span class="sv-help">Väntar</span>'}</span></div>`).join('');
        if (m.status === 'VantarJustering') {
            return `<div class="sv-card sv-noprint"><div class="sv-cardh"><h2>Justering pågår</h2>${pill('VantarJustering', (j.approvedCount || 0) + ' av ' + (j.totalSigners || 0) + ' har justerat')}</div>
                <p class="sv-help">Protokollet är låst. Ordförande, sekreterare och justerare godkänner det, på plats med QR-koden eller via en länk i e-post.</p>
                ${signerList || '<p class="sv-help">Inga justerare.</p>'}
                <div class="d-flex gap-2 flex-wrap mt-3">
                    ${j.canApprove ? '<button type="button" class="btn btn-primary" data-act="approve">Godkänn protokollet</button>' : ''}
                    <button type="button" class="btn btn-outline-secondary" data-act="qr"><i class="bi bi-qr-code me-1"></i>Visa QR-kod</button>
                    <button type="button" class="btn btn-outline-secondary" data-act="mailJust"><i class="bi bi-envelope me-1"></i>Skicka länk via e-post</button>
                </div><div id="justQr" class="mt-3"></div></div>`;
        }
        if (m.status === 'Justerat') {
            return `<div class="sv-card sv-noprint"><div class="sv-cardh"><h2>Justerat</h2>${pill('Justerat', 'Justerat')}</div>${signerList}
                <p class="sv-help mt-2 mb-0">Ärenden och motioner på mötet har fått besked om besluten.</p></div>`;
        }
        const byRole = r => signers.filter(s => s.role === r).map(s => esc(s.memberName || '')).filter(Boolean);
        const chair = byRole('Ordförande'), secr = byRole('Sekreterare'), adj = byRole('Justerare');
        const hasAdjusterItem = agenda.some(a => a.itemType === 'election' && a.electionRole === 'adjuster');
        const missingAdjuster = hasAdjusterItem && !adj.length;
        const line = (label, names, missing) => `<div class="sv-person"><span class="sv-help">${label}</span>
            <span>${names.length ? '<strong>' + names.join(', ') + '</strong>' : `<span class="text-warning-emphasis">${missing}</span>`}</span></div>`;
        const canSend = (chair.length || secr.length || adj.length) && !missingAdjuster;
        return `<div class="sv-card sv-noprint"><div class="sv-cardh"><h2>Klar med protokollet?</h2></div>
            <p class="sv-help">När protokollet är klart skickas det för justering. Då låses det och de som skriver under godkänner det digitalt.</p>
            <p class="mb-1">Protokollet skrivs under av:</p><div class="mb-3">
                ${line('Ordförande', chair, 'ingen vald')}${line('Sekreterare', secr, 'ingen vald')}
                ${hasAdjusterItem || adj.length ? line('Justerare', adj, 'ingen vald ännu — välj under punkten "Val av justerare"') : ''}</div>
            <button type="button" class="btn btn-primary" data-act="sendJust" ${canSend ? '' : 'disabled'}><i class="bi bi-send me-1"></i>Skicka för justering…</button>
            ${missingAdjuster ? '<p class="sv-help mt-2 mb-0">Välj justerare först.</p>' : ''}</div>`;
    }
    async function sendForJustering(mid) {
        if (!await svConfirm('Skicka protokollet för justering?', 'Protokollet låses för redigering tills justerarna godkänt det.', 'Skicka för justering')) return;
        post('BoardMeeting', 'SendForJustering', { meetingId: mid }).then(r => {
            if (r.success) { phaseByMeeting[mid] = 'efter'; showMsg('Protokollet är skickat för justering.', 'success', true); reloadMeeting(); }
            else failMsg(r, 'Protokollet kunde inte skickas.');
        });
    }
    async function approveProtokoll(mid) {
        if (!await svConfirm('Godkänn protokollet?', 'Det här är din digitala underskrift av protokollet.', 'Godkänn')) return;
        post('BoardMeeting', 'ApproveProtokoll', { meetingId: mid }).then(r => {
            if (!r.success) { failMsg(r, 'Protokollet kunde inte justeras.'); return; }
            showMsg(r.locked ? 'Protokollet är justerat.' + (r.notified ? ` ${r.notified} besked om beslut skickades.` : '') : `Du har godkänt protokollet (${r.approved} av ${r.total}).`, 'success', true);
            reloadMeeting(); refreshCounts();
        });
    }
    function showJusteringQr(mid) {
        const el = $('justQr'); if (!el) return;
        el.innerHTML = `<p class="sv-help mb-1">Låt justerarna skanna koden med mobilen:</p>
            <img src="/umbraco/surface/BoardMeeting/GetJusteringQr?meetingId=${mid}&_=${Date.now()}" alt="QR-kod för justering" style="width:240px;height:240px;border:1px solid var(--bs-border-color);border-radius:.5rem;background:#fff;">`;
    }
    async function sendJusteringMail(mid) {
        if (!await svConfirm('Skicka justeringslänk?', 'En länk skickas via e-post till dem som inte har justerat.', 'Skicka')) return;
        post('BoardMeeting', 'SendJusteringEmails', { meetingId: mid }).then(r => {
            if (r.success) showMsg('Länken skickades till ' + (r.sent || 0) + ' person(er).'); else failMsg(r, 'Länken kunde inte skickas.');
        });
    }
    async function reopenJustering(mid) {
        if (!await svConfirm('Återöppna protokollet?', 'Protokollet kan redigeras igen och gjorda godkännanden nollställs.', 'Återöppna')) return;
        post('BoardMeeting', 'ReopenJustering', { meetingId: mid }).then(r => { if (r.success) { phaseByMeeting[mid] = 'under'; reloadMeeting(); } else failMsg(r); });
    }

    // Klubbens beslut → motion till kretsen, direkt från protokollet.
    document.addEventListener('click', e => {
        const b = e.target.closest('[data-krets-from]'); if (!b) return;
        const it = (window._mAgenda || []).find(a => a.id === parseInt(b.dataset.kretsFrom, 10));
        kretsMotionDialog(it ? it.id : 0, it ? it.heading : '', it ? it.decision : '');
    });

    // =====================================================================
    //  MÖTESMALLAR
    // =====================================================================
    let svMallType = null, svMallItems = [];
    function viewMallar() {
        setHead('Mötesmallar', `Dagordningen som föreslås när ni skapar ett möte. Ändringarna gäller bara er ${IS_CLUB ? 'klubb' : 'krets'}.`, '<a href="#vy=moten">← Möten</a>');
        if (!CAN_MANAGE) { body().innerHTML = errBox('Mötesmallarna ändras av administratören.'); return; }
        const start = () => {
            if (!svMeetingTypes.length) { body().innerHTML = errBox('Mötestyperna kunde inte hämtas. Ladda om sidan.'); return; }
            body().innerHTML = `<div class="sv-card"><label class="sv-label" for="svMallType">Mötestyp</label>
                <select id="svMallType" class="form-select" style="max-width:420px;">${svMeetingTypes.map(t => `<option value="${esc(t.key)}">${esc(t.label)}</option>`).join('')}</select>
                <div id="svMallStatus" class="sv-help mt-1"></div></div><div id="svMallEditor"></div>`;
            $('svMallType').addEventListener('change', e => svMallLoad(e.target.value));
            svMallLoad(svMeetingTypes[0].key);
        };
        catalogReady.then(start);
    }
    function svMallLoad(typeKey) {
        svMallType = typeKey;
        $('svMallEditor').innerHTML = spinner();
        get('BoardMeetingTemplate', 'GetTemplate', { ownerType: OWNER_TYPE, ownerId: OWNER_ID, meetingTypeKey: typeKey }).then(r => {
            if (!r.success) { $('svMallEditor').innerHTML = errBox(r.message || 'Fel'); return; }
            svMallItems = (r.items || []).map(i => ({ itemType: i.itemType, heading: i.heading, electionRole: i.electionRole || '', electionCount: i.electionCount || 1, electionSource: i.electionSource || 'attendees' }));
            $('svMallStatus').innerHTML = r.hasSaved ? 'Ni använder en egen anpassad mall.' : 'Ni använder standardmallen.';
            renderMallEditor();
        });
    }
    function renderMallEditor() {
        let h = '<div class="sv-card">';
        if (!svMallItems.length) h += '<p class="sv-help">Inga punkter. Lägg till nedan.</p>';
        svMallItems.forEach((it, idx) => {
            // ⚠️ "awards" måste finnas som val — annars gör nästa sparning tyst om en utdelningspunkt till en anteckning.
            const typeSel = `<select class="form-select form-select-sm d-inline-block" style="width:auto;" data-mall-type="${idx}" aria-label="Typ av punkt">
                ${[['note', 'Anteckningar'], ['text', 'Beslut'], ['election', 'Personval'], ['awards', 'Utdelning av märken']].map(([v, l]) => `<option value="${v}" ${it.itemType === v ? 'selected' : ''}>${l}</option>`).join('')}</select>`;
            let electOpts = '';
            if (it.itemType === 'election') {
                electOpts = `<div class="w-100 mt-2 ps-4 d-flex gap-2 flex-wrap align-items-center">
                    <label class="sv-help mb-0">Antal</label><input type="number" min="1" max="9" class="form-control form-control-sm" style="width:72px;" value="${it.electionCount || 1}" data-mall-count="${idx}">
                    <label class="sv-help mb-0">Välj bland</label><select class="form-select form-select-sm d-inline-block" style="width:auto;" data-mall-source="${idx}">
                        <option value="attendees" ${it.electionSource !== 'members' ? 'selected' : ''}>Närvarande</option><option value="members" ${it.electionSource === 'members' ? 'selected' : ''}>Alla medlemmar</option></select>
                    <label class="sv-help mb-0">Roll på protokollet</label><select class="form-select form-select-sm d-inline-block" style="width:auto;" data-mall-role="${idx}">
                        ${[['', '— (ingen)'], ['chairman', 'Ordförande'], ['secretary', 'Sekreterare'], ['adjuster', 'Justerare']].map(([v, l]) => `<option value="${v}" ${(it.electionRole || '') === v ? 'selected' : ''}>${l}</option>`).join('')}</select></div>`;
            }
            h += `<div class="sv-person" style="flex-wrap:wrap;"><span class="d-flex align-items-center gap-2 flex-wrap" style="flex:1 1 auto;">
                    <strong>${idx + 1}.</strong><input type="text" class="form-control d-inline-block" style="flex:1 1 240px;max-width:460px;" value="${esc(it.heading)}" data-mall-name="${idx}" aria-label="Rubrik">${typeSel}</span>
                <span class="d-flex gap-1"><button type="button" class="btn btn-sm btn-outline-secondary" data-mall-move="-1" data-idx="${idx}" ${idx === 0 ? 'disabled' : ''} aria-label="Flytta upp"><i class="bi bi-arrow-up"></i></button>
                    <button type="button" class="btn btn-sm btn-outline-secondary" data-mall-move="1" data-idx="${idx}" ${idx === svMallItems.length - 1 ? 'disabled' : ''} aria-label="Flytta ner"><i class="bi bi-arrow-down"></i></button>
                    <button type="button" class="btn btn-sm btn-outline-danger" data-mall-rm="${idx}">Ta bort</button></span>${electOpts}</div>`;
        });
        h += `<div class="row g-2 mt-2" style="max-width:640px;"><div class="col-12 col-md-7"><select id="svMallAddType" class="form-select" aria-label="Punkt att lägga till">${agendaCatalogOptions()}</select></div>
            <div class="col-12 col-md-5 d-none" id="svMallAddCustomWrap"><input type="text" id="svMallAddHeading" class="form-control" placeholder="Rubrik"></div>
            <div class="col-12"><button type="button" class="btn btn-outline-primary" id="svMallAdd"><i class="bi bi-plus-lg me-1"></i>Lägg till punkt</button></div></div></div>
            <div class="d-flex gap-2 flex-wrap"><button type="button" class="btn btn-primary" id="svMallSave">Spara mallen</button>
            <button type="button" class="btn btn-outline-secondary" id="svMallReset">Återställ till standard…</button></div>`;
        const c = $('svMallEditor'); c.innerHTML = h;
        c.querySelectorAll('[data-mall-name]').forEach(i => i.addEventListener('change', () => { svMallItems[+i.dataset.mallName].heading = i.value; }));
        c.querySelectorAll('[data-mall-type]').forEach(s => s.addEventListener('change', () => {
            const it = svMallItems[+s.dataset.mallType]; it.itemType = s.value;
            if (s.value !== 'election') { it.electionRole = ''; it.electionCount = 1; it.electionSource = 'attendees'; }
            renderMallEditor();
        }));
        c.querySelectorAll('[data-mall-count]').forEach(i => i.addEventListener('change', () => { svMallItems[+i.dataset.mallCount].electionCount = Math.max(1, parseInt(i.value, 10) || 1); }));
        c.querySelectorAll('[data-mall-source]').forEach(s => s.addEventListener('change', () => { svMallItems[+s.dataset.mallSource].electionSource = s.value === 'members' ? 'members' : 'attendees'; }));
        c.querySelectorAll('[data-mall-role]').forEach(s => s.addEventListener('change', () => { svMallItems[+s.dataset.mallRole].electionRole = s.value; }));
        c.querySelectorAll('[data-mall-move]').forEach(b => b.addEventListener('click', () => {
            const idx = +b.dataset.idx, j2 = idx + +b.dataset.mallMove; if (j2 < 0 || j2 >= svMallItems.length) return;
            [svMallItems[idx], svMallItems[j2]] = [svMallItems[j2], svMallItems[idx]]; renderMallEditor();
        }));
        c.querySelectorAll('[data-mall-rm]').forEach(b => b.addEventListener('click', () => { svMallItems.splice(+b.dataset.mallRm, 1); renderMallEditor(); }));
        $('svMallAddType').addEventListener('change', () => $('svMallAddCustomWrap').classList.toggle('d-none', $('svMallAddType').value !== 'ovrigt'));
        $('svMallAdd').addEventListener('click', () => {
            const sel = $('svMallAddType'); const key = sel.value; if (!key) return;
            const opt = sel.options[sel.selectedIndex];
            let heading = opt.dataset.heading || opt.textContent;
            if (key === 'ovrigt') { heading = $('svMallAddHeading').value.trim(); if (!heading) { showMsg('Skriv en rubrik.', 'warning'); return; } }
            svMallItems.push({ itemType: opt.dataset.type || 'text', heading, electionRole: opt.dataset.role || '', electionCount: parseInt(opt.dataset.count || '1', 10), electionSource: opt.dataset.source || 'attendees' });
            renderMallEditor();
        });
        $('svMallSave').addEventListener('click', () => post('BoardMeetingTemplate', 'SaveTemplate', { ownerType: OWNER_TYPE, ownerId: OWNER_ID, meetingTypeKey: svMallType, itemsJson: JSON.stringify(svMallItems) })
            .then(r => { if (r.success) { $('svMallStatus').innerHTML = '<i class="bi bi-check-lg text-success me-1"></i>Mallen är sparad. Ni använder en egen anpassad mall.'; } else failMsg(r, 'Mallen kunde inte sparas.'); }));
        $('svMallReset').addEventListener('click', async () => {
            if (!await svConfirm('Återställ mallen?', 'Era anpassningar för den här mötestypen tas bort och standardmallen används igen.', 'Återställ', true)) return;
            post('BoardMeetingTemplate', 'ResetTemplate', { ownerType: OWNER_TYPE, ownerId: OWNER_ID, meetingTypeKey: svMallType })
                .then(r => { if (r.success) svMallLoad(svMallType); else failMsg(r); });
        });
    }

    // =====================================================================
    //  ÄRENDEN — kön
    // =====================================================================
    let isFilter = 'all', isData = null;
    function viewArenden() {
        setHead('Ärenden', CAN_PLACE
            ? 'Ärenden som ledamöterna vill ta upp. Placera dem under en punkt på ett kommande möte.'
            : 'Ärenden du vill ta upp på ett styrelsemöte. Sekreteraren eller ordföranden placerar dem på dagordningen.');
        setActions([
            { label: 'Anmäl ett ärende…', icon: 'bi-inbox', fn: () => issueDialog(null) },
            CAN_PLACE ? { label: 'Lägg in en inkommen skrivelse…', icon: 'bi-envelope', fn: () => issueDialog(null, true) } : null
        ]);
        get('BoardWork', 'GetIssues', OWN()).then(r => {
            if (!r.success) { body().innerHTML = errBox(r.message || 'Ärendena kunde inte hämtas.'); return; }
            isData = r;
            renderIssues();
            // Direktlänk från klubbsidans styrelsekort: #vy=arenden&ny=1 öppnar dialogen en gång.
            if (new URLSearchParams(location.hash.replace(/^#/, '')).get('ny') === '1') {
                history.replaceState(null, '', location.pathname + location.search + '#vy=arenden');
                issueDialog(null);
            }
        }).catch(() => body().innerHTML = errBox('Ärendena kunde inte hämtas.'));
    }
    function renderIssues() {
        const r = isData, all = r.issues || [];
        let h = newIssueBox(0);
        if (r.canPlace) {
            const waiting = all.filter(i => i.state === 'Waiting').reverse();   // äldst först
            // Rälsens antal följer listan som visas — annars kan de två säga olika saker en stund.
            counts.arenden = waiting.length; renderRail('arenden');
            if (waiting.length) {
                h += `<div class="sv-card sv-attn"><div class="sv-cardh"><h2>Att placera (${waiting.length})</h2><span class="sv-help">Äldst först</span></div>
                    <div class="table-responsive"><table class="table sv-table align-middle mb-0 bg-transparent"><thead><tr><th>Ärende</th><th class="d-none d-md-table-cell">Anmält av</th><th class="d-none d-md-table-cell">Önskat möte</th><th class="text-end"></th></tr></thead><tbody>`;
                waiting.forEach(i => {
                    h += `<tr><td><strong>${esc(i.title)}</strong><div class="sv-help">${esc(i.kindLabel)} · ${esc(svDate(i.submittedDate))}<span class="d-md-none"> · ${esc(i.submittedBy)}</span></div></td>
                        <td class="d-none d-md-table-cell">${esc(i.sourceKind === 'Skrivelse' ? 'Inkommen skrivelse' : i.sourceKind === 'Motion' ? 'Motionerna' : i.submittedBy)}</td>
                        <td class="d-none d-md-table-cell">${esc(i.wishMeeting ? svDate(i.wishMeeting.slice(-10)) : 'Nästa möte')}</td>
                        <td class="text-end text-nowrap"><button type="button" class="btn btn-sm btn-primary" data-place="${i.id}">Placera…</button>
                        ${rowMenu([{ label: 'Visa ärendet', fn: () => issueDetail(i) }, { label: 'Ändra…', fn: () => issueDialog(i) }, { divider: true }, { label: 'Avvisa…', danger: true, fn: () => rejectDialog(i) }])}</td></tr>`;
                });
                h += '</tbody></table></div></div>';
            } else {
                h += '<div class="sv-todo done mb-3"><span class="n">✓</span><span class="t">Inga ärenden väntar på att placeras.</span></div>';
            }
        }
        const rest = r.canPlace ? all.filter(i => i.state !== 'Waiting') : all;
        const groups = { all: rest, Placed: rest.filter(i => i.state === 'Placed'), Handled: rest.filter(i => i.state === 'Handled'), closed: rest.filter(i => i.state === 'Withdrawn' || i.state === 'Rejected'), Waiting: rest.filter(i => i.state === 'Waiting') };
        const shown = groups[isFilter] || rest;
        h += `<div class="d-flex flex-wrap gap-2 align-items-center mb-2 sv-help"><span>${r.canPlace ? 'Placerade och behandlade' : 'Dina ärenden'}</span>
            <select id="isFilter" class="form-select form-select-sm" style="width:auto;">
                <option value="all" ${isFilter === 'all' ? 'selected' : ''}>Alla (${rest.length})</option>
                ${r.canPlace ? '' : `<option value="Waiting" ${isFilter === 'Waiting' ? 'selected' : ''}>Väntar (${groups.Waiting.length})</option>`}
                <option value="Placed" ${isFilter === 'Placed' ? 'selected' : ''}>Placerade (${groups.Placed.length})</option>
                <option value="Handled" ${isFilter === 'Handled' ? 'selected' : ''}>Behandlade (${groups.Handled.length})</option>
                <option value="closed" ${isFilter === 'closed' ? 'selected' : ''}>Återkallade och avvisade (${groups.closed.length})</option></select></div>`;
        if (!shown.length) {
            h += `<div class="sv-card"><p class="sv-help mb-0">${r.canPlace ? 'Inga ärenden här.' : 'Du har inte anmält några ärenden. Välj <strong>Anmäl ett ärende</strong> under Åtgärder.'}</p></div>`;
        } else {
            h += '<div class="sv-card p-0"><div class="table-responsive"><table class="table sv-table align-middle mb-0"><thead><tr><th class="ps-3">Ärende</th><th>Läge</th><th class="text-end pe-3"></th></tr></thead><tbody>';
            shown.forEach(i => {
                const where = i.placement ? `<div class="sv-help">${esc(i.placement.meetingTitle)} ${esc(svDate(i.placement.meetingDate))}, ${esc(i.placement.paragraph)}</div>` : '';
                const items = [{ label: 'Visa ärendet', fn: () => issueDetail(i) }];
                if (i.placement) items.push({ label: 'Öppna mötet', fn: () => go('mote', i.placement.meetingId, 'forbered') });
                if (r.canPlace && i.state === 'Placed') {
                    items.push({ label: 'Flytta till ett annat möte…', fn: () => placeIssueDialog(i.id, 0) });
                    items.push({ label: 'Ta tillbaka till kön', fn: () => unplaceIssue(i) });
                }
                if (!r.canPlace && i.state === 'Waiting') {
                    items.push({ label: 'Ändra…', fn: () => issueDialog(i) });
                    items.push({ divider: true });
                    items.push({ label: 'Återkalla…', danger: true, fn: () => withdrawIssue(i) });
                }
                h += `<tr><td class="ps-3"><strong>${esc(i.title)}</strong><div class="sv-help">${esc(i.kindLabel)} · ${r.canPlace ? 'anmält av ' + esc(i.submittedBy) + ' · ' : ''}${esc(svDate(i.submittedDate))}</div></td>
                    <td>${pill(i.state, i.state === 'Placed' && i.placement ? 'Placerad ' + i.placement.paragraph : i.stateLabel)}${where}</td>
                    <td class="text-end pe-3">${rowMenu(items)}</td></tr>`;
            });
            h += '</tbody></table></div></div>';
        }
        body().innerHTML = h;
        $('isFilter').addEventListener('change', e => { isFilter = e.target.value; renderIssues(); });
        body().querySelectorAll('[data-place]').forEach(b => b.addEventListener('click', () => placeIssueDialog(parseInt(b.dataset.place, 10), 0)));
    }

    function issueDialog(issue, letter) {
        const wish = (isData && isData.wishMeetings) || null;
        const meetingsP = wish ? Promise.resolve(wish) : get('BoardWork', 'GetIssues', OWN()).then(r => (isData = r).wishMeetings || []);
        meetingsP.then(meetings => {
            const k = issue ? issue.kind : 'Beslut';
            formModal({
                title: issue ? 'Ändra ärendet' : letter ? 'Lägg in en inkommen skrivelse' : 'Anmäl ett ärende',
                okLabel: issue ? 'Spara' : letter ? 'Lägg i kön' : 'Anmäl ärendet',
                html: `${letter ? '<p class="sv-help">En skrivelse som kommit till föreningen, t.ex. ett brev eller ett mejl. Den läggs i kön och placeras som andra ärenden.</p>' : ''}
                    <label class="sv-label" for="isTitle">Rubrik</label><input type="text" id="isTitle" class="form-control mb-3" maxlength="300" value="${esc(issue ? issue.title : '')}">
                    <label class="sv-label" for="isBody">Underlag</label><textarea id="isBody" class="form-control mb-1" rows="4">${esc(issue ? issue.body || '' : '')}</textarea>
                    <div class="sv-help mb-3">Skriv det styrelsen behöver veta. Dokument bifogas på mötet när ärendet är placerat.</div>
                    <span class="sv-label">Vad gäller det?</span>
                    ${[['Beslut', 'Ett beslut'], ['Information', 'Information till styrelsen'], ['Diskussion', 'En diskussion']].map(([v, l]) =>
                        `<div class="form-check"><input class="form-check-input" type="radio" name="isKind" id="isKind_${v}" value="${v}" ${k === v ? 'checked' : ''}><label class="form-check-label" for="isKind_${v}">${l}</label></div>`).join('')}
                    <label class="sv-label mt-3" for="isWish">Vilket möte?</label>
                    <select id="isWish" class="form-select"><option value="">Nästa möte</option>${meetings.map(m => `<option value="${m.id}" ${issue && issue.wishMeetingId === m.id ? 'selected' : ''}>${esc(m.title)} ${esc(svDate(m.meetingDate))}</option>`).join('')}</select>
                    <div class="sv-help mt-1">Sekreteraren bestämmer var ärendet hamnar${issue || letter ? '' : ', och du får besked'}.</div>`,
                onOk: () => {
                    const title = $('isTitle').value.trim();
                    if (!title) return Promise.resolve('Skriv en rubrik.');
                    const p = { title, body: $('isBody').value, kind: (document.querySelector('input[name="isKind"]:checked') || {}).value || 'Beslut', wishMeetingId: $('isWish').value || '' };
                    const call = issue ? post('BoardWork', 'UpdateIssue', Object.assign({ issueId: issue.id }, p))
                        : post('BoardWork', 'CreateIssue', Object.assign(OWN(), p, { sourceKind: letter ? 'Skrivelse' : 'Ledamot' }));
                    return call.then(r => {
                        if (!r.success) return r.message || 'Ärendet kunde inte sparas.';
                        showMsg(issue ? 'Ärendet är ändrat.' : letter ? 'Skrivelsen ligger i kön.'
                            : 'Ärendet är anmält.' + (r.notified ? ' Sekreteraren och ordföranden har fått besked.' : ''), 'success', true);
                        refreshCounts();
                        if (parseHash().view === 'arenden') route(); else go('arenden');
                        return true;
                    });
                }
            });
        });
    }

    function issueDetail(i) {
        formModal({
            title: i.title, wide: true,
            html: `<p class="mb-2">${pill(i.state, i.stateLabel)} <span class="sv-help ms-2">${esc(i.kindLabel)} · anmält av ${esc(i.submittedBy)} ${esc(svDate(i.submittedDate))}</span></p>
                ${i.body ? `<div class="mb-3" style="white-space:pre-wrap;">${esc(i.body)}</div>` : '<p class="sv-help">Inget underlag.</p>'}
                ${i.placement ? `<p class="mb-1"><strong>Placering:</strong> ${esc(i.placement.meetingTitle)} ${esc(svDate(i.placement.meetingDate))}, ${esc(i.placement.paragraph)}</p>` : ''}
                ${i.placement && i.placement.decision ? `<p class="mb-1"><strong>Beslut:</strong> <span style="white-space:pre-wrap;">${esc(i.placement.decision)}</span></p>` : ''}
                ${i.closedReason ? `<p class="mb-1"><strong>Skäl:</strong> ${esc(i.closedReason)}</p>` : ''}`
        });
    }

    function placeIssueDialog(issueId, meetingId) {
        const issue = ((isData && isData.issues) || []).find(x => x.id === issueId) || { id: issueId, title: '' };
        placeDialog({
            title: 'Placera ärendet' + (issue.title ? ': ' + issue.title : ''),
            intro: issue.submittedBy ? `Anmält av ${esc(issue.submittedBy)} · ${esc(issue.kindLabel || '')}` : '',
            meetingId, annualOnly: false, notifyName: issue.submittedBy,
            onPlace: (mid, parent, notify) => post('BoardWork', 'PlaceIssue', { issueId, meetingId: mid, parentItemId: parent || '', notify: notify ? '1' : '0' })
                .then(r => {
                    if (!r.success) return r.message || 'Ärendet kunde inte placeras.';
                    showMsg(`Placerat: ärendet ligger nu som <strong>${esc(r.paragraph)}</strong> på ${esc(r.meetingTitle)}.${r.notified ? ' Den som anmälde det har fått besked.' : ''}`, 'success', true);
                    refreshCounts();
                    const v = parseHash().view;
                    if (v === 'mote') reloadMeeting(); else route();
                    return true;
                })
        });
    }
    async function unplaceIssue(i) {
        if (!await svConfirm('Ta tillbaka till kön?', `${i.title} tas bort från dagordningen och väntar på placering igen.`, 'Ta tillbaka')) return;
        post('BoardWork', 'UnplaceIssue', { issueId: i.id }).then(r => { if (r.success) { refreshCounts(); route(); } else failMsg(r); });
    }
    async function withdrawIssue(i) {
        if (!await svConfirm('Återkalla ärendet?', `${i.title} tas bort ur kön.`, 'Återkalla', true)) return;
        post('BoardWork', 'WithdrawIssue', { issueId: i.id }).then(r => { if (r.success) { showMsg('Ärendet är återkallat.', 'success', true); route(); } else failMsg(r); });
    }
    function rejectDialog(i) {
        formModal({
            title: 'Avvisa ärendet', okLabel: 'Avvisa', okClass: 'btn-danger', wide: false,
            html: `<p><strong>${esc(i.title)}</strong> tas inte upp på något möte.</p>
                <label class="sv-label" for="rjReason">Skäl</label><textarea id="rjReason" class="form-control mb-2" rows="3"></textarea>
                <div class="form-check"><input class="form-check-input" type="checkbox" id="rjNotify" checked><label class="form-check-label" for="rjNotify">Skicka skälet till ${esc(i.submittedBy || 'den som anmälde ärendet')}</label></div>`,
            onOk: () => {
                const reason = $('rjReason').value.trim();
                if (!reason) return Promise.resolve('Skriv ett skäl. Den som anmälde ärendet får läsa det.');
                return post('BoardWork', 'RejectIssue', { issueId: i.id, reason, notify: $('rjNotify').checked ? '1' : '0' })
                    .then(r => { if (!r.success) return r.message || 'Ärendet kunde inte avvisas.'; showMsg('Ärendet är avvisat.' + (r.notified ? ' Den som anmälde det har fått skälet.' : ''), 'success', true); refreshCounts(); route(); return true; });
            }
        });
    }

    // Placeringsdialogen — gemensam för ärenden och motioner.
    function placeDialog({ title, intro, meetingId, annualOnly, notifyName, onPlace }) {
        get('BoardWork', 'GetPlaceTargets', Object.assign(OWN(), { annualOnly: annualOnly ? '1' : '0' })).then(r => {
            if (!r.success) { failMsg(r); return; }
            const meetings = r.meetings || [];
            if (!meetings.length) {
                showMsg(annualOnly ? 'Det finns inget kommande årsmöte att placera på. Lägg in årsmötet under Möten först.'
                    : 'Det finns inget kommande möte att placera på. Skapa ett möte under Möten först.', 'warning');
                return;
            }
            const pre = meetings.find(m => m.id === meetingId) || meetings[0];
            const guess = items => {
                const want = annualOnly ? /motion/i : /beslutsärenden/i;
                return (items.find(x => want.test(x.heading)) || {}).id || 0;
            };
            formModal({
                title, okLabel: 'Placera', wide: true,
                html: `${intro ? `<div class="alert alert-info py-2">${intro}</div>` : ''}
                    <label class="sv-label" for="plMeeting">Möte</label>
                    <select id="plMeeting" class="form-select mb-3">${meetings.map(m => `<option value="${m.id}" ${m.id === pre.id ? 'selected' : ''}>${esc(m.title)} — ${esc(svDate(m.meetingDate))}</option>`).join('')}</select>
                    <span class="sv-label">Under punkt</span><div id="plItems" class="mb-2"></div>
                    <div class="sv-help mb-2" id="plPreview"></div>
                    ${notifyName ? `<div class="form-check"><input class="form-check-input" type="checkbox" id="plNotify" checked><label class="form-check-label" for="plNotify">Meddela ${esc(notifyName)} var ärendet placerats</label></div>` : ''}`,
                onOpen: () => {
                    const draw = () => {
                        const m = meetings.find(x => x.id === parseInt($('plMeeting').value, 10));
                        const g = guess(m.items);
                        $('plItems').innerHTML = m.items.map(it => `<div class="form-check"><input class="form-check-input" type="radio" name="plParent" id="plP_${it.id}" value="${it.id}" ${it.id === g ? 'checked' : ''}>
                            <label class="form-check-label" for="plP_${it.id}">${esc(it.label)} ${esc(it.heading)}</label></div>`).join('')
                            + `<div class="form-check"><input class="form-check-input" type="radio" name="plParent" id="plP_0" value="0" ${g ? '' : 'checked'}>
                            <label class="form-check-label" for="plP_0">Som en egen punkt, före mötets avslutande</label></div>`;
                        const prev = () => {
                            const v = parseInt((document.querySelector('input[name="plParent"]:checked') || {}).value || '0', 10);
                            const it = m.items.find(x => x.id === v);
                            $('plPreview').textContent = it ? `Blir en underpunkt till ${it.label} ${it.heading}.` : 'Blir en egen punkt på dagordningen.';
                        };
                        $('plItems').querySelectorAll('input').forEach(i => i.addEventListener('change', prev));
                        prev();
                    };
                    $('plMeeting').addEventListener('change', draw);
                    draw();
                },
                onOk: () => onPlace(parseInt($('plMeeting').value, 10),
                    parseInt((document.querySelector('input[name="plParent"]:checked') || {}).value || '0', 10),
                    $('plNotify') ? $('plNotify').checked : false)
            });
        });
    }

    // =====================================================================
    //  MOTIONER
    // =====================================================================
    let moData = null;
    function viewMotioner() {
        get('BoardWork', 'GetBoardMotions', OWN()).then(r => {
            if (!r.success) { setHead('Motioner', ''); body().innerHTML = errBox(r.message || 'Motionerna kunde inte hämtas.'); return; }
            moData = r;
            renderMotions();
        }).catch(() => body().innerHTML = errBox('Motionerna kunde inte hämtas.'));
    }
    function renderMotions() {
        const r = moData, a = r.annualMeeting;
        setHead('Motioner', a ? `Till årsmötet ${esc(svDate(a.meetingDate.slice(0, 10)))} · sista dag för motioner ${a.motionDeadline ? esc(svDate(a.motionDeadline)) : '<em>inte satt</em>'}`
            : 'Motioner till årsmötet. Inget kommande årsmöte är inlagt under Möten.');
        setActions([
            { header: 'Till årsmötet' },
            r.canPlace && a ? { label: 'Lägg motionerna på årsmötets dagordning', icon: 'bi-list-ol', fn: placeAllMotions } : null,
            r.canPlace ? { label: 'Lägg in en motion som kommit på papper…', icon: 'bi-file-earmark-plus', fn: paperMotionDialog } : null,
            r.canPlace && a ? { label: 'Sista dag för motioner…', icon: 'bi-calendar-check', fn: () => deadlineDialog({ id: a.id, meetingDate: a.meetingDate, motionDeadline: a.motionDeadline }) } : null,
            { label: 'Öppna medlemmarnas motionssida', icon: 'bi-box-arrow-up-right', fn: () => window.open(r.motionPageUrl, '_blank') },
            r.isClub ? { divider: true } : null,
            r.isClub ? { header: 'Till kretsen' } : null,
            r.isClub ? { label: 'Skicka en motion till kretsen…', icon: 'bi-send', fn: () => kretsMotionDialog(0, '', '') } : null
        ]);
        const motions = r.motions || [];
        let h = '';
        if (!a) h += `<div class="sv-todo"><span class="n">!</span><span class="t">Inget kommande årsmöte är inlagt. Medlemmarna kan ändå lämna motioner; de knyts till årsmötet när det läggs in.</span><button type="button" class="btn btn-sm btn-outline-secondary" data-sv-go="moten">Möten</button></div>`;
        else if (r.canPlace && !a.motionDeadline) h += `<div class="sv-todo"><span class="n">!</span><span class="t">Årsmötet har ingen sista dag för motioner. Medlemmarna ser dagen på motionssidan.</span><button type="button" class="btn btn-sm btn-outline-secondary" id="moSetDl">Sätt dag…</button></div>`;

        const noOpinion = motions.filter(m => m.state === 'Received');
        if (r.canPlace && noOpinion.length) {
            h += `<div class="sv-card sv-attn"><div class="sv-cardh"><h2>Saknar styrelsens yttrande (${noOpinion.length})</h2><span class="sv-help">Yttrandet följer med årsmöteshandlingarna</span></div>`;
            noOpinion.forEach(m => {
                h += `<div class="sv-person"><span><strong>${esc(m.number)} ${esc(m.title)}</strong><span class="sv-help d-block">${esc(m.motionerName)} · inkom ${esc(svDate(m.submittedDate))}${m.isLate ? ' · <span class="text-danger">efter sista dag</span>' : ''}</span></span>
                    <button type="button" class="btn btn-sm btn-primary" data-opinion="${m.id}">Skriv yttrande…</button></div>`;
            });
            h += '</div>';
        }

        h += '<div class="sv-card p-0"><div class="sv-cardh px-3 pt-3"><h2>' + (r.isClub ? 'Motioner till årsmötet' : 'Motioner till kretsårsmötet') + '</h2></div>';
        if (!motions.length) h += `<p class="sv-help px-3 pb-3 mb-0">Inga motioner har kommit in. Medlemmarna lämnar motioner på <a href="${esc(r.motionPageUrl)}" target="_blank" rel="noopener">motionssidan</a>, som de når från ${r.isClub ? 'klubbsidan' : 'kretssidan'}.</p>`;
        else {
            h += '<div class="table-responsive"><table class="table sv-table align-middle mb-0"><thead><tr><th class="ps-3">Nr</th><th>Motion</th><th>Läge</th><th class="text-end pe-3"></th></tr></thead><tbody>';
            motions.forEach(m => {
                const items = [{ label: 'Visa motionen', fn: () => motionDetail(m) }];
                if (r.canPlace && m.state !== 'Withdrawn' && m.state !== 'Decided') {
                    items.push({ label: m.boardProposal ? 'Ändra yttrandet…' : 'Skriv yttrande…', fn: () => opinionDialog(m) });
                    items.push({ label: 'Ta upp på ett styrelsemöte', fn: () => takeToBoard(m) });
                    items.push({ label: m.placement ? 'Flytta på dagordningen…' : 'Lägg på årsmötets dagordning…', fn: () => placeMotionDialog(m) });
                    if (m.placement) items.push({ label: 'Ta bort från dagordningen', fn: () => unplaceMotion(m) });
                    items.push({ label: 'Bilagor…', fn: () => motionFilesDialog(m) });
                }
                if (m.placement) items.push({ label: 'Öppna mötet', fn: () => go('mote', m.placement.meetingId) });
                const support = (m.coSigners.length ? `${m.coSigners.length} medmotionär${m.coSigners.length === 1 ? '' : 'er'} · ` : '') + `👍 ${m.up} · 👎 ${m.down}`;
                h += `<tr><td class="ps-3 text-nowrap">${esc(m.number)}</td>
                    <td><strong>${esc(m.title)}</strong><div class="sv-help">${esc(m.motionerName)} · inkom ${esc(svDate(m.submittedDate))}${m.isLate ? ' · <span class="text-danger">efter sista dag</span>' : ''}</div>
                        <div class="sv-help small">${support}</div></td>
                    <td>${pill(m.state, m.state === 'OnAgenda' && m.placement ? 'På dagordningen ' + m.placement.paragraph : m.stateLabel)}${m.boardProposalLabel ? `<div class="sv-help small">Styrelsen föreslår: ${esc(m.boardProposalLabel)}</div>` : ''}</td>
                    <td class="text-end pe-3">${rowMenu(items)}</td></tr>`;
            });
            h += '</tbody></table></div>';
        }
        h += '</div>';

        if (r.isClub && r.toKrets) {
            const k = r.toKrets, km = k.motions || [];
            h += `<div class="sv-card"><div class="sv-cardh"><h2>Motioner till ${esc(k.regionName || 'kretsen')}</h2>
                <span class="sv-help">${k.annualMeeting ? 'Kretsårsmöte ' + esc(svDate(k.annualMeeting.meetingDate)) + (k.annualMeeting.motionDeadline ? ' · sista dag ' + esc(svDate(k.annualMeeting.motionDeadline)) : '') : 'Kretsens årsmöte är inte inlagt'}</span></div>`;
            if (!km.length) h += '<p class="sv-help mb-0">Klubben har inte skickat några motioner till kretsen. En motion till kretsen hänvisar till styrelsens beslut i protokollet.</p>';
            km.forEach(m => {
                h += `<div class="sv-person"><span><strong>${esc(m.number)} ${esc(m.title)}</strong><span class="sv-help d-block">${esc(m.sourceReference || '')} · skickad ${esc(svDate(m.submittedDate))}</span></span>
                    <span class="d-flex align-items-center gap-2">${pill(m.state, m.stateLabel)}${rowMenu([
                        { label: 'Visa motionen', fn: () => motionDetail(m) },
                        (m.state !== 'Decided' && m.state !== 'Withdrawn') ? { label: 'Bilagor…', fn: () => motionFilesDialog(m) } : null,
                        (m.state === 'Received' || m.state === 'OpinionReady') ? { divider: true } : null,
                        (m.state === 'Received' || m.state === 'OpinionReady') ? { label: 'Återkalla…', danger: true, fn: () => withdrawClubMotion(m) } : null
                    ])}</span></div>`;
            });
            h += '</div>';
        }
        body().innerHTML = h;
        body().querySelectorAll('[data-opinion]').forEach(b => b.addEventListener('click', () => opinionDialog(motions.find(x => x.id === parseInt(b.dataset.opinion, 10)))));
        const dl = $('moSetDl'); if (dl) dl.addEventListener('click', () => deadlineDialog({ id: a.id, meetingDate: a.meetingDate, motionDeadline: a.motionDeadline }));
    }

    function motionText(m) {
        return `<p class="mb-1"><strong>${esc(m.number)}</strong> · ${esc(m.motionerName)}${m.signedByName ? ` (för styrelsen: ${esc(m.signedByName)})` : ''} · inkom ${esc(svDate(m.submittedDate))}${m.isLate ? ' <span class="text-danger">· efter sista dag</span>' : ''}</p>
            ${m.coSigners && m.coSigners.length ? `<p class="sv-help mb-2">Medmotionärer: ${m.coSigners.map(esc).join(', ')}</p>` : ''}
            ${m.sourceReference ? `<p class="sv-help mb-2">Hänvisning till styrelsens beslut: ${esc(m.sourceReference)}</p>` : ''}
            ${m.background ? `<div class="mb-2" style="white-space:pre-wrap;">${esc(m.background)}</div>` : ''}
            <p class="mb-1"><strong>Förslag till beslut</strong></p><ul>${(m.proposals || []).map(p => `<li>${esc(p)}</li>`).join('')}</ul>
            ${motionFilesHtml(m, false)}`;
    }
    function fileSize(n) { return n >= 1048576 ? (n / 1048576).toFixed(1).replace('.', ',') + ' MB' : Math.max(1, Math.round(n / 1024)) + ' kB'; }
    function motionFilesHtml(m, canEdit) {
        const list = m.attachments || [];
        if (!list.length) return canEdit ? '<p class="sv-help mb-2">Motionen har inga bilagor.</p>' : '';
        return '<p class="mb-1"><strong>Bilagor</strong></p><ul class="list-unstyled mb-2">' + list.map(a =>
            `<li class="d-flex flex-wrap gap-2 align-items-center"><i class="bi bi-paperclip" aria-hidden="true"></i>
                <a href="${esc(a.url)}" target="_blank" rel="noopener">${esc(a.fileName)}</a><span class="sv-help small">${fileSize(a.size)}</span>
                ${canEdit ? `<button type="button" class="btn btn-link btn-sm text-danger p-0" data-mf-remove="${a.id}">Ta bort</button>` : ''}</li>`).join('') + '</ul>';
    }
    /** Bilagor till en motion: för en motion som kom på papper, eller klubbens egen motion till kretsen. */
    function motionFilesDialog(m) {
        const render = body => {
            body.innerHTML = `<p class="mb-2"><strong>${esc(m.number)} ${esc(m.title)}</strong></p>${motionFilesHtml(m, true)}
                ${(m.attachments || []).length < 5 ? `<label class="sv-label" for="mfFile">Bifoga en fil</label>
                <input type="file" id="mfFile" class="form-control mb-1" accept=".pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.txt,.csv,.jpg,.jpeg,.png">
                <p class="sv-help mb-0">Högst 5 bilagor per motion. Bilagorna syns tillsammans med motionen.</p>` : '<p class="sv-help mb-0">Motionen har redan 5 bilagor. Ta bort en för att bifoga en ny.</p>'}`;
            $('svFormOk').classList.toggle('d-none', (m.attachments || []).length >= 5);
            body.querySelectorAll('[data-mf-remove]').forEach(b => b.addEventListener('click', () =>
                post('BoardWork', 'RemoveMotionAttachment', { attachmentId: b.dataset.mfRemove }).then(r => {
                    if (!r.success) { failMsg(r); return; }
                    m.attachments = (m.attachments || []).filter(a => String(a.id) !== b.dataset.mfRemove);
                    render(body); renderMotions();
                })));
        };
        formModal({
            title: 'Bilagor', okLabel: 'Bifoga filen', wide: false,
            html: '',
            onOk: async body => {
                const f = body.querySelector('#mfFile')?.files?.[0];
                if (!f) return 'Välj en fil.';
                const fd = new FormData(); fd.append('motionId', m.id); fd.append('file', f);
                const r = await svRequest('/umbraco/surface/BoardWork/AddMotionAttachment', { method: 'POST', headers: { 'RequestVerificationToken': token() }, body: fd });
                if (!r.success) return r.message || 'Filen kunde inte bifogas.';
                m.attachments = (m.attachments || []).concat([{ id: r.id, fileName: r.fileName, size: f.size, url: '/umbraco/surface/BoardWork/MotionAttachment?id=' + r.id }]);
                render(body); renderMotions();
                return false;
            }
        });
        render($('svFormBody'));
    }
    function motionDetail(m) {
        formModal({
            title: m.title, wide: true,
            html: `<p>${pill(m.state, m.stateLabel)}</p>${motionText(m)}
                <p class="sv-help mb-1">👍 ${m.up} · 👎 ${m.down}</p>
                ${(m.upNames || []).length ? `<p class="sv-help small mb-1">👍 ${m.upNames.map(esc).join(', ')}</p>` : ''}
                ${(m.downNames || []).length ? `<p class="sv-help small mb-1">👎 ${m.downNames.map(esc).join(', ')}</p>` : ''}
                ${m.boardProposalLabel ? `<div class="sv-card mb-2"><strong>Styrelsens förslag: ${esc(m.boardProposalLabel)}</strong>${m.boardOpinion ? `<div class="mt-1" style="white-space:pre-wrap;">${esc(m.boardOpinion)}</div>` : ''}</div>` : ''}
                ${m.placement ? `<p class="mb-1"><strong>Dagordning:</strong> ${esc(m.placement.meetingTitle)} ${esc(svDate(m.placement.meetingDate))}, ${esc(m.placement.paragraph)}</p>` : ''}
                ${m.decision ? `<p class="mb-1"><strong>Årsmötets beslut:</strong> <span style="white-space:pre-wrap;">${esc(m.decision)}</span></p>` : ''}`
        });
    }
    function opinionDialog(m) {
        if (!m) return;
        formModal({
            title: 'Styrelsens yttrande', okLabel: 'Spara yttrandet', wide: true,
            html: `${motionText(m)}
                <label class="sv-label mt-2" for="opText">Styrelsens yttrande</label><textarea id="opText" class="form-control mb-3" rows="5">${esc(m.boardOpinion || '')}</textarea>
                <span class="sv-label">Styrelsens förslag till årsmötet</span>
                ${[['Bifall', 'Bifall'], ['Avslag', 'Avslag'], ['Besvarad', 'Anse motionen besvarad'], ['DelvisBifall', 'Delvis bifall']].map(([v, l]) =>
                    `<div class="form-check"><input class="form-check-input" type="radio" name="opProp" id="opProp_${v}" value="${v}" ${m.boardProposal === v ? 'checked' : ''}><label class="form-check-label" for="opProp_${v}">${l}</label></div>`).join('')}
                <p class="sv-help mt-2 mb-0">Yttrandet och förslaget följer med årsmöteshandlingarna och syns för medlemmarna på motionssidan.</p>`,
            onOk: () => {
                const proposal = (document.querySelector('input[name="opProp"]:checked') || {}).value || '';
                if (!proposal) return Promise.resolve('Välj styrelsens förslag.');
                return post('BoardWork', 'SaveMotionOpinion', { motionId: m.id, opinion: $('opText').value, proposal })
                    .then(r => { if (!r.success) return r.message || 'Yttrandet kunde inte sparas.'; showMsg('Yttrandet är sparat.', 'success', true); refreshCounts(); route(); return true; });
            }
        });
    }
    function placeMotionDialog(m) {
        placeDialog({
            title: 'Lägg på årsmötets dagordning: ' + m.number, intro: esc(m.title) + ' · ' + esc(m.motionerName),
            meetingId: m.meetingId || 0, annualOnly: true, notifyName: null,
            onPlace: (mid, parent) => post('BoardWork', 'PlaceMotion', { motionId: m.id, meetingId: mid, parentItemId: parent || '' })
                .then(r => { if (!r.success) return r.message || 'Motionen kunde inte placeras.'; showMsg(`Motionen ligger nu som <strong>${esc(r.paragraph)}</strong> på årsmötet.`, 'success', true); route(); return true; })
        });
    }
    async function placeAllMotions() {
        const a = moData.annualMeeting;
        if (!await svConfirm('Lägg motionerna på dagordningen?', `Årets motioner läggs som underpunkter under årsmötets punkt om motioner (${svDate(a.meetingDate.slice(0, 10))}). Motioner som redan ligger där eller är återkallade hoppas över.`, 'Lägg på dagordningen')) return;
        post('BoardWork', 'PlaceAllMotions', Object.assign(OWN(), { meetingId: a.id })).then(r => {
            if (!r.success) { failMsg(r); return; }
            let t = `${r.placed} motioner lades under ${esc(r.paragraph)}.`;
            if (r.withoutOpinion) t += ` ${r.withoutOpinion} saknar fortfarande styrelsens yttrande.`;
            if (r.failed && r.failed.length) t += ' Gick inte: ' + r.failed.map(esc).join('; ');
            showMsg(t, r.failed && r.failed.length ? 'warning' : 'success', true);
            route();
        });
    }
    async function unplaceMotion(m) {
        if (!await svConfirm('Ta bort från dagordningen?', `${m.number} ${m.title} tas bort från årsmötets dagordning. Motionen finns kvar.`, 'Ta bort')) return;
        post('BoardWork', 'UnplaceMotion', { motionId: m.id }).then(r => { if (r.success) route(); else failMsg(r); });
    }
    function takeToBoard(m) {
        post('BoardWork', 'TakeMotionToBoard', { motionId: m.id }).then(r => {
            if (!r.success) { failMsg(r); return; }
            showMsg(r.already ? 'Motionen ligger redan i ärendekön.' : 'Motionen ligger nu i ärendekön, så att styrelsen kan bereda yttrandet på ett möte. <a href="#vy=arenden">Öppna ärendekön</a>', 'success', true);
            refreshCounts();
        });
    }

    function proposalsEditor(prefix, list) {
        const rows = (list && list.length ? list : ['']).map((p, i) => `<input type="text" class="form-control mb-2" data-prop="${prefix}" value="${esc(p)}" placeholder="att …" aria-label="Förslag ${i + 1}">`).join('');
        return `<div id="${prefix}Props">${rows}</div><button type="button" class="btn btn-link p-0" data-add-prop="${prefix}">+ Lägg till ett förslag</button>
            <div class="sv-help">Skriv varje förslag som en egen rad som börjar med "att".</div>`;
    }
    function bindProposals(el) {
        el.querySelectorAll('[data-add-prop]').forEach(b => b.addEventListener('click', () => {
            const box = $(b.dataset.addProp + 'Props');
            const inp = document.createElement('input');
            inp.type = 'text'; inp.className = 'form-control mb-2'; inp.placeholder = 'att …'; inp.dataset.prop = b.dataset.addProp;
            box.appendChild(inp); inp.focus();
        }));
    }
    function readProposals(prefix) { return Array.from(document.querySelectorAll(`[data-prop="${prefix}"]`)).map(i => i.value.trim()).filter(Boolean); }

    function paperMotionDialog() {
        formModal({
            title: 'Lägg in en motion som kommit på papper', okLabel: 'Lägg in motionen', wide: true,
            html: `<p class="sv-help">För en motion som kommit in på papper eller i ett mejl, så att den finns med i handlingarna.</p>
                <div class="row g-2"><div class="col-12 col-md-8"><label class="sv-label" for="pmName">Motionär</label><input type="text" id="pmName" class="form-control" placeholder="För- och efternamn"></div>
                <div class="col-12 col-md-4"><label class="sv-label" for="pmDate">Inkom</label><input type="text" id="pmDate" class="form-control" value="${new Date().toISOString().slice(0, 10)}"></div></div>
                <label class="sv-label mt-3" for="pmTitle">Rubrik</label><input type="text" id="pmTitle" class="form-control mb-3" maxlength="300">
                <label class="sv-label" for="pmBg">Bakgrund</label><textarea id="pmBg" class="form-control mb-3" rows="4"></textarea>
                <span class="sv-label">Förslag till beslut</span>${proposalsEditor('pm', [])}`,
            onOpen: el => { bindProposals(el); datePick($('pmDate'), false, { maxDate: 'today' }); },
            onOk: () => post('BoardWork', 'AddPaperMotion', Object.assign(OWN(), {
                motionerName: $('pmName').value.trim(), title: $('pmTitle').value.trim(), background: $('pmBg').value,
                proposalsJson: JSON.stringify(readProposals('pm')), submittedDate: $('pmDate').value.trim()
            })).then(r => { if (!r.success) return r.message || 'Motionen kunde inte läggas in.'; showMsg(`Motion ${esc(r.number)} är inlagd.`, 'success', true); refreshCounts(); route(); return true; })
        });
    }

    // Motion från klubben till kretsen — KRÄVER en hänvisning till styrelsens beslut (Stefan 2026-10-08).
    function kretsMotionDialog(agendaItemId, heading, decision) {
        if (!IS_CLUB) return;
        get('BoardWork', 'GetDecisionReferences', { clubId: OWNER_ID }).then(r => {
            if (!r.success) { failMsg(r); return; }
            const refs = r.references || [];
            if (!refs.length) {
                showMsg('En motion till kretsen ska hänvisa till ett beslut i styrelsens protokoll, och inga beslut finns antecknade de senaste två åren. Skriv beslutet under en punkt på ett möte först.', 'warning');
                return;
            }
            formModal({
                title: 'Motion till kretsen', okLabel: 'Skicka till kretsen', wide: true,
                html: `<p class="sv-help">Motionen skickas i styrelsens namn och undertecknas av ordföranden. Kretsens styrelse får den, skriver sitt yttrande och tar upp den på kretsårsmötet.</p>
                    <label class="sv-label" for="kmRef">Styrelsens beslut</label>
                    <select id="kmRef" class="form-select mb-1"><option value="">Välj beslutet motionen bygger på…</option>${refs.map(x => `<option value="${x.agendaItemId}" ${x.agendaItemId === agendaItemId ? 'selected' : ''}>${esc(x.text)}${x.justerat ? '' : ' (ej justerat)'}</option>`).join('')}</select>
                    <div class="sv-help mb-3" id="kmDecision"></div>
                    <label class="sv-label" for="kmTitle">Rubrik</label><input type="text" id="kmTitle" class="form-control mb-3" maxlength="300" value="${esc(heading || '')}">
                    <label class="sv-label" for="kmBg">Bakgrund</label><textarea id="kmBg" class="form-control mb-3" rows="4"></textarea>
                    <span class="sv-label">Förslag till beslut</span>${proposalsEditor('km', decision ? [decision] : [])}`,
                onOpen: el => {
                    bindProposals(el);
                    const show = () => { const x = refs.find(y => y.agendaItemId === parseInt($('kmRef').value, 10)); $('kmDecision').textContent = x ? 'Beslut: ' + x.decision : ''; };
                    $('kmRef').addEventListener('change', show); show();
                },
                onOk: () => {
                    const ref = parseInt($('kmRef').value, 10);
                    if (!ref) return Promise.resolve('Välj det beslut i protokollet som motionen bygger på.');
                    return post('BoardWork', 'SubmitClubMotion', {
                        clubId: OWNER_ID, title: $('kmTitle').value.trim(), background: $('kmBg').value,
                        proposalsJson: JSON.stringify(readProposals('km')), sourceAgendaItemId: ref
                    }).then(rr => {
                        if (!rr.success) return rr.message || 'Motionen kunde inte skickas.';
                        showMsg(`Motion ${esc(rr.number)} är skickad till kretsen.` + (rr.notified ? ' Kretsens sekreterare och ordförande har fått besked.' : ''), 'success', true);
                        go('motioner');
                        return true;
                    });
                }
            });
        });
    }
    async function withdrawClubMotion(m) {
        if (!await svConfirm('Återkalla motionen?', `${m.number} ${m.title} dras tillbaka från kretsårsmötet.`, 'Återkalla', true)) return;
        post('BoardWork', 'WithdrawClubMotion', { motionId: m.id }).then(r => { if (r.success) route(); else failMsg(r); });
    }

    // =====================================================================
    //  STYRELSEN OCH MANDAT
    // =====================================================================
    let SV_ROLE_BY_ID = {};
    function viewStyrelsen() {
        setHead('Styrelsen och mandat', 'Vem som sitter i styrelsen, övriga förtroendevalda och när mandaten går ut.');
        setActions([CAN_MANAGE ? { label: 'Lägg till förtroendevald…', icon: 'bi-person-plus', fn: addRoleDialog } : null]);
        Promise.all([
            get('BoardRole', 'GetBoardMembers', OWN()),
            get('BoardRole', 'GetExpiringTerms', Object.assign(OWN(), { withinDays: 365 }))
        ]).then(([r, e]) => renderStyrelsen((r.success && r.data) ? r.data : [], (e.success && e.data) ? e.data : []))
          .catch(() => body().innerHTML = errBox('Styrelsen kunde inte hämtas.'));
    }
    function personRow(r) {
        SV_ROLE_BY_ID[r.id] = r;
        const term = r.termEndsDate ? (r.isTermExpired ? `<span class="text-danger">Mandat gick ut ${esc(svDate(r.termEndsDate))}</span>` : `Vald t.o.m. ${esc(svDate(r.termEndsDate))}`)
            : '<span class="sv-help">Ingen mandattid angiven</span>';
        return `<div class="sv-person"><span><strong>${esc(r.memberName || '-')}</strong><span class="d-block">${esc(r.title)}</span><span class="sv-help d-block">${term}</span></span>
            ${CAN_MANAGE ? rowMenu([{ label: 'Ta bort uppdraget…', danger: true, fn: () => removeRole(r.id) }]) : ''}</div>`;
    }
    function renderStyrelsen(roles, expiring) {
        const board = roles.filter(r => r.isBoardMember);
        const uppdrag = roles.filter(r => KRETS_UPPDRAG.some(u => u.key === r.roleKey));
        const ovriga = roles.filter(r => !r.isBoardMember && !VALB_KEYS.includes(r.roleKey) && !KRETS_UPPDRAG.some(u => u.key === r.roleKey));
        let h = '<div class="sv-card"><h2>Styrelsen</h2>' + (board.length ? board.map(personRow).join('') : '<p class="sv-help mb-0">Inga styrelseledamöter är tilldelade ännu.</p>') + '</div>';
        if (!IS_CLUB) {
            h += `<div class="sv-card" id="svKretsUppdrag"><h2>Kretsens uppdrag</h2>
                <p class="sv-help">Granskar resultatlistor och banor och tar emot tävlingsansökningar. Uppdragen är inga styrelseplatser och räknas inte i beslutsförheten.</p>`;
            KRETS_UPPDRAG.forEach(u => {
                const holders = uppdrag.filter(r => r.roleKey === u.key);
                h += `<div class="mt-3"><div class="sv-label">${esc(u.label)}</div><div class="sv-help mb-1">${esc(u.task)}</div>`
                    + (holders.length ? holders.map(personRow).join('') : `<p class="text-warning-emphasis mb-1" data-uppdrag-missing="${esc(u.key)}"><i class="bi bi-exclamation-triangle me-1"></i>Ingen är utsedd. Ärendena går då till kretsens kontaktadress och kretsadministratören.</p>`)
                    + '</div>';
            });
            h += '</div>';
        }
        if (ovriga.length) h += '<div class="sv-card"><h2>Övriga förtroendevalda</h2><p class="sv-help">Valda på årsmötet men sitter inte i styrelsen — kallas inte till styrelsemöten.</p>' + ovriga.map(personRow).join('') + '</div>';
        if (expiring.length) {
            h += '<div class="sv-card sv-attn"><h2>Mandat som snart går ut</h2><p class="sv-help">Dags att förbereda val på årsmötet för dessa.</p>';
            expiring.forEach(r => { h += `<div class="sv-person"><span>${esc(r.memberName || '-')} – ${esc(r.title)}</span><span>${r.isTermExpired ? '<span class="text-danger">utgånget</span>' : 'till ' + esc(svDate(r.termEndsDate))}</span></div>`; });
            h += '</div>';
        }
        body().innerHTML = h;
    }
    function memberPickerHtml(prefix) {
        return IS_CLUB ? `<select id="${prefix}Member" class="form-select"><option value="">Välj medlem…</option></select>`
            : `<input type="text" id="${prefix}Search" class="form-control" placeholder="Sök namn…" autocomplete="off"><select id="${prefix}Results" class="form-select mt-1 d-none" size="4"></select>`;
    }
    function initMemberPicker(prefix) {
        if (IS_CLUB) {
            get('ClubAdmin', 'GetClubMembers', { clubId: OWNER_ID }).then(r => {
                const sel = $(prefix + 'Member'); if (!sel || !r.success) return;
                sel.innerHTML += (r.data || []).filter(m => m.isApproved !== false).map(m => `<option value="${m.id}">${esc(m.memberName)}</option>`).join('');
            });
        } else {
            const inp = $(prefix + 'Search'); let t = null;
            inp?.addEventListener('input', e => {
                clearTimeout(t); const q = e.target.value.trim();
                if (q.length < 2) { $(prefix + 'Results').classList.add('d-none'); return; }
                t = setTimeout(() => get('BoardRole', 'SearchMembers', Object.assign({ query: q }, OWN())).then(r => {
                    const s = $(prefix + 'Results');
                    s.innerHTML = (r.data || []).map(m => `<option value="${m.id}">${esc(m.name)}</option>`).join('');
                    s.classList.remove('d-none');
                }), 300);
            });
            $(prefix + 'Results')?.addEventListener('change', function () {
                const o = this.options[this.selectedIndex];
                if (o?.value) { inp.value = o.textContent; this.dataset.id = o.value; this.classList.add('d-none'); }
            });
        }
    }
    function pickedMemberId(prefix) { return IS_CLUB ? ($(prefix + 'Member')?.value || '') : ($(prefix + 'Results')?.dataset.id || ''); }

    function addRoleDialog() {
        formModal({
            title: 'Lägg till förtroendevald', okLabel: 'Lägg till', wide: true,
            html: `<p class="sv-help">Styrelseroller bockas i som "Sitter i styrelsen" automatiskt; revisor m.fl. lämnas obockade och kallas då inte till styrelsemöten.</p>
                <div class="row g-3"><div class="col-12"><label class="sv-label">Person</label>${memberPickerHtml('ar')}</div>
                <div class="col-12 col-md-6"><label class="sv-label" for="arRole">Roll</label><select id="arRole" class="form-select"><option value="">Välj roll…</option></select>
                    <div class="form-check mt-2"><input class="form-check-input" type="checkbox" id="arIsBoard" checked><label class="form-check-label sv-help" for="arIsBoard">Sitter i styrelsen (kallas till styrelsemöten)</label></div></div>
                <div class="col-12 col-md-6 d-none" id="arCustomWrap"><label class="sv-label" for="arCustom">Egen titel</label><input type="text" id="arCustom" class="form-control" placeholder="t.ex. Materialförvaltare"></div>
                <div class="col-6 col-md-4"><label class="sv-label" for="arElected">Vald datum</label><input type="text" id="arElected" class="form-control"></div>
                <div class="col-6 col-md-3"><label class="sv-label" for="arYears">Mandat (år)</label><input type="number" id="arYears" class="form-control" min="1" max="10" placeholder="2"></div>
                <div class="col-12 col-md-5"><label class="sv-label" for="arEnds">Mandat t.o.m.</label><input type="text" id="arEnds" class="form-control"></div></div>`,
            onOpen: () => {
                initMemberPicker('ar');
                get('BoardRole', 'GetAvailableRoles', { ownerType: OWNER_TYPE }).then(r => {
                    if (!r.success) return;
                    const sel = $('arRole');
                    const opt = x => `<option value="${esc(x.key)}" data-board="${x.isBoardMember}">${esc(x.label)}</option>`;
                    sel.innerHTML += r.data.filter(x => !VALB_KEYS.includes(x.key) && !x.isKretsUppdrag).map(opt).join('');
                    const krets = r.data.filter(x => x.isKretsUppdrag);
                    if (krets.length) sel.innerHTML += `<optgroup label="Kretsens uppdrag">${krets.map(opt).join('')}</optgroup>`;
                    sel.innerHTML += '<option value="Custom" data-board="false">Annat…</option>';
                });
                $('arRole').addEventListener('change', () => {
                    const sel = $('arRole'), o = sel.options[sel.selectedIndex];
                    $('arCustomWrap').classList.toggle('d-none', sel.value !== 'Custom');
                    if (o) $('arIsBoard').checked = o.dataset.board === 'true';
                });
                let dirty = false;
                const ends = datePick($('arEnds'), false, { onChange: () => dirty = true });
                const fill = () => {
                    if (dirty || !ends) return;
                    const el = $('arElected').value, y = parseInt($('arYears').value, 10);
                    if (!el || !y) return;
                    const dd = new Date(el + 'T00:00:00'); if (isNaN(dd)) return;
                    dd.setFullYear(dd.getFullYear() + y); ends.setDate(dd.toISOString().slice(0, 10), false);
                };
                datePick($('arElected'), false, { onChange: fill });
                $('arYears').addEventListener('input', fill);
            },
            onOk: () => {
                const memberId = pickedMemberId('ar'), roleKey = $('arRole').value, custom = $('arCustom').value.trim();
                if (!memberId) return Promise.resolve('Välj en person.');
                if (!roleKey) return Promise.resolve('Välj en roll.');
                if (roleKey === 'Custom' && !custom) return Promise.resolve('Skriv en titel.');
                return post('BoardRole', 'AssignBoardRole', Object.assign(OWN(), {
                    memberId, roleKey, customTitle: roleKey === 'Custom' ? custom : '', isBoardMember: $('arIsBoard').checked,
                    electedDate: $('arElected').value || '', termEndsDate: $('arEnds').value || '', termYears: $('arYears').value || ''
                })).then(r => { if (!r.success) return r.message || 'Personen kunde inte läggas till.'; showMsg('Uppdraget är tillagt.', 'success', true); route(); return true; });
            }
        });
    }
    async function removeRole(id) {
        const r = SV_ROLE_BY_ID[id];
        const name = (r && r.memberName) || 'personen', title = (r && r.title) || '';
        let msg = `${name}${title ? ` (${title})` : ''} tas bort.`;
        // ⚠️ Vapenbehörigheten kontrolleras FÖRE borttagningen — efteråt är den borta och varningen omöjlig.
        if (IS_CLUB && r && r.memberId) {
            try {
                const impact = await get('FirearmAdmin', 'GetRemovalImpact', { clubId: OWNER_ID, memberId: r.memberId });
                if (impact.success && impact.isFirearmViewer) {
                    msg += `\n\n${name} är klubbens föreningsintygsansvarige och kan läsa medlemmarnas vapeninnehav. Den behörigheten upphör direkt.`;
                    if (impact.wouldLeaveClubWithout) msg += '\n\nOBS: klubben har då INGEN som kan läsa vapeninnehav. Utse en efterträdare.';
                }
            } catch (e) { /* ett misslyckat uppslag får inte blockera borttagningen */ }
        }
        if (!await svConfirm('Ta bort uppdraget?', msg, 'Ta bort', true)) return;
        post('BoardRole', 'RemoveBoardRole', { boardRoleId: id }).then(res => { if (res.success) route(); else failMsg(res); });
    }

    // =====================================================================
    //  ÅRSHJUL
    // =====================================================================
    let svWheelYear = null, svWheelItems = [];
    function viewArshjul() {
        const y = svWheelYear || new Date().getFullYear();
        setHead('Årshjul ' + y, 'Föreningens årliga uppgifter. Bocka av när de är klara. Standarduppgifterna fylls i automatiskt och går att ändra.');
        get('BoardGovernance', 'GetYearWheel', Object.assign(OWN(), { year: y })).then(r => {
            if (!r.success) { body().innerHTML = errBox(r.message || 'Årshjulet kunde inte hämtas.'); return; }
            svWheelYear = y; svWheelItems = r.data || [];
            const years = r.years || [y]; if (!years.includes(svWheelYear)) years.unshift(svWheelYear);
            let h = `<div class="d-flex flex-wrap gap-2 align-items-center mb-2 sv-help"><label class="d-flex align-items-center gap-2 mb-0">Visa år
                <select id="whYear" class="form-select form-select-sm" style="width:auto;">${years.map(yy => `<option ${yy == svWheelYear ? 'selected' : ''}>${yy}</option>`).join('')}</select></label></div><div class="sv-card">`;
            svWheelItems.forEach(it => {
                const dt = it.targetDate ? `<span class="${it.isOverdue ? 'text-danger' : 'sv-help'}"> · ${esc(svDate(it.targetDate))}${it.isOverdue ? ' (försenat)' : ''}</span>` : '';
                h += `<div class="sv-person" id="svWheel_${it.id}"><label class="d-flex align-items-center gap-2 mb-0" style="cursor:pointer;">
                    <input type="checkbox" class="form-check-input sv-check" data-wh-done="${it.id}" ${it.done ? 'checked' : ''}>
                    <span class="svWheelText ${it.done ? 'text-decoration-line-through sv-help' : ''}">${esc(it.title)}${dt}</span></label>
                    ${CAN_MANAGE ? rowMenu([{ label: 'Ändra…', fn: () => wheelEdit(it) }, { divider: true }, { label: 'Ta bort…', danger: true, fn: () => wheelRemove(it) }]) : ''}</div>`;
            });
            h += `<div class="row g-2 mt-2" style="max-width:640px;"><div class="col-12 col-md-7"><input type="text" id="whNewTitle" class="form-control" placeholder="Lägg till egen uppgift…" aria-label="Ny uppgift"></div>
                <div class="col-6 col-md-3"><input type="text" id="whNewDate" class="form-control" placeholder="Datum" aria-label="Datum"></div>
                <div class="col-6 col-md-2"><button type="button" class="btn btn-outline-primary w-100" id="whAdd">Lägg till</button></div></div></div>`;
            body().innerHTML = h;
            datePick($('whNewDate'), false);
            $('whYear').addEventListener('change', e => { svWheelYear = parseInt(e.target.value, 10); viewArshjul(); });
            body().querySelectorAll('[data-wh-done]').forEach(cb => cb.addEventListener('change', () => {
                const id = parseInt(cb.dataset.whDone, 10), done = cb.checked;
                post('BoardGovernance', 'SetWheelDone', { itemId: id, done }).then(rr => {
                    if (!rr.success) { cb.checked = !done; return; }
                    const t = $('svWheel_' + id)?.querySelector('.svWheelText');
                    if (t) { t.classList.toggle('text-decoration-line-through', done); t.classList.toggle('sv-help', done); }
                });
            }));
            $('whAdd').addEventListener('click', () => {
                const title = $('whNewTitle').value.trim(); if (!title) return;
                post('BoardGovernance', 'AddWheelItem', Object.assign(OWN(), { year: svWheelYear, title, targetDate: $('whNewDate').value || '' })).then(rr => { if (rr.success) viewArshjul(); else failMsg(rr); });
            });
        }).catch(() => body().innerHTML = errBox('Årshjulet kunde inte hämtas.'));
    }
    function wheelEdit(it) {
        formModal({
            title: 'Ändra uppgiften', wide: false,
            html: `<label class="sv-label" for="weTitle">Uppgift</label><input type="text" id="weTitle" class="form-control mb-3" value="${esc(it.title)}">
                <label class="sv-label" for="weDate">Datum (frivilligt)</label><input type="text" id="weDate" class="form-control" value="${esc(it.targetDate || '')}">`,
            onOpen: () => datePick($('weDate'), false),
            onOk: () => {
                const title = $('weTitle').value.trim(); if (!title) return Promise.resolve('Uppgiften måste ha en text.');
                return post('BoardGovernance', 'UpdateWheelItem', { itemId: it.id, title, targetDate: $('weDate').value || '' })
                    .then(r => { if (!r.success) return r.message || 'Uppgiften kunde inte sparas.'; viewArshjul(); return true; });
            }
        });
    }
    async function wheelRemove(it) {
        if (!await svConfirm('Ta bort uppgiften?', `${it.title} tas bort från årshjulet.`, 'Ta bort', true)) return;
        post('BoardGovernance', 'RemoveWheelItem', { itemId: it.id }).then(r => { if (r.success) viewArshjul(); });
    }

    // =====================================================================
    //  VALBEREDNING
    // =====================================================================
    let svNomYear = null;
    function viewValberedning() {
        const y = svNomYear || (new Date().getFullYear() + 1);
        setHead('Valberedning', 'Valberedningen väljs på årsmötet och står utanför styrelsen. Den arbetar inför nästa årsmöte.');
        setActions([
            CAN_MANAGE ? { label: 'Lägg till ledamot i valberedningen…', icon: 'bi-person-plus', fn: vbAddDialog } : null,
            { label: 'Skriv ut valberedningens förslag', icon: 'bi-printer', fn: () => window.open(`/styrelse/valforslag?type=${OWNER_TYPE}&id=${OWNER_ID}&year=${svNomYear || y}`, '_blank') }
        ]);
        Promise.all([
            get('BoardGovernance', 'GetNominations', Object.assign(OWN(), { year: y })),
            get('BoardRole', 'GetBoardMembers', OWN())
        ]).then(([r, roster]) => {
            if (!r.success) { body().innerHTML = errBox(r.message || 'Valberedningen kunde inte hämtas.'); return; }
            svNomYear = y;
            renderValberedning(r, (roster.success && roster.data) ? roster.data : []);
        }).catch(() => body().innerHTML = errBox('Valberedningen kunde inte hämtas.'));
    }
    function renderValberedning(r, roster) {
        const noms = r.data || [], posts = r.postsUpForElection || [], years = r.years || [svNomYear];
        if (!years.includes(svNomYear)) years.unshift(svNomYear);
        const vb = roster.filter(x => VALB_KEYS.includes(x.roleKey))
            .sort((a, b) => (a.roleKey === 'ValberedningSammankallande' ? 0 : 1) - (b.roleKey === 'ValberedningSammankallande' ? 0 : 1));
        let h = '<div class="sv-card"><h2>Valberedningens ledamöter</h2>' + (vb.length ? vb.map(personRow).join('') : '<p class="sv-help mb-0">Inga ledamöter tillagda ännu.</p>') + '</div>';
        h += `<div class="sv-card"><div class="sv-cardh"><h2>Inför årsmötet ${svNomYear}</h2>
            <label class="sv-help d-flex align-items-center gap-2 mb-0">Inför år <select id="vbYear" class="form-select form-select-sm" style="width:auto;">${years.map(yy => `<option ${yy == svNomYear ? 'selected' : ''}>${yy}</option>`).join('')}</select></label></div>
            <p class="mb-0"><strong>Poster på val (mandat t.o.m. ${svNomYear}):</strong> ${posts.length ? posts.map(p => `${esc(p.title)} (${esc(p.memberName || '')})`).join(', ')
                : `inga mandat med slutdatum t.o.m. ${svNomYear}. Ange mandattider under <a href="#vy=styrelsen">Styrelsen och mandat</a> så listas posterna automatiskt.`}</p></div>`;
        h += '<div class="sv-card"><h2>Förslag på kandidater</h2>';
        if (!noms.length) h += '<p class="sv-help">Inga förslag ännu.</p>';
        noms.forEach(n => {
            h += `<div class="sv-person"><span><strong>${esc(n.candidateName)}</strong> <span class="sv-help">till ${esc(n.postLabel)}</span>${n.notes ? `<span class="sv-help d-block">${esc(n.notes)}</span>` : ''}</span>
                <span class="d-flex gap-1 align-items-center"><select class="form-select form-select-sm" style="width:auto;" data-nom-status="${n.id}" aria-label="Status">${NOM_STATUSES.map(s => `<option ${s === n.status ? 'selected' : ''}>${s}</option>`).join('')}</select>
                ${rowMenu([{ label: 'Ta bort förslaget…', danger: true, fn: () => nomRemove(n) }])}</span></div>`;
        });
        h += `<div class="row g-2 mt-2" style="max-width:720px;">
            <div class="col-12 col-md-4"><label class="sv-help" for="nomName">Kandidat</label><input type="text" id="nomName" class="form-control" placeholder="För- och efternamn"></div>
            <div class="col-12 col-md-4"><label class="sv-help" for="nomPost">Föreslås till</label><select id="nomPost" class="form-select">${POST_KEYS.map(p => `<option value="${p[0]}">${p[1]}</option>`).join('')}<option value="">Annan…</option></select>
                <input type="text" id="nomPostCustom" class="form-control mt-1 d-none" placeholder="Egen post"></div>
            <div class="col-12 col-md-4"><label class="sv-help" for="nomStatus">Status</label><select id="nomStatus" class="form-select">${NOM_STATUSES.map(s => `<option>${s}</option>`).join('')}</select></div>
            <div class="col-12"><input type="text" id="nomNotes" class="form-control" placeholder="Anteckning (frivilligt)" aria-label="Anteckning"></div>
            <div class="col-12"><button type="button" class="btn btn-outline-primary" id="nomAdd"><i class="bi bi-plus-lg me-1"></i>Lägg till förslag</button></div></div></div>`;
        body().innerHTML = h;
        $('vbYear').addEventListener('change', e => { svNomYear = parseInt(e.target.value, 10); viewValberedning(); });
        $('nomPost').addEventListener('change', () => $('nomPostCustom').classList.toggle('d-none', $('nomPost').value !== ''));
        body().querySelectorAll('[data-nom-status]').forEach(s => s.addEventListener('change', () =>
            post('BoardGovernance', 'SetNominationStatus', { nominationId: parseInt(s.dataset.nomStatus, 10), status: s.value }).then(rr => { if (!rr.success) failMsg(rr); })));
        $('nomAdd').addEventListener('click', () => {
            const name = $('nomName').value.trim(), postKey = $('nomPost').value;
            const postLabel = postKey ? (POST_KEYS.find(p => p[0] === postKey)?.[1] || postKey) : $('nomPostCustom').value.trim();
            if (!name) { showMsg('Skriv kandidatens namn.', 'warning'); return; }
            if (!postLabel) { showMsg('Ange vilken post.', 'warning'); return; }
            post('BoardGovernance', 'AddNomination', Object.assign(OWN(), { year: svNomYear, postKey, postLabel, candidateName: name, status: $('nomStatus').value, notes: $('nomNotes').value || '' }))
                .then(rr => { if (rr.success) viewValberedning(); else failMsg(rr); });
        });
    }
    function vbAddDialog() {
        formModal({
            title: 'Lägg till ledamot i valberedningen', okLabel: 'Lägg till', wide: false,
            html: `<label class="sv-label">Person</label>${memberPickerHtml('vb')}
                <div class="form-check mt-3"><input class="form-check-input" type="checkbox" id="vbSamman"><label class="form-check-label" for="vbSamman">Sammankallande</label></div>`,
            onOpen: () => initMemberPicker('vb'),
            onOk: () => {
                const memberId = pickedMemberId('vb'); if (!memberId) return Promise.resolve('Välj en person.');
                return post('BoardRole', 'AssignBoardRole', Object.assign(OWN(), { memberId, roleKey: $('vbSamman').checked ? 'ValberedningSammankallande' : 'Valberedning', customTitle: '', isBoardMember: false }))
                    .then(r => { if (!r.success) return r.message || 'Personen kunde inte läggas till.'; viewValberedning(); return true; });
            }
        });
    }
    async function nomRemove(n) {
        if (!await svConfirm('Ta bort förslaget?', `${n.candidateName} till ${n.postLabel} tas bort.`, 'Ta bort', true)) return;
        post('BoardGovernance', 'RemoveNomination', { nominationId: n.id }).then(r => { if (r.success) viewValberedning(); });
    }

    // =====================================================================
    //  Start
    // =====================================================================
    const VIEWS = {
        oversikt: viewOversikt, moten: viewMoten, mote: viewMote, mallar: viewMallar, arenden: viewArenden,
        motioner: viewMotioner, arshjul: viewArshjul, styrelsen: viewStyrelsen, valberedning: viewValberedning
    };
    const catalogReady = get('BoardMeetingTemplate', 'GetCatalog', {})
        .then(r => { if (r.success) { svCatalog = r.items || []; svMeetingTypes = r.types || []; } })
        .catch(() => { });

    $('svScope')?.addEventListener('change', e => {
        const v = e.target.value.split('-');
        location.href = '/styrelse?type=' + v[0] + '&id=' + v[1];
    });

    catalogReady.then(() => { route(); refreshCounts(); });
})();
