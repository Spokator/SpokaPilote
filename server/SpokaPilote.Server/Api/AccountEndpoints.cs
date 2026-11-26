using System.Text.RegularExpressions;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Security;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Api;

public sealed class LoginRequest
{
    public string? Login { get; set; }
    public string? Password { get; set; }
}

public sealed class PasswordChangeRequest
{
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
}

public sealed class UserRequest
{
    public string? Login { get; set; }
    public string? DisplayName { get; set; }
    public string? Role { get; set; }
    /// <summary>Création : obligatoire. Modification : vide ou absent = inchangé, sinon réinitialisation.</summary>
    public string? Password { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Comptes nominatifs : connexion par session (cookie), compte courant, gestion des comptes (Administrateur).
/// Le jeton administrateur reste accepté partout (scripts, API) et permet de créer le premier compte.
/// </summary>
public static partial class AccountEndpoints
{
    private const string GenericLoginError = "Identifiant ou mot de passe incorrect.";

    [GeneratedRegex("^[a-z0-9][a-z0-9._@-]{1,63}$")]
    private static partial Regex LoginPattern();

    public static void MapAccountApi(this RouteGroupBuilder api)
    {
        // ------------------------------------------------------------------ session

        // Connexion : publique, limitée par adresse IP (mêmes règles que les jetons refusés). L'en-tête X-SpokaPilote-CSRF
        // doit être présent : un formulaire d'un autre site ne peut pas l'ajouter (protection contre la connexion forcée).
        api.MapPost("/session", async (HttpRequest request, ServerStore store, SessionService sessions, AuthFailureLimiter limiter,
            AuditService audit, TimeProvider clock, ILogger<SessionService> log, CancellationToken ct) =>
        {
            var http = request.HttpContext;
            var ip = http.Connection.RemoteIpAddress;
            if (limiter.BlockedFor(ip) is { } wait) return RequireAccessFilter.TooManyAttempts(http, wait);
            if (string.IsNullOrEmpty(request.Headers[SessionService.CsrfHeader]))
                return Results.Json(new ApiError($"En-tête {SessionService.CsrfHeader} requis."), statusCode: StatusCodes.Status403Forbidden);
            var (req, error) = await GroupEndpoints.ReadAsync<LoginRequest>(request, ct);
            if (error is not null) return error;

            var login = (req!.Login ?? "").Trim().ToLowerInvariant();
            var user = LoginPattern().IsMatch(login) ? store.GetUserByLogin(login) : null;
            bool ok = false, upgrade = false;
            if (user is { Enabled: true }) ok = PasswordHasher.Verify(req.Password, user.PasswordHash, out upgrade);
            else PasswordHasher.VerifyDummy(req.Password); // même durée de réponse que pour un compte existant
            if (!ok)
            {
                limiter.RecordFailure(ip);
                var reason = user is null ? "compte inconnu" : !user.Enabled ? "compte désactivé" : "mot de passe incorrect";
                audit.Record(Sanitize(login.Length == 0 ? "(vide)" : login, 64), null, AuditActions.LoginFailure, null, reason, ip?.ToString(), success: false);
                log.LogWarning("Connexion refusée pour « {Login} » depuis {Remote} ({Reason}).", Sanitize(login, 64), ip, reason);
                return Results.Json(new ApiError(GenericLoginError), statusCode: StatusCodes.Status401Unauthorized);
            }

            if (upgrade) store.UpgradePasswordHash(user!.Id, PasswordHasher.Hash(req.Password!));
            // Une éventuelle session précédente de ce navigateur est fermée (pas de fixation de session).
            if (request.Cookies[SessionService.CookieName] is { Length: > 0 } previous && sessions.Resolve(previous) is { } old)
                store.DeleteSession(old.IdHash);
            var now = clock.GetUtcNow();
            store.SetLastLogin(user!.Id, now);
            var session = sessions.Open(user with { LastLogin = now }, http);
            audit.Record(user.Label, user.Role, AuditActions.LoginSuccess, null, null, ip?.ToString());
            return Results.Ok(SessionView(Principal.ForSession(session), store));
        });

        var view = api.MapGroup("").RequireAccess(Access.View);
        var admin = api.MapGroup("").RequireAccess(Access.Admin);

        // Appelant courant (compte ou jeton administrateur), droits et jeton anti-CSRF de la session.
        view.MapGet("/session", (HttpContext http, ServerStore store) => Results.Ok(SessionView(Principal.Of(http)!, store)));

        view.MapDelete("/session", (HttpContext http, SessionService sessions, AuditService audit) =>
        {
            var p = Principal.Of(http)!;
            if (!p.IsSession) return Results.NoContent(); // jeton : rien à fermer côté serveur
            sessions.Close(http, p.Session);
            audit.Record(http, AuditActions.Logout);
            return Results.NoContent();
        });

        view.MapPut("/session/password", async (HttpRequest request, ServerStore store, AuditService audit, TimeProvider clock, CancellationToken ct) =>
        {
            var p = Principal.Of(request.HttpContext)!;
            if (!p.IsSession)
                return GroupEndpoints.BadRequest("Le jeton administrateur n'a pas de mot de passe : connectez-vous avec un compte.");
            var (req, error) = await GroupEndpoints.ReadAsync<PasswordChangeRequest>(request, ct);
            if (error is not null) return error;
            var user = store.GetUser(p.User!.Id)!;
            if (!PasswordHasher.Verify(req!.CurrentPassword, user.PasswordHash, out _))
            {
                audit.Record(request.HttpContext, AuditActions.AccountOwnPassword, user.Login, "mot de passe actuel incorrect", success: false);
                return GroupEndpoints.BadRequest("Mot de passe actuel incorrect.");
            }
            if (PasswordHasher.CheckPolicy(req.NewPassword, user.Login) is { } rule) return GroupEndpoints.BadRequest(rule);
            store.SetPassword(user.Id, PasswordHasher.Hash(req.NewPassword!), clock.GetUtcNow());
            // Les autres sessions du compte (autres navigateurs) sont fermées ; celle-ci reste ouverte.
            var closed = store.DeleteSessionsForUser(user.Id, p.Session!.IdHash);
            audit.Record(request.HttpContext, AuditActions.AccountOwnPassword, user.Login, closed > 0 ? $"{closed} autre(s) session(s) fermée(s)" : null);
            return Results.NoContent();
        });

        // ------------------------------------------------------------------ comptes (Administrateur)

        admin.MapGet("/users", (ServerStore store) => Results.Ok(new
        {
            users = store.GetUsers().Select(UserView),
            roles = Roles.All.Select(r => new { id = r, label = Roles.Label(r), description = Roles.Description(r) }),
            passwordMinLength = PasswordHasher.MinimumLength,
        }));

        admin.MapPost("/users", async (HttpRequest request, ServerStore store, AuditService audit, TimeProvider clock, CancellationToken ct) =>
        {
            var (req, error) = await GroupEndpoints.ReadAsync<UserRequest>(request, ct);
            if (error is not null) return error;
            var login = (req!.Login ?? "").Trim().ToLowerInvariant();
            if (!LoginPattern().IsMatch(login))
                return GroupEndpoints.BadRequest("Identifiant invalide : 2 à 64 caractères, lettres minuscules, chiffres et . _ - @ (commence par une lettre ou un chiffre).");
            if (!Roles.IsValid(req.Role)) return GroupEndpoints.BadRequest("Rôle inconnu : admin, technician ou readonly.");
            if (PasswordHasher.CheckPolicy(req.Password, login) is { } rule) return GroupEndpoints.BadRequest(rule);
            var displayName = CleanDisplayName(req.DisplayName);
            var id = store.CreateUser(login, displayName, req.Role!, PasswordHasher.Hash(req.Password!), req.Enabled, clock.GetUtcNow());
            if (id is null) return GroupEndpoints.Conflict($"L'identifiant « {login} » est déjà utilisé.");
            var user = store.GetUser(id.Value)!;
            audit.Record(request.HttpContext, AuditActions.AccountCreate, user.Label, $"rôle : {Roles.Label(user.Role)}{(user.Enabled ? "" : " (désactivé)")}");
            return Results.Json(UserView(user), statusCode: StatusCodes.Status201Created);
        });

        admin.MapPut("/users/{id:long}", async (long id, HttpRequest request, ServerStore store, AuditService audit, TimeProvider clock,
            CancellationToken ct) =>
        {
            if (store.GetUser(id) is not { } before) return UserNotFound();
            var (req, error) = await GroupEndpoints.ReadAsync<UserRequest>(request, ct);
            if (error is not null) return error;
            if (!Roles.IsValid(req!.Role)) return GroupEndpoints.BadRequest("Rôle inconnu : admin, technician ou readonly.");
            var p = Principal.Of(request.HttpContext)!;
            if (p.User?.Id == id && (!req.Enabled || req.Role != Roles.Admin))
                return GroupEndpoints.Conflict("Vous ne pouvez ni désactiver votre propre compte ni lui retirer le rôle Administrateur.");
            if (LeavesNoAdmin(store, id, req.Role!, req.Enabled)) return LastAdminConflict();
            var resetPassword = !string.IsNullOrEmpty(req.Password);
            if (resetPassword && PasswordHasher.CheckPolicy(req.Password, before.Login) is { } rule) return GroupEndpoints.BadRequest(rule);

            var now = clock.GetUtcNow();
            store.UpdateUser(id, CleanDisplayName(req.DisplayName), req.Role!, req.Enabled, now);
            if (resetPassword) store.SetPassword(id, PasswordHasher.Hash(req.Password!), now);
            // Mot de passe réinitialisé ou compte désactivé : ses sessions ouvertes sont fermées.
            if (resetPassword || !req.Enabled) store.DeleteSessionsForUser(id, p.User?.Id == id ? p.Session?.IdHash : null);

            var after = store.GetUser(id)!;
            var changes = new List<string>();
            if (before.Role != after.Role) changes.Add($"rôle : {Roles.Label(before.Role)} → {Roles.Label(after.Role)}");
            if (before.Enabled != after.Enabled) changes.Add(after.Enabled ? "réactivé" : "désactivé");
            if ((before.DisplayName ?? "") != (after.DisplayName ?? "")) changes.Add($"nom affiché : « {after.DisplayName} »");
            if (changes.Count > 0) audit.Record(request.HttpContext, AuditActions.AccountUpdate, after.Label, string.Join(" ; ", changes));
            if (resetPassword) audit.Record(request.HttpContext, AuditActions.AccountPassword, after.Label);
            return Results.Ok(UserView(after));
        });

        admin.MapDelete("/users/{id:long}", (long id, HttpContext http, ServerStore store, AuditService audit) =>
        {
            if (store.GetUser(id) is not { } user) return UserNotFound();
            if (Principal.Of(http)!.User?.Id == id) return GroupEndpoints.Conflict("Vous ne pouvez pas supprimer votre propre compte.");
            if (LeavesNoAdmin(store, id, null, false)) return LastAdminConflict();
            store.DeleteUser(id);
            audit.Record(http, AuditActions.AccountDelete, user.Label, $"rôle : {Roles.Label(user.Role)}");
            return Results.NoContent();
        });
    }

    /// <summary>
    /// La modification (ou suppression : role null) laisserait-elle des comptes sans aucun administrateur actif ? Supprimer
    /// le tout dernier compte reste possible : la console revient alors au seul jeton administrateur.
    /// </summary>
    private static bool LeavesNoAdmin(ServerStore store, long id, string? newRole, bool enabled)
    {
        var users = store.GetUsers();
        var remaining = users.Where(u => u.Id != id).ToList();
        var adminsAfter = remaining.Count(u => u.Enabled && u.Role == Roles.Admin) + (newRole == Roles.Admin && enabled ? 1 : 0);
        var accountsAfter = remaining.Count + (newRole is null ? 0 : 1);
        return accountsAfter > 0 && adminsAfter == 0;
    }

    private static IResult LastAdminConflict() => GroupEndpoints.Conflict(
        "Au moins un compte Administrateur actif doit rester : sans lui, seul le jeton administrateur permettrait encore de gérer les comptes.");

    private static object SessionView(Principal p, ServerStore store) => new
    {
        mode = p.IsSession ? "session" : "token",
        version = ApiEndpoints.Version,
        login = p.User?.Login,
        displayName = p.User?.DisplayName,
        label = p.Actor,
        role = p.Role,
        roleLabel = Roles.Label(p.Role),
        csrf = p.Session?.Csrf,
        expiresAt = p.Session?.ExpiresAt,
        permissions = new { view = p.Access.HasFlag(Access.View), operate = p.Access.HasFlag(Access.Operate), admin = p.Access.HasFlag(Access.Admin) },
        // Démarrage : aucun compte encore créé (la console propose de créer le premier administrateur).
        accountCount = p.Access.HasFlag(Access.Admin) ? store.CountUsers() : (int?)null,
    };

    private static object UserView(UserRecord u) => new
    {
        id = u.Id,
        login = u.Login,
        displayName = u.DisplayName,
        role = u.Role,
        roleLabel = Roles.Label(u.Role),
        enabled = u.Enabled,
        createdAt = u.CreatedAt,
        updatedAt = u.UpdatedAt,
        passwordChangedAt = u.PasswordChangedAt,
        lastLogin = u.LastLogin,
    };

    private static string? CleanDisplayName(string? name)
    {
        var clean = Sanitize((name ?? "").Trim(), 80);
        return clean.Length == 0 ? null : clean;
    }

    private static string Sanitize(string text, int max) => new(text.Where(c => !char.IsControl(c)).Take(max).ToArray());

    private static IResult UserNotFound() => Results.Json(new ApiError("Compte introuvable."), statusCode: StatusCodes.Status404NotFound);
}
