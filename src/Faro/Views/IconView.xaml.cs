using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Faro.Controls;
using Faro.Model;
using Faro.Services;

namespace Faro.Views;

public partial class IconView : UserControl, IFileView
{
    private BrowserTab _tab;
    private readonly ItemsInteraction _interaction;
    private IFileViewHost Host => Window.GetWindow(this) as IFileViewHost;

    public IconView()
    {
        InitializeComponent();
        _interaction = new ItemsInteraction(List, Overlay, () => Host, () => ActiveFolder)
        {
            ItemsInRect = r =>
            {
                var panel = Panel;
                if (panel == null) return Enumerable.Empty<FileItem>();
                var tl = List.TranslatePoint(r.TopLeft, panel);
                var pr = new Rect(tl, r.Size);
                return panel.IndicesInRect(pr).Where(i => i < List.Items.Count).Select(i => (FileItem)List.Items[i]);
            }
        };
        List.PreviewKeyDown += OnKey;
        List.SelectionChanged += (_, _) => SaveSelection();
        SetIconSize(Settings.Current.IconSize);
        Loaded += (_, _) =>
        {
            var w = Window.GetWindow(this);
            if (w != null) { w.Activated -= OnWindowActivated; w.Activated += OnWindowActivated; }
        };
    }

    private void OnWindowActivated(object s, EventArgs e)
    {
        var p = Panel;
        if (p != null) p.SuppressBringIntoViewUntil = DateTime.Now.AddMilliseconds(400);
    }

    private VirtualizingWrapPanel Panel => ItemsInteraction.FindChild<VirtualizingWrapPanel>(List);

    /// <summary>Para pruebas: desplaza a una fracción (0 = arriba, 1 = abajo).</summary>
    public void ScrollToFraction(double f) { var p = Panel; if (p != null) p.SetVerticalOffset(f * Math.Max(0, p.ExtentHeight - p.ViewportHeight)); }

    public string DebugScroll()
    {
        var p = Panel; if (p == null) return "sin panel";
        var sv = ItemsInteraction.FindScrollViewer(List);
        var kids = p.Children.Cast<UIElement>().Select(k => (k as FrameworkElement)?.DataContext is FileItem fi ? $"{List.Items.IndexOf(fi)}@{k.TranslatePoint(new Point(), p).Y:0}" : "?").ToList();
        return $"offset={p.VerticalOffset:0} extent={p.ExtentHeight:0} viewport={p.ViewportHeight:0} sv={sv?.VerticalOffset:0} hijos={kids.Count} primeros=[{string.Join(", ", kids.Take(4))}] últimos=[{string.Join(", ", kids.TakeLast(2))}]";
    }

    public void SetIconSize(double s)
    {
        Resources["IconSize"] = s;
        Resources["LabelWidth"] = Math.Max(s + 34, 96);
        Resources["NameWidth"] = Math.Max(s + 34, 96) - 10;
        var p = Panel;
        // en «Este equipo» cabe además la barra de espacio de cada disco
        double w = Math.Max(s + 40, 100), h = s + 8 + 44 + 8 + (_tab?.Location?.Kind == LocationKind.Computer ? 22 : 0);
        if (p != null) { p.ItemWidth = w; p.ItemHeight = h; }
        else Dispatcher.BeginInvoke(() => { var pp = Panel; if (pp != null) { pp.ItemWidth = w; pp.ItemHeight = h; } }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void Attach(BrowserTab tab)
    {
        if (_tab != null) { _tab.ItemsReset -= RestoreSelection; _tab.ItemsReset -= FitCells; }
        _tab = tab;
        List.ItemsSource = tab?.Items;
        if (tab != null) tab.ItemsReset += RestoreSelection;
        if (tab != null) tab.ItemsReset += FitCells;
        RestoreSelection();
    }

    private bool _wasComputer;
    private void FitCells()
    {
        bool c = _tab?.Location?.Kind == LocationKind.Computer;
        if (c != _wasComputer && Resources["IconSize"] is double s) { _wasComputer = c; SetIconSize(s); }
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
            if (_tab.SelectedPaths.Count > 0)
            {
                FileItem first = null;
                foreach (var i in _tab.Items)
                    if (_tab.SelectedPaths.Contains(i.Path)) { List.SelectedItems.Add(i); first ??= i; }
                if (first != null) ItemsInteraction.ScrollTo(List, first);
            }
        }
        finally { _restoring = false; }
    }

    public IList<FileItem> SelectedItems => List.SelectedItems.Cast<FileItem>().OrderBy(i => List.Items.IndexOf(i)).ToList();
    public FileItem FocusedItem => (Keyboard.FocusedElement as ListBoxItem)?.DataContext as FileItem ?? List.SelectedItem as FileItem;
    public string ActiveFolder => _tab?.FolderPath;

    public void SelectItems(IEnumerable<FileItem> items, bool scroll = true)
    {
        List.SelectedItems.Clear();
        FileItem first = null;
        foreach (var i in items) { List.SelectedItems.Add(i); first ??= i; }
        if (first != null && scroll) { ItemsInteraction.ScrollTo(List, first); ItemsInteraction.FocusItem(List, first); }
    }

    public void SelectAll() => List.SelectAll();

    public void FocusView()
    {
        if (List.SelectedItem is FileItem f) ItemsInteraction.FocusItem(List, f);
        else List.Focus();
    }

    public void BeginRename(FileItem item)
    {
        ItemsInteraction.ScrollTo(List, item);
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

    /// <summary>Flechas en cuadrícula: arriba/abajo saltan una fila entera.</summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        int count = List.Items.Count;
        if (count == 0) return;
        var panel = Panel;
        int cols = panel?.ItemsPerRow ?? 1;
        int cur = List.SelectedItem != null ? List.Items.IndexOf(FocusedItem ?? List.SelectedItem) : -1;
        int next = cur;
        switch (e.Key)
        {
            case Key.Left: next = cur <= 0 ? 0 : cur - 1; break;
            case Key.Right: next = cur < 0 ? 0 : Math.Min(count - 1, cur + 1); break;
            case Key.Up:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
                next = cur < 0 ? 0 : Math.Max(0, cur - cols); break;
            case Key.Down:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
                next = cur < 0 ? 0 : (cur + cols < count ? cur + cols : cur); break;
            case Key.Home: next = 0; break;
            case Key.End: next = count - 1; break;
            case Key.PageDown: next = Math.Min(count - 1, Math.Max(0, cur) + cols * 4); break;
            case Key.PageUp: next = Math.Max(0, cur - cols * 4); break;
            default: return;
        }
        e.Handled = true;
        var item = (FileItem)List.Items[next];
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && cur >= 0)
        {
            int a = Math.Min(cur, next), b = Math.Max(cur, next);
            for (int i = a; i <= b; i++) if (!List.SelectedItems.Contains(List.Items[i])) List.SelectedItems.Add(List.Items[i]);
        }
        else
        {
            List.SelectedItems.Clear();
            List.SelectedItem = item;
        }
        panel?.ScrollToIndex(next);
        ItemsInteraction.FocusItem(List, item);
    }
}
