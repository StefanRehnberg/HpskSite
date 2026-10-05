// training-stats-verify.mjs — fas B5: klubbstatistiken räknar klubbens träningar (ClubTraining)
// som typen Träning, och en INSTÄLLD träning räknas inte.
//
// KÖR:  HPSK_USER="admin.claude@pistol.nu" HPSK_PASS="123456" node hpsk-verify/training-stats-verify.mjs
//
// Mäter SKILLNADEN före/efter en fixturträning, så befintlig data i klubben spelar ingen roll.
import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const CLUB_ID = 2604;
let pass = 0, fail = 0;
const failures = [];
const eq = (n, a, e) => { const c = JSON.stringify(a) === JSON.stringify(e); if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n} — fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`); } };
const sql = q => execFileSync('sqlcmd', ['-S', 'localhost\\SQLEXPRESS', '-d', 'Umbraco', '-E', '-C', '-b', '-W', '-h', '-1', '-Q',
  'SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; ' + q], { encoding: 'utf8' }).trim();
const insert = cancelled => sql(`INSERT INTO dbo.ClubTraining (ClubId,[Date],Name,IsCancelled,RegistrationRequired,LoanWeaponsOffered,IsMandatory,CreatedByMemberId,CreatedDate,UpdatedDate)
  VALUES (${CLUB_ID}, DATEFROMPARTS(YEAR(GETDATE()),12,30), 'ZZST Statistik', ${cancelled ? 1 : 0}, 0, 0, 0, 8315, GETDATE(), GETDATE())`);

const main = async () => {
  const browser = await chromium.launch();
  const page = await (await browser.newContext()).newPage();
  try {
    await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER || '');
    await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS || '');
    await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
    await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
    const trainingCount = async () => page.evaluate(async c => {
      const d = await (await fetch(`/umbraco/surface/ClubStatistics/GetClubStatistics?clubId=${c}&force=true`)).json();
      const byType = (d && d.data && d.data.clubActivities && d.data.clubActivities.byType) || null;
      if (!byType) return { err: JSON.stringify(d).slice(0, 200) };
      const t = byType.find(x => x.type === 'Träning');
      return { n: t ? t.count : 0 };
    }, CLUB_ID);

    const before = await trainingCount();
    if (before.err) { console.error('AVBRYTER: hittade inte byType i svaret — ' + before.err); process.exitCode = 1; return; }
    insert(false);
    const after = await trainingCount();
    eq('en träning i år ökar Träning med ett', after.n - before.n, 1);
    insert(true);
    const afterCancelled = await trainingCount();
    eq('en INSTÄLLD träning räknas inte', afterCancelled.n - after.n, 0);
  } finally {
    sql(`DELETE FROM dbo.ClubTraining WHERE Name = 'ZZST Statistik'`);
    await browser.close();
  }
  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log('  - ' + f)); process.exitCode = 1; }
};
main().catch(e => { console.error(e); process.exitCode = 1; });
