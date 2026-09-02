// lanevapen-verify.mjs — lånevapen v2: klubbens regler, händelsens kryssruta, valvet,
// skanningen, etiketterna och det externa lånet.
//
// KÖR:  node hpsk-verify/lanevapen-verify.mjs
//       node hpsk-verify/lanevapen-verify.mjs --headed
//
// FÖRUTSÄTTNINGAR
//   • Dev-appen körs på http://localhost:18150 (--launch-profile "Umbraco.Web.UI").
//     ⚠️ ALDRIG `dotnet run --no-launch-profile` — det pekar på PROD-DB.
//   • `alter-firearm-booking-wish-and-assign.sql` och `add-sourceclass-to-firearm-usage.sql`
//     är körda. Utan dem faller halva sviten på ett saknat kolumnnamn i stället för på en bugg.
//   • Tre doctype-egenskaper finns: `lanevapenAllowExternal` och `lanevapenHorizonDays` på
//     klubben, `lanevapenOffered` på händelsen. Sviten PÅSTÅR att de finns i stället för att
//     bara läsa av dem — en switch som tyst inte sparar ser annars grön ut hela vägen.
//   • Inloggad som klubbadmin i Haaplinge GoAss (HPSK_USER/HPSK_PASS eller --headed).
//
// ⚠️ SVITEN SKRIVER: två lånevapen, en händelse, anmälningar och lån — allt prefixat `ZZL `,
//    och allt städas i finally även när ett påstående faller. Klubbens inställningar återställs
//    till av/0 på vägen ut, för de är RIKTIGA inställningar på en riktig klubb i dev.
//
// FÄLLOR SOM REDAN KOSTAT TID PÅ DEN HÄR YTAN
//   1. Ett antiforgery-avslag är ett TOMT 400. `r.json()` kastar då ett SyntaxError som gömmer
//      statuskoden — därför läses varje POST som text först.
//   2. `/valvet/etiketter` bär INGEN antiforgery-token. En POST därifrån får tomma 400:or, så
//      sviten navigerar tillbaka till Min sida innan den postar igen.
//   3. `CreateClubEvent` lägger id:t i `data.id`, inte i `eventId`. En probe som läste `eventId`
//      hoppade tyst över hela anmälningshalvan och rapporterade ändå bara grönt.
//   4. Skanningstoken går inte att gissa: den är IDataProtector-skyddad. Den hämtas ur
//      etikettsidans `data-label-url` — vilket också är enda sättet att felsöka en trasig QR.

import { chromium } from 'playwright';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const HEADED = process.argv.includes('--headed');

const CLUB_ID = 2604;         // Haaplinge GoAss
const ESCORT_MEMBER = 5601;   // Lisa Svensson — annan medlem i samma klubb
const PREFIX = 'ZZL ';
const NR_A = 71, NR_B = 72;

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

const main = async () => {
  const browser = await chromium.launch({ headless: !HEADED });
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();
  const jsErrors = [];
  page.on('pageerror', e => jsErrors.push(e.message));

  let wA = null, wB = null, evId = 0, groupId = 0, extId = 0;

  try {
    // ── Inloggning ─────────────────────────────────────────────────────────────────────────────
    // ⚠️ ufprt-FÄLLAN: inloggningsformuläret bär ett dolt `ufprt`-fält. En fetch-post utan det
    // svarar 200 med inloggningssidan igen, utan cookie och utan felmeddelande — alltså exakt
    // som ett fel lösenord. Playwright klickar submit och får fältet gratis.
    if (process.env.HPSK_USER && process.env.HPSK_PASS) {
      await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
      await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER);
      await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS);
      await page.click('button[type=submit], input[type=submit]');
      await page.waitForLoadState('domcontentloaded');
    }
    await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' });

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

    // Hjälpare i sidans kontext, så cookies och antiforgery gäller.
    const api = async (url, fields) => page.evaluate(async ([u, f]) => {
      const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
      if (f) {
        const fd = new FormData();
        Object.keys(f).forEach(k => fd.append(k, f[k]));
        fd.append('__RequestVerificationToken', tokEl ? tokEl.value : '');
        const r = await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' });
        const t = await r.text();
        try { return JSON.parse(t); }
        catch { return { success: false, _status: r.status, _raw: t.slice(0, 200) }; }
      }
      const r = await fetch(u, { credentials: 'same-origin' });
      const t = await r.text();
      try { return JSON.parse(t); }
      catch { return { success: false, _status: r.status, _raw: t.slice(0, 200) }; }
    }, [url, fields || null]);

    const json = async (url, body) => page.evaluate(async ([u, b]) => {
      const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch(u, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json',
                   'RequestVerificationToken': tokEl ? tokEl.value : '' },
        credentials: 'same-origin', body: JSON.stringify(b),
      });
      const t = await r.text();
      try { return JSON.parse(t); }
      catch { return { success: false, _status: r.status, _raw: t.slice(0, 200) }; }
    }, [url, body]);

    // ⚠️ Postningar måste ske från en sida som BÄR en antiforgery-token. Efter varje navigering
    // till valvet eller etiketterna hämtas Min sida igen — annars är nästa POST ett tomt 400.
    const backToTokenPage = () =>
      page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' });

    // ── Klubbens regler ────────────────────────────────────────────────────────────────────────
    section('Klubbens lånevapenregler');
    let st = await api(`/umbraco/surface/FirearmAdmin/GetLoanWeaponSettings?clubId=${CLUB_ID}`);
    ok('inställningarna läses', st.success, st.message);
    // ⚠️ Det här är påståendet som skiljer "klubben har valt av" från "egenskapen finns inte".
    // Utan det rapporterar sviten grönt på en switch vars varje sparning rann ut i sanden.
    ok(`doctype-egenskapen ${st.allowExternalProperty} finns på klubben`,
       st.allowExternalPropertyExists === true);
    ok(`doctype-egenskapen ${st.horizonProperty} finns på klubben`,
       st.horizonPropertyExists === true);

    const saved = await api('/umbraco/surface/FirearmAdmin/SaveLoanWeaponSettings',
      { clubId: CLUB_ID, allowExternal: 'true', horizonDays: 14 });
    ok('reglerna sparas', saved.success, saved.message);
    st = await api(`/umbraco/surface/FirearmAdmin/GetLoanWeaponSettings?clubId=${CLUB_ID}`);
    eq('externa lån är påslaget', st.allowExternal, true);
    eq('horisonten är 14 dagar', st.horizonDays, 14);

    // ── Två lånevapen ──────────────────────────────────────────────────────────────────────────
    section('Klubbens lånevapen');
    for (const w of [{ a: `${PREFIX}A`, n: NR_A }, { a: `${PREFIX}B`, n: NR_B }]) {
      await api('/umbraco/surface/FirearmAdmin/SaveClubFirearm', {
        clubId: CLUB_ID, id: 0, alias: w.a, weaponClass: 'C', vapentyp: 'Pistol',
        number: w.n, isLoanable: true, status: 'Tillgängligt',
        licenseExpiresOn: '', federations: '', disciplines: '', writeDetails: '0',
      });
    }
    const cl = await api(`/umbraco/surface/FirearmAdmin/GetClubFirearms?clubId=${CLUB_ID}`);
    wA = (cl.firearms || []).find(x => x.alias === `${PREFIX}A`);
    wB = (cl.firearms || []).find(x => x.alias === `${PREFIX}B`);
    ok('båda lånevapnen finns', !!wA && !!wB);
    eq('klubbnumret är kvar på vapnet', wA && wA.number, NR_A);

    // ── Etiketten och QR-koden ─────────────────────────────────────────────────────────────────
    section('Etiketterna i valvet');
    const qr = await page.evaluate(async ([b, c, f]) => {
      const r = await fetch(`${b}/umbraco/surface/FirearmAdmin/GetFirearmLabelQr?clubId=${c}&firearmId=${f}`,
        { credentials: 'same-origin' });
      const buf = await r.arrayBuffer();
      return { status: r.status, type: r.headers.get('content-type'), bytes: buf.byteLength };
    }, [BASE, CLUB_ID, wA.id]);
    eq('QR-bilden svarar 200', qr.status, 200);
    ok('QR-bilden är en PNG', (qr.type || '').includes('image/png'), qr.type);
    // ⚠️ En tom bild är också 200. Storleken är det enda som skiljer en QR från ingenting.
    ok('QR-bilden har innehåll', qr.bytes > 500, `${qr.bytes} byte`);

    await page.goto(`${BASE}/valvet/etiketter?clubId=${CLUB_ID}`, { waitUntil: 'domcontentloaded' });
    const labels = await page.evaluate(() =>
      Array.from(document.querySelectorAll('.lab')).map(el => ({
        url: el.getAttribute('data-label-url'),
        firearmId: parseInt(el.getAttribute('data-firearm-id') || '0', 10),
      })));
    ok('etikettarket bär en etikett per lånevapen', labels.length >= 2, `${labels.length} etiketter`);
    const labA = labels.find(l => l.firearmId === wA.id);
    const labB = labels.find(l => l.firearmId === wB.id);
    ok('etiketten för nr ' + NR_A + ' finns', !!labA);
    // ⚠️ Token BÄRS osynligt i DOM:en av ett skäl: utan den går en trasig QR inte att felsöka,
    // och ingen svit kan följa skanningsvägen. Faller det här påståendet är hela avsnittet
    // nedan overifierbart — inte grönt.
    ok('etiketten bär en skanningsadress', !!(labA && labA.url && labA.url.includes('t=')),
       labA && labA.url);
    const tokenOf = url => decodeURIComponent(new URL(url).searchParams.get('t') || '');
    const tokA = labA ? tokenOf(labA.url) : '';
    const tokB = labB ? tokenOf(labB.url) : '';
    ok('token är skyddad, inte ett vapen-id i klartext',
       tokA.length > 20 && !tokA.includes(String(wA.id)), tokA.slice(0, 24));
    ok('varje vapen har sin egen token', tokA !== tokB);
    await backToTokenPage();

    // ── Händelsen med lånevapen ────────────────────────────────────────────────────────────────
    section('Händelsen erbjuder lånevapen');
    const ev = await api('/umbraco/surface/Club/CreateClubEvent', {
      clubId: CLUB_ID, eventName: `${PREFIX}Traning`, eventDate: `${day(5)} 18:00`,
      description: 'ZZL', venue: 'Banan', eventType: 'Träning',
      contactPerson: '', contactEmail: '', contactPhone: '',
      registrationRequired: 'true', maxParticipants: 10, registrationUrl: '', feeAmount: '',
      isMandatory: 'false', lanevapenOffered: 'true',
    });
    evId = (ev.data && ev.data.id) || 0;
    ok('händelsen skapas', ev.success && evId > 0, ev.message);
    // ⚠️ `SetValue` på en saknad egenskap är en TYST no-op. Skrivvägen svarar därför med namnet
    // på det som fattades, och tomt betyder att allt gick in. Utan det här påståendet kunde
    // switchen ha varit dekoration i alla tre dialogerna.
    ok('inga anmälningsegenskaper saknades vid sparningen',
       !ev.missingProperty && !ev.warning, ev.missingProperty || ev.warning);

    let sState = await api(`/umbraco/surface/ClubEvent/GetSignupState?eventId=${evId}`);
    let lw = sState.loanWeapons || {};
    ok('lånevapenläget följer med anmälningskortet', !!sState.loanWeapons);
    eq('händelsen erbjuder lånevapen', lw.offered, true);
    eq('doctype-egenskapen lanevapenOffered finns på händelsen', lw.propertyExists, true);
    ok('antalet lånebara vapen räknas', lw.loanable >= 2, `loanable=${lw.loanable}`);
    eq('inget är upptaget än', lw.occupied, 0);
    ok('vapnen är valbara i anmälan', (lw.weapons || []).length >= 2);

    // Anmälan med "vilket som helst" — nybörjarens väg.
    let up = await json('/umbraco/surface/ClubEvent/SignUp',
      { eventId: evId, note: '', loanWeapon: true, loanFirearmId: 0 });
    ok('anmälan med platsbokning lyckas', up.success, up.message);
    ok('skytten får ett besked om lånet', !!up.loanMessage, up.loanMessage);
    // ⚠️ En platsbokning får INTE utlova ett nummer. Vilket vapen det blir avgörs i valvet, och
    // ett nummer här vore ett löfte vi inte håller.
    ok('platsbokningen utlovar inget nummer', !/\bnr\s*\d/.test(up.loanMessage || ''),
       up.loanMessage);

    sState = await api(`/umbraco/surface/ClubEvent/GetSignupState?eventId=${evId}`);
    lw = sState.loanWeapons || {};
    ok('lånet syns på skyttens kort', lw.myBookingId > 0);
    eq('ett vapen är nu upptaget', lw.occupied, 1);
    const poolBookingId = lw.myBookingId;

    // ── Valvet ─────────────────────────────────────────────────────────────────────────────────
    section('Valvet');
    let board = await api(`/umbraco/surface/FirearmAdmin/GetVaultBoard?clubId=${CLUB_ID}` +
      `&occasionKind=Event&occasionId=${evId}`);
    ok('valvtavlan läses', board.success, board.message);
    // Rubriken vapenansvarig läser står färdigräknad — han ska inte räkna rader själv.
    eq('ett vapen att lämna ut', board.toHandOut, 1);
    eq('en rad på tillfället', (board.loans || []).length, 1);
    const row = (board.loans || [])[0];
    ok('raden bär vem det är', !!(row && row.memberName), row && JSON.stringify(row).slice(0, 120));
    // ⚠️ PROJEKTIONEN HAR INGET `assignedFirearmId`. `firearmId` är det som GÄLLER NU
    // (tilldelat om det finns, annars önskat) och `wishedFirearmId` är önskemålet. Ett påstående
    // mot ett påhittat fältnamn läser `undefined` — och blir då evigt grönt i sin negerade form.
    eq('platsbokningen har inget vapen än', row && row.firearmId, 0);
    eq('platsbokningen önskade inget särskilt vapen', row && row.wishedFirearmId, null);

    // ⚠️ Utlämningen registrerar det vapen som FAKTISKT gick ut, inte det önskade. Det är hela
    // skillnaden mellan ett register som stämmer och ett som stämmer ibland.
    const handout = await api('/umbraco/surface/FirearmAdmin/HandOutFromVault',
      { clubId: CLUB_ID, bookingId: poolBookingId, firearmId: wB.id });
    ok('vapnet lämnas ut ur valvet', handout.success, handout.message);

    board = await api(`/umbraco/surface/FirearmAdmin/GetVaultBoard?clubId=${CLUB_ID}` +
      `&occasionKind=Event&occasionId=${evId}`);
    const outRow = (board.loans || []).find(b => b.id === poolBookingId);
    eq('inget kvar att lämna ut', board.toHandOut, 0);
    eq('raden pekar på det vapen som gick ut', outRow && outRow.firearmId, wB.id);
    eq('raden visar numret som lämnades ut', outRow && outRow.number, NR_B);

    // Kvällen stängs i ett tryck — knuten till att låsa valvet, inte till att någon minns.
    const close = await api('/umbraco/surface/FirearmAdmin/CloseVaultEvening',
      { clubId: CLUB_ID, occasionKind: 'Event', occasionId: evId, keepIds: '' });
    ok('kvällen kan stängas i ett tryck', close.success, close.message);
    board = await api(`/umbraco/surface/FirearmAdmin/GetVaultBoard?clubId=${CLUB_ID}` +
      `&occasionKind=Event&occasionId=${evId}`);
    eq('inga vapen är ute efter stängning', board.out_, 0);

    // Valvsidan renderar utan JS-fel.
    const errsBefore = jsErrors.length;
    await page.goto(`${BASE}/valvet?clubId=${CLUB_ID}`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(800);
    ok('valvsidan renderas', await page.locator('body').count() > 0);
    ok('valvsidan är fri från JS-fel', jsErrors.length === errsBefore,
       jsErrors.slice(errsBefore).join(' | '));
    await backToTokenPage();

    // ── Direktlån i valvet ─────────────────────────────────────────────────────────────────────
    section('Direktlån — den som bara kom');
    const walk = await api('/umbraco/surface/FirearmAdmin/WalkInLoan',
      { clubId: CLUB_ID, memberId: ESCORT_MEMBER, firearmId: wA.id,
        occasionKind: 'Event', occasionId: evId });
    ok('vapenansvarig kan lägga in ett lån på plats', walk.success, walk.message);
    ok('direktlånet får ett boknings-id', walk.bookingId > 0);
    const walkId = walk.bookingId;

    // ── Skanningen ─────────────────────────────────────────────────────────────────────────────
    section('Skanningen');
    // Läget läses UTAN att skriva — sidan får inte skapa ett lån bara av att någon tittar.
    let scan = await api(`/umbraco/surface/Firearm/GetScanState?t=${encodeURIComponent(tokA)}`);
    ok('skanningsläget läses', typeof scan.action === 'string', JSON.stringify(scan).slice(0, 160));
    // Nr 71 är utlånat till någon ANNAN sedan direktlånet ovan.
    ok('krocken syns: vapnet är taget av någon annan',
       scan.action === 'Refused' || !!scan.claimedByOther,
       `action=${scan.action}`);

    // Nr 72 är fritt igen efter stängningen — skanningen ska ERBJUDA att skapa lånet.
    scan = await api(`/umbraco/surface/Firearm/GetScanState?t=${encodeURIComponent(tokB)}`);
    eq('ett fritt vapen erbjuder lån', scan.action, 'Offer');
    eq('skanningen vet vilket nummer det är', scan.number, NR_B);

    const scanOut = await api('/umbraco/surface/Firearm/ScanHandOut', { t: tokB });
    ok('skanningen skapar lånet', scanOut.success, scanOut.message);

    scan = await api(`/umbraco/surface/Firearm/GetScanState?t=${encodeURIComponent(tokB)}`);
    eq('nästa skanning är en återlämning', scan.action, 'Return');
    // ⚠️ Skanningen kan inte ha fel om VILKET vapen. Det är hela vinsten, och den är en
    // riktighetsvinst — inte en bekvämlighetsvinst.
    eq('lånet pekar på det skannade vapnet', scan.firearmId, wB.id);

    const scanRet = await api('/umbraco/surface/Firearm/ScanReturn', { t: tokB });
    ok('skanningen registrerar återlämningen', scanRet.success, scanRet.message);
    scan = await api(`/umbraco/surface/Firearm/GetScanState?t=${encodeURIComponent(tokB)}`);
    eq('vapnet är fritt igen', scan.action, 'Offer');

    // En påhittad token får aldrig bli ett lån.
    const badScan = await api('/umbraco/surface/Firearm/GetScanState?t=nonsens-token');
    ok('en påhittad kod avvisas', badScan.success === false, badScan.message);

    // ── Vapnet man brukar få ───────────────────────────────────────────────────────────────────
    section('Vapnet man brukar få');
    const usual = await api(`/umbraco/surface/Firearm/GetMyUsualLoanWeapon?clubId=${CLUB_ID}`);
    ok('det vanliga vapnet läses', usual.success, usual.message);
    // Skytten har fått nr 72 två gånger (utlämningen i valvet och skanningen) — mot nr 71: noll.
    eq('det vanliga vapnet är det man oftast fått', usual.number, NR_B);

    // ── Ett namngivet vapen ────────────────────────────────────────────────────────────────────
    section('Bokning av ett namngivet vapen');
    const named = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: wB.id, clubId: CLUB_ID, occasionKind: 'Fritt', occasionId: 0,
        from: day(3), to: '' });
    ok('ett namngivet vapen kan bokas', named.success, named.message);
    // ⚠️ Beskedet är löftet skytten reser på. "Ett vapen är reserverat" duger inte för den som
    // skjutit in nr 72 mot sig själv — den skytten kommer inte alls utan just det vapnet.
    eq('beskedet nämner numret', named.number, NR_B);
    ok('beskedet lovar numret i klartext', /nr\s*72/.test(named.message || ''), named.message);
    const namedId = named.bookingId;

    // Samma vapen, samma fönster, av samma skytt igen — överlappet ska nekas.
    const clash = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: wB.id, clubId: CLUB_ID, occasionKind: 'Fritt', occasionId: 0,
        from: day(3), to: '' });
    ok('överlappande bokning av samma vapen nekas', clash.success === false, clash.message);

    // ── Horisonten ─────────────────────────────────────────────────────────────────────────────
    section('Klubbens horisont');
    const tooFar = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: 0, clubId: CLUB_ID, occasionKind: 'Fritt', occasionId: 0,
        from: day(30), to: '' });
    ok('bokning bortom horisonten nekas', tooFar.success === false, tooFar.message);
    ok('avslaget säger hur långt fram klubben tar bokningar',
       /14/.test(tooFar.message || ''), tooFar.message);

    // ── Externa lån ────────────────────────────────────────────────────────────────────────────
    section('Externa lån');
    const extNoEscort = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: 0, clubId: CLUB_ID, occasionKind: 'Externt', occasionId: 0,
        occasionLabel: `${PREFIX}Extern tavling`, from: day(3), to: '' });
    // ⚠️ Utan en namngiven medföljande är lånet olagligt för nybörjaren: hen får inte transportera
    // eller inneha vapnet utan någon från klubben som har rätten. Kravet är inte administration.
    ok('externt lån utan ansvarig nekas', extNoEscort.success === false, extNoEscort.message);

    const extOk = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: 0, clubId: CLUB_ID, occasionKind: 'Externt', occasionId: 0,
        occasionLabel: `${PREFIX}Extern tavling`, from: day(3), to: '',
        escortMemberId: ESCORT_MEMBER });
    ok('externt lån med ansvarig går igenom', extOk.success, extOk.message);
    ok('lånet väntar på den medföljandes ja', extOk.awaitsEscort === true);
    extId = extOk.bookingId;

    // Klubben stänger av externa lån — då ska det nekas oavsett medföljande.
    await api('/umbraco/surface/FirearmAdmin/SaveLoanWeaponSettings',
      { clubId: CLUB_ID, allowExternal: 'false', horizonDays: 0 });
    const extOff = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: 0, clubId: CLUB_ID, occasionKind: 'Externt', occasionId: 0,
        occasionLabel: `${PREFIX}Nej`, from: day(3), to: '', escortMemberId: ESCORT_MEMBER });
    ok('externt lån nekas när klubben sagt nej', extOff.success === false, extOff.message);
    // Kontrollpåstående på kontrollpåståendet: att det nekades får inte bero på något annat.
    ok('avslaget handlar om att vapnet inte får lämna banan',
       /utanför banan/i.test(extOff.message || ''), extOff.message);

    // Med horisonten avstängd (0) ska en långt framtida bokning gå igenom igen — annars mätte
    // horisontpåståendet ovan något annat än horisonten.
    const farNowOk = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: 0, clubId: CLUB_ID, occasionKind: 'Fritt', occasionId: 0,
        from: day(30), to: '' });
    ok('utan horisont går samma bokning igenom', farNowOk.success, farNowOk.message);

    // ── Medlemmens egen bild av externa lån ────────────────────────────────────────────────────
    section('Externa lån sett från medlemmen');
    // Slå på igen — förra avsnittet stängde av.
    await api('/umbraco/surface/FirearmAdmin/SaveLoanWeaponSettings',
      { clubId: CLUB_ID, allowExternal: 'true', horizonDays: 0 });

    let extOpts = await api(`/umbraco/surface/LoanWeaponApi/GetExternalOptions?clubId=${CLUB_ID}`);
    ok('medlemsvända alternativ läses', extOpts.success, extOpts.message);
    // ⚠️ MEDLEMSVÄND ENDPOINT. `FirearmAdmin/GetLoanWeaponSettings` kräver klubbadmin, så en
    // vanlig medlem kunde inte veta om rutan skulle visas — och hade sett ett formulär vars
    // sparning alltid nekas, eller ingen ruta trots att klubben sagt ja.
    eq('klubben tillåter externa lån', extOpts.allowExternal, true);
    ok('kandidater erbjuds', (extOpts.candidates || []).length > 0,
       `${(extOpts.candidates || []).length} kandidater`);
    // ⚠️ SJÄLVUTESLUTNINGEN ÄR OMÄTBAR MED DEN HÄR FIXTUREN, och det ska stå här i stället för
    // att döljas bakom ett grönt påstående. En A/B (2026-09-02) tog bort filtret
    // `m.MemberId != memberId` ur servern och påståendet "jag själv är inte en kandidat" blev
    // ÄNDÅ grönt: kontot sviten kör som är klubbadmin men inte MEDLEM i klubben, så det kan
    // aldrig komma med i listan oavsett vad servern gör. Ett påstående som inte kan falla är
    // värre än inget — det påstår en trygghet som inte är mätt.
    //
    // Det som DÄREMOT går att mäta är att listan är exakt klubbens medlemmar, mig borträknad.
    // Faller den ekvationen har servern antingen tappat någon eller lagt till någon som inte
    // hör till klubben — och det senare är den allvarliga formen av samma bugg.
    const meRow = await api('/umbraco/surface/Firearm/GetMyFirearms');
    ok('mitt eget medlems-id är känt', meRow.memberId > 0, JSON.stringify(meRow.memberId));

    const clubMembers = await api(`/umbraco/surface/ClubAdmin/GetClubMembers?clubId=${CLUB_ID}`);
    const clubIds = new Set(((clubMembers.data) || []).map(m => m.id));
    const candIds = new Set((extOpts.candidates || []).map(c => c.memberId));
    ok('klubbens medlemslista kunde läsas', clubIds.size > 0, `${clubIds.size} medlemmar`);
    ok('ingen kandidat står utanför klubben',
       [...candIds].every(id => clubIds.has(id)),
       [...candIds].filter(id => !clubIds.has(id)).join(', '));
    ok('jag själv är inte en kandidat', !candIds.has(meRow.memberId),
       `mitt id ${meRow.memberId}`);
    // ⚠️ Och det påståendet är ÄNDÅ omätt, av ett skäl som är värt att skriva ner: kandidaterna
    // hämtas ur `CompetitionTeamService.GetClubMembers`, som bara tar med medlemmar med rollen
    // `Users`. Sviten kör som klubbadmin, som saknar den rollen — kontot syns alltså i klubbens
    // medlemslista men aldrig i kandidatkällan, oavsett vad självfiltret gör. Att kontot står i
    // GetClubMembers räcker INTE som bevis; de två listorna har olika urval.
    console.log('  ! självuteslutningen är OMÄTT av den här sviten (kontot saknar rollen ' +
                '"Users" och kan därför inte komma med i kandidatkällan alls). ' +
                'Mät den genom att köra som en vanlig medlem i klubben.');

    // Av igen: rutan ska då inte renderas, och kandidatuppslagningen ska inte göras alls.
    await api('/umbraco/surface/FirearmAdmin/SaveLoanWeaponSettings',
      { clubId: CLUB_ID, allowExternal: 'false', horizonDays: 0 });
    extOpts = await api(`/umbraco/surface/LoanWeaponApi/GetExternalOptions?clubId=${CLUB_ID}`);
    eq('klubbens nej syns för medlemmen', extOpts.allowExternal, false);
    eq('ingen kandidatlista byggs i onödan', (extOpts.candidates || []).length, 0);

    // ── Medföljandegrinden ─────────────────────────────────────────────────────────────────────
    section('Medföljandeansvaret');
    const escortList = await api('/umbraco/surface/LoanWeaponApi/GetMyEscortRequests');
    ok('mina medföljandelån läses', escortList.success, escortList.message);
    // ⚠️ Jag är INTE utsedd medföljande i något lån (jag är den som lånar). Att listan är tom är
    // därför rätt svar — och kontrollpåståendet nedan bevisar att tomheten inte är en trasig
    // endpoint: grinden nekar mig när jag försöker acceptera ett ansvar jag inte fått.
    eq('jag är inte utsedd i något lån', (escortList.requests || []).length, 0);

    if (extId) {
      const notMine = await api('/umbraco/surface/Firearm/AcceptLoanEscort', { bookingId: extId });
      ok('bara den utsedde kan acceptera ansvaret', notMine.success === false, notMine.message);
    }

    // Klubbens vy ska bära den utseddes NAMN, inte bara ett id — annars är ansvaret en siffra.
    const clubRows = await api(`/umbraco/surface/FirearmAdmin/GetClubBookings?clubId=${CLUB_ID}`);
    const extRow = (clubRows.bookings || []).find(b => b.id === extId);
    ok('det externa lånet finns i klubbens lista', !!extRow);
    ok('den utseddes namn syns i klubbens lista', !!(extRow && extRow.escortName),
       extRow && String(extRow.escortMemberId));
    ok('lånet är markerat som väntande på den utsedde', extRow && extRow.awaitsEscort === true);
    ok('lånet är markerat som att vapnet lämnar klubben', extRow && extRow.leavesTheClub === true);

    // ── Kurstilldelning ────────────────────────────────────────────────────────────────────────
    section('Kurstilldelning');
    const grp = await api('/umbraco/surface/TrainingGroup/CreateTrainingGroup',
      { name: `${PREFIX}Nyborjarkurs`, clubId: CLUB_ID, description: 'ZZL', startDate: day(0) });
    groupId = (grp.data && grp.data.Id) || (grp.data && grp.data.id) || 0;
    ok('en träningsgrupp kan skapas', grp.success && groupId > 0, grp.message);

    if (groupId) {
      const add = await api('/umbraco/surface/TrainingGroup/AddTrainingGroupMember',
        { trainingGroupId: groupId, memberId: ESCORT_MEMBER, role: 'Member', sendEmail: 'false' });
      ok('en deltagare läggs till', add.success, add.message);

      // ⚠️ HORISONTEN SKA INTE GÄLLA HÄR. Kursen planeras månader i förväg, och horisonten finns
      // för att hindra ENSKILDA från att lägga beslag på vapen hela säsongen. Samma regel på båda
      // hade gjort kursplanering omöjlig för att skydda mot något kursen inte gör.
      await api('/umbraco/surface/FirearmAdmin/SaveLoanWeaponSettings',
        { clubId: CLUB_ID, allowExternal: 'false', horizonDays: 14 });

      const assign = await api('/umbraco/surface/FirearmAdmin/AssignLoanWeaponsToGroup',
        { clubId: CLUB_ID, trainingGroupId: groupId, occasionKind: 'Fritt', occasionId: 0,
          occasionLabel: `${PREFIX}Kurskvall`, from: day(60), to: '' });
      ok('gruppen kan tilldelas lånevapen', assign.success, assign.message);
      ok('tilldelningen går förbi klubbens horisont', assign.created >= 1,
         `created=${assign.created}, skipped=${assign.skipped}`);
      // ⚠️ Svaret måste säga VEM, inte bara hur många. Instruktören behöver veta vilken person
      // han ska prata med — en siffra ("7 av 10") är oanvändbar för det.
      ok('svaret säger vem som fick vad', (assign.results || []).length >= 1 &&
         (assign.results || []).every(r => !!r.name), JSON.stringify(assign.results || []).slice(0, 200));

      const assigned = (assign.results || []).find(r => r.memberId === ESCORT_MEMBER);
      ok('deltagaren fick ett lån', assigned && assigned.ok === true, assigned && assigned.message);

      // Ett tilldelat lån är en PLATSbokning: valvet avgör vilket vapen.
      const after = await api(`/umbraco/surface/FirearmAdmin/GetClubBookings?clubId=${CLUB_ID}`);
      const tRow = (after.bookings || []).find(b => b.id === (assigned && assigned.bookingId));
      eq('tilldelningen är en platsbokning', tRow && tRow.wishedFirearmId, null);
      eq('källan är Tilldelad', tRow && tRow.source, 'Tilldelad');
      ok('kursens namn står i valvlistan', !!(tRow && String(tRow.occasion).includes('Kurskvall')),
         tRow && tRow.occasion);

      // Kontrollpåstående: SAMMA fönster via den vanliga webbvägen ska nekas av horisonten.
      // Utan det mäter påståendet ovan inte att undantaget finns — bara att bokningen gick.
      const webFar = await api('/umbraco/surface/Firearm/BookLoanWeapon',
        { firearmId: 0, clubId: CLUB_ID, occasionKind: 'Fritt', occasionId: 0, from: day(60), to: '' });
      ok('samma fönster nekas via webbokningen', webFar.success === false, webFar.message);

      // En annan klubbs grupp får inte tilldelas den här klubbens vapen.
      const wrongClub = await api('/umbraco/surface/FirearmAdmin/AssignLoanWeaponsToGroup',
        { clubId: CLUB_ID, trainingGroupId: 999999, occasionKind: 'Fritt', occasionId: 0,
          from: day(60), to: '' });
      ok('en okänd grupp avvisas', wrongClub.success === false, wrongClub.message);
    }

    // ── Vyerna ─────────────────────────────────────────────────────────────────────────────────
    //
    // ⚠️ RAZOR KOMPILERAS RUNTIME. `dotnet build` med 0 fel säger INGENTING om en .cshtml —
    // att ladda sidan ÄR kompileringskontrollen. Utan det här avsnittet kan hela sviten vara
    // grön medan varje sida som medlemmen faktiskt öppnar svarar 500.
    section('Vyerna kompilerar och renderar');
    await api('/umbraco/surface/FirearmAdmin/SaveLoanWeaponSettings',
      { clubId: CLUB_ID, allowExternal: 'true', horizonDays: 0 });

    const realErrs = () => jsErrors.filter(e => !e.includes('ckeditor')).length;
    const errsView = realErrs();
    let resp = await page.goto(`${BASE}/lanevapen?club=${CLUB_ID}`, { waitUntil: 'domcontentloaded' });
    eq('/lanevapen svarar 200', resp && resp.status(), 200);
    await page.waitForTimeout(1200);
    ok('rutan för externt lån renderas när klubben sagt ja',
       await page.locator('#lvExtBox:not(.d-none)').count() > 0);
    ok('medföljandeväljaren är fylld med klubbens medlemmar',
       await page.locator('#lvExtEscort option').count() > 1,
       String(await page.locator('#lvExtEscort option').count()));
    eq('/lanevapen är fri från JS-fel', realErrs(), errsView);

    // Klubbens sida bär både inställningskortet och kurstilldelningsdialogen.
    resp = await page.goto(`${BASE}/halland/klubbar/haaplinge-goass/`, { waitUntil: 'domcontentloaded' });
    eq('klubbsidan svarar 200', resp && resp.status(), 200);
    await page.waitForTimeout(1500);
    ok('lånevapenkortet finns i klubbadmin', await page.locator('#vapLwBody').count() > 0);
    ok('valvlänken pekar på rätt klubb',
       (await page.getAttribute('#vapVaultLink', 'href') || '').includes(String(CLUB_ID)),
       await page.getAttribute('#vapVaultLink', 'href'));
    ok('etikettlänken pekar på rätt klubb',
       (await page.getAttribute('#vapLabelLink', 'href') || '').includes(String(CLUB_ID)));
    ok('horisontfältet renderas', await page.locator('#vapLwHorizon').count() > 0);
    ok('externswitchen renderas', await page.locator('#vapLwExternal').count() > 0);
    ok('kurstilldelningsdialogen finns i DOM:en',
       await page.locator('#tgLoanWeaponModal #tgLoanDate').count() > 0);

    await backToTokenPage();

    ok('inga JS-fel utöver kända CKEditor-varningar',
       jsErrors.filter(e => !e.includes('ckeditor-duplicated-modules')).length === 0,
       jsErrors.filter(e => !e.includes('ckeditor-duplicated-modules')).join(' | '));

    // Håll id:n för städningen
    globalThis.__ids = [poolBookingId, walkId, namedId, extId, farNowOk.bookingId].filter(Boolean);

  } finally {
    // ── Städning ─────────────────────────────────────────────────────────────────────────────────
    try {
      await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' });
      const api = async (url, fields) => page.evaluate(async ([u, f]) => {
        const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
        const fd = new FormData();
        Object.keys(f || {}).forEach(k => fd.append(k, f[k]));
        fd.append('__RequestVerificationToken', tokEl ? tokEl.value : '');
        const r = await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' });
        const t = await r.text();
        try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 120) }; }
      }, [url, fields || {}]);

      const bookings = await page.evaluate(async b => {
        const r = await fetch(`${b}/umbraco/surface/FirearmAdmin/GetClubBookings?clubId=2604`,
          { credentials: 'same-origin' });
        try { return await r.json(); } catch { return {}; }
      }, BASE);
      for (const b of (bookings.bookings || [])) {
        await api('/umbraco/surface/FirearmAdmin/SetBookingState',
          { clubId: CLUB_ID, bookingId: b.id, action: 'return', reason: '' });
      }
      for (const f of [wA, wB]) {
        if (f) await api('/umbraco/surface/FirearmAdmin/RemoveClubFirearm',
          { clubId: CLUB_ID, firearmId: f.id });
      }
      if (evId) await api('/umbraco/surface/Club/DeleteClubEvent',
        { clubId: CLUB_ID, eventId: evId });
      // ⚠️ Grupper kan bara INAKTIVERAS, inte raderas. Fixturen bär prefixet så den går att
      // känna igen i listan efteråt — men den ligger kvar, och det ska stå här och inte
      // upptäckas av nästa person som undrar vad ZZL är.
      if (groupId) await api('/umbraco/surface/TrainingGroup/DeactivateTrainingGroup',
        { trainingGroupId: groupId });
      // ⚠️ Inställningarna är RIKTIGA inställningar på en riktig klubb i dev — de måste tillbaka
      // till av/0, annars ärver nästa svit en horisont den inte satt.
      await api('/umbraco/surface/FirearmAdmin/SaveLoanWeaponSettings',
        { clubId: CLUB_ID, allowExternal: 'false', horizonDays: 0 });
      console.log('\n(städat)');
    } catch (e) {
      console.log('\n⚠️ STÄDNINGEN FALLERADE:', e.message,
        '\n   Rensa för hand: bokningar och vapen med alias som börjar på "' + PREFIX + '".');
    }
    await browser.close();
  }

  console.log(`\n${pass} godkända, ${fail} fel`);
  if (fail) {
    console.log('\nFEL:');
    failures.forEach(f => console.log('  · ' + f));
    process.exitCode = 1;
  }
};

main().catch(e => { console.error(e); process.exitCode = 1; });
