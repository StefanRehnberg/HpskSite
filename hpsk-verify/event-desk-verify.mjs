// event-desk-verify.mjs — arrangörens deltagarsida, /evenemang/deltagare
//
// KÖR:  node hpsk-verify/event-desk-verify.mjs
//
// ⚠️⚠️ TRE PÅSTÅENDEN BÄR YTAN, och alla tre är lätta att bygga fel:
//
//  1. BETALNINGEN GÅR ATT BOCKA AV. Det fanns ingen väg alls — ConfirmPayment hade noll
//     anropare i hela kodbasen. Det var hela skälet att sidan byggdes.
//
//  2. "EJ AVPRICKAD" ÄR INTE "FRÅNVARANDE". Switchen är binär, närvaron har fyra lägen. En
//     oberörd switch som lästes som frånvaro vore ett påstående systemet hittat på — och
//     siffran är på väg att bli underlag för ett föreningsintyg.
//
//  3. GÄSTEN HAR INGEN EGEN SKULD. Skulden är per sällskap. Visar gästraden ett eget belopp
//     summerar skärmen 450 + 180 + 90 för ett sällskap som är skyldigt 450.
//
// ⚠️ SVITEN SKRIVER. Ett evenemang med pris skapas och raderas i finally. Betalningsrader går
// inte att radera (det är en verifikationsliggare) och ligger kvar med flit.

import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const CLUB = `${BASE}/halland/klubbar/haaplinge-goass/`;
const CLUB_ID = 2604;
const NAME = 'ZZDESK Avgiftskvall';

let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);

const sql = q => execFileSync('sqlcmd',
  ['-S', 'localhost\\SQLEXPRESS', '-d', 'Umbraco', '-E', '-C', '-b', '-W', '-h', '-1', '-Q', q],
  { encoding: 'utf8', maxBuffer: 1 << 24 });

const PRICES = JSON.stringify([{ id: 'p1', label: 'Vuxen', amount: 180 }]);

const main = async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  const jsErrors = [];
  page.on('pageerror', e => { if (!/ckeditor/i.test(e.message)) jsErrors.push(e.message); });
  let eventId = 0, post;

  try {
    await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER);
    await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS);
    await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
    await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
    await page.goto(CLUB, { waitUntil: 'domcontentloaded', timeout: 120000 });
    await page.evaluate(() => { const b = document.getElementById('cookieConsentBanner'); if (b) b.remove(); });

    if (await page.locator('#clubAdmin-tab').count() === 0) {
      console.error('\nAVBRYTER: ingen adminpanel — varje påstående nedan hade mätt en tom sida.');
      process.exitCode = 1;
      return;
    }

    post = (url, fields) => page.evaluate(async ([url, fields]) => {
      const tok = document.querySelector('input[name="__RequestVerificationToken"]');
      const fd = new FormData();
      Object.entries(fields).forEach(([k, v]) => fd.append(k, String(v)));
      if (tok) fd.append('__RequestVerificationToken', tok.value);
      const r = await fetch(url, { method: 'POST', body: fd });
      const t = await r.text();
      try { return JSON.parse(t); } catch { return { success: false, message: t.slice(0, 150) }; }
    }, [url, fields]);

    const start = new Date(); start.setDate(start.getDate() + 14);
    const iso = start.toISOString().slice(0, 10);
    const c = await post('/umbraco/surface/Club/CreateClubEvent', {
      clubId: CLUB_ID, eventName: NAME, eventType: 'Träning', eventDate: `${iso} 18:00`,
      registrationRequired: true, maxParticipants: 20, eventPrices: PRICES,
      eventAudience: 'Club', eventSwishNumber: '1231231231',
    });
    ok('evenemanget skapades', c && c.success, c && c.message);
    if (!c || !c.success) return;
    eventId = c.data.id;

    // Funktionären anmäler sig själv, så det finns en betalande part.
    const signup = await page.evaluate(async id => {
      const tok = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch('/umbraco/surface/ClubEvent/SignUp', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tok ? tok.value : '' },
        body: JSON.stringify({ eventId: id, priceId: 'p1' }),
      });
      return await r.json().catch(() => null);
    }, eventId);
    ok('funktionären är anmäld', signup && signup.success, signup && signup.message);

    // ── Sidan ─────────────────────────────────────────────────────────────────────────
    section('Sidan öppnas');
    await page.goto(`${BASE}/evenemang/deltagare?e=${eventId}`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => {
      const b = document.getElementById('deskBody');
      return b && !b.innerText.includes('Hämtar');
    }, null, { timeout: 30000 }).catch(() => {});

    ok('evenemangets namn står i huvudet', (await page.locator('body').innerText()).includes(NAME));
    ok('det finns en rad i listan', await page.locator('#deskBody tr').count() >= 1);
    // ⚠️ Antiforgery-token MÅSTE finnas: sidan är chromeless, och utan den blir varje
    // avprickning ett tyst 400.
    ok('sidan bär en antiforgery-token', await page.locator('input[name="__RequestVerificationToken"]').count() === 1);

    // ── ⚠️ Påstående 2: ej avprickad är inte frånvarande ──────────────────────────────
    section('Ej avprickad är ett eget tillstånd');
    const before = await page.evaluate(() => {
      const tr = document.querySelector('#deskBody tr');
      const sw = tr.querySelector('input[type=checkbox]');
      return { switchOn: sw.checked, label: tr.querySelector('.form-check-label').innerText.trim() };
    });
    eq('switchen är av innan någon prickat av', before.switchOn, false);
    ok('men etiketten säger INTE "Ej här" — den säger att inget är registrerat',
       before.label === '—', `etiketten var "${before.label}"`);
    ok('och kortet räknar ej avprickade separat',
       (await page.locator('#cNotRecorded').innerText()).trim() !== '0');

    // ── ⚠️ Påstående 1: betalningen går att bocka av ──────────────────────────────────
    section('Betalningen');
    const payState = await page.evaluate(() => {
      const tr = document.querySelector('#deskBody tr');
      return tr.children[2].innerText.trim();
    });
    ok('raden visar att det är obetalt', /Obetalt/i.test(payState), payState);

    // Medlemmen begär betalning (visar Swish-koden) — det är det som skapar raden.
    const startPay = await page.evaluate(async id => {
      const tok = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch('/umbraco/surface/ClubEvent/StartPayment', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tok ? tok.value : '' },
        body: JSON.stringify({ eventId: id }),
      });
      return await r.json().catch(() => null);
    }, eventId);
    ok('Swish-koden gick att begära UTAN räkenskapsår', startPay && startPay.success,
       startPay && startPay.message);

    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => {
      const b = document.getElementById('deskBody');
      return b && !b.innerText.includes('Hämtar');
    }, null, { timeout: 30000 }).catch(() => {});

    const canConfirm = await page.evaluate(() => {
      const items = [...document.querySelectorAll('#deskBody .dropdown-item')];
      return items.some(i => /Ta emot betalning/i.test(i.innerText));
    });
    ok('menyn erbjuder "Ta emot betalning"', canConfirm,
       'det var precis den vägen som saknades helt');

    // Bekräfta via endpointen — dialogen är en prompt() och blockerar automationen.
    const paymentId = +sql(`SET NOCOUNT ON;
SELECT TOP 1 Id FROM dbo.LedgerPayment
WHERE SourceType = 'Event' AND SourceId = ${eventId} AND ConfirmedUtc IS NULL AND VoidedUtc IS NULL
ORDER BY Id DESC;`).trim();
    ok('betalningsraden finns i liggaren', paymentId > 0, `id ${paymentId}`);

    const conf = await page.evaluate(async ([id, pid]) => {
      const tok = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch('/umbraco/surface/ClubEvent/ConfirmPayment', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tok ? tok.value : '' },
        body: JSON.stringify({ eventId: id, paymentId: pid, actualAmount: 180 }),
      });
      return await r.json().catch(() => null);
    }, [eventId, paymentId]);
    ok('betalningen gick att ta emot', conf && conf.success, conf && conf.message);
    // ⚠️ Klubben bokför inte hos oss, så svaret får INTE påstå "bokförd".
    ok('och beskedet säger kvitterad, inte bokförd',
       conf && conf.posted === false && /kvitterad/i.test(conf.message || ''), JSON.stringify(conf));

    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => {
      const b = document.getElementById('deskBody');
      return b && !b.innerText.includes('Hämtar');
    }, null, { timeout: 30000 }).catch(() => {});
    const after = await page.evaluate(() => document.querySelector('#deskBody tr').children[2].innerText.trim());
    ok('raden visar nu mottaget', /Mottaget/i.test(after), after);
    ok('och kortet räknar in pengarna',
       (await page.locator('#cSettled').innerText()).includes('180'));

    // ── ⚠️ Påstående 3: gästen har ingen egen skuld ───────────────────────────────────
    section('Gästen bär ingen egen skuld');
    const meId = +sql(`SET NOCOUNT ON;
SELECT TOP 1 MemberId FROM dbo.ClubEventParticipant
WHERE EventId = ${eventId} AND MemberId > 0 ORDER BY Id;`).trim();
    const guest = await page.evaluate(async ([id, host]) => {
      const tok = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch('/umbraco/surface/ClubEvent/AddGuest', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tok ? tok.value : '' },
        body: JSON.stringify({ eventId: id, guestOfMemberId: host, name: 'ZZDESK Gast', priceId: 'p1' }),
      });
      return await r.json().catch(() => null);
    }, [eventId, meId]);
    ok('en gäst gick att lägga till', guest && guest.success, guest && guest.message);

    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => {
      const b = document.getElementById('deskBody');
      return b && b.querySelectorAll('tr').length >= 2;
    }, null, { timeout: 30000 }).catch(() => {});

    const rows = await page.evaluate(() => [...document.querySelectorAll('#deskBody tr')].map(tr => ({
      namn: tr.children[0].innerText.trim(),
      betalning: tr.children[2].innerText.trim(),
    })));
    const g = rows.find(r => /ZZDESK Gast/.test(r.namn));
    ok('gästraden finns', !!g, JSON.stringify(rows));
    if (g) {
      ok('gästen är märkt som gäst', /gäst/i.test(g.namn), g.namn);
      // ⚠️ Kärnan: ingen egen siffra, utan en hänvisning till den som betalar.
      ok('och visar att den ingår i någon annans betalning', /Ingår i/i.test(g.betalning), g.betalning);
      ok('utan ett eget belopp', !/\d+\s*kr/.test(g.betalning), g.betalning);
    }

    ok('inga JS-fel', jsErrors.length === 0, jsErrors.join(' | '));

  } finally {
    if (eventId) {
      await page.goto(CLUB, { waitUntil: 'domcontentloaded' }).catch(() => {});
      const d = await post('/umbraco/surface/Club/DeleteClubEvent', { eventId }).catch(() => null);
      if (!d || !d.success) console.log(`  ! kunde inte radera ${eventId}: ${d && d.message}`);
      // ⚠️ Att radera noden tar INTE med sig deltagarraderna — de är nycklade på EventId utan
      // koppling till noden. Utan det här ligger de kvar osynliga för alltid.
      try { sql(`SET NOCOUNT ON; DELETE FROM dbo.ClubEventParticipant WHERE EventId = ${eventId};`); }
      catch { /* best effort */ }
    }
    await browser.close();
  }

  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log(`  - ${f}`)); process.exitCode = 1; }
};

main();
