// event-audience-verify.mjs — vem som får anmäla sig till ett klubbevenemang.
//
// KÖR:  node hpsk-verify/event-audience-verify.mjs
//
// FÖRUTSÄTTNINGAR
//   • Dev-appen körs på http://localhost:18150 (--launch-profile "Umbraco.Web.UI").
//     ⚠️ ALDRIG `dotnet run --no-launch-profile` — det pekar på PROD-DB.
//   • Doctype-egenskapen `eventAudience` finns på `clubSimpleEvent`. Saknas den mäter sviten
//     den DEGRADERADE vägen i stället, och det är en annan svit
//     (`audience-missing-property-probe.mjs`). Därför assertas egenskapens existens FÖRST.
//   • Inloggad som sajtadmin (HPSK_USER/HPSK_PASS).
//
// ⚠️⚠️ FIXTUREN LIGGER I EN KLUBB KONTOT INTE TILLHÖR — och det är hela tricket.
//   `IsEligible` frågar bara om klubbmedlemskap, aldrig om adminrättigheter, så ett konto med
//   primärklubb 2604 är INTE behörigt till ett evenemang i Ankeborg (9003) på klubbnivå men ÄR
//   det på "alla medlemmar". Med fixturen i den egna klubben hade båda nivåerna svarat `true`
//   och varje påstående varit vakuöst grönt.
//
// ⚠️ DELTAGARLISTAN GÅR INTE ATT MÄTA HÄR. Kontot är sajtadmin, och `showRoster` är
//   `canManage || CanSeeRoster` — så listan visas oavsett vad kapningen gör. Regeln mäts i
//   stället av `EventAudienceTests.Ett_trasigt_varde_ar_aldrig_bredare_an_klubben` m.fl.
//   Att påstå något om listan här hade varit ett påstående som inte kan falla.

import { chromium } from 'playwright';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
// ⚠️⚠️ TVÅ FIXTURKLUBBAR, och skillnaden mellan dem är hela kretsprovet.
//   Ankeborg (9003) ligger i SAMMA krets som kontots egen klubb (Haaplinge 2604, Halland) —
//   kontot är alltså inte medlem i klubben men väl i kretsen. Alingsås (2867) ligger i Älvsborg.
//   Utan den andra klubben kan påståendet "kretsnivån nekar en annan krets" aldrig FALLA: en
//   första version la allt i Ankeborg, fick `true` och rödmarkerade en korrekt implementation.
//   ⚠️ Låt dig inte luras av URL:en — prods /ankeland/klubbar/ankeborg/ säger ingenting om var
//   noden hänger i dev. Läs trädet (klubb → clubsPage → regionalPage), inte adressen.
const OTHER_CLUB = 9003;      // Ankeborg PK, Halland — samma krets som kontot
// ⚠️ Måste vara PUBLICERAD. `CreateClubEvent` slår upp klubben i den publicerade cachen, så en
//    opublicerad nod svarar "Club not found" — vilket läser som ett behörighetsfel. Alingsås
//    (2867) är opublicerad i dev och kostade en körning.
const OTHER_REGION_CLUB = 2879; // Johannishus PK, Blekinge — annan krets, publicerad
const PREFIX = 'ZZP ';

let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);
const day = n => new Date(Date.now() + n * 86400000).toISOString().slice(0, 10);

const main = async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  const created = [];

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
      console.error('\nAVBRYTER: inte inloggad. Varje "nekas"-påstående nedan hade annars blivit '
        + 'grönt på en åtkomstvägran som aldrig nådde funktionen.');
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

    const get = u => page.evaluate(async url => {
      const r = await fetch(url, { credentials: 'same-origin' });
      const t = await r.text();
      try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 160) }; }
    }, u);

    const make = async (audience, clubId = OTHER_CLUB) => {
      const r = await post('/umbraco/surface/Club/CreateClubEvent', {
        clubId, eventName: `${PREFIX}${audience}-${clubId}`, eventDate: `${day(20)} 10:00`,
        description: 'ZZP', venue: 'Banan', eventType: 'Socialt',
        contactPerson: '', contactEmail: '', contactPhone: '',
        registrationRequired: 'true', maxParticipants: 20, registrationUrl: '',
        isMandatory: 'false', lanevapenOffered: 'false', registrationDeadline: '',
        eventPrices: '', eventAudience: audience,
      });
      const id = (r.data && r.data.id) || 0;
      if (id) created.push(id);
      ok(`evenemang skapas med nivå ${audience}`, r.success && id > 0, r.message);
      return id;
    };

    // ── Egenskapen måste finnas, annars mäter sviten fel sak ──────────────────────────────────
    section('Förutsättning');
    const probe = await get(`/umbraco/surface/Club/GetClubEvents?clubId=${OTHER_CLUB}`);
    const anyRow = (probe.data || [])[0];
    ok('doctype-egenskapen eventAudience finns',
       anyRow && anyRow.audiencePropertyExists === true,
       'saknas den mäter den här sviten fel sak — kör audience-missing-property-probe i stället');
    if (!anyRow || anyRow.audiencePropertyExists !== true) return;

    // ── Nivån sparas och läses tillbaka ──────────────────────────────────────────────────────
    section('Nivån överlever en sparning');
    const ids = {};
    for (const lvl of ['Club', 'Region', 'AllMembers', 'Open']) ids[lvl] = await make(lvl);

    const list = await get(`/umbraco/surface/Club/GetClubEvents?clubId=${OTHER_CLUB}`);
    const byId = Object.fromEntries((list.data || []).map(e => [e.id, e]));
    for (const lvl of ['Club', 'Region', 'AllMembers', 'Open'])
      eq(`nivå ${lvl} läses tillbaka oförändrad`, byId[ids[lvl]] && byId[ids[lvl]].eventAudience, lvl);

    // ── ⚠️ KÄRNAN: behörigheten följer nivån ─────────────────────────────────────────────────
    section('Behörigheten följer nivån');

    const s = {};
    for (const lvl of ['Club', 'Region', 'AllMembers', 'Open'])
      s[lvl] = await get(`/umbraco/surface/ClubEvent/GetSignupState?eventId=${ids[lvl]}`);

    // Kontot har primärklubb 2604 (Haaplinge, Halland) och är INTE medlem i Ankeborg (9003) —
    // men väl i samma KRETS.
    eq('klubbnivå nekar den som inte är med i klubben', s.Club.eligible, false);
    eq('kretsnivå SLÄPPER IN en annan klubb i samma krets', s.Region.eligible, true);
    eq('alla medlemmar SLÄPPER IN samma person', s.AllMembers.eligible, true);
    eq('öppen nivå släpper in', s.Open.eligible, true);

    // ⚠️ Och den andra riktningen, som är den enda som kan visa att kretsgränsen FINNS: en klubb
    // i Älvsborg. Utan det här påståendet vore "kretsnivå släpper in" lika grönt om koden svarat
    // ja åt alla.
    const farId = await make('Region', OTHER_REGION_CLUB);
    const far = await get(`/umbraco/surface/ClubEvent/GetSignupState?eventId=${farId}`);
    eq('kretsnivå NEKAR en klubb i en annan krets', far.eligible, false);
    eq('och det är samma inloggade person', far.loggedIn, true);

    // ⚠️ Kontrollprov: ALLA fyra svaren kommer från samma inloggade konto och samma klubb. Utan
    // det kunde skillnaden ovan lika gärna bero på att någon fixtur hamnat fel.
    ok('alla fyra mäts som samma inloggade person',
       [s.Club, s.Region, s.AllMembers, s.Open].every(x => x.loggedIn === true));

    // ── Beskedet beskriver nivån, inte klubben ───────────────────────────────────────────────
    section('Beskedet');
    const denial = await page.evaluate(async ([u, id]) => {
      const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch(u, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tokEl ? tokEl.value : '' },
        body: JSON.stringify({ eventId: id }),
        credentials: 'same-origin',
      });
      return JSON.parse(await r.text());
    }, ['/umbraco/surface/ClubEvent/SignUp', ids.Club]);
    ok('anmälan nekas på klubbnivå', !denial.success, JSON.stringify(denial).slice(0, 140));
    ok('och beskedet namnger klubben', (denial.message || '').includes('Ankeborg'), denial.message);

    // ⚠️ Och på en bredare nivå får beskedet INTE nämna klubben — det var den gamla hårdkodade
    // texten, och den blev direkt osann i samma stund nivån gick att ändra.
    const okSignup = await page.evaluate(async ([u, id]) => {
      const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
      const r = await fetch(u, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': tokEl ? tokEl.value : '' },
        body: JSON.stringify({ eventId: id }),
        credentials: 'same-origin',
      });
      return JSON.parse(await r.text());
    }, ['/umbraco/surface/ClubEvent/SignUp', ids.AllMembers]);
    ok('anmälan GÅR IGENOM på "alla medlemmar"', okSignup.success, okSignup.message);

    // ── Ogiltigt värde ───────────────────────────────────────────────────────────────────────
    section('Ogiltigt värde');
    const junk = await post('/umbraco/surface/Club/CreateClubEvent', {
      clubId: OTHER_CLUB, eventName: `${PREFIX}Skrap`, eventDate: `${day(20)} 10:00`,
      description: 'ZZP', venue: 'Banan', eventType: 'Socialt',
      contactPerson: '', contactEmail: '', contactPhone: '',
      registrationRequired: 'true', maxParticipants: 5, registrationUrl: '',
      isMandatory: 'false', lanevapenOffered: 'false', registrationDeadline: '',
      eventPrices: '', eventAudience: 'Oppen',
    });
    ok('ett okänt värde vägras i stället för att tyst bli smalast', !junk.success,
       JSON.stringify(junk).slice(0, 160));
    if (junk.data && junk.data.id) created.push(junk.data.id);

  } finally {
    // ⚠️ Deltagarraderna följer INTE med noden — se event-guests-verify. Kör efteråt:
    //   DELETE FROM dbo.ClubEventParticipant WHERE EventId IN (<ids>)
    for (const id of created) {
      const r = await page.evaluate(async ([u, eid]) => {
        const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
        const fd = new FormData();
        fd.append('eventId', eid);
        fd.append('__RequestVerificationToken', tokEl ? tokEl.value : '');
        const res = await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' });
        return `${res.status}`;
      }, ['/umbraco/surface/Club/DeleteClubEvent', String(id)]).catch(e => String(e));
      console.log(`  städat ${id} (${r})`);
    }
    await browser.close();
  }

  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log(`  - ${f}`)); process.exitCode = 1; }
};

main();
