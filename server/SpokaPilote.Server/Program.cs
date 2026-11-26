using Microsoft.AspNetCore.Server.Kestrel.Core;
using SpokaPilote.Server;
using SpokaPilote.Server.Alerts;
using SpokaPilote.Server.Api;
using SpokaPilote.Server.Security;
using SpokaPilote.Server.Storage;

// SpokaPilote Server : console centrale auto-hébergée. Les postes y envoient leurs rapports d'analyse
// (« spokapilote scan » avec ServerUrl configuré) ; l'IT suit le parc depuis la console web servie par ce même processus.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(sp => ServerOptions.FromConfiguration(
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>().ContentRootPath));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new ServerStore(sp.GetRequiredService<ServerOptions>()));
builder.Services.AddSingleton(sp => TokenService.Initialize(
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ServerOptions>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("SpokaPilote.Jetons")));
builder.Services.AddSingleton<AuthFailureLimiter>();
// Alertes (e-mail, webhooks) et tâches périodiques : expiration des tâches à distance, postes non vus, résumé quotidien.
builder.Services.AddSpokaPiloteAlerts();
// Limite globale de Kestrel (la limite fine, après décompression, est appliquée par BodyReader).
builder.Services.AddOptions<KestrelServerOptions>().Configure<ServerOptions>((k, o) => k.Limits.MaxRequestBodySize = o.MaxReportBytes + 64 * 1024);

var app = builder.Build();

// Initialisation au démarrage : base, jetons (générés et journalisés si absents) — une erreur ici arrête le serveur.
var options = app.Services.GetRequiredService<ServerOptions>();
app.Services.GetRequiredService<ServerStore>();
app.Services.GetRequiredService<TokenService>();
app.Logger.LogInformation("SpokaPilote Server {Version} — données : {DataDirectory}, historique : {History} rapports par poste.",
    ApiEndpoints.Version, options.DataDirectory, options.HistoryPerMachine);

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    if (context.Request.Path.StartsWithSegments("/api")) headers.CacheControl = "no-store";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // La console est petite et évolue avec le serveur : toujours revalider.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapSpokaPiloteApi();
app.MapFallback("/api/{**path}", () => Results.Json(new ApiError("Route d'API inconnue."), statusCode: StatusCodes.Status404NotFound));

app.Run();

/// <summary>Point d'entrée (partial pour les tests d'intégration WebApplicationFactory).</summary>
public partial class Program;
