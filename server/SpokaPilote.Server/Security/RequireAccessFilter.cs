using System.Globalization;

namespace SpokaPilote.Server.Security;

/// <summary>
/// Filtre d'endpoint : exige un appelant accordant l'un des droits demandés — jeton (en-tête Authorization: Bearer) ou
/// session d'un compte nominatif (cookie). Réponses : 401 sans authentification valide, 403 si le droit manque ou si
/// le jeton anti-CSRF d'une session est absent sur une requête qui modifie quelque chose, 429 si l'adresse a accumulé
/// trop de tentatives refusées.
/// </summary>
public sealed class RequireAccessFilter(Access allowed) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var limiter = http.RequestServices.GetRequiredService<AuthFailureLimiter>();
        var ip = http.Connection.RemoteIpAddress;
        if (limiter.BlockedFor(ip) is { } wait) return TooManyAttempts(http, wait);

        var header = http.Request.Headers.Authorization.ToString();
        Principal? principal;
        if (TokenService.ExtractBearer(header) is not null)
        {
            var access = http.RequestServices.GetRequiredService<TokenService>().Authenticate(header);
            principal = access switch
            {
                Access.Admin => Principal.AdminToken,
                Access.Agent => Principal.AgentToken,
                _ => null,
            };
            if (principal is null)
            {
                // Seul un jeton présenté et refusé compte comme tentative (une requête sans jeton ne devine rien).
                limiter.RecordFailure(ip);
                http.RequestServices.GetRequiredService<ILogger<RequireAccessFilter>>()
                    .LogWarning("Jeton refusé depuis {Remote} ({Method} {Path}).", ip, http.Request.Method, http.Request.Path);
                return Unauthorized(http, "Jeton absent ou invalide (en-tête Authorization: Bearer <jeton>).");
            }
        }
        else if (http.Request.Cookies[SessionService.CookieName] is { Length: > 0 } cookie)
        {
            var session = http.RequestServices.GetRequiredService<SessionService>().Resolve(cookie);
            if (session is null) return Unauthorized(http, "Session expirée : reconnectez-vous.");
            // Protection CSRF : le cookie est SameSite=Strict, et toute requête qui modifie quelque chose doit en plus
            // présenter le jeton anti-CSRF de la session (en-tête qu'un autre site ne peut pas ajouter).
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) &&
                !SessionService.CsrfMatches(session, http.Request.Headers[SessionService.CsrfHeader].ToString()))
                return Results.Json(new ApiError("Jeton anti-CSRF absent ou invalide : rechargez la page de la console."),
                    statusCode: StatusCodes.Status403Forbidden);
            principal = Principal.ForSession(session);
        }
        else
        {
            return Unauthorized(http, "Authentification requise : jeton (en-tête Authorization: Bearer <jeton>) ou session de la console.");
        }

        if ((principal.Access & allowed) == 0)
            return Results.Json(new ApiError(Forbidden(principal, allowed)), statusCode: StatusCodes.Status403Forbidden);

        http.Items[Principal.Item] = principal;
        return await next(context);
    }

    internal static IResult TooManyAttempts(HttpContext http, TimeSpan wait)
    {
        http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return Results.Json(new ApiError("Trop de tentatives refusées depuis cette adresse : réessayez dans une minute."),
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static IResult Unauthorized(HttpContext http, string message)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer realm=\"SpokaPilote\"";
        return Results.Json(new ApiError(message), statusCode: StatusCodes.Status401Unauthorized);
    }

    private static string Forbidden(Principal principal, Access allowed)
    {
        if (principal.Access == Access.Agent)
            return allowed.HasFlag(Access.Admin) || allowed.HasFlag(Access.View) || allowed.HasFlag(Access.Operate)
                ? "Cette opération nécessite le jeton administrateur ou un compte de la console."
                : "Ce jeton ne permet pas cette opération.";
        if (allowed == Access.Agent) return "Opération réservée aux postes (jeton d'enrôlement).";
        var role = Roles.Label(principal.Role);
        return allowed.HasFlag(Access.Operate)
            ? $"Votre rôle ({role}) ne permet que la consultation."
            : $"Opération réservée aux administrateurs (votre rôle : {role}).";
    }
}

public sealed record ApiError(string Error);

public static class AccessExtensions
{
    public static TBuilder RequireAccess<TBuilder>(this TBuilder builder, Access allowed) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new RequireAccessFilter(allowed));
}
