using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace ZamazywanieTwarzy;

/// <summary>
/// Rozpoznawanie twarzy SFace (OpenCV Zoo, licencja Apache 2.0): wyrównanie twarzy do wzorca 112×112
/// według 5 punktów i wektor 128 cech. Wektory są normalizowane, więc podobieństwo dwóch twarzy
/// to zwykły iloczyn skalarny (cosinus kąta).
/// Wektor (dana biometryczna) nie trafia na dysk — zapisuje się wyrównany wycinek (JPEG), a wektor liczy się
/// zawsze z odkodowanego wycinka, więc po ponownym uruchomieniu wychodzi identyczny.
/// </summary>
sealed class Rozpoznawanie : IDisposable
{
    const int R = 112;

    /// <summary>Docelowe położenie oczu, nosa i kącików ust (jak w cv::FaceRecognizerSF::alignCrop).</summary>
    static readonly float[] Wzorzec =
        [38.2946f, 51.6963f, 73.5318f, 51.5014f, 56.0252f, 71.7366f, 41.5493f, 92.3655f, 70.7299f, 92.2041f];

    readonly InferenceSession _sesja;
    readonly string _wejscie;
    readonly string _wyjscie;

    public Rozpoznawanie(string model, SessionOptions opcje)
    {
        _sesja = new InferenceSession(model, opcje);
        _wejscie = _sesja.InputMetadata.Keys.First();
        _wyjscie = _sesja.OutputMetadata.Keys.First();
    }

    /// <summary>Wyrównany wycinek twarzy do zapisania na dysku i wektor cech policzony z tego samego wycinka.</summary>
    public (byte[] Wycinek, float[] Cechy) Opisz(SKImage obraz, float[] punkty)
    {
        using var wyrownana = Wyrownaj(obraz, punkty);
        var wycinek = Obraz.Koduj(wyrownana, jakosc: 95);
        return (wycinek, CechyZWycinka(wycinek));
    }

    public float[] CechyZWycinka(byte[] wycinek)
    {
        using var bmp = Obraz.DekodujBgra(wycinek);
        return Cechy(bmp);
    }

    float[] Cechy(SKBitmap wyrownana)
    {
        var t = new float[3 * R * R];
        var px = wyrownana.GetPixelSpan();
        int pl = R * R;
        // Kanały R,G,B — jak swapRB w OpenCV. Sprawdzone na zdjęciach testowych: BGR wyraźnie gorzej rozdziela osoby.
        for (int i = 0; i < pl; i++)
        {
            t[i] = px[i * 4 + 2];
            t[pl + i] = px[i * 4 + 1];
            t[2 * pl + i] = px[i * 4];
        }
        using var wejscie = OrtValue.CreateTensorValueFromMemory(t, [1, 3, R, R]);
        using var opcje = new RunOptions();
        using var wyniki = _sesja.Run(opcje, [_wejscie], [wejscie], [_wyjscie]);
        var cechy = wyniki[0].GetTensorDataAsSpan<float>().ToArray();
        Normalizuj(cechy);
        return cechy;
    }

    /// <summary>Podobieństwo (skala + obrót + przesunięcie) dopasowane metodą najmniejszych kwadratów.</summary>
    public static SKBitmap Wyrownaj(SKImage obraz, float[] p)
    {
        float sx = 0, sy = 0, dx = 0, dy = 0;
        for (int i = 0; i < 5; i++)
        {
            sx += p[2 * i]; sy += p[2 * i + 1];
            dx += Wzorzec[2 * i]; dy += Wzorzec[2 * i + 1];
        }
        sx /= 5; sy /= 5; dx /= 5; dy /= 5;

        float l1 = 0, l2 = 0, m = 0;
        for (int i = 0; i < 5; i++)
        {
            float ax = p[2 * i] - sx, ay = p[2 * i + 1] - sy;
            float bx = Wzorzec[2 * i] - dx, by = Wzorzec[2 * i + 1] - dy;
            l1 += ax * bx + ay * by;
            l2 += ax * by - ay * bx;
            m += ax * ax + ay * ay;
        }
        float a = l1 / m, b = l2 / m;
        float tx = dx - (a * sx - b * sy), ty = dy - (b * sx + a * sy);

        var wynik = new SKBitmap(new SKImageInfo(R, R, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var c = new SKCanvas(wynik);
        c.Clear(SKColors.Black);
        c.SetMatrix(new SKMatrix(a, -b, tx, b, a, ty, 0, 0, 1));
        c.DrawImage(obraz, 0, 0, Obraz.Gladko, null);
        return wynik;
    }

    public static void Normalizuj(float[] v)
    {
        double s = 0;
        foreach (var x in v) s += x * x;
        float n = (float)Math.Sqrt(s);
        if (n > 0) for (int i = 0; i < v.Length; i++) v[i] /= n;
    }

    public static float Podobienstwo(float[] a, float[] b)
    {
        float s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    public void Dispose() => _sesja.Dispose();
}
