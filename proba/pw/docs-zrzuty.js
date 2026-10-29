// Zrzuty ekranu do README (docs/*.jpg): ramki z podpisami, podgląd po zamazaniu, siatka zdjęć.
const { chromium } = require('playwright-core');
const path = require('path');

const BAZA = 'http://127.0.0.1:5077';
const DOCS = path.join(__dirname, '..', '..', 'docs');

(async () => {
  require('fs').mkdirSync(DOCS, { recursive: true });
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width: 1400, height: 860 } });
  const zrzut = n => page.screenshot({ path: path.join(DOCS, n + '.jpg'), type: 'jpeg', quality: 82 });

  await page.goto(`${BAZA}/?t=testtoken0123456789abcdef`);
  await page.evaluate(k => {
    localStorage.setItem('zt.katalog', JSON.stringify(k));
    localStorage.setItem('zt.podkatalogi', 'false');
    localStorage.setItem('zt.podpisy', 'true');
  }, 'F:\\Claude\\zamazywanie-twarzy\\testdane\\zdjecia');
  await page.reload();
  await page.waitForSelector('.karta img');
  await page.waitForLoadState('networkidle');
  await zrzut('siatka');

  await page.click('.karta >> text=01_situation_room');
  await page.waitForSelector('.podpis');
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(500);
  await zrzut('podglad-ramki');

  await Promise.all([
    page.waitForResponse(r => r.url().includes('zamazane=true')),
    page.check('#pz-zamazane'),
  ]);
  await page.waitForTimeout(800);
  await zrzut('podglad-zamazany');

  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
