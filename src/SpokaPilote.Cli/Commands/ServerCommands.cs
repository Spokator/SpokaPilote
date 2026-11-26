using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Online;

namespace SpokaPilote.Cli;

/// <summary>Console centrale SpokaPilote Server : envoi des rapports et commande « spokapilote server ».</summary>
internal static partial class Program
{
    /// <summary>
    /// Après « scan » / « install » : envoie le rapport à la console centrale si ServerUrl est configuré.
    /// Un échec n'est jamais bloquant (le code de sortie de l'analyse ou de l'installation est conservé).
    /// </summary>
    private static async Task UploadToServerAsync(Args a, Settings settings, ScanResult scan, IReadOnlyList<InstallOutcome>? outcomes, CancellationToken ct)
    {
        if (!ServerClient.IsConfigured(settings) || a.Flag("no-upload")) return;
        try
        {
            var client = new ServerClient(settings);
            _lastUpload = await client.UploadReportAsync(ScanReport.From(scan, outcomes), ct);
            WriteLine($"Rapport envoyé à la console centrale ({client.BaseUri.Authority}).", ConsoleColor.Gray);
        }
        catch (ServerException ex)
        {
            _lastUploadError = ex.Message;
            WriteLine($"⚠ Rapport non envoyé à la console centrale : {ex.Message}", ConsoleColor.Yellow);
            WriteLine($"  Sans conséquence sur l'analyse ; le rapport local reste dans {AppPaths.Reports}.", ConsoleColor.DarkGray);
            // Niveau Info : consigné dans le journal sans répéter le message déjà affiché en console.
            Log.Info($"Envoi du rapport à la console centrale impossible : {ex.Message}");
        }
    }

    private static async Task<int> ServerAsync(Args a, CancellationToken ct)
    {
        var sub = a.Positional(0) ?? "status";
        return sub switch
        {
            "status" or "etat" or "test" => await ServerStatusAsync(a, ct),
            "upload" or "envoyer" => await ServerUploadAsync(a, ct),
            _ => ServerUsage(),
        };
    }

    private static int ServerUsage()
    {
        WriteLine("Usage : spokapilote server status                         teste l'adresse et le jeton de la console centrale", ConsoleColor.Yellow);
        WriteLine("        spokapilote server upload <rapport.json|dossier>  envoie des rapports existants (ex. \\\\srv\\rapports)", ConsoleColor.Yellow);
        WriteLine("Enrôlement : spokapilote config set ServerUrl https://...  puis  spokapilote config set ServerToken <jeton>", ConsoleColor.Gray);
        return ExitUsage;
    }

    /// <summary>« spokapilote server status » : adresse, certificat, jeton, documents publiés.</summary>
    private static async Task<int> ServerStatusAsync(Args a, CancellationToken ct)
    {
        var settings = LoadSettings(a);
        if (!ServerClient.IsConfigured(settings))
        {
            WriteLine("Aucune console centrale configurée sur ce poste.", ConsoleColor.Yellow);
            return ServerUsage();
        }

        ServerClient client;
        try
        {
            client = new ServerClient(settings);
        }
        catch (ServerException ex)
        {
            WriteLine(ex.Message, ConsoleColor.Red);
            return ExitError;
        }

        WriteLine($"Console centrale : {client.BaseUri}", ConsoleColor.White);
        var status = await client.TestAsync(ct);
        WriteLine($"  {(status.Reachable ? "✓" : "✗")} Joignable{(status.ServerVersion is null ? "" : $" (SpokaPilote Server {status.ServerVersion})")}",
            status.Reachable ? ConsoleColor.Green : ConsoleColor.Red);
        if (status.Reachable)
            WriteLine($"  {(status.Authorized ? "✓" : "✗")} Jeton {(status.Authorized ? "accepté" : "refusé ou absent")}",
                status.Authorized ? ConsoleColor.Green : ConsoleColor.Red);
        WriteLine($"  {status.Message}", status.Authorized && status.Access == "agent" ? ConsoleColor.Gray : ConsoleColor.Yellow);
        if (!status.Authorized) return ExitError;

        // Documents publiés par l'IT : simple présence (le contenu est exploité par l'analyse et les imprimantes).
        foreach (var (label, absent, present, fetch) in new (string, string, string, Func<CancellationToken, Task<string?>>)[]
                 {
                     ("Politique de pilotes", "aucune publiée", "publiée", client.GetPolicyJsonAsync),
                     ("Profil d'imprimantes", "aucun publié", "publié", client.GetPrinterProfileJsonAsync),
                 })
        {
            try
            {
                var json = await fetch(ct);
                WriteLine($"  · {label} : {(json is null ? absent : $"{present} ({json.Length:N0} caractères)")}", ConsoleColor.Gray);
            }
            catch (ServerException ex)
            {
                WriteLine($"  ⚠ {label} : {ex.Message}", ConsoleColor.Yellow);
            }
        }
        return ExitOk;
    }

    /// <summary>« spokapilote server upload » : reprise de rapports JSON existants (fichier ou dossier, ex. un partage --output-dir).</summary>
    private static async Task<int> ServerUploadAsync(Args a, CancellationToken ct)
    {
        var target = a.Positional(1);
        if (target is null) return ServerUsage();
        var settings = LoadSettings(a);
        ServerClient client;
        try
        {
            client = new ServerClient(settings);
        }
        catch (ServerException ex)
        {
            WriteLine(ex.Message, ConsoleColor.Red);
            return ExitError;
        }

        var files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*.json", SearchOption.AllDirectories)
            : File.Exists(target) ? [target] : [];
        if (files.Length == 0)
        {
            WriteLine($"Aucun rapport JSON trouvé : {target}", ConsoleColor.Yellow);
            return ExitError;
        }

        WriteLine($"Envoi vers {client.BaseUri} :", ConsoleColor.White);
        int sent = 0, failed = 0, ignored = 0;
        foreach (var file in files)
        {
            ScanReport report;
            try
            {
                report = ScanReport.Load(file);
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException)
            {
                ignored++;
                Log.Debug($"Ignoré ({file}) : {ex.Message}");
                continue;
            }
            try
            {
                var result = await client.UploadReportAsync(report, ct);
                sent++;
                WriteLine($"  ✓ {result.Machine} ({report.Timestamp:dd/MM/yyyy HH:mm}) — {Path.GetFileName(file)}", ConsoleColor.Gray);
            }
            catch (ServerException ex)
            {
                failed++;
                WriteLine($"  ✗ {Path.GetFileName(file)} : {ex.Message}", ConsoleColor.Red);
                // Jeton refusé ou console injoignable : inutile d'insister sur les fichiers suivants.
                if (ex.StatusCode is null or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) break;
            }
        }
        WriteLine($"{sent} rapport(s) envoyé(s){(failed > 0 ? $", {failed} en échec" : "")}{(ignored > 0 ? $", {ignored} fichier(s) ignoré(s) (pas un rapport SpokaPilote complet)" : "")}.",
            failed > 0 ? ConsoleColor.Yellow : sent > 0 ? ConsoleColor.Green : ConsoleColor.Yellow);
        return sent == 0 ? ExitError : failed > 0 ? ExitPartial : ExitOk;
    }
}
