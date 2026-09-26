// Banor ur funktion, välj skjutplats (byt / flytta ner), flytt mellan skjutlag och
// dra-och-släpp i startlisteredigeraren. Kör: node hpsk-verify/skjutplats-verify.mjs [compId]
//
// ⚠️ SKRIVER i dev: flyttar skyttar och markerar banor, men varje ändring görs ogjord och
// slutläget jämförs mot utgångsläget (alla platser + trasiga banor). Genereringen provas INTE
// här — den numrerar om hela listan; den täcks av StartListPlacementTests.
import { chromium } from 'playwright';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const COMP = +(process.argv[2] || 2576);
let pass = 0, fail = 0;
const ok = (c, m) => { if (c) { pass++; console.log('  ok  ' + m); } else { fail++; console.log('  FEL ' + m); } };

const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1400, height: 1000 } });
const page = await ctx.newPage();
const jsErrors = [];
page.on('pageerror', e => { if (!/ckeditor-duplicated-modules/.test(e.message)) jsErrors.push(e.message); });
page.on('dialog', d => d.accept());   // confirmIfResultsExist() frågar när resultat finns

await page.goto(BASE + '/login-&-register/?tab=login', { waitUntil: 'domcontentloaded' });
await page.locator('#loginModel_Username').fill('builder.claude@pistol.nu');
await page.locator('#loginModel_Password').fill('123456');
await page.locator('#loginModel_Password').press('Enter');
await page.waitForLoadState('networkidle');

const mgmt = await page.goto(`${BASE}/competitionmanagement?competitionId=${COMP}`, { waitUntil: 'networkidle' });
ok(mgmt.status() === 200, 'tävlingsadministrationen svarar 200');

const post = (path, body) => page.evaluate(async ([path, body]) => {
    const r = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': getAntiForgeryToken() }, body: JSON.stringify(body) });
    return r.json();
}, [path, body]);
const getJson = path => page.evaluate(async p => (await fetch(p + (p.includes('?') ? '&' : '?') + '_=' + Date.now(), { cache: 'no-store' })).json(), path);

const lists = await getJson(`/umbraco/surface/PrecisionStartList/GetStartLists?competitionId=${COMP}`);
const slId = lists.startLists?.[0]?.id;
ok(!!slId, `startlista hittad (${slId})`);
const edit = () => getJson(`/umbraco/surface/PrecisionStartList/GetStartListForEditing?startListId=${slId}`);
const snap = cfg => JSON.stringify((cfg.teams || []).flatMap(t => (t.shooters || []).map(s => `${t.teamNumber}|${s.memberId}|${s.weaponClass}|${s.position}`)).sort());
const lanesOf = (cfg, team) => Object.fromEntries((cfg.teams.find(t => t.teamNumber === team).shooters || []).map(s => [s.memberId, s.position]));

const e0 = await edit();
ok(e0.success, 'redigeraren får startlistan');
const S0 = snap(e0.configuration);
const brokenBefore = e0.brokenLanes || [];
const max = e0.maxPerTeam;
ok(max > 0, `antal banor per skjutlag: ${max}`);

// Skjutlag med minst tre skyttar, och ett annat skjutlag att flytta till.
const teams = e0.configuration.teams;
const T = teams.find(t => (t.shooters || []).length >= 3);
const U = teams.find(t => t !== T);
if (!T) { console.log('Inget skjutlag med minst tre skyttar — fixturen räcker inte.'); process.exit(1); }
const tSorted = [...T.shooters].sort((a, b) => a.position - b.position);
const first = tSorted[0], last = tSorted[tSorted.length - 1];
console.log(`  (skjutlag ${T.teamNumber}: banor ${tSorted.map(s => s.position).join(',')}; flyttar ${last.name} från bana ${last.position})`);

try {
    // ── 1. Banor ur funktion ─────────────────────────────────────────────────
    const occupiedLane = first.position;
    const r1 = await post('/umbraco/surface/PrecisionStartList/SetBrokenLanes', { competitionId: COMP, lanes: [occupiedLane] });
    ok(r1.success && r1.lanes?.length === 1 && r1.lanes[0] === occupiedLane, `bana ${occupiedLane} markerad som trasig`);
    ok((r1.onBrokenLanes || []).some(o => o.name === first.name && o.lane === occupiedLane), `svaret namnger ${first.name} som står på den trasiga banan`);
    const e1 = await edit();
    ok(snap(e1.configuration) === S0, 'ingen skytt flyttades av att banan markerades');
    ok((e1.brokenLanes || []).includes(occupiedLane), 'redigeraren får den trasiga banan');

    const toBroken = await post('/umbraco/surface/PrecisionStartList/SetShooterPosition',
        { startListId: slId, memberId: last.memberId, weaponClass: last.weaponClass, sourceTeamNumber: T.teamNumber, targetTeamNumber: T.teamNumber, lane: occupiedLane });
    ok(!toBroken.success && /ur funktion/.test(toBroken.message || ''), `en trasig bana kan inte väljas (${toBroken.message})`);

    const tooHigh = await post('/umbraco/surface/PrecisionStartList/SetShooterPosition',
        { startListId: slId, memberId: last.memberId, weaponClass: last.weaponClass, sourceTeamNumber: T.teamNumber, targetTeamNumber: T.teamNumber, lane: max + 1 });
    ok(!tooHigh.success && /finns inte/.test(tooHigh.message || ''), `bana utanför skjutlaget vägras (${tooHigh.message})`);

    // ── 2. Redigeraren med en trasig bana ────────────────────────────────────
    await page.locator('#startlists-tab').click().catch(() => {});
    await page.evaluate(id => window.openStartListEditor(id), slId);
    await page.waitForSelector('#teamCardsContainer .team-card', { timeout: 15000 });
    await page.waitForTimeout(500);
    ok(await page.locator('#teamCardsContainer .shooter-on-broken').count() >= 1, 'skytten på den trasiga banan är rödmarkerad');
    ok((await page.locator('#brokenLanesBtnCount').textContent()).includes('1'), 'knappen "Banor ur funktion" visar antalet (1)');
    ok(await page.locator('#teamCardsContainer a:has-text("Välj skjutplats")').count() > 0, 'menyvalet "Välj skjutplats…" finns');
    ok(await page.locator('#teamCardsContainer .shooter-row[draggable="true"]').count() > 0, 'raderna går att dra (dator)');

    // Trasiga banor i dialogen: ta bort den (lagad) genom krysset.
    await page.locator('#brokenLanesBtn').click();
    await page.waitForSelector('#brokenLanesModal.show');
    ok((await page.locator('#brokenLanesChips').innerText()).includes(`Bana ${occupiedLane}`), 'dialogen visar den trasiga banan');
    await page.locator(`#brokenLanesChips button[data-lane="${occupiedLane}"]`).click();
    await page.waitForFunction(() => /Alla banor fungerar/.test(document.getElementById('brokenLanesChips').innerText));
    ok(true, 'banan togs bort (lagad) och dialogen säger att alla banor fungerar');
    // Lägg till via fältet och ta bort igen — båda vägarna i dialogen.
    await page.locator('#brokenLaneInput').fill(String(max));
    await page.locator('#brokenLanesModal button:has-text("Markera som trasig")').click();
    await page.waitForFunction(m => document.getElementById('brokenLanesChips').innerText.includes('Bana ' + m), max);
    ok((await edit()).brokenLanes.includes(max), `bana ${max} markerad via dialogen`);
    await page.locator(`#brokenLanesChips button[data-lane="${max}"]`).click();
    await page.waitForFunction(() => /Alla banor fungerar/.test(document.getElementById('brokenLanesChips').innerText));
    ok((await edit()).brokenLanes.length === 0, 'alla banor fungerar igen');
    await page.locator('#brokenLanesModal .btn-close').click();
    await page.waitForTimeout(400);

    // ── 3. Välj skjutplats: upptagen bana frågar, flytta ner, och tillbaka ─
    const ask = await post('/umbraco/surface/PrecisionStartList/SetShooterPosition',
        { startListId: slId, memberId: last.memberId, weaponClass: last.weaponClass, sourceTeamNumber: T.teamNumber, targetTeamNumber: T.teamNumber, lane: first.position });
    ok(!ask.success && ask.needsChoice && ask.occupantName === first.name, `upptagen bana frågar (${ask.message})`);
    ok(snap((await edit()).configuration) === S0, 'frågan ändrade ingenting');

    const shift = await post('/umbraco/surface/PrecisionStartList/SetShooterPosition',
        { startListId: slId, memberId: last.memberId, weaponClass: last.weaponClass, sourceTeamNumber: T.teamNumber, targetTeamNumber: T.teamNumber, lane: first.position, mode: 'shift' });
    ok(shift.success, `flytta ner: ${shift.message}`);
    const afterShift = lanesOf((await edit()).configuration, T.teamNumber);
    ok(afterShift[last.memberId] === first.position, `${last.name} står på bana ${first.position}`);
    ok(afterShift[first.memberId] === tSorted[1].position, `${first.name} flyttade ett steg (till bana ${tSorted[1].position})`);
    ok(new Set(Object.values(afterShift)).size === Object.values(afterShift).length, 'ingen bana delas av två skyttar');
    const back = await post('/umbraco/surface/PrecisionStartList/SetShooterPosition',
        { startListId: slId, memberId: last.memberId, weaponClass: last.weaponClass, sourceTeamNumber: T.teamNumber, targetTeamNumber: T.teamNumber, lane: last.position, mode: 'shift' });
    ok(back.success && snap((await edit()).configuration) === S0, 'flytta ner tillbaka återställer skjutlaget exakt');

    // ── 4. Byt plats, två gånger = oförändrat ──────────────────────────────
    const sw = await post('/umbraco/surface/PrecisionStartList/SetShooterPosition',
        { startListId: slId, memberId: last.memberId, weaponClass: last.weaponClass, sourceTeamNumber: T.teamNumber, targetTeamNumber: T.teamNumber, lane: first.position, mode: 'swap' });
    const afterSwap = lanesOf((await edit()).configuration, T.teamNumber);
    ok(sw.success && afterSwap[last.memberId] === first.position && afterSwap[first.memberId] === last.position, `byt plats: ${sw.message}`);
    const middle = tSorted.slice(1, -1);
    ok(middle.every(s => afterSwap[s.memberId] === s.position), 'övriga skyttar står kvar vid byte');
    await post('/umbraco/surface/PrecisionStartList/SetShooterPosition',
        { startListId: slId, memberId: last.memberId, weaponClass: last.weaponClass, sourceTeamNumber: T.teamNumber, targetTeamNumber: T.teamNumber, lane: last.position, mode: 'swap' });
    ok(snap((await edit()).configuration) === S0, 'byt tillbaka återställer');

    // ── 5. Till ett annat skjutlag ─────────────────────────────────────────
    if (U) {
        const uMembers = new Set((U.shooters || []).map(s => s.memberId));
        // 5a. Spärren: en skytt som redan står i skjutlaget kan inte flyttas dit.
        const twin = tSorted.find(s => uMembers.has(s.memberId));
        if (twin) {
            const g = await post('/umbraco/surface/PrecisionStartList/SetShooterPosition',
                { startListId: slId, memberId: twin.memberId, weaponClass: twin.weaponClass, sourceTeamNumber: T.teamNumber, targetTeamNumber: U.teamNumber, lane: max });
            ok(!g.success && /två banor/.test(g.message || ''), `${twin.name} står redan i skjutlag ${U.teamNumber} — flytten vägras (${g.message})`);
            ok(snap((await edit()).configuration) === S0, 'den vägrade flytten ändrade ingenting');
        } else console.log('  (ingen skytt står i båda skjutlagen — spärren provas bara i enhetstesterna)');

        // 5b. En skytt som inte står i målskjutlaget, till en ledig bana: luckan står kvar.
        const mover = [...tSorted].reverse().find(s => !uMembers.has(s.memberId));
        const uTaken = new Set((U.shooters || []).map(s => s.position));
        let free = 0; for (let p = 1; p <= max; p++) if (!uTaken.has(p)) { free = p; break; }
        if (mover && free) {
            const mv = await post('/umbraco/surface/PrecisionStartList/SetShooterPosition',
                { startListId: slId, memberId: mover.memberId, weaponClass: mover.weaponClass, sourceTeamNumber: T.teamNumber, targetTeamNumber: U.teamNumber, lane: free });
            const cfg = (await edit()).configuration;
            const tNow = lanesOf(cfg, T.teamNumber), uNow = lanesOf(cfg, U.teamNumber);
            ok(mv.success && uNow[mover.memberId] === free, `${mover.name} till skjutlag ${U.teamNumber} bana ${free}: ${mv.message}`);
            ok(tSorted.filter(s => s !== mover).every(s => tNow[s.memberId] === s.position), `skjutlag ${T.teamNumber}: övriga står kvar, luckan på bana ${mover.position} lämnas`);
            const mb = await post('/umbraco/surface/PrecisionStartList/SetShooterPosition',
                { startListId: slId, memberId: mover.memberId, weaponClass: mover.weaponClass, sourceTeamNumber: U.teamNumber, targetTeamNumber: T.teamNumber, lane: mover.position });
            ok(mb.success && snap((await edit()).configuration) === S0, 'tillbaka till sin bana återställer');
        } else console.log('  (ingen lämplig skytt eller ledig bana — hoppar över flytt mellan skjutlag)');
    }

    // ── 6. Dra och släpp i redigeraren: upptagen bana frågar, byt ──────────
    await page.evaluate(id => loadStartListForEditing(id), slId);
    await page.waitForTimeout(800);
    const card = page.locator(`#teamCardsContainer .team-card[data-team-number="${T.teamNumber}"]`);
    const src = card.locator(`.shooter-row[data-member-id="${last.memberId}"]`);
    const dst = card.locator(`.shooter-row[data-member-id="${first.memberId}"]`);
    await src.scrollIntoViewIfNeeded();
    await src.dragTo(dst);
    await page.waitForSelector('#setLaneModal.show', { timeout: 8000 });
    ok(await page.locator('#setLaneChoice:not(.d-none)').count() === 1, 'dra till en upptagen bana öppnar frågan Byt / Flytta ner');
    ok((await page.locator('#setLaneSwapLabel').innerText()).includes(first.name), `frågan namnger ${first.name}`);
    await page.locator('#setLaneSwapBtn').click();
    await page.waitForSelector('#setLaneModal', { state: 'hidden', timeout: 8000 });
    const afterDrag = lanesOf((await edit()).configuration, T.teamNumber);
    ok(afterDrag[last.memberId] === first.position && afterDrag[first.memberId] === last.position, 'dra-och-släpp + Byt plats bytte platserna');
    await post('/umbraco/surface/PrecisionStartList/SetShooterPosition',
        { startListId: slId, memberId: last.memberId, weaponClass: last.weaponClass, sourceTeamNumber: T.teamNumber, targetTeamNumber: T.teamNumber, lane: last.position, mode: 'swap' });
    ok(snap((await edit()).configuration) === S0, 'återställd efter dra-och-släpp');

    // ── 7. Välj skjutplats via menyvalet i dialogen, till en ledig bana ─────
    await page.evaluate(id => loadStartListForEditing(id), slId);
    await page.waitForTimeout(800);
    const takenT = new Set(tSorted.map(s => s.position));
    let freeT = 0; for (let p = 1; p <= max; p++) if (!takenT.has(p)) { freeT = p; break; }
    if (freeT) {
        const row = page.locator(`#teamCardsContainer .team-card[data-team-number="${T.teamNumber}"] .shooter-row[data-member-id="${last.memberId}"]`);
        await row.locator('[data-bs-toggle="dropdown"]').click();
        await row.locator('a:has-text("Välj skjutplats")').click();
        await page.waitForSelector('#setLaneModal.show');
        ok((await page.locator('#setLaneShooterName').innerText()) === last.name, 'dialogen visar rätt skytt');
        await page.locator('#setLaneNumber').fill(String(freeT));
        await page.locator('#setLaneSubmitBtn').click();
        await page.waitForSelector('#setLaneModal', { state: 'hidden', timeout: 8000 });
        ok(lanesOf((await edit()).configuration, T.teamNumber)[last.memberId] === freeT, `dialogen flyttade ${last.name} till ledig bana ${freeT}`);
        await page.evaluate(id => loadStartListForEditing(id), slId);
        await page.waitForTimeout(800);
        ok(await page.locator(`#teamCardsContainer .team-card[data-team-number="${T.teamNumber}"] .lane-empty[data-lane="${last.position}"]`).count() === 1,
            `luckan på bana ${last.position} visas som ledig rad`);
        await post('/umbraco/surface/PrecisionStartList/SetShooterPosition',
            { startListId: slId, memberId: last.memberId, weaponClass: last.weaponClass, sourceTeamNumber: T.teamNumber, targetTeamNumber: T.teamNumber, lane: last.position });
        ok(snap((await edit()).configuration) === S0, 'återställd efter dialogen');
    } else console.log('  (ingen ledig bana i skjutlaget — hoppar över dialogen till ledig bana)');

    ok(jsErrors.length === 0, 'inga JS-fel' + (jsErrors.length ? ': ' + jsErrors.join(' | ') : ''));
} finally {
    await post('/umbraco/surface/PrecisionStartList/SetBrokenLanes', { competitionId: COMP, lanes: brokenBefore });
    const eEnd = await edit();
    ok(snap(eEnd.configuration) === S0, 'SLUTKONTROLL: alla skyttar står på sina ursprungliga banor');
    ok(JSON.stringify(eEnd.brokenLanes) === JSON.stringify(brokenBefore), 'SLUTKONTROLL: trasiga banor som före');
    await browser.close();
    console.log(`\n${pass}/${pass + fail}`);
    process.exit(fail ? 1 : 0);
}
