// event-dialog-no-training-verify.mjs — händelsedialogerna utan typen Träning och utan lånevapen,
// utan att en GAMMAL träningshändelse tappar typ eller lånevapen när den sparas.
//
// KÖR:  HPSK_USER="admin.claude@pistol.nu" HPSK_PASS="123456" node hpsk-verify/event-dialog-no-training-verify.mjs
//
// Fixturen ("ZZED …", klubb 2604) skapas via CreateClubEvent — servern tar fortfarande emot typen
// Träning och lånevapen, precis som de gamla händelserna i prod har dem — och raderas i finally.
import { chromium } from 'playwright';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const CLUB_ID = 2604;
const PREFIX = 'ZZED ';

let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);
const day = k => { const d = new Date(); d.setDate(d.getDate() + k); return d.toISOString().slice(0, 10); };

const main = async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  page.on('dialog', d => d.dismiss().catch(() => {}));
  const created = [];
  const form = (u, f) => page.evaluate(async ([u, f]) => {
    const fd = new FormData(); Object.keys(f).forEach(k => fd.append(k, f[k]));
    fd.append('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]')?.value || '');
    const t = await (await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' })).text();
    try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 150) }; }
  }, [u, f]);
  const get = u => page.evaluate(async u => { const t = await (await fetch(u, { credentials: 'same-origin' })).text(); try { return JSON.parse(t); } catch { return { success: false }; } }, u);
  try {
    await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER || '');
    await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS || '');
    await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
    await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});
    await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded', timeout: 120000 });
    if (await page.locator('#firearms-member-tab').count() === 0) { console.error('AVBRYTER: inte inloggad.'); process.exitCode = 1; return; }

    section('Fixtur');
    const mk = async (name, type, loan) => {
      const r = await form('/umbraco/surface/Club/CreateClubEvent', {
        clubId: CLUB_ID, eventName: PREFIX + name, eventDate: `${day(12)} 18:00`, description: '', venue: 'Hålan',
        eventType: type, contactPerson: '', contactEmail: '', contactPhone: '', registrationRequired: 'true',
        maxParticipants: 0, registrationUrl: '', isMandatory: 'false', lanevapenOffered: loan ? 'true' : 'false',
        registrationDeadline: '', eventPrices: '' });
      const id = (r.data && r.data.id) || 0; if (id) created.push(id); return id;
    };
    const oldTraining = await mk('Gammal träning', 'Träning', true);
    const cleaning = await mk('Städdag', 'Städning', false);
    ok('två fixturhändelser skapades', oldTraining > 0 && cleaning > 0);
    if (!oldTraining || !cleaning) return;

    await page.goto(`${BASE}/halland/klubbar/haaplinge-goass/`, { waitUntil: 'domcontentloaded', timeout: 120000 });
    await page.evaluate(() => document.getElementById('cookieConsentBanner')?.remove());
    await page.click('#clubAdmin-tab');
    await page.waitForFunction(ids => typeof allEvents !== 'undefined' && ids.every(id => allEvents.some(e => e.id === id)),
      [oldTraining, cleaning], { timeout: 30000 }).catch(() => {});

    section('Ny händelse');
    const opts = await page.$$eval('#eventType option', o => o.map(x => x.value));
    ok('typen Träning finns inte i listan', !opts.includes('Träning'), opts.join(','));
    ok('men de andra typerna finns kvar', ['Städning', 'Möte', 'Socialt'].every(t => opts.includes(t)), opts.join(','));
    await page.evaluate(() => showCreateEventModal());
    await page.waitForSelector('#eventModal.show', { timeout: 10000 });
    ok('lånevapenrutan syns inte på en ny händelse', await page.locator('#clubEventLanevapenWrap').evaluate(e => e.classList.contains('d-none')));
    await page.evaluate(() => bootstrap.Modal.getInstance(document.getElementById('eventModal')).hide());
    await page.waitForSelector('#eventModal.show', { state: 'detached', timeout: 10000 }).catch(() => {});
    await page.waitForTimeout(400);

    section('Gammal träningshändelse');
    await page.evaluate(id => editEvent(id), oldTraining);
    await page.waitForSelector('#eventModal.show', { timeout: 10000 });
    eq('typen visas som Annat…', await page.inputValue('#eventType'), 'Annat');
    eq('med Träning i fritextfältet', await page.inputValue('#eventCustomType'), 'Träning');
    ok('lånevapenrutan VISAS, eftersom händelsen har lånevapen', !(await page.locator('#clubEventLanevapenWrap').evaluate(e => e.classList.contains('d-none'))));
    ok('och är ikryssad', await page.isChecked('#clubEventLanevapenOffered'));
    // Spara utan att röra något — typ och lånevapen ska överleva.
    await page.click('#eventSaveBtn');
    await page.waitForSelector('#eventModal.show', { state: 'detached', timeout: 20000 }).catch(() => {});
    await page.waitForTimeout(1500);
    const ev = await get(`/umbraco/surface/Club/GetClubEvents?clubId=${CLUB_ID}`);
    const after = (ev.data || []).find(e => e.id === oldTraining);
    eq('typen är fortfarande Träning efter sparning', after?.type, 'Träning');
    eq('lånevapnen är kvar efter sparning', after?.lanevapenOffered, true);

    section('Kontrollprov');
    await page.waitForTimeout(500);
    await page.evaluate(id => editEvent(id), cleaning);
    await page.waitForSelector('#eventModal.show', { timeout: 10000 });
    eq('en vanlig städdag visas med sin typ', await page.inputValue('#eventType'), 'Städning');
    ok('och utan lånevapenruta', await page.locator('#clubEventLanevapenWrap').evaluate(e => e.classList.contains('d-none')));
  } finally {
    for (const id of created) {
      await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded' }).catch(() => {});
      await page.evaluate(async id => {
        const fd = new FormData(); fd.append('eventId', id);
        fd.append('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]')?.value || '');
        await fetch('/umbraco/surface/Club/DeleteClubEvent', { method: 'POST', body: fd, credentials: 'same-origin' });
      }, String(id)).catch(() => {});
    }
    console.log(created.length ? `\n(städat ${created.length} händelser)` : '');
    await browser.close();
  }
  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log('  - ' + f)); process.exitCode = 1; }
};
main().catch(e => { console.error(e); process.exitCode = 1; });
