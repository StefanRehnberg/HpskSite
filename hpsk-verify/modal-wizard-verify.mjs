// modal-wizard-verify.mjs — den generiska stegmaskinen i _ModalSectionNav.
//
// KÖR:  node hpsk-verify/modal-wizard-verify.mjs
//
// VARFÖR DEN FINNS: tävlingsguidens stegmaskin är handskriven och hårdkodad till sju steg. Att
// kopiera in den i händelsedialogerna hade gett en fjärde kopia. Den här härleder stegen ur SAMMA
// rubriker som flikarna, vilket ger egenskapen som är hela poängen: skapandet och redigeringen
// kan dela markup.
//
// ⚠️ SVITEN SKRIVER INGENTING i databasen. Den bygger syntetisk markup i sidan och river den.
//
// ⚠️⚠️ REGRESSIONEN ÄR HALVA SVITEN: `hpskModalSectionNav` används redan av tävlingsredigeringen,
// och guideläget delar dess gruppering. Går grupperingen sönder märks det där först.

import { chromium } from 'playwright';

const BASE = process.env.HPSK_BASE || 'http://localhost:18150';

let pass = 0, fail = 0;
const failures = [];
const ok = (n, c, d) => { if (c) { pass++; console.log(`  ✓ ${n}`); } else { fail++; failures.push(n); console.log(`  ✗ ${n}${d ? ` — ${d}` : ''}`); } };
const eq = (n, a, e) => ok(n, JSON.stringify(a) === JSON.stringify(e), `fick ${JSON.stringify(a)}, väntade ${JSON.stringify(e)}`);
const section = t => console.log(`\n== ${t}`);

// Tre avsnitt, med ett obligatoriskt fält i det första — nog för att pröva både navigering
// och att valideringen stoppar rätt steg.
const MARKUP = `
<div class="modal" id="zzWiz" style="display:block;position:static"><div class="modal-dialog"><div class="modal-content">
  <div class="modal-body">
    <form id="zzWizForm">
      <h6 class="text-primary">Grunduppgifter</h6>
      <input type="text" id="zzName" required>
      <h6 class="text-primary">Plats</h6>
      <input type="text" id="zzVenue">
      <h6 class="text-primary">Anmälan</h6>
      <input type="text" id="zzMax">
    </form>
  </div>
</div></div></div>`;

const main = async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
  const jsErrors = [];
  page.on('pageerror', e => jsErrors.push(e.message));

  try {
    await page.goto(`${BASE}/login-%26-register/?tab=login`, { waitUntil: 'domcontentloaded' });
    await page.fill('input[name="loginModel.Username"]', process.env.HPSK_USER);
    await page.fill('input[name="loginModel.Password"]', process.env.HPSK_PASS);
    await page.click('button[type=submit], input[type=submit]', { noWaitAfter: true });
    await page.waitForURL(u => !String(u).includes('login'), { timeout: 180000 }).catch(() => {});

    // ⚠️ LÅT INLOGGNINGEN LANDA FÖRST. En goto medan navigeringen är i luften avbryter den med
    // ERR_ABORTED, vilket läser som att sidan är trasig — den var bara upptagen.
    for (let i = 0; i < 3; i++) {
      try { await page.goto(`${BASE}/user-profile-page/`, { waitUntil: 'domcontentloaded', timeout: 120000 }); break; }
      catch { await page.waitForTimeout(3000); }
    }
    if (await page.locator('#firearms-member-tab').count() === 0) {
      console.error('\nAVBRYTER: inte inloggad — varje påstående nedan hade mätt en sida utan partialen.');
      process.exitCode = 1;
      return;
    }

    // ⚠️ MÅSTE vara en sida som renderar `_ModalSectionNav`. Klubbsidans adminpanel gör det via
    // tävlingsmodalerna — men bara för en klubbadmin. Anonymt finns hjälparen inte alls, och då
    // mäter varje påstående nedan ingenting.
    for (let i = 0; i < 3; i++) {
      try { await page.goto(`${BASE}/halland/klubbar/haaplinge-goass/`, { waitUntil: 'domcontentloaded', timeout: 120000 }); break; }
      catch { await page.waitForTimeout(3000); }
    }

    section('Förutsättning');
    const api = await page.evaluate(() => ({
      nav: typeof window.hpskModalSectionNav === 'function',
      wizard: typeof window.hpskModalWizard === 'function',
      next: typeof window.hpskWizardNext === 'function',
      prev: typeof window.hpskWizardPrev === 'function',
      state: typeof window.hpskWizardState === 'function',
      all: typeof window.hpskWizardValidateAll === 'function',
      reveal: typeof window.hpskRevealField === 'function',
    }));
    ok('partialen är laddad på sidan', api.nav,
       'utan den mäter resten ingenting — kontrollera att kontot är klubbadmin');
    if (!api.nav) return;
    ok('guidens API finns', api.wizard && api.next && api.prev && api.state && api.all,
       JSON.stringify(api));
    ok('och flikarnas API är kvar', api.reveal);

    // ── Grupperingen ────────────────────────────────────────────────────────────────────────
    section('Stegen härleds ur rubrikerna');
    const built = await page.evaluate(m => {
      document.body.insertAdjacentHTML('beforeend', m);
      const n = window.hpskModalWizard('#zzWiz', { onStep: s => { window.__zzLast = s; } });
      const nav = document.querySelector('#zzWiz .hpsk-sectabs');
      return {
        n,
        secs: document.querySelectorAll('#zzWiz .hpsk-section').length,
        dots: nav ? nav.querySelectorAll('button').length : 0,
        isSteps: nav ? nav.classList.contains('hpsk-steps') : false,
        titles: nav ? [...nav.querySelectorAll('button')].map(b => b.textContent) : [],
        cb: window.__zzLast || null,
      };
    }, MARKUP);

    eq('tre avsnitt bildas ur tre rubriker', built.secs, 3);
    eq('och tre steg', built.n, 3);
    eq('stegraden är i guideläge', built.isSteps, true);
    eq('stegen bär rubrikernas namn', built.titles, ['Grunduppgifter', 'Plats', 'Anmälan']);
    ok('onStep anropas direkt', built.cb && built.cb.index === 0 && built.cb.count === 3,
       JSON.stringify(built.cb));
    ok('och säger att det är första steget', built.cb && built.cb.isFirst && !built.cb.isLast);

    // ── ⚠️ Ett steg i taget ─────────────────────────────────────────────────────────────────
    section('Ett steg i taget');
    const vis = await page.evaluate(() =>
      [...document.querySelectorAll('#zzWiz .hpsk-section')].map(s => !s.hidden));
    eq('bara första avsnittet syns', vis, [true, false, false]);

    // ── ⚠️ KÄRNAN: valideringen stoppar på det SYNLIGA steget ───────────────────────────────
    section('Valideringen');
    const blocked = await page.evaluate(() => {
      const moved = window.hpskWizardNext('#zzWiz');
      return { moved, at: window.hpskWizardState('#zzWiz').index,
               invalid: document.getElementById('zzName').classList.contains('is-invalid') };
    });
    eq('Nästa vägras när det obligatoriska fältet är tomt', blocked.moved, false);
    eq('och vi står kvar på steg 1', blocked.at, 0);
    ok('fältet märks ut', blocked.invalid);

    const moved = await page.evaluate(() => {
      document.getElementById('zzName').value = 'Gåsaskjutningen';
      const m = window.hpskWizardNext('#zzWiz');
      return { m, at: window.hpskWizardState('#zzWiz').index,
               vis: [...document.querySelectorAll('#zzWiz .hpsk-section')].map(s => !s.hidden),
               cb: window.__zzLast };
    });
    eq('med fältet ifyllt går Nästa igenom', moved.m, true);
    eq('och vi står på steg 2', moved.at, 1);
    eq('bara andra avsnittet syns', moved.vis, [false, true, false]);
    ok('onStep följde med', moved.cb && moved.cb.index === 1, JSON.stringify(moved.cb));

    // ⚠️ Bakåt validerar INTE — att spärra vägen tillbaka låser in någon i ett steg de
    // försöker lämna för att rätta något tidigare.
    const back = await page.evaluate(() => {
      document.getElementById('zzName').value = '';
      window.hpskWizardPrev('#zzWiz');
      return window.hpskWizardState('#zzWiz').index;
    });
    eq('Bakåt går även med ett tomt obligatoriskt fält', back, 0);

    // ── Sista steget ────────────────────────────────────────────────────────────────────────
    section('Sista steget');
    const last = await page.evaluate(() => {
      document.getElementById('zzName').value = 'Gåsaskjutningen';
      window.hpskWizardNext('#zzWiz');
      window.hpskWizardNext('#zzWiz');
      const st = window.hpskWizardState('#zzWiz');
      const again = window.hpskWizardNext('#zzWiz');   // får inte gå förbi slutet
      return { st, again, after: window.hpskWizardState('#zzWiz').index };
    });
    eq('vi når sista steget', last.st.index, 2);
    eq('och det vet om att det är sist', last.st.isLast, true);
    eq('Nästa går inte förbi slutet', last.after, 2);

    // ── ⚠️ Helvalideringen byter till rätt steg ─────────────────────────────────────────────
    section('Helvalideringen');
    const all = await page.evaluate(() => {
      document.getElementById('zzName').value = '';       // ett fel på steg 1, vi står på 3
      const okAll = window.hpskWizardValidateAll('#zzWiz');
      return { okAll, at: window.hpskWizardState('#zzWiz').index };
    });
    eq('Skapa vägras när ett fält på ETT ANNAT steg är tomt', all.okAll, false);
    // ⚠️ Utan det här hamnar felmeddelandet på något osynligt, och Skapa ser ut att inte göra
    // någonting — samma fälla som hpskRevealField finns för.
    eq('och guiden hoppar till steget där felet finns', all.at, 0);

    const allOk = await page.evaluate(() => {
      document.getElementById('zzName').value = 'Gåsaskjutningen';
      return window.hpskWizardValidateAll('#zzWiz');
    });
    eq('och släpper igenom när allt är ifyllt', allOk, true);

    // ── Regression: flikläget ───────────────────────────────────────────────────────────────
    section('Flikläget är orört');
    const tabs = await page.evaluate(m => {
      document.getElementById('zzWiz').remove();
      document.body.insertAdjacentHTML('beforeend', m.replace('zzWiz', 'zzTab'));
      const n = window.hpskModalSectionNav('#zzTab');
      const nav = document.querySelector('#zzTab .hpsk-sectabs');
      const first = nav && nav.querySelector('button');
      // I flikläge SKA knapparna gå att klicka.
      if (first) nav.querySelectorAll('button')[1].click();
      return {
        n,
        isSteps: nav ? nav.classList.contains('hpsk-steps') : null,
        disabled: first ? first.disabled : null,
        at: document.querySelector('#zzTab .modal-body').dataset.hpskCurrentSection,
        vis: [...document.querySelectorAll('#zzTab .hpsk-section')].map(s => !s.hidden),
      };
    }, MARKUP);
    eq('flikläget bildar samma tre avsnitt', tabs.n, 3);
    eq('och är INTE i guideläge', tabs.isSteps, false);
    eq('flikknapparna går att klicka', tabs.disabled, false);
    eq('ett klick byter avsnitt', tabs.vis, [false, true, false]);

    ok('inga JS-fel', jsErrors.filter(e => !/ckeditor/.test(e)).length === 0, jsErrors.join(' | '));

  } finally {
    await page.evaluate(() => {
      ['zzWiz', 'zzTab'].forEach(id => { const el = document.getElementById(id); if (el) el.remove(); });
    }).catch(() => {});
    await browser.close();
  }

  console.log(`\n${pass}/${pass + fail} gröna`);
  if (fail) { console.log('FALLERADE:'); failures.forEach(f => console.log(`  - ${f}`)); process.exitCode = 1; }
};

main();
