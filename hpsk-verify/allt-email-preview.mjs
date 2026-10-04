// Renderar medlemsutskicket om /allt (bilderna hämtas från prod) + kollar /allt i mobilbredd.
import { chromium } from 'playwright';
const UA='Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1';
const br = await chromium.launch({ headless: true });
const dir='C:/Repos/HpskSite/Marketing/medlemsutskick-2026-10-allt/';
const p1 = await (await br.newContext({ viewport:{width:700,height:900} })).newPage();
await p1.goto('file:///'+dir+'forhandsgranskning.html'); await p1.waitForTimeout(1500);
await p1.screenshot({ path: dir+'forhandsgranskning.png', fullPage:true });
console.log('mail height', await p1.evaluate(()=>document.body.scrollHeight));
const m = await (await br.newContext({ viewport:{width:390,height:844}, deviceScaleFactor:2, userAgent:UA, isMobile:true })).newPage();
await m.goto('https://pistol.nu/allt',{waitUntil:'networkidle'}); await m.waitForTimeout(1000);
const b = m.locator('#qrOpen');
console.log('mobile button text:', JSON.stringify((await b.innerText()).trim()), JSON.stringify(await b.boundingBox()));
await m.screenshot({ path: 'C:/Users/Stefan/AppData/Local/Temp/claude/C--Repos-HpskSite/e663b383-ecfa-4ade-9151-68209b8439a2/scratchpad/allt-mobile.png' });
await br.close();
