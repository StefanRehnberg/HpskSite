// Resultatlistans utskrift: ingen markering av inloggad/klubbkamrater, och BÅDA flikarna
// (individuellt + lag) följer med. Läser bara. Kör: node hpsk-verify/result-print-verify.mjs [compId]
import { chromium } from 'playwright';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const COMP = process.argv[2] || '2576';
let fail = 0, pass = 0;
const ok = (c, m) => { if (c) { pass++; console.log('  ok  ' + m); } else { fail++; console.log('  FEL ' + m); } };

const browser = await chromium.launch();
const page = await browser.newPage();

// Resultatsidans URL via tävlingens startlist-endpoint (samma trick som andra sviter).
const sl = await (await page.request.get(`${BASE}/umbraco/surface/PrecisionStartList/GetStartLists?competitionId=${COMP}`)).json();
const slUrl = sl.startLists?.[0]?.url;
if (!slUrl) { console.log('Hittar ingen startlista för att härleda URL'); process.exit(1); }
const resultUrl = BASE + slUrl.replace(/startlista\/?$/, 'resultat/');
console.log('Resultatsida: ' + resultUrl);

const resp = await page.goto(resultUrl, { waitUntil: 'networkidle' });
ok(resp.status() === 200, 'resultatsidan svarar 200');

const hasTabs = await page.locator('#teamResults').count() > 0;
ok(hasTabs, 'tävlingen har lagfliken (fixturkrav)');

// Lagen ska vara laddade UTAN att fliken klickats.
await page.waitForFunction(() => !document.querySelector('#teamResultsContent')?.innerText.includes('Laddar lagresultat'), null, { timeout: 15000 }).catch(() => {});
const teamText = await page.locator('#teamResultsContent').innerText();
ok(!teamText.includes('Laddar lagresultat'), 'lagresultaten är laddade utan klick på fliken');
ok(await page.locator('#teamResultsContent table, #teamResultsContent .alert').count() > 0, 'laginnehållet är renderat (tabell eller besked)');

// Markera en rad som inloggad och en som klubbkamrat, som renderarna gör.
const marked = await page.evaluate(() => {
    const rows = document.querySelectorAll('#resultsContent tbody tr');
    if (rows.length < 2) return false;
    rows[0].classList.add('current-user'); rows[1].classList.add('same-club');
    return true;
});
ok(marked, 'minst två resultatrader att markera');

const bg = () => page.evaluate(() => {
    const a = document.querySelector('#resultsContent tr.current-user');
    const b = document.querySelector('#resultsContent tr.same-club');
    return [getComputedStyle(a).backgroundColor, getComputedStyle(b).backgroundColor];
});

await page.emulateMedia({ media: 'screen' });
const [scrA, scrB] = await bg();
ok(scrA !== 'rgba(0, 0, 0, 0)' && scrB !== 'rgba(0, 0, 0, 0)', `kontrollprov: på skärmen är raderna tonade (${scrA} / ${scrB})`);

await page.emulateMedia({ media: 'print' });
const [prA, prB] = await bg();
ok(prA === 'rgba(0, 0, 0, 0)', `utskrift: inloggads rad är neutral (${prA})`);
ok(prB === 'rgba(0, 0, 0, 0)', `utskrift: klubbkamraternas rad är neutral (${prB})`);

const panes = await page.evaluate(() => ['individualResults', 'teamResults'].map(id => {
    const el = document.getElementById(id);
    const cs = getComputedStyle(el);
    return { id, display: cs.display, opacity: cs.opacity, h: el.getBoundingClientRect().height };
}));
for (const p of panes) ok(p.display !== 'none' && p.opacity === '1' && p.h > 0, `utskrift: ${p.id} syns (${p.display}, opacity ${p.opacity}, höjd ${Math.round(p.h)})`);

const headings = await page.locator('.print-section-heading').evaluateAll(els => els.map(e => getComputedStyle(e).display));
ok(headings.length === 2 && headings.every(d => d !== 'none'), 'utskrift: rubrikerna Individuella resultat / Lagresultat syns');

await page.emulateMedia({ media: 'screen' });
const scrPanes = await page.evaluate(() => getComputedStyle(document.getElementById('teamResults')).display);
ok(scrPanes === 'none', 'kontrollprov: på skärmen är lagfliken fortfarande dold tills man klickar');
const scrHead = await page.locator('.print-section-heading').first().evaluate(e => getComputedStyle(e).display);
ok(scrHead === 'none', 'kontrollprov: utskriftsrubrikerna syns inte på skärmen');

await page.emulateMedia({ media: 'print' });
const pdf = await page.pdf({ path: 'result-print-verify.pdf', format: 'A4' });
ok(pdf.length > 1000, `PDF skapad (${pdf.length} byte) → hpsk-verify/result-print-verify.pdf`);

await browser.close();
console.log(`\n${pass}/${pass + fail}`);
process.exit(fail ? 1 : 0);
