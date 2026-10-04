// training-migration-preview.mjs — torrkörning av fas B3 över ALLA klubbar. Skriver ingenting.
// KÖR:  HPSK_USER=... HPSK_PASS=... node hpsk-verify/training-migration-preview.mjs [--base https://pistol.nu]
import { chromium } from 'playwright';

const i = process.argv.indexOf('--base');
const BASE = i > 0 ? process.argv[i + 1] : (process.env.HPSK_BASE || 'http://localhost:18150');

const browser = await chromium.launch({ headless: true });
const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER || '');
await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS || '');
await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
const r = await page.evaluate(async () => (await fetch('/umbraco/surface/TrainingMigration/Preview')).json());
if (!r.success) { console.error(r.message || r); process.exit(1); }

console.log(`Att flytta: ${r.toMigrate}  Lämnas: ${r.skipped}  Deltagarrader: ${r.participantsMoved}  Lånevapenbokningar: ${r.bookingsMoved}\n`);
const byAction = {};
for (const row of r.rows) (byAction[row.action] ||= []).push(row);
for (const [action, rows] of Object.entries(byAction)) {
  console.log(`── ${action} (${rows.length})`);
  const reasons = {};
  rows.forEach(x => reasons[x.reason] = (reasons[x.reason] || 0) + 1);
  Object.entries(reasons).forEach(([k, v]) => console.log(`   ${v} × ${k}`));
}
const clubs = {};
r.rows.filter(x => x.action === 'migrera').forEach(x => clubs[x.clubName] = (clubs[x.clubName] || 0) + 1);
console.log('\nPer klubb:'); Object.entries(clubs).forEach(([k, v]) => console.log(`   ${v} × ${k}`));
await browser.close();
