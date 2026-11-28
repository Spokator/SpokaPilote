using System.Globalization;
using System.Text.Json;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Online;

/// <summary>
/// Fraîcheur des catalogues .cab signés relayés par la console (Dell, HP). Un ancien catalogue reste validement signé :
/// une console compromise pourrait le resservir indéfiniment (« rejeu ») pour masquer une mise à jour de sécurité ou
/// remettre en avant un ancien paquet. Le poste retient donc, par adresse de catalogue, la date de signature
/// (horodatage Authenticode) du plus récent catalogue accepté, et la dernière fois que la source elle-même l'a servi.
/// Une copie de la console n'est acceptée que si elle n'est PAS plus ancienne que le dernier catalogue accepté, et si elle
/// a été signée il y a moins de <see cref="MaxAge"/> — ou, pour un catalogue que l'éditeur ne republie pas souvent, si la
/// source a servi cette même version (ou une plus ancienne) directement au poste il y a moins de <see cref="MaxAge"/>.
/// Sinon : téléchargement direct (qui renouvelle cette confirmation). Le rejeu est ainsi borné à <see cref="MaxAge"/>.
/// </summary>
public sealed class CatalogFreshness
{
    /// <summary>Âge maximal d'un catalogue servi par la console sans confirmation récente de la source.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private sealed record Entry(DateTimeOffset SignedAt, DateTimeOffset? ConfirmedAt);

    private static readonly Lock FileLock = new();
    private readonly string _path;
    private readonly TimeProvider _clock;

    /// <param name="path">Fichier d'état (défaut : <c>catalog-freshness.json</c> du cache).</param>
    public CatalogFreshness(string? path = null, TimeProvider? clock = null)
    {
        _path = path ?? Path.Combine(AppPaths.Cache, "catalog-freshness.json");
        _clock = clock ?? TimeProvider.System;
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>Clé : adresse sans requête ni fragment, en minuscules (les serveurs Dell et HP ignorent la casse).</summary>
    internal static string Key(Uri official) => official.GetLeftPart(UriPartial.Path).ToLowerInvariant();

    /// <summary>Motif du refus d'un catalogue signé le <paramref name="signedAt"/> servi par la console, ou null s'il est accepté.</summary>
    public string? CheckConsoleCopy(Uri official, DateTimeOffset? signedAt)
    {
        if (signedAt is not { } signed) return "catalogue sans date de signature (horodatage) : fraîcheur invérifiable";
        var known = Read().GetValueOrDefault(Key(official));
        if (known is not null && signed < known.SignedAt)
            return $"catalogue signé le {Format(signed)}, plus ancien que le dernier accepté (signé le {Format(known.SignedAt)}) : copie périmée";
        if (Now - signed <= MaxAge) return null;
        if (known is { ConfirmedAt: { } confirmed } && Now - confirmed <= MaxAge) return null;
        return $"catalogue signé le {Format(signed)}, il y a plus de {MaxAge.TotalDays:0} jours, sans confirmation récente de la source";
    }

    /// <summary>Catalogue plus ancien que le dernier accepté pour cette adresse (rejeu probable, et non simple vieillesse) ?</summary>
    public bool IsOlderThanAccepted(Uri official, DateTimeOffset signedAt) =>
        Read().GetValueOrDefault(Key(official)) is { } known && signedAt < known.SignedAt;

    /// <summary>Copie de la console acceptée : sa date de signature devient la référence si elle est plus récente.</summary>
    public void RecordConsoleCopy(Uri official, DateTimeOffset signedAt) => Update(official, known =>
        known is null ? new Entry(signedAt, null) : signedAt > known.SignedAt ? known with { SignedAt = signedAt } : known);

    /// <summary>
    /// Catalogue obtenu directement de la source : il fait foi (même s'il est plus ancien que la référence : l'éditeur a pu
    /// retirer une version), et confirme à cette date la version servie par la source.
    /// </summary>
    public void RecordDirect(Uri official, DateTimeOffset? signedAt)
    {
        if (signedAt is not { } signed) return;
        Update(official, _ => new Entry(signed, Now));
    }

    private void Update(Uri official, Func<Entry?, Entry> change)
    {
        lock (FileLock)
        {
            var all = Read();
            var key = Key(official);
            all[key] = change(all.GetValueOrDefault(key));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = $"{_path}.{DataProtection.RandomName(6)}.tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // État facultatif : sans lui, seule la limite d'âge s'applique (et le direct est plus souvent choisi).
                Log.Debug($"Fraîcheur des catalogues non enregistrée : {ex.Message}");
            }
        }
    }

    private Dictionary<string, Entry> Read()
    {
        lock (FileLock)
        {
            try
            {
                if (File.Exists(_path) &&
                    JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) is { } all)
                    return new Dictionary<string, Entry>(all, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                Log.Debug($"Fraîcheur des catalogues illisible ({ex.Message}) : limite d'âge seule.");
            }
            return new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
    }

    private static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
