# Porównanie modeli na zdjęciach testowych

Wygenerowano 2026-10-29 12:47 narzędziem `proba/porownanie`. Zdjęcia: `testdane` (17 grupowych, 15 wzorców, 3 osoby).
Modele InsightFace są tu wyłącznie punktem odniesienia (licencja: tylko niekomercyjne badania).

## Rozpoznawanie (te same twarze z YuNet, wzorzec = najlepszy z 5 wzorców osoby)

Oznaczone wystąpienia osób ze wzorców: 27 z 27. Porównania z osobami obcymi: każda
pozostała twarz z każdą osobą ze wzorców, której na tym zdjęciu na pewno nie ma (wg `testdane/ZRODLA.md`).

| Model | właściwa osoba na 1. miejscu | min / mediana podobieństwa właściwej osoby | maks. podobieństwo obcej osoby | odsłonięte automatycznie przy zerze pomyłek | rozdzielenie d′ | ms / twarz |
|---|---|---|---|---|---|---|
| SFace (obecny) | 27/27 | 0.434 / 0.679 | 0.427 (n=278) | **27/27** | 4.92 | 39.0 |
| ArcFace R50 (InsightFace) | 27/27 | 0.423 / 0.683 | 0.249 (n=278) | **27/27** | 6.31 | 164.0 |

„Odsłonięte automatycznie przy zerze pomyłek”: ile właściwych osób przekracza najwyższe podobieństwo, jakie osiągnęła
jakakolwiek obca twarz — czyli ile zielonych byłoby przy progu ustawionym tak, żeby na tym zbiorze nikt obcy nie został odsłonięty.
Skale podobieństwa różnią się między modelami, więc porównywać należy tę kolumnę i d′, a nie surowe wartości.

## Wykrywanie (te same 3 skale i kafelki 640×640 co w programie)

| Detektor | wykrytych ramek | oznaczonych osób znalezionych | s / zdjęcie |
|---|---|---|---|
| YuNet, próg 0,6 (obecnie) | 277 | 27/27 | 0.60 |
| YuNet, próg 0,5 | 317 | 27/27 | 0.59 |
| SCRFD-10G, próg 0,5 | 251 | 27/27 | 2.81 |

Różnice między detektorami (arkusze wycinków w `proba/porownanie/wyniki/`, poza repozytorium):

- tylko SCRFD-10G, próg 0,5: **10** (`tylko_SCRFD_10G__próg_0_5.png`)
- tylko YuNet 0,6 (nie ma w: SCRFD-10G, próg 0,5): **36** (`tylko_YuNet_0_6__nie_ma_w__SCRFD_10G__próg_0_5_.png`)
- tylko YuNet 0,6 (nie ma w: YuNet, próg 0,5): **0** (`tylko_YuNet_0_6__nie_ma_w__YuNet__próg_0_5_.png`)
- tylko YuNet, próg 0,5: **40** (`tylko_YuNet__próg_0_5.png`)
