// event-guest-desk-verify.mjs — funktionären lägger till en gäst i disken, och prisradens
// automatiska etikett läcker inte ut som ett namn.
//
// KÖR:  node hpsk-verify/event-guest-desk-verify.mjs
//
// FÖRUTSÄTTNINGAR
//   • Dev-appen körs på http://localhost:18150 (--launch-profile "Umbraco.Web.UI").
//     ⚠️ ALDRIG `dotnet run --no-launch-profile` — det pekar på PROD-DB.
//   • Inloggad som klubbadmin i klubb 2604 (HPSK_USER/HPSK_PASS).
//
// TVÅ SAKER SOM RAPPORTERADES SAMMA DAG:
//   1. Arrangören kunde inte lägga till en gäst. Uppropet kunde bara lägga till MEDLEMMAR, så
//      frun som kom med Hugo fick ingen rad alls — ingen plats räknad, ingen avgift, ingen bock.
//   2. "Avgift" låste sig som namn på det första priset. Det är den etikett ett ENSAMT pris
//      lagras med, osynlig så länge den är ensam — men blir ett synligt namn i samma stund en
//      andra rad läggs till: "Avgift 180 / Barn 90".
//
// ⚠️ SVITEN SKRIVER: ett evenemang, en anmälan och en gäst — prefixade `ZZD `. Deltagarraderna
//    följer INTE med noden (de är nycklade på EventId), så kör efteråt:
//      DELETE FROM dbo.ClubEventParticipant WHERE MemberName LIKE 'ZZD %';

import { chromium } from 'playwright';
import { execSync } from 'node:child_process';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const CLUB_ID = 2604;
const PREFIX = 'ZZD ';
// Lisa Svensson - en ANNAN medlem i samma klubb, sa varden aldrig ar den inloggade.
const HOST_MEMBER = 5601;

let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);
const day = n => new Date(Date.now() + n * 86400000).toISOString().slice(0, 10);

// ⚠⚠ QUOTED_IDENTIFIER ON på varje sats — ClubEventParticipant bär ett FILTRERAT index
// (det som släpper in flera gäster) och SQL Server vägrar all DML mot en sådan tabell när
// inställningen är av, vilket är sqlcmds standard. Utan -b exitar sqlcmd dessutom 0 på felet.
const q = sqlText => execSync(
  `sqlcmd -S "localhost\\SQLEXPRESS" -d Umbraco -E -C -b -W -h -1 -Q "SET QUOTED_IDENTIFIER ON; ${sqlText}"`,
  { encoding: 'utf8' }).trim();

// En medlem som varken är den inloggade eller värden ovan.
const LEGACY_MEMBER = 2344;
const LEGACY_CREATED = '2026-09-01 10:00:00';

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
      console.error('\nAVBRYTER: inte inloggad — varje "nekas" hade blivit vakuöst grönt.');
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

    // ── Fixtur: ETT pris, alltså det läge där etiketten är osynlig ───────────────────────────
    section('Fixtur');
    const ev = await post('/umbraco/surface/Club/CreateClubEvent', {
      clubId: CLUB_ID, eventName: `${PREFIX}Disken`, eventDate: `${day(10)} 18:00`,
      description: 'ZZD', venue: 'Banan', eventType: 'Socialt',
      contactPerson: '', contactEmail: '', contactPhone: '',
      registrationRequired: 'true', maxParticipants: 10, registrationUrl: '',
      isMandatory: 'false', lanevapenOffered: 'false', registrationDeadline: '',
      eventAudience: 'Club', eventSwishNumber: '',
      eventPrices: JSON.stringify([{ Id: '', Label: 'Avgift', Amount: 180 }]),
    });
    evId = (ev.data && ev.data.id) || 0;
    ok('evenemanget skapas', ev.success && evId > 0, ev.message);
    if (!evId) return;

    // ── ⚠️⚠️ FÄLTLISTAN SOM GLÖMMER ──────────────────────────────────────────────────────────
    //
    // `contactPhone` saknades i `GetClubEvents`-projektionen fram till 2026-09-19. Följden var
    // TYST DATAFÖRLUST: redigeringsdialogen fyller telefonfältet ur den här listan, så det fylldes
    // med tomt — och sparningen skriver `SetValue("contactPhone", "")` villkorslöst. Varje
    // redigering från klubb- eller kretspanelen raderade alltså kontakttelefonen.
    //
    // ⚠️ Mäts på API:et och inte i dialogen, med flit: klubbpanelens händelselista fylls
    // asynkront och en DOM-avläsning gav TVÅ falska resultat under felsökningen — först "fältet
    // saknas" (mitt grep-mönster matchade inte understrecket), sedan "alla fält tomma" (listan var
    // inte laddad). Projektionen är det som faktiskt bär felet, och den går att mäta exakt.
    section('Fältlistan');
    const list = await get(`/umbraco/surface/Club/GetClubEvents?clubId=${CLUB_ID}`);
    const row = (list.data || []).find(e => e.id === evId);
    ok('projektionen bär contactPhone', row && 'contactPhone' in row,
       `fälten: ${row ? Object.keys(row).join(',') : '(ingen rad)'}`);
    // ⚠️ Kontrollprov: de fält som ALDRIG saknades. Utan dem vore påståendet ovan grönt även om
    // projektionen råkade bära allt av en slump.
    ok('och de fält som alltid fanns', row && 'contactPerson' in row && 'contactEmail' in row);

    section('Prisradens namn');
    eq('ett ensamt pris lagras med platshållaretiketten', (row.eventPrices || [])[0]?.label, 'Avgift');

    // ⚠️ Och den syns INTE för medlemmen så länge den är ensam — kortet skriver bara beloppet.
    // Det är hela skälet den får heta så: den är intern tills en andra rad tillkommer.
    const card = await get(`/umbraco/surface/ClubEvent/GetSignupState?eventId=${evId}`);
    eq('kortet har exakt ett pris', (card.event.prices || []).length, 1);

    // ⚠️ KÄRNAN: när raderna öppnas i dialogen ska platshållaren TÖMMAS, så arrangören namnger
    // den i stället för att skicka ut "Avgift 180 / Barn 90" till medlemmarna.
    //
    // ⚠️⚠️ MÅSTE KÖRAS PÅ EN SIDA SOM RENDERAR `_EventRegistrationFields`. Min sida gör det inte,
    // och första körningen föll på `hpskEventPriceFill is not defined` — vilket läser som att
    // funktionen saknas i produkten. Klubbsidans adminpanel har den, men BARA för en klubbadmin:
    // anonymt innehåller sidan noll träffar på hjälparen.
    await page.goto(`${BASE}/halland/klubbar/haaplinge-goass/`, { waitUntil: 'domcontentloaded' });
    const hasHelpers = await page.evaluate(() => typeof hpskEventPriceFill === 'function');
    ok('klubbsidan renderar prisredigeraren', hasHelpers,
       'utan den mäter provet nedan ingenting — kontrollera att kontot är klubbadmin i 2604');
    if (!hasHelpers) return;
    const cleared = await page.evaluate(([label]) => {
      if (typeof hpskEventPriceFill !== 'function' || typeof hpskEventPriceExpand !== 'function')
        return { err: 'hjälparna finns inte på sidan' };
      // Bygg upp fälten som dialogen gör, med ETT pris som bär platshållaren.
      const host = document.createElement('div');
      host.innerHTML = '<div id="zzPriceSimple"><input id="zzPriceSingle" value="180"></div>'
                     + '<div id="zzPriceRows" style="display:none"><div id="zzPriceRowsBody"></div></div>';
      document.body.appendChild(host);
      hpskEventPriceFill('zz', { eventPrices: [{ id: 'avgift', label, amount: 180 }] });
      hpskEventPriceExpand('zz');
      const inputs = document.querySelectorAll('#zzPriceRowsBody input[type=text]');
      const out = { count: inputs.length, first: inputs[0] ? inputs[0].value : null,
                    placeholder: inputs[0] ? inputs[0].placeholder : null };
      host.remove();
      return out;
    }, ['Avgift']);

    ok('prisradens fält finns', !cleared.err && cleared.count === 1, JSON.stringify(cleared));
    eq('platshållaren töms när raderna öppnas', cleared.first, '');
    ok('och platshållartexten bjuder in till ett namn',
       (cleared.placeholder || '').includes('Vuxen'), cleared.placeholder);

    // ⚠️ Kontrollprov: ett namn arrangören VALT får inte tömmas. Utan det skulle påståendet ovan
    // vara grönt även om koden tömde varje etikett.
    const kept = await page.evaluate(() => {
      const host = document.createElement('div');
      host.innerHTML = '<div id="zyPriceSimple"><input id="zyPriceSingle" value="180"></div>'
                     + '<div id="zyPriceRows" style="display:none"><div id="zyPriceRowsBody"></div></div>';
      document.body.appendChild(host);
      hpskEventPriceFill('zy', { eventPrices: [{ id: 'vuxen', label: 'Vuxen', amount: 180 }] });
      hpskEventPriceExpand('zy');
      const v = document.querySelector('#zyPriceRowsBody input[type=text]').value;
      host.remove();
      return v;
    });
    eq('ett valt namn behålls', kept, 'Vuxen');

    // ── Funktionären lägger till en gäst ─────────────────────────────────────────────────────
    section('Gäst i disken');

    // ⚠️⚠️ VÄRDEN MÅSTE VARA NÅGON ANNAN ÄN DEN INLOGGADE. Första versionen anmälde sig själv och
    // satte `guestOfMemberId` till sitt EGET id — då är den nya arrangörsvägen identisk med
    // medlemmens egen, och påståendet "hör till rätt medlem" kan inte falla. Det var ett vakuöst
    // grönt på precis den rad funktionen finns för.
    //
    // ⚠️ `AddGuestAsync` kräver att värden är ANMÄLD, och ingen endpoint låter en funktionär anmäla
    // någon annan — därför sätts raden i SQL. Den städas i finally.
    execSync(`sqlcmd -S "localhost\\SQLEXPRESS" -d Umbraco -E -C -b -Q `
      + `"SET QUOTED_IDENTIFIER ON; INSERT INTO dbo.ClubEventParticipant `
      + `(EventId, MemberId, MemberName, SignedUpAt, SignedUpByMemberId, CreatedDate, UpdatedDate) `
      + `VALUES (${evId}, ${HOST_MEMBER}, 'ZZD Vardmedlem', GETDATE(), ${HOST_MEMBER}, GETDATE(), GETDATE());"`,
      { stdio: 'pipe' });
    // ⚠️ Tillbaka till en sida med antiforgery-token — klubbsidan har en, men POST:arna nedan
    // gick mot Min sida i resten av sviten och token hämtas ur DOM:en.
    await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' });
    const roster = await get(`/umbraco/surface/ClubEvent/GetRoster?eventId=${evId}`);
    ok('uppropet bär prisraderna', Array.isArray(roster.event.prices), JSON.stringify(roster.event));
    const host = (roster.rows || []).find(r => !r.isGuest && r.memberId === HOST_MEMBER);
    ok('en ANNAN medlem är anmäld och kan bära gästen', !!host, JSON.stringify((roster.rows||[]).map(r=>r.memberId)));
    if (!host) return;

    // ⚠️ KÄRNAN: funktionären pekar ut NÅGON ANNAN som ansvarig. Före den här ändringen fanns
    // ingen väg alls, och frun som kom med Hugo fick ingen rad.
    const g = await json('/umbraco/surface/ClubEvent/AddGuest', {
      eventId: evId, guestOfMemberId: host.memberId, name: `${PREFIX}Karin`, priceId: '',
    });
    ok('funktionären lägger till gästen', g.success, JSON.stringify(g).slice(0, 160));

    const after = await get(`/umbraco/surface/ClubEvent/GetRoster?eventId=${evId}`);
    const guest = (after.rows || []).find(r => r.isGuest);
    ok('gästen står på listan', !!guest, JSON.stringify(after.rows));
    eq('och hör till rätt medlem', guest && guest.guestOfMemberId, host.memberId);
    eq('gästen tar en plats', after.counts.signedUp, 2);

    // ── Den gamla diskraden ───────────────────────────────────────────
    // ⚠⚠ RAPPORTERAT 2026-09-20: "Kommer tillsammans med" erbjöd en person som servern sedan
    // vägrade, med ett besked skrivet till MEDLEMMEN ("Anmäl dig själv först") som funktionären
    // i disken inte kan agera på. Rader som disken skapade före 2026-09-20 saknar SignedUpAt.
    // Fixturen återskapar exakt den formen — den går inte att skapa via API:et längre.
    section('En rad utan anmälningstid kan inte bära en gäst — och läks');
    q(`INSERT INTO dbo.ClubEventParticipant `
      + `(EventId, MemberId, MemberName, SignedUpAt, CreatedDate, UpdatedDate) `
      + `VALUES (${evId}, ${LEGACY_MEMBER}, 'ZZD Gammalrad', NULL, '${LEGACY_CREATED}', '${LEGACY_CREATED}');`);

    await page.goto(`${BASE}/evenemang/deltagare?e=${evId}`, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => {
      const b = document.getElementById('deskBody');
      return b && !b.innerText.includes('Hämtar');
    }, null, { timeout: 30000 }).catch(() => {});

    const hostOptions = () => page.evaluate(() => {
      window.deskAddOpen();
      return [...document.querySelectorAll('#deskGuestHost option')]
        .map(o => ({ id: +o.value, text: o.innerText.trim() }));
    });

    let opts = await hostOptions();
    // KONTROLLPROV: den riktigt anmälda värden MÅSTE finnas. Utan det vore påståendet nedan
    // grönt även på en tom väljare — alltså även om filtret stängt ute alla.
    ok('den anmälda värden erbjuds', opts.some(o => o.id === HOST_MEMBER), JSON.stringify(opts));
    ok('men raden utan anmälningstid erbjuds INTE som värd',
       !opts.some(o => o.id === LEGACY_MEMBER), JSON.stringify(opts));

    // Servern måste ändå vägra, och beskedet ska tala till den som läser det.
    const refused = await json('/umbraco/surface/ClubEvent/AddGuest', {
      eventId: evId, guestOfMemberId: LEGACY_MEMBER, name: `${PREFIX}Nekad`, priceId: '',
    });
    ok('servern vägrar ändå', refused && refused.success === false, JSON.stringify(refused));
    ok('och beskedet säger INTE "Anmäl dig själv" till funktionären',
       refused && !/anmäl dig själv/i.test(refused.message || ''), refused && refused.message);
    ok('utan namnger personen som saknar anmälan',
       refused && /ZZD Gammalrad|Gammalrad/i.test(refused.message || '')
         || (refused && /står inte som anmäld/i.test(refused.message || '')),
       refused && refused.message);

    // Läkningen: en avprickning fyller i den uppgift som saknas — med radens EGEN skapelsetid.
    const legacyRowId = +q(`SET NOCOUNT ON; SELECT TOP 1 Id FROM dbo.ClubEventParticipant `
      + `WHERE EventId = ${evId} AND MemberId = ${LEGACY_MEMBER};`);
    const healed = await json('/umbraco/surface/ClubEvent/SetAttendance',
      { eventId: evId, participantId: legacyRowId, status: 'Present', note: null });
    ok('avprickningen gick igenom', healed && healed.success, healed && healed.message);

    const stamped = q(`SET NOCOUNT ON; SELECT CONVERT(varchar(19), SignedUpAt, 120) `
      + `FROM dbo.ClubEventParticipant WHERE Id = ${legacyRowId};`);
    // ⚠⚠ CreatedDate, ALDRIG dagens datum. Stämplas "nu" skrivs det om NÄR anmälan gjordes,
    // och den tiden bär både Anmäld-kolumnen och platsordningen.
    ok('raden läktes med sin EGEN skapelsetid, inte med dagens datum',
       stamped === LEGACY_CREATED, `SignedUpAt blev "${stamped}", väntade "${LEGACY_CREATED}"`);

    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => {
      const b = document.getElementById('deskBody');
      return b && !b.innerText.includes('Hämtar');
    }, null, { timeout: 30000 }).catch(() => {});
    opts = await hostOptions();
    ok('och då erbjuds den som värd', opts.some(o => o.id === LEGACY_MEMBER), JSON.stringify(opts));

    await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' });
    const nowOk = await json('/umbraco/surface/ClubEvent/AddGuest', {
      eventId: evId, guestOfMemberId: LEGACY_MEMBER, name: `${PREFIX}Sent`, priceId: '',
    });
    ok('och gästen går att lägga till', nowOk && nowOk.success, JSON.stringify(nowOk).slice(0, 160));

    ok('inga JS-fel', jsErrors.filter(e => !/ckeditor/.test(e)).length === 0, jsErrors.join(' | '));

  } finally {
    if (evId) {
      const r = await page.evaluate(async ([u, id]) => {
        const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
        const fd = new FormData();
        fd.append('eventId', id);
        fd.append('__RequestVerificationToken', tokEl ? tokEl.value : '');
        const res = await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' });
        return `${res.status}`;
      }, ['/umbraco/surface/Club/DeleteClubEvent', String(evId)]).catch(e => String(e));
      console.log(`\nStädning: evenemang ${evId} (${r}). Kör SQL-raden i huvudet för deltagarraderna.`);
    }
    await browser.close();
  }

  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log(`  - ${f}`)); process.exitCode = 1; }
};

main();
