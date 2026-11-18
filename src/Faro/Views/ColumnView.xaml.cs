using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Faro.Model;
using Faro.Services;

namespace Faro.Views;

/// <summary>
/// Vista de columnas (Ctrl+3): cada carpeta seleccionada abre otra columna a la derecha;
/// al seleccionar un archivo, la última columna muestra su vista previa.
/// </summary>
public partial class ColumnView : UserControl, IFileView
{
    private BrowserTab _tab;
    private IFileViewHost Host => Window.GetWindow(this) as IFileViewHost;

    private sealed class Col
    {
        public string Folder;
        public System.Collections.IList Items;
        public ListBox List;
        public FrameworkElement Root;
        public Border Body;
        public CancellationTokenSource Cts;
        /// <summary>Ancho que quiere el usuario; el real se recorta a lo que cabe en la ventana (ver Apply).</summary>
        public double Desired;
    }

    private readonly List<Col> _cols = new();
    private FrameworkElement _preview;
    private ListBox _active;
    /// <summary>
    /// Lo más ancha que puede ser una columna en la ventana ACTUAL: como mucho el 45 % de la vista (caben dos) y dejando
    /// sitio a otra columna. Antes el mínimo fijo era 560 px y el ancho guardado llegaba a 1370: en una ventana
    /// estrecha la columna no cabía, la vista se iba al final y sólo se veía la punta derecha de la fila.
    /// </summary>
    private double MaxCol
    {
        get
        {
            double view = Scroller.ActualWidth;
            return view <= 0 ? 600 : Math.Max(160, Math.Min(view * 0.45, view - 200));
        }
    }
    private double _colWidth = Settings.Current.ListColumnWidths.TryGetValue("column", out var w) && w >= 120 ? Math.Min(w, 600) : 290;

    private void Apply(Col c) => ((Grid)c.Root).Width = Math.Clamp(c.Desired, 120, Math.Max(120, MaxCol));

    /// <summary>Plantilla del asa (una sola, compartida: una plantilla en uso ya no se puede modificar).</summary>
    private static readonly ControlTemplate GripTemplate = MakeGripTemplate();
    private static ControlTemplate MakeGripTemplate()
    {
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var t = new ControlTemplate(typeof(Thumb)) { VisualTree = f };
        t.Seal();
        return t;
    }

    private double FitWidth(System.Collections.IList items)
    {
        var tf = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double max = 120;
        foreach (FileItem i in items.Cast<FileItem>().Take(5000))
        {
            var ft = new FormattedText(i.DisplayName, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 13, Brushes.Black, 1.0);
            max = Math.Max(max, ft.Width);
        }
        return Math.Min(MaxCol, max + 70);
    }

    public ColumnView()
    {
        InitializeComponent();
        // al cambiar el tamaño de la ventana las columnas se encogen o recuperan su ancho preferido
        Scroller.SizeChanged += (_, _) => { foreach (var c in _cols) Apply(c); FitPreview(); if (_preview != null) ScrollToEnd(); };
    }

    public void Attach(BrowserTab tab)
    {
        if (_tab != null) _tab.ItemsReset -= Rebuild;
        _tab = tab;
        if (tab != null) tab.ItemsReset += Rebuild;
        Rebuild();
    }

    private void Rebuild()
    {
        Columns.Children.Clear();
        _cols.Clear();
        _preview = null;
        if (_tab == null) return;
        var c = AddColumn(_tab.FolderPath, _tab.Items);
        _active = c.List;
        RestoreSelection();
    }

    private Col AddColumn(string folder, System.Collections.IList items)
    {
        var list = new Faro.Controls.QuietListBox
        {
            ItemsSource = items, SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            ItemContainerStyle = (Style)Resources["ColumnItem"], FocusVisualStyle = null
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var body = new Border { Child = list, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 4, 0, 4) };
        body.SetResourceReference(Border.BorderBrushProperty, "Line.Separator");
        // Asa en el borde derecho: arrastrar = cambiar el ancho; doble clic = ajustar al nombre más largo
        var grip = new Thumb { Width = 7, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -3, 0), Cursor = Cursors.SizeWE };
        grip.Template = GripTemplate;
        var root = new Grid();
        root.Children.Add(body); root.Children.Add(grip);
        var col = new Col { Folder = folder, Items = items, List = list, Root = root, Body = body, Desired = _colWidth };
        Apply(col);
        grip.DragDelta += (_, e) =>
        {
            col.Desired = Math.Clamp(root.Width + e.HorizontalChange, 120, Math.Max(120, MaxCol));
            Apply(col);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) foreach (var c in _cols) { c.Desired = col.Desired; Apply(c); }
            FitPreview();
        };
        grip.DragCompleted += (_, _) => { _colWidth = col.Desired; Settings.Current.ListColumnWidths["column"] = _colWidth; };
        grip.MouseDoubleClick += (_, _) => { col.Desired = _colWidth = FitWidth(items); Apply(col); FitPreview(); };
        string capturedFolder = folder;
        _ = new ItemsInteraction(list, null, () => Host, () => capturedFolder);
        list.SelectionChanged += (_, _) => OnColumnSelection(col);
        list.GotKeyboardFocus += (_, _) => { _active = list; Host?.OnSelectionChanged(); };
        list.PreviewKeyDown += (s, e) => OnKey(col, e);
        _cols.Add(col);
        Columns.Children.Add(root);
        if (_cols.Count > 1) MainWindow.FadeIn(root, 180, 0, 1, 18);
        return col;
    }

    private void TrimAfter(Col col)
    {
        int idx = _cols.IndexOf(col);
        for (int i = _cols.Count - 1; i > idx; i--)
        {
            _cols[i].Cts?.Cancel();
            Columns.Children.Remove(_cols[i].Root);
            _cols.RemoveAt(i);
        }
        if (_preview != null) { Columns.Children.Remove(_preview); _preview = null; }
    }

    private async void OnColumnSelection(Col col)
    {
        if (!_cols.Contains(col)) return; // columna ya descartada
        if (col == _cols[0] && _tab != null && !_restoring)
        {
            _tab.SelectedPaths.Clear();
            foreach (FileItem i in col.List.SelectedItems) _tab.SelectedPaths.Add(i.Path);
        }
        TrimAfter(col);
        if (col.List.SelectedItems.Count != 1 || col.List.SelectedItem is not FileItem item) return;
        if (item.IsNavigable)
        {
            var items = new BulkObservableCollection<FileItem>();
            var next = AddColumn(item.Path, items);
            var cts = next.Cts = new CancellationTokenSource();
            ScrollToEnd();
            try
            {
                var list = await Task.Run(() => Providers.LoadFolder(item.Path, Settings.Current.ShowHidden, cts.Token));
                if (cts.IsCancellationRequested || !_cols.Contains(next)) return;
                _tab?.SortList(list);
                items.ReplaceAll(list);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var msg = new TextBlock { Text = "Sin permiso", Margin = new Thickness(12), FontSize = 12 };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
                next.Body.Child = msg;
            }
            catch (OperationCanceledException) { }
        }
        else
        {
            // la previsualización ocupa todo el espacio libre a la derecha y se reajusta con la ventana (FitPreview)
            double hostW = PreviewHostWidth();
            var pane = PreviewPane.BuildFluid(item, hostW, PreviewHostHeight());
            var host = new Grid { Width = hostW, Background = Brushes.Transparent };
            host.Children.Add(pane);
            _preview = host;
            Columns.Children.Add(_preview);
            MainWindow.FadeIn(_preview, 180, 0, 1, 18);
            ScrollToEnd();
        }
        Host?.OnSelectionChanged();
    }

    // ActualWidth/ActualHeight y no ViewportWidth: dentro de SizeChanged el viewport aún tiene el tamaño ANTERIOR,
    // así que al achicar la ventana las columnas y la previsualización se calculaban con el ancho viejo (en ventana
    // la previsualización quedaba cortada por la derecha). La barra vertical está desactivada: miden lo mismo.
    private double PreviewHostWidth() => Math.Max(240, Math.Floor(Scroller.ActualWidth - _cols.Sum(c => c.Root.Width)));

    private double PreviewHostHeight() => Scroller.ActualHeight - (Scroller.ComputedHorizontalScrollBarVisibility == Visibility.Visible ? SystemParameters.HorizontalScrollBarHeight : 0);

    /// <summary>Al cambiar el tamaño de la ventana o de una columna, la previsualización sigue llenando el hueco.</summary>
    private void FitPreview()
    {
        if (_preview is not Grid host) return;
        host.Width = PreviewHostWidth();
        if (host.Children.Count > 0) PreviewPane.Fit((FrameworkElement)host.Children[0], host.Width, PreviewHostHeight());
    }

    private void ScrollToEnd() => Dispatcher.BeginInvoke(() => Scroller.ScrollToRightEnd(), System.Windows.Threading.DispatcherPriority.Loaded);

    private void OnKey(Col col, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        int idx = _cols.IndexOf(col);
        if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (idx + 1 < _cols.Count)
            {
                var next = _cols[idx + 1];
                if (next.List.Items.Count > 0)
                {
                    if (next.List.SelectedItem == null) next.List.SelectedIndex = 0;
                    ItemsInteraction.FocusItem(next.List, (FileItem)next.List.SelectedItem);
                }
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (idx > 0)
            {
                var prev = _cols[idx - 1];
                TrimAfter(prev);
                // reabrir la columna actual para mantener el contexto como Finder
                if (prev.List.SelectedItem is FileItem f) { OnColumnSelection(prev); ItemsInteraction.FocusItem(prev.List, f); }
            }
            e.Handled = true;
        }
    }

    private ListBox ActiveList => _active != null && _cols.Any(c => c.List == _active) ? _active : _cols.LastOrDefault(c => c.List.SelectedItems.Count > 0)?.List ?? _cols.FirstOrDefault()?.List;

    public IList<FileItem> SelectedItems => ActiveList?.SelectedItems.Cast<FileItem>().ToList() ?? new List<FileItem>();
    public FileItem FocusedItem => (Keyboard.FocusedElement as ListBoxItem)?.DataContext as FileItem ?? ActiveList?.SelectedItem as FileItem;

    public string ActiveFolder
    {
        get
        {
            var l = ActiveList;
            var c = _cols.FirstOrDefault(x => x.List == l);
            return c?.Folder ?? _tab?.FolderPath;
        }
    }

    public void SelectItems(IEnumerable<FileItem> items, bool scroll = true)
    {
        var target = ActiveList;
        if (target == null) return;
        var arr = items.ToList();
        // si los elementos no están en la columna activa, buscar la que los contenga
        var col = _cols.FirstOrDefault(c => arr.Count > 0 && c.Items.Contains(arr[0]));
        if (col != null) target = col.List;
        target.SelectedItems.Clear();
        foreach (var i in arr) target.SelectedItems.Add(i);
        if (arr.Count > 0 && scroll) { target.ScrollIntoView(arr[0]); ItemsInteraction.FocusItem(target, arr[0]); }
    }

    public void SelectAll() => ActiveList?.SelectAll();

    public void FocusView()
    {
        var l = ActiveList;
        if (l == null) return;
        if (l.SelectedItem is FileItem f) ItemsInteraction.FocusItem(l, f); else l.Focus();
    }

    public void BeginRename(FileItem item)
    {
        foreach (var c in _cols) if (c.Items.Contains(item)) c.List.ScrollIntoView(item);
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

    private bool _restoring;
    public void RestoreSelection()
    {
        if (_tab == null || _cols.Count == 0) return;
        _restoring = true;
        try
        {
            var l = _cols[0].List;
            l.SelectedItems.Clear();
            foreach (var i in _tab.Items) if (_tab.SelectedPaths.Contains(i.Path)) l.SelectedItems.Add(i);
            if (l.SelectedItem != null) l.ScrollIntoView(l.SelectedItem);
        }
        finally { _restoring = false; }
    }
}
