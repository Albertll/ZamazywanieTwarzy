# Zamazywanie twarzy — prototyp

Lokalna aplikacja, która rozpoznaje na zdjęciach osoby z bazy i **zamazuje wszystkich poza osobami ze zgodą**
(tryb „dozwolonych twarzy”). Działa w całości na komputerze, bez internetu i bez karty graficznej.
Interfejs otwiera się w przeglądarce, a pod spodem działa lokalny serwer (.NET 10).

## Uruchomienie

Z repozytorium: `dotnet run --project src\ZamazywanieTwarzy` (wymaga .NET 10 SDK) albo zbuduj wersję
„jeden plik, bez instalowania .NET” (sekcja *Budowanie*) — powstaje `publikacja\ZamazywanieTwarzy.exe`, obok którego
muszą leżeć katalogi `modele` i `wwwroot`. Program otwiera przeglądarkę sam. Okno konsoli to serwer: jego zamknięcie albo przycisk „Zakończ” wyłącza program.

Dane programu (baza osób i wzorców twarzy, miniatury) trafiają do `dane\` obok pliku .exe.
Inne miejsce: `ZamazywanieTwarzy.exe --dane D:\Zaszyfrowany\dane`. **To dane biometryczne — trzymaj je na zaszyfrowanym dysku.**

Szybka próba na zdjęciach testowych (domena publiczna / CC — autorzy i licencje w `testdane\ZRODLA.md`;
zdjęć nie ma w repozytorium):
0. `powershell -ExecutionPolicy Bypass -File testdane\pobierz.ps1` — pobiera 32 zdjęcia (55 MB) z Wikimedia Commons.
1. *Osoby i grupy* → **Import wzorców z katalogu…** → `testdane\wzorce`, zaznacz „mają zgodę”.
2. *Zdjęcia* → **Wybierz…** → `testdane\zdjecia` → **Przetwórz zdjęcia**.
3. Kliknij zdjęcie, sprawdź żółte twarze, potem **Zapisz zamazane kopie…**.

## Jak to działa

| Kolor | Znaczenie | W kopii |
|---|---|---|
| 🟢 zielony | osoba ze ważną zgodą — rozpoznana z dużą pewnością albo przypisana ręcznie | zostaje |
| 🔴 czerwony | osoba bez zgody (lub zgoda wygasła), ramka dorysowana ręcznie, „zamazuj zawsze” | zamazana |
| 🟡 żółty | nierozpoznana albo „prawdopodobnie X”, ale za mała pewność | zamazana, do sprawdzenia |
| ⚪ szary | oznaczona ręcznie jako „to nie twarz” | bez zmian |

Zasada: **każda wątpliwość = zamazanie.** Automatyczne „zielone” wymaga jednocześnie:
- podobieństwa ≥ 0,50 (*próg „zielonego”*),
- przewagi ≥ 0,05 nad drugą najbardziej podobną osobą (rodzeństwo, sobowtóry),
- zgodności z co najmniej dwoma wzorcami osoby (jeśli ma ich ≥ 2).

Kliknięcie twarzy pozwala przypisać ją do osoby (z podpowiedzią 3 najbardziej podobnych), dodać nową osobę,
oznaczyć „to nie twarz” albo „zamazuj zawsze”. Przypisana twarz domyślnie trafia do wzorców osoby — program
uczy się w trakcie pracy i od razu ponownie dopasowuje wszystkie pozostałe twarze.
**Dorysuj ramkę** służy do twarzy, której program nie znalazł — to jedyna obrona przed przeoczeniem.

**Podpisy** (przełącznik w podglądzie zdjęcia): pod ramką rozpoznanej osoby jej imię i nazwisko w kolorze ramki,
„Imię Nazwisko?” dla żółtej z podpowiedzią; nierozpoznanych się nie podpisuje. Kliknięcie podpisu zaznacza twarz,
a nachodzące na siebie podpisy na zdjęciach grupowych są rozsuwane. Podpisy są tylko w programie — **zapisywane kopie
ich nie zawierają**.

Pozostałe funkcje:
- **Grupy** (klasa, chór, kadra; osoba może być w kilku). Przy przetwarzaniu można wybrać, kto może być na zdjęciach —
  twarze są wtedy porównywane tylko z tymi osobami (mniej pomyłek). Grupę można zarchiwizować po roku szkolnym.
- **Import listy z CSV** (`imię; nazwisko; grupy; zgoda; zgoda ważna do`) — także plik z polskiego Excela (Windows-1250).
- **Zgoda z datą ważności** — po tej dacie osoba sama zmienia się na czerwoną.
- **Ostrzeżenie „stare wzorce”** — gdy najnowsze zdjęcie wzorcowe ma ponad 12 miesięcy (twarze dzieci szybko się zmieniają).
  Wiek wzorca brany jest z EXIF (data zrobienia), a gdy go brak — z daty modyfikacji pliku.
- **Nierozpoznane** — żółte twarze zebrane w grupy podobnych; przypisanie całej grupy jednym kliknięciem.
- **Zapis kopii** do osobnego katalogu (oryginały nietknięte), bez EXIF (GPS, miniatury), plus `raport.csv`.
  Zdjęcie zmienione po przetworzeniu nie zostanie zapisane, dopóki nie przetworzysz go ponownie.

## Wyniki na zdjęciach testowych (dorośli, 17 zdjęć, 272 twarze)

- Czas: 17 zdjęć 2600–4096 px w ~12 s (3 zdjęcia równolegle, procesor bez GPU).
- **Żadnej obcej osoby nie odsłonięto.** Najwyższe podobieństwo obcej osoby do wzorca: 0,448; najniższe właściwej
  osoby en face: 0,513. Stąd domyślny próg „zielonego” 0,50.
- 25 z 27 wystąpień osób ze wzorców rozpoznanych automatycznie; 2 twarze z profilu zostały żółte (do potwierdzenia).
- Na zdjęciach bez osób ze wzorców (astronauci, gabinet Reagana) — 0 zielonych.
- Po jednym ręcznym przypisaniu nowej osoby program sam rozpoznał ją na innym zdjęciu.

**To dane dla dorosłych z publicznych zdjęć.** Dla dzieci rozpoznawanie będzie słabsze (modele uczone głównie na
dorosłych, rodzeństwo, szybkie zmiany twarzy) — przed użyciem trzeba sprawdzić skuteczność na własnych zdjęciach
i ewentualnie podnieść próg „zielonego” w *Ustawieniach*.

## Znane ograniczenia prototypu

- **Przeoczone twarze nie są zamazywane.** Na zdjęciu tłumu (`14_tlum_sala_kongresu`) nie wykryto drobnych,
  optycznie rozmytych twarzy na galerii. Przed publikacją trzeba przejrzeć „podgląd po zamazaniu” i dorysować brakujące.
- Fałszywe wykrycia (tył głowy, oparcie krzesła) są zamazywane — nieszkodliwe, ale zaśmiecają listę „Nierozpoznane”.
- Bardzo małe, nieostre twarze bywają do siebie podobne niezależnie od osoby, więc grupy w „Nierozpoznanych” warto przejrzeć
  przed przypisaniem (kliknięcie miniatury ją odznacza).
- Nie ma zakresów zgody (np. „strona www tak, Facebook nie”), cofania zmian ani kont użytkowników.
- Formaty: JPG, PNG, WEBP, BMP (bez HEIC z iPhone'a).

## Budowanie

Projekt: `src\ZamazywanieTwarzy` (.NET 10, ASP.NET Core minimal API, ONNX Runtime 1.31, SkiaSharp 4, SQLite).

```
dotnet run --project src\ZamazywanieTwarzy
dotnet publish src\ZamazywanieTwarzy -c Release -r win-x64 --self-contained -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publikacja
```

| Plik | Rola |
|---|---|
| `Detektor.cs` | YuNet: kafelki 640×640 w 3 skalach (duże twarze / typowe / małe w tle), scalanie NMS |
| `Rozpoznawanie.cs` | SFace: wyrównanie twarzy wg 5 punktów do 112×112, wektor 128 cech |
| `Zamazywanie.cs` | owal z zapasem, zmniejszenie do kilku pikseli + rozmycie (nieodwracalne) |
| `Silnik.cs` | przetwarzanie, dopasowanie do osób, reguły kolorów, import, zapis kopii |
| `Baza.cs` | SQLite: osoby, grupy, wzorce, zdjęcia, twarze |
| `Program.cs`, `Api.cs` | serwer na 127.0.0.1, losowy klucz w adresie, API dla interfejsu |
| `wwwroot\` | interfejs (HTML/CSS/JS bez bibliotek) |

Bezpieczeństwo serwera: nasłuch tylko na 127.0.0.1, losowy klucz w adresie otwieranym przy starcie (ciasteczko
`SameSite=Strict`), sprawdzanie nagłówka `Host` (ochrona przed DNS rebinding) i własny nagłówek przy zmianach (CSRF).

Testy: `proba\pw\ui-test.js` (przeklikanie ekranów) i `proba\pw\ui-akcje.js` (przypisanie, nowa osoba, „to nie twarz”,
dorysowanie ramki, import CSV w Windows-1250, zapis kopii) — Edge przez playwright-core, serwer z `ZAMAZYWANIE_TOKEN`
i `--port 5077`. Zrzuty ekranu: `proba\zrzuty\`.

## Licencje modeli

- YuNet `face_detection_yunet_2023mar.onnx` — OpenCV Zoo, **MIT**
- SFace `face_recognition_sface_2021dec.onnx` — OpenCV Zoo, **Apache 2.0**

Obie pozwalają na użycie komercyjne (w przeciwieństwie do popularnych modeli InsightFace).
Teksty licencji i źródła: `src\ZamazywanieTwarzy\modele\` (kopiowane też do wersji opublikowanej).
