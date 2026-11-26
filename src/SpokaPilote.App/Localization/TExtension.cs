using System.Windows.Markup;

namespace SpokaPilote.App.Localization;

/// <summary>
/// Texte traduit dans le XAML : <c>Text="{l:T 'Taille'}"</c>. Nécessaire dans les modèles (DataTemplate), que
/// <see cref="Loc.Translate"/> ne parcourt pas : ils sont créés plus tard, à l'affichage des données.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
