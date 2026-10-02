// Kör konfiguratorns EGEN JavaScript (faltSuggestShootingTime) på slumpade stationer och skriver
// fall + väntat svar till cases.json — C#-sidan räknar samma fall och jämför.
import { readFileSync, writeFileSync } from 'fs';
const src = readFileSync('C:/Repos/HpskSite/src/HpskSite/Views/Partials/_FaltskytteConfiguratorScript.cshtml', 'utf8');
const a = src.indexOf('const SHB_MAX_DISTANCES');
const b = src.indexOf('// Difficulty % per the experienced-Banläggare');
let code = src.slice(a, b);
let mode = 'Normal', faltCfgMorker = false;
const fn = new Function('getMode', code + '\nreturn { faltSuggestShootingTime, faltTargetGroupBounds };'.replace('', ''));
// faltCfgScoringMode och faltCfgMorker är globala i vyn — vi binder dem här.
const wrapped = new Function('state', `
  function faltCfgScoringMode() { return state.mode; }
  var faltCfgMorker = state.morker;
  ${code}
  return { faltSuggestShootingTime };`);
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
    weaponStartPosition: rnd(2) === 0 ? '45 grader' : 'Hölster',
    minShotsPerFigure: rnd(3) === 0 ? 1 : 0,
    maxShotsPerFigure: [6, 4, 2, 3][rnd(4)],
    targetGroups: groups
  };
  const wc = wcs[rnd(wcs.length)];
  const morker = rnd(4) === 0;
  const poang = rnd(2) === 0;
  const api = wrapped({ mode: poang ? 'Poang' : 'Normal', morker });
  const r = api.faltSuggestShootingTime(st, wc, morker);
  cases.push({ st, wc, morker, poang, expected: r ? r.totalSec : null });
}
writeFileSync(new URL('./shb-parity-cases.json', import.meta.url), JSON.stringify(cases));
console.log('cases', cases.length, 'with value', cases.filter(c => c.expected != null).length);
