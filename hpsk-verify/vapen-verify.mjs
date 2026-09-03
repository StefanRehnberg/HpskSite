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

    // ── MAGNUMKLASSERNA I VAPENGRUPPVÄLJAREN (2026-09-02) ─────────────────────────────────────
    // ⚠️ M1–M9 är INTE kompetensnivåer som C1/C2/C3 — de är olika VAPEN (SA respektive DA revolver
    // 41-44, 357, fri 9mm). Gruppkoden "M" identifierar därför inget magnumvapen, och väljaren
    // erbjöd bara gruppkoderna. Rapporterat från prodtest.
    section('Magnumklasserna går att välja');

    const wcOpts = (list.options || {}).weaponClasses || [];
    ok('valmängden är {id,label}, inte strängar',
       wcOpts.length > 0 && typeof wcOpts[0] === 'object' && 'id' in wcOpts[0],
       JSON.stringify(wcOpts[0]));
    // Grupperna får inte ha trängts ut av tillägget.
    for (const g of ['A', 'A_Opt', 'B', 'C', 'R', 'M', 'L']) {
      ok(`vapengruppen ${g} erbjuds fortfarande`, wcOpts.some(o => o.id === g));
    }
    for (const m of ['M1', 'M2', 'M3', 'M9']) {
      ok(`magnumklassen ${m} erbjuds`, wcOpts.some(o => o.id === m));
    }
    // ⚠️ Etiketten MÅSTE bära beskrivningen — "M2" ensamt är ett val ingen kan göra.
    const m2opt = wcOpts.find(o => o.id === 'M2');
    ok('M2:s etikett namnger vapnet', !!m2opt && /Revolver/i.test(m2opt.label),
       m2opt && m2opt.label);
    // Kontrollprov: gruppkoderna ska INTE ha fått en påhittad beskrivning.
    const copt = wcOpts.find(o => o.id === 'C');
    eq('gruppkoden C:s etikett är koden själv', copt && copt.label, 'C');

    // Och den ska gå att SPARA. Validatorn gick tidigare via Enum.TryParse<WeaponClass>, som
    // avvisar "M2" — väljaren hade alltså erbjudit ett värde som inte kunde sparas.
    const magnum = await api('/umbraco/surface/Firearm/SaveFirearm', {
      Id: 0, Alias: `${PREFIX}Magnum`, WeaponClass: 'M2', Vapentyp: 'Revolver',
      AcquisitionStatus: 'Innehas', WriteDetails: '0',
    });
    ok('ett magnumvapen kan sparas med klass M2', magnum.success, magnum.message);

    list = await api('/umbraco/surface/Firearm/GetMyFirearms');
    const mRow = (list.firearms || []).find(f => f.alias === `${PREFIX}Magnum`);
    eq('klassen lagras som M2, inte som M', mRow && mRow.weaponClass, 'M2');

    // ⚠️ En kompetensnivå ska INTE gå att spara i ett VAPENfält. C1/C2/C3 är samma pistol och
    // olika skytt — nivån ändras när skytten avancerar, vapnet gör det inte. Magnum är undantaget
    // just för att M1/M2 verkligen är skilda vapen.
    const level = await api('/umbraco/surface/Firearm/SaveFirearm', {
      Id: 0, Alias: `${PREFIX}Nivakoll`, WeaponClass: 'C1', Vapentyp: 'Pistol',
      AcquisitionStatus: 'Innehas', WriteDetails: '0',
    });
    ok('en kompetensnivå (C1) nekas som vapengrupp', level.success === false, level.message);
    ok('avslaget namnger värdet', /C1/.test(level.message || ''), level.message);

    // ── FAS 3: vapenuppgifterna in på blanketten ──────────────────────────────────────────────
    // Registret kunde svara på fabrikat/modell/kaliber/piplängd och på "antal vapen sedan tidigare"
    // per förbund, men utfärdandeformuläret hämtade det inte — utfärdaren skrev av uppgifterna för
    // hand ur ett register som redan hade dem.
    section('Föreningsintyget hämtar vapenuppgifterna (fas 3)');

    // ⚠️ EGEN förfrågan. Steg 6 avslutar med att sätta sin förfrågan till Avslagen, och
    // GetIntygFirearmRequests listar bara ÖPPNA — en återanvändning hade gett en tom lista och
    // sett ut som att endpointen inte fungerar.
    const fi = await api('/umbraco/surface/Firearm/RequestIntyg', {
      clubId: CLUB_ID, firearmId: rowHeld.id, kind: 'Fornyelse',
      forbund: 'Svenska Pistolskytteförbundet', vapengrupp: 'C', message: 'ZZV fas 3',
    });
    ok('en öppen förfrågan kan skapas för fas 3', fi.success, fi.message);

    // Medlems-id kommer ur GetMyFirearms — ägarens eget id på ägarens egen endpoint.
    const me = await api('/umbraco/surface/Firearm/GetMyFirearms');
    const MY_ID = me.memberId || 0;
    ok('GetMyFirearms svarar med ägarens eget id', MY_ID > 0, String(MY_ID));

    const fiReqs = await api(
      `/umbraco/surface/Foreningsintyg/GetIntygFirearmRequests?memberId=${MY_ID}`);
    ok('öppna förfrågningar listas för utfärdaren', fiReqs.success, fiReqs.message);
    const fiReq = (fiReqs.requests || []).find(r => r.id === fi.requestId);
    ok('förfrågan på fixturvapnet finns i listan', !!fiReq,
       JSON.stringify((fiReqs.requests || []).map(r => r.id)));

    // ⚠️ KONTROLLPROV, och avsnittets viktigaste påstående: listningen får INTE bära
    // vapenuppgifter. Gjorde den det skulle en läsning ske varje gång formuläret öppnades, utan
    // att någon bett om den, och medlemmens läslogg skulle fyllas av rader som inte var riktiga
    // uppslagningar.
    const reqJson = JSON.stringify(fiReqs.requests || []);
    ok('listningen bär INGA vapenuppgifter',
       !reqJson.includes(SECRET.Fabrikat) &&
       !reqJson.includes(SECRET.Licensnummer) &&
       !reqJson.includes(SECRET.Tillverkningsnummer));

    if (fiReq) {
      const nBefore = ((await api('/umbraco/surface/Firearm/GetMyAccessLog?includeOwn=true'))
                        .entries || []).length;

      const fetched = await api('/umbraco/surface/Foreningsintyg/FetchIntygFirearmData',
        { memberId: MY_ID, requestId: fiReq.id });
      ok('uppgifterna hämtas till blanketten', fetched.success, fetched.message);

      if (fetched.success) {
        eq('fabrikat kommer ur registret', fetched.fabrikat, SECRET.Fabrikat);
        eq('modell kommer ur registret', fetched.modell, SECRET.Modell);
        eq('kaliber kommer ur registret', fetched.kaliber, SECRET.Kaliber);
        eq('piplängd kommer ur registret', fetched.piplangd, SECRET.Piplangd);
        // Blanketten skopar antalet till ETT förbund, så förbundet måste följa med förfrågan.
        eq('förbundet följer med förfrågan', fetched.forbund, 'Svenska Pistolskytteförbundet');
        ok('antal vapen sedan tidigare är ett tal',
           typeof fetched.antalSedanTidigare === 'number', String(fetched.antalSedanTidigare));

        // ⚠️ "SEDAN TIDIGARE" — vapnet intyget gäller får INTE räknas som ett av de tidigare.
        // Fixturvapnet innehas och ligger i samma förbund, så utan exkluderingen skulle antalet
        // vara minst 1. Det spelar roll vid varje förnyelse.
        const mine = (await api('/umbraco/surface/Firearm/GetMyFirearms')).firearms || [];
        const heldSameForbund = mine.filter(f =>
          f.acquisitionStatus === 'Innehas' &&
          (f.federations || []).includes(fetched.forbund));
        eq('vapnet intyget gäller räknas INTE som "sedan tidigare"',
           fetched.antalSedanTidigare, Math.max(0, heldSameForbund.length - 1));

        // ⚠️ Uppslagningen SKA ha lämnat ett spår. En hämtning utan loggrad vore precis det som
        // löftet på medlemmens sida förbjuder.
        const logAfter = (await api('/umbraco/surface/Firearm/GetMyAccessLog?includeOwn=true'))
                          .entries || [];
        ok('hämtningen skrev en rad i läsloggen', logAfter.length > nBefore,
           `före=${nBefore}, efter=${logAfter.length}`);
      }

      // ⚠️ En förfrågan som tillhör en ANNAN medlem måste nekas — annars kunde en klubbadmin
      // skicka ett godtyckligt requestId och dra ut någon annans vapen.
      const wrongMember = await api('/umbraco/surface/Foreningsintyg/FetchIntygFirearmData',
        { memberId: BOARD_MEMBER_ID, requestId: fiReq.id });
      ok('förfrågan för en annan medlem nekas', wrongMember.success === false, wrongMember.message);

      const noSuchRequest = await api('/umbraco/surface/Foreningsintyg/FetchIntygFirearmData',
        { memberId: MY_ID, requestId: 99999999 });
      ok('okänd förfrågan nekas namngivet', noSuchRequest.success === false, noSuchRequest.message);
    }

    // Stäng fas 3-förfrågan så nästa körning kan skapa sin egen (dubbletter vägras).
    if (fi.requestId) {
      await api('/umbraco/surface/FirearmAdmin/SetIntygRequestStatus',
        { clubId: CLUB_ID, requestId: fi.requestId, status: 'Avslagen', note: 'ZZV städning' });
    }

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

    // ── STEG 7, klassen som TREDJE del av nyckeln (2026-09-02) ────────────────────────────────
    // ⚠️ DETTA ÄR KÄRNAN I ÄNDRINGEN. Resultatlistan grupperar en officiell tävling per
    // (tävling, VAPENKLASS), så en skytt anmäld i både A1 och C1 har två rader med SAMMA
    // tävlings-id. Utan klassen i nyckeln skrev en taggning av A-vapnet TYST över C-vapnets, och
    // skytten kunde aldrig ange mer än ett vapen per tävlingsdag. 19 skyttar på tävling 2586 är
    // anmälda i två klasser, så det är inget kantfall.
    section('Vapen per (tillfälle, klass) — steg 7:s nyckel');

    const usageBefore = find(list, 'Innehav').usageCount;

    const tagA = await api('/umbraco/surface/Firearm/SetUsage',
      { sourceKind: 'comp', sourceId: 990010, sourceClass: 'A1',
        firearmId: rowHeld.id, occurredOn: '2026-08-20' });
    ok('taggning av A1 på en tävling accepteras', tagA.success, tagA.message);

    const tagC = await api('/umbraco/surface/Firearm/SetUsage',
      { sourceKind: 'comp', sourceId: 990010, sourceClass: 'C1',
        firearmId: rowHeld.id, occurredOn: '2026-08-20' });
    ok('taggning av C1 på SAMMA tävling accepteras', tagC.success, tagC.message);

    list = await api('/umbraco/surface/Firearm/GetMyFirearms');
    // ⚠️ +2, inte +1. Faller den här raden har den tysta överskrivningen kommit tillbaka.
    eq('två klasser på samma tävling = TVÅ tillfällen',
       find(list, 'Innehav').usageCount, usageBefore + 2);

    let u = (await api('/umbraco/surface/Firearm/GetMyUsage')).usage || {};
    ok('nyckeln bär klassen sist och gement',
       Object.keys(u).includes('comp:990010:a1') && Object.keys(u).includes('comp:990010:c1'),
       Object.keys(u).filter(k => k.startsWith('comp:990010')).join(','));

    // ⚠️ Id-vs-Namn-fällan. En klass finns i TVÅ strängformer — "C_Vet_Y" och "C Vet Y" — som är
    // IDENTISKA för C1/A1 och olika för varje klass med ändelse. En rak strängjämförelse ser
    // därför korrekt ut i all testning och delar just veteran-, dam-, junior- och optikklasserna
    // i två tillfällen.
    const vetId = await api('/umbraco/surface/Firearm/SetUsage',
      { sourceKind: 'comp', sourceId: 990011, sourceClass: 'C_Vet_Y',
        firearmId: rowHeld.id, occurredOn: '2026-08-21' });
    ok('taggning med klassens ID-form accepteras', vetId.success, vetId.message);

    const beforeVetName = find(await api('/umbraco/surface/Firearm/GetMyFirearms'), 'Innehav').usageCount;
    const vetName = await api('/umbraco/surface/Firearm/SetUsage',
      { sourceKind: 'comp', sourceId: 990011, sourceClass: 'C Vet Y',
        firearmId: rowHeld.id, occurredOn: '2026-08-21' });
    ok('taggning med klassens NAMN-form accepteras', vetName.success, vetName.message);

    eq('ID- och namnformen av samma klass är SAMMA tillfälle',
       find(await api('/umbraco/surface/Firearm/GetMyFirearms'), 'Innehav').usageCount,
       beforeVetName);

    // ⚠️ Avtaggningen måste vara klass-scopad i BÅDA riktningarna. Är klassen inte med i DELETE:n
    // raderar ett borttaget A-vapen också C-vapnets rad — alltså samma överskrivning, baklänges.
    const beforeUntag = find(await api('/umbraco/surface/Firearm/GetMyFirearms'), 'Innehav').usageCount;
    await api('/umbraco/surface/Firearm/SetUsage',
      { sourceKind: 'comp', sourceId: 990010, sourceClass: 'A1', firearmId: 0 });
    eq('avtaggning av EN klass rör inte den andra',
       find(await api('/umbraco/surface/Firearm/GetMyFirearms'), 'Innehav').usageCount,
       beforeUntag - 1);

    u = (await api('/umbraco/surface/Firearm/GetMyUsage')).usage || {};
    ok('C1 står kvar när A1 tagits bort',
       Object.keys(u).includes('comp:990010:c1') && !Object.keys(u).includes('comp:990010:a1'),
       Object.keys(u).filter(k => k.startsWith('comp:990010')).join(','));

    // GetMyUsage bär numera också de valbara vapnen — taggningsytan behöver ett anrop, inte två.
    const usagePayload = await api('/umbraco/surface/Firearm/GetMyUsage');
    ok('GetMyUsage returnerar valbara vapen', Array.isArray(usagePayload.firearms) &&
       usagePayload.firearms.length > 0);
    // ⚠️ KONTROLLPROV: väljarlistan får bära alias och vapengrupp, men ALDRIG vapenuppgifter.
    // Vore de med hade taggningsytan varit en avkryptering utan loggrad.
    ok('valbara vapen bär INGA skyddade uppgifter',
       !JSON.stringify(usagePayload.firearms).includes(SECRET.Fabrikat) &&
       !JSON.stringify(usagePayload.firearms).includes(SECRET.Licensnummer),
       JSON.stringify(usagePayload.firearms).slice(0, 200));

    // Städa fixturens taggningar så nästa körning mäter sitt eget delta.
    for (const t of [{ id: 990010, cls: 'C1' }, { id: 990011, cls: 'C_Vet_Y' }]) {
      await api('/umbraco/surface/Firearm/SetUsage',
        { sourceKind: 'comp', sourceId: t.id, sourceClass: t.cls, firearmId: 0 });
    }

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

    // ── KLUBBVAPNETS EGNA LICENSUPPGIFTER (2026-09-02) ────────────────────────────────────────
    // Klubbvapen är licensbelagda precis som medlemmarnas och kan behöva nya licenser sökta, men
    // formuläret bar bara nummer/namn/grupp/typ/status/lånebart. Rapporterat från prod.
    section('Klubbvapnets licensuppgifter');

    const CF_SECRET = {
      fabrikat: 'ZZV Klubbfabrikat',
      modell: 'ZZV K-1',
      kaliber: '.22 LR',
      piplangd: '14,0 cm',
      tillverkningsnummer: 'ZZV-K-77123',
      licensnummer: 'ZZV-KL-9001',
      licensdatum: '2019-03-03',
      anteckning: 'ZZV klubbanteckning',
    };
    const CF_EXPIRES = '2027-04-05';

    const cfSave = await api('/umbraco/surface/FirearmAdmin/SaveClubFirearm', {
      clubId: CLUB_ID, id: w1.id, alias: `${PREFIX}Klubb 1`, weaponClass: 'C',
      vapentyp: 'Pistol', number: 91, isLoanable: true, status: 'Tillgängligt',
      licenseExpiresOn: CF_EXPIRES,
      federations: 'Svenska Pistolskytteförbundet',
      disciplines: 'Precision',
      writeDetails: '1',
      ...CF_SECRET,
    });
    ok('klubbvapnets licensuppgifter kan sparas', cfSave.success, cfSave.message);

    let cfList = await api(`/umbraco/surface/FirearmAdmin/GetClubFirearms?clubId=${CLUB_ID}`);
    let cf1 = (cfList.firearms || []).find(x => x.id === w1.id);
    ok('klubbvapnet bär förfallodatum i klartext', cf1 && cf1.licenseExpiresOn === CF_EXPIRES,
       cf1 && cf1.licenseExpiresOn);
    ok('dagar till förfall räknas av servern',
       cf1 && typeof cf1.daysUntilExpiry === 'number', cf1 && String(cf1.daysUntilExpiry));
    ok('klubbvapnet bär förbund', cf1 && (cf1.federations || []).includes('Svenska Pistolskytteförbundet'),
       JSON.stringify(cf1 && cf1.federations));
    ok('klubbvapnet bär gren', cf1 && (cf1.disciplines || []).length > 0,
       JSON.stringify(cf1 && cf1.disciplines));
    ok('klubbvapnet markeras ha skyddade uppgifter', cf1 && cf1.hasDetails === true);

    // ⚠️ KONTROLLPROV: listningen får inte bära klartexten. Listan renderas vid varje sidbesök,
    // och en avmaskering där vore både ologgad och oombedd.
    const cfListJson = JSON.stringify(cfList.firearms || []);
    ok('klubblistan bär INGA krypterade uppgifter',
       !cfListJson.includes(CF_SECRET.licensnummer) &&
       !cfListJson.includes(CF_SECRET.tillverkningsnummer) &&
       !cfListJson.includes(CF_SECRET.fabrikat));
    // ⚠️ KONTROLLPROV PÅ KONTROLLPROVET: nålarna måste vara riktiga strängar. Läses en nyckel med
    // fel skiftläge blir den undefined och frånvaropåståendet ovan kan aldrig falla.
    ok('nålarna i kontrollprovet är riktiga värden',
       typeof CF_SECRET.licensnummer === 'string' && CF_SECRET.licensnummer.length > 0 &&
       typeof CF_SECRET.tillverkningsnummer === 'string');

    const cfReveal = await api('/umbraco/surface/FirearmAdmin/RevealClubFirearmDetails',
      { clubId: CLUB_ID, firearmId: w1.id });
    ok('klubbadmin kan hämta uppgifterna', cfReveal.success, cfReveal.message);
    if (cfReveal.success) {
      eq('fabrikat round-trippar', cfReveal.details.fabrikat, CF_SECRET.fabrikat);
      eq('licensnummer round-trippar', cfReveal.details.licensnummer, CF_SECRET.licensnummer);
      eq('tillverkningsnummer round-trippar',
         cfReveal.details.tillverkningsnummer, CF_SECRET.tillverkningsnummer);
      eq('anteckning round-trippar', cfReveal.details.anteckning, CF_SECRET.anteckning);
    }

    // ⚠️ EN ANNAN KLUBBS (eller en MEDLEMS) VAPEN får inte gå att läsa genom att posta sitt eget
    // clubId. rowHeld är ett MEDLEMSvapen — grinden ska neka det här, inte lämna ut det.
    const cfForeign = await api('/umbraco/surface/FirearmAdmin/RevealClubFirearmDetails',
      { clubId: CLUB_ID, firearmId: rowHeld.id });
    ok('ett medlemsvapen kan INTE läsas via klubbytan', cfForeign.success === false, cfForeign.message);

    // ⚠️⚠️ DEN DESTRUKTIVA FÄLLAN. writeDetails=0 betyder "rör inte de krypterade uppgifterna".
    // Tolkades det som "spara tomt" skulle varje statusändring radera klubbens licensuppgifter —
    // tyst, och utan att någon märkte det förrän licensen skulle förnyas.
    const cfStatusOnly = await api('/umbraco/surface/FirearmAdmin/SaveClubFirearm', {
      clubId: CLUB_ID, id: w1.id, alias: `${PREFIX}Klubb 1`, weaponClass: 'C',
      vapentyp: 'Pistol', number: 91, isLoanable: true, status: 'Service',
      licenseExpiresOn: CF_EXPIRES,
      federations: 'Svenska Pistolskytteförbundet',
      disciplines: 'Precision',
      writeDetails: '0',
    });
    ok('en statusändring utan hämtning sparas', cfStatusOnly.success, cfStatusOnly.message);

    const cfAfter = await api('/umbraco/surface/FirearmAdmin/RevealClubFirearmDetails',
      { clubId: CLUB_ID, firearmId: w1.id });
    eq('licensnummret finns KVAR efter en sparning utan hämtning',
       cfAfter.success ? cfAfter.details.licensnummer : null, CF_SECRET.licensnummer);

    // ⚠️ Och relationerna måste också överleva. De skrivs om HELT vid varje sparning, så ett
    // utelämnat fält rensar dem — vilket tar förbundet med sig, och förbundet är vad blankettens
    // "antal vapen sedan tidigare" räknas i.
    cfList = await api(`/umbraco/surface/FirearmAdmin/GetClubFirearms?clubId=${CLUB_ID}`);
    cf1 = (cfList.firearms || []).find(x => x.id === w1.id);
    ok('förbundet finns kvar efter statusändringen',
       cf1 && (cf1.federations || []).includes('Svenska Pistolskytteförbundet'),
       JSON.stringify(cf1 && cf1.federations));

    // Kontrollprov åt andra hållet: ett TOMT federations-fält ska verkligen rensa. Utan det
    // påståendet vore raden ovan grön även om servern ignorerade fältet helt.
    await api('/umbraco/surface/FirearmAdmin/SaveClubFirearm', {
      clubId: CLUB_ID, id: w1.id, alias: `${PREFIX}Klubb 1`, weaponClass: 'C',
      vapentyp: 'Pistol', number: 91, isLoanable: true, status: 'Tillgängligt',
      licenseExpiresOn: CF_EXPIRES, federations: '', disciplines: '', writeDetails: '0',
    });
    cfList = await api(`/umbraco/surface/FirearmAdmin/GetClubFirearms?clubId=${CLUB_ID}`);
    cf1 = (cfList.firearms || []).find(x => x.id === w1.id);
    eq('ett tomt förbundsfält RENSAR relationerna', (cf1 && cf1.federations) || [], []);

    // Återställ fixturen till Tillgängligt utan förfallodatum, så bokningsavsnittet nedan mäter
    // det det tror att det mäter.
    await api('/umbraco/surface/FirearmAdmin/SaveClubFirearm', {
      clubId: CLUB_ID, id: w1.id, alias: `${PREFIX}Klubb 1`, weaponClass: 'C',
      vapentyp: 'Pistol', number: 91, isLoanable: true, status: 'Tillgängligt',
      licenseExpiresOn: '', federations: '', disciplines: '', writeDetails: '0',
    });


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

    // ── FÖNSTERREGELN: listan och bokningen måste svara för SAMMA fönster ─────────────────────
    // ⚠️ Regeln "tom sluttid = hela dagen" låg i två handskrivna kopior som hade glidit isär om
    // ett BAKVÄNT fönster: tillgänglighetslistan tolkade 14:00–10:00 som hela dagen och visade
    // vapnet som ledigt, medan bokningen vägrade samma fönster. En rad som ser bokbar ut och
    // nekas i nästa klick. Båda går nu via FirearmBookingWindow.
    section('Bokningsfönstret (en regel, inte två)');

    const revFrom = `${day} 14:00`;
    const revTo = `${day} 10:00`;

    const availRev = await api('/umbraco/surface/LoanWeaponApi/GetAvailability' +
      `?clubId=${CLUB_ID}&from=${encodeURIComponent(revFrom)}&to=${encodeURIComponent(revTo)}`);
    ok('tillgänglighet NEKAR ett bakvänt fönster', availRev.success === false, availRev.message);
    ok('och säger varför, inte bara "ogiltigt datum"',
       /sluta efter/i.test(availRev.message || ''), availRev.message);

    const bookRev = await api('/umbraco/surface/Firearm/BookLoanWeapon',
      { firearmId: w2.id, occasionKind: 'Fritt', occasionId: 0, from: revFrom, to: revTo, note: '' });
    ok('bokningen nekar samma fönster', bookRev.success === false, bookRev.message);
    // ⚠️ KÄRNAN: samma svar från båda ytorna. Divergensen var att den ena sa ja och den andra nej.
    ok('listan och bokningen ger SAMMA avslagsskäl',
       /sluta efter/i.test(bookRev.message || ''), bookRev.message);

    // Kontrollprov: en TOM sluttid ska fortfarande betyda hela dagen på BÅDA ytorna — annars
    // vore påståendena ovan gröna av att allting nekas.
    const availWhole = await api('/umbraco/surface/LoanWeaponApi/GetAvailability' +
      `?clubId=${CLUB_ID}&from=${encodeURIComponent(day)}&to=`);
    ok('tom sluttid = hela dagen (listan svarar)', availWhole.success === true, availWhole.message);
    ok('och etiketten säger "hela dagen"',
       /hela dagen/i.test(availWhole.windowLabel || ''), availWhole.windowLabel);

    // ── MIN SIDA: förklaringen och vägen till ett intyg ───────────────────────────────────────
    // ⚠️ Vyerna är runtime-kompilerade, så `dotnet build` validerar dem INTE. Att ladda sidan och
    // klicka i den ÄR kompileringskontrollen — det är halva skälet det här avsnittet finns.
    section('Min sida: förklaring och intygsväg');

    await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' });
    await page.click('#firearms-member-tab');

    // Fliken är lazy: vänta på en RIKTIG rad, inte på en timeout (som bara byter racet mot en
    // långsammare maskin).
    let panelReady = true;
    try {
      await page.waitForFunction(() => {
        const t = document.getElementById('fwTrust');
        // ⚠️ Ingressen är "Bara du ser dina vapen", inte längre "Krypterat" — kryptering är HUR
        // vi skyddar, medlemmens fråga är VEM som ser. Väntar man på det gamla ordet hänger
        // sviten i 15 s och rapporterar att fliken inte renderar.
        return t && t.textContent && /Bara du ser dina vapen/.test(t.textContent);
      }, null, { timeout: 15000 });
    } catch { panelReady = false; }
    ok('vapenfliken renderar', panelReady);

    const explain = page.locator('#fwExplain details');
    ok('förklaringsrutan finns', await explain.count() === 1);

    // ⚠️ RUTAN ÄR KOLLAPSAD när registret har vapen (öppen bara vid tomt register — första
    // besöket är när frågan "vem kan se det här?" avgör om medlemmen skriver in något alls).
    // En kollapsad <details> renderar inte sitt innehåll, så `innerText` gav 53 tecken — bara
    // rubriken — och fyra påståenden föll på en fullt fungerande ruta. Öppna den och läs;
    // då prövas dessutom att den GÅR att öppna, vilket är mer värt än att läsa dold text.
    ok('rutan är kollapsad när registret inte är tomt',
       await explain.evaluate(el => !el.open));

    await page.locator('#fwExplain summary').click();
    ok('rutan går att öppna', await explain.evaluate(el => el.open));

    const explainText = await page.locator('#fwExplain').innerText();
    ok('förklaringen namnger vem som kan läsa',
       /f\u00f6reningsintygsansvarig/i.test(explainText), explainText.slice(0, 160));
    ok('förklaringen namnger läsloggen', /l\u00e4slogg/i.test(explainText));
    ok('förklaringen säger att behörigheten upphör vid avgång',
       /upph\u00f6r/i.test(explainText));
    // ⚠️ Texten skrevs om 2026-09-02 efter Stefans genomgång. Tre påståenden som DÅ var rätt är
    // nu fel att kräva: "ligger öppet" (lagringsform beskriven som åtkomstform), "lämnar ut"
    // (antyder ett utlämnande till tredje part) och "förvaringssätt lagras inte alls" (drog in
    // inbrottsrisk i en ruta som ska minska oro). Sviten assertar i stället att de INTE står där.
    ok('förklaringen lovar att listan är privat',
       /bara du ser din lista/i.test(explainText), explainText.slice(0, 160));
    ok('förklaringen säger att ALLA uppgifter är hemliga',
       /alla uppgifter du l\u00e4gger in h\u00e4r \u00e4r\s*hemliga/i.test(explainText.replace(/\s+/g, ' ')));
    // ⚠️ FRÅNVAROPÅSTÅENDEN. Formuleringarna nedan är de tre Stefan underkände; skrivs de tillbaka
    // ska sviten falla. Ett frånvaropåstående utan ett närvaropåstående på samma text vore vakuöst
    // — därför står de två raderna ovan först.
    ok('säger INTE att något "ligger öppet"', !/ligger \u00f6ppet/i.test(explainText));
    ok('säger INTE att uppgifter "lämnas ut"', !/l\u00e4mnar ut|l\u00e4mnas ut/i.test(explainText));
    ok('nämner INTE förvaringssätt', !/f\u00f6rvaringss\u00e4tt/i.test(explainText));
    // ⚠️ Kontrollprov mot en tom ruta: rubriken ensam skulle annars göra påståendena ovan gröna.
    ok('förklaringen är inte tom', explainText.length > 600, `längd ${explainText.length}`);

    // ⚠️ HELA POÄNGEN med ändringen: intygsvägen ligger på TOPPNIVÅ, inte bara på ett vapenkort.
    // Ett föreningsintyg gäller oftast ett vapen medlemmen ännu inte äger, så förstagångssökaren
    // har en tom garderob — och låg knappen bara på ett kort fanns ingen väg in för just hen.
    const intygBtn = page.locator('[data-fw="intyg"]');
    ok('"Begär föreningsintyg" finns som egen ingång', await intygBtn.count() >= 1);

    await intygBtn.first().click();
    let chooserOpen = true;
    try {
      await page.waitForFunction(
        () => document.getElementById('fwIntygStartModal')?.classList.contains('show'),
        null, { timeout: 8000 });
    } catch { chooserOpen = false; }
    ok('valet mellan nytt och befintligt vapen öppnas', chooserOpen);
    ok('nytt vapen erbjuds', await page.locator('[data-fw="intyg-new"]').count() === 1);
    ok('befintligt vapen erbjuds', await page.locator('[data-fw="intyg-existing"]').count() === 1);
    // Fixturen HAR vapen, så den andra vägen ska vara öppen. Är den låst här är grinden inverterad.
    ok('befintlig-vägen är öppen när garderoben inte är tom',
       await page.locator('[data-fw="intyg-existing"]').isDisabled() === false);

    // Vägen "nytt vapen" ska leda till vapenformuläret i PLANERAT-läge, med förklaringen synlig.
    await page.locator('[data-fw="intyg-new"]').click();
    let editOpen = true;
    try {
      await page.waitForFunction(
        () => document.getElementById('fwEditModal')?.classList.contains('show'),
        null, { timeout: 8000 });
    } catch { editOpen = false; }
    ok('nytt vapen öppnar vapenformuläret', editOpen);
    if (editOpen) {
      ok('steg 1 av 2 förklaras i formuläret',
         await page.locator('#fwEditIntygNote').isVisible());
      // ⚠️ Ett vapen man SÖKER licens för ägs ännu inte. Står status på "Innehas" måste medlemmen
      // rätta vårt förval själv för det normala fallet.
      eq('status förvalt till Planerat', await page.locator('#fwStatus').inputValue(), 'Planerat');
      await page.locator('#fwEditModal .btn-close').click();
      await page.waitForTimeout(400);
    }

    // Begär-modalen bär en VÄLJARE, inte ett dolt fält — den öppnas nu även utan valt vapen.
    await page.locator('[data-fw="intyg"]').first().click();
    await page.waitForFunction(
      () => document.getElementById('fwIntygStartModal')?.classList.contains('show'),
      null, { timeout: 8000 }).catch(() => {});
    await page.locator('[data-fw="intyg-existing"]').click();
    let reqOpen = true;
    try {
      await page.waitForFunction(
        () => document.getElementById('fwReqModal')?.classList.contains('show'),
        null, { timeout: 8000 });
    } catch { reqOpen = false; }
    ok('förfrågan öppnas från toppknappen', reqOpen);
    if (reqOpen) {
      const nOpts = await page.locator('#fwReqFirearm option').count();
      ok('förfrågan har en vapenväljare med alternativ', nOpts > 0, `${nOpts} alternativ`);
      await page.locator('#fwReqModal .btn-close').click();
      await page.waitForTimeout(400);
    }

    // ── KLUBBENS VAPENFORMULÄR I DOM:EN ──────────────────────────────────────────────────────
    // ⚠️ Vyerna är runtime-kompilerade, så `dotnet build` validerar dem INTE. Att ladda sidan och
    // öppna modalen ÄR kompileringskontrollen — och en trasig partial ger 500 på hela
    // klubbadminsidan, alltså den yta klubbens administratörer arbetar i varje dag.
    section('Klubbens vapenformulär (DOM)');

    const clubJsErrors = [];
    page.on('pageerror', e => clubJsErrors.push(e.message));

    // ⚠️ KLUBBSIDANS URL ÄR INTE GISSNINGSBAR. Den byggs av URL-provideren ur trädet, så den
    // riktiga adressen är /{krets}/klubbar/{slug}/ — `/klubbar/{slug}/` 404:ar och `/clubs/{slug}/`
    // svarar 301 (en lagrad omdirigering, alltså inget att förlita sig på). Prova kandidater och
    // säg vilken som bar, i stället för att rapportera "formuläret är trasigt" på ett 404.
    const clubCandidates = [
      `${BASE}/halland/klubbar/haaplinge-goass/`,
      `${BASE}/clubs/haaplinge-goass/`,
    ];
    let clubPageOk = false;
    for (const u of clubCandidates) {
      try {
        const resp = await page.goto(u, { waitUntil: 'domcontentloaded' });
        if (resp && resp.status() < 400) { clubPageOk = true; break; }
      } catch { /* nästa kandidat */ }
    }

    ok('klubbsidan svarar', clubPageOk, page.url());

    if (clubPageOk) {
      // ⚠️ Administrationspanelen ligger i display:none tills fliken öppnas — en modal inuti en
      // dold panel får .show men blir aldrig synlig. Samma fälla som geometritesterna går i.
      const hasAdminTab = await page.locator('#clubAdmin-tab').count() > 0;
      ok('Administration-fliken finns för klubbadmin', hasAdminTab);

      if (hasAdminTab) {
        await page.click('#clubAdmin-tab');
        await page.waitForTimeout(800);
        // ⚠️ UPPDELAT 2026-09-02: rälsposten "Vapen & lånevapen" är två poster — **Klubbvapen**
        // under Klubben och **Föreningsintyg** under Medlemmar. Det här avsnittet handlar om
        // klubbens EGNA vapen och deras formulär, alltså Klubbvapen. Den gamla väljaren matchade
        // ingenting efter uppdelningen och sviten föll på en rälspost, inte på en bugg.
        const railBtn = page.locator('#clubFirearms-tab, [data-bs-target="#clubFirearmsTab"]');
        const hasRail = await railBtn.count() > 0;
        ok('rälsposten Klubbvapen finns', hasRail);

        if (hasRail) {
          await railBtn.first().click();
          // Vänta på klubbvapenlistan, inte på en timeout.
          let listed = true;
          try {
            await page.waitForFunction(() => {
              const b = document.getElementById('vapClubBody');
              return b && b.querySelector('table, p');
            }, null, { timeout: 15000 });
          } catch { listed = false; }
          ok('klubbens vapenlista renderar', listed);

          ok('kolumnen "Licens t.o.m." finns',
             (await page.locator('#vapClubBody th').allInnerTexts()).some(t => /Licens/.test(t)));

          // Öppna formuläret och kontrollera att HELA fältuppsättningen finns. Det är hela
          // rapporten: klubbvapen är licensbelagda och formuläret bar inte licensuppgifterna.
          //
          // ⚠️ "Lägg till vapen" LIGGER I ÅTGÄRDER-MENYN sedan de två korten slogs ihop
          // 2026-09-03. Menyn måste öppnas först — en direktklickning på ett menyval i en stängd
          // dropdown tidsgränsar på "element is not visible", vilket läser som att formuläret
          // är trasigt. Att i stället klicka med `force: true` hade fungerat och samtidigt slutat
          // mäta att knappen går att NÅ, alltså gömt exakt det fel som just uppstod här.
          const cfAdd = page.locator('#vapPanel [data-vap-action="cf-add"]').first();
          if (!(await cfAdd.isVisible().catch(() => false))) {
            await page.locator('#vapPanel .dropdown-toggle').first().click();
            await cfAdd.waitFor({ state: 'visible', timeout: 5000 });
          }
          await cfAdd.click();
          let cfOpen = true;
          try {
            await page.waitForFunction(
              () => document.getElementById('vapCfModal')?.classList.contains('show'),
              null, { timeout: 8000 });
          } catch { cfOpen = false; }
          ok('klubbvapenformuläret öppnas', cfOpen);

          if (cfOpen) {
            for (const id of ['vapCfNumber','vapCfAlias','vapCfClass','vapCfType','vapCfStatus',
                              'vapCfExpires','vapCfFabrikat','vapCfModell','vapCfKaliber',
                              'vapCfPiplangd','vapCfTillverkningsnummer','vapCfLicensnummer',
                              'vapCfLicensdatum','vapCfAnteckning']) {
              ok(`formuläret har ${id}`, await page.locator('#' + id).count() === 1);
            }
            // ⚠️ Magnumklasserna måste finnas i KLUBBENS väljare också — klubbvapen är
            // licensbelagda på samma sätt, och listan kommer ur samma delade källa.
            const cfClassVals = await page.locator('#vapCfClass option')
              .evaluateAll(els => els.map(e => e.value));
            ok('klubbväljaren erbjuder magnumklassen M2', cfClassVals.includes('M2'),
               cfClassVals.join(','));
            ok('klubbväljaren har kvar vapengruppen C', cfClassVals.includes('C'));
            const cfM2Label = await page.locator('#vapCfClass option[value="M2"]').innerText();
            ok('klubbväljarens M2 namnger vapnet', /Revolver/i.test(cfM2Label), cfM2Label);

            ok('förbundsrutorna är fyllda',
               await page.locator('#vapCfForbund input[type=checkbox]').count() > 0);
            ok('grenrutorna är fyllda',
               await page.locator('#vapCfDisciplines input[type=checkbox]').count() > 0);

            // ⚠️ AUTOFYLL-FIXEN. Webbläsaren fyllde i användarens E-POSTADRESS i "Namn":
            // etiketten läses som ett profilfält. autocomplete="off" är en stark uppmaning och
            // inte en garanti, så fälten bär dessutom egna namn som ingen heuristik känner igen.
            eq('namnfältet har autocomplete=off',
               await page.locator('#vapCfAlias').getAttribute('autocomplete'), 'off');
            eq('licensnummerfältet har autocomplete=off',
               await page.locator('#vapCfLicensnummer').getAttribute('autocomplete'), 'off');
            ok('namnfältet bär ett eget name',
               (await page.locator('#vapCfAlias').getAttribute('name')) === 'vapCfAlias');
            // Och det ska vara TOMT när formuläret öppnas för ett nytt vapen.
            eq('namnfältet är tomt i ett nytt formulär',
               await page.locator('#vapCfAlias').inputValue(), '');

            await page.locator('#vapCfModal .btn-close').click();
            await page.waitForTimeout(400);
          }
        }
      }

      // Samma autofyll-fix på medlemsmodalen — samma etikett, samma heuristik.
      // Klubbsidan loggar 'ckeditor-duplicated-modules' vid varje besok -- befintligt brus,
      // dokumenterat i CLAUDE.md och orelaterat till vapenytan. Filtreras bort, precis som i
      // featured-picker-sviten, sa kontrollen kan falla pa nagot som faktiskt ar vart.
      const realJsErrors = clubJsErrors.filter(m => !/ckeditor-duplicated-modules/.test(m));
      ok('klubbsidan kastade inga JS-fel', realJsErrors.length === 0,
         realJsErrors.slice(0, 3).join(' | '));
    }

    await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' });

    // ── TAGGNINGSYTAN (steg 7) — den som fattades helt ────────────────────────────────────────
    // ⚠️ SetUsage och GetMyUsage anropades från INGEN vy. API:et fungerade, men en medlem kunde
    // inte ange ett vapen alls. Det här avsnittet mäter att ytan finns och att ett klick i den
    // verkligen landar i databasen — inte bara att endpointen svarar.
    section('Taggningsytan (steg 7)');

    // Träningsmodalens vapenrad. ⚠️ Vyerna är runtime-kompilerade, så det här är också
    // kompileringskontrollen för TrainingScoreEntry.cshtml.
    await page.click('#results-tab').catch(() => {});
    await page.waitForTimeout(600);

    const openedModal = await page.evaluate(() => {
      if (typeof openTrainingScoreModal !== 'function') return false;
      openTrainingScoreModal();
      return true;
    });
    ok('träningsmodalen går att öppna', openedModal);

    if (openedModal) {
      let rowShown = true;
      try {
        await page.waitForFunction(() => {
          const r = document.getElementById('trainingFirearmRow');
          return r && !r.classList.contains('d-none');
        }, null, { timeout: 10000 });
      } catch { rowShown = false; }
      // Fixturmedlemmen HAR vapen, så raden ska visas. Är den gömd här är villkoret inverterat.
      ok('träningsmodalen visar vapenväljaren', rowShown);

      const tOpts = await page.locator('#trainingFirearmId option').count();
      // > 1: "inget angivet" plus minst ett riktigt vapen. Exakt 1 betyder tom väljare.
      ok('vapenväljaren i modalen är fylld', tOpts > 1, `${tOpts} alternativ`);
      // ⚠️ Frivilligt med flit — fältet är skyttens egen anteckning, inte ett intygspåstående.
      ok('vapnet är frivilligt (förvalt "inget angivet")',
         await page.locator('#trainingFirearmId').inputValue() === '0');

      await page.evaluate(() => {
        const m = bootstrap.Modal.getInstance(document.getElementById('addTrainingScoreModal'));
        if (m) m.hide();
      });
      await page.waitForTimeout(500);
    }

    // Efterhandsvägen på Resultat-fliken, hela vägen ner i databasen.
    // ⚠️ Fixturen är ett RIKTIGT träningspass och raderas nedan — utan det växer dev-loggen med
    // ett pass per körning och nästa körning mäter inte längre sin egen rad.
    const today = new Date().toISOString().slice(0, 10);
    const created = await page.evaluate(async ([d]) => {
      const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch('/umbraco/surface/TrainingScoring/RecordTrainingScore', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json',
                   'RequestVerificationToken': tokEl ? tokEl.value : '' },
        credentials: 'same-origin',
        body: JSON.stringify({
          trainingDate: d, weaponClass: 'C', discipline: 'Precision', isCompetition: false,
          series: [{ seriesNumber: 1, total: 48, xCount: 2, entryMethod: 'SeriesTotal' }],
          notes: 'ZZV taggningsfixtur',
        }),
      });
      const t = await r.text();
      try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 200) }; }
    }, [today]);
    ok('ett träningspass kan sparas', created.success, created.message || created._raw);
    // ⚠️ ID:t är hela förutsättningen för att modalen ska kunna tagga passet den just sparade.
    // Utan det fanns inget tillfälle att hänga taggningen på, och svaret bar det inte förut.
    ok('sparningen returnerar radens id', (created.trainingScoreId || 0) > 0,
       `trainingScoreId=${created.trainingScoreId}`);

    const tsId = created.trainingScoreId || 0;
    if (tsId > 0) {
      // Rendera om listan så den nya raden finns, och läs Vapen-kolumnen.
      await page.evaluate(() => { if (typeof loadResults === 'function') loadResults(); });
      let cellReady = true;
      try {
        await page.waitForFunction(
          () => document.querySelector('[data-fwtag="set"]') !== null,
          null, { timeout: 15000 });
      } catch { cellReady = false; }
      ok('Resultat-fliken renderar en vapenväljare per rad', cellReady);

      if (cellReady) {
        ok('kolumnrubriken "Vapen" finns',
           (await page.locator('#resultsContent th').allInnerTexts()).some(t => /Vapen/.test(t)));

        // Välj fixturvapnet i raden för det pass vi just skapade.
        const sel = page.locator(`[data-fwtag="set"][data-kind="training"][data-id="${tsId}"]`);
        ok('raden för det nya passet har en väljare', await sel.count() === 1);

        if (await sel.count() === 1) {
          await sel.selectOption(String(rowHeld.id));
          // Sparningen är ett POST bakom en change-lyssnare; vänta på att servern har den.
          let landed = false;
          for (let i = 0; i < 20 && !landed; i++) {
            await page.waitForTimeout(500);
            const u = (await api('/umbraco/surface/Firearm/GetMyUsage')).usage || {};
            landed = u[`training:${tsId}`] === rowHeld.id;
          }
          // ⚠️ KÄRNAN i steg 7: ett klick i listan landar i databasen. Faller den här raden är
          // ytan tillbaka i det läge där API:et fungerar men ingen kan nå det.
          ok('ett val i listan sparas i databasen', landed);

          // Och tillbaka till "inget angivet" — en taggning måste gå att ångra.
          await sel.selectOption('0');
          let cleared = false;
          for (let i = 0; i < 20 && !cleared; i++) {
            await page.waitForTimeout(500);
            const u = (await api('/umbraco/surface/Firearm/GetMyUsage')).usage || {};
            cleared = u[`training:${tsId}`] === undefined;
          }
          ok('taggningen går att ta bort igen', cleared);
        }
      }

      // Städa fixturpasset. DeleteTrainingScore är [HttpDelete].
      const del = await page.evaluate(async ([id]) => {
        const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
        const r = await fetch(`/umbraco/surface/TrainingScoring/DeleteTrainingScore?id=${id}`, {
          method: 'DELETE',
          headers: { 'RequestVerificationToken': tokEl ? tokEl.value : '' },
          credentials: 'same-origin',
        });
        const t = await r.text();
        try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 200) }; }
      }, [tsId]);
      ok('fixturpasset raderas igen', del.success, del.message || del._raw);
    }

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

        // ⚠️ Taggningarna måste tas FÖRE vapenraderna, och de måste tas explicit.
        // RemoveFirearm GÖMMER (IsActive=0), det raderar inte — så CASCADE fyrar aldrig och
        // FirearmUsage-raderna blir kvar. Mätt: en `training:990001`-rad låg kvar efter varje
        // körning. Den växte inte (samma nyckel skrivs över), men en svit som lämnar rader
        // efter sig gör nästa körnings delta-mätningar svårare att lita på.
        for (const t of [
          { kind: 'training', id: 990001, cls: '' },
          { kind: 'comp',     id: 990002, cls: '' },
          { kind: 'comp',     id: 990010, cls: 'A1' },
          { kind: 'comp',     id: 990010, cls: 'C1' },
          { kind: 'comp',     id: 990011, cls: 'C_Vet_Y' },
        ]) {
          await post('/umbraco/surface/Firearm/SetUsage',
            { sourceKind: t.kind, sourceId: t.id, sourceClass: t.cls, firearmId: 0 });
        }

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
        '⚠️ RemoveFirearm GÖMMER (IsActive=0), det raderar inte — raderna ligger kvar med '
        + 'IsActive=0 och växer med varje körning. Rensa dem när de blir många:\n'
        + '  sqlcmd -S localhost\\SQLEXPRESS -d Umbraco -E -C -b -Q "SET QUOTED_IDENTIFIER ON; '
        + 'DELETE FROM Firearm WHERE Alias LIKE \'ZZV%\' AND IsActive = 0;"\n'
        + '  (QUOTED_IDENTIFIER ON krävs — Firearm bär ett filtrerat index.)');
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
