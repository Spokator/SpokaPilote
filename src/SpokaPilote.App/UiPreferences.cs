using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpokaPilote.App;

/// <summary>
/// Préférences d'interface propres à l'utilisateur Windows (premier lancement, langue...), stockées dans
/// %LocalAppData%\SpokaPilote\ui.json — distinctes de la configuration du poste (%ProgramData%), partagée et déployable.
/// En mode portable, elles sont rangées sur la clé (voir AppPaths.UiPreferencesFile).
/// </summary>
public sealed class UiPreferences
{
    public bool OnboardingCompleted { get; set; }
    /// <summary>« fr », « en » ou null (langue de Windows).</summary>
    public string? Language { get; set; }
    /// <summary>Dernière page affichée (« accueil », « pilotes »... voir MainViewModel.PageKeys).</summary>
    public string? LastPage { get; set; }
    /// <summary>
    /// Type d'analyse choisi à l'Accueil (avant d'analyser) : false = analyse rapide, true = analyse complète (le catalogue
    /// Microsoft est aussi interrogé pour les pilotes de fabricant déjà installés). Repris par F5 et « Analyser à nouveau ».
    /// </summary>
    public bool DeepScan { get; set; }
    /// <summary>
    /// Ancien index d'onglet (versions 3.1 et antérieures, 8 onglets) : lu une fois pour retrouver la page, puis oublié.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LastTab { get; set; }

    /// <summary>Page à rouvrir : <see cref="LastPage"/>, sinon conversion de l'ancien <see cref="LastTab"/>.</summary>
    [JsonIgnore]
    public string? StartPage => LastPage ?? (LastTab is { } tab ? FromLegacyTab(tab) : null);

    /// <summary>
    /// Ancienne navigation (Ce poste, Pilotes, Périphériques, Imprimantes, Dépôt de pilotes, Historique, Journal,
    /// Paramètres) → nouvelle. Imprimantes, Dépôt et Journal sont désormais dans Outils.
    /// </summary>
    public static string? FromLegacyTab(int index) => index switch
    {
        0 => "accueil",
        1 => "pilotes",
        2 => "peripheriques",
        3 or 4 or 6 => "outils",
        5 => "historique",
        7 => "parametres",
        _ => null,
    };

    /// <summary>%LocalAppData%\SpokaPilote\ui.json ; en mode portable, Data\ui.json sur la clé (rien n'est écrit sur le poste).</summary>
    private static string FilePath => Core.Infrastructure.AppPaths.UiPreferencesFile;

    public static UiPreferences Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(FilePath)) ?? new UiPreferences();
        }
        catch
        {
            // préférences illisibles : valeurs par défaut
        }
        return new UiPreferences();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // non bloquant
        }
    }
}
