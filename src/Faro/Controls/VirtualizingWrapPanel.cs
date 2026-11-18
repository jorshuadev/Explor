using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Faro.Controls;

/// <summary>
/// Panel en cuadrícula virtualizado para la vista de iconos: sólo crea los elementos
/// visibles, así una carpeta con 100.000 archivos se abre y desplaza igual de rápido.
/// Las columnas se reparten el ancho sobrante, como en Finder.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    private Size _extent, _viewport;
    private Point _offset;
    private const double Pad = 8;

    public int ItemsPerRow { get; private set; } = 1;
    private double ColumnWidth => ItemsPerRow > 0 ? Math.Max(ItemWidth, (_viewport.Width - Pad * 2) / ItemsPerRow) : ItemWidth;

    private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? ItemWidth * 6 : available.Width;
        double h = double.IsInfinity(available.Height) ? 600 : available.Height;
        int count = ItemCount;
        ItemsPerRow = Math.Max(1, (int)((w - Pad * 2) / ItemWidth));
        int rows = (count + ItemsPerRow - 1) / ItemsPerRow;
        UpdateScroll(new Size(w, h), new Size(w, rows * ItemHeight + Pad * 2));

        // rango visible (+ una fila de margen)
        int firstRow = Math.Max(0, (int)((_offset.Y - Pad) / ItemHeight) - 1);
        // margen generoso por debajo: así al agrandar la ventana (pantalla completa) o al hacer scroll
        // las celdas ya existen y no hay que crearlas en ese momento. Pero al abrir una carpeta se crea
        // sólo lo visible (la primera vez cada celda carga su plantilla: crear el margen de golpe
        // costaba ~0,5 s) y el margen se añade fila a fila cuando la app está ociosa.
        int visibleLast = (int)((_offset.Y + h) / ItemHeight) + 1;
        int wantedLast = (int)((_offset.Y + Math.Max(h, SystemParameters.PrimaryScreenHeight)) / ItemHeight) + 2;
        int lastRow = Math.Min(wantedLast, visibleLast + _extraRows);
        if (lastRow < wantedLast && (lastRow + 1) * ItemsPerRow < count) GrowWhenIdle();
        int first = Math.Min(count, firstRow * ItemsPerRow);
        int last = Math.Min(count - 1, (lastRow + 1) * ItemsPerRow - 1);

        // Tocar InternalChildren ANTES que el generador: es lo que lo conecta. Si la vista se mide antes
        // (al arrancar, al abrir una carpeta en iconos) el generador aún es null y la medición fallaba en bucle.
        // Sin ListBox dueño (la vista se está quitando de la ventana) el generador lanza NullReference
        // en cada pasada de layout: era el bucle de errores que congelaba la app.
        if (ItemsControl.GetItemsOwner(this) == null) return new Size(w, h);
        _ = InternalChildren;
        var gen = ItemContainerGenerator;
        if (gen == null) return new Size(w, h);
        if (count > 0 && first <= last)
        {
            var startPos = gen.GeneratorPositionFromIndex(first);
            int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;
            using (gen.StartAt(startPos, GeneratorDirection.Forward, true))
            {
                for (int i = first; i <= last; i++, childIndex++)
                {
                    var child = gen.GenerateNext(out bool isNew) as UIElement;
                    if (child == null) break;
                    if (isNew)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        gen.PrepareItemContainer(child);
                    }
                    else if (!InternalChildren.Contains(child))
                    {
                        // Contenedor RECICLADO: el generador lo devuelve como "no nuevo" pero ya no es hijo
                        // del panel (se quitó al reciclarlo). Hay que volver a insertarlo o la celda no se ve.
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        gen.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(ColumnWidth, ItemHeight));
                }
            }
        }
        CleanUp(first, last);
        return new Size(w, h);
    }

    private int _extraRows;
    private bool _growPending;

    private void GrowWhenIdle()
    {
        if (_growPending) return;
        _growPending = true;
        Dispatcher.BeginInvoke(() => { _growPending = false; _extraRows++; InvalidateMeasure(); },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void CleanUp(int first, int last)
    {
        var children = InternalChildren;
        var gen = ItemContainerGenerator as IRecyclingItemContainerGenerator;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            int idx = ItemContainerGenerator.IndexFromGeneratorPosition(pos);
            if (idx < first || idx > last)
            {
                // si la celda que se recicla tiene el foco, pasarlo a la lista: si no, al reactivar la
                // ventana WPF devuelve el foco a esa celda (que ya muestra OTRO archivo) y salta hasta ella
                if (children[i].IsKeyboardFocusWithin)
                {
                    var owner = ItemsControl.GetItemsOwner(this);
                    SuppressBringIntoViewUntil = DateTime.Now.AddMilliseconds(400);
                    if (owner != null)
                    {
                        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(owner), owner);
                        // Focus() en una ventana inactiva la trae al frente (tapaba la ventana de progreso)
                        if (Window.GetWindow(owner)?.IsActive == true) owner.Focus();
                    }
                }
                if (gen != null && VirtualizingPanel.GetVirtualizationMode(ItemsControl.GetItemsOwner(this)) == VirtualizationMode.Recycling)
                    gen.Recycle(pos, 1);
                else
                    ItemContainerGenerator.Remove(pos, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (ItemsControl.GetItemsOwner(this) == null) return final;
        double cw = ColumnWidth;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            int idx = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (idx < 0) continue;
            int row = idx / ItemsPerRow, col = idx % ItemsPerRow;
            child.Arrange(new Rect(Pad + col * cw, Pad + row * ItemHeight - _offset.Y, cw, ItemHeight));
        }
        return final;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                if (args.Position.Index >= 0 && args.Position.Index < InternalChildren.Count)
                    RemoveInternalChildRange(args.Position.Index, Math.Min(args.ItemUICount, InternalChildren.Count - args.Position.Index));
                break;
            case NotifyCollectionChangedAction.Reset:
                _extraRows = 0;
                SetVerticalOffset(0);
                break;
        }
        base.OnItemsChanged(sender, args);
    }

    /// <summary>Rectángulo (en coordenadas del panel) de un índice: para la selección con recuadro.</summary>
    public Rect RectOfIndex(int idx)
    {
        int row = idx / ItemsPerRow, col = idx % ItemsPerRow;
        return new Rect(Pad + col * ColumnWidth, Pad + row * ItemHeight - _offset.Y, ColumnWidth, ItemHeight);
    }

    public IEnumerable<int> IndicesInRect(Rect r)
    {
        // r en coordenadas del panel (ya restado el desplazamiento)
        int count = ItemCount;
        double cw = ColumnWidth;
        double top = r.Top + _offset.Y - Pad, bottom = r.Bottom + _offset.Y - Pad;
        int r0 = Math.Max(0, (int)(top / ItemHeight)), r1 = (int)(bottom / ItemHeight);
        int c0 = Math.Max(0, (int)((r.Left - Pad) / cw)), c1 = Math.Min(ItemsPerRow - 1, (int)((r.Right - Pad) / cw));
        for (int row = r0; row <= r1; row++)
            for (int col = c0; col <= c1; col++)
            {
                int i = row * ItemsPerRow + col;
                if (i >= 0 && i < count)
                {
                    // sólo la zona del icono+nombre cuenta, no el hueco
                    var cell = new Rect(Pad + col * cw + cw * 0.15, Pad + row * ItemHeight - _offset.Y + 4, cw * 0.7, ItemHeight - 8);
                    if (cell.IntersectsWith(r)) yield return i;
                }
            }
    }

    public int IndexAt(Point p)
    {
        double cw = ColumnWidth;
        int col = (int)((p.X - Pad) / cw), row = (int)((p.Y + _offset.Y - Pad) / ItemHeight);
        if (col < 0 || col >= ItemsPerRow || row < 0 || p.X < Pad) return -1;
        int i = row * ItemsPerRow + col;
        return i < ItemCount ? i : -1;
    }

    /// <summary>Durante un momento se ignoran las peticiones automáticas de WPF de «traer a la vista» (al reactivar la ventana).</summary>
    public DateTime SuppressBringIntoViewUntil { get; set; }

    // Las peticiones automáticas de WPF (foco, reactivar ventana) NO mueven el scroll: sólo el usuario o ScrollToIndex.
    protected override void BringIndexIntoView(int index) { }

    public void ScrollToIndex(int index)
    {
        if (index < 0) return;
        int row = index / Math.Max(1, ItemsPerRow);
        double top = Pad + row * ItemHeight, bottom = top + ItemHeight;
        if (top < _offset.Y) SetVerticalOffset(top - Pad);
        else if (bottom > _offset.Y + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height + Pad);
    }

    // ================= IScrollInfo =================
    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer ScrollOwner { get; set; }

    private void UpdateScroll(Size viewport, Size extent)
    {
        bool changed = viewport != _viewport || extent != _extent;
        _viewport = viewport; _extent = extent;
        double max = Math.Max(0, _extent.Height - _viewport.Height);
        if (_offset.Y > max) _offset.Y = max;
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    public void LineUp() => SetVerticalOffset(_offset.Y - 40);
    public void LineDown() => SetVerticalOffset(_offset.Y + 40);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - 72);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + 72);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Height - _viewport.Height)));
        if (Math.Abs(offset - _offset.Y) < 0.1) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    // Las peticiones automáticas de WPF no mueven el scroll (ver BringIndexIntoView).
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
