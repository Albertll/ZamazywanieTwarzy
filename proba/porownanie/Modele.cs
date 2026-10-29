using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace ZamazywanieTwarzy;

interface IRozpoznawanie
{
    string Nazwa { get; }
    float[] Cechy(SKImage obraz, float[] punkty);
}

/// <summary>SFace dokładnie tak jak w programie: wektor z zapisanego wycinka JPEG.</summary>
sealed class SFace(Rozpoznawanie r) : IRozpoznawanie
{
    public string Nazwa => "SFace (obecny)";
    public float[] Cechy(SKImage obraz, float[] punkty) => r.Opisz(obraz, punkty).Cechy;
}

/// <summary>Modele w stylu ArcFace (InsightFace, AuraFace): wejście 112×112 RGB, (piksel − średnia) / skala.</summary>
sealed class ArcFace(string nazwa, string model, float srednia, float skala, SessionOptions opcje) : IRozpoznawanie
{
    const int R = 112;
    readonly InferenceSession _sesja = new(model, opcje);
    public string Nazwa => nazwa;

    public float[] Cechy(SKImage obraz, float[] punkty)
    {
        using var wyr = Rozpoznawanie.Wyrownaj(obraz, punkty);
        var px = wyr.GetPixelSpan();
        var t = new float[3 * R * R];
        int pl = R * R;
        for (int i = 0; i < pl; i++)
        {
            t[i] = (px[i * 4 + 2] - srednia) / skala;
            t[pl + i] = (px[i * 4 + 1] - srednia) / skala;
            t[2 * pl + i] = (px[i * 4] - srednia) / skala;
        }
        using var we = OrtValue.CreateTensorValueFromMemory(t, [1, 3, R, R]);
        using var ro = new RunOptions();
        using var wy = _sesja.Run(ro, [_sesja.InputMetadata.Keys.First()], [we], [_sesja.OutputNames[0]]);
        var c = wy[0].GetTensorDataAsSpan<float>().ToArray();
        Rozpoznawanie.Normalizuj(c);
        return c;
    }
}

/// <summary>
/// SCRFD-10G (InsightFace) przy tym samym przeglądaniu zdjęcia co YuNet w programie: 3 skale, kafelki 640×640
/// z zakładką 128. Dekodowanie jak w insightface/python-package/insightface/model_zoo/scrfd.py.
/// </summary>
sealed class Scrfd(string model, SessionOptions opcje)
{
    const int Rozmiar = 640, Zakladka = 128, Kotwice = 2;
    static readonly int[] Kroki = [8, 16, 32];
    readonly InferenceSession _sesja = new(model, opcje);

    public List<Wykrycie> Wykryj(SKBitmap obraz, float prog, bool maleTwarze)
    {
        int dl = Math.Max(obraz.Width, obraz.Height);
        var skale = new List<double> { Math.Min(1.0, (double)Rozmiar / dl) };
        if (dl > Rozmiar) skale.Add(Math.Min(1.0, 1600.0 / dl));
        if (maleTwarze && dl > 1600) skale.Add(Math.Min(1.0, 3200.0 / dl));
        var wszystkie = new List<Wykrycie>();
        foreach (var s in skale.Distinct())
        {
            int w = (int)Math.Round(obraz.Width * s), h = (int)Math.Round(obraz.Height * s);
            using var skalowany = s >= 0.999 ? null : Obraz.Skaluj(obraz, w, h);
            var zrodlo = skalowany ?? obraz;
            w = zrodlo.Width; h = zrodlo.Height;
            var tensor = new float[3 * Rozmiar * Rozmiar];
            for (int ty = 0; ; ty += Rozmiar - Zakladka)
            {
                for (int tx = 0; ; tx += Rozmiar - Zakladka)
                {
                    Wypelnij(zrodlo, tx, ty, tensor);
                    foreach (var d in Uruchom(tensor, prog))
                    {
                        bool ucieta = (tx > 0 && d.X < 2) || (ty > 0 && d.Y < 2)
                            || (tx + Rozmiar < w && d.X + d.W > Rozmiar - 2) || (ty + Rozmiar < h && d.Y + d.H > Rozmiar - 2);
                        if (!ucieta) wszystkie.Add(d.Przeksztalc(tx, ty, (float)(1 / s)));
                    }
                    if (tx + Rozmiar >= w) break;
                }
                if (ty + Rozmiar >= h) break;
            }
        }
        return Nms(wszystkie, 0.3f, true);
    }

    /// <summary>RGB, (piksel − 127,5) / 128; poza obrazem piksel 0 — jak w insightface.</summary>
    static void Wypelnij(SKBitmap bmp, int x0, int y0, float[] t)
    {
        const float sr = 127.5f, sk = 128f;
        Array.Fill(t, -sr / sk);
        int pl = Rozmiar * Rozmiar, wiersz = bmp.RowBytes;
        var px = bmp.GetPixelSpan();
        int xk = Math.Min(bmp.Width, x0 + Rozmiar), yk = Math.Min(bmp.Height, y0 + Rozmiar);
        for (int y = y0; y < yk; y++)
            for (int x = x0; x < xk; x++)
            {
                int p = y * wiersz + x * 4, i = (y - y0) * Rozmiar + (x - x0);
                t[i] = (px[p + 2] - sr) / sk;
                t[pl + i] = (px[p + 1] - sr) / sk;
                t[2 * pl + i] = (px[p] - sr) / sk;
            }
    }

    List<Wykrycie> Uruchom(float[] tensor, float prog)
    {
        using var we = OrtValue.CreateTensorValueFromMemory(tensor, [1, 3, Rozmiar, Rozmiar]);
        using var ro = new RunOptions();
        using var wy = _sesja.Run(ro, [_sesja.InputMetadata.Keys.First()], [we], _sesja.OutputNames);
        var lista = new List<Wykrycie>();
        for (int k = 0; k < Kroki.Length; k++)
        {
            int krok = Kroki[k], kol = Rozmiar / krok;
            var wyniki = wy[k].GetTensorDataAsSpan<float>();
            var ramki = wy[3 + k].GetTensorDataAsSpan<float>();
            var punkty = wy[6 + k].GetTensorDataAsSpan<float>();
            for (int i = 0; i < wyniki.Length; i++)
            {
                if (wyniki[i] < prog) continue;
                int miejsce = i / Kotwice;
                float cx = miejsce % kol * krok, cy = miejsce / kol * krok;
                float x1 = cx - ramki[i * 4] * krok, y1 = cy - ramki[i * 4 + 1] * krok;
                float x2 = cx + ramki[i * 4 + 2] * krok, y2 = cy + ramki[i * 4 + 3] * krok;
                var p = new float[10];
                for (int n = 0; n < 5; n++)
                {
                    p[2 * n] = cx + punkty[i * 10 + 2 * n] * krok;
                    p[2 * n + 1] = cy + punkty[i * 10 + 2 * n + 1] * krok;
                }
                lista.Add(new Wykrycie(x1, y1, x2 - x1, y2 - y1, wyniki[i], p));
            }
        }
        return Nms(lista, 0.4f, false);
    }

    static List<Wykrycie> Nms(List<Wykrycie> wejscie, float progIou, bool scalaj)
    {
        var wynik = new List<Wykrycie>();
        foreach (var a in wejscie.OrderByDescending(d => d.Pewnosc))
        {
            bool tlumiona = wynik.Any(b =>
            {
                float czesc = Iou.Czesc(a, b);
                return czesc / (a.Pole + b.Pole - czesc) > progIou || (scalaj && czesc > 0.7f * Math.Min(a.Pole, b.Pole));
            });
            if (!tlumiona) wynik.Add(a);
        }
        return wynik;
    }
}

static class Iou
{
    public static float Czesc(Wykrycie a, Wykrycie b)
    {
        float ix = Math.Max(0, Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X));
        float iy = Math.Max(0, Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y));
        return ix * iy;
    }

    public static float Licz(Wykrycie a, Wykrycie b)
    {
        float c = Czesc(a, b);
        return c / (a.Pole + b.Pole - c);
    }
}
