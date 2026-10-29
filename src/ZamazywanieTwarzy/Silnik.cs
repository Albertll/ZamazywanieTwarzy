using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace ZamazywanieTwarzy;

/// <summary>Zadanie w tle (przetwarzanie, import, zapis kopii) — jego postęp odpytuje przeglądarka.</summary>
sealed class Zadanie
{
    int _gotowe;
    public required string Rodzaj { get; init; }
    public bool Trwa { get; set; } = true;
    public int Wszystkie { get; set; }
    public int Gotowe => _gotowe;
    public string? Biezacy { get; set; }
    public ConcurrentQueue<string> Bledy { get; } = new();
    public string? Komunikat { get; set; }
    public DateTime Start { get; } = DateTime.Now;
    public DateTime? Koniec { get; set; }
    public void Krok() => Interlocked.Increment(ref _gotowe);

    public void Etap(string nazwa, int wszystkie)
    {
        Biezacy = nazwa;
        Wszystkie = wszystkie;
        Interlocked.Exchange(ref _gotowe, 0);
    }
}

/// <summary>Cała logika aplikacji: przetwarzanie zdjęć, dopasowanie do osób, decyzje, import i zapis kopii.</summary>
sealed class Silnik : IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly StringComparer Polski = StringComparer.Create(new CultureInfo("pl-PL"), true);

    readonly Baza _baza;
    readonly Detektor _detektor;
    readonly Rozpoznawanie _rozpoznawanie;
    readonly SessionOptions _opcje;
    readonly Lock _blokadaZadania = new(), _blokadaDopasowania = new(), _blokadaPamieci = new();

    // Wektory cech (dane biometryczne) istnieją tylko tutaj, w pamięci procesu — na dysku leżą wyrównane wycinki
    // twarzy, z których po starcie programu liczy się je od nowa. Klucz: id wiersza w tabeli twarze / wzorce.
    readonly ConcurrentDictionary<long, float[]> _cechyTwarzy = new(), _cechyWzorcow = new();
    CancellationTokenSource? _anuluj;
    Ustawienia? _ustawienia;

    public string KatalogDanych { get; }
    public Zadanie? Zadanie { get; private set; }
    public int Rownoleglosc { get; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    static Silnik() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public Silnik(string katalogModeli, string katalogDanych)
    {
        KatalogDanych = katalogDanych;
        Directory.CreateDirectory(Path.Combine(katalogDanych, "miniatury"));
        Directory.CreateDirectory(Path.Combine(katalogDanych, "podglady"));
        _opcje = new SessionOptions
        {
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / Rownoleglosc),
        };
        _detektor = new Detektor(Path.Combine(katalogModeli, "face_detection_yunet_2023mar.onnx"), _opcje);
        _rozpoznawanie = new Rozpoznawanie(Path.Combine(katalogModeli, "face_recognition_sface_2021dec.onnx"), _opcje);
        _baza = new Baza(Path.Combine(katalogDanych, "baza.db"));
    }

    string SciezkaMiniatury(long id) => Path.Combine(KatalogDanych, "miniatury", $"{id}.jpg");
    string SciezkaPodgladu(long id) => Path.Combine(KatalogDanych, "podglady", $"{id}.jpg");

    // ───────────── ustawienia ─────────────

    public Ustawienia Ustawienia
    {
        get
        {
            if (_ustawienia is not null) return _ustawienia;
            using var c = _baza.Otworz();
            var json = Baza.Skalar(c, "SELECT wartosc FROM ustawienia WHERE klucz='ustawienia'") as string;
            return _ustawienia = json is null ? new Ustawienia() : JsonSerializer.Deserialize<Ustawienia>(json, Json) ?? new Ustawienia();
        }
    }

    public void ZapiszUstawienia(Ustawienia u)
    {
        u.ProgRozpoznania = Math.Clamp(u.ProgRozpoznania, 0.1, 0.95);
        u.ProgZgody = Math.Clamp(u.ProgZgody, u.ProgRozpoznania, 0.99);
        u.Margines = Math.Clamp(u.Margines, 0, 0.5);
        u.ProgWykrywania = Math.Clamp(u.ProgWykrywania, 0.3, 0.99);
        u.SilaZamazania = Math.Clamp(u.SilaZamazania, 3, 16);
        u.MiesiaceWzorca = Math.Clamp(u.MiesiaceWzorca, 1, 120);
        if (u.Tryb != Ustawienia.TrybBezRozpoznawania) u.Tryb = Ustawienia.TrybRozpoznawania;
        bool zmianaTrybu = u.Tryb != Ustawienia.Tryb;
        if (zmianaTrybu && Zadanie is { Trwa: true })
            throw new BladUzytkownika("Poczekaj na koniec bieżącego zadania, zanim zmienisz tryb.");

        using var c = _baza.Otworz();
        Baza.Wykonaj(c, "INSERT INTO ustawienia(klucz, wartosc) VALUES('ustawienia', @w) ON CONFLICT(klucz) DO UPDATE SET wartosc=excluded.wartosc",
            ("@w", JsonSerializer.Serialize(u, Json)));
        _ustawienia = u;
        if (!zmianaTrybu) return;
        if (u.RozpoznawanieWlaczone) PrzygotujPamiec();
        else WylaczRozpoznawanie(c);
    }

    /// <summary>
    /// Przejście na tryb bez rozpoznawania: znikają wzorce twarzy, wycinki do rozpoznawania i wyniki automatycznego
    /// dopasowania. Zostają osoby, grupy, zgody i ręczne decyzje operatora.
    /// </summary>
    void WylaczRozpoznawanie(SqliteConnection c)
    {
        lock (_blokadaPamieci)
        {
            Baza.Transakcja(c, () =>
            {
                Baza.Wykonaj(c, "DELETE FROM wzorce");
                Baza.Wykonaj(c, "UPDATE twarze SET wyrownana=NULL, osoba_auto=NULL, wynik_auto=NULL, wynik_2wz=NULL, wynik_druga=NULL, kandydaci=NULL");
            });
            _cechyTwarzy.Clear();
            _cechyWzorcow.Clear();
            Baza.Odchudz(c);
        }
    }

    // ───────────── zadania w tle ─────────────

    void Uruchom(string rodzaj, Func<Zadanie, CancellationToken, Task> praca)
    {
        Zadanie z;
        CancellationToken token;
        lock (_blokadaZadania)
        {
            if (Zadanie is { Trwa: true })
                throw new BladUzytkownika("Trwa inne zadanie — poczekaj na jego koniec albo je przerwij.");
            _anuluj = new CancellationTokenSource();
            Zadanie = z = new Zadanie { Rodzaj = rodzaj };
            token = _anuluj.Token;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await praca(z, token);
                z.Komunikat ??= "Gotowe";
            }
            catch (OperationCanceledException) { z.Komunikat = "Przerwano"; }
            catch (Exception ex) { z.Komunikat = "Błąd: " + ex.Message; }
            finally
            {
                z.Biezacy = null;
                z.Koniec = DateTime.Now;
                z.Trwa = false;
            }
        });
    }

    public void Przerwij() => _anuluj?.Cancel();

    static string Normalizuj(string? katalog)
    {
        if (string.IsNullOrWhiteSpace(katalog)) throw new BladUzytkownika("Nie wybrano katalogu.");
        var k = Path.TrimEndingDirectorySeparator(Path.GetFullPath(katalog.Trim()));
        if (!Directory.Exists(k)) throw new BladUzytkownika($"Katalog nie istnieje: {k}");
        return k;
    }

    static (string Warunek, (string, object?)[] Parametry) WarunekKatalogu(string katalog, bool podkatalogi)
    {
        var k = Path.TrimEndingDirectorySeparator(Path.GetFullPath(katalog));
        var prefiks = k.EndsWith(Path.DirectorySeparatorChar) ? k : k + Path.DirectorySeparatorChar;
        return podkatalogi
            ? ("(z.katalog = @k OR substr(z.katalog, 1, length(@p)) = @p)", [("@k", k), ("@p", prefiks)])
            : ("z.katalog = @k", [("@k", k)]);
    }

    // ───────────── przetwarzanie zdjęć ─────────────

    public void Przetworz(PrzetworzZadanie p)
    {
        var katalog = Normalizuj(p.Katalog);
        var grupy = string.Join(",", p.Grupy ?? []);
        var u = Ustawienia;
        Uruchom("Przetwarzanie zdjęć", async (z, token) =>
        {
            var pliki = Directory.EnumerateFiles(katalog, "*", new EnumerationOptions { RecurseSubdirectories = p.Podkatalogi, IgnoreInaccessible = true })
                .Where(Obraz.JestZdjeciem).Order(StringComparer.OrdinalIgnoreCase).ToList();
            z.Wszystkie = pliki.Count;
            try
            {
                await Parallel.ForEachAsync(pliki, new ParallelOptions { MaxDegreeOfParallelism = Rownoleglosc, CancellationToken = token }, (plik, _) =>
                {
                    z.Biezacy = Path.GetFileName(plik);
                    try { PrzetworzPlik(plik, grupy, u); }
                    catch (Exception ex) { z.Bledy.Enqueue($"{Path.GetFileName(plik)}: {ex.Message}"); }
                    z.Krok();
                    return ValueTask.CompletedTask;
                });
            }
            finally
            {
                DopasujWszystko();
            }
            z.Komunikat = $"Przetworzono zdjęć: {z.Gotowe}" + (z.Bledy.IsEmpty ? "" : $", błędy: {z.Bledy.Count}");
        });
    }

    void PrzetworzPlik(string plik, string grupy, Ustawienia u)
    {
        var info = new FileInfo(plik);
        long zmod = info.LastWriteTimeUtc.Ticks;
        using var c = _baza.Otworz();
        var istniejace = Baza.Lista(c, "SELECT id, rozmiar, zmodyfikowano FROM zdjecia WHERE sciezka=@s",
            r => new[] { r.GetInt64(0), r.GetInt64(1), r.GetInt64(2) }, ("@s", plik)).FirstOrDefault();
        if (istniejace is not null && istniejace[1] == info.Length && istniejace[2] == zmod)
        {
            // bez zmian — tylko nowy wybór grup, które mają być na zdjęciu (i ewentualnie brakujące wycinki do rozpoznawania)
            Baza.Wykonaj(c, "UPDATE zdjecia SET grupy=@g WHERE id=@id", ("@g", grupy), ("@id", istniejace[0]));
            if (u.RozpoznawanieWlaczone) UzupelnijWycinki(c, istniejace[0], plik);
            return;
        }

        using var bmp = Obraz.Wczytaj(plik);
        var wykrycia = _detektor.Wykryj(bmp, (float)u.ProgWykrywania, u.MaleTwarze);
        using var piksele = bmp.PeekPixels();
        using var obraz = SKImage.FromPixels(piksele);
        var twarze = wykrycia.Select(w =>
        {
            (byte[]? Wycinek, float[]? Cechy) opis = u.RozpoznawanieWlaczone ? _rozpoznawanie.Opisz(obraz, w.Punkty) : (null, null);
            return (W: w, opis.Wycinek, opis.Cechy, Mini: Obraz.Wycinek(obraz, w.X, w.Y, w.W, w.H));
        }).ToList();
        byte[] miniatura;
        using (var m = Obraz.Pomniejsz(bmp, 360))
            miniatura = Obraz.Koduj(m, jakosc: 80);
        var data = Exif.DataZrobienia(plik) ?? info.LastWriteTime;

        long id = istniejace?[0] ?? 0;
        var noweCechy = new List<(long Id, float[] Cechy)>();
        Baza.Transakcja(c, () =>
        {
            (string, object?)[] pola =
            [
                ("@s", plik), ("@k", Path.GetDirectoryName(plik)), ("@w", bmp.Width), ("@h", bmp.Height), ("@r", info.Length),
                ("@m", zmod), ("@d", Baza.Iso(data)), ("@g", grupy), ("@p", Baza.Iso(DateTime.Now)), ("@id", id),
            ];
            if (id != 0)
            {
                ZapomnijTwarze(c, "zdjecie_id=@id", ("@id", id));
                Baza.Wykonaj(c, "DELETE FROM twarze WHERE zdjecie_id=@id", ("@id", id));
                Baza.Wykonaj(c, "UPDATE zdjecia SET szerokosc=@w, wysokosc=@h, rozmiar=@r, zmodyfikowano=@m, data_zdjecia=@d, grupy=@g, przetworzono=@p WHERE id=@id", pola);
            }
            else
            {
                Baza.Wykonaj(c, "INSERT INTO zdjecia(sciezka, katalog, szerokosc, wysokosc, rozmiar, zmodyfikowano, data_zdjecia, grupy, przetworzono) VALUES(@s, @k, @w, @h, @r, @m, @d, @g, @p)", pola);
                id = Baza.OstatnieId(c);
            }
            foreach (var t in twarze)
            {
                Baza.Wykonaj(c, "INSERT INTO twarze(zdjecie_id, x, y, w, h, pewnosc, punkty, wyrownana, miniatura) VALUES(@z, @x, @y, @w, @h, @p, @pk, @wy, @m)",
                    ("@z", id), ("@x", t.W.X), ("@y", t.W.Y), ("@w", t.W.W), ("@h", t.W.H), ("@p", t.W.Pewnosc),
                    ("@pk", string.Join(";", t.W.Punkty.Select(v => v.ToString("0.#", CultureInfo.InvariantCulture)))),
                    ("@wy", t.Wycinek), ("@m", t.Mini));
                if (t.Cechy is not null) noweCechy.Add((Baza.OstatnieId(c), t.Cechy));
            }
        });
        // do pamięci dopiero po zatwierdzeniu — wycofana transakcja nie zostawi wektora pod cudzym id
        foreach (var (nid, cechy) in noweCechy) _cechyTwarzy[nid] = cechy;
        File.WriteAllBytes(SciezkaMiniatury(id), miniatura);
        File.Delete(SciezkaPodgladu(id));
    }

    /// <summary>
    /// Twarze zdjęcia bez wyrównanego wycinka (przetworzone w trybie bez rozpoznawania albo w wersji 0.1)
    /// dostają go z zapisanych punktów twarzy — bez ponownego wykrywania, więc ręczne decyzje zostają.
    /// </summary>
    int UzupelnijWycinki(SqliteConnection c, long zdjecieId, string plik)
    {
        var brak = Baza.Lista(c, "SELECT id, punkty FROM twarze WHERE zdjecie_id=@z AND reczna=0 AND wyrownana IS NULL AND punkty IS NOT NULL",
            r => (Id: r.GetInt64(0), Punkty: r.GetString(1).Split(';').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray()),
            ("@z", zdjecieId));
        if (brak.Count == 0) return 0;
        using var bmp = Obraz.Wczytaj(plik);
        using var piksele = bmp.PeekPixels();
        using var obraz = SKImage.FromPixels(piksele);
        foreach (var t in brak)
        {
            var (wycinek, cechy) = _rozpoznawanie.Opisz(obraz, t.Punkty);
            Baza.Wykonaj(c, "UPDATE twarze SET wyrownana=@w WHERE id=@id", ("@w", wycinek), ("@id", t.Id));
            _cechyTwarzy[t.Id] = cechy;
        }
        return brak.Count;
    }

    /// <summary>Usuwa z pamięci wektory twarzy, które za chwilę znikną z bazy (SQLite może ponownie użyć ich id).</summary>
    void ZapomnijTwarze(SqliteConnection c, string warunek, params (string, object?)[] p)
    {
        foreach (var id in Baza.Lista(c, $"SELECT id FROM twarze WHERE {warunek}", r => r.GetInt64(0), p))
            _cechyTwarzy.TryRemove(id, out _);
    }

    void ZapomnijWzorce(SqliteConnection c, string warunek, params (string, object?)[] p)
    {
        foreach (var id in Baza.Lista(c, $"SELECT id FROM wzorce WHERE {warunek}", r => r.GetInt64(0), p))
            _cechyWzorcow.TryRemove(id, out _);
    }

    // ───────────── wektory cech w pamięci ─────────────

    /// <summary>
    /// Po starcie programu (albo po włączeniu rozpoznawania): odtwarza brakujące wycinki — baza z wersji 0.1 albo
    /// zdjęcia przetworzone w trybie bez rozpoznawania — i liczy w pamięci wektory wszystkich twarzy i wzorców.
    /// </summary>
    public void PrzygotujPamiec()
    {
        if (!Ustawienia.RozpoznawanieWlaczone) return;
        using (var c = _baza.Otworz())
            if (Convert.ToInt64(Baza.Skalar(c, "SELECT (SELECT COUNT(*) FROM twarze WHERE reczna=0) + (SELECT COUNT(*) FROM wzorce)")) == 0)
                return;
        Uruchom("Wczytywanie twarzy do pamięci", (z, token) =>
        {
            OdtworzWycinkiTwarzy(z, token);
            OdtworzWycinkiWzorcow(z, token);
            UzupelnijCechy(z, token);
            z.Etap("Dopasowanie do osób", 0);
            DopasujWszystko();
            z.Komunikat = $"W pamięci: {_cechyTwarzy.Count} twarzy i {_cechyWzorcow.Count} wzorców" + (z.Bledy.IsEmpty ? "" : $", uwagi: {z.Bledy.Count}");
            return Task.CompletedTask;
        });
    }

    void OdtworzWycinkiTwarzy(Zadanie z, CancellationToken token)
    {
        using var c = _baza.Otworz();
        var zdjecia = Baza.Lista(c, """
            SELECT DISTINCT z.id, z.sciezka, z.rozmiar, z.zmodyfikowano FROM zdjecia z JOIN twarze t ON t.zdjecie_id = z.id
            WHERE t.reczna = 0 AND t.wyrownana IS NULL AND t.punkty IS NOT NULL
            """, r => (Id: r.GetInt64(0), Sciezka: r.GetString(1), Rozmiar: r.GetInt64(2), Zmod: r.GetInt64(3)));
        if (zdjecia.Count == 0) return;
        z.Etap("Przygotowanie wycinków twarzy", zdjecia.Count);
        int pominiete = 0;
        Parallel.ForEach(zdjecia, new ParallelOptions { MaxDegreeOfParallelism = Rownoleglosc, CancellationToken = token }, zd =>
        {
            var info = new FileInfo(zd.Sciezka);
            if (info.Exists && info.Length == zd.Rozmiar && info.LastWriteTimeUtc.Ticks == zd.Zmod)
            {
                try
                {
                    using var cz = _baza.Otworz();
                    UzupelnijWycinki(cz, zd.Id, zd.Sciezka);
                }
                catch (Exception ex) { z.Bledy.Enqueue($"{Path.GetFileName(zd.Sciezka)}: {ex.Message}"); }
            }
            else Interlocked.Increment(ref pominiete);
            z.Krok();
        });
        if (pominiete > 0)
            z.Bledy.Enqueue($"Zdjęć brakujących albo zmienionych od przetworzenia: {pominiete} — ich twarze zostają zamazane, dopóki nie przetworzysz katalogu ponownie.");
    }

    /// <summary>Wzorce z wersji 0.1 (sam wektor, bez wycinka): wycinek z twarzy, z której powstały, albo ze zdjęcia źródłowego.</summary>
    void OdtworzWycinkiWzorcow(Zadanie z, CancellationToken token)
    {
        using var c = _baza.Otworz();
        var brak = Baza.Lista(c, """
            SELECT w.id, w.zrodlo, t.wyrownana, o.imie || ' ' || o.nazwisko
            FROM wzorce w JOIN osoby o ON o.id = w.osoba_id LEFT JOIN twarze t ON t.id = w.twarz_id
            WHERE w.wyrownana IS NULL
            """, r => (Id: r.GetInt64(0), Zrodlo: r.GetString(1), ZTwarzy: r.IsDBNull(2) ? null : (byte[])r[2], Osoba: r.GetString(3)));
        if (brak.Count == 0) return;
        z.Etap("Odtwarzanie wzorców z poprzedniej wersji", brak.Count);
        var u = Ustawienia;
        foreach (var w in brak)
        {
            token.ThrowIfCancellationRequested();
            byte[]? wycinek = w.ZTwarzy;
            try
            {
                if (wycinek is null && File.Exists(w.Zrodlo))
                {
                    using var bmp = Obraz.Wczytaj(w.Zrodlo);
                    var twarz = _detektor.Wykryj(bmp, (float)u.ProgWykrywania, false).MaxBy(d => d.Pole);
                    if (twarz is not null)
                    {
                        using var piksele = bmp.PeekPixels();
                        using var obraz = SKImage.FromPixels(piksele);
                        wycinek = _rozpoznawanie.Opisz(obraz, twarz.Punkty).Wycinek;
                    }
                }
            }
            catch (Exception) { wycinek = null; }

            if (wycinek is not null)
                Baza.Wykonaj(c, "UPDATE wzorce SET wyrownana=@w WHERE id=@id", ("@w", wycinek), ("@id", w.Id));
            else
            {
                Baza.Wykonaj(c, "DELETE FROM wzorce WHERE id=@id", ("@id", w.Id));
                z.Bledy.Enqueue($"{w.Osoba.Trim()}: usunięto wzorzec, którego nie da się odtworzyć (brak pliku {w.Zrodlo})");
            }
            z.Krok();
        }
        Baza.Odchudz(c);
    }

    /// <summary>Liczy w pamięci wektory twarzy i wzorców, których jeszcze tam nie ma (wycinki są w bazie).</summary>
    void UzupelnijCechy(Zadanie? z = null, CancellationToken token = default)
    {
        lock (_blokadaPamieci)
        {
            using var c = _baza.Otworz();
            var twarze = Baza.Lista(c, "SELECT id FROM twarze WHERE wyrownana IS NOT NULL", r => r.GetInt64(0)).Where(id => !_cechyTwarzy.ContainsKey(id)).ToList();
            var wzorce = Baza.Lista(c, "SELECT id FROM wzorce WHERE wyrownana IS NOT NULL", r => r.GetInt64(0)).Where(id => !_cechyWzorcow.ContainsKey(id)).ToList();
            if (twarze.Count + wzorce.Count == 0) return;
            z?.Etap("Liczenie cech twarzy w pamięci", twarze.Count + wzorce.Count);
            Licz(c, "twarze", twarze, _cechyTwarzy, z, token);
            Licz(c, "wzorce", wzorce, _cechyWzorcow, z, token);
        }
    }

    void Licz(SqliteConnection c, string tabela, List<long> idki, ConcurrentDictionary<long, float[]> cel, Zadanie? z, CancellationToken token)
    {
        foreach (var paczka in idki.Chunk(400))
        {
            token.ThrowIfCancellationRequested();
            var wycinki = Baza.Lista(c, $"SELECT id, wyrownana FROM {tabela} WHERE id IN ({string.Join(",", paczka)})",
                r => (Id: r.GetInt64(0), Wycinek: (byte[])r[1]));
            Parallel.ForEach(wycinki, new ParallelOptions { MaxDegreeOfParallelism = Rownoleglosc }, w =>
            {
                cel[w.Id] = _rozpoznawanie.CechyZWycinka(w.Wycinek);
                z?.Krok();
            });
        }
    }

    // ───────────── dopasowanie twarzy do osób ─────────────

    /// <summary>
    /// Dla każdej twarzy liczy podobieństwo do wzorców osób (z grup wybranych przy przetwarzaniu zdjęcia)
    /// i zapisuje 3 najlepsze osoby. Kolor twarzy wynika z tego dopiero przy odczycie (<see cref="Status"/>),
    /// więc zmiana zgody albo progów nie wymaga ponownego dopasowania.
    /// </summary>
    public void DopasujWszystko()
    {
        if (!Ustawienia.RozpoznawanieWlaczone) return;
        UzupelnijCechy();
        lock (_blokadaDopasowania)
        {
            using var c = _baza.Otworz();
            var wzorce = Baza.Lista(c, "SELECT id, osoba_id FROM wzorce", r => (Id: r.GetInt64(0), Osoba: r.GetInt64(1)))
                .Where(w => _cechyWzorcow.ContainsKey(w.Id))
                .GroupBy(w => w.Osoba).ToDictionary(g => g.Key, g => g.Select(x => _cechyWzorcow[x.Id]).ToArray());
            var grupyOsob = Baza.Lista(c, "SELECT osoba_id, grupa_id FROM osoby_grupy", r => (r.GetInt64(0), r.GetInt64(1)))
                .ToLookup(x => x.Item1, x => x.Item2);
            var twarze = Baza.Lista(c, "SELECT t.id, z.grupy FROM twarze t JOIN zdjecia z ON z.id = t.zdjecie_id WHERE t.reczna = 0",
                r => (Id: r.GetInt64(0), Grupy: r.GetString(1)));

            Baza.Transakcja(c, () =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE twarze SET osoba_auto=@o, wynik_auto=@w, wynik_2wz=@w2, wynik_druga=@d, kandydaci=@k WHERE id=@id";
                var po = cmd.Parameters.Add("@o", SqliteType.Integer);
                var pw = cmd.Parameters.Add("@w", SqliteType.Real);
                var pw2 = cmd.Parameters.Add("@w2", SqliteType.Real);
                var pd = cmd.Parameters.Add("@d", SqliteType.Real);
                var pk = cmd.Parameters.Add("@k", SqliteType.Text);
                var pid = cmd.Parameters.Add("@id", SqliteType.Integer);

                foreach (var t in twarze)
                {
                    pid.Value = t.Id;
                    // Bez wycinka (zdjęcie zniknęło albo zmieniło się) nie ma czego porównać — twarz zostaje nierozpoznana, czyli zamazana.
                    if (!_cechyTwarzy.TryGetValue(t.Id, out var cechyTwarzy))
                    {
                        po.Value = pw.Value = pw2.Value = pd.Value = pk.Value = DBNull.Value;
                        cmd.ExecuteNonQuery();
                        continue;
                    }
                    HashSet<long>? dozwolone = t.Grupy.Length == 0 ? null : t.Grupy.Split(',').Select(long.Parse).ToHashSet();
                    var oceny = new List<(long Osoba, float Najlepszy, float Drugi)>();
                    foreach (var (osoba, cechy) in wzorce)
                    {
                        if (dozwolone is not null && !grupyOsob[osoba].Any(dozwolone.Contains)) continue;
                        float b1 = -1, b2 = -1;
                        foreach (var w in cechy)
                        {
                            float s = Rozpoznawanie.Podobienstwo(cechyTwarzy, w);
                            if (s > b1) { b2 = b1; b1 = s; }
                            else if (s > b2) b2 = s;
                        }
                        oceny.Add((osoba, b1, b2));
                    }
                    oceny.Sort((a, b) => b.Najlepszy.CompareTo(a.Najlepszy));
                    po.Value = oceny.Count > 0 ? oceny[0].Osoba : DBNull.Value;
                    pw.Value = oceny.Count > 0 ? oceny[0].Najlepszy : DBNull.Value;
                    pw2.Value = oceny.Count > 0 && oceny[0].Drugi > -1 ? oceny[0].Drugi : DBNull.Value;
                    pd.Value = oceny.Count > 1 ? oceny[1].Najlepszy : DBNull.Value;
                    pk.Value = JsonSerializer.Serialize(oceny.Take(3).Select(o => new { o = o.Osoba, s = Math.Round(o.Najlepszy, 3) }));
                    cmd.ExecuteNonQuery();
                }
            });
        }
    }

    /// <summary>
    /// Kolor i decyzja dla twarzy. Zasada trybu „dozwolonych”: odsłonięta zostaje tylko osoba ze zgodą
    /// rozpoznana z dużą pewnością albo przypisana ręcznie — każda wątpliwość oznacza zamazanie.
    /// </summary>
    static StatusTwarzy Status(TwarzRek t, Dictionary<long, OsobaInfo> osoby, Ustawienia u)
    {
        if (t.NieTwarz) return new("szary", false, "Oznaczona: to nie twarz", null);
        if (t.Reczna) return new("czerwony", true, "Ramka dorysowana ręcznie — zamazywana", null);

        long? osobaId = t.OsobaReczna;
        bool auto = false;
        if (osobaId is null && t.OsobaAuto is long oa && osoby.ContainsKey(oa) && t.WynikAuto >= u.ProgRozpoznania)
        {
            osobaId = oa;
            auto = true;
        }
        OsobaInfo? o = osobaId is long id ? osoby.GetValueOrDefault(id) : null;
        string wynik = t.WynikAuto is double w ? w.ToString("0.00", CultureInfo.InvariantCulture) : "";

        if (t.Wymus) return new("czerwony", true, (o is null ? "" : o.Nazwa + " — ") + "zamazywana zawsze (decyzja ręczna)", osobaId);
        if (o is null) return new("zolty", true, u.RozpoznawanieWlaczone ? "Nierozpoznana" : "Nieprzypisana — zamazywana", null);
        if (!o.ZgodaWazna)
            return new("czerwony", true, $"{o.Nazwa} — brak ważnej zgody" + (auto ? $" ({wynik})" : ""), o.Id);
        if (!auto)
            return new("zielony", false, $"{o.Nazwa} — zgoda (przypisana ręcznie)", o.Id);

        // Rozpoznanie automatyczne osoby ze zgodą: pomyłka odsłoniłaby kogoś obcego, więc warunki są ostrzejsze.
        if (t.WynikAuto < u.ProgZgody)
            return new("zolty", true, $"Prawdopodobnie {o.Nazwa} ({wynik}) — za mała pewność, potwierdź", o.Id);
        if (t.WynikDruga is double d && t.WynikAuto - d < u.Margines)
            return new("zolty", true, $"Prawdopodobnie {o.Nazwa} ({wynik}), ale podobna też inna osoba — potwierdź", o.Id);
        if (u.DwaWzorce && o.Wzorce >= 2 && (t.Wynik2Wz ?? -1) < u.ProgRozpoznania)
            return new("zolty", true, $"Prawdopodobnie {o.Nazwa} ({wynik}) — pasuje tylko do jednego wzorca, potwierdź", o.Id);
        return new("zielony", false, $"{o.Nazwa} — zgoda ({wynik})", o.Id);
    }

    static Dictionary<long, OsobaInfo> WczytajOsoby(SqliteConnection c)
    {
        var osoby = Baza.Lista(c, """
            SELECT o.id, o.imie, o.nazwisko, o.zgoda, o.zgoda_do, o.uwagi,
                   (SELECT COUNT(*) FROM wzorce w WHERE w.osoba_id = o.id),
                   (SELECT MAX(data_zdjecia) FROM wzorce w WHERE w.osoba_id = o.id)
            FROM osoby o
            """, r => new OsobaInfo
        {
            Id = r.GetInt64(0), Imie = r.GetString(1), Nazwisko = r.GetString(2), Zgoda = r.GetInt64(3) != 0,
            ZgodaDo = Baza.Data(Baza.Tekst(r, 4)), Uwagi = r.GetString(5), Wzorce = r.GetInt32(6), NajnowszyWzorzec = Baza.Data(Baza.Tekst(r, 7)),
        }).ToDictionary(o => o.Id);
        foreach (var (osoba, grupa) in Baza.Lista(c, "SELECT osoba_id, grupa_id FROM osoby_grupy", r => (r.GetInt64(0), r.GetInt64(1))))
            if (osoby.TryGetValue(osoba, out var o)) o.Grupy.Add(grupa);
        return osoby;
    }

    static List<TwarzRek> WczytajTwarze(SqliteConnection c, string warunek, params (string, object?)[] p) =>
        Baza.Lista(c, $"""
            SELECT t.id, t.zdjecie_id, t.x, t.y, t.w, t.h, t.pewnosc, t.reczna, t.nie_twarz, t.wymus,
                   t.osoba_reczna, t.osoba_auto, t.wynik_auto, t.wynik_2wz, t.wynik_druga, t.kandydaci
            FROM twarze t JOIN zdjecia z ON z.id = t.zdjecie_id
            WHERE {warunek}
            """, r => new TwarzRek
        {
            Id = r.GetInt64(0), ZdjecieId = r.GetInt64(1), X = r.GetFloat(2), Y = r.GetFloat(3), W = r.GetFloat(4), H = r.GetFloat(5),
            Pewnosc = r.GetFloat(6), Reczna = r.GetInt64(7) != 0, NieTwarz = r.GetInt64(8) != 0, Wymus = r.GetInt64(9) != 0,
            OsobaReczna = Baza.Liczba(r, 10), OsobaAuto = Baza.Liczba(r, 11), WynikAuto = Baza.Ulamek(r, 12),
            Wynik2Wz = Baza.Ulamek(r, 13), WynikDruga = Baza.Ulamek(r, 14), Kandydaci = Baza.Tekst(r, 15),
        }, p);

    // ───────────── odczyt dla interfejsu ─────────────

    public object ListaZdjec(string? katalog, bool podkatalogi, long? osoba)
    {
        if (osoba is null && string.IsNullOrWhiteSpace(katalog)) return Array.Empty<object>();
        using var c = _baza.Otworz();
        var u = Ustawienia;
        var osoby = WczytajOsoby(c);
        var (warunek, parametry) = osoba is null ? WarunekKatalogu(katalog!, podkatalogi) : ("1=1", []);
        var zdjecia = Baza.Lista(c, $"SELECT z.id, z.sciezka, z.szerokosc, z.wysokosc, z.przetworzono FROM zdjecia z WHERE {warunek}",
            r => (Id: r.GetInt64(0), Sciezka: r.GetString(1), W: r.GetInt32(2), H: r.GetInt32(3), Wersja: r.GetString(4)), parametry);
        var statusy = WczytajTwarze(c, warunek, parametry)
            .GroupBy(t => t.ZdjecieId).ToDictionary(g => g.Key, g => g.Select(t => Status(t, osoby, u)).ToList());

        var wynik = new List<object>();
        foreach (var z in zdjecia.OrderBy(z => z.Sciezka, Polski))
        {
            var st = statusy.GetValueOrDefault(z.Id) ?? [];
            if (osoba is not null && !st.Any(s => s.OsobaId == osoba && s.Kolor != "zolty")) continue;
            wynik.Add(new
            {
                z.Id, Nazwa = Path.GetFileName(z.Sciezka), z.Sciezka, Szerokosc = z.W, Wysokosc = z.H, z.Wersja,
                Zielone = st.Count(s => s.Kolor == "zielony"), Czerwone = st.Count(s => s.Kolor == "czerwony"),
                Zolte = st.Count(s => s.Kolor == "zolty"),
            });
        }
        return wynik;
    }

    public object Zdjecie(long id)
    {
        using var c = _baza.Otworz();
        var u = Ustawienia;
        var osoby = WczytajOsoby(c);
        var z = Baza.Lista(c, "SELECT id, sciezka, szerokosc, wysokosc, przetworzono, data_zdjecia FROM zdjecia WHERE id=@id",
            r => new { Id = r.GetInt64(0), Sciezka = r.GetString(1), Szerokosc = r.GetInt32(2), Wysokosc = r.GetInt32(3), Wersja = r.GetString(4), Data = Baza.Tekst(r, 5) },
            ("@id", id)).FirstOrDefault() ?? throw new BladUzytkownika("Nie ma takiego zdjęcia.");
        var twarze = WczytajTwarze(c, "t.zdjecie_id = @id", ("@id", id)).OrderBy(t => t.X).Select(t =>
        {
            var s = Status(t, osoby, u);
            return new
            {
                t.Id, t.X, t.Y, t.W, t.H, t.Pewnosc, s.Kolor, s.Zamaz, s.Opis, s.OsobaId,
                OsobaNazwa = s.OsobaId is long o && osoby.TryGetValue(o, out var oi) ? oi.Nazwa : null,
                t.Reczna, t.NieTwarz, t.Wymus, Przypisana = t.OsobaReczna is not null, Wynik = t.WynikAuto,
                Kandydaci = Kandydaci(t.Kandydaci, osoby),
            };
        }).ToList();
        return new { z.Id, Nazwa = Path.GetFileName(z.Sciezka), z.Sciezka, z.Szerokosc, z.Wysokosc, z.Wersja, z.Data, Twarze = twarze };
    }

    static List<object> Kandydaci(string? json, Dictionary<long, OsobaInfo> osoby)
    {
        var wynik = new List<object>();
        if (string.IsNullOrEmpty(json)) return wynik;
        foreach (var k in JsonDocument.Parse(json).RootElement.EnumerateArray())
            if (osoby.TryGetValue(k.GetProperty("o").GetInt64(), out var o))
                wynik.Add(new { OsobaId = o.Id, o.Nazwa, Wynik = k.GetProperty("s").GetDouble(), Zgoda = o.ZgodaWazna });
        return wynik;
    }

    /// <summary>Nierozpoznane twarze z katalogu pogrupowane po podobieństwie — „to ta sama osoba”.</summary>
    public object Nierozpoznane(string? katalog, bool podkatalogi)
    {
        var u = Ustawienia;
        // grupowanie podobnych twarzy to też porównywanie biometryczne
        if (string.IsNullOrWhiteSpace(katalog) || !u.RozpoznawanieWlaczone) return Array.Empty<object>();
        UzupelnijCechy();
        using var c = _baza.Otworz();
        var osoby = WczytajOsoby(c);
        var (warunek, parametry) = WarunekKatalogu(katalog, podkatalogi);
        var zolte = WczytajTwarze(c, warunek, parametry)
            .Where(t => _cechyTwarzy.ContainsKey(t.Id) && Status(t, osoby, u).Kolor == "zolty").ToList();
        if (zolte.Count == 0) return Array.Empty<object>();
        var cechy = zolte.ToDictionary(t => t.Id, t => _cechyTwarzy[t.Id]);

        // Próg wyraźnie powyżej rozpoznania: małe, nieostre twarze są do siebie „podobne” bez względu na osobę.
        float prog = (float)Math.Max(u.ProgRozpoznania + 0.1, 0.5);
        var klastry = new List<(float[] Suma, List<TwarzRek> Twarze)>();
        foreach (var t in zolte.OrderByDescending(t => t.Pewnosc))
        {
            var v = cechy[t.Id];
            int najlepszy = -1;
            float max = prog;
            for (int i = 0; i < klastry.Count; i++)
            {
                var srodek = (float[])klastry[i].Suma.Clone();
                Rozpoznawanie.Normalizuj(srodek);
                float s = Rozpoznawanie.Podobienstwo(v, srodek);
                if (s >= max) { max = s; najlepszy = i; }
            }
            if (najlepszy < 0)
                klastry.Add(((float[])v.Clone(), [t]));
            else
            {
                var k = klastry[najlepszy];
                for (int j = 0; j < v.Length; j++) k.Suma[j] += v[j];
                k.Twarze.Add(t);
            }
        }

        return klastry.OrderByDescending(k => k.Twarze.Count).Select((k, nr) =>
        {
            var sugestia = k.Twarze.Where(t => t.OsobaAuto is not null && t.WynikAuto >= u.ProgRozpoznania)
                .GroupBy(t => t.OsobaAuto!.Value).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            return new
            {
                Nr = nr + 1,
                Twarze = k.Twarze.Select(t => new { t.Id, t.ZdjecieId }).ToList(),
                SugestiaId = sugestia,
                SugestiaNazwa = sugestia is long s && osoby.TryGetValue(s, out var o) ? o.Nazwa : null,
            };
        }).ToList();
    }

    public byte[]? MiniaturaZdjecia(long id)
    {
        var plik = SciezkaMiniatury(id);
        if (File.Exists(plik)) return File.ReadAllBytes(plik);
        var sciezka = SciezkaZdjecia(id);
        using var bmp = Obraz.Wczytaj(sciezka);
        using var m = Obraz.Pomniejsz(bmp, 360);
        var bajty = Obraz.Koduj(m, jakosc: 80);
        ZapiszBezpiecznie(plik, bajty);
        return bajty;
    }

    public byte[] Podglad(long id, bool zamazane)
    {
        var plik = SciezkaPodgladu(id);
        byte[] bajty;
        if (File.Exists(plik)) bajty = File.ReadAllBytes(plik);
        else
        {
            using var bmp = Obraz.Wczytaj(SciezkaZdjecia(id));
            using var m = Obraz.Pomniejsz(bmp, 1600);
            bajty = Obraz.Koduj(m, jakosc: 85);
            ZapiszBezpiecznie(plik, bajty);
        }
        if (!zamazane) return bajty;

        using var c = _baza.Otworz();
        var u = Ustawienia;
        var osoby = WczytajOsoby(c);
        var szerokosc = Convert.ToInt32(Baza.Skalar(c, "SELECT szerokosc FROM zdjecia WHERE id=@id", ("@id", id)));
        using var podglad = SKBitmap.Decode(bajty);
        float s = (float)podglad.Width / szerokosc;
        var obszary = WczytajTwarze(c, "t.zdjecie_id = @id", ("@id", id))
            .Where(t => Status(t, osoby, u).Zamaz)
            .Select(t => Zamazywanie.Obszar(t.X * s, t.Y * s, t.W * s, t.H * s)).ToList();
        using var wynik = Zamazywanie.Zastosuj(podglad, obszary, u.SilaZamazania);
        return Obraz.Koduj(wynik, jakosc: 85);
    }

    public byte[]? MiniaturaTwarzy(long id)
    {
        using var c = _baza.Otworz();
        return Baza.Skalar(c, "SELECT miniatura FROM twarze WHERE id=@id", ("@id", id)) as byte[];
    }

    string SciezkaZdjecia(long id)
    {
        using var c = _baza.Otworz();
        return Baza.Skalar(c, "SELECT sciezka FROM zdjecia WHERE id=@id", ("@id", id)) as string
            ?? throw new BladUzytkownika("Nie ma takiego zdjęcia.");
    }

    static void ZapiszBezpiecznie(string plik, byte[] bajty)
    {
        var tymczasowy = plik + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tymczasowy, bajty);
        try { File.Move(tymczasowy, plik, true); }
        catch (IOException) { File.Delete(tymczasowy); }
    }

    // ───────────── decyzje użytkownika ─────────────

    public void Przypisz(long[] twarze, long osobaId, bool wzorzec)
    {
        using var c = _baza.Otworz();
        if (Baza.Skalar(c, "SELECT 1 FROM osoby WHERE id=@o", ("@o", osobaId)) is null)
            throw new BladUzytkownika("Nie ma takiej osoby.");
        wzorzec &= Ustawienia.RozpoznawanieWlaczone;
        Baza.Transakcja(c, () =>
        {
            foreach (var t in twarze)
            {
                Baza.Wykonaj(c, "UPDATE twarze SET osoba_reczna=@o, nie_twarz=0 WHERE id=@t AND reczna=0", ("@o", osobaId), ("@t", t));
                ZapomnijWzorce(c, "twarz_id=@t", ("@t", t));
                Baza.Wykonaj(c, "DELETE FROM wzorce WHERE twarz_id=@t", ("@t", t));
                if (wzorzec)
                    Baza.Wykonaj(c, """
                        INSERT INTO wzorce(osoba_id, twarz_id, wyrownana, miniatura, zrodlo, data_zdjecia, dodano)
                        SELECT @o, t.id, t.wyrownana, t.miniatura, z.sciezka, COALESCE(z.data_zdjecia, @teraz), @teraz
                        FROM twarze t JOIN zdjecia z ON z.id = t.zdjecie_id
                        WHERE t.id = @t AND t.wyrownana IS NOT NULL AND t.miniatura IS NOT NULL
                        """, ("@o", osobaId), ("@t", t), ("@teraz", Baza.Iso(DateTime.Now)));
            }
        });
        DopasujWszystko();
    }

    public long NowaOsoba(long[] twarze, OsobaDto osoba, bool wzorzec)
    {
        var id = ZapiszOsobe(null, osoba, dopasuj: false);
        Przypisz(twarze, id, wzorzec);
        return id;
    }

    public void Oznacz(OznaczZadanie p)
    {
        using var c = _baza.Otworz();
        Baza.Transakcja(c, () =>
        {
            foreach (var t in p.Twarze)
            {
                if (p.Cofnij)
                {
                    Baza.Wykonaj(c, "UPDATE twarze SET osoba_reczna=NULL, nie_twarz=0, wymus=0 WHERE id=@t", ("@t", t));
                    ZapomnijWzorce(c, "twarz_id=@t", ("@t", t));
                    Baza.Wykonaj(c, "DELETE FROM wzorce WHERE twarz_id=@t", ("@t", t));
                }
                if (p.NieTwarz is bool nt)
                {
                    Baza.Wykonaj(c, "UPDATE twarze SET nie_twarz=@v, osoba_reczna=CASE WHEN @v=1 THEN NULL ELSE osoba_reczna END WHERE id=@t", ("@v", nt ? 1 : 0), ("@t", t));
                    if (nt)
                    {
                        ZapomnijWzorce(c, "twarz_id=@t", ("@t", t));
                        Baza.Wykonaj(c, "DELETE FROM wzorce WHERE twarz_id=@t", ("@t", t));
                    }
                }
                if (p.Wymus is bool w)
                    Baza.Wykonaj(c, "UPDATE twarze SET wymus=@v WHERE id=@t", ("@v", w ? 1 : 0), ("@t", t));
            }
        });
        DopasujWszystko();
    }

    public long DodajRamke(long zdjecieId, RamkaDto r)
    {
        using var c = _baza.Otworz();
        var wymiary = Baza.Lista(c, "SELECT szerokosc, wysokosc FROM zdjecia WHERE id=@id", x => (W: x.GetInt32(0), H: x.GetInt32(1)), ("@id", zdjecieId))
            .FirstOrDefault();
        if (wymiary.W == 0) throw new BladUzytkownika("Nie ma takiego zdjęcia.");
        float x0 = Math.Clamp(r.X, 0, wymiary.W), y0 = Math.Clamp(r.Y, 0, wymiary.H);
        float w = Math.Clamp(r.W, 1, wymiary.W - x0), h = Math.Clamp(r.H, 1, wymiary.H - y0);

        byte[]? mini = null;
        try
        {
            using var podglad = SKBitmap.Decode(Podglad(zdjecieId, false));
            float s = (float)podglad.Width / wymiary.W;
            using var piksele = podglad.PeekPixels();
            using var obraz = SKImage.FromPixels(piksele);
            mini = Obraz.Wycinek(obraz, x0 * s, y0 * s, w * s, h * s);
        }
        catch (Exception) { }

        Baza.Wykonaj(c, "INSERT INTO twarze(zdjecie_id, x, y, w, h, pewnosc, miniatura, reczna) VALUES(@z, @x, @y, @w, @h, 1, @m, 1)",
            ("@z", zdjecieId), ("@x", x0), ("@y", y0), ("@w", w), ("@h", h), ("@m", mini));
        return Baza.OstatnieId(c);
    }

    public void UsunRamke(long id)
    {
        using var c = _baza.Otworz();
        if (Baza.Wykonaj(c, "DELETE FROM twarze WHERE id=@id AND reczna=1", ("@id", id)) == 0)
            throw new BladUzytkownika("Usunąć można tylko ramkę dorysowaną ręcznie. Wykrytą twarz oznacz jako „to nie twarz”.");
    }

    // ───────────── osoby i grupy ─────────────

    public object Osoby()
    {
        using var c = _baza.Otworz();
        var u = Ustawienia;
        var osoby = WczytajOsoby(c);
        var zdjeciaOsoby = WczytajTwarze(c, "1=1")
            .Select(t => (t.ZdjecieId, S: Status(t, osoby, u)))
            .Where(x => x.S.OsobaId is not null && x.S.Kolor != "zolty")
            .GroupBy(x => x.S.OsobaId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ZdjecieId).Distinct().Count());
        var granica = DateTime.Today.AddMonths(-u.MiesiaceWzorca);
        return osoby.Values.OrderBy(o => o.Nazwisko, Polski).ThenBy(o => o.Imie, Polski).Select(o => new
        {
            o.Id, o.Imie, o.Nazwisko, o.Nazwa, o.Zgoda, ZgodaDo = o.ZgodaDo?.ToString("yyyy-MM-dd"), o.ZgodaWazna, o.Uwagi, o.Grupy,
            o.Wzorce, NajnowszyWzorzec = o.NajnowszyWzorzec?.ToString("yyyy-MM-dd"),
            StaryWzorzec = o.Wzorce > 0 && o.NajnowszyWzorzec < granica,
            Zdjecia = zdjeciaOsoby.GetValueOrDefault(o.Id),
        }).ToList();
    }

    public long ZapiszOsobe(long? id, OsobaDto d, bool dopasuj = true)
    {
        if (string.IsNullOrWhiteSpace(d.Imie)) throw new BladUzytkownika("Podaj imię.");
        DateTime? zgodaDo = null;
        if (!string.IsNullOrWhiteSpace(d.ZgodaDo))
            zgodaDo = Baza.Data(d.ZgodaDo) ?? throw new BladUzytkownika("Niepoprawna data ważności zgody.");
        using var c = _baza.Otworz();
        long wynik = id ?? 0;
        Baza.Transakcja(c, () =>
        {
            (string, object?)[] pola =
            [
                ("@i", d.Imie.Trim()), ("@n", (d.Nazwisko ?? "").Trim()), ("@z", d.Zgoda ? 1 : 0),
                ("@zd", zgodaDo?.ToString("yyyy-MM-dd")), ("@u", (d.Uwagi ?? "").Trim()), ("@t", Baza.Iso(DateTime.Now)), ("@id", wynik),
            ];
            if (id is null)
            {
                Baza.Wykonaj(c, "INSERT INTO osoby(imie, nazwisko, zgoda, zgoda_do, uwagi, utworzono) VALUES(@i, @n, @z, @zd, @u, @t)", pola);
                wynik = Baza.OstatnieId(c);
            }
            else if (Baza.Wykonaj(c, "UPDATE osoby SET imie=@i, nazwisko=@n, zgoda=@z, zgoda_do=@zd, uwagi=@u WHERE id=@id", pola) == 0)
                throw new BladUzytkownika("Nie ma takiej osoby.");

            if (d.Grupy is not null)
            {
                Baza.Wykonaj(c, "DELETE FROM osoby_grupy WHERE osoba_id=@o", ("@o", wynik));
                foreach (var g in d.Grupy.Distinct())
                    Baza.Wykonaj(c, "INSERT OR IGNORE INTO osoby_grupy(osoba_id, grupa_id) SELECT @o, id FROM grupy WHERE id=@g", ("@o", wynik), ("@g", g));
            }
        });
        if (dopasuj) DopasujWszystko();
        return wynik;
    }

    public void UsunOsobe(long id)
    {
        using var c = _baza.Otworz();
        ZapomnijWzorce(c, "osoba_id=@id", ("@id", id));
        Baza.Wykonaj(c, "DELETE FROM osoby WHERE id=@id", ("@id", id));
        DopasujWszystko();
    }

    public object Wzorce(long osobaId)
    {
        using var c = _baza.Otworz();
        return Baza.Lista(c, "SELECT id, zrodlo, data_zdjecia, twarz_id FROM wzorce WHERE osoba_id=@o ORDER BY data_zdjecia DESC",
            r => new { Id = r.GetInt64(0), Zrodlo = r.GetString(1), Data = r.GetString(2)[..10], ZTwarzy = !r.IsDBNull(3) }, ("@o", osobaId));
    }

    public byte[]? MiniaturaWzorca(long id)
    {
        using var c = _baza.Otworz();
        return Baza.Skalar(c, "SELECT miniatura FROM wzorce WHERE id=@id", ("@id", id)) as byte[];
    }

    public void UsunWzorzec(long id)
    {
        using var c = _baza.Otworz();
        ZapomnijWzorce(c, "id=@id", ("@id", id));
        Baza.Wykonaj(c, "DELETE FROM wzorce WHERE id=@id", ("@id", id));
        DopasujWszystko();
    }

    public object Grupy()
    {
        using var c = _baza.Otworz();
        return Baza.Lista(c, "SELECT g.id, g.nazwa, g.archiwalna, (SELECT COUNT(*) FROM osoby_grupy og WHERE og.grupa_id = g.id) FROM grupy g",
            r => new { Id = r.GetInt64(0), Nazwa = r.GetString(1), Archiwalna = r.GetInt64(2) != 0, Liczba = r.GetInt32(3) })
            .OrderBy(g => g.Archiwalna).ThenBy(g => g.Nazwa, Polski).ToList();
    }

    public long ZapiszGrupe(long? id, GrupaDto g)
    {
        if (string.IsNullOrWhiteSpace(g.Nazwa)) throw new BladUzytkownika("Podaj nazwę grupy.");
        using var c = _baza.Otworz();
        try
        {
            if (id is null)
            {
                Baza.Wykonaj(c, "INSERT INTO grupy(nazwa, archiwalna) VALUES(@n, @a)", ("@n", g.Nazwa.Trim()), ("@a", g.Archiwalna ? 1 : 0));
                return Baza.OstatnieId(c);
            }
            Baza.Wykonaj(c, "UPDATE grupy SET nazwa=@n, archiwalna=@a WHERE id=@id", ("@n", g.Nazwa.Trim()), ("@a", g.Archiwalna ? 1 : 0), ("@id", id));
            return id.Value;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new BladUzytkownika($"Grupa „{g.Nazwa.Trim()}” już istnieje.");
        }
    }

    public void UsunGrupe(long id)
    {
        using var c = _baza.Otworz();
        Baza.Wykonaj(c, "DELETE FROM grupy WHERE id=@id", ("@id", id));
        DopasujWszystko();
    }

    /// <summary>Skrót: zgoda zapisywana każdej osobie z grupy osobno — później zmienia się ją per osoba.</summary>
    public int ZgodaGrupy(long id, ZgodaDto z)
    {
        DateTime? zgodaDo = null;
        if (!string.IsNullOrWhiteSpace(z.ZgodaDo))
            zgodaDo = Baza.Data(z.ZgodaDo) ?? throw new BladUzytkownika("Niepoprawna data ważności zgody.");
        using var c = _baza.Otworz();
        return Baza.Wykonaj(c, "UPDATE osoby SET zgoda=@z, zgoda_do=@zd WHERE id IN (SELECT osoba_id FROM osoby_grupy WHERE grupa_id=@g)",
            ("@z", z.Zgoda ? 1 : 0), ("@zd", zgodaDo?.ToString("yyyy-MM-dd")), ("@g", id));
    }

    // ───────────── import ─────────────

    /// <summary>
    /// Lista osób z Excela/CSV: imię; nazwisko; grupy; zgoda; zgoda ważna do.
    /// Kodowanie: UTF-8 (z BOM lub bez), UTF-16 albo Windows-1250 — tak zapisuje CSV polski Excel.
    /// </summary>
    public object ImportCsv(byte[] bajty)
    {
        var linie = Dekoduj(bajty).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (linie.Count == 0) throw new BladUzytkownika("Plik jest pusty.");
        char sep = linie[0].Contains(';') ? ';' : linie[0].Contains('\t') ? '\t' : ',';
        int start = linie[0].Contains("imi", StringComparison.OrdinalIgnoreCase) || linie[0].Contains("nazw", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        char[] sepGrup = new[] { '|', '/', ',', ';' }.Where(z => z != sep).ToArray();

        int dodano = 0, zmieniono = 0, noweGrupy = 0;
        var bledy = new List<string>();
        using var c = _baza.Otworz();
        var grupy = Baza.Lista(c, "SELECT id, nazwa FROM grupy", r => (Id: r.GetInt64(0), Nazwa: r.GetString(1)))
            .ToDictionary(g => g.Nazwa, g => g.Id, Polski);
        var osoby = Baza.Lista(c, "SELECT id, imie, nazwisko FROM osoby", r => (Id: r.GetInt64(0), Klucz: r.GetString(1) + "\u0001" + r.GetString(2)))
            .GroupBy(o => o.Klucz, Polski).ToDictionary(g => g.Key, g => g.First().Id, Polski);

        Baza.Transakcja(c, () =>
        {
            for (int i = start; i < linie.Count; i++)
            {
                var pola = PodzielCsv(linie[i], sep);
                string Pole(int n) => n < pola.Count ? pola[n].Trim() : "";
                if (Pole(0).Length == 0) { bledy.Add($"wiersz {i + 1}: brak imienia"); continue; }

                bool zgoda = Pole(3).ToLowerInvariant() is "tak" or "t" or "1" or "x" or "yes" or "true" or "✓" or "+";
                string? zgodaDo = null;
                if (Pole(4).Length > 0)
                {
                    if (DateTime.TryParseExact(Pole(4), ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd-MM-yyyy", "dd/MM/yyyy"],
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd))
                        zgodaDo = dd.ToString("yyyy-MM-dd");
                    else bledy.Add($"wiersz {i + 1}: nieczytelna data „{Pole(4)}” — pominięta");
                }

                var klucz = Pole(0) + "\u0001" + Pole(1);
                if (osoby.TryGetValue(klucz, out var id))
                {
                    Baza.Wykonaj(c, "UPDATE osoby SET zgoda=@z, zgoda_do=@zd WHERE id=@id", ("@z", zgoda ? 1 : 0), ("@zd", zgodaDo), ("@id", id));
                    zmieniono++;
                }
                else
                {
                    Baza.Wykonaj(c, "INSERT INTO osoby(imie, nazwisko, zgoda, zgoda_do, utworzono) VALUES(@i, @n, @z, @zd, @t)",
                        ("@i", Pole(0)), ("@n", Pole(1)), ("@z", zgoda ? 1 : 0), ("@zd", zgodaDo), ("@t", Baza.Iso(DateTime.Now)));
                    osoby[klucz] = id = Baza.OstatnieId(c);
                    dodano++;
                }

                foreach (var nazwa in Pole(2).Split(sepGrup, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!grupy.TryGetValue(nazwa, out var g))
                    {
                        Baza.Wykonaj(c, "INSERT INTO grupy(nazwa) VALUES(@n)", ("@n", nazwa));
                        grupy[nazwa] = g = Baza.OstatnieId(c);
                        noweGrupy++;
                    }
                    Baza.Wykonaj(c, "INSERT OR IGNORE INTO osoby_grupy(osoba_id, grupa_id) VALUES(@o, @g)", ("@o", id), ("@g", g));
                }
            }
        });
        DopasujWszystko();
        return new { dodano, zmieniono, noweGrupy, bledy };
    }

    static string Dekoduj(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1250).GetString(b); }
    }

    static List<string> PodzielCsv(string linia, char sep)
    {
        var pola = new List<string>();
        var sb = new StringBuilder();
        bool cudzyslow = false;
        for (int i = 0; i < linia.Length; i++)
        {
            char z = linia[i];
            if (cudzyslow)
            {
                if (z == '"' && i + 1 < linia.Length && linia[i + 1] == '"') { sb.Append('"'); i++; }
                else if (z == '"') cudzyslow = false;
                else sb.Append(z);
            }
            else if (z == '"') cudzyslow = true;
            else if (z == sep) { pola.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(z);
        }
        pola.Add(sb.ToString());
        return pola;
    }

    /// <summary>Katalog wzorców: &lt;katalog&gt;\&lt;Imię Nazwisko&gt;\zdjęcia — z każdego zdjęcia brana jest największa twarz.</summary>
    public void ImportujWzorce(ImportWzorcowZadanie p)
    {
        var katalog = Normalizuj(p.Katalog);
        var u = Ustawienia;
        if (!u.RozpoznawanieWlaczone)
            throw new BladUzytkownika("Wzorce twarzy są potrzebne tylko w trybie z rozpoznawaniem (Ustawienia).");
        Uruchom("Import wzorców", (z, token) =>
        {
            var pliki = Directory.GetDirectories(katalog).Order(Polski)
                .SelectMany(k => Directory.EnumerateFiles(k).Where(Obraz.JestZdjeciem).Order(Polski).Select(f => (Nazwa: Path.GetFileName(k), Plik: f)))
                .ToList();
            if (pliki.Count == 0)
                throw new BladUzytkownika("W podkatalogach nie ma zdjęć. Oczekiwany układ: <katalog>\\<Imię Nazwisko>\\zdjęcia.");
            z.Wszystkie = pliki.Count;
            using var c = _baza.Otworz();
            var idOsob = new Dictionary<string, long>(Polski);
            foreach (var (nazwa, plik) in pliki)
            {
                token.ThrowIfCancellationRequested();
                z.Biezacy = $"{nazwa}: {Path.GetFileName(plik)}";
                try
                {
                    if (!idOsob.TryGetValue(nazwa, out var osoba))
                        idOsob[nazwa] = osoba = ZnajdzLubUtworzOsobe(c, nazwa, p.Zgoda, p.GrupaId);
                    if (Baza.Skalar(c, "SELECT 1 FROM wzorce WHERE osoba_id=@o AND zrodlo=@z", ("@o", osoba), ("@z", plik)) is null)
                        DodajWzorzecZPliku(c, osoba, plik, nazwa, u, z);
                }
                catch (Exception ex) { z.Bledy.Enqueue($"{nazwa}\\{Path.GetFileName(plik)}: {ex.Message}"); }
                z.Krok();
            }
            DopasujWszystko();
            z.Komunikat = $"Wzorce dla osób: {idOsob.Count}" + (z.Bledy.IsEmpty ? "" : $", uwagi: {z.Bledy.Count}");
            return Task.CompletedTask;
        });
    }

    static long ZnajdzLubUtworzOsobe(SqliteConnection c, string nazwa, bool zgoda, long? grupaId)
    {
        var czesci = nazwa.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string imie = czesci.Length > 1 ? string.Join(' ', czesci[..^1]) : nazwa.Trim();
        string nazwisko = czesci.Length > 1 ? czesci[^1] : "";
        var id = Baza.Skalar(c, "SELECT id FROM osoby WHERE lower(imie)=lower(@i) AND lower(nazwisko)=lower(@n)", ("@i", imie), ("@n", nazwisko)) as long?;
        if (id is null)
        {
            Baza.Wykonaj(c, "INSERT INTO osoby(imie, nazwisko, zgoda, utworzono) VALUES(@i, @n, @z, @t)",
                ("@i", imie), ("@n", nazwisko), ("@z", zgoda ? 1 : 0), ("@t", Baza.Iso(DateTime.Now)));
            id = Baza.OstatnieId(c);
        }
        if (grupaId is not null)
            Baza.Wykonaj(c, "INSERT OR IGNORE INTO osoby_grupy(osoba_id, grupa_id) SELECT @o, id FROM grupy WHERE id=@g", ("@o", id), ("@g", grupaId));
        return id.Value;
    }

    void DodajWzorzecZPliku(SqliteConnection c, long osoba, string plik, string nazwa, Ustawienia u, Zadanie z)
    {
        using var bmp = Obraz.Wczytaj(plik);
        var twarze = _detektor.Wykryj(bmp, (float)u.ProgWykrywania, false).OrderByDescending(d => d.Pole).ToList();
        if (twarze.Count == 0)
        {
            z.Bledy.Enqueue($"{nazwa}\\{Path.GetFileName(plik)}: nie znaleziono twarzy");
            return;
        }
        var w = twarze[0];
        if (twarze.Count > 1 && twarze[1].Pole > 0.5f * w.Pole)
            z.Bledy.Enqueue($"{nazwa}\\{Path.GetFileName(plik)}: kilka podobnej wielkości twarzy — wzięto największą, sprawdź wzorce osoby");
        using var piksele = bmp.PeekPixels();
        using var obraz = SKImage.FromPixels(piksele);
        var (wycinek, cechy) = _rozpoznawanie.Opisz(obraz, w.Punkty);
        var data = Exif.DataZrobienia(plik) ?? File.GetLastWriteTime(plik);
        Baza.Wykonaj(c, "INSERT INTO wzorce(osoba_id, wyrownana, miniatura, zrodlo, data_zdjecia, dodano) VALUES(@o, @w, @m, @z, @d, @t)",
            ("@o", osoba), ("@w", wycinek), ("@m", Obraz.Wycinek(obraz, w.X, w.Y, w.W, w.H)),
            ("@z", plik), ("@d", Baza.Iso(data)), ("@t", Baza.Iso(DateTime.Now)));
        _cechyWzorcow[Baza.OstatnieId(c)] = cechy;
    }

    // ───────────── zapis zamazanych kopii ─────────────

    public void Eksportuj(EksportZadanie p)
    {
        var katalog = Normalizuj(p.Katalog);
        if (string.IsNullOrWhiteSpace(p.Cel)) throw new BladUzytkownika("Podaj katalog docelowy.");
        var cel = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Cel.Trim()));
        if (string.Equals(cel, katalog, StringComparison.OrdinalIgnoreCase))
            throw new BladUzytkownika("Katalog docelowy musi być inny niż katalog ze zdjęciami — oryginały nigdy nie są nadpisywane.");
        var u = Ustawienia;

        Uruchom("Zapis zamazanych kopii", async (z, token) =>
        {
            List<(long Id, string Sciezka, long Rozmiar, long Zmod)> zdjecia;
            Dictionary<long, OsobaInfo> osoby;
            ILookup<long, TwarzRek> twarze;
            using (var c = _baza.Otworz())
            {
                var (warunek, parametry) = WarunekKatalogu(katalog, p.Podkatalogi);
                zdjecia = Baza.Lista(c, $"SELECT z.id, z.sciezka, z.rozmiar, z.zmodyfikowano FROM zdjecia z WHERE {warunek}",
                    r => (r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)), parametry);
                osoby = WczytajOsoby(c);
                twarze = WczytajTwarze(c, warunek, parametry).ToLookup(t => t.ZdjecieId);
            }
            if (zdjecia.Count == 0) throw new BladUzytkownika("W tym katalogu nie ma przetworzonych zdjęć.");
            z.Wszystkie = zdjecia.Count;
            Directory.CreateDirectory(cel);
            var raport = new ConcurrentBag<string>();

            await Parallel.ForEachAsync(zdjecia, new ParallelOptions { MaxDegreeOfParallelism = Rownoleglosc, CancellationToken = token }, (zd, _) =>
            {
                z.Biezacy = Path.GetFileName(zd.Sciezka);
                try
                {
                    var info = new FileInfo(zd.Sciezka);
                    if (!info.Exists) throw new BladUzytkownika("plik już nie istnieje");
                    if (info.Length != zd.Rozmiar || info.LastWriteTimeUtc.Ticks != zd.Zmod)
                        throw new BladUzytkownika("plik zmienił się po przetworzeniu — przetwórz katalog ponownie");

                    var statusy = twarze[zd.Id].Select(t => (T: t, S: Status(t, osoby, u))).ToList();
                    var wzgledna = Path.GetRelativePath(katalog, zd.Sciezka);
                    bool png = Path.GetExtension(zd.Sciezka).Equals(".png", StringComparison.OrdinalIgnoreCase);
                    var docelowy = Path.GetFullPath(Path.Combine(cel, Path.ChangeExtension(wzgledna, png ? ".png" : ".jpg")));
                    if (string.Equals(docelowy, zd.Sciezka, StringComparison.OrdinalIgnoreCase))
                        throw new BladUzytkownika("plik docelowy byłby oryginałem — pominięto");
                    Directory.CreateDirectory(Path.GetDirectoryName(docelowy)!);

                    using var bmp = Obraz.Wczytaj(zd.Sciezka);
                    using var wynik = Zamazywanie.Zastosuj(bmp,
                        statusy.Where(x => x.S.Zamaz).Select(x => Zamazywanie.Obszar(x.T.X, x.T.Y, x.T.W, x.T.H)), u.SilaZamazania);
                    // zapis bez EXIF: nie przecieknie ani GPS, ani miniatura z niezamazanym oryginałem
                    File.WriteAllBytes(docelowy, Obraz.Koduj(wynik, png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 92));
                    raport.Add(string.Join(";", wzgledna,
                        statusy.Count(x => x.S.Kolor == "zielony"), statusy.Count(x => x.S.Kolor == "czerwony" && !x.T.Reczna),
                        statusy.Count(x => x.S.Kolor == "zolty"), statusy.Count(x => x.T.Reczna)));
                }
                catch (Exception ex) { z.Bledy.Enqueue($"{Path.GetFileName(zd.Sciezka)}: {ex.Message}"); }
                z.Krok();
                return ValueTask.CompletedTask;
            });

            File.WriteAllText(Path.Combine(cel, "raport.csv"),
                "plik;zostawione (zgoda);zamazane bez zgody;zamazane nierozpoznane;dorysowane ręcznie\r\n" + string.Join("\r\n", raport.Order(Polski)) + "\r\n",
                new UTF8Encoding(true));
            z.Komunikat = $"Zapisano {raport.Count} z {zdjecia.Count} zdjęć do {cel}" + (z.Bledy.IsEmpty ? "" : $", błędy: {z.Bledy.Count}");
        });
    }

    // ───────────── sprzątanie ─────────────

    /// <summary>
    /// Po zakończeniu pracy z katalogiem: usuwa z programu wszystko o jego zdjęciach — wykryte twarze z wycinkami,
    /// decyzje, miniatury i podglądy. Zdjęcia na dysku, zapisane kopie, osoby i ich wzorce zostają.
    /// </summary>
    public object UsunDaneKatalogu(KatalogDto p)
    {
        if (string.IsNullOrWhiteSpace(p.Katalog)) throw new BladUzytkownika("Nie wybrano katalogu.");
        if (Zadanie is { Trwa: true }) throw new BladUzytkownika("Poczekaj na koniec bieżącego zadania.");
        var (warunek, parametry) = WarunekKatalogu(p.Katalog, p.Podkatalogi);
        using var c = _baza.Otworz();
        var zdjecia = Baza.Lista(c, $"SELECT z.id FROM zdjecia z WHERE {warunek}", r => r.GetInt64(0), parametry);
        if (zdjecia.Count == 0) throw new BladUzytkownika("Program nie ma żadnych danych o zdjęciach z tego katalogu.");
        long twarze = 0;
        var wZdjeciach = $"zdjecie_id IN (SELECT z.id FROM zdjecia z WHERE {warunek})";
        lock (_blokadaPamieci)
        {
            Baza.Transakcja(c, () =>
            {
                twarze = Convert.ToInt64(Baza.Skalar(c, $"SELECT COUNT(*) FROM twarze WHERE {wZdjeciach}", parametry));
                ZapomnijTwarze(c, wZdjeciach, parametry);
                Baza.Wykonaj(c, $"DELETE FROM zdjecia WHERE id IN (SELECT z.id FROM zdjecia z WHERE {warunek})", parametry);
            });
            Baza.Odchudz(c);
        }
        foreach (var id in zdjecia)
        {
            File.Delete(SciezkaMiniatury(id));
            File.Delete(SciezkaPodgladu(id));
        }
        return new { Zdjecia = zdjecia.Count, Twarze = twarze };
    }

    // ───────────── przeglądanie katalogów ─────────────

    public static object Katalogi(string? sciezka)
    {
        if (string.IsNullOrWhiteSpace(sciezka))
        {
            var skroty = new[]
            {
                ("Obrazy", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                ("Pulpit", Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
                ("Dokumenty", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            }.Where(x => Directory.Exists(x.Item2)).Select(x => new { Nazwa = x.Item1, Sciezka = x.Item2 });
            var dyski = DriveInfo.GetDrives().Where(d => d.IsReady)
                .Select(d => new { Nazwa = $"{d.Name.TrimEnd('\\')} {SafeLabel(d)}".Trim(), Sciezka = d.Name });
            return new { Sciezka = "", Rodzic = (string?)null, Katalogi = skroty.Concat(dyski).ToList(), LiczbaZdjec = 0 };
        }

        var di = new DirectoryInfo(Path.GetFullPath(sciezka));
        if (!di.Exists) throw new BladUzytkownika($"Katalog nie istnieje: {di.FullName}");
        var opcje = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        return new
        {
            Sciezka = di.FullName,
            Rodzic = di.Parent?.FullName ?? "",
            Katalogi = di.EnumerateDirectories("*", opcje).OrderBy(d => d.Name, Polski).Select(d => new { Nazwa = d.Name, Sciezka = d.FullName }).ToList(),
            LiczbaZdjec = di.EnumerateFiles("*", opcje).Count(f => Obraz.JestZdjeciem(f.Name)),
        };
    }

    static string SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel; }
        catch (Exception) { return ""; }
    }

    public void Dispose()
    {
        _detektor.Dispose();
        _rozpoznawanie.Dispose();
        _opcje.Dispose();
    }
}
