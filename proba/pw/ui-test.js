// Przeklikanie interfejsu w Edge (playwright-core) + zrzuty ekranu do proba\zrzuty
const { chromium } = require('playwright-core');
const path = require('path');

const BAZA = 'http://127.0.0.1:5077';
const KATALOG = 'F:\\Claude\\zamazywanie-twarzy\\testdane\\zdjecia';
const OUT = path.join(__dirname, '..', 'zrzuty');

(async () => {
  require('fs').mkdirSync(OUT, { recursive: true });
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width: 1500, height: 920 } });
  const bledy = [];
  page.on('console', m => { if (m.type() === 'error') bledy.push('console: ' + m.text()); });
  page.on('pageerror', e => bledy.push('pageerror: ' + e.message));
  page.on('response', r => { if (r.status() >= 400) bledy.push(`HTTP ${r.status()} ${r.url()}`); });
  const zrzut = async n => page.screenshot({ path: path.join(OUT, n + '.png') });

  await page.goto(`${BAZA}/?t=testtoken0123456789abcdef`);
  await page.evaluate(k => { localStorage.setItem('zt.katalog', JSON.stringify(k)); localStorage.setItem('zt.podkatalogi', 'false'); }, KATALOG);
  await page.reload();
  await page.waitForSelector('.karta');
  await page.waitForTimeout(800);
  await zrzut('1-zdjecia');

  await page.click('.karta[data-id="1"]');
  await page.waitForSelector('#pz-svg .ramka');
  await page.waitForTimeout(800);
  await zrzut('2-przegladarka');

  await page.click('#pz-svg .ramka.zolty');
  await page.waitForSelector('.twarz-naglowek');
  await page.waitForTimeout(400);
  await zrzut('3-panel-twarzy');

  await page.check('#pz-zamazane');
  await page.waitForTimeout(1500);
  await zrzut('4-podglad-zamazany');
  await page.click('#pz-zamknij');

  await page.click('nav button[data-widok="nierozpoznane"]');
  await page.waitForSelector('.klaster');
  await page.waitForTimeout(1200);
  await zrzut('5-nierozpoznane');

  await page.click('nav button[data-widok="osoby"]');
  await page.waitForSelector('.tabela');
  await zrzut('6-osoby');
  await page.click('.tabela tbody tr');
  await page.waitForSelector('#eo-wzorce figure');
  await page.waitForTimeout(500);
  await zrzut('7-edycja-osoby');
  await page.click('#eo-anuluj');

  await page.click('nav button[data-widok="ustawienia"]');
  await page.waitForSelector('.ustawienia');
  await zrzut('8-ustawienia');

  console.log(bledy.length ? bledy.join('\n') : 'brak błędów w konsoli i HTTP');
  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
