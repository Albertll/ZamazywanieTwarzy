#:package Microsoft.Data.Sqlite@10.0.14
// Podgląd bazy programu do testów: dotnet run proba\sql.cs -- <baza.db> "<zapytanie>" ["<zapytanie>" ...]
using Microsoft.Data.Sqlite;

using var c = new SqliteConnection($"Data Source={args[0]};Mode=ReadOnly");
c.Open();
foreach (var sql in args.Skip(1))
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    using var r = cmd.ExecuteReader();
    var wiersze = new List<string>();
    while (r.Read())
        wiersze.Add(string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : r.GetValue(i) is byte[] b ? $"<{b.Length} B>" : r.GetValue(i).ToString())));
    Console.WriteLine($"> {sql}\n  " + (wiersze.Count == 0 ? "(brak)" : string.Join("\n  ", wiersze)));
}
