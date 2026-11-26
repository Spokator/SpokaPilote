using System.Globalization;

namespace SpokaPilote.Server;

/// <summary>Réglages du serveur, lus dans les variables d'environnement (ou appsettings.json).</summary>
public sealed class ServerOptions
{
    /// <summary>Dossier des données persistantes : base SQLite et jetons générés (volume /data dans le conteneur).</summary>
    public required string DataDirectory { get; init; }
    /// <summary>Nombre de rapports conservés par poste (les plus récents).</summary>
    public int HistoryPerMachine { get; init; } = 20;
    /// <summary>Taille maximale d'un rapport (après décompression).</summary>
    public long MaxReportBytes { get; init; } = 5 * 1024 * 1024;
    /// <summary>Taille maximale d'une politique ou d'un profil d'imprimantes.</summary>
    public long MaxDocumentBytes { get; init; } = 1024 * 1024;
    /// <summary>Un poste sans rapport depuis ce nombre de jours est signalé « non vu ».</summary>
    public int StaleDays { get; init; } = 7;
    /// <summary>Une tâche à distance jamais récupérée par son poste expire après ce nombre de jours.</summary>
    public int JobExpiryDays { get; init; } = 7;
    /// <summary>Une tâche « en cours » sans résultat après ce nombre d'heures est considérée comme bloquée (expirée).</summary>
    public int JobRunningTimeoutHours { get; init; } = 6;
    /// <summary>Tentatives d'authentification refusées tolérées par adresse IP et par minute (0 = pas de limite).</summary>
    public int AuthFailuresPerMinute { get; init; } = 10;
    /// <summary>Session de la console fermée après ce nombre d'heures d'inactivité (expiration glissante).</summary>
    public int SessionIdleHours { get; init; } = 8;
    /// <summary>Journal d'audit : nombre maximal d'entrées conservées (les plus anciennes sont supprimées).</summary>
    public int AuditMaxEntries { get; init; } = 5000;
    /// <summary>Journal d'audit : ancienneté maximale des entrées, en jours.</summary>
    public int AuditRetentionDays { get; init; } = 180;

    public string DatabasePath => Path.Combine(DataDirectory, "spokapilote.db");

    public static ServerOptions FromConfiguration(IConfiguration config, string contentRoot)
    {
        static int Int(IConfiguration c, string key, int fallback, int min, int max) =>
            int.TryParse(c[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : fallback;

        var dataDir = config["SPOKAPILOTE_DATA_DIR"];
        return new ServerOptions
        {
            DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(dataDir) ? Path.Combine(contentRoot, "data") : dataDir),
            HistoryPerMachine = Int(config, "SPOKAPILOTE_HISTORY", 20, 1, 1000),
            MaxReportBytes = Int(config, "SPOKAPILOTE_MAX_REPORT_MB", 5, 1, 100) * 1024L * 1024L,
            StaleDays = Int(config, "SPOKAPILOTE_STALE_DAYS", 7, 0, 3650),
            JobExpiryDays = Int(config, "SPOKAPILOTE_JOB_EXPIRY_DAYS", 7, 1, 365),
            JobRunningTimeoutHours = Int(config, "SPOKAPILOTE_JOB_TIMEOUT_HOURS", 6, 1, 168),
            AuthFailuresPerMinute = Int(config, "SPOKAPILOTE_AUTH_FAILURES_PER_MINUTE", 10, 0, 10000),
            SessionIdleHours = Int(config, "SPOKAPILOTE_SESSION_HOURS", 8, 1, 168),
            AuditMaxEntries = Int(config, "SPOKAPILOTE_AUDIT_MAX_ENTRIES", 5000, 100, 1_000_000),
            AuditRetentionDays = Int(config, "SPOKAPILOTE_AUDIT_DAYS", 180, 1, 3650),
        };
    }
}
