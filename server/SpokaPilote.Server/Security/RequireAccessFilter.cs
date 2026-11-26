using System.Globalization;

namespace SpokaPilote.Server.Security;

/// <summary>
/// Filtre d'endpoint : exige un jeton accordant l'un des droits demandés (401 sans jeton valide, 403 si insuffisant,
/// 429 si l'adresse a accumulé trop de jetons refusés).
/// </summary>
public sealed class RequireAccessFilter(Access allowed) : IEndpointFilter
{
    public const string AccessItem = "SpokaPilote.Access";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var limiter = http.RequestServices.GetRequiredService<AuthFailureLimiter>();
        var ip = http.Connection.RemoteIpAddress;
        if (limiter.BlockedFor(ip) is { } wait)
        {
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Results.Json(new ApiError("Trop de tentatives avec un jeton refusé depuis cette adresse : réessayez dans une minute."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var tokens = http.RequestServices.GetRequiredService<TokenService>();
        var header = http.Request.Headers.Authorization.ToString();
        var access = tokens.Authenticate(header);
        if (access == Access.None)
        {
            // Seul un jeton présenté et refusé compte comme tentative (une requête sans jeton ne devine rien).
            if (TokenService.ExtractBearer(header) is not null)
            {
                limiter.RecordFailure(ip);
                http.RequestServices.GetRequiredService<ILogger<RequireAccessFilter>>()
                    .LogWarning("Jeton refusé depuis {Remote} ({Method} {Path}).", ip, http.Request.Method, http.Request.Path);
            }
            http.Response.Headers.WWWAuthenticate = "Bearer realm=\"SpokaPilote\"";
            return Results.Json(new ApiError("Jeton absent ou invalide (en-tête Authorization: Bearer <jeton>)."), statusCode: StatusCodes.Status401Unauthorized);
        }
        if ((access & allowed) == 0)
            return Results.Json(new ApiError(allowed == Access.Admin
                ? "Cette opération nécessite le jeton administrateur."
                : "Ce jeton ne permet pas cette opération."), statusCode: StatusCodes.Status403Forbidden);

        http.Items[AccessItem] = access;
        return await next(context);
    }
}

public sealed record ApiError(string Error);

public static class AccessExtensions
{
    public static TBuilder RequireAccess<TBuilder>(this TBuilder builder, Access allowed) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new RequireAccessFilter(allowed));
}
