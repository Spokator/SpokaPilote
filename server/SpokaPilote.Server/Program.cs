using Microsoft.AspNetCore.Server.Kestrel.Core;
using SpokaPilote.Server;
using SpokaPilote.Server.Alerts;
using SpokaPilote.Server.Api;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Cache;
using SpokaPilote.Server.Jobs;
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
// Console 3.1 : comptes nominatifs (sessions), journal d'audit, planificateur des actions planifiées.
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<ScheduleRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScheduleRunner>());
// Alertes (e-mail, webhooks) et tâches périodiques : expiration des tâches à distance, postes non vus, résumé quotidien.
builder.Services.AddSpokaPiloteAlerts();
// Grands parcs (3.2) : contrôle d'admission des postes (503 + Retry-After en cas d'afflux), mesures de l'API,
// sauvegardes à chaud et entretien de la base en arrière-plan (compression de l'historique hérité, rétention, ANALYZE).
builder.Services.AddSingleton<AdmissionControl>();
// Diffusion par anneaux et suspension automatique (4.0) : politiques effectives par anneau, disjoncteur.
builder.Services.AddSingleton<SpokaPilote.Server.Rollout.RolloutService>();
builder.Services.AddSingleton<RequestMetrics>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<StorageMaintenance>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StorageMaintenance>());
// ---- Cache de pilotes et de catalogues (4.0) : fichiers officiels et catalogues servis aux postes depuis /data/cache.
builder.Services.AddSpokaPiloteDriverCache();
// ---- fin du cache de pilotes
// Les accès à SQLite sont synchrones : assez de fils prêts d'emblée pour absorber un afflux sans attendre la croissance
// progressive du pool (les requêtes des postes restent bornées par le contrôle d'admission).
ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
ThreadPool.SetMinThreads(Math.Max(minWorkers, 64), minIo);
// Limite globale de Kestrel (la limite fine, après décompression, est appliquée par BodyReader).
builder.Services.AddOptions<KestrelServerOptions>().Configure<ServerOptions>((k, o) => k.Limits.MaxRequestBodySize = o.MaxReportBytes + 64 * 1024);

var app = builder.Build();

// Initialisation au démarrage : base, jetons (générés et journalisés si absents) — une erreur ici arrête le serveur.
var options = app.Services.GetRequiredService<ServerOptions>();
var store = app.Services.GetRequiredService<ServerStore>();
app.Services.GetRequiredService<TokenService>();
// Diffusion par anneaux : plafond des politiques servies aux postes 3.x (et 4.0 sans décisions).
app.Services.GetRequiredService<SpokaPilote.Server.Rollout.RolloutService>().Limits =
    new SpokaPilote.Server.Rollout.RolloutLimits(options.RolloutMaxRules, options.RolloutMaxKb * 1024);
app.Logger.LogInformation("SpokaPilote Server {Version} — données : {DataDirectory}, historique : {History} rapports par poste.",
    ApiEndpoints.Version, options.DataDirectory, options.HistoryPerMachine);
if (store.OpenedSchemaVersion is > 0 and < ServerStore.SchemaVersion)
    app.Logger.LogInformation("Base migrée du schéma {From} au schéma {To} en {Seconds:0.0} s.", store.OpenedSchemaVersion, ServerStore.SchemaVersion,
        store.MigrationDuration.TotalSeconds);

app.Use(app.Services.GetRequiredService<RequestMetrics>().InvokeAsync);

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
// ---- Cache de pilotes (4.0) : /api/cache/*, /api/settings/cache
app.MapDriverCacheApi();
// ---- fin du cache de pilotes
app.MapFallback("/api/{**path}", () => Results.Json(new ApiError("Route d'API inconnue."), statusCode: StatusCodes.Status404NotFound));

app.Run();

/// <summary>Point d'entrée (partial pour les tests d'intégration WebApplicationFactory).</summary>
public partial class Program;
