'use strict';

// ───────────── stan i narzędzia ─────────────

const NAZWY_KOLOROW = { zielony: 'Zgoda — zostaje', czerwony: 'Zamazywana', zolty: 'Do sprawdzenia — zamazywana', szary: 'Pominięta (nie twarz)' };

const stan = {
  widok: 'zdjecia',
  katalog: wczytaj('katalog', ''),
  podkatalogi: wczytaj('podkatalogi', true),
  grupyNaZdjeciach: wczytaj('grupyNaZdjeciach', []),
  filtr: 'wszystkie',
  osobaFiltr: null,
  zdjecia: [],
  osoby: [],
  grupy: [],
  zadanie: null,
  grupaWybrana: null,
  szukaj: '',
  ustawienia: null,
};

// Tryb „bez rozpoznawania”: tylko wykrywanie twarzy, osoby ze zgodą odsłania operator — chowamy wszystko, co porównuje twarze.
const rozpoznawanie = () => stan.ustawienia?.tryb !== 'bezRozpoznawania';

function wczytaj(klucz, domyslna) {
  try { const v = localStorage.getItem('zt.' + klucz); return v === null ? domyslna : JSON.parse(v); } catch { return domyslna; }
}
function zapamietaj(klucz, wartosc) {
  try { localStorage.setItem('zt.' + klucz, JSON.stringify(wartosc)); } catch { /* tryb prywatny */ }
}

const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

async function api(metoda, url, dane) {
  const opcje = { method: metoda, headers: { 'X-Zamazywanie': '1' } };
  if (dane instanceof Blob) opcje.body = dane;
  else if (dane !== undefined) {
    opcje.headers['Content-Type'] = 'application/json';
    opcje.body = JSON.stringify(dane);
  }
  const r = await fetch(url, opcje);
  if (!r.ok) {
    let tekst = await r.text();
    try { tekst = JSON.parse(tekst).blad ?? tekst; } catch { /* zwykły tekst */ }
    throw new Error(tekst || `${r.status} ${r.statusText}`);
  }
  return (r.headers.get('content-type') || '').includes('json') ? r.json() : null;
}

function toast(tekst, blad = false) {
  const t = $('#toast');
  t.textContent = tekst;
  t.className = blad ? 'widoczny blad' : 'widoczny';
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (t.className = ''), blad ? 7000 : 3500);
}

async function bezpiecznie(fn) {
  try { return await fn(); } catch (e) { toast(e.message, true); }
}

function otworzModal(html, klasa = '') {
  const m = document.createElement('div');
  m.className = 'modal ' + klasa;
  m.innerHTML = html;
  document.body.appendChild(m);
  return m;
}

document.addEventListener('keydown', e => {
  const m = $$('.modal').pop();
  if (!m) return;
  if (m.klawisz) m.klawisz(e);
  else if (e.key === 'Escape') m.remove();
});

function nazwaGrupy(id) { return stan.grupy.find(g => g.id === id)?.nazwa ?? '?'; }

function wyborOsoby(id, wybrana) {
  return `<select id="${id}"><option value="">— wybierz osobę —</option>${stan.osoby.map(o =>
    `<option value="${o.id}" ${o.id === wybrana ? 'selected' : ''}>${esc(o.nazwa)} ${o.zgodaWazna ? '· zgoda' : '· BEZ zgody'}</option>`).join('')}</select>`;
}

function formularzOsoby(p, o) {
  return `<div class="formularz">
    <label>Imię<input id="${p}-imie" value="${esc(o.imie)}"></label>
    <label>Nazwisko<input id="${p}-nazwisko" value="${esc(o.nazwisko)}"></label>
    <label class="check"><input type="checkbox" id="${p}-zgoda" ${o.zgoda ? 'checked' : ''}> ma zgodę na publikację wizerunku</label>
    <label>Zgoda ważna do <small>(puste = bezterminowo)</small><input type="date" id="${p}-zgodaDo" value="${esc(o.zgodaDo || '')}"></label>
    <fieldset><legend>Grupy</legend>${stan.grupy.length ? stan.grupy.map(g =>
      `<label class="check"><input type="checkbox" class="${p}-grupa" value="${g.id}" ${(o.grupy || []).includes(g.id) ? 'checked' : ''}> ${esc(g.nazwa)}</label>`).join('')
      : '<small>Brak grup — dodasz je w zakładce „Osoby i grupy”.</small>'}</fieldset>
    <label>Uwagi<input id="${p}-uwagi" value="${esc(o.uwagi)}"></label>
  </div>`;
}

function odczytajOsobe(p, el) {
  return {
    imie: $(`#${p}-imie`, el).value.trim(),
    nazwisko: $(`#${p}-nazwisko`, el).value.trim(),
    zgoda: $(`#${p}-zgoda`, el).checked,
    zgodaDo: $(`#${p}-zgodaDo`, el).value || null,
    grupy: $$(`.${p}-grupa:checked`, el).map(c => +c.value),
    uwagi: $(`#${p}-uwagi`, el).value.trim(),
  };
}

async function zaladujOsobyGrupy() {
  [stan.osoby, stan.grupy] = await Promise.all([api('GET', '/api/osoby'), api('GET', '/api/grupy')]);
}

// ───────────── nawigacja i zadania w tle ─────────────

$$('nav button').forEach(b => (b.onclick = () => pokaz(b.dataset.widok)));

async function pokaz(widok) {
  stan.widok = widok;
  $$('nav button').forEach(b => b.classList.toggle('aktywny', b.dataset.widok === widok));
  await odswiez();
}

async function odswiez() {
  const w = { zdjecia: widokZdjec, nierozpoznane: widokNierozpoznanych, osoby: widokOsob, ustawienia: widokUstawien }[stan.widok];
  await bezpiecznie(async () => {
    if (!stan.ustawienia) await zaladujUstawienia();
    await w();
  });
}

async function zaladujUstawienia() {
  stan.ustawienia = await api('GET', '/api/ustawienia');
  $('nav button[data-widok="nierozpoznane"]').hidden = !rozpoznawanie();
  if (!rozpoznawanie() && stan.widok === 'nierozpoznane') stan.widok = 'zdjecia';
}

async function sprawdzZadanie() {
  try {
    const z = await api('GET', '/api/zadanie');
    const trwalo = stan.zadanie?.trwa;
    stan.zadanie = z;
    rysujPasekZadania();
    if (z.trwa) setTimeout(sprawdzZadanie, 700);
    else if (trwalo) {
      toast(z.komunikat || 'Gotowe');
      if (z.bledy?.length) pokazBledy(z);
      odswiez();
    }
  } catch {
    setTimeout(sprawdzZadanie, 3000);
  }
}

function rysujPasekZadania() {
  const z = stan.zadanie, el = $('#pasek-zadania');
  if (!z?.trwa) { el.innerHTML = ''; return; }
  const proc = z.wszystkie ? Math.round((100 * z.gotowe) / z.wszystkie) : 0;
  el.innerHTML = `<b>${esc(z.rodzaj)}</b><span>${z.gotowe} / ${z.wszystkie || '…'}</span>
    <div class="postep"><div style="width:${proc}%"></div></div><span class="biezacy">${esc(z.biezacy || '')}</span>
    <button id="przerwij">Przerwij</button>`;
  $('#przerwij').onclick = () => bezpiecznie(() => api('POST', '/api/przerwij'));
}

async function uruchomZadanie(url, dane) {
  await api('POST', url, dane);
  stan.zadanie = { trwa: true, rodzaj: '…', gotowe: 0, wszystkie: 0 };
  rysujPasekZadania();
  sprawdzZadanie();
}

function pokazBledy(z) {
  const m = otworzModal(`<div class="okno szerokie"><h2>${esc(z.rodzaj)} — uwagi (${z.bledy.length})</h2>
    <p>${esc(z.komunikat)}</p><ul class="bledy">${z.bledy.map(b => `<li>${esc(b)}</li>`).join('')}</ul>
    <div class="stopka"><span class="odstep"></span><button class="glowny" id="ok">OK</button></div></div>`);
  $('#ok', m).onclick = () => m.remove();
}

$('#zakoncz').onclick = async () => {
  if (!confirm('Wyłączyć program? Przetworzone dane i baza osób zostają zapisane.')) return;
  await bezpiecznie(() => api('POST', '/api/zakoncz'));
  document.body.innerHTML = '<p class="pusto">Program został wyłączony. Możesz zamknąć tę kartę.</p>';
};

// ───────────── wybór katalogu ─────────────

function wybierzKatalog(tytul, start) {
  return new Promise(resolve => {
    let biezaca = '';
    const m = otworzModal(`<div class="okno"><h2>${esc(tytul)}</h2>
      <div class="wiersz"><input id="kat-sciezka" placeholder="Wklej ścieżkę albo wybierz poniżej"><button id="kat-idz">Przejdź</button></div>
      <div id="kat-lista" class="lista-katalogow"></div>
      <div class="stopka"><small id="kat-info"></small><span class="odstep"></span>
        <button id="kat-anuluj">Anuluj</button><button id="kat-wybierz" class="glowny">Wybierz ten katalog</button></div></div>`);
    const zakoncz = wynik => { m.remove(); resolve(wynik); };
    async function laduj(s) {
      try {
        const d = await api('GET', '/api/katalogi?sciezka=' + encodeURIComponent(s));
        biezaca = d.sciezka;
        $('#kat-sciezka', m).value = d.sciezka;
        $('#kat-lista', m).innerHTML =
          (d.rodzic !== null ? `<button class="katalog wyzej" data-s="${esc(d.rodzic)}">↰ ${d.rodzic ? 'katalog wyżej' : 'dyski i skróty'}</button>` : '') +
          d.katalogi.map(k => `<button class="katalog" data-s="${esc(k.sciezka)}">📁 ${esc(k.nazwa)}</button>`).join('') ||
          '<small>Brak podkatalogów.</small>';
        $('#kat-info', m).textContent = d.sciezka ? `zdjęć w tym katalogu: ${d.liczbaZdjec}` : '';
        $('#kat-wybierz', m).disabled = !d.sciezka;
      } catch (e) {
        toast(e.message, true);
        if (s) laduj('');
      }
    }
    $('#kat-lista', m).onclick = e => { const b = e.target.closest('.katalog'); if (b) laduj(b.dataset.s); };
    $('#kat-idz', m).onclick = () => laduj($('#kat-sciezka', m).value);
    $('#kat-sciezka', m).onkeydown = e => { if (e.key === 'Enter') laduj(e.target.value); };
    $('#kat-anuluj', m).onclick = () => zakoncz(null);
    $('#kat-wybierz', m).onclick = () => zakoncz(biezaca);
    m.klawisz = e => { if (e.key === 'Escape') zakoncz(null); };
    laduj(start || '');
  });
}

// ───────────── widok: zdjęcia ─────────────

const FILTRY = [
  ['wszystkie', 'Wszystkie', () => true],
  ['zolte', 'Do sprawdzenia', z => z.zolte > 0],
  ['czerwone', 'Z zamazanymi', z => z.czerwone > 0],
  ['zielone', 'Same zgody', z => z.zielone > 0 && !z.zolte && !z.czerwone],
  ['bez', 'Bez twarzy', z => !z.zielone && !z.zolte && !z.czerwone],
];
const filtrZdjec = z => FILTRY.find(f => f[0] === stan.filtr)[2](z);

async function widokZdjec() {
  await zaladujOsobyGrupy();
  const q = stan.osobaFiltr ? `osoba=${stan.osobaFiltr.id}` : `katalog=${encodeURIComponent(stan.katalog)}&podkatalogi=${stan.podkatalogi}`;
  stan.zdjecia = stan.katalog || stan.osobaFiltr ? await api('GET', '/api/zdjecia?' + q) : [];
  stan.grupyNaZdjeciach = stan.grupyNaZdjeciach.filter(id => stan.grupy.some(g => g.id === id));
  const widoczne = stan.zdjecia.filter(filtrZdjec);
  const aktywneGrupy = stan.grupy.filter(g => !g.archiwalna);

  $('#widok').innerHTML = `
    <section class="pasek-narzedzi">
      <div class="pole-katalog">
        <span class="etykieta">Katalog ze zdjęciami</span>
        <div class="sciezka" title="${esc(stan.katalog)}">${esc(stan.katalog || 'nie wybrano')}</div>
        <button id="wybierz-katalog">Wybierz…</button>
        <label class="check"><input type="checkbox" id="podkatalogi" ${stan.podkatalogi ? 'checked' : ''}> razem z podkatalogami</label>
      </div>
      ${rozpoznawanie() ? `<div class="pole-grupy">
        <span class="etykieta">Kto może być na zdjęciach — zawęża rozpoznawanie, działa przy „Przetwórz”</span>
        <div class="chipy">
          <button class="chip ${stan.grupyNaZdjeciach.length ? '' : 'wlaczony'}" data-grupa="">Wszyscy z bazy</button>
          ${aktywneGrupy.map(g => `<button class="chip ${stan.grupyNaZdjeciach.includes(g.id) ? 'wlaczony' : ''}" data-grupa="${g.id}">${esc(g.nazwa)}</button>`).join('')}
        </div>
      </div>` : `<div class="pole-grupy podpowiedz">Tryb bez rozpoznawania: program tylko znajduje twarze i zamazuje wszystkie.
        Osobę ze zgodą odsłaniasz, klikając jej twarz i przypisując ją z listy osób.</div>`}
      <div class="akcje">
        <button id="przetworz" class="glowny" ${stan.katalog ? '' : 'disabled'}>Przetwórz zdjęcia</button>
        <button id="eksportuj" ${stan.zdjecia.length && !stan.osobaFiltr ? '' : 'disabled'}>Zapisz zamazane kopie…</button>
        <button id="usun-dane" class="cichy" ${stan.zdjecia.length && !stan.osobaFiltr ? '' : 'disabled'}
          title="Po zakończeniu pracy: usuń z programu twarze, wycinki, decyzje i podglądy z tego katalogu">Usuń dane katalogu…</button>
      </div>
    </section>
    <section class="filtry">
      ${stan.osobaFiltr ? `<span class="chip wlaczony">Zdjęcia z osobą: ${esc(stan.osobaFiltr.nazwa)}<button id="bez-osoby" title="Wróć do katalogu">×</button></span>` : ''}
      ${FILTRY.map(([k, t, f]) => `<button class="filtr ${stan.filtr === k ? 'aktywny' : ''}" data-filtr="${k}">${t}<b>${stan.zdjecia.filter(f).length}</b></button>`).join('')}
      <span class="legenda"><i class="kropka zielony"></i>zgoda — zostaje <i class="kropka czerwony"></i>bez zgody
        <i class="kropka zolty"></i>${rozpoznawanie() ? 'nierozpoznana — zamazana, do sprawdzenia' : 'nieprzypisana — zamazana'}</span>
    </section>
    <section class="siatka">${widoczne.length ? widoczne.map(karta).join('') : `<p class="pusto">${pustaSiatka()}</p>`}</section>`;

  $('#wybierz-katalog').onclick = async () => {
    const k = await wybierzKatalog('Katalog ze zdjęciami', stan.katalog);
    if (k === null) return;
    stan.katalog = k;
    stan.osobaFiltr = null;
    zapamietaj('katalog', k);
    odswiez();
  };
  $('#podkatalogi').onchange = e => { stan.podkatalogi = e.target.checked; zapamietaj('podkatalogi', stan.podkatalogi); odswiez(); };
  $$('.pole-grupy .chip').forEach(b => (b.onclick = () => {
    const id = b.dataset.grupa ? +b.dataset.grupa : null;
    stan.grupyNaZdjeciach = id === null ? [] : stan.grupyNaZdjeciach.includes(id)
      ? stan.grupyNaZdjeciach.filter(x => x !== id) : [...stan.grupyNaZdjeciach, id];
    zapamietaj('grupyNaZdjeciach', stan.grupyNaZdjeciach);
    widokZdjec();
  }));
  $('#przetworz').onclick = () => bezpiecznie(() =>
    uruchomZadanie('/api/przetworz', { katalog: stan.katalog, podkatalogi: stan.podkatalogi, grupy: stan.grupyNaZdjeciach }));
  $('#eksportuj').onclick = eksportuj;
  $('#usun-dane').onclick = usunDaneKatalogu;
  $$('.filtr').forEach(b => (b.onclick = () => { stan.filtr = b.dataset.filtr; widokZdjec(); }));
  const bezOsoby = $('#bez-osoby');
  if (bezOsoby) bezOsoby.onclick = () => { stan.osobaFiltr = null; odswiez(); };
  $$('.karta').forEach(k => (k.onclick = () => otworzZdjecie(+k.dataset.id)));
}

function karta(z) {
  return `<figure class="karta" data-id="${z.id}">
    <img loading="lazy" src="/api/zdjecia/${z.id}/miniatura?v=${encodeURIComponent(z.wersja)}" alt="">
    <figcaption><span class="nazwa" title="${esc(z.sciezka)}">${esc(z.nazwa)}</span><span class="liczniki">${
      z.zielone ? `<i class="z" title="zgoda">${z.zielone}</i>` : ''}${z.czerwone ? `<i class="c" title="bez zgody">${z.czerwone}</i>` : ''}${
      z.zolte ? `<i class="y" title="do sprawdzenia">${z.zolte}</i>` : ''}</span></figcaption></figure>`;
}

function pustaSiatka() {
  if (!stan.katalog && !stan.osobaFiltr) return 'Wybierz katalog ze zdjęciami, a potem kliknij „Przetwórz zdjęcia”.';
  if (!stan.zdjecia.length) return stan.osobaFiltr ? 'Ta osoba nie występuje na przetworzonych zdjęciach.' : 'W tym katalogu nie ma jeszcze przetworzonych zdjęć — kliknij „Przetwórz zdjęcia”.';
  return 'Brak zdjęć w tym filtrze.';
}

async function eksportuj() {
  const zolte = stan.zdjecia.reduce((s, z) => s + z.zolte, 0);
  const m = otworzModal(`<div class="okno"><h2>Zapisz zamazane kopie</h2>
    <p>Kopie zdjęć z katalogu <b>${esc(stan.katalog)}</b> trafią do katalogu docelowego. Twarze
      <i class="kropka zielony"></i>zielone zostają, <i class="kropka czerwony"></i>czerwone i <i class="kropka zolty"></i>żółte są zamazywane.
      Oryginały nie są zmieniane, a kopie nie zawierają metadanych EXIF (GPS, miniatury).</p>
    ${zolte ? `<p class="ostrzezenie">Zostało ${zolte} żółtych twarzy do sprawdzenia — zostaną zamazane. Jeśli to osoby ze zgodą, przypisz je przed zapisem.</p>` : ''}
    <p class="podpowiedz">Program może przeoczyć twarz (bardzo małą, z profilu, zasłoniętą). Przed publikacją przejrzyj zdjęcia w „podglądzie po zamazaniu” — brakującą twarz zaznaczysz przyciskiem „Dorysuj ramkę”.</p>
    <label class="formularz">Katalog docelowy<div class="wiersz"><input id="ek-cel" value="${esc(stan.katalog.replace(/[\\/]+$/, '') + '_zamazane')}"><button id="ek-wybierz">Wybierz…</button></div></label>
    <div class="stopka"><span class="odstep"></span><button id="ek-anuluj">Anuluj</button><button id="ek-start" class="glowny">Zapisz kopie</button></div></div>`);
  $('#ek-wybierz', m).onclick = async () => {
    const k = await wybierzKatalog('Katalog docelowy', $('#ek-cel', m).value.replace(/[^\\/]+[\\/]?$/, ''));
    if (k) $('#ek-cel', m).value = k;
  };
  $('#ek-anuluj', m).onclick = () => m.remove();
  $('#ek-start', m).onclick = () => bezpiecznie(async () => {
    await uruchomZadanie('/api/eksport', { katalog: stan.katalog, podkatalogi: stan.podkatalogi, cel: $('#ek-cel', m).value });
    m.remove();
  });
}

function usunDaneKatalogu() {
  const twarze = stan.zdjecia.reduce((s, z) => s + z.zielone + z.czerwone + z.zolte, 0);
  const m = otworzModal(`<div class="okno"><h2>Usuń dane katalogu</h2>
    <p>Program usunie wszystko, co zapamiętał o ${stan.zdjecia.length} zdjęciach z katalogu <b>${esc(stan.katalog)}</b>:
      wykryte twarze (${twarze}) z wycinkami, decyzje, miniatury i podglądy. Baza zostanie od razu „odchudzona”, żeby
      usunięte dane nie zostały w pliku.</p>
    <p><b>Zostają:</b> zdjęcia na dysku, zapisane zamazane kopie, osoby z grupami i zgodami${rozpoznawanie() ? ' oraz ich wzorce twarzy' : ''}.</p>
    <p class="ostrzezenie">Jeśli zamazane kopie nie zostały jeszcze zapisane, decyzje dla tych zdjęć przepadną.</p>
    <div class="stopka"><span class="odstep"></span><button id="ud-anuluj">Anuluj</button><button id="ud-ok" class="glowny">Usuń dane</button></div></div>`);
  $('#ud-anuluj', m).onclick = () => m.remove();
  $('#ud-ok', m).onclick = () => bezpiecznie(async () => {
    const w = await api('POST', '/api/zdjecia/usun-dane', { katalog: stan.katalog, podkatalogi: stan.podkatalogi });
    m.remove();
    toast(`Usunięto dane ${w.zdjecia} zdjęć (${w.twarze} twarzy)`);
    odswiez();
  });
}

// ───────────── przeglądanie zdjęcia i decyzje ─────────────

async function otworzZdjecie(id) {
  const m = otworzModal(`<div class="przegladarka">
    <header class="przegl-gora">
      <button id="pz-poprz" title="Poprzednie (←)">‹</button><button id="pz-nast" title="Następne (→)">›</button>
      <span class="nazwa" id="pz-nazwa"></span>
      <label class="check"><input type="checkbox" id="pz-podpisy"> podpisy</label>
      <label class="check"><input type="checkbox" id="pz-zamazane"> podgląd po zamazaniu</label>
      <button id="pz-rysuj" title="Zaznacz myszą twarz, której program nie znalazł — zostanie zamazana">Dorysuj ramkę</button>
      <button id="pz-zamknij" title="Zamknij (Esc)">✕</button>
    </header>
    <div class="przegl-tresc">
      <div class="scena"><div class="obraz-ramka" id="pz-ramka"><img id="pz-obraz" alt=""><svg id="pz-svg"></svg><div class="podpisy" id="pz-warstwa"></div></div></div>
      <aside class="panel" id="pz-panel"></aside>
    </div></div>`, 'pelny');
  const p = { id, dane: null, wybrana: null, rysowanie: false, zamazane: false, zmiany: false, podpisy: wczytaj('podpisy', true) };
  const svg = $('#pz-svg', m), img = $('#pz-obraz', m), panel = $('#pz-panel', m), warstwa = $('#pz-warstwa', m);

  const lista = () => stan.zdjecia.filter(filtrZdjec);
  const zamknij = () => { m.remove(); if (p.zmiany) odswiez(); };

  async function laduj(noweId, zachowajWybor = false) {
    p.id = noweId;
    p.dane = await api('GET', '/api/zdjecia/' + noweId);
    if (!zachowajWybor || !p.dane.twarze.some(t => t.id === p.wybrana)) p.wybrana = null;
    $('#pz-nazwa', m).textContent = p.dane.nazwa;
    $('#pz-nazwa', m).title = p.dane.sciezka;
    svg.setAttribute('viewBox', `0 0 ${p.dane.szerokosc} ${p.dane.wysokosc}`);
    svg.setAttribute('preserveAspectRatio', 'none');
    obraz();
    rysuj();
  }

  function obraz() {
    img.src = `/api/zdjecia/${p.id}/podglad?zamazane=${p.zamazane}&v=${p.zamazane ? Date.now() : encodeURIComponent(p.dane.wersja)}`;
    $('#pz-ramka', m).classList.toggle('zamazane', p.zamazane);
  }

  function rysuj() {
    svg.innerHTML = p.dane.twarze.map(t =>
      `<rect class="ramka ${t.kolor} ${t.id === p.wybrana ? 'wybrana' : ''}" data-id="${t.id}" x="${t.x}" y="${t.y}" width="${t.w}" height="${t.h}"><title>${esc(t.opis)}</title></rect>`).join('');
    rysujPodpisy();
    const t = p.dane.twarze.find(x => x.id === p.wybrana);
    panel.innerHTML = t ? panelTwarzy(t) : panelListy();
    podepnijPanel(t);
  }

  // Podpis pod ramką (nad nią, gdy twarz jest przy dolnej krawędzi). Pozycja w % — nadąża za zmianą rozmiaru okna.
  // Żółta z podpowiedzią dostaje „?”; nierozpoznanych nie podpisujemy, żeby nie zaśmiecać zdjęć grupowych.
  function rysujPodpisy() {
    const { szerokosc: W, wysokosc: H } = p.dane;
    warstwa.classList.toggle('ukryte', !p.podpisy);
    warstwa.innerHTML = p.dane.twarze.filter(t => t.osobaNazwa && t.kolor !== 'szary').map(t => {
      const nad = (t.y + t.h) / H > 0.93;
      const lewo = (100 * (t.x + t.w / 2)) / W, gora = (100 * (nad ? t.y : t.y + t.h)) / H;
      return `<span class="podpis ${t.kolor} ${nad ? 'nad' : ''} ${t.id === p.wybrana ? 'wybrany' : ''}" data-id="${t.id}"
        style="left:${lewo.toFixed(3)}%;top:${gora.toFixed(3)}%" title="${esc(t.opis)}">${esc(t.osobaNazwa)}${t.kolor === 'zolty' ? '?' : ''}</span>`;
    }).join('');
    rozsunPodpisy();
  }

  // Na zdjęciach grupowych sąsiednie podpisy nachodzą na siebie — kolidujący przesuwamy w dół (te „nad” ramką: w górę).
  function rozsunPodpisy() {
    const etykiety = $$('.podpis', warstwa);
    etykiety.forEach(e => e.style.removeProperty('--dy'));
    const zajete = [];
    etykiety.map(e => ({ e, r: e.getBoundingClientRect(), nad: e.classList.contains('nad') }))
      .filter(x => x.r.width > 0)
      .sort((a, b) => a.r.top - b.r.top || a.r.left - b.r.left)
      .forEach(({ e, r, nad }) => {
        let dy = 0;
        for (let i = 0; i < 20; i++) {
          const k = zajete.find(z => r.left < z.right && r.right > z.left && r.top + dy < z.bottom && r.bottom + dy > z.top);
          if (!k) break;
          dy = nad ? k.top - r.bottom - 2 : k.bottom - r.top + 2;
        }
        if (dy) e.style.setProperty('--dy', dy + 'px');
        zajete.push({ left: r.left, right: r.right, top: r.top + dy, bottom: r.bottom + dy });
      });
  }
  img.addEventListener('load', rozsunPodpisy);
  new ResizeObserver(rozsunPodpisy).observe(img);
  warstwa.onclick = e => {
    const s = e.target.closest('.podpis');
    if (s) { p.wybrana = +s.dataset.id; rysuj(); }
  };

  function panelListy() {
    const tw = p.dane.twarze;
    const ile = k => tw.filter(t => t.kolor === k).length;
    return `<h3 style="margin-top:0">Twarze na zdjęciu (${tw.length})</h3>
      <p class="podpowiedz"><i class="kropka zielony"></i>${ile('zielony')} zostaje · <i class="kropka czerwony"></i>${ile('czerwony')} bez zgody · <i class="kropka zolty"></i>${ile('zolty')} do sprawdzenia
      <br>Kliknij twarz na zdjęciu albo na liście. Brakującą twarz zaznacz przyciskiem „Dorysuj ramkę”.</p>
      <div class="lista-twarzy">${tw.map(t => `<button class="pozycja-twarzy" data-id="${t.id}">
        <img class="mini" src="/api/twarze/${t.id}/miniatura" alt="" onerror="this.style.visibility='hidden'">
        <span><i class="kropka ${t.kolor}"></i> ${esc(t.opis)}</span></button>`).join('') || '<p class="podpowiedz">Nie znaleziono twarzy.</p>'}</div>`;
  }

  function panelTwarzy(t) {
    const naglowek = `<button class="wstecz" id="pz-wstecz">‹ wszystkie twarze</button>
      <div class="twarz-naglowek"><img class="mini-duza" src="/api/twarze/${t.id}/miniatura" alt="" onerror="this.style.visibility='hidden'">
      <div><div class="status ${t.kolor}">${esc(NAZWY_KOLOROW[t.kolor])}</div><div class="opis">${esc(t.opis)}</div>
      ${t.reczna ? '' : `<small>pewność wykrycia twarzy: ${Math.round(t.pewnosc * 100)}%</small>`}</div></div>`;
    if (t.reczna)
      return naglowek + `<p class="podpowiedz">Ramka dorysowana ręcznie — ten obszar zawsze zostanie zamazany.</p>
        <button id="pz-usun-ramke" class="niebezpieczny">Usuń ramkę</button>`;
    return naglowek + (rozpoznawanie() ? `
      <h3>Kto to jest?</h3>
      <div class="kandydaci">${t.kandydaci.filter(k => k.wynik >= 0.2).map(k => `<button class="kandydat" data-osoba="${k.osobaId}">
        <i class="kropka ${k.zgoda ? 'zielony' : 'czerwony'}"></i>${esc(k.nazwa)}<small>podobieństwo ${k.wynik.toFixed(2)}</small></button>`).join('')
        || '<span class="podpowiedz">Nikt z bazy nie jest podobny. Wybierz osobę z listy albo dodaj nową.</span>'}</div>` : `
      <h3>Kto to jest?</h3>
      <p class="podpowiedz">Przypisz osobę z listy — jeśli ma zgodę, jej twarz zostanie odsłonięta.</p>`) + `
      <div class="wybor-osoby">${wyborOsoby('pz-osoba', t.przypisana ? t.osobaId : null)}<button id="pz-przypisz">Przypisz</button></div>
      ${rozpoznawanie() ? '<label class="check"><input type="checkbox" id="pz-wzorzec" checked> zapamiętaj tę twarz jako wzorzec osoby</label>' : ''}
      <details id="pz-nowa"><summary>+ Nowa osoba</summary>${formularzOsoby('pz-n', {})}
        <button id="pz-dodaj" class="glowny">Dodaj osobę i przypisz</button></details>
      <h3>Inne decyzje</h3>
      <div class="przyciski">
        <button id="pz-nietwarz">${t.nieTwarz ? 'Jednak to twarz' : 'To nie twarz'}</button>
        <button id="pz-wymus">${t.wymus ? 'Nie wymuszaj zamazania' : 'Zamazuj zawsze'}</button>
        ${t.przypisana || t.nieTwarz || t.wymus ? '<button id="pz-cofnij">Cofnij moje decyzje</button>' : ''}
      </div>`;
  }

  async function akcja(fn, komunikat) {
    await bezpiecznie(async () => {
      await fn();
      p.zmiany = true;
      if (komunikat) toast(komunikat);
      await laduj(p.id, true);
    });
  }

  function podepnijPanel(t) {
    $$('.pozycja-twarzy', panel).forEach(b => (b.onclick = () => { p.wybrana = +b.dataset.id; rysuj(); }));
    const el = s => $(s, panel);
    if (!t) return;
    el('#pz-wstecz').onclick = () => { p.wybrana = null; rysuj(); };
    if (t.reczna) {
      el('#pz-usun-ramke').onclick = () => akcja(() => api('DELETE', '/api/twarze/' + t.id), 'Usunięto ramkę');
      return;
    }
    const wzorzec = () => el('#pz-wzorzec')?.checked ?? false;
    const przypisz = osobaId => akcja(() => api('POST', '/api/twarze/przypisz', { twarze: [t.id], osobaId, wzorzec: wzorzec() }), 'Przypisano');
    $$('.kandydat', panel).forEach(b => (b.onclick = () => przypisz(+b.dataset.osoba)));
    el('#pz-przypisz').onclick = () => { const v = el('#pz-osoba').value; if (v) przypisz(+v); else toast('Wybierz osobę z listy.', true); };
    el('#pz-dodaj').onclick = () => akcja(async () => {
      const osoba = odczytajOsobe('pz-n', panel);
      await api('POST', '/api/twarze/nowa-osoba', { twarze: [t.id], osoba, wzorzec: wzorzec() });
      await zaladujOsobyGrupy();
    }, 'Dodano osobę');
    el('#pz-nietwarz').onclick = () => akcja(() => api('POST', '/api/twarze/oznacz', { twarze: [t.id], nieTwarz: !t.nieTwarz }));
    el('#pz-wymus').onclick = () => akcja(() => api('POST', '/api/twarze/oznacz', { twarze: [t.id], wymus: !t.wymus }));
    const cofnij = el('#pz-cofnij');
    if (cofnij) cofnij.onclick = () => akcja(() => api('POST', '/api/twarze/oznacz', { twarze: [t.id], cofnij: true }), 'Cofnięto');
  }

  // klik w ramkę albo rysowanie nowej
  let start = null, nowa = null;
  const punkt = e => {
    const pt = svg.createSVGPoint();
    pt.x = e.clientX; pt.y = e.clientY;
    return pt.matrixTransform(svg.getScreenCTM().inverse());
  };
  svg.onpointerdown = e => {
    if (!p.rysowanie) {
      const r = e.target.closest('.ramka');
      if (r) { p.wybrana = +r.dataset.id; rysuj(); }
      return;
    }
    start = punkt(e);
    nowa = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
    nowa.setAttribute('class', 'ramka nowa');
    svg.appendChild(nowa);
    svg.setPointerCapture(e.pointerId);
  };
  svg.onpointermove = e => {
    if (!start) return;
    const q = punkt(e);
    nowa.setAttribute('x', Math.min(start.x, q.x)); nowa.setAttribute('y', Math.min(start.y, q.y));
    nowa.setAttribute('width', Math.abs(q.x - start.x)); nowa.setAttribute('height', Math.abs(q.y - start.y));
  };
  svg.onpointerup = e => {
    if (!start) return;
    const q = punkt(e), r = { x: Math.min(start.x, q.x), y: Math.min(start.y, q.y), w: Math.abs(q.x - start.x), h: Math.abs(q.y - start.y) };
    start = null;
    nowa.remove();
    przelaczRysowanie(false);
    if (r.w < 6 || r.h < 6) return;
    akcja(async () => { const w = await api('POST', `/api/zdjecia/${p.id}/twarze`, r); p.wybrana = w.id; }, 'Dodano ramkę — ten obszar zostanie zamazany');
  };
  function przelaczRysowanie(wl) {
    p.rysowanie = wl;
    $('#pz-rysuj', m).classList.toggle('wlaczony', wl);
    $('#pz-ramka', m).classList.toggle('rysowanie', wl);
  }

  function przejdz(o) {
    const l = lista(), i = l.findIndex(z => z.id === p.id);
    const nast = l[i + o];
    if (nast) bezpiecznie(() => laduj(nast.id));
  }

  $('#pz-poprz', m).onclick = () => przejdz(-1);
  $('#pz-nast', m).onclick = () => przejdz(1);
  $('#pz-zamknij', m).onclick = zamknij;
  $('#pz-rysuj', m).onclick = () => przelaczRysowanie(!p.rysowanie);
  $('#pz-zamazane', m).onchange = e => { p.zamazane = e.target.checked; obraz(); };
  $('#pz-podpisy', m).checked = p.podpisy;
  $('#pz-podpisy', m).onchange = e => { p.podpisy = e.target.checked; zapamietaj('podpisy', p.podpisy); rysujPodpisy(); };  m.klawisz = e => {
    if (e.target.matches('input, select, textarea')) return;
    if (e.key === 'Escape') { if (p.rysowanie) przelaczRysowanie(false); else zamknij(); }
    else if (e.key === 'ArrowLeft') przejdz(-1);
    else if (e.key === 'ArrowRight') przejdz(1);
  };
  await bezpiecznie(() => laduj(id));
}

// ───────────── widok: nierozpoznane ─────────────

async function widokNierozpoznanych() {
  await zaladujOsobyGrupy();
  if (!stan.katalog) {
    $('#widok').innerHTML = '<p class="pusto">Najpierw wybierz i przetwórz katalog w zakładce „Zdjęcia”.</p>';
    return;
  }
  const klastry = await api('GET', `/api/nierozpoznane?katalog=${encodeURIComponent(stan.katalog)}&podkatalogi=${stan.podkatalogi}`);
  $('#widok').innerHTML = `
    <section class="naglowek-widoku"><h2>Nierozpoznane twarze (${klastry.reduce((s, k) => s + k.twarze.length, 0)})</h2>
      <p>Podobne twarze z katalogu <b>${esc(stan.katalog)}</b> są zebrane w grupy — zwykle jedna grupa to jedna osoba.
      Kliknij miniaturę, żeby ją odznaczyć (np. inna osoba w grupie); dwuklik otwiera zdjęcie. Przypisane twarze trafiają do wzorców,
      więc na pozostałych zdjęciach ta osoba będzie rozpoznawana automatycznie.</p></section>
    <section class="klastry">${klastry.map(k => `
      <article class="klaster" data-nr="${k.nr}">
        <header><b>${k.twarze.length === 1 ? 'Pojedyncza twarz' : `${k.twarze.length} podobnych twarzy`}</b>${k.sugestiaNazwa ? ` · może to <b>${esc(k.sugestiaNazwa)}</b>?` : ''}</header>
        <div class="twarze">${k.twarze.map(t => `<img class="mini zaznaczona" data-id="${t.id}" data-zdjecie="${t.zdjecieId}" src="/api/twarze/${t.id}/miniatura" alt="" title="Klik — odznacz, dwuklik — otwórz zdjęcie">`).join('')}</div>
        <footer>${wyborOsoby('k-osoba-' + k.nr, k.sugestiaId)}<button class="k-przypisz">Przypisz zaznaczone</button>
          <button class="k-nowa">Nowa osoba…</button><button class="k-nietwarz">To nie twarze</button></footer>
      </article>`).join('') || '<p class="pusto">Wszystkie twarze w tym katalogu są rozpoznane albo rozstrzygnięte.</p>'}</section>`;

  $$('.klaster').forEach(el => {
    const zaznaczone = () => $$('.mini.zaznaczona', el).map(i => +i.dataset.id);
    $$('.mini', el).forEach(i => {
      i.onclick = () => i.classList.toggle('zaznaczona');
      i.ondblclick = () => {
        stan.zdjecia.some(z => z.id === +i.dataset.zdjecie) || stan.zdjecia.push({ id: +i.dataset.zdjecie });
        otworzZdjecie(+i.dataset.zdjecie);
      };
    });
    const nr = el.dataset.nr;
    $('.k-przypisz', el).onclick = () => bezpiecznie(async () => {
      const osobaId = +$(`#k-osoba-${nr}`).value;
      if (!osobaId) throw new Error('Wybierz osobę z listy.');
      if (!zaznaczone().length) throw new Error('Nie zaznaczono żadnej twarzy.');
      await api('POST', '/api/twarze/przypisz', { twarze: zaznaczone(), osobaId, wzorzec: true });
      toast('Przypisano');
      widokNierozpoznanych();
    });
    $('.k-nietwarz', el).onclick = () => bezpiecznie(async () => {
      await api('POST', '/api/twarze/oznacz', { twarze: zaznaczone(), nieTwarz: true });
      widokNierozpoznanych();
    });
    $('.k-nowa', el).onclick = () => {
      const m = otworzModal(`<div class="okno"><h2>Nowa osoba (${zaznaczone().length} twarzy)</h2>${formularzOsoby('kn', {})}
        <div class="stopka"><span class="odstep"></span><button id="kn-anuluj">Anuluj</button><button id="kn-zapisz" class="glowny">Dodaj i przypisz</button></div></div>`);
      $('#kn-anuluj', m).onclick = () => m.remove();
      $('#kn-zapisz', m).onclick = () => bezpiecznie(async () => {
        await api('POST', '/api/twarze/nowa-osoba', { twarze: zaznaczone(), osoba: odczytajOsobe('kn', m), wzorzec: true });
        m.remove();
        toast('Dodano osobę');
        widokNierozpoznanych();
      });
    };
  });
}

// ───────────── widok: osoby i grupy ─────────────

async function widokOsob() {
  await zaladujOsobyGrupy();
  if (stan.grupaWybrana !== null && !stan.grupy.some(g => g.id === stan.grupaWybrana)) stan.grupaWybrana = null;
  const grupa = stan.grupy.find(g => g.id === stan.grupaWybrana);

  $('#widok').innerHTML = `<div class="dwie-kolumny">
    <aside class="grupy-panel">
      <h3>Grupy</h3>
      <button class="grupa ${grupa ? '' : 'aktywna'}" data-g="">Wszyscy <small>${stan.osoby.length}</small></button>
      ${stan.grupy.map(g => `<button class="grupa ${g.id === stan.grupaWybrana ? 'aktywna' : ''} ${g.archiwalna ? 'archiwalna' : ''}" data-g="${g.id}">${esc(g.nazwa)} <small>${g.liczba}</small></button>`).join('')}
      <div class="wiersz" style="margin-top:8px"><input id="nowa-grupa" placeholder="Nowa grupa, np. 3b"><button id="dodaj-grupe">Dodaj</button></div>
      ${grupa ? `<div class="akcje-grupy"><h4>${esc(grupa.nazwa)}</h4>
        <button id="g-nazwa">Zmień nazwę</button>
        <button id="g-arch">${grupa.archiwalna ? 'Przywróć z archiwum' : 'Archiwizuj (np. po roku szkolnym)'}</button>
        <button id="g-zgoda">Ustaw zgodę dla wszystkich…</button>
        <button id="g-usun" class="niebezpieczny">Usuń grupę</button></div>` : ''}
    </aside>
    <section class="osoby-panel">
      <div class="pasek-narzedzi">
        <input id="szukaj" placeholder="Szukaj osoby…" value="${esc(stan.szukaj)}">
        <button id="dodaj-osobe" class="glowny">+ Dodaj osobę</button>
        <button id="import-csv">Import listy z CSV…</button>
        ${rozpoznawanie() ? '<button id="import-wzorcow">Import wzorców z katalogu…</button>' : ''}
      </div>
      <div id="tabela-osob"></div>
    </section></div>`;
  rysujTabeleOsob();

  $$('.grupa').forEach(b => (b.onclick = () => { stan.grupaWybrana = b.dataset.g ? +b.dataset.g : null; widokOsob(); }));
  $('#szukaj').oninput = e => { stan.szukaj = e.target.value; rysujTabeleOsob(); };
  $('#dodaj-grupe').onclick = () => bezpiecznie(async () => {
    const nazwa = $('#nowa-grupa').value.trim();
    if (!nazwa) throw new Error('Podaj nazwę grupy.');
    const w = await api('POST', '/api/grupy', { nazwa, archiwalna: false });
    stan.grupaWybrana = w.id;
    widokOsob();
  });
  $('#nowa-grupa').onkeydown = e => { if (e.key === 'Enter') $('#dodaj-grupe').click(); };
  $('#dodaj-osobe').onclick = () => edytujOsobe(null);
  $('#import-csv').onclick = importCsv;
  if (rozpoznawanie()) $('#import-wzorcow').onclick = importWzorcow;
  if (grupa) {
    $('#g-nazwa').onclick = () => {
      const nazwa = prompt('Nowa nazwa grupy:', grupa.nazwa);
      if (nazwa) bezpiecznie(async () => { await api('PUT', '/api/grupy/' + grupa.id, { nazwa, archiwalna: grupa.archiwalna }); widokOsob(); });
    };
    $('#g-arch').onclick = () => bezpiecznie(async () => {
      await api('PUT', '/api/grupy/' + grupa.id, { nazwa: grupa.nazwa, archiwalna: !grupa.archiwalna });
      widokOsob();
    });
    $('#g-usun').onclick = () => {
      if (!confirm(`Usunąć grupę „${grupa.nazwa}”? Osoby zostają w bazie, znika tylko przynależność do grupy.`)) return;
      bezpiecznie(async () => { await api('DELETE', '/api/grupy/' + grupa.id); stan.grupaWybrana = null; widokOsob(); });
    };
    $('#g-zgoda').onclick = () => {
      const m = otworzModal(`<div class="okno"><h2>Zgoda dla grupy „${esc(grupa.nazwa)}”</h2>
        <p>Ustawienie zostanie zapisane każdej z ${grupa.liczba} osób osobno — później zmienisz je pojedynczo, np. gdy ktoś wycofa zgodę.</p>
        <div class="formularz"><label class="check"><input type="checkbox" id="gz-zgoda" checked> mają zgodę na publikację wizerunku</label>
        <label>Zgoda ważna do <small>(puste = bezterminowo)</small><input type="date" id="gz-do"></label></div>
        <div class="stopka"><span class="odstep"></span><button id="gz-anuluj">Anuluj</button><button id="gz-ok" class="glowny">Zapisz</button></div></div>`);
      $('#gz-anuluj', m).onclick = () => m.remove();
      $('#gz-ok', m).onclick = () => bezpiecznie(async () => {
        const w = await api('POST', `/api/grupy/${grupa.id}/zgoda`, { zgoda: $('#gz-zgoda', m).checked, zgodaDo: $('#gz-do', m).value || null });
        m.remove();
        toast(`Zmieniono zgodę ${w.zmieniono} osobom`);
        widokOsob();
      });
    };
  }
}

function rysujTabeleOsob() {
  const szukaj = stan.szukaj.trim().toLocaleLowerCase('pl');
  const lista = stan.osoby.filter(o => (stan.grupaWybrana === null || o.grupy.includes(stan.grupaWybrana))
    && (!szukaj || o.nazwa.toLocaleLowerCase('pl').includes(szukaj)));
  $('#tabela-osob').innerHTML = !lista.length
    ? `<p class="pusto">${stan.osoby.length ? 'Brak osób spełniających warunki.' : 'Baza osób jest pusta. Dodaj osoby ręcznie, zaimportuj listę z CSV albo katalog ze zdjęciami wzorcowymi.'}</p>`
    : `<table class="tabela"><thead><tr><th>Osoba</th><th>Grupy</th><th>Zgoda</th>${rozpoznawanie() ? '<th>Wzorce twarzy</th>' : ''}<th>Zdjęcia</th></tr></thead><tbody>${lista.map(o => `
      <tr data-id="${o.id}">
        <td><b>${esc(o.nazwa)}</b>${o.uwagi ? `<br><small>${esc(o.uwagi)}</small>` : ''}</td>
        <td>${o.grupy.map(g => `<span class="znacznik grupa-z">${esc(nazwaGrupy(g))}</span>`).join('')}</td>
        <td>${o.zgodaWazna ? `<span class="znacznik zielony">tak${o.zgodaDo ? ' do ' + o.zgodaDo : ''}</span>`
          : `<span class="znacznik czerwony">${o.zgoda ? 'wygasła ' + o.zgodaDo : 'brak'}</span>`}</td>
        ${rozpoznawanie() ? `<td>${o.wzorce ? `${o.wzorce} <small>· najnowszy ${o.najnowszyWzorzec}</small>` : ''}
          ${o.wzorce === 0 ? '<span class="ostrzezenie">brak — osoba nie będzie rozpoznawana</span>' : ''}
          ${o.staryWzorzec ? '<br><span class="ostrzezenie" title="Twarze, zwłaszcza dzieci, szybko się zmieniają — dodaj aktualne zdjęcie">⚠ stare wzorce</span>' : ''}</td>` : ''}
        <td>${o.zdjecia ? `<a href="#" class="pokaz-zdjecia" data-id="${o.id}">${o.zdjecia}</a>` : '0'}</td>
      </tr>`).join('')}</tbody></table>`;
  $$('#tabela-osob tbody tr').forEach(tr => (tr.onclick = e => {
    const o = stan.osoby.find(x => x.id === +tr.dataset.id);
    if (e.target.closest('.pokaz-zdjecia')) { e.preventDefault(); pokazZdjeciaOsoby(o); }
    else edytujOsobe(o);
  }));
}

function pokazZdjeciaOsoby(o) {
  stan.osobaFiltr = { id: o.id, nazwa: o.nazwa };
  stan.filtr = 'wszystkie';
  $$('.modal').forEach(m => m.remove());
  pokaz('zdjecia');
}

function edytujOsobe(o) {
  const nowa = !o;
  const dane = o ?? { zgoda: false, grupy: stan.grupaWybrana !== null ? [stan.grupaWybrana] : [] };
  const pokazWzorce = !nowa && rozpoznawanie();
  const m = otworzModal(`<div class="okno"><h2>${nowa ? 'Nowa osoba' : esc(o.nazwa)}</h2>${formularzOsoby('eo', dane)}
    ${!rozpoznawanie() ? ''
      : nowa ? '<p class="podpowiedz">Wzorce twarzy dodasz, przypisując tę osobę do twarzy na zdjęciu (z zaznaczonym „zapamiętaj jako wzorzec”).</p>'
      : `<h3>Wzorce twarzy</h3><div id="eo-wzorce" class="wzorce"><small>ładowanie…</small></div>
         <p class="podpowiedz">Usuń wzorzec, który przedstawia kogoś innego — psułby rozpoznawanie.</p>`}
    <div class="stopka">${nowa ? '' : '<button id="eo-usun" class="niebezpieczny">Usuń osobę</button><button id="eo-zdjecia">Pokaż zdjęcia z tą osobą</button>'}
      <span class="odstep"></span><button id="eo-anuluj">Anuluj</button><button id="eo-zapisz" class="glowny">Zapisz</button></div></div>`);

  async function wzorce() {
    const lista = await api('GET', `/api/osoby/${o.id}/wzorce`);
    $('#eo-wzorce', m).innerHTML = lista.map(w => `<figure class="wzorzec" title="${esc(w.zrodlo)}">
      <img src="/api/wzorce/${w.id}/miniatura" alt=""><figcaption>${esc(w.data)}</figcaption>
      <button data-id="${w.id}" title="Usuń wzorzec">×</button></figure>`).join('') || '<small>Brak wzorców.</small>';
    $$('#eo-wzorce button', m).forEach(b => (b.onclick = () => bezpiecznie(async () => {
      await api('DELETE', '/api/wzorce/' + b.dataset.id);
      wzorce();
    })));
  }
  if (pokazWzorce) bezpiecznie(wzorce);

  $('#eo-anuluj', m).onclick = () => { m.remove(); widokOsob(); };
  $('#eo-zapisz', m).onclick = () => bezpiecznie(async () => {
    const d = odczytajOsobe('eo', m);
    await (nowa ? api('POST', '/api/osoby', d) : api('PUT', '/api/osoby/' + o.id, d));
    m.remove();
    toast('Zapisano');
    odswiez();
  });
  if (!nowa) {
    $('#eo-zdjecia', m).onclick = () => pokazZdjeciaOsoby(o);
    $('#eo-usun', m).onclick = () => {
      if (!confirm(`Usunąć osobę „${o.nazwa}” razem z jej wzorcami twarzy? Tego nie da się cofnąć.`)) return;
      bezpiecznie(async () => { await api('DELETE', '/api/osoby/' + o.id); m.remove(); toast('Usunięto osobę'); odswiez(); });
    };
  }
}

function importCsv() {
  const m = otworzModal(`<div class="okno"><h2>Import listy osób z CSV</h2>
    <p>Kolumny w kolejności: <b>imię; nazwisko; grupy; zgoda; zgoda ważna do</b>. Pierwszy wiersz może być nagłówkiem.</p>
    <ul class="podpowiedz">
      <li>Kilka grup w jednej komórce rozdziel znakiem <b>|</b> lub <b>/</b> — np. <code>3b|chór</code>. Brakujące grupy zostaną utworzone.</li>
      <li>Zgoda: <code>tak</code>, <code>1</code> albo <code>x</code> = jest; puste lub <code>nie</code> = brak.</li>
      <li>Data: <code>2027-06-30</code> albo <code>30.06.2027</code>.</li>
      <li>Osoba o tym samym imieniu i nazwisku zostanie zaktualizowana, a nie zdublowana.</li>
      <li>Plik z polskiego Excela („CSV rozdzielany średnikami”) jest czytany poprawnie.</li>
    </ul>
    <input type="file" id="ic-plik" accept=".csv,.txt">
    <div class="stopka"><span class="odstep"></span><button id="ic-anuluj">Anuluj</button><button id="ic-ok" class="glowny">Importuj</button></div></div>`);
  $('#ic-anuluj', m).onclick = () => m.remove();
  $('#ic-ok', m).onclick = () => bezpiecznie(async () => {
    const plik = $('#ic-plik', m).files[0];
    if (!plik) throw new Error('Wybierz plik.');
    const w = await api('POST', '/api/import/csv', plik);
    m.remove();
    pokazBledy({ rodzaj: 'Import listy', komunikat: `Dodano: ${w.dodano}, zaktualizowano: ${w.zmieniono}, nowe grupy: ${w.noweGrupy}.`, bledy: w.bledy.length ? w.bledy : ['bez uwag'] });
    widokOsob();
  });
}

function importWzorcow() {
  let katalog = '';
  const m = otworzModal(`<div class="okno"><h2>Import wzorców z katalogu</h2>
    <p>Katalog z podkatalogami nazwanymi jak osoby, a w nich zdjęcia twarzy (najlepiej 3–10 różnych, aktualnych):</p>
    <pre class="podpowiedz">wzorce\\
  Jan Kowalski\\  zdjecie1.jpg, zdjecie2.jpg…
  Anna Nowak\\    …</pre>
    <p class="podpowiedz">Z każdego zdjęcia brana jest największa twarz. Osoby, których nie ma w bazie, zostaną dodane.</p>
    <div class="formularz">
      <label>Katalog wzorców<div class="wiersz"><input id="iw-kat" readonly placeholder="nie wybrano"><button id="iw-wybierz">Wybierz…</button></div></label>
      <label>Dodaj do grupy<select id="iw-grupa"><option value="">— bez grupy —</option>${stan.grupy.map(g => `<option value="${g.id}">${esc(g.nazwa)}</option>`).join('')}</select></label>
      <label class="check"><input type="checkbox" id="iw-zgoda"> nowo dodane osoby mają zgodę</label>
    </div>
    <div class="stopka"><span class="odstep"></span><button id="iw-anuluj">Anuluj</button><button id="iw-ok" class="glowny">Importuj</button></div></div>`);
  $('#iw-wybierz', m).onclick = async () => {
    const k = await wybierzKatalog('Katalog wzorców', katalog || stan.katalog);
    if (k) { katalog = k; $('#iw-kat', m).value = k; }
  };
  $('#iw-anuluj', m).onclick = () => m.remove();
  $('#iw-ok', m).onclick = () => bezpiecznie(async () => {
    if (!katalog) throw new Error('Wybierz katalog wzorców.');
    const g = $('#iw-grupa', m).value;
    await uruchomZadanie('/api/import/wzorce', { katalog, grupaId: g ? +g : null, zgoda: $('#iw-zgoda', m).checked });
    m.remove();
  });
}

// ───────────── widok: ustawienia ─────────────

// [klucz, nazwa, opis, krok (null = pole wyboru), dotyczy tylko trybu z rozpoznawaniem]
const USTAWIENIA = [
  ['progZgody', 'Próg „zielonego” (automatyczne odsłonięcie)', 'Minimalne podobieństwo do wzorców, żeby twarz osoby ze zgodą została odsłonięta bez potwierdzania. Wyżej = ostrożniej (więcej żółtych do sprawdzenia).', 0.01, true],
  ['progRozpoznania', 'Próg rozpoznania', 'Od tej wartości twarz uznaje się za daną osobę (osoba bez zgody → czerwona; ze zgodą, ale poniżej progu „zielonego” → żółta z podpowiedzią). Zalecane dla modelu SFace: 0,363.', 0.01, true],
  ['margines', 'Wymagana przewaga nad drugą osobą', 'Jeśli dwie osoby z bazy są podobne (rodzeństwo), zielone tylko przy takiej przewadze najlepszej nad drugą.', 0.01, true],
  ['dwaWzorce', 'Zgodność z dwoma wzorcami', 'Zielone tylko, gdy twarz pasuje do co najmniej dwóch wzorców osoby (gdy osoba ma ich ≥ 2).', null, true],
  ['progWykrywania', 'Czułość wykrywania twarzy', 'Pewność, od której coś uznaje się za twarz. Niżej = mniej przeoczonych twarzy, ale więcej fałszywych ramek. Działa przy ponownym przetworzeniu.', 0.05, false],
  ['maleTwarze', 'Szukaj małych twarzy w tle', 'Dodatkowe przejście w wysokiej rozdzielczości — wolniej, ale mniej przeoczeń na zdjęciach grupowych.', null, false],
  ['silaZamazania', 'Siła zamazania', 'Na ile pól w poziomie rozbić twarz przed rozmyciem. Mniej = mocniej (3–16).', 1, false],
  ['miesiaceWzorca', 'Wiek wzorców (miesiące)', 'Po tylu miesiącach od najnowszego zdjęcia wzorcowego osoba dostaje ostrzeżenie „stare wzorce”.', 1, true],
];

async function widokUstawien() {
  const [u, info] = await Promise.all([api('GET', '/api/ustawienia'), api('GET', '/api/info')]);
  stan.ustawienia = u;
  const bez = u.tryb === 'bezRozpoznawania';
  $('#widok').innerHTML = `<h2>Ustawienia</h2><div class="ustawienia">
    <fieldset class="tryby"><legend>Tryb pracy</legend>
      <label class="tryb"><input type="radio" name="tryb" value="rozpoznawanie" ${bez ? '' : 'checked'}>
        <span><b>Z rozpoznawaniem twarzy</b><br><small>Program porównuje twarze ze wzorcami osób i sam odsłania osoby ze zgodą.
        Wektory cech twarzy (dane biometryczne) istnieją tylko w pamięci, póki program działa — na dysku leżą wycinki twarzy,
        z których po starcie liczy się je od nowa. Porównywana jest każda twarz, także osób bez zgody.</small></span></label>
      <label class="tryb"><input type="radio" name="tryb" value="bezRozpoznawania" ${bez ? 'checked' : ''}>
        <span><b>Bez rozpoznawania — bez danych biometrycznych</b><br><small>Program tylko znajduje twarze i zamazuje wszystkie.
        Osoby ze zgodą odsłania operator, przypisując je z listy. Bez wzorców, porównywania i grupowania podobnych twarzy.</small></span></label>
    </fieldset>
    ${USTAWIENIA.map(([k, nazwa, opis, krok, tylkoRozp]) => `<div class="ustawienie ${tylkoRozp ? 'tylko-rozp' : ''}"><label for="u-${k}"><b>${nazwa}</b></label>
      ${krok === null ? `<input type="checkbox" id="u-${k}" ${u[k] ? 'checked' : ''}>` : `<input type="number" id="u-${k}" step="${krok}" value="${u[k]}">`}
      <p>${opis}</p></div>`).join('')}
    <div class="stopka"><small>Dane programu (baza osób, wycinki twarzy, miniatury): ${esc(info.katalogDanych)}</small><span class="odstep"></span>
      <button id="u-zapisz" class="glowny">Zapisz</button></div></div>`;

  const wybranyTryb = () => $('input[name=tryb]:checked').value;
  const pokazPola = () => $$('.tylko-rozp').forEach(e => (e.hidden = wybranyTryb() === 'bezRozpoznawania'));
  $$('input[name=tryb]').forEach(i => (i.onchange = pokazPola));
  pokazPola();

  $('#u-zapisz').onclick = () => bezpiecznie(async () => {
    const nowe = { tryb: wybranyTryb() };
    for (const [k, , , krok] of USTAWIENIA) nowe[k] = krok === null ? $('#u-' + k).checked : +$('#u-' + k).value;
    const wylaczenie = nowe.tryb === 'bezRozpoznawania' && !bez, wlaczenie = nowe.tryb !== 'bezRozpoznawania' && bez;
    if (wylaczenie) {
      const wzorce = (await api('GET', '/api/osoby')).reduce((s, o) => s + o.wzorce, 0);
      if (!confirm(`Przejście na tryb bez rozpoznawania usunie wszystkie wzorce twarzy (${wzorce}), wycinki twarzy przygotowane `
        + 'do porównywania i automatyczne dopasowania. Osoby, grupy, zgody i ręczne decyzje zostają.\n\nKontynuować?')) return;
    }
    await api('PUT', '/api/ustawienia', nowe);
    await zaladujUstawienia();
    toast(wlaczenie ? 'Włączono rozpoznawanie — program przygotowuje twarze do porównywania. Wzorce osób trzeba dodać od nowa.'
      : wylaczenie ? 'Tryb bez rozpoznawania — wzorce i wycinki twarzy usunięte' : 'Zapisano ustawienia');
    if (wlaczenie) sprawdzZadanie();
    widokUstawien();
  });
}

// ───────────── start ─────────────

odswiez();
sprawdzZadanie();
