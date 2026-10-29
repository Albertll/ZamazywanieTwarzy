// Porównanie modeli na zdjęciach testowych (testdane, pobiera je testdane\pobierz.ps1).
//   dotnet run --project proba\porownanie -c Release
// Wynik: proba\porownanie\WYNIKI.md (same liczby) i arkusze wycinków proba\porownanie\wyniki\*.png (poza repozytorium).
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using SkiaSharp;
using ZamazywanieTwarzy;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string repo = AppContext.BaseDirectory;
while (!Directory.Exists(Path.Combine(repo, "testdane"))) repo = Path.GetDirectoryName(repo) ?? throw new Exception("Nie znaleziono katalogu testdane");
string modeleProgramu = Path.Combine(repo, "src", "ZamazywanieTwarzy", "modele");
string modeleObce = Path.Combine(repo, "proba", "modele-porownanie");
string katalogWynikow = Path.Combine(repo, "proba", "porownanie", "wyniki");
Directory.CreateDirectory(katalogWynikow);

var opcje = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
var yunet = new Detektor(Path.Combine(modeleProgramu, "face_detection_yunet_2023mar.onnx"), opcje);
var rozpoznawanie = new List<IRozpoznawanie> { new SFace(new Rozpoznawanie(Path.Combine(modeleProgramu, "face_recognition_sface_2021dec.onnx"), opcje)) };
string arcface = Path.Combine(modeleObce, "buffalo_l", "w600k_r50.onnx");
if (File.Exists(arcface))
{
    // normalizacja jak w insightface (model bez warstw Sub/Mul na wejściu); wariant bez normalizacji dawał d′ = 0,19
    rozpoznawanie.Add(new ArcFace("ArcFace R50 (InsightFace)", arcface, 127.5f, 127.5f, opcje));
}
string auraface = Path.Combine(modeleObce, "auraface_glintr100.onnx");
if (File.Exists(auraface))
    rozpoznawanie.Add(new ArcFace("AuraFace", auraface, 127.5f, 127.5f, opcje));
string scrfdPlik = Path.Combine(modeleObce, "buffalo_l", "det_10g.onnx");
var scrfd = File.Exists(scrfdPlik) ? new Scrfd(scrfdPlik, opcje) : null;

// Kto jest na zdjęciach (testdane\ZRODLA.md) i gdzie: O = Obama, B = Biden, C = Clinton; pozycja = x i szerokość ramki YuNet.
var naZdjeciu = new Dictionary<string, string>
{
    ["01"] = "OBC", ["02"] = "OBC", ["03"] = "OBC", ["04"] = "OBC", ["05"] = "OC", ["06"] = "OC", ["07"] = "OC", ["08"] = "OC",
    ["09"] = "OB", ["10"] = "BC", ["11"] = "B", ["12"] = "C", ["13"] = "BC", ["14"] = "OB", ["15"] = "", ["16"] = "", ["17"] = "",
};
var oznaczone = new (string Zdj, float X, float W, char Kto)[]
{
    ("01", 250, 219, 'B'), ("01", 1196, 169, 'O'), ("01", 2863, 246, 'C'), ("02", 1831, 115, 'O'), ("02", 2051, 98, 'B'),
    ("02", 2465, 92, 'C'), ("03", 895, 143, 'B'), ("03", 1602, 134, 'O'), ("04", 1691, 65, 'C'), ("04", 1945, 64, 'O'),
    ("05", 1864, 224, 'O'), ("06", 2216, 101, 'O'), ("06", 3183, 93, 'C'), ("07", 265, 162, 'C'), ("07", 793, 146, 'O'),
    ("08", 2011, 268, 'C'), ("08", 2798, 305, 'O'), ("09", 807, 413, 'B'), ("09", 2040, 505, 'O'), ("10", 567, 93, 'C'),
    ("10", 1051, 91, 'B'), ("11", 912, 225, 'B'), ("12", 924, 425, 'C'), ("13", 1633, 73, 'B'), ("13", 2605, 171, 'C'),
    ("14", 1380, 38, 'B'), ("14", 1623, 44, 'O'),
};
var skrot = new Dictionary<string, char> { ["Barack Obama"] = 'O', ["Joe Biden"] = 'B', ["Hillary Clinton"] = 'C' };

var czas = rozpoznawanie.ToDictionary(r => r.Nazwa, _ => new Stopwatch());
float[][] Opisz(SKImage obraz, float[] punkty) => rozpoznawanie.Select(r =>
{
    czas[r.Nazwa].Start();
    var c = r.Cechy(obraz, punkty);
    czas[r.Nazwa].Stop();
    return c;
}).ToArray();

// ───── wzorce ─────
Console.WriteLine("Wzorce...");
var wzorce = new List<(char Kto, string Plik, float[][] Cechy)>();
foreach (var kat in Directory.GetDirectories(Path.Combine(repo, "testdane", "wzorce")))
    foreach (var plik in Directory.GetFiles(kat, "*.jpg").Order())
    {
        using var bmp = Obraz.Wczytaj(plik);
        var w = yunet.Wykryj(bmp, 0.6f, false).MaxBy(d => d.Pole)!;
        using var pix = bmp.PeekPixels();
        using var obraz = SKImage.FromPixels(pix);
        wzorce.Add((skrot[Path.GetFileName(kat)], plik, Opisz(obraz, w.Punkty)));
    }

// ───── zdjęcia grupowe: rozpoznawanie na twarzach z YuNet + porównanie wykrywania ─────
var twarze = new List<(string Zdj, Wykrycie W, char? Kto, float[][] Cechy)>();
var detektory = new List<(string Nazwa, Func<SKBitmap, List<Wykrycie>> Wykryj)>
{
    ("YuNet, próg 0,6 (obecnie)", b => yunet.Wykryj(b, 0.6f, true)),
    ("YuNet, próg 0,5", b => yunet.Wykryj(b, 0.5f, true)),
};
if (scrfd is not null) detektory.Add(("SCRFD-10G, próg 0,5", b => scrfd.Wykryj(b, 0.5f, true)));
var wykrycia = detektory.ToDictionary(d => d.Nazwa, _ => new Dictionary<string, List<Wykrycie>>());
var czasWykrywania = detektory.ToDictionary(d => d.Nazwa, _ => new Stopwatch());
var tylko = new Dictionary<string, List<(string Zdj, SKBitmap Wycinek, float Pewnosc)>>();

foreach (var plik in Directory.GetFiles(Path.Combine(repo, "testdane", "zdjecia"), "*.jpg").Order())
{
    string zdj = Path.GetFileName(plik)[..2];
    Console.WriteLine($"Zdjęcie {Path.GetFileName(plik)}...");
    using var bmp = Obraz.Wczytaj(plik);
    using var pix = bmp.PeekPixels();
    using var obraz = SKImage.FromPixels(pix);
    foreach (var (nazwa, wykryj) in detektory)
    {
        czasWykrywania[nazwa].Start();
        wykrycia[nazwa][zdj] = wykryj(bmp);
        czasWykrywania[nazwa].Stop();
    }
    // każda etykieta trafia do jednej ramki: najbliższej po x i szerokości (osoby stojące jedna nad drugą mają podobne x)
    var bazoweRamki = wykrycia[detektory[0].Nazwa][zdj];
    var przypisane = new Dictionary<Wykrycie, char>();
    foreach (var o in oznaczone.Where(o => o.Zdj == zdj))
    {
        var r = bazoweRamki.MinBy(w => Math.Abs(w.X - o.X) + Math.Abs(w.W - o.W));
        if (r is not null && Math.Abs(r.X - o.X) < 5 && Math.Abs(r.W - o.W) < 5) przypisane[r] = o.Kto;
        else Console.WriteLine($"  brak ramki dla etykiety {o.Zdj} x={o.X} w={o.W}");
    }
    foreach (var w in bazoweRamki)
        twarze.Add((zdj, w, przypisane.TryGetValue(w, out var kto) ? kto : null, Opisz(obraz, w.Punkty)));
    // wykrycia jednego detektora, których nie ma w obecnym (YuNet 0,6), i odwrotnie — do obejrzenia
    var bazowe = wykrycia[detektory[0].Nazwa][zdj];
    foreach (var (nazwa, _) in detektory.Skip(1))
    {
        var inne = wykrycia[nazwa][zdj];
        Dodaj($"tylko {nazwa}", inne.Where(a => !bazowe.Any(b => Iou.Licz(a, b) > 0.3f)));
        Dodaj($"tylko YuNet 0,6 (nie ma w: {nazwa})", bazowe.Where(a => !inne.Any(b => Iou.Licz(a, b) > 0.3f)));
    }

    void Dodaj(string klucz, IEnumerable<Wykrycie> lista)
    {
        if (!tylko.TryGetValue(klucz, out var l)) tylko[klucz] = l = [];
        foreach (var w in lista)
        {
            var wyc = new SKBitmap(new SKImageInfo(112, 128, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var c = new SKCanvas(wyc);
            c.Clear(new SKColor(40, 40, 40));
            float bok = Math.Max(w.W, w.H) * 1.4f, cx = w.X + w.W / 2, cy = w.Y + w.H / 2;
            c.DrawImage(obraz, new SKRect(cx - bok / 2, cy - bok / 2, cx + bok / 2, cy + bok / 2), new SKRect(0, 0, 112, 112), Obraz.Gladko, null);
            using var f = new SKFont(SKTypeface.Default, 11);
            using var p = new SKPaint { Color = SKColors.White, IsAntialias = true };
            c.DrawText($"{zdj} {w.Pewnosc:0.00} {w.W:0}px", 3, 124, SKTextAlign.Left, f, p);
            l.Add((zdj, wyc, w.Pewnosc));
        }
    }
}

// ───── raport ─────
var md = new StringBuilder();
md.AppendLine("# Porównanie modeli na zdjęciach testowych");
md.AppendLine();
md.AppendLine($"Wygenerowano {DateTime.Now:yyyy-MM-dd HH:mm} narzędziem `proba/porownanie`. Zdjęcia: `testdane` (17 grupowych, 15 wzorców, 3 osoby).");
md.AppendLine("Modele InsightFace są tu wyłącznie punktem odniesienia (licencja: tylko niekomercyjne badania).");
md.AppendLine();
md.AppendLine("## Rozpoznawanie (te same twarze z YuNet, wzorzec = najlepszy z 5 wzorców osoby)");
md.AppendLine();
md.AppendLine($"Oznaczone wystąpienia osób ze wzorców: {twarze.Count(t => t.Kto is not null)} z {oznaczone.Length}. Porównania z osobami obcymi: każda");
md.AppendLine("pozostała twarz z każdą osobą ze wzorców, której na tym zdjęciu na pewno nie ma (wg `testdane/ZRODLA.md`).");
md.AppendLine();
md.AppendLine("| Model | właściwa osoba na 1. miejscu | min / mediana podobieństwa właściwej osoby | maks. podobieństwo obcej osoby | odsłonięte automatycznie przy zerze pomyłek | rozdzielenie d′ | ms / twarz |");
md.AppendLine("|---|---|---|---|---|---|---|");
for (int m = 0; m < rozpoznawanie.Count; m++)
{
    float Wynik(float[] cechy, char kto) => wzorce.Where(w => w.Kto == kto).Max(w => Rozpoznawanie.Podobienstwo(cechy, w.Cechy[m]));
    var tp = twarze.Where(t => t.Kto is char).Select(t =>
    {
        var oceny = "OBC".ToDictionary(k => k, k => Wynik(t.Cechy[m], k));
        return (Wlasciwa: oceny[t.Kto!.Value], Pierwsza: oceny.MaxBy(o => o.Value).Key == t.Kto);
    }).ToList();
    var obce = twarze.Where(t => t.Kto is null)
        .SelectMany(t => "OBC".Where(k => !naZdjeciu[t.Zdj].Contains(k)).Select(k => Wynik(t.Cechy[m], k))).ToList();
    float maksObcy = obce.Max();
    var w = tp.Select(t => t.Wlasciwa).Order().ToList();
    double sr1 = w.Average(), sr2 = obce.Average();
    double war1 = w.Average(x => (x - sr1) * (x - sr1)), war2 = obce.Average(x => (x - sr2) * (x - sr2));
    double dprim = (sr1 - sr2) / Math.Sqrt((war1 + war2) / 2);
    int odsloniete = tp.Count(t => t.Pierwsza && t.Wlasciwa > maksObcy);
    var r = rozpoznawanie[m];
    md.AppendLine($"| {r.Nazwa} | {tp.Count(t => t.Pierwsza)}/{tp.Count} | {w[0]:0.000} / {w[w.Count / 2]:0.000} | {maksObcy:0.000} (n={obce.Count}) | **{odsloniete}/{tp.Count}** | {dprim:0.00} | {czas[r.Nazwa].Elapsed.TotalMilliseconds / (twarze.Count + wzorce.Count):0.0} |");
}
md.AppendLine();
md.AppendLine("„Odsłonięte automatycznie przy zerze pomyłek”: ile właściwych osób przekracza najwyższe podobieństwo, jakie osiągnęła");
md.AppendLine("jakakolwiek obca twarz — czyli ile zielonych byłoby przy progu ustawionym tak, żeby na tym zbiorze nikt obcy nie został odsłonięty.");
md.AppendLine("Skale podobieństwa różnią się między modelami, więc porównywać należy tę kolumnę i d′, a nie surowe wartości.");
md.AppendLine();
md.AppendLine("## Wykrywanie (te same 3 skale i kafelki 640×640 co w programie)");
md.AppendLine();
md.AppendLine("| Detektor | wykrytych ramek | oznaczonych osób znalezionych | s / zdjęcie |");
md.AppendLine("|---|---|---|---|");
var bazoweOzn = twarze.Where(t => t.Kto is not null).Select(t => (t.Zdj, t.W)).ToList();
foreach (var (nazwa, _) in detektory)
{
    int lacznie = wykrycia[nazwa].Values.Sum(l => l.Count);
    var brak = bazoweOzn.Where(o => !wykrycia[nazwa][o.Zdj].Any(w => Iou.Licz(w, o.W) > 0.3f)).ToList();
    md.AppendLine($"| {nazwa} | {lacznie} | {bazoweOzn.Count - brak.Count}/{bazoweOzn.Count}"
        + (brak.Count == 0 ? "" : $" (brak: {string.Join(", ", brak.Select(o => $"{o.Zdj} x={o.W.X:0} {o.W.W:0}px"))})")
        + $" | {czasWykrywania[nazwa].Elapsed.TotalSeconds / 17:0.00} |");
    foreach (var o in brak)
    {
        var najblizsze = wykrycia[nazwa][o.Zdj].MaxBy(w => Iou.Licz(w, o.W));
        if (najblizsze is not null)
            Console.WriteLine($"  {nazwa}: {o.Zdj} x={o.W.X:0} y={o.W.Y:0} {o.W.W:0}x{o.W.H:0} — najbliższa ramka x={najblizsze.X:0} y={najblizsze.Y:0} {najblizsze.W:0}x{najblizsze.H:0}, IoU {Iou.Licz(najblizsze, o.W):0.00}");
    }
}
md.AppendLine();
md.AppendLine("Różnice między detektorami (arkusze wycinków w `proba/porownanie/wyniki/`, poza repozytorium):");
md.AppendLine();
foreach (var (klucz, lista) in tylko.OrderBy(k => k.Key))
{
    string plik = string.Concat(klucz.Select(z => char.IsLetterOrDigit(z) ? z : '_')) + ".png";
    md.AppendLine($"- {klucz}: **{lista.Count}** (`{plik}`)");
    if (lista.Count == 0) continue;
    int kol = 12, wier = (lista.Count + kol - 1) / kol;
    using var arkusz = new SKBitmap(kol * 114, wier * 130);
    using (var c = new SKCanvas(arkusz))
    {
        c.Clear(SKColors.Black);
        for (int i = 0; i < lista.Count; i++) c.DrawBitmap(lista[i].Wycinek, i % kol * 114, i / kol * 130, new SKSamplingOptions(SKFilterMode.Nearest), null);
    }
    File.WriteAllBytes(Path.Combine(katalogWynikow, plik), Obraz.Koduj(arkusz, SKEncodedImageFormat.Png));
}

File.WriteAllText(Path.Combine(repo, "proba", "porownanie", "WYNIKI.md"), md.ToString(), new UTF8Encoding(false));
Console.WriteLine();
Console.WriteLine(md);
