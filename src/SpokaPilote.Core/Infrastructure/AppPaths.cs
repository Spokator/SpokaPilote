namespace SpokaPilote.Core.Infrastructure;

/// <summary>Emplacements disque utilisés par SpokaPilote (tous sous %ProgramData%\SpokaPilote par défaut).</summary>
public static class AppPaths
{
    private static string? _root;

    public static string Root
    {
        get
        {
            if (_root is not null) return _root;
            var overridden = Environment.GetEnvironmentVariable("SPOKAPILOTE_HOME");
            _root = !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpokaPilote");
            return _root;
        }
        set => _root = value;
    }

    public static string Cache => Ensure(Path.Combine(Root, "Cache"));
    public static string Downloads => Ensure(Path.Combine(Root, "Downloads"));
    public static string Extracted => Ensure(Path.Combine(Root, "Extracted"));
    public static string Logs => Ensure(Path.Combine(Root, "Logs"));
    public static string Backups => Ensure(Path.Combine(Root, "Backups"));
    public static string Reports => Ensure(Path.Combine(Root, "Reports"));
    public static string SettingsFile => Path.Combine(Ensure(Root), "settings.json");

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Retourne un nom de fichier/dossier sûr à partir d'un texte quelconque.</summary>
    public static string SafeName(string value, int maxLength = 80)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
        var s = new string(chars).Trim('_', '.');
        if (s.Length > maxLength) s = s[..maxLength];
        return string.IsNullOrEmpty(s) ? "item" : s;
    }
}
