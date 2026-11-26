namespace SpokaPilote.Server.Jobs;

/// <summary>Paramètres d'une tâche à distance : uniquement des options connues, jamais de commande libre.</summary>
/// <param name="DryRun">Simulation : le poste montre ce qu'il ferait sans rien modifier (installation, imprimantes, annulation, mise à jour).</param>
public sealed record JobParameters(bool DryRun = false);

/// <summary>Une action autorisée : identifiant (contrat avec les postes), libellés et options acceptées.</summary>
/// <param name="ChangesSystem">L'action modifie le poste (une confirmation explicite est demandée dans la console).</param>
public sealed record JobType(string Id, string Label, string Description, bool ChangesSystem, bool SupportsDryRun);

/// <summary>
/// Liste blanche des tâches à distance. Le serveur refuse tout autre type, et le poste (spokapilote agent run) applique
/// sa propre liste blanche : aucune commande ni script arbitraire ne peut transiter par la console.
/// </summary>
public static class JobTypes
{
    public const string Scan = "scan";
    public const string InstallMissing = "install-missing";
    public const string InstallUpdates = "install-updates";
    public const string DeployPrinters = "deploy-printers";
    public const string RollbackLast = "rollback-last";
    public const string SelfUpdate = "self-update";

    public static readonly IReadOnlyList<JobType> All =
    [
        new(Scan, "Analyser le poste",
            "Inventaire du matériel et recherche des pilotes manquants ou à mettre à jour. Ne modifie rien.", false, false),
        new(InstallMissing, "Installer les pilotes manquants",
            "Installe uniquement les pilotes des périphériques en erreur, après un point de restauration et une sauvegarde des pilotes.", true, true),
        new(InstallUpdates, "Installer pilotes manquants et mises à jour",
            "Installe les pilotes manquants et les mises à jour proposées (hors pilotes bloqués par la politique), avec point de restauration et sauvegarde.", true, true),
        new(DeployPrinters, "Déployer le profil d'imprimantes",
            "Applique le profil d'imprimantes publié sur la console (création ou mise à jour des files d'impression).", true, true),
        new(RollbackLast, "Annuler la dernière intervention",
            "Retire les pilotes installés lors de la dernière intervention : les périphériques reviennent à leur pilote précédent.", true, true),
        new(SelfUpdate, "Mettre à jour SpokaPilote",
            "Télécharge la dernière version officielle (vérifiée) et l'installe silencieusement.", true, true),
    ];

    public static JobType? Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>États d'une tâche à distance.</summary>
public static class JobStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";

    public static readonly string[] Active = [Pending, Running];
    public static readonly string[] Finished = [Succeeded, Failed, Expired, Cancelled];

    /// <summary>Filtres de la vue « Tâches » : un état, un groupe (active, finished) ou tout (null).</summary>
    public static string[]? Filter(string? filter) => filter?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => null,
        "active" => Active,
        "finished" => Finished,
        Pending or Running or Succeeded or Failed or Expired or Cancelled => [filter.Trim().ToLowerInvariant()],
        _ => [],
    };
}
