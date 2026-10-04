// training-panel-verify.mjs — fas B5: träningarna syns i kalendern och på klubbsidan, öppnas i
// träningspanelen, går att anmäla sig till, och räknas i aktivitetssammanställningen. En
// närvaro på en städdag som INTE är obligatorisk räknas inte.
//
// KÖR:  HPSK_USER="admin.claude@pistol.nu" HPSK_PASS="123456" node hpsk-verify/training-panel-verify.mjs
//
// Fixturen heter "ZZB5 …" (en träning + en städdag i klubb 2604) och tas bort i finally,
// kontrollerat i SQL. Kontot (8315) är medlem i 2604.
import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const CLUB_ID = 2604;
const ME = 8315;
const PREFIX = 'ZZB5 ';

let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);
const sql = q => execFileSync('sqlcmd',
  ['-S', 'localhost\\SQLEXPRESS', '-d', 'Umbraco', '-E', '-C', '-b', '-W', '-h', '-1', '-s', '|', '-Q',
   'SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; ' + q], { encoding: 'utf8' }).trim();
const n = q => parseInt(sql(q) || '0', 10);
const day = k => { const d = new Date(); d.setDate(d.getDate() + k); return d.toISOString().slice(0, 10); };
const YEAR = new Date().getFullYear();

const main = async () => {
  const browser = await chromium.launch({ headless: true });
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();
  let trainingId = 0, eventId = 0;
  try {
    await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER || '');
    await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS || '');
    await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
    await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
    await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded', timeout: 120000 });
    if (await page.locator('#firearms-member-tab').count() === 0) { console.error('AVBRYTER: inte inloggad.'); process.exitCode = 1; return; }

    const get = u => page.evaluate(async u => { const t = await (await fetch(u, { credentials: 'same-origin' })).text(); try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 150) }; } }, u);
    const post = (u, b) => page.evaluate(async ([u, b]) => {
      const r = await fetch(u, { method: 'POST', credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': document.querySelector('input[name="__RequestVerificationToken"]')?.value || '' },
        body: JSON.stringify(b) });
      const t = await r.text(); try { return JSON.parse(t); } catch { return { success: false, _status: r.status, _raw: t.slice(0, 150) }; }
    }, [u, b]);
    const form = (u, f) => page.evaluate(async ([u, f]) => {
      const fd = new FormData(); Object.keys(f).forEach(k => fd.append(k, f[k]));
      fd.append('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]')?.value || '');
      const t = await (await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' })).text();
      try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 150) }; }
    }, [u, f]);

    // ── Fixtur ───────────────────────────────────────────────────────────────────────────────
    section('Fixtur');
    const s = await post('/umbraco/surface/ClubTraining/Save', {
      clubId: CLUB_ID, name: PREFIX + 'Kvällsträning', date: day(2), startTime: '18:00', endTime: '20:00',
      discipline: 'Precision', venue: 'Hålan', skjutledareMemberId: 5513, registrationRequired: '1', maxParticipants: 10,
      loanWeaponsOffered: '0', isMandatory: '0' });
    trainingId = s.id || 0;
    ok('träningen skapas', trainingId > 0, s.message);
    const ev = await form('/umbraco/surface/Club/CreateClubEvent', {
      clubId: CLUB_ID, eventName: PREFIX + 'Städdag', eventDate: `${day(2)} 10:00`, description: '', venue: 'Banan',
      eventType: 'Städning', contactPerson: '', contactEmail: '', contactPhone: '', registrationRequired: 'true',
      maxParticipants: 0, registrationUrl: '', isMandatory: 'false', lanevapenOffered: 'false', registrationDeadline: '', eventPrices: '' });
    eventId = (ev.data && ev.data.id) || 0;
    ok('städdagen skapas', eventId > 0, ev.message);
    if (!trainingId || !eventId) return;

    // ── Kalenderns data ──────────────────────────────────────────────────────────────────────
    section('Kalendern');
    const up = await get(`/umbraco/surface/Club/GetUpcomingEvents?clubId=${CLUB_ID}&days=30`);
    const row = (up.data || []).find(e => e.kind === 'training' && e.id === trainingId);
    ok('träningen står bland kommande', !!row);
    eq('med panel-adressen, inte en sida', row?.url, `#training-${trainingId}`);
    eq('i kalenderns träningsfilter', row?.scope, 'training');
    ok('och med starttiden', String(row?.date || '').includes('T18:00'), row?.date);

    // ── Panelen ──────────────────────────────────────────────────────────────────────────────
    section('Panelen');
    await page.goto(`${BASE}/halland/klubbar/haaplinge-goass/`, { waitUntil: 'domcontentloaded', timeout: 120000 });
    ok('dialogen finns på klubbsidan', await page.locator('#hpskTrainingPanel').count() === 1);
    ok('och finns i ett exemplar trots två inkluderingar', await page.locator('#hpskTrainingPanel').count() === 1);
    // Öppna via en riktig länk, så att den delegerade lyssnaren prövas — inte bara funktionen.
    // ⚠️ Cookie-bannern är position:fixed längst ned och fångar klicket. Ta bort NODEN — att trycka
    // på en samtyckesknapp är ett val, och det är inte svitens att göra.
    await page.evaluate(id => {
      document.getElementById('cookieConsentBanner')?.remove();
      const a = document.createElement('a'); a.href = '#training-' + id; a.id = 'zzb5link'; a.textContent = 'x';
      document.body.prepend(a);
    }, trainingId);
    await page.click('#zzb5link');
    await page.waitForSelector('#hpskTrainingPanel.show', { timeout: 10000 }).catch(() => {});
    ok('länken öppnar dialogen', await page.locator('#hpskTrainingPanel.show').count() === 1);
    ok('och följdes inte (adressen är oförändrad)', !page.url().includes('#training-'), page.url());
    const frame = page.frameLocator('#hpskTrainingPanelFrame');
    await frame.locator('h1').waitFor({ timeout: 20000 }).catch(() => {});
    ok('panelen visar träningens namn', (await frame.locator('h1').innerText().catch(() => '')).includes(PREFIX + 'Kvällsträning'));
    const facts = await frame.locator('.tp-facts').innerText().catch(() => '');
    ok('och tid, plats och skjutledare', /18:00/.test(facts) && /Hålan/.test(facts) && /Skjutledare/.test(facts), facts.slice(0, 200));
    eq('anmälningskortet vet att det är en träning', await frame.locator('#ces-card').getAttribute('data-kind', { timeout: 10000 }).catch(() => null), 'Training');
    await frame.locator('#ces-card').waitFor({ state: 'visible', timeout: 15000 }).catch(() => {});
    ok('kortet visas', await frame.locator('#ces-card').isVisible().catch(() => false));
    const height = await page.evaluate(() => parseInt(document.getElementById('hpskTrainingPanelFrame').style.height, 10) || 0);
    ok('iframen fick sin höjd från sidan i den', height > 320, String(height));
    const missing = await get('/traning/panel?id=999999').catch(() => null);
    const missingHtml = await page.evaluate(async () => (await fetch('/traning/panel?id=999999')).text());
    // Sidan HTML-kodar å/ä (&#xE4;) — jämför på en ASCII-del av meningen.
    ok('en okänd träning säger det', /finns inte l/.test(missingHtml));

    // ── Anmälan och upprop med sorten ────────────────────────────────────────────────────────
    section('Anmälan');
    const su = await post('/umbraco/surface/ClubEvent/SignUp', { eventId: trainingId, kind: 'Training' });
    ok('jag anmäler mig till träningen', su.success, su.message);
    eq('raden bär sorten Training', n(`SELECT COUNT(*) FROM dbo.ClubEventParticipant WHERE OccasionKind='Training' AND EventId=${trainingId} AND MemberId=${ME}`), 1);
    eq('och ingen rad hamnade på en händelse med samma id', n(`SELECT COUNT(*) FROM dbo.ClubEventParticipant WHERE OccasionKind='Event' AND EventId=${trainingId}`), 0);
    const st = await get(`/umbraco/surface/ClubEvent/GetSignupState?eventId=${trainingId}&kind=training`);
    eq('anmälningsläget räknar mig', st.counts?.signedUp, 1);
    const at1 = await post('/umbraco/surface/ClubEvent/SetAttendance', { eventId: trainingId, kind: 'Training', memberId: ME, status: 'Present' });
    ok('närvaro på träningen registreras', at1.success, at1.message);
    const su2 = await post('/umbraco/surface/ClubEvent/SignUp', { eventId, kind: 'Event' });
    ok('jag anmäler mig till städdagen', su2.success, su2.message);
    const at2 = await post('/umbraco/surface/ClubEvent/SetAttendance', { eventId, kind: 'Event', memberId: ME, status: 'Present' });
    ok('närvaro på städdagen registreras', at2.success, at2.message);

    // ── Aktivitetssammanställningen ──────────────────────────────────────────────────────────
    section('Aktivitet');
    const sum = await get(`/umbraco/surface/Foreningsintyg/GetActivitySummary?memberId=${ME}&year=${YEAR}`);
    const tr = (sum.data?.entries || []).find(e => e.title === PREFIX + 'Kvällsträning');
    const cl = (sum.data?.entries || []).find(e => e.title === PREFIX + 'Städdag');
    ok('träningen står i sammanställningen', !!tr, JSON.stringify(sum).slice(0, 300));
    eq('och räknas', tr?.countsAsActivity, true);
    ok('städdagen står också där — den göms inte', !!cl);
    eq('men räknas inte', cl?.countsAsActivity, false);
    ok('och skälet säger varför', /utan skytte/.test(cl?.notCountedReason || ''), cl?.notCountedReason);

    // Kontrollprov: samma städdag, obligatorisk → räknas. Annars kan "räknas inte" vara sant
    // för att händelser aldrig räknas alls.
    sql(`UPDATE pd SET pd.intValue = 1 FROM umbracoPropertyData pd JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId AND pt.Alias = 'isMandatory'
         JOIN umbracoContentVersion cv ON cv.id = pd.versionId AND cv.nodeId = ${eventId}`);
    const hasMand = n(`SELECT COUNT(*) FROM umbracoPropertyData pd JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId AND pt.Alias = 'isMandatory'
         JOIN umbracoContentVersion cv ON cv.id = pd.versionId AND cv.nodeId = ${eventId}`);
    if (hasMand > 0) {
      // Innehållscachen läser inte SQL direkt — gå via redigeringen i stället.
      await form('/umbraco/surface/Club/EditClubEvent', {
        clubId: CLUB_ID, eventId, eventName: PREFIX + 'Städdag', eventDate: `${day(2)} 10:00`, description: '', venue: 'Banan',
        eventType: 'Städning', contactPerson: '', contactEmail: '', contactPhone: '', registrationRequired: 'true',
        maxParticipants: 0, registrationUrl: '', isMandatory: 'true', lanevapenOffered: 'false', registrationDeadline: '', eventPrices: '' });
    }
    const sum2 = await get(`/umbraco/surface/Foreningsintyg/GetActivitySummary?memberId=${ME}&year=${YEAR}`);
    const cl2 = (sum2.data?.entries || []).find(e => e.title === PREFIX + 'Städdag');
    eq('en OBLIGATORISK städdag räknas (kontrollprov)', cl2?.countsAsActivity, true);
  } finally {
    sql(`DELETE FROM dbo.ClubEventParticipant WHERE (OccasionKind='Training' AND EventId=${trainingId || 0}) OR (OccasionKind='Event' AND EventId=${eventId || 0});
         DELETE FROM dbo.ClubTraining WHERE Name LIKE '${PREFIX}%';`);
    if (eventId) {
      await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' }).catch(() => {});
      await page.evaluate(async id => {
        const fd = new FormData(); fd.append('eventId', id);
        fd.append('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]')?.value || '');
        await fetch('/umbraco/surface/Club/DeleteClubEvent', { method: 'POST', body: fd, credentials: 'same-origin' });
      }, String(eventId)).catch(() => {});
    }
    const left = n(`SELECT (SELECT COUNT(*) FROM dbo.ClubTraining WHERE Name LIKE '${PREFIX}%')
                         + (SELECT COUNT(*) FROM umbracoNode WHERE id = ${eventId || 0} AND trashed = 0)`);
    console.log(left === 0 ? '\n(städat)' : `\n⚠️ ${left} fixturrester kvar`);
    await browser.close();
  }
  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log('  - ' + f)); process.exitCode = 1; }
};
main().catch(e => { console.error(e); process.exitCode = 1; });
