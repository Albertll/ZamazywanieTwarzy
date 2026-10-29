// Podpisy pod ramkami: widoczne dla rozpoznanych, klik zaznacza twarz, przełącznik je chowa.
const { chromium } = require('playwright-core');
const path = require('path');

const BAZA = 'http://127.0.0.1:5077';
const OUT = path.join(__dirname, '..', 'zrzuty');
const wynik = [];
const sprawdz = (w, opis) => wynik.push(`${w ? 'OK  ' : 'BŁĄD'} ${opis}`);

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width: 1500, height: 920 } });
  const bledy = [];
  page.on('pageerror', e => bledy.push('pageerror: ' + e.message));
  await page.goto(`${BAZA}/?t=testtoken0123456789abcdef`);
  await page.evaluate(k => { localStorage.setItem('zt.katalog', JSON.stringify(k)); localStorage.setItem('zt.podkatalogi', 'false'); },
    'F:\\Claude\\zamazywanie-twarzy\\testdane\\zdjecia');
  await page.reload();
  await page.waitForSelector('.karta');

  for (const [id, nazwa] of [[1, 'p1-situation-room'], [3, 'p2-gabinet-grupowe']]) {
    await page.click(`.karta[data-id="${id}"]`);
    await page.waitForSelector('#pz-svg .ramka');
    await page.waitForTimeout(900);
    await page.screenshot({ path: path.join(OUT, nazwa + '.png') });
    if (id === 1) {
      const podpisy = await page.$$eval('.podpis', s => s.map(x => x.textContent.trim()));
      sprawdz(['Joe Biden', 'Barack Obama', 'Hillary Clinton'].every(n => podpisy.includes(n)), `podpisy na 01: ${podpisy.join(', ')}`);
      await page.click('.podpis >> text=Hillary Clinton');
      await page.waitForTimeout(300);
      sprawdz((await page.$eval('#pz-panel', e => e.innerText)).includes('Hillary Clinton — zgoda'), 'klik w podpis zaznacza twarz');
      await page.uncheck('#pz-podpisy');
      sprawdz(!(await page.isVisible('.podpis')), 'przełącznik chowa podpisy');
      await page.check('#pz-podpisy');
    }
    await page.click('#pz-zamknij');
    await page.waitForTimeout(300);
  }
  console.log(wynik.join('\n') + (bledy.length ? '\n' + bledy.join('\n') : '\nbrak błędów JS'));
  await browser.close();
})().catch(e => { console.log(wynik.join('\n')); console.error(e); process.exit(1); });
