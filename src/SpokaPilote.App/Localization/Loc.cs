using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace SpokaPilote.App.Localization;

/// <summary>
/// Traduction de l'interface. Le français est la langue de référence (textes écrits directement dans le XAML et le
/// code) ; pour l'anglais, chaque texte français est remplacé par sa traduction trouvée dans Localization/en.json.
/// Un texte absent du dictionnaire reste en français : une nouvelle fonction n'a qu'à ajouter ses traductions.
/// </summary>
public static class Loc
{
    private static Dictionary<string, string> _dictionary = new(StringComparer.Ordinal);

    /// <summary>« fr » ou « en ».</summary>
    public static string Language { get; private set; } = "fr";
    public static bool IsEnglish => Language == "en";

    /// <summary>Langue choisie dans les préférences, sinon celle de Windows (français si Windows est en français).</summary>
    public static void Initialize(string? preferred)
    {
        Language = preferred is "fr" or "en"
            ? preferred
            : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? "fr" : "en";
        if (!IsEnglish) return;
        try
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream("SpokaPilote.App.Localization.en.json");
            if (stream is null) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (data is not null)
            {
                _dictionary = new Dictionary<string, string>(data, StringComparer.Ordinal);
                // Le XAML fusionne les espaces consécutifs du texte : on indexe aussi la forme normalisée.
                foreach (var (key, value) in data) _dictionary.TryAdd(Normalize(key), value);
            }
            var culture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch
        {
            Language = "fr";
        }
    }

    /// <summary>Traduit un texte français (renvoie le texte d'origine s'il n'a pas de traduction).</summary>
    public static string T(string french)
    {
        if (!IsEnglish || string.IsNullOrEmpty(french)) return french;
        if (_dictionary.TryGetValue(french, out var english)) return english;
        return _dictionary.TryGetValue(Normalize(french), out english) ? english : french;
    }

    private static string Normalize(string s) => System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");

    /// <summary>Traduit un format composite : T("{0} pilote(s)", n).</summary>
    public static string F(string frenchFormat, params object?[] args) => string.Format(T(frenchFormat), args);

    /// <summary>Traduit les textes statiques d'une fenêtre ou d'un élément (textes liés aux données ignorés).</summary>
    public static void Translate(DependencyObject root)
    {
        if (!IsEnglish) return;
        var visited = new HashSet<DependencyObject>();
        Walk(root, visited);
        Core.Infrastructure.Log.Debug($"Traduction : {visited.Count} élément(s) parcouru(s), {_translated} texte(s) traduit(s).");
    }

    private static int _translated;

    private static string Tr(string french)
    {
        var english = T(french);
        if (!ReferenceEquals(english, french)) _translated++;
        return english;
    }

    private static void Walk(DependencyObject node, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node)) return;
        TranslateNode(node);
        // Copie préalable : traduire un élément peut remplacer ses enfants logiques (texte, contenu).
        foreach (var child in LogicalTreeHelper.GetChildren(node).Cast<object>().ToList())
        {
            if (child is DependencyObject d) Walk(d, visited);
        }
        if (node is DataGrid grid)
        {
            foreach (var column in grid.Columns)
                if (column.Header is string h) column.Header = Tr(h);
        }
        if (node is ItemsControl items && items.ItemsSource is null)
        {
            foreach (var item in ((IEnumerable)items.Items).Cast<object>().ToList())
                if (item is DependencyObject d) Walk(d, visited);
        }
    }

    private static void TranslateNode(DependencyObject node)
    {
        switch (node)
        {
            case Window w when !IsBound(w, Window.TitleProperty):
                w.Title = Tr(w.Title);
                break;
            case TextBlock tb:
                if (!IsBound(tb, TextBlock.TextProperty) && tb.Inlines.Count <= 1 && !string.IsNullOrEmpty(tb.Text))
                    tb.Text = Tr(tb.Text);
                else
                    foreach (var run in tb.Inlines.OfType<Run>().ToList())
                        if (!IsBound(run, Run.TextProperty) && !string.IsNullOrWhiteSpace(run.Text))
                            run.Text = Tr(run.Text.Trim()) is var t && t != run.Text.Trim() ? Pad(run.Text, t) : run.Text;
                break;
            case HeaderedContentControl hc when hc.Header is string header && !IsBound(hc, HeaderedContentControl.HeaderProperty):
                hc.Header = Tr(header);
                break;
        }
        if (node is ContentControl cc && cc.Content is string content && !IsBound(cc, ContentControl.ContentProperty))
            cc.Content = Tr(content);
        if (node is FrameworkElement fe)
        {
            if (fe.ToolTip is string tip && !IsBound(fe, FrameworkElement.ToolTipProperty)) fe.ToolTip = Tr(tip);
            // Noms et aides des lecteurs d'écran : un texte lié aux données n'est pas remplacé (la liaison serait perdue).
            var name = AutomationProperties.GetName(fe);
            if (!string.IsNullOrEmpty(name) && !IsBound(fe, AutomationProperties.NameProperty)) AutomationProperties.SetName(fe, Tr(name));
            var help = AutomationProperties.GetHelpText(fe);
            if (!string.IsNullOrEmpty(help) && !IsBound(fe, AutomationProperties.HelpTextProperty)) AutomationProperties.SetHelpText(fe, Tr(help));
        }
    }

    private static string Pad(string original, string translated) =>
        (original.StartsWith(' ') ? " " : "") + translated + (original.EndsWith(' ') ? " " : "");

    private static bool IsBound(DependencyObject d, DependencyProperty p) => BindingOperations.IsDataBound(d, p);
}
