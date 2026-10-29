using System.Globalization;
using System.Text;

namespace ZamazywanieTwarzy;

/// <summary>Minimalny odczyt daty zrobienia zdjęcia (EXIF DateTimeOriginal) z pliku JPEG.</summary>
static class Exif
{
    public static DateTime? DataZrobienia(string sciezka)
    {
        try
        {
            using var f = File.OpenRead(sciezka);
            var b = new byte[(int)Math.Min(f.Length, 256 * 1024)];
            f.ReadExactly(b);
            if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return null;
            int i = 2;
            while (i + 4 < b.Length && b[i] == 0xFF)
            {
                byte znacznik = b[i + 1];
                int dl = (b[i + 2] << 8) | b[i + 3];
                if (znacznik == 0xE1 && i + 10 < b.Length && Encoding.ASCII.GetString(b, i + 4, 4) == "Exif")
                    return Tiff(b, i + 10);
                if (znacznik == 0xDA) break;
                i += 2 + dl;
            }
        }
        catch (Exception) { }
        return null;
    }

    static DateTime? Tiff(byte[] b, int t)
    {
        bool le = b[t] == (byte)'I';
        int U16(int o) => le ? b[o] | (b[o + 1] << 8) : (b[o] << 8) | b[o + 1];
        int U32(int o) => le ? b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)
                             : (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        string? Tekst(int wpis)
        {
            int ile = U32(wpis + 4);
            int o = ile > 4 ? t + U32(wpis + 8) : wpis + 8;
            return Encoding.ASCII.GetString(b, o, Math.Max(0, ile - 1));
        }

        string? data = null;
        int? exifIfd = null;
        int ifd0 = t + U32(t + 4);
        for (int n = 0, ile = U16(ifd0); n < ile; n++)
        {
            int wpis = ifd0 + 2 + n * 12;
            int znacznik = U16(wpis);
            if (znacznik == 0x8769) exifIfd = t + U32(wpis + 8);
            else if (znacznik == 0x0132) data = Tekst(wpis);
        }
        if (exifIfd is int e)
            for (int n = 0, ile = U16(e); n < ile; n++)
            {
                int wpis = e + 2 + n * 12;
                if (U16(wpis) == 0x9003) { data = Tekst(wpis); break; }
            }

        return DateTime.TryParseExact(data?.Trim('\0', ' '), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
    }
}
