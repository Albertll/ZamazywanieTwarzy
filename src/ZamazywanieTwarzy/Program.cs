using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ZamazywanieTwarzy;

// Lokalny serwer: nasłuchuje tylko na 127.0.0.1, a dostęp daje losowy token z adresu otwieranego w przeglądarce.
// Bez tokenu dowolna strona otwarta w tej samej przeglądarce mogłaby wysyłać zapytania do serwera.

string katalogAplikacji = AppContext.BaseDirectory;
string katalogDanych = Path.Combine(katalogAplikacji, "dane");
int port = 0;
bool otworzPrzegladarke = true;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dane" when i + 1 < args.Length: katalogDanych = Path.GetFullPath(args[++i]); break;
        case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
        case "--bez-przegladarki": otworzPrzegladarke = false; break;
    }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [],
    ContentRootPath = katalogAplikacji,
    WebRootPath = Path.Combine(katalogAplikacji, "wwwroot"),
});
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("Zamazywanie twarzy — ładowanie modeli...");
using var silnik = new Silnik(Path.Combine(katalogAplikacji, "modele"), katalogDanych);
var app = builder.Build();

string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
if (Environment.GetEnvironmentVariable("ZAMAZYWANIE_TOKEN") is { Length: >= 16 } staly) token = staly;

app.Use(async (ctx, next) =>
{
    // Nagłówek Host chroni przed DNS rebinding (obca domena wskazująca na 127.0.0.1).
    if (ctx.Request.Host.Host is not ("127.0.0.1" or "localhost"))
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    if (ctx.Request.Query.TryGetValue("t", out var t) && t == token)
    {
        ctx.Response.Cookies.Append("zt", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
        ctx.Response.Redirect("/");
        return;
    }
    if (ctx.Request.Cookies["zt"] != token)
    {
        ctx.Response.StatusCode = 403;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync("Brak dostępu. Otwórz adres wypisany w oknie programu (zawiera jednorazowy klucz).");
        return;
    }
    // Zmiany tylko z naszego interfejsu: własny nagłówek wymusza zapytanie wstępne CORS, którego obca strona nie przejdzie.
    if (!HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers["X-Zamazywanie"] != "1")
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    ctx.Response.Headers.CacheControl = "no-store";
    await next();
});

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (BladUzytkownika ex)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { blad = ex.Message });
    }
    catch (Exception ex) when (!ctx.Response.HasStarted)
    {
        Console.Error.WriteLine(ex);
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { blad = "Błąd programu: " + ex.Message });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
Api.Mapuj(app, silnik);

await app.StartAsync();
var adres = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
var url = $"{adres}/?t={token}";
Console.WriteLine();
Console.WriteLine($"Program działa. Jeśli przeglądarka nie otworzyła się sama, wejdź na:\n  {url}");
Console.WriteLine($"Dane (baza osób, miniatury): {katalogDanych}");
Console.WriteLine("Zamknięcie tego okna wyłącza program.");
if (otworzPrzegladarke)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch (Exception) { }
}
await app.WaitForShutdownAsync();
