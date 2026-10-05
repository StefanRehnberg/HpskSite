// Ingångarna till /min-kurs: "Min kurs" i användarmenyn för kurstränare, och "Öppna kursen"
// på klubbpanelens Träningsgrupper. Bygger en egen grupp i klubb 2604 med collaborator.claude
// (5514) som tränare och raderar den efteråt.
//
// Kör: node hpsk-verify/minkurs-entry-verify.mjs
import { chromium } from 'playwright';
import { execSync } from 'child_process';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const sql = q => execSync(`sqlcmd -S "localhost\\SQLEXPRESS" -d Umbraco -E -C -b -W -h -1 -Q "SET NOCOUNT ON; ${q}"`, { encoding: 'utf8' }).trim();
let pass = 0, fail = 0; const failed = [];
const ok = (c, m, d = '') => { if (c) { pass++; console.log('  ✓ ' + m); } else { fail++; failed.push(m); console.log('  ✗ ' + m + (d ? ' — ' + d : '')); } };

async function login(browser, user) {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="loginModel.Username"]', user);
  await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS || '123456');
  await page.click('form button[type="submit"]:visible');
  await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
  return { ctx, page };
}

async function menuHasMinKurs(page) {
  await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded', timeout: 120000 });
  return await page.locator('a.dropdown-item[href="/min-kurs"]').count();
}

let groupId = 0;
const browser = await chromium.launch();
try {
  groupId = parseInt(sql(`INSERT INTO TrainingGroups (Name, ClubId, StartDate, IsActive, CreatedDate, CreatedByMemberId) OUTPUT INSERTED.Id VALUES ('ZZM Ingangskurs', 2604, GETDATE(), 1, GETDATE(), 5513)`), 10);
  ok(groupId > 0, 'fixturgruppen skapades', String(groupId));

  console.log('\n== Användarmenyn');
  {
    const { ctx, page } = await login(browser, 'collaborator.claude@pistol.nu');
    ok(await menuHasMinKurs(page) === 0, 'medlem utan kurs ser inte Min kurs (kontrollprov)');
    sql(`INSERT INTO TrainingGroupMembers (TrainingGroupId, MemberId, Role, JoinedDate, AddedByMemberId, IsActive) VALUES (${groupId}, 5514, 'Trainer', GETDATE(), 5513, 1)`);
    ok(await menuHasMinKurs(page) === 1, 'tränare i en aktiv kurs ser Min kurs');
    sql(`UPDATE TrainingGroups SET IsActive = 0 WHERE Id = ${groupId}`);
    ok(await menuHasMinKurs(page) === 0, 'tränare i en INAKTIV kurs ser den inte');
    sql(`UPDATE TrainingGroups SET IsActive = 1 WHERE Id = ${groupId}`);
    sql(`UPDATE TrainingGroupMembers SET Role = 'Member' WHERE TrainingGroupId = ${groupId}`);
    ok(await menuHasMinKurs(page) === 0, 'deltagare (inte tränare) ser den inte');
    sql(`UPDATE TrainingGroupMembers SET Role = 'Trainer' WHERE TrainingGroupId = ${groupId}`);
    await page.goto(`${BASE}/min-kurs`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => /ZZM Ingangskurs|Du är inte tränare/.test(document.body.innerText), null, { timeout: 30000 }).catch(() => {});
    ok(/ZZM Ingangskurs/.test(await page.innerText('body')), 'länken leder till kursen');
    await ctx.close();
  }

  console.log('\n== Klubbpanelen');
  {
    const { ctx, page } = await login(browser, 'builder.claude@pistol.nu');
    await page.goto(`${BASE}/halland/klubbar/haaplinge-goass/`, { waitUntil: 'domcontentloaded', timeout: 120000 });
    await page.evaluate(() => document.getElementById('cookieConsentBanner')?.remove());
    await page.click('#clubAdmin-tab');
    await page.waitForFunction(() => typeof loadClubTrainingGroups === 'function', null, { timeout: 30000 });
    await page.evaluate(() => loadClubTrainingGroups());
    const link = page.locator(`a[href="/min-kurs?g=${groupId}"]`);
    await link.first().waitFor({ state: 'attached', timeout: 30000 }).catch(() => {});
    ok(await link.count() === 1, 'aktiv grupp har Öppna kursen');
    ok(await link.getAttribute('target') === '_blank', 'och öppnar i ny flik');
    sql(`UPDATE TrainingGroups SET IsActive = 0 WHERE Id = ${groupId}`);
    await page.evaluate(() => loadClubTrainingGroups());
    await page.waitForTimeout(2500);
    ok(await link.count() === 0, 'inaktiv grupp har ingen Öppna kursen');
    await ctx.close();
  }
} finally {
  if (groupId > 0) {
    sql(`DELETE FROM TrainingGroupMembers WHERE TrainingGroupId = ${groupId}; DELETE FROM TrainingGroups WHERE Id = ${groupId}`);
    console.log('\n(städat)');
  }
  await browser.close();
}
console.log(`\n${pass}/${pass + fail} gröna`);
if (fail) { console.log('FALLERADE:\n' + failed.map(f => '  - ' + f).join('\n')); process.exit(1); }
