using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Install;

/// <summary>Programme à lancer (chemin complet) et ses arguments, tels que transmis à CreateProcess.</summary>
public sealed record VendorInvocation(string FileName, string Arguments);

/// <summary>
/// Lignes de commande des installeurs constructeurs. Elles viennent de métadonnées (catalogues Dell, HP, descripteurs
/// Lenovo) et sont exécutées en SYSTEM : elles ne sont jamais confiées telles quelles à un interpréteur.
/// <list type="bullet">
/// <item>modèle : ni enchaînement, ni redirection, ni variable d'environnement (<c>&amp; | &lt; &gt; ^ !</c>, caractère de
///   contrôle, retour à la ligne, <c>%</c> hors des emplacements <c>%FILE%</c>, <c>%PACKAGEPATH%</c>, <c>%LOGPATH%</c>),
///   guillemets appariés ;</item>
/// <item>programme : l'installeur téléchargé et vérifié (<c>%FILE%</c>, ou son nom) ou un fichier extrait du paquet
///   (<c>%PACKAGEPATH%\…</c>, ou un nom relatif au dossier d'extraction), de type .exe, .cmd ou .bat ; jamais un
///   programme du système (cmd, powershell, msiexec…) ni un chemin hors de ces dossiers ;</item>
/// <item>lancement direct (CreateProcess, sans interpréteur) ; seul un script .cmd/.bat du paquet passe par
///   <c>cmd.exe /d</c>, avec les mêmes contrôles appliqués à la ligne complète, après expansion.</item>
/// </list>
/// Formes réelles acceptées : Dell <c>"%FILE%" /s /l="%PACKAGEPATH%\dup.log"</c> ; HP <c>"Setup.exe" /s /v/qn</c>,
/// <c>install.cmd</c> ; Lenovo <c>n32ww12w.exe /VERYSILENT /DIR=%PACKAGEPATH% /EXTRACT="YES"</c> puis
/// <c>%PACKAGEPATH%\n32ww12w.exe /verysilent /DIR=%PACKAGEPATH%\TMP</c> ; NVIDIA <c>"%PACKAGEPATH%\setup.exe" -s …</c>.
/// </summary>
public static class VendorCommand
{
    public const string FilePlaceholder = "%FILE%";
    public const string PackagePlaceholder = "%PACKAGEPATH%";
    public const string LogPlaceholder = "%LOGPATH%";
    /// <summary>Commande spéciale : installer les INF extraits avec pnputil (descripteurs Lenovo de type « INF »).</summary>
    public const string InfMarker = "@INF";

    private static readonly string[] Placeholders = [FilePlaceholder, PackagePlaceholder, LogPlaceholder];
    private static readonly string[] ProgramExtensions = [".exe", ".cmd", ".bat"];
    private const int MaxLength = 2048;

    /// <summary>
    /// Programmes du système qui n'ont rien à faire dans une commande de paquet (interpréteurs, lanceurs) : refusés dès
    /// l'analyse du catalogue, avec un message clair (ils le seraient de toute façon à l'exécution, hors du paquet).
    /// </summary>
    private static readonly string[] SystemPrograms =
    [
        "cmd", "powershell", "pwsh", "msiexec", "rundll32", "regsvr32", "wscript", "cscript", "mshta", "conhost", "start",
        "certutil", "bitsadmin", "wmic", "schtasks", "reg", "net", "sc", "bash", "wsl", "curl", "explorer",
    ];

    /// <summary>Motif du refus de ce modèle de commande, ou null s'il est acceptable.</summary>
    public static string? Validate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "commande vide";
        if (template.Length > MaxLength) return "commande trop longue";
        if (UnsafeCharacter(StripPlaceholders(template)) is { } c) return $"caractère interdit {c}";
        if (template.Count(ch => ch == '"') % 2 != 0) return "guillemets non appariés";
        var (program, _) = Split(template);
        return ValidateProgram(program);
    }

    /// <summary>
    /// Programme et arguments à lancer pour <paramref name="template"/>. <paramref name="packageAllowed"/> = faux pour la
    /// commande d'extraction (seul l'installeur téléchargé peut être lancé). Lève <see cref="SecurityException"/> pour une
    /// commande hors de la forme attendue, <see cref="FileNotFoundException"/> si le programme manque dans le paquet.
    /// </summary>
    public static VendorInvocation Resolve(string template, string file, string packagePath, string workingDirectory, bool packageAllowed)
    {
        if (Validate(template) is { } problem)
            throw new SecurityException($"Commande d'installation refusée ({problem}) : {template}");
        var (programTemplate, argumentsTemplate) = Split(template);
        var program = Expand(programTemplate, file, packagePath);
        var arguments = Expand(argumentsTemplate, file, packagePath);

        string full;
        if (Path.IsPathFullyQualified(program)) full = Path.GetFullPath(program);
        else if (Path.IsPathRooted(program) || program.Contains(':')) throw Refused(programTemplate, "chemin invalide");
        else full = Path.GetFullPath(Path.Combine(workingDirectory, program));
        full = WithImplicitExtension(full);

        var isInstaller = string.Equals(full, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
        if (!isInstaller && !(packageAllowed && IsUnder(full, packagePath)))
            throw Refused(programTemplate, packageAllowed
                ? "ni l'installeur téléchargé ni un fichier extrait du paquet"
                : "seul l'installeur téléchargé peut extraire le paquet");
        var extension = Path.GetExtension(full).ToLowerInvariant();
        if (!ProgramExtensions.Contains(extension)) throw Refused(programTemplate, $"type de programme non autorisé ({extension})");
        if (!File.Exists(full))
            throw new FileNotFoundException($"Programme d'installation introuvable dans le paquet : {Path.GetFileName(full)}.", full);

        if (extension is ".cmd" or ".bat")
        {
            // Un script ne se lance qu'avec cmd.exe : la ligne complète (chemins réels compris) est revérifiée.
            var line = arguments.Length > 0 ? $"\"{full}\" {arguments}" : $"\"{full}\"";
            if (UnsafeCharacter(line) is { } c) throw Refused(programTemplate, $"caractère interdit {c} après expansion");
            return new VendorInvocation(ProcessRunner.SystemTool("cmd.exe"), $"/d /s /c \"{line}\"");
        }
        return new VendorInvocation(full, arguments);
    }

    private static SecurityException Refused(string program, string why) =>
        new($"Commande d'installation refusée : « {program} » ({why}).");

    private static string? ValidateProgram(string program)
    {
        if (program.Length == 0) return "aucun programme";
        if (program.Contains('"')) return "programme mal délimité";
        if (program.Equals(FilePlaceholder, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = program.StartsWith(PackagePlaceholder + "\\", StringComparison.OrdinalIgnoreCase)
            ? program[(PackagePlaceholder.Length + 1)..]
            : program;
        if (relative.Length == 0 || relative.Contains('%') || relative.Contains(':') || relative.StartsWith('\\') || relative.StartsWith('/'))
            return "le programme doit être l'installeur téléchargé ou un fichier du paquet";
        if (relative.Split('\\', '/').Any(segment => segment is ".." or "."))
            return "chemin relatif interdit (..)";
        if (relative == program && SystemPrograms.Contains(Path.GetFileNameWithoutExtension(relative), StringComparer.OrdinalIgnoreCase))
            return $"programme du système interdit ({relative})";
        return null;
    }

    /// <summary>Premier caractère dangereux pour cmd.exe (enchaînement, redirection, échappement, expansion), ou null.</summary>
    private static string? UnsafeCharacter(string text)
    {
        foreach (var c in text)
        {
            if (c is '\r' or '\n') return "(retour à la ligne)";
            if (char.IsControl(c)) return $"(U+{(int)c:X4})";
            if (c is '&' or '|' or '<' or '>' or '^' or '!' or '%' or '`') return $"« {c} »";
        }
        return null;
    }

    private static string StripPlaceholders(string template)
    {
        foreach (var p in Placeholders) template = template.Replace(p, "", StringComparison.OrdinalIgnoreCase);
        return template;
    }

    /// <summary>« "programme" arguments » ou « programme arguments » → (programme, arguments).</summary>
    internal static (string Program, string Arguments) Split(string template)
    {
        var text = template.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return (text[1..], "");
            return (text[1..end].Trim(), text[(end + 1)..].Trim());
        }
        var space = text.IndexOfAny([' ', '\t']);
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    private static string Expand(string text, string file, string packagePath) => text
        .Replace(FilePlaceholder, file, StringComparison.OrdinalIgnoreCase)
        .Replace(PackagePlaceholder, packagePath, StringComparison.OrdinalIgnoreCase)
        .Replace(LogPlaceholder, AppPaths.Logs, StringComparison.OrdinalIgnoreCase);

    /// <summary>« setup » → « setup.exe » (ou .cmd, .bat) s'il existe, comme le ferait cmd.exe dans le dossier courant.</summary>
    private static string WithImplicitExtension(string path)
    {
        if (Path.HasExtension(path) || File.Exists(path)) return path;
        foreach (var ext in ProgramExtensions)
            if (File.Exists(path + ext)) return path + ext;
        return path;
    }

    private static bool IsUnder(string path, string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd('\\', '/') + '\\';
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
