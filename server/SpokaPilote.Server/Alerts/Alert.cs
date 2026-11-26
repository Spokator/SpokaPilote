namespace SpokaPilote.Server.Alerts;

/// <summary>Motifs d'alerte (servent aussi de clé à l'anti-spam).</summary>
public static class AlertReasons
{
    public const string MachineToProcess = "to-process";
    public const string InstallFailed = "install-failed";
    public const string JobFailed = "job-failed";
    public const string MachineStale = "stale";
    public const string DangerousDriver = "dangerous-driver";
    public const string DailySummary = "daily-summary";
    public const string RolloutSuspended = "rollout-suspended";
    public const string Test = "test";

    public static string Label(string reason) => reason switch
    {
        MachineToProcess => "Poste à traiter",
        InstallFailed => "Échec d'installation",
        JobFailed => "Tâche à distance échouée",
        MachineStale => "Poste non vu",
        DangerousDriver => "Pilote dangereux détecté",
        DailySummary => "Résumé quotidien",
        RolloutSuspended => "Pilote suspendu automatiquement",
        Test => "Alerte de test",
        _ => reason,
    };
}

/// <summary>Une information affichée sous forme « libellé : valeur » (poste, modèle, tâche...).</summary>
public sealed record AlertFact(string Label, string Value);

/// <summary>Alerte prête à être envoyée par e-mail et webhooks.</summary>
/// <param name="Severity">« critical », « warning » ou « info » (couleur des messages).</param>
/// <param name="Link">Lien vers la console (null si l'adresse publique n'est pas renseignée).</param>
public sealed record Alert(
    string Reason,
    string Severity,
    string Title,
    string Message,
    string? Machine,
    IReadOnlyList<AlertFact> Facts,
    string? Link,
    DateTimeOffset Timestamp);

/// <summary>Résultat d'un envoi sur un canal (e-mail ou un webhook).</summary>
public sealed record ChannelResult(string Channel, bool Success, string Message);
