// allt-email-shot.mjs â€” bild till medlemsutskicket om /allt. Tas mot PROD (publik sida, inga personer).
// Döljer rubrikblocket som överlappar cirkeln och klipper ut själva kartan.
// KÖR: node hpsk-verify/allt-email-shot.mjs <utfil.png>
import { chromium } from 'playwright';
const br = await chromium.launch({ headless: true });
const ctx = await br.newContext({ viewport: { width: 1100, height: 760 }, deviceScaleFactor: 2,
  userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36' });
const page = await ctx.newPage();
await page.goto('https://pistol.nu/allt', { waitUntil: 'networkidle' });
await page.waitForTimeout(1500);
await page.addStyleTag({ content: '.hdr,.tools,.crumbs{display:none!important}' });
await page.waitForTimeout(300);
const box = await page.locator('#svg').boundingBox();
// Cirkeln är centrerad i svg:n; ta en kvadrat runt den.
const side = Math.min(box.width, box.height);
const clip = { x: box.x + (box.width - side) / 2, y: box.y + (box.height - side) / 2, width: side, height: side };
await page.screenshot({ path: process.argv[2] || 'allt-shot.png', clip });
await br.close();
console.log(JSON.stringify(clip));
