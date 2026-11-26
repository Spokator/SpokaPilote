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

    // ------------------------------------------------------------------ grands parcs (3.2)

    /// <summary>Historique : rapports plus anciens que ce nombre de jours supprimés (le dernier de chaque poste est gardé ; 0 = sans limite d'âge).</summary>
    public int HistoryDays { get; init; }
    /// <summary>Rapports traités en même temps ; au-delà ils attendent leur tour (file), puis sont refusés (503 + Retry-After).</summary>
    public int MaxConcurrentUploads { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 16);
    /// <summary>Rapports en attente au plus (au-delà : 503 immédiat avec Retry-After).</summary>
    public int UploadQueueLimit { get; init; } = 200;
    /// <summary>Interrogations d'agents traitées en même temps / en attente au plus.</summary>
    public int MaxConcurrentAgentRequests { get; init; } = 32;
    public int AgentQueueLimit { get; init; } = 1000;
    /// <summary>Attente maximale dans la file avant un refus 503.</summary>
    public TimeSpan QueueTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Délai conseillé aux postes refusés (Retry-After), étalé aléatoirement entre la moitié et une fois et demie.</summary>
    public int RetryAfterSeconds { get; init; } = 60;
    /// <summary>Sauvegardes conservées dans /data/backups (les plus récentes).</summary>
    public int BackupKeep { get; init; } = 7;
    /// <summary>Heure de la sauvegarde automatique quotidienne (« 03:00 », fuseau du serveur) ; vide = pas de sauvegarde automatique.</summary>
    public string? BackupTime { get; init; }

    // ------------------------------------------------------------------ cache de pilotes (4.0)

    /// <summary>Dossier du cache de pilotes et de catalogues (par défaut /data/cache).</summary>
    public string? CacheDirectoryOverride { get; init; }
    /// <summary>Téléchargements simultanés au plus depuis les sources officielles pour le cache (au-delà : le poste télécharge directement).</summary>
    public int CacheMaxDownloads { get; init; } = 8;
    public string CacheDirectory => CacheDirectoryOverride ?? Path.Combine(DataDirectory, "cache");

    // ------------------------------------------------------------------ diffusion par anneaux (4.0)

    /// <summary>Règles de diffusion au plus dans la politique d'un poste 3.x (ou 4.0 sans décisions) : au-delà, forme compacte.</summary>
    public int RolloutMaxRules { get; init; } = 2000;
    /// <summary>Taille maximale de cette politique, en Ko (les postes refusent une politique de plus de 2 Mo).</summary>
    public int RolloutMaxKb { get; init; } = 1024;

    public string DatabasePath => Path.Combine(DataDirectory, "spokapilote.db");
    public string BackupDirectory => Path.Combine(DataDirectory, "backups");

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
            HistoryDays = Int(config, "SPOKAPILOTE_HISTORY_DAYS", 0, 0, 3650),
            MaxConcurrentUploads = Int(config, "SPOKAPILOTE_MAX_CONCURRENT_UPLOADS", Math.Clamp(Environment.ProcessorCount, 2, 16), 1, 256),
            UploadQueueLimit = Int(config, "SPOKAPILOTE_UPLOAD_QUEUE", 200, 0, 100_000),
            MaxConcurrentAgentRequests = Int(config, "SPOKAPILOTE_MAX_CONCURRENT_AGENTS", 32, 1, 1024),
            AgentQueueLimit = Int(config, "SPOKAPILOTE_AGENT_QUEUE", 1000, 0, 100_000),
            RetryAfterSeconds = Int(config, "SPOKAPILOTE_RETRY_AFTER_SECONDS", 60, 5, 3600),
            BackupKeep = Int(config, "SPOKAPILOTE_BACKUP_KEEP", 7, 1, 365),
            BackupTime = string.IsNullOrWhiteSpace(config["SPOKAPILOTE_BACKUP_TIME"]) ? null : config["SPOKAPILOTE_BACKUP_TIME"]!.Trim(),
            CacheDirectoryOverride = string.IsNullOrWhiteSpace(config["SPOKAPILOTE_CACHE_DIR"]) ? null : Path.GetFullPath(config["SPOKAPILOTE_CACHE_DIR"]!.Trim()),
            CacheMaxDownloads = Int(config, "SPOKAPILOTE_CACHE_MAX_DOWNLOADS", 8, 1, 256),
            RolloutMaxRules = Int(config, "SPOKAPILOTE_ROLLOUT_MAX_RULES", 2000, 50, 20_000),
            RolloutMaxKb = Int(config, "SPOKAPILOTE_ROLLOUT_MAX_KB", 1024, 64, 1900),
        };
    }
}
