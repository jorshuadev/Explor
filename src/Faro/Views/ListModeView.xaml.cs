using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Faro.Model;
using Faro.Services;

namespace Faro.Views;

/// <summary>Vista de lista (Ctrl+2): columnas ordenables y carpetas desplegables con el triángulo.</summary>
public partial class ListModeView : UserControl, IFileView
{
    private BrowserTab _tab;
    private readonly ItemsInteraction _interaction;
    private IFileViewHost Host => Window.GetWindow(this) as IFileViewHost;

    public ListModeView()
    {
        InitializeComponent();
        _interaction = new ItemsInteraction(List, Overlay, () => Host, () => ActiveFolder) { ItemsInRect = RowsInRect };
        List.PreviewKeyDown += OnKey;
        List.SelectionChanged += (_, _) => SaveSelection();
        foreach (var (col, key) in new[] { (ColName, "name"), (ColDate, "date"), (ColSize, "size"), (ColKind, "kind") })
            if (Settings.Current.ListColumnWidths.TryGetValue(key, out var w) && w > 30) col.Width = w;
        Unloaded += (_, _) => SaveWidths();
        Settings.Changed += UpdateHeaders;
        UpdateHeaders();
    }

    private void SaveWidths()
    {
        Settings.Current.ListColumnWidths["name"] = ColName.ActualWidth;
        Settings.Current.ListColumnWidths["date"] = ColDate.ActualWidth;
        Settings.Current.ListColumnWidths["size"] = ColSize.ActualWidth;
        Settings.Current.ListColumnWidths["kind"] = ColKind.ActualWidth;
    }

    private IEnumerable<FileItem> RowsInRect(Rect r)
    {
        var panel = ItemsInteraction.FindChild<VirtualizingStackPanel>(List);
        if (panel == null) yield break;
        foreach (UIElement child in panel.Children)
        {
            if (child is not ListViewItem lvi || lvi.DataContext is not FileItem fi) continue;
            var b = lvi.TransformToAncestor(List).TransformBounds(new Rect(0, 0, Math.Min(lvi.ActualWidth, ColName.ActualWidth), lvi.ActualHeight));
            if (b.IntersectsWith(r)) yield return fi;
        }
    }

    public void Attach(BrowserTab tab)
    {
        if (_tab != null) { _tab.ItemsReset -= RestoreSelection; _tab.PropertyChanged -= TabChanged; }
        _tab = tab;
        List.ItemsSource = tab?.Items;
        if (tab != null) { tab.ItemsReset += RestoreSelection; tab.PropertyChanged += TabChanged; }
        UpdateLocationColumn();
        RestoreSelection();
    }

    private void TabChanged(object s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.Location)) UpdateLocationColumn();
    }

    private void UpdateLocationColumn()
    {
        bool show = _tab?.Location != null && _tab.Location.Kind is LocationKind.Search or LocationKind.Tag or LocationKind.Trash or LocationKind.Recents;
        ColLocation.Width = show ? 160 : 0;
        if (_tab?.Location?.Kind == LocationKind.Trash) ((TextBlock)ColDate.Header).Text = "Fecha de eliminación";
        else UpdateHeaders();
    }

    private void UpdateHeaders()
    {
        var s = Settings.Current;
        foreach (var col in new[] { ColName, ColLocation, ColDate, ColSize, ColKind, ColCreated })
        {
            if (col.Header is not TextBlock tb) continue;
            var key = tb.Tag as string;
            var baseText = key switch
            {
                "Name" => "Nombre", "Location" => "Ubicación", "Modified" => "Fecha de modificación", "Size" => "Tamaño", "Kind" => "Tipo", "Created" => "Fecha de creación", _ => tb.Text
            };
            bool active = Enum.TryParse<SortField>(key, out var f) && f == s.SortBy;
            tb.Text = active ? baseText + (s.SortDescending ? "  ˅" : "  ˄") : baseText;
            tb.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader h || h.Column?.Header is not TextBlock tb) return;
        if (!Enum.TryParse<SortField>(tb.Tag as string, out var field)) return;
        var s = Settings.Current;
        if (s.SortBy == field) s.SortDescending = !s.SortDescending;
        else { s.SortBy = field; s.SortDescending = field is SortField.Modified or SortField.Size or SortField.Created; }
        Settings.NotifyChanged();
        _tab?.Resort();
    }

    private bool _restoring;
    private void SaveSelection()
    {
        if (_restoring || _tab == null) return;
        _tab.SelectedPaths.Clear();
        foreach (FileItem i in List.SelectedItems) _tab.SelectedPaths.Add(i.Path);
    }

    public void RestoreSelection()
    {
        if (_tab == null) return;
        _restoring = true;
        try
        {
            List.SelectedItems.Clear();
            FileItem first = null;
            if (_tab.SelectedPaths.Count > 0)
                foreach (var i in _tab.Items)
                    if (_tab.SelectedPaths.Contains(i.Path)) { List.SelectedItems.Add(i); first ??= i; }
            if (first != null) List.ScrollIntoView(first);
        }
        finally { _restoring = false; }
    }

    public IList<FileItem> SelectedItems => List.SelectedItems.Cast<FileItem>().OrderBy(i => List.Items.IndexOf(i)).ToList();
    public FileItem FocusedItem => (Keyboard.FocusedElement as ListViewItem)?.DataContext as FileItem ?? List.SelectedItem as FileItem;

    public string ActiveFolder
    {
        get
        {
            // en lista, si hay una sola carpeta desplegada seleccionada se crea dentro de la carpeta actual igualmente (como Finder)
            return _tab?.FolderPath;
        }
    }

    public void SelectItems(IEnumerable<FileItem> items, bool scroll = true)
    {
        List.SelectedItems.Clear();
        FileItem first = null;
        foreach (var i in items) { List.SelectedItems.Add(i); first ??= i; }
        if (first != null && scroll) { List.ScrollIntoView(first); ItemsInteraction.FocusItem(List, first); }
    }

    public void SelectAll() => List.SelectAll();

    public void FocusView()
    {
        if (List.SelectedItem is FileItem f) ItemsInteraction.FocusItem(List, f);
        else List.Focus();
    }

    public void BeginRename(FileItem item)
    {
        List.ScrollIntoView(item);
        item.IsEditing = true;
    }

    private void RenameBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag as string != "a")
        {
            tb.Tag = "a";
            RenameBox.Attach(tb, () => Host);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        var item = FocusedItem;
        if (item == null) return;
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (e.Key == Key.Right && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Alt)
        {
            if (item.CanExpand && !item.IsExpanded) Host?.ExpandToggle(item, true, alt);
            e.Handled = true;
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Alt)
        {
            if (item.IsExpanded) Host?.ExpandToggle(item, false);
            else if (item.ParentRow != null) { SelectItems(new[] { item.ParentRow }); }
            e.Handled = true;
        }
    }
}
