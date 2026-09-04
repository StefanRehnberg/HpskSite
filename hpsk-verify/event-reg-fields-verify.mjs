// event-reg-fields-verify.mjs — anmälningsfälten på en klubb-/kretshändelse, fält för fält
// genom VARJE läs- och skrivväg.
//
// KÖR:  node hpsk-verify/event-reg-fields-verify.mjs
//       (statisk läsning av ClubController.cs — kräver ingen körande app och ingen inloggning)
//
// ⚠️ VARFÖR DEN FINNS. Samma fel har uppstått TRE gånger på den här ytan, och varje gång var
// orsaken en handskriven fältlista som glömde ett fält:
//   1. Anmälningsfälten låg bara i evenemangssidans dialog — den som SKAPADE en händelse i
//      klubbens panel hade ingen väg till anmälan alls.
//   2. `GetEventEditData` lämnade `lanevapenOffered` utanför, så kryssrutan renderades alltid
//      urklickad — och nästa Spara från evenemangssidan skrev `false` på riktigt. Det var
//      Luleå PK:s felrapport 2026-09-04, och flaggan var verkligen nollställd i prod.
//   3. "Kopiera händelse" postade sex grundfält till `CreateClubEvent`, så kopian tappade tyst
//      anmälan, kapaciteten, obligatorisk-flaggan, lånevapnen och sista anmälningsdag.
//
// Partialen `_EventRegistrationFields.cshtml` delar MARKUPEN, men varje endpoint-signatur är
// fortfarande handskriven. Den här sviten är påståendet att ingen av dem glömt ett fält.
//
// ⚠️ TVÅ UNDANTAG ÄR AVSIKTLIGA och står som förväntade här, inte som fel:
//   • `GetUpcomingEvents` är kalendern och bär bara vad brickorna behöver.
//   • `CopyClubEvent` har ingen fältlista alls — den itererar källnodens egna egenskaper, vilket
//     är just varför den inte kan glömma nästa fält.
// Faller ett av undantagen ut är det ett FYND: någon har börjat lista fält för hand igen.

import fs from 'fs';
import path from 'path';

// HPSK_SRC finns för A/B:n. Kör sviten mot en äldre kopia av filen och den SKA falla — annars
// mäter den ingenting. Bevisat 2026-09-04 mot `git show HEAD:…ClubController.cs` från före
// fixen: `GetEventEditData` och `EditEventDetails` föll på `lanevapenOffered`, alltså exakt
// Luleås bugg, och `CopyClubEvent` föll på att endpointen inte fanns.
const SRC = process.env.HPSK_SRC
  || path.join(import.meta.dirname, '..', 'src', 'HpskSite', 'Controllers', 'ClubController.cs');

const FIELDS = ['registrationRequired', 'maxParticipants', 'isMandatory',
                'registrationUrl', 'lanevapenOffered', 'registrationDeadline'];

// endpoint -> vilka fält som SKA finnas i dess egen metodkropp.
// 'all' = alla sex. En lista = de enda som förväntas (och de övriga är alltså avsiktligt borta).
const EXPECTED = {
  GetClubEvents: 'all',
  GetRegionEvents: 'all',
  GetEventEditData: 'all',
  GetUpcomingEvents: ['registrationRequired', 'isMandatory', 'registrationDeadline'],
  CreateClubEvent: 'all',
  EditClubEvent: 'all',
  CreateRegionEvent: 'all',
  EditRegionEvent: 'all',
  EditEventDetails: 'all',
  CopyClubEvent: [],   // ingen fältlista — kopierar source.Properties
};

let pass = 0, fail = 0;
const failures = [];
function ok(name, cond, detail) {
  if (cond) { pass++; console.log(`  ✓ ${name}`); }
  else { fail++; failures.push(name); console.log(`  ✗ ${name}${detail ? ` — ${detail}` : ''}`); }
}

const src = fs.readFileSync(SRC, 'utf8');

// Metodkroppen: från signaturen till nästa metod på samma indentering.
function bodyOf(name) {
  const sig = new RegExp(`\\n\\s+(?:public|private)[^\\n]*\\b${name}\\s*\\(`);
  const m = src.match(sig);
  if (!m) return null;
  const start = m.index;
  const rest = src.slice(start + 10);
  const nxt = rest.match(/\n {8}(?:public|private) /);
  return rest.slice(0, nxt ? nxt.index : rest.length);
}

console.log('\n== Anmälningsfälten genom varje läs- och skrivväg');

for (const [endpoint, expected] of Object.entries(EXPECTED)) {
  const body = bodyOf(endpoint);
  ok(`${endpoint} finns kvar`, body !== null,
     'endpointen hittades inte — döpt om, flyttad eller borttagen');
  if (!body) continue;

  const want = expected === 'all' ? FIELDS : expected;
  if (want.length > 0) {
    // Bara när det FINNS fält att kräva. Ett "bär sina 0 fält" hade varit ett påstående som
    // inte kan falla, och en falsk grön rad är sämre än ingen rad.
    const missing = want.filter(f => !body.includes(f));
    ok(`${endpoint} bär sina ${want.length} fält`, missing.length === 0,
       `saknar ${missing.join(', ')}`);
  }

  // Undantagen mäts i BÅDA riktningarna: börjar någon lista fält för hand i CopyClubEvent, eller
  // lägga in kapacitet i kalendern, ska det synas här och inte upptäckas av en klubb.
  if (expected !== 'all') {
    const unexpected = FIELDS.filter(f => !want.includes(f) && body.includes(f));
    ok(`${endpoint} har inte vuxit en egen fältlista`, unexpected.length === 0,
       `bär oväntat ${unexpected.join(', ')} — är undantaget fortfarande sant?`);
  }
}

// Skrivvägen ska vara EN. Hittas SetValue på lånevapen- eller deadline-egenskapen någon
// annanstans än i ApplyEventRegistrationFields är det den glidning partialen finns för.
console.log('\n== En skrivväg');
const applyBody = bodyOf('ApplyEventRegistrationFields') || '';
for (const prop of ['EventOfferedProperty', 'DeadlineProperty', 'MandatoryProperty']) {
  const writes = [...src.matchAll(new RegExp(`SetValue\\(\\s*[^;]*${prop}`, 'g'))];
  const inApply = [...applyBody.matchAll(new RegExp(`SetValue\\(\\s*[^;]*${prop}`, 'g'))];
  // CopyClubEvent får skriva deadline: den räknar om datumet, vilket är en annan handling.
  const allowedOutside = prop === 'DeadlineProperty' ? 1 : 0;
  ok(`${prop} skrivs bara via ApplyEventRegistrationFields`,
     writes.length - inApply.length <= allowedOutside,
     `${writes.length} skrivningar totalt, ${inApply.length} i Apply`);
}

console.log(`\n${fail === 0 ? '✅' : '❌'} ${pass}/${pass + fail}`);
if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log('  - ' + f)); }
process.exit(fail === 0 ? 0 : 1);
