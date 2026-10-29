using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace ZamazywanieTwarzy;

/// <summary>
/// Detektor twarzy YuNet (OpenCV Zoo, licencja MIT). Model ma stałe wejście 640×640, więc zdjęcie
/// przegląda się w kilku skalach kafelkami 640×640 z zakładką, a wyniki scala się (NMS).
/// Dekodowanie wyjść jak w cv::FaceDetectorYN.
/// </summary>
sealed class Detektor(string model, SessionOptions opcje) : IDisposable
{
    const int Rozmiar = 640;
    const int Zakladka = 128;
    static readonly int[] Kroki = [8, 16, 32];
    static readonly string[] Wyjscia =
        ["cls_8", "cls_16", "cls_32", "obj_8", "obj_16", "obj_32", "bbox_8", "bbox_16", "bbox_32", "kps_8", "kps_16", "kps_32"];

    readonly InferenceSession _sesja = new(model, opcje);

    public List<Wykrycie> Wykryj(SKBitmap obraz, float prog, bool maleTwarze)
    {
        int dl = Math.Max(obraz.Width, obraz.Height);
        // 1. całe zdjęcie w jednym kafelku — duże twarze; 2. ~1600 px — typowe zdjęcia grupowe; 3. ~3200 px — małe twarze w tle
        var skale = new List<double> { Math.Min(1.0, (double)Rozmiar / dl) };
        if (dl > Rozmiar) skale.Add(Math.Min(1.0, 1600.0 / dl));
        if (maleTwarze && dl > 1600) skale.Add(Math.Min(1.0, 3200.0 / dl));

        var wszystkie = new List<Wykrycie>();
        foreach (var s in skale.Distinct())
            Przebieg(obraz, s, prog, wszystkie);
        return Nms(wszystkie, 0.3f, scalaj: true);
    }

    void Przebieg(SKBitmap obraz, double s, float prog, List<Wykrycie> wynik)
    {
        int w = (int)Math.Round(obraz.Width * s), h = (int)Math.Round(obraz.Height * s);
        using var skalowany = s >= 0.999 ? null : Obraz.Skaluj(obraz, w, h);
        var zrodlo = skalowany ?? obraz;
        w = zrodlo.Width; h = zrodlo.Height;

        var tensor = new float[3 * Rozmiar * Rozmiar];
        int krok = Rozmiar - Zakladka;
        for (int ty = 0; ; ty += krok)
        {
            for (int tx = 0; ; tx += krok)
            {
                Obraz.WypelnijBgr(zrodlo, tx, ty, Rozmiar, tensor);
                foreach (var d in Uruchom(tensor, prog))
                {
                    // Ramka ucięta wewnętrzną krawędzią kafelka — cała twarz jest w sąsiednim kafelku albo w grubszej skali.
                    bool ucieta = (tx > 0 && d.X < 2) || (ty > 0 && d.Y < 2)
                        || (tx + Rozmiar < w && d.X + d.W > Rozmiar - 2) || (ty + Rozmiar < h && d.Y + d.H > Rozmiar - 2);
                    if (!ucieta)
                        wynik.Add(d.Przeksztalc(tx, ty, (float)(1 / s)));
                }
                if (tx + Rozmiar >= w) break;
            }
            if (ty + Rozmiar >= h) break;
        }
    }

    List<Wykrycie> Uruchom(float[] tensor, float prog)
    {
        using var wejscie = OrtValue.CreateTensorValueFromMemory(tensor, [1, 3, Rozmiar, Rozmiar]);
        using var opcjeUruchomienia = new RunOptions();
        using var wyniki = _sesja.Run(opcjeUruchomienia, ["input"], [wejscie], Wyjscia);
        var lista = new List<Wykrycie>();
        for (int k = 0; k < Kroki.Length; k++)
        {
            int krok = Kroki[k], kolumny = Rozmiar / krok, wiersze = Rozmiar / krok;
            var cls = wyniki[k].GetTensorDataAsSpan<float>();
            var obj = wyniki[3 + k].GetTensorDataAsSpan<float>();
            var bbox = wyniki[6 + k].GetTensorDataAsSpan<float>();
            var kps = wyniki[9 + k].GetTensorDataAsSpan<float>();
            for (int r = 0; r < wiersze; r++)
            for (int c = 0; c < kolumny; c++)
            {
                int i = r * kolumny + c;
                float pewnosc = MathF.Sqrt(Math.Clamp(cls[i], 0f, 1f) * Math.Clamp(obj[i], 0f, 1f));
                if (pewnosc < prog) continue;
                float cx = (c + bbox[i * 4]) * krok, cy = (r + bbox[i * 4 + 1]) * krok;
                float bw = MathF.Exp(bbox[i * 4 + 2]) * krok, bh = MathF.Exp(bbox[i * 4 + 3]) * krok;
                var punkty = new float[10];
                for (int n = 0; n < 5; n++)
                {
                    punkty[2 * n] = (kps[i * 10 + 2 * n] + c) * krok;
                    punkty[2 * n + 1] = (kps[i * 10 + 2 * n + 1] + r) * krok;
                }
                lista.Add(new Wykrycie(cx - bw / 2, cy - bh / 2, bw, bh, pewnosc, punkty));
            }
        }
        return Nms(lista, 0.3f, scalaj: false);
    }

    /// <summary>Tłumienie nie-maksimów; przy scalaniu skal tłumi też ramkę prawie w całości zawartą w lepszej.</summary>
    static List<Wykrycie> Nms(List<Wykrycie> wejscie, float progIou, bool scalaj)
    {
        var wynik = new List<Wykrycie>();
        foreach (var a in wejscie.OrderByDescending(d => d.Pewnosc))
        {
            bool tlumiona = false;
            foreach (var b in wynik)
            {
                float ix = Math.Max(0, Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X));
                float iy = Math.Max(0, Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y));
                float czesc = ix * iy;
                if (czesc / (a.Pole + b.Pole - czesc) > progIou || (scalaj && czesc > 0.7f * Math.Min(a.Pole, b.Pole)))
                {
                    tlumiona = true;
                    break;
                }
            }
            if (!tlumiona) wynik.Add(a);
        }
        return wynik;
    }

    public void Dispose() => _sesja.Dispose();
}
