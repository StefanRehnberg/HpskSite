// event-modal-rebuild-verify.mjs — händelsedialogerna: guide vid skapande, flikar vid redigering.
//
// KÖR:  node hpsk-verify/event-modal-rebuild-verify.mjs
//
// ⚠️ SVITEN SKAPAR INGEN HÄNDELSE. Den öppnar dialogerna, mäter kromet och stänger dem igen.
// Sparknappen trycks aldrig — ingen nod, ingen deltagarrad, inget att städa.
//
// ⚠️⚠️ TVÅ PÅSTÅENDEN BÄR HELA OMBYGGNADEN, och de är lätta att utelämna:
//
//  1. LÄGESVÄXLINGEN. Klubbens och kretsens dialog är EN modal som används för BÅDA
//     presentationerna. Utan `hpskModalTabs` blir den permanent en guide efter det första
//     skapandet — prickar i stället för flikar, knappar som inte går att klicka — och
//     ingenting felar. Sviten kör därför skapa → redigera → skapa i EN session.
//
//  2. ATT INGET FÄLT HAMNAR UTANFÖR ETT AVSNITT. Grupperingen flyttar noder, och en nod som
//     ingen rubrik fångar in blir kvar utanför flikväxlingen. Det är samma familj som
//     "fältlista-som-glömmer", som bitit den här ytan fem gånger: fältet finns i markupen,
//     ser rätt ut i en kodläsning, och är ändå inte med.

import { chromium } from 'playwright';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
const CLUB = `${BASE}/halland/klubbar/ankeborg-pistolklubb/`;
const REGION = `${BASE}/halland/`;
const EVENTPAGE = `${BASE}/halland/klubbar/ankeborg-pistolklubb/staddag-pa-banan/`;

let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);

// ⚠️ Avgift och På plats finns BARA när anmälan krävs — ett avsnitt utan höjd faller ur
// raden. Det är själva poängen med uppdelningen: inga flikar för något som inte gäller.
const SEC_BASE = ['Grunduppgifter', 'Plats och beskrivning', 'Kontakt', 'Anmälan'];
const SEC_FULL = [...SEC_BASE, 'Avgift och betalning', 'På plats'];

// Läser hela kromet i ETT svep, så ingen assertion kan mäta ett annat ögonblick än nästa.
const readModal = (page, sel, btns) => page.evaluate(([sel, btns]) => {
  const modal = document.querySelector(sel);
  const body = modal.querySelector('.modal-body');
  const nav = body.querySelector('.hpsk-sectabs');
  const secs = [...body.querySelectorAll('.hpsk-section')];
  const vis = el => !!el && el.offsetParent !== null;
  const b = id => document.getElementById(id);

  // ⚠️ Fält UTANFÖR varje avsnitt är det tysta felet. Räkna båda mängderna.
  const controls = [...body.querySelectorAll('input, select, textarea')];
  const outside = controls.filter(c => !c.closest('.hpsk-section')).map(c => c.id || c.name || '(namnlös)');

  return {
    isSteps: nav ? nav.classList.contains('hpsk-steps') : null,
    titles: nav ? [...nav.querySelectorAll('button')].map(x => x.textContent) : [],
    disabled: nav ? [...nav.querySelectorAll('button')].map(x => x.disabled) : [],
    shown: secs.map(s => !s.hidden),
    controls: controls.length,
    outside,
    back: vis(b(btns.back)), backDisabled: b(btns.back) ? b(btns.back).disabled : null,
    next: vis(b(btns.next)),
    save: vis(b(btns.save)), saveText: b(btns.save) ? b(btns.save).innerText.trim() : '',
    title: document.getElementById(btns.title) ? document.getElementById(btns.title).textContent : '',
  };
}, [sel, btns]);

const closeModal = (page, sel) => page.evaluate(s => {
  const m = bootstrap.Modal.getInstance(document.querySelector(s));
  if (m) m.hide();
}, sel).then(() => page.waitForTimeout(600));

const main = async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  const jsErrors = [];
  page.on('pageerror', e => jsErrors.push(e.message));

  try {
    // ── Inloggning ────────────────────────────────────────────────────────────────────────
    await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER);
    await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS);
    await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
    await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});

    // Cookie-bannern ligger position:fixed langst ned och fangar klick pa modalens sidfot.
    // Den TAS BORT i testet i stallet for att klickas i - att trycka pa en samtyckesknapp ar
    // ett val, och det ar inte svitens att gora.
    const dropBanner = () => page.evaluate(() => {
      const b = document.getElementById('cookieConsentBanner');
      if (b) b.remove();
    }).catch(() => {});

    const go = async url => {
      for (let i = 0; i < 3; i++) {
        try {
          await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 120000 });
          await dropBanner();
          return true;
        }
        catch { await page.waitForTimeout(3000); }
      }
      return false;
    };

    await go(`${BASE}/user-profile-page/`);
    if (await page.locator('#firearms-member-tab').count() === 0) {
      console.error('\nAVBRYTER: inte inloggad — varje påstående nedan hade mätt en sida utan adminpanelen.');
      process.exitCode = 1;
      return;
    }

    // ══════════════════════════════════════════════════════ KLUBBEN
    await go(CLUB);
    section('Klubben — förutsättning');
    const api = await page.evaluate(() => ({
      wizard: typeof window.hpskModalWizard === 'function',
      tabs: typeof window.hpskModalTabs === 'function',
      create: typeof window.showCreateEventModal === 'function',
    }));
    ok('partialen är laddad på klubbsidan', api.wizard && api.tabs, JSON.stringify(api));
    if (!api.wizard || !api.tabs) return;

    await page.click('#clubAdmin-tab').catch(() => {});
    await page.waitForTimeout(1200);
    ok('Ny händelse-knappen finns', await page.locator('button[onclick="showCreateEventModal()"]').count() > 0);

    // ── Guide vid skapande ────────────────────────────────────────────────────────────────
    section('Klubben — guide vid skapande');
    await page.click('button[onclick="showCreateEventModal()"]');
    await page.waitForTimeout(900);
    const B = { back: 'eventWizBack', next: 'eventWizNext', save: 'eventSaveBtn', title: 'eventModalTitle' };
    let m = await readModal(page, '#eventModal', B);

    eq('fyra avsnitt när anmälan inte krävs', m.titles, SEC_BASE);
    eq('stegraden är i guideläge', m.isSteps, true);
    eq('stegknapparna går inte att klicka', m.disabled, [true, true, true, true]);
    eq('bara första avsnittet syns', m.shown.indexOf(true), 0);
    eq('och exakt ETT avsnitt åt gången', m.shown.filter(Boolean).length, 1);
    ok('INGET fält hamnade utanför ett avsnitt', m.outside.length === 0, m.outside.join(', '));
    ok('och det finns fält att gruppera alls', m.controls > 10, `${m.controls} kontroller`);
    ok('Bakåt syns men är avstängd på steg 1', m.back && m.backDisabled);
    ok('Nästa syns', m.next);
    ok('Spara är GÖMD tills sista steget', !m.save);
    ok('rubriken räknar stegen', /steg 1 av 4/.test(m.title), m.title);

    // Valideringen stoppar det synliga steget
    await page.click('#eventWizNext');
    await page.waitForTimeout(300);
    m = await readModal(page, '#eventModal', B);
    eq('Nästa vägras med tomt händelsenamn', m.shown.indexOf(true), 0);
    ok('fältet märks ut', await page.locator('#eventName.is-invalid').count() === 1);

    await page.fill('#eventName', 'ZZ Guidetest');
    await page.click('#eventWizNext');
    await page.waitForTimeout(300);
    m = await readModal(page, '#eventModal', B);
    eq('med namnet ifyllt går Nästa igenom', m.shown.indexOf(true), 1);
    ok('Bakåt går att klicka på steg 2', m.back && !m.backDisabled);
    ok('rubriken följde med', /steg 2 av 4/.test(m.title), m.title);

    await page.click('#eventWizNext'); await page.waitForTimeout(250);
    await page.click('#eventWizNext'); await page.waitForTimeout(350);
    m = await readModal(page, '#eventModal', B);
    eq('sista avsnittet syns', m.shown.indexOf(true), 3);
    ok('Nästa är borta på sista steget', !m.next);
    ok('Spara syns nu', m.save);
    ok('och heter Skapa händelsen', /Skapa h/i.test(m.saveText), m.saveText);

    await page.click('#eventWizBack'); await page.waitForTimeout(300);
    m = await readModal(page, '#eventModal', B);
    eq('Bakåt går tillbaka', m.shown.indexOf(true), 2);
    ok('och Spara göms igen', !m.save);

    // ⚠⚠ Anmälan delar sig i tre när den slås på — OCH guiden får inte kasta om till steg 1.
    section('Klubben — Anmälan delar sig i tre');
    await page.click('#eventWizNext'); await page.waitForTimeout(350);
    m = await readModal(page, '#eventModal', B);
    eq('vi står på Anmälan, sista av fyra', m.shown.indexOf(true), 3);
    ok('och Spara syns därför', m.save);

    await page.check('#clubEventRegistrationRequired');
    await page.waitForTimeout(600);
    m = await readModal(page, '#eventModal', B);
    eq('nu finns sex avsnitt', m.titles, SEC_FULL);
    eq('och vi står KVAR på Anmälan', m.shown.indexOf(true), 3);
    eq('fortfarande ETT avsnitt åt gången', m.shown.filter(Boolean).length, 1);
    ok('Nästa är tillbaka — Anmälan är inte längre sist', m.next);
    ok('Spara göms igen', !m.save);
    ok('INGET fält utanför ett avsnitt efter ombyggnaden', m.outside.length === 0, m.outside.join(', '));

    await page.click('#eventWizNext'); await page.waitForTimeout(350);
    m = await readModal(page, '#eventModal', B);
    eq('Nästa leder till Avgift och betalning', m.shown.indexOf(true), 4);
    ok('avgiftsfältet syns där', await page.locator('#clubEventPriceSingle').isVisible());
    ok('Swish-numret också', await page.locator('#clubEventSwishNumber').isVisible());
    ok('men obligatoriskt deltagande INTE — det hör till På plats',
       !(await page.locator('#clubEventIsMandatory').isVisible()));

    await page.click('#eventWizNext'); await page.waitForTimeout(350);
    m = await readModal(page, '#eventModal', B);
    eq('och därefter På plats', m.shown.indexOf(true), 5);
    ok('obligatoriskt deltagande syns där', await page.locator('#clubEventIsMandatory').isVisible());
    ok('lånevapen också', await page.locator('#clubEventLanevapenOffered').isVisible());
    ok('och nu är det sista steget igen', m.save && !m.next);

    // ⚠️ Kontrollprov åt andra hållet. Utan det kunde "sex avsnitt" lika gärna betyda att de
    // två alltid renderas — alltså ett påstående som inte kan falla.
    // Kryssrutan bor i Anmalan-avsnittet, och vi star pa Pa plats - ett dolt falt gar inte
    // att klicka. Backa dit forst; det provar dessutom Bakat over de NYA avsnitten.
    await page.click('#eventWizBack'); await page.waitForTimeout(300);
    await page.click('#eventWizBack'); await page.waitForTimeout(350);
    m = await readModal(page, '#eventModal', B);
    eq('Bakat tar oss tillbaka till Anmalan', m.shown.indexOf(true), 3);
    await page.uncheck('#clubEventRegistrationRequired');
    await page.waitForTimeout(600);
    m = await readModal(page, '#eventModal', B);
    eq('urkryssad ger fyra avsnitt igen', m.titles, SEC_BASE);

    // —— ⚠⚠ ETT OGILTIGT FÄLT FÅR ALDRIG GÖRA SPARA TYST ——
    //
    // Rapporterat 2026-09-19: "jag klickar på Skapa händelsen men inget händer". Orsaken var
    // att valideringen bara tittade på [required]. En felskriven e-post är inte obligatorisk,
    // så den släpptes igenom både på sitt eget steg och av hpskWizardValidateAll — varefter
    // anroparens form.checkValidity() sa nej och reportValidity() inte kunde visa någon bubbla
    // på ett fält i ett dölt avsnitt. Enda spåret var en konsolrad i webbläsaren:
    // "An invalid form control with name='contactEmail' is not focusable."
    //
    // Två halvor mäts, och båda behövs: guiden ska stoppa felet PÅ SITT EGET STEG, och
    // flikläget — där man aldrig "passerar" några steg — ska hoppa dit från Spara.
    section('Klubben — ogiltigt fält blockerar, högljutt');
    await closeModal(page, '#eventModal');
    await page.click('button[onclick="showCreateEventModal()"]');
    await page.waitForTimeout(900);
    await page.fill('#eventName', 'ZZ Validering');
    await page.evaluate(() => {
      const f = document.getElementById('eventDate');
      if (f._flatpickr) f._flatpickr.setDate('2026-10-15 18:00', true); else f.value = '2026-10-15 18:00';
    });
    await page.evaluate(() => { document.getElementById('eventContactEmail').value = 'kalle'; });

    await page.click('#eventWizNext'); await page.waitForTimeout(250);
    await page.click('#eventWizNext'); await page.waitForTimeout(300);
    m = await readModal(page, '#eventModal', B);
    eq('vi når Kontakt-steget', m.shown.indexOf(true), 2);

    await page.click('#eventWizNext'); await page.waitForTimeout(350);
    m = await readModal(page, '#eventModal', B);
    eq('Nästa vägras av den felskrivna e-posten — inte bara av tomma obligatoriska fält',
       m.shown.indexOf(true), 2);
    ok('och fältet är utmärkt', await page.locator('#eventContactEmail.is-invalid').count() === 1);

    // ⚠️ Kontrollprov: rättas fältet ska Nästa släppa igenom. Utan det kunde påståendet
    // ovan lika gärna betyda att Kontakt-steget ALLTID vägrar.
    await page.fill('#eventContactEmail', 'kalle@example.com');
    await page.click('#eventWizNext'); await page.waitForTimeout(350);
    m = await readModal(page, '#eventModal', B);
    eq('rättat fält släpper igenom', m.shown.indexOf(true), 3);
    await closeModal(page, '#eventModal');

    // ── ⚠️⚠️ Flikar vid redigering, i SAMMA session ───────────────────────────────────────
    section('Klubben — flikar vid redigering (samma modal)');
    await page.waitForTimeout(600);
    const opened = await page.evaluate(() => {
      const btn = document.querySelector('#eventsTab button[onclick^="editEvent("]');
      if (!btn) return false;
      btn.click();
      return true;
    });
    ok('en händelse gick att öppna för redigering', opened,
       'ingen rad i listan — kontrollera att klubben har en händelse');
    if (opened) {
      await page.waitForTimeout(900);
      m = await readModal(page, '#eventModal', B);
      ok('samma avsnitt som vid skapande, plus de anmälningsberoende',
         JSON.stringify(m.titles) === JSON.stringify(SEC_BASE)
         || JSON.stringify(m.titles) === JSON.stringify(SEC_FULL), JSON.stringify(m.titles));
      eq('men INTE i guideläge', m.isSteps, false);
      ok('flikknapparna går att klicka',
         m.disabled.length > 0 && m.disabled.every(d => d === false), JSON.stringify(m.disabled));
      ok('Bakåt och Nästa är borta', !m.back && !m.next);
      ok('Spara syns', m.save);
      ok('och heter Spara igen', /^Spara/i.test(m.saveText), m.saveText);
      ok('INGET fält utanför ett avsnitt i flikläget heller', m.outside.length === 0, m.outside.join(', '));

      await page.click('#eventModal .hpsk-sectabs button:nth-child(3)');
      await page.waitForTimeout(300);
      m = await readModal(page, '#eventModal', B);
      eq('ett klick byter avsnitt', m.shown.indexOf(true), 2);
      eq('och bara ETT avsnitt syns', m.shown.filter(Boolean).length, 1);

      // ⚠⚠ FLIKLÄGETS HÄLFT av samma fel. Här passerar man inga steg, så ingen stegvalidering
      // hinner fånga något — Spara är enda tillfället, och då MÅSTE den hoppa till fliken som
      // bär felet. Gör den inte det är resultatet en knapp som inte gör någonting.
      await page.evaluate(() => { document.getElementById('eventContactEmail').value = 'kalle'; });
      await page.click('#eventModal .hpsk-sectabs button:nth-child(1)');
      await page.waitForTimeout(250);

      let editPosted = false;
      const onEditReq = r => { if (/\/Club\/(Edit|Create)ClubEvent/.test(r.url())) editPosted = true; };
      page.on('request', onEditReq);
      await page.click('#eventSaveBtn');
      await page.waitForTimeout(1200);
      page.off('request', onEditReq);

      m = await readModal(page, '#eventModal', B);
      ok('ingenting sparades', !editPosted);
      ok('dialogen står kvar öppen', await page.locator('#eventModal.show').count() === 1);
      eq('och Spara HOPPADE till Kontakt, där felet finns', m.shown.indexOf(true), 2);
      ok('fältet är utmärkt', await page.locator('#eventContactEmail.is-invalid').count() === 1);

      await closeModal(page, '#eventModal');
    }

    // ── ⚠️⚠️ ...och tillbaka till guiden ──────────────────────────────────────────────────
    section('Klubben — guiden går att komma tillbaka till');
    await page.click('button[onclick="showCreateEventModal()"]');
    await page.waitForTimeout(900);
    m = await readModal(page, '#eventModal', B);
    eq('modalen är i guideläge igen', m.isSteps, true);
    eq('och står på steg 1', m.shown.indexOf(true), 0);
    ok('Nästa är tillbaka', m.next);
    ok('Spara är gömd igen', !m.save);
    await closeModal(page, '#eventModal');

    // ══════════════════════════════════════════════════════ KRETSEN
    section('Kretsen');
    await go(REGION);
    const rApi = await page.evaluate(() => typeof window.showCreateRegionEventModal === 'function'
                                        && typeof window.hpskModalTabs === 'function');
    ok('kretsens adminpanel är laddad', rApi,
       'kontot ser inte kretsens adminpanel — varje påstående nedan hade mätt ingenting');
    if (rApi) {
      await page.click('#regionAdmin-tab').catch(() => {});
      await page.waitForTimeout(800);
      await page.click('#regionEvents-tab').catch(() => {});
      await page.waitForTimeout(1200);

      const RB = { back: 'regionEventWizBack', next: 'regionEventWizNext', save: 'regionEventSaveBtn', title: 'regionEventModalTitle' };
      await page.evaluate(() => window.showCreateRegionEventModal());
      await page.waitForTimeout(900);
      let r = await readModal(page, '#regionEventModal', RB);
      eq('samma fyra avsnitt som klubben', r.titles, SEC_BASE);
      eq('guideläge vid skapande', r.isSteps, true);
      eq('bara första avsnittet syns', r.shown.indexOf(true), 0);
      ok('INGET fält utanför ett avsnitt', r.outside.length === 0, r.outside.join(', '));
      ok('Spara är gömd', !r.save);
      ok('rubriken räknar stegen', /steg 1 av 4/.test(r.title), r.title);

      await page.click('#regionEventWizNext'); await page.waitForTimeout(300);
      r = await readModal(page, '#regionEventModal', RB);
      eq('Nästa vägras med tomt namn', r.shown.indexOf(true), 0);
      await page.fill('#regionEventName', 'ZZ Kretsguide');
      await page.click('#regionEventWizNext'); await page.waitForTimeout(350);
      r = await readModal(page, '#regionEventModal', RB);
      eq('och går igenom när det är ifyllt', r.shown.indexOf(true), 1);
      await closeModal(page, '#regionEventModal');

      const rOpened = await page.evaluate(() => {
        const btn = document.querySelector('#regionEventsTab button[onclick^="editRegionEvent("]');
        if (!btn) return false;
        btn.click();
        return true;
      });
      if (rOpened) {
        await page.waitForTimeout(900);
        r = await readModal(page, '#regionEventModal', RB);
        eq('redigering ger flikar, inte prickar', r.isSteps, false);
        ok('och flikarna går att klicka',
           r.disabled.length > 0 && r.disabled.every(d => d === false), JSON.stringify(r.disabled));
        ok('Bakåt och Nästa är borta', !r.back && !r.next);
        ok('Spara syns', r.save);
        await closeModal(page, '#regionEventModal');
      } else {
        console.log('  – kretsen har ingen händelse att redigera (lägesväxlingen mäts på klubben)');
      }
    }

    // ══════════════════════════════════════════════════════ HÄNDELSESIDAN
    section('Händelsens egen sida — bara flikar');
    await go(EVENTPAGE);
    const eApi = await page.evaluate(() => typeof window.openEditDetailsModal === 'function'
                                        && typeof window.hpskModalTabs === 'function');
    ok('händelsesidans dialoger är laddade', eApi,
       'kontot ser inte redigeringsknapparna — påståendena nedan hade mätt ingenting');
    if (eApi) {
      await page.evaluate(() => window.openEditDetailsModal());
      await page.waitForTimeout(900);
      const e = await readModal(page, '#editDetailsModal',
                                { back: '_ingen', next: '_ingen', save: '_ingen', title: 'editDetailsModalLabel' });
      const EP = ['Grunduppgifter', 'Plats och beskrivning', 'Kontakt', 'Utökade detaljer', 'Anmälan'];
      const epReq = await page.locator('#editRegistrationRequired').isChecked();
      eq('den här sidan bär också de utökade fälten', e.titles.slice(0, 5), EP);
      eq('och de anmälningsberoende avsnitten följer kryssrutan',
         e.titles.slice(5), epReq ? ['Avgift och betalning', 'På plats'] : []);
      eq('ALDRIG guideläge — dialogen kan bara redigera', e.isSteps, false);
      ok('flikarna går att klicka',
         e.disabled.length >= 5 && e.disabled.every(d => d === false), JSON.stringify(e.disabled));
      eq('bara första avsnittet syns', e.shown.filter(Boolean).length, 1);
      eq('och det är Grunduppgifter', e.shown.indexOf(true), 0);
      ok('INGET fält utanför ett avsnitt', e.outside.length === 0, e.outside.join(', '));

      await page.click('#editDetailsModal .hpsk-sectabs button:nth-child(4)');
      await page.waitForTimeout(300);
      const e2 = await readModal(page, '#editDetailsModal',
                                 { back: '_ingen', next: '_ingen', save: '_ingen', title: 'editDetailsModalLabel' });
      eq('ett klick byter till Utökade detaljer', e2.shown.indexOf(true), 3);
      eq('och bara ETT avsnitt syns', e2.shown.filter(Boolean).length, 1);
      await closeModal(page, '#editDetailsModal');
    }

    // ckeditor-bruset på klubbsidan är befintligt och orelaterat.
    const real = jsErrors.filter(e => !/ckeditor/i.test(e));
    ok('inga JS-fel', real.length === 0, real.join(' | '));

  } finally {
    await browser.close();
  }

  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log(`  - ${f}`)); process.exitCode = 1; }
};

main();
