using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SpokaPilote.App;

/// <summary>
/// Visible quand la valeur est « vraie » : booléen vrai, texte non vide, nombre non nul, collection non vide, objet non
/// null. Paramètre « not » : l'inverse.
/// </summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            null => false,
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            double d => d != 0,
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter is "not") truthy = !truthy;
        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible quand la valeur (texte) est égale au paramètre (page affichée, état...).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Boutons radio liés à une valeur texte (filtres) : coché quand la valeur vaut le paramètre.</summary>
public sealed class EqualsToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter?.ToString() ?? "" : Binding.DoNothing;
}
