using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Matching;

namespace SpokaPilote.Core.Repository;

public sealed class IndexedInf
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public string? Class { get; init; }
    public string? Provider { get; init; }
    public string? Version { get; init; }
    public DateTime? Date { get; init; }
    public bool HasCatalog { get; init; }
    public List<InfModel> Models { get; init; } = new();
}

/// <summary>
/// Dépôt de pilotes local ou réseau (ex. \\srv\pilotes) : tous les INF sont indexés par ID matériel.
/// L'index est mis en cache et seuls les fichiers modifiés sont relus.
/// </summary>
public sealed class DriverRepository
{
    public string RootPath { get; }
    public IReadOnlyList<IndexedInf> Infs => _infs;
    private List<IndexedInf> _infs = new();

    public DriverRepository(string rootPath) => RootPath = rootPath;

    private string IndexFile
    {
        get
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RootPath.ToLowerInvariant())))[..16];
            return System.IO.Path.Combine(AppPaths.Ensure(System.IO.Path.Combine(AppPaths.Cache, "repo-index")), hash + ".json");
        }
    }

    public async Task IndexAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(RootPath))
        {
            Log.Warn($"Dépôt introuvable : {RootPath}");
            _infs = new();
            return;
        }

        var previous = new Dictionary<string, IndexedInf>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(IndexFile))
            {
                var cached = JsonSerializer.Deserialize<List<IndexedInf>>(await File.ReadAllTextAsync(IndexFile, ct));
                if (cached is not null) foreach (var c in cached) previous[c.Path] = c;
            }
        }
        catch { /* index corrompu : reconstruction complète */ }

        var files = await Task.Run(() => SafeEnumerate(RootPath, "*.inf").ToList(), ct).ConfigureAwait(false);
        var result = new List<IndexedInf>(files.Count);
        var parsed = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fi = new FileInfo(file);
                if (previous.TryGetValue(file, out var known) && known.Size == fi.Length && known.LastWriteUtc == fi.LastWriteTimeUtc)
                {
                    result.Add(known);
                    continue;
                }
                var inf = InfParser.Parse(file);
                if (inf.Models.Count == 0) continue;
                result.Add(new IndexedInf
                {
                    Path = file,
                    Size = fi.Length,
                    LastWriteUtc = fi.LastWriteTimeUtc,
                    Class = inf.Class,
                    Provider = inf.Provider,
                    Version = inf.DriverVersion,
                    Date = inf.DriverDate,
                    HasCatalog = !string.IsNullOrEmpty(inf.CatalogFile),
                    Models = inf.Models,
                });
                parsed++;
                if (parsed % 50 == 0) progress?.Report($"Indexation du dépôt : {parsed} INF analysés...");
            }
            catch (Exception ex)
            {
                Log.Debug($"INF ignoré ({file}) : {ex.Message}");
            }
        }

        _infs = result;
        try { await File.WriteAllTextAsync(IndexFile, JsonSerializer.Serialize(result), ct); } catch { /* cache facultatif */ }
        Log.Info($"Dépôt {RootPath} : {result.Count} paquet(s) INF indexé(s) ({parsed} nouveau(x)).");
    }

    internal static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files = Array.Empty<string>();
            string[] subdirs = Array.Empty<string>();
            try
            {
                files = Directory.GetFiles(dir, pattern);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Log.Debug($"Dossier ignoré : {dir} ({ex.Message})");
            }
            foreach (var f in files) yield return f;
            foreach (var d in subdirs) stack.Push(d);
        }
    }
}
