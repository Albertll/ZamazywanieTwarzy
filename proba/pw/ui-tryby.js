// Tryb bez rozpoznawania w interfejsie, powrót do rozpoznawania i „Usuń dane katalogu”.
const { chromium } = require('playwright-core');
const path = require('path');

const BAZA = 'http://127.0.0.1:5077';
const OUT = path.join(__dirname, '..', 'zrzuty');
const wynik = [];
const sprawdz = (w, opis) => wynik.push(`${w ? 'OK  ' : 'BŁĄD'} ${opis}`);

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
  const bledy = [];
  page.on('pageerror', e => bledy.push('pageerror: ' + e.message));
  page.on('dialog', d => d.accept());
  const czekajNaZadanie = () => page.waitForFunction(() => !document.querySelector('#pasek-zadania').innerText.trim(), null, { timeout: 120000 });
  await page.goto(`${BAZA}/?t=testtoken0123456789abcdef`);
  await page.evaluate(k => { localStorage.setItem('zt.katalog', JSON.stringify(k)); localStorage.setItem('zt.podkatalogi', 'false'); },
    'F:\\Claude\\zamazywanie-twarzy\\testdane\\zdjecia');
  await page.reload();
  await page.waitForSelector('.karta');

  // przełączenie na tryb bez rozpoznawania
  await page.click('nav button[data-widok="ustawienia"]');
  await page.waitForSelector('input[name=tryb]');
  await page.check('input[value=bezRozpoznawania]');
  sprawdz(await page.isHidden('#u-progZgody'), 'progi rozpoznawania schowane po wybraniu trybu bez rozpoznawania');
  await page.screenshot({ path: path.join(OUT, 't1-ustawienia-tryb.png') });
  await page.click('#u-zapisz');
  await page.waitForTimeout(1200);
  sprawdz(await page.isHidden('nav button[data-widok="nierozpoznane"]'), 'zakładka „Nierozpoznane” schowana');

  await page.click('nav button[data-widok="zdjecia"]');
  await page.waitForSelector('.karta');
  sprawdz((await page.$$eval('.liczniki .z', z => z.length)) === 0, 'bez rozpoznawania nikt nie jest zielony automatycznie');
  await page.click('.karta >> text=01_situation_room');
  await page.waitForSelector('#pz-svg .ramka');
  await page.click('#pz-svg .ramka >> nth=0');
  await page.waitForSelector('.twarz-naglowek');
  sprawdz(!(await page.$('.kandydat')) && !(await page.$('#pz-wzorzec')), 'panel twarzy bez podpowiedzi i bez „zapamiętaj jako wzorzec”');
  const opcja = await page.$eval('#pz-osoba', s => [...s.options].find(o => o.text.startsWith('Joe Biden')).value);
  await page.selectOption('#pz-osoba', opcja);
  await page.click('#pz-przypisz');
  await page.waitForTimeout(800);
  sprawdz((await page.$$eval('#pz-svg .ramka.zielony', r => r.length)) === 1, 'ręczne przypisanie osoby ze zgodą odsłania twarz');
  await page.screenshot({ path: path.join(OUT, 't2-bez-rozpoznawania.png') });
  await page.click('#pz-zamknij');

  // powrót do rozpoznawania
  await page.click('nav button[data-widok="ustawienia"]');
  await page.waitForSelector('input[name=tryb]');
  await page.check('input[value=rozpoznawanie]');
  await page.click('#u-zapisz');
  await page.waitForTimeout(800);
  await czekajNaZadanie();
  sprawdz(await page.isVisible('nav button[data-widok="nierozpoznane"]'), 'po włączeniu rozpoznawania zakładka wraca');

  // usunięcie danych katalogu
  await page.click('nav button[data-widok="zdjecia"]');
  await page.waitForSelector('#usun-dane:not([disabled])');
  await page.click('#usun-dane');
  await page.screenshot({ path: path.join(OUT, 't3-usun-dane.png') });
  await page.click('#ud-ok');
  await page.waitForTimeout(1200);
  sprawdz((await page.$$('.karta')).length === 0, 'po „Usuń dane katalogu” siatka jest pusta');

  console.log(wynik.join('\n') + (bledy.length ? '\n' + bledy.join('\n') : '\nbrak błędów JS'));
  await browser.close();
})().catch(e => { console.log(wynik.join('\n')); console.error(e); process.exit(1); });
