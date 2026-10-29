namespace ZamazywanieTwarzy;

/// <summary>Punkty końcowe HTTP używane przez interfejs w przeglądarce.</summary>
static class Api
{
    public static void Mapuj(WebApplication app, Silnik s)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/info", () => new { s.KatalogDanych, s.Rownoleglosc });
        api.MapGet("/zadanie", () => s.Zadanie is not { } z ? new { trwa = false } as object : new
        {
            z.Rodzaj, z.Trwa, z.Wszystkie, z.Gotowe, z.Biezacy, Bledy = z.Bledy.ToArray(), z.Komunikat, z.Start, z.Koniec,
        });
        api.MapPost("/przerwij", () => { s.Przerwij(); return Results.Ok(); });
        api.MapPost("/przetworz", (PrzetworzZadanie p) => { s.Przetworz(p); return Results.Ok(); });
        api.MapPost("/eksport", (EksportZadanie p) => { s.Eksportuj(p); return Results.Ok(); });

        api.MapGet("/katalogi", (string? sciezka) => Silnik.Katalogi(sciezka));

        api.MapGet("/zdjecia", (string? katalog, bool? podkatalogi, long? osoba) => s.ListaZdjec(katalog, podkatalogi ?? true, osoba));
        api.MapGet("/zdjecia/{id:long}", (long id) => s.Zdjecie(id));
        api.MapGet("/zdjecia/{id:long}/miniatura", (long id) => Jpeg(s.MiniaturaZdjecia(id)));
        api.MapGet("/zdjecia/{id:long}/podglad", (long id, bool? zamazane) => Jpeg(s.Podglad(id, zamazane == true)));
        api.MapPost("/zdjecia/{id:long}/twarze", (long id, RamkaDto r) => new { id = s.DodajRamke(id, r) });

        api.MapGet("/twarze/{id:long}/miniatura", (long id) => Jpeg(s.MiniaturaTwarzy(id)));
        api.MapDelete("/twarze/{id:long}", (long id) => { s.UsunRamke(id); return Results.Ok(); });
        api.MapPost("/twarze/przypisz", (PrzypiszZadanie p) => { s.Przypisz(p.Twarze, p.OsobaId, p.Wzorzec); return Results.Ok(); });
        api.MapPost("/twarze/nowa-osoba", (NowaOsobaZadanie p) => new { id = s.NowaOsoba(p.Twarze, p.Osoba, p.Wzorzec) });
        api.MapPost("/twarze/oznacz", (OznaczZadanie p) => { s.Oznacz(p); return Results.Ok(); });
        api.MapGet("/nierozpoznane", (string? katalog, bool? podkatalogi) => s.Nierozpoznane(katalog, podkatalogi ?? true));

        api.MapGet("/osoby", () => s.Osoby());
        api.MapPost("/osoby", (OsobaDto d) => new { id = s.ZapiszOsobe(null, d) });
        api.MapPut("/osoby/{id:long}", (long id, OsobaDto d) => new { id = s.ZapiszOsobe(id, d) });
        api.MapDelete("/osoby/{id:long}", (long id) => { s.UsunOsobe(id); return Results.Ok(); });
        api.MapGet("/osoby/{id:long}/wzorce", (long id) => s.Wzorce(id));
        api.MapGet("/wzorce/{id:long}/miniatura", (long id) => Jpeg(s.MiniaturaWzorca(id)));
        api.MapDelete("/wzorce/{id:long}", (long id) => { s.UsunWzorzec(id); return Results.Ok(); });

        api.MapGet("/grupy", () => s.Grupy());
        api.MapPost("/grupy", (GrupaDto g) => new { id = s.ZapiszGrupe(null, g) });
        api.MapPut("/grupy/{id:long}", (long id, GrupaDto g) => new { id = s.ZapiszGrupe(id, g) });
        api.MapDelete("/grupy/{id:long}", (long id) => { s.UsunGrupe(id); return Results.Ok(); });
        api.MapPost("/grupy/{id:long}/zgoda", (long id, ZgodaDto z) => new { zmieniono = s.ZgodaGrupy(id, z) });

        api.MapPost("/import/csv", async (HttpRequest req) =>
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            return s.ImportCsv(ms.ToArray());
        });
        api.MapPost("/import/wzorce", (ImportWzorcowZadanie p) => { s.ImportujWzorce(p); return Results.Ok(); });

        api.MapGet("/ustawienia", () => s.Ustawienia);
        api.MapPut("/ustawienia", (Ustawienia u) => { s.ZapiszUstawienia(u); return s.Ustawienia; });

        api.MapPost("/zakoncz", (IHostApplicationLifetime zycie) =>
        {
            _ = Task.Delay(300).ContinueWith(_ => zycie.StopApplication());
            return Results.Ok();
        });
    }

    static IResult Jpeg(byte[]? bajty) =>
        bajty is null ? Results.NotFound() : Results.File(bajty, "image/jpeg");
}
