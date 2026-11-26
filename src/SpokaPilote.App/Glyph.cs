using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace SpokaPilote.App;

/// <summary>
/// Icône décorative (police Segoe Fluent Icons) : invisible pour les lecteurs d'écran, qui lisent le libellé voisin
/// plutôt qu'un caractère privé sans signification.
/// </summary>
public sealed class Glyph : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => null!;
}
