// training-migration-verify.mjs — fas B3: gamla träningshändelser blir ClubTraining.
//
// KÖR:  HPSK_USER="admin.claude@pistol.nu" HPSK_PASS="123456" node hpsk-verify/training-migration-verify.mjs
//
// FIXTUR: sviten skapar sina EGNA händelser i klubb 2604 (Träning med en anmäld deltagare, Duell,
// och en "Precisionskurs" som INTE ska flyttas) och migrerar bara den klubben. Har klubben andra
// träningshändelser som skulle flyttas avbryter sviten INNAN något skrivs — den rör aldrig
// befintligt dev-data. Allt fixturen skapar tas bort i finally, och städningen kontrolleras i SQL.
//
// ⚠️ Noderna AVPUBLICERAS av migreringen och raderas sedan av städningen. ClubTraining-raderna och
// deltagarraderna har ingen koppling till noden, så de raderas uttryckligen i SQL.
import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const CLUB_ID = 2610;   // Kungsbacka-Wiske — inga händelser i dev
const PREFIX = 'ZZTM ';

let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);
const sql = q => execFileSync('sqlcmd',
  ['-S', 'localhost\\SQLEXPRESS', '-d', 'Umbraco', '-E', '-C', '-b', '-W', '-h', '-1', '-s', '|', '-Q',
   'SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; ' + q],
  { encoding: 'utf8', maxBuffer: 1 << 24 }).trim();
const n = q => parseInt(sql(q) || '0', 10);
const day = d => new Date(Date.now() + d * 86400000).toISOString().slice(0, 10);

const main = async () => {
  const browser = await chromium.launch({ headless: true });
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();
  const created = [];

  try {
    await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER || '');
    await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS || '');
    await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
    await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
    await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded', timeout: 120000 });
    if (await page.locator('#firearms-member-tab').count() === 0) {
      console.error('AVBRYTER: inte inloggad — varje nekas-påstående hade blivit grönt utan att nå funktionen.');
      process.exitCode = 1; return;
    }

    const form = (u, f) => page.evaluate(async ([u, f]) => {
      const fd = new FormData(); Object.keys(f).forEach(k => fd.append(k, f[k]));
      fd.append('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]')?.value || '');
      const t = await (await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' })).text();
      try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 200) }; }
    }, [u, f]);
    const get = u => page.evaluate(async u => {
      const t = await (await fetch(u, { credentials: 'same-origin' })).text();
      try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 200) }; }
    }, u);
    const json = (u, b) => page.evaluate(async ([u, b]) => {
      const r = await fetch(u, { method: 'POST', credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json',
                   'RequestVerificationToken': document.querySelector('input[name="__RequestVerificationToken"]')?.value || '' },
        body: JSON.stringify(b) });
      const t = await r.text();
      try { return JSON.parse(t); } catch { return { success: false, _status: r.status, _raw: t.slice(0, 200) }; }
    }, [u, b]);

    // ── Förkontroll: rör aldrig befintligt dev-data ──────────────────────────────────────────
    section('Förkontroll');
    const before = await get(`/umbraco/surface/TrainingMigration/Preview?clubId=${CLUB_ID}`);
    ok('torrkörningen svarar', before.success === true, JSON.stringify(before).slice(0, 200));
    const foreign = (before.rows || []).filter(r => r.action === 'migrera' && !String(r.name).startsWith(PREFIX));
    if (foreign.length) {
      console.error(`AVBRYTER: klubb ${CLUB_ID} har ${foreign.length} egna träningshändelser som skulle flyttas — sviten rör inte dem.`);
      process.exitCode = 1; return;
    }

    // ── Fixtur ───────────────────────────────────────────────────────────────────────────────
    section('Fixtur');
    const mk = async (name, type, extra = {}) => {
      const r = await form('/umbraco/surface/Club/CreateClubEvent', {
        clubId: CLUB_ID, eventName: PREFIX + name, eventDate: `${day(9)} 18:30`, description: 'ZZTM beskrivning',
        venue: 'Inomhushallen', eventType: type, contactPerson: '', contactEmail: '', contactPhone: '',
        registrationRequired: 'true', maxParticipants: 12, registrationUrl: '', isMandatory: 'false',
        lanevapenOffered: 'true', registrationDeadline: '', eventPrices: '', ...extra });
      const id = (r.data && r.data.id) || 0;
      if (id) created.push(id);
      return id;
    };
    const trId = await mk('Tisdagsträning', 'Träning', { isMandatory: 'true' });
    const duId = await mk('Duellkväll', 'Duell');
    const kuId = await mk('Precisionskurs', 'Precisionskurs');
    ok('tre fixturhändelser skapades', trId > 0 && duId > 0 && kuId > 0, `${trId} ${duId} ${kuId}`);
    if (!trId || !duId || !kuId) return;

    // Kontot är inte medlem i fixturklubben, så deltagarraden skrivs i SQL. Det som prövas är att
    // migreringen FLYTTAR raden, inte anmälningsvägen (den har egna sviter).
    sql(`INSERT INTO dbo.ClubEventParticipant (EventId, OccasionKind, MemberId, MemberName, SignedUpAt, SignedUpByMemberId, CreatedDate)
         VALUES (${trId}, 'Event', 8315, 'ZZTM Deltagare', GETDATE(), 8315, GETDATE())`);
    ok('en deltagare står på träningen', n(`SELECT COUNT(*) FROM dbo.ClubEventParticipant WHERE OccasionKind='Event' AND EventId=${trId}`) === 1);
    const signedBefore = (await get(`/umbraco/surface/ClubEvent/GetSignupState?eventId=${trId}`)).counts?.signedUp;
    eq('en anmäld före migreringen', signedBefore, 1);
    const trainingsBefore = n('SELECT COUNT(*) FROM dbo.ClubTraining');

    // ── Torrkörning ──────────────────────────────────────────────────────────────────────────
    section('Torrkörning skriver ingenting');
    const dry = await get(`/umbraco/surface/TrainingMigration/Preview?clubId=${CLUB_ID}`);
    const rowOf = (res, id) => (res.rows || []).find(r => r.eventId === id);
    eq('träningen ska migreras', rowOf(dry, trId)?.action, 'migrera');
    eq('duellen ska migreras', rowOf(dry, duId)?.action, 'migrera');
    eq('duellen får grenen Duell', rowOf(dry, duId)?.discipline, 'Duell');
    eq('kursen listas inte alls', rowOf(dry, kuId), undefined);
    eq('torrkörningen ser deltagaren', rowOf(dry, trId)?.participants, 1);
    eq('torrkörningen är inte tillämpad', dry.applied, false);
    // Ett utelämnat apply på POST-vägen är också en torrkörning.
    const dryPost = await json('/umbraco/surface/TrainingMigration/Run', { clubId: CLUB_ID });
    eq('POST utan apply är en torrkörning', dryPost.applied, false);
    eq('inga träningar skapades av torrkörningarna', n('SELECT COUNT(*) FROM dbo.ClubTraining'), trainingsBefore);
    eq('deltagarraden står kvar på händelsen',
       n(`SELECT COUNT(*) FROM dbo.ClubEventParticipant WHERE OccasionKind='Event' AND EventId=${trId}`), 1);

    // ── Skarp körning ────────────────────────────────────────────────────────────────────────
    section('Skarp körning');
    const run = await json('/umbraco/surface/TrainingMigration/Run', { clubId: CLUB_ID, apply: '1' });
    eq('körningen är tillämpad', run.applied, true);
    const tr = rowOf(run, trId), du = rowOf(run, duId);
    eq('träningen flyttades', tr?.action, 'migrerad');
    eq('duellen flyttades', du?.action, 'migrerad');
    const tid = tr?.trainingId || 0;
    ok('träningen fick ett id', tid > 0);
    eq('träningsraden pekar på noden', n(`SELECT LegacyEventNodeId FROM dbo.ClubTraining WHERE Id=${tid}`), trId);
    eq('dag och klockslag', sql(`SELECT CONVERT(char(10),[Date],23)+'|'+ISNULL(StartTime,'-') FROM dbo.ClubTraining WHERE Id=${tid}`),
       `${day(9)}|18:30`);
    eq('obligatorisk följde med', n(`SELECT CAST(IsMandatory AS int) FROM dbo.ClubTraining WHERE Id=${tid}`), 1);
    eq('lånevapen följde med', n(`SELECT CAST(LoanWeaponsOffered AS int) FROM dbo.ClubTraining WHERE Id=${tid}`), 1);
    eq('anmälan krävs följde med', n(`SELECT CAST(RegistrationRequired AS int) FROM dbo.ClubTraining WHERE Id=${tid}`), 1);
    eq('taket följde med', n(`SELECT MaxParticipants FROM dbo.ClubTraining WHERE Id=${tid}`), 12);
    eq('duellens gren', sql(`SELECT Discipline FROM dbo.ClubTraining WHERE LegacyEventNodeId=${duId}`), 'Duell');
    eq('deltagaren flyttades till träningen',
       n(`SELECT COUNT(*) FROM dbo.ClubEventParticipant WHERE OccasionKind='Training' AND EventId=${tid}`), 1);
    eq('och ligger inte kvar på händelsen',
       n(`SELECT COUNT(*) FROM dbo.ClubEventParticipant WHERE OccasionKind='Event' AND EventId=${trId}`), 0);
    eq('händelsen är avpublicerad', n(`SELECT ISNULL(CAST(published AS int),0) FROM umbracoDocument WHERE nodeId=${trId}`), 0);
    eq('kursen är orörd och publicerad', n(`SELECT ISNULL(CAST(published AS int),0) FROM umbracoDocument WHERE nodeId=${kuId}`), 1);
    eq('kursen fick ingen träning', n(`SELECT COUNT(*) FROM dbo.ClubTraining WHERE LegacyEventNodeId=${kuId}`), 0);

    // B2 på riktigt: deltagandet läses som en träning.
    const st = await get(`/umbraco/surface/ClubEvent/GetSignupState?eventId=${tid}&kind=training`);
    eq('anmälningsläget för träningen visar samma anmälda', st.counts?.signedUp, 1);
    ok('och jag står som anmäld', st.me?.signedUp === true || st.mine?.signedUp === true || st.isSignedUp === true
       || JSON.stringify(st).includes('"signedUp":true'), JSON.stringify(st).slice(0, 200));

    // ── Idempotens ───────────────────────────────────────────────────────────────────────────
    section('En andra körning gör ingenting');
    const again = await json('/umbraco/surface/TrainingMigration/Run', { clubId: CLUB_ID, apply: '1' });
    eq('träningen står som klar', rowOf(again, trId)?.action, 'klar');
    eq('ingen ny träning skapades',
       n(`SELECT COUNT(*) FROM dbo.ClubTraining WHERE LegacyEventNodeId IN (${trId},${duId})`), 2);

    // ── Behörighet ───────────────────────────────────────────────────────────────────────────
    section('Bara sajtadmin');
    const anon = await browser.newContext({ ignoreHTTPSErrors: true });
    const ap = await anon.newPage();
    await ap.goto(`${BASE}/`, { waitUntil: 'domcontentloaded' });
    const denied = await ap.evaluate(async () => {
      const t = await (await fetch('/umbraco/surface/TrainingMigration/Preview')).text();
      try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 100) }; }
    });
    eq('utloggad nekas', denied.success, false);
    ok('och får inga rader', !denied.rows);
    await anon.close();
  } finally {
    // ── Städning ─────────────────────────────────────────────────────────────────────────────
    if (created.length) {
      const ids = created.join(',');
      sql(`DELETE p FROM dbo.ClubEventParticipant p WHERE (p.OccasionKind='Event' AND p.EventId IN (${ids}))
             OR (p.OccasionKind='Training' AND p.EventId IN (SELECT Id FROM dbo.ClubTraining WHERE LegacyEventNodeId IN (${ids})));
           DELETE FROM dbo.ClubTraining WHERE LegacyEventNodeId IN (${ids});`);
      for (const id of created) {
        await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' }).catch(() => {});
        await page.evaluate(async id => {
          const fd = new FormData(); fd.append('eventId', id);
          fd.append('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]')?.value || '');
          await fetch('/umbraco/surface/Club/DeleteClubEvent', { method: 'POST', body: fd, credentials: 'same-origin' });
        }, String(id)).catch(() => {});
      }
      const left = n(`SELECT COUNT(*) FROM umbracoNode WHERE id IN (${ids}) AND trashed=0`)
                 + n(`SELECT COUNT(*) FROM dbo.ClubTraining WHERE LegacyEventNodeId IN (${ids})`);
      console.log(left === 0 ? '\n(städat)' : `\n⚠️ ${left} fixturrester kvar — ids ${ids}`);
    }
    await browser.close();
  }
  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log('  - ' + f)); process.exitCode = 1; }
};
main().catch(e => { console.error(e); process.exitCode = 1; });
