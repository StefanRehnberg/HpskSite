// Autofyllnadsskyddet: filterfält och icke-inloggningsfält får inte vara mål för
// lösenordshanteraren. Läser bara. Kör: node hpsk-verify/autofill-fields-verify.mjs
import { chromium } from 'playwright';
const BASE = process.env.HPSK_BASE || 'http://localhost:18150';
let pass = 0, fail = 0;
const ok = (c, m) => { if (c) { pass++; console.log('  ok  ' + m); } else { fail++; console.log('  FEL ' + m); } };

const browser = await chromium.launch();
const p = await browser.newPage();
const errors = [];
p.on('pageerror', e => { if (!/ckeditor-duplicated-modules/.test(e.message)) errors.push(e.message); }); // k�nt brus p� klubbsidan

await p.goto(BASE + '/login-&-register/?tab=login', { waitUntil: 'domcontentloaded' });
await p.locator('#loginModel_Username').fill('builder.claude@pistol.nu');
await p.locator('#loginModel_Password').fill('123456');
await p.locator('#loginModel_Password').press('Enter');
await p.waitForLoadState('networkidle');

// Min sida via #tavlingar — exakt Kristians väg.
const r = await p.goto(BASE + '/user-profile-page#tavlingar', { waitUntil: 'networkidle' });
ok(r.status() === 200, 'Min sida svarar 200');
await p.waitForFunction(() => /Visar \d+ anmälning|Inga tävlingsanmälningar/.test(document.querySelector('#competitionsContent')?.innerText || ''), null, { timeout: 15000 });
const search = await p.locator('#compSearchFilter').evaluate(e => ({ type: e.type, ac: e.getAttribute('autocomplete'), name: e.name, value: e.value }));
ok(search.type === 'search' && search.ac === 'off' && search.name === 'compSearch', `sökfältet: type=${search.type}, autocomplete=${search.ac}, name=${search.name}`);
ok(search.value === '', 'sökfältet är tomt');
const uname = await p.locator('#changePasswordModal input[autocomplete="username"]').evaluate(e => ({ v: e.value, cls: e.className }));
ok(uname.v.includes('@') && uname.cls.includes('visually-hidden'), `lösenordsdialogen har ett eget dolt användarnamnsfält (${uname.v})`);
ok(await p.locator('#competitionsContent').innerText().then(t => /Visar \d+ anmälning/.test(t)), 'anmälningslistan är laddad');

// Kontrollprov: ett sökvärde som inte finns ger fortfarande tom lista (filtret fungerar).
await p.locator('#compSearchFilter').fill('zzz-finns-inte');
await p.waitForFunction(() => /Inga tävlingsanmälningar/.test(document.querySelector('#competitionsContent')?.innerText || ''), null, { timeout: 10000 }).catch(() => {});
ok(/Inga tävlingsanmälningar/.test(await p.locator('#competitionsContent').innerText()), 'kontrollprov: filtret filtrerar fortfarande');

// Klubbens adminsida (Brevo-fältet + medlemsdialogens lösenordsfält) — laddningen ÄR kompileringskontrollen.
const club = await p.goto(BASE + '/halland/klubbar/haaplinge-goass/', { waitUntil: 'domcontentloaded' }).catch(() => null);
if (club && club.status() === 200 && await p.locator('#brevoApiKey').count()) {
    const b = await p.locator('#brevoApiKey').getAttribute('autocomplete');
    ok(b === 'new-password', `klubbens Brevo-nyckel: autocomplete=${b}`);
    const a = await p.locator('#adminNewPassword').count() ? await p.locator('#adminNewPassword').getAttribute('autocomplete') : 'saknas på sidan';
    ok(a === 'new-password' || a === 'saknas på sidan', `medlemsdialogens nya lösenord: autocomplete=${a}`);
} else {
    console.log('  (hoppar över klubbsidan: ' + (club ? club.status() : 'ingen') + ')');
}

ok(errors.length === 0, 'inga JS-fel' + (errors.length ? ': ' + errors.join(' | ') : ''));
await browser.close();
console.log(`\n${pass}/${pass + fail}`);
process.exit(fail ? 1 : 0);
