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

// ⚠️⚠️ TVÄTTEN MÅSTE UTGÅ FRÅN MEDLEMMENS FAKTISKA NAMN.
//
// Första versionen letade efter en HÅRDKODAD namnlista ("Lisa Svensson") och missade därför
// medlem 1078:s riktiga namn helt — det stod kvar i föreningsintygets sidfot på båda sidorna och
// som "Sökande" på sida 2. Bilden var alltså på väg att publiceras med en möjligen verklig
// persons namn på en vapenlicensblankett. Ett publicerat skärmskott går inte att ta tillbaka.
//
// Namnet hämtas nu ur samma endpoint som ytorna själva använder, och varje NAMNDEL tvättas var
// för sig: blanketten delar upp namnet i Efternamn och Tilltalsnamn, så bara helnamnet räcker
// inte. Personnummer, e-post och telefonnummer tvättas på mönster, eftersom de dyker upp på
// ställen man inte listar i förväg.
let REAL = [], FULL = [], PARTS = [], APPLICANT = '';

// ⚠️⚠️ EN YTA KAN BÄRA FLERA IDENTITETER. Andra försöket lärde bara SÖKANDENS namn — och då stod
// ordförandens namn kvar i föreningsintygets namnförtydligande, för blanketten bär två personer:
// den som ansöker och den som skriver under på styrelsens vägnar. Kontrollen var dessutom lika
// smal som tvätten, så den godkände bilden.
//
// Därför lärs varje namn KLUBBEN känner, plus sökandens — inte de namn jag råkar tänka på. En
// lista som är för lång kostar ingenting; en som är för kort kostar en publicerad personuppgift.
const learnNames = async (memberId) => {
  const applicant = await page.evaluate(async ([b, m]) => {
    const r = await fetch(`${b}/umbraco/surface/Foreningsintyg/GetActivitySummary?memberId=${m}&year=2026`,
      { credentials: 'same-origin' });
    try { return ((await r.json()).data || {}).memberName || ''; } catch { return ''; }
  }, [B, memberId]);

  const clubNames = await page.evaluate(async ([b, c]) => {
    const r = await fetch(`${b}/umbraco/surface/ClubAdmin/GetClubMembers?clubId=${c}`,
      { credentials: 'same-origin' });
    try { return ((await r.json()).data || []).map(m => m.memberName || '').filter(Boolean); }
    catch { return []; }
  }, [B, CLUB]);

  if (!applicant) {
    throw new Error('Kunde inte läsa sökandens namn — vägrar ta bilden hellre än att gissa.');
  }

  // ⚠️ TVÅ SKILDA LISTOR, av ett skäl som kostade en bild att upptäcka. Tredje försöket bytte
  // varje NAMNDEL var den än stod, och då blev blankettens egna ord fel: en skräpmedlem i dev
  // heter något med "Pistol", så kryssrutan "Pistol" blev "Fransson" och
  // "Svenska Pistolskytteförbundet" blev "Svenska Franssonskytteförbundet". En bild där
  // förbundets namn är utbytt är oanvändbar, och felet syns bara om man läser bilden.
  //
  //   FULL  — hela namn (innehåller mellanslag). Byts var de än står. Kan inte krocka med ett
  //           domänord, och täcker både sökanden och den som skriver under.
  //   PARTS — SÖKANDENS namndelar. Byts BARA när de utgör hela fältvärdet, eftersom blanketten
  //           renderar Efternamn och Tilltalsnamn som egna fält. Aldrig i löpande text.
  APPLICANT = applicant;
  FULL = [...new Set([applicant, ...clubNames])]
    .filter(n => n.includes(' '))
    .sort((a, b) => b.length - a.length);
  PARTS = applicant.split(/\s+/).filter(x => x.length > 2);
  REAL = [...FULL, ...PARTS];
  console.log(`  tvättar bort ${FULL.length} hela namn + ${PARTS.length} namndelar `
            + `(${PARTS.join(', ')})`);
};

const scrub = () => page.evaluate(([full, parts, fake, applicant]) => {
  const esc = r => r.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const firsts = ['Karin', 'Olle', 'Majken', 'Bosse'];
  const lasts = ['Nyberg', 'Fransson', 'Ahl', 'Rylander'];

  // Regler som får gälla var som helst: hela namn, personnummer, e-post, telefonnummer.
  // ⚠️ SÖKANDEN FÖRST OCH ALLTID SAMMA FIKTIVA NAMN. Utan den bindningen fick sökandens namn
  // ett index ur en längdsorterad lista, och blanketten blev internt motstridig: sida 1 sa
  // "Karin Nyberg" (fälten fylls med det) medan sidfoten och "Sökande" på sida 2 sa någon annan.
  // Ett dokument som pekar ut två personer som samma sökande är sämre än inget dokument.
  const anywhere = [
    [new RegExp(esc(applicant), 'g'), fake[0]],
    ...full.filter(n => n !== applicant)
           .map((n, i) => [new RegExp(esc(n), 'g'), fake[1 + (i % (fake.length - 1))]]),
    [/\b(19|20)?\d{6}[-\s]?\d{4}\b/g, '19750312-XXXX'],
    [/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g, 'karin.nyberg@example.se'],
    [/\b0\d{1,3}[-\s]?\d{2,3}\s?\d{2}\s?\d{2}\b/g, '070-123 45 67'],
  ];

  // Namndelar: bara när noden ENBART innehåller namndelen. Det är så ett ifyllt fält ser ut.
  const exact = new Map();
  parts.forEach((pt, i) => exact.set(pt, i === 0 ? firsts[0] : lasts[0]));

  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  let n, hits = 0;
  while ((n = walker.nextNode())) {
    const v0 = n.nodeValue;
    if (!v0 || !v0.trim()) continue;
    let v = v0;
    if (exact.has(v0.trim())) {
      v = v0.replace(v0.trim(), exact.get(v0.trim()));
    } else {
      anywhere.forEach(([re, to]) => { v = v.replace(re, to); });
    }
    if (v !== v0) { n.nodeValue = v; hits++; }
  }
  return hits;
}, [FULL, PARTS, FAKE, APPLICANT]);

// ⚠️ KONTROLL EFTER TVÄTTEN, inte i stället för den. En tvätt som tyst missade något är värre än
// ingen tvätt, för den ger falsk trygghet. Hittas namnet kvar tas ingen bild.
const assertClean = async (what) => {
  const left = await page.evaluate(([real]) => {
    const t = document.body.innerText || '';
    return real.filter(r => t.includes(r));
  }, [REAL]);
  if (left.length) {
    throw new Error(`${what}: namnet står KVAR efter tvätten (${left.join(', ')}) — ingen bild tas.`);
  }
};

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


  // ── 6. Aktivitetssammanställningen ────────────────────────────────────────────────────────
  //
  // ⚠️ RENDERAS FÖR EN ANNAN MEDLEM ÄN DEN INLOGGADE, med flit: kontot sviten kör som har noll
  // registrerad verksamhet i dev, och en tom sammanställning visar ingenting av det funktionen
  // gör. Medlem 1078 har 41 aktivitetsdagar 2026. Samma renderare används av både Min sida och
  // klubbens Aktivitet-sida, så bilden är sann för båda ytorna.
  await page.setViewportSize({ width: 1000, height: 1400 });
  await page.goto(`${B}/user-profile-page/`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1200);
  await page.click('[data-bs-target="#activity-pane"]');
  await page.waitForTimeout(1500);
  await learnNames(1078);
  await page.evaluate(async () => {
    await window.hpskLoadActivitySummary('myActivitySummary', 1078, 2026);
  });
  await page.waitForTimeout(2500);
  await scrub();
  await assertClean('aktivitet');
  await shot('aktivitet.png', '#activity-pane');

  // ── 7. Föreningsintyget ───────────────────────────────────────────────────────────────────
  //
  // ⚠️ MEST KÄNSLIGA YTAN PÅ HELA SAJTEN. Blanketten bär namn, personnummer, adress, telefon och
  // vapenuppgifter. INGET av det får ut. Fälten fylls därför med påhittade värden i DOM:en före
  // bilden — dev-datat rörs inte, och läsaren ser en ifylld blankett i stället för en med
  // "uppgifter saknas", vilket också är den rättvisande bilden av en klubb som har sitt register
  // i ordning.
  await page.setViewportSize({ width: 1000, height: 1500 });
  await page.goto(`${B}/foreningsintyg/utkast?memberId=1078&clubId=${CLUB}`,
                  { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(1800);

  // ⚠️ ORDNINGEN ÄR TVÄTT FÖRST, IFYLLNAD SEDAN. Omvänt skrev personnummermönstret över det
  // påhittade organisationsnumret — blanketten visade då samma siffra på båda raderna, vilket
  // både ser fel ut och antyder att fälten hänger ihop.
  await scrub();
  await page.evaluate(() => {
    const set = (label, value) => {
      // Blanketten är fältetiketter med ett värde intill. Hitta etiketten, skriv i värdet.
      const nodes = Array.from(document.querySelectorAll('*'))
        .filter(e => e.children.length === 0 && e.textContent.trim() === label);
      nodes.forEach(n => {
        const holder = n.parentElement;
        if (!holder) return;
        const val = Array.from(holder.children).find(c => c !== n);
        if (val) val.textContent = value;
      });
    };
    set('Efternamn', 'Nyberg');
    set('Tilltalsnamn', 'Karin');
    set('Personnummer eller organisationsnummer', '19750312-XXXX');
    set('E-postadress', 'karin.nyberg@example.se');
    set('Adress', 'Banvägen 12');
    set('Postnummer', '432 00');
    set('Ort', 'Varberg');
    set('Telefon', '');
    set('Telefon (mobil)', '070-123 45 67');
    set('Organisationsnummer', '802000-0000');
    set('Har varit medlem kontinuerligt sedan datum', '2019-04-01');
    // ⚠️ Varningen om saknade registeruppgifter gäller DEV-MEDLEMMEN, inte funktionen, och den
    // dominerade bilden. Klassväljaren tog den inte — den letas nu på sin text i stället, vilket
    // är det enda som säkert matchar oavsett hur rutan är uppbyggd.
    Array.from(document.querySelectorAll('div,section,aside'))
      .filter(e => /Uppgifter saknas i medlemsregistret/.test(e.textContent || '')
                   && e.children.length < 8)
      .forEach(e => e.remove());
  });
  await assertClean('foreningsintyg');
  // ⚠️ BARA SIDA 1. Hela blanketten är två A4 i höjd, och med Om-sidans höjdtak krympte den till
  // 175 px bredd — alltså en bild som visar att det finns ett formulär utan att gå att läsa. Sida
  // 1 bär personuppgifterna, föreningen, förbundsvalet och vapenraden, vilket är det bilden ska
  // visa; sida 2 är kryssrutor för enhandsvapen och underskriften.
  await shot('foreningsintyg.png', '.sheet >> nth=0');

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
