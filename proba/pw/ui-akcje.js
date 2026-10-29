// Akcje w interfejsie: przypisanie, nowa osoba, dorysowanie ramki, „to nie twarz”, grupa, import CSV, zapis kopii.
const { chromium } = require('playwright-core');
const fs = require('fs');
const path = require('path');

const BAZA = 'http://127.0.0.1:5077';
const KATALOG = 'F:\\Claude\\zamazywanie-twarzy\\testdane\\zdjecia';
const CEL = 'F:\\Claude\\zamazywanie-twarzy\\wynik-test';
const OUT = path.join(__dirname, '..', 'zrzuty');
const wynik = [];
const sprawdz = (warunek, opis) => wynik.push(`${warunek ? 'OK  ' : 'BŁĄD'} ${opis}`);

(async () => {
  fs.rmSync(CEL, { recursive: true, force: true });
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width: 1500, height: 920 } });
  const bledy = [];
  page.on('pageerror', e => bledy.push('pageerror: ' + e.message));
  page.on('console', m => { if (m.type() === 'error') bledy.push('console: ' + m.text()); });
  page.on('dialog', d => d.accept());
  const zrzut = n => page.screenshot({ path: path.join(OUT, n + '.png') });
  const ramki = () => page.$$eval('#pz-svg .ramka', r => r.map(x => ({ id: +x.dataset.id, x: +x.getAttribute('x'), kolor: x.classList[1] })));

  await page.goto(`${BAZA}/?t=testtoken0123456789abcdef`);
  await page.evaluate(k => { localStorage.setItem('zt.katalog', JSON.stringify(k)); localStorage.setItem('zt.podkatalogi', 'false'); }, KATALOG);
  await page.reload();
  await page.waitForSelector('.karta');

  // 1. Obama z profilu (żółty, „prawdopodobnie”) → przypisanie z podpowiedzi → zielony
  await page.click('.karta[data-id="4"]');
  await page.waitForSelector('#pz-svg .ramka');
  let r = await ramki();
  const obama = r.find(x => Math.abs(x.x - 1864) < 30);
  sprawdz(obama?.kolor === 'zolty', `zdjęcie 05: Obama z profilu jest żółty przed decyzją (${obama?.kolor})`);
  await page.click(`#pz-svg .ramka[data-id="${obama.id}"]`);
  await page.click('.kandydat >> text=Barack Obama');
  await page.waitForTimeout(800);
  r = await ramki();
  sprawdz(r.find(x => x.id === obama.id)?.kolor === 'zielony', 'po przypisaniu Obama jest zielony');
  await zrzut('a1-po-przypisaniu');

  // 2. Zdjęcie 01: mężczyzna w prawym dolnym rogu → nowa osoba bez zgody → czerwony
  await page.click('#pz-zamknij');
  await page.waitForTimeout(500);
  await page.click('.karta[data-id="1"]');
  await page.waitForSelector('#pz-svg .ramka');
  r = await ramki();
  const gates = r.find(x => Math.abs(x.x - 3502) < 30);
  await page.click(`#pz-svg .ramka[data-id="${gates.id}"]`);
  await page.click('#pz-nowa summary');
  await page.fill('#pz-n-imie', 'Robert');
  await page.fill('#pz-n-nazwisko', 'Gates');
  await page.click('#pz-dodaj');
  await page.waitForTimeout(800);
  r = await ramki();
  sprawdz(r.find(x => x.id === gates.id)?.kolor === 'czerwony', 'nowa osoba bez zgody → czerwony');

  // 3. „To nie twarz” na żółtej ramce
  const zolta = r.find(x => x.kolor === 'zolty');
  await page.click(`#pz-svg .ramka[data-id="${zolta.id}"]`);
  await page.click('#pz-nietwarz');
  await page.waitForTimeout(600);
  r = await ramki();
  sprawdz(r.find(x => x.id === zolta.id)?.kolor === 'szary', '„To nie twarz” → szary (pominięta)');

  // 4. Dorysowanie ramki
  const przed = r.length;
  await page.click('#pz-rysuj');
  const box = await page.$eval('#pz-obraz', e => { const b = e.getBoundingClientRect(); return { x: b.x, y: b.y }; });
  await page.mouse.move(box.x + 40, box.y + 40);
  await page.mouse.down();
  await page.mouse.move(box.x + 110, box.y + 120, { steps: 5 });
  await page.mouse.up();
  await page.waitForTimeout(800);
  r = await ramki();
  sprawdz(r.length === przed + 1 && r.some(x => x.kolor === 'czerwony' && x.x < 400), 'dorysowana ramka dodana jako zamazywana');
  await page.check('#pz-zamazane');
  await page.waitForTimeout(1500);
  await zrzut('a2-po-decyzjach-zamazane');
  await page.click('#pz-zamknij');

  // 5. Grupa + import CSV w Windows-1250
  await page.click('nav button[data-widok="osoby"]');
  await page.waitForSelector('#nowa-grupa');
  await page.fill('#nowa-grupa', 'Gabinet');
  await page.click('#dodaj-grupe');
  await page.waitForTimeout(500);
  await page.click('#import-csv');
  await page.setInputFiles('#ic-plik', path.join(__dirname, '..', 'lista-cp1250.csv'));
  await page.click('#ic-ok');
  await page.waitForSelector('.bledy');
  const raportImportu = await page.$eval('.okno', e => e.innerText);
  sprawdz(raportImportu.includes('Dodano: 3'), 'import CSV: 3 osoby dodane (wiersz bez imienia odrzucony)');
  await zrzut('a3-import-csv');
  await page.click('#ok');
  await page.click('.grupa >> text=Wszyscy');
  await page.waitForTimeout(500);
  const tabela = await page.$eval('#tabela-osob', e => e.innerText);
  sprawdz(tabela.includes('Zażółć Gęślą') && tabela.includes('Łucja Źdźbło'), 'polskie znaki z pliku Windows-1250 zachowane');
  sprawdz(/Jan Kowalski[\s\S]*wygasła 2020-01-01/.test(tabela), 'zgoda z datą w przeszłości pokazana jako wygasła');
  await zrzut('a4-osoby-po-imporcie');

  // 6. Zapis zamazanych kopii
  await page.click('nav button[data-widok="zdjecia"]');
  await page.waitForSelector('#eksportuj:not([disabled])');
  await page.click('#eksportuj');
  await page.fill('#ek-cel', CEL);
  await page.click('#ek-start');
  for (let i = 0; i < 240 && !fs.existsSync(path.join(CEL, 'raport.csv')); i++) await page.waitForTimeout(500);
  const pliki = fs.existsSync(CEL) ? fs.readdirSync(CEL) : [];
  sprawdz(pliki.filter(f => f.endsWith('.jpg')).length === 17, `zapisano 17 kopii (jest ${pliki.filter(f => f.endsWith('.jpg')).length})`);
  sprawdz(pliki.includes('raport.csv'), 'raport.csv zapisany');
  await zrzut('a5-po-zapisie');

  console.log(wynik.join('\n'));
  console.log(bledy.length ? '\n' + bledy.join('\n') : '\nbrak błędów JS');
  await browser.close();
})().catch(e => { console.log(wynik.join('\n')); console.error(e); process.exit(1); });
