// event-copy-ui-verify.mjs — går kopian att skilja från sitt original i listan?
//
// KÖR:  node hpsk-verify/event-copy-ui-verify.mjs
//
// ⚠️⚠️ RAPPORTERAT 2026-09-19, och rapporten var en följd, inte en orsak: "det skapades en
// kopia som också hette Eventet, och Gå till händelsen öppnade originalets sida". Apploggen
// visade att kopieringen fungerade — två noder, `Eventet` och `Eventet (1)`, med skilda
// URL:er. Felet var att LISTAN visar `eventName`-EGENSKAPEN, som är identisk, medan
// "Gå till händelsen" följer NODENS url, som kommer från ett nodnamn ingen ser. Två rader
// såg likadana ut, och nästa steg blev: "jag raderade den andra, vet inte om det var
// originalet eller kopian".
//
// ⚠️ Sviten mäter därför SÄRSKILJBARHETEN, inte att kopieringen sker — den mäts av
// event-copy-verify. Här är frågan om en människa kan se vilken rad som är vilken.
//
// ⚠️ SVITEN SKRIVER. Två händelser skapas och raderas i finally.

import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const CLUB = `${BASE}/halland/klubbar/haaplinge-goass/`;
const CLUB_ID = 2604;
const NAME = 'ZZUI Samma namn';

let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);

const sql = q => execFileSync('sqlcmd',
  ['-S', 'localhost\\SQLEXPRESS', '-d', 'Umbraco', '-E', '-C', '-b', '-W', '-h', '-1', '-Q', q],
  { encoding: 'utf8', maxBuffer: 1 << 24 });

const main = async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  const made = [];
  let post;

  try {
    await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER);
    await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS);
    await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
    await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
    await page.goto(CLUB, { waitUntil: 'domcontentloaded', timeout: 120000 });
    await page.evaluate(() => { const b = document.getElementById('cookieConsentBanner'); if (b) b.remove(); });

    if (await page.locator('#clubAdmin-tab').count() === 0) {
      console.error('\nAVBRYTER: ingen adminpanel — varje påstående nedan hade mätt en tom sida.');
      process.exitCode = 1;
      return;
    }

    post = (url, fields) => page.evaluate(async ([url, fields]) => {
      const tok = document.querySelector('input[name="__RequestVerificationToken"]');
      const fd = new FormData();
      Object.entries(fields).forEach(([k, v]) => fd.append(k, String(v)));
      if (tok) fd.append('__RequestVerificationToken', tok.value);
      const r = await fetch(url, { method: 'POST', body: fd });
      const t = await r.text();
      try { return JSON.parse(t); } catch { return { success: false, message: t.slice(0, 150) }; }
    }, [url, fields]);

    const src = await post('/umbraco/surface/Club/CreateClubEvent', {
      clubId: CLUB_ID, eventName: NAME, eventType: 'Träning', eventDate: '2026-11-04 18:00',
    });
    ok('källhändelsen skapades', src && src.success, src && src.message);
    if (!src || !src.success) return;
    made.push(src.data.id);

    // Listan hamtas vid SIDLADDNING, inte nar fliken oppnas - en fixtur som skapats efterat
    // finns darfor inte i allEvents forran sidan laddas om. Det kostade en korning.
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.evaluate(() => { const b = document.getElementById('cookieConsentBanner'); if (b) b.remove(); });
    await page.click('#clubAdmin-tab').catch(() => {});
    // Vanta pa att listan RITATS, inte en fix tid: den hamtas asynkront och en for kort
    // sovning ger ett rott pastaende om en knapp som bara inte hunnit fram.
    await page.waitForFunction(
      id => !!document.querySelector(`#eventsTab button[onclick="copyEvent(${id})"]`),
      src.data.id, { timeout: 30000 }).catch(() => {});

    // ── ⚠️ KONTROLLPROV FÖRE: ensam om sitt namn → inget datumsuffix ────────────────────
    // Utan det här kunde "kopians rad bär sitt datum" lika gärna betyda att vi skriver ut
    // datumet på VARJE rad, vilket vore brus och inte särskiljning.
    section('Innan kopian finns');
    const before = await page.evaluate(id => {
      const tr = [...document.querySelectorAll('#eventsTab tbody tr')]
        .find(x => x.innerHTML.includes(`copyEvent(${id})`));
      return tr ? tr.querySelector('td').innerText.replace(/\s+/g, ' ').trim() : null;
    }, src.data.id);
    ok('källans rad syns', !!before, String(before));
    ok('och bär INGET datum i namnkolumnen när den är ensam om namnet',
       before !== null && !before.includes('\u00b7'), String(before));

    // ── Kopiera genom det riktiga gränssnittet ────────────────────────────────────────
    section('Kopiera genom dialogen');
    const opened = await page.evaluate(id => {
      const btn = document.querySelector(`#eventsTab button[onclick="copyEvent(${id})"]`);
      if (!btn) return false;
      btn.click();
      return true;
    }, src.data.id);
    ok('Kopiera-valet finns på raden', opened);
    if (!opened) return;
    await page.waitForTimeout(800);

    const def = await page.evaluate(() => ({
      namn: document.getElementById('copyEventName').value,
      datum: document.getElementById('copyEventDate').value,
    }));
    eq('förvalt namn är källans', def.namn, NAME);
    ok('förvalt datum är en vecka fram', def.datum.startsWith('2026-11-11'), def.datum);

    await page.click('#copyEventModal .modal-footer .btn-primary');
    await page.waitForTimeout(3500);

    const copyId = +sql(`SET NOCOUNT ON;
SELECT TOP 1 n.id FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
WHERE ct.alias = 'clubSimpleEvent' AND n.parentId = ${CLUB_ID} AND n.trashed = 0
  AND n.id <> ${src.data.id} AND n.text LIKE '${NAME}%'
ORDER BY n.id DESC;`).trim();
    ok('kopian skapades', copyId > 0, `id ${copyId}`);
    if (copyId > 0) made.push(copyId);

    // ── ⚠️ KÄRNAN: går raderna att skilja åt? ─────────────────────────────────────────
    section('Raderna går att skilja åt');
    const rows = await page.evaluate(([a, b]) => {
      const find = id => [...document.querySelectorAll('#eventsTab tbody tr')]
        .find(tr => tr.innerHTML.includes(`copyEvent(${id})`));
      const read = tr => tr ? { text: tr.innerText.replace(/\s+/g, ' ').trim(),
                                url: (tr.querySelector('a.dropdown-item') || {}).getAttribute
                                     ? tr.querySelector('a.dropdown-item').getAttribute('href') : null } : null;
      return { src: read(find(a)), copy: read(find(b)) };
    }, [src.data.id, copyId]);

    ok('båda raderna finns i listan', rows.src && rows.copy, JSON.stringify(rows));
    if (rows.src && rows.copy) {
      ok('källans rad bär ett datum i namnkolumnen', /4 nov/.test(rows.src.text), rows.src.text);
      ok('kopians rad bär sitt EGNA datum', /11 nov/.test(rows.copy.text), rows.copy.text);
      ok('radernas text är INTE identisk', rows.src.text !== rows.copy.text);
      // ⚠️ Det som gjorde att fel händelse öppnades: länkarna måste peka på olika sidor.
      ok('Gå till händelsen pekar på olika sidor', rows.src.url && rows.copy.url && rows.src.url !== rows.copy.url,
         `${rows.src.url} vs ${rows.copy.url}`);
    }

    // ── Kvittot ───────────────────────────────────────────────────────────────────────
    section('Kvittot säger vilken som är ny');
    const notice = await page.evaluate(() => {
      const b = document.getElementById('eventsCopyNotice');
      return b ? { syns: b.offsetParent !== null, text: (b.innerText || '').trim() } : null;
    });
    ok('en notis visas', notice && notice.syns, JSON.stringify(notice));
    ok('och den namnger kopians datum', notice && /11 november/.test(notice.text), notice && notice.text);
    ok('ingen alert blockerade sidan', await page.locator('#eventsTab').count() === 1);

  } finally {
    await page.goto(CLUB, { waitUntil: 'domcontentloaded' }).catch(() => {});
    for (const id of made) {
      const d = await post('/umbraco/surface/Club/DeleteClubEvent', { eventId: id }).catch(() => null);
      if (!d || !d.success) console.log(`  ! kunde inte radera ${id}: ${d && d.message}`);
    }
    await browser.close();
  }

  const left = sql(`SET NOCOUNT ON;
SELECT COUNT(*) FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
WHERE ct.alias = 'clubSimpleEvent' AND n.text LIKE '${NAME}%' AND n.trashed = 0;`).trim();
  ok('fixturen är städad', left === '0', `${left} kvar`);

  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log(`  - ${f}`)); process.exitCode = 1; }
};

main();
