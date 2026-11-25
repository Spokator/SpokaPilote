using System.Net;
using System.Text;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Engine;

public sealed record FleetMachine(ScanReport Report, string SourceFile)
{
    public string Name => Report.System.ComputerName;
    public string Model => $"{Report.System.Manufacturer} {Report.System.DisplayModel}".Trim();
    /// <summary>Périphériques en erreur à cause d'un pilote (hors pannes matérielles).</summary>
    public int Problems => Report.Devices.Count(d => d.IsPresent && d.HasProblem && !d.IsHardwareFault);
    public int HardwareFaults => Report.Devices.Count(d => d.IsPresent && d.IsHardwareFault);
    public int Unresolved => Report.UnresolvedDeviceInfos.Count(d => !d.IsHardwareFault);
    public int Proposed => Report.Candidates.Count;
    public int Installed => Report.Installation?.Count(o => o.Status == "Installed") ?? 0;
    public int Failed => Report.Installation?.Count(o => o.Status is "Failed" or "Blocked") ?? 0;
    public bool Healthy => Problems == 0 && Failed == 0;
}

/// <summary>
/// Consolide les rapports JSON de tout un parc (ex. \\srv\rapports, alimenté par
/// « spokapilote install --yes --output-dir \\srv\rapports ») en une page HTML unique.
/// </summary>
public static class FleetReport
{
    public static List<FleetMachine> Load(string folder)
    {
        var machines = new List<FleetMachine>();
        foreach (var file in Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories))
        {
            try { machines.Add(new FleetMachine(ScanReport.Load(file), file)); }
            catch (Exception ex) { Log.Debug($"Rapport ignoré ({file}) : {ex.Message}"); }
        }
        // Un seul rapport par poste : le plus récent.
        return machines
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(m => m.Report.Timestamp).First())
            .OrderBy(m => m.Healthy).ThenBy(m => m.Name)
            .ToList();
    }

    public static string ToHtml(IReadOnlyList<FleetMachine> machines, string folder)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Parc SpokaPilote</title><style>
            :root{--bg:#f6f7f9;--card:#fff;--text:#1b1f24;--muted:#5b6470;--line:#e3e6ea;--ok:#1a7f37;--warn:#9a6700;--bad:#cf222e}
            @media (prefers-color-scheme:dark){:root{--bg:#0f1216;--card:#171b21;--text:#e6e9ed;--muted:#9aa4b1;--line:#2a313a;--ok:#3fb950;--warn:#d29922;--bad:#f85149}}
            body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
            main{max-width:1200px;margin:0 auto;padding:24px 16px}h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 8px}
            .muted{color:var(--muted)}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px;margin-top:16px}
            .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}.k{font-size:12px;color:var(--muted)}.v{font-size:24px;font-weight:600}
            table{width:100%;border-collapse:collapse;background:var(--card);border:1px solid var(--line)}
            th,td{text-align:left;padding:7px 10px;border-bottom:1px solid var(--line);vertical-align:top;overflow-wrap:anywhere}th{font-size:12px;color:var(--muted)}
            .ok{color:var(--ok)}.warn{color:var(--warn)}.bad{color:var(--bad)}.scroll{overflow-x:auto}code{font-size:12px}
            </style></head><body><main>
            """);
        sb.Append($"<h1>Parc informatique — rapport SpokaPilote</h1><div class=\"muted\">{DateTime.Now:dd/MM/yyyy HH:mm} · source : {E(folder)}</div>");

        var healthy = machines.Count(m => m.Healthy);
        sb.Append("<div class=\"grid\">");
        void Card(string k, string v, string cls = "") => sb.Append($"<div class=\"card\"><div class=\"k\">{E(k)}</div><div class=\"v {cls}\">{E(v)}</div></div>");
        Card("Postes", machines.Count.ToString());
        Card("Sans problème", healthy.ToString(), "ok");
        Card("À traiter", (machines.Count - healthy).ToString(), machines.Count - healthy > 0 ? "warn" : "ok");
        Card("Modèles différents", machines.Select(m => m.Model).Distinct().Count().ToString());
        Card("Pilotes installés", machines.Sum(m => m.Installed).ToString());
        Card("Échecs d'installation", machines.Sum(m => m.Failed).ToString(), machines.Sum(m => m.Failed) > 0 ? "bad" : "");
        sb.Append("</div>");

        // Matériel sans pilote, agrégé sur tout le parc : la liste de ce qu'il reste à trouver.
        var unresolved = machines
            .SelectMany(m => m.Report.UnresolvedDeviceInfos.Where(d => !d.IsHardwareFault).Select(d => (Machine: m.Name, Device: d)))
            .GroupBy(x => x.Device.HardwareIds.FirstOrDefault() ?? x.Device.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();
        if (unresolved.Count > 0)
        {
            sb.Append("<h2 class=\"bad\">Matériel sans pilote trouvé (tout le parc)</h2><div class=\"scroll\"><table><tr><th>Périphérique</th><th>ID matériel</th><th>Postes</th><th>Diagnostic</th></tr>");
            foreach (var g in unresolved)
            {
                var d = g.First().Device;
                sb.Append($"<tr><td>{E(d.Name)}</td><td><code>{E(g.Key)}</code></td><td>{g.Count()} : {E(string.Join(", ", g.Select(x => x.Machine).Distinct()))}</td><td class=\"muted\">{E(d.Hint)}</td></tr>");
            }
            sb.Append("</table></div>");
        }

        sb.Append("<h2>Postes</h2><div class=\"scroll\"><table><tr><th>Poste</th><th>Modèle</th><th>N° de série</th><th>Windows</th><th>Dernière analyse</th><th>Pilotes en erreur</th><th>Pannes matérielles</th><th>Proposés</th><th>Installation</th></tr>");
        foreach (var m in machines)
        {
            var s = m.Report.System;
            var install = m.Report.Installation is null ? "<span class=\"muted\">analyse seule</span>"
                : $"<span class=\"ok\">{m.Installed} ok</span>{(m.Failed > 0 ? $" · <span class=\"bad\">{m.Failed} échec(s)</span>" : "")}";
            sb.Append($"<tr><td><b>{E(m.Name)}</b></td><td>{E(m.Model)}</td><td>{E(s.SerialNumber)}</td><td>{E(s.OsDisplayVersion)} <span class=\"muted\">{s.OsVersion}</span></td>" +
                      $"<td>{m.Report.Timestamp:dd/MM/yyyy HH:mm}</td><td class=\"{(m.Problems > 0 ? "warn" : "ok")}\">{m.Problems}</td><td class=\"{(m.HardwareFaults > 0 ? "warn" : "muted")}\">{m.HardwareFaults}</td><td>{m.Proposed}</td><td>{install}</td></tr>");
        }
        sb.Append("</table></div>");

        sb.Append("<h2>Modèles</h2><div class=\"scroll\"><table><tr><th>Modèle</th><th>Postes</th><th>À traiter</th></tr>");
        foreach (var g in machines.GroupBy(m => m.Model).OrderByDescending(g => g.Count()))
            sb.Append($"<tr><td>{E(g.Key)}</td><td>{g.Count()}</td><td>{g.Count(m => !m.Healthy)}</td></tr>");
        sb.Append("</table></div><p class=\"muted\">Généré par SpokaPilote.</p></main></body></html>");
        return sb.ToString();
    }
}
