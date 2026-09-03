// omsidan-verify.mjs — /om-pistol-nu/: ingen markup far sta som SYNLIG TEXT.
//
// KOR: node hpsk-verify/omsidan-verify.mjs
//
// Slabbarnas Title/Teaser/Detail ar modellvarden och renderas med `@slab.X`, alltsa
// HTML-KODAT. Ett `<strong>` i strangen hamnar darfor som lasbar text mitt i stycket
// ("...andamalet, och <strong>varje uppslagning..."). Det upptacktes 2026-09-03 av att Stefan
// LASTE sidan — inget bygge, ingen svit och ingen kompilator ser det, eftersom strangen ar
// giltig C# och sidan renderar utan fel.
//
// Rutnatet "Pa gang" langre ner ar ra markup i vyn och bar HTML som vanligt. Blanda inte ihop.
import { chromium } from 'playwright';
const B = 'http://localhost:18150';
const br = await chromium.launch({ headless: true });
const page = await (await br.newContext({ viewport: { width: 1280, height: 900 } })).newPage();
await page.goto(`${B}/om-pistol-nu/`, { waitUntil: 'domcontentloaded' });
await page.waitForTimeout(1500);

// Falla ut varje "Las mer" sa hela Detail-texten hamnar i DOM:en.
await page.evaluate(() => document.querySelectorAll('.collapse').forEach(c => c.classList.add('show')));
await page.waitForTimeout(800);

// ⚠️ Riktiga radbrytningar ar legitima i innerText (rubrik/teaser/detalj ligger pa egna rader).
// Forsta versionen hade dem i monstret och gav atta falska larm.
const TAG = /<\/?[a-z][^>]*>|&(?:amp|lt|gt|quot|nbsp|#\d+);/i;
const BS = String.fromCharCode(92);
const LITERALS = [BS + 'n', BS + '"', BS + 'u00'];

const rows = await page.evaluate(() => Array.from(document.querySelectorAll('.about-slab')).map(sl => {
  const im = sl.querySelector('img.about-shot');
  const p = sl.querySelector('.prose');
  return { src: im ? im.getAttribute('src').replace('/images/about/', '') : '', text: p ? p.innerText : '' };
}));

console.log('slabs:', rows.length);
let fel = 0;
for (const r of rows) {
  const tag = r.text.match(TAG);
  const lit = LITERALS.find(x => r.text.includes(x));
  if (tag || lit) { fel++; console.log(`  FEL ${r.src}: "${tag ? tag[0] : lit}"`); }
}
console.log('slabs med utskriven markup:', fel);

// ⚠️ Kontrollprov: paverkar monstret alls nagot? En text som aldrig lastes ger 0 fel.
const probe = rows.some(r => TAG.test(r.text + ' <strong>x</strong>'));
console.log('kontrollprov (monstret triggar):', probe ? 'ok' : 'MISSLYCKAT');
const read = rows.filter(r => r.text.length > 80).length;
console.log('slabs med lasbar text:', read, 'av', rows.length);
await br.close();

if (fel) process.exitCode = 1;
