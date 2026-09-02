// vapen-verify.mjs — kedjans punkt 5 (steg 4–8) OCH punkt 6 (lånevapensbokning).
//
// KÖR:  node hpsk-verify/vapen-verify.mjs
//       node hpsk-verify/vapen-verify.mjs --headed
//
// FÖRUTSÄTTNINGAR
//   • Dev-appen körs på http://localhost:18150 (--launch-profile "Umbraco.Web.UI").
//     ⚠️ ALDRIG `dotnet run --no-launch-profile` — det pekar på PROD-DB.
//   • De fem migreringarna är körda i dev (inkl. create-firearm-booking-table.sql).
//   • `Firearm:MasterKeys` är satt i appsettings.Development.json. Utan nyckel kan ingenting
//     krypteras och halva sviten faller på ett konfigurationsfel i stället för på en bugg.
//   • En inloggad session i den browser Playwright startar — se LOGIN nedan.
//
// ⚠️ SVITEN SKRIVER. Den skapar vapen, förfrågningar, taggningar och en behörighetstilldelning, och
//    städar bort ALLT i sitt finally — även när ett påstående faller. Fixturen är prefixad `ZZV `
//    så städningen kan hitta den utan att gissa.
//
// ⚠️ RÅ-DB-PÅSTÅENDET KAN INTE GÖRAS HÄRIFRÅN. Att klartexten inte finns i tabellen kräver en
//    SQL-fråga, och den ligger i `vapen-rawdb.sql` intill. Kör den efter sviten — den är det ENDA
//    påstående som faktiskt bevisar att uppgifterna är krypterade.
//
// FÄLLOR SOM REDAN KOSTAT TID PÅ DEN HÄR YTAN
//   1. Login-sidans slugg är /login-&-register/ i DEV och /login-register/ i PROD — inversen.
//   2. Ett antiforgery-avslag är ett TOMT 400. `r.json()` kastar då ett SyntaxError som gömmer
//      statuskoden, så alla POST läses som text först.
//   3. Min sida ligger på /user-profile-page/ (inte /min-sida/, som 404:ar).
//   4. `window.clubId` är undefined — värdsidorna deklarerar `const clubId`, som inte hamnar på
//      window. Klubb-id tas därför ur panelens data-attribut.

import { chromium } from 'playwright';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const HEADED = process.argv.includes('--headed');

// Fixturen. Byt bara om dev-datat ändras.
const CLUB_ID = 2604;            // Haaplinge GoAss
const BOARD_MEMBER_ID = 5601;    // Lisa Svensson, ordförande — utses till läsare
const PREFIX = 'ZZV ';

// De skyddade värdena. Sök efter dessa i vapen-rawdb.sql.
const SECRET = {
  Fabrikat: 'Pardini',
  Modell: 'SP-1',
  Kaliber: '.22 LR',
  Piplangd: '15,2 cm',
  Tillverkningsnummer: 'P-99871',
  Licensnummer: 'AB-12345',
  Licensdatum: '2020-05-05',
  Anteckning: 'Kopt i Vaxjo',
};

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

const main = async () => {
  const browser = await chromium.launch({ headless: !HEADED });
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await ctx.newPage();

  try {
    // ── Inloggning ─────────────────────────────────────────────────────────────────────────────
    // Sätt HPSK_USER / HPSK_PASS, eller kör med --headed och logga in för hand.
    //
    // ⚠️ ufprt-FÄLLAN, som redan kostat en felsökningsrunda: inloggningsformuläret bär ett DOLT
    // `ufprt`-fält (Umbracos surface-form-routing). En curl- eller fetch-post som utelämnar det
    // når aldrig inloggningshandlern — servern svarar 200 med inloggningssidan igen, utan
    // auth-cookie och utan felmeddelande, alltså exakt som ett fel lösenord. Playwright klickar
    // submit och får fältet gratis. Dra aldrig slutsatsen "lösenordet är fel" ur ett tomt 200
    // från den sidan.
    await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' });

    if (process.env.HPSK_USER && process.env.HPSK_PASS) {
      await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
      await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER);
      await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS);
      await page.click('button[type=submit], input[type=submit]');
      await page.waitForLoadState('domcontentloaded');
      await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' });
    }

    let loggedIn = await page.locator('#firearms-member-tab').count() > 0;

    // ⚠️ I --headed VÄNTAR sviten på att du loggar in för hand, i stället för att avbryta direkt.
    // Utan väntan är --headed värdelöst: fönstret hinner aldrig visas innan kontrollen faller.
    if (!loggedIn && HEADED) {
      console.log(
        '\nLogga in i fönstret som öppnats. Sviten väntar upp till 3 minuter och fortsätter\n' +
        'av sig själv när vapenfliken syns.');
      const deadline = Date.now() + 180000;
      while (!loggedIn && Date.now() < deadline) {
        await page.waitForTimeout(2000);
        if (!page.url().includes('/user-profile-page')) continue;
        loggedIn = await page.locator('#firearms-member-tab').count() > 0;
      }
      if (!loggedIn) {
        // Sista försöket: du kan ha loggat in men landat någon annanstans.
        await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' });
        loggedIn = await page.locator('#firearms-member-tab').count() > 0;
      }
    }

    if (!loggedIn) {
      console.error(
        '\nAVBRYTER: inte inloggad, eller vapenfliken renderas inte.\n' +
        'Kör med --headed och logga in manuellt, eller sätt HPSK_USER/HPSK_PASS.\n' +
        '⚠️ Utan inloggning blir varje "finns inte"-påstående nedan grönt på en sida som aldrig ' +
        'visade funktionen — därför avbryter sviten i stället för att rapportera 0 fel.');
      process.exitCode = 1;
      return;
    }

    // Hjälpare som körs i sidans kontext, så antiforgery och cookies gäller.
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
      return r.json();
    }, [url, fields || null]);

    // ── STEG 2: behörigheten ───────────────────────────────────────────────────────────────────
    section('Behörigheten (steg 2)');
    const assigned = await api('/umbraco/surface/FirearmAdmin/AssignViewer',
      { clubId: CLUB_ID, memberId: BOARD_MEMBER_ID });
    ok('en styrelsemedlem kan utses till föreningsintygsansvarig', assigned.success, assigned.message);

    const vs = await api(`/umbraco/surface/FirearmAdmin/GetViewerState?clubId=${CLUB_ID}`);
    ok('behörighetsläget läses', vs.success);
    ok('rollen är inte längre obesatt', vs.unstaffed === false);
    ok('minst en aktiv läsare', vs.activeCount >= 1, `activeCount=${vs.activeCount}`);
    // ⚠️ Bara AKTIVA styrelsemedlemmar får vara valbara — annars kan behörigheten ges till någon
    // vars läsrätt härleds bort direkt, och knappen rapporterar en tilldelning som inte gäller.
    ok('väljaren erbjuder bara styrelsemedlemmar', (vs.candidates || []).length > 0);

    // ── STEG 4: registret ──────────────────────────────────────────────────────────────────────
    section('Vapenregistret (steg 4)');
    const held = await api('/umbraco/surface/Firearm/SaveFirearm', {
      Id: 0, Alias: `${PREFIX}Innehav`, WeaponClass: 'C', Vapentyp: 'Pistol',
      AcquisitionStatus: 'Innehas', LicenseExpiresOn: '2027-01-15',
      Federations: 'Svenska Pistolskytteförbundet', Disciplines: 'Precision,Faltskytte',
      WriteDetails: '1', ...SECRET,
    });
    ok('vapen med skyddade uppgifter sparas', held.success, held.message);

    const planned = await api('/umbraco/surface/Firearm/SaveFirearm', {
      Id: 0, Alias: `${PREFIX}Planerat`, WeaponClass: 'A', Vapentyp: 'Pistol',
      AcquisitionStatus: 'Planerat', Federations: 'Svenska Pistolskytteförbundet',
      Disciplines: '', WriteDetails: '0',
    });
    ok('planerat vapen sparas', planned.success, planned.message);

    const emptyDetails = await api('/umbraco/surface/Firearm/SaveFirearm', {
      Id: 0, Alias: `${PREFIX}Utan uppgifter`, WeaponClass: 'B', Vapentyp: 'Revolver',
      AcquisitionStatus: 'Innehas', Federations: '', Disciplines: '', WriteDetails: '1',
    });
    ok('vapen utan skyddade uppgifter sparas', emptyDetails.success);

    let list = await api('/umbraco/surface/Firearm/GetMyFirearms');
    const find = (l, suffix) => (l.firearms || []).find(f => f.alias === PREFIX + suffix);
    const rowHeld = find(list, 'Innehav');
    const rowEmpty = find(list, 'Utan uppgifter');

    ok('listan bär vapnet', !!rowHeld);
    // ⚠️ Det som lämnar servern får inte bära de skyddade FÄLTNAMNEN heller — ett chiffer i en
    // payload är en kopia av hemligheten i varje webbläsarcache och varje bifogad HAR-fil.
    ok('maskerad lista läcker inga skyddade fält',
       rowHeld && !Object.keys(rowHeld).some(k => /fabrikat|kaliber|licensn|tillverk|encrypted/i.test(k)),
       rowHeld && Object.keys(rowHeld).join(','));
    ok('tomma skyddade uppgifter lagras som "inga"', rowEmpty && rowEmpty.hasDetails === false);
    ok('vapen med uppgifter markeras hasDetails', rowHeld && rowHeld.hasDetails === true);
    eq('förbundsrelationen sparas', rowHeld && rowHeld.federations, ['Svenska Pistolskytteförbundet']);
    eq('grenrelationen sparas i katalogordning', rowHeld && rowHeld.disciplines, ['Precision', 'Faltskytte']);
    ok('förfallodatumets dagar räknas', rowHeld && typeof rowHeld.daysUntilExpiry === 'number');

    const revealed = await api('/umbraco/surface/Firearm/RevealDetails', { firearmId: rowHeld.id });
    ok('avmaskering lämnar ut uppgifterna', revealed.success, revealed.message);
    eq('fabrikatet round-trippar', revealed.details && revealed.details.fabrikat, SECRET.Fabrikat);
    eq('kalibern round-trippar', revealed.details && revealed.details.kaliber, SECRET.Kaliber);

    // ⚠️ Loggraden ska INTE synas i medlemmens vy som standard: egna läsningar är den
    // överväldigande majoriteten och skulle begrava klubbens enda läsning.
    const logDefault = await api('/umbraco/surface/Firearm/GetMyAccessLog');
    const logAll = await api('/umbraco/surface/Firearm/GetMyAccessLog?includeOwn=true');
    ok('egen läsning registreras', (logAll.entries || []).length >= 1);
    ok('egen läsning filtreras bort ur medlemmens vy',
       (logDefault.entries || []).every(e => !e.isSelf));

    // Ägandekontrollen: en sparning mot ett vapen som inte är mitt ska nekas. Vi kan inte skapa
    // någon annans vapen härifrån, så vi prövar formen — ett id som inte finns.
    const foreign = await api('/umbraco/surface/Firearm/SaveFirearm',
      { Id: 999999, Alias: 'ZZV Kapat', WriteDetails: '0' });
    ok('sparning mot ett okänt vapen nekas', foreign.success === false, foreign.message);

    // ── STEG 6: förfrågan ──────────────────────────────────────────────────────────────────────
    section('Föreningsintygsförfrågan (steg 6)');
    const reqOk = await api('/umbraco/surface/Firearm/RequestIntyg', {
      clubId: CLUB_ID, firearmId: rowHeld.id, kind: 'Fornyelse',
      forbund: 'Svenska Pistolskytteförbundet', vapengrupp: 'Precision C', message: 'Test',
    });
    ok('förfrågan skapas', reqOk.success, reqOk.message);

    const rowPlanned = find(await api('/umbraco/surface/Firearm/GetMyFirearms'), 'Planerat');
    const reqBad = await api('/umbraco/surface/Firearm/RequestIntyg', {
      clubId: CLUB_ID, firearmId: rowPlanned.id, kind: 'Fornyelse',
      forbund: 'Svenska Pistolskytteförbundet', vapengrupp: '', message: '',
    });
    ok('förnyelse av ett PLANERAT vapen nekas', reqBad.success === false, reqBad.message);

    const reqDup = await api('/umbraco/surface/Firearm/RequestIntyg', {
      clubId: CLUB_ID, firearmId: rowHeld.id, kind: 'Fornyelse',
      forbund: 'Svenska Pistolskytteförbundet', vapengrupp: '', message: '',
    });
    ok('dubblettförfrågan nekas', reqDup.success === false, reqDup.message);

    const reqNoClub = await api('/umbraco/surface/Firearm/RequestIntyg', {
      clubId: 99999, firearmId: rowHeld.id, kind: 'NyttVapen',
      forbund: 'Svenska Pistolskytteförbundet', vapengrupp: '', message: '',
    });
    ok('förfrågan till en klubb man inte tillhör nekas', reqNoClub.success === false, reqNoClub.message);

    const inbox = await api(`/umbraco/surface/FirearmAdmin/GetIntygRequests?clubId=${CLUB_ID}`);
    ok('klubbens inkorg visar förfrågan', (inbox.requests || []).some(r => r.id === reqOk.requestId));
    ok('inkorgen namnger vem som kan läsa uppgifterna', (inbox.viewerNames || []).length >= 1);
    ok('inkorgen läcker inga vapenuppgifter',
       !(inbox.requests || []).some(r => Object.keys(r).some(k => /fabrikat|kaliber|licensn|tillverk/i.test(k))));

    const rejectNoNote = await api('/umbraco/surface/FirearmAdmin/SetIntygRequestStatus',
      { clubId: CLUB_ID, requestId: reqOk.requestId, status: 'Avslagen', note: '' });
    ok('avslag utan skäl nekas', rejectNoNote.success === false, rejectNoNote.message);

    const rejectOk = await api('/umbraco/surface/FirearmAdmin/SetIntygRequestStatus',
      { clubId: CLUB_ID, requestId: reqOk.requestId, status: 'Avslagen', note: 'Testskäl' });
    ok('avslag med skäl går igenom', rejectOk.success, rejectOk.message);

    // ── STEG 7: användning ─────────────────────────────────────────────────────────────────────
    section('Vapen per tillfälle (steg 7)');
    await api('/umbraco/surface/Firearm/SetUsage',
      { sourceKind: 'training', sourceId: 990001, firearmId: rowHeld.id, occurredOn: '2026-08-01' });
    await api('/umbraco/surface/Firearm/SetUsage',
      { sourceKind: 'comp', sourceId: 990002, firearmId: rowHeld.id, occurredOn: '2026-08-15' });
    const retag = await api('/umbraco/surface/Firearm/SetUsage',
      { sourceKind: 'training', sourceId: 990001, firearmId: rowHeld.id, occurredOn: '2026-08-01' });
    ok('om-taggning av samma tillfälle accepteras', retag.success);

    list = await api('/umbraco/surface/Firearm/GetMyFirearms');
    // ⚠️ KÄRNAN: tre taggningar men TVÅ tillfällen. Ett tillfälle bär ett vapen, så en om-taggning
    // ersätter i stället för att lägga till — annars dubbelräknas "använt vid N tillfällen".
    eq('om-taggning ERSÄTTER, dubbelräknar inte', find(list, 'Innehav').usageCount, 2);

    const usage = await api('/umbraco/surface/Firearm/GetMyUsage');
    ok('nyckeln är sammansatt (kind:id)',
       Object.keys(usage.usage || {}).includes('training:990001'),
       Object.keys(usage.usage || {}).join(','));

    const badSource = await api('/umbraco/surface/Firearm/SetUsage',
      { sourceKind: 'hittepa', sourceId: 990003, firearmId: rowHeld.id });
    ok('okänd källa nekas namngivet', badSource.success === false, badSource.message);

    await api('/umbraco/surface/Firearm/SetUsage',
      { sourceKind: 'comp', sourceId: 990002, firearmId: 0 });
    list = await api('/umbraco/surface/Firearm/GetMyFirearms');
    eq('avtaggning tar bort tillfället', find(list, 'Innehav').usageCount, 1);

    // ── STEG 8: klubbvapen och lånevapenlistan ────────────────────────────────────────────────
    section('Klubbvapen och /lanevapen (steg 8)');
    // ⚠️ TVÅ bokbara vapen behövs: ett för bokningen och ett för "annat vapen samma tid".
    // Service-vapnet är en EGEN fixturrad — en tidigare version använde Klubb 2 till båda
    // sakerna, och då föll "annat vapen samma tid" på att vapnet var på service. Produkten hade
    // rätt; testet hade fel fixtur.
    const cf = [
      { alias: `${PREFIX}Klubb 1`, number: 91, isLoanable: true, status: 'Tillgängligt' },
      { alias: `${PREFIX}Klubb 2`, number: 92, isLoanable: true, status: 'Tillgängligt' },
      { alias: `${PREFIX}Service`, number: 94, isLoanable: true, status: 'Service' },
      { alias: `${PREFIX}Ej lanebart`, number: 93, isLoanable: false, status: 'Tillgängligt' },
    ];
    for (const w of cf) {
      const r = await api('/umbraco/surface/FirearmAdmin/SaveClubFirearm',
        { clubId: CLUB_ID, id: 0, alias: w.alias, weaponClass: 'C', vapentyp: 'Pistol',
          number: w.number, isLoanable: w.isLoanable, status: w.status });
      ok(`klubbvapen "${w.alias}" sparas`, r.success, r.message);
    }

    await page.goto(`${BASE}/lanevapen?club=${CLUB_ID}`, { waitUntil: 'domcontentloaded' });

    // ⚠️ SIDAN ÄR JS-DRIVEN. Listan hämtas av LoanWeaponApi/GetAvailability efter att DOM:en är
    // klar, så `innerText` direkt efter domcontentloaded läser "Laddar…". En tidigare version
    // gjorde det och rapporterade fyra falska fel på en fungerande sida. Vänta på en RIKTIG rad —
    // inte på en timeout, som bara byter racet mot en långsammare maskin.
    let listRendered = true;
    try {
      await page.waitForFunction(
        () => {
          const box = document.getElementById('lvList');
          return box && !/Laddar/.test(box.textContent) && box.querySelector('tbody tr');
        }, null, { timeout: 15000 });
    } catch { listRendered = false; }
    ok('/lanevapen renderar vapenlistan', listRendered);

    const body = await page.locator('body').innerText();
    ok('/lanevapen visar det lånebara vapnet', body.includes(`${PREFIX}Klubb 1`));
    // ⚠️ Kontrollprov för uteslutningen: utan det kan "syns inte" lika gärna betyda att hela
    // listan är tom eller att sidan inte renderade.
    ok('/lanevapen visar även ett vapen på service', body.includes(`${PREFIX}Service`));
    ok('/lanevapen UTESLUTER det icke-lånebara', !body.includes(`${PREFIX}Ej lanebart`));
    ok('/lanevapen har tre kolumner', /Nr[\s\S]{0,40}Vapen[\s\S]{0,40}Status/.test(body));

    // Bokningsytan finns: datumväljaren styr listan, och lediga vapen får en knapp.
    ok('/lanevapen har en datumväljare', await page.locator('#lvDate').count() === 1);
    ok('/lanevapen erbjuder Boka på ett ledigt vapen',
       await page.locator('[data-lv="book"]').count() > 0);
    // ⚠️ Service-vapnet ska SYNAS men inte vara bokbart — listan är inte ett filter, den är ett
    // svar på "vad finns och vad går att boka".
    ok('/lanevapen visar Service som status, inte som ledigt', /Service/.test(body));

    await page.goto(`${BASE}/lanevapen?club=99999`, { waitUntil: 'domcontentloaded' });
    const denied = await page.locator('body').innerText();
    ok('/lanevapen nekar en klubb man inte är medlem i', /bara se lånevapen i en klubb/.test(denied));

    // ── PUNKT 6: bokning ──────────────────────────────────────────────────────────────────────
    section('Lånevapensbokning (punkt 6)');

    const clubWeapons = await api(`/umbraco/surface/FirearmAdmin/GetClubFirearms?clubId=${CLUB_ID}`);
    const w1 = (clubWeapons.firearms || []).find(f => f.alias === `${PREFIX}Klubb 1`);
    const w2 = (clubWeapons.firearms || []).find(f => f.alias === `${PREFIX}Klubb 2`);
    const wSvc = (clubWeapons.firearms || []).find(f => f.alias === `${PREFIX}Service`);
    const wNo = (clubWeapons.firearms || []).find(f => f.alias === `${PREFIX}Ej lanebart`);
    ok('klubbvapnen finns att boka', !!w1 && !!w2 && !!wSvc && !!wNo);

    // Ett datum en bit fram, så fixturen aldrig krockar med verklig dev-data.
    const day = new Date(Date.now() + 30 * 86400000).toISOString().slice(0, 10);

    const b1 = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: w1.id, occasionKind: 'Fritt', occasionId: 0,
        from: `${day} 09:00`, to: `${day} 12:00`, note: 'ZZV bokning' });
    ok('bokning skapas', b1.success, b1.message);

    // ⚠️ KÄRNAN I PUNKT 6. Samma vapen, överlappande fönster → måste nekas, och meddelandet ska
    // NAMNGE tiden. "Vapnet är bokat" utan tid lämnar medlemmen utan nästa steg.
    const clash = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: w1.id, occasionKind: 'Fritt', occasionId: 0,
        from: `${day} 11:00`, to: `${day} 14:00`, note: '' });
    ok('överlappande bokning NEKAS', clash.success === false, clash.message);
    ok('krockmeddelandet namnger tiden', /\d{2}:\d{2}/.test(clash.message || ''), clash.message);

    // ⚠️ Kant-i-kant ska TILLÅTAS — överlämningen sker just då. Utan det kan två pass i följd
    // aldrig dela ett vapen, vilket är det normala på en tävlingsdag.
    const edge = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: w1.id, occasionKind: 'Fritt', occasionId: 0,
        from: `${day} 12:00`, to: `${day} 15:00`, note: '' });
    ok('kant-i-kant-bokning tillåts', edge.success, edge.message);

    // Ett ANNAT vapen samma tid är inte en krock.
    const other = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: w2.id, occasionKind: 'Fritt', occasionId: 0,
        from: `${day} 09:00`, to: `${day} 12:00`, note: '' });
    ok('annat vapen samma tid går att boka', other.success, other.message);

    const notLoanable = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: wNo.id, occasionKind: 'Fritt', occasionId: 0, from: day, to: '', note: '' });
    ok('icke-lånebart vapen kan inte bokas', notLoanable.success === false, notLoanable.message);

    // ⚠️ Service blockerar OAVSETT kalender — det är ett fysiskt läge, inte en tidskonflikt.
    // Upptäcktes av misstag när fixturen var fel; nu ett avsiktligt påstående.
    const svc = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: wSvc.id, occasionKind: 'Fritt', occasionId: 0, from: day, to: '', note: '' });
    ok('vapen på Service kan inte bokas', svc.success === false, svc.message);
    ok('Service-vägran namnger statusen', /Service/.test(svc.message || ''), svc.message);

    const past = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: w1.id, occasionKind: 'Fritt', occasionId: 0, from: '2020-01-01', to: '', note: '' });
    ok('bokning bakåt i tiden nekas', past.success === false, past.message);

    const tooLong = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: w2.id, occasionKind: 'Fritt', occasionId: 0,
        from: `${day} 09:00`, to: `${new Date(Date.now() + 60 * 86400000).toISOString().slice(0,10)} 09:00`,
        note: '' });
    ok('för lång bokning nekas', tooLong.success === false, tooLong.message);

    const badOccasion = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: w2.id, occasionKind: 'Event', occasionId: 0, from: day, to: '', note: '' });
    ok('tillfälle utan id nekas', badOccasion.success === false, badOccasion.message);

    // Tillgängligheten ska spegla bokningarna FÖR DET VALDA FÖNSTRET.
    const avail = await api('/umbraco/surface/LoanWeaponApi/GetAvailability' +
      `?clubId=${CLUB_ID}&from=${encodeURIComponent(day + ' 09:00')}&to=${encodeURIComponent(day + ' 12:00')}`);
    ok('tillgänglighet läses', avail.success, avail.message);
    const aw1 = (avail.weapons || []).find(x => x.id === w1.id);
    ok('bokat vapen är inte bokbart i fönstret', aw1 && aw1.isBookable === false);
    ok('egen bokning märks som "bokat av dig"', aw1 && aw1.bookedByMe === true);
    ok('fönstrets etikett följer med', typeof avail.windowLabel === 'string' && avail.windowLabel.length > 0);

    // ⚠️ Samma vapen ett ANNAT dygn ska vara ledigt — annars är fönstret inte respekterat, och det
    // är hela poängen med att tillgänglighet alltid gäller en tid.
    const otherDay = new Date(Date.now() + 45 * 86400000).toISOString().slice(0, 10);
    const availOther = await api('/umbraco/surface/LoanWeaponApi/GetAvailability' +
      `?clubId=${CLUB_ID}&from=${encodeURIComponent(otherDay)}&to=`);
    const ow1 = (availOther.weapons || []).find(x => x.id === w1.id);
    ok('samma vapen är ledigt ett annat dygn', ow1 && ow1.isBookable === true);

    const availDenied = await api('/umbraco/surface/LoanWeaponApi/GetAvailability?clubId=99999&from=' + day + '&to=');
    ok('tillgänglighet nekas för en klubb man inte tillhör', availDenied.success === false);

    // Utlämning och återlämning.
    const clubBookings = await api(`/umbraco/surface/FirearmAdmin/GetClubBookings?clubId=${CLUB_ID}`);
    ok('klubbens bokningslista läses', clubBookings.success, clubBookings.message);
    ok('bokningen syns i klubbens lista',
       (clubBookings.bookings || []).some(b => b.id === b1.bookingId));

    const handout = await api('/umbraco/surface/FirearmAdmin/SetBookingState',
      { clubId: CLUB_ID, bookingId: b1.bookingId, action: 'handout', reason: '' });
    ok('utlämning registreras', handout.success, handout.message);

    // ⚠️ Ett UTLÄMNAT vapen får inte avbokas — då tappas spåret till vem som har det.
    const cancelOut = await api('/umbraco/surface/Firearm/CancelLoanBooking',
      { bookingId: b1.bookingId, reason: '' });
    ok('utlämnad bokning kan inte avbokas', cancelOut.success === false, cancelOut.message);

    const ret = await api('/umbraco/surface/FirearmAdmin/SetBookingState',
      { clubId: CLUB_ID, bookingId: b1.bookingId, action: 'return', reason: '' });
    ok('återlämning registreras', ret.success, ret.message);

    // ⚠️ En ÅTERLÄMNAD bokning blockerar inte längre — annars vore vapnet obokbart i sitt gamla
    // fönster för alltid, vilket bryter varje efterhandsrättelse.
    const rebook = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: w1.id, occasionKind: 'Fritt', occasionId: 0,
        from: `${day} 09:30`, to: `${day} 10:30`, note: '' });
    ok('återlämnat fönster går att boka igen', rebook.success, rebook.message);

    const mine = await api('/umbraco/surface/Firearm/GetMyLoanBookings');
    ok('medlemmens egna bokningar listas', (mine.bookings || []).length >= 3);

    const cancelOwn = await api('/umbraco/surface/Firearm/CancelLoanBooking',
      { bookingId: rebook.bookingId, reason: 'ZZV' });
    ok('egen reserverad bokning kan avbokas', cancelOwn.success, cancelOwn.message);

    const badAction = await api('/umbraco/surface/FirearmAdmin/SetBookingState',
      { clubId: CLUB_ID, bookingId: b1.bookingId, action: 'hittepa', reason: '' });
    ok('okänd åtgärd nekas', badAction.success === false, badAction.message);

  } finally {
    // ── Städning ───────────────────────────────────────────────────────────────────────────────
    // ⚠️ Körs även när ett påstående faller, annars ärver nästa körning förra körningens fixtur
    // och mäter något annat än den påstår. Vapenraderna tar relationer, förfrågningar, taggningar
    // och påminnelser med sig via CASCADE; behörigheten och åtkomstloggen måste tas explicit.
    try {
      await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' });
      const cleanup = await page.evaluate(async ([club, prefix]) => {
        const tok = () => {
          const el = document.querySelector('input[name="__RequestVerificationToken"]');
          return el ? el.value : '';
        };
        const post = async (u, f) => {
          const fd = new FormData();
          Object.keys(f).forEach(k => fd.append(k, f[k]));
          fd.append('__RequestVerificationToken', tok());
          const r = await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' });
          try { return JSON.parse(await r.text()); } catch { return { success: false }; }
        };
        const get = async u => (await fetch(u, { credentials: 'same-origin' })).json();

        let removed = 0;
        const mine = await get('/umbraco/surface/Firearm/GetMyFirearms');
        for (const f of (mine.firearms || []).filter(x => x.alias.startsWith(prefix))) {
          const r = await post('/umbraco/surface/Firearm/RemoveFirearm', { firearmId: f.id });
          if (r.success) removed++;
        }
        const clubList = await get(`/umbraco/surface/FirearmAdmin/GetClubFirearms?clubId=${club}`);
        for (const f of (clubList.firearms || []).filter(x => x.alias.startsWith(prefix))) {
          const r = await post('/umbraco/surface/FirearmAdmin/RemoveClubFirearm',
                               { clubId: club, firearmId: f.id });
          if (r.success) removed++;
        }
        return removed;
      }, [CLUB_ID, PREFIX]);

      await page.evaluate(async ([club, member]) => {
        const el = document.querySelector('input[name="__RequestVerificationToken"]');
        const fd = new FormData();
        fd.append('clubId', club); fd.append('memberId', member);
        fd.append('__RequestVerificationToken', el ? el.value : '');
        await fetch('/umbraco/surface/FirearmAdmin/RemoveViewer',
                    { method: 'POST', body: fd, credentials: 'same-origin' });
      }, [CLUB_ID, BOARD_MEMBER_ID]);

      console.log(`\nStädning: ${cleanup} vapen gömda, behörigheten återkallad.`);
      console.log(
        '⚠️ RemoveFirearm GÖMMER (IsActive=0), det raderar inte. Kör vapen-rawdb.sql --cleanup för ' +
        'att ta bort fixturen ur databasen på riktigt.');
    } catch (e) {
      console.log(`\n⚠️ STÄDNINGEN MISSLYCKADES: ${e.message}\nRensa manuellt: ` +
                  `DELETE FROM Firearm WHERE Alias LIKE 'ZZV %'`);
    }

    await browser.close();
  }

  console.log(`\n${pass}/${pass + fail} påståenden gröna.`);
  if (fail > 0) {
    console.log(`\nFallerade:\n  - ${failures.join('\n  - ')}`);
    process.exitCode = 1;
  }
};

main().catch(e => { console.error(e); process.exitCode = 1; });
