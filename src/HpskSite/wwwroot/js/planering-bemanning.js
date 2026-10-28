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
        timer: null, seq: 0, editing: null, polling: false,
        weekMap: null, wTok: 0, wp: null, pre: null, solo: null
    };
    // Stefan 2026-10-28: en placering tilldelas, den begärs inte — ingen ja/nej. Planned = "Placerad"; har personen fått besked (notifiedAt) står det "Meddelad".
    const STATUS = { Planned: 'Placerad', Accepted: 'Anmäld själv', Confirmed: 'Bekräftad' };
    const STATUS_CLS = { Planned: 'off', Accepted: 'blue', Confirmed: 'ok' };
    const STATE_CLS = { 'Anmäld': 'wait', 'Ingen placering': 'off', 'Placerad': 'off', 'Meddelad': 'blue', 'Anmäld själv': 'blue', 'Bekräftad': 'ok', 'Förberedelser': 'off' };
    // Ett gammalt Invited/Declined som skulle ligga kvar läses som en vanlig placering (servern normaliserar redan, det här är bältet till hängslena).
    const asStatus = s => (s === 'Accepted' || s === 'Confirmed') ? s : 'Planned';
    const statusLabel = (status, notifiedAt) => { const s = asStatus(status); return s === 'Planned' && notifiedAt ? 'Meddelad' : STATUS[s]; };
    const statusCls = (status, notifiedAt) => { const s = asStatus(status); return s === 'Planned' && notifiedAt ? 'blue' : STATUS_CLS[s]; };
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
    // Personens dag för dag (egna starter och krockar) finns på personens sida under Personer — för en familj, och för varje planering som
    // hör ihop med en tävling: där finns startlistan, och "jobbar hen när hen själv ska skjuta?" är frågan (persontestet, scenario 6).
    // Det här är bara villkoret för att HÄMTA datan; den är ingen flik. Servern läser veckan för en ensam planering.
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
            g.volunteersTotal = (g.volunteersTotal || 0) + (pg.volunteersTotal || 0);
            g.volunteersWithJob = (g.volunteersWithJob || 0) + (pg.volunteersWithJob || 0);
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
        if (!d.success) throw new Error(d.message || 'Placeringen kunde inte läsas.');
        for (const g of (d.groups || [])) for (const a of (g.assignments || [])) if (a.id === id) return a;
        return null;
    }

    // ---------------------------------------------------------------- adressen
    function hp() { return new URLSearchParams((location.hash || '').replace(/^#/, '')); }
    // En gammal adress (#del=veckan) landar i Personer, med samma person om en angavs.
    function part() { const d = hp().get('del'); return d === 'veckan' ? 'personer' : (d === 'personer' || d === 'hitta' ? d : 'rutnat'); }
    function personKeyParam() { return part() === 'personer' ? (hp().get('person') || '') : ''; }
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
    function leave() { stopPoll(); }

    async function draw() {
        const seq = ++B.seq, p = part(), pk = personKeyParam();
        if (hp().get('del') === 'veckan') history.replaceState(null, '', '#vy=bemanning&del=personer' + (pk ? '&person=' + encodeURIComponent(pk) : ''));
        A.setActions([]);
        const body = $('plBody');
        body.innerHTML = switchHtml(p) + '<div id="plbArea">' + A.spinner() + '</div>';
        try {
            if (p === 'rutnat') {
                await loadGrid(); if (seq !== B.seq) return; drawGrid();
                // Länk från Startlistor i Hantera tävlingen (#vy=bemanning&oppna=startlista): öppna startlistedialogen direkt, en gång.
                if (hp().get('oppna') === 'startlista') {
                    history.replaceState(null, '', '#vy=bemanning');
                    if (B.grid && B.grid.patrolPlanAvailable && !B.grid.family) openPatrolPlan();
                    else A.showMsg('Startlistan kan inte byggas om härifrån: planeringen är inte kopplad till en fälttävling, eller så saknar du tävlingens personalbehörighet.', 'warning');
                }
            }
            else if (p === 'personer') { await loadPeople(); if (seq !== B.seq) return; if (pk) drawPerson(pk); else { drawPeople(); loadWeekList(seq); } }
            else { await loadPeople(); if (seq !== B.seq) return; await drawFind(seq); }
        } catch (e) {
            if (seq !== B.seq) return;
            A.setHead('Bemanning', '');
            area().innerHTML = `<div class="pl-card"><p class="text-danger mb-0">${esc(e.message || 'Bemanningen kunde inte läsas.')}</p></div>`;
        }
    }
    function switchHtml(p) {
        const b = (k, l) => `<button type="button" class="btn ${p === k ? 'btn-primary' : 'btn-outline-primary'}" data-plb-part="${k}" aria-pressed="${p === k}">${l}</button>`;
        return `<div class="btn-group plb-switch mb-3" role="group" aria-label="Bemanningens delar">${b('rutnat', 'Rutnät')}${b('personer', 'Personer')}${b('hitta', 'Hitta funktionärer')}</div>`;
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
        // Planera dagen (persontestet): "11 rutor saknar folk" räknade rader, inte personer — tävlingsledaren räknade själv ut att det saknades 14.
        // Nu räknas PLATSER: behov minus bemannat per roll och dag (per pass när dagen har flera pass).
        const missingFor = (r, c) => {
            const pc = ((r.passCover || {})[c.key]) || [];
            if (pc.some(p => p.needed > 0)) return pc.reduce((a, p) => a + Math.max(0, (p.needed || 0) - (p.filled || 0)), 0);
            return Math.max(0, needFor(r, c.key) - filledFor(r, c.key));
        };
        // Platserna räknas på servern (GridResponse.PlacesMissing) — EN räkning för rubriken och Dagens plan. JS-räkningen finns kvar bara som reserv för en familjs sammanslagna rutnät.
        const places = g.placesMissing != null && !g.family ? g.placesMissing : (g.rows || []).reduce((acc, r) => acc + cols.reduce((a, c) => a + missingFor(r, c), 0), 0);
        const holes = (g.rows || []).reduce((a, r) => a + (r.gaps || []).length, 0);
        const parts = [`${g.totalAssigned || 0} placeringar`, `${(g.rows || []).length} roller`, `${cols.length} ${cols.length === 1 ? 'dag' : 'dagar'}`];
        if (places) parts.push(`<span class="text-danger fw-semibold" data-plb-missing="${places}">${places} ${places === 1 ? 'plats saknas' : 'platser saknas'}</span>`);
        // "Lucka" = en stund mellan två uppdrag på samma post då ingen står där (persontest 6b: ordet förklarades ingenstans).
        if (holes) parts.push(`<span class="text-danger fw-semibold" title="Tider då ingen står på posten, mellan två placeringar samma dag">${holes} ${holes === 1 ? 'obemannad tid' : 'obemannade tider'} mellan placeringarna</span>`);
        if (g.volunteersTotal > 0) {
            parts.push(`<span data-plb-volunteers="${g.volunteersWithJob || 0}/${g.volunteersTotal}">${g.volunteersWithJob || 0} av ${g.volunteersTotal} anmälda funktionärer är placerade</span>`);
        }
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
            html: `<p class="pl-help small">Kryssa i de funktioner planeringen ska bemanna. En roll som har folk står kvar. Hur många du behöver av varje anger du i nästa steg.</p>${roleChoiceList(B.grid, 'plbRm')}`,
            onOk: body => saveRoleChoice(body)
        });
    }
    // ================================================================ Lägg in en namnlista
    // För den som har namnen på papper eller i ett mejl (en stor tävling, en gång): klistra in, kontrollera träffarna, välj var de ska stå. Nås bara ur Åtgärder och
    // ur tipset i Hitta funktionärer — aldrig ur en ruta, och aldrig för den som bara ska lägga in en person.
    function openNameList() {
        const rows = (B.grid.rows || []).map((r, i) => ({ r, i })).filter(x => !B.grid.family || x.r.planningId);
        const cols = (B.grid.columns || []).filter(c => c.key);
        if (!rows.length) { A.showMsg('Lägg till en roll först.', 'warning'); return; }
        if (!cols.length) { A.showMsg('Lägg till en dag först.', 'warning'); return; }
        const rowLabel = x => `${x.r.roleName}${x.r.scopeLabel ? ' · ' + x.r.scopeLabel : ''}`;
        const roles = [];
        rows.forEach(x => { if (!roles.some(y => y.r.roleKey === x.r.roleKey && y.r.planningId === x.r.planningId)) roles.push(x); });
        const st = { text: '', items: null, sel: new Set(), mode: 'spread', role: '', row: '', day: '0', from: '', to: '' };
        const who = p => `${esc(p.name)}${p.club ? ' · ' + esc(p.club) : ''}${p.shooterClass ? ' · ' + esc(p.shooterClass) : ''}${p.starts ? ' · skjuter ' + esc(p.starts) : ''}${p.where ? ' · ' + esc(p.where.toLowerCase()) : ''}`;
        const planOf = () => rows[0].r.planningId;
        // Byter dialogen steg försvinner knappen som hade fokus; då når Escape inte dialogen. Dialogen tar själv fokus (inget fält, så att tangentbordet inte hoppar fram på telefonen).
        const refocus = () => { const m = document.getElementById('plFormModal'); if (m && !m.contains(document.activeElement)) m.focus({ preventScroll: true }); };

        function stepNames() {
            A.formModal({
                title: 'Lägg in en namnlista', okLabel: 'Nästa', wide: true,
                html: `<p class="mb-2">Klistra in namnen, ett per rad. Står det klass, patrull eller starttid efter ett <b>semikolon (;)</b> hjälper det när två heter lika.</p>
                    <textarea class="form-control mb-2" id="nlText" rows="6" placeholder="Anna Persson; B1; patrull 10 10:30"></textarea>
                    <button type="button" class="btn btn-sm btn-primary" id="nlGo">Hitta personerna</button>
                    <div class="small mt-3" id="nlRes" aria-live="polite"></div>`,
                onOpen: b => {
                    const text = b.querySelector('#nlText'), res = b.querySelector('#nlRes');
                    if (st.text) text.value = st.text;
                    const show = () => {
                        const items = st.items || [];
                        const by = s => items.filter(i => i.status === s);
                        const rowsHtml = [];
                        const okHtml = by('Matched').map(i => `<li class="py-1 border-bottom"><span class="text-success" aria-hidden="true">✓</span> ${who(i.chosen)}</li>`);
                        by('Ambiguous').forEach((i, k) => rowsHtml.push(`<li class="py-1 border-bottom"><label class="d-block fw-semibold" for="nlAmb${k}">${esc(i.query)} — flera heter så, vilken?</label>
                            <select class="form-select form-select-sm" id="nlAmb${k}" data-nl-amb data-k="${k}"><option value="">Välj…</option>${i.options.map(o => `<option value="${o.memberId}"${st.sel.has(o.memberId) ? ' selected' : ''}>${who(o).replace(/<[^>]+>/g, '')}</option>`).join('')}</select></li>`));
                        by('Suggested').forEach(i => rowsHtml.push(`<li class="py-1 border-bottom"><label><input type="checkbox" class="form-check-input me-1" data-nl-sug="${i.chosen.memberId}"${st.sel.has(i.chosen.memberId) ? ' checked' : ''}>${esc(i.query)} — menar du <b>${esc(i.chosen.name)}</b>${i.chosen.club ? ' (' + esc(i.chosen.club) + ')' : ''}${i.chosen.starts ? ', skjuter ' + esc(i.chosen.starts) : ''}?</label></li>`));
                        by('NotFound').forEach(i => rowsHtml.push(`<li class="py-1 border-bottom text-danger">✗ ${esc(i.query)} — hittades inte bland de anmälda${(i.options || []).length ? `<span class="d-block plb-dim">Liknande: ${i.options.map(o => esc(o.name)).join(', ')}</span>` : ''}</li>`));
                        by('Duplicate').forEach(i => rowsHtml.push(`<li class="py-1 border-bottom plb-dim">${esc(i.query)} — står redan tidigare i listan</li>`));
                        res.innerHTML = `<p class="mb-1" id="nlSum"><b>${by('Matched').length} av ${items.length}</b> hittades${by('Ambiguous').length ? ` · <b>${by('Ambiguous').length}</b> behöver ditt val` : ''}${by('Suggested').length ? ` · ${by('Suggested').length} nästan` : ''}${by('NotFound').length ? ` · <span class="text-danger">${by('NotFound').length} hittades inte</span>` : ''}. <span class="fw-semibold" id="nlCount">${st.sel.size} valda</span>.</p>
                            ${rowsHtml.length ? `<div class="fw-semibold mb-1" id="nlAttn">Det här behöver du titta på</div><ul class="list-unstyled mb-2" style="max-height:24vh;overflow:auto">${rowsHtml.join('')}</ul>` : ''}
                            ${okHtml.length ? `<details><summary>De ${okHtml.length} som hittades</summary><ul class="list-unstyled mb-0" style="max-height:20vh;overflow:auto">${okHtml.join('')}</ul></details>` : ''}`;
                    };
                    const go = async () => {
                        st.text = text.value;
                        if (!st.text.trim()) { res.innerHTML = '<span class="text-danger">Klistra in minst ett namn.</span>'; return; }
                        res.textContent = 'Letar…';
                        const d = await spost('MatchNameList', { text: st.text }, planOf()).catch(() => null);
                        if (!d || !d.success) { res.innerHTML = `<span class="text-danger">${esc((d && d.message) || 'Kunde inte läsa namnlistan.')}</span>`; return; }
                        st.items = d.items || []; st.sel = new Set();
                        st.items.filter(i => i.status === 'Matched').forEach(i => st.sel.add(i.chosen.memberId));
                        show();
                    };
                    b.querySelector('#nlGo').addEventListener('click', go);
                    text.addEventListener('paste', () => setTimeout(() => { if (text.value.trim()) go(); }, 50));
                    text.addEventListener('input', () => { st.text = text.value; });
                    res.addEventListener('change', e => {
                        const sg = e.target.closest('[data-nl-sug]');
                        if (sg) { const id = parseInt(sg.dataset.nlSug, 10); if (sg.checked) st.sel.add(id); else st.sel.delete(id); }
                        const am = e.target.closest('[data-nl-amb]');
                        if (am) { if (am._id) st.sel.delete(am._id); am._id = parseInt(am.value, 10) || 0; if (am._id) st.sel.add(am._id); }
                        const c = b.querySelector('#nlCount'); if (c) c.textContent = `${st.sel.size} valda`;
                    });
                    if (st.items) show();
                    refocus();
                },
                onOk: async b => {
                    st.text = b.querySelector('#nlText').value;
                    if (!st.text.trim()) return 'Klistra in minst ett namn.';
                    if (!st.items) { b.querySelector('#nlGo').click(); return; }
                    if (!st.sel.size) return 'Ingen av namnen är vald. Välj minst en person.';
                    stepWhere();
                }
            });
        }

        function stepWhere() {
            const roleOpts = `<option value="">Välj roll…</option>${roles.map((x, k) => `<option value="${k}"${st.role === String(k) ? ' selected' : ''}>${esc(x.r.roleName)}</option>`).join('')}`;
            const rowOpts = `<option value="">Välj roll och station…</option>${rows.map((x, k) => `<option value="${k}"${st.row === String(k) ? ' selected' : ''}>${esc(rowLabel(x))}</option>`).join('')}`;
            A.formModal({
                title: 'Var ska de stå?', okLabel: st.mode === 'spread' ? 'Nästa' : 'Lägg in', wide: false,
                html: `<p class="mb-2"><button type="button" class="btn btn-link btn-sm p-0" data-nl-back>← Tillbaka till namnen</button> <span class="plb-dim small">${st.sel.size} ${st.sel.size === 1 ? 'person' : 'personer'}</span></p>
                    <div class="form-check mb-2"><input class="form-check-input" type="radio" name="nlMode" id="nlSpread" value="spread"${st.mode === 'spread' ? ' checked' : ''}>
                        <label class="form-check-label" for="nlSpread"><b>Sprid dem över stationerna åt mig</b> <span class="badge text-bg-light border fw-normal">rekommenderas</span>
                        <span class="d-block small plb-dim">Du ser förslaget först — inget läggs in förrän du bekräftar.</span></label></div>
                    <div class="mb-3 ms-4" id="nlSpreadBox"><label class="form-label small mb-0" for="nlRole">Som vilken roll?</label><select class="form-select form-select-sm" id="nlRole">${roleOpts}</select></div>
                    <div class="form-check mb-2"><input class="form-check-input" type="radio" name="nlMode" id="nlSame" value="same"${st.mode === 'same' ? ' checked' : ''}>
                        <label class="form-check-label" for="nlSame"><b>Alla på samma ställe</b></label></div>
                    <div class="ms-4" id="nlSameBox">
                        <div class="mb-2"><label class="form-label small mb-0" for="nlRow">Roll och station</label><select class="form-select form-select-sm" id="nlRow">${rowOpts}</select></div>
                        <div class="row g-2"><div class="col-12 col-sm-6"><label class="form-label small mb-0" for="nlDay">Dag</label><select class="form-select form-select-sm" id="nlDay">${cols.map((c, k) => `<option value="${k}"${st.day === String(k) ? ' selected' : ''}>${esc(c.label)}</option>`).join('')}</select></div>
                            <div class="col-6 col-sm-3"><label class="form-label small mb-0" for="nlFrom">Från klockan</label><input type="text" class="form-control form-control-sm" id="nlFrom" data-hpsk-time inputmode="decimal" placeholder="t.ex. 8" value="${esc(st.from)}"></div>
                            <div class="col-6 col-sm-3"><label class="form-label small mb-0" for="nlTo">Till klockan</label><input type="text" class="form-control form-control-sm" id="nlTo" data-hpsk-time inputmode="decimal" placeholder="t.ex. 12" value="${esc(st.to)}"></div>
                            <div class="col-12 pl-help small">Utan klockslag gäller placeringen hela dagen.</div></div></div>
                    <div class="pl-help small mt-3">Personerna står som Placerade — ingen får något mejl förrän du meddelar dem.</div>`,
                onOpen: b => {
                    const sync = () => {
                        st.mode = b.querySelector('#nlSame').checked ? 'same' : 'spread';
                        b.querySelector('#nlSpreadBox').classList.toggle('d-none', st.mode !== 'spread');
                        b.querySelector('#nlSameBox').classList.toggle('d-none', st.mode !== 'same');
                        const ok = document.getElementById('plFormOk'); if (ok) ok.textContent = st.mode === 'spread' ? 'Nästa' : 'Lägg in';
                    };
                    b.addEventListener('change', e => {
                        if (e.target.closest('[name=nlMode]')) sync();
                        st.role = b.querySelector('#nlRole').value; st.row = b.querySelector('#nlRow').value; st.day = b.querySelector('#nlDay').value;
                        st.from = b.querySelector('#nlFrom').value; st.to = b.querySelector('#nlTo').value;
                    });
                    b.addEventListener('click', e => { if (e.target.closest('[data-nl-back]')) stepNames(); });
                    sync();
                    refocus();
                },
                onOk: async b => {
                    st.role = b.querySelector('#nlRole').value; st.row = b.querySelector('#nlRow').value; st.day = b.querySelector('#nlDay').value;
                    const ids = [...st.sel];
                    if (st.mode === 'spread') {
                        if (st.role === '') return 'Välj vilken roll de ska ha.';
                        const x = roles[parseInt(st.role, 10)];
                        openDistribution({ roleKey: x.r.roleKey, roleName: x.r.roleName, planningId: x.r.planningId, memberIds: ids, back: stepWhere });
                        return;
                    }
                    if (st.row === '') return 'Välj roll och station.';
                    const x = rows[parseInt(st.row, 10)], c = cols[parseInt(st.day, 10)];
                    const from = normTime(b.querySelector('#nlFrom').value), to = normTime(b.querySelector('#nlTo').value);
                    if (from === null || to === null) return 'Skriv klockslagen till exempel 8 eller 8:30.';
                    if (!!from !== !!to) return 'Skriv både från och till, eller inget klockslag alls.';
                    const body = { roleKey: x.r.roleKey, scopeType: x.r.scopeType || null, scopeKey: x.r.scopeKey || null, date: c.key, from: from || null, to: to || null, memberIds: ids };
                    const d = await spost('AssignMany', body, x.r.planningId);
                    if (!d.success) return d.message || 'Det gick inte att lägga in.';
                    let added = d.added.length;
                    const clashes = d.skipped.filter(s => s.clash), others = d.skipped.filter(s => !s.clash);
                    let forced = 0;
                    if (clashes.length && await A.plConfirm(`${clashes.length} ${clashes.length === 1 ? 'person är' : 'personer är'} upptagna`,
                        clashes.slice(0, 6).map(s => `${s.name}: ${s.reason}`).join('\n') + (clashes.length > 6 ? `\n…och ${clashes.length - 6} till` : '') + (added ? `\n\nDe andra ${added} är redan inlagda.` : '') + '\n\nLägg in dem ändå? Väljer du Avbryt lämnas de utanför.', 'Lägg in ändå')) {
                        const f = await spost('AssignMany', Object.assign({}, body, { memberIds: clashes.map(s => s.memberId), force: true }), x.r.planningId);
                        if (f.success) { forced = f.added.length; added += forced; }
                    }
                    await loadGrid(); drawGrid();
                    const left = clashes.length - forced + others.length;
                    A.showMsg(`${added} ${added === 1 ? 'person lades' : 'personer lades'} in${forced ? ` (${forced} trots att de var upptagna)` : ''}.`
                        + (left ? ` ${left} hoppades över: ${[...clashes.slice(forced), ...others].slice(0, 4).map(s => esc(s.name) + ' (' + esc(s.reason) + ')').join('; ')}${left > 4 ? '…' : ''}.` : ''), left ? 'warning' : 'success');
                    return true;
                }
            });
        }
        stepNames();
    }

    // EN väg för att meddela (Stefan 2026-10-28): servern skickar ETT mejl (och en notis för medlemmar) per människa med alla hens ännu ej meddelade placeringar och en knapp till
    // hens tävlingsdagssida. Ingen ja/nej. Raderna märks som meddelade och statusen förblir Placerad. Samma anrop för "alla" (bandet) och "bara den här personen".
    async function sendNotice(ids) {
        const d = await spost('NotifyPlacements', { ids });
        if (!d.success) return { ok: false, text: d.message || 'Det gick inte att meddela.' };
        const names = (d.unreachable || []).slice(0, 3).map(esc).join(', ') + ((d.unreachable || []).length > 3 ? ' m.fl.' : '');
        const parts = [];
        if (d.people) parts.push(`${d.people} ${d.people === 1 ? 'person har' : 'personer har'} fått besked om ${d.placements} ${d.placements === 1 ? 'placering' : 'placeringar'}` + (d.mailed ? ` (${d.mailed} mejl${d.pushed ? ' och ' + d.pushed + ' notiser' : ''})` : ''));
        if (d.alreadyTold) parts.push(`${d.alreadyTold} var redan meddelade`);
        if ((d.unreachable || []).length) parts.push(`${d.unreachable.length} nåddes inte (${names}) — saknar konto med e-post eller har ingen e-post`);
        return { ok: d.people > 0 || (d.alreadyTold > 0 && !(d.unreachable || []).length), warn: (d.unreachable || []).length > 0, text: parts.join('. ') + '.' };
    }

    // Bandets steg "Meddela de inlagda": alla placeringar som ännu inte är meddelade och har en kontaktväg. Bekräftelsen säger hur många personer och placeringar det gäller, eftersom mejlen inte går att ta tillbaka.
    async function openSendRequests() {
        const todo = [];
        (B.grid.rows || []).forEach(r => Object.values(r.cells || {}).forEach(list => (list || []).forEach(e => {
            if (asStatus(e.status) === 'Planned' && !e.notifiedAt && !e.readOnly && e.hasContact) todo.push(e);
        })));
        if (!todo.length) { A.showMsg('Alla inlagda har redan fått besked.', 'success'); return; }
        const people = new Set(todo.map(e => e.memberId ? 'm' + e.memberId : 'n' + (e.displayName || '').toLowerCase())).size;
        if (!await A.plConfirm(`Meddela ${people} ${people === 1 ? 'person' : 'personer'}?`,
            `Varje person får ETT mejl (och en notis, om hen har konto) med sina ${todo.length === people ? 'placeringar' : todo.length + ' placeringar'} och en knapp till sin tävlingsdagssida. Ingen behöver svara. Det går inte att ta tillbaka när det är skickat.`, 'Meddela')) return;
        let r;
        try { r = await sendNotice(todo.map(e => e.id)); } catch (err) { r = { ok: false, text: err.message }; }
        await loadGrid(); drawGrid();
        A.showMsg(r.text, r.ok ? (r.warn ? 'warning' : 'success') : 'danger');
    }

    // Planera dagen: "Fördela dem åt mig". De namngivna personerna (ur den inklistrade listan) sprids över rollens stationer och dagens block med tider som inte krockar med deras
    // egna starter. Förslaget visas först (inget är inlagt), du kryssar bort det du inte vill ha, och allt kan ångras. Öppnas ur "Lägg in en namnlista" (steg 2) och ersätter då dess innehåll.
    async function openDistribution({ roleKey, roleName, planningId, memberIds, back }) {
        let r = null, blockMinutes = 0;   // 0 = servern väljer åt användaren (3, 2 eller 1,5 timme: det som placerar flest)
        const sel = new Set();
        const fetchPlan = () => spost('PreviewDistribution', { roleKey, memberIds, blockMinutes }, planningId).catch(() => null);
        const stationNo = s => { const m = /(\d+)/.exec(s || ''); return m ? parseInt(m[1], 10) : 0; };
        const draw = b => {
            const body = b.querySelector('#plbDsBody');
            if (!r || !r.success) { body.innerHTML = `<div class="alert alert-danger py-2 small" role="alert">${esc((r && r.message) || 'Kunde inte räkna ut en fördelning.')}</div>`; return; }
            const rows = r.rows.map((x, i) => ({ x, i })).sort((a, c) => stationNo(a.x.scope) - stationNo(c.x.scope) || (a.x.scope || '').localeCompare(c.x.scope || '', 'sv') || a.x.time.localeCompare(c.x.time));
            const rowHtml = ({ x, i }) => `<label class="list-group-item d-flex gap-2 align-items-start py-1 small${x.warning ? ' plb-cand-busy' : ''}">
                <input class="form-check-input mt-1 flex-shrink-0" type="checkbox" data-ds="${i}"${sel.has(i) ? ' checked' : ''}>
                <span><span class="fw-semibold">${esc(x.scope || x.role)}</span> · ${esc(x.dayLabel)} ${esc(x.time)} — <span class="fw-semibold">${esc(x.name)}</span>
                <span class="d-block plb-dim">${esc(x.why)}</span>${x.warning ? `<span class="d-block text-warning-emphasis">${esc(x.warning)}</span>` : ''}</span></label>`;
            const perStation = [];
            r.rows.map((x, i) => ({ x, i })).sort((a, c) => stationNo(a.x.scope) - stationNo(c.x.scope) || (a.x.scope || '').localeCompare(c.x.scope || '', 'sv')).forEach(({ x }) => {
                const k = x.scope || x.role; const hit = perStation.find(s => s.scope === k);
                if (hit) hit.ids.add(x.memberId); else perStation.push({ scope: k, ids: new Set([x.memberId]) });
            });
            perStation.forEach(s => { s.n = s.ids.size; });
            body.innerHTML = `
                <p class="mb-2" id="plbDsSummary"><b>${new Set(r.rows.map(x => x.memberId)).size} av dina ${memberIds.length} namn</b> får en plats${(r.unplaced || []).length ? ` · <span class="fw-semibold">${r.unplaced.length} får ingen (se nedan)</span>` : ''} ·
                    ${r.stillMissing ? `<span class="fw-semibold">${r.stillMissing} ${r.blockMinutes >= 480 ? (r.stillMissing === 1 ? 'plats' : 'platser') : 'skift'} saknar fortfarande folk</span>` : 'alla platser blir fyllda'}${r.blockMinutes < 480 && r.stillMissing ? ' <span class="plb-dim">(ett skift är en station under en del av dagen — så räknas det här, inte som platserna i bandet)</span>' : ''}.
                    <span class="d-block small plb-dim" id="plbDsPerStation">Så här många per station: ${perStation.map(s => `${esc(s.scope)} ${s.n}`).join(' · ')}.${r.blockMinutes >= 480 ? ' Var och en står på sin station hela dagen.' : ' Dagen delas i skift, eftersom alla inte kan stå hela dagen.'}</span>
                    <span class="d-block small plb-dim">${sel.size} av ${r.rows.length} förslag är markerade.</span></p>
                ${(r.unplaced || []).length ? `<details class="mb-2" id="plbDsUnplaced" open><summary class="fw-semibold small">Fick ingen plats (${r.unplaced.length}) — och varför</summary>
                    <ul class="list-unstyled small mb-0">${r.unplaced.map(u => `<li class="py-1 border-bottom">${esc(u.name)} <span class="plb-dim">— ${esc(u.reason)}</span></li>`).join('')}</ul></details>` : ''}
                <div class="d-flex flex-wrap gap-2 align-items-end mb-2">
                    <div><label class="form-label small mb-0" for="plbDsBlock">Så länge står varje person åt gången</label>
                        <select class="form-select form-select-sm" id="plbDsBlock"><option value="0"${blockMinutes === 0 ? ' selected' : ''}>Välj åt mig (rekommenderas)</option>${[480, 240, 180, 120, 90].map(m => `<option value="${m}"${blockMinutes === m ? ' selected' : ''}>${m === 480 ? 'Hela dagen' : m / 60 + ' timmar'}</option>`).join('')}</select></div>
                    <span class="small plb-dim align-self-center" id="plbDsBlockNote">${blockMinutes === 0 ? (r.blockMinutes >= 480 ? 'Valde hela dagen — så få byten som möjligt.' : `Valde ${r.blockMinutes / 60} timmar${r.blockMinutes < 180 ? ' — kortare än vanligt, så att fler av dina namn får plats' : ''}, eftersom hela dagen inte räckte till alla platser.`) : ''}</span>
                    <button type="button" class="btn btn-sm btn-outline-secondary" id="plbDsRecalc">Räkna om</button>
                </div>
                ${r.rows.length ? `<div class="list-group mb-2 plb-many-list" id="plbDsList" style="max-height:34vh;overflow:auto">${rows.map(rowHtml).join('')}</div>` : '<p class="plb-dim">Ingen kunde placeras.</p>'}
                ${(r.unknown || []).length ? `<p class="small text-danger mb-2">${r.unknown.length} ${r.unknown.length === 1 ? 'person finns' : 'personer finns'} inte bland anmälda eller funktionärer och ${r.unknown.length === 1 ? 'togs' : 'togs'} inte med.</p>` : ''}
                ${(r.unfilled || []).length ? `<div class="mb-2" id="plbDsUnfilled"><div class="fw-semibold small mb-1">Platser som fortfarande saknar folk (${r.unfilled.length})</div>
                    <ul class="list-unstyled small mb-0">${r.unfilled.slice(0, 20).map(u => `<li class="py-1 border-bottom">${esc(u.scope || '')} · ${esc(u.dayLabel)} ${esc(u.time)} — saknas ${u.missing} <span class="plb-dim">(${esc(u.reason)})</span></li>`).join('')}${r.unfilled.length > 20 ? `<li class="py-1 plb-dim">…och ${r.unfilled.length - 20} till</li>` : ''}</ul></div>` : ''}
                <div class="text-danger small" id="plbDsErr" role="alert"></div>`;
        };
        const load = async b => {
            b.querySelector('#plbDsBody').innerHTML = '<p class="plb-dim">Räknar ut en fördelning…</p>';
            r = await fetchPlan();
            sel.clear(); if (r && r.success) r.rows.forEach((_, i) => sel.add(i));
            draw(b);
        };
        A.formModal({
            title: 'Fördela dem åt mig', okLabel: 'Lägg in förslaget', wide: true,
            html: `${back ? '<p class="mb-2"><button type="button" class="btn btn-link btn-sm p-0" data-ds-back>← Tillbaka</button></p>' : ''}<div class="alert alert-warning py-2 small mb-2" id="plbDsBanner"><b>Förslag — inget är inlagt än.</b> Ingen funktionär ser något och ingen får något mejl förrän du lagt in och meddelat.</div><div id="plbDsBody"></div>`,
            onOpen: b => {
                b.addEventListener('change', e => {
                    const cb = e.target.closest('[data-ds]');
                    if (cb) { const i = parseInt(cb.dataset.ds, 10); if (cb.checked) sel.add(i); else sel.delete(i);
                        const f = document.activeElement; const keep = f && f.dataset ? f.dataset.ds : null; const sc = b.querySelector('#plbDsList') ? b.querySelector('#plbDsList').scrollTop : 0;
                        draw(b); if (b.querySelector('#plbDsList')) b.querySelector('#plbDsList').scrollTop = sc; if (keep != null) { const again = b.querySelector(`[data-ds="${keep}"]`); if (again) again.focus({ preventScroll: true }); } }
                });
                b.addEventListener('click', async e => {
                    if (back && e.target.closest('[data-ds-back]')) { back(); return; }
                    if (!e.target.closest('#plbDsRecalc')) return;
                    const v = parseInt(b.querySelector('#plbDsBlock').value, 10); blockMinutes = isNaN(v) ? 0 : v; await load(b);
                });
                load(b);
            },
            onOk: async b => {
                if (!r || !r.success) return 'Det finns ingen fördelning att lägga in.';
                if (!sel.size) return 'Markera minst ett förslag.';
                const body = { roleKey, memberIds, blockMinutes: r.blockMinutes, fingerprint: r.fingerprint, rows: [...sel].sort((a, c) => a - c).map(i => ({ slotKey: r.rows[i].slotKey, memberId: r.rows[i].memberId })) };
                const d = await spost('ApplyDistribution', body, planningId);
                if (!d.success) return d.message || 'Det gick inte att lägga in.';
                await loadGrid(); drawGrid();
                const skip = (d.skipped || []).length;
                A.showMsg(`${d.added.length} ${d.added.length === 1 ? 'plats fylldes' : 'platser fylldes'} som Placerade — de står inlagda men ingen har fått besked än, och inget mejl har gått iväg. <b>Nästa steg:</b> <button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-plb-plan-action="skicka">meddela de inlagda</button>.${skip ? ` ${skip} hoppades över: ${d.skipped.slice(0, 3).map(s => esc(s.name) + ' (' + esc(s.reason) + ')').join('; ')}.` : ''}`
                    + (d.batchId ? ` <button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-plb-undoprop="${esc(d.batchId)}">Ångra</button>` : ''), 'success');
                return true;
            }
        });
    }

    // Planera dagen steg 3: "Fyll med förslag". Förslaget räknas ut men sparas inte — det visas här, du kryssar bort det du inte vill ha och trycker Lägg in.
    // Allt läggs in som Planerat i EN omgång (inget mejl), och går att ångra så länge ingen hunnit svara.
    async function openProposal() {
        A.showMsg('Räknar ut ett förslag…', 'info');
        const r = await sget('GetProposal').catch(() => null);
        A.showMsg('');   // "Räknar ut…" ska inte bli kvar efter att dialogen stängts
        if (!r || !r.success) { A.showMsg(esc((r && r.message) || 'Kunde inte räkna ut ett förslag.'), 'danger'); return; }
        if (!r.rows.length && !r.unfilled.length) {
            // Stefan 2026-10-27: 30 anmälda och inga behov satta gav "Alla platser är redan fyllda" — fel. Förslaget fyller de platser som BEHÖVS; utan behov finns inga platser.
            if (!(B.grid.placesNeeded > 0)) A.showMsg('Det finns inga platser att fylla än: ni har inte sagt hur många som behövs av varje roll, och förslaget fyller de platser som behövs. <button type="button" class="btn btn-link btn-sm p-0 align-baseline fw-semibold" data-plb-plan-action="behov">Sätt antal…</button>', 'warning');
            else A.showMsg('Alla platser är redan fyllda — det finns inget att föreslå.', 'success');
            return;
        }
        const sel = new Set(r.rows.map((x, i) => i));
        const rowHtml = (x, i) => `<label class="list-group-item d-flex gap-2 align-items-start py-1 small${x.warning ? ' plb-cand-busy' : ''}">
            <input class="form-check-input mt-1 flex-shrink-0" type="checkbox" data-pr="${i}" checked>
            <span><span class="fw-semibold">${esc(x.name)}</span> — ${esc(x.role)}${x.scope ? ' · ' + esc(x.scope) : ''}, ${esc(x.dayLabel)} ${esc(x.time)}
            <span class="d-block plb-dim">${esc(x.why)}</span>${x.warning ? `<span class="d-block text-warning-emphasis">${esc(x.warning)}</span>` : ''}</span></label>`;
        const unHtml = u => `<li class="py-1 border-bottom small"><span class="fw-semibold">${esc(u.role)}${u.scope ? ' · ' + esc(u.scope) : ''}</span>, ${esc(u.dayLabel)} ${esc(u.time)} — saknas <b>${u.missing}</b>
            <span class="d-block plb-dim">${esc(u.reason)}</span></li>`;
        const open = r.unfilled;
        A.formModal({
            title: 'Förslag på bemanning', okLabel: 'Lägg in de markerade', wide: true,
            html: `<div class="alert alert-warning py-2 small mb-2" id="plbPrBanner"><b>Förslag — inget är inlagt än.</b> Ingen funktionär ser något och ingen får något mejl förrän du lagt in och meddelat.</div>
                <p class="mb-2" id="plbPrSummary"></p>
                ${r.rows.length ? `<div class="list-group mb-3 plb-many-list" id="plbPrList" style="max-height:34vh;overflow:auto">${r.rows.map(rowHtml).join('')}</div>` : '<p class="plb-dim">Det finns ingen som kan läggas in just nu.</p>'}
                ${open.length ? `<div class="fw-semibold small mb-1" id="plbPrOpenHead">Det här gick inte att fylla</div><ul class="list-unstyled mb-2" id="plbPrOpen">${open.map(unHtml).join('')}</ul>` : ''}                <div class="text-danger small" id="plbPrErr" role="alert"></div>`,
            onOpen: b => {
                const sum = () => {
                    b.querySelector('#plbPrSummary').innerHTML = `${sel.size} av ${r.rows.length} förslag markerade · ${r.placed} av ${r.needed} platser kan fyllas · <span class="${r.stillMissing ? 'text-danger fw-semibold' : ''}">${r.stillMissing} ${r.stillMissing === 1 ? 'plats saknas' : 'platser saknas'} efter förslaget</span>.`;
                };
                sum();
                const list = b.querySelector('#plbPrList');
                if (list) list.addEventListener('change', e => { const cb = e.target.closest('[data-pr]'); if (!cb) return; const i = parseInt(cb.dataset.pr, 10); if (cb.checked) sel.add(i); else sel.delete(i); sum(); });
            },
            onOk: async b => {
                if (!sel.size) return 'Markera minst ett förslag.';
                const body = { fingerprint: r.fingerprint, rows: [...sel].sort((a, c) => a - c).map(i => ({ slotKey: r.rows[i].slotKey, memberId: r.rows[i].memberId })) };
                const d = await spost('ApplyProposal', body);
                if (!d.success) return d.message || 'Det gick inte att lägga in.';
                await loadGrid(); drawGrid();
                const skip = (d.skipped || []).length;
                A.showMsg(`${d.added.length} ${d.added.length === 1 ? 'person lades' : 'personer lades'} in som Placerade. Ingen har fått något mejl än.${skip ? ` ${skip} hoppades över: ${d.skipped.slice(0, 3).map(s => esc(s.name) + ' (' + esc(s.reason) + ')').join('; ')}.` : ''}`
                    + (d.batchId ? ` <button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-plb-undoprop="${esc(d.batchId)}">Ångra</button>` : ''), 'success');
                return true;
            }
        });
    }
    async function undoProposal(batchId) {
        const d = await spost('UndoProposal', { batchId }).catch(() => null);
        if (!d || !d.success) { A.showMsg(esc((d && d.message) || 'Det gick inte att ångra.'), 'danger'); return; }
        await loadGrid(); drawGrid();
        A.showMsg(`${d.removed} ${d.removed === 1 ? 'placering togs' : 'placeringar togs'} bort.${d.kept ? ` ${d.kept} står kvar eftersom de hunnit få svar eller ändrats.` : ''}`, 'success');
    }
    document.addEventListener('click', e => {
        const b = e.target.closest && e.target.closest('[data-plb-undoprop]');
        if (b && A && A.isActive()) { e.preventDefault(); undoProposal(b.dataset.plbUndoprop); }
    });

    // Planera dagen steg 3b: "Bygg startlistan runt funktionärerna" (fält). Förhandsvisningen sparar inget; du ser vad som flyttas, vilka som inte går att
    // placera utan krock och vilka reserver som lämnas utan start. Lås de som ska ligga kvar. Tillämpningen tar en backup först och går att ångra.
    async function openPatrolPlan() {
        const settings = { patrolSize: '', intervalMinutes: '', firstStart: '', gap: '', keep: true };
        let r = null;
        const qs = () => { const q = {}; if (settings.patrolSize) q.patrolSize = settings.patrolSize; if (settings.intervalMinutes) q.intervalMinutes = settings.intervalMinutes;
            if (settings.firstStart) q.firstStart = settings.firstStart; if (settings.gap) q.gap = settings.gap; if (!settings.keep) q.keep = 'false'; return q; };
        const fetchPlan = async () => sget('GetPatrolPlan', qs()).catch(() => null);
        r = await fetchPlan();
        if (!r || !r.success) { A.showMsg(esc((r && r.message) || 'Kunde inte räkna ut en startlista.'), 'danger'); return; }
        const draw = b => {
            const c = r.change, s = r.settings;
            lastMoved = (r.moves || []).filter(m => m.kind !== 'removed');
            const lockRows = (r.current || []).map(m => `<label class="list-group-item d-flex gap-2 align-items-center py-1 small">
                <input class="form-check-input flex-shrink-0 mt-0" type="checkbox" data-pp-lock="${m.patrolMemberId}"${m.locked ? ' checked' : ''} aria-label="Lås ${esc(m.name)}">
                <span class="fw-semibold">${esc(m.name)}</span><span class="plb-dim">${esc(m.shootingClass)} · patrull ${m.number} kl ca ${esc(m.time)}</span></label>`).join('');
            // Finns en startlista bygger vi inom de grupper som redan finns: var och en med sin egen första start (fältets lista skapas en grupp i taget).
            const hasGroups = (r.groups || []).length > 0;
            const groupsHtml = hasGroups ? `<div class="mb-2" id="plbPpGroups"><div class="fw-semibold small mb-1">Grupperna som byggs om — var och en med sin egen första start</div>
                <ul class="list-unstyled small mb-1">${r.groups.map(g => `<li class="py-1 border-bottom"><b>${esc(g.label || 'Alla')}</b> · första start ${esc(g.firstStart)} · ${g.patrolSize} per patrull · ${g.interval} min mellan · ${g.patrols} ${g.patrols === 1 ? 'patrull' : 'patruller'}</li>`).join('')}</ul>
                <p class="plb-dim small mb-0">Tiderna per patrull är uppskattningar (gruppens första start + avstånd). Det är gruppens första start som gäller, och ingen flyttas till en annan grupp.</p></div>` : '';
            // Skyddet ligger överst: kryssrutan måste synas utan att man rullar i dialogen (persontest 6: den låg gömd under de synliga raderna).
            // Skyddsrutan visas bara när något faktiskt skulle ändras — annars skrämmer den i onödan (persontest 6b).
            const willChange = (c.moved || 0) + (c.added || 0) + (c.removed || 0) > 0;
            const blockedHtml = r.blocked && willChange ? `<div class="alert alert-danger py-2 small" id="plbPpBlocked" role="alert"><b>${esc(r.blocked)}</b>${r.published ? '<span class="d-block fw-normal mt-1">Publicerad betyder att skyttarna kan se sina starttider på sajten. Bygger du om listan ser de de nya tiderna nästa gång de tittar — ingen får något mejl om ändringen, så de som flyttas behöver du själv säga till.</span>' : ''}<label class="d-block mt-1"><input type="checkbox" class="form-check-input me-1" id="plbPpForce">Bygg om ändå</label></div>` : '';
            b.querySelector('#plbPpBody').innerHTML = `
                ${blockedHtml}
                ${groupsHtml}
                <div class="row g-2 mb-2">
                    ${hasGroups ? '' : `<div class="col-6 col-md-3"><label class="form-label mb-0 small" for="plbPpSize">Skyttar per patrull</label><input type="number" min="1" max="12" class="form-control form-control-sm" id="plbPpSize" inputmode="numeric" value="${s.patrolSize}"></div>
                    <div class="col-6 col-md-3"><label class="form-label mb-0 small" for="plbPpInt">Minuter mellan patruller</label><input type="number" min="1" max="120" class="form-control form-control-sm" id="plbPpInt" inputmode="numeric" value="${s.intervalMinutes}"></div>
                    <div class="col-6 col-md-3"><label class="form-label mb-0 small" for="plbPpFirst">Första start</label><input type="time" class="form-control form-control-sm" id="plbPpFirst" value="${esc((s.firstStart || '').slice(11, 16))}"></div>`}
                    <div class="col-12 col-md-6"><label class="form-label mb-0 small" for="plbPpGap">Minuter mellan starterna för den som skjuter i flera klasser</label><input type="number" min="0" max="600" class="form-control form-control-sm" id="plbPpGap" inputmode="numeric" value="${s.gap || ''}" aria-describedby="plbPpGapHelp" style="max-width:8rem"><div class="plb-dim small" id="plbPpGapHelp">Lämna tomt om ingen skjuter i flera klasser.</div></div>
                </div>
                <label class="d-block small mb-2"><input type="checkbox" class="form-check-input me-1" id="plbPpKeep"${s.keep ? ' checked' : ''}>Behåll de som redan har en tid som passar — flytta bara de som krockar${r.keptFitting ? ` (${r.keptFitting} behålls)` : ''}</label>
                <button type="button" class="btn btn-sm btn-outline-secondary mb-2" id="plbPpRecalc">Räkna om</button>
                <p class="mb-2" id="plbPpSummary"><b>${r.patrols.length} patruller</b>, ${r.patrols.reduce((n, p) => n + p.members.length, 0)} starter. I dag finns ${r.existing.patrols} patruller med ${r.existing.shooters} starter.
                    ${c.moved} flyttas · ${c.unchanged} ligger kvar · ${c.added} är nya${c.removed ? ` · ${c.removed} tas bort (inte anmälda längre)` : ''}.${r.lockedKept - (r.keptFitting || 0) > 0 ? ` ${r.lockedKept - (r.keptFitting || 0)} låsta ligger kvar på sin tid.` : ''}</p>
                ${!(r.moves || []).length && !r.unavoidable.length ? `<div class="alert alert-success py-2 small" id="plbPpNothing"><b>Ingen behöver flyttas.</b> Ingen skytt har en start som krockar med en placering, så startlistan behöver inte ändras.</div>` : ''}
                ${(r.moves || []).length ? `<div class="mb-2" id="plbPpMoves"><div class="fw-semibold small mb-1">Så här ändras startlistan (${r.moves.length}) <button type="button" class="btn btn-link btn-sm p-0 align-baseline fw-normal" data-plb-copymoves>Kopiera som lista att ringa efter</button></div>
                    <ul class="list-unstyled small mb-0" style="max-height:24vh;overflow:auto">${r.moves.slice(0, 60).map(m => `<li class="py-1 border-bottom"><span class="fw-semibold">${esc(m.name)}</span> <span class="plb-dim">${esc(m.shootingClass)}</span> —
                        ${m.kind === 'moved' ? `patrull ${m.fromPatrol} kl ca ${esc(m.fromTime)} → <b>patrull ${m.toPatrol} kl ca ${esc(m.toTime)}</b>` : m.kind === 'added' ? `får <b>patrull ${m.toPatrol} kl ${esc(m.toTime)}</b>` : `tas bort (var patrull ${m.fromPatrol} kl ${esc(m.fromTime)})`}
                        <span class="d-block plb-dim">${esc(m.why)}</span></li>`).join('')}${r.moves.length > 60 ? `<li class="py-1 plb-dim">…och ${r.moves.length - 60} till</li>` : ''}</ul></div>` : ''}
                ${r.unavoidable.length ? `<div class="alert alert-warning py-2 small" id="plbPpUnavoidable"><b>Går inte att undvika krock:</b> ${r.unavoidable.map(u => esc(u.name) + ' kl ' + esc(u.time)).join('; ')}. Placeringen täcker hela dagen — ändra placeringen eller starten för hand.</div>` : ''}
                ${r.reservesLeftOut.length ? `<p class="small plb-dim" id="plbPpReserves">Utan start (hoppar in vid lucka): ${r.reservesLeftOut.map(esc).join(', ')}.</p>` : ''}
                ${(r.ungrouped || []).length ? `<div class="alert alert-warning py-2 small" id="plbPpUngrouped"><b>De här har ingen grupp att gå i:</b> ${r.ungrouped.map(esc).join('; ')}. Deras vapenklass finns inte i någon grupp i startlistan, så de placeras inte. Skapa gruppen under Startlistor först.</div>` : ''}
                ${r.lockedDropped.length ? `<p class="small text-warning-emphasis">Låsta som inte är anmälda längre och därför tas bort: ${r.lockedDropped.map(esc).join(', ')}.</p>` : ''}
                ${r.lockedOverflow.length ? `<p class="small text-warning-emphasis">Fick ingen plats i sin patrull och har en egen patrull vid samma tid: ${r.lockedOverflow.map(esc).join(', ')}.</p>` : ''}
                <details class="mb-2"><summary class="small fw-semibold">Patrullerna (${r.patrols.length})</summary>
                    <div class="small" id="plbPpPatrols" style="max-height:26vh;overflow:auto">${r.patrols.map(p => `<div class="py-1 border-bottom"><b>Patrull ${p.number}</b> kl ca ${esc(p.time)}<span class="plb-dim">${esc(p.group || '')}</span><br>${p.members.map(m => esc(m.name)).join(', ')}</div>`).join('')}</div></details>
                <details class="mb-2"><summary class="small fw-semibold">Lås skyttar som ska ligga kvar (${(r.current || []).filter(m => m.locked).length} låsta)</summary>
                    ${lockRows ? `<div class="list-group mt-1" id="plbPpLocks" style="max-height:26vh;overflow:auto">${lockRows}</div>` : '<p class="small plb-dim mt-1">Det finns ingen startlista än.</p>'}</details>
                ${r.undoable ? `<p class="small mb-0"><button type="button" class="btn btn-link btn-sm p-0 align-baseline" id="plbPpUndo">Ångra senaste ombyggnaden</button> — startlistan som den var före.</p>` : ''}
                <div class="text-danger small" id="plbPpErr" role="alert"></div>`;
        };
        const reload = async b => {
            const v = id => b.querySelector(id) ? b.querySelector(id).value.trim() : '';
            settings.patrolSize = v('#plbPpSize'); settings.intervalMinutes = v('#plbPpInt'); settings.gap = v('#plbPpGap');
            if (b.querySelector('#plbPpKeep')) settings.keep = b.querySelector('#plbPpKeep').checked;
            const t = v('#plbPpFirst'); settings.firstStart = t ? ((r.settings.firstStart || '').slice(0, 10) + 'T' + t) : '';
            const n = await fetchPlan();
            if (!n || !n.success) { b.querySelector('#plbPpErr').textContent = (n && n.message) || 'Kunde inte räkna om.'; return; }
            r = n; draw(b);
        };
        A.formModal({
            title: 'Bygg startlistan runt funktionärerna', okLabel: 'Bygg om startlistan', wide: true,
            html: `<div class="alert alert-warning py-2 small mb-2"><b>Förhandsvisning — inget är ändrat än.</b> En säkerhetskopia av dagens startlista tas innan något skrivs, och du kan ångra efteråt.</div><div id="plbPpBody"></div>`,
            onOpen: b => {
                draw(b);
                b.addEventListener('click', async e => {
                    if (e.target.closest('#plbPpRecalc')) { await reload(b); return; }
                    if (e.target.closest('#plbPpUndo')) {
                        if (!await A.plConfirm('Ångra ombyggnaden?', 'Startlistan återställs till hur den var före senaste ombyggnaden.', 'Ångra')) return;
                        let d = await spost('UndoPatrolPlan', { backupId: r.undoable });
                        if (!d.success && /ändrats/.test(d.message || '') && await A.plConfirm('Startlistan har ändrats sedan', esc(d.message) + '\n\nÅterställ ändå?', 'Återställ ändå'))
                            d = await spost('UndoPatrolPlan', { backupId: r.undoable, force: true });
                        if (!d.success) { b.querySelector('#plbPpErr').textContent = d.message || 'Det gick inte att återställa.'; return; }
                        A.showMsg('Startlistan är återställd.', 'success'); await loadGrid(); drawGrid(); await reload(b);
                    }
                });
                b.addEventListener('change', async e => {
                    const cb = e.target.closest('[data-pp-lock]'); if (!cb) return;
                    const d = await spost('SetPatrolLock', { patrolMemberId: parseInt(cb.dataset.ppLock, 10), locked: cb.checked });
                    if (!d.success) { cb.checked = !cb.checked; b.querySelector('#plbPpErr').textContent = d.message || 'Det gick inte att låsa.'; return; }
                    await reload(b);
                });
            },
            onOk: async b => {
                const force = !!(b.querySelector('#plbPpForce') && b.querySelector('#plbPpForce').checked);
                if (!r.blocked ? false : !((r.change.moved || 0) + (r.change.added || 0) + (r.change.removed || 0))) return 'Ingen behöver flyttas — startlistan behöver inte ändras.';
                if (r.blocked && !force) {
                    const f = b.querySelector('#plbPpForce');
                    if (f) { f.scrollIntoView({ block: 'center' }); f.focus(); }
                    return 'Startlistan är skyddad. Kryssa i "Bygg om ändå" om du verkligen vill.';
                }
                const s = r.settings;
                const d = await spost('ApplyPatrolPlan', { fingerprint: r.fingerprint, patrolSize: s.patrolSize, intervalMinutes: s.intervalMinutes, firstStart: s.firstStart, grouping: s.grouping, gap: s.gap, keep: s.keep, force });
                if (!d.success) return d.message || 'Det gick inte att bygga om.';
                await loadGrid(); drawGrid();   // stegets status och krocklistan ska visa det nya läget direkt
                A.showMsg(`Startlistan är ombyggd: ${d.patrols} patruller, ${d.shooters} starter. Dagens lista är sparad.`
                    + ` <button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-plb-undopatrol="${d.backupId}">Ångra</button>`
                    + (lastMoved.length ? ` · <button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-plb-copymoves>Kopiera listan över de ${lastMoved.length} som fick ny tid</button>` : ''), 'success');
                return true;
            }
        });
    }
    document.addEventListener('click', async e => {
        const b = e.target.closest && e.target.closest('[data-plb-undopatrol]');
        if (!b || !A || !A.isActive()) return;
        e.preventDefault();
        const d = await spost('UndoPatrolPlan', { backupId: parseInt(b.dataset.plbUndopatrol, 10) }).catch(() => null);
        if (d && d.success) { await loadGrid(); drawGrid(); }   // annars står "klart" kvar och ångra ser ut att inte ha hänt något
        A.showMsg(d && d.success ? 'Startlistan är återställd.' : esc((d && d.message) || 'Det gick inte att ångra.'), d && d.success ? 'success' : 'danger');
    });
    // De som fick ny tid får inget mejl — arrangören ringer dem. Listan är namn, klass och ny tid, som text att klistra in eller skriva ut.
    let lastMoved = [];
    const movedText = ms => ms.map(m => `${m.name} (${m.shootingClass}): patrull ${m.toPatrol} kl ca ${m.toTime}`).join('\n');
    document.addEventListener('click', async e => {
        const b = e.target.closest && e.target.closest('[data-plb-copymoves]');
        if (!b || !A || !A.isActive()) return;
        e.preventDefault();
        const ms = lastMoved;
        try { await navigator.clipboard.writeText(movedText(ms)); A.showMsg(`Listan över ${ms.length} skyttar med ny tid är kopierad. Klistra in den i ett mejl eller ett dokument.`, 'success'); }
        catch (err) { A.showMsg('Kunde inte kopiera. Så här ser listan ut:<br>' + esc(movedText(ms)).replace(/\n/g, '<br>'), 'warning'); }
    });

    // Planera dagen: hur gör ni med funktionärer som också skjuter? Fyra svar i klartext som ett förval för planeringen — och ett eget läge per person.
    async function openShooterMode() {
        const r = await sget('GetShooterMode').catch(() => null);
        if (!r || !r.success) { A.showMsg(esc((r && r.message) || 'Kunde inte läsa inställningen.'), 'danger'); return; }
        let mode = r.mode || '';
        const radio = m => `<label class="list-group-item d-flex gap-2 align-items-start"><input class="form-check-input mt-1 flex-shrink-0" type="radio" name="plbSm" value="${esc(m.key)}"${m.key === mode ? ' checked' : ''}>
            <span><span class="fw-semibold d-block">${esc(m.title)}</span><span class="small plb-dim">${esc(m.help)}</span></span></label>`;
        const titleOf = k => (r.modes.find(m => m.key === k) || {}).title || k;
        A.formModal({
            title: 'Hur gör ni med funktionärer som skjuter?', okLabel: 'Spara', wide: false,
            html: `<p class="pl-help mb-2">Välj det som passar er. Det är förvalet för alla — en enskild person kan få ett eget läge nedan.</p>
                <div class="list-group mb-3" role="radiogroup" aria-label="Hur funktionärer som skjuter hanteras">${r.modes.map(radio).join('')}</div>
                ${(r.overrides || []).length ? `<div class="small fw-semibold mb-1">Egna undantag</div><ul class="list-unstyled small mb-2" id="plbSmOver">${r.overrides.map(o => `<li class="d-flex justify-content-between gap-2 py-1 border-bottom"><span>${esc(o.name)} — ${esc(titleOf(o.mode))}</span><button type="button" class="btn btn-link btn-sm p-0" data-sm-clear="${o.memberId}">Ta bort</button></li>`).join('')}</ul>` : ''}
                <p class="pl-help small mb-0">Ett eget läge sätter du på personen: öppna personen under Personer.</p>`,
            onOpen: b => {
                b.addEventListener('click', async e => {
                    const c = e.target.closest('[data-sm-clear]'); if (!c) return;
                    const d = await spost('SetShooterMode', { memberId: parseInt(c.dataset.smClear, 10), mode: '' });
                    if (d.success) c.closest('li').remove(); else A.showMsg(esc(d.message || 'Det gick inte.'), 'danger');
                });
            },
            onOk: async b => {
                const sel = b.querySelector('input[name="plbSm"]:checked');
                if (!sel) return 'Välj ett av alternativen.';
                const d = await spost('SetShooterMode', { mode: sel.value });
                if (!d.success) return d.message || 'Det gick inte att spara.';
                await loadGrid(); drawGrid();
                return true;
            }
        });
    }
    // Ett eget läge för en person (personens sida).
    async function openPersonShooterMode(memberId, name) {
        const r = await sget('GetShooterMode').catch(() => null);
        if (!r || !r.success) { A.showMsg(esc((r && r.message) || 'Kunde inte läsa inställningen.'), 'danger'); return; }
        const own = ((r.overrides || []).find(o => o.memberId === memberId) || {}).mode || '';
        A.formModal({
            title: 'Hur gör ' + esc(name) + ' som skjutande funktionär?', okLabel: 'Spara', wide: false,
            html: `<div class="list-group" role="radiogroup">
                <label class="list-group-item d-flex gap-2"><input class="form-check-input mt-1" type="radio" name="plbPm" value=""${own === '' ? ' checked' : ''}><span><span class="fw-semibold d-block">Som alla andra</span><span class="small plb-dim">Planeringens förval${r.mode ? ': ' + esc((r.modes.find(m => m.key === r.mode) || {}).title || '') : ' (inte valt än)'}.</span></span></label>
                ${r.modes.map(m => `<label class="list-group-item d-flex gap-2"><input class="form-check-input mt-1" type="radio" name="plbPm" value="${esc(m.key)}"${m.key === own ? ' checked' : ''}><span><span class="fw-semibold d-block">${esc(m.title)}</span><span class="small plb-dim">${esc(m.help)}</span></span></label>`).join('')}</div>`,
            onOk: async b => {
                const sel = b.querySelector('input[name="plbPm"]:checked');
                const d = await spost('SetShooterMode', { memberId, mode: sel ? sel.value : '' });
                if (!d.success) return d.message || 'Det gick inte att spara.';
                A.showMsg('Sparat.', 'success');
                return true;
            }
        });
    }

    // Planera dagen steg 0 (Stefan 2026-10-26): hur länge en skytt är upptagen av sin egen start. Förval per gren, ändringsbart här.
    async function openShootTime() {
        const r = await sget('GetShootTime').catch(() => null);
        if (!r || !r.success) { A.showMsg(esc((r && r.message) || 'Skjuttiden kunde inte läsas.'), 'danger'); return; }
        const d = r.defaults || {};
        A.formModal({
            title: 'Skjuttid', okLabel: 'Spara', wide: false,
            html: `<p class="pl-help mb-2">Så här länge räknar vi att en skytt är upptagen av sin egen start. Det används för att se när någon som både skjuter och jobbar inte hinner med placeringen — och av förslagen.</p>
                <div class="mb-2"><label class="form-label mb-0" for="plbStBefore">Minuter före starten <span class="plb-dim small">(förberedelse, vapenkontroll)</span></label>
                    <input type="number" min="0" max="720" class="form-control" id="plbStBefore" inputmode="numeric" style="max-width:140px" value="${r.before}"></div>
                <div class="mb-2"><label class="form-label mb-0" for="plbStDur">Så länge tar skjutningen <span class="plb-dim small">(minuter; används när slutet inte är känt, som i fält)</span></label>
                    <input type="number" min="1" max="720" class="form-control" id="plbStDur" inputmode="numeric" style="max-width:140px" value="${r.duration}"></div>
                <div class="mb-2"><label class="form-label mb-0" for="plbStAfter">Minuter efter <span class="plb-dim small">(vila och förflyttning)</span></label>
                    <input type="number" min="0" max="720" class="form-control" id="plbStAfter" inputmode="numeric" style="max-width:140px" value="${r.after}"></div>
                <p class="pl-help small mb-0">Förval för den här grenen: ${d.before} min före, ${d.duration} min skjutning, ${d.after} min efter.${r.isOwn ? ' <button type="button" class="btn btn-link btn-sm p-0 align-baseline" id="plbStReset">Använd förvalet</button>' : ''}</p>`,
            onOpen: b => {
                const rs = b.querySelector('#plbStReset');
                if (rs) rs.addEventListener('click', () => {
                    b.querySelector('#plbStBefore').value = d.before; b.querySelector('#plbStDur').value = d.duration; b.querySelector('#plbStAfter').value = d.after;
                });
            },
            onOk: async b => {
                const n = id => { const v = b.querySelector(id).value.trim(); return v === '' ? null : parseInt(v, 10); };
                const before = n('#plbStBefore'), duration = n('#plbStDur'), after = n('#plbStAfter');
                if ([before, duration, after].some(v => v !== null && isNaN(v))) return 'Skriv minuter som tal.';
                const same = before === d.before && duration === d.duration && after === d.after;
                const res = await spost('SetShootTime', { before, duration, after, reset: same });
                if (!res.success) return res.message || 'Skjuttiden kunde inte sparas.';
                await loadGrid(); drawGrid();
                A.showMsg('Skjuttiden är sparad. Krockarna räknas om.', 'success');
                return true;
            }
        });
    }
    document.addEventListener('click', async e => {
        if (!A || !A.isActive() || !e.target.closest('[data-plb-saveroles]')) return;
        e.preventDefault();
        const card = $('plbChooseRoles'); if (!card) return;
        const res = await saveRoleChoice(card);
        if (typeof res === 'string') A.showMsg(esc(res), 'danger');
    });

    // ================================================================ Bemanningen steg för steg (bandet; internt "Planera dagen")
    // Ett band överst i Rutnätet som visar läget i TEXT: vad som är klart och vad som behöver dig. Stegen härleds på servern ur datan (PlanStepRules) — "klart" betyder att datan
    // säger det, inte att något klickats. Ingenting låses; alla steg går att göra i valfri ordning, och varje knapp öppnar en dialog som redan finns. Öppet när planeringen är ny,
    // en rad efteråt (valet minns per planering). En liten klubbtävling ser tre rader, inte fem.
    const PLAN_ACTIONS = {
        roller: { label: 'Välj roller…', fn: () => ((B.grid.roleChoices || []).length ? openChooseRoles() : openNewRole()) },
        behov: { label: 'Sätt antal…', changed: 'Ändra…', fn: () => openNeeds() },
        lage: { label: 'Välj hur ni gör…', changed: 'Ändra…', fn: () => openShooterMode() },
        skjuttid: { label: 'Skjuttid…', fn: () => openShootTime() },
        forslag: { label: 'Fyll med förslag…', fn: () => openProposal() },
        skicka: { label: 'Meddela de inlagda…', fn: () => openSendRequests() },
        koppla: { label: 'Koppla till medlemmar…', fn: () => openMatch() },
        startlista: { label: 'Bygg om startlistan…', fn: () => openPatrolPlan() },
        skrivut: { label: 'Skriv ut', fn: () => window.open('/tavlingsplanering/blad?p=' + A.ID + '&vy=bemanning', '_blank', 'noopener') },
    };
    function planPref() { try { return localStorage.getItem('plbPlan:' + A.ID); } catch (e) { return null; } }
    function setPlanPref(v) { try { localStorage.setItem('plbPlan:' + A.ID, v); } catch (e) { /* utan lagring ritas bandet om med förvalet */ } }
    function planHtml(g) {
        const steps = g.planSteps || [];
        if (!steps.length || g.family) return '';
        const pref = planPref();
        const todo = steps.filter(s => s.state === 'Todo'), next = todo[0];
        const open = pref === 'open' ? true : pref === 'closed' ? false : steps[0].state === 'Todo';
        const stateOf = s => s.state === 'Done'
            ? '<span class="badge text-bg-success fw-normal plb-step-state">klart</span>'
            : '<span class="badge text-bg-warning fw-normal plb-step-state">behöver dig</span>';
        const btns = s => (s.actions || []).filter(k => PLAN_ACTIONS[k]).map((k, i) => `<button type="button" class="btn btn-sm ${i === 0 && s.state === 'Todo' && k !== 'skrivut' ? 'btn-primary' : 'btn-outline-secondary'}" data-plb-plan-action="${esc(k)}">${esc(s.state === 'Done' && PLAN_ACTIONS[k].changed ? PLAN_ACTIONS[k].changed : PLAN_ACTIONS[k].label)}</button>`).join(' ');
        const line = next
            ? `<b>${todo.length} ${todo.length === 1 ? 'steg kvar' : 'steg kvar'}</b> · Nästa: <b>${esc(next.title)}</b> — ${esc(next.text)}`
            : `<b>Allt är klart</b> — alla platser är fyllda och inget krockar.${(steps.find(s => s.key === 'startlista') || {}).text ? ' ' + esc(steps.find(s => s.key === 'startlista').text) + '.' : ''}`;
        return `<section class="pl-card" id="plbPlan" aria-labelledby="plbPlanH">
            <div class="d-flex justify-content-between align-items-start gap-2">
                <h2 class="h6 mb-1" id="plbPlanH">Bemanningen steg för steg</h2>
                <button type="button" class="btn btn-link btn-sm p-0" id="plbPlanToggle" data-plb-plan-toggle="${open ? 'closed' : 'open'}" aria-expanded="${open}" aria-controls="plbPlanSteps">${open ? 'Visa som en rad' : 'Visa alla steg'}</button>
            </div>
            ${open ? '' : `<p class="mb-0 small" id="plbPlanLine">${line}</p>`}
            <ol class="list-unstyled mb-0 small" id="plbPlanSteps"${open ? '' : ' hidden'}>${steps.map(s => `<li class="py-2 border-top d-flex flex-wrap gap-2 align-items-center" data-plb-step="${esc(s.key)}">
                <span class="d-flex gap-2 align-items-start flex-grow-1" style="min-width:14rem">${stateOf(s)}<span><span class="fw-semibold">${esc(s.title)}</span><span class="d-block plb-dim">${esc(s.text)}</span></span></span>
                <span class="d-flex gap-1 flex-wrap">${btns(s)}</span></li>`).join('')}</ol>
        </section>`;
    }
    document.addEventListener('click', e => {
        if (!A || !A.isActive()) return;
        const t = e.target.closest && e.target.closest('[data-plb-plan-toggle]');
        if (t) { e.preventDefault(); setPlanPref(t.dataset.plbPlanToggle); drawGrid(); const again = $('plbPlanToggle'); if (again) again.focus(); return; }
        const a = e.target.closest && e.target.closest('[data-plb-plan-action]');
        if (a && PLAN_ACTIONS[a.dataset.plbPlanAction]) { e.preventDefault(); PLAN_ACTIONS[a.dataset.plbPlanAction].fn(); }
    });

    // "Vem behövs, och hur många?" — ETT ställe för antalen: en rad per roll, som i ett Excelark. Förut krävdes "Åtgärder → Antal per dag…" per rad (persontesterna gick den vägen
    // fem gånger). Roller med olika antal per dag, station eller pass redovisas och ändras på raden, så att inget överskrivs i smyg.
    function openNeeds() {
        const g = B.grid, cols = (g.columns || []).filter(c => c.key);
        if (!cols.length) { A.showMsg('Lägg till en dag först — antalen gäller per dag.', 'warning'); return; }
        const roles = [];
        (g.rows || []).filter(r => !g.family || r.planningId).forEach(r => {
            if (r.roleKey === 'tavlingsledning') return;
            let x = roles.find(y => y.roleKey === r.roleKey && y.planningId === r.planningId);
            if (!x) roles.push(x = { roleKey: r.roleKey, name: r.roleName, planningId: r.planningId, station: String(r.scopeType || '').toLowerCase() === 'station', rows: [] });
            x.rows.push(r);
        });
        roles.forEach(x => {
            const vals = x.rows.map(r => r.needed || 0), first = vals[0] || 0;
            x.value = first;
            x.uniform = vals.every(v => v === first) && !x.rows.some(r => r.hasOwnStationNeed);
            x.byPass = x.rows.some(r => Object.values(r.neededByPass || {}).some(v => v > 0));
            x.byDay = x.rows.some(r => new Set(Object.values(r.neededByDay || {})).size > 1);
            x.editable = x.uniform && !x.byPass && !x.byDay;
        });
        if (!roles.length) { A.showMsg('Välj roller först.', 'warning'); return; }
        // Persontest 6b: "8" skrevs för åtta stationer och blev 64 platser — "per station" stod i andra meningen. Nu står det vid själva fältet, och summan räknas live.
        const line = (x, i) => `<tr><th scope="row" class="fw-normal">${esc(x.name)}${x.station ? ' <span class="plb-dim small d-block">på varje station</span>' : ''}</th>
            <td>${x.editable
                ? `<label class="visually-hidden" for="plbNd${i}">Antal ${esc(x.name)}${x.station ? ' på varje station' : ''}</label><div class="d-flex align-items-center gap-2 flex-wrap"><input type="number" min="0" max="50" class="form-control form-control-sm" style="width:5.5rem" id="plbNd${i}" data-nd="${i}" inputmode="numeric" value="${x.value}">${x.station ? '<span class="small">på varje station</span>' : ''}</div><div class="small plb-dim" id="plbNdSum${i}" aria-live="polite">${sumHint(x, x.value)}</div>`
                : `<span class="small text-body-secondary">Olika på raderna — ändra där (Åtgärder → Antal per dag…)</span>`}</td></tr>`;
        const stationWord = n => `${n} ${n === 1 ? 'station' : 'stationer'}`;
        const sumHint = (x, n) => {
            if (isNaN(n) || n === 0 || !x.station || x.rows.length < 2) return '';
            const warn = n > 3 ? ` <b class="text-danger">Det är många per station — menar du ${n} på varje station?</b>` : '';
            return `${n} på varje station × ${stationWord(x.rows.length)} = ${n * x.rows.length} platser.${warn}`;
        };
        A.formModal({
            title: 'Vem behövs, och hur många?', okLabel: 'Spara', wide: false,
            html: `<p class="pl-help small mb-2">${cols.length > 1 ? 'Hur många som behövs varje dag, av varje roll.' : 'Hur många som behövs av varje roll.'} <b>Gäller rollen stationer</b> skriver du hur många på varje station — en ansvarig och en markör på varje station betyder 1 och 1. Tävlingsledning bemannas av den som skapade planeringen.</p>
                <p class="pl-help small mb-2" id="plbNeedsSimult"><b>Antalet är hur många som behövs samtidigt.</b> Behöver en stationschef avlösning under dagen är det fortfarande <b>1</b> — avbytarna lägger du in i rutans <b>skiftschema</b> (knappen <i>bemanning</i> i rutan), var och en med klockslag, till exempel 08–11, 11–14 och 14–17. Där ser du vem som står när och var det blir hål, och den som avlöser ser det i sitt schema.</p>
                <table class="table table-sm align-middle mb-2" id="plbNeedsTable"><tbody>${roles.map(line).join('')}</tbody></table>
                <div class="d-flex gap-2 flex-wrap mb-1"><button type="button" class="btn btn-sm btn-outline-secondary" id="plbNdMoreRoles">Välj fler roller…</button>
                    <button type="button" class="btn btn-sm btn-outline-secondary" id="plbNdOwnRole">Egen roll…</button></div>
                <div class="text-danger small" id="plbNdErr" role="alert"></div>`,
            onOpen: b => {
                const sums = () => b.querySelectorAll('[data-nd]').forEach(el => { const i = parseInt(el.dataset.nd, 10), s = b.querySelector('#plbNdSum' + i); if (s) s.innerHTML = sumHint(roles[i], el.value.trim() === '' ? NaN : parseInt(el.value, 10)); });
                b.addEventListener('input', e => { if (e.target.closest('[data-nd]')) sums(); });
                sums();
                b.querySelector('#plbNdMoreRoles').addEventListener('click', () => { if ((g.roleChoices || []).length) openChooseRoles(); else openNewRole(); });
                b.querySelector('#plbNdOwnRole').addEventListener('click', () => openNewRole());
            },
            onOk: async b => {
                const changes = [...b.querySelectorAll('[data-nd]')].map(el => ({ x: roles[parseInt(el.dataset.nd, 10)], n: el.value.trim() === '' ? NaN : parseInt(el.value, 10) }))
                    .filter(c => c.x.editable && c.n !== c.x.value);
                if (changes.some(c => isNaN(c.n) || c.n < 0 || c.n > 50)) return 'Skriv ett antal mellan 0 och 50.';
                if (!changes.length) return true;
                for (const { x, n } of changes) {
                    const d = await spost('SetRoleNeeds', { roleKey: x.roleKey, scopeKind: x.station ? 'Station' : 'All', scopeKey: '', all: n, days: cols.map(c => ({ date: c.key, count: n })), passes: [] }, x.planningId);
                    if (!d.success) return `${x.name}: ${d.message || 'Det gick inte att spara.'}`;
                }
                await loadGrid(); drawGrid();
                A.showMsg(`${changes.length} ${changes.length === 1 ? 'roll fick' : 'roller fick'} nytt antal.`, 'success');
                return true;
            }
        });
    }

    function drawGrid() {
        const g = B.grid, cols = g.columns || [];
        A.setHead('Bemanning', gridSummary());
        A.setActions([
            { label: 'Lägg till en roll…', icon: 'bi-plus-lg', fn: () => openNewRole() },
            (g.roleChoices || []).length && { label: 'Välj roller…', icon: 'bi-ui-checks', fn: openChooseRoles },
            { label: 'Lägg till en dag…', icon: 'bi-calendar-plus', fn: () => openDay(null) },
            { header: 'Fyll platserna' },
            { label: 'Lägg in en namnlista…', icon: 'bi-clipboard', fn: openNameList },
            !g.family && linked() && (g.volunteersTotal || 0) > 0 && { label: 'Fyll med förslag…', icon: 'bi-magic', fn: openProposal },
            !g.family && linked() && g.patrolPlanAvailable && { header: 'Startlistan' },
            !g.family && linked() && g.patrolPlanAvailable && { label: 'Bygg startlistan runt funktionärerna…', icon: 'bi-diagram-2', fn: openPatrolPlan },
            linked() && { header: 'Inställningar' },
            linked() && { label: 'Funktionärer som skjuter…', icon: 'bi-bullseye', fn: openShooterMode },
            linked() && { label: 'Skjuttid…', icon: 'bi-stopwatch', fn: openShootTime },
            { divider: true },
            { label: 'Skriv ut bemanningen', icon: 'bi-printer', fn: () => window.open('/tavlingsplanering/blad?p=' + A.ID + '&vy=bemanning', '_blank', 'noopener') },
        ]);

        let notes = planHtml(g) + disciplineLine();
        // Omgång 3 steg 10: en ny planering väljer sina roller i stället för att mötas av hela katalogen.
        if (g.roleChoicePending && (g.roleChoices || []).length) notes += chooseRolesCard(g);
        if (!linked()) notes += `<p class="pl-help small mb-2"><i class="bi bi-info-circle me-1" aria-hidden="true"></i>Planeringen är inte kopplad till någon tävling än. Bemanningen sparas i planeringen och följer med när den kopplas.</p>`;
        // Planera dagen: frågan ställs en gång, när det finns både startlista/anmälda och funktionärer, och inget val är gjort.
        if (!g.family && linked() && !g.shooterMode && (g.volunteersTotal || 0) > 0)
            notes += `<div class="pl-next" id="plbShooterAsk"><div class="t"><b>Hur gör ni med funktionärer som också skjuter?</b> Fyra sätt att göra — välj ert, så anpassar sig förslagen.</div>
                <button type="button" class="btn btn-sm btn-primary" data-plb-shootermode>Välj</button></div>`;
        // Stående "Ångra": senaste tillämpade förslaget går att ångra så länge raderna är orörda — också efter en omladdning.
        if (g.undoBatchId) notes += `<div class="pl-next" id="plbUndoBar"><div class="t">Senaste förslaget lade in <b>${g.undoRows}</b> ${g.undoRows === 1 ? 'person' : 'personer'}. Du kan ångra det så länge ingen har svarat eller ändrat raderna.</div>
            <button type="button" class="btn btn-sm btn-outline-secondary" data-plb-undoprop="${esc(g.undoBatchId)}">Ångra förslaget</button></div>`;
        const vols = g.volunteers || [];
        const noNeeds = !(g.placesNeeded > 0);   // utan behov har förslaget inga platser att fylla (Stefan 2026-10-27)
        if (vols.length) notes += `<div class="pl-next warn"><div class="t"><b>${vols.length} ${vols.length === 1 ? 'har anmält sig' : 'har anmält sig'}</b> men är inte placerade än: ${vols.slice(0, 6).map(v => esc(v.name)).join(', ')}${vols.length > 6 ? ' m.fl.' : ''}${noNeeds && !g.family ? '<div class="small mt-1" id="plbNoNeedsHint">Förslaget fyller de platser som behövs. Ni har ännu inte sagt hur många som behövs av varje roll — gör det först.</div>' : ''}</div>
            <div class="d-flex gap-1 flex-wrap">${!g.family && linked() ? (noNeeds ? '<button type="button" class="btn btn-sm btn-primary" data-plb-plan-action="behov"><i class="bi bi-123 me-1" aria-hidden="true"></i>Sätt antal…</button>' : '<button type="button" class="btn btn-sm btn-primary" data-plb-proposal><i class="bi bi-magic me-1" aria-hidden="true"></i>Fyll med förslag</button>') : ''}
            <button type="button" class="btn btn-sm btn-warning" data-plb-queue>Visa dem</button></div></div>`;
        const clashes = g.clashes || [];
        if (clashes.length) notes += `<div class="alert alert-danger py-2 small"><div class="fw-semibold mb-1"><i class="bi bi-exclamation-octagon me-1" aria-hidden="true"></i>Samma person på två poster samtidigt</div>`
            + clashes.slice(0, 8).map(c => `<div>· <button type="button" class="btn btn-link p-0 align-baseline fw-semibold" data-plb-person="${esc(c.personKey)}">${esc(c.name)}</button> — ${esc(c.a)} och ${esc(c.b)}${c.dateKey ? ` <span class="text-body-secondary">(${esc(dayLabel(c.dateKey))})</span>` : ''}</div>`).join('')
            + (clashes.length > 8 ? `<div class="text-body-secondary">…och ${clashes.length - 8} till</div>` : '') + '</div>';
        // Omgång 3 steg 9: krockar över hela veckan — med en annan del av veckan, en egen start eller en avsikt att tävla.
        const fam = g.familyClashes || [];
        if (fam.length) {
            const KIND = { uppdrag: 'två platser samtidigt', start: 'placerad under egen start', avsikt: 'tänker tävla samma dag' };
            notes += `<div class="alert alert-warning py-2 small" id="plbFamilyClashes"><div class="fw-semibold mb-1"><i class="bi bi-calendar2-week me-1" aria-hidden="true"></i>Krockar i veckan</div>`
                + fam.slice(0, 10).map(c => `<div data-clash-kind="${esc(c.kind || '')}">· <button type="button" class="btn btn-link p-0 align-baseline fw-semibold" data-plb-person="${esc(c.personKey)}">${esc(c.name)}</button>
                    <span class="badge ${c.kind === 'avsikt' ? 'text-bg-light border' : 'text-bg-warning'} fw-normal">${esc(KIND[c.kind] || c.kind || '')}</span>
                    ${esc(c.a)} — ${esc(c.b)}${c.dateKey ? ` <span class="text-body-secondary">(${esc(dayLabel(c.dateKey))})</span>` : ''}</div>`).join('')
                + (fam.length > 10 ? `<div class="text-body-secondary">…och ${fam.length - 10} till</div>` : '') + '</div>';
        }
        const un = g.unreachable || [];
        if (un.length) notes += `<div class="alert alert-danger py-2 small d-flex gap-2 align-items-center flex-wrap"><div class="flex-grow-1"><i class="bi bi-exclamation-triangle-fill me-1" aria-hidden="true"></i><b>${un.length} kan inte nås alls</b> — varken konto eller e-post: ${esc(un.slice(0, 10).join(', '))}${un.length > 10 ? ' m.fl.' : ''}.</div>
            <button type="button" class="btn btn-sm btn-danger" data-plb-match>Koppla till medlemmar…</button></div>`;
        else if (g.externalCount) notes += `<p class="pl-help small mb-2"><i class="bi bi-info-circle me-1" aria-hidden="true"></i>${g.externalCount} ${g.externalCount === 1 ? 'person saknar' : 'personer saknar'} konto på pistol.nu. De kan få besked med e-post, men inget eget schema och inga påminnelser förrän de har ett konto.</p>`;

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
                    // Fler än behovet (Anders, persontest 12: 3 på en plats som behöver 1) är ingen brist men värt att se — en diskret gul text, ingen larmfärg.
                    const over = filled > need;
                    tally = `<div class="plb-need ${short ? 'text-danger fw-semibold' : over ? 'text-warning-emphasis' : 'text-success'}" title="${over ? 'Fler på plats samtidigt än behovet' : 'Flest på plats samtidigt'}" data-plb-over="${over ? filled - need : 0}">${filled}/${need}${short ? ' — saknas ' + (need - filled) : ''}${over ? ' — ' + (filled - need) + ' över behovet' : ''}</div>`;
                }
                if (entries.length > filled && filled > 0) tally += `<div class="plb-dim small">${entries.length} placeringar, högst ${filled} samtidigt</div>`;
                const holes = (row.gaps || []).filter(x => x.dateKey === c.key);
                if (holes.length) {
                    cls += ' plb-short';
                    tally += `<div class="plb-need text-danger fw-semibold"><i class="bi bi-exclamation-triangle-fill me-1" aria-hidden="true"></i>ingen ${holes.map(x => esc(x.from) + '–' + esc(x.to)).join(', ')}</div>`;
                }
                // EN ingång i rutan (Stefan 2026-10-28): skiftschemat är dialogen för att lägga till, byta och avlösa. En tom ruta visar "+ lägg till", en ruta med folk "bemanning".
                const sched = entries.length || holes.length
                    ? `<button type="button" class="plb-add plb-sc" data-plb-sched="${ri}" data-col="${ci}" title="Se och ändra vem som står när, lägg till någon"><i class="bi bi-people" aria-hidden="true"></i> bemanning</button>`
                    : `<button type="button" class="plb-add" data-plb-sched="${ri}" data-col="${ci}"><i class="bi bi-plus" aria-hidden="true"></i> lägg till</button>`;
                h += `<td class="${cls}">${entries.map(e => entryHtml(e, row, c.key)).join('')}${tally}${sched}</td>`;
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
            <p class="pl-help small mt-2 mb-0">Klicka på <b>lägg till</b> i en ruta för att sätta in en person. Utan tid gäller placeringen hela dagen. Gulmarkerade namn saknar konto på pistol.nu.${g.family ? ' Varje del har sin egen rubrik; en grå ruta betyder att delen inte har den dagen planerad.' : ''}</p>${hiddenNote}`;
        const t = $('plbGrid');
        // Rollkolumnen är smalare på telefon (se css) — samma mått här, annars rullar rutnätet i onödan.
        if (t) t.style.minWidth = phone ? '' : (260 + Math.max(1, shownCount) * 215) + 'px';
        const nr = $('plbNewRole');
        if (nr) nr.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); createRole(nr.value); } });
        wireRowOrder();
    }

    // Planera dagen (persontestet): krocken med personens egen start ska synas VID rutan, inte bara i en gul ruta högst upp. Servern ger
    // krockarna som text ("Stationsmarkör 12:00–14:00 (…)" mot "egen start i …"); vi hittar den som hör till just den här raden.
    function startClashFor(e, row, dateKey) {
        const list = (B.grid.familyClashes || []).filter(c => c.kind === 'start' && c.personKey === keyOf(e) && c.dateKey === dateKey);
        for (const c of list) {
            const mine = /^egen start/.test(c.a || '') ? c.b : c.a, theirs = mine === c.a ? c.b : c.a;
            if (!mine || !row || mine.indexOf(row.roleName) !== 0) continue;
            if (e.timeLabel ? mine.indexOf(e.timeLabel) < 0 : mine.indexOf('hela dagen') < 0) continue;
            return theirs;
        }
        return null;
    }
    function entryHtml(e, row, dateKey) {
        const clash = startClashFor(e, row, dateKey);
        return `<div class="plb-entry${clash ? ' plb-entry-clash' : ''}"><button type="button" class="plb-person" data-plb-person="${esc(keyOf(e))}" title="Visa ${esc(e.displayName)}${e.clubName ? ', ' + esc(e.clubName) : ''}">`
            + (e.isResponsible ? '<i class="bi bi-star-fill text-warning" title="Ansvarig" aria-label="Ansvarig"></i>' : '')
            + `<span class="plb-name${e.isExternal ? ' text-warning-emphasis' : ''}">${esc(e.displayName)}</span>`
            + (e.clubName ? `<span class="plb-club">${esc(e.clubName)}</span>` : '')
            + (e.timeLabel ? `<span class="badge text-bg-light border fw-normal plb-time">${esc(e.timeLabel)}</span>` : '')
            // Omgång 3 (K8): en gul rad som säger vad det gäller, i stället för en röd triangel som såg ut som ett fel.
            + (e.isExternal && !e.hasContact ? '<span class="plb-nocontact">saknar konto och e-post</span>' : '')
            + (clash ? `<span class="plb-startclash"><i class="bi bi-exclamation-triangle-fill me-1" aria-hidden="true"></i>${esc(clash.replace(/^egen start i [^:]*: /, 'skjuter själv '))}</span>` : '')
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

    // ---- kandidatlistan (Planera dagen steg 1): anmälda funktionärer och skyttar, med start och ledighet ----
    const normTime = v => window.hpskNormalizeTime ? window.hpskNormalizeTime(v) : (v || '').trim();
    function candBadges(c, slotKnown) {
        const b = [];
        if (c.alreadyHere) b.push('<span class="badge text-bg-info fw-normal">Står redan här</span>');
        else if (c.assignedToday > 0) b.push('<span class="badge text-bg-secondary fw-normal">Placerad</span>');
        if (c.volunteer) b.push('<span class="badge text-bg-success fw-normal">Anmäld funktionär</span>');
        if (c.free === false) b.push('<span class="badge text-bg-warning fw-normal">Upptagen</span>');
        else if (c.free === true && slotKnown) b.push('<span class="badge text-bg-light border fw-normal">Ledig</span>');
        return b.join(' ');
    }
    // Var personen redan är placerad den dagen — annars går det inte att se vilka som är tagna och var.
    function candPlaced(c) {
        const p = (c.placements || []).filter(Boolean);
        return p.length ? `<span class="d-block text-body-secondary"><i class="bi bi-person-check me-1" aria-hidden="true"></i>Placerad: ${esc(p.join('; '))}</span>` : '';
    }
    function candSub(c) {
        const parts = [];
        const s = (c.starts || [])[0];
        if (s) parts.push(`skjuter ${s.time}${s.where ? ' · ' + s.where.split(' · ')[0].toLowerCase() : ''}${c.shooterClass ? ' · ' + c.shooterClass : ''}`);
        else if (c.shooterClass) parts.push('klass ' + c.shooterClass);
        if (c.club) parts.push(c.club);
        return parts.join(' · ');
    }

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
                if (!await A.plConfirm('Ta bort raden ' + row.roleName + '?', n + (n === 1 ? ' person står' : ' personer står') + ' på raden och förlorar sin plats.', 'Ta bort raden', true)) return;
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
                    ${col ? '<div class="pl-help small mt-1">Datumet går inte att ändra. Flytta placeringarna till en ny dag i stället.</div>' : ''}</div>
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
                if (!await A.plConfirm('Ta bort ' + col.label + '?', n + (n === 1 ? ' placering ligger' : ' placeringar ligger') + ' på dagen och tas bort med den.', 'Ta bort dagen', true)) return;
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
                <p class="pl-help small mt-2 mb-0">Alla placeringar den dagen kopieras. Den som redan står på samma roll den andra dagen kopieras inte en gång till.</p>`,
            onOk: async b => {
                const to = b.querySelector('#plbCopyTo').value;
                if (!to) return 'Välj en dag.';
                const d = await spost('CopyDay', { fromDate: col.key, toDate: to });
                if (!d.success) return d.message || 'Det gick inte att kopiera.';
                await loadGrid(); drawGrid();
                A.showMsg('Kopierade ' + (d.copied || 0) + ' placeringar.' + (d.skipped ? ' ' + d.skipped + ' fanns redan.' : ''));
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
                    if (!d.success) { await refreshPart(); return d.message || 'Placeringen flyttades, men inte till den dagen.'; }
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
        const extra = extraPeople().length;
        const parts = [`${(m.totalPeople || 0) + extra} personer`, `${(m.assignedCount || 0) + extra} placerade`];
        const hard = B.weekMap ? [...B.weekMap.values()].filter(w => w.clashes > 0).length : 0;
        if (hard) parts.push(`<span class="text-danger fw-semibold">${hard} med krock</span>`);
        if (m.unnotifiedCount) parts.push(`${m.unnotifiedCount} ej meddelade`);
        if (m.externalCount) parts.push(`${m.externalCount} utan konto`);
        return parts.join(' · ');
    }
    function matches(p) {
        const asg = p.assignments || [];
        if (B.show === 'queue' && !((p.volunteer || (p.availabilityLabels || []).length) && asg.length === 0)) return false;
        if (B.show === 'assigned' && !asg.length && !p.weekOnly) return false;
        if (B.show === 'unnotified' && !asg.some(a => !a.readOnly && asStatus(a.status) === 'Planned' && !a.notifiedAt)) return false;
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
        let notes = `<p class="pl-help small mb-2" id="plbPeopleHint"><i class="bi bi-info-circle me-1" aria-hidden="true"></i>Här ser du varje person och vad hen ska göra. Vill du <b>placera någon</b>? Tryck på <b>lägg till</b> i rätt ruta i <button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-plb-part="rutnat">Rutnätet</button>, eller på <b>Placera</b> vid personen här.${weekTab() ? ' Ett rött märke betyder krock: någon jobbar medan hen själv ska skjuta, eller har två placeringar på samma tid. Öppna personen för dag för dag.' : ''}</p>`;
        if (m.unassignedVolunteerCount) notes += `<div class="pl-next warn"><div class="t"><b>${m.unassignedVolunteerCount} ${m.unassignedVolunteerCount === 1 ? 'person har' : 'personer har'} anmält sig</b> utan att bli placerade. De väntar på att du fördelar rollerna.</div>
            ${B.show === 'queue' ? '' : '<button type="button" class="btn btn-sm btn-warning" data-plb-show="queue">Visa dem</button>'}</div>`;
        if (m.unnotifiedCount) notes += `<div class="pl-next"><div class="t">${m.unnotifiedCount} ${m.unnotifiedCount === 1 ? 'person har' : 'personer har'} ännu inte fått besked om sin placering.</div>
            ${B.show === 'unnotified' ? '' : '<button type="button" class="btn btn-sm btn-outline-secondary" data-plb-show="unnotified">Visa</button>'}</div>`;
        const roles = new Map();
        (B.people || []).forEach(p => (p.assignments || []).forEach(a => roles.set(a.roleKey, a.roleName)));
        const opt = (v, l, cur) => `<option value="${esc(v)}"${v === cur ? ' selected' : ''}>${esc(l)}</option>`;
        area().innerHTML = notes + `<div class="pl-card">
            <div class="d-flex flex-wrap gap-2 align-items-center mb-2">
                <input type="search" class="form-control form-control-sm" id="plbQ" style="max-width:240px" placeholder="Sök namn, roll, förening" aria-label="Sök" value="${esc(B.q)}">
                <select class="form-select form-select-sm" id="plbShow" style="width:auto" aria-label="Vilka som visas">
                    ${opt('', 'Alla personer', B.show)}${opt('queue', 'Anmälda, inte placerade', B.show)}${opt('assigned', 'Är placerade', B.show)}${opt('unnotified', 'Inte meddelade', B.show)}${opt('prep', 'Har förberedelser', B.show)}${opt('external', 'Utan konto', B.show)}
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
        if (!asg.length) chips = p.weekOnly ? '<span class="plb-dim small">Placerad i en annan del</span>' : p.volunteer
            ? '<span class="text-warning-emphasis small"><i class="bi bi-hand-thumbs-up me-1" aria-hidden="true"></i>Anmäld — inte placerad än</span>'
            : ((p.availabilityLabels || []).length ? '<span class="text-warning-emphasis small">Har angett tider — inte placerad än</span>'
                : ((p.prep || []).length ? '<span class="plb-dim small">Bara förberedelser</span>' : '<span class="plb-dim small">—</span>'));
        const state = p.weekOnly ? '' : `<span class="pl-state ${STATE_CLS[p.state] || 'off'}">${esc(p.state)}</span>`;
        const startsLine = (p.starts || []).length ? `<div class="plb-dim small" data-plb-starts>Skjuter själv: ${p.starts.map(esc).join(', ')}</div>` : '';
        const menu = (p.readOnly || p.weekOnly) ? '' : A.rowMenu([
            { label: 'Öppna ' + first(p.displayName), fn: () => nav('personer', p.key) },
            { label: 'Placera på en plats…', fn: () => openAssignment({ person: p }) },
            asg.some(a => !a.readOnly) && (p.memberId || p.email) && { label: 'Meddela om placeringarna…', fn: () => notifyPerson(p) },
        ]);
        const call = p.phone ? ` <a class="btn btn-sm btn-outline-success" href="tel:${esc(p.phone)}" title="Ring ${esc(p.displayName)}" aria-label="Ring ${esc(p.displayName)}"><i class="bi bi-telephone" aria-hidden="true"></i></a>` : '';
        return `<tr><td><button type="button" class="btn btn-link p-0 fw-semibold text-start" data-plb-person="${esc(p.key)}">${esc(p.displayName)}</button>${weekBadges(p)}
                ${p.hasAdminAccess ? ' <i class="bi bi-shield-lock text-warning" title="Får hantera tävlingen" aria-label="Får hantera tävlingen"></i>' : ''}
                ${p.isExternal ? ' <span class="badge text-bg-light border fw-normal" title="Saknar konto på pistol.nu">utan konto</span>' : ''}
                ${!B.byOrg && orgOf(p) !== 'Ingen förening' ? `<div class="plb-dim small">${esc(orgOf(p))}</div>` : ''}${startsLine}
                <div class="d-md-none mt-1 d-flex gap-1 flex-wrap">${chips}</div><div class="d-sm-none mt-1">${state}</div></td>
            <td class="d-none d-md-table-cell"><div class="d-flex gap-1 flex-wrap">${chips}</div></td>
            <td class="d-none d-sm-table-cell">${state}</td>
            <td class="text-end text-nowrap">${menu}${call}</td></tr>`;
    }
    function drawPeopleRows() {
        const host = $('plbPeople'); if (!host) return;
        const everyone = allPeople();
        let list = everyone.filter(matches);
        const total = everyone.length;
        $('plbCount').textContent = list.length === total ? `${total} personer` : `Visar ${list.length} av ${total}`;
        if (!list.length) {
            host.innerHTML = `<p class="pl-help mb-0">${total ? 'Ingen person matchar.' : 'Ingen är placerad än. Sätt in folk i rutnätet, eller använd Hitta funktionärer.'}</p>`;
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
                rows += `<tr class="plb-org"><th colspan="4" scope="rowgroup">${esc(lastOrg)} <span class="plb-dim fw-normal small">· ${n} ${n === 1 ? 'person' : 'personer'}${o ? ' · ' + o.assignments + ' placeringar' : ''}</span></th></tr>`;
            }
            rows += personRow(p);
        });
        host.innerHTML = `<div class="table-responsive"><table class="table table-sm align-middle mb-0"><thead><tr>
                <th scope="col">Person</th><th scope="col" class="d-none d-md-table-cell">Placeringar</th><th scope="col" class="d-none d-sm-table-cell">Läge</th><th scope="col" class="text-end"><span class="visually-hidden">Åtgärder</span></th>
            </tr></thead><tbody>${rows}</tbody></table></div>`
            + (list.length > slice.length ? `<button type="button" class="btn btn-sm btn-outline-secondary mt-2" data-plb-more>Visa ${Math.min(60, list.length - slice.length)} till (${list.length - slice.length} kvar)</button>` : '')
            + (B.byOrg ? '<p class="pl-help small mt-2 mb-0">Föreningen är den som personen ställer upp för — förvalt huvudklubben.</p>' : '');
    }

    // ---- en person ----
    function findPerson(key) {
        return (B.people || []).find(x => x.key === key)
            || (B.people || []).find(x => (x.displayName || '').toLowerCase() === String(key).replace(/^n:/, ''))
            || extraPeople().find(x => x.key === key)
            || (B.solo && B.solo.key === key ? B.solo : null)
            || null;
    }
    function personGone() {
        A.setHead('Bemanning', '');
        area().innerHTML = `<p class="mb-2"><a href="#vy=bemanning&del=personer" data-plb-back>← Alla personer</a></p>
            <div class="pl-card"><p class="mb-0">Personen finns inte längre i bemanningen.</p></div>`;
    }
    // En person som bara är placerad i en annan del av familjen står inte i huvudplaneringens egen lista: hen läses ur personens vecka.
    async function drawPersonFromWeek(key) {
        const seq = B.seq;
        area().innerHTML = A.spinner();
        let d = null;
        try { d = await sget('GetFamilyPersonWeek', { personKey: key }); } catch (e) { d = null; }
        if (seq !== B.seq || personKeyParam() !== key) return;
        if (d && d.success) { B.solo = weekOnlyPerson(d); B.pre = d; drawPerson(key); } else personGone();
    }
    const placeCount = p => p.weekOnly ? p.weekCount : ((B.weekMap && family() && B.weekMap.get(p.key)) ? B.weekMap.get(p.key).assignments : (p.assignments || []).length);
    // Åtgärderna på personens sida. Kopplingar gäller hela familjen (identityPost); veckans data (wk) tillför "Ångra koppling…" när
    // namnet inte stod i huvudplaneringen, och det egna läget som skjutande funktionär.
    function personActions(p, wk) {
        if (p.readOnly) return [];
        const asg = p.assignments || [], editable = asg.filter(a => !a.readOnly), own = !p.weekOnly;
        const orig = (asg.find(a => a.originalName) || {}).originalName;
        const canUndo = family() && wk && !wk.isExternal && !(orig && orig !== p.displayName);
        const shooter = wk && !wk.isExternal && wk.memberId > 0 && linked();
        return [
            own && { label: 'Placera ' + first(p.displayName) + '…', icon: 'bi-plus-lg', fn: () => openAssignment({ person: p }) },
            own && editable.length && (p.memberId || p.email) && { label: 'Meddela om placeringarna…', icon: 'bi-send', fn: () => notifyPerson(p) },
            own && { divider: true },
            { label: 'Byt namn…', icon: 'bi-pencil', fn: () => openRenamePerson(p) },
            !p.memberId && { label: 'Koppla till en medlem…', icon: 'bi-person-check', fn: () => openLinkPerson(p) },
            own && { label: p.email ? 'Ändra e-post…' : 'Lägg till e-post…', icon: 'bi-envelope', fn: () => openEmailPerson(p) },
            own && asg.length && { divider: true },
            own && editable.length && { label: 'Lämna över alla placeringar till någon annan…', icon: 'bi-arrow-left-right', fn: () => openReplace(p) },
            (canUndo || shooter) && { divider: true },
            canUndo && { label: 'Ångra koppling…', icon: 'bi-arrow-counterclockwise', fn: () => undoIdentity(p) },
            shooter && { label: 'Hur gör hen som skjutande funktionär…', icon: 'bi-bullseye', fn: () => openPersonShooterMode(wk.memberId, wk.name) },
        ];
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
                <p class="pl-help small mt-2 mb-0">Placeringar samma dagar visas som krockar i rutnätet.</p>`
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
                + '<p class="pl-help small mb-0">Placeringar de dagarna visas som krockar i rutnätet, så att du kan lägga dem när hen inte skjuter.</p>',
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
            if (family()) drawPersonFromWeek(key); else personGone();
            return;
        }
        const ro = p.readOnly || p.weekOnly;   // en person som bara står i en annan del ändras i den delen och i Rutnätet
        const asg = p.assignments || [];
        const editable = asg.filter(a => !a.readOnly);
        const sub = [p.clubName ? esc(p.clubName) : '', p.memberId ? 'Medlem på pistol.nu' : '<span class="text-warning-emphasis">Saknar konto på pistol.nu</span>'].filter(Boolean).join(' · ');
        A.setHead(p.displayName, sub);
        A.setActions(personActions(p, null));

        let h = `<p class="mb-3"><a href="#vy=bemanning&del=personer" data-plb-back>← Alla personer</a></p><div id="plbWeekTop"></div>`;
        const orig = (asg.find(a => a.originalName) || {}).originalName;
        if (orig && orig !== p.displayName) h += `<div class="pl-next"><div class="t">Hette <b>${esc(orig)}</b> när hen skrevs in i planen.</div><button type="button" class="btn btn-sm btn-outline-secondary" data-plb-undo>Ångra kopplingen</button></div>`;

        // Kontakt
        const contact = [];
        if (p.phone) contact.push(`<a href="tel:${esc(p.phone)}"><i class="bi bi-telephone me-1" aria-hidden="true"></i>${esc(p.phone)}</a>`);
        if (p.email) contact.push(`<a href="mailto:${esc(p.email)}"><i class="bi bi-envelope me-1" aria-hidden="true"></i>${esc(p.email)}</a>`);
        if (!p.memberId) {
            h += `<div class="pl-card"><h2>Hur når vi ${esc(first(p.displayName))}?</h2>
                <p class="pl-help mb-2">Utan konto på pistol.nu får hen inget eget schema och inga påminnelser. Med e-post kan hen åtminstone få besked om sin placering.</p>
                ${contact.length ? `<p class="mb-2">${contact.join(' · ')}</p>` : '<p class="mb-2 text-danger">Varken konto eller e-post — hen kan inte nås genom pistol.nu.</p>'}
                <div class="d-flex gap-2 flex-wrap"><button type="button" class="btn btn-sm btn-outline-primary" data-plb-link>Koppla till en medlem…</button>${p.weekOnly ? '' : `<button type="button" class="btn btn-sm btn-outline-secondary" data-plb-email>${p.email ? 'Ändra e-post…' : 'Lägg till e-post…'}</button>`}</div></div>`;
        } else if (contact.length) {
            h += `<p class="mb-3">${contact.join(' · ')}</p>`;
        }

        // Uppdragen
        h += `<div class="pl-card"><div class="pl-cardh"><h2>Placeringar (${p.weekOnly ? p.weekCount : asg.length})</h2></div>`;
        h += p.weekOnly ? `<p class="pl-help mb-0">${esc(first(p.displayName))} är placerad i en annan del av veckan. Placeringarna ändras i Rutnätet och syns dag för dag nedan.</p>` : asg.length ?`<div class="table-responsive"><table class="table table-sm align-middle mb-0"><thead><tr><th scope="col">Roll</th><th scope="col">När</th><th scope="col" class="d-none d-sm-table-cell">Läge</th><th scope="col" class="text-end"><span class="visually-hidden">Åtgärder</span></th></tr></thead><tbody>`
            + asg.map(a => {
                const menu = a.readOnly ? `<span class="badge text-bg-light border fw-normal">${esc(p.sourceLabel || 'Stationer-fliken')}</span>` : A.rowMenu([
                    { label: 'Ändra placeringen…', fn: () => openAssignment({ id: a.id, person: p }) },
                    { label: 'Hitta ersättare…', icon: 'bi-person-plus', fn: () => openFindReplacement(a, p) },
                    a.dateKey && { label: 'Se stationens schema…', fn: () => openScheduleFor(a) },
                    { label: 'Flytta till en annan dag…', fn: () => moveFromPerson(a) },
                    (p.memberId || p.email) && { label: 'Meddela om placeringen…', fn: () => notifyOne(a, p) },
                    { divider: true },
                    { label: 'Ta bort placeringen…', danger: true, fn: () => removeFromPerson(a, p) },
                ]);
                const st = `<span class="pl-state ${statusCls(a.status, a.notifiedAt)}">${esc(statusLabel(a.status, a.notifiedAt))}</span>`;
                return `<tr><td><b>${esc(a.roleName)}</b>${a.functionTitle ? ` <span class="plb-dim">· ${esc(a.functionTitle)}</span>` : ''}${a.isResponsible ? ' <i class="bi bi-star-fill text-warning" title="Ansvarig" aria-label="Ansvarig"></i>' : ''}
                        ${a.scopeLabel && a.scopeLabel !== 'Hela tävlingen' ? `<div class="plb-dim small">${esc(a.scopeLabel)}</div>` : ''}<div class="d-sm-none mt-1">${st}</div></td>
                    <td>${a.dayLabel ? esc(a.dayLabel) + '<br>' : ''}<span class="plb-dim small">${esc(a.shiftLabel || a.passLabel || 'Hela dagen')}</span></td>
                    <td class="d-none d-sm-table-cell">${st}</td><td class="text-end">${menu}</td></tr>`;
            }).join('') + '</tbody></table></div>'
            : '<p class="pl-help mb-0">Inga placeringar än.</p>';
        h += '</div>';

        // Personens dag för dag, egna starter och krockar — läses efter att sidan ritats (loadWeekCards).
        h += '<div id="plbWeek"></div>';

        // Behörighet — när planeringen är kopplad till en tävling, eller är en huvudplanering med delar (steg 5: gäller då alla
        // veckans tävlingar). Annars finns ingen tävling att hantera.
        if ((linked() || family()) && !ro) {
            h += `<div class="pl-card"><h2>Behörighet</h2>` + (p.memberId
                ? `<div class="form-check form-switch mb-0"><input class="form-check-input" type="checkbox" role="switch" id="plbAccess"${p.hasAdminAccess ? ' checked' : ''}>
                    <label class="form-check-label" for="plbAccess">${linked() ? 'Får hantera tävlingen i pistol.nu' : 'Får hantera alla veckans tävlingar i pistol.nu'}</label>
                    <div class="pl-help small">${linked()
                        ? 'Anmälningar, startlistor, resultat och funktionärssidorna. Gör inte personen till tävlingsledare. Gäller alla hens placeringar.'
                            + (family() ? ' Huvudplaneringen har en egen tävling: behörigheten gäller bara den, inte delarnas tävlingar.' : '')
                        : 'Anmälningar, startlistor och funktionärssidorna i <b>varje tävling i veckan</b> — men inte ekonomin och inte att ändra själva tävlingen. Ge bara till någon du litar på. Bara huvudplaneringens sammankallande kan ge den.'}</div></div>`
                : '<p class="pl-help mb-0">Behörigheten ges till en inloggning — koppla personen till en medlem först.</p>') + '</div>';
        }

        // Ställer upp för
        const reps = p.representChoices || [];
        if (!ro) {
            const repVal = p.representsId ? (p.representsType + ':' + p.representsId) : '';
            h += `<div class="pl-card"><h2>Ställer upp för</h2>` + (reps.length
                ? `<label class="visually-hidden" for="plbRep">Förening</label><select class="form-select" id="plbRep" style="max-width:420px">
                    <option value=""${p.representsIsDefault || !p.representsId ? ' selected' : ''}>${p.memberId ? 'Huvudklubben' + (p.representsIsDefault && p.representsName ? ' (' + esc(p.representsName) + ')' : '') : 'Ingen förening'}</option>
                    ${reps.map(c => { const v = c.type + ':' + c.id; return `<option value="${esc(v)}"${!p.representsIsDefault && v === repVal ? ' selected' : ''}>${esc(c.name)}${c.type === 'region' ? ' (krets)' : ''}</option>`; }).join('')}
                  </select><div class="pl-help small mt-1">Gäller alla hens placeringar.</div>`
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
        loadWeekCards(p, sub);
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
        area().querySelector('[data-plb-undo]')?.addEventListener('click', () => undoIdentity(p));
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
                <p class="pl-help small mt-1 mb-0">Gäller alla ${placeCount(p)} placeringar${family() ? ' i hela veckan' : ''}.</p>`,
            onOpen: b => { const i = b.querySelector('#plbPName'); i.focus(); i.select(); },
            onOk: async b => {
                const v = b.querySelector('#plbPName').value.trim();
                if (!v) return 'Skriv ett namn.';
                if (v === p.displayName) return true;
                const r = await identityPost({ personKey: p.key, newName: v });
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
                <p class="pl-help small mt-2 mb-0">Alla ${placeCount(p)} placeringar${family() ? ' i hela veckan' : ''} kopplas till medlemmen. Namnet blir medlemmens, och det gamla namnet går att få tillbaka.</p>`,
            onOpen: b => {
                const i = b.querySelector('#plbLinkQ'); i.focus(); i.dispatchEvent(new Event('input'));
                wireMemberSearch(b, '#plbLinkQ', '#plbLinkRes', m => { picked = m; b.querySelector('#plbLinkPicked').innerHTML = `Vald: <b>${esc(m.memberName)}</b>${m.clubName ? ' · ' + esc(m.clubName) : ''}`; });
            },
            onOk: async () => {
                if (!picked) return 'Välj en medlem i listan.';
                const r = await identityPost({ personKey: p.key, memberId: picked.memberId });
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
                <p class="pl-help small mt-1 mb-0">Med e-post kan hen få besked om sin placering. Utan konto på pistol.nu får hen inget eget schema och inga påminnelser.</p>`,
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
            title: 'Lämna över placeringarna', okLabel: 'Lämna över', wide: false,
            html: `<p class="mb-2">Om ${esc(first(p.displayName))} inte kan längre: alla ${n} placeringar går över till någon annan. Beskedet följer inte med — den nya personen står som Placerad tills du meddelar hen.</p>
                <label class="form-label" for="plbRepQ">Vem tar över?</label>
                <input type="search" class="form-control" id="plbRepQ" autocomplete="off" placeholder="Sök medlem, eller skriv ett namn">
                <div class="list-group mt-1 d-none" id="plbRepRes"></div>
                <div class="mt-2" id="plbRepPicked"></div>
                <div id="plbRepContact" class="mt-3">
                    <div class="pl-help small mb-1">Har personen inget konto? Skriv e-post och telefon, så når beskedet och tävlingsdagens lista rätt person.</div>
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
                const r = await identityPost(picked
                    ?{ personKey: p.key, memberId: picked.memberId, isReplacement: true }
                    : { personKey: p.key, newName: typed, isReplacement: true,
                        email: b.querySelector('#plbRepEmail').value.trim(), phone: b.querySelector('#plbRepPhone').value.trim() });
                if (!r.success) return r.message || 'Det gick inte att lämna över.';
                await loadPeople();
                nav('personer', picked ? 'm:' + picked.memberId : 'n:' + typed.toLowerCase());
                return true;
            }
        });
    }
    // ---- skiftschemat för en ruta (Stefan 2026-10-27) ----
    // Rollen och dagen har ett schema med vem som står när. Gränsen mellan två skift flyttas på ett ställe och följer med grannen, hålen syns som
    // röda stycken mot skjuttiden, och allt sparas i en transaktion (SaveCellShifts). Ersätter "Dela passet" på personens sida.
    const hmMin = h => { const m = /^(\d{1,2}):(\d{2})$/.exec(h || ''); return m ? (+m[1]) * 60 + (+m[2]) : null; };
    const minHm = n => String(Math.floor(n / 60)).padStart(2, '0') + ':' + String(n % 60).padStart(2, '0');
    // Samma uträkning som servern (ShiftScheduleRules.Holes): stretchar i fönstret där färre än "need" är på plats. En post utan tider är på plats hela dagen.
    function scHoles(rows, win, need) {
        if (!win || win.to <= win.from || need < 1) return [];
        const live = rows.filter(r => !r.removed);
        const timed = live.map(r => ({ a: hmMin(r.from), b: hmMin(r.to) })).filter(s => s.a != null && s.b != null && s.b > s.a);
        const allDay = live.length - live.filter(r => hmMin(r.from) != null && hmMin(r.to) != null && hmMin(r.to) > hmMin(r.from)).length;
        const cuts = new Set([win.from, win.to]);
        timed.forEach(s => { if (s.a > win.from && s.a < win.to) cuts.add(s.a); if (s.b > win.from && s.b < win.to) cuts.add(s.b); });
        const pts = [...cuts].sort((x, y) => x - y), holes = [];
        for (let i = 0; i + 1 < pts.length; i++) {
            const a = pts[i], b = pts[i + 1];
            if (allDay + timed.filter(s => s.a <= a && s.b >= b).length >= need) continue;
            if (holes.length && holes[holes.length - 1].to === a) holes[holes.length - 1].to = b; else holes.push({ from: a, to: b });
        }
        return holes;
    }
    // Lika långa skift på hela kvartar (som ShiftScheduleRules.Split); det sista tar resten.
    function scSplit(win, n) {
        const out = [], step = (win.to - win.from) / n, quarter = step >= 15;
        let cur = win.from;
        for (let i = 1; i <= n; i++) {
            let end;
            if (i === n) end = win.to;
            else {
                const raw = win.from + step * i;
                end = quarter ? Math.round(raw / 15) * 15 : Math.round(raw);
                if (end <= cur) end = cur + 1;
                if (end >= win.to) end = win.to - 1;
            }
            out.push({ from: cur, to: end }); cur = end;
        }
        return out;
    }
    async function openScheduleFor(a) {
        try { await ensureGrid(); } catch (e) { A.showMsg(esc(e.message), 'danger'); return; }
        const norm = t => (!t || String(t).toLowerCase() === 'all') ? '' : String(t).toLowerCase();
        const row = (B.grid.rows || []).find(r => r.roleKey === a.roleKey && norm(r.scopeType) === norm(a.scopeType) && (r.scopeKey || '') === (a.scopeKey || ''));
        const col = row ? colsFor(row).find(c => c.key === a.dateKey) : null;
        if (!row || !col) { A.showMsg('Rutan hittades inte i rutnätet. Öppna den därifrån.', 'warning'); return; }
        openSchedule(row, col);
    }
    function openSchedule(row, col) {
        const PAGE = 20;
        const cells = (row.cells && row.cells[col.key]) || [];
        const roleOn = () => (B.grid.wholeDayRoles || []).some(k => String(k).toLowerCase() === String(row.roleKey).toLowerCase());
        const S = {
            winFrom: col.windowFrom || '', winTo: col.windowTo || '', fromList: !!(col.windowFrom && col.windowTo),
            need: Math.max(1, needFor(row, col.key) || 0), n: 3, pick: null, clash: null,
            rows: cells.map(e => ({
                id: e.id, name: e.displayName, memberId: e.memberId || 0, from: e.from || '', to: e.to || '', status: e.status, notifiedAt: e.notifiedAt || null,
                locked: !!(e.passId > 0 || e.readOnly), passLabel: e.passId > 0 ? e.timeLabel : '', isNew: false, removed: false, swapping: false,
                orig: { from: e.from || '', to: e.to || '', memberId: e.memberId || 0 }
            })),
        };
        // En tom ruta öppnas med en färdig rad: ett namn och Spara, utan tid = hela dagen.
        const emptyStart = S.rows.length === 0;
        if (emptyStart) S.rows.push({ id: 0, name: '', memberId: 0, from: '', to: '', status: 'Planned', isNew: true, removed: false, locked: false });
        // Utan startlista: förslaget är hela spannet som skiften redan täcker — arrangören kan skriva över det.
        if (!S.fromList) {
            const t = S.rows.filter(r => hmMin(r.from) != null && hmMin(r.to) != null);
            if (t.length) { S.winFrom = minHm(Math.min(...t.map(r => hmMin(r.from)))); S.winTo = minHm(Math.max(...t.map(r => hmMin(r.to)))); }
        }
        const win = () => { const f = hmMin(S.winFrom), t = hmMin(S.winTo); return f != null && t != null && t > f ? { from: f, to: t } : null; };
        const title = `${row.roleName}${row.scopeLabel ? ' · ' + row.scopeLabel : ''} · ${col.label}`;
        let body = null, modal = null;
        // Flest samtidigt på plats, så att "2 av 3 platser" stämmer med det som står i raderna (ett skift utan tid gäller hela skjuttiden).
        const peak = () => {
            const w = win(), ev = [];
            S.rows.filter(r => !r.removed && ((r.name || '').trim() || r.memberId)).forEach(r => {
                const a = hmMin(r.from), b = hmMin(r.to);
                if (a != null && b != null && b > a) ev.push([a, 1], [b, -1]); else ev.push([w ? w.from : 0, 1], [w ? w.to : 1440, -1]);
            });
            ev.sort((x, y) => x[0] - y[0] || x[1] - y[1]);
            let cur = 0, max = 0; ev.forEach(e => { cur += e[1]; if (cur > max) max = cur; });
            return max;
        };
        const draw = () => {
            if (!body) return;
            const w = win(), holes = scHoles(S.rows.filter(r => (r.name || "").trim() || r.memberId), w, S.need), live = S.rows.filter(r => !r.removed);
            const pk = peak(), places = `<span class="plb-dim fw-normal ms-1" id="plbScPeak">· ${pk} av ${S.need} ${S.need === 1 ? 'plats' : 'platser'} bemannade samtidigt</span>`;
            const winHtml = S.fromList
                ? `<div class="small"><b>Skjuttid ${esc(S.winFrom)}–${esc(S.winTo)}</b> <span class="plb-dim">(första till sista start i startlistan)</span></div>`
                : `<div class="d-flex flex-wrap align-items-center gap-2 small"><span>Tid som ska vara bemannad <span class="plb-dim">(valfritt)</span>:</span>
                    <input type="text" class="form-control form-control-sm" style="width:5.5rem" id="plbScWF" data-hpsk-time inputmode="decimal" placeholder="från" value="${esc(S.winFrom)}" aria-label="Bemannad från">
                    <span>–</span><input type="text" class="form-control form-control-sm" style="width:5.5rem" id="plbScWT" data-hpsk-time inputmode="decimal" placeholder="till" value="${esc(S.winTo)}" aria-label="Bemannad till"></div>`;
            const wholeHtml = S.fromList ? `<div class="form-check mb-0"><input class="form-check-input" type="checkbox" id="plbScWhole"${roleOn() ? ' checked' : ''}>
                    <label class="form-check-label small" for="plbScWhole">Ska vara bemannad hela skjuttiden <span class="plb-dim">— gäller alla rader för ${esc(row.roleName)}</span></label></div>` : '';
            let bar = '';
            if (w) {
                const pct = m => ((m - w.from) / (w.to - w.from) * 100).toFixed(2);
                const segs = live.filter(r => hmMin(r.from) != null && hmMin(r.to) != null && hmMin(r.to) > hmMin(r.from)).map(r => {
                    const a = Math.max(hmMin(r.from), w.from), b = Math.min(hmMin(r.to), w.to);
                    return b > a ? `<div class="position-absolute top-0 bottom-0 border-start border-end px-1 small text-truncate" style="left:${pct(a)}%;width:${(pct(b) - pct(a)).toFixed(2)}%;background:var(--bs-success-bg-subtle);color:var(--bs-success-text-emphasis)" title="${esc(r.name || 'Ingen vald')} ${esc(r.from)}–${esc(r.to)}">${esc(first(r.name || ''))}</div>` : '';
                }).join('');
                const allDay = live.some(r => ((r.name || "").trim() || r.memberId) && (hmMin(r.from) == null || hmMin(r.to) == null));
                bar = `<div class="position-relative rounded border mb-1" id="plbScBar" style="height:1.9rem;background:${allDay ? 'var(--bs-success-bg-subtle)' : 'var(--bs-danger-bg-subtle)'}">${segs}</div>
                    <div class="d-flex justify-content-between small plb-dim mb-1"><span>${esc(minHm(w.from))}</span><span>${esc(minHm(w.to))}</span></div>`;
            }
            const holeHtml = !w ? `<div class="small mb-2" id="plbScHoles">${places}</div>` : holes.length
                ? `<div class="small text-danger fw-semibold mb-2" id="plbScHoles"><i class="bi bi-exclamation-triangle-fill me-1" aria-hidden="true"></i>${S.need > 1 ? `Färre än ${S.need} på plats` : 'Ingen'} ${holes.map(h => minHm(h.from) + '–' + minHm(h.to)).join(', ')}${places}</div>`
                : `<div class="small text-success mb-2" id="plbScHoles"><i class="bi bi-check-circle-fill me-1" aria-hidden="true"></i>Hela tiden är bemannad.${places}</div>`;
            const rowsHtml = live.length ? live.map(r => { const i = S.rows.indexOf(r); return scRowHtml(r, i); }).join('')
                : '<p class="plb-dim small mb-2" id="plbScEmpty">Inga skift än. Lägg till ett, eller dela dagen i lika långa skift.</p>';
            const allNew = !live.some(r => !r.isNew);
            const splitHtml = w && allNew ? `<span class="d-inline-flex align-items-center gap-1"><button type="button" class="btn btn-outline-secondary btn-sm" data-sc-split>Dela dagen i</button>
                    <select class="form-select form-select-sm" style="width:4.2rem" id="plbScN" aria-label="Antal skift">${[2, 3, 4, 5, 6].map(n => `<option${n === S.n ? ' selected' : ''}>${n}</option>`).join('')}</select> skift</span>` : '';
            const clashHtml = S.clash ? `<div class="alert alert-warning py-2 small" role="alert" id="plbScClash">${S.clash.map(esc).join('<br>')}
                    <div class="mt-2"><button type="button" class="btn btn-sm btn-warning" data-sc-force>Spara ändå</button> <button type="button" class="btn btn-sm btn-outline-secondary ms-1" data-sc-adj>Ändra tider</button></div></div>` : '';
            body.innerHTML = `<p class="pl-help small mb-2">Här lägger du till vem som står när. Flyttar du ett klockslag följer grannskiftet med. Ett skift utan tider gäller hela dagen. Avlösaren ser sin tid i Mitt schema.</p>
                <div class="d-flex flex-wrap align-items-center justify-content-between gap-2 mb-2">${winHtml}${wholeHtml}</div>
                ${bar}${holeHtml}
                <div class="d-flex flex-wrap align-items-center gap-2 mb-2"><button type="button" class="btn btn-outline-primary btn-sm" data-sc-add><i class="bi bi-plus-lg me-1" aria-hidden="true"></i>Lägg till skift</button>${splitHtml}</div>
                ${clashHtml}<div id="plbScRows">${rowsHtml}</div>`;
            if (S.pick) paintPick();
        };
        const scRowHtml = (r, i) => {
            const who = r.isNew || r.swapping
                ? `<input type="text" class="form-control form-control-sm plb-sc-name" data-i="${i}" autocomplete="off" placeholder="${r.swapping ? 'Sök en medlem' : 'Sök medlem eller skriv ett namn'}" value="${esc(r.isNew ? r.name : (r.swapName || ''))}" aria-label="Person">`
                : `<span class="fw-semibold">${esc(r.name)}</span> <span class="pl-state ${statusCls(r.status, r.notifiedAt)}">${esc(statusLabel(r.status, r.notifiedAt))}</span>
                    ${r.locked ? '' : `<button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-sc-swap="${i}">Byt person…</button>`}`;
            const times = r.locked
                ? `<span class="plb-dim small">${esc(r.passLabel || 'Passets tid')}</span>`
                : `<input type="text" class="form-control form-control-sm plb-sc-from" data-i="${i}" data-hpsk-time inputmode="decimal" style="width:5.2rem" placeholder="från" value="${esc(r.from)}" aria-label="Från klockan">
                    <span>–</span><input type="text" class="form-control form-control-sm plb-sc-to" data-i="${i}" data-hpsk-time inputmode="decimal" style="width:5.2rem" placeholder="till" value="${esc(r.to)}" aria-label="Till klockan">`;
            return `<div class="plb-sc-row border rounded p-2 mb-2${S.pick && S.pick.i === i ? ' border-primary' : ''}" data-i="${i}"><div class="d-flex flex-wrap align-items-center gap-2">
                <div class="flex-grow-1" style="min-width:11rem">${who}${r.warn ? `<div class="small text-warning-emphasis">${esc(r.warn)}</div>` : ''}</div>${times}
                <button type="button" class="btn btn-sm btn-outline-danger" data-sc-del="${i}" aria-label="Ta bort skiftet" title="Ta bort skiftet"><i class="bi bi-x-lg" aria-hidden="true"></i></button></div>
                <div class="plb-sc-pickbox mt-2" data-i="${i}"></div></div>`;
        };

        // ---- Personlistan under raden man fyller i: en sida i taget från servern, med filter. Namnfältet på raden är sökrutan. ----
        const bd = (cls, t) => `<span class="badge ${cls} fw-normal me-1 mt-1 text-wrap text-start">${esc(t)}</span>`;
        const cap = s => s ? s.charAt(0).toUpperCase() + s.slice(1) : s;
        const pkRow = (c, attr) => {
            const roleLower = String(row.roleName || '').toLowerCase();
            const badges = (c.volunteer ? bd('text-bg-success', c.roleMatch ? 'Anmäld som ' + roleLower : 'Anmäld som funktionär') : '')
                + (!c.volunteer && c.isShooter ? bd('text-bg-primary', 'Skytt i tävlingen') : '')
                + (c.shooterClass ? bd('border border-primary text-primary bg-transparent', 'Deltar i ' + c.shooterClass) : '')
                + (c.alreadyHere ? bd('text-bg-warning', 'Står redan här') : '')
                + (c.placements || []).map(p => bd('text-bg-warning', 'Står på ' + p)).join('')
                + (c.conflict ? bd(c.conflict.hard ? 'text-bg-danger' : 'text-bg-warning', cap(c.conflict.label)) : '');
            return `<button type="button" class="list-group-item list-group-item-action py-2" style="min-height:48px" ${attr}><span class="fw-semibold">${esc(c.name)}</span>${c.club ? ` <span class="small text-body-secondary">${esc(c.club)}</span>` : ''}${badges ? `<span class="d-block">${badges}</span>` : ''}</button>`;
        };
        const paintPickList = () => {
            const P = S.pick; if (!P || !body) return;
            const list = body.querySelector('.plb-pk-list'), count = body.querySelector('.plb-pk-count'), pg = body.querySelector('.plb-pk-pg'); if (!list) return;
            if (P.err) { list.innerHTML = `<div class="small text-danger p-2">${esc(P.err)}</div>`; return; }
            if (!P.list) { list.innerHTML = '<div class="small plb-dim p-2">Hämtar…</div>'; return; }
            const vols = P.list.map((c, k) => [c, k]).filter(x => x[0].volunteer), shs = P.list.map((c, k) => [c, k]).filter(x => !x[0].volunteer);
            let html = '';
            if (P.note) html += `<p class="small plb-dim mb-1">${esc(P.note)}</p>`;
            if (vols.length) html += `<div class="small fw-semibold mb-1">Anmälda funktionärer</div><div class="list-group mb-2">${vols.map(x => pkRow(x[0], `data-pk-pick="${x[1]}"`)).join('')}</div>`;
            if (shs.length) html += `<div class="small fw-semibold mb-1">Skyttar i tävlingen som inte anmält sig som funktionär</div><div class="list-group mb-2">${shs.map(x => pkRow(x[0], `data-pk-pick="${x[1]}"`)).join('')}</div>`;
            if (P.members.length) html += `<div class="small fw-semibold mb-1">Övriga medlemmar</div><div class="list-group mb-2">${P.members.map((m, k) => `<button type="button" class="list-group-item list-group-item-action py-2" style="min-height:48px" data-pk-mem="${k}"><span class="fw-semibold">${esc(m.memberName)}</span>${m.clubName ? ` <span class="small text-body-secondary">${esc(m.clubName)}</span>` : ''}</button>`).join('')}</div>`;
            if (!P.list.length && !P.members.length) html += P.q
                ? '<p class="small plb-dim p-2 mb-0">Ingen matchar. Skriv namnet och spara, så läggs personen in som text, utan kallelse.</p>'
                : '<p class="small plb-dim p-2 mb-0">Ingen att visa. Slå på "Skyttar i tävlingen" eller sök efter ett namn.</p>';
            list.innerHTML = html;
            list.scrollTop = 0;
            const from = P.page * PAGE, pages = Math.max(1, Math.ceil(P.total / PAGE));
            count.textContent = P.list.length ? `${from + 1}–${from + P.list.length} av ${P.total.toLocaleString('sv-SE')}${P.shooters ? '' : ' anmälda funktionärer'}` : '0 träffar';
            pg.innerHTML = `<button type="button" class="btn btn-sm btn-outline-secondary" data-pk-prev${P.page === 0 ? ' disabled' : ''}>‹ Föregående</button><span class="small">Sida ${P.page + 1} av ${pages.toLocaleString('sv-SE')}</span><button type="button" class="btn btn-sm btn-outline-secondary" data-pk-next${P.page >= pages - 1 ? ' disabled' : ''}>Nästa ›</button>`;
        };
        const paintPick = () => {
            if (!body) return;
            const P = S.pick;
            body.querySelectorAll('.plb-sc-pickbox').forEach(b => { if (!P || +b.dataset.i !== P.i) b.innerHTML = ''; });
            body.querySelectorAll('.plb-sc-row').forEach(el => el.classList.toggle('border-primary', !!P && +el.dataset.i === P.i));
            if (!P) return;
            const box = body.querySelector(`.plb-sc-pickbox[data-i="${P.i}"]`); if (!box) return;
            box.innerHTML = `<div class="border border-primary rounded p-2">
                <div class="d-flex justify-content-between align-items-center gap-2 mb-2"><span class="small fw-semibold">Välj person till skiftet ovan</span><button type="button" class="btn btn-link btn-sm p-0" data-pk-close>Stäng listan</button></div>
                <div class="form-check form-switch mb-2"><input class="form-check-input" type="checkbox" role="switch" id="plbPkSh"${P.shooters ? ' checked' : ''}><label class="form-check-label" for="plbPkSh">Skyttar i tävlingen</label></div>
                ${P.shooters ? `<div class="border rounded p-2 mb-2 bg-body-tertiary">
                    <div class="form-check"><input class="form-check-input plb-pk-f" type="checkbox" id="plbPkUn" data-f="un"${P.un ? ' checked' : ''}><label class="form-check-label small" for="plbPkUn">Bara de som inte är placerade den dagen</label></div>
                    <div class="form-check"><input class="form-check-input plb-pk-f" type="checkbox" id="plbPkFr" data-f="fr"${P.fr ? ' checked' : ''}><label class="form-check-label small" for="plbPkFr">Bara de som är lediga på skiftets tid</label></div>
                    <div class="form-check"><input class="form-check-input plb-pk-f" type="checkbox" id="plbPkVo" data-f="vo"${P.vo ? ' checked' : ''}><label class="form-check-label small" for="plbPkVo">Bara anmälda funktionärer</label></div></div>` : ''}
                <div class="plb-pk-list" style="max-height:300px;overflow:auto"></div>
                <div class="d-flex flex-wrap justify-content-between align-items-center gap-2 mt-2"><span class="small plb-dim plb-pk-count"></span><span class="d-inline-flex align-items-center gap-2 plb-pk-pg"></span></div></div>`;
            paintPickList();
        };
        let pkTimer = null, pkSeq = 0;
        const loadPick = delay => {
            clearTimeout(pkTimer);
            pkTimer = setTimeout(async () => {
                const P = S.pick; if (!P) return;
                const r = S.rows[P.i]; if (!r) return;
                const my = ++pkSeq;
                const f = normTime(r.from), t = normTime(r.to), w = win();
                const qs = { planningId: row.planningId || A.ID, roleKey: row.roleKey, date: col.key, scopeKey: row.scopeKey || '', take: PAGE, skip: P.page * PAGE,
                    onlyVolunteers: (!P.shooters || P.vo) ? 'true' : 'false', onlyUnplaced: P.shooters && P.un ? 'true' : 'false', onlyFree: P.shooters && P.fr ? 'true' : 'false' };
                if (f && t) { qs.from = f; qs.to = t; } else if (w) { qs.from = minHm(w.from); qs.to = minHm(w.to); }   // utan tid prövas mot dagens fönster, så att krockar syns direkt
                if (P.q) qs.q = P.q;
                let d = null; try { d = await sget('GetCandidates', qs); } catch (e) { d = null; }
                if (my !== pkSeq || S.pick !== P) return;
                if (!d || !d.success) { P.err = (d && d.message) || 'Kunde inte läsa listan.'; paintPickList(); return; }
                P.err = '';
                if (!P.shooters && !P.auto && d.volunteers === 0) {   // ingen har anmält sig som funktionär: visa skyttarna i stället för en tom lista
                    P.auto = true; P.shooters = true; P.note = 'Ingen har anmält sig som funktionär. Här är skyttarna i tävlingen.';
                    paintPick(); loadPick(0); return;
                }
                P.list = d.candidates || []; P.total = d.total || 0; P.members = [];
                if (P.q.length >= 2 && P.page === 0) {
                    try {
                        const m = await sget('SearchMembers', { query: P.q }); if (my !== pkSeq || S.pick !== P) return;
                        const known = new Set(P.list.map(c => c.memberId));
                        P.members = ((m && m.members) || []).filter(x => !known.has(x.memberId)).slice(0, 8);
                    } catch (e) { P.members = []; }
                }
                paintPickList();
            }, delay);
        };
        const pickOpen = (i, q) => {
            S.pick = { i, q: q || '', page: 0, shooters: false, un: false, fr: false, vo: false, list: null, total: 0, members: [], auto: false, note: '', err: '' };
            paintPick(); loadPick(0);
        };
        const pickApply = (memberId, name, freeFalse, label) => {
            const r = S.rows[S.pick.i]; if (!r) return;
            r.memberId = memberId; r.warn = freeFalse ? label : '';
            if (r.swapping) r.swapName = name; else r.name = name;
            S.pick = null; S.clash = null; draw();
        };

        // Gränsen följer med: ändras ett skifts slut flyttas det som började när det slutade, och tvärtom.
        const onTime = (i, which, raw) => {
            const r = S.rows[i]; if (!r) return;
            const v = normTime(raw);
            const old = r[which];
            r[which] = v === null ? raw : v;
            r.warn = ''; S.clash = null;
            if (v && old && v !== old) S.rows.forEach((o, k) => { if (k !== i && !o.removed && !o.locked) { if (which === 'to' && o.from === old) o.from = v; if (which === 'from' && o.to === old) o.to = v; } });
            draw();
            if (S.pick) loadPick(0);   // krockarna i listan gäller den nya tiden
        };
        // Sparar allt eller inget. Ett hårt hinder (egen start, annan placering) stoppar först och frågar; "Spara ändå" skickar force.
        const save = async force => {
            const shifts = [], deleteIds = [];
            for (const r of S.rows) {
                if (r.removed) { if (!r.isNew) deleteIds.push(r.id); continue; }
                const f = normTime(r.from), t = normTime(r.to);
                if (f === null || t === null) return 'Klockslagen går inte att läsa. Skriv till exempel 8 eller 8:30.';
                if (!r.locked && ((f && !t) || (!f && t))) return 'Skriv både från och till, eller lämna båda tomma för hela dagen.';
                if (!r.locked && f && t && hmMin(t) <= hmMin(f)) return `Sluttiden ${t} måste vara efter starttiden ${f}.`;
                if (r.isNew) {
                    if (!(r.name || '').trim() && !f && !t) continue;   // en tom rad som aldrig fylldes i
                    if (!(r.name || '').trim()) return `Välj eller skriv vem som tar skiftet ${f}–${t}, eller ta bort det.`;
                    shifts.push({ id: 0, memberId: r.memberId || 0, displayName: r.name.trim(), from: f, to: t });
                } else if (!r.locked && (f !== r.orig.from || t !== r.orig.to || (r.memberId && r.memberId !== r.orig.memberId))) {
                    if (r.swapping && !r.memberId) return 'Välj en medlem att byta till, eller avbryt bytet genom att ladda om dialogen.';
                    shifts.push({ id: r.id, memberId: r.memberId || 0, from: f, to: t });
                }
            }
            if (!shifts.length && !deleteIds.length) return true;
            const d = await spost('SaveCellShifts', { roleKey: row.roleKey, scopeType: row.scopeType || null, scopeKey: row.scopeKey || null, date: col.key, shifts, deleteIds, force: !!force }, row.planningId);
            if (!d.success) {
                if (d.clash) { S.clash = d.clashes && d.clashes.length ? d.clashes : [d.message]; draw(); return 'Någon har en krock. Välj Spara ändå eller ändra tiderna.'; }
                return d.message || 'Det gick inte att spara schemat.';
            }
            await refreshPart();
            if (d.notified) A.showMsg(`Klart. ${d.notified} ${d.notified === 1 ? 'person har' : 'personer har'} fått besked om sin nya tid.`, 'success');
            return true;
        };
        modal = A.formModal({
            title, okLabel: 'Spara schemat', wide: true,
            html: '<div id="plbSc"></div>',
            onOpen: b => {
                body = b.querySelector('#plbSc'); draw();
                if (emptyStart) {
                    if (window.matchMedia('(min-width: 768px)').matches) { const inp = body.querySelector('.plb-sc-name'); if (inp) inp.focus(); }   // på telefonen skulle tangentbordet täcka listan
                    if (!S.pick) pickOpen(0, '');
                }
                b.addEventListener('change', async e => {
                    const t = e.target;
                    if (t.classList.contains('plb-sc-from')) onTime(+t.dataset.i, 'from', t.value);
                    else if (t.classList.contains('plb-sc-to')) onTime(+t.dataset.i, 'to', t.value);
                    else if (t.id === 'plbScWF' || t.id === 'plbScWT') { const v = normTime(t.value); S[t.id === 'plbScWF' ? 'winFrom' : 'winTo'] = v === null ? t.value : v; draw(); }
                    else if (t.id === 'plbScN') S.n = +t.value;
                    else if (t.id === 'plbPkSh' && S.pick) { S.pick.shooters = t.checked; S.pick.page = 0; S.pick.note = ''; S.pick.list = null; paintPick(); loadPick(0); }
                    else if (t.classList.contains('plb-pk-f') && S.pick) { S.pick[t.dataset.f] = t.checked; S.pick.page = 0; S.pick.list = null; paintPickList(); loadPick(0); }
                    else if (t.id === 'plbScWhole') {
                        t.disabled = true;
                        try {
                            const d = await spost('SetRoleWholeDay', { roleKey: row.roleKey, on: t.checked }, row.planningId);
                            if (!d.success) { t.checked = !t.checked; A.showMsg(esc(d.message || 'Det gick inte att spara valet.'), 'danger'); }
                            else { await loadGrid(); drawGrid(); }
                        } catch (er) { t.checked = !t.checked; A.showMsg(esc(er.message), 'danger'); }
                        t.disabled = false;
                    }
                });
                b.addEventListener('input', e => {
                    const t = e.target; if (!t.classList.contains('plb-sc-name')) return;
                    const i = +t.dataset.i, r = S.rows[i]; if (!r) return;
                    if (r.swapping) r.swapName = t.value; else { r.name = t.value; r.memberId = 0; r.warn = ''; }
                    S.clash = null;
                    if (!S.pick || S.pick.i !== i) { pickOpen(i, t.value.trim()); return; }
                    S.pick.q = t.value.trim(); S.pick.page = 0; S.pick.list = null; paintPickList(); loadPick(250);
                });
                b.addEventListener('focusin', e => {
                    const t = e.target; if (!t.classList.contains('plb-sc-name')) return;
                    const i = +t.dataset.i; if (!S.pick || S.pick.i !== i) pickOpen(i, t.value.trim());
                });
                b.addEventListener('click', async e => {
                    const nm = e.target.closest('.plb-sc-name');
                    if (nm && !S.pick) { pickOpen(+nm.dataset.i, nm.value.trim()); return; }   // fältet har fokus men listan är stängd
                    const t = e.target.closest('[data-sc-add],[data-sc-del],[data-sc-swap],[data-sc-split],[data-pk-pick],[data-pk-mem],[data-pk-close],[data-pk-prev],[data-pk-next],[data-sc-force],[data-sc-adj]'); if (!t) return;
                    if (t.hasAttribute('data-sc-add')) {
                        const w = win(), ends = S.rows.filter(r => !r.removed && hmMin(r.to) != null).map(r => hmMin(r.to));
                        const from = ends.length ? Math.max(...ends) : (w ? w.from : null);
                        const to = w && from != null && w.to > from ? w.to : null;
                        S.rows.push({ id: 0, name: '', memberId: 0, from: from != null ? minHm(from) : '', to: to != null ? minHm(to) : '', status: 'Planned', isNew: true, removed: false, locked: false });
                        S.pick = null; S.clash = null; draw();
                        const inp = body.querySelectorAll('.plb-sc-name'); if (inp.length) inp[inp.length - 1].focus();
                    } else if (t.hasAttribute('data-sc-del')) {
                        const r = S.rows[+t.dataset.scDel]; if (!r) return;
                        if (r.isNew) S.rows.splice(+t.dataset.scDel, 1); else r.removed = true;
                        S.pick = null; S.clash = null; draw();
                    } else if (t.hasAttribute('data-sc-swap')) {
                        const r = S.rows[+t.dataset.scSwap]; if (!r) return;
                        r.swapping = true; S.pick = null; draw();
                        const inp = body.querySelector(`.plb-sc-name[data-i="${t.dataset.scSwap}"]`); if (inp) inp.focus(); else pickOpen(+t.dataset.scSwap, '');
                    } else if (t.hasAttribute('data-sc-split')) {
                        const w = win(); if (!w) return;
                        S.rows = S.rows.filter(r => !r.isNew);
                        scSplit(w, S.n).forEach(p => S.rows.push({ id: 0, name: '', memberId: 0, from: minHm(p.from), to: minHm(p.to), status: 'Planned', isNew: true, removed: false, locked: false }));
                        S.pick = null; S.clash = null; draw();
                        const inp = body.querySelector('.plb-sc-name'); if (inp) inp.focus();
                    } else if (t.hasAttribute('data-pk-pick')) {
                        const c = S.pick && S.pick.list ? S.pick.list[+t.dataset.pkPick] : null; if (!c) return;
                        pickApply(c.memberId, c.name, c.free === false, c.conflict ? cap(c.conflict.label) : '');
                    } else if (t.hasAttribute('data-pk-mem')) {
                        const m = S.pick ? S.pick.members[+t.dataset.pkMem] : null; if (!m) return;
                        pickApply(m.memberId, m.memberName, false, '');
                    } else if (t.hasAttribute('data-pk-close')) { S.pick = null; paintPick(); }
                    else if (t.hasAttribute('data-pk-prev') || t.hasAttribute('data-pk-next')) {
                        if (!S.pick) return;
                        S.pick.page = Math.max(0, S.pick.page + (t.hasAttribute('data-pk-next') ? 1 : -1)); S.pick.list = null; paintPickList(); loadPick(0);
                    } else if (t.hasAttribute('data-sc-adj')) { S.clash = null; draw(); }
                    else if (t.hasAttribute('data-sc-force')) {
                        t.disabled = true;
                        let res; try { res = await save(true); } catch (er) { res = er.message || 'Det gick inte att spara schemat.'; }
                        if (res === true) modal.hide(); else { S.clash = null; draw(); A.showMsg(esc(String(res)), 'danger'); }
                    }
                });
            },
            onOk: () => save(false)
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
                    <div class="pl-help small mt-2">Den som tar över får besked om placeringen. ${esc(r.target.holder)} får också besked om att hen inte ska komma.${r.target.hadAccess ? ' <b>Behörigheten i placeringen följer inte med</b> — den nya personen får den inte, så ge den på nytt om den behövs.' : ''}</div>`
                    : `<p class="mb-0">Ingen har anmält sig till den dagen ännu. Skicka en förfrågan under Hitta funktionärer, eller sök en medlem under Ändra placeringen.</p>`),
            onOk: async b => {
                const sel = b.querySelector('input[name="plbRepl"]:checked');
                if (!sel) return list.length ? 'Välj vem som tar över.' : 'Det finns ingen att lämna över till.';
                const chosen = list.find(c => String(c.memberId) === sel.value);
                if (chosen && chosen.free === false && confirmedBusy !== sel.value) {
                    confirmedBusy = sel.value;
                    return `${chosen.name} är upptagen${chosen.busyWith ? ' (' + chosen.busyWith + ')' : ''} den tiden. Tryck Lämna över igen om du vill lämna över ändå.`;
                }
                const d = await spost('ReplaceAssignmentHolder', { id: a.id, memberId: +sel.value });
                if (!d.success) return d.message || 'Placeringen kunde inte lämnas över.';
                if (after) await after(); else { await loadPeople(); drawPerson(p.key); }
                A.showMsg(`Placeringen är lämnad till <b>${esc(d.name)}</b>. ${d.notified ? 'Hen har fått besked.' : 'Hen kunde inte nås automatiskt — hör av dig.'}`, 'success');
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
        if (!await A.plConfirm('Ta bort placeringen?', p.displayName + ' tas bort från ' + a.roleName + (a.dayLabel ? ' ' + a.dayLabel : '') + '.', 'Ta bort', true)) return;
        try {
            const d = await spost('DeleteAssignment', { id: a.id });
            if (!d.success) { A.failMsg(d, 'Det gick inte att ta bort.'); return; }
            await refreshPart();
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }
    async function notifyOne(a, p) {
        if (!await A.plConfirm('Meddela ' + p.displayName + '?', 'Hen får ett mejl' + (p.memberId ? ' och en notis' : '') + ' om ' + a.roleName + (a.dayLabel ? ' ' + a.dayLabel : '') + ', med en knapp till sin tävlingsdagssida. Hen behöver inte svara.', 'Meddela')) return;
        try {
            const r = await sendNotice([a.id]);
            A.showMsg(r.text, r.ok ? (r.warn ? 'warning' : 'success') : 'danger');
            await refreshPart();
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }
    async function notifyPerson(p) {
        const list = (p.assignments || []).filter(a => !a.readOnly && asStatus(a.status) === 'Planned' && !a.notifiedAt);
        if (!list.length) { A.showMsg(esc(first(p.displayName)) + ' har redan fått besked om alla sina placeringar.', 'success'); return; }
        if (!await A.plConfirm('Meddela ' + p.displayName + '?', 'Hen får ETT mejl' + (p.memberId ? ' och en notis' : '') + ' med ' + (list.length === 1 ? 'sin placering' : 'alla sina ' + list.length + ' placeringar') + ' och en knapp till sin tävlingsdagssida. Hen behöver inte svara.', 'Meddela')) return;
        try {
            const r = await sendNotice(list.map(a => a.id));
            A.showMsg(r.text, r.ok ? (r.warn ? 'warning' : 'success') : 'danger');
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
        await refreshPart();
    }

    // ---- uppdragets dialog: person, roll, dag, tid, läge, anteckning ----
    async function openAssignment({ id, person, roleKey, dateKey }) {
        let full = null;
        try {
            await Promise.all([ensureRoles(), ensureGrid()]);
            if (id) {
                full = await fullAssignment(id);
                if (!full) { A.showMsg('Placeringen finns inte längre.', 'danger'); return; }
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
        const status = full ? asStatus(full.status) : 'Planned';
        const has = person && (person.assignments || []).length
            ? `<div class="pl-help small mb-3">${esc(first(person.displayName))} har redan: ${(person.assignments || []).map(a => esc(a.label || a.roleName)).join(' · ')}</div>` : '';
        A.formModal({
            title: full ? 'Ändra placeringen' : (person ? 'Placera ' + first(person.displayName) : 'Lägg till en funktionär'),
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
                <p class="pl-help small mt-1 mb-3">Utan tider gäller placeringen hela dagen.</p>
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
                    return (d.message || 'Personen är redan placerad här.') + ' Tryck ' + (full ? 'Spara' : 'Lägg till') + ' igen för att spara ändå.';
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
            let h = `<p class="pl-help">Personer som skrivits in för hand får varken eget schema eller notiser. Här gissar vi vem som menades — <b>du bekräftar</b>. En koppling gäller alla personens placeringar.</p>
                <p class="small mb-2">${rows.length} utan koppling · ${d.confident || 0} säkra förslag · ${d.unmatched || 0} utan träff</p>`;
            if (sure.length) h += confirmAll
                ? `<div class="pl-next"><div class="t">Koppla ${sure.length} personer?<ul class="small mb-0 mt-1">${sure.slice(0, 12).map(m => `<li>${esc(m.name)} → ${esc(m.candidates[0].name)}</li>`).join('')}${sure.length > 12 ? `<li>…och ${sure.length - 12} till</li>` : ''}</ul></div>
                    <div class="d-flex gap-2"><button type="button" class="btn btn-sm btn-primary" data-m-allyes>Ja, koppla</button><button type="button" class="btn btn-sm btn-outline-secondary" data-m-allno>Avbryt</button></div></div>`
                : `<button type="button" class="btn btn-sm btn-primary mb-3" data-m-all>Koppla alla säkra (${sure.length})</button>`;
            h += rows.map((m, i) => {
                const head = `<div class="fw-semibold">${esc(m.name)} <span class="plb-dim fw-normal small">${m.rowCount} placeringar</span>${m.confident ? ' <span class="pl-state ok">säkert förslag</span>' : ''}</div>`;
                if (m.looksLikeTwoPeople) return `<div class="border rounded p-2 mb-2">${head}<div class="small text-warning-emphasis">Ser ut som två personer i samma ruta. Dela upp dem i två placeringar först.</div></div>`;
                const dup = (m.inPlan || []).length
                    ? `<div class="small mt-1 text-warning-emphasis">Finns redan i planen som ${m.inPlan.map((x, j) => `<button type="button" class="btn btn-sm btn-link p-0 align-baseline fw-semibold" data-m-merge="${i}" data-j="${j}">${esc(x.name)}</button> <span class="plb-dim">(${x.rowCount} placeringar, ${esc(x.reason)})</span>`).join(', ')} — samma person?</div>` : '';
                if (!(m.candidates || []).length) return `<div class="border rounded p-2 mb-2">${head}${dup}<div class="small plb-dim">Ingen medlem liknar namnet. Lägg till e-post på personen, så kan hen åtminstone få besked om sin placering.</div></div>`;
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
            html: `<div class="form-check form-switch mb-2"><input class="form-check-input" type="checkbox" role="switch" id="plbMailYes"><label class="form-check-label" for="plbMailYes">Bara de som är meddelade</label></div>
                <label class="form-label" for="plbMailBox" id="plbMailSum"></label>
                <textarea class="form-control font-monospace" id="plbMailBox" rows="6" readonly style="font-size:.85rem"></textarea>
                <div class="d-flex gap-2 flex-wrap mt-2"><button type="button" class="btn btn-sm btn-primary" id="plbMailCopy"><i class="bi bi-clipboard me-1" aria-hidden="true"></i>Kopiera adresserna</button>
                    <a class="btn btn-sm btn-outline-secondary" id="plbMailTo" href="#"><i class="bi bi-envelope-open me-1" aria-hidden="true"></i>Öppna i e-postprogrammet</a></div>
                <p class="pl-help small mt-2">Klistra in adresserna som <b>hemlig kopia (BCC)</b>, så ser funktionärerna inte varandras adresser.</p>
                <div id="plbMailMissing"></div>`,
            onOpen: b => {
                const paint = () => {
                    const onlyYes = b.querySelector('#plbMailYes').checked;
                    const rows = (B.people || []).filter(x => !onlyYes || (x.assignments || []).some(a => a.status === 'Accepted' || a.status === 'Confirmed' || a.notifiedAt));
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
        area().innerHTML = `<p class="pl-help small mb-2" id="plbFindHint"><i class="bi bi-info-circle me-1" aria-hidden="true"></i>Det här är för att <b>bjuda in</b> klubbar och medlemmar att anmäla sig som funktionärer. Har du redan namnen på papper eller i ett mejl? <button type="button" class="btn btn-sm btn-outline-primary ms-1" data-plb-assignmany>Lägg in en namnlista…</button></p>
            <div class="pl-card"><h2>1. Vilka får anmäla sig?</h2>
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
                <p class="mb-0">${volunteers ? `<b>${volunteers}</b> har anmält sig${un ? ` — <span class="text-warning-emphasis fw-semibold">${un} väntar på en plats</span> <button type="button" class="btn btn-link btn-sm p-0 align-baseline" data-plb-queue>Visa dem</button>` : ' — alla har fått placeringar'}.` : '<span class="pl-help">Ingen har anmält sig än.</span>'}</p></div>`;
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

    // ================================================================ Personens vecka (på personens sida under Personer)
    // Dag för dag alla placeringar i alla delar, egna starter och avsikter, med krockar och för korta platsbyten, och lediga fönster att
    // skjuta i. Läses för sig (GetFamilyPersonWeek) efter att sidan ritats — saknas den ritas sidan ändå. Valideringsytan: placeringarna
    // ändras i tabellen på sidan och i Rutnätet; här ändras inget utom vem personen är (kopplingen gäller hela familjen).
    const WEEK_ICON = { Assignment: 'bi-person-badge', Start: 'bi-bullseye', Intent: 'bi-flag' };
    const CLASH_TEXT = { Overlap: ['text-bg-danger', 'krockar med'], TightTravel: ['text-bg-warning', 'kort tid att ta sig till/från'], Intent: ['text-bg-light border', 'tänker tävla samma dag:'] };
    // En person som bara är placerad i en annan del av familjen står inte i huvudplaneringens egen lista — hen läggs till ur veckans lista.
    function weekOnlyPerson(w) {
        return { key: w.key, displayName: w.name, memberId: w.memberId > 0 ? w.memberId : null, isExternal: !!w.isExternal,
            assignments: [], weekOnly: true, weekCount: w.assignments || 0, readOnly: false, clubName: '', state: '' };
    }
    function extraPeople() {
        if (!family() || !B.weekMap) return [];
        const have = new Set((B.people || []).map(p => p.key));
        const out = [];
        B.weekMap.forEach(w => { if (!have.has(w.key)) out.push(weekOnlyPerson(w)); });
        return out;
    }
    const allPeople = () => (B.people || []).concat(extraPeople());
    // Krock- och varningsmärkena i listan: läses efter att listan ritats, så att den aldrig väntar på dem. Saknas de ritas listan som förut.
    async function loadWeekList(seq) {
        if (!weekTab()) return;
        try {
            const d = await sget('GetFamilyWeek');
            if (seq !== B.seq || !d.success) return;
            B.weekMap = new Map((d.people || []).map(x => [x.key, x]));
            if (part() === 'personer' && !personKeyParam() && $('plbPeople')) drawPeopleKeepFilter();
        } catch (e) { /* listan klarar sig utan märkena */ }
    }
    function weekBadges(p) {
        const w = B.weekMap && B.weekMap.get(p.key);
        if (!w) return '';
        return (w.clashes ? ` <span class="badge text-bg-danger fw-normal" data-plb-clash>${w.clashes} ${w.clashes === 1 ? 'krock' : 'krockar'}</span>` : '')
            + (w.warnings ? ` <span class="badge text-bg-warning fw-normal" data-plb-warn>${w.warnings} ${w.warnings === 1 ? 'varning' : 'varningar'}</span>` : '');
    }
    // Ett uppdrag som INTE står i personens egen placeringstabell (det ligger i en annan del) kan lämnas över här; de i tabellen har knappen i sin meny.
    function weekItemHtml(i, ownIds) {
        const rid = i.kind === 'Assignment' && /^a\d+$/.test(i.sourceKey || '') ? i.sourceKey.slice(1) : '';
        const clashes = (i.clashes || []).map(c => { const t = CLASH_TEXT[c.kind] || ['text-bg-light border', c.kind]; return `<div class="small mt-1"><span class="badge ${t[0]} fw-normal">${esc(t[1])}</span> ${esc(c.with)}</div>`; }).join('');
        return `<li class="d-flex flex-wrap flex-sm-nowrap gap-2 py-2 border-bottom plbw-item" data-kind="${esc(i.kind)}"><i class="bi ${WEEK_ICON[i.kind] || 'bi-dot'} mt-1 ${i.hasClash ? 'text-danger' : 'text-body-secondary'}" aria-hidden="true"></i>
            <div class="min-w-0 flex-grow-1" style="flex-basis:12rem"><div>${esc(i.label)}${i.endsNextDay ? ' <span class="badge text-bg-light border fw-normal">fortsätter nästa dag</span>' : ''}</div>
            ${i.place ? `<div class="plb-dim small"><i class="bi bi-geo-alt me-1" aria-hidden="true"></i>${esc(i.place)}</div>` : ''}${clashes}</div>
            ${rid && !ownIds.has(+rid) ? `<button type="button" class="btn btn-sm btn-outline-secondary flex-shrink-0 align-self-start" data-repl="${rid}"><i class="bi bi-person-plus me-1" aria-hidden="true"></i>Hitta ersättare</button>` : ''}</li>`;
    }
    // Steg 6c (B12): när personen har tid att skjuta själv, per tävling i veckan.
    function shootWindowsHtml(list) {
        if (!list || !list.length) return '';
        return `<div class="pl-card" id="plbwShoot"><h2 class="h6 mb-1">Lediga fönster att skjuta i</h2>
            <p class="pl-help small mb-2">Tiden mellan placeringar och egna starter, minst en timme, kl. 07–20 — så ser du när hen hinner skjuta innan startlistan är gjord.</p>
            ${list.map(t => `<div class="mb-2"><b>${esc(t.name)}</b>${t.declared ? ' <span class="badge text-bg-success fw-normal">Tänker tävla</span>' : ''}
                ${(t.days || []).map(day => `<div class="small plbw-win"><span class="plb-dim">${esc(day.label.charAt(0).toUpperCase() + day.label.slice(1))}:</span> ${day.windows.length ? day.windows.map(w => esc(w.from + '–' + w.to)).join(', ') : '<span class="text-danger">ingen ledig tid</span>'}</div>`).join('')}</div>`).join('')}</div>`;
    }
    // Kopplingen: hela familjen när planeringen är en familj (samma person kan stå i flera delar), annars den här planeringen.
    async function identityPost(body) {
        const r = await spost(family() ? 'ApplyFamilyPersonIdentity' : 'ApplyPersonIdentity', body);
        if (r && r.success) {
            B.weekMap = null;
            if (r.skipped) A.showMsg(r.skipped + ' del' + (r.skipped === 1 ? '' : 'ar') + ' fick du inte ändra och rördes inte.', 'warning');
        }
        return r;
    }
    async function undoIdentity(p) {
        try {
            const r = await spost(family() ? 'UndoFamilyPersonIdentity' : 'UndoPersonIdentity', { personKey: p.key });
            if (!r.success) { A.failMsg(r, 'Det gick inte att ångra.'); return; }
            B.weekMap = null;
            A.showMsg('Personen har sitt gamla namn igen.');
            nav('personer');
        } catch (e) { A.showMsg(esc(e.message), 'danger'); }
    }
    async function loadWeekCards(p, sub) {
        if (!weekTab()) return;
        const tok = ++B.wTok;
        let d = null;
        if (B.pre && B.pre.key === p.key) { d = B.pre; B.pre = null; }
        else { try { d = await sget('GetFamilyPersonWeek', { personKey: p.key }); } catch (e) { d = null; } }
        if (tok !== B.wTok || !$('plbWeek')) return;
        if (!d || !d.success) { ['plbWeekTop', 'plbWeek'].forEach(i => { const x = $(i); if (x) x.remove(); }); return; }
        B.wp = d;
        A.setHead(p.displayName, sub + (d.clashes ? ` · <span class="text-danger fw-semibold">${d.clashes} ${d.clashes === 1 ? 'krock' : 'krockar'}</span>` : '')
            + (d.warnings ? ` · <span class="text-warning-emphasis fw-semibold">${d.warnings} ${d.warnings === 1 ? 'varning' : 'varningar'}</span>` : ''));
        A.setActions(personActions(p, d));
        const top = $('plbWeekTop'), m = d.match;
        if (top && !p.readOnly && d.isExternal && m && !m.looksLikeTwoPeople && ((m.candidates || []).length || (m.inPlan || []).length)) {
            top.innerHTML = `<div class="pl-next warn" id="plbwMatch"><div class="t"><b>Är det här någon du känner igen?</b> ${esc(d.name)} saknar konto. Kopplingen gäller alla placeringar${family() ? ' i hela veckan' : ''}.</div>
                <div class="d-flex flex-wrap gap-1 mt-1">${(m.candidates || []).map((c, i) => `<button type="button" class="btn btn-sm btn-outline-primary" data-cand="${i}">Koppla till ${esc(c.name)}${c.clubName ? ' · ' + esc(c.clubName) : ''}</button>`).join('')}
                ${(m.inPlan || []).map((c, i) => `<button type="button" class="btn btn-sm btn-outline-secondary" data-inplan="${i}">Samma person som ${esc(c.name)}</button>`).join('')}</div></div>`;
            const go = async (body, goKey) => {
                const r = await identityPost(Object.assign({ personKey: d.key }, body));
                if (!r.success) { A.showMsg(esc(r.message || 'Det gick inte att koppla.'), 'danger'); return; }
                A.showMsg('Klart — ' + (r.affected != null ? r.affected + (r.affected === 1 ? ' placering' : ' placeringar') + ' uppdaterade.' : 'kopplingen är gjord.'), 'success');
                await loadPeople(); nav('personer', goKey);
            };
            top.querySelectorAll('[data-cand]').forEach(b => b.addEventListener('click', () => {
                const c = (m.candidates || [])[parseInt(b.dataset.cand, 10)]; if (c) go({ memberId: c.memberId }, 'm:' + c.memberId);
            }));
            top.querySelectorAll('[data-inplan]').forEach(b => b.addEventListener('click', () => {
                const c = (m.inPlan || [])[parseInt(b.dataset.inplan, 10)]; if (!c) return;
                go(c.memberId > 0 ? { memberId: c.memberId } : { newName: c.name }, c.memberId > 0 ? 'm:' + c.memberId : 'n:' + c.name.trim().toLowerCase());
            }));
        } else if (top) top.innerHTML = '';
        const ownIds = new Set((p.assignments || []).map(a => a.id));
        const days = (d.days || []).map(day => `<div class="pl-card plbw-day" data-date="${esc(day.date)}"><h2 class="h6 mb-1">${esc(day.label.charAt(0).toUpperCase() + day.label.slice(1))}</h2>
            ${(day.carriedIn || []).map(c => `<div class="plb-dim small"><i class="bi bi-moon me-1" aria-hidden="true"></i>Från kvällen före: ${esc(c.label)}</div>`).join('')}
            <ul class="list-unstyled mb-0">${(day.items || []).map(i => weekItemHtml(i, ownIds)).join('')}</ul></div>`).join('');
        $('plbWeek').innerHTML = days
            ? `<p class="pl-help small mb-2"><i class="bi bi-calendar2-week me-1" aria-hidden="true"></i>Dag för dag${family() ? ', över alla delar' : ''}, med egna starter. Krock betyder att hen jobbar medan hen själv ska skjuta, eller har två placeringar på samma tid.</p>` + days + shootWindowsHtml(d.shootWindows)
            : shootWindowsHtml(d.shootWindows);
        $('plbWeek').querySelectorAll('[data-repl]').forEach(b => b.addEventListener('click', () =>
            openFindReplacement({ id: parseInt(b.dataset.repl, 10) }, { key: p.key }, () => nav('personer', p.key))));
    }

    // ================================================================ Klick i delen (delegerat)
    document.addEventListener('click', e => {
        if (!A || !A.isActive()) return;
        const t = e.target.closest('[data-plb-part],[data-plb-person],[data-plb-sched],[data-plb-move],[data-plb-del],[data-plb-queue],[data-plb-match],[data-plb-show],[data-plb-more],[data-plb-back],[data-plb-scope],[data-plb-unscope],[data-plb-slot],[data-plb-request],[data-plb-copylink],[data-plb-day],[data-plb-shootermode],[data-plb-proposal],[data-plb-assignmany]');
        if (!t || !$('plBody').contains(t)) return;
        e.preventDefault();
        if (t.hasAttribute('data-plb-assignmany')) openNameList();
        else if (t.hasAttribute('data-plb-proposal')) openProposal();
        else if (t.hasAttribute('data-plb-shootermode')) openShooterMode();
        else if (t.hasAttribute('data-plb-day')) { B.phoneDay = t.dataset.plbDay; drawGrid(); }
        else if (t.dataset.plbPart) { B.show = ''; nav(t.dataset.plbPart); }
        else if (t.dataset.plbPerson) { nav('personer', t.dataset.plbPerson); }
        else if (t.hasAttribute('data-plb-sched')) { const r = (B.grid.rows || [])[+t.dataset.plbSched], c = (B.grid.columns || [])[+t.dataset.col]; if (r && c) openSchedule(r, colsFor(r).find(x => x.key === c.key) || c); }
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
            else if (p === 'personer') { await loadPeople(); B.grid = null; if (pk) drawPerson(pk); else { drawPeopleKeepFilter(); loadWeekList(B.seq); } }
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
        if (document.querySelector('.modal.show') || document.querySelector('#plBody .dropdown-menu.show')) return;
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
