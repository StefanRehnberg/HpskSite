// course-verify.mjs — fas D: kursen (träningsgruppen med kopplade träningar), Min kurs,
// närvaro, serier, anteckningar, anmälningsrätt via kursen och tränare utan serierätt.
//
// KÖR:  HPSK_USER="admin.claude@pistol.nu" HPSK_PASS="123456" node hpsk-verify/course-verify.mjs [--shots]
//
// FIXTUR (allt heter "ZZMK", tas bort i finally och kontrolleras i SQL):
//   Grupp A, klubb 2604: tränare builder.claude (5513, klubbadmin) och outsider.claude (5515, ingen
//     serierätt); deltagare collaborator.claude (5514) och chairman.claude (5601). Två träningar:
//     i dag, och en vecka sedan (obligatorisk för kursen, ingen närvaro → "Missad").
//   Grupp B, klubb 2610: deltagare outsider.claude, som INTE är medlem i 2610 — kursen ska ge
//     anmälningsrätt till B:s träning ändå.
import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const SHOTS = process.argv.includes('--shots');
const P = 'ZZMK';
let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);
const sql = q => execFileSync('sqlcmd', ['-S', 'localhost\\SQLEXPRESS', '-d', 'Umbraco', '-E', '-C', '-b', '-W', '-h', '-1', '-s', '|', '-Q',
  'SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; ' + q], { encoding: 'utf8' }).trim();
const n = q => parseInt(sql(q) || '0', 10);
const ymd = k => { const d = new Date(); d.setDate(d.getDate() + k); return d.toISOString().slice(0, 10); };

async function login(browser, user) {
  const ctx = await browser.newContext({ viewport: { width: 390, height: 844 } });
  const page = await ctx.newPage();
  await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="loginModel.Username"]', user);
  await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS || '123456');
  await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
  await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
  await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded', timeout: 120000 });
  return page;
}
const api = page => ({
  get: u => page.evaluate(async u => { const t = await (await fetch(u, { credentials: 'same-origin' })).text(); try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 150) }; } }, u),
  post: (u, b) => page.evaluate(async ([u, b]) => {
    const r = await fetch(u, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json',
      'RequestVerificationToken': document.querySelector('input[name="__RequestVerificationToken"]')?.value || '' }, body: JSON.stringify(b) });
    const t = await r.text(); try { return JSON.parse(t); } catch { return { success: false, _status: r.status, _raw: t.slice(0, 150) }; }
  }, [u, b]),
});

const insertGroup = (name, club) => n(`INSERT INTO dbo.TrainingGroups (Name, ClubId, StartDate, IsActive, CreatedDate, CreatedByMemberId)
  VALUES ('${name}', ${club}, GETDATE(), 1, GETDATE(), 8315); SELECT CAST(SCOPE_IDENTITY() AS int);`);
const addMember = (g, m, role) => sql(`INSERT INTO dbo.TrainingGroupMembers (TrainingGroupId, MemberId, Role, JoinedDate, IsActive)
  VALUES (${g}, ${m}, '${role}', GETDATE(), 1);`);
const insertTraining = (club, name, day, mandatory = 0) => n(`INSERT INTO dbo.ClubTraining (ClubId,[Date],StartTime,EndTime,Name,IsCancelled,RegistrationRequired,LoanWeaponsOffered,IsMandatory,CreatedByMemberId,CreatedDate,UpdatedDate)
  VALUES (${club}, '${day}', '18:00', '20:00', '${name}', 0, 0, 0, ${mandatory}, 8315, GETDATE(), GETDATE()); SELECT CAST(SCOPE_IDENTITY() AS int);`);

const main = async () => {
  const browser = await chromium.launch();
  let gA = 0, gB = 0, tToday = 0, tPast = 0, tB = 0, loanWeapons = [];
  try {
    // ── Fixtur ───────────────────────────────────────────────────────────────────────────────
    section('Fixtur');
    gA = insertGroup(`${P} Kurs A`, 2604); gB = insertGroup(`${P} Kurs B`, 2610);
    addMember(gA, 5513, 'Trainer'); addMember(gA, 5515, 'Trainer'); addMember(gA, 5514, 'Member'); addMember(gA, 5601, 'Member');
    tToday = insertTraining(2604, `${P} Kvällsträning`, ymd(0));
    tPast = insertTraining(2604, `${P} Teori`, ymd(-7));
    tB = insertTraining(2610, `${P} Kungsbacka`, ymd(3));
    ok('grupper och träningar skapade', gA > 0 && gB > 0 && tToday > 0 && tPast > 0 && tB > 0);

    const admin = await login(browser, process.env.HPSK_USER || 'admin.claude@pistol.nu');
    if (await admin.locator('#firearms-member-tab').count() === 0) { console.error('AVBRYTER: inte inloggad.'); process.exitCode = 1; return; }
    const A = api(admin);

    // ── Kopplingar och krav ──────────────────────────────────────────────────────────────────
    section('Kopplingar och kursens krav');
    const lk = await A.post('/umbraco/surface/TrainingCourse/Link', { groupId: gA, trainingIds: [tToday, tPast, tB] });
    eq('två träningar kopplas — en i en annan klubb vägras', lk.linked, 2);
    const cand = await A.get(`/umbraco/surface/TrainingCourse/Candidates?groupId=${gA}`);
    ok('kandidatlistan märker dem som kopplade', (cand.trainings || []).filter(t => t.linked && [tToday, tPast].includes(t.id)).length === 2);
    const sl = await A.post('/umbraco/surface/TrainingCourse/SetLink', { groupId: gA, trainingId: tPast, attendance: 'Mandatory', note: 'Teori och prov' });
    ok('teoritillfället görs obligatoriskt för kursen', sl.success, sl.message);
    eq('träningens EGEN obligatorisk-flagga rörs inte', n(`SELECT CAST(IsMandatory AS int) FROM dbo.ClubTraining WHERE Id=${tPast}`), 0);
    // Kursen B kräver anmälan — träningen själv gör det inte. Kursens krav ska öppna anmälan för deltagaren.
    ok('kurs B kräver anmälan', (await A.post('/umbraco/surface/TrainingCourse/SetDefaults', { groupId: gB, attendance: 'Optional', registration: 'Required' })).success);
    eq('B:s träning kopplas till B', (await A.post('/umbraco/surface/TrainingCourse/Link', { groupId: gB, trainingIds: [tB] })).linked, 1);

    const c1 = (await A.get(`/umbraco/surface/TrainingCourse/Get?groupId=${gA}`)).course;
    eq('kursen har två tillfällen', c1?.occasions?.length, 2);
    eq('två deltagare (tränarna räknas inte)', c1?.participants?.map(p => p.memberId).sort(), [5514, 5601]);
    eq('två tränare', c1?.trainers?.length, 2);
    const past = c1?.occasions?.find(o => o.trainingId === tPast);
    eq('teorin är obligatorisk för kursen', past?.isMandatory, true);
    eq('och bär sin anteckning', past?.note, 'Teori och prov');
    eq('dagens tillfälle är i dag', c1?.occasions?.find(o => o.trainingId === tToday)?.isToday, true);
    eq('en obligatorisk dag utan närvaro är "Missad"', c1?.cells?.[`5514:${tPast}`]?.missed, true);
    eq('kontrollprov: dagens tillfälle är inte missat', c1?.cells?.[`5514:${tToday}`]?.missed, false);

    // ── Min kurs: närvaro ────────────────────────────────────────────────────────────────────
    section('Min kurs — närvaro');
    await admin.goto(`${BASE}/min-kurs?g=${gA}`, { waitUntil: 'domcontentloaded' });
    await admin.evaluate(() => document.getElementById('cookieConsentBanner')?.remove());
    await admin.waitForSelector('[data-mk-person="5514"]', { timeout: 30000 }).catch(() => {});
    ok('deltagarkortet renderas', await admin.locator('[data-mk-person="5514"]').count() === 1);
    // ⚠️ Etiketten har text-uppercase, och innerText följer den ("I DAG").
    ok('dagens tillfälle står överst', /i dag/i.test(await admin.locator('.mk-today').innerText().catch(() => '')));
    ok('missad obligatorisk dag syns på kortet', (await admin.locator('[data-mk-person="5514"] .mk-missed').innerText().catch(() => '')).includes('Missade obligatoriskt'));
    await admin.click('[data-mk-person="5514"] [data-status="Present"]');
    await admin.waitForFunction(() => document.querySelector('[data-mk-person="5514"] [data-status="Present"]')?.classList.contains('btn-success'), null, { timeout: 15000 }).catch(() => {});
    eq('Här sparas som närvaro på träningen', sql(`SELECT AttendanceStatus FROM dbo.ClubEventParticipant WHERE OccasionKind='Training' AND EventId=${tToday} AND MemberId=5514`), 'Present');
    ok('knappen visar läget', await admin.locator('[data-mk-person="5514"] [data-status="Present"].btn-success').count() === 1);
    eq('rubriken räknar "1 av 2 här"', /1 av 2 här/.test(await admin.locator('.mk-today').innerText()), true);

    // ── Serier ───────────────────────────────────────────────────────────────────────────────
    section('Serier');
    await admin.click('[data-mk-person="5514"] [data-mk="series"]');
    await admin.waitForSelector('#mkSeriesModal.show', { timeout: 10000 });
    ok('kraven för deltagaren står i dialogen', /krav brons \d+, silver \d+, guld \d+/.test(await admin.locator('#mkSeriesReq').innerText()));
    for (const s of ['10', '10', '9', '9', '9']) await admin.click(`#mkPad [data-shot="${s}"]`);
    ok('summan och valören visas före sparning', /47 p/.test(await admin.locator('#mkSeriesSum').innerText()), await admin.locator('#mkSeriesSum').innerText());
    if (SHOTS) await admin.screenshot({ path: 'course-series.png' });
    await admin.click('#mkSeriesSave');
    await admin.waitForFunction(() => /Serie 1/.test(document.getElementById('mkSeriesList').innerText), null, { timeout: 15000 }).catch(() => {});
    ok('serien står under Dagens serier', /Serie 1/.test(await admin.locator('#mkSeriesList').innerText()));
    const s1 = sql(`SELECT CONCAT(Total,'|',ISNULL(Valor,'-'),'|',CASE WHEN MarkenSeriesId IS NULL THEN 0 ELSE 1 END) FROM dbo.TrainingCourseSeries WHERE TrainingGroupId=${gA} AND MemberId=5514 AND SeriesNumber=1`);
    ok('kursraden: 47 p, en valör, och en märkesserie', /^47\|(Guld|Silver|Brons)\|1$/.test(s1), s1);
    eq('märkesserien är godkänd, i vapengrupp C', sql(`SELECT CONCAT(Status,'|',WeaponGroup,'|',Total) FROM dbo.MarkenSeries WHERE Id=(SELECT MarkenSeriesId FROM dbo.TrainingCourseSeries WHERE TrainingGroupId=${gA} AND MemberId=5514 AND SeriesNumber=1)`), 'Verified|C|47');
    eq('träningsloggen fick en rad med serien', n(`SELECT TotalScore FROM dbo.TrainingScores WHERE Id=(SELECT TrainingScoreId FROM dbo.TrainingCourseSeries WHERE TrainingGroupId=${gA} AND MemberId=5514 AND SeriesNumber=1)`), 47);

    await admin.click('label[for="mkModeTotal"]');
    await admin.fill('#mkTotal', '30');
    await admin.click('#mkSeriesSave');
    await admin.waitForFunction(() => /Serie 2/.test(document.getElementById('mkSeriesList').innerText), null, { timeout: 15000 }).catch(() => {});
    const s2 = sql(`SELECT CONCAT(Total,'|',ISNULL(Valor,'-'),'|',CASE WHEN MarkenSeriesId IS NULL THEN 0 ELSE 1 END,'|',CAST(ShotByShot AS int)) FROM dbo.TrainingCourseSeries WHERE TrainingGroupId=${gA} AND MemberId=5514 AND SeriesNumber=2`);
    eq('en serie under brons: ingen valör, ingen märkesserie, bara total', s2, '30|-|0|0');
    eq('båda serierna ligger i SAMMA loggrad', n(`SELECT COUNT(DISTINCT TrainingScoreId) FROM dbo.TrainingCourseSeries WHERE TrainingGroupId=${gA} AND MemberId=5514`), 1);
    eq('loggradens summa är 77', n(`SELECT TOP 1 ts.TotalScore FROM dbo.TrainingScores ts JOIN dbo.TrainingCourseSeries s ON s.TrainingScoreId=ts.Id WHERE s.TrainingGroupId=${gA} AND s.MemberId=5514`), 77);

    // Aktiviteten: loggraden är funktionärsregistrerad.
    const sum = await A.get(`/umbraco/surface/Foreningsintyg/GetActivitySummary?memberId=5514&year=${new Date().getFullYear()}`);
    const scoreId = n(`SELECT TOP 1 TrainingScoreId FROM dbo.TrainingCourseSeries WHERE TrainingGroupId=${gA} AND MemberId=5514`);
    const logEntry = (sum.data?.entries || []).find(e => e.sourceKind === 'training' && e.sourceId === scoreId);
    eq('aktiviteten märker kursens loggrad som funktionärsregistrerad', logEntry?.evidence, 'FunctionaryRecorded');

    // Ta bort serie 1: märkesserien försvinner, loggen numreras om.
    const mId = n(`SELECT MarkenSeriesId FROM dbo.TrainingCourseSeries WHERE TrainingGroupId=${gA} AND MemberId=5514 AND SeriesNumber=1`);
    const sId = n(`SELECT Id FROM dbo.TrainingCourseSeries WHERE TrainingGroupId=${gA} AND MemberId=5514 AND SeriesNumber=1`);
    const del = await A.post('/umbraco/surface/TrainingCourse/DeleteSeries', { groupId: gA, seriesId: sId });
    ok('serien går att ta bort', del.success, del.message);
    eq('märkesserien är borta', n(`SELECT COUNT(*) FROM dbo.MarkenSeries WHERE Id=${mId}`), 0);
    eq('den kvarvarande serien är nu serie 1', sql(`SELECT CONCAT(SeriesNumber,'|',Total) FROM dbo.TrainingCourseSeries WHERE TrainingGroupId=${gA} AND MemberId=5514`), '1|30');
    eq('loggradens summa är 30', n(`SELECT TotalScore FROM dbo.TrainingScores WHERE Id=${scoreId}`), 30);
    await admin.evaluate(() => bootstrap.Modal.getInstance(document.getElementById('mkSeriesModal'))?.hide());

    // ── Anteckning ───────────────────────────────────────────────────────────────────────────
    section('Anteckning');
    const nt = await A.post('/umbraco/surface/TrainingCourse/SaveNote', { groupId: gA, trainingId: tPast, memberId: 5601, note: 'Jobba med avtryckningen.' });
    ok('anteckningen sparas', nt.success, nt.message);
    eq('en anteckning om någon utanför kursen vägras', (await A.post('/umbraco/surface/TrainingCourse/SaveNote', { groupId: gA, trainingId: tPast, memberId: 5515, note: 'x' })).success, false);
    await admin.goto(`${BASE}/min-kurs?g=${gA}`, { waitUntil: 'domcontentloaded' });
    await admin.waitForSelector('[data-mk-person="5601"]', { timeout: 30000 }).catch(() => {});
    ok('dagens vy visar anteckningen från förra tillfället', (await admin.locator('#mkBody').innerText()).includes('Jobba med avtryckningen.'));
    ok('översikten markerar den obligatoriska dagen med ★', (await admin.locator('.mk-grid thead').innerText()).includes('★'));
    if (SHOTS) await admin.screenshot({ path: 'course-minkurs.png', fullPage: true });

    // ── Tränare utan serierätt ───────────────────────────────────────────────────────────────
    section('Tränare utan serierätt');
    const out = await login(browser, 'outsider.claude@pistol.nu');
    const O = api(out);
    const sr = await O.get(`/umbraco/surface/TrainingCourse/Series?groupId=${gA}&trainingId=${tToday}`);
    eq('tränaren får läsa kursens serier', sr.success, true);
    eq('men får inte registrera', sr.canRecord, false);
    ok('och får veta varför', /skjutledare/.test(sr.cannotRecordReason || ''));
    eq('servern vägrar en registrering', (await O.post('/umbraco/surface/TrainingCourse/RecordSeries', { groupId: gA, trainingId: tToday, memberId: 5514, total: 40 })).success, false);
    const att = await O.post('/umbraco/surface/ClubEvent/SetAttendance', { eventId: tToday, kind: 'Training', memberId: 5601, status: 'Present' });
    ok('men tränaren håller upprop på kursens träning', att.success, att.message);

    // ── Lånevapen till tillfället (D7) ───────────────────────────────────────────────────────
    // Klubb 2604 har inga AKTIVA lånebara vapen i dev, så två aktiveras tillfälligt och återställs
    // i finally. Utan dem vägras varje platsbokning av kapacitetsspärren.
    section('Lånevapen till tillfället');
    loanWeapons = sql(`SELECT TOP 2 Id FROM dbo.Firearm WHERE ScopeKind='Club' AND ScopeId=2604 AND IsLoanable=1 AND IsActive=0 ORDER BY Id`).split(/\s+/).filter(Boolean).map(Number);
    if (loanWeapons.length) sql(`UPDATE dbo.Firearm SET IsActive=1 WHERE Id IN (${loanWeapons.join(',')})`);
    const lw0 = await O.get(`/umbraco/surface/TrainingCourse/LoanWeapons?groupId=${gA}&trainingId=${tToday}`);
    ok('kursens tränare (inte klubbadmin) får läsa tillfällets lånevapen', lw0.success, lw0.message);
    eq('ingen deltagare har en plats än', (lw0.participants || []).filter(p => p.booked).length, 0);
    const asg = await O.post('/umbraco/surface/TrainingCourse/AssignLoanWeapons', { groupId: gA, trainingId: tToday, memberIds: [5514, 5601, 5513] });
    eq('två deltagare får en plats', asg.created, 2);
    ok('en tränare (inte deltagare) vägras per rad', (asg.results || []).some(r => r.memberId === 5513 && !r.ok && /deltagare/.test(r.message || '')));
    // ⚠️ Kärnan i fixen: en träningsbokning måste bära träningens id, annars hittar träningens
    // lånelista och borttagningsspärren den aldrig.
    eq('bokningarna bär träningens id', n(`SELECT COUNT(*) FROM dbo.FirearmBooking WHERE OccasionKind='Training' AND OccasionId=${tToday}`), 2);
    eq('källan är Tilldelad', n(`SELECT COUNT(*) FROM dbo.FirearmBooking WHERE OccasionKind='Training' AND OccasionId=${tToday} AND Source='Tilldelad'`), 2);
    const lw1 = await O.get(`/umbraco/surface/TrainingCourse/LoanWeapons?groupId=${gA}&trainingId=${tToday}`);
    eq('listan visar båda som bokade', (lw1.participants || []).filter(p => p.booked).length, 2);
    ok('en träning som inte är kopplad till kursen vägras', !(await O.post('/umbraco/surface/TrainingCourse/AssignLoanWeapons', { groupId: gA, trainingId: tB, memberIds: [5514] })).success);
    const delT = await A.post('/umbraco/surface/ClubTraining/Delete', { trainingId: tToday });
    ok('borttagningsspärren räknar lånevapenbokningarna', delT.success !== true && /2 lånevapenbokningar/.test(delT.message || ''), delT.message);
    await admin.goto(`${BASE}/min-kurs?g=${gA}`, { waitUntil: 'domcontentloaded' });
    await admin.waitForSelector('.mk-person', { timeout: 30000 }).catch(() => {});
    ok('menyvalet Lånevapen syns i Min kurs', await admin.locator('#mkLoanItem:not(.d-none)').count() === 1);
    await admin.evaluate(() => document.getElementById('cookieConsentBanner')?.remove());
    await admin.click('#mkActions .dropdown-toggle');
    await admin.click('[data-mk="loan"]');
    await admin.waitForFunction(() => /har redan en plats/.test(document.getElementById('mkLoanList')?.innerText || ''), null, { timeout: 15000 }).catch(() => {});
    ok('dialogen säger att båda redan har en plats', /2 av 2 har redan en plats/.test(await admin.locator('#mkLoanList').innerText()));
    ok('och Boka-knappen är avstängd när inget återstår', await admin.locator('#mkLoanSave').isDisabled());
    await admin.keyboard.press('Escape');

    // ── Anmälningsrätt via kursen ────────────────────────────────────────────────────────────
    section('Anmälningsrätt via kursen');
    const before = await O.get(`/umbraco/surface/ClubEvent/GetSignupState?eventId=${tB}&kind=training`);
    eq('outsider (inte medlem i 2610) får inte anmäla sig utan kursen', before.eligible, false);
    addMember(gB, 5515, 'Member');
    const after = await O.get(`/umbraco/surface/ClubEvent/GetSignupState?eventId=${tB}&kind=training`);
    eq('som kursdeltagare får hen anmäla sig', after.eligible, true);
    const su = await O.post('/umbraco/surface/ClubEvent/SignUp', { eventId: tB, kind: 'Training' });
    ok('och anmälan går igenom', su.success, su.message);
    await out.context().close();
  } finally {
    const ids = [tToday, tPast, tB].filter(Boolean).join(',') || '0';
    const groups = [gA, gB].filter(Boolean).join(',') || '0';
    if (loanWeapons.length) sql(`UPDATE dbo.Firearm SET IsActive=0 WHERE Id IN (${loanWeapons.join(",")})`);
    sql(`DELETE FROM dbo.FirearmBooking WHERE OccasionKind='Training' AND OccasionId IN (${ids});`);
    sql(`DELETE FROM dbo.MarkenSeries WHERE Id IN (SELECT MarkenSeriesId FROM dbo.TrainingCourseSeries WHERE TrainingGroupId IN (${groups}) AND MarkenSeriesId IS NOT NULL);
         DELETE FROM dbo.TrainingScores WHERE Id IN (SELECT TrainingScoreId FROM dbo.TrainingCourseSeries WHERE TrainingGroupId IN (${groups}));
         DELETE FROM dbo.TrainingCourseSeries WHERE TrainingGroupId IN (${groups});
         DELETE FROM dbo.TrainingCourseNote WHERE TrainingGroupId IN (${groups});
         DELETE FROM dbo.ClubEventParticipant WHERE OccasionKind='Training' AND EventId IN (${ids});
         DELETE FROM dbo.TrainingGroupTraining WHERE TrainingGroupId IN (${groups});
         DELETE FROM dbo.TrainingGroupMembers WHERE TrainingGroupId IN (${groups});
         DELETE FROM dbo.TrainingGroups WHERE Id IN (${groups});
         DELETE FROM dbo.ClubTraining WHERE Name LIKE '${P}%';`);
    const left = n(`SELECT (SELECT COUNT(*) FROM dbo.TrainingGroups WHERE Name LIKE '${P}%') + (SELECT COUNT(*) FROM dbo.ClubTraining WHERE Name LIKE '${P}%')
                   + (SELECT COUNT(*) FROM dbo.MarkenSeries WHERE Notes LIKE 'Kursserie: ${P}%') + (SELECT COUNT(*) FROM dbo.TrainingScores WHERE Notes LIKE 'Kurs: ${P}%')`);
    console.log(left === 0 ? '\n(städat)' : `\n⚠️ ${left} fixturrester kvar`);
    await browser.close();
  }
  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log('  - ' + f)); process.exitCode = 1; }
};
main().catch(e => { console.error(e); process.exitCode = 1; });
