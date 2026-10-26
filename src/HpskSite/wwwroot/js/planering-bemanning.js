/* =====================================================================================
   Planeringens bemanning (omgång 2, steg 2 — 2026-10-24). En del i planeringen (#vy=bemanning), inte en
   egen sida: Stefan "Bemanningen känns fortfarande lösryckt … knappen Mer … modalerna låsta mot
   vänsterkanten". Tre delar, valda med växeln överst (&del=):
     · Rutnät             — roll × dag, personerna i rutorna (Excel-formen arrangörerna planerar i)
     · Personer           — en rad per människa; ett namn öppnar personen i sidan (&person=)
     · Hitta funktionärer — vilka som får anmäla sig, vad som behövs, förfrågan och länken

   Planeringens form: blå Åtgärder = handlingar för delen man står på, grå radmeny per rad, dialoger
   (formModal/plConfirm). Inga offcanvas, inga prompt/confirm/alert.

   ⚠️ Varje anrop till bemanningens API bär ?planningId= — så vet servern att det gäller planeringen
      (StaffingController.OwnerAsync). competitionId skickas som 0.
   ⚠️ SaveAssignment skriver ALLA fält. En ändring skickar med radens övriga värden (e-post, telefon,
      plats, ansvarig, behörighet) ur GetRoster — annars töms de.
   ⚠️ SaveRole skriver alla rollens flaggor; ett namnbyte skickar med dem ur GetRoles.
   ⚠️ Ingen text från servern når ett attribut som kod: knapparna bär index eller nycklar i data-attribut.
   ===================================================================================== */
window.PlaneringBemanning = (function () {
    'use strict';
    let A = null;   // planeringens API (planering.js)
    const B = {
        grid: null, people: null, meta: null, roles: null, scopes: null, scopeOptions: null, signup: null, help: null,
        q: '', show: '', role: '', byOrg: false, shown: 60,
        timer: null, seq: 0, editing: null, polling: false
    };
    const STATUS = { Planned: 'Planerad', Invited: 'Inbjuden', Accepted: 'Accepterad', Declined: 'Avböjt', Confirmed: 'Bekräftad' };
    const STATUS_CLS = { Planned: 'off', Invited: 'wait', Accepted: 'blue', Declined: 'no', Confirmed: 'ok' };
    const STATE_CLS = { 'Anmäld': 'wait', 'Avböjt': 'no', 'Inbjuden': 'wait', 'Planerad': 'off', 'Accepterad': 'blue', 'Bekräftad': 'ok', 'Förberedelser': 'off' };
    const KINDS = [['Tavlingsdag', 'Tävlingsdag'], ['Forberedelse', 'Förberedelse (bygga, materiel)'], ['Efterarbete', 'Efterarbete (återställning)']];
    const $ = id => document.getElementById(id);
    const esc = s => A.esc(s);

    // ---------------------------------------------------------------- anrop
    function url(action, qs) {
        return '/umbraco/surface/Staffing/' + action + '?' + new URLSearchParams(Object.assign({ planningId: A.ID, competitionId: 0 }, qs || {})).toString();
    }
    function sget(action, qs) { return A.req(url(action, qs), { headers: { Accept: 'application/json' } }); }
    // Steg 2 (K6): en ändring av en rad går till RADENS planering. I familjens rutnät är den inte sidans egen —
    // varje rad och post bär sitt planningId, och servern prövar rätten i just den delen.
    function spost(action, body, pid) {
        return A.req(url(action, pid ? { planningId: pid } : null), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': A.token() },
            body: JSON.stringify(Object.assign({ competitionId: 0 }, body || {}))
        });
    }
    // Familjens rutnät (steg 2): en huvudplanering med delplaneringar visar det gemensamma och varje del i EN tabell.
    // Servern ger varje dels egna, oförändrade rutnät; här läggs de ihop till samma form som ett enskilt rutnät
    // (columns = familjens dagar, rows = alla delars rader i delordning), så ritningen är densamma.
    const family = () => !!(A && A.S() && A.S().planning && (A.S().planning.subPlannings || []).length);
    // Veckan (personens dag med egna starter och krockar) hör till en familj — och till varje planering som hör ihop med en tävling:
    // där finns startlistan, och "jobbar hen när hen själv ska skjuta?" är frågan (persontestet, scenario 6). Servern läser veckan för en ensam planering.
    const weekTab = () => family() || !!(A && A.S() && A.S().planning && A.S().planning.competition);
    function familyGrid(f) {
        const g = { family: true, columns: f.columns || [], rows: [], parts: [], hiddenParts: f.hiddenParts || 0, familyClashes: f.familyClashes || [],
            clashes: [], volunteers: [], unreachable: [], totalAssigned: 0, externalCount: 0, roleChoices: [], roleChoicePending: false };
        (f.parts || []).forEach((p, pi) => {
            const pg = p.grid || {};
            g.parts.push({ planningId: p.planningId, name: p.name, isShared: !!p.isShared, hostLabel: p.hostLabel, dayKeys: p.dayKeys || [],
                columns: pg.columns || [], hasUndated: (pg.columns || []).some(c => !c.key), pending: !!pg.roleChoicePending, rows: (pg.rows || []).length });
            if (p.isShared) { g.roleChoices = pg.roleChoices || []; g.roleChoicePending = !!pg.roleChoicePending; }
            g.totalAssigned += pg.totalAssigned || 0;
            g.externalCount += pg.externalCount || 0;
            (pg.unreachable || []).forEach(n => { if (g.unreachable.indexOf(n) < 0) g.unreachable.push(n); });
            g.volunteers = g.volunteers.concat(pg.volunteers || []);
            g.clashes = g.clashes.concat(pg.clashes || []);
            (pg.rows || []).forEach(r => { r.partIndex = pi; r.planningId = r.planningId || p.planningId; g.rows.push(r); });
        });
        return g;
    }
    async function fetchGrid() {
        if (family()) {
            const f = await sget('GetFamilyGrid');
            if (!f.success) throw new Error(f.message || 'Rutnätet kunde inte läsas.');
            return familyGrid(f);
        }
        const d = await sget('GetGrid');
        if (!d.success) throw new Error(d.message || 'Rutnätet kunde inte läsas.');
        return d;
    }
    async function loadGrid() { B.grid = await fetchGrid(); }
    // Delens egna kolumner (med pass och dag-id) — familjens axel har dem inte; dagar ändras i delen.
    const partOf = row => (B.grid.parts || [])[row.partIndex] || null;
    const colsFor = row => (B.grid.family && row && partOf(row)) ? partOf(row).columns : (B.grid.columns || []);
    async function loadPeople() { const d = await sget('GetPeople'); if (!d.success) throw new Error(d.message || 'Personerna kunde inte läsas.'); B.people = d.people || []; B.meta = d; }
    async function loadRoles() { const d = await sget('GetRoles'); if (!d.success) throw new Error(d.message || 'Rollerna kunde inte läsas.'); B.roles = d.roles || []; }
    async function ensureGrid() { if (!B.grid) await loadGrid(); }
    async function ensureRoles() { if (!B.roles) await loadRoles(); }
    // Rollerna i en bestämd del (familjens rutnät) — hämtas direkt, aldrig ur sidans egen cache.
    async function rolesIn(pid) {
        const d = await sget('GetRoles', { planningId: pid });
        if (!d.success) throw new Error(d.message || 'Rollerna kunde inte läsas.');
        return d.roles || [];
    }
    async function fullAssignment(id) {
        const d = await sget('GetRoster');
        if (!d.success) throw new Error(d.message || 'Uppdraget kunde inte läsas.');
        for (const g of (d.groups || [])) for (const a of (g.assignments || [])) if (a.id === id) return a;
        return null;
    }

    // ---------------------------------------------------------------- adressen
    function hp() { return new URLSearchParams((location.hash || '').replace(/^#/, '')); }
    function part() { const d = hp().get('del'); return d === 'personer' || d === 'hitta' || (d === 'veckan' && weekTab()) ? d : 'rutnat'; }
    function personKeyParam() { return part() === 'personer' || part() === 'veckan' ? (hp().get('person') || '') : ''; }
    function nav(del, person) {
        let h = '#vy=bemanning';
        if (del && del !== 'rutnat') h += '&del=' + del;
        if (person) h += '&person=' + encodeURIComponent(person);
        if (location.hash !== h) history.pushState(null, '', h);
        draw();
    }
    const area = () => $('plbArea');
    const linked = () => !!(A.S() && A.S().planning.competition);
    const first = n => ((n || '').trim().split(/\s+/)[0]) || 'personen';
    const keyOf = e => e.memberId ? ('m:' + e.memberId) : ('n:' + (e.displayName || '').trim().toLowerCase());

    // ---------------------------------------------------------------- start och växeln
    function render(api) {
        A = api;
        startPoll();
        draw();
    }
    function leave() { stopPoll(); closeEditor(); }

    async function draw() {
        const seq = ++B.seq, p = part(), pk = personKeyParam();
        closeEditor();
        A.setActions([]);
        const body = $('plBody');
        body.innerHTML = switchHtml(p) + '<div id="plbArea">' + A.spinner() + '</div>';
        try {
            if (p === 'rutnat') { await loadGrid(); if (seq !== B.seq) return; drawGrid(); }
            else if (p === 'personer') { await loadPeople(); if (seq !== B.seq) return; pk ? drawPerson(pk) : drawPeople(); }
            else if (p === 'veckan') { await (pk ? drawWeekPerson(pk, seq) : drawWeek(seq)); }
            else { await loadPeople(); if (seq !== B.seq) return; await drawFind(seq); }
        } catch (e) {
            if (seq !== B.seq) return;
            A.setHead('Bemanning', '');
            area().innerHTML = `<div class="pl-card"><p class="text-danger mb-0">${esc(e.message || 'Bemanningen kunde inte läsas.')}</p></div>`;
        }
    }
    function switchHtml(p) {
        const b = (k, l) => `<button type="button" class="btn ${p === k ? 'btn-primary' : 'btn-outline-primary'}" data-plb-part="${k}" aria-pressed="${p === k}">${l}</button>`;
        // Steg 3: i en huvudplanering med delar visar "Veckan" varje persons dag för dag över alla delar.
        return `<div class="btn-group plb-switch mb-3" role="group" aria-label="Bemanningens delar">${b('rutnat', 'Rutnät')}${weekTab() ? b('veckan', 'Veckan') : ''}${b('personer', 'Personer')}${b('hitta', 'Hitta funktionärer')}</div>`;
    }

    // Omgång 2 steg 4: grenen väljs när planeringen skapas och styr bara vilka roller som föreslås — därför står den här,
    // där den verkar, och inte under Inställningar. En kopplad planering följer tävlingens gren.
    function disciplineLine() {
        const p = A.S().planning, discs = p.disciplines || [];
        const nameOf = id => (discs.find(d => d.id === id) || {}).name || id || '';
        if (p.competition) return `<p class="pl-help small mb-2" id="plbDisc"><i class="bi bi-diagram-3 me-1" aria-hidden="true"></i>Rollerna följer tävlingens gren: <b>${esc(nameOf(p.competition.typeId))}</b>.</p>`;
        return `<p class="pl-help small mb-2" id="plbDisc"><i class="bi bi-diagram-3 me-1" aria-hidden="true"></i>${p.disciplineKey
            ? `Rollerna föreslås för <b>${esc(nameOf(p.disciplineKey))}</b>.` : 'Ingen gren är vald — rollerna föreslås för alla grenar.'}
            <button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-plb-disc="1">${p.disciplineKey ? 'Ändra…' : 'Välj gren…'}</button></p>`;
    }
    function openDiscipline() {
        const p = A.S().planning;
        A.formModal({
            title: 'Vilken gren gäller planeringen?', okLabel: 'Spara', wide: false,
            html: `<select id="plbDiscSel" class="form-select"><option value="">Ingen gren — eller flera</option>
                ${(p.disciplines || []).map(d => `<option value="${esc(d.id)}"${d.id === p.disciplineKey ? ' selected' : ''}>${esc(d.name)}</option>`).join('')}</select>
                <p class="pl-help small mt-2 mb-0">Styr vilka roller bemanningen föreslår, till exempel stationschef i fältskytte. När planeringen kopplas till en tävling gäller tävlingens gren.</p>`,
            onOk: async body => {
                const v = body.querySelector('#plbDiscSel').value;
                const r = await A.req('/umbraco/surface/Planning/SetDiscipline', { method: 'POST',
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'RequestVerificationToken': A.token() },
                    body: new URLSearchParams({ id: A.ID, value: v }).toString() });
                if (!r.success) return r.message || 'Grenen kunde inte sparas.';
                p.disciplineKey = v || null;
                draw();
                return true;
            }
        });
    }
    document.addEventListener('click', e => { if (A && A.isActive() && e.target.closest('[data-plb-disc]')) { e.preventDefault(); openDiscipline(); } });

    // ================================================================ Rutnätet
    function needFor(row, key) {
        if (!key) return 0;
        const m = row.neededByDay || {};
        return Object.prototype.hasOwnProperty.call(m, key) ? (m[key] || 0) : (row.needed || 0);
    }
    function hasNeed(row) {
        if ((row.needed || 0) > 0) return true;
        const m = row.neededByDay || {};
        return Object.keys(m).some(k => m[k] > 0);
    }
    function dayLabel(key) { const c = (B.grid.columns || []).find(x => x.key === key); return c ? c.label : key; }
    // Omgång 3 (3.2, "tiden är sanningen"): flest på plats samtidigt — inte antalet uppdrag. Servern räknar.
    function filledFor(row, key) {
        const m = row.filledByDay || {};
        if (Object.prototype.hasOwnProperty.call(m, key)) return m[key] || 0;
        return ((row.cells && row.cells[key]) || []).length;
    }

    function gridSummary() {
        const g = B.grid, cols = (g.columns || []).filter(c => c.key);
        const passShort = r => cols.filter(c => (((r.passCover || {})[c.key]) || []).some(p => p.needed > 0 && p.filled < p.needed)).length;
        const short = (g.rows || []).reduce((acc, r) => acc + cols.filter(c => { const n = needFor(r, c.key); return n > 0 && filledFor(r, c.key) < n; }).length + passShort(r), 0);
        const holes = (g.rows || []).reduce((a, r) => a + (r.gaps || []).length, 0);
        const parts = [`${g.totalAssigned || 0} uppdrag`, `${(g.rows || []).length} roller`, `${cols.length} ${cols.length === 1 ? 'dag' : 'dagar'}`];
        if (short) parts.push(`<span class="text-danger fw-semibold">${short} ${short === 1 ? 'ruta saknar' : 'rutor saknar'} folk</span>`);
        if (holes) parts.push(`<span class="text-danger fw-semibold">${holes} ${holes === 1 ? 'lucka' : 'luckor'} i tiden</span>`);
        return parts.join(' · ');
    }

    // Omgång 3 steg 10 ("Välj roller"): grenens inbyggda roller med kryssrutor. Det valda tas fram, resten döljs —
    // utom roller som har folk, som står kvar. Egna roller rör valet inte.
    function roleChoiceList(g, idPrefix) {
        return `<div class="row g-1">${(g.roleChoices || []).map((c, i) => `<div class="col-sm-6"><div class="form-check">
            <input class="form-check-input" type="checkbox" id="${idPrefix}${i}" data-plb-rolechoice="${esc(c.key)}"${c.chosen || c.hasCrew ? ' checked' : ''}${c.hasCrew ? ' disabled' : ''}>
            <label class="form-check-label" for="${idPrefix}${i}">${esc(c.name)}${c.hasCrew ? ' <span class="text-body-secondary small">(har folk)</span>' : ''}</label>
            ${c.description ? `<div class="small text-body-secondary">${esc(c.description)}</div>` : ''}</div></div>`).join('')}</div>`;
    }
    function chooseRolesCard(g) {
        return `<div class="pl-card" id="plbChooseRoles"><h2 class="h5">Vilka roller behöver ni?</h2>
            <p class="pl-help small">Kryssa i de funktioner planeringen ska bemanna. Resten visas inte — ni kan ta fram dem när som helst under
            <b>Åtgärder → Välj roller</b>, och ni kan alltid skriva en egen roll längst ned i rutnätet.</p>
            ${roleChoiceList(g, 'plbRc')}
            <div class="mt-2"><button type="button" class="btn btn-primary btn-sm" data-plb-saveroles>Spara rollerna</button></div></div>`;
    }
    async function saveRoleChoice(scope) {
        const keys = [...scope.querySelectorAll('[data-plb-rolechoice]')].filter(x => x.checked).map(x => x.dataset.plbRolechoice);
        const r = await spost('ChooseRoles', { keys });
        if (!r.success) return r.message || 'Rollerna kunde inte sparas.';
        await loadGrid(); drawGrid();
        if ((r.kept || []).length) A.showMsg('Rollerna är sparade. Kvar eftersom de har folk: ' + esc(r.kept.join(', ')) + '.', 'warning');
        else A.showMsg('Rollerna är sparade.', 'success');
        return true;
    }
    function openChooseRoles() {
        A.formModal({
            title: 'Välj roller', okLabel: 'Spara rollerna',
            html: `<p class="pl-help small">Kryssa i de funktioner planeringen ska bemanna. En roll som har folk står kvar.</p>${roleChoiceList(B.grid, 'plbRm')}`,
            onOk: body => saveRoleChoice(body)
        });
    }
    document.addEventListener('click', async e => {
        if (!A || !A.isActive() || !e.target.closest('[data-plb-saveroles]')) return;
        e.preventDefault();
        const card = $('plbChooseRoles'); if (!card) return;
        const res = await saveRoleChoice(card);
        if (typeof res === 'string') A.showMsg(esc(res), 'danger');
    });

    function drawGrid() {
        const g = B.grid, cols = g.columns || [];
        A.setHead('Bemanning', gridSummary());
        A.setActions([
            { label: 'Lägg till en roll…', icon: 'bi-plus-lg', fn: () => openNewRole() },
            (g.roleChoices || []).length && { label: 'Välj roller…', icon: 'bi-ui-checks', fn: openChooseRoles },
            { label: 'Lägg till en dag…', icon: 'bi-calendar-plus', fn: () => openDay(null) },
            { divider: true },
            { label: 'Skriv ut bemanningen', icon: 'bi-printer', fn: () => window.open('/tavlingsplanering/blad?p=' + A.ID + '&vy=bemanning', '_blank', 'noopener') },
        ]);

        let notes = disciplineLine();
        // Omgång 3 steg 10: en ny planering väljer sina roller i stället för att mötas av hela katalogen.
        if (g.roleChoicePending && (g.roleChoices || []).length) notes += chooseRolesCard(g);
        if (!linked()) notes += `<p class="pl-help small mb-2"><i class="bi bi-info-circle me-1" aria-hidden="true"></i>Planeringen är inte kopplad till någon tävling än. Bemanningen sparas i planeringen och följer med när den kopplas.</p>`;
        const vols = g.volunteers || [];
        if (vols.length) notes += `<div class="pl-next warn"><div class="t"><b>${vols.length} ${vols.length === 1 ? 'har anmält sig' : 'har anmält sig'}</b> men har inget uppdrag än: ${vols.slice(0, 6).map(v => esc(v.name)).join(', ')}${vols.length > 6 ? ' m.fl.' : ''}</div>
            <button type="button" class="btn btn-sm btn-warning" data-plb-queue>Visa dem</button></div>`;
        const clashes = g.clashes || [];
        if (clashes.length) notes += `<div class="alert alert-danger py-2 small"><div class="fw-semibold mb-1"><i class="bi bi-exclamation-octagon me-1" aria-hidden="true"></i>Samma person på två poster samtidigt</div>`
            + clashes.slice(0, 8).map(c => `<div>· <button type="button" class="btn btn-link p-0 align-baseline fw-semibold" data-plb-person="${esc(c.personKey)}">${esc(c.name)}</button> — ${esc(c.a)} och ${esc(c.b)}${c.dateKey ? ` <span class="text-body-secondary">(${esc(dayLabel(c.dateKey))})</span>` : ''}</div>`).join('')
            + (clashes.length > 8 ? `<div class="text-body-secondary">…och ${clashes.length - 8} till</div>` : '') + '</div>';
        // Omgång 3 steg 9: krockar över hela veckan — med en annan del av veckan, en egen start eller en avsikt att tävla.
        const fam = g.familyClashes || [];
        if (fam.length) {
            const KIND = { uppdrag: 'två uppdrag samtidigt', start: 'uppdrag under egen start', avsikt: 'tänker tävla samma dag' };
            notes += `<div class="alert alert-warning py-2 small" id="plbFamilyClashes"><div class="fw-semibold mb-1"><i class="bi bi-calendar2-week me-1" aria-hidden="true"></i>Krockar i veckan</div>`
                + fam.slice(0, 10).map(c => `<div data-clash-kind="${esc(c.kind || '')}">· <button type="button" class="btn btn-link p-0 align-baseline fw-semibold" data-plb-person="${esc(c.personKey)}">${esc(c.name)}</button>
                    <span class="badge ${c.kind === 'avsikt' ? 'text-bg-light border' : 'text-bg-warning'} fw-normal">${esc(KIND[c.kind] || c.kind || '')}</span>
                    ${esc(c.a)} — ${esc(c.b)}${c.dateKey ? ` <span class="text-body-secondary">(${esc(dayLabel(c.dateKey))})</span>` : ''}</div>`).join('')
                + (fam.length > 10 ? `<div class="text-body-secondary">…och ${fam.length - 10} till</div>` : '') + '</div>';
        }
        const un = g.unreachable || [];
        if (un.length) notes += `<div class="alert alert-danger py-2 small d-flex gap-2 align-items-center flex-wrap"><div class="flex-grow-1"><i class="bi bi-exclamation-triangle-fill me-1" aria-hidden="true"></i><b>${un.length} kan inte nås alls</b> — varken konto eller e-post: ${esc(un.slice(0, 10).join(', '))}${un.length > 10 ? ' m.fl.' : ''}.</div>
            <button type="button" class="btn btn-sm btn-danger" data-plb-match>Koppla till medlemmar…</button></div>`;
        else if (g.externalCount) notes += `<p class="pl-help small mb-2"><i class="bi bi-info-circle me-1" aria-hidden="true"></i>${g.externalCount} ${g.externalCount === 1 ? 'person saknar' : 'personer saknar'} konto på pistol.nu. De kan få förfrågan med e-post, men inget eget schema och inga påminnelser förrän de har ett konto.</p>`;

        // Omgång 3 (K8): på telefon en dag i taget — tävlingsdagen först — i stället för ett rutnät som rullar i sidled.
        const phone = window.innerWidth < 576;
        let dayKey = null;
        if (phone && cols.length > 1) {
            dayKey = cols.some(c => c.key === B.phoneDay) ? B.phoneDay
                : ((cols.find(c => c.key && (!c.kind || c.kind === 'Tavlingsdag')) || cols[0]).key);
            notes += `<div class="plb-days d-flex gap-1 flex-wrap mb-2" role="group" aria-label="Välj dag">${cols.map(c =>
                `<button type="button" class="btn btn-sm ${c.key === dayKey ? 'btn-primary' : 'btn-outline-secondary'}" data-plb-day="${esc(c.key || '')}"${c.key === dayKey ? ' aria-pressed="true"' : ''}>${esc(c.label)}</button>`).join('')}</div>`;
        }
        const showCol = c => dayKey === null || c.key === dayKey;
        const shownCount = cols.filter(showCol).length;

        let head = '<th class="plb-rolecol" scope="col">Roll</th>';
        cols.forEach((c, ci) => {
            if (!showCol(c)) return;
            const prep = c.kind && c.kind !== 'Tavlingsdag';
            // Familjens axel är unionen av delarnas dagar; en dag ändras i sin del, inte här.
            const menu = c.key && !g.family ? A.rowMenu([
                { label: 'Ändra dagen…', fn: () => openDay(c) },
                cols.filter(x => x.key).length > 1 && { label: 'Kopiera dagens bemanning…', fn: () => openCopyDay(c) },
                { divider: true },
                { label: 'Ta bort dagen…', danger: true, fn: () => deleteDay(c) },
            ]) : '';
            head += `<th class="plb-cell" scope="col"><div class="d-flex justify-content-between align-items-start gap-1"><div>
                <div>${esc(c.label)}${prep ? ` <span class="badge text-bg-light border fw-normal">${esc(c.kindLabel)}</span>` : ''}${c.key && !c.isPlanned ? ' <span class="badge text-bg-warning fw-normal" title="Dagen finns inte i planen men har bemanning">ej i planen</span>' : ''}</div>
                ${c.dayLabel ? `<div class="plb-daylabel">${esc(c.dayLabel)}</div>` : ''}${c.timeLabel ? `<div class="plb-dim">${esc(c.timeLabel)}</div>` : ''}</div>${menu}</div></th>`;
        });

        const rowHtml = (row, ri) => {
            const part = g.family ? partOf(row) : null;
            const menu = A.rowMenu([
                { label: 'Antal per dag…', fn: () => openNeed(row) },
                { label: 'Byt namn…', fn: () => openRenameRole(row) },
                { divider: true },
                { label: 'Ta bort raden…', danger: true, fn: () => deleteRole(row) },
            ]);
            let h = `<tr draggable="true" data-role="${esc(row.roleKey)}" data-pid="${row.planningId || 0}"><th class="plb-rolecol" scope="row"><div class="plb-rolehead">
                <div class="plb-roletitle"><button type="button" class="plb-grip" title="Dra för att flytta raden, eller använd piltangenterna" aria-label="Flytta ${esc(row.roleName)}"><i class="bi bi-grip-vertical" aria-hidden="true"></i></button>
                <div class="min-w-0"><span class="plb-rolename">${esc(row.roleName)}</span>${row.scopeLabel ? `<div class="plb-dim">${esc(row.scopeLabel)}</div>` : ''}</div></div>${menu}</div></th>`;
            cols.forEach((c, ci) => {
                if (!showCol(c)) return;
                // Grå dag: delen har ingen planerad dag här (familjens axel är unionen). Dagen läggs till under delens meny.
                if (part && (c.key ? part.dayKeys.indexOf(c.key) < 0 : !part.hasUndated) && !((row.cells && row.cells[c.key]) || []).length) {
                    h += `<td class="plb-cell plb-offday" title="${esc(part.name)} har ingen planerad dag ${esc(c.label)}" aria-label="Ingen planerad dag i ${esc(part.name)}"><span class="plb-dim" aria-hidden="true">–</span></td>`;
                    return;
                }
                const entries = (row.cells && row.cells[c.key]) || [];
                const need = needFor(row, c.key);
                const filled = filledFor(row, c.key);
                const pcov = ((row.passCover || {})[c.key]) || [];
                let cls = 'plb-cell', tally = '';
                if (need === 0 && c.key && hasNeed(row) && !entries.length && !pcov.some(p => p.needed > 0)) tally = '<div class="plb-need plb-dim">behövs inte</div>';
                if (pcov.length) {
                    // Omgång 3 (3.2): flera pass samma dag — ett tal per pass, "FM 2/3 · EM 3/3".
                    const anyShort = pcov.some(p => p.needed > 0 && p.filled < p.needed);
                    if (pcov.some(p => p.needed > 0)) cls += anyShort ? ' plb-short' : ' plb-full';
                    tally = `<div class="plb-need plb-passes">${pcov.map(p => {
                        const s = p.needed > 0 && p.filled < p.needed;
                        return `<span class="${s ? 'text-danger fw-semibold' : (p.needed > 0 ? 'text-success' : 'plb-dim')}" data-plb-pass="${p.passId}">${esc(p.label)} ${p.filled}${p.needed > 0 ? '/' + p.needed : ''}</span>`;
                    }).join(' · ')}</div>`;
                } else if (need > 0 && c.key) {
                    const short = filled < need;
                    cls += short ? ' plb-short' : ' plb-full';
                    tally = `<div class="plb-need ${short ? 'text-danger fw-semibold' : 'text-success'}" title="Flest på plats samtidigt">${filled}/${need}${short ? ' — saknas ' + (need - filled) : ''}</div>`;
                }
                if (entries.length > filled && filled > 0) tally += `<div class="plb-dim small">${entries.length} uppdrag, högst ${filled} samtidigt</div>`;
                const holes = (row.gaps || []).filter(x => x.dateKey === c.key);
                if (holes.length) {
                    cls += ' plb-short';
                    tally += `<div class="plb-need text-danger fw-semibold"><i class="bi bi-exclamation-triangle-fill me-1" aria-hidden="true"></i>ingen ${holes.map(x => esc(x.from) + '–' + esc(x.to)).join(', ')}</div>`;
                }
                h += `<td class="${cls}">${entries.map(entryHtml).join('')}${tally}<button type="button" class="plb-add" data-plb-add="${ri}" data-col="${ci}"><i class="bi bi-plus" aria-hidden="true"></i> lägg till</button></td>`;
            });
            return h + '</tr>';
        };
        // Familjens rutnät: en rubrikrad per del (det gemensamma först), delens rader under. Radens index ri är
        // fortfarande platsen i den platta listan — det är det knapparna bär.
        const partHeader = p => {
            const menu = A.rowMenu([
                { label: 'Lägg till en roll…', fn: () => openNewRole(p.planningId) },
                { label: 'Lägg till en dag…', fn: () => openDay(null, p.planningId) },
            ]);
            return `<tr class="plb-parthead" data-part="${p.planningId}"><th colspan="${shownCount + 1}" scope="colgroup"><div class="d-flex justify-content-between align-items-center gap-2">
                <div class="min-w-0"><span class="fw-semibold">${p.isShared ? 'Gemensamt — hela veckan' : esc(p.name)}</span>
                ${p.hostLabel ? `<span class="plb-dim small ms-2">sköts av ${esc(p.hostLabel)}</span>` : ''}
                ${p.pending && !p.isShared ? '<span class="plb-dim small ms-2">visar bara roller med folk — rollerna väljs i delen</span>' : ''}</div>${menu}</div></th></tr>`;
        };
        let rows;
        if (g.family) {
            const flat = (g.rows || []).map((r, ri) => [r, ri]);
            rows = (g.parts || []).map((p, pi) => {
                const mine = flat.filter(([r]) => r.partIndex === pi);
                return partHeader(p) + (mine.length ? mine.map(([r, ri]) => rowHtml(r, ri)).join('')
                    : `<tr><td class="plb-dim small p-2" colspan="${shownCount + 1}">Inga roller än i ${p.isShared ? 'det gemensamma' : esc(p.name)}.</td></tr>`);
            }).join('');
        } else rows = (g.rows || []).map(rowHtml).join('');
        const hiddenNote = g.hiddenParts ? `<p class="pl-help small mt-2 mb-0"><i class="bi bi-eye-slash me-1" aria-hidden="true"></i>${g.hiddenParts} ${g.hiddenParts === 1 ? 'del visas inte' : 'delar visas inte'} — du får inte ändra bemanningen i ${g.hiddenParts === 1 ? 'den' : 'dem'}.</p>` : '';

        area().innerHTML = notes
            + `<div class="plb-wrap border rounded"><table class="table table-sm mb-0 plb-grid" id="plbGrid"><thead><tr>${head}</tr></thead>
                <tbody id="plbGridBody">${rows || `<tr><td class="plb-dim p-3" colspan="${shownCount + 1}">Inga roller än. Skriv den första nedan.</td></tr>`}</tbody>
                ${g.family ? '' : `<tfoot><tr><td class="plb-rolecol"><label class="visually-hidden" for="plbNewRole">Ny roll</label><input type="text" class="form-control form-control-sm" id="plbNewRole" placeholder="+ Ny roll — skriv namnet" autocomplete="off"></td>
                <td class="plb-dim small" colspan="${Math.max(1, shownCount)}">Skriv vad <em>ni</em> kallar funktionen och tryck Enter.</td></tr></tfoot>`}</table></div>
            <p class="pl-help small mt-2 mb-0">Klicka på <b>lägg till</b> i en ruta för att sätta in en person. Utan tid gäller uppdraget hela dagen. Gulmarkerade namn saknar konto på pistol.nu.${g.family ? ' Varje del har sin egen rubrik; en grå ruta betyder att delen inte har den dagen planerad.' : ''}</p>${hiddenNote}`;
        const t = $('plbGrid');
        // Rollkolumnen är smalare på telefon (se css) — samma mått här, annars rullar rutnätet i onödan.
        if (t) t.style.minWidth = phone ? '' : (260 + Math.max(1, shownCount) * 215) + 'px';
        const nr = $('plbNewRole');
        if (nr) nr.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); createRole(nr.value); } });
        wireRowOrder();
    }

    function entryHtml(e) {
        return `<div class="plb-entry"><button type="button" class="plb-person" data-plb-person="${esc(keyOf(e))}" title="Visa ${esc(e.displayName)}${e.clubName ? ', ' + esc(e.clubName) : ''}">`
            + (e.isResponsible ? '<i class="bi bi-star-fill text-warning" title="Ansvarig" aria-label="Ansvarig"></i>' : '')
            + `<span class="plb-name${e.isExternal ? ' text-warning-emphasis' : ''}">${esc(e.displayName)}</span>`
            + (e.clubName ? `<span class="plb-club">${esc(e.clubName)}</span>` : '')
            + (e.timeLabel ? `<span class="badge text-bg-light border fw-normal plb-time">${esc(e.timeLabel)}</span>` : '')
            // Omgång 3 (K8): en gul rad som säger vad det gäller, i stället för en röd triangel som såg ut som ett fel.
            + (e.isExternal && !e.hasContact ? '<span class="plb-nocontact">saknar konto och e-post</span>' : '')
            + '</button>'
            + (e.readOnly ? '' : `<button type="button" class="plb-mini" data-plb-move="${e.id}" title="Flytta ${esc(e.displayName)} till en annan dag" aria-label="Flytta till en annan dag"><i class="bi bi-arrow-left-right" aria-hidden="true"></i></button>`
                + `<button type="button" class="plb-mini text-danger" data-plb-del="${e.id}" title="Ta bort ${esc(e.displayName)} från rutan" aria-label="Ta bort från rutan">&times;</button>`)
            + '</div>';
    }
    function findEntry(id) {
        for (const r of (B.grid.rows || [])) for (const k of Object.keys(r.cells || {})) {
            const e = (r.cells[k] || []).find(x => x.id === id);
            if (e) return { entry: e, row: r, dateKey: k };
        }
        return null;
    }

    // ---- rutans snabbinmatning: skriv ett namn, Enter, nästa namn ----
    let searchTimer = null;
    function ensureEditor() {
        let ed = $('plbEditor');
        if (ed) return ed;
        ed = document.createElement('div');
        ed.id = 'plbEditor';
        ed.className = 'card shadow plb-editor';
        ed.setAttribute('role', 'dialog');
        ed.innerHTML = `<div class="card-body p-2">
            <div class="small fw-semibold mb-1" id="plbEdHead"></div>
            <label class="visually-hidden" for="plbEdName">Person</label>
            <input type="text" class="form-control form-control-sm mb-1" id="plbEdName" autocomplete="off" placeholder="Sök medlem eller skriv ett namn">
            <div class="list-group mb-1" id="plbEdSuggest" style="display:none"></div>
            <div class="d-flex gap-1 mb-1">
                <input type="text" class="form-control form-control-sm" id="plbEdFrom" data-hpsk-time inputmode="decimal" placeholder="från, t.ex. 8" aria-label="Från klockan">
                <input type="text" class="form-control form-control-sm" id="plbEdTo" data-hpsk-time inputmode="decimal" placeholder="till" aria-label="Till klockan">
            </div>
            <div class="d-flex justify-content-between align-items-center gap-2">
                <span class="plb-dim" style="font-size:.75rem">Enter sparar · Esc stänger</span>
                <div class="d-flex gap-1"><button type="button" class="btn btn-sm btn-outline-secondary" id="plbEdClose">Stäng</button><button type="button" class="btn btn-sm btn-primary" id="plbEdSave">Lägg till</button></div>
            </div>
            <div class="text-danger small mt-1" id="plbEdErr" role="alert"></div></div>`;
        document.body.appendChild(ed);
        const name = $('plbEdName');
        name.addEventListener('input', () => {
            if (B.editing) B.editing.memberId = 0;
            const q = name.value.trim();
            clearTimeout(searchTimer);
            if (q.length < 2) { hideSuggest(); return; }
            searchTimer = setTimeout(() => edSearch(q), 200);
        });
        const keys = e => {
            if (e.key === 'Enter') { e.preventDefault(); edCommit(); }
            else if (e.key === 'Escape') { e.preventDefault(); closeEditor(); }
        };
        name.addEventListener('keydown', keys);
        $('plbEdFrom').addEventListener('keydown', keys);
        $('plbEdTo').addEventListener('keydown', keys);
        $('plbEdSave').addEventListener('click', edCommit);
        $('plbEdClose').addEventListener('click', closeEditor);
        $('plbEdSuggest').addEventListener('click', e => {
            const b = e.target.closest('[data-i]'); if (!b || !B.editing) return;
            const m = (B.editing.list || [])[parseInt(b.dataset.i, 10)]; if (!m) return;
            B.editing.memberId = m.memberId;
            name.value = m.memberName;
            hideSuggest();
            $('plbEdFrom').focus();
        });
        return ed;
    }
    function hideSuggest() { const b = $('plbEdSuggest'); if (b) { b.style.display = 'none'; b.innerHTML = ''; } }
    function openEditor(btn) {
        const row = (B.grid.rows || [])[parseInt(btn.dataset.plbAdd, 10)];
        const col = (B.grid.columns || [])[parseInt(btn.dataset.col, 10)];
        if (!row || !col) return;
        const ed = ensureEditor();
        B.editing = { row, col, memberId: 0, list: [] };
        const pt = B.grid.family ? partOf(row) : null;
        $('plbEdHead').textContent = (pt ? (pt.isShared ? 'Gemensamt' : pt.name) + ' · ' : '') + row.roleName + ' · ' + (col.key ? col.label : 'Utan dag');
        $('plbEdName').value = ''; $('plbEdFrom').value = ''; $('plbEdTo').value = ''; $('plbEdErr').textContent = '';
        hideSuggest();
        ed.style.display = 'block';
        const cell = (btn.closest('td') || btn).getBoundingClientRect();
        const w = Math.min(330, window.innerWidth - 16);
        ed.style.width = w + 'px';
        let top = cell.bottom + 4, left = cell.left;
        if (top + ed.offsetHeight > window.innerHeight - 8) top = Math.max(8, cell.top - ed.offsetHeight - 4);
        if (left + w > window.innerWidth - 8) left = Math.max(8, window.innerWidth - w - 8);
        ed.style.top = top + 'px'; ed.style.left = left + 'px';
        $('plbEdName').focus();
    }
    function closeEditor() {
        const ed = $('plbEditor');
        if (ed) ed.style.display = 'none';
        B.editing = null;
        hideSuggest();
    }
    async function edSearch(q) {
        try {
            const d = await sget('SearchMembers', { query: q });
            const box = $('plbEdSuggest'); if (!box || !B.editing) return;
            const list = (d && d.success && d.members) ? d.members.slice(0, 8) : [];
            B.editing.list = list;
            box.innerHTML = list.length
                ? list.map((m, i) => `<button type="button" class="list-group-item list-group-item-action py-1 small" data-i="${i}">${esc(m.memberName)}${m.clubName ? ` <span class="plb-dim">${esc(m.clubName)}</span>` : ''}</button>`).join('')
                : '<div class="list-group-item small plb-dim">Ingen medlem heter så — Enter sparar namnet som det är skrivet.</div>';
            box.style.display = 'block';
        } catch (e) { hideSuggest(); }
    }
    async function edCommit() {
        const ed = B.editing; if (!ed) return;
        const err = $('plbEdErr'); err.textContent = '';
        const name = $('plbEdName').value.trim();
        if (!name) { closeEditor(); return; }
        const from = window.hpskNormalizeTime ? window.hpskNormalizeTime($('plbEdFrom').value) : $('plbEdFrom').value.trim();
        const to = window.hpskNormalizeTime ? window.hpskNormalizeTime($('plbEdTo').value) : $('plbEdTo').value.trim();
        if (from === null || to === null) { err.textContent = 'Skriv klockslaget till exempel 8 eller 8:30.'; return; }
        const day = ed.col.key || '';
        if ((from || to) && !day) { err.textContent = 'Tider kräver en dag.'; return; }
        const payload = {
            id: 0, memberId: ed.memberId || 0, displayName: name, roleKey: ed.row.roleKey,
            scopeType: ed.row.scopeType || null, scopeKey: ed.row.scopeKey || null,
            startsAt: from && day ? day + ' ' + from : null, endsAt: to && day ? day + ' ' + to : null,
            dayDate: day || null, status: 'Planned', allowDuplicate: !!ed.allowDup
        };
        $('plbEdSave').disabled = true;
        try {
            const d = await spost('SaveAssignment', payload, ed.row.planningId);
            if (!d.success && d.duplicate && !ed.allowDup) {
                ed.allowDup = true;
                err.textContent = (d.message || 'Personen har redan det här uppdraget.') + ' Tryck Enter igen för att lägga till ändå.';
                return;
            }
            if (!d.success) { err.textContent = d.message || 'Det gick inte att spara.'; return; }
            ed.allowDup = false; ed.memberId = 0;
            $('plbEdName').value = ''; $('plbEdFrom').value = ''; $('plbEdTo').value = '';
            await loadGrid();
            const keep = { roleKey: ed.row.roleKey, scopeKey: ed.row.scopeKey || '', colKey: ed.col.key, pid: ed.row.planningId };
            drawGrid();
            // Rutan står kvar öppen för nästa namn — det är hela poängen med ett rutnät.
            const ri = (B.grid.rows || []).findIndex(r => r.roleKey === keep.roleKey && (r.scopeKey || '') === keep.scopeKey && r.planningId === keep.pid);
            const ci = (B.grid.columns || []).findIndex(c => c.key === keep.colKey);
            const btn = document.querySelector(`[data-plb-add="${ri}"][data-col="${ci}"]`);
            if (btn) openEditor(btn);
        } catch (e) { err.textContent = e.message || 'Det gick inte att spara.'; }
        finally { const s = $('plbEdSave'); if (s) s.disabled = false; }
    }
    // Ett klick utanför rutan stänger den. composedPath: en vald träff tar bort sig själv ur sidan innan
    // klicket når hit, och då hade closest() inte hittat rutan.
    document.addEventListener('click', e => {
        if (!B.editing) return;
        const path = typeof e.composedPath === 'function' ? e.composedPath() : [];
        for (const el of path) {
            if (!el || el.nodeType !== 1) continue;
            if (el.id === 'plbEditor' || (el.classList && el.classList.contains('plb-add'))) return;
        }
        closeEditor();
    });

    // ---- radernas ordning: dra, eller piltangenterna på greppet ----
    let dragRole = null, dragPid = '0', pendingFocus = null;
    // Ordningen gäller en del i taget (familjens rutnät): raderna dras bara inom sin egen del.
    function roleOrder(pid) {
        const out = [];
        document.querySelectorAll('#plbGridBody tr[data-role]').forEach(r => {
            if (String(r.dataset.pid || 0) !== String(pid || 0)) return;
            if (out[out.length - 1] !== r.dataset.role) out.push(r.dataset.role);
        });
        return out;
    }
    function wireRowOrder() {
        const body = $('plbGridBody'); if (!body) return;
        const clear = () => body.querySelectorAll('tr').forEach(r => r.classList.remove('plb-dragging', 'plb-dropbefore', 'plb-dropafter'));
        body.addEventListener('dragstart', e => {
            const tr = e.target.closest && e.target.closest('tr[data-role]'); if (!tr) return;
            dragRole = tr.dataset.role; dragPid = tr.dataset.pid || '0'; tr.classList.add('plb-dragging');
            try { e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', dragRole); } catch (x) { }
        });
        body.addEventListener('dragend', () => { dragRole = null; clear(); });
        body.addEventListener('dragover', e => {
            if (!dragRole) return;
            e.preventDefault();
            const tr = e.target.closest && e.target.closest('tr[data-role]');
            body.querySelectorAll('tr').forEach(r => r.classList.remove('plb-dropbefore', 'plb-dropafter'));
            if (!tr || tr.dataset.role === dragRole || (tr.dataset.pid || '0') !== dragPid) return;
            const b = tr.getBoundingClientRect();
            tr.classList.add(e.clientY < b.top + b.height / 2 ? 'plb-dropbefore' : 'plb-dropafter');
        });
        body.addEventListener('drop', e => {
            if (!dragRole) return;
            e.preventDefault();
            const tr = e.target.closest && e.target.closest('tr[data-role]');
            if (!tr || tr.dataset.role === dragRole || (tr.dataset.pid || '0') !== dragPid) return;
            const b = tr.getBoundingClientRect(), before = e.clientY < b.top + b.height / 2;
            const keys = roleOrder(dragPid).filter(k => k !== dragRole), at = keys.indexOf(tr.dataset.role);
            if (at < 0) return;
            keys.splice(before ? at : at + 1, 0, dragRole);
            saveOrder(keys, parseInt(dragPid, 10) || 0);
        });
        body.addEventListener('keydown', e => {
            if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return;
            const grip = e.target.closest && e.target.closest('.plb-grip'); if (!grip) return;
            const tr = grip.closest('tr[data-role]'); if (!tr) return;
            e.preventDefault();
            const pid = tr.dataset.pid || '0';
            const keys = roleOrder(pid), i = keys.indexOf(tr.dataset.role), j = e.key === 'ArrowUp' ? i - 1 : i + 1;
            if (i < 0 || j < 0 || j >= keys.length) return;
            keys.splice(j, 0, keys.splice(i, 1)[0]);
            pendingFocus = tr.dataset.role;
            saveOrder(keys, parseInt(pid, 10) || 0);
        });
    }
    async function saveOrder(keys, pid) {
        try {
            const r = await spost('ReorderRoles', { roleKeys: keys }, pid);
            if (!r.success) { A.failMsg(r, 'Ordningen kunde inte sparas.'); return; }
            await loadGrid(); drawGrid();
            if (pendingFocus) {
                const g = document.querySelector(`#plbGridBody tr[data-role="${CSS.escape(pendingFocus)}"] .plb-grip`);
                if (g) g.focus();
                pendingFocus = null;
            }
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }

    // ---- roller ----
    async function createRole(name) {
        name = (name || '').trim(); if (!name) return;
        try {
            const d = await spost('SaveRole', { displayName: name });
            if (!d.success) { A.failMsg(d, 'Rollen kunde inte skapas.'); return; }
            B.roles = null;
            await loadGrid(); drawGrid();
            const nr = $('plbNewRole'); if (nr) nr.focus();
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }
    // I familjens rutnät hör en ny roll till en del: den som anges, annars väljs den i dialogen.
    function partPick(pid, id) {
        if (!B.grid.family || pid) return '';
        return `<div class="mb-3"><label class="form-label" for="${id}">I vilken del?</label><select class="form-select" id="${id}">${(B.grid.parts || []).map(p =>
            `<option value="${p.planningId}">${p.isShared ? 'Gemensamt — hela veckan' : esc(p.name)}</option>`).join('')}</select></div>`;
    }
    const pickedPart = (b, pid, id) => pid || (b.querySelector('#' + id) ? parseInt(b.querySelector('#' + id).value, 10) || 0 : 0);
    function openNewRole(pid) {
        A.formModal({
            title: 'Lägg till en roll', okLabel: 'Lägg till', wide: false,
            html: `${partPick(pid, 'plbRolePart')}<label class="form-label" for="plbRoleName">Vad kallar ni funktionen?</label>
                <input type="text" class="form-control" id="plbRoleName" maxlength="100" autocomplete="off" placeholder="t.ex. Markör, Kassa, Banbyggare">
                <div class="pl-help small mt-1">Rollen blir en rad i rutnätet.</div>`,
            onOpen: b => b.querySelector('#plbRoleName').focus(),
            onOk: async b => {
                const v = b.querySelector('#plbRoleName').value.trim();
                if (!v) return 'Skriv rollens namn.';
                const d = await spost('SaveRole', { displayName: v }, pickedPart(b, pid, 'plbRolePart'));
                if (!d.success) return d.message || 'Rollen kunde inte skapas.';
                B.roles = null; await loadGrid(); drawGrid();
                return true;
            }
        });
    }
    async function openRenameRole(row) {
        let roleList;
        try { roleList = B.grid.family ? await rolesIn(row.planningId) : (await ensureRoles(), B.roles || []); } catch (e) { A.showMsg(esc(e.message), 'danger'); return; }
        const role = roleList.find(r => r.key === row.roleKey) || {};
        A.formModal({
            title: 'Byt namn på rollen', okLabel: 'Spara', wide: false,
            html: `<label class="form-label" for="plbRoleName">Vad kallar ni funktionen?</label>
                <input type="text" class="form-control" id="plbRoleName" maxlength="100" autocomplete="off" value="${esc(row.roleName)}">
                <div class="pl-help small mt-1">Alla som har rollen får det nya namnet.</div>`,
            onOpen: b => { const i = b.querySelector('#plbRoleName'); i.focus(); i.select(); },
            onOk: async b => {
                const v = b.querySelector('#plbRoleName').value.trim();
                if (!v) return 'Skriv rollens namn.';
                // SaveRole skriver alla rollens flaggor — skicka med dem, annars nollställs de.
                const d = await spost('SaveRole', {
                    roleKey: row.roleKey, displayName: v, pluralName: role.plural || null,
                    defaultScopeType: role.defaultScopeType || null, supportsTargetRange: !!role.supportsTargetRange,
                    supportsFunctionTitle: !!role.supportsFunctionTitle, description: role.description || null
                }, row.planningId);
                if (!d.success) return d.message || 'Namnet kunde inte sparas.';
                B.roles = null; await loadGrid(); drawGrid();
                return true;
            }
        });
    }
    async function deleteRole(row) {
        try {
            let d = await spost('DeleteRole', { roleKey: row.roleKey, deleteAssignments: false }, row.planningId);
            if (!d.success && d.requiresConfirm) {
                const n = d.inUse || 0;
                if (!await A.plConfirm('Ta bort raden ' + row.roleName + '?', n + (n === 1 ? ' person har' : ' personer har') + ' uppdrag på raden och förlorar dem.', 'Ta bort raden', true)) return;
                d = await spost('DeleteRole', { roleKey: row.roleKey, deleteAssignments: true }, row.planningId);
            }
            if (!d.success) { A.failMsg(d, 'Raden kunde inte tas bort.'); return; }
            B.roles = null; await loadGrid(); drawGrid();
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }

    // ---- behovet per roll och dag ----
    function openNeed(row) {
        const days = colsFor(row).filter(c => c.key);
        const kinds = [...new Set(days.map(c => c.kind).filter(Boolean))];
        const word = { Tavlingsdag: 'tävlingsdagar', Forberedelse: 'förberedelsedagar', Efterarbete: 'dagar med efterarbete' };
        // Omgång 3 (3.2): en stationsrad gäller varje station — eller bara den här. Dagar med flera pass får ett antal per pass.
        const station = !!row.isStation;
        const byPass = row.neededByPass || {};
        const passInputs = c => (c.passes || []).length < 2 ? '' : `<div class="d-flex flex-wrap gap-2 justify-content-end pb-1">${c.passes.map(p =>
            `<label class="small d-flex align-items-center gap-1 mb-0">${esc(p.label)}
                <input type="number" min="0" max="999" class="form-control form-control-sm plb-need-pass" style="width:70px" inputmode="numeric" data-pass="${p.passId}" data-day="${esc(c.key)}"
                    value="${Object.prototype.hasOwnProperty.call(byPass, p.passId) ? byPass[p.passId] : needFor(row, c.key)}"></label>`).join('')}</div>`;
        A.formModal({
            title: 'Hur många behövs — ' + row.roleName + (station ? ' · ' + (row.scopeLabel || 'station') : '') + '?', okLabel: 'Spara', wide: false,
            html: `${station ? `<div class="mb-2"><label class="form-label small mb-1" for="plbNeedScope">Antalet gäller</label>
                    <select id="plbNeedScope" class="form-select form-select-sm"><option value="">Varje station</option>
                        <option value="${esc(row.scopeKey || '')}"${row.hasOwnStationNeed ? ' selected' : ''}>Bara ${esc(row.scopeLabel || 'den här stationen')}</option></select></div>` : ''}
                <div class="d-flex align-items-center gap-2 mb-2 flex-wrap"><label class="form-label mb-0" for="plbNeedAll">Samma antal alla dagar</label>
                    <input type="number" min="0" max="999" class="form-control form-control-sm" id="plbNeedAll" style="width:90px" inputmode="numeric" value="${row.needed || 0}"></div>
                ${kinds.length > 1 ? `<div class="d-flex gap-1 flex-wrap mb-3"><button type="button" class="btn btn-sm btn-outline-secondary" data-need-quick="">Alla dagar</button>${kinds.map(k => `<button type="button" class="btn btn-sm btn-outline-secondary" data-need-quick="${esc(k)}">Bara ${esc(word[k] || k)}</button>`).join('')}</div>` : ''}
                ${days.length ? days.map(c => `<div class="border-bottom py-1"><div class="d-flex justify-content-between align-items-center gap-2">
                    <label class="mb-0" for="plbNeed_${esc(c.key)}">${esc(c.label)}${c.dayLabel ? ` <span class="plb-dim small">· ${esc(c.dayLabel)}</span>` : ''}</label>
                    <input type="number" min="0" max="999" class="form-control form-control-sm plb-need-day" style="width:80px" inputmode="numeric" id="plbNeed_${esc(c.key)}" data-day="${esc(c.key)}" data-kind="${esc(c.kind || '')}" value="${needFor(row, c.key)}"></div>
                    ${passInputs(c)}</div>`).join('')
                    : '<p class="pl-help small mb-0">Planen har inga dagar än — lägg till en dag, eller ange bara antalet för alla dagar.</p>'}
                <p class="pl-help small mb-0 mt-2">0 betyder att rollen inte behövs den dagen. ${days.some(c => (c.passes || []).length > 1) ? 'Med flera pass räknas de som är på plats samtidigt under varje pass.' : 'Räknas som flest på plats samtidigt.'}</p>`,
            onOpen: b => {
                const setDay = (i, v) => { i.value = v; b.querySelectorAll(`.plb-need-pass[data-day="${i.dataset.day}"]`).forEach(p => { p.value = v; }); };
                b.querySelector('#plbNeedAll').addEventListener('input', e => b.querySelectorAll('.plb-need-day').forEach(i => setDay(i, e.target.value)));
                b.querySelectorAll('.plb-need-day').forEach(i => i.addEventListener('input', () => setDay(i, i.value)));
                b.addEventListener('click', e => {
                    const q = e.target.closest('[data-need-quick]'); if (!q) return;
                    const kind = q.dataset.needQuick, v = b.querySelector('#plbNeedAll').value || '0';
                    b.querySelectorAll('.plb-need-day').forEach(i => setDay(i, (!kind || i.dataset.kind === kind) ? v : '0'));
                });
            },
            onOk: async b => {
                const all = parseInt(b.querySelector('#plbNeedAll').value || '0', 10);
                if (isNaN(all) || all < 0 || all > 999) return 'Ange ett antal mellan 0 och 999.';
                const list = []; let bad = false;
                b.querySelectorAll('.plb-need-day').forEach(i => {
                    const n = parseInt(i.value || '0', 10);
                    if (isNaN(n) || n < 0 || n > 999) bad = true;
                    list.push({ date: i.dataset.day, count: isNaN(n) ? 0 : n });
                });
                const passes = [];
                b.querySelectorAll('.plb-need-pass').forEach(i => {
                    const n = parseInt(i.value || '0', 10);
                    if (isNaN(n) || n < 0 || n > 999) bad = true;
                    passes.push({ passId: parseInt(i.dataset.pass, 10), count: isNaN(n) ? 0 : n });
                });
                if (bad) return 'Ange ett antal mellan 0 och 999 för varje dag.';
                const scopeSel = b.querySelector('#plbNeedScope');
                const d = await spost('SetRoleNeeds', { roleKey: row.roleKey, all, days: list, passes,
                    scopeKind: station ? 'Station' : 'All', scopeKey: scopeSel ? scopeSel.value : '' }, row.planningId);
                if (!d.success) return d.message || 'Behovet kunde inte sparas.';
                await loadGrid(); drawGrid();
                return true;
            }
        });
    }

    // ---- dagarna ----
    function openDay(col, pid) {
        const kind = (col && col.kind) || 'Tavlingsdag';
        A.formModal({
            title: col ? 'Ändra dagen' : 'Lägg till en dag', okLabel: col ? 'Spara' : 'Lägg till', wide: false,
            html: `${partPick(pid, 'plbDayPart')}<div class="mb-3"><label class="form-label" for="plbDayDate">Datum</label>
                    <input type="text" class="form-control" id="plbDayDate" autocomplete="off" placeholder="ÅÅÅÅ-MM-DD" value="${col ? esc(col.key) : ''}"${col ? ' disabled' : ''}>
                    ${col ? '<div class="pl-help small mt-1">Datumet går inte att ändra. Flytta uppdragen till en ny dag i stället.</div>' : ''}</div>
                <div class="mb-3"><label class="form-label" for="plbDayLabel">Vad kallar ni dagen? <span class="plb-dim">(valfritt)</span></label>
                    <input type="text" class="form-control" id="plbDayLabel" maxlength="100" placeholder="t.ex. Iordningställande, Vapengrupp C" value="${col ? esc(col.dayLabel || '') : ''}"></div>
                <fieldset><legend class="form-label fs-6">Vad händer den dagen?</legend>
                    ${KINDS.map(([k, l]) => `<div class="form-check"><input class="form-check-input" type="radio" name="plbDayKind" id="plbDayKind_${k}" value="${k}"${k === kind ? ' checked' : ''}><label class="form-check-label" for="plbDayKind_${k}">${l}</label></div>`).join('')}
                </fieldset>
                <p class="pl-help small mb-0 mt-2">Förberedelsedagar syns inte i deltagarnas dagsprogram, bara för funktionärerna.</p>`,
            onOpen: b => { if (!col) A.datePicker(b.querySelector('#plbDayDate')); },
            onOk: async b => {
                const date = col ? col.key : b.querySelector('#plbDayDate').value.trim();
                if (!date) return 'Välj ett datum.';
                const k = (b.querySelector('input[name="plbDayKind"]:checked') || {}).value || 'Tavlingsdag';
                const d = await spost('SaveDay', { id: col ? (col.dayId || 0) : 0, date, label: b.querySelector('#plbDayLabel').value.trim(), kind: k }, pickedPart(b, pid, 'plbDayPart'));
                if (!d.success) return d.message || 'Dagen kunde inte sparas.';
                await loadGrid(); drawGrid();
                return true;
            }
        });
    }
    async function deleteDay(col) {
        try {
            let d = await spost('DeleteDay', { id: col.dayId || 0, dateKey: col.key, deleteAssignments: false });
            if (!d.success && d.requiresConfirm) {
                const n = d.inUse || 0;
                if (!await A.plConfirm('Ta bort ' + col.label + '?', n + (n === 1 ? ' uppdrag ligger' : ' uppdrag ligger') + ' på dagen och tas bort med den.', 'Ta bort dagen', true)) return;
                d = await spost('DeleteDay', { id: col.dayId || 0, dateKey: col.key, deleteAssignments: true });
            }
            if (!d.success) { A.failMsg(d, 'Dagen kunde inte tas bort.'); return; }
            await loadGrid(); drawGrid();
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }
    function dayOptions(selected, exclude, withNone, row) {
        const days = colsFor(row).filter(c => c.key && c.key !== exclude);
        return (withNone ? `<option value="">Utan dag</option>` : '')
            + days.map(c => `<option value="${esc(c.key)}"${c.key === selected ? ' selected' : ''}>${esc(c.label)}${c.dayLabel ? ' — ' + esc(c.dayLabel) : ''}</option>`).join('');
    }
    function openCopyDay(col) {
        A.formModal({
            title: 'Kopiera bemanningen från ' + col.label, okLabel: 'Kopiera', wide: false,
            html: `<label class="form-label" for="plbCopyTo">Till vilken dag?</label><select class="form-select" id="plbCopyTo">${dayOptions('', col.key, false)}</select>
                <p class="pl-help small mt-2 mb-0">Alla uppdrag den dagen kopieras. Den som redan står på samma roll den andra dagen kopieras inte en gång till.</p>`,
            onOk: async b => {
                const to = b.querySelector('#plbCopyTo').value;
                if (!to) return 'Välj en dag.';
                const d = await spost('CopyDay', { fromDate: col.key, toDate: to });
                if (!d.success) return d.message || 'Det gick inte att kopiera.';
                await loadGrid(); drawGrid();
                A.showMsg('Kopierade ' + (d.copied || 0) + ' uppdrag.' + (d.skipped ? ' ' + d.skipped + ' fanns redan.' : ''));
                return true;
            }
        });
    }
    async function moveEntry(id) {
        const f = findEntry(id); if (!f) return;
        // Familjens rutnät (B13): "Flytta raden till en annan del…" — uppdraget behåller sitt id, sin dag och sin tid.
        const fam = !!B.grid.family;
        const partSel = fam ? `<div class="mb-3"><label class="form-label" for="plbMovePart">Till vilken del?</label><select class="form-select" id="plbMovePart">${(B.grid.parts || []).map(p =>
            `<option value="${p.planningId}"${p.planningId === f.row.planningId ? ' selected' : ''}>${p.isShared ? 'Gemensamt — hela veckan' : esc(p.name)}${p.planningId === f.row.planningId ? ' (nu)' : ''}</option>`).join('')}</select></div>` : '';
        A.formModal({
            title: 'Flytta ' + f.entry.displayName, okLabel: 'Flytta', wide: false,
            html: `${partSel}<label class="form-label" for="plbMoveTo">${fam ? 'Till vilken dag? ' : 'Till vilken dag?'}${fam ? '<span class="plb-dim">(valfritt)</span>' : ''}</label>
                <select class="form-select" id="plbMoveTo">${fam ? '<option value="">Samma dag</option>' : ''}${dayOptions('', f.dateKey, false, f.row)}</select>
                <p class="pl-help small mt-2 mb-0">Rollen och klockslagen följer med.${fam ? ' Byter du del måste rollen finnas där.' : ''}</p>`,
            onOk: async b => {
                const to = b.querySelector('#plbMoveTo').value;
                const part = fam ? parseInt(b.querySelector('#plbMovePart').value, 10) || 0 : 0;
                const newPart = fam && part && part !== f.row.planningId;
                if (!to && !newPart) return fam ? 'Välj en annan del eller en annan dag.' : 'Välj en dag.';
                let pid = f.row.planningId;
                if (newPart) {
                    const m = await spost('MoveAssignmentToPlanning', { id, targetPlanningId: part }, pid);
                    if (!m.success) return m.message || 'Det gick inte att flytta.';
                    pid = part;
                    if (m.accessEnded) A.showMsg('Behörigheten i tävlingen upphörde för ' + esc(f.entry.displayName) + ' — den gällde den förra delens tävling.', 'warning');
                }
                if (to) {
                    const d = await spost('MoveAssignmentToDay', { id, date: to }, pid);
                    if (!d.success) { await refreshPart(); return d.message || 'Uppdraget flyttades, men inte till den dagen.'; }
                }
                await refreshPart();
                return true;
            }
        });
    }
    async function removeEntry(id) {
        const f = findEntry(id); if (!f) return;
        if (!await A.plConfirm('Ta bort ' + f.entry.displayName + '?', f.entry.displayName + ' tas bort från ' + f.row.roleName + ' ' + dayLabel(f.dateKey) + '.', 'Ta bort', true)) return;
        try {
            const d = await spost('DeleteAssignment', { id }, f.row.planningId);
            if (!d.success) { A.failMsg(d, 'Det gick inte att ta bort.'); return; }
            await loadGrid(); drawGrid();
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }

    // ================================================================ Personer
    function peopleSummary() {
        const m = B.meta || {};
        const parts = [`${m.totalPeople || 0} personer`, `${m.assignedCount || 0} med uppdrag`];
        if (m.needsResponseCount) parts.push(`${m.needsResponseCount} väntar på svar`);
        if (m.externalCount) parts.push(`${m.externalCount} utan konto`);
        return parts.join(' · ');
    }
    function matches(p) {
        const asg = p.assignments || [];
        if (B.show === 'queue' && !((p.volunteer || (p.availabilityLabels || []).length) && asg.length === 0)) return false;
        if (B.show === 'assigned' && !asg.length) return false;
        if (B.show === 'invited' && !asg.some(a => a.status === 'Invited')) return false;
        if (B.show === 'declined' && !asg.some(a => a.status === 'Declined')) return false;
        if (B.show === 'prep' && !(p.prep || []).length) return false;
        if (B.show === 'external' && !p.isExternal) return false;
        if (B.role && !asg.some(a => a.roleKey === B.role)) return false;
        if (B.q) {
            const hay = [p.displayName, p.clubName, p.representsName, (p.roleLabels || []).join(' '), p.state, p.phone, p.email,
                (p.volunteer && p.volunteer.comment) || ''].join(' ').toLowerCase();
            if (!hay.includes(B.q.toLowerCase())) return false;
        }
        return true;
    }
    function orgOf(p) { return p.representsName || p.clubName || 'Ingen förening'; }

    function drawPeople() {
        A.setHead('Bemanning', peopleSummary());
        A.setActions([
            { label: 'Lägg till en funktionär…', icon: 'bi-person-plus', fn: () => openAssignment({}) },
            { label: 'Koppla personer till medlemmar…', icon: 'bi-person-check', fn: openMatch },
            { label: 'E-postlista…', icon: 'bi-envelope', fn: openEmailList },
        ]);
        const m = B.meta || {};
        let notes = '';
        if (m.unassignedVolunteerCount) notes += `<div class="pl-next warn"><div class="t"><b>${m.unassignedVolunteerCount} ${m.unassignedVolunteerCount === 1 ? 'person har' : 'personer har'} anmält sig</b> utan att få ett uppdrag. De väntar på att du fördelar rollerna.</div>
            ${B.show === 'queue' ? '' : '<button type="button" class="btn btn-sm btn-warning" data-plb-show="queue">Visa dem</button>'}</div>`;
        if (m.declinedCount) notes += `<div class="pl-next warn"><div class="t">${m.declinedCount} ${m.declinedCount === 1 ? 'person har' : 'personer har'} tackat nej till ett uppdrag — platsen behöver fyllas.</div>
            ${B.show === 'declined' ? '' : '<button type="button" class="btn btn-sm btn-outline-secondary" data-plb-show="declined">Visa</button>'}</div>`;
        const roles = new Map();
        (B.people || []).forEach(p => (p.assignments || []).forEach(a => roles.set(a.roleKey, a.roleName)));
        const opt = (v, l, cur) => `<option value="${esc(v)}"${v === cur ? ' selected' : ''}>${esc(l)}</option>`;
        area().innerHTML = notes + `<div class="pl-card">
            <div class="d-flex flex-wrap gap-2 align-items-center mb-2">
                <input type="search" class="form-control form-control-sm" id="plbQ" style="max-width:240px" placeholder="Sök namn, roll, förening" aria-label="Sök" value="${esc(B.q)}">
                <select class="form-select form-select-sm" id="plbShow" style="width:auto" aria-label="Vilka som visas">
                    ${opt('', 'Alla personer', B.show)}${opt('queue', 'Anmälda utan uppdrag', B.show)}${opt('assigned', 'Har uppdrag', B.show)}${opt('invited', 'Väntar på svar', B.show)}${opt('declined', 'Har tackat nej', B.show)}${opt('prep', 'Har förberedelser', B.show)}${opt('external', 'Utan konto', B.show)}
                </select>
                <select class="form-select form-select-sm" id="plbRoleF" style="width:auto" aria-label="Roll">${opt('', 'Alla roller', B.role)}${[...roles].sort((a, b) => a[1].localeCompare(b[1], 'sv')).map(([k, n]) => opt(k, n, B.role)).join('')}</select>
                <div class="form-check form-switch mb-0 ms-sm-2"><input class="form-check-input" type="checkbox" role="switch" id="plbByOrg"${B.byOrg ? ' checked' : ''}><label class="form-check-label small" for="plbByOrg">Gruppera per förening</label></div>
                <span class="plb-dim small ms-auto" id="plbCount" aria-live="polite"></span>
            </div>
            <div id="plbPeople"></div></div>`;
        const redraw = () => { B.shown = 60; drawPeopleRows(); };
        $('plbQ').addEventListener('input', e => { B.q = e.target.value.trim(); redraw(); });
        $('plbShow').addEventListener('change', e => { B.show = e.target.value; drawPeople(); });
        $('plbRoleF').addEventListener('change', e => { B.role = e.target.value; redraw(); });
        $('plbByOrg').addEventListener('change', e => { B.byOrg = e.target.checked; redraw(); });
        drawPeopleRows();
    }

    function personRow(p) {
        const asg = p.assignments || [];
        let chips = asg.slice(0, 3).map(a => `<span class="pl-role">${a.isResponsible ? '<i class="bi bi-star-fill text-warning me-1" aria-label="Ansvarig"></i>' : ''}${esc(a.roleName)}${a.dayLabel ? ` <span class="plb-dim">· ${esc(a.dayLabel)}</span>` : ''}</span>`).join(' ');
        if (asg.length > 3) chips += ` <span class="pl-role">+${asg.length - 3}</span>`;
        if (!asg.length) chips = p.volunteer
            ? '<span class="text-warning-emphasis small"><i class="bi bi-hand-thumbs-up me-1" aria-hidden="true"></i>Anmäld — inget uppdrag än</span>'
            : ((p.availabilityLabels || []).length ? '<span class="text-warning-emphasis small">Har angett tider — inget uppdrag än</span>'
                : ((p.prep || []).length ? '<span class="plb-dim small">Bara förberedelser</span>' : '<span class="plb-dim small">—</span>'));
        const state = `<span class="pl-state ${STATE_CLS[p.state] || 'off'}">${esc(p.state)}</span>`;
        const menu = p.readOnly ? '' : A.rowMenu([
            { label: 'Öppna ' + first(p.displayName), fn: () => nav('personer', p.key) },
            { label: 'Ge ett uppdrag…', fn: () => openAssignment({ person: p }) },
            asg.some(a => !a.readOnly) && (p.memberId || p.email) && { label: 'Skicka förfrågan om uppdragen…', fn: () => notifyPerson(p) },
        ]);
        const call = p.phone ? ` <a class="btn btn-sm btn-outline-success" href="tel:${esc(p.phone)}" title="Ring ${esc(p.displayName)}" aria-label="Ring ${esc(p.displayName)}"><i class="bi bi-telephone" aria-hidden="true"></i></a>` : '';
        return `<tr><td><button type="button" class="btn btn-link p-0 fw-semibold text-start" data-plb-person="${esc(p.key)}">${esc(p.displayName)}</button>
                ${p.hasAdminAccess ? ' <i class="bi bi-shield-lock text-warning" title="Får hantera tävlingen" aria-label="Får hantera tävlingen"></i>' : ''}
                ${p.isExternal ? ' <span class="badge text-bg-light border fw-normal" title="Saknar konto på pistol.nu">utan konto</span>' : ''}
                ${!B.byOrg && orgOf(p) !== 'Ingen förening' ? `<div class="plb-dim small">${esc(orgOf(p))}</div>` : ''}
                <div class="d-md-none mt-1 d-flex gap-1 flex-wrap">${chips}</div><div class="d-sm-none mt-1">${state}</div></td>
            <td class="d-none d-md-table-cell"><div class="d-flex gap-1 flex-wrap">${chips}</div></td>
            <td class="d-none d-sm-table-cell">${state}</td>
            <td class="text-end text-nowrap">${menu}${call}</td></tr>`;
    }
    function drawPeopleRows() {
        const host = $('plbPeople'); if (!host) return;
        let list = (B.people || []).filter(matches);
        const total = (B.people || []).length;
        $('plbCount').textContent = list.length === total ? `${total} personer` : `Visar ${list.length} av ${total}`;
        if (!list.length) {
            host.innerHTML = `<p class="pl-help mb-0">${total ? 'Ingen person matchar.' : 'Ingen har ett uppdrag än. Sätt in folk i rutnätet, eller använd Hitta funktionärer.'}</p>`;
            return;
        }
        if (B.byOrg) list = list.slice().sort((a, b) => orgOf(a).localeCompare(orgOf(b), 'sv') || a.displayName.localeCompare(b.displayName, 'sv'));
        const slice = list.slice(0, B.shown);
        const orgCounts = new Map(((B.meta && B.meta.byOrganisation) || []).map(o => [o.name, o]));
        let rows = '', lastOrg = null;
        slice.forEach(p => {
            if (B.byOrg && orgOf(p) !== lastOrg) {
                lastOrg = orgOf(p);
                const n = list.filter(x => orgOf(x) === lastOrg).length, o = orgCounts.get(lastOrg);
                rows += `<tr class="plb-org"><th colspan="4" scope="rowgroup">${esc(lastOrg)} <span class="plb-dim fw-normal small">· ${n} ${n === 1 ? 'person' : 'personer'}${o ? ' · ' + o.assignments + ' uppdrag' : ''}</span></th></tr>`;
            }
            rows += personRow(p);
        });
        host.innerHTML = `<div class="table-responsive"><table class="table table-sm align-middle mb-0"><thead><tr>
                <th scope="col">Person</th><th scope="col" class="d-none d-md-table-cell">Uppdrag</th><th scope="col" class="d-none d-sm-table-cell">Läge</th><th scope="col" class="text-end"><span class="visually-hidden">Åtgärder</span></th>
            </tr></thead><tbody>${rows}</tbody></table></div>`
            + (list.length > slice.length ? `<button type="button" class="btn btn-sm btn-outline-secondary mt-2" data-plb-more>Visa ${Math.min(60, list.length - slice.length)} till (${list.length - slice.length} kvar)</button>` : '')
            + (B.byOrg ? '<p class="pl-help small mt-2 mb-0">Föreningen är den som personen ställer upp för — förvalt huvudklubben.</p>' : '');
    }

    // ---- en person ----
    function findPerson(key) {
        return (B.people || []).find(x => x.key === key)
            || (B.people || []).find(x => (x.displayName || '').toLowerCase() === String(key).replace(/^n:/, ''))
            || null;
    }
    // ---- Omgång 3 steg 9: tävlar själv (avsikterna) ----
    async function shootData() {
        return A.req('/umbraco/surface/Planning/ShootIntents?id=' + A.ID, { headers: { 'Accept': 'application/json' } });
    }
    async function loadShootCard(p) {
        const box = $('plbShoot'); if (!box) return;
        let r;
        try { r = await shootData(); } catch (e) { r = null; }
        if (!$('plbShoot')) return;
        if (!r || !r.success || !(r.targets || []).length) { box.remove(); return; }
        const mine = (r.intents || []).filter(i => i.personKey === p.key);
        const byId = new Map(r.targets.map(t => [t.planningId, t]));
        const when = t => t.from ? fmtDate(t.from) + (t.to && t.to !== t.from ? '–' + fmtDate(t.to) : '') : '';
        box.innerHTML = `<div class="pl-cardh"><h2>Tävlar själv</h2>${r.canEdit && !p.readOnly ? '<button type="button" class="btn btn-sm btn-outline-secondary" data-plb-shoot>Ändra…</button>' : ''}</div>`
            + (mine.length ? `<ul class="list-unstyled mb-0">${mine.map(i => { const t = byId.get(i.targetPlanningId) || {};
                return `<li><i class="bi bi-bullseye me-1" aria-hidden="true"></i><b>${esc(t.name || '')}</b>${i.classes ? ' · ' + esc(i.classes) : ''}${when(t) ? ` <span class="plb-dim small">· ${esc(when(t))}</span>` : ''}</li>`; }).join('')}</ul>
                <p class="pl-help small mt-2 mb-0">Uppdrag samma dagar visas som krockar i rutnätet.</p>`
                : `<p class="pl-help mb-0">${esc(first(p.displayName))} har inte sagt att hen tänker tävla i någon av veckans tävlingar.</p>`);
        const btn = box.querySelector('[data-plb-shoot]');
        if (btn) btn.addEventListener('click', () => openShoot(p, r, mine));
    }
    function fmtDate(d) {
        try { return new Date(d + 'T00:00:00').toLocaleDateString('sv-SE', { weekday: 'short', day: 'numeric', month: 'short' }); } catch (e) { return d; }
    }
    function openShoot(p, r, mine) {
        const picked = new Map(mine.map(i => [i.targetPlanningId, i.classes || '']));
        A.formModal({
            title: 'Vilka tävlingar tänker ' + first(p.displayName) + ' tävla i?', okLabel: 'Spara', wide: false,
            html: r.targets.map((t, i) => `<div class="border rounded p-2 mb-2"><div class="form-check">
                    <input class="form-check-input plb-shoot-cb" type="checkbox" id="plbSh${i}" data-t="${i}"${picked.has(t.planningId) ? ' checked' : ''}>
                    <label class="form-check-label" for="plbSh${i}"><b>${esc(t.name)}</b>${t.from ? ` <span class="plb-dim small">· ${esc(fmtDate(t.from))}</span>` : ''}</label></div>
                    <input type="text" class="form-control form-control-sm mt-1 plb-shoot-cls" data-t="${i}" maxlength="200" placeholder="Klass (valfritt), t.ex. C1" value="${esc(picked.get(t.planningId) || '')}"></div>`).join('')
                + '<p class="pl-help small mb-0">Uppdrag de dagarna visas som krockar i rutnätet, så att du kan lägga dem när hen inte skjuter.</p>',
            onOk: async b => {
                const picks = [];
                b.querySelectorAll('.plb-shoot-cb').forEach(cb => {
                    if (!cb.checked) return;
                    const t = r.targets[+cb.dataset.t];
                    const cls = b.querySelector(`.plb-shoot-cls[data-t="${cb.dataset.t}"]`);
                    picks.push({ targetPlanningId: t.planningId, classes: cls ? cls.value.trim() : '' });
                });
                const d = await A.req('/umbraco/surface/Planning/SaveShootIntents', { method: 'POST',
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'RequestVerificationToken': A.token() },
                    body: new URLSearchParams({ id: A.ID, personKey: p.key, displayName: p.displayName, picks: JSON.stringify(picks) }).toString() });
                if (!d.success) return d.message || 'Det gick inte att spara.';
                loadShootCard(p);
                return true;
            }
        });
    }

    function drawPerson(key) {
        const p = findPerson(key);
        if (!p) {
            A.setHead('Bemanning', '');
            area().innerHTML = `<p class="mb-2"><a href="#vy=bemanning&del=personer" data-plb-back>← Alla personer</a></p>
                <div class="pl-card"><p class="mb-0">Personen finns inte längre i bemanningen.</p></div>`;
            return;
        }
        const asg = p.assignments || [];
        const editable = asg.filter(a => !a.readOnly);
        const sub = [p.clubName ? esc(p.clubName) : '', p.memberId ? 'Medlem på pistol.nu' : '<span class="text-warning-emphasis">Saknar konto på pistol.nu</span>'].filter(Boolean).join(' · ');
        A.setHead(p.displayName, sub);
        A.setActions(p.readOnly ? [] : [
            { label: 'Ge ' + first(p.displayName) + ' ett uppdrag…', icon: 'bi-plus-lg', fn: () => openAssignment({ person: p }) },
            editable.length && (p.memberId || p.email) && { label: 'Skicka förfrågan om uppdragen…', icon: 'bi-send', fn: () => notifyPerson(p) },
            { divider: true },
            { label: 'Byt namn…', icon: 'bi-pencil', fn: () => openRenamePerson(p) },
            !p.memberId && { label: 'Koppla till en medlem…', icon: 'bi-person-check', fn: () => openLinkPerson(p) },
            { label: p.email ? 'Ändra e-post…' : 'Lägg till e-post…', icon: 'bi-envelope', fn: () => openEmailPerson(p) },
            asg.length && { divider: true },
            editable.length && { label: 'Lämna över alla uppdrag till någon annan…', icon: 'bi-arrow-left-right', fn: () => openReplace(p) },
        ]);

        let h = `<p class="mb-3"><a href="#vy=bemanning&del=personer" data-plb-back>← Alla personer</a></p>`;
        const orig = (asg.find(a => a.originalName) || {}).originalName;
        if (orig && orig !== p.displayName) h += `<div class="pl-next"><div class="t">Hette <b>${esc(orig)}</b> när hen skrevs in i planen.</div><button type="button" class="btn btn-sm btn-outline-secondary" data-plb-undo>Ångra kopplingen</button></div>`;

        // Kontakt
        const contact = [];
        if (p.phone) contact.push(`<a href="tel:${esc(p.phone)}"><i class="bi bi-telephone me-1" aria-hidden="true"></i>${esc(p.phone)}</a>`);
        if (p.email) contact.push(`<a href="mailto:${esc(p.email)}"><i class="bi bi-envelope me-1" aria-hidden="true"></i>${esc(p.email)}</a>`);
        if (!p.memberId) {
            h += `<div class="pl-card"><h2>Hur når vi ${esc(first(p.displayName))}?</h2>
                <p class="pl-help mb-2">Utan konto på pistol.nu får hen inget eget schema och inga påminnelser. Med e-post kan hen åtminstone få förfrågan.</p>
                ${contact.length ? `<p class="mb-2">${contact.join(' · ')}</p>` : '<p class="mb-2 text-danger">Varken konto eller e-post — hen kan inte nås genom pistol.nu.</p>'}
                <div class="d-flex gap-2 flex-wrap"><button type="button" class="btn btn-sm btn-outline-primary" data-plb-link>Koppla till en medlem…</button><button type="button" class="btn btn-sm btn-outline-secondary" data-plb-email>${p.email ? 'Ändra e-post…' : 'Lägg till e-post…'}</button></div></div>`;
        } else if (contact.length) {
            h += `<p class="mb-3">${contact.join(' · ')}</p>`;
        }

        // Uppdragen
        h += `<div class="pl-card"><div class="pl-cardh"><h2>Uppdrag (${asg.length})</h2></div>`;
        h += asg.length ? `<div class="table-responsive"><table class="table table-sm align-middle mb-0"><thead><tr><th scope="col">Roll</th><th scope="col">När</th><th scope="col" class="d-none d-sm-table-cell">Läge</th><th scope="col" class="text-end"><span class="visually-hidden">Åtgärder</span></th></tr></thead><tbody>`
            + asg.map(a => {
                const timed = a.shiftLabel && a.shiftLabel.indexOf('–') > 0;
                const menu = a.readOnly ? `<span class="badge text-bg-light border fw-normal">${esc(p.sourceLabel || 'Stationer-fliken')}</span>` : A.rowMenu([
                    { label: 'Ändra uppdraget…', fn: () => openAssignment({ id: a.id, person: p }) },
                    { label: 'Hitta ersättare…', icon: 'bi-person-plus', fn: () => openFindReplacement(a, p) },
                    timed && { label: 'Dela passet — någon annan tar resten…', fn: () => openSplit(a, p) },
                    { label: 'Flytta till en annan dag…', fn: () => moveFromPerson(a) },
                    (p.memberId || p.email) && { label: 'Skicka förfrågan om uppdraget…', fn: () => notifyOne(a, p) },
                    { divider: true },
                    { label: 'Ta bort uppdraget…', danger: true, fn: () => removeFromPerson(a, p) },
                ]);
                const st = `<span class="pl-state ${STATUS_CLS[a.status] || 'off'}">${esc(STATUS[a.status] || a.status)}</span>`;
                return `<tr><td><b>${esc(a.roleName)}</b>${a.functionTitle ? ` <span class="plb-dim">· ${esc(a.functionTitle)}</span>` : ''}${a.isResponsible ? ' <i class="bi bi-star-fill text-warning" title="Ansvarig" aria-label="Ansvarig"></i>' : ''}
                        ${a.scopeLabel && a.scopeLabel !== 'Hela tävlingen' ? `<div class="plb-dim small">${esc(a.scopeLabel)}</div>` : ''}<div class="d-sm-none mt-1">${st}</div></td>
                    <td>${a.dayLabel ? esc(a.dayLabel) + '<br>' : ''}<span class="plb-dim small">${esc(a.shiftLabel || a.passLabel || 'Hela dagen')}</span></td>
                    <td class="d-none d-sm-table-cell">${st}</td><td class="text-end">${menu}</td></tr>`;
            }).join('') + '</tbody></table></div>'
            : '<p class="pl-help mb-0">Inga uppdrag än.</p>';
        h += '</div>';

        // Behörighet — när planeringen är kopplad till en tävling, eller är en huvudplanering med delar (steg 5: gäller då alla
        // veckans tävlingar). Annars finns ingen tävling att hantera.
        if ((linked() || family()) && !p.readOnly) {
            h += `<div class="pl-card"><h2>Behörighet</h2>` + (p.memberId
                ? `<div class="form-check form-switch mb-0"><input class="form-check-input" type="checkbox" role="switch" id="plbAccess"${p.hasAdminAccess ? ' checked' : ''}>
                    <label class="form-check-label" for="plbAccess">${linked() ? 'Får hantera tävlingen i pistol.nu' : 'Får hantera alla veckans tävlingar i pistol.nu'}</label>
                    <div class="pl-help small">${linked()
                        ? 'Anmälningar, startlistor, resultat och funktionärssidorna. Gör inte personen till tävlingsledare. Gäller alla hens uppdrag.'
                            + (family() ? ' Huvudplaneringen har en egen tävling: behörigheten gäller bara den, inte delarnas tävlingar.' : '')
                        : 'Anmälningar, startlistor och funktionärssidorna i <b>varje tävling i veckan</b> — men inte ekonomin och inte att ändra själva tävlingen. Ge bara till någon du litar på. Bara huvudplaneringens sammankallande kan ge den.'}</div></div>`
                : '<p class="pl-help mb-0">Behörigheten ges till en inloggning — koppla personen till en medlem först.</p>') + '</div>';
        }

        // Ställer upp för
        const reps = p.representChoices || [];
        if (!p.readOnly) {
            const repVal = p.representsId ? (p.representsType + ':' + p.representsId) : '';
            h += `<div class="pl-card"><h2>Ställer upp för</h2>` + (reps.length
                ? `<label class="visually-hidden" for="plbRep">Förening</label><select class="form-select" id="plbRep" style="max-width:420px">
                    <option value=""${p.representsIsDefault || !p.representsId ? ' selected' : ''}>${p.memberId ? 'Huvudklubben' + (p.representsIsDefault && p.representsName ? ' (' + esc(p.representsName) + ')' : '') : 'Ingen förening'}</option>
                    ${reps.map(c => { const v = c.type + ':' + c.id; return `<option value="${esc(v)}"${!p.representsIsDefault && v === repVal ? ' selected' : ''}>${esc(c.name)}${c.type === 'region' ? ' (krets)' : ''}</option>`; }).join('')}
                  </select><div class="pl-help small mt-1">Gäller alla hens uppdrag.</div>`
                : `<p class="pl-help mb-0">${p.representsName ? esc(p.representsName) : 'Ingen förening känd. Koppla personen till en medlem, så kan du välja.'}</p>`) + '</div>';
        }

        // Omgång 3 steg 9: vilka av veckans tävlingar personen tänker tävla i — läses för sig (en lista för hela familjen).
        h += `<div class="pl-card" id="plbShoot"><h2>Tävlar själv</h2><p class="pl-help mb-0">Läser…</p></div>`;

        // Övrigt
        const extras = [];
        if (p.volunteer) extras.push(`<li><i class="bi bi-hand-thumbs-up me-1" aria-hidden="true"></i>Har anmält sig: ${esc(p.volunteer.slotsSummary || '—')}${p.volunteer.comment ? ' — ”' + esc(p.volunteer.comment) + '”' : ''}</li>`);
        (p.availabilityLabels || []).forEach(l => extras.push(`<li><i class="bi bi-calendar-week me-1" aria-hidden="true"></i>Kan: ${esc(l)}</li>`));
        (p.prep || []).forEach(x => extras.push(`<li><i class="bi bi-list-check me-1" aria-hidden="true"></i>${x.isAreaLead ? 'Ansvarig: ' + esc(x.areaName) : esc(x.itemTitle || '')}${x.isOverdue ? ' <span class="pl-state no">försenad</span>' : ''}</li>`));
        if (extras.length) h += `<div class="pl-card"><h2>Övrigt</h2><ul class="list-unstyled mb-0">${extras.join('')}</ul></div>`;

        area().innerHTML = h;
        loadShootCard(p);
        const acc = $('plbAccess');
        if (acc) acc.addEventListener('change', async () => {
            // Steg 4–6 UX: en kraftig behörighet ska inte slås på med ett klick — i huvudplaneringen frågar vi först.
            if (acc.checked && !linked() && family()) {
                acc.checked = false;
                A.formModal({
                    title: 'Ge behörighet i alla veckans tävlingar?', okLabel: 'Ge behörighet', wide: false,
                    html: `<p>${esc(first(p.displayName))} får tävlingshanteringen — anmälningar, startlistor och funktionärer — i <b>varje tävling i veckan</b>, också de som andra klubbar ansvarar för.</p>
                        <p class="pl-help small mb-0">Ekonomin och att ändra själva tävlingen ingår inte. Behörigheten upphör för en tävling när delen kopplas loss.</p>`,
                    onOk: async () => {
                        const g = await spost('SetPersonAdminAccess', { personKey: p.key, grant: true });
                        if (!g.success) return g.message || 'Behörigheten kunde inte ges.';
                        await loadPeople(); drawPerson(p.key);
                        return true;
                    }
                });
                return;
            }
            acc.disabled = true;
            try {
                const r = await spost('SetPersonAdminAccess', { personKey: p.key, grant: acc.checked });
                if (!r.success) { acc.checked = !acc.checked; A.failMsg(r, 'Behörigheten kunde inte ändras.'); }
                else { await loadPeople(); drawPerson(p.key); }
            } catch (e) { acc.checked = !acc.checked; A.showMsg(esc(e.message), 'danger'); }
            finally { const x = $('plbAccess'); if (x) x.disabled = false; }
        });
        const rep = $('plbRep');
        if (rep) rep.addEventListener('change', async () => {
            const parts = rep.value ? rep.value.split(':') : ['', '0'];
            try {
                const r = await spost('SetPersonRepresents', { personKey: p.key, type: parts[0], id: parseInt(parts[1], 10) || 0 });
                if (!r.success) { A.failMsg(r, 'Föreningen kunde inte sparas.'); return; }
                await loadPeople(); drawPerson(p.key);
            } catch (e) { A.showMsg(esc(e.message), 'danger'); }
        });
        area().querySelector('[data-plb-link]')?.addEventListener('click', () => openLinkPerson(p));
        area().querySelector('[data-plb-email]')?.addEventListener('click', () => openEmailPerson(p));
        area().querySelector('[data-plb-undo]')?.addEventListener('click', async () => {
            try {
                const r = await spost('UndoPersonIdentity', { personKey: p.key });
                if (!r.success) { A.failMsg(r, 'Det gick inte att ångra.'); return; }
                A.showMsg('Personen har sitt gamla namn igen.');
                nav('personer');
            } catch (e) { A.showMsg(esc(e.message), 'danger'); }
        });
    }

    // ---- medlemssökning i en dialog: lista under fältet, ett klick väljer ----
    function wireMemberSearch(body, inputSel, boxSel, onPick) {
        const input = body.querySelector(inputSel), box = body.querySelector(boxSel);
        let t = null, list = [];
        const hide = () => { box.innerHTML = ''; box.classList.add('d-none'); };
        input.addEventListener('input', () => {
            clearTimeout(t);
            const q = input.value.trim();
            if (q.length < 2) { hide(); return; }
            t = setTimeout(async () => {
                try {
                    const d = await sget('SearchMembers', { query: q });
                    list = (d && d.members) ? d.members.slice(0, 8) : [];
                    box.innerHTML = list.length
                        ? list.map((m, i) => `<button type="button" class="list-group-item list-group-item-action py-1" data-i="${i}">${esc(m.memberName)}${m.clubName ? ` <span class="plb-dim small">${esc(m.clubName)}</span>` : ''}</button>`).join('')
                        : '<div class="list-group-item small plb-dim">Ingen medlem heter så.</div>';
                    box.classList.remove('d-none');
                } catch (e) { hide(); }
            }, 200);
        });
        box.addEventListener('click', e => {
            const b = e.target.closest('[data-i]'); if (!b) return;
            const m = list[parseInt(b.dataset.i, 10)]; if (!m) return;
            hide(); onPick(m);
        });
    }

    function openRenamePerson(p) {
        A.formModal({
            title: 'Byt namn', okLabel: 'Spara', wide: false,
            html: `<label class="form-label" for="plbPName">Namn</label><input type="text" class="form-control" id="plbPName" maxlength="200" value="${esc(p.displayName)}">
                <p class="pl-help small mt-1 mb-0">Gäller alla ${(p.assignments || []).length} uppdrag.</p>`,
            onOpen: b => { const i = b.querySelector('#plbPName'); i.focus(); i.select(); },
            onOk: async b => {
                const v = b.querySelector('#plbPName').value.trim();
                if (!v) return 'Skriv ett namn.';
                if (v === p.displayName) return true;
                const r = await spost('ApplyPersonIdentity', { personKey: p.key, newName: v });
                if (!r.success) return r.message || 'Namnet kunde inte bytas.';
                await loadPeople();
                nav('personer', p.memberId ? p.key : 'n:' + v.toLowerCase());
                return true;
            }
        });
    }
    function openLinkPerson(p) {
        let picked = null;
        A.formModal({
            title: 'Koppla ' + p.displayName + ' till en medlem', okLabel: 'Koppla', wide: false,
            html: `<label class="form-label" for="plbLinkQ">Sök medlem</label>
                <input type="search" class="form-control" id="plbLinkQ" autocomplete="off" value="${esc(p.displayName)}">
                <div class="list-group mt-1 d-none" id="plbLinkRes"></div>
                <div class="mt-2" id="plbLinkPicked"></div>
                <p class="pl-help small mt-2 mb-0">Alla ${(p.assignments || []).length} uppdrag kopplas till medlemmen. Namnet blir medlemmens, och det gamla namnet går att få tillbaka.</p>`,
            onOpen: b => {
                const i = b.querySelector('#plbLinkQ'); i.focus(); i.dispatchEvent(new Event('input'));
                wireMemberSearch(b, '#plbLinkQ', '#plbLinkRes', m => { picked = m; b.querySelector('#plbLinkPicked').innerHTML = `Vald: <b>${esc(m.memberName)}</b>${m.clubName ? ' · ' + esc(m.clubName) : ''}`; });
            },
            onOk: async () => {
                if (!picked) return 'Välj en medlem i listan.';
                const r = await spost('ApplyPersonIdentity', { personKey: p.key, memberId: picked.memberId });
                if (!r.success) return r.message || 'Det gick inte att koppla.';
                await loadPeople();
                nav('personer', 'm:' + picked.memberId);
                return true;
            }
        });
    }
    function openEmailPerson(p) {
        A.formModal({
            title: (p.email ? 'Ändra e-post för ' : 'E-post till ') + p.displayName, okLabel: 'Spara', wide: false,
            html: `<label class="form-label" for="plbPEmail">E-postadress</label><input type="email" class="form-control" id="plbPEmail" autocomplete="off" value="${esc(p.email || '')}" placeholder="namn@exempel.se">
                <p class="pl-help small mt-1 mb-0">Med e-post kan hen få förfrågan och sitt uppdrag. Utan konto på pistol.nu får hen inget eget schema och inga påminnelser.</p>`,
            onOpen: b => b.querySelector('#plbPEmail').focus(),
            onOk: async b => {
                const v = b.querySelector('#plbPEmail').value.trim();
                if (!v) return 'Skriv en e-postadress.';
                const r = await spost('ApplyPersonIdentity', { personKey: p.key, email: v });
                if (!r.success) return r.message || 'E-posten kunde inte sparas.';
                await loadPeople(); drawPerson(p.key);
                return true;
            }
        });
    }
    function openReplace(p) {
        let picked = null;
        const n = (p.assignments || []).filter(a => !a.readOnly).length;
        A.formModal({
            title: 'Lämna över uppdragen', okLabel: 'Lämna över', wide: false,
            html: `<p class="mb-2">Om ${esc(first(p.displayName))} inte kan längre: alla ${n} uppdrag går över till någon annan. Svaren följer inte med — den nya personen står som Planerad tills hen tackat ja.</p>
                <label class="form-label" for="plbRepQ">Vem tar över?</label>
                <input type="search" class="form-control" id="plbRepQ" autocomplete="off" placeholder="Sök medlem, eller skriv ett namn">
                <div class="list-group mt-1 d-none" id="plbRepRes"></div>
                <div class="mt-2" id="plbRepPicked"></div>
                <div id="plbRepContact" class="mt-3">
                    <div class="pl-help small mb-1">Har personen inget konto? Skriv e-post och telefon, så når förfrågan och tävlingsdagens lista rätt person.</div>
                    <div class="row g-2"><div class="col-sm-7"><input type="email" class="form-control" id="plbRepEmail" autocomplete="off" placeholder="E-post (valfritt)"></div>
                        <div class="col-sm-5"><input type="tel" class="form-control" id="plbRepPhone" autocomplete="off" placeholder="Telefon (valfritt)"></div></div>
                </div>
                <div class="pl-help small mt-2">Länkar som skickats till ${esc(first(p.displayName))} slutar gälla.</div>`,
            onOpen: b => {
                const i = b.querySelector('#plbRepQ'); i.focus();
                const contact = b.querySelector('#plbRepContact');
                i.addEventListener('input', () => { picked = null; b.querySelector('#plbRepPicked').innerHTML = ''; contact.classList.remove('d-none'); });
                wireMemberSearch(b, '#plbRepQ', '#plbRepRes', m => { picked = m; i.value = m.memberName; contact.classList.add('d-none');
                    b.querySelector('#plbRepPicked').innerHTML = `Tar över: <b>${esc(m.memberName)}</b>${m.clubName ? ' · ' + esc(m.clubName) : ''}`; });
            },
            onOk: async b => {
                const typed = b.querySelector('#plbRepQ').value.trim();
                if (!picked && !typed) return 'Skriv eller välj vem som tar över.';
                // Omgång 3 (K3): hela identiteten byts — utan kontaktuppgifter står den nya utan e-post och telefon, aldrig med den förras.
                const r = await spost('ApplyPersonIdentity', picked
                    ? { personKey: p.key, memberId: picked.memberId, isReplacement: true }
                    : { personKey: p.key, newName: typed, isReplacement: true,
                        email: b.querySelector('#plbRepEmail').value.trim(), phone: b.querySelector('#plbRepPhone').value.trim() });
                if (!r.success) return r.message || 'Det gick inte att lämna över.';
                await loadPeople();
                nav('personer', picked ? 'm:' + picked.memberId : 'n:' + typed.toLowerCase());
                return true;
            }
        });
    }
    async function openSplit(a, p) {
        let picked = null;
        A.formModal({
            title: 'Dela passet ' + (a.shiftLabel || ''), okLabel: 'Dela passet', wide: false,
            html: `<p class="pl-help mb-2">${esc(first(p.displayName))} har ${esc(a.roleName)} ${esc(a.dayLabel || '')} ${esc(a.shiftLabel || '')}. Någon annan tar resten av passet.</p>
                <div class="mb-3"><label class="form-label" for="plbSplitAt">Från klockan</label><input type="text" class="form-control" id="plbSplitAt" data-hpsk-time inputmode="decimal" placeholder="t.ex. 13" style="max-width:140px"></div>
                <label class="form-label" for="plbSplitQ">Vem tar över?</label>
                <input type="search" class="form-control" id="plbSplitQ" autocomplete="off" placeholder="Sök medlem, eller skriv ett namn">
                <div class="list-group mt-1 d-none" id="plbSplitRes"></div>
                <div class="pl-help small mt-2">Den som tar över står som Planerad tills hen tackat ja.</div>`,
            onOpen: b => {
                b.querySelector('#plbSplitAt').focus();
                b.querySelector('#plbSplitQ').addEventListener('input', () => { picked = null; });
                wireMemberSearch(b, '#plbSplitQ', '#plbSplitRes', m => { picked = m; b.querySelector('#plbSplitQ').value = m.memberName; });
            },
            onOk: async b => {
                const raw = b.querySelector('#plbSplitAt').value;
                const at = window.hpskNormalizeTime ? window.hpskNormalizeTime(raw) : raw.trim();
                if (!at) return 'Skriv klockslaget där passet delas, till exempel 13.';
                const who = b.querySelector('#plbSplitQ').value.trim();
                if (!picked && !who) return 'Skriv eller välj vem som tar över.';
                const r = await spost('SplitAssignment', { id: a.id, at, memberId: picked ? picked.memberId : 0, newName: picked ? null : who });
                if (!r.success) return r.message || 'Passet kunde inte delas.';
                await loadPeople(); drawPerson(p.key);
                return true;
            }
        });
    }
    // Steg 6b (B11): Hitta ersättare. De som anmält sig till dagen i någon del av familjen, lediga och med rätt roll först.
    // Ett val lämnar över samma rad (id, dag, tid, roll behålls) och aviserar den som tar över.
    async function openFindReplacement(a, p, after) {
        const r = await sget('GetReplacementCandidates', { assignmentId: a.id }).catch(() => null);
        if (!r || !r.success) { A.showMsg(esc((r && r.message) || 'Kunde inte leta efter någon.'), 'danger'); return; }
        const list = r.candidates || [];
        const allBusy = list.length > 0 && list.every(c => c.free === false);
        let confirmedBusy = null;   // en upptagen person kräver ett andra klick — en varning, inget stopp
        const badge = c => c.free === true ? '<span class="badge text-bg-success fw-normal">Ledig</span>'
            : c.free === false ? '<span class="badge text-bg-warning fw-normal">Upptagen</span>'
            : '<span class="badge text-bg-secondary fw-normal">Tiden är inte satt</span>';
        const rows = list.length ? list.map(c => `<label class="list-group-item d-flex gap-2 align-items-start">
                <input class="form-check-input mt-1 flex-shrink-0" type="radio" name="plbRepl" value="${c.memberId}">
                <span><b>${esc(c.name)}</b> ${badge(c)}${c.roleMatch ? ' <span class="badge text-bg-light border fw-normal">Har anmält sig som ' + esc(r.target.role) + '</span>' : ''}
                ${c.free === false && c.busyWith ? `<span class="d-block small text-warning-emphasis">${esc(c.busyWith)}</span>` : ''}
                ${c.phone ? `<a class="d-block small" href="tel:${esc(c.phone)}">${esc(c.phone)}</a>` : ''}
                ${c.comment ? `<span class="d-block plb-dim small">${esc(c.comment)}</span>` : ''}</span></label>`).join('')
            : '';
        A.formModal({
            title: 'Hitta ersättare', okLabel: 'Lämna över', wide: false,
            html: `<p class="pl-help mb-2">${esc(r.target.holder)} har ${esc(r.target.role)} ${esc(r.target.when)}. Här är de som anmält sig till dagen.</p>`
                + (allBusy ? '<p class="small text-warning-emphasis mb-2"><b>Ingen ledig just nu.</b> Alla som anmält sig till dagen är upptagna den tiden. Du kan välja någon ändå, eller skicka en förfrågan under Hitta funktionärer.</p>' : '')
                + (list.length ? `<div class="list-group" role="radiogroup" aria-label="Möjliga ersättare">${rows}</div>
                    <div class="pl-help small mt-2">Den som tar över får besked och står som <b>Inbjuden</b> tills hen svarat. ${esc(r.target.holder)} får också besked om att hen inte ska komma.${r.target.hadAccess ? ' <b>Behörigheten i uppdraget följer inte med</b> — den nya personen får den inte, så ge den på nytt om den behövs.' : ''}</div>`
                    : `<p class="mb-0">Ingen har anmält sig till den dagen ännu. Skicka en förfrågan under Hitta funktionärer, eller sök en medlem under Ändra uppdraget.</p>`),
            onOk: async b => {
                const sel = b.querySelector('input[name="plbRepl"]:checked');
                if (!sel) return list.length ? 'Välj vem som tar över.' : 'Det finns ingen att lämna över till.';
                const chosen = list.find(c => String(c.memberId) === sel.value);
                if (chosen && chosen.free === false && confirmedBusy !== sel.value) {
                    confirmedBusy = sel.value;
                    return `${chosen.name} är upptagen${chosen.busyWith ? ' (' + chosen.busyWith + ')' : ''} den tiden. Tryck Lämna över igen om du vill lämna över ändå.`;
                }
                const d = await spost('ReplaceAssignmentHolder', { id: a.id, memberId: +sel.value });
                if (!d.success) return d.message || 'Uppdraget kunde inte lämnas över.';
                if (after) await after(); else { await loadPeople(); drawPerson(p.key); }
                A.showMsg(`Uppdraget är lämnat till <b>${esc(d.name)}</b>. ${d.notified ? 'Hen har fått besked.' : 'Hen kunde inte nås automatiskt — hör av dig.'}`, 'success');
                return true;
            }
        });
    }
    async function moveFromPerson(a) {
        try { await ensureGrid(); } catch (e) { A.showMsg(esc(e.message), 'danger'); return; }
        A.formModal({
            title: 'Flytta ' + a.roleName, okLabel: 'Flytta', wide: false,
            html: `<label class="form-label" for="plbMoveTo">Till vilken dag?</label><select class="form-select" id="plbMoveTo">${dayOptions('', '', false)}</select>
                <p class="pl-help small mt-2 mb-0">Rollen och klockslagen följer med.</p>`,
            onOk: async b => {
                const to = b.querySelector('#plbMoveTo').value;
                if (!to) return 'Välj en dag.';
                const d = await spost('MoveAssignmentToDay', { id: a.id, date: to });
                if (!d.success) return d.message || 'Det gick inte att flytta.';
                await refreshPart();
                return true;
            }
        });
    }
    async function removeFromPerson(a, p) {
        if (!await A.plConfirm('Ta bort uppdraget?', p.displayName + ' tas bort från ' + a.roleName + (a.dayLabel ? ' ' + a.dayLabel : '') + '.', 'Ta bort', true)) return;
        try {
            const d = await spost('DeleteAssignment', { id: a.id });
            if (!d.success) { A.failMsg(d, 'Det gick inte att ta bort.'); return; }
            await refreshPart();
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }
    async function notifyOne(a, p) {
        if (!await A.plConfirm('Skicka förfrågan till ' + p.displayName + '?', 'Hen får ett mejl' + (p.memberId ? ' och en notis' : '') + ' om ' + a.roleName + (a.dayLabel ? ' ' + a.dayLabel : '') + ', med en länk där hen svarar ja eller nej.', 'Skicka')) return;
        try {
            const d = await spost('NotifyAssignment', { id: a.id });
            if (!d.success) { A.failMsg(d, 'Förfrågan kunde inte skickas.'); return; }
            const ch = [d.email && 'e-post', d.push && 'notis'].filter(Boolean);
            A.showMsg('Förfrågan är skickad' + (ch.length ? ' (' + ch.join(' och ') + ')' : '') + '.');
            await refreshPart();
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }
    async function notifyPerson(p) {
        const list = (p.assignments || []).filter(a => !a.readOnly);
        if (!list.length) return;
        if (!await A.plConfirm('Skicka förfrågan till ' + p.displayName + '?', 'Hen får ett mejl' + (p.memberId ? ' och en notis' : '') + ' per uppdrag (' + list.length + ' st), med en länk där hen svarar ja eller nej.', 'Skicka')) return;
        let ok = 0, fail = '';
        for (const a of list) {
            try { const d = await spost('NotifyAssignment', { id: a.id }); if (d.success) ok++; else fail = d.message || fail; }
            catch (e) { fail = e.message; }
        }
        if (ok) A.showMsg(`Förfrågan är skickad om ${ok} av ${list.length} uppdrag.` + (fail ? ' ' + esc(fail) : ''));
        else A.showMsg(esc(fail || 'Förfrågan kunde inte skickas.'), 'danger');
        await refreshPart();
    }

    // ---- uppdragets dialog: person, roll, dag, tid, läge, anteckning ----
    async function openAssignment({ id, person, roleKey, dateKey }) {
        let full = null;
        try {
            await Promise.all([ensureRoles(), ensureGrid()]);
            if (id) {
                full = await fullAssignment(id);
                if (!full) { A.showMsg('Uppdraget finns inte längre.', 'danger'); return; }
            }
        } catch (e) { A.showMsg(esc(e.message), 'danger'); return; }
        let memberId = full ? (full.memberId || 0) : (person ? (person.memberId || 0) : 0);
        let lockedName = full ? full.displayName : (person ? person.displayName : '');
        let locked = !!lockedName;
        const roles = (B.roles || []).slice();
        const rk = full ? full.roleKey : (roleKey || '');
        if (rk && !roles.some(r => r.key === rk)) roles.unshift({ key: rk, name: full ? full.roleName : rk });
        const curDay = full ? (full.dateKey || '') : (dateKey || '');
        let from = '', to = '';
        if (full && full.shiftLabel && full.shiftLabel.indexOf('–') > -1) {
            const parts = full.shiftLabel.split('–'); from = parts[0].replace('från ', '').trim(); to = (parts[1] || '').trim();
        }
        const status = full ? (full.status || 'Planned') : 'Planned';
        const has = person && (person.assignments || []).length
            ? `<div class="pl-help small mb-3">${esc(first(person.displayName))} har redan: ${(person.assignments || []).map(a => esc(a.label || a.roleName)).join(' · ')}</div>` : '';
        A.formModal({
            title: full ? 'Ändra uppdraget' : (person ? 'Ge ' + first(person.displayName) + ' ett uppdrag' : 'Lägg till en funktionär'),
            okLabel: full ? 'Spara' : 'Lägg till', wide: false,
            html: `<div class="mb-3"><span class="form-label d-block">Person</span>
                    <div id="plbWhoLocked" class="d-flex justify-content-between align-items-center border rounded px-2 py-1${locked ? '' : ' d-none'}">
                        <b id="plbWhoName">${esc(lockedName)}</b>${full ? '' : '<button type="button" class="btn btn-sm btn-link p-0" id="plbWhoChange">Byt person</button>'}</div>
                    <div id="plbWhoPick"${locked ? ' class="d-none"' : ''}>
                        <label class="visually-hidden" for="plbWhoQ">Sök medlem eller skriv ett namn</label>
                        <input type="search" class="form-control" id="plbWhoQ" autocomplete="off" placeholder="Sök medlem, eller skriv namnet på någon utan konto">
                        <div class="list-group mt-1 d-none" id="plbWhoRes"></div>
                        <label class="form-label mt-2 small" for="plbWhoPhone">Telefon <span class="plb-dim">(valfritt)</span></label>
                        <input type="tel" class="form-control form-control-sm" id="plbWhoPhone" autocomplete="off" style="max-width:220px">
                    </div></div>
                ${has}
                <div class="row g-2">
                    <div class="col-sm-6"><label class="form-label" for="plbARole">Roll</label>
                        <select class="form-select" id="plbARole"><option value="">Välj roll</option>${roles.map(r => `<option value="${esc(r.key)}"${r.key === rk ? ' selected' : ''}>${esc(r.name)}</option>`).join('')}</select></div>
                    <div class="col-sm-6"><label class="form-label" for="plbADay">Dag</label>
                        <select class="form-select" id="plbADay">${dayOptions(curDay, '', true)}</select></div>
                    <div class="col-6 col-sm-3"><label class="form-label" for="plbAFrom">Från</label><input type="text" class="form-control" id="plbAFrom" data-hpsk-time inputmode="decimal" placeholder="t.ex. 8" value="${esc(from)}"></div>
                    <div class="col-6 col-sm-3"><label class="form-label" for="plbATo">Till</label><input type="text" class="form-control" id="plbATo" data-hpsk-time inputmode="decimal" placeholder="t.ex. 16" value="${esc(to)}"></div>
                    <div class="col-sm-6"><label class="form-label" for="plbAStatus">Läge</label>
                        <select class="form-select" id="plbAStatus">${Object.keys(STATUS).map(k => `<option value="${k}"${k === status ? ' selected' : ''}>${STATUS[k]}</option>`).join('')}</select></div>
                </div>
                <p class="pl-help small mt-1 mb-3">Utan tider gäller uppdraget hela dagen.</p>
                <label class="form-label" for="plbANote">Anteckning <span class="plb-dim">(valfritt)</span></label>
                <textarea class="form-control" id="plbANote" rows="2">${esc(full ? (full.note || '') : '')}</textarea>`,
            onOpen: b => {
                const change = b.querySelector('#plbWhoChange');
                if (change) change.addEventListener('click', () => {
                    locked = false; memberId = 0;
                    b.querySelector('#plbWhoLocked').classList.add('d-none');
                    b.querySelector('#plbWhoPick').classList.remove('d-none');
                    b.querySelector('#plbWhoQ').focus();
                });
                const q = b.querySelector('#plbWhoQ');
                q.addEventListener('input', () => { memberId = 0; });
                wireMemberSearch(b, '#plbWhoQ', '#plbWhoRes', m => {
                    memberId = m.memberId; q.value = m.memberName;
                    if (m.phone && !b.querySelector('#plbWhoPhone').value) b.querySelector('#plbWhoPhone').value = m.phone;
                });
                (locked ? b.querySelector('#plbARole') : q).focus();
            },
            onOk: async b => {
                const name = locked ? lockedName : b.querySelector('#plbWhoQ').value.trim();
                if (!name) return 'Välj eller skriv en person.';
                const role = b.querySelector('#plbARole').value;
                if (!role) return 'Välj en roll.';
                const day = b.querySelector('#plbADay').value;
                const norm = v => window.hpskNormalizeTime ? window.hpskNormalizeTime(v) : v.trim();
                const f = norm(b.querySelector('#plbAFrom').value), t = norm(b.querySelector('#plbATo').value);
                if (f === null || t === null) return 'Skriv klockslaget till exempel 8 eller 8:30.';
                if ((f || t) && !day) return 'Välj en dag för tiderna.';
                // Skiftet (det gamla passet) behålls bara om varken dag eller tid ändrats.
                const keepPass = full && full.passId && day === (full.dateKey || '') && f === from && t === to;
                const payload = {
                    id: full ? full.id : 0,
                    memberId: memberId || 0,
                    displayName: name,
                    phone: full ? (full.phone || null) : (locked ? (person && person.phone) || null : (b.querySelector('#plbWhoPhone').value.trim() || null)),
                    email: full ? (full.email || null) : (locked && person ? person.email || null : null),
                    roleKey: role,
                    functionTitle: full ? (full.functionTitle || null) : null,
                    scopeType: full ? (full.scopeType || null) : null,
                    scopeKey: full ? (full.scopeKey || null) : null,
                    targetFrom: full ? (full.targetFrom || 0) : 0,
                    targetTo: full ? (full.targetTo || 0) : 0,
                    passId: keepPass ? full.passId : 0,
                    startsAt: f && day ? day + ' ' + f : null,
                    endsAt: t && day ? day + ' ' + t : null,
                    dayDate: day || null,
                    isResponsible: full ? !!full.isResponsible : false,
                    hasAdminAccess: full ? !!full.hasAdminAccess : !!(person && person.hasAdminAccess),
                    status: b.querySelector('#plbAStatus').value,
                    note: b.querySelector('#plbANote').value.trim() || null,
                    allowDuplicate: !!b.dataset.allowDup
                };
                const d = await spost('SaveAssignment', payload);
                if (!d.success && d.duplicate && !b.dataset.allowDup) {
                    b.dataset.allowDup = '1';
                    return (d.message || 'Personen har redan det här uppdraget.') + ' Tryck ' + (full ? 'Spara' : 'Lägg till') + ' igen för att spara ändå.';
                }
                if (!d.success) return d.message || 'Det gick inte att spara.';
                B.grid = null;
                await refreshPart();
                return true;
            }
        });
    }

    // ---- koppla personer till medlemmar ----
    function openMatch() {
        let rows = [], confirmAll = false;
        const load = async (body) => {
            body.innerHTML = A.spinner();
            try {
                const d = await sget('SuggestPersonMatches');
                if (!d.success) { body.innerHTML = `<p class="text-danger">${esc(d.message || 'Förslagen kunde inte läsas.')}</p>`; return; }
                rows = d.people || [];
                paint(body, d);
            } catch (e) { body.innerHTML = `<p class="text-danger">${esc(e.message)}</p>`; }
        };
        const paint = (body, d) => {
            if (!rows.length) { body.innerHTML = '<p class="mb-0"><i class="bi bi-check2-circle text-success me-1" aria-hidden="true"></i>Alla i bemanningen är kopplade till en medlem.</p>'; return; }
            const sure = rows.filter(m => m.confident && (m.candidates || []).length);
            let h = `<p class="pl-help">Personer som skrivits in för hand får varken eget schema eller notiser. Här gissar vi vem som menades — <b>du bekräftar</b>. En koppling gäller alla personens uppdrag.</p>
                <p class="small mb-2">${rows.length} utan koppling · ${d.confident || 0} säkra förslag · ${d.unmatched || 0} utan träff</p>`;
            if (sure.length) h += confirmAll
                ? `<div class="pl-next"><div class="t">Koppla ${sure.length} personer?<ul class="small mb-0 mt-1">${sure.slice(0, 12).map(m => `<li>${esc(m.name)} → ${esc(m.candidates[0].name)}</li>`).join('')}${sure.length > 12 ? `<li>…och ${sure.length - 12} till</li>` : ''}</ul></div>
                    <div class="d-flex gap-2"><button type="button" class="btn btn-sm btn-primary" data-m-allyes>Ja, koppla</button><button type="button" class="btn btn-sm btn-outline-secondary" data-m-allno>Avbryt</button></div></div>`
                : `<button type="button" class="btn btn-sm btn-primary mb-3" data-m-all>Koppla alla säkra (${sure.length})</button>`;
            h += rows.map((m, i) => {
                const head = `<div class="fw-semibold">${esc(m.name)} <span class="plb-dim fw-normal small">${m.rowCount} uppdrag</span>${m.confident ? ' <span class="pl-state ok">säkert förslag</span>' : ''}</div>`;
                if (m.looksLikeTwoPeople) return `<div class="border rounded p-2 mb-2">${head}<div class="small text-warning-emphasis">Ser ut som två personer i samma ruta. Dela upp dem i två uppdrag först.</div></div>`;
                const dup = (m.inPlan || []).length
                    ? `<div class="small mt-1 text-warning-emphasis">Finns redan i planen som ${m.inPlan.map((x, j) => `<button type="button" class="btn btn-sm btn-link p-0 align-baseline fw-semibold" data-m-merge="${i}" data-j="${j}">${esc(x.name)}</button> <span class="plb-dim">(${x.rowCount} uppdrag, ${esc(x.reason)})</span>`).join(', ')} — samma person?</div>` : '';
                if (!(m.candidates || []).length) return `<div class="border rounded p-2 mb-2">${head}${dup}<div class="small plb-dim">Ingen medlem liknar namnet. Lägg till e-post på personen, så kan hen åtminstone få förfrågan.</div></div>`;
                return `<div class="border rounded p-2 mb-2">${head}${dup}<div class="d-flex gap-2 align-items-center flex-wrap mt-1">
                    <label class="visually-hidden" for="plbMSel${i}">Medlem för ${esc(m.name)}</label>
                    <select class="form-select form-select-sm" id="plbMSel${i}" style="max-width:420px"><option value="">Välj medlem</option>${m.candidates.map((c, j) => `<option value="${c.memberId}"${j === 0 && m.confident ? ' selected' : ''}>${esc(c.name)}${c.clubName ? ' — ' + esc(c.clubName) : ''} (${esc(c.reason)})</option>`).join('')}</select>
                    <button type="button" class="btn btn-sm btn-outline-primary" data-m-link="${i}">Koppla</button></div></div>`;
            }).join('');
            body.innerHTML = h;
        };
        let changed = false;
        A.formModal({
            title: 'Koppla personer till medlemmar', cancelLabel: 'Stäng',
            html: A.spinner(),
            onOpen: b => {
                load(b);
                b.addEventListener('click', async e => {
                    const t = e.target.closest('[data-m-all],[data-m-allyes],[data-m-allno],[data-m-link],[data-m-merge]'); if (!t) return;
                    if (t.hasAttribute('data-m-all')) { confirmAll = true; paint(b, { confident: rows.filter(m => m.confident).length, unmatched: rows.filter(m => !(m.candidates || []).length).length }); return; }
                    if (t.hasAttribute('data-m-allno')) { confirmAll = false; paint(b, { confident: rows.filter(m => m.confident).length, unmatched: rows.filter(m => !(m.candidates || []).length).length }); return; }
                    t.disabled = true;
                    try {
                        if (t.hasAttribute('data-m-allyes')) {
                            for (const m of rows.filter(x => x.confident && (x.candidates || []).length))
                                await spost('ApplyPersonIdentity', { personKey: m.key, memberId: m.candidates[0].memberId });
                            confirmAll = false;
                        } else if (t.hasAttribute('data-m-link')) {
                            const i = parseInt(t.dataset.mLink, 10), sel = b.querySelector('#plbMSel' + i);
                            const id = sel ? parseInt(sel.value, 10) : 0;
                            if (!id) { t.disabled = false; sel && sel.focus(); return; }
                            const r = await spost('ApplyPersonIdentity', { personKey: rows[i].key, memberId: id });
                            if (!r.success) { A.failMsg(r, 'Det gick inte att koppla.'); }
                        } else {
                            const m = rows[parseInt(t.dataset.mMerge, 10)], x = (m.inPlan || [])[parseInt(t.dataset.j, 10)];
                            const r = await spost('ApplyPersonIdentity', { personKey: m.key, newName: x.name, memberId: x.memberId > 0 ? x.memberId : 0 });
                            if (!r.success) { A.failMsg(r, 'Det gick inte att slå ihop.'); }
                        }
                        changed = true;
                        await load(b);
                    } catch (ex) { A.showMsg(esc(ex.message), 'danger'); t.disabled = false; }
                });
            }
        });
        $('plFormModal').addEventListener('hidden.bs.modal', () => { if (changed) draw(); }, { once: true });
    }

    // ---- e-postlistan ----
    function openEmailList() {
        A.formModal({
            title: 'E-postlista', cancelLabel: 'Stäng',
            html: `<div class="form-check form-switch mb-2"><input class="form-check-input" type="checkbox" role="switch" id="plbMailYes"><label class="form-check-label" for="plbMailYes">Bara de som tackat ja</label></div>
                <label class="form-label" for="plbMailBox" id="plbMailSum"></label>
                <textarea class="form-control font-monospace" id="plbMailBox" rows="6" readonly style="font-size:.85rem"></textarea>
                <div class="d-flex gap-2 flex-wrap mt-2"><button type="button" class="btn btn-sm btn-primary" id="plbMailCopy"><i class="bi bi-clipboard me-1" aria-hidden="true"></i>Kopiera adresserna</button>
                    <a class="btn btn-sm btn-outline-secondary" id="plbMailTo" href="#"><i class="bi bi-envelope-open me-1" aria-hidden="true"></i>Öppna i e-postprogrammet</a></div>
                <p class="pl-help small mt-2">Klistra in adresserna som <b>hemlig kopia (BCC)</b>, så ser funktionärerna inte varandras adresser.</p>
                <div id="plbMailMissing"></div>`,
            onOpen: b => {
                const paint = () => {
                    const onlyYes = b.querySelector('#plbMailYes').checked;
                    const rows = (B.people || []).filter(x => !onlyYes || (x.assignments || []).some(a => a.status === 'Accepted' || a.status === 'Confirmed'));
                    const seen = new Set(), addrs = [], missing = [];
                    rows.forEach(x => {
                        const e = (x.email || '').trim();
                        if (!e) { missing.push(x); return; }
                        if (seen.has(e.toLowerCase())) return;
                        seen.add(e.toLowerCase()); addrs.push(e);
                    });
                    b.querySelector('#plbMailBox').value = addrs.join('; ');
                    b.querySelector('#plbMailSum').textContent = addrs.length + (addrs.length === 1 ? ' adress' : ' adresser');
                    const to = b.querySelector('#plbMailTo');
                    to.href = 'mailto:?bcc=' + encodeURIComponent(addrs.join(','));
                    to.classList.toggle('disabled', !addrs.length);
                    b.querySelector('#plbMailMissing').innerHTML = missing.length
                        ? `<div class="alert alert-warning py-2 mb-0"><div class="fw-semibold">${missing.length} ${missing.length === 1 ? 'person nås' : 'personer nås'} inte av mejlet</div><div class="small">${missing.map(x => esc(x.displayName)).join(', ')}</div><div class="small mt-1">Lägg till e-post på personen, eller kontakta hen på annat sätt.</div></div>`
                        : '';
                };
                b.querySelector('#plbMailYes').addEventListener('change', paint);
                b.querySelector('#plbMailCopy').addEventListener('click', async e => {
                    const box = b.querySelector('#plbMailBox'); if (!box.value) return;
                    try { await navigator.clipboard.writeText(box.value); e.currentTarget.innerHTML = '<i class="bi bi-check2 me-1" aria-hidden="true"></i>Kopierat'; }
                    catch (x) { box.removeAttribute('readonly'); box.select(); box.setAttribute('readonly', 'readonly'); }
                });
                paint();
            }
        });
    }

    // ================================================================ Hitta funktionärer
    function longDate(s) {
        if (!s) return '';
        const d = new Date(s + 'T12:00:00');
        return isNaN(d) ? s : d.toLocaleDateString('sv-SE', { weekday: 'long', day: 'numeric', month: 'long' });
    }
    function signupPath() { return (B.signup && B.signup.signupPath) || ('/bemanna?p=' + A.ID); }
    async function copyLink() {
        const u = location.origin + signupPath();
        try { await navigator.clipboard.writeText(u); A.showMsg('Länken är kopierad: ' + esc(u)); }
        catch (e) { A.showMsg('Kopiera länken: ' + esc(u), 'info'); }
    }
    async function drawFind(seq) {
        const [sc, su, he] = await Promise.all([sget('GetSourceScopes'), sget('GetSignupOverview'), sget('GetHelpSlots')]);
        if (!B.scopeOptions) { const o = await sget('GetSourceScopeOptions'); B.scopeOptions = o.success ? o : { clubs: [], regions: [], suggested: [] }; }
        if (seq !== B.seq) return;
        B.scopes = sc.success ? (sc.scopes || []) : [];
        B.signup = su.success ? su : null;
        B.help = he.success ? (he.slots || []) : [];
        paintFind();
    }
    function scopeOpen(o) { return (B.scopes || []).some(s => s.scopeType === o.scopeType && String(s.scopeKey).toLowerCase() === String(o.scopeKey).toLowerCase()); }
    function paintFind() {
        const n = (B.scopes || []).length;
        A.setHead('Bemanning', 'Hitta funktionärer: vilka som får anmäla sig, vad ni behöver och förfrågan');
        A.setActions([
            { label: 'Skicka förfrågan…', icon: 'bi-send', fn: openRequest },
            { label: 'Kopiera länken till anmälan', icon: 'bi-link-45deg', fn: copyLink },
            { label: 'Visa anmälningssidan som medlem', icon: 'bi-eye', fn: () => window.open(signupPath() + '&preview=1', '_blank', 'noopener') },
            { divider: true },
            { label: 'Lägg till något annat ni behöver hjälp med…', icon: 'bi-plus-lg', fn: () => openSlot(null) },
        ]);
        const chips = (B.scopes || []).map(s => `<span class="pl-role d-inline-flex align-items-center gap-1 me-1 mb-1">${esc(s.label)}<button type="button" class="btn-close" style="font-size:.55rem" data-plb-unscope="${s.id}" aria-label="Ta bort ${esc(s.label)}"></button></span>`).join('');
        const sugg = ((B.scopeOptions && B.scopeOptions.suggested) || []).map((o, i) => scopeOpen(o) ? '' : `<button type="button" class="btn btn-sm btn-outline-success" data-plb-scope="s" data-i="${i}"><i class="bi bi-plus-lg me-1" aria-hidden="true"></i>${esc(o.label)}</button>`).join(' ');
        const d = B.signup || { needDays: [] };
        const days = d.needDays || [];
        const need = days.length ? days.map(day => `<div class="border-bottom py-1"><b>${esc(longDate(day.date))}</b> <span class="plb-dim small">· ${esc(day.kindLabel)}${day.dayLabel ? ' · ' + esc(day.dayLabel) : ''}</span>
                <div class="small">${day.roles.map(r => `${esc(r.roleName)} <span class="${r.missing > 0 ? 'text-danger fw-semibold' : 'text-success'}">${r.missing > 0 ? r.missing + ' fattas' : 'fullt'}</span>`).join(' · ')}</div></div>`).join('')
            : '<p class="pl-help mb-0">Ni har inte angett hur många som behövs. Välj <b>Antal per dag…</b> i en rolls meny i rutnätet — då ser medlemmarna vad som behövs när de anmäler sig.</p>';
        const slots = (B.help || []).length
            ? `<div class="table-responsive"><table class="table table-sm align-middle mb-0"><tbody>${B.help.map(s => `<tr><td><b>${esc(s.headline)}</b>${s.description ? `<div class="plb-dim small">${esc(s.description)}</div>` : ''}</td>
                <td class="small">${esc(longDate(s.date))}${s.startTime || s.endTime ? `<br><span class="plb-dim">${esc(s.startTime || '')}–${esc(s.endTime || '')}</span>` : ''}</td>
                <td class="text-end">${A.rowMenu([{ label: 'Ändra…', fn: () => openSlot(s) }, { divider: true }, { label: 'Ta bort…', danger: true, fn: () => deleteSlot(s) }])}</td></tr>`).join('')}</tbody></table></div>`
            : '<p class="pl-help mb-0">Inget utöver rollerna.</p>';
        const volunteers = (B.people || []).filter(p => !!p.volunteer).length, un = (B.meta && B.meta.unassignedVolunteerCount) || 0;
        area().innerHTML = `<div class="pl-card"><h2>1. Vilka får anmäla sig?</h2>
                <p class="pl-help">Medlemmar i de klubbar och kretsar du väljer kan anmäla sig. Andra kan du fortfarande sätta in själv i rutnätet.</p>
                <div class="mb-2">${chips || '<span class="pl-help">Ingen vald än.</span>'}</div>
                ${sugg ? `<div class="d-flex gap-1 flex-wrap mb-2">${sugg}</div>` : ''}
                <label class="form-label small fw-semibold mb-1" for="plbScopeQ">Lägg till en klubb eller krets</label>
                <input type="search" class="form-control form-control-sm" id="plbScopeQ" style="max-width:360px" placeholder="Skriv klubbens eller kretsens namn" autocomplete="off">
                <div id="plbScopeRes"></div></div>
            <div class="pl-card"><h2>2. Det här behöver ni hjälp med</h2>${need}
                ${d.roleChoiceSettable ? `<div class="form-check form-switch mt-3"><input class="form-check-input" type="checkbox" role="switch" id="plbRoleChoice"${d.roleChoice ? ' checked' : ''}>
                    <label class="form-check-label" for="plbRoleChoice">Medlemmen väljer vilka roller hen kan ta</label>
                    <div class="pl-help small">Av: medlemmen anger bara vilka dagar hen kan, och du väljer uppgiften.</div></div>` : ''}
                <h3 class="h6 fw-bold mt-3">Något annat ni behöver hjälp med</h3>
                <p class="pl-help small">Sådant som inte är en roll i rutnätet, till exempel att bygga banan eller baka till kiosken.</p>
                ${slots}
                <button type="button" class="btn btn-sm btn-outline-primary mt-2" data-plb-slot>Lägg till…</button></div>
            <div class="pl-card"><h2>3. Be om hjälp</h2>
                <p class="mb-2">${n ? 'Förfrågan går till ' + (B.scopes || []).map(s => esc(s.label)).join(', ') + ' och innehåller länken till anmälan.' : 'Välj först vilka som får anmäla sig (steg 1). Länken kan du dela redan nu, till exempel i klubbens grupp.'}</p>
                <div class="d-flex gap-2 flex-wrap mb-2">
                    <button type="button" class="btn btn-primary btn-sm" data-plb-request${n ? '' : ' disabled'}><i class="bi bi-send me-1" aria-hidden="true"></i>Skicka förfrågan…</button>
                    <button type="button" class="btn btn-outline-secondary btn-sm" data-plb-copylink><i class="bi bi-link-45deg me-1" aria-hidden="true"></i>Kopiera länken</button></div>
                <p class="mb-0">${volunteers ? `<b>${volunteers}</b> har anmält sig${un ? ` — <span class="text-warning-emphasis fw-semibold">${un} väntar på uppdrag</span> <button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-plb-queue>Visa dem</button>` : ' — alla har fått uppdrag'}.` : '<span class="pl-help">Ingen har anmält sig än.</span>'}</p></div>`;
        const q = $('plbScopeQ');
        q.addEventListener('input', () => {
            const v = q.value.trim().toLowerCase(), host = $('plbScopeRes'), o = B.scopeOptions || {};
            if (!v) { host.innerHTML = ''; return; }
            const all = [];
            (o.regions || []).forEach((x, i) => { if (!scopeOpen(x) && x.label.toLowerCase().includes(v)) all.push(['r', i, x]); });
            (o.clubs || []).forEach((x, i) => { if (!scopeOpen(x) && x.label.toLowerCase().includes(v)) all.push(['c', i, x]); });
            host.innerHTML = all.length
                ? `<div class="list-group mt-1" style="max-width:360px;max-height:220px;overflow:auto">${all.slice(0, 12).map(([s, i, x]) => `<button type="button" class="list-group-item list-group-item-action py-1 small" data-plb-scope="${s}" data-i="${i}">${esc(x.label)}${x.scopeType === 'Region' ? ' (krets)' : ''}</button>`).join('')}</div>${all.length > 12 ? `<div class="plb-dim small mt-1">Skriv mer för att se fler (${all.length} träffar).</div>` : ''}`
                : '<div class="plb-dim small mt-1">Ingen klubb eller krets heter så.</div>';
        });
        const rc = $('plbRoleChoice');
        if (rc) rc.addEventListener('change', async () => {
            rc.disabled = true;
            try {
                const r = await spost('SetSignupRoleChoice', { value: rc.checked ? '1' : '0' });
                if (!r.success) { rc.checked = !rc.checked; A.failMsg(r, 'Valet kunde inte sparas.'); }
                else if (B.signup) B.signup.roleChoice = r.roleChoice;
            } catch (e) { rc.checked = !rc.checked; A.showMsg(esc(e.message), 'danger'); }
            rc.disabled = false;
        });
    }
    async function addScope(src, i) {
        const o = B.scopeOptions || {};
        const x = (src === 'r' ? o.regions : src === 'c' ? o.clubs : o.suggested || [])[i]; if (!x) return;
        try {
            const r = await spost('AddSourceScope', { scopeType: x.scopeType, scopeKey: x.scopeKey || 'self' });
            if (!r.success) { A.failMsg(r, 'Det gick inte att lägga till.'); return; }
            await drawFind(B.seq);
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }
    async function removeScope(id) {
        try {
            const r = await spost('DeleteSourceScope', { id });
            if (!r.success) { A.failMsg(r, 'Det gick inte att ta bort.'); return; }
            await drawFind(B.seq);
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }
    async function openRequest() {
        let pv = null;
        try { pv = await sget('PreviewStaffRequest'); } catch (e) { A.showMsg(esc(e.message), 'danger'); return; }
        if (!pv.success) { A.failMsg(pv, 'Förfrågan kunde inte förberedas.'); return; }
        A.formModal({
            title: 'Skicka förfrågan om funktionärer', okLabel: 'Skicka', wide: false,
            html: `<p class="mb-2">Mottagare: ${(pv.audienceLabels || []).map(esc).join(', ') || '—'}</p>
                <fieldset class="mb-3"><legend class="form-label fs-6">Till vem?</legend>
                    <div class="form-check"><input class="form-check-input" type="radio" name="plbReqMode" id="plbReqRelay" value="Relay" checked>
                        <label class="form-check-label" for="plbReqRelay"><b>Klubbarnas och kretsarnas ansvariga</b> — de skickar vidare <span class="plb-dim">(${pv.relayCount || 0} e-post)</span></label></div>
                    <div class="form-check"><input class="form-check-input" type="radio" name="plbReqMode" id="plbReqDirect" value="Direct">
                        <label class="form-check-label" for="plbReqDirect"><b>Direkt till medlemmarna</b> <span class="plb-dim">(${pv.directCount || 0} medlemmar)</span></label>
                        <div class="pl-help small">${pv.directAvailable ? 'E-post skickas med klubbens utskickstjänst, notis till de som tillåtit det.' : 'Klubben har ingen utskickstjänst — då når förfrågan medlemmarna bara som notis. Skicka hellre till de ansvariga.'}</div></div>
                </fieldset>
                <label class="form-label" for="plbReqMsg">Meddelande <span class="plb-dim">(valfritt)</span></label>
                <textarea class="form-control" id="plbReqMsg" rows="3" placeholder="t.ex. Vi behöver markörer och kassa. Anmäl dig via länken."></textarea>
                ${pv.lastSent ? `<p class="pl-help small mt-2 mb-0">Senast skickat: ${esc(pv.lastSent)}</p>` : ''}`,
            onOk: async b => {
                const mode = (b.querySelector('input[name="plbReqMode"]:checked') || {}).value || 'Relay';
                const d = await spost('SendStaffRequest', { mode, message: b.querySelector('#plbReqMsg').value.trim() });
                if (!d.success) return d.message || 'Förfrågan kunde inte skickas.';
                A.showMsg(`Förfrågan är skickad: ${d.sent || 0} e-post${d.push ? ' och ' + d.push + ' notiser' : ''} (${d.recipients || 0} mottagare).`);
                return true;
            }
        });
    }
    function openSlot(s) {
        A.formModal({
            title: s ? 'Ändra' : 'Något annat ni behöver hjälp med', okLabel: s ? 'Spara' : 'Lägg till', wide: false,
            html: `<div class="mb-3"><label class="form-label" for="plbSlotHead">Vad behöver ni hjälp med?</label><input type="text" class="form-control" id="plbSlotHead" maxlength="200" placeholder="t.ex. Bygga banan" value="${esc(s ? s.headline : '')}"></div>
                <div class="row g-2"><div class="col-sm-6"><label class="form-label" for="plbSlotDate">Datum</label><input type="text" class="form-control" id="plbSlotDate" placeholder="ÅÅÅÅ-MM-DD" value="${esc(s ? s.date : '')}"></div>
                    <div class="col-6 col-sm-3"><label class="form-label" for="plbSlotFrom">Från</label><input type="text" class="form-control" id="plbSlotFrom" data-hpsk-time inputmode="decimal" placeholder="9" value="${esc(s && s.startTime ? s.startTime : '')}"></div>
                    <div class="col-6 col-sm-3"><label class="form-label" for="plbSlotTo">Till</label><input type="text" class="form-control" id="plbSlotTo" data-hpsk-time inputmode="decimal" placeholder="15" value="${esc(s && s.endTime ? s.endTime : '')}"></div></div>
                <label class="form-label mt-3" for="plbSlotDesc">Kort beskrivning <span class="plb-dim">(valfritt)</span></label>
                <textarea class="form-control" id="plbSlotDesc" rows="2" placeholder="t.ex. Ta med arbetshandskar">${esc(s && s.description ? s.description : '')}</textarea>`,
            onOpen: b => { A.datePicker(b.querySelector('#plbSlotDate')); b.querySelector('#plbSlotHead').focus(); },
            onOk: async b => {
                const payload = {
                    id: s ? s.id : 0, date: b.querySelector('#plbSlotDate').value.trim(),
                    startTime: b.querySelector('#plbSlotFrom').value.trim(), endTime: b.querySelector('#plbSlotTo').value.trim(),
                    headline: b.querySelector('#plbSlotHead').value.trim(), description: b.querySelector('#plbSlotDesc').value.trim()
                };
                if (!payload.headline) return 'Skriv vad ni behöver hjälp med.';
                if (!payload.date) return 'Välj ett datum.';
                const d = await spost('SaveHelpSlot', payload);
                if (!d.success) return d.message || 'Det gick inte att spara.';
                await drawFind(B.seq);
                return true;
            }
        });
    }
    async function deleteSlot(s) {
        if (!await A.plConfirm('Ta bort ' + s.headline + '?', 'De som anmält sig till det får det borttaget ur sina val.', 'Ta bort', true)) return;
        try {
            const d = await spost('DeleteHelpSlot', { id: s.id });
            if (!d.success) { A.failMsg(d, 'Det gick inte att ta bort.'); return; }
            await drawFind(B.seq);
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }

    // ================================================================ Veckan (steg 3)
    // Personens vecka över familjen: en rad per människa, och per människa dag för dag alla uppdrag i alla delar, egna
    // starter och avsikter, med krockar och för korta platsbyten. Valideringsytan — rutnätet är författarytan, så här
    // ändras inget utom vem personen är (kopplingen gäller hela familjen).
    const WEEK_ICON = { Assignment: 'bi-person-badge', Start: 'bi-bullseye', Intent: 'bi-flag' };
    const CLASH_TEXT = { Overlap: ['text-bg-danger', 'krockar med'], TightTravel: ['text-bg-warning', 'kort tid att ta sig till/från'], Intent: ['text-bg-light border', 'tänker tävla samma dag:'] };
    async function drawWeek(seq) {
        const d = await sget('GetFamilyWeek');
        if (seq !== B.seq) return;
        if (!d.success) throw new Error(d.message || 'Veckan kunde inte läsas.');
        B.week = d.people || [];
        B.weekStarts = !!d.startsIncluded;
        paintWeek();
    }
    function weekRows() {
        const q = (B.wq || '').trim().toLowerCase();
        const list = (B.week || []).filter(p => !q || p.name.toLowerCase().indexOf(q) >= 0);
        if (!list.length) return `<p class="plb-dim p-3 mb-0">${(B.week || []).length ? 'Ingen heter så.' : 'Ingen har något uppdrag i veckan än.'}</p>`;
        return `<div class="list-group list-group-flush">${list.map(p => `<button type="button" class="list-group-item list-group-item-action d-flex flex-wrap gap-2 align-items-baseline" data-plbw-person="${esc(p.key)}">
            <span class="fw-semibold${p.isExternal ? ' text-warning-emphasis' : ''}">${esc(p.name)}</span>
            ${p.clashes ? `<span class="badge text-bg-danger fw-normal">${p.clashes} ${p.clashes === 1 ? 'krock' : 'krockar'}</span>` : ''}
            ${p.warnings ? `<span class="badge text-bg-warning fw-normal">${p.warnings} ${p.warnings === 1 ? 'varning' : 'varningar'}</span>` : ''}
            <span class="plb-dim small ms-auto">${p.assignments} ${p.assignments === 1 ? 'uppdrag' : 'uppdrag'} · ${esc((p.days || []).map(d => d.label).join(', '))}</span></button>`).join('')}</div>`;
    }
    function paintWeek() {
        const list = B.week || [], hard = list.filter(p => p.clashes > 0).length;
        A.setHead('Bemanning', `${list.length} ${list.length === 1 ? 'person' : 'personer'}` + (hard ? ` · <span class="text-danger fw-semibold">${hard} med krock</span>` : ''));
        A.setActions([]);
        area().innerHTML = `<p class="pl-help small mb-2"><i class="bi bi-calendar2-week me-1" aria-hidden="true"></i>Varje persons vecka över alla delar. Öppna en person för dag för dag, egna starter och platsbyten. ${B.weekStarts ? 'Krock betyder att någon jobbar medan hen själv ska skjuta, eller har två uppdrag på samma tid.' : 'Egna starter räknas först när du öppnar personen.'}</p>
            <label class="visually-hidden" for="plbwQ">Sök person</label>
            <input type="search" class="form-control mb-2" id="plbwQ" placeholder="Sök person" autocomplete="off" value="${esc(B.wq || '')}">
            <div class="border rounded" id="plbwRows">${weekRows()}</div>`;
        $('plbwQ').addEventListener('input', e => { B.wq = e.target.value; $('plbwRows').innerHTML = weekRows(); });
    }
    function weekItemHtml(i) {
        const clashes = (i.clashes || []).map(c => { const t = CLASH_TEXT[c.kind] || ['text-bg-light border', c.kind]; return `<div class="small mt-1"><span class="badge ${t[0]} fw-normal">${esc(t[1])}</span> ${esc(c.with)}</div>`; }).join('');
        // Steg 6b: ett uppdrag (sourceKey "a{id}") kan lämnas över — det är där man ser att någon hoppat av.
        const rid = i.kind === 'Assignment' && /^a\d+$/.test(i.sourceKey || '') ? i.sourceKey.slice(1) : '';
        return `<li class="d-flex gap-2 py-2 border-bottom plbw-item" data-kind="${esc(i.kind)}"><i class="bi ${WEEK_ICON[i.kind] || 'bi-dot'} mt-1 ${i.hasClash ? 'text-danger' : 'text-body-secondary'}" aria-hidden="true"></i>
            <div class="min-w-0 flex-grow-1"><div>${esc(i.label)}${i.endsNextDay ? ' <span class="badge text-bg-light border fw-normal">fortsätter nästa dag</span>' : ''}${i.kind === 'Assignment' && i.status === 'Invited' ? ' <span class="badge text-bg-info fw-normal">Väntar på svar</span>' : ''}</div>
            ${i.place ? `<div class="plb-dim small"><i class="bi bi-geo-alt me-1" aria-hidden="true"></i>${esc(i.place)}</div>` : ''}${clashes}</div>
            ${rid ? `<button type="button" class="btn btn-sm btn-outline-secondary flex-shrink-0 align-self-start" data-plbw-repl="${rid}"><i class="bi bi-person-plus me-1" aria-hidden="true"></i>Hitta ersättare</button>` : ''}</li>`;
    }
    // Steg 6c (B12): när personen har tid att skjuta själv, per tävling i veckan.
    function shootWindowsHtml(list) {
        if (!list || !list.length) return '';
        return `<div class="pl-card" id="plbwShoot"><h2 class="h6 mb-1">Lediga fönster att skjuta i</h2>
            <p class="pl-help small mb-2">Tiden mellan uppdrag och egna starter, minst en timme, kl. 07–20 — så ser du när hen hinner skjuta innan startlistan är gjord.</p>
            ${list.map(t => `<div class="mb-2"><b>${esc(t.name)}</b>${t.declared ? ' <span class="badge text-bg-success fw-normal">Tänker tävla</span>' : ''}
                ${(t.days || []).map(day => `<div class="small plbw-win"><span class="plb-dim">${esc(day.label.charAt(0).toUpperCase() + day.label.slice(1))}:</span> ${day.windows.length ? day.windows.map(w => esc(w.from + '–' + w.to)).join(', ') : '<span class="text-danger">ingen ledig tid</span>'}</div>`).join('')}</div>`).join('')}</div>`;
    }
    async function drawWeekPerson(key, seq) {
        const d = await sget('GetFamilyPersonWeek', { personKey: key });
        if (seq !== B.seq) return;
        const back = '<button type="button" class="btn btn-link p-0 mb-2" data-plbw-back><i class="bi bi-arrow-left me-1" aria-hidden="true"></i>Alla personer</button>';
        if (!d.success) { A.setHead('Bemanning', ''); A.setActions([]); area().innerHTML = back + `<div class="pl-card"><p class="mb-0">${esc(d.message || 'Personen kunde inte läsas.')}</p></div>`; return; }
        B.wp = d;
        A.setHead(d.name, `${d.assignments} ${d.assignments === 1 ? 'uppdrag' : 'uppdrag'}`
            + (d.clashes ? ` · <span class="text-danger fw-semibold">${d.clashes} ${d.clashes === 1 ? 'krock' : 'krockar'}</span>` : '')
            + (d.warnings ? ` · <span class="text-warning-emphasis fw-semibold">${d.warnings} ${d.warnings === 1 ? 'varning' : 'varningar'}</span>` : ''));
        A.setActions([d.isExternal ? { label: 'Koppla till en medlem…', icon: 'bi-link-45deg', fn: () => openWeekLink(d, null) } : { label: 'Ångra koppling…', icon: 'bi-arrow-counterclockwise', fn: () => undoWeekLink(d) }]);
        let h = back;
        const m = d.match;
        if (d.isExternal && m && !m.looksLikeTwoPeople && ((m.candidates || []).length || (m.inPlan || []).length)) {
            h += `<div class="pl-next warn" id="plbwMatch"><div class="t"><b>Är det här någon du känner igen?</b> ${esc(d.name)} saknar konto. Kopplingen gäller alla uppdrag i hela veckan.</div>
                <div class="d-flex flex-wrap gap-1 mt-1">${(m.candidates || []).map((c, i) => `<button type="button" class="btn btn-sm btn-outline-primary" data-plbw-cand="${i}">Koppla till ${esc(c.name)}${c.clubName ? ' · ' + esc(c.clubName) : ''}</button>`).join('')}
                ${(m.inPlan || []).map((c, i) => `<button type="button" class="btn btn-sm btn-outline-secondary" data-plbw-inplan="${i}">Samma person som ${esc(c.name)}</button>`).join('')}</div></div>`;
        } else if (d.isExternal) h += `<p class="pl-help small"><i class="bi bi-info-circle me-1" aria-hidden="true"></i>${esc(d.name)} saknar konto på pistol.nu — inget eget schema och inga påminnelser förrän hen är kopplad.</p>`;
        h += (d.days || []).map(day => `<div class="pl-card plbw-day" data-date="${esc(day.date)}"><h2 class="h6 mb-1">${esc(day.label.charAt(0).toUpperCase() + day.label.slice(1))}</h2>
            ${(day.carriedIn || []).map(c => `<div class="plb-dim small"><i class="bi bi-moon me-1" aria-hidden="true"></i>Från kvällen före: ${esc(c.label)}</div>`).join('')}
            <ul class="list-unstyled mb-0">${(day.items || []).map(weekItemHtml).join('')}</ul></div>`).join('');
        h += shootWindowsHtml(d.shootWindows);
        h += '<p class="pl-help small mt-2"><i class="bi bi-pencil me-1" aria-hidden="true"></i>Uppdragen ändras i<button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-plb-part="rutnat">Rutnätet</button>.</p>';
        area().innerHTML = h;
    }
    async function applyWeekLink(d, body, goKey) {
        const r = await spost('ApplyFamilyPersonIdentity', Object.assign({ personKey: d.key }, body));
        if (!r.success) return r.message || 'Det gick inte att koppla.';
        A.showMsg('Klart — ' + r.affected + (r.affected === 1 ? ' uppdrag' : ' uppdrag') + ' i veckan uppdaterade.' + (r.skipped ? ' ' + r.skipped + ' del' + (r.skipped === 1 ? '' : 'ar') + ' fick du inte ändra och rördes inte.' : ''), 'success');
        nav('veckan', goKey);
        return true;
    }
    function openWeekLink(d) {
        let picked = null;
        A.formModal({
            title: 'Koppla ' + d.name + ' till en medlem', okLabel: 'Koppla', wide: false,
            html: `<label class="form-label" for="plbLinkQ">Sök medlem</label><input type="search" class="form-control" id="plbLinkQ" autocomplete="off" value="${esc(d.name)}">
                <div class="list-group mt-1 d-none" id="plbLinkRes"></div><div class="mt-2" id="plbLinkPicked"></div>
                <p class="pl-help small mt-2 mb-0">Alla ${d.assignments} uppdrag i hela veckan kopplas till medlemmen. Namnet blir medlemmens, och det gamla namnet går att få tillbaka.</p>`,
            onOpen: b => {
                const i = b.querySelector('#plbLinkQ'); i.focus(); i.dispatchEvent(new Event('input'));
                wireMemberSearch(b, '#plbLinkQ', '#plbLinkRes', m => { picked = m; b.querySelector('#plbLinkPicked').innerHTML = `Vald: <b>${esc(m.memberName)}</b>${m.clubName ? ' · ' + esc(m.clubName) : ''}`; });
            },
            onOk: async () => picked ? applyWeekLink(d, { memberId: picked.memberId }, 'm:' + picked.memberId) : 'Välj en medlem i listan.'
        });
    }
    async function undoWeekLink(d) {
        try {
            const r = await spost('UndoFamilyPersonIdentity', { personKey: d.key });
            if (!r.success) { A.failMsg(r, 'Det gick inte att ångra.'); return; }
            A.showMsg('Personen har sitt gamla namn igen.');
            nav('veckan');
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }
    document.addEventListener('click', async e => {
        if (!A || !A.isActive() || part() !== 'veckan') return;
        const t = e.target.closest('[data-plbw-person],[data-plbw-back],[data-plbw-cand],[data-plbw-inplan],[data-plbw-repl]');
        if (!t || !$('plBody').contains(t)) return;
        e.preventDefault();
        if (t.dataset.plbwRepl && B.wp) { const key = B.wp.key; openFindReplacement({ id: parseInt(t.dataset.plbwRepl, 10) }, { key }, () => nav('veckan', key)); }
        else if (t.dataset.plbwPerson) nav('veckan', t.dataset.plbwPerson);
        else if (t.hasAttribute('data-plbw-back')) nav('veckan');
        else if (t.dataset.plbwCand != null && B.wp) {
            const c = ((B.wp.match || {}).candidates || [])[parseInt(t.dataset.plbwCand, 10)]; if (!c) return;
            const res = await applyWeekLink(B.wp, { memberId: c.memberId }, 'm:' + c.memberId);
            if (typeof res === 'string') A.showMsg(esc(res), 'danger');
        } else if (t.dataset.plbwInplan != null && B.wp) {
            const c = ((B.wp.match || {}).inPlan || [])[parseInt(t.dataset.plbwInplan, 10)]; if (!c) return;
            const res = await applyWeekLink(B.wp, c.memberId > 0 ? { memberId: c.memberId } : { newName: c.name }, c.memberId > 0 ? 'm:' + c.memberId : 'n:' + c.name.trim().toLowerCase());
            if (typeof res === 'string') A.showMsg(esc(res), 'danger');
        }
    });

    // ================================================================ Klick i delen (delegerat)
    document.addEventListener('click', e => {
        if (!A || !A.isActive()) return;
        const t = e.target.closest('[data-plb-part],[data-plb-person],[data-plb-add],[data-plb-move],[data-plb-del],[data-plb-queue],[data-plb-match],[data-plb-show],[data-plb-more],[data-plb-back],[data-plb-scope],[data-plb-unscope],[data-plb-slot],[data-plb-request],[data-plb-copylink],[data-plb-day]');
        if (!t || !$('plBody').contains(t)) return;
        e.preventDefault();
        if (t.hasAttribute('data-plb-day')) { B.phoneDay = t.dataset.plbDay; closeEditor(); drawGrid(); }
        else if (t.dataset.plbPart) { B.show = ''; nav(t.dataset.plbPart); }
        else if (t.dataset.plbPerson) { closeEditor(); nav(family() && part() !== 'personer' ? 'veckan' : 'personer', t.dataset.plbPerson); }
        else if (t.hasAttribute('data-plb-add')) openEditor(t);
        else if (t.dataset.plbMove) moveEntry(parseInt(t.dataset.plbMove, 10));
        else if (t.dataset.plbDel) removeEntry(parseInt(t.dataset.plbDel, 10));
        else if (t.hasAttribute('data-plb-queue')) { B.show = 'queue'; B.q = ''; B.role = ''; nav('personer'); }
        else if (t.hasAttribute('data-plb-match')) openMatch();
        else if (t.dataset.plbShow) { B.show = t.dataset.plbShow; drawPeople(); }
        else if (t.hasAttribute('data-plb-more')) { B.shown += 60; drawPeopleRows(); }
        else if (t.hasAttribute('data-plb-back')) nav('personer');
        else if (t.dataset.plbScope) addScope(t.dataset.plbScope, parseInt(t.dataset.i, 10));
        else if (t.dataset.plbUnscope) removeScope(parseInt(t.dataset.plbUnscope, 10));
        else if (t.hasAttribute('data-plb-slot')) openSlot(null);
        else if (t.hasAttribute('data-plb-request')) openRequest();
        else if (t.hasAttribute('data-plb-copylink')) copyLink();
    });

    // ================================================================ Läs om den del som syns
    async function refreshPart() {
        const p = part(), pk = personKeyParam();
        try {
            if (p === 'rutnat') { await loadGrid(); drawGrid(); }
            else if (p === 'personer') { await loadPeople(); B.grid = null; pk ? drawPerson(pk) : drawPeopleKeepFilter(); }
            else if (p === 'veckan') await draw();
            else { await loadPeople(); await drawFind(B.seq); }
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }
    // Listan ritas om utan att sök och filter tappas (fältet man skriver i står kvar).
    function drawPeopleKeepFilter() {
        if (!$('plbPeople')) { drawPeople(); return; }
        A.setHead('Bemanning', peopleSummary());
        drawPeopleRows();
    }

    // Pollningen: flera kan bygga bemanningen samtidigt (planeringsmötet). Aldrig medan någon skriver,
    // har en dialog eller meny öppen, eller står i rutans snabbinmatning.
    function startPoll() { if (!B.timer) B.timer = setInterval(poll, 15000); }
    function stopPoll() { if (B.timer) { clearInterval(B.timer); B.timer = null; } }
    async function poll() {
        if (!A || !A.isActive() || B.polling || document.visibilityState !== 'visible') return;
        if (B.editing || document.querySelector('.modal.show') || document.querySelector('#plBody .dropdown-menu.show')) return;
        const ae = document.activeElement;
        if (ae && $('plBody').contains(ae) && /^(INPUT|TEXTAREA|SELECT)$/.test(ae.tagName)) return;
        B.polling = true;
        try {
            const p = part();
            if (p === 'rutnat') {
                const d = await fetchGrid();
                if (JSON.stringify(d) !== JSON.stringify(B.grid)) { B.grid = d; drawGrid(); }
            } else if (p === 'personer') {
                const d = await sget('GetPeople');
                if (d.success && JSON.stringify(d.people) !== JSON.stringify(B.people)) {
                    B.people = d.people || []; B.meta = d;
                    const pk = personKeyParam();
                    pk ? drawPerson(pk) : drawPeopleKeepFilter();
                }
            }
        } catch (e) { /* tyst: nästa varv försöker igen */ }
        finally { B.polling = false; }
    }

    return { render, leave };
})();
