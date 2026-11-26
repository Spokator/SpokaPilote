using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Security;

/// <summary>Rôles des comptes nominatifs.</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Technician = "technician";
    public const string ReadOnly = "readonly";

    public static readonly IReadOnlyList<string> All = [Admin, Technician, ReadOnly];

    public static bool IsValid(string? role) => role is Admin or Technician or ReadOnly;

    public static string Label(string? role) => role switch
    {
        Admin => "Administrateur",
        Technician => "Technicien",
        ReadOnly => "Lecture seule",
        _ => role ?? "",
    };

    public static string Description(string role) => role switch
    {
        Admin => "Tout : comptes, paramètres, alertes, jetons, politique et profil d'imprimantes, journal d'audit.",
        Technician => "Consultation, actions à distance, planifications et groupes de postes.",
        _ => "Consultation uniquement.",
    };

    public static Access AccessFor(string role) => role switch
    {
        Admin => Access.Admin | Access.Operate | Access.View,
        Technician => Access.Operate | Access.View,
        ReadOnly => Access.View,
        _ => Access.None,
    };
}

/// <summary>Appelant authentifié : jeton (administrateur ou enrôlement) ou compte nominatif connecté par session.</summary>
/// <param name="Actor">Nom inscrit dans le journal d'audit et dans l'historique des tâches.</param>
public sealed record Principal(Access Access, string Actor, string? Role, UserRecord? User = null, SessionRecord? Session = null)
{
    public const string Item = "SpokaPilote.Principal";
    public const string AdminTokenActor = "Jeton administrateur";

    public static readonly Principal AdminToken = new(Access.Admin | Access.Operate | Access.View, AdminTokenActor, Roles.Admin);
    public static readonly Principal AgentToken = new(Access.Agent, "Jeton d'enrôlement", null);

    public bool IsSession => Session is not null;

    public static Principal ForSession(SessionRecord session) =>
        new(Roles.AccessFor(session.User.Role), session.User.Label, session.User.Role, session.User, session);

    public static Principal? Of(HttpContext http) => http.Items.TryGetValue(Item, out var p) ? p as Principal : null;
}
