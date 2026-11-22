// Kör webbläsarens EGEN JavaScript på slumpade stationer och skriver fall + väntat svar — C#-sidan räknar samma fall och jämför.
//   shb-parity-cases.json  faltSuggestShootingTime (konfiguratorn) för 400 stationer
//   shb-parity-table.json  faltShbBucket och faltShbGroupLabel (figurväljarens rubriker) för grupp 0–15 × vapenklasser
// Fas 1a: tabellen ligger i C# (ShbFieldRules.MaxDistances) och ges ut som JSON till webbläsaren. Här läser vi den
// ur C#-källan med ett regex och matar JS-koden med den, precis som partialen _FaltskytteShbRules gör vid körning.
// Funktionerna (faltShbBucket, faltShbGroupLabel) läses ur partialen, resten ur konfiguratorn — inga kopior.
import { readFileSync, writeFileSync } from 'fs';
const root = 'C:/Repos/HpskSite/src/HpskSite/';
const cfgSrc = readFileSync(root + 'Views/Partials/_FaltskytteConfiguratorScript.cshtml', 'utf8');
const shbSrc = readFileSync(root + 'Views/Partials/_FaltskytteShbRules.cshtml', 'utf8');
const csSrc = readFileSync(root + 'CompetitionTypes/Faltskytte/Services/ShbFieldRules.cs', 'utf8');

// ── Tabellen ur C# ──
const maxDistances = {};
for (const m of csSrc.matchAll(/\[(\d+)\] = \((\d+), (\d+), (\d+), (null|\d+)\),/g))
  maxDistances[m[1]] = { AR: +m[2], B: +m[3], C: +m[4], M: m[5] === 'null' ? null : +m[5] };
const timeLine = csSrc.match(/MaxTimePerShot = new\(\) \{([^}]*)\}/)[1];
const maxTimePerShot = {};
for (const m of timeLine.matchAll(/\["(\w+)"\] = ([\d.]+)/g)) maxTimePerShot[m[1]] = +m[2];
if (Object.keys(maxDistances).length !== 15 || Object.keys(maxTimePerShot).length !== 4) throw new Error('kunde inte läsa tabellen ur ShbFieldRules.cs');

// ── Funktionerna ur partialen (faltShbBucket, faltShbGroupLabel) ──
const pa = shbSrc.indexOf('window.faltShbBucket = ');
const pb = shbSrc.lastIndexOf('}\n</script>');
if (pa < 0 || pb < pa) throw new Error('hittade inte funktionerna i _FaltskytteShbRules.cshtml');
const partialCode = shbSrc.slice(pa, pb);

// ── Konfiguratorns kod: från tilläggskonstanterna till difficulty-delen ──
const a = cfgSrc.indexOf('const SHB_TILLAGG_WEAPON_45');
const b = cfgSrc.indexOf('// Difficulty % per the experienced-Banläggare');
if (a < 0 || b < a) throw new Error('hittade inte konfiguratorns regelkod');
const code = cfgSrc.slice(a, b);

const wrapped = new Function('state', 'FALT_SHB_DATA', `
  var window = { FALT_SHB: FALT_SHB_DATA };
  var FALT_SHB = FALT_SHB_DATA;
  ${partialCode}
  var faltShbBucket = window.faltShbBucket;
  function faltCfgScoringMode() { return state.mode; }
  var faltCfgMorker = state.morker;
  ${code}
  return { faltSuggestShootingTime, faltShbBucket, faltShbGroupLabel: window.faltShbGroupLabel };`);
const FALT_SHB = { maxDistances, maxTimePerShot };

let seed = 7;
const rnd = n => { seed = (seed * 1103515245 + 12345) % 2147483648; return Math.floor(seed / 65536) % n; };
const wcs = ['A', 'B', 'C', 'R', 'M3'];
const cases = [];
for (let i = 0; i < 400; i++) {
  const groups = [];
  const ng = 1 + rnd(3);
  for (let g = 0; g < ng; g++) {
    const figs = [];
    const nf = 1 + rnd(4);
    for (let f = 0; f < nf; f++) figs.push({ sizeGroup: rnd(16), targetsPerFigure: 1 + rnd(2) });
    groups.push({ distance: rnd(4) === 0 ? null : 5 + rnd(120), figures: figs });
  }
  const st = {
    shootingTimeSec: 20,
    supportHand: rnd(3) === 0 ? 'Stödhand tillåten' : 'Stödhand ej tillåten',
    weaponStartPosition: ['45 grader', 'Hölster', 'Hands-up'][rnd(3)],
    minShotsPerFigure: rnd(3) === 0 ? 1 : 0,
    maxShotsPerFigure: [6, 4, 2, 3][rnd(4)],
    targetGroups: groups
  };
  const wc = wcs[rnd(wcs.length)];
  const morker = rnd(4) === 0;
  const poang = rnd(2) === 0;
  const api = wrapped({ mode: poang ? 'Poang' : 'Normal', morker }, FALT_SHB);
  const r = api.faltSuggestShootingTime(st, wc, morker);
  cases.push({ st, wc, morker, poang, expected: r ? r.totalSec : null });
}
writeFileSync(new URL('./shb-parity-cases.json', import.meta.url), JSON.stringify(cases));
console.log('cases', cases.length, 'with value', cases.filter(c => c.expected != null).length);

// ── Tabell: bucket per vapenklass och väljarens rubrik per grupp 0–15 ──
const api = wrapped({ mode: 'Normal', morker: false }, FALT_SHB);
const buckets = {};
for (const wc of ['', 'A', 'A_Opt', 'A_M', 'A_P', 'A_G', 'R', 'B', 'C', 'M', 'M1', 'M9', 'MX', 'Mag']) buckets[wc] = api.faltShbBucket(wc || null);
const labels = {};
for (let g = 0; g <= 15; g++) labels[g] = api.faltShbGroupLabel(g);
writeFileSync(new URL('./shb-parity-table.json', import.meta.url), JSON.stringify({ buckets, labels }));
console.log('table: buckets', Object.keys(buckets).length, 'labels', Object.keys(labels).length);
