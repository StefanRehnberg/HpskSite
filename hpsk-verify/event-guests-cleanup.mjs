// event-guests-cleanup.mjs — städar kvarglömda ZZG-fixturer efter en avbruten svitkörning.
//
// KÖR:  node hpsk-verify/event-guests-cleanup.mjs
//
// ⚠️ Evenemangsnoden MÅSTE raderas genom endpointen, inte i SQL — Umbracos publicerade cache
//    känner inte till en rad som försvinner bakom ryggen på content-servicen.
// ⚠️ Deltagarraderna följer INTE med noden (de är nycklade på EventId, utan koppling till noden).
//    Kör därför efteråt:
//      sqlcmd -S localhost\SQLEXPRESS -d Umbraco -E -C -b -Q "SET QUOTED_IDENTIFIER ON;
//        DELETE FROM dbo.ClubEventParticipant WHERE MemberName LIKE 'ZZG %';"

import { chromium } from 'playwright';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const CLUB_ID = 2604;

const main = async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  try {
    await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER);
    await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS);
    await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
    await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
    await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded', timeout: 120000 });

    const list = await page.evaluate(async ([u]) => {
      const r = await fetch(u, { credentials: 'same-origin' });
      return JSON.parse(await r.text());
    }, [`/umbraco/surface/Club/GetClubEvents?clubId=${CLUB_ID}`]);

    const rows = (list.events || list.data || []).filter(e => (e.eventName || e.name || '').startsWith('ZZG '));
    console.log(`Hittade ${rows.length} ZZG-evenemang.`);

    for (const e of rows) {
      const res = await page.evaluate(async ([u, id]) => {
        const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
        const fd = new FormData();
        fd.append('eventId', id);
        fd.append('__RequestVerificationToken', tokEl ? tokEl.value : '');
        const r = await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' });
        return `${r.status} ${(await r.text()).slice(0, 100)}`;
      }, ['/umbraco/surface/Club/DeleteClubEvent', String(e.id)]);
      console.log(`  ${e.id} ${e.eventName || e.name} — ${res}`);
    }
  } finally {
    await browser.close();
  }
};

main();
