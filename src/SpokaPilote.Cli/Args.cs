namespace SpokaPilote.Cli;

/// <summary>Analyse minimaliste des arguments : commande, positionnels, --drapeaux et --clé valeur.</summary>
internal sealed class Args
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "html", "report", "sources", "repo", "range", "name", "driver", "location", "output-dir", "out", "only", "exclude",
    };

    private readonly List<string> _positionals = new();
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Key, string Value)> _values = new();

    public string? Command { get; }

    public Args(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--"))
            {
                var key = arg[2..];
                var eq = key.IndexOf('=');
                if (eq > 0) _values.Add((key[..eq], key[(eq + 1)..]));
                else if (ValueOptions.Contains(key) && i + 1 < args.Length) _values.Add((key, args[++i]));
                else _flags.Add(key);
            }
            else if (arg is "-h" or "/?") Command = "help";
            else if (Command is null) Command = arg.ToLowerInvariant();
            else _positionals.Add(arg);
        }
    }

    public bool Flag(string name) => _flags.Contains(name);
    public string? Value(string name) => _values.LastOrDefault(v => v.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    public IEnumerable<string> Values(string name) => _values.Where(v => v.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(v => v.Value);
    public string? Positional(int index) => index < _positionals.Count ? _positionals[index] : null;
}
