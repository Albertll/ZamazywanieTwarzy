using SkiaSharp;

namespace ZamazywanieTwarzy;

/// <summary>
/// Zamazanie twarzy: obszar (z zapasem na włosy i podbródek) zmniejsza się do kilku pikseli,
/// a potem rozciąga i rozmywa w owalu. Z kilku pikseli nie da się odtworzyć twarzy —
/// w przeciwieństwie do samego rozmycia Gaussa, które przy słabym promieniu bywa częściowo odwracalne.
/// </summary>
static class Zamazywanie
{
    public static SKRect Obszar(float x, float y, float w, float h) =>
        new(x - 0.25f * w, y - 0.35f * h, x + w + 0.25f * w, y + h + 0.2f * h);

    public static void Zamaz(SKCanvas c, SKImage zrodlo, SKRect obszar, int sila)
    {
        sila = Math.Clamp(sila, 3, 16);
        int kw = sila, kh = Math.Max(1, (int)Math.Round(sila * obszar.Height / obszar.Width));
        using var maly = new SKBitmap(new SKImageInfo(kw, kh, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var mc = new SKCanvas(maly))
            mc.DrawImage(zrodlo, obszar, new SKRect(0, 0, kw, kh), Obraz.Gladko, null);
        using var malyObraz = SKImage.FromBitmap(maly);

        c.Save();
        using var owal = new SKRoundRect(obszar, obszar.Width / 2, obszar.Height / 2);
        c.ClipRoundRect(owal, SKClipOperation.Intersect, true);
        float sigma = obszar.Width / (sila * 1.5f);
        using var farba = new SKPaint();
        farba.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        c.DrawImage(malyObraz, new SKRect(0, 0, kw, kh), obszar, new SKSamplingOptions(SKFilterMode.Linear), farba);
        c.Restore();
    }

    /// <summary>Zwraca nową bitmapę z zamazanymi obszarami (podanymi we współrzędnych tej bitmapy).</summary>
    public static SKBitmap Zastosuj(SKBitmap obraz, IEnumerable<SKRect> obszary, int sila)
    {
        var wynik = obraz.Copy();
        using var zrodlo = SKImage.FromBitmap(obraz);
        using var c = new SKCanvas(wynik);
        foreach (var o in obszary)
            Zamaz(c, zrodlo, o, sila);
        return wynik;
    }
}
