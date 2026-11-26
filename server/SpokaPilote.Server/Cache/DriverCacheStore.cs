using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Server.Cache;

/// <summary>Empreintes publiées par la source officielle (transmises par le poste) ; null = inconnue.</summary>
public sealed record ExpectedHashes(string? Sha256, string? Sha1, string? Md5)
{
    public static readonly ExpectedHashes None = new(null, null, null);
    public bool Any => Sha256 is not null || Sha1 is not null || Md5 is not null;

    /// <summary>Le contenu décrit par ces empreintes calculées correspond-il à toutes les empreintes attendues ?</summary>
    public bool MatchedBy(string sha256, string sha1, string md5) =>
        (Sha256 is null || Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase)) &&
        (Sha1 is null || Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase)) &&
        (Md5 is null || Md5.Equals(md5, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Statistiques du cache (console, /api/metrics, « spokapilote server status »).</summary>
public sealed record DriverCacheStats(
    bool Enabled, bool Catalogs, long SizeBytes, long MaxBytes, int Files, int CatalogEntries, int Blobs,
    long FileHits, long FileMisses, long CatalogHits, long CatalogMisses, long BytesServedFromCache, long BytesFromOrigin,
    long Refused, long Bypassed, long Errors, long HashMismatches, int DownloadsInProgress, long FreeBytes, long VolumeBytes,
    bool DiskGuardActive, DateTimeOffset? MicrosoftCatalogPausedUntil)
{
    public long Hits => FileHits + CatalogHits;
    public long Misses => FileMisses + CatalogMisses;
    /// <summary>Part des demandes servies sans retourner à la source (0 à 1 ; null sans demande).</summary>
    public double? HitRatio => Hits + Misses == 0 ? null : (double)Hits / (Hits + Misses);
}

/// <summary>Fichier ou catalogue du cache (liste « les plus demandés » de la console).</summary>
public sealed record DriverCacheTopEntry(string Url, string Kind, long Size, long Hits, long BytesSaved, DateTimeOffset FetchedAt, DateTimeOffset LastAccess);

/// <summary>
/// Cache de pilotes et de catalogues de la console : stockage par empreinte SHA-256 (un même fichier atteint par deux
/// adresses n'est stocké qu'une fois) dans /data/cache, index JSON, éviction des moins récemment demandés au-delà de la
/// taille maximale, garde d'espace disque, téléchargement unique par adresse (les demandes simultanées attendent le même
/// téléchargement et reçoivent les octets au fil de l'eau), vérification des empreintes publiées.
/// <para>
/// Le cache n'est qu'un transport : le poste refait toutes ses vérifications (empreinte, signature Authenticode d'un
/// éditeur approuvé, ID matériel de l'INF) exactement comme pour un téléchargement direct.
/// </para>
/// </summary>
public sealed class DriverCacheStore : IDisposable
{
    /// <summary>Octets retenus en fin de fichier pendant un téléchargement, jusqu'à la vérification de l'empreinte.</summary>
    internal const int Holdback = 64 * 1024;
    private const int ChunkSize = 1 << 20;
    private const long DiskCheckEvery = 64L * 1024 * 1024;
    /// <summary>Taille maximale d'un catalogue relayé (le plus gros, CatalogPC.cab de Dell, fait quelques dizaines de Mo).</summary>
    internal const long MaxCatalogBytes = 512L * 1024 * 1024;
    /// <summary>Un catalogue absent de la source (404) est mémorisé au plus une heure.</summary>
    internal static readonly TimeSpan NegativeTtl = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions IndexJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly DriverCacheSettingsService _settings;
    private readonly DriverCacheOrigin _origin;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly Func<string, (long Free, long Total)> _disk;
    private readonly SemaphoreSlim _originSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlobInfo> _blobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<CatalogFetch>> _catalogFlights = new(StringComparer.Ordinal);
    private readonly CacheTotals _totals = new();
    private readonly Timer _saveTimer;
    private readonly Lock _saveLock = new();
    private long _sizeBytes;
    private bool _dirty;
    private bool _diskWarned;

    public DriverCacheStore(ServerOptions options, DriverCacheSettingsService settings, DriverCacheOrigin origin, TimeProvider clock,
        ILogger<DriverCacheStore> log, string? root = null, Func<string, (long Free, long Total)>? disk = null, int? maxDownloads = null)
    {
        _settings = settings;
        _origin = origin;
        _clock = clock;
        _log = log;
        _disk = disk ?? DiskSpace;
        Root = Path.GetFullPath(root ?? options.CacheDirectory);
        MaxConcurrentDownloads = Math.Clamp(maxDownloads ?? options.CacheMaxDownloads, 1, 256);
        _originSlots = new SemaphoreSlim(MaxConcurrentDownloads);
        Directory.CreateDirectory(BlobRoot);
        Directory.CreateDirectory(TempRoot);
        Load();
        _saveTimer = new Timer(_ => SaveIfDirty(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public string Root { get; }
    public int MaxConcurrentDownloads { get; }
    private string BlobRoot => Path.Combine(Root, "blobs");
    private string TempRoot => Path.Combine(Root, "tmp");
    private string IndexPath => Path.Combine(Root, "index.json");
    public DriverCacheSettings Settings => _settings.Current();

    private DateTimeOffset Now => _clock.GetUtcNow();

    internal string BlobPath(string sha256) => Path.Combine(BlobRoot, sha256[..2], sha256);

    // ------------------------------------------------------------------ fichiers (paquets de pilotes)

    public enum LookupKind { Hit, Flight, Refused }

    /// <summary>Résultat de <see cref="BeginFile"/> : fichier présent, téléchargement en cours (rejoint ou lancé), ou refus.</summary>
    public sealed record FileLookup(LookupKind Kind, string? BlobPath = null, long Size = 0, string? Sha256 = null, string? ContentType = null,
        Flight? Flight = null, bool Joined = false, int Status = 200, string? Message = null, string? CacheHeader = null);

    /// <summary>
    /// Fichier officiel demandé par un poste : présent et à jour (et conforme aux empreintes attendues) → servi depuis le
    /// disque ; sinon un téléchargement unique est lancé ou rejoint. Refus (503) si trop de téléchargements sont en cours,
    /// si le fichier dépasse le cache ou si le volume manque de place : le poste télécharge alors directement.
    /// </summary>
    public FileLookup BeginFile(Uri url, ExpectedHashes expected, long? sizeHint)
    {
        var settings = Settings;
        if (!OfficialHosts.IsOfficialDownload(url))
            return new FileLookup(LookupKind.Refused, Status: 403, Message: $"{url.Host} ne fait pas partie des sources officielles autorisées.",
                CacheHeader: "refused");
        var key = "file:" + Normalize(url);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Sha256 is { } sha && _blobs.TryGetValue(sha, out var blob) &&
                Now - entry.FetchedAt < TimeSpan.FromDays(settings.FileMaxAgeDays) && expected.MatchedBy(blob.Sha256, blob.Sha1, blob.Md5) &&
                File.Exists(BlobPath(sha)))
            {
                entry.LastAccess = blob.LastAccess = Now;
                entry.Hits++;
                _totals.FileHits++;
                _dirty = true;
                return new FileLookup(LookupKind.Hit, BlobPath(sha), blob.Size, sha, entry.ContentType, CacheHeader: "hit");
            }
            if (_flights.TryGetValue(key, out var running))
            {
                _totals.FileHits++; // une seule requête vers la source pour toutes les demandes simultanées
                return new FileLookup(LookupKind.Flight, Flight: running, Joined: true, CacheHeader: "wait");
            }
            if (sizeHint is > 0 && sizeHint > settings.MaxBytes * 9 / 10)
            {
                _totals.Bypassed++;
                return new FileLookup(LookupKind.Refused, Status: 503, Message: "Fichier plus volumineux que le cache.", CacheHeader: "bypass");
            }
            if (!HasRoomFor(sizeHint ?? 0, settings))
            {
                _totals.Bypassed++;
                return new FileLookup(LookupKind.Refused, Status: 503, Message: "Espace disque insuffisant sur la console : cache suspendu.", CacheHeader: "bypass");
            }
            if (!_originSlots.Wait(0))
            {
                _totals.Bypassed++;
                return new FileLookup(LookupKind.Refused, Status: 503,
                    Message: $"{MaxConcurrentDownloads} téléchargements déjà en cours sur la console.", CacheHeader: "bypass");
            }
            var flight = new Flight(key, url, Path.Combine(TempRoot, Guid.NewGuid().ToString("N") + ".part"), expected, sizeHint);
            _flights[key] = flight;
            _totals.FileMisses++;
            _dirty = true;
            _ = Task.Run(() => RunFlightAsync(flight));
            return new FileLookup(LookupKind.Flight, Flight: flight, CacheHeader: "miss");
        }
    }

    /// <summary>Téléchargement unique en cours : les lecteurs suivent l'écriture du fichier temporaire.</summary>
    public sealed class Flight(string key, Uri url, string tempPath, ExpectedHashes expected, long? sizeHint)
    {
        public string Key { get; } = key;
        public Uri Url { get; } = url;
        public string TempPath { get; } = tempPath;
        public ExpectedHashes Expected { get; } = expected;
        public long? SizeHint { get; } = sizeHint;
        public long? Length { get; internal set; }
        public string? ContentType { get; internal set; }
        public int OriginStatus { get; internal set; }
        public string? Error { get; internal set; }
        private long _written;
        public long Written => Interlocked.Read(ref _written);
        internal void Advance(long n) => Interlocked.Add(ref _written, n);
        /// <summary>true quand les en-têtes de la source sont reçus et acceptés ; false en cas d'échec avant le premier octet.</summary>
        public TaskCompletionSource<bool> Headers { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Chemin du fichier vérifié et rangé (null = échec : les lecteurs doivent interrompre la réponse).</summary>
        public TaskCompletionSource<string?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Pulse() => Interlocked.Exchange(ref _pulse, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        internal Task NextPulse => Volatile.Read(ref _pulse).Task;
    }

    private async Task RunFlightAsync(Flight flight)
    {
        var ct = _shutdown.Token;
        string? committed = null;
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await _origin.GetAsync(flight.Url, u => OfficialHosts.IsOfficialDownload(u), stall.Token).ConfigureAwait(false);
            flight.OriginStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                throw new FlightException($"La source officielle a répondu {(int)response.StatusCode} ({flight.Url.Host}).");
            var settings = Settings;
            var length = response.Content.Headers.ContentLength;
            if (length is { } l)
            {
                if (l > settings.MaxBytes * 9 / 10) throw new FlightException("Fichier plus volumineux que le cache.", bypass: true);
                lock (_lock)
                {
                    if (!HasRoomFor(l, settings)) throw new FlightException("Espace disque insuffisant sur la console : cache suspendu.", bypass: true);
                    MakeRoom(l, settings);
                }
            }
            flight.Length = length;
            flight.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
            try
            {
                await using (var output = new FileStream(flight.TempPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous))
                await using (var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
                {
                    flight.Headers.TrySetResult(true);
                    long nextDiskCheck = DiskCheckEvery;
                    while (true)
                    {
                        stall.CancelAfter(TimeSpan.FromSeconds(120));
                        var read = await input.ReadAsync(buffer.AsMemory(0, ChunkSize), stall.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        var chunk = buffer.AsSpan(0, read);
                        sha256.AppendData(chunk);
                        sha1.AppendData(chunk);
                        md5.AppendData(chunk);
                        await output.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                        await output.FlushAsync(stall.Token).ConfigureAwait(false);
                        flight.Advance(read);
                        flight.Pulse();
                        var written = flight.Written;
                        if (length is { } expectedLength && written > expectedLength)
                            throw new FlightException("La source a envoyé plus d'octets qu'annoncé.");
                        if (written > settings.MaxBytes * 9 / 10) throw new FlightException("Fichier plus volumineux que le cache.", bypass: true);
                        if (written >= nextDiskCheck)
                        {
                            nextDiskCheck += DiskCheckEvery;
                            if (!HasRoomFor(0, settings)) throw new FlightException("Espace disque insuffisant sur la console : téléchargement interrompu.", bypass: true);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var size = flight.Written;
            if (length is { } announced && size != announced) throw new FlightException($"Téléchargement incomplet ({size} octets sur {announced}).");
            var h256 = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            var h1 = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
            var h5 = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
            if (!flight.Expected.MatchedBy(h256, h1, h5))
            {
                lock (_lock) _totals.HashMismatches++;
                throw new FlightException($"Empreinte différente de celle publiée par la source pour {Path.GetFileName(flight.Url.AbsolutePath)} : fichier refusé.");
            }
            committed = Commit(flight.TempPath, h256, h1, h5, size, flight.Key, flight.Url, "file", flight.ContentType, 200);
            lock (_lock) _totals.BytesFromOrigin += size;
            _log.LogInformation("Cache de pilotes : {Url} téléchargé ({Megabytes:0.0} Mo).", flight.Url, size / 1048576.0);
        }
        catch (Exception ex)
        {
            flight.Error = ex switch
            {
                FlightException f => f.Message,
                CacheRefusedException r => r.Message,
                OperationCanceledException when ct.IsCancellationRequested => "Console en cours d'arrêt.",
                OperationCanceledException => "La source officielle ne répond plus (délai dépassé).",
                _ => $"Source officielle injoignable : {ex.Message}",
            };
            lock (_lock)
            {
                if (ex is FlightException { Bypass: true }) _totals.Bypassed++;
                else _totals.Errors++;
                _dirty = true;
            }
            if (ex is CacheRefusedException) flight.OriginStatus = 403;
            _log.LogWarning("Cache de pilotes : échec pour {Url} — {Error}", flight.Url, flight.Error);
            TryDelete(flight.TempPath);
        }
        finally
        {
            flight.Headers.TrySetResult(false);
            flight.Done.TrySetResult(committed);
            flight.Pulse();
            lock (_lock) _flights.Remove(flight.Key);
            _originSlots.Release();
        }
    }

    private sealed class FlightException(string message, bool bypass = false) : Exception(message)
    {
        public bool Bypass { get; } = bypass;
    }

    /// <summary>
    /// Envoie au poste le fichier en cours de téléchargement, au fil de l'écriture (pas de mise en mémoire : quelques
    /// tampons de 1 Mo). Les derniers octets ne partent qu'une fois l'empreinte vérifiée ; en cas d'échec, la connexion est
    /// interrompue (le poste voit un transfert incomplet et télécharge directement).
    /// </summary>
    public async Task ServeFlightAsync(HttpContext http, Flight flight, string cacheHeader, CancellationToken ct)
    {
        bool ok;
        try
        {
            ok = await flight.Headers.Task.WaitAsync(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ok = false;
            flight.Error ??= "La source officielle ne répond pas.";
        }
        if (!ok)
        {
            await WriteFlightErrorAsync(http, flight, ct).ConfigureAwait(false);
            return;
        }

        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = flight.ContentType ?? "application/octet-stream";
        if (flight.Length is { } len) response.ContentLength = len;
        response.Headers[DriverCacheEndpoints.CacheHeader] = cacheHeader;
        response.Headers.AcceptRanges = "none"; // pendant le téléchargement : flux complet seulement (Range servi une fois en cache)
        // En-têtes envoyés tout de suite : le poste sait que le transfert a commencé, même si la source est lente.
        await response.StartAsync(ct).ConfigureAwait(false);

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        FileStream? input = null;
        long position = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var done = flight.Done.Task.IsCompleted;
                if (done)
                {
                    var final = flight.Done.Task.Result;
                    if (final is null)
                    {
                        http.Abort();
                        return;
                    }
                    input ??= OpenShared(final);
                    if (input is null)
                    {
                        http.Abort();
                        return;
                    }
                    input.Position = position;
                    int n;
                    while ((n = await input.ReadAsync(buffer.AsMemory(0, ChunkSize), ct).ConfigureAwait(false)) > 0)
                    {
                        await response.Body.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        position += n;
                    }
                    RecordServed(position, fromCache: cacheHeader != "miss");
                    return;
                }

                var available = flight.Written - Holdback - position;
                if (available > 0)
                {
                    input ??= OpenShared(flight.TempPath);
                    if (input is null)
                    {
                        // Fichier temporaire déjà rangé : la fin du téléchargement est imminente.
                        await Task.WhenAny(flight.Done.Task, Task.Delay(50, ct)).ConfigureAwait(false);
                        continue;
                    }
                    input.Position = position;
                    var n = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(available, ChunkSize)), ct).ConfigureAwait(false);
                    if (n > 0)
                    {
                        await response.Body.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        position += n;
                        continue;
                    }
                }
                await Task.WhenAny(flight.NextPulse, Task.Delay(250, ct)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && ct.IsCancellationRequested)
        {
            // Poste déconnecté : le téléchargement continue pour les autres.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (input is not null) await input.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task WriteFlightErrorAsync(HttpContext http, Flight flight, CancellationToken ct)
    {
        var status = flight.OriginStatus switch
        {
            403 when flight.Error?.Contains("Redirection refusée", StringComparison.Ordinal) == true => StatusCodes.Status403Forbidden,
            404 or 410 => StatusCodes.Status404NotFound,
            _ when flight.Error?.Contains("Espace disque", StringComparison.Ordinal) == true ||
                   flight.Error?.Contains("plus volumineux", StringComparison.Ordinal) == true => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status502BadGateway,
        };
        http.Response.StatusCode = status;
        http.Response.Headers[DriverCacheEndpoints.CacheHeader] = status == 503 ? "bypass" : status == 403 ? "refused" : "error";
        if (flight.OriginStatus > 0) http.Response.Headers[DriverCacheEndpoints.OriginStatusHeader] = flight.OriginStatus.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await http.Response.WriteAsJsonAsync(new Security.ApiError(flight.Error ?? "Échec du téléchargement."), ct).ConfigureAwait(false);
    }

    private static FileStream? OpenShared(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Octets envoyés aux postes ; ceux servis depuis le cache sont autant de téléchargements Internet évités.</summary>
    public void RecordServed(long bytes, bool fromCache)
    {
        if (bytes <= 0 || !fromCache) return;
        lock (_lock)
        {
            _totals.BytesServedFromCache += bytes;
            _dirty = true;
        }
    }

    /// <summary>Le fichier rangé sous son empreinte (doublon : le fichier temporaire est supprimé) et l'entrée mise à jour.</summary>
    private string Commit(string tempPath, string sha256, string sha1, string md5, long size, string key, Uri url, string kind, string? contentType, int status)
    {
        var path = BlobPath(sha256);
        lock (_lock)
        {
            var now = Now;
            if (_blobs.TryGetValue(sha256, out var existing) && File.Exists(path))
            {
                TryDelete(tempPath);
                existing.LastAccess = now;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Move(tempPath, path, overwrite: true);
                if (_blobs.Remove(sha256, out var stale)) _sizeBytes -= stale.Size;
                _blobs[sha256] = new BlobInfo { Sha256 = sha256, Sha1 = sha1, Md5 = md5, Size = size, CreatedAt = now, LastAccess = now };
                _sizeBytes += size;
            }
            var previousHits = _entries.TryGetValue(key, out var old) ? old.Hits : 0;
            _entries[key] = new CacheEntry
            {
                Key = key, Url = url.ToString(), Kind = kind, Sha256 = sha256, Size = size, ContentType = contentType, Status = status,
                FetchedAt = now, LastAccess = now, Hits = previousHits,
            };
            _dirty = true;
            DropUnreferencedBlobs();
            MakeRoom(0, Settings);
        }
        SaveIfDirty();
        return path;
    }

    // ------------------------------------------------------------------ catalogues et métadonnées

    public enum CatalogVerdict { Cacheable, PassThrough, Throttled }

    /// <summary>Demande de catalogue : clé normalisée, adresse officielle, construction de la requête, analyse de la réponse.</summary>
    public sealed record CatalogRequest(string Key, Uri Url, Func<Uri, HttpRequestMessage> Build, Func<string, CatalogVerdict>? Classify = null,
        bool MicrosoftCatalog = false);

    /// <summary>Réponse à servir au poste.</summary>
    public sealed record CatalogResult(int Status, string CacheState, string? Path = null, byte[]? Body = null, string? ContentType = null,
        DateTimeOffset? FetchedAt = null, int? RetryAfterSeconds = null, string? Error = null, int? OriginStatus = null);

    private sealed record CatalogFetch(CatalogResult Result);

    private readonly SemaphoreSlim _msCatalogSlots = new(2);
    private DateTimeOffset _msCatalogLast = DateTimeOffset.MinValue;
    private DateTimeOffset _msCatalogPausedUntil = DateTimeOffset.MinValue;
    /// <summary>Espacement minimal des requêtes de la console vers le Microsoft Update Catalog.</summary>
    internal static TimeSpan MicrosoftCatalogInterval { get; set; } = TimeSpan.FromMilliseconds(400);
    internal static readonly TimeSpan MicrosoftCatalogPause = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Catalogue ou métadonnée : servi depuis le cache tant qu'il a moins de CatalogTtlHours heures, sinon redemandé (une
    /// seule fois pour toutes les demandes simultanées). Une page « Too many search requests » du Microsoft Update Catalog
    /// n'est jamais mise en cache : la console fait une pause de 3 minutes et répond 429 (ou sert la copie périmée).
    /// </summary>
    public async Task<CatalogResult> GetCatalogAsync(CatalogRequest request, CancellationToken ct)
    {
        if (!OfficialHosts.IsOfficialMetadata(request.Url))
            return new CatalogResult(403, "refused", Error: $"{request.Url.Host} ne fait pas partie des sources officielles autorisées.");
        var settings = Settings;
        var ttl = TimeSpan.FromHours(settings.CatalogTtlHours);
        Task<CatalogFetch> task;
        lock (_lock)
        {
            if (Fresh(request.Key, ttl) is { } hit)
            {
                _totals.CatalogHits++;
                _dirty = true;
                return hit;
            }
            if (!_catalogFlights.TryGetValue(request.Key, out task!))
            {
                _totals.CatalogMisses++;
                _dirty = true;
                task = Task.Run(() => FetchCatalogAsync(request, _shutdown.Token));
                _catalogFlights[request.Key] = task;
                _ = task.ContinueWith(_ => { lock (_lock) _catalogFlights.Remove(request.Key); }, TaskScheduler.Default);
            }
            else
            {
                _totals.CatalogHits++;
            }
        }
        var fetched = await task.WaitAsync(ct).ConfigureAwait(false);
        return fetched.Result;
    }

    /// <summary>Entrée encore valable (sous verrou) : copie du cache, ou absence mémorisée (404).</summary>
    private CatalogResult? Fresh(string key, TimeSpan ttl)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;
        var age = Now - entry.FetchedAt;
        if (entry.Status == 404)
            return age < (ttl < NegativeTtl ? ttl : NegativeTtl)
                ? new CatalogResult(404, "hit", FetchedAt: entry.FetchedAt, OriginStatus: 404, Error: "Absent de la source officielle.")
                : null;
        if (age >= ttl || entry.Sha256 is not { } sha || !_blobs.TryGetValue(sha, out var blob) || !File.Exists(BlobPath(sha))) return null;
        entry.LastAccess = blob.LastAccess = Now;
        entry.Hits++;
        return new CatalogResult(200, "hit", BlobPath(sha), ContentType: entry.ContentType, FetchedAt: entry.FetchedAt);
    }

    /// <summary>Copie périmée encore présente (servie si la source est injoignable ou limite les requêtes).</summary>
    private CatalogResult? Stale(string key, string reason)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Status != 200 || entry.Sha256 is not { } sha || !_blobs.ContainsKey(sha) ||
                !File.Exists(BlobPath(sha))) return null;
            entry.LastAccess = Now;
            entry.Hits++;
            _log.LogInformation("Cache de pilotes : copie périmée de {Url} servie ({Reason}).", entry.Url, reason);
            return new CatalogResult(200, "stale", BlobPath(sha), ContentType: entry.ContentType, FetchedAt: entry.FetchedAt);
        }
    }

    private async Task<CatalogResult> FetchOnceAsync(CatalogRequest request, CancellationToken ct)
    {
        var tmp = Path.Combine(TempRoot, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var response = await _origin.SendAsync(request.Url, request.Build, u => OfficialHosts.IsOfficialMetadata(u), timeout.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is 404 or 410)
            {
                lock (_lock)
                {
                    _entries[request.Key] = new CacheEntry
                    {
                        Key = request.Key, Url = request.Url.ToString(), Kind = "catalog", Status = 404, FetchedAt = Now, LastAccess = Now,
                    };
                    _dirty = true;
                }
                return new CatalogResult(404, "miss", OriginStatus: 404, Error: "Absent de la source officielle.");
            }
            if (!response.IsSuccessStatusCode)
                return Stale(request.Key, $"source : {code}") ??
                       new CatalogResult(502, "error", OriginStatus: code, Error: $"La source officielle a répondu {code} ({request.Url.Host}).");
            if (response.Content.Headers.ContentLength > MaxCatalogBytes)
                return new CatalogResult(503, "bypass", Error: "Catalogue trop volumineux pour la console.");

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            long size = 0;
            await using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int n;
                    while ((n = await input.ReadAsync(buffer.AsMemory(0, 81920), timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        size += n;
                        if (size > MaxCatalogBytes) return new CatalogResult(503, "bypass", Error: "Catalogue trop volumineux pour la console.");
                        sha256.AppendData(buffer, 0, n);
                        sha1.AppendData(buffer, 0, n);
                        md5.AppendData(buffer, 0, n);
                        await output.WriteAsync(buffer.AsMemory(0, n), timeout.Token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            var verdict = CatalogVerdict.Cacheable;
            if (request.Classify is not null)
            {
                var text = size <= 16 * 1024 * 1024 ? await File.ReadAllTextAsync(tmp, timeout.Token).ConfigureAwait(false) : "";
                verdict = request.Classify(text);
            }
            switch (verdict)
            {
                case CatalogVerdict.Throttled:
                    return Throttled(request);
                case CatalogVerdict.PassThrough:
                    // Réponse valide mais non garantie (page inattendue) : transmise telle quelle, jamais mise en cache.
                    return new CatalogResult(200, "bypass", Body: await File.ReadAllBytesAsync(tmp, timeout.Token).ConfigureAwait(false),
                        ContentType: contentType, FetchedAt: Now);
            }
            if (!HasRoomFor(size, Settings))
            {
                // Volume presque plein : rien n'est rangé, la réponse est seulement relayée (petits catalogues).
                if (size > 64 * 1024 * 1024) return new CatalogResult(503, "bypass", Error: "Espace disque insuffisant sur la console : cache suspendu.");
                return new CatalogResult(200, "bypass", Body: await File.ReadAllBytesAsync(tmp, timeout.Token).ConfigureAwait(false),
                    ContentType: contentType, FetchedAt: Now);
            }
            var path = Commit(tmp, Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant(),
                Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(), Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
                size, request.Key, request.Url, "catalog", contentType, 200);
            lock (_lock) _totals.BytesFromOrigin += size;
            return new CatalogResult(200, "miss", path, ContentType: contentType, FetchedAt: Now);
        }
        catch (CacheRefusedException ex)
        {
            return new CatalogResult(403, "refused", Error: ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            lock (_lock)
            {
                _totals.Errors++;
                _dirty = true;
            }
            var reason = ex is OperationCanceledException ? "délai dépassé" : ex.Message;
            _log.LogWarning("Cache de pilotes : catalogue {Url} injoignable — {Reason}", request.Url, reason);
            return Stale(request.Key, reason) ?? new CatalogResult(502, "error", Error: $"Source officielle injoignable : {reason}");
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private CatalogResult Throttled(CatalogRequest request)
    {
        lock (_lock)
        {
            _msCatalogPausedUntil = Now + MicrosoftCatalogPause;
            _dirty = true;
        }
        _log.LogWarning("Microsoft Update Catalog : « Too many search requests » — pause de {Minutes} minutes (réponse jamais mise en cache).",
            MicrosoftCatalogPause.TotalMinutes);
        return Stale(request.Key, "catalogue Microsoft saturé") ?? RateLimited();
    }

    private CatalogResult RateLimited()
    {
        var wait = _msCatalogPausedUntil - Now;
        return new CatalogResult(429, "throttled", RetryAfterSeconds: Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)),
            Error: "Le Microsoft Update Catalog limite temporairement les recherches.");
    }

    private async Task<CatalogFetch> FetchCatalogAsync(CatalogRequest request, CancellationToken ct)
    {
        if (!request.MicrosoftCatalog) return new CatalogFetch(await FetchOnceAsync(request, ct).ConfigureAwait(false));

        // Microsoft Update Catalog : limité par adresse IP publique. La console fait ses requêtes une à deux à la fois,
        // espacées, et suspend tout pendant 3 minutes après une page « Too many search requests ».
        if (Now < _msCatalogPausedUntil) return new CatalogFetch(Stale(request.Key, "catalogue Microsoft saturé") ?? RateLimited());
        if (!await _msCatalogSlots.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false))
            return new CatalogFetch(Stale(request.Key, "file d'attente") ??
                                    new CatalogResult(503, "bypass", RetryAfterSeconds: 30, Error: "File d'attente du Microsoft Update Catalog pleine sur la console."));
        try
        {
            if (Now < _msCatalogPausedUntil) return new CatalogFetch(Stale(request.Key, "catalogue Microsoft saturé") ?? RateLimited());
            TimeSpan wait;
            lock (_lock)
            {
                var next = _msCatalogLast + MicrosoftCatalogInterval;
                var now = Now;
                wait = next > now ? next - now : TimeSpan.Zero;
                _msCatalogLast = now + wait;
            }
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            return new CatalogFetch(await FetchOnceAsync(request, ct).ConfigureAwait(false));
        }
        finally
        {
            _msCatalogSlots.Release();
        }
    }

    // ------------------------------------------------------------------ taille, espace disque, éviction

    /// <summary>
    /// Reste-t-il assez de place sur le volume pour <paramref name="incoming"/> octets ? Le cache n'entame jamais l'espace
    /// libre minimal (MinFreeGb, et au moins 5 % du volume) : la base, ses sauvegardes et son compactage en ont besoin.
    /// </summary>
    private bool HasRoomFor(long incoming, DriverCacheSettings settings)
    {
        var (free, total) = SafeDisk();
        var reserve = Reserve(settings, total);
        var ok = free - incoming >= reserve;
        if (!ok && !_diskWarned)
        {
            _diskWarned = true;
            _log.LogWarning("Cache de pilotes suspendu : {Free:0.0} Go libres sur le volume (minimum {Reserve:0.0} Go). Les postes téléchargent directement.",
                free / 1073741824.0, reserve / 1073741824.0);
        }
        else if (ok) _diskWarned = false;
        return ok;
    }

    /// <summary>Espace que le cache laisse toujours libre : MinFreeGb, et au moins 5 % du volume.</summary>
    internal static long Reserve(DriverCacheSettings settings, long volumeBytes) => Math.Max(settings.MinFreeBytes, volumeBytes / 20);

    private (long Free, long Total) SafeDisk()
    {
        try
        {
            return _disk(Root);
        }
        catch (Exception ex)
        {
            // Volume impossible à mesurer : par prudence, rien n'est mis en cache.
            _log.LogDebug("Espace disque inconnu pour {Root} : {Error}", Root, ex.Message);
            return (0, 0);
        }
    }

    private static (long Free, long Total) DiskSpace(string path)
    {
        var drive = new DriveInfo(path);
        return (drive.AvailableFreeSpace, drive.TotalSize);
    }

    /// <summary>Éviction (sous verrou) des fichiers les moins récemment demandés jusqu'à laisser la place à <paramref name="incoming"/> octets.</summary>
    private void MakeRoom(long incoming, DriverCacheSettings settings)
    {
        if (_sizeBytes + incoming <= settings.MaxBytes) return;
        var target = settings.MaxBytes - incoming;
        foreach (var blob in _blobs.Values.OrderBy(b => b.LastAccess).ToList())
        {
            if (_sizeBytes <= target) break;
            if (!TryDelete(BlobPath(blob.Sha256))) continue; // en cours d'envoi (Windows) : réessayé plus tard
            _blobs.Remove(blob.Sha256);
            _sizeBytes -= blob.Size;
            foreach (var key in _entries.Where(e => e.Value.Sha256 == blob.Sha256).Select(e => e.Key).ToList()) _entries.Remove(key);
            _totals.Evictions++;
            _dirty = true;
        }
    }

    /// <summary>Réapplique la taille maximale (après une modification des réglages) et oublie les absences (404) expirées.</summary>
    public void EnforceLimits()
    {
        lock (_lock)
        {
            var now = Now;
            foreach (var key in _entries.Where(e => e.Value.Status == 404 && now - e.Value.FetchedAt > NegativeTtl).Select(e => e.Key).ToList())
                _entries.Remove(key);
            MakeRoom(0, Settings);
            _dirty = true;
        }
        SaveIfDirty();
    }

    private void DropUnreferencedBlobs()
    {
        var used = _entries.Values.Where(e => e.Sha256 is not null).Select(e => e.Sha256!).ToHashSet(StringComparer.Ordinal);
        foreach (var blob in _blobs.Values.Where(b => !used.Contains(b.Sha256)).ToList())
        {
            if (!TryDelete(BlobPath(blob.Sha256))) continue;
            _blobs.Remove(blob.Sha256);
            _sizeBytes -= blob.Size;
        }
    }

    /// <summary>Vide le cache : « all », « files » (paquets) ou « catalogs ». Les téléchargements en cours se terminent normalement.</summary>
    public (int Entries, long Bytes) Purge(string scope)
    {
        int removed;
        long before;
        lock (_lock)
        {
            before = _sizeBytes;
            var keys = _entries.Where(e => scope switch
            {
                "files" => e.Value.Kind == "file",
                "catalogs" => e.Value.Kind == "catalog",
                _ => true,
            }).Select(e => e.Key).ToList();
            foreach (var key in keys) _entries.Remove(key);
            removed = keys.Count;
            DropUnreferencedBlobs();
            if (scope is "all" or "catalogs") _msCatalogPausedUntil = DateTimeOffset.MinValue;
            _dirty = true;
        }
        SaveIfDirty();
        lock (_lock) return (removed, before - _sizeBytes);
    }

    // ------------------------------------------------------------------ statistiques

    public DriverCacheStats Stats()
    {
        var settings = Settings;
        var (free, total) = SafeDisk();
        lock (_lock)
        {
            return new DriverCacheStats(settings.Enabled, settings.Catalogs, _sizeBytes, settings.MaxBytes,
                _entries.Values.Count(e => e.Kind == "file"), _entries.Values.Count(e => e.Kind == "catalog"), _blobs.Count,
                _totals.FileHits, _totals.FileMisses, _totals.CatalogHits, _totals.CatalogMisses, _totals.BytesServedFromCache, _totals.BytesFromOrigin,
                _totals.Refused, _totals.Bypassed, _totals.Errors, _totals.HashMismatches, _flights.Count, free, total,
                free < Reserve(settings, total),
                _msCatalogPausedUntil > Now ? _msCatalogPausedUntil : null);
        }
    }

    public void RecordRefused()
    {
        lock (_lock)
        {
            _totals.Refused++;
            _dirty = true;
        }
    }

    /// <summary>Les entrées les plus utiles (octets évités = taille × demandes servies par le cache).</summary>
    public List<DriverCacheTopEntry> Top(int count)
    {
        lock (_lock)
        {
            return _entries.Values.Where(e => e.Status == 200)
                .OrderByDescending(e => e.Size * e.Hits).ThenByDescending(e => e.LastAccess).Take(count)
                .Select(e => new DriverCacheTopEntry(e.Url, e.Kind, e.Size, e.Hits, e.Size * e.Hits, e.FetchedAt, e.LastAccess)).ToList();
        }
    }

    // ------------------------------------------------------------------ index sur disque

    internal sealed class CacheEntry
    {
        public string Key { get; set; } = "";
        public string Url { get; set; } = "";
        /// <summary>« file » (paquet) ou « catalog » (catalogue, métadonnée).</summary>
        public string Kind { get; set; } = "file";
        public string? Sha256 { get; set; }
        public long Size { get; set; }
        public string? ContentType { get; set; }
        /// <summary>200 (contenu en cache) ou 404 (absence mémorisée, catalogues seulement).</summary>
        public int Status { get; set; } = 200;
        public DateTimeOffset FetchedAt { get; set; }
        public DateTimeOffset LastAccess { get; set; }
        public long Hits { get; set; }
    }

    internal sealed class BlobInfo
    {
        public string Sha256 { get; set; } = "";
        public string Sha1 { get; set; } = "";
        public string Md5 { get; set; } = "";
        public long Size { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastAccess { get; set; }
    }

    internal sealed class CacheTotals
    {
        public long FileHits { get; set; }
        public long FileMisses { get; set; }
        public long CatalogHits { get; set; }
        public long CatalogMisses { get; set; }
        public long BytesServedFromCache { get; set; }
        public long BytesFromOrigin { get; set; }
        public long Refused { get; set; }
        public long Bypassed { get; set; }
        public long Errors { get; set; }
        public long HashMismatches { get; set; }
        public long Evictions { get; set; }
    }

    private sealed class CacheIndex
    {
        public int Version { get; set; } = 1;
        public List<CacheEntry> Entries { get; set; } = new();
        public List<BlobInfo> Blobs { get; set; } = new();
        public CacheTotals Totals { get; set; } = new();
    }

    /// <summary>
    /// Relit l'index et le rapproche du disque : entrées sans fichier oubliées, fichiers sans entrée (ou dont le nom n'est
    /// pas l'empreinte annoncée par l'index) supprimés, fichiers temporaires d'un téléchargement interrompu supprimés.
    /// </summary>
    private void Load()
    {
        foreach (var tmp in Directory.EnumerateFiles(TempRoot)) TryDelete(tmp);
        CacheIndex? index = null;
        try
        {
            if (File.Exists(IndexPath)) index = JsonSerializer.Deserialize<CacheIndex>(File.ReadAllText(IndexPath), IndexJson);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log.LogWarning("Index du cache de pilotes illisible ({Error}) : cache vidé.", ex.Message);
        }
        index ??= new CacheIndex();
        lock (_lock)
        {
            foreach (var blob in index.Blobs)
            {
                if (blob.Sha256.Length != 64 || !blob.Sha256.All(Uri.IsHexDigit)) continue;
                var path = BlobPath(blob.Sha256);
                if (!File.Exists(path) || new FileInfo(path).Length != blob.Size) continue;
                _blobs[blob.Sha256] = blob;
                _sizeBytes += blob.Size;
            }
            foreach (var entry in index.Entries)
                if (entry.Status == 404 || (entry.Sha256 is not null && _blobs.ContainsKey(entry.Sha256)))
                    _entries[entry.Key] = entry;
            CopyTotals(index.Totals, _totals);
            DropUnreferencedBlobs();
        }
        foreach (var dir in Directory.EnumerateDirectories(BlobRoot))
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            bool known;
            lock (_lock) known = _blobs.ContainsKey(Path.GetFileName(file));
            if (!known) TryDelete(file);
        }
        _dirty = true;
        SaveIfDirty();
    }

    private static void CopyTotals(CacheTotals from, CacheTotals to)
    {
        to.FileHits = from.FileHits; to.FileMisses = from.FileMisses; to.CatalogHits = from.CatalogHits; to.CatalogMisses = from.CatalogMisses;
        to.BytesServedFromCache = from.BytesServedFromCache; to.BytesFromOrigin = from.BytesFromOrigin; to.Refused = from.Refused;
        to.Bypassed = from.Bypassed; to.Errors = from.Errors; to.HashMismatches = from.HashMismatches; to.Evictions = from.Evictions;
    }

    internal void SaveIfDirty()
    {
        lock (_saveLock)
        {
            string json;
            lock (_lock)
            {
                if (!_dirty) return;
                _dirty = false;
                var totals = new CacheTotals();
                CopyTotals(_totals, totals);
                json = JsonSerializer.Serialize(new CacheIndex { Entries = _entries.Values.ToList(), Blobs = _blobs.Values.ToList(), Totals = totals }, IndexJson);
            }
            try
            {
                var tmp = IndexPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, IndexPath, overwrite: true);
            }
            catch (IOException ex)
            {
                lock (_lock) _dirty = true;
                _log.LogWarning("Index du cache de pilotes non enregistré : {Error}", ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------ utilitaires

    /// <summary>Adresse normalisée (schéma et hôte en minuscules, sans fragment) : clé du cache.</summary>
    internal static string Normalize(Uri url) =>
        new UriBuilder(url) { Scheme = url.Scheme.ToLowerInvariant(), Host = url.Host.ToLowerInvariant(), Fragment = "" }.Uri.AbsoluteUri;

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _saveTimer.Dispose();
        SaveIfDirty();
        _shutdown.Dispose();
    }

}
