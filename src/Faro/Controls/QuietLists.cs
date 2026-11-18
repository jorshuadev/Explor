using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Faro.Controls;

/// <summary>
/// Listas de archivos sin árbol de accesibilidad por elemento. Cuando algún programa escucha UI Automation
/// (teclado táctil, herramientas de captura, extensiones…), WPF recorre TODAS las celdas tras cada
/// navegación para avisarle: 50-90 ms por carpeta y más en carpetas grandes. La lista sigue
/// exponiéndose como un único control; lo que se omite es una entrada por archivo.
/// </summary>
public sealed class QuietListBox : ListBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new QuietPeer(this);
}

/// <inheritdoc cref="QuietListBox"/>
public sealed class QuietListView : ListView
{
    protected override AutomationPeer OnCreateAutomationPeer() => new QuietPeer(this);
}

internal sealed class QuietPeer : FrameworkElementAutomationPeer
{
    public QuietPeer(FrameworkElement owner) : base(owner) { }
    protected override List<AutomationPeer> GetChildrenCore() => null;
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
    protected override string GetClassNameCore() => Owner.GetType().Name;
}
