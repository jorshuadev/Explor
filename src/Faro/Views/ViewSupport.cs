using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Faro.Model;

namespace Faro.Views;

/// <summary>Lo que una vista necesita de la ventana.</summary>
public interface IFileViewHost
{
    BrowserTab Tab { get; }
    void OpenItems(IList<FileItem> items, bool inNewTab = false);
    void ShowContextMenu(UIElement target, IList<FileItem> items);
    DragDropEffects DragOverFolder(string targetFolder, DragEventArgs e);
    void DropOnFolder(string targetFolder, DragEventArgs e);
    void StartDrag(UIElement source, IList<FileItem> items);
    void OnSelectionChanged();
    void CommitRename(FileItem item, string newName);
    void SpringLoad(FileItem folder);
    void ExpandToggle(FileItem item, bool? expand = null, bool recursive = false);
}

/// <summary>Contrato común de las cuatro vistas.</summary>
public interface IFileView
{
    void Attach(BrowserTab tab);
    IList<FileItem> SelectedItems { get; }
    FileItem FocusedItem { get; }
    void SelectItems(IEnumerable<FileItem> items, bool scroll = true);
    void SelectAll();
    void FocusView();
    void BeginRename(FileItem item);
    /// <summary>Carpeta donde actúan "nueva carpeta" y "pegar" (en columnas: la columna activa).</summary>
    string ActiveFolder { get; }
    void RestoreSelection();
}

/// <summary>
/// Comportamiento compartido por las vistas basadas en ListBox: doble clic, arrastrar,
/// soltar sobre carpetas (con apertura automática al mantener encima), selección con
/// recuadro, menú contextual y "escribir para seleccionar".
/// </summary>
public sealed class ItemsInteraction
{
    private readonly ListBox _list;
    private readonly Canvas _overlay;
    private readonly Func<IFileViewHost> _host;
    private readonly Func<string> _folder;
    private Point _downPos;
    private DateTime _downTime;
    private FileItem _downItem;
    private bool _deferredSelect;
    private bool _marquee;
    private Rectangle _marqueeRect;
    private HashSet<FileItem> _marqueeBase;
    private string _typeBuffer = "";
    private DateTime _lastType;
    private FileItem _dropItem;
    private readonly DispatcherTimer _springTimer;
    private bool _suppressRightUp;

    public Func<Rect, IEnumerable<FileItem>> ItemsInRect { get; set; }

    public ItemsInteraction(ListBox list, Canvas overlay, Func<IFileViewHost> host, Func<string> folder)
    {
        _list = list; _overlay = overlay; _host = host; _folder = folder;
        list.AllowDrop = true;
        // ListBox trae su propia búsqueda por teclado: seleccionaba el elemento pero se comía la tecla y
        // pedía el desplazamiento por BringIndexIntoView, que la cuadrícula ignora → la selección quedaba fuera de vista.
        list.IsTextSearchEnabled = false;
        list.PreviewMouseLeftButtonDown += OnDown;
        list.PreviewMouseMove += OnMove;
        list.PreviewMouseLeftButtonUp += OnUp;
        list.MouseDoubleClick += OnDouble;
        list.PreviewMouseRightButtonDown += OnRightDown;
        list.MouseRightButtonUp += OnRightUp;
        list.DragEnter += OnDragOver;
        list.DragOver += OnDragOver;
        list.DragLeave += (_, _) => SetDropItem(null);
        list.Drop += OnDrop;
        list.TextInput += OnTextInput;
        list.SelectionChanged += (_, _) => _host()?.OnSelectionChanged();
        _springTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _springTimer.Tick += (_, _) =>
        {
            _springTimer.Stop();
            if (_dropItem != null && _dropItem.IsNavigable) { var f = _dropItem; SetDropItem(null); _host()?.SpringLoad(f); }
        };
    }

    public static FileItem ItemFromSource(DependencyObject d)
    {
        while (d != null && d is not ListBoxItem)
        {
            if (d is TextBox tb && tb.IsVisible && tb.Name == "RenameBox") return null;
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return (d as ListBoxItem)?.DataContext as FileItem;
    }

    private static bool IsOnScrollBar(DependencyObject d)
    {
        while (d != null)
        {
            if (d is ScrollBar) return true;
            if (d is ListBoxItem) return false;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : null;
        }
        return false;
    }

    private static bool IsExpander(DependencyObject d)
    {
        while (d != null && d is not ListBoxItem)
        {
            if (d is FrameworkElement fe && fe.Name == "Disclosure") return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : null;
        }
        return false;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (IsOnScrollBar(src)) return;
        if (src is TextBox) return;
        _downPos = e.GetPosition(_list);
        _downTime = DateTime.Now;
        _downItem = ItemFromSource(src);
        _deferredSelect = false;
        if (_downItem != null && IsExpander(src))
        {
            _host()?.ExpandToggle(_downItem, null, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
            e.Handled = true;
            _downItem = null;
            return;
        }
        if (_downItem == null)
        {
            // clic en vacío: deseleccionar y empezar el recuadro
            if (e.ClickCount == 1)
            {
                _list.Focus();
                bool add = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                _marqueeBase = add ? _list.SelectedItems.Cast<FileItem>().ToHashSet() : new HashSet<FileItem>();
                if (!add) _list.UnselectAll();
                if (_overlay != null && ItemsInRect != null)
                {
                    _marquee = true;
                    _list.CaptureMouse();
                }
                e.Handled = true;
            }
            return;
        }
        // si ya estaba seleccionado y hay varios, esperar al soltar para no perder la selección al arrastrar
        if (_list.SelectedItems.Contains(_downItem) && Keyboard.Modifiers == ModifierKeys.None && _list.SelectedItems.Count > 1)
        {
            _deferredSelect = true;
            e.Handled = true;
            _list.Focus();
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { EndMarquee(); return; }
        var p = e.GetPosition(_list);
        if (_marquee)
        {
            UpdateMarquee(_downPos, p);
            return;
        }
        if (_downItem == null) return;
        // Arrastre a prueba de despistes: el umbral de Windows (4 px) convertía un clic con el pulso un poco
        // movido en «mover». Hace falta desplazarse más y que el botón lleve un momento pulsado.
        double dx = Math.Max(SystemParameters.MinimumHorizontalDragDistance * 3, 12), dy = Math.Max(SystemParameters.MinimumVerticalDragDistance * 3, 12);
        if (Math.Abs(p.X - _downPos.X) < dx && Math.Abs(p.Y - _downPos.Y) < dy) return;
        if ((DateTime.Now - _downTime).TotalMilliseconds < 180) return;
        var items = _list.SelectedItems.Cast<FileItem>().ToList();
        if (!items.Contains(_downItem)) { _list.SelectedItem = _downItem; items = new List<FileItem> { _downItem }; }
        _deferredSelect = false;
        var it = _downItem;
        _downItem = null;
        _host()?.StartDrag(_list, items);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_marquee) { EndMarquee(); e.Handled = true; return; }
        if (_deferredSelect && _downItem != null)
        {
            _list.SelectedItems.Clear();
            _list.SelectedItem = _downItem;
            if (_list.ItemContainerGenerator.ContainerFromItem(_downItem) is ListBoxItem lbi) lbi.Focus();
        }
        _deferredSelect = false;
        _downItem = null;
    }

    private void UpdateMarquee(Point a, Point b)
    {
        if (_marqueeRect == null)
        {
            _marqueeRect = new Rectangle { StrokeThickness = 1, IsHitTestVisible = false };
            _marqueeRect.SetResourceReference(Shape.FillProperty, "Bg.Marquee");
            _marqueeRect.SetResourceReference(Shape.StrokeProperty, "Accent");
            _overlay.Children.Add(_marqueeRect);
        }
        var r = new Rect(a, b);
        var pOverlay = _list.TranslatePoint(r.TopLeft, _overlay);
        Canvas.SetLeft(_marqueeRect, pOverlay.X); Canvas.SetTop(_marqueeRect, pOverlay.Y);
        _marqueeRect.Width = r.Width; _marqueeRect.Height = r.Height;
        var hit = ItemsInRect(r).ToHashSet();
        hit.UnionWith(_marqueeBase);
        var current = _list.SelectedItems.Cast<FileItem>().ToHashSet();
        if (!current.SetEquals(hit))
        {
            foreach (var x in current.Where(x => !hit.Contains(x)).ToList()) _list.SelectedItems.Remove(x);
            foreach (var x in hit.Where(x => !current.Contains(x))) _list.SelectedItems.Add(x);
        }
        // desplazamiento automático en los bordes
        var sv = FindScrollViewer(_list);
        if (sv != null)
        {
            if (b.Y > _list.ActualHeight - 10) sv.LineDown();
            else if (b.Y < 10) sv.LineUp();
        }
    }

    private void EndMarquee()
    {
        if (!_marquee) return;
        _marquee = false;
        _list.ReleaseMouseCapture();
        if (_marqueeRect != null) { _overlay.Children.Remove(_marqueeRect); _marqueeRect = null; }
    }

    private void OnDouble(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var item = ItemFromSource(e.OriginalSource as DependencyObject);
        if (item == null || IsExpander(e.OriginalSource as DependencyObject)) return;
        var sel = _list.SelectedItems.Cast<FileItem>().ToList();
        if (!sel.Contains(item)) sel = new List<FileItem> { item };
        _host()?.OpenItems(sel, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        e.Handled = true;
    }

    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemFromSource(e.OriginalSource as DependencyObject);
        _suppressRightUp = IsOnScrollBar(e.OriginalSource as DependencyObject);
        if (item != null && !_list.SelectedItems.Contains(item))
        {
            _list.SelectedItems.Clear();
            _list.SelectedItem = item;
        }
        else if (item == null && !_suppressRightUp) _list.UnselectAll();
        _list.Focus();
        e.Handled = true;
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressRightUp) return;
        var items = _list.SelectedItems.Cast<FileItem>().ToList();
        _host()?.ShowContextMenu(_list, items);
        e.Handled = true;
    }

    // ---------- soltar ----------
    private void SetDropItem(FileItem i)
    {
        if (_dropItem == i) return;
        if (_dropItem != null) _dropItem.IsDropTarget = false;
        _dropItem = i;
        if (i != null) i.IsDropTarget = true;
        _springTimer.Stop();
        if (i != null && i.IsNavigable) _springTimer.Start();
    }

    private string TargetFor(DragEventArgs e, out FileItem item)
    {
        item = ItemFromSource(e.OriginalSource as DependencyObject);
        if (item != null && item.IsNavigable) return item.Path;
        item = null;
        return _folder();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var target = TargetFor(e, out var item);
        SetDropItem(item);
        e.Effects = target == null ? DragDropEffects.None : (_host()?.DragOverFolder(target, e) ?? DragDropEffects.None);
        // autodesplazamiento
        var sv = FindScrollViewer(_list);
        var p = e.GetPosition(_list);
        if (sv != null) { if (p.Y < 20) sv.LineUp(); else if (p.Y > _list.ActualHeight - 20) sv.LineDown(); }
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var target = TargetFor(e, out _);
        SetDropItem(null);
        if (target != null) _host()?.DropOnFolder(target, e);
        e.Handled = true;
    }

    // ---------- escribir para seleccionar ----------
    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (e.Text == " ") return; // espacio = Visualización rápida
        if ((DateTime.Now - _lastType).TotalMilliseconds > 900) _typeBuffer = "";
        _lastType = DateTime.Now;
        _typeBuffer += e.Text;
        var items = _list.Items.Cast<FileItem>();
        var match = items.FirstOrDefault(i => i.DisplayName.StartsWith(_typeBuffer, StringComparison.CurrentCultureIgnoreCase))
                    ?? items.Where(i => string.Compare(i.DisplayName, _typeBuffer, StringComparison.CurrentCultureIgnoreCase) > 0)
                        .OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault();
        if (match != null)
        {
            _list.SelectedItems.Clear();
            _list.SelectedItem = match;
            ScrollTo(_list, match);
            FocusItem(_list, match);
        }
        e.Handled = true;
    }

    /// <summary>Lleva un elemento a la vista (en la cuadrícula de iconos lo hace el propio panel).</summary>
    public static void ScrollTo(ListBox list, FileItem item)
    {
        var wrap = FindChild<Faro.Controls.VirtualizingWrapPanel>(list);
        if (wrap != null) wrap.ScrollToIndex(list.Items.IndexOf(item));
        else list.ScrollIntoView(item);
    }

    public static void FocusItem(ListBox list, FileItem item)
    {
        list.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            // si entretanto empezó a renombrarse (nueva carpeta), no quitarle el foco al cuadro de texto
            if (item.IsEditing) return;
            if (list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem c) c.Focus();
        });
    }

    public static ScrollViewer FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var r = FindScrollViewer(VisualTreeHelper.GetChild(d, i));
            if (r != null) return r;
        }
        return null;
    }

    public static T FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        if (d is T t) return t;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var r = FindChild<T>(VisualTreeHelper.GetChild(d, i));
            if (r != null) return r;
        }
        return null;
    }
}

/// <summary>Cuadro de renombrar en línea (Intro / F2): selecciona el nombre sin la extensión, como Finder.</summary>
public static class RenameBox
{
    public static void Attach(TextBox box, Func<IFileViewHost> host)
    {
        void Start()
        {
            if (box.DataContext is not FileItem item || !item.IsEditing) return;
            box.Text = item.Name;
            box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                box.Focus();
                Keyboard.Focus(box);
                int dot = item.IsDirectory ? -1 : item.Name.LastIndexOf('.');
                if (dot > 0) box.Select(0, dot); else box.SelectAll();
            });
        }
        box.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Start(); };
        if (box.IsVisible) Start();
        box.PreviewKeyDown += (_, e) =>
        {
            if (box.DataContext is not FileItem item) return;
            if (e.Key == Key.Enter) { Commit(box, item, host); e.Handled = true; }
            else if (e.Key == Key.Escape) { item.IsEditing = false; e.Handled = true; FocusBack(box); }
            else if (e.Key == Key.Tab) { Commit(box, item, host); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (box.DataContext is FileItem item && item.IsEditing) Commit(box, item, host);
        };
    }

    private static void Commit(TextBox box, FileItem item, Func<IFileViewHost> host)
    {
        if (!item.IsEditing) return;
        item.IsEditing = false;
        var name = box.Text.Trim();
        if (name.Length > 0 && name != item.Name) host()?.CommitRename(item, name);
        FocusBack(box);
    }

    private static void FocusBack(TextBox box)
    {
        DependencyObject d = box;
        while (d != null && d is not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        if (d is ListBoxItem lbi) lbi.Focus();
    }
}
