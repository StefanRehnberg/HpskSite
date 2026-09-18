// event-guests-verify.mjs — en skytt anmäler sig själv, sin fru och sin son.
//
// KÖR:  node hpsk-verify/event-guests-verify.mjs
//       node hpsk-verify/event-guests-verify.mjs --headed
//
// FÖRUTSÄTTNINGAR
//   • Dev-appen körs på http://localhost:18150 (--launch-profile "Umbraco.Web.UI").
//     ⚠️ ALDRIG `dotnet run --no-launch-profile` — det pekar på PROD-DB.
//   • `add-price-choice-to-club-event-participant.sql` är körd. Utan den faller varje anmälan
//     på ett saknat kolumnnamn i stället för på ett påstående, och det UNIKA INDEXET är
//     ofiltrerat — då ryms exakt EN gäst per evenemang och felet ser ut som en dubblettspärr.
//   • Doctype-egenskapen `eventPrices` finns på `clubSimpleEvent`.
//   • Inloggad som klubbadmin i Haaplinge GoAss (HPSK_USER/HPSK_PASS eller --headed).
//
// ⚠️ SVITEN SKRIVER: ett evenemang, en anmälan och fyra gäster — allt prefixat `ZZG `. Den bygger
//    sin egen fixtur i stället för att mäta befintlig data, eftersom platsgränsen och sällskapets
//    summa bara går att pröva på ett evenemang vars kapacitet och priser vi själva satt.
//
// ⚠️⚠️ STÄDNINGEN ÄR TVÅDELAD, och den andra halvan går INTE att göra härifrån.
//    Att radera evenemangsNODEN tar inte med sig deltagarRADERNA: `ClubEventParticipant` är
//    nycklad på `EventId` och har ingen koppling till Umbraco-noden. Raderna blir föräldralösa och
//    osynliga (varje läsväg går via ett evenemang som inte längre finns), så sviten kan inte se
//    dem — mätt: tre körningar lämnade 9 rader medan städningen rapporterade lyckat. KÖR EFTERÅT:
//      sqlcmd -S localhost\SQLEXPRESS -d Umbraco -E -C -b -Q "SET QUOTED_IDENTIFIER ON;
//        DELETE FROM dbo.ClubEventParticipant WHERE MemberName LIKE 'ZZG %';"
//
// DET SVITEN EGENTLIGEN PRÖVAR: att EN RAD ÄR EN PERSON. Tre påståenden bär den:
//    platserna räknas per rad, summan är 450 och inte 180, och varje person prickas av för sig.
//    Vore sällskapet en rad med "+2" skulle alla tre vara fel — och alla tre tyst.
//
// FÄLLOR SOM REDAN KOSTAT TID PÅ DEN HÄR YTAN
//   1. Ett antiforgery-avslag är ett TOMT 400. `r.json()` kastar då ett SyntaxError som gömmer
//      statuskoden — därför läses varje POST som text först.
//   2. `CreateClubEvent` lägger id:t i `data.id`, inte i `eventId`.
//   3. `GetSignupState` bygger `party` bara när `mine` finns. Ett påstående om sällskapet innan
//      medlemmen anmält sig själv mäter alltså ingenting.
//   4. Sista navigeringen får INTE vara en ren JSON-endpoint — den bär ingen antiforgery-token,
//      så städningens POST blir ett tomt 400 som `r.text()` sväljer. Sviten rapporterade
//      "städning klar" och lämnade kvar evenemanget plus tre gästrader i dev.
//
// A/B, mätt 2026-09-18: 5 av 48 faller när platsen/reservdelningen slutar räkna gästrader OCH
// avbokningens kaskad tas bort.
//   ⚠️ `seatsLeft` föll INTE under den mutationen, och det är inte en lucka i sviten:
//   `roster.Seated` räknar RADER medan reservdelningen räknar `seatsTaken`. Påståendet
//   "gästerna tar VAR SIN plats" mäter alltså att gästerna ÄR rader — det faller mot en mutation
//   som slutar skapa raderna, inte mot en som räknar dem fel. Två skilda mutationer krävs.

import { chromium } from 'playwright';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const HEADED = process.argv.includes('--headed');

const CLUB_ID = 2604;         // Haaplinge GoAss
const PREFIX = 'ZZG ';
const SEATS = 3;              // jag + två gäster fyller evenemanget exakt

let pass = 0, fail = 0;
const failures = [];

function ok(name, cond, detail) {
  if (cond) { pass++; console.log(`  ✓ ${name}`); }
  else {
    fail++; failures.push(name);
    console.log(`  ✗ ${name}${detail ? ` — ${detail}` : ''}`);
  }
}
function eq(name, actual, expected) {
  ok(name, JSON.stringify(actual) === JSON.stringify(expected),
     `fick ${JSON.stringify(actual)}, väntade ${JSON.stringify(expected)}`);
}
function section(t) { console.log(`\n== ${t}`); }

const day = n => new Date(Date.now() + n * 86400000).toISOString().slice(0, 10);

const PRICES = JSON.stringify([
  { Id: 'vuxen', Label: 'Vuxen', Amount: 180 },
  { Id: 'barn', Label: 'Barn 7-15', Amount: 90 },
  { Id: 'liten', Label: 'Under 7 ar', Amount: 0 },
]);

const main = async () => {
  const browser = await chromium.launch({ headless: !HEADED });
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();
  const jsErrors = [];
  page.on('pageerror', e => jsErrors.push(e.message));

  let evId = 0;

  try {
    // ── Inloggning ─────────────────────────────────────────────────────────────────────────────
    // ⚠️ ufprt-FÄLLAN: inloggningsformuläret bär ett dolt `ufprt`-fält. En fetch-post utan det
    // svarar 200 med inloggningssidan igen, utan cookie och utan felmeddelande — alltså exakt
    // som ett fel lösenord. Playwright klickar submit och får fältet gratis.
    if (process.env.HPSK_USER && process.env.HPSK_PASS) {
      await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
      await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER);
      await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS);
      // ⚠️ Första inloggningen efter en omstart kan ta långt över 30 s (Umbraco värmer upp), och
      // klickets egen navigeringsväntan kastar då TimeoutError mitt i en lyckad inloggning.
      // Klicket är gjort oavsett — vi väntar på cookien i stället, och avbrottet ovan fångar
      // ändå fallet att inloggningen verkligen misslyckades.
      await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
      // ⚠️ Vänta tills inloggningen LANDAT. En goto medan navigeringen är i luften avbryter den
      // med ERR_ABORTED, vilket läser som att sidan är trasig — den var bara upptagen.
      await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
    }
    // Samma skäl: första sidladdningen kan kollidera med en pågående navigering.
    for (let i = 0; i < 3; i++) {
      try { await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded', timeout: 120000 }); break; }
      catch { await page.waitForTimeout(3000); }
    }

    let loggedIn = await page.locator('#firearms-member-tab').count() > 0;
    if (!loggedIn && HEADED) {
      console.log('\nLogga in i fönstret. Sviten väntar upp till 3 minuter.');
      const deadline = Date.now() + 180000;
      while (!loggedIn && Date.now() < deadline) {
        await page.waitForTimeout(2000);
        if (!page.url().includes('/user-profile-page')) continue;
        loggedIn = await page.locator('#firearms-member-tab').count() > 0;
      }
    }
    if (!loggedIn) {
      console.error(
        '\nAVBRYTER: inte inloggad.\n' +
        '⚠️ Utan inloggning blir varje "nekas"-påstående nedan grönt på en åtkomstvägran som ' +
        'aldrig nådde funktionen — därför avbryter sviten i stället för att rapportera 0 fel.');
      process.exitCode = 1;
      return;
    }

    const api = async (url, fields) => page.evaluate(async ([u, f]) => {
      const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
      if (f) {
        const fd = new FormData();
        Object.keys(f).forEach(k => fd.append(k, f[k]));
        fd.append('__RequestVerificationToken', tokEl ? tokEl.value : '');
        const r = await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' });
        const t = await r.text();
        try { return JSON.parse(t); } catch { return { success: false, _status: r.status, _raw: t.slice(0, 200) }; }
      }
      const r = await fetch(u, { credentials: 'same-origin' });
      const t = await r.text();
      try { return JSON.parse(t); } catch { return { success: false, _status: r.status, _raw: t.slice(0, 200) }; }
    }, [url, fields || null]);

    const json = async (url, body) => page.evaluate(async ([u, b]) => {
      const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch(u, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'RequestVerificationToken': tokEl ? tokEl.value : '',
        },
        body: JSON.stringify(b),
        credentials: 'same-origin',
      });
      const t = await r.text();
      try { return JSON.parse(t); } catch { return { success: false, _status: r.status, _raw: t.slice(0, 200) }; }
    }, [url, body]);

    const state = async () => api(`/umbraco/surface/ClubEvent/GetSignupState?eventId=${evId}`);

    // ── Fixtur ────────────────────────────────────────────────────────────────────────────────
    section('Fixtur');

    const ev = await api('/umbraco/surface/Club/CreateClubEvent', {
      clubId: CLUB_ID, eventName: `${PREFIX}Sommarfest`, eventDate: `${day(7)} 15:00`,
      description: 'ZZG', venue: 'Klubbstugan', eventType: 'Socialt',
      contactPerson: '', contactEmail: '', contactPhone: '',
      registrationRequired: 'true', maxParticipants: SEATS, registrationUrl: '',
      isMandatory: 'false', lanevapenOffered: 'false',
      registrationDeadline: '', eventPrices: PRICES,
    });
    evId = (ev.data && ev.data.id) || 0;
    ok('evenemanget skapas', ev.success && evId > 0, ev.message);
    if (!evId) return;

    let s = await state();
    eq('tre prisrader lästes tillbaka', (s.event.prices || []).map(p => p.label),
       ['Vuxen', 'Barn 7-15', 'Under 7 ar']);
    eq('platserna är tre', s.event.maxParticipants, SEATS);
    // ⚠️ Kontrollprov: ett sällskap får inte finnas innan medlemmen anmält sig själv, annars
    // mäter varje påstående nedan något som fanns från början.
    eq('inget sällskap innan jag anmält mig', s.party, null);

    // ── Kärnan: Hugo, hans fru och hans son ───────────────────────────────────────────────────
    section('En rad är en person');

    const me = await json('/umbraco/surface/ClubEvent/SignUp', { eventId: evId, priceId: 'vuxen' });
    ok('jag anmäler mig själv', me.success, me.message);

    s = await state();
    eq('sällskapet är en person', s.party.people, 1);
    eq('summan är 180', s.party.total, 180);
    eq('en plats kvar räknas ännu inte bort för gästerna', s.counts.seatsLeft, SEATS - 1);

    const fru = await json('/umbraco/surface/ClubEvent/AddGuest',
      { eventId: evId, name: `${PREFIX}Karin`, priceId: 'vuxen' });
    ok('frun anmäls', fru.success, fru.message);

    const son = await json('/umbraco/surface/ClubEvent/AddGuest',
      { eventId: evId, name: `${PREFIX}Emil`, priceId: 'barn' });
    ok('sonen anmäls', son.success, son.message);

    s = await state();
    eq('sällskapet är TRE personer', s.party.people, 3);
    eq('summan är 450, inte 180', s.party.total, 450);
    eq('inget saknat pris', s.party.missingPrice, false);
    // ⚠️ DET HÄR ÄR PÅSTÅENDET SOM FALLER om gästerna inte är egna rader: tre personer tar tre
    // platser, och arrangören ställer fram tre stolar.
    eq('gästerna tar VAR SIN plats', s.counts.seatsLeft, 0);
    eq('tre anmälda totalt', s.counts.signedUp, 3);
    eq('gästernas etiketter följde med', s.party.guests.map(g => g.feeLabel), ['Vuxen', 'Barn 7-15']);

    // ── Platsgränsen splittrar sällskapet, synligt ────────────────────────────────────────────
    section('Platsgränsen');

    const extra = await json('/umbraco/surface/ClubEvent/AddGuest',
      { eventId: evId, name: `${PREFIX}Alva`, priceId: 'liten' });
    ok('en fjärde person får plats som reserv', extra.success, extra.message);
    eq('och får VETA att hon är reserv', extra.isReserve, true);

    s = await state();
    eq('sällskapet är fyra personer', s.party.people, 4);
    eq('summan räknar gratisraden som 0, inte som saknad', s.party.total, 450);
    eq('reserven är fortfarande inte saknat pris', s.party.missingPrice, false);
    eq('en reserv', s.counts.reserves, 1);

    // ── Vägranden ─────────────────────────────────────────────────────────────────────────────
    section('Vägranden');

    const dubbel = await json('/umbraco/surface/ClubEvent/AddGuest',
      { eventId: evId, name: `${PREFIX}Karin`, priceId: 'vuxen' });
    ok('samma namn två gånger vägras', !dubbel.success, `svarade ${JSON.stringify(dubbel)}`);
    ok('och säger vem det gällde', (dubbel.message || '').includes('Karin'), dubbel.message);

    const utanPris = await json('/umbraco/surface/ClubEvent/AddGuest',
      { eventId: evId, name: `${PREFIX}Namnlos`, priceId: '' });
    ok('gäst utan prisval vägras', !utanPris.success, `svarade ${JSON.stringify(utanPris)}`);
    ok('och räknar upp alternativen',
       (utanPris.message || '').includes('Vuxen') && (utanPris.message || '').includes('Barn 7-15'),
       utanPris.message);

    const utanNamn = await json('/umbraco/surface/ClubEvent/AddGuest',
      { eventId: evId, name: '   ', priceId: 'vuxen' });
    ok('gäst utan namn vägras', !utanNamn.success, `svarade ${JSON.stringify(utanNamn)}`);

    // ── Uppropet prickar av VARJE person ──────────────────────────────────────────────────────
    section('Uppropet');

    const roster = await api(`/umbraco/surface/ClubEvent/GetRoster?eventId=${evId}`);
    ok('uppropslistan läses', roster.success, roster.message);
    const guestRows = (roster.rows || []).filter(r => r.isGuest);
    eq('tre gäster på listan', guestRows.length, 3);
    // ⚠️ Varje gäst bär MemberId 0. Utan radens id kan uppropet inte peka ut EN av dem.
    eq('gästerna bär medlems-id 0', [...new Set(guestRows.map(r => r.memberId))], [0]);
    ok('men har var sitt radnummer', new Set(guestRows.map(r => r.id)).size === 3);
    ok('och vet vem de hör till',
       guestRows.every(r => r.guestOfMemberId && r.guestOfMemberId > 0),
       JSON.stringify(guestRows.map(r => r.guestOfMemberId)));

    const karin = guestRows.find(r => r.name === `${PREFIX}Karin`);
    const tick = await json('/umbraco/surface/ClubEvent/SetAttendance',
      { eventId: evId, participantId: karin.id, status: 'Present', note: null });
    ok('frun prickas av', tick.success, tick.message);

    const roster2 = await api(`/umbraco/surface/ClubEvent/GetRoster?eventId=${evId}`);
    const karin2 = (roster2.rows || []).find(r => r.id === karin.id);
    eq('och bara hon', karin2.attendanceStatus, 'Present');
    eq('sonen står kvar som ej registrerad',
       (roster2.rows || []).find(r => r.name === `${PREFIX}Emil`).attendanceStatus, null);
    // ⚠️ En gäst har inget konto att skanna QR-affischen med. Vore hon märkt självregistrerad
    // skulle ett Föreningsintyg vila på ett svagare bevis än det faktiskt är.
    eq('gästen är INTE självregistrerad', karin2.selfRegistered, false);

    // ── Avbokning ─────────────────────────────────────────────────────────────────────────────
    section('Avbokning');

    const emil = (roster2.rows || []).find(r => r.name === `${PREFIX}Emil`);
    const dropEmil = await json('/umbraco/surface/ClubEvent/CancelGuest',
      { eventId: evId, participantId: emil.id });
    ok('en enskild gäst kan avbokas', dropEmil.success, dropEmil.message);

    s = await state();
    eq('sällskapet är tre', s.party.people, 3);
    eq('summan tappade 90', s.party.total, 360);
    eq('och reserven fick platsen', s.counts.reserves, 0);

    // ⚠️ KÄRNAN I KASKADEN: avbokar jag mig själv får ingen gäst stå kvar utan ansvarig.
    const dropMe = await json('/umbraco/surface/ClubEvent/Cancel', { eventId: evId });
    ok('jag avbokar mig själv', dropMe.success, dropMe.message);
    ok('och beskedet säger att gästerna följde med',
       (dropMe.message || '').toLowerCase().includes('gäster'), dropMe.message);

    s = await state();
    eq('ingen är anmäld längre', s.counts.signedUp, 0);
    eq('och alla platser är lediga igen', s.counts.seatsLeft, SEATS);

    // ⚠️ Kontrollprov åt andra hållet: en gäst får inte gå att lägga till på en anmälan som
    // inte finns — då hänger hen på ingenting.
    const foraldralos = await json('/umbraco/surface/ClubEvent/AddGuest',
      { eventId: evId, name: `${PREFIX}Ingen`, priceId: 'vuxen' });
    ok('gäst utan egen anmälan vägras', !foraldralos.success, `svarade ${JSON.stringify(foraldralos)}`);
    ok('och säger vad man ska göra först',
       (foraldralos.message || '').toLowerCase().includes('anmäl dig själv'), foraldralos.message);

    // ── Inga JS-fel ───────────────────────────────────────────────────────────────────────────
    section('Körningen');
    ok('inga JS-fel under körningen',
       jsErrors.filter(e => !/ckeditor-duplicated-modules/.test(e)).length === 0,
       jsErrors.join(' | '));

  } finally {
    // ── Städning ──────────────────────────────────────────────────────────────────────────────
    // ⚠️ Deltagarraderna hänger på EventId och följer med noden.
    //
    // ⚠️⚠️ NAVIGERA TILLBAKA TILL EN RIKTIG SIDA FÖRST. Städningen postade tidigare från
    // vilken sida sviten råkade stå på, och en ren JSON-endpoint bär INGEN antiforgery-token —
    // resultatet blev ett TOMT 400 som `r.text()` svalde, så sviten rapporterade "städning klar"
    // och lämnade kvar evenemanget plus tre gästrader i dev. Samma fälla som lånevapensvitens
    // punkt 2. Städningen assertas därför också, i stället för att bara loggas.
    if (evId) {
      let del = '';
      try {
        await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded', timeout: 120000 });
        del = await page.evaluate(async ([u, id]) => {
          const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
          const fd = new FormData();
          fd.append('eventId', id);
          fd.append('__RequestVerificationToken', tokEl ? tokEl.value : '');
          const r = await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' });
          return `${r.status} ${(await r.text()).slice(0, 120)}`;
        }, ['/umbraco/surface/Club/DeleteClubEvent', String(evId)]);
      } catch (e) { del = String(e); }
      ok(`fixturen städas (evenemang ${evId})`, /"success":true/.test(del), del);
    }
    await browser.close();
  }

  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) {
    console.log('FALLERADE:');
    failures.forEach(f => console.log(`  - ${f}`));
    process.exitCode = 1;
  }
};

main();
