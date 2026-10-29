using Microsoft.Data.Sqlite;

namespace ZamazywanieTwarzy;

/// <summary>Lokalna baza SQLite: osoby, grupy, wzorce twarzy, przetworzone zdjęcia i wykryte twarze.</summary>
sealed class Baza
{
    const string Schemat = """
        CREATE TABLE IF NOT EXISTS ustawienia(klucz TEXT PRIMARY KEY, wartosc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS grupy(
            id INTEGER PRIMARY KEY,
            nazwa TEXT NOT NULL UNIQUE COLLATE NOCASE,
            archiwalna INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS osoby(
            id INTEGER PRIMARY KEY,
            imie TEXT NOT NULL,
            nazwisko TEXT NOT NULL DEFAULT '',
            zgoda INTEGER NOT NULL DEFAULT 0,
            zgoda_do TEXT NULL,
            uwagi TEXT NOT NULL DEFAULT '',
            utworzono TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS osoby_grupy(
            osoba_id INTEGER NOT NULL REFERENCES osoby(id) ON DELETE CASCADE,
            grupa_id INTEGER NOT NULL REFERENCES grupy(id) ON DELETE CASCADE,
            PRIMARY KEY(osoba_id, grupa_id));
        CREATE TABLE IF NOT EXISTS zdjecia(
            id INTEGER PRIMARY KEY,
            sciezka TEXT NOT NULL UNIQUE,
            katalog TEXT NOT NULL,
            szerokosc INTEGER NOT NULL,
            wysokosc INTEGER NOT NULL,
            rozmiar INTEGER NOT NULL,
            zmodyfikowano INTEGER NOT NULL,
            data_zdjecia TEXT NULL,
            grupy TEXT NOT NULL DEFAULT '',
            przetworzono TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_zdjecia_katalog ON zdjecia(katalog);
        CREATE TABLE IF NOT EXISTS twarze(
            id INTEGER PRIMARY KEY,
            zdjecie_id INTEGER NOT NULL REFERENCES zdjecia(id) ON DELETE CASCADE,
            x REAL NOT NULL, y REAL NOT NULL, w REAL NOT NULL, h REAL NOT NULL,
            pewnosc REAL NOT NULL,
            punkty TEXT NULL,
            wyrownana BLOB NULL,
            miniatura BLOB NULL,
            reczna INTEGER NOT NULL DEFAULT 0,
            nie_twarz INTEGER NOT NULL DEFAULT 0,
            wymus INTEGER NOT NULL DEFAULT 0,
            osoba_reczna INTEGER NULL REFERENCES osoby(id) ON DELETE SET NULL,
            osoba_auto INTEGER NULL,
            wynik_auto REAL NULL,
            wynik_2wz REAL NULL,
            wynik_druga REAL NULL,
            kandydaci TEXT NULL);
        CREATE INDEX IF NOT EXISTS ix_twarze_zdjecie ON twarze(zdjecie_id);
        CREATE TABLE IF NOT EXISTS wzorce(
            id INTEGER PRIMARY KEY,
            osoba_id INTEGER NOT NULL REFERENCES osoby(id) ON DELETE CASCADE,
            twarz_id INTEGER NULL REFERENCES twarze(id) ON DELETE SET NULL,
            wyrownana BLOB NULL,
            miniatura BLOB NOT NULL,
            zrodlo TEXT NOT NULL DEFAULT '',
            data_zdjecia TEXT NOT NULL,
            dodano TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_wzorce_osoba ON wzorce(osoba_id);
        CREATE INDEX IF NOT EXISTS ix_wzorce_twarz ON wzorce(twarz_id);
        """;

    readonly string _polaczenie;

    public Baza(string plik)
    {
        _polaczenie = new SqliteConnectionStringBuilder { DataSource = plik, Pooling = true }.ToString();
        using var c = Otworz();
        Wykonaj(c, "PRAGMA journal_mode=WAL;");
        Wykonaj(c, Schemat);
        UsunWektoryZDysku(c);
    }

    /// <summary>
    /// Wersja 0.1 zapisywała wektory cech twarzy (dane biometryczne) w kolumnach „cechy”. Teraz wektory istnieją
    /// tylko w pamięci, a na dysku leży wyrównany wycinek twarzy, z którego liczy się je od nowa — kolumny są usuwane,
    /// a VACUUM fizycznie nadpisuje stare strony pliku.
    /// </summary>
    static void UsunWektoryZDysku(SqliteConnection c)
    {
        bool zmiana = false;
        foreach (var tabela in new[] { "twarze", "wzorce" })
        {
            var kolumny = Lista(c, $"SELECT name FROM pragma_table_info('{tabela}')", r => r.GetString(0));
            if (!kolumny.Contains("wyrownana")) Wykonaj(c, $"ALTER TABLE {tabela} ADD COLUMN wyrownana BLOB NULL");
            if (kolumny.Contains("cechy"))
            {
                Wykonaj(c, $"ALTER TABLE {tabela} DROP COLUMN cechy");
                zmiana = true;
            }
        }
        if (zmiana) Odchudz(c);
    }

    /// <summary>Fizycznie usuwa skasowane dane z pliku bazy i z dziennika WAL.</summary>
    public static void Odchudz(SqliteConnection c)
    {
        Wykonaj(c, "VACUUM;");
        Wykonaj(c, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public SqliteConnection Otworz()
    {
        var c = new SqliteConnection(_polaczenie);
        c.Open();
        // secure_delete: kasowane rekordy (wycinki twarzy, decyzje) są zerowane w pliku, a nie tylko oznaczane jako wolne
        Wykonaj(c, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000; PRAGMA secure_delete=ON;");
        return c;
    }

    static SqliteCommand Polecenie(SqliteConnection c, string sql, (string, object?)[] p)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p)
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public static int Wykonaj(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = Polecenie(c, sql, p);
        return cmd.ExecuteNonQuery();
    }

    public static object? Skalar(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = Polecenie(c, sql, p);
        var w = cmd.ExecuteScalar();
        return w is DBNull ? null : w;
    }

    public static List<T> Lista<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> mapuj, params (string, object?)[] p)
    {
        using var cmd = Polecenie(c, sql, p);
        using var r = cmd.ExecuteReader();
        var lista = new List<T>();
        while (r.Read()) lista.Add(mapuj(r));
        return lista;
    }

    /// <summary>Transakcja przez BEGIN/COMMIT w SQL — polecenia pomocnicze nie muszą znać obiektu transakcji.</summary>
    public static void Transakcja(SqliteConnection c, Action praca)
    {
        Wykonaj(c, "BEGIN IMMEDIATE;");
        try
        {
            praca();
            Wykonaj(c, "COMMIT;");
        }
        catch
        {
            Wykonaj(c, "ROLLBACK;");
            throw;
        }
    }

    public static long OstatnieId(SqliteConnection c) => (long)Skalar(c, "SELECT last_insert_rowid();")!;

    public static string? Tekst(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? Liczba(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    public static double? Ulamek(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    public static DateTime? Data(string? s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    public static string Iso(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
