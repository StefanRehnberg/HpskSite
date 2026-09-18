// audience-missing-property-probe.mjs — mäter den DEGRADERADE vägen, som bara finns innan
// doctype-egenskapen `eventAudience` lagts till.
//
// ⚠️ ENGÅNGSMÄTNING. När egenskapen väl finns går det här läget inte att framkalla igen utan att
//    ta bort den, och då försvinner data. Kör den FÖRE operatören lägger in egenskapen.
//
// Varför det måste mätas: `SetValue` på en saknad doctype-egenskap är en TYST no-op. Utan den här
// kontrollen skulle en arrangör kunna välja "alla medlemmar", få "sparat", och händelsen stå kvar
// stängd — vilket är exakt den tystnad hela fältlista-som-glömmer-familjen handlar om.

import { chromium } from 'playwright';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const CLUB_ID = 2604;

let pass = 0, fail = 0;
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };

const main = async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  let evId = 0;
  try {
    await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER);
    await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS);
    await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
    await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
    await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded', timeout: 120000 });

    const post = (u, f) => page.evaluate(async ([url, fields]) => {
      const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
      const fd = new FormData();
      Object.keys(fields).forEach(k => fd.append(k, fields[k]));
      fd.append('__RequestVerificationToken', tokEl ? tokEl.value : '');
      const r = await fetch(url, { method: 'POST', body: fd, credentials: 'same-origin' });
      const t = await r.text();
      try { return JSON.parse(t); } catch { return { success: false, _status: r.status, _raw: t.slice(0, 160) }; }
    }, [u, f]);

    const base = {
      clubId: CLUB_ID, eventName: 'ZZA Publikprov', eventDate: '2027-01-15 10:00',
      description: 'ZZA', venue: 'Banan', eventType: 'Socialt',
      contactPerson: '', contactEmail: '', contactPhone: '',
      registrationRequired: 'true', maxParticipants: 5, registrationUrl: '',
      isMandatory: 'false', lanevapenOffered: 'false', registrationDeadline: '', eventPrices: '',
    };

    console.log('\n== Egenskapen saknas');

    // ⚠️ KÄRNAN: ett val som inte går att spara måste VÄGRAS och NAMNGE egenskapen.
    const wide = await post('/umbraco/surface/Club/CreateClubEvent',
      { ...base, eventAudience: 'AllMembers' });
    ok('en bredare nivå vägras när egenskapen saknas', !wide.success, JSON.stringify(wide).slice(0, 160));
    ok('och meddelandet namnger egenskapen',
       (wide.message || '').includes('eventAudience'), wide.message);

    // Kontrollprov: standardvalet får INTE vägras — annars vore varje sparning på en händelsetyp
    // utan egenskapen blockerad, och påståendet ovan hade varit grönt av fel skäl.
    const narrow = await post('/umbraco/surface/Club/CreateClubEvent',
      { ...base, eventAudience: 'Club' });
    evId = (narrow.data && narrow.data.id) || 0;
    ok('standardvalet går igenom ändå', narrow.success && evId > 0, narrow.message);

    // Och ett skräpvärde vägras oavsett egenskapen — det är ett trasigt formulär, inte ett val.
    const junk = await post('/umbraco/surface/Club/CreateClubEvent',
      { ...base, eventAudience: 'Oppen' });
    ok('ett okänt värde vägras', !junk.success, JSON.stringify(junk).slice(0, 160));
    ok('och säger att valet är ogiltigt',
       (junk.message || '').toLowerCase().includes('giltigt'), junk.message);

  } finally {
    if (evId) {
      const del = await post0(page, evId).catch(e => String(e));
      console.log(`\nStädning: evenemang ${evId} — ${del}`);
    }
    await browser.close();
  }
  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) process.exitCode = 1;
};

async function post0(page, evId) {
  return page.evaluate(async ([u, id]) => {
    const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
    const fd = new FormData();
    fd.append('eventId', id);
    fd.append('__RequestVerificationToken', tokEl ? tokEl.value : '');
    const r = await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' });
    return `${r.status} ${(await r.text()).slice(0, 80)}`;
  }, ['/umbraco/surface/Club/DeleteClubEvent', String(evId)]);
}

main();
