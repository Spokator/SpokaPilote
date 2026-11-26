using SpokaPilote.Server.Security;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Audit;

/// <summary>Codes des actions consignées dans le journal d'audit (contrat de l'API et de l'export CSV).</summary>
public static class AuditActions
{
    public const string LoginSuccess = "login.success";
    public const string LoginFailure = "login.failure";
    public const string Logout = "login.logout";
    public const string AccountCreate = "account.create";
    public const string AccountUpdate = "account.update";
    public const string AccountDelete = "account.delete";
    public const string AccountPassword = "account.password";
    public const string AccountOwnPassword = "account.own-password";
    public const string JobCreate = "job.create";
    public const string JobCancel = "job.cancel";
    public const string ScheduleCreate = "schedule.create";
    public const string ScheduleUpdate = "schedule.update";
    public const string ScheduleDelete = "schedule.delete";
    public const string ScheduleRun = "schedule.run";
    public const string GroupCreate = "group.create";
    public const string GroupUpdate = "group.update";
    public const string GroupDelete = "group.delete";
    public const string GroupMembers = "group.members";
    public const string SettingsAlerts = "settings.alerts";
    public const string AlertTest = "settings.alert-test";
    public const string PolicyUpdate = "policy.update";
    public const string PolicyDelete = "policy.delete";
    public const string PrinterProfileUpdate = "printer-profile.update";
    public const string PrinterProfileDelete = "printer-profile.delete";
    public const string MachineDelete = "machine.delete";
    public const string AuditExport = "audit.export";
    public const string MaintenanceBackup = "maintenance.backup";
    public const string MaintenanceVacuum = "maintenance.vacuum";
    public const string RolloutSettings = "rollout.settings";
    public const string RolloutRingCreate = "rollout.ring-create";
    public const string RolloutRingUpdate = "rollout.ring-update";
    public const string RolloutRingDelete = "rollout.ring-delete";
    public const string RolloutRingOrder = "rollout.ring-order";
    public const string RolloutSuspend = "rollout.suspend";
    public const string RolloutAutoSuspend = "rollout.auto-suspend";
    public const string RolloutResume = "rollout.resume";
    public const string RolloutAcknowledge = "rollout.acknowledge";
    public const string RolloutApprove = "rollout.approve";
    public const string RolloutUnapprove = "rollout.unapprove";

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [LoginSuccess] = "Connexion",
        [LoginFailure] = "Connexion refusée",
        [Logout] = "Déconnexion",
        [AccountCreate] = "Compte créé",
        [AccountUpdate] = "Compte modifié",
        [AccountDelete] = "Compte supprimé",
        [AccountPassword] = "Mot de passe réinitialisé",
        [AccountOwnPassword] = "Mot de passe changé",
        [JobCreate] = "Action à distance lancée",
        [JobCancel] = "Tâche annulée",
        [ScheduleCreate] = "Planification créée",
        [ScheduleUpdate] = "Planification modifiée",
        [ScheduleDelete] = "Planification supprimée",
        [ScheduleRun] = "Planification exécutée",
        [GroupCreate] = "Groupe créé",
        [GroupUpdate] = "Groupe modifié",
        [GroupDelete] = "Groupe supprimé",
        [GroupMembers] = "Membres du groupe modifiés",
        [SettingsAlerts] = "Paramètres des alertes modifiés",
        [AlertTest] = "Alerte de test envoyée",
        [PolicyUpdate] = "Politique de pilotes enregistrée",
        [PolicyDelete] = "Politique de pilotes supprimée",
        [PrinterProfileUpdate] = "Profil d'imprimantes enregistré",
        [PrinterProfileDelete] = "Profil d'imprimantes supprimé",
        [MachineDelete] = "Poste supprimé",
        [AuditExport] = "Journal d'audit exporté",
        [MaintenanceBackup] = "Sauvegarde de la base",
        [MaintenanceVacuum] = "Compactage de la base (VACUUM)",
        [RolloutSettings] = "Réglages de la diffusion modifiés",
        [RolloutRingCreate] = "Anneau de déploiement créé",
        [RolloutRingUpdate] = "Anneau de déploiement modifié",
        [RolloutRingDelete] = "Anneau de déploiement supprimé",
        [RolloutRingOrder] = "Ordre des anneaux modifié",
        [RolloutSuspend] = "Pilote suspendu",
        [RolloutAutoSuspend] = "Pilote suspendu automatiquement",
        [RolloutResume] = "Diffusion d'un pilote reprise",
        [RolloutAcknowledge] = "Blocage d'un pilote maintenu",
        [RolloutApprove] = "Pilote diffusé partout",
        [RolloutUnapprove] = "Diffusion forcée annulée",
    };

    /// <summary>Catégories du filtre de la console (préfixe du code).</summary>
    public static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>
    {
        ["login"] = "Connexions",
        ["account"] = "Comptes",
        ["job"] = "Actions à distance",
        ["schedule"] = "Planifications",
        ["group"] = "Groupes",
        ["settings"] = "Paramètres et alertes",
        ["policy"] = "Politique de pilotes",
        ["printer-profile"] = "Profil d'imprimantes",
        ["machine"] = "Postes",
        ["audit"] = "Journal d'audit",
        ["maintenance"] = "Sauvegarde et entretien",
        ["rollout"] = "Déploiement (anneaux)",
    };

    public static string Label(string action) => Labels.GetValueOrDefault(action, action);
}

/// <summary>
/// Journal d'audit : qui a fait quoi et quand. L'écriture n'échoue jamais l'opération qu'elle consigne (une erreur est
/// seulement journalisée). Rétention : SPOKAPILOTE_AUDIT_MAX_ENTRIES entrées et SPOKAPILOTE_AUDIT_DAYS jours.
/// </summary>
public sealed class AuditService(ServerStore store, ServerOptions options, TimeProvider clock, ILogger<AuditService> log)
{
    public const string SchedulerActor = "Planificateur";

    /// <summary>Consigne une action faite par l'appelant de la requête (compte, jeton administrateur).</summary>
    public void Record(HttpContext http, string action, string? target = null, string? detail = null, bool success = true)
    {
        var principal = Principal.Of(http);
        Record(principal?.Actor ?? "anonyme", principal?.Role, action, target, detail, http.Connection.RemoteIpAddress?.ToString(), success);
    }

    public void Record(string actor, string? role, string action, string? target, string? detail, string? ip, bool success = true)
    {
        try
        {
            store.AddAudit(clock.GetUtcNow(), actor, role, action, target, detail, ip, success, options.AuditMaxEntries, options.AuditRetentionDays);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Journal d'audit : impossible de consigner « {Action} » ({Actor}).", action, actor);
        }
    }

    /// <summary>Rétention périodique (l'ancienneté s'applique aussi quand plus rien n'est consigné).</summary>
    public int Purge(DateTimeOffset now) => store.PurgeAudit(now, options.AuditMaxEntries, options.AuditRetentionDays);
}
