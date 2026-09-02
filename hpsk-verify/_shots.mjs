// _shots.mjs — skärmskott till /om-pistol-nu/ för lånevapen.
//
// ⚠️ INGA RIKTIGA PERSONNAMN I ETT PUBLIKT SKÄRMSKOTT. Vem som lånat vilket vapen är känsligt,
// och dev-databasens medlemmar kan vara riktiga personer — det går inte att veta här. Skriptet
// skriver därför över varje namn i DOM:en med fiktiva namn INNAN bilden tas. Layouten är äkta,
// personerna är påhittade. Demoklubben Ankeborg finns bara i prod, annars hade den varit rätt val.
//
// ⚠️ SKRIVER I DEV och städar allt i finally, prefix `ZZS `.
//
// KÖR: node hpsk-verify/_shots.mjs

import { chromium } from 'playwright';
import fs from 'fs';

const B = 'http://localhost:18150';
const OUT = 'C:/Repos/HpskSite/src/HpskSite/wwwroot/images/about';
const CLUB = 2604;
// ⚠️ RIKTIGA VAPENNAMN, inget testprefix. Ett publikt skärmskott med "ZZS Pardini" ser ut som
// att produkten är ett labb. Städningen sker på id, inte på namn, så prefixet behövs inte.
const NAMES = ['Pardini SP', 'Walther GSP', 'Ruger Mark IV'];
const OTHER = 5601;

// Fiktiva namn som ersätter det som står i DOM:en.
const FAKE = ['Karin Nyberg', 'Olle Fransson', 'Majken Ahl', 'Bosse Rylander'];

const br = await chromium.launch({ headless: true });
const ctx = await br.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();

// ⚠️ MÖRKT TEMA. /om-pistol-nu/ tvingar `data-bs-theme="dark"` på hela ytan just för att
// skärmskotten är mörka — ett ljust skott blir en vit fläck i en mörk sida. Temat sätts i
// localStorage, samma nyckel som sajtens växlare, INNAN någon sida laddas: de fristående
// lånevapensidorna läser den i sin head. Affischen och etikettarket är utskrifter och ska
// förbli svart på vitt — papper är inte mörkt.
await ctx.addInitScript(() => {
  try { localStorage.setItem('hpsk-theme', 'dark'); } catch (e) { /* strunt samma */ }
});

let wA = null, wB = null, wC = null;

const api = async (url, fields) => page.evaluate(async ([u, f]) => {
  const tokEl = document.querySelector('input[name="__RequestVerificationToken"]');
  if (f) {
    const fd = new FormData();
    Object.keys(f).forEach(k => fd.append(k, f[k]));
    fd.append('__RequestVerificationToken', tokEl ? tokEl.value : '');
    const r = await fetch(u, { method: 'POST', body: fd, credentials: 'same-origin' });
    const t = await r.text();
    try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 160) }; }
  }
  const r = await fetch(u, { credentials: 'same-origin' });
  const t = await r.text();
  try { return JSON.parse(t); } catch { return { success: false, _raw: t.slice(0, 160) }; }
}, [url, fields || null]);

const home = () => page.goto(`${B}/user-profile-page/`, { waitUntil: 'domcontentloaded' });

// Byter ut alla personnamn i en lista av selektorer mot de fiktiva.
const anonymise = (selectors) => page.evaluate(([sels, names]) => {
  let i = 0;
  const seen = new Map();
  sels.forEach(sel => {
    document.querySelectorAll(sel).forEach(el => {
      const key = el.textContent.trim();
      if (!seen.has(key)) seen.set(key, names[i++ % names.length]);
      el.textContent = seen.get(key);
    });
  });
  return [...seen.values()];
}, [selectors, FAKE]);

const shot = async (name, target) => {
  const p = `${OUT}/${name}`;
  await (target ? page.locator(target).screenshot({ path: p })
                : page.screenshot({ path: p }));
  const kb = Math.round(fs.statSync(p).size / 1024);
  console.log(`  ${name}  ${kb} kB`);
};

try {
  await page.goto(`${B}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
  await page.fill('input[name="loginModel.Username"]', 'admin.claude@pistol.nu');
  await page.fill('input[name="loginModel.Password"]', '123456');
  await page.click('button[type=submit], input[type=submit]');
  await page.waitForLoadState('domcontentloaded');
  await home();

  // ⚠️ DEV BÄR RESTER FRÅN VARJE SVITKÖRNING. Etikettarket och bokningslistan visade "ZZS Ett
  // nr 71" i dubbletter — sant för dev, men det gör skärmskottet oanvändbart. Rester göms via
  // appens egen väg (RemoveClubFirearm sätter IsActive=0), inte med SQL: samma regler ska gälla.
  const stale = await api(`/umbraco/surface/FirearmAdmin/GetClubFirearms?clubId=${CLUB}`);
  let hidden = 0;
  for (const f of (stale.firearms || [])) {
    if (!/^ZZ/.test(f.alias || '')) continue;
    const r = await api('/umbraco/surface/FirearmAdmin/RemoveClubFirearm',
      { clubId: CLUB, firearmId: f.id });
    if (r.success) hidden++;
  }
  console.log('gamla svit-vapen gömda:', hidden);

  // ── Fixtur: tre lånevapen, nr 7 först eftersom det är historien ("jag har alltid nr 7") ──
  for (const w of [{ a: NAMES[0], n: 7 }, { a: NAMES[1], n: 12 }, { a: NAMES[2], n: 23 }]) {
    await api('/umbraco/surface/FirearmAdmin/SaveClubFirearm', {
      clubId: CLUB, id: 0, alias: w.a, weaponClass: 'C', vapentyp: 'Pistol',
      number: w.n, isLoanable: true, status: 'Tillgängligt',
      licenseExpiresOn: '', federations: '', disciplines: '', writeDetails: '0',
    });
  }
  const cl = await api(`/umbraco/surface/FirearmAdmin/GetClubFirearms?clubId=${CLUB}`);
  const find = n => (cl.firearms || []).find(x => x.alias === n);
  wA = find(NAMES[0]); wB = find(NAMES[1]); wC = find(NAMES[2]);
  console.log('vapen:', wA && wA.number, wB && wB.number, wC && wC.number);

  const today = new Date().toISOString().slice(0, 10);

  // Två som ska ut (en med önskat nummer, en som tar vilket som helst) …
  const b1 = await api('/umbraco/surface/Firearm/BookLoanWeapon',
    { firearmId: wA.id, clubId: CLUB, occasionKind: 'Fritt', occasionId: 0, from: today, to: '' });
  const b2 = await api('/umbraco/surface/FirearmAdmin/WalkInLoan',
    { clubId: CLUB, memberId: OTHER, firearmId: 0, occasionKind: 'Fritt', occasionId: 0 });
  // … och en som redan är ute.
  const b3 = await api('/umbraco/surface/FirearmAdmin/WalkInLoan',
    { clubId: CLUB, memberId: OTHER, firearmId: wC.id, occasionKind: 'Fritt', occasionId: 0 });
  console.log('bokningar:', b1.success, b2.success, b3.success);

  // ── 1. Valvet. Telefonbredd, för det är en telefonsida ────────────────────────────────────
  await page.setViewportSize({ width: 440, height: 1000 });
  await page.goto(`${B}/valvet?club=${CLUB}`, { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => {
    const b = document.getElementById('vtBoard');
    return b && !b.querySelector('.spinner-border');
  }, null, { timeout: 20000 });
  const used = await anonymise(['.vt-name']);
  console.log('  namn ersatta med:', used.join(', '));
  await shot('lanevapen-valvet.png', '.vt-wrap');

  // ── 2. Affischen till valvväggen ──────────────────────────────────────────────────────────
  await page.setViewportSize({ width: 900, height: 1240 });
  await page.goto(`${B}/valvet/affisch?club=${CLUB}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1200);
  // ⚠️ Reservadressen på affischen är dev-värden (`localhost:18150`). Att publicera den skulle
  // både se oseriöst ut och lära läsaren en adress som inte finns. Texten byts mot den riktiga —
  // det är exakt vad en klubb får på sin egen affisch.
  await page.evaluate(() => {
    const u = document.querySelector('.url');
    if (u) u.textContent = 'https://pistol.nu/valvet?club=2604';
  });
  await shot('lanevapen-affisch.png', '.sheet');

  // ── 3. Etiketterna ────────────────────────────────────────────────────────────────────────
  // Utskriftssidorna ska vara ljusa — de föreställer papper.
  await page.setViewportSize({ width: 1000, height: 800 });
  await page.goto(`${B}/valvet/etiketter?club=${CLUB}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1500);
  await shot('lanevapen-etiketter.png', 'body');

  // ── 4. Skanningen, som skytten ser den ────────────────────────────────────────────────────
  const labels = await page.evaluate(() =>
    Array.from(document.querySelectorAll('.lab')).map(el => ({
      url: el.getAttribute('data-label-url'),
      id: parseInt(el.getAttribute('data-firearm-id') || '0', 10),
    })));
  const lab = labels.find(l => l.id === wB.id);
  await page.setViewportSize({ width: 440, height: 780 });
  await page.goto(lab.url.replace(/^https?:\/\/[^/]+/, B), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1500);
  await shot('lanevapen-skanna.png', 'body');

  // ── 5. Medlemmens bokningslista ───────────────────────────────────────────────────────────
  // ⚠️ Listan visar bara nummer, namn på vapnet och status — aldrig VEM som bokat. Därför behövs
  // ingen anonymisering här, och det är också själva poängen: bara du ser dina lån.
  await page.setViewportSize({ width: 520, height: 900 });
  await page.goto(`${B}/lanevapen?club=${CLUB}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2000);
  await shot('lanevapen-boka.png', '.lv-wrap');

} finally {
  try {
    await page.setViewportSize({ width: 1280, height: 800 });
    await home();
    const mine = await api(`/umbraco/surface/FirearmAdmin/GetClubBookings?clubId=${CLUB}`);
    for (const b of (mine.bookings || [])) {
      await api('/umbraco/surface/FirearmAdmin/SetBookingState',
        { clubId: CLUB, bookingId: b.id, action: 'return', reason: '' });
    }
    for (const f of [wA, wB, wC]) {
      if (f) await api('/umbraco/surface/FirearmAdmin/RemoveClubFirearm',
        { clubId: CLUB, firearmId: f.id });
    }
    console.log('(städat)');
  } catch (e) {
    console.log('⚠️ städningen fallerade:', e.message, '— rensa alias som börjar på', P);
  }
  await br.close();
}
