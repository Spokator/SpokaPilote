using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Alerts;

/// <summary>Serveur d'envoi des e-mails d'alerte.</summary>
public sealed class SmtpSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    /// <summary>« starttls » (port 587, recommandé), « ssl » (TLS implicite, port 465) ou « none » (relais interne, port 25).</summary>
    public string Security { get; set; } = "starttls";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }
    public List<string> To { get; set; } = new();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From) && To.Count > 0;
}

/// <summary>Webhook : Microsoft Teams (flux Workflows), Discord, Slack ou JSON générique.</summary>
public sealed class WebhookSettings
{
    /// <summary>Identifiant stable (permet de modifier un webhook sans renvoyer son adresse secrète).</summary>
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "";
    /// <summary>« teams », « discord », « slack » ou « json ».</summary>
    public string Format { get; set; } = "json";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
}

/// <summary>Déclencheurs d'alerte activés.</summary>
public sealed class AlertTriggers
{
    public bool MachineToProcess { get; set; } = true;
    public bool InstallFailed { get; set; } = true;
    public bool JobFailed { get; set; } = true;
    public bool MachineStale { get; set; } = true;
    public bool DangerousDriver { get; set; } = true;
    /// <summary>Version de pilote suspendue automatiquement par le disjoncteur de la diffusion par anneaux (4.0).</summary>
    public bool RolloutSuspended { get; set; } = true;
}

/// <summary>Réglages des alertes : enregistrés dans la base (page Paramètres) ; une variable d'environnement définie est prioritaire.</summary>
public sealed class AlertSettings
{
    public SmtpSettings Smtp { get; set; } = new();
    public List<WebhookSettings> Webhooks { get; set; } = new();
    public AlertTriggers Triggers { get; set; } = new();
    /// <summary>Alerte « poste non vu » après ce nombre de jours sans rapport (0 = jamais).</summary>
    public int StaleDays { get; set; } = 7;
    /// <summary>Heure du résumé quotidien (« 08:00 », heure du serveur), null = pas de résumé.</summary>
    public string? DailySummaryTime { get; set; }
    /// <summary>Adresse publique de la console, pour les liens des alertes (ex. https://spokapilote.mon-entreprise.fr).</summary>
    public string? ConsoleUrl { get; set; }

    public bool HasChannel => Smtp.IsConfigured || Webhooks.Any(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Url));

    public AlertSettings Clone() => JsonSerializer.Deserialize<AlertSettings>(JsonSerializer.Serialize(this, ServerStore.Json), ServerStore.Json)!;
}

/// <summary>Modification envoyée par la console. Secrets : null = inchangé, chaîne vide = effacé.</summary>
public sealed class AlertSettingsUpdate
{
    public SmtpUpdate? Smtp { get; set; }
    public List<WebhookUpdate>? Webhooks { get; set; }
    public AlertTriggers? Triggers { get; set; }
    public int? StaleDays { get; set; }
    public string? DailySummaryTime { get; set; }
    public string? ConsoleUrl { get; set; }

    public sealed class SmtpUpdate
    {
        public string? Host { get; set; }
        public int? Port { get; set; }
        public string? Security { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? From { get; set; }
        public List<string>? To { get; set; }
    }

    public sealed class WebhookUpdate
    {
        /// <summary>Webhook existant (adresse conservée si Url est null) ; null = nouveau webhook.</summary>
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Format { get; set; }
        public string? Url { get; set; }
        public bool Enabled { get; set; } = true;
    }
}

public sealed class AlertSettingsException(string message) : Exception(message);

/// <summary>
/// Lecture et enregistrement des réglages d'alerte. Les variables d'environnement (SPOKAPILOTE_SMTP_*, SPOKAPILOTE_WEBHOOK_*,
/// SPOKAPILOTE_DAILY_SUMMARY, SPOKAPILOTE_PUBLIC_URL, SPOKAPILOTE_ALERT_STALE_DAYS) priment sur la console : les champs
/// correspondants y sont affichés comme verrouillés. Les secrets (mot de passe SMTP, adresses des webhooks, qui contiennent
/// un jeton) ne sont jamais renvoyés à la console.
/// </summary>
public sealed class AlertSettingsService(ServerStore store, IConfiguration config, ServerOptions options)
{
    public static readonly string[] Formats = ["teams", "discord", "slack", "json"];
    public static readonly string[] SecurityModes = ["starttls", "ssl", "none"];
    public const string EnvWebhookId = "env";
    private const int MaxWebhooks = 10;

    private string? Env(string name) => config[name] is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    /// <summary>Champs fixés par variable d'environnement (non modifiables dans la console).</summary>
    public HashSet<string> LockedFields()
    {
        var locked = new HashSet<string>();
        foreach (var (field, variable) in new[]
                 {
                     ("smtp.host", "SPOKAPILOTE_SMTP_HOST"), ("smtp.port", "SPOKAPILOTE_SMTP_PORT"), ("smtp.security", "SPOKAPILOTE_SMTP_SECURITY"),
                     ("smtp.username", "SPOKAPILOTE_SMTP_USERNAME"), ("smtp.password", "SPOKAPILOTE_SMTP_PASSWORD"),
                     ("smtp.from", "SPOKAPILOTE_SMTP_FROM"), ("smtp.to", "SPOKAPILOTE_SMTP_TO"),
                     ("dailySummaryTime", "SPOKAPILOTE_DAILY_SUMMARY"), ("consoleUrl", "SPOKAPILOTE_PUBLIC_URL"),
                     ("staleDays", "SPOKAPILOTE_ALERT_STALE_DAYS"),
                 })
            if (Env(variable) is not null) locked.Add(field);
        return locked;
    }

    /// <summary>Réglages enregistrés dans la base (sans les variables d'environnement).</summary>
    private AlertSettings Stored()
    {
        var doc = store.GetDocument(ServerStore.AlertSettingsKey);
        if (doc is null) return new AlertSettings { StaleDays = options.StaleDays };
        try
        {
            return JsonSerializer.Deserialize<AlertSettings>(doc.Json, ServerStore.Json) ?? new AlertSettings { StaleDays = options.StaleDays };
        }
        catch (JsonException)
        {
            return new AlertSettings { StaleDays = options.StaleDays };
        }
    }

    /// <summary>Réglages effectifs : base + variables d'environnement prioritaires.</summary>
    public AlertSettings Current()
    {
        var s = Stored();
        var smtp = s.Smtp;
        if (Env("SPOKAPILOTE_SMTP_HOST") is { } host) smtp.Host = host;
        if (int.TryParse(Env("SPOKAPILOTE_SMTP_PORT"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536) smtp.Port = port;
        if (Env("SPOKAPILOTE_SMTP_SECURITY")?.ToLowerInvariant() is { } sec && SecurityModes.Contains(sec)) smtp.Security = sec;
        if (Env("SPOKAPILOTE_SMTP_USERNAME") is { } user) smtp.Username = user;
        if (Env("SPOKAPILOTE_SMTP_PASSWORD") is { } pass) smtp.Password = pass;
        if (Env("SPOKAPILOTE_SMTP_FROM") is { } from) smtp.From = from;
        if (Env("SPOKAPILOTE_SMTP_TO") is { } to) smtp.To = SplitAddresses(to);
        if (Env("SPOKAPILOTE_WEBHOOK_URL") is { } url)
        {
            var format = Env("SPOKAPILOTE_WEBHOOK_FORMAT")?.ToLowerInvariant();
            s.Webhooks.RemoveAll(w => w.Id == EnvWebhookId);
            s.Webhooks.Insert(0, new WebhookSettings
            {
                Id = EnvWebhookId,
                Name = "Variable d'environnement",
                Format = format is not null && Formats.Contains(format) ? format : "json",
                Url = url,
            });
        }
        if (Env("SPOKAPILOTE_DAILY_SUMMARY") is { } daily) s.DailySummaryTime = ParseTime(daily) is null ? null : daily;
        if (Env("SPOKAPILOTE_PUBLIC_URL") is { } publicUrl) s.ConsoleUrl = publicUrl.TrimEnd('/');
        if (int.TryParse(Env("SPOKAPILOTE_ALERT_STALE_DAYS"), NumberStyles.None, CultureInfo.InvariantCulture, out var stale)) s.StaleDays = Math.Clamp(stale, 0, 3650);
        return s;
    }

    /// <summary>Vue destinée à la console : aucun secret, seulement s'il est renseigné.</summary>
    public object View()
    {
        var s = Current();
        return new
        {
            smtp = new
            {
                host = s.Smtp.Host,
                port = s.Smtp.Port,
                security = s.Smtp.Security,
                username = s.Smtp.Username,
                passwordSet = !string.IsNullOrEmpty(s.Smtp.Password),
                from = s.Smtp.From,
                to = s.Smtp.To,
                configured = s.Smtp.IsConfigured,
            },
            webhooks = s.Webhooks.Select(w => new
            {
                id = w.Id,
                name = w.Name,
                format = w.Format,
                url = MaskUrl(w.Url),
                insecure = w.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
                enabled = w.Enabled,
                locked = w.Id == EnvWebhookId,
            }),
            triggers = s.Triggers,
            staleDays = s.StaleDays,
            dailySummaryTime = s.DailySummaryTime,
            consoleUrl = s.ConsoleUrl,
            locked = LockedFields().Order(),
            hasChannel = s.HasChannel,
        };
    }

    /// <summary>Applique une modification de la console (validée) ; les champs verrouillés par variable d'environnement sont ignorés.</summary>
    public void Update(AlertSettingsUpdate u, DateTimeOffset now)
    {
        var s = Stored();
        var locked = LockedFields();
        if (u.Smtp is { } smtp)
        {
            if (!locked.Contains("smtp.host")) s.Smtp.Host = Clean(smtp.Host);
            if (!locked.Contains("smtp.port") && smtp.Port is { } port)
                s.Smtp.Port = port is > 0 and < 65536 ? port : throw new AlertSettingsException("Port SMTP invalide (1 à 65535).");
            if (!locked.Contains("smtp.security") && smtp.Security is { } sec)
                s.Smtp.Security = SecurityModes.Contains(sec.Trim().ToLowerInvariant()) ? sec.Trim().ToLowerInvariant()
                    : throw new AlertSettingsException("Sécurité SMTP : starttls, ssl ou none.");
            if (!locked.Contains("smtp.username")) s.Smtp.Username = Clean(smtp.Username);
            if (!locked.Contains("smtp.password") && smtp.Password is not null) s.Smtp.Password = smtp.Password.Length == 0 ? null : smtp.Password;
            if (!locked.Contains("smtp.from")) s.Smtp.From = Clean(smtp.From);
            if (!locked.Contains("smtp.to") && smtp.To is not null) s.Smtp.To = smtp.To.SelectMany(SplitAddresses).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (s.Smtp.Host is { } h && (h.Length > 253 || h.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':' or '[' or ']'))))
                throw new AlertSettingsException($"Serveur SMTP invalide : « {h} ».");
            if (s.Smtp.From is { } f && !IsEmail(f)) throw new AlertSettingsException($"Adresse d'expéditeur invalide : « {f} ».");
            foreach (var to in s.Smtp.To.Where(t => !IsEmail(t))) throw new AlertSettingsException($"Adresse de destinataire invalide : « {to} ».");
            if (s.Smtp.Host is not null && (s.Smtp.From is null || s.Smtp.To.Count == 0))
                throw new AlertSettingsException("E-mail : renseignez l'expéditeur et au moins un destinataire.");
        }

        if (u.Webhooks is { } hooks)
        {
            if (hooks.Count > MaxWebhooks) throw new AlertSettingsException($"{MaxWebhooks} webhooks au plus.");
            var previous = s.Webhooks.ToDictionary(w => w.Id);
            var list = new List<WebhookSettings>();
            foreach (var h in hooks.Where(h => h.Id != EnvWebhookId))
            {
                var format = h.Format?.Trim().ToLowerInvariant() ?? "json";
                if (!Formats.Contains(format)) throw new AlertSettingsException($"Format de webhook inconnu : « {h.Format} » (teams, discord, slack ou json).");
                string url;
                if (!string.IsNullOrWhiteSpace(h.Url)) url = ValidateWebhookUrl(h.Url.Trim());
                else if (h.Id is not null && previous.TryGetValue(h.Id, out var old)) url = old.Url;
                else throw new AlertSettingsException("Adresse du webhook manquante.");
                list.Add(new WebhookSettings
                {
                    Id = h.Id is not null && previous.ContainsKey(h.Id) ? h.Id : WebhookSettings.NewId(),
                    Name = (Clean(h.Name) ?? format)[..Math.Min((Clean(h.Name) ?? format).Length, 60)],
                    Format = format,
                    Url = url,
                    Enabled = h.Enabled,
                });
            }
            s.Webhooks = list;
        }

        if (u.Triggers is not null) s.Triggers = u.Triggers;
        if (u.StaleDays is { } days && !locked.Contains("staleDays"))
            s.StaleDays = days is >= 0 and <= 3650 ? days : throw new AlertSettingsException("Délai « non vu » : 0 à 3650 jours.");
        if (!locked.Contains("dailySummaryTime"))
        {
            var t = Clean(u.DailySummaryTime);
            if (t is not null && ParseTime(t) is null) throw new AlertSettingsException($"Heure du résumé invalide : « {t} » (format 08:00).");
            s.DailySummaryTime = t;
        }
        if (!locked.Contains("consoleUrl"))
        {
            var c = Clean(u.ConsoleUrl)?.TrimEnd('/');
            if (c is not null && !(Uri.TryCreate(c, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.Query)))
                throw new AlertSettingsException($"Adresse de la console invalide : « {c} ».");
            s.ConsoleUrl = c;
        }
        s.Webhooks.RemoveAll(w => w.Id == EnvWebhookId);
        store.SetDocument(ServerStore.AlertSettingsKey, JsonSerializer.Serialize(s, ServerStore.Json), now);
    }

    public static string ValidateWebhookUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new AlertSettingsException("Adresse de webhook invalide : https://… attendu.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new AlertSettingsException("L'adresse du webhook ne doit pas contenir d'identifiants.");
        if (url.Length > 2000) throw new AlertSettingsException("Adresse de webhook trop longue.");
        return url;
    }

    /// <summary>« https://discord.com/api/webhooks/123/secret » → « https://discord.com/… » (le jeton n'est jamais réaffiché).</summary>
    public static string MaskUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}/…" : "…";

    public static TimeSpan? ParseTime(string? value) =>
        TimeSpan.TryParseExact(value?.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;

    private static List<string> SplitAddresses(string value) =>
        value.Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool IsEmail(string value) => MailAddress.TryCreate(value, out var a) && a.Address == value && value.Contains('@');

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
