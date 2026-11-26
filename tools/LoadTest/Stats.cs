using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SpokaPilote.LoadTest;

/// <summary>Temps de réponse et codes HTTP par libellé (thread-safe).</summary>
public sealed class Recorder
{
    private readonly ConcurrentDictionary<string, Series> _series = new();

    public void Add(string label, double ms, int status, long bytes) => _series.GetOrAdd(label, _ => new Series()).Add(ms, status, bytes);

    public IEnumerable<(string Label, Summary Summary)> Summaries() =>
        _series.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value.Summarize()));

    public Summary? Get(string label) => _series.TryGetValue(label, out var s) ? s.Summarize() : null;

    public void Clear() => _series.Clear();

    private sealed class Series
    {
        private readonly List<double> _ms = new();
        private readonly Dictionary<int, int> _status = new();
        private long _bytes;
        private readonly Lock _lock = new();

        public void Add(double ms, int status, long bytes)
        {
            lock (_lock)
            {
                _ms.Add(ms);
                _status[status] = _status.GetValueOrDefault(status) + 1;
                _bytes += bytes;
            }
        }

        public Summary Summarize()
        {
            lock (_lock)
            {
                var sorted = _ms.OrderBy(x => x).ToArray();
                double P(double q) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
                return new Summary(sorted.Length, P(0.50), P(0.95), P(0.99), sorted.Length == 0 ? 0 : sorted[^1],
                    sorted.Length == 0 ? 0 : sorted.Average(), sorted.Length == 0 ? 0 : _bytes / sorted.Length,
                    new Dictionary<int, int>(_status));
            }
        }
    }
}

public sealed record Summary(int Count, double P50, double P95, double P99, double Max, double Mean, long AvgBytes, Dictionary<int, int> Status)
{
    public int Errors => Status.Where(s => s.Key is 0 or >= 400).Sum(s => s.Value);

    public string StatusText => string.Join(" ", Status.OrderBy(s => s.Key).Select(s => $"{(s.Key == 0 ? "ERR" : s.Key.ToString(CultureInfo.InvariantCulture))}×{s.Value}"));
}

/// <summary>Échantillonnage de « docker stats » (mémoire et CPU du conteneur) pendant un scénario.</summary>
public sealed class DockerSampler : IAsyncDisposable
{
    private readonly string? _container;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    public double MaxMemoryMb { get; private set; }
    public double LastMemoryMb { get; private set; }
    private readonly List<double> _cpu = new();

    public DockerSampler(string? container)
    {
        _container = container;
        _loop = container is null ? Task.CompletedTask : Task.Run(LoopAsync);
    }

    public double AverageCpu => _cpu.Count == 0 ? 0 : _cpu.Average();
    public double MaxCpu => _cpu.Count == 0 ? 0 : _cpu.Max();

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var line = await Docker($"stats --no-stream --format \"{{{{.MemUsage}}}}|{{{{.CPUPerc}}}}\" {_container}");
            var parts = line.Trim().Split('|');
            if (parts.Length == 2)
            {
                var mem = ParseMb(parts[0].Split('/')[0].Trim());
                LastMemoryMb = mem;
                MaxMemoryMb = Math.Max(MaxMemoryMb, mem);
                if (double.TryParse(parts[1].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu)) _cpu.Add(cpu);
            }
            try { await Task.Delay(3000, _cts.Token); } catch (OperationCanceledException) { break; }
        }
    }

    public static double ParseMb(string text)
    {
        var units = new (string Suffix, double Factor)[] { ("GiB", 1024), ("MiB", 1), ("KiB", 1 / 1024.0), ("GB", 1000), ("MB", 1), ("kB", 1 / 1000.0), ("B", 1 / 1048576.0) };
        foreach (var (suffix, factor) in units)
            if (text.EndsWith(suffix, StringComparison.Ordinal) &&
                double.TryParse(text[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return v * factor;
        return 0;
    }

    public static async Task<string> Docker(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return output;
        }
        catch (Exception ex)
        {
            return "docker: " + ex.Message;
        }
    }

    /// <summary>Taille des fichiers de la base (spokapilote.db, -wal, -shm) dans le conteneur, en octets.</summary>
    public static async Task<string> DatabaseFiles(string container) =>
        (await Docker($"exec {container} sh -c \"ls -l /data/ && du -cb /data/spokapilote.db*\"")).Trim();

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _loop;
        _cts.Dispose();
    }
}

public static class Report
{
    public static string Table(IEnumerable<(string Label, Summary Summary)> rows, double seconds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Requête | n | req/s | p50 (ms) | p95 (ms) | p99 (ms) | max (ms) | taille moy. | statuts |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var (label, s) in rows)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {label} | {s.Count} | {s.Count / Math.Max(seconds, 0.001):0.0} | {s.P50:0.0} | {s.P95:0.0} | {s.P99:0.0} | {s.Max:0} | {Size(s.AvgBytes)} | {s.StatusText} |"));
        return sb.ToString();
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.0} Mo"),
        >= 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} Ko"),
        _ => $"{bytes} o",
    };
}
