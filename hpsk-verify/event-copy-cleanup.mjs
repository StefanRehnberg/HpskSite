// Stadar bort ZZ-handelser som verifieringssviterna lamnat kvar.
// ⚠️ Att radera handelsenoden tar INTE med sig ClubEventParticipant-raderna - de ar nycklade
// pa EventId utan koppling till noden. Darfor SQL-stadningen sist.
import { chromium } from 'playwright';
const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const IDS = (process.argv[2] || '').split(',').map(x => +x.trim()).filter(Boolean);

const main = async () => {
  if (!IDS.length) { console.log('Anvandning: node hpsk-verify/event-copy-cleanup.mjs 9189,9190'); return; }
  const browser = await chromium.launch({ headless: true });
  const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER);
  await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS);
  await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
  await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
  await page.goto(`${BASE}/halland/klubbar/ankeborg-pistolklubb/`, { waitUntil: 'domcontentloaded' });

  for (const id of IDS) {
    for (const url of ['/umbraco/surface/Club/DeleteClubEvent', '/umbraco/surface/Club/DeleteRegionEvent']) {
      const r = await page.evaluate(async ([url, id]) => {
        const tok = document.querySelector('input[name="__RequestVerificationToken"]');
        const fd = new FormData();
        fd.append('eventId', String(id));
        if (tok) fd.append('__RequestVerificationToken', tok.value);
        const res = await fetch(url, { method: 'POST', body: fd });
        const t = await res.text();
        return `${res.status} ${t.slice(0, 120)}`;
      }, [url, id]);
      console.log(`  ${id} via ${url.split('/').pop()}: ${r}`);
      if (r.includes('"success":true')) break;
    }
  }
  await browser.close();
};
main();
