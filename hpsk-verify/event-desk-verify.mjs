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
// ⚠️ EN ANNAN MEDLEM an den inloggade. Lagger funktionaren till SIG SJALV ar raden inte en
// walk-in utan hens egen anmalan, och pastaendet om "deltagare tillagd i disken" mater ingenting.
const WALKIN_MEMBER = 5601;
// ⚠️ Ännu en annan medlem — raden utan pris får inte krocka med walk-in-fallet ovan.
const NOPRICE_MEMBER = 2344;

let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);

// ⚠⚠ QUOTED_IDENTIFIER ON PA VARJE SATS. ClubEventParticipant bar ett FILTRERAT index (det
// som slapper in flera gaster), och SQL Server vagrar all DML mot en sadan tabell nar
// installningen ar av - vilket ar sqlcmds standard. Utan detta misslyckas bade fixturen och
// stadningens DELETE, den senare TYST om -b saknas. Samma falla som MarkenSeries och Firearm.
const sql = q => execFileSync('sqlcmd',
  ['-S', 'localhost\\SQLEXPRESS', '-d', 'Umbraco', '-E', '-C', '-b', '-W', '-h', '-1', '-Q', 'SET QUOTED_IDENTIFIER ON; ' + q],
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
    // ⚠️ Token MÅSTE finnas - skriv-endpointsen ar [ValidateAntiForgeryToken], och utan den
    // blir varje avprickning ett TYST 400. Den kommer fran Master.cshtml sedan sidan fick
    // sajtens layout. FLERA per sida ar normalt pa hela sajten (klubbsidan har tre), sa
    // pastaendet ar "minst en" - ett krav pa exakt en hade varit ett pastaende om layouten.
    ok('sidan bar en antiforgery-token', await page.locator('input[name="__RequestVerificationToken"]').count() >= 1);

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
      return items.some(i => /Hantera betalning/i.test(i.innerText));
    });
    ok('menyn erbjuder "Hantera betalning" - samma namn som pa tavling', canConfirm,
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

    // —— ⚠⚠ EN DELTAGARE SOM LÄGGS TILL I DISKEN GÅR ATT TA BETALT AV ——
    //
    // Rapporterat 2026-09-19: "lägger jag till deltagare på nya event-adminsidan finns inget
    // sätt att ta betalt". Två orsaker, och båda mäts här:
    //   1. Walk-in-raden föddes UTAN FeeAmount — personen var gratis för alltid.
    //   2. Menyvalet hängde på att en betalningsrad fanns, och en rad skapas bara av MEDLEMMENS
    //      "visa Swish-koden". Funktionärens tillagda deltagare hade därför ingen.
    section('Deltagare tillagd i disken');

    // ⚠⚠ EN DISKANMÄLAN TAR EN PLATS. Raden skrevs tidigare med SignedUpAt = null, och
    // platsräkningen krävde SignedUpAt — så tjugo personer tillagda i en lokal för tjugo
    // rapporterade tjugo lediga platser, tyst. Mäts som ett DELTA över just den här
    // handlingen, aldrig mot ett absolut tal: fixturen bär redan en anmäld och en gäst, och
    // ett absolut tal hade då mätt dem i stället.
    const summary = () => page.evaluate(async id => {
      const d = await (await fetch(`/umbraco/surface/ClubEvent/GetRoster?eventId=${id}`)).json();
      return { signedUp: d.counts.signedUp, seatsLeft: d.counts.seatsLeft };
    }, eventId);
    const beforeSeats = await summary();

    const walkIn = await page.evaluate(async ([id, pid]) => {
      const tok = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch('/umbraco/surface/ClubEvent/SetAttendance', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tok ? tok.value : '' },
        body: JSON.stringify({ eventId: id, memberId: pid, status: 'Present', priceId: 'p1' }),
      });
      return await r.json().catch(() => null);
    }, [eventId, WALKIN_MEMBER]);
    ok('deltagaren gick att lägga till', walkIn && walkIn.success, walkIn && walkIn.message);

    const afterSeats = await summary();
    ok('platsen räknas — en ledig plats färre',
       beforeSeats.seatsLeft - afterSeats.seatsLeft === 1,
       `${beforeSeats.seatsLeft} → ${afterSeats.seatsLeft}`);
    ok('och den räknas bland de anmälda',
       afterSeats.signedUp - beforeSeats.signedUp === 1,
       `${beforeSeats.signedUp} → ${afterSeats.signedUp}`);

    // ⚠️ MÄT AVGIFTEN I DATABASEN. En rad utan FeeAmount ser likadan ut i listan tills någon
    // försöker ta betalt — det var precis så felet gick att missa.
    const fee = sql(`SET NOCOUNT ON;
SELECT ISNULL(CAST(FeeAmount AS NVARCHAR(20)),'NULL') FROM dbo.ClubEventParticipant
WHERE EventId = ${eventId} AND MemberId = ${WALKIN_MEMBER};`).trim();
    ok('och fick ett PRIS, inte noll-och-gratis', fee === '180.00' || fee === '180',
       `FeeAmount = ${fee}`);

    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => {
      const b = document.getElementById('deskBody');
      return b && !b.innerText.includes('Hämtar');
    }, null, { timeout: 30000 }).catch(() => {});

    // ⚠⚠ RADEN LETAS UPP PA NAMNET, ALDRIG PA BRICKAN. Nyckla soket pa "oanmald" och en
    // felaktig bricka gor att raden inte hittas alls - da HOPPAS pastaendena om brickan over
    // och rapporteras som grona. Namnet kommer ur GetRoster, alltsa fran servern och inte fran
    // den yta som provas.
    const readWalkRow = async () => {
      const namn = await page.evaluate(async ([id, mid]) => {
        const d = await (await fetch(`/umbraco/surface/ClubEvent/GetRoster?eventId=${id}`)).json();
        const row = (d.rows || []).find(x => x.memberId === mid && !x.cancelled);
        return row ? row.name : '';
      }, [eventId, WALKIN_MEMBER]);
      if (!namn) return null;
      return page.evaluate(n => {
        const trs = [...document.querySelectorAll('#deskBody tr')];
        const tr = trs.find(x => x.children[0] && x.children[0].innerText.includes(n));
        if (!tr) return null;
      return {
        namnCell: tr.children[0].innerText.trim(),
        anmald: tr.children[1].innerText.trim(),
        betalning: tr.children[2].innerText.trim(),
        narvaro: tr.children[3].innerText.trim(),
        harMeny: [...tr.querySelectorAll('.dropdown-item')].some(i => /Hantera betalning/i.test(i.innerText)),
        harBetalvag: tr.innerHTML.includes('deskPayOpen('),
      };
      }, namn);
    };

    let walkRow = await readWalkRow();
    ok('raden finns med betalningsläge', !!walkRow, JSON.stringify(walkRow));
    if (walkRow) {
      ok('den visar en skuld', /Obetalt/i.test(walkRow.betalning), walkRow.betalning);
      // ⚠️ KÄRNAN i rapporten: vägen måste finnas UTAN att medlemmen först tryckt fram koden.
      ok('och "Hantera betalning" erbjuds även utan att Swish-koden visats', walkRow.harMeny);

      // ⚠⚠ INGEN BRICKA FÅR SÄGA EMOT LISTAN. Står raden här ÄR personen anmäld —
      // disken anmälde hen. "På plats" lästes som närvaro och sa emot närvarokolumnen;
      // "oanmäld" sa emot själva listan. Båda var falska påståenden om raden.
      ok('namncellen bär ingen bricka som säger emot listan',
         !/oanmäld|på plats|närvarande/i.test(walkRow.namnCell), walkRow.namnCell);
      // ⚠⚠ OCH ANMÄLD-KOLUMNEN ÄR IFYLLD. En diskanmälan är en anmälan, med ett
      // klockslag. Raden skrevs tidigare med SignedUpAt = null, och därmed påstod modellen
      // att personen aldrig anmält sig — vilket är roten till hela brickröran OCH till att
      // raden inte tog någon plats.
      ok('och Anmäld-kolumnen bär en anmälningstid — en diskanmälan är en anmälan',
         /\d{4}-\d{2}-\d{2} \d{2}:\d{2}/.test(walkRow.anmald),
         `Anmäld-cellen var "${walkRow.anmald}"`);

      const absent = await page.evaluate(async ([id, WALKIN]) => {
        const tok = document.querySelector('input[name="__RequestVerificationToken"]');
        const rows = await (await fetch(`/umbraco/surface/ClubEvent/GetRoster?eventId=${id}`)).json();
        const row = (rows.rows || []).find(x => x.memberId === WALKIN && !x.cancelled);
        if (!row) return null;
        const r = await fetch('/umbraco/surface/ClubEvent/SetAttendance', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tok ? tok.value : '' },
          body: JSON.stringify({ eventId: id, participantId: row.id, status: 'Absent', note: null }),
        });
        return await r.json().catch(() => null);
      }, [eventId, WALKIN_MEMBER]);
      ok('raden som lades till i disken gick att pricka av som frånvarande', absent && absent.success,
         absent && absent.message);

      await page.reload({ waitUntil: 'domcontentloaded' });
      await page.waitForFunction(() => {
        const b = document.getElementById('deskBody');
        return b && !b.innerText.includes('Hämtar');
      }, null, { timeout: 30000 }).catch(() => {});

      const afterAbsent = await readWalkRow();
      // Kontrollprov åt båda hållen: brickan ska stå KVAR (anmälan saknas fortfarande)
      // medan närvarokolumnen säger frånvaro. Bara ett frannvaropostående hade varit grönt
      // även om hela raden försvunnit.
      ok('närvarokolumnen säger frånvaro efter avprickningen',
         afterAbsent && /ej här|frånvarande/i.test(afterAbsent.narvaro),
         afterAbsent && afterAbsent.narvaro);
      ok('och namncellen säger fortfarande ingenting om närvaro',
         afterAbsent && !/oanmäld|på plats|närvarande/i.test(afterAbsent.namnCell),
         afterAbsent && afterAbsent.namnCell);

      walkRow = afterAbsent || walkRow;
    }

    const reg = await page.evaluate(async ([id, payer]) => {
      const tok = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch('/umbraco/surface/ClubEvent/RegisterPayment', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tok ? tok.value : '' },
        body: JSON.stringify({ eventId: id, payerMemberId: payer, actualAmount: 180, method: 'kontant' }),
      });
      return await r.json().catch(() => null);
    }, [eventId, WALKIN_MEMBER]);
    ok('betalningen gick att registrera på plats', reg && reg.success, reg && reg.message);

    // ⚠️ Kontrollprov: ett okänt betalsätt får INTE tyst bli Swish — kontanter och Swish
    // landar på olika konton.
    const bad = await page.evaluate(async id => {
      const tok = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch('/umbraco/surface/ClubEvent/RegisterPayment', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tok ? tok.value : '' },
        body: JSON.stringify({ eventId: id, payerMemberId: 1, actualAmount: 50, method: 'hittepa' }),
      });
      return await r.json().catch(() => null);
    }, eventId);
    ok('ett okänt betalsätt vägras', bad && bad.success === false, JSON.stringify(bad));

    const paidRows = sql(`SET NOCOUNT ON;
SELECT COUNT(*) FROM dbo.LedgerPayment
WHERE SourceType = 'Event' AND SourceId = ${eventId}
  AND PayerMemberId = ${WALKIN_MEMBER} AND ConfirmedUtc IS NOT NULL AND Method = 'kontant';`).trim();
    ok('och den ligger i liggaren som kontant', paidRows === '1', `${paidRows} rader`);

    // —— ⚠⚠ EN RAD UTAN PRIS SÄGER DET, OCH GÅR ATT RÄTTA ——
    //
    // Rapporterat 2026-09-19, ANDRA halvan: prisfixen gäller bara NYA rader. En deltagare som
    // lades till innan evenemanget hade ett pris — eller innan disken började fråga efter ett
    // — bär inget belopp, och raden påstod då "Ingen avgift" på ett evenemang som kostar pengar.
    // Det är ett falskt påstående: personen blir aldrig debiterad och ingen märker det.
    section('Rad utan pris');

    // Bygg tillståndet: en deltagare UTAN priceId, precis som de gamla raderna.
    const noPrice = await page.evaluate(async ([id, pid]) => {
      const tok = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch('/umbraco/surface/ClubEvent/SetAttendance', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tok ? tok.value : '' },
        body: JSON.stringify({ eventId: id, memberId: pid, status: 'Present' }),
      });
      return await r.json().catch(() => null);
    }, [eventId, NOPRICE_MEMBER]);
    ok('en deltagare gick att lägga till', noPrice && noPrice.success, noPrice && noPrice.message);

    // ⚠⚠ TILLSTÅNDET GÅR INTE LÄNGRE ATT SKAPA GENOM API:ET, och det ÄR fixen: på ett
    // evenemang med ETT pris väljer ResolvePriceChoice det självt, så en ny rad får alltid ett
    // belopp. Raderna som saknar pris är HISTORISKA — skapade innan disken satte något — och
    // fixturen byggs därför i SQL. Att i stället låta bli att testa hade lämnat både
    // "Pris saknas"-vyn och rättningen omätta, och det är just de som används på verklig data.
    sql(`SET NOCOUNT ON;
UPDATE dbo.ClubEventParticipant SET FeeAmount = NULL, FeePriceId = NULL, FeeLabel = NULL
WHERE EventId = ${eventId} AND MemberId = ${NOPRICE_MEMBER};`);
    const legacy = sql(`SET NOCOUNT ON;
SELECT ISNULL(CAST(FeeAmount AS NVARCHAR(20)),'NULL') FROM dbo.ClubEventParticipant
WHERE EventId = ${eventId} AND MemberId = ${NOPRICE_MEMBER};`).trim();
    ok('fixturen är en rad utan pris, som de gamla', legacy === 'NULL', legacy);

    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => {
      const b = document.getElementById('deskBody');
      return b && !b.innerText.includes('Hämtar');
    }, null, { timeout: 30000 }).catch(() => {});

    const noPriceRow = await page.evaluate(async id => {
      const r = await fetch(`/umbraco/surface/ClubEvent/GetRoster?eventId=${id}`);
      const d = await r.json();
      const row = (d.rows || []).find(x => x.fee == null && !x.isGuest && !x.cancelled);
      const trs = [...document.querySelectorAll('#deskBody tr')];
      const tr = trs.find(x => row && x.innerText.includes(row.name));
      if (!row) return { debug: (d.rows||[]).map(x=>({n:x.name,fee:x.fee,g:x.isGuest,c:x.cancelled})) };
      return row ? {
        state: row.paymentState,
        cell: tr ? tr.children[2].innerText.trim() : null,
        meny: tr ? [...tr.querySelectorAll('.dropdown-item')].map(i => i.innerText.trim()) : [],
        id: row.id,
      } : null;
    }, eventId);

    ok('raden finns', !!(noPriceRow && noPriceRow.id), JSON.stringify(noPriceRow));
    if (noPriceRow && noPriceRow.id) {
      // ⚠⚠ KÄRNAN: den får INTE påstå att evenemanget är gratis.
      ok('den påstår INTE "Ingen avgift"', !/Ingen avgift/i.test(noPriceRow.cell || ''), noPriceRow.cell);
      ok('utan säger att priset saknas', /Pris saknas/i.test(noPriceRow.cell || ''), noPriceRow.cell);
      // ⚠️ "Ändra pris" ÄR BORTTAGEN, med flit. Betalningsdialogen frågar hur mycket
      // som kom in, så priset är ett förslag och inte ett andra ställe att uttrycka samma
      // sak på. BÅDA riktningarna mäts — ett rent frånvaropostående vore grönt även på en
      // rad som tappat hela sin meny, vilket är precis det som hände här en gång.
      ok('menyn erbjuder INTE längre "Ändra pris"',
         !noPriceRow.meny.some(m => /pris/i.test(m)), noPriceRow.meny.join(', '));
      ok('utan pekar på "Hantera betalning", där beloppet anges',
         noPriceRow.meny.some(m => /Hantera betalning/i.test(m)), noPriceRow.meny.join(', '));

      const setP = await page.evaluate(async ([id, rowId]) => {
        const tok = document.querySelector('input[name="__RequestVerificationToken"]');
        const r = await fetch('/umbraco/surface/ClubEvent/SetParticipantPrice', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tok ? tok.value : '' },
          body: JSON.stringify({ eventId: id, participantId: rowId, priceId: 'p1' }),
        });
        return await r.json().catch(() => null);
      }, [eventId, noPriceRow.id]);
      ok('priset gick att sätta i efterhand', setP && setP.success, setP && setP.message);

      // ⚠️ Kontrollprov: ett pris som inte finns på evenemanget får inte tyst bli "inget pris"
      // — då hade en felstavad rad sett ut som ett medvetet gratisval.
      const badP = await page.evaluate(async ([id, rowId]) => {
        const tok = document.querySelector('input[name="__RequestVerificationToken"]');
        const r = await fetch('/umbraco/surface/ClubEvent/SetParticipantPrice', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tok ? tok.value : '' },
          body: JSON.stringify({ eventId: id, participantId: rowId, priceId: 'finns-inte' }),
        });
        return await r.json().catch(() => null);
      }, [eventId, noPriceRow.id]);
      ok('ett okänt pris vägras', badP && badP.success === false, JSON.stringify(badP));

      const after = sql(`SET NOCOUNT ON;
SELECT ISNULL(CAST(FeeAmount AS NVARCHAR(20)),'NULL') FROM dbo.ClubEventParticipant
WHERE Id = ${noPriceRow.id};`).trim();
      ok('och beloppet står på raden i databasen', after === '180.00' || after === '180', after);
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
