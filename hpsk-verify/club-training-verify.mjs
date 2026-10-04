// club-training-verify.mjs — fas B4: klubbens träningar (lista, ny, schema, skjutledare, inställd).
//
// KÖR:  HPSK_USER="admin.claude@pistol.nu" HPSK_PASS="123456" node hpsk-verify/club-training-verify.mjs
//
// Fixturen heter "ZZCT …" och tas bort i finally (träningar, schemat, deltagarrader), kontrollerat
// i SQL. Inga Umbraco-noder skapas — träningar är SQL-rader.
import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const CLUB_ID = 2604;           // Haaplinge GoAss
const MEMBER_A = 5513, MEMBER_B = 5514;   // builder.claude, collaborator.claude — båda i 2604
const NON_MEMBER = 1085;        // Falkenbergs PK, inte med i 2604 (1083 är med i båda)
const PREFIX = 'ZZCT ';

let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);
const sql = q => execFileSync('sqlcmd',
  ['-S', 'localhost\\SQLEXPRESS', '-d', 'Umbraco', '-E', '-C', '-b', '-W', '-h', '-1', '-s', '|', '-Q',
   'SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; ' + q], { encoding: 'utf8' }).trim();
const n = q => parseInt(sql(q) || '0', 10);
const ymd = d => d.toISOString().slice(0, 10);
const plus = k => { const d = new Date(); d.setDate(d.getDate() + k); return d; };
// Nästa måndag minst 20 dagar fram, så schemat ligger i listans standardfönster.
const monday = (() => { const d = plus(20); while (d.getDay() !== 1) d.setDate(d.getDate() + 1); return d; })();

async function login(ctx, user, pass) {
  const page = await ctx.newPage();
  await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="loginModel.Username"]', user);
  await page.fill('input[name="loginModel.Password"]', pass);
  await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
  await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
  await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded', timeout: 120000 });
  return page;
}
const helpers = page => ({
  get: u => page.evaluate(async u => { const t = await (await fetch(u, { credentials: 'same-origin' })).text(); try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 150) }; } }, u),
  post: (u, b) => page.evaluate(async ([u, b]) => {
    const r = await fetch(u, { method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': document.querySelector('input[name="__RequestVerificationToken"]')?.value || '' },
      body: JSON.stringify(b) });
    const t = await r.text(); try { return JSON.parse(t); } catch { return { success: false, _status: r.status, _raw: t.slice(0, 150) }; }
  }, [u, b]),
});

const main = async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
    const page = await login(ctx, process.env.HPSK_USER || '', process.env.HPSK_PASS || '');
    if (await page.locator('#firearms-member-tab').count() === 0) {
      console.error('AVBRYTER: inte inloggad.'); process.exitCode = 1; return;
    }
    const { get, post } = helpers(page);

    // ── Ett tillfälle ────────────────────────────────────────────────────────────────────────
    section('Ny träning');
    const base = { clubId: CLUB_ID, name: PREFIX + 'Enstaka', date: ymd(plus(21)), startTime: '18:00', endTime: '20:00',
                   discipline: 'Precision', venue: 'Hålan', skjutledareMemberId: MEMBER_A,
                   registrationRequired: '1', maxParticipants: 8, loanWeaponsOffered: '1', isMandatory: '0' };
    eq('namn krävs', (await post('/umbraco/surface/ClubTraining/Save', { ...base, name: ' ' })).success, false);
    eq('ett oläsligt klockslag vägras', (await post('/umbraco/surface/ClubTraining/Save', { ...base, startTime: '25:00' })).success, false);
    eq('slut före start vägras', (await post('/umbraco/surface/ClubTraining/Save', { ...base, endTime: '17:00' })).success, false);
    const s1 = await post('/umbraco/surface/ClubTraining/Save', base);
    ok('träningen sparas', s1.success && s1.id > 0, s1.message);
    const one = s1.id;

    let list = await get(`/umbraco/surface/ClubTraining/List?clubId=${CLUB_ID}`);
    const r1 = (list.trainings || []).find(t => t.id === one);
    ok('den står i listan', !!r1);
    eq('med gren och skjutledare', [r1?.disciplineLabel, r1?.skjutledareMemberId], ['Precision', MEMBER_A]);
    eq('med tid och tak', [r1?.startTime, r1?.endTime, r1?.maxParticipants], ['18:00', '20:00', 8]);
    eq('lånevapen och anmälan', [r1?.loanWeaponsOffered, r1?.registrationRequired], [true, true]);
    eq('listan säger att jag får ändra', list.canEdit, true);

    // Ändra: "1"/"0" måste gå åt båda hållen.
    const s1b = await post('/umbraco/surface/ClubTraining/Save', { ...base, id: one, loanWeaponsOffered: '0', name: PREFIX + 'Enstaka ändrad' });
    ok('ändringen sparas', s1b.success, s1b.message);
    eq('lånevapen gick att stänga av', n(`SELECT CAST(LoanWeaponsOffered AS int) FROM dbo.ClubTraining WHERE Id=${one}`), 0);

    // ── Schema ───────────────────────────────────────────────────────────────────────────────
    section('Nytt schema');
    // Tis + tor i fyra veckor, med uppehåll vecka 2 → 8 − 2 = 6 tillfällen.
    const wk2tue = new Date(monday); wk2tue.setDate(monday.getDate() + 8);
    const wk2thu = new Date(monday); wk2thu.setDate(monday.getDate() + 10);
    const sched = { clubId: CLUB_ID, name: PREFIX + 'Schema', discipline: 'Faltskytte', startTime: '18:30', endTime: '20:30',
                    weekdays: [2, 4], from: ymd(monday), to: ymd(new Date(monday.getTime() + 27 * 86400000)),
                    breaks: [{ label: 'Lov', from: ymd(wk2tue), to: ymd(wk2thu) }],
                    rotation: [MEMBER_A, MEMBER_B], registrationRequired: '1', maxParticipants: 10, loanWeaponsOffered: '1' };
    const pv = await post('/umbraco/surface/ClubTraining/PreviewSchedule', sched);
    eq('förhandsvisningen räknar 6 tillfällen', pv.count, 6);
    ok('och säger det i klartext', /Skapar 6 tillfällen/.test(pv.summary || ''), pv.summary);
    eq('förhandsvisningen skrev ingenting', n(`SELECT COUNT(*) FROM dbo.ClubTraining WHERE Name='${PREFIX}Schema'`), 0);
    eq('ingen veckodag vägras', (await post('/umbraco/surface/ClubTraining/PreviewSchedule', { ...sched, weekdays: [] })).success, false);

    const cs = await post('/umbraco/surface/ClubTraining/CreateSchedule', sched);
    ok('schemat skapas', cs.success && cs.scheduleId > 0, cs.message);
    eq('med exakt det antal förhandsvisningen sa', cs.created, pv.count);
    eq('raderna bär schemats id', n(`SELECT COUNT(*) FROM dbo.ClubTraining WHERE ScheduleId=${cs.scheduleId}`), 6);
    eq('inget tillfälle under uppehållet',
       n(`SELECT COUNT(*) FROM dbo.ClubTraining WHERE ScheduleId=${cs.scheduleId} AND [Date] BETWEEN '${ymd(wk2tue)}' AND '${ymd(wk2thu)}'`), 0);
    const leaders = sql(`SELECT STRING_AGG(CAST(SkjutledareMemberId AS varchar), ',') WITHIN GROUP (ORDER BY [Date]) FROM dbo.ClubTraining WHERE ScheduleId=${cs.scheduleId}`);
    // Rotationen räknar även uppehållets två dagar, så ordningen fortsätter som om de funnits.
    eq('skjutledarna turas om', leaders, [MEMBER_A, MEMBER_B, MEMBER_A, MEMBER_B, MEMBER_A, MEMBER_B, MEMBER_A, MEMBER_B]
        .filter((_, i) => i !== 2 && i !== 3).join(','));
    eq('grenen följde med', sql(`SELECT TOP 1 Discipline FROM dbo.ClubTraining WHERE ScheduleId=${cs.scheduleId}`), 'Faltskytte');

    // ── Skjutledare, inställd, borttagning ───────────────────────────────────────────────────
    section('Byt skjutledare');
    eq('en medlem i en annan klubb vägras', (await post('/umbraco/surface/ClubTraining/SetSkjutledare', { trainingId: one, memberId: NON_MEMBER })).success, false);
    const ho = await post('/umbraco/surface/ClubTraining/SetSkjutledare', { trainingId: one, memberId: MEMBER_B });
    ok('bytet går igenom', ho.success, ho.message);
    eq('och syns i databasen', n(`SELECT SkjutledareMemberId FROM dbo.ClubTraining WHERE Id=${one}`), MEMBER_B);

    section('Ställ in och ta bort');
    const can = await post('/umbraco/surface/ClubTraining/SetCancelled', { trainingId: one, cancelled: '1' });
    ok('träningen ställs in', can.success);
    eq('inställd i databasen', n(`SELECT CAST(IsCancelled AS int) FROM dbo.ClubTraining WHERE Id=${one}`), 1);
    sql(`INSERT INTO dbo.ClubEventParticipant (EventId, OccasionKind, MemberId, MemberName, SignedUpAt, SignedUpByMemberId, CreatedDate)
         VALUES (${one}, 'Training', ${MEMBER_A}, 'ZZCT Deltagare', GETDATE(), ${MEMBER_A}, GETDATE())`);
    const del1 = await post('/umbraco/surface/ClubTraining/Delete', { trainingId: one });
    eq('en träning med en anmäld kan inte tas bort', del1.success, false);
    ok('och svaret pekar på att ställa in den', /Ställ in/.test(del1.message || ''), del1.message);
    const firstSched = n(`SELECT TOP 1 Id FROM dbo.ClubTraining WHERE ScheduleId=${cs.scheduleId} ORDER BY [Date] DESC`);
    eq('en träning utan anmälda går att ta bort', (await post('/umbraco/surface/ClubTraining/Delete', { trainingId: firstSched })).success, true);

    // ── Deltagarsidan som träningspanel ──────────────────────────────────────────────────────
    section('Deltagarsidan');
    await page.goto(`${BASE}/evenemang/deltagare?e=${one}&kind=training`, { waitUntil: 'domcontentloaded' });
    const head = await page.locator('.desk-head').innerText().catch(() => '');
    ok('sidan visar träningens namn', head.includes(PREFIX + 'Enstaka ändrad'), head.slice(0, 120));
    ok('och skjutledaren', /Skjutledare:/.test(head), head.slice(0, 160));
    // ⚠️ Har träningen raderats (A/B av raderingsspärren) finns ingen #deskApp — läs med kort
    // timeout så sviten fortsätter och rapporterar i stället för att krascha här.
    eq('sidan skickar sorten', await page.locator('#deskApp').getAttribute('data-kind', { timeout: 5000 }).catch(() => null), 'Training');
    // ⚠️ Vänta på NAMNET, inte på att "Laddar" försvunnit: rostern hämtas efter sidladdningen och
    // tabellen står en stund tom utan någon laddningstext — en första version läste den då och
    // blev röd ungefär varannan körning.
    const deskHasRow = await page.waitForFunction(() => /ZZCT Deltagare/.test(document.querySelector('#deskApp')?.innerText || ''),
      null, { timeout: 30000 }).then(() => true).catch(() => false);
    ok('deltagaren står i listan', deskHasRow);

    // ── Ytan i klubbadmin ────────────────────────────────────────────────────────────────────
    section('Klubbadmin');
    await page.goto(`${BASE}/halland/klubbar/haaplinge-goass/`, { waitUntil: 'domcontentloaded', timeout: 120000 });
    await page.click('#clubAdmin-tab');
    await page.click('#clubTrainings-tab');
    await page.waitForSelector(`#ctrList [data-ctr-row="${one}"]`, { timeout: 30000 }).catch(() => {});
    ok('träningen står i klubbadminens lista', await page.locator(`#ctrList [data-ctr-row="${one}"]`).count() === 1);
    ok('schemats tillfällen står där', await page.locator('#ctrList tr', { hasText: PREFIX + 'Schema' }).count() === 5);
    const openHref = await page.locator(`#ctrList [data-ctr-row="${one}"] a`).getAttribute('href');
    eq('Öppna går till deltagarsidan med sorten', openHref, `/evenemang/deltagare?e=${one}&kind=training`);
    await page.click('#ctrActions .dropdown-toggle');
    await page.click('[data-ctr="schedule"]');
    await page.waitForSelector('#ctrScheduleModal.show', { timeout: 10000 });
    await page.fill('#ctsName', PREFIX + 'UI');
    await page.click('label[for="ctsDay2"]');
    await page.evaluate(([f, t]) => {
      const set = (id, v) => { const el = document.getElementById(id); if (el._flatpickr) el._flatpickr.setDate(v, true); else { el.value = v; el.dispatchEvent(new Event('input', { bubbles: true })); } };
      set('ctsFrom', f); set('ctsTo', t);
    }, [ymd(monday), ymd(new Date(monday.getTime() + 13 * 86400000))]);
    await page.waitForFunction(() => /Skapar 2 tillfällen/.test(document.getElementById('ctsSummary').textContent), null, { timeout: 10000 }).catch(() => {});
    ok('dialogen räknar ut antalet medan man fyller i', /Skapar 2 tillfällen/.test(await page.locator('#ctsSummary').innerText()),
       await page.locator('#ctsSummary').innerText());
    eq('Skapa-knappen är aktiv', await page.locator('#ctsCreate').isDisabled(), false);

    // ── Behörighet ───────────────────────────────────────────────────────────────────────────
    section('Behörighet');
    const ctx2 = await browser.newContext({ ignoreHTTPSErrors: true });
    const p2 = await ctx2.newPage(); await p2.goto(`${BASE}/`, { waitUntil: 'domcontentloaded' });
    const anon = await helpers(p2).get(`/umbraco/surface/ClubTraining/List?clubId=${CLUB_ID}`);
    eq('utloggad nekas listan', anon.success, false);
    ok('och får inga träningar', !anon.trainings);
    await ctx2.close();
  } finally {
    // Deltagarraden städas på NAMNET också: raderades träningen trots anmälan (A/B) är raden
    // föräldralös och hittas inte via träningens id.
    sql(`DELETE p FROM dbo.ClubEventParticipant p WHERE p.MemberName LIKE '${PREFIX}%'
           OR (p.OccasionKind='Training' AND p.EventId IN (SELECT Id FROM dbo.ClubTraining WHERE Name LIKE '${PREFIX}%'));
         DELETE FROM dbo.ClubTraining WHERE Name LIKE '${PREFIX}%';
         DELETE FROM dbo.ClubTrainingSchedule WHERE Name LIKE '${PREFIX}%';`);
    const left = n(`SELECT (SELECT COUNT(*) FROM dbo.ClubTraining WHERE Name LIKE '${PREFIX}%') + (SELECT COUNT(*) FROM dbo.ClubTrainingSchedule WHERE Name LIKE '${PREFIX}%')`);
    console.log(left === 0 ? '\n(städat)' : `\n⚠️ ${left} fixturrader kvar`);
    await browser.close();
  }
  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log('  - ' + f)); process.exitCode = 1; }
};
main().catch(e => { console.error(e); process.exitCode = 1; });
