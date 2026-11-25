namespace SpokaPilote.Server.Security;

/// <summary>Filtre d'endpoint : exige un jeton accordant l'un des droits demandés (401 sans jeton valide, 403 si insuffisant).</summary>
public sealed class RequireAccessFilter(Access allowed) : IEndpointFilter
{
    public const string AccessItem = "SpokaPilote.Access";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var tokens = http.RequestServices.GetRequiredService<TokenService>();
        var access = tokens.Authenticate(http.Request.Headers.Authorization);
        if (access == Access.None)
        {
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
