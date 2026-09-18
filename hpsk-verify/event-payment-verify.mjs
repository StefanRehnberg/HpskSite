// event-payment-verify.mjs — betalning vid evenemangsanmälan, genom verifikationsliggaren.
//
// KÖR:  node hpsk-verify/event-payment-verify.mjs
//
// FÖRUTSÄTTNINGAR
//   • Dev-appen körs på http://localhost:18150 (--launch-profile "Umbraco.Web.UI").
//     ⚠️ ALDRIG `dotnet run --no-launch-profile` — det pekar på PROD-DB.
//   • Liggarens tabeller finns (P1). `/health/ledger` ska svara OK.
//   • Doctype-egenskaperna `eventPrices` och `swishNumber` finns på `clubSimpleEvent`.
//   • Inloggad som klubbadmin i den klubb fixturen skapas i.
//
// ⚠️⚠️ TRE STEG SOM ALDRIG FÅR SLÅS IHOP, och sviten mäter gränserna mellan dem:
//     Request  — skulden finns, inga pengar
//     Claim    — betalaren SÄGER att hen betalat. Fortfarande inga pengar, inget kvitto
//     Confirm  — arrangören har sett pengarna. NU kvitto och bokföring
//   Utan Swish-API är steg två allt vi vet. Ett kvitto vid QR-visning hade varit en urkund på en
//   betalning som kanske aldrig gjordes.
//
// ⚠️ SVITEN SKRIVER: ett evenemang, en anmälan och betalningsrader — allt prefixat `ZZB `.
//    Betalningsrader i liggaren går INTE att radera (det är en verifikationsliggare), så de
//    makuleras i stället. Se städningen sist.

import { chromium } from 'playwright';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const CLUB_ID = 2604;
const PREFIX = 'ZZB ';

let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);
const day = n => new Date(Date.now() + n * 86400000).toISOString().slice(0, 10);

const PRICES = JSON.stringify([
  { Id: 'vuxen', Label: 'Vuxen', Amount: 180 },
  { Id: 'barn', Label: 'Barn 7-15', Amount: 90 },
]);

const main = async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  const jsErrors = [];
  page.on('pageerror', e => jsErrors.push(e.message));
  let evId = 0;

  try {
    await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER);
    await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS);
    await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
    await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
    for (let i = 0; i < 3; i++) {
      try { await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded', timeout: 120000 }); break; }
      catch { await page.waitForTimeout(3000); }
    }
    if (await page.locator('#firearms-member-tab').count() === 0) {
      console.error('\nAVBRYTER: inte inloggad — varje "nekas" nedan hade blivit vakuöst grönt.');
      process.exitCode = 1;
      return;
    }

    const post = (u, f) => page.evaluate(async ([url, fields]) => {
      const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
      const fd = new FormData();
      Object.keys(fields).forEach(k => fd.append(k, fields[k]));
      fd.append('__RequestVerificationToken', tokEl ? tokEl.value : '');
      const r = await fetch(url, { method: 'POST', body: fd, credentials: 'same-origin' });
      const t = await r.text();
      try { return JSON.parse(t); } catch { return { success: false, _status: r.status, _raw: t.slice(0, 160) }; }
    }, [u, f]);

    const json = (u, b) => page.evaluate(async ([url, body]) => {
      const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tokEl ? tokEl.value : '' },
        body: JSON.stringify(body), credentials: 'same-origin',
      });
      const t = await r.text();
      try { return JSON.parse(t); } catch { return { success: false, _status: r.status, _raw: t.slice(0, 200) }; }
    }, [u, b]);

    const get = u => page.evaluate(async url => {
      const r = await fetch(url, { credentials: 'same-origin' });
      const t = await r.text();
      try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 200) }; }
    }, u);

    const state = () => get(`/umbraco/surface/ClubEvent/GetSignupState?eventId=${evId}`);

    // ── Fixtur ────────────────────────────────────────────────────────────────────────────────
    section('Fixtur');
    const ev = await post('/umbraco/surface/Club/CreateClubEvent', {
      clubId: CLUB_ID, eventName: `${PREFIX}Betalprov`, eventDate: `${day(14)} 18:00`,
      description: 'ZZB', venue: 'Banan', eventType: 'Socialt',
      contactPerson: '', contactEmail: '', contactPhone: '',
      registrationRequired: 'true', maxParticipants: 10, registrationUrl: '',
      isMandatory: 'false', lanevapenOffered: 'false', registrationDeadline: '',
      eventPrices: PRICES, eventAudience: 'Club',
      // ⚠️ Numret sätts PÅ HÄNDELSEN, inte på klubben. Dev-klubben har tomt `swishNumber`, och att
      // skriva ett på den vore att ändra en riktig inställning på en riktig klubb. Åsidosättningen
      // försvinner med fixturen.
      eventSwishNumber: '0701234567',
    });
    evId = (ev.data && ev.data.id) || 0;
    ok('evenemanget skapas', ev.success && evId > 0, ev.message);
    if (!evId) return;

    let s = await state();
    ok('evenemanget kan ta betalt (Swish-nummer + avgift)', s.payment && s.payment.swishAvailable === true,
       `payment=${JSON.stringify(s.payment)}`);
    // ⚠️ Åsidosättningen ska INTE flaggas som ärvd — det är den distinktionen arrangörens skärm
    // bygger sitt besked på.
    eq('numret kommer från händelsen, inte från ägaren', s.payment.swishFromOwner, false);
    eq('och det är numret vi satte', s.payment.swishNumber, '0701234567');

    // ⚠️ AVBRYT om betalning inte är möjlig. Utan spärren jämför varje påstående nedan `undefined`
    // mot `undefined` och blir GRÖNT — första körningen rapporterade "ett andra klick återanvänder
    // samma betalning" som klart på en betalning som aldrig skapades.
    if (!s.payment || !s.payment.swishAvailable) {
      console.error('\nAVBRYTER: evenemanget kan inte ta betalt, så betalningsstegen nedan skulle '
        + 'bli vakuöst gröna.');
      fail++; failures.push('förutsättning: swishAvailable');
      return;
    }

    // ── Skulden uppstår med anmälan ───────────────────────────────────────────────────────────
    section('Skulden');
    const me = await json('/umbraco/surface/ClubEvent/SignUp', { eventId: evId, priceId: 'vuxen' });
    ok('jag anmäler mig', me.success, me.message);

    s = await state();
    eq('skulden är 180', s.party.outstanding, 180);
    eq('inget är betalt', s.party.confirmedPaid, 0);
    eq('och anmälan är INTE giltig än', s.party.isPaidUp, false);

    // Gästen läggs till FÖRE betalningen — hela poängen med att skulden är per sällskap.
    const g = await json('/umbraco/surface/ClubEvent/AddGuest',
      { eventId: evId, name: `${PREFIX}Karin`, priceId: 'barn' });
    ok('en gäst läggs till innan betalning', g.success, g.message);

    s = await state();
    eq('skulden växte till 270', s.party.outstanding, 270);

    // ── Request ───────────────────────────────────────────────────────────────────────────────
    section('Betalningen begärs');
    const start = await json('/umbraco/surface/ClubEvent/StartPayment', { eventId: evId });
    ok('betalningen skapas', start.success, JSON.stringify(start).slice(0, 200));

    // ⚠️⚠️ VÄGRAS BETALNINGEN för att klubbens liggare inte är uppsatt är det RÄTT beteende, inte
    // ett fel i den här koden — och det är hela skälet kontrollen flyttades hit från Confirm.
    // Utan räkenskapsår kan arrangören inte kvittera pengar som redan swishats.
    if (!start.success && /räkenskapsår|ekonomiinställningar/i.test(start.message || '')) {
      console.log('\n  ⓘ Klubbens liggare saknar räkenskapsår i dev — betalningen vägras FÖRE');
      console.log('    medlemmen ombeds swisha, vilket är avsikten. Stegen nedan kräver en');
      console.log('    uppsatt liggare och hoppas över.');
      ok('spärren slår till före pengar begärs', true);
      return;
    }

    // ⚠️ Samma skäl som ovan: utan avbrottet blir resten vakuöst grönt på undefined.
    if (!start.success || !start.paymentId) {
      console.error('\nAVBRYTER: ingen betalning skapades — resten hade mätt undefined mot undefined.');
      fail++; failures.push('förutsättning: StartPayment');
      return;
    }
    eq('på HELA sällskapets skuld', start.amount, 270);
    ok('referensen bär betalningsnumret',
       String(start.reference || '').includes(String(start.paymentId)), start.reference);
    ok('referensen håller Swish-gränsen', (start.reference || '').length <= 50, start.reference);

    // ⚠️ Idempotensen: ett andra klick får inte skapa en andra skuld.
    const again = await json('/umbraco/surface/ClubEvent/StartPayment', { eventId: evId });
    eq('ett andra klick återanvänder samma betalning', again.paymentId, start.paymentId);

    // ⚠️ QR:en genereras på servern. Ett 404 här betyder att Swish-numret inte dög — och det
    // hade annars visat sig som en trasig bild långt senare.
    const qr = await page.evaluate(async url => {
      const r = await fetch(url, { credentials: 'same-origin' });
      return { status: r.status, type: r.headers.get('content-type'), size: (await r.blob()).size };
    }, `/umbraco/surface/ClubEvent/GetPaymentQr?eventId=${evId}&paymentId=${start.paymentId}`);
    eq('QR-koden genereras', qr.status, 200);
    ok('och är en PNG med innehåll', qr.type === 'image/png' && qr.size > 500, JSON.stringify(qr));

    s = await state();
    eq('en begäran är INTE en betalning', s.party.outstanding, 270);
    eq('och anmälan är fortfarande ogiltig', s.party.isPaidUp, false);

    // ── Claim ─────────────────────────────────────────────────────────────────────────────────
    section('Betalaren säger att hen betalat');
    const claim = await json('/umbraco/surface/ClubEvent/ClaimPayment',
      { eventId: evId, paymentId: start.paymentId });
    ok('påståendet registreras', claim.success, claim.message);

    s = await state();
    eq('skulden är borta — anmälan är giltig', s.party.outstanding, 0);
    eq('anmälan är giltig', s.party.isPaidUp, true);
    // ⚠️⚠️ KÄRNAN: ett påstående är INTE pengar. Går det här igenom som bekräftat är
    // avprickningslistan värdelös som kontroll.
    eq('men det är INTE pengar', s.party.confirmedPaid, 0);
    eq('och det syns att bekräftelse saknas', s.party.awaitingConfirmation, true);

    const dubbel = await json('/umbraco/surface/ClubEvent/ClaimPayment',
      { eventId: evId, paymentId: start.paymentId });
    ok('ett andra påstående avvisas', !dubbel.success, JSON.stringify(dubbel).slice(0, 120));

    // ── Arrangörens lista ─────────────────────────────────────────────────────────────────────
    section('Avprickningslistan');
    const list = await get(`/umbraco/surface/ClubEvent/GetPayments?eventId=${evId}`);
    ok('listan läses', list.success, list.message);
    eq('en förväntad betalning', list.expected, 1);
    eq('noll är avklarade', list.settled, 0);
    eq('och 270 är utestående', list.outstanding, 270);
    const row = (list.payments || [])[0];
    ok('raden visar påstått men inte bekräftat',
       row && row.claimed && !row.confirmed, JSON.stringify(row));

    // ── Confirm ───────────────────────────────────────────────────────────────────────────────
    section('Arrangören bekräftar');
    const conf = await json('/umbraco/surface/ClubEvent/ConfirmPayment',
      { eventId: evId, paymentId: start.paymentId });
    // ⚠️ Bokföringen KAN vägra (stängt räkenskapsår, saknad kontoroll). Det är inte ett fel i den
    // här koden, men det måste SYNAS — annars ser en obokförd betalning bekräftad ut.
    ok('betalningen bokförs', conf.success, conf.message);

    if (conf.success) {
      s = await state();
      eq('nu är det pengar', s.party.confirmedPaid, 270);
      eq('och inget väntar på bekräftelse', s.party.awaitingConfirmation, false);

      const list2 = await get(`/umbraco/surface/ClubEvent/GetPayments?eventId=${evId}`);
      eq('listan visar den som avklarad', list2.settled, 1);
      eq('och noll utestående', list2.outstanding, 0);
      ok('ett kvitto utfärdades', (list2.payments || [])[0]?.receiptId > 0,
         JSON.stringify((list2.payments || [])[0]));
    }

    ok('inga JS-fel', jsErrors.filter(e => !/ckeditor/.test(e)).length === 0, jsErrors.join(' | '));

  } finally {
    // ⚠️ Evenemangsnoden raderas; deltagarraderna följer INTE med (se event-guests-verify).
    //    Betalningsraderna ligger kvar i liggaren MED FLIT — en verifikationsliggare raderar
    //    ingenting. Kör efteråt:
    //      DELETE FROM dbo.ClubEventParticipant WHERE MemberName LIKE 'ZZB %';
    if (evId) {
      const r = await page.evaluate(async ([u, id]) => {
        const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
        const fd = new FormData();
        fd.append('eventId', id);
        fd.append('__RequestVerificationToken', tokEl ? tokEl.value : '');
        const res = await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' });
        return `${res.status} ${(await res.text()).slice(0, 80)}`;
      }, ['/umbraco/surface/Club/DeleteClubEvent', String(evId)]).catch(e => String(e));
      console.log(`\nStädning: evenemang ${evId} — ${r}`);
      console.log('⚠️ Betalningsrader ligger kvar i liggaren (avsiktligt). Deltagarrader: se huvudet.');
    }
    await browser.close();
  }

  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log(`  - ${f}`)); process.exitCode = 1; }
};

main();
