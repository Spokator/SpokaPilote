using System.Globalization;
using System.Text;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Security;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Api;

/// <summary>Journal d'audit : consultation et export CSV (Administrateur).</summary>
public static class AuditEndpoints
{
    public static void MapAuditApi(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("").RequireAccess(Access.Admin);

        admin.MapGet("/audit", (string? category, string? actor, string? q, int? limit, ServerStore store, ServerOptions options) => Results.Ok(new
        {
            entries = store.GetAudit(category, actor, q, Math.Clamp(limit ?? 500, 1, 5000)).Select(View),
            total = store.CountAudit(),
            retention = new { maxEntries = options.AuditMaxEntries, days = options.AuditRetentionDays },
            categories = AuditActions.Categories.Select(c => new { id = c.Key, label = c.Value }),
        }));

        // Export CSV (séparateur « ; », UTF-8 avec BOM : s'ouvre directement dans Excel en français).
        admin.MapGet("/audit/export", (string? category, string? actor, string? q, HttpContext http, ServerStore store, AuditService audit,
            TimeProvider clock) =>
        {
            var entries = store.GetAudit(category, actor, q, 1_000_000);
            var csv = new StringBuilder();
            csv.Append('﻿');
            csv.Append("Date (UTC);Compte;Rôle;Action;Code;Cible;Détail;Adresse IP;Résultat\r\n");
            foreach (var e in entries)
                csv.Append(string.Join(';',
                    Cell(e.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), Cell(e.Actor), Cell(Roles.Label(e.Role)),
                    Cell(AuditActions.Label(e.Action)), Cell(e.Action), Cell(e.Target), Cell(e.Detail), Cell(e.Ip), Cell(e.Success ? "réussi" : "échec")))
                    .Append("\r\n");
            audit.Record(http, AuditActions.AuditExport, null, $"{entries.Count} entrée(s)");
            var name = $"spokapilote-audit-{clock.GetUtcNow():yyyy-MM-dd}.csv";
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", name);
        });
    }

    private static object View(AuditEntry e) => new
    {
        id = e.Id,
        at = e.At,
        actor = e.Actor,
        role = e.Role,
        roleLabel = e.Role is null ? null : Roles.Label(e.Role),
        action = e.Action,
        actionLabel = AuditActions.Label(e.Action),
        target = e.Target,
        detail = e.Detail,
        ip = e.Ip,
        success = e.Success,
    };

    /// <summary>Cellule CSV entre guillemets ; une valeur commençant par = + - @ est neutralisée (injection de formule).</summary>
    internal static string Cell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}
