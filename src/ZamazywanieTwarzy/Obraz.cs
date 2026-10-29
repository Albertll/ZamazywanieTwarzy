using SkiaSharp;

namespace ZamazywanieTwarzy;

/// <summary>Wczytywanie, skalowanie i zapis zdjęć (SkiaSharp).</summary>
static class Obraz
{
    static readonly string[] Rozszerzenia = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];
    public static readonly SKSamplingOptions Gladko = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public static bool JestZdjeciem(string sciezka) =>
        Rozszerzenia.Contains(Path.GetExtension(sciezka).ToLowerInvariant());

    /// <summary>Dekoduje plik do BGRA i obraca zgodnie z EXIF — tak, jak pokazuje go przeglądarka zdjęć.</summary>
    public static SKBitmap Wczytaj(string sciezka)
    {
        // Bajty zamiast SKCodec.Create(ścieżka) — natywne otwieranie pliku bywa zawodne przy polskich znakach w ścieżce.
        using var dane = SKData.CreateCopy(File.ReadAllBytes(sciezka));
        using var kodek = SKCodec.Create(dane) ?? throw new BladUzytkownika($"Nieobsługiwany format pliku: {Path.GetFileName(sciezka)}");
        var info = new SKImageInfo(kodek.Info.Width, kodek.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        var wynik = kodek.GetPixels(info, bmp.GetPixels());
        if (wynik is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bmp.Dispose();
            throw new BladUzytkownika($"Nie udało się odczytać zdjęcia {Path.GetFileName(sciezka)}: {wynik}");
        }
        return Obroc(bmp, kodek.EncodedOrigin);
    }

    static SKBitmap Obroc(SKBitmap zrodlo, SKEncodedOrigin orientacja)
    {
        if (orientacja is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            return zrodlo;

        int w = zrodlo.Width, h = zrodlo.Height;
        bool zamiana = orientacja is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        // x' = m0*x + m1*y + m2, y' = m3*x + m4*y + m5
        var m = orientacja switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        var cel = new SKBitmap(new SKImageInfo(zamiana ? h : w, zamiana ? w : h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(cel))
        {
            c.SetMatrix(m);
            c.DrawBitmap(zrodlo, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        }
        zrodlo.Dispose();
        return cel;
    }

    public static SKBitmap Skaluj(SKBitmap zrodlo, int w, int h)
    {
        var cel = new SKBitmap(new SKImageInfo(Math.Max(1, w), Math.Max(1, h), SKColorType.Bgra8888, SKAlphaType.Premul));
        zrodlo.ScalePixels(cel, Gladko);
        return cel;
    }

    public static SKBitmap Pomniejsz(SKBitmap zrodlo, int maxBok)
    {
        double s = Math.Min(1.0, (double)maxBok / Math.Max(zrodlo.Width, zrodlo.Height));
        return Skaluj(zrodlo, (int)Math.Round(zrodlo.Width * s), (int)Math.Round(zrodlo.Height * s));
    }

    public static byte[] Koduj(SKBitmap bmp, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg, int jakosc = 85)
    {
        using var dane = bmp.Encode(format, jakosc);
        return dane.ToArray();
    }

    /// <summary>Kwadratowy wycinek wokół twarzy (do list w interfejsie).</summary>
    public static byte[] Wycinek(SKImage obraz, float x, float y, float w, float h, int rozmiar = 128)
    {
        float bok = Math.Max(w, h) * 1.5f, cx = x + w / 2, cy = y + h / 2;
        using var bmp = new SKBitmap(new SKImageInfo(rozmiar, rozmiar, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(0x30, 0x30, 0x30));
            c.DrawImage(obraz, new SKRect(cx - bok / 2, cy - bok / 2, cx + bok / 2, cy + bok / 2),
                new SKRect(0, 0, rozmiar, rozmiar), Gladko, null);
        }
        return Koduj(bmp, jakosc: 85);
    }

    /// <summary>Wypełnia tensor [1,3,r,r] kanałami B,G,R (0–255) z fragmentu obrazu; poza obrazem zera.</summary>
    public static void WypelnijBgr(SKBitmap bmp, int x0, int y0, int r, float[] tensor)
    {
        Array.Clear(tensor);
        int plaszczyzna = r * r, wiersz = bmp.RowBytes;
        var px = bmp.GetPixelSpan();
        int xk = Math.Min(bmp.Width, x0 + r), yk = Math.Min(bmp.Height, y0 + r);
        for (int y = y0; y < yk; y++)
        {
            int zr = y * wiersz, cel = (y - y0) * r - x0;
            for (int x = x0; x < xk; x++)
            {
                int p = zr + x * 4, i = cel + x;
                tensor[i] = px[p];
                tensor[plaszczyzna + i] = px[p + 1];
                tensor[2 * plaszczyzna + i] = px[p + 2];
            }
        }
    }
}
