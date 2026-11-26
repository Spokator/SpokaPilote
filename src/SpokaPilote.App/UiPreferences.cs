using System.IO;
using System.Text.Json;

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
    public int LastTab { get; set; }

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
