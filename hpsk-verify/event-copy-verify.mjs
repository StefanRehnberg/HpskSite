// event-copy-verify.mjs — kopieras ALLT när en händelse kopieras, för klubb OCH krets?
//
// KÖR:  node hpsk-verify/event-copy-verify.mjs
//
// ⚠️⚠️ JÄMFÖRELSEN ÄR GENERISK MOT DOCTYPEN, inte mot en fältlista i den här filen. En
// handskriven lista här hade varit exakt den bugg sviten finns för att fånga — "kopiera
// händelse" byggde tidigare sin egen fältlista och tappade anmälan, kapaciteten,
// obligatorisk-flaggan, lånevapnen och sista anmälningsdag TYST. Frågan ställs därför till
// `cmsPropertyType`: varje egenskap doctypen bär ska antingen vara LIKA i kopian, eller
// stå med i den korta listan över dem som med avsikt skiljer sig.
//
// ⚠️ Ett fält som är TOMT på källan bevisar ingenting — då är "lika" sant av sig självt.
// Sviten räknar dem separat och FALLER om för många är otestade, i stället för att
// rapportera grönt på en jämförelse som inte kunde falla.
//
// ⚠️ SVITEN SKRIVER. Den skapar två händelser per scope (källa + kopia) och raderar dem i
// sitt finally, med en SQL-städning som sista utpost.

import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const CLUB_URL = `${BASE}/halland/klubbar/ankeborg-pistolklubb/`;
const REGION_URL = `${BASE}/halland/`;
const TAG = 'ZZCOPY';

let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);

const sql = q => execFileSync('sqlcmd',
  ['-S', 'localhost\\SQLEXPRESS', '-d', 'Umbraco', '-E', '-C', '-b', '-W', '-h', '-1', '-s', '|', '-Q', q],
  { encoding: 'utf8', maxBuffer: 1 << 24 });

// ⚠️ Jämförelsen görs I SQL och returnerar 1/0, aldrig värdena. sqlcmd trunkerar NVARCHAR(MAX)
// vid 256 tecken, och eventPrices är JSON som passerar det — en jämförelse på transporterade
// värden hade blivit röd på identiskt innehåll (eller, värre, grön på trunkerade prefix).
const compare = (srcId, copyId) => {
  const q = `SET NOCOUNT ON;
WITH v AS (
  SELECT cv.nodeId, pd.propertyTypeId,
         COALESCE(pd.textValue, pd.varcharValue,
                  CAST(pd.intValue AS NVARCHAR(MAX)),
                  CONVERT(NVARCHAR(30), pd.dateValue, 126),
                  CAST(pd.decimalValue AS NVARCHAR(MAX)), '') AS val
  FROM umbracoPropertyData pd
  JOIN umbracoContentVersion cv ON cv.id = pd.versionId AND cv.[current] = 1
  WHERE cv.nodeId IN (${srcId}, ${copyId})
)
SELECT pt.Alias + '|'
     + CASE WHEN ISNULL(s.val,'') = ISNULL(c.val,'') THEN '1' ELSE '0' END + '|'
     + CAST(LEN(ISNULL(s.val,'')) AS NVARCHAR(20)) + '|'
     + CAST(LEN(ISNULL(c.val,'')) AS NVARCHAR(20))
FROM cmsPropertyType pt
JOIN cmsContentType ct ON ct.nodeId = pt.contentTypeId AND ct.alias = 'clubSimpleEvent'
LEFT JOIN v s ON s.propertyTypeId = pt.id AND s.nodeId = ${srcId}
LEFT JOIN v c ON c.propertyTypeId = pt.id AND c.nodeId = ${copyId}
ORDER BY pt.Alias;`;
  return sql(q).split('\n').map(l => l.trim()).filter(l => l && l.includes('|')).map(l => {
    const [alias, same, srcLen, copLen] = l.split('|');
    return { alias, same: same === '1', srcLen: +srcLen, copLen: +copLen };
  });
};

const dates = id => {
  const q = `SET NOCOUNT ON;
SELECT pt.Alias + '|' + CONVERT(NVARCHAR(30), pd.dateValue, 126)
FROM umbracoPropertyData pd
JOIN umbracoContentVersion cv ON cv.id = pd.versionId AND cv.[current] = 1
JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
WHERE cv.nodeId = ${id} AND pd.dateValue IS NOT NULL ORDER BY pt.Alias;`;
  const out = {};
  sql(q).split('\n').map(l => l.trim()).filter(l => l.includes('|')).forEach(l => {
    const [a, v] = l.split('|'); out[a] = v;
  });
  return out;
};

const days = (a, b) => Math.round((new Date(b) - new Date(a)) / 86400000);

// De enda egenskaper som MED AVSIKT skiljer sig i en kopia.
const EXPECTED_DIFF = ['eventName', 'eventDate', 'eventEndDate', 'registrationDeadline'];

const PRICES = JSON.stringify([
  { id: 'p1', label: 'Vuxen', amount: 180 },
  { id: 'p2', label: 'Barn 7-15', amount: 90 },
]);

const main = async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  const created = [];
  // Deklareras har, inte i try: stadningen i finally behover den.
  let post;

  try {
    await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER);
    await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS);
    await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
    await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});

    const go = async url => {
      for (let i = 0; i < 3; i++) {
        try { await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 120000 }); return true; }
        catch { await page.waitForTimeout(3000); }
      }
      return false;
    };

    await go(`${BASE}/user-profile-page/`);
    if (await page.locator('#firearms-member-tab').count() === 0) {
      console.error('\nAVBRYTER: inte inloggad — varje påstående nedan hade mätt en tom sida.');
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
      try { return JSON.parse(t); } catch { return { success: false, message: `HTTP ${r.status}: ${t.slice(0, 120)}` }; }
    }, [url, fields]);

    // ═══════════════════════════════════════════════ ETT SCOPE
    const runScope = async (label, pageUrl, ownerIdSel, createUrl, ownerField) => {
      section(label);
      await go(pageUrl);
      // Klubbens id ligger i ett data-attribut (vapenpanelen), kretsens som en global i
      // RegionalPage. window.clubId FINNS INTE - den deklareras med const och hamnar inte pa
      // window; det ar dokumenterat i CLAUDE.md och kostade en runda nagon annan gang.
      const ownerId = await page.evaluate(sel => {
        if (sel === 'REGION') return window.regionId || 0;
        const el = document.querySelector(sel);
        return el ? +(el.getAttribute('data-club-id') || 0) : 0;
      }, ownerIdSel);
      ok(`${label}: ägarnoden hittades`, ownerId > 0, `selektor ${ownerIdSel}`);
      if (!ownerId) return;

      const start = new Date(); start.setDate(start.getDate() + 30);
      const iso = d => d.toISOString().slice(0, 10);
      const deadline = new Date(start); deadline.setDate(deadline.getDate() - 5);
      const endDate = new Date(start); endDate.setDate(endDate.getDate() + 2);

      // ── Källa med SÅ MÅNGA fält som skrivvägarna kan sätta ─────────────────────────────
      const c = await post(createUrl, {
        [ownerField]: ownerId,
        eventName: `${TAG} Kalla ${label}`,
        eventType: 'Träning',
        description: 'Beskrivning som ska följa med',
        venue: 'Banan',
        contactPerson: 'Kalle Kontakt',
        contactEmail: 'kalle@example.com',
        contactPhone: '070-1234567',
        eventDate: `${iso(start)} 18:00`,
        registrationRequired: true,
        maxParticipants: 24,
        isMandatory: true,
        registrationUrl: '',
        lanevapenOffered: true,
        registrationDeadline: iso(deadline),
        eventPrices: PRICES,
        eventAudience: 'Region',
        eventSwishNumber: '1231231231',
      });
      ok(`${label}: källhändelsen skapades`, c && c.success, c && c.message);
      if (!c || !c.success) return;
      const srcId = c.data && (c.data.id || c.data.eventId);
      created.push(srcId);

      // Fälten som bara händelsesidans dialog kan sätta.
      const e = await post('/umbraco/surface/Club/EditEventDetails', {
        eventId: srcId,
        eventName: `${TAG} Kalla ${label}`,
        eventType: 'Träning',
        eventDate: `${iso(start)} 18:00`,
        venue: 'Banan',
        description: 'Beskrivning som ska följa med',
        contactPerson: 'Kalle Kontakt',
        contactEmail: 'kalle@example.com',
        contactPhone: '070-1234567',
        equipmentRequired: 'Hörselskydd\nSkyddsglasögon',
        targetAudience: 'Nybörjare',
        eventEndDate: iso(endDate),
        registrationRequired: true,
        registrationUrl: '',
        isMandatory: true,
        maxParticipants: 24,
        lanevapenOffered: true,
        registrationDeadline: iso(deadline),
        eventPrices: PRICES,
        eventAudience: 'Region',
        eventSwishNumber: '1231231231',
      });
      ok(`${label}: de utökade fälten sattes`, e && e.success, e && e.message);

      // ── Kopian ────────────────────────────────────────────────────────────────────────
      const SHIFT = 21;
      const newStart = new Date(start); newStart.setDate(newStart.getDate() + SHIFT);
      const cp = await post('/umbraco/surface/Club/CopyClubEvent', {
        sourceEventId: srcId,
        eventName: `${TAG} Kopia ${label}`,
        eventDate: `${iso(newStart)} 18:00`,
      });
      ok(`${label}: KOPIERINGEN GICK IGENOM`, cp && cp.success, cp && cp.message);
      if (!cp || !cp.success) return;
      const copyId = cp.data && cp.data.id;
      created.push(copyId);

      // ── ⚠️ Generisk jämförelse mot doctypen ───────────────────────────────────────────
      const rows = compare(srcId, copyId);
      ok(`${label}: doctypens egenskaper gick att läsa`, rows.length >= 20, `${rows.length} rader`);

      const shouldMatch = rows.filter(r => !EXPECTED_DIFF.includes(r.alias));
      const tested = shouldMatch.filter(r => r.srcLen > 0);
      const untested = shouldMatch.filter(r => r.srcLen === 0).map(r => r.alias);
      const differing = tested.filter(r => !r.same).map(r => `${r.alias} (${r.srcLen}→${r.copLen})`);

      ok(`${label}: ALLA satta egenskaper kopierades`, differing.length === 0, differing.join(', '));
      ok(`${label}: jämförelsen prövade tillräckligt mycket`, tested.length >= 12,
         `bara ${tested.length} satta fält — resten bevisar ingenting`);
      console.log(`     (${tested.length} prövade, ${untested.length} tomma på källan: ${untested.join(', ') || '-'})`);

      // Kopian får inte vara TOM på ett fält källan hade.
      const emptied = tested.filter(r => r.copLen === 0).map(r => r.alias);
      ok(`${label}: inget fält tömdes i kopian`, emptied.length === 0, emptied.join(', '));

      // ── Datumen ───────────────────────────────────────────────────────────────────────
      const ds = dates(srcId), dc = dates(copyId);
      eq(`${label}: startdatumet blev det angivna`, days(ds.eventDate, dc.eventDate), SHIFT);
      ok(`${label}: slutdatumet flyttades lika mycket`,
         ds.eventEndDate && dc.eventEndDate && days(ds.eventEndDate, dc.eventEndDate) === SHIFT,
         `${ds.eventEndDate} → ${dc.eventEndDate}`);
      ok(`${label}: sista anmälningsdag flyttades lika mycket`,
         ds.registrationDeadline && dc.registrationDeadline
         && days(ds.registrationDeadline, dc.registrationDeadline) === SHIFT,
         `${ds.registrationDeadline} → ${dc.registrationDeadline}`);
      // ⚠️ Det som gör flytten nödvändig: deadline måste ligga kvar FÖRE starten, annars
      // föds kopian med stängd anmälan fast den ser komplett ut.
      ok(`${label}: kopians deadline ligger kvar före dess startdatum`,
         new Date(dc.registrationDeadline) < new Date(dc.eventDate),
         `${dc.registrationDeadline} vs ${dc.eventDate}`);
    };

    await runScope('Klubb', CLUB_URL, '[data-club-id]',
                   '/umbraco/surface/Club/CreateClubEvent', 'clubId');
    await runScope('Krets', REGION_URL, 'REGION',
                   '/umbraco/surface/Club/CreateRegionEvent', 'regionId');

    // ── Kretsens väg in ur gränssnittet ──────────────────────────────────────────────────
    section('Kretsen når kopieringen ur sitt eget gränssnitt');
    await go(REGION_URL);
    const regionUi = await page.evaluate(() => ({
      fn: typeof window.copyRegionEvent === 'function',
      modal: !!document.getElementById('copyRegionEventModal'),
    }));
    ok('kretsen har en kopieringsfunktion', regionUi.fn, JSON.stringify(regionUi));
    ok('och en dialog att fylla i datum i', regionUi.modal, JSON.stringify(regionUi));

  } finally {
    // ⚠️ STÄDA FRÅN EN SIDA SOM BÄR EN ANTIFORGERY-TOKEN. Sviten slutar på kretssidan,
    // som inte har någon i DOM:en — POST:en blev då ett tyst 400 och fixturen låg kvar
    // medan körningen rapporterade klart. Samma fälla som event-guests-sviten gick i.
    await page.goto(CLUB_URL, { waitUntil: 'domcontentloaded', timeout: 120000 }).catch(() => {});
    for (const id of created) {
      if (!id) continue;
      const del = await post('/umbraco/surface/Club/DeleteClubEvent', { eventId: id })
        .catch(() => null);
      // En sväld raderingsmiss är hur en svit börjar lämna spår efter sig.
      if (!del || !del.success) console.log('  ! kunde inte radera ' + id + ': ' + (del && del.message));
    }
    try {
      sql(`SET NOCOUNT ON; DELETE FROM dbo.ClubEventParticipant WHERE MemberName LIKE '${TAG}%';`);
    } catch { /* best effort */ }
    await browser.close();
  }

  // Städningen assertas — en svit som lämnar noder efter sig gör nästa körning otillförlitlig.
  const leftovers = sql(`SET NOCOUNT ON;
SELECT COUNT(*) FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
WHERE ct.alias = 'clubSimpleEvent' AND n.text LIKE '${TAG}%' AND n.trashed = 0;`).trim();
  ok('fixturen är städad', leftovers === '0', `${leftovers} noder kvar`);

  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log(`  - ${f}`)); process.exitCode = 1; }
};

main();
