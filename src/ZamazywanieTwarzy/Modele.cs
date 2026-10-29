namespace ZamazywanieTwarzy;

/// <summary>Twarz znaleziona przez detektor: ramka i 5 punktów (oczy, nos, kąciki ust) w pikselach zdjęcia.</summary>
sealed record Wykrycie(float X, float Y, float W, float H, float Pewnosc, float[] Punkty)
{
    public float Pole => W * H;

    public Wykrycie Przeksztalc(float dx, float dy, float skala) => new(
        (X + dx) * skala, (Y + dy) * skala, W * skala, H * skala, Pewnosc,
        Punkty.Select((p, i) => (p + (i % 2 == 0 ? dx : dy)) * skala).ToArray());
}

/// <summary>Ustawienia zapisywane w bazie (JSON).</summary>
sealed class Ustawienia
{
    public const string TrybRozpoznawania = "rozpoznawanie";
    public const string TrybBezRozpoznawania = "bezRozpoznawania";

    /// <summary>
    /// „rozpoznawanie” — twarze porównywane ze wzorcami; wektory cech istnieją tylko w pamięci RAM.
    /// „bezRozpoznawania” — tylko wykrywanie twarzy, osoby ze zgodą odsłania operator; żadnego przetwarzania biometrycznego.
    /// </summary>
    public string Tryb { get; set; } = TrybRozpoznawania;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool RozpoznawanieWlaczone => Tryb != TrybBezRozpoznawania;

    /// <summary>Od tego podobieństwa twarz uznaje się za konkretną osobę (SFace: zalecane 0,363).</summary>
    public double ProgRozpoznania { get; set; } = 0.363;

    /// <summary>Automatyczne „zielone” wymaga wyższej pewności niż samo rozpoznanie — pomyłka odsłoniłaby obcą osobę.</summary>
    public double ProgZgody { get; set; } = 0.5;

    /// <summary>Wymagana przewaga nad drugą najbardziej podobną osobą (rodzeństwo, sobowtóry).</summary>
    public double Margines { get; set; } = 0.05;

    /// <summary>Zielone tylko, gdy twarz pasuje do co najmniej dwóch wzorców osoby (jeśli osoba ma ich ≥ 2).</summary>
    public bool DwaWzorce { get; set; } = true;

    public double ProgWykrywania { get; set; } = 0.6;
    public bool MaleTwarze { get; set; } = true;

    /// <summary>Na ile „komórek” w poziomie rozbić zamazywaną twarz — mniej = mocniej.</summary>
    public int SilaZamazania { get; set; } = 6;

    /// <summary>Po ilu miesiącach ostrzegać, że najnowszy wzorzec osoby jest stary (twarze dzieci szybko się zmieniają).</summary>
    public int MiesiaceWzorca { get; set; } = 12;
}

sealed class OsobaInfo
{
    public long Id;
    public string Imie = "";
    public string Nazwisko = "";
    public bool Zgoda;
    public DateTime? ZgodaDo;
    public string Uwagi = "";
    public List<long> Grupy = [];
    public int Wzorce;
    public DateTime? NajnowszyWzorzec;

    public string Nazwa => (Imie + " " + Nazwisko).Trim();
    public bool ZgodaWazna => Zgoda && (ZgodaDo is null || ZgodaDo.Value.Date >= DateTime.Today);
}

sealed class TwarzRek
{
    public long Id, ZdjecieId;
    public float X, Y, W, H, Pewnosc;
    public bool Reczna, NieTwarz, Wymus;
    public long? OsobaReczna, OsobaAuto;
    public double? WynikAuto, Wynik2Wz, WynikDruga;
    public string? Kandydaci;
}

sealed record StatusTwarzy(string Kolor, bool Zamaz, string Opis, long? OsobaId);

/// <summary>Błąd do pokazania użytkownikowi (HTTP 400), a nie awaria programu.</summary>
sealed class BladUzytkownika(string komunikat) : Exception(komunikat);

record PrzetworzZadanie(string Katalog, bool Podkatalogi, long[]? Grupy);
record EksportZadanie(string Katalog, bool Podkatalogi, string Cel);
record PrzypiszZadanie(long[] Twarze, long OsobaId, bool Wzorzec);
record OsobaDto(string Imie, string? Nazwisko, bool Zgoda, string? ZgodaDo, long[]? Grupy, string? Uwagi);
record NowaOsobaZadanie(long[] Twarze, OsobaDto Osoba, bool Wzorzec);
record OznaczZadanie(long[] Twarze, bool? NieTwarz, bool? Wymus, bool Cofnij);
record RamkaDto(float X, float Y, float W, float H);
record GrupaDto(string Nazwa, bool Archiwalna);
record ZgodaDto(bool Zgoda, string? ZgodaDo);
record ImportWzorcowZadanie(string Katalog, long? GrupaId, bool Zgoda);
record KatalogDto(string Katalog, bool Podkatalogi);
