using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Faro.Core;
using Faro.Model;
using Faro.Services;
using Faro.Shell;

namespace Faro.Windows;

/// <summary>
/// Visualización rápida (barra espaciadora). No roba el foco: las flechas siguen moviendo la
/// selección en la ventana de Faro y la vista previa la sigue, igual que en macOS.
/// </summary>
public sealed partial class QuickLookWindow : Window
{
    private readonly MainWindow _owner;
    /// <summary>Botones propios de cada tipo de archivo (buscar, editar, girar, velocidad…), a la izquierda de «Abrir».</summary>
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private string _titleText;
    private DispatcherTimer _noticeTimer;
    private IList<FileItem> _items = new List<FileItem>();
    private int _index;
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _content = new() { ClipToBounds = true };
    private readonly Button _openBtn;
    private MediaElement _media;
    private DispatcherTimer _mediaTimer;
    private int _token;
    private Border _frame;
    private bool _closing;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // como macOS: la vista rápida se encoge y desaparece
        if (!_closing && !ConfirmLeave()) { e.Cancel = true; return; }
        if (!_closing && _frame != null) { e.Cancel = true; _closing = true; StopMedia(); MainWindow.FadeOutThen(_frame, Close, 160, 0.88); return; }
        base.OnClosing(e);
    }

    public QuickLookWindow(MainWindow owner)
    {
        _owner = owner;
        Owner = owner;
        ShowActivated = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0, 0, 0, 1), UseAeroCaptionButtons = false });
        SetResourceReference(BackgroundProperty, "Bg.Popup");
        ShowInTaskbar = false;
        Width = 820; Height = 620;
        FontFamily = (FontFamily)FindResource("UiFont");
        Title = "Visualización rápida";
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = owner.Left + (owner.ActualWidth - Width) / 2;
        Top = owner.Top + (owner.ActualHeight - Height) / 2;

        var close = ToolBtn("", "Cerrar (Esc)", () => Close());
        var full = ToolBtn("", "Pantalla completa", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        _openBtn = new Button { Style = (Style)FindResource("MacButton"), Height = 24, MinWidth = 0, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 8, 0), Content = "Abrir" };
        _openBtn.Click += (_, _) => { if (Current != null && ConfirmLeave()) _owner.OpenItems(new[] { Current }); };

        var bar = new Grid { Height = 40, Background = Brushes.Transparent };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        left.Children.Add(close); left.Children.Add(full);
        bar.Children.Add(left);
        Grid.SetColumn(_title, 1); _title.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary"); bar.Children.Add(_title);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_tools); right.Children.Add(_openBtn);
        Grid.SetColumn(right, 2); bar.Children.Add(right);
        bar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) full.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); else try { DragMove(); } catch { } };

        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(_content);
        var frame = _frame = new Border
        {
            Child = dock
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Bg.Popup");
        frame.SetResourceReference(Border.BorderBrushProperty, "Line.Popup");
        Content = frame;
        Loaded += (_, _) => MainWindow.FadeIn(frame, 200, 0, 0.9);

        PreviewKeyDown += (_, e) =>
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.S && _editing) { SaveText(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.F && _text != null) { ShowFind(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.C && _imageShown != null && Keyboard.FocusedElement is not TextBox) { CopyImage(); e.Handled = true; return; }
            if (e.Key == Key.Escape)
            {
                if (_find?.IsKeyboardFocusWithin == true) HideFind();
                else if (_editing) CancelEdit();
                else Close();
                e.Handled = true; return;
            }
            // Dentro de un cuadro de texto las teclas son para escribir o mover la selección;
            // solo el Espacio en el texto de solo lectura sigue cerrando, como en macOS.
            if (Keyboard.FocusedElement is TextBox box)
            {
                if (box == _text && !_editing && e.Key == Key.Space) { Close(); e.Handled = true; }
                return;
            }
            if (HandleMediaKey(e.Key, Keyboard.Modifiers)) { e.Handled = true; return; }
            if (e.Key is Key.Space) { Close(); e.Handled = true; }
            else if (e.Key is Key.Left or Key.Up) { Step(-1); e.Handled = true; }
            else if (e.Key is Key.Right or Key.Down) { Step(1); e.Handled = true; }
        };
        Closed += (_, _) => StopMedia();
        SourceInitialized += (_, _) => Theme.ApplyWindowEffects(this);
    }

    private Button ToolBtn(string glyph, string tip, Action a)
    {
        var b = new Button { Style = (Style)FindResource("ToolButton"), ToolTip = tip, Height = 26, MinWidth = 26 };
        b.Content = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 11 };
        b.Click += (_, _) => a();
        return b;
    }

    /// <summary>Botón de la barra con texto (las funciones de cada visor llevan nombre, no solo icono).</summary>
    private Button TextBtn(string text, string tip, Action a)
    {
        var b = new Button { Style = (Style)FindResource("ToolButton"), ToolTip = tip, Height = 26, MinWidth = 0, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 2, 0) };
        ToolTipService.SetShowOnDisabled(b, true);
        b.Content = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => a();
        return b;
    }

    private static void SetBtnText(Button b, string text) { if (b.Content is TextBlock t) t.Text = text; }

    private void SetTitle(string text) { _titleText = text; _noticeTimer?.Stop(); _title.Text = text; }

    /// <summary>Aviso breve en el título («Copiado», «Guardado»), luego vuelve el nombre.</summary>
    private void Notice(string text)
    {
        _title.Text = text;
        _noticeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _noticeTimer.Stop();
        _noticeTimer.Tick -= RestoreTitle; _noticeTimer.Tick += RestoreTitle;
        _noticeTimer.Start();
    }

    private void RestoreTitle(object s, EventArgs e) { _noticeTimer.Stop(); _title.Text = _titleText; }

    private FileItem Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

    public void ShowItems(IList<FileItem> items, int index)
    {
        _items = items.ToList();
        _index = index;
        if (!IsVisible) Show();
        Render();
    }

    /// <summary>La ventana principal cambió la selección: mostrar lo nuevo.</summary>
    public void Follow(IList<FileItem> sel)
    {
        if (!IsVisible || sel == null || sel.Count == 0) return;
        if (sel.Count == 1 && Current == sel[0]) return;
        if (!ConfirmLeave()) return;
        _items = sel.ToList(); _index = 0;
        Render();
    }

    private void Step(int d)
    {
        if (_items.Count > 1 && ConfirmLeave()) { _index = (_index + d + _items.Count) % _items.Count; Render(); }
    }

    private void StopMedia()
    {
        _mediaTimer?.Stop();
        if (_media != null) { try { _media.Stop(); _media.Close(); } catch { } _media = null; }
    }

    private async void Render()
    {
        int token = ++_token;
        StopMedia();
        ResetViewers();
        var item = Current;
        if (item == null) { _content.Child = null; return; }
        if (_content.Child is UIElement old) MainWindow.FadeIn(old, 1, 0);
        SetTitle(_items.Count > 1 ? $"{item.DisplayName}  ({_index + 1} de {_items.Count})" : item.DisplayName);
        _openBtn.ToolTip = item.IsNavigable ? null : "Abrir con la app predeterminada";
        var path = item.Special == ItemSpecial.TrashEntry ? item.Path : item.Path;
        var cat = item.Kind.Category;
        var ext = item.Extension;

        if (item.IsNavigable || item.Special == ItemSpecial.App)
        {
            _content.Child = InfoCard(item, item.Icon);
            return;
        }

        // 1) imágenes que WPF decodifica directamente (y las que tengan códec WIC instalado)
        if (cat is KindCategory.Image or KindCategory.RawPhoto && ext is not ("psd" or "svg"))
        {
            var (decoded, w, h) = await Task.Run(() => { var s = ImageSize(path); return (DecodeImage(path), s.w, s.h); });
            if (token != _token) return;
            ImageSource img = decoded ?? await ThumbnailLoader.LoadLargeAsync(path, 1600, allowIcon: false);
            if (token != _token) return;
            if (img != null)
            {
                if (w > 0) SetTitle($"{_titleText}  —  {w} × {h}");
                _content.Child = ImageView(img, path, w);
                return;
            }
        }

        // 2) vídeo y audio
        if (FileKinds.IsPlayable(cat))
        {
            _content.Child = MediaView(path, cat == KindCategory.Audio, item);
            return;
        }

        // 2b) comprimidos: la lista de lo que traen dentro
        if (cat == KindCategory.Archive && Archives.CanOpen(ext))
        {
            var listing = await Task.Run(() => Archives.TryList(path, 400));
            if (token != _token) return;
            if (listing != null && listing.Total > 0) { _content.Child = ArchiveView(item, listing); return; }
        }

        // 3) texto y código
        if (cat is KindCategory.Text or KindCategory.Code or KindCategory.Web or KindCategory.Subtitle || (cat == KindCategory.Other && item.Size < 2_000_000 && LooksText(path)))
        {
            if (ext is not ("html" or "htm" or "url"))
            {
                var text = await Task.Run(() => ReadText(path));
                if (token != _token) return;
                if (text != null) { _content.Child = TextView(text, item, cat is KindCategory.Code or KindCategory.Web); return; }
            }
        }

        // 4) PDF e Illustrator (.ai compatible PDF) con el motor PDF de Windows
        if (ext is "pdf" or "ai")
        {
            var doc = await PdfRenderer.OpenAsync(path);
            if (token != _token) return;
            if (doc != null && doc.PageCount > 0) { _content.Child = PdfView(doc, item, token); return; }
        }

        // 5) controladores de vista previa del sistema (Office, fuentes...)
        if (PreviewHandlerHost.FindHandler(path) != null)
        {
            var host = new PreviewHandlerHost(path);
            _content.Child = new Border { Child = host, Margin = new Thickness(1, 0, 1, 10) };
            return;
        }

        // 5) miniatura grande del Shell (PSD, AI, vídeos raros, 3D...) o ficha con icono
        var big = await ThumbnailLoader.LoadLargeAsync(path, 1024, allowIcon: false);
        if (token != _token) return;
        _content.Child = big != null ? ImageView(big, null, 0) : InfoCard(item, item.Icon);
    }

    /// <summary>Olvida el estado del visor anterior (texto en edición, imagen, botones propios).</summary>
    private void ResetViewers()
    {
        _tools.Children.Clear();
        _text = null; _textFile = null; _editing = false; _dirty = false; _find = null;
        _imagePath = null; _imageShown = null;
        _mediaKeys = null;
    }

    private FrameworkElement PdfView(global::Windows.Data.Pdf.PdfDocument doc, FileItem item, int token)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 12) };
        var sv = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(6, 0, 6, 8) };
        uint pages = Math.Min(doc.PageCount, 60u);
        SetTitle($"{item.DisplayName}  —  {doc.PageCount} página{(doc.PageCount == 1 ? "" : "s")}");

        // Zoom: Ctrl+rueda o los botones − / +. Al 100 % las páginas ocupan el ancho (máx. 1100 px).
        double zoom = 1;
        Button zoomLabel = null;
        var zoomOut = TextBtn("−", "Alejar (Ctrl+rueda)", () => SetZoom(zoom / 1.25));
        zoomLabel = TextBtn("100 %", "Volver al ancho de la ventana", () => SetZoom(1));
        var zoomIn = TextBtn("+", "Acercar (Ctrl+rueda)", () => SetZoom(zoom * 1.25));
        _tools.Children.Add(zoomOut); _tools.Children.Add(zoomLabel); _tools.Children.Add(zoomIn);
        void ApplyZoom()
        {
            double baseW = Math.Max(200, Math.Min(sv.ViewportWidth - 48, 1100));
            sv.HorizontalScrollBarVisibility = zoom > 1.001 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            foreach (var page in stack.Children.OfType<Border>())
                if (page.Child is Image im)
                {
                    im.MaxWidth = Math.Abs(zoom - 1) < 0.001 ? 1100 : double.PositiveInfinity;
                    im.Width = Math.Abs(zoom - 1) < 0.001 ? double.NaN : baseW * zoom;
                }
        }
        void SetZoom(double z)
        {
            z = Math.Clamp(z, 0.5, 3);
            if (Math.Abs(z - 1) < 0.05) z = 1;
            double rel = sv.ExtentHeight > 0 ? sv.VerticalOffset / sv.ExtentHeight : 0;
            zoom = z;
            SetBtnText(zoomLabel, $"{Math.Round(z * 100)} %");
            ApplyZoom();
            sv.UpdateLayout();
            sv.ScrollToVerticalOffset(rel * sv.ExtentHeight);
        }
        sv.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            SetZoom(e.Delta > 0 ? zoom * 1.1 : zoom / 1.1);
            e.Handled = true;
        };
        sv.SizeChanged += (_, _) => { if (Math.Abs(zoom - 1) > 0.001) ApplyZoom(); };
        _ = Task.Run(async () =>
        {
            for (uint i = 0; i < pages; i++)
            {
                if (token != _token) return;
                var img = await PdfRenderer.RenderPageAsync(doc, i, 1400);
                if (img == null) continue;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (token != _token) return;
                    var im = new Image { Source = img, Stretch = Stretch.Uniform, Margin = new Thickness(10, 4, 10, 8), MaxWidth = 1100 };
                    RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
                    var page = new Border { Child = im, Background = Brushes.White, Margin = new Thickness(14, 0, 14, 10), HorizontalAlignment = HorizontalAlignment.Center,
                        Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.25, Direction = 270 } };
                    stack.Children.Add(page);
                    if (Math.Abs(zoom - 1) > 0.001) ApplyZoom();
                    if (stack.Children.Count == 1) MainWindow.FadeIn(page, 160, 6);
                });
            }
        });
        return sv;
    }

    private static BitmapSource DecodeImage(string path)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.UriSource = new Uri(path);
            bi.DecodePixelWidth = (int)Math.Min(2400, SystemParameters.PrimaryScreenWidth * 1.5);
            bi.EndInit();
            bi.Freeze();
            // aplicar la orientación EXIF (fotos del móvil)
            return ApplyExifRotation(path, bi);
        }
        catch { return null; }
    }

    private static BitmapSource ApplyExifRotation(string path, BitmapSource img)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (dec.Frames[0].Metadata is BitmapMetadata md && md.ContainsQuery("System.Photo.Orientation"))
            {
                var o = md.GetQuery("System.Photo.Orientation");
                int angle = Convert.ToInt32(o) switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
                if (angle != 0)
                {
                    var tb = new TransformedBitmap(img, new RotateTransform(angle));
                    tb.Freeze();
                    return tb;
                }
            }
        }
        catch { }
        return img;
    }

    private static bool LooksText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var b = new byte[4096]; int n = fs.Read(b, 0, b.Length);
            return FileKinds.LooksLikeText(b.AsSpan(0, n));
        }
        catch { return false; }
    }

    /// <summary>Contenido de un comprimido: resumen arriba y una fila por entrada (carpetas incluidas).</summary>
    private FrameworkElement ArchiveView(FileItem item, Archives.Listing listing)
    {
        var root = new DockPanel { Margin = new Thickness(18, 4, 18, 12) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new Image { Source = item.Icon, Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0) });
        var summary = new TextBlock
        {
            Text = $"{Format.Items(listing.Total)} dentro, {Format.Size(listing.Bytes)} al descomprimir",
            VerticalAlignment = VerticalAlignment.Center, FontSize = 13
        };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        head.Children.Add(summary);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var rows = new StackPanel();
        foreach (var e in listing.Entries)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = e.IsFolder ? e.Name + "\\" : e.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
            name.SetResourceReference(TextBlock.ForegroundProperty, e.IsFolder ? "Fg.Secondary" : "Fg.Primary");
            row.Children.Add(name);
            if (!e.IsFolder)
            {
                var size = new TextBlock { Text = Format.Size(e.Size), Margin = new Thickness(16, 0, 0, 0), FontSize = 12 };
                size.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
                Grid.SetColumn(size, 1);
                row.Children.Add(size);
            }
            rows.Children.Add(row);
        }
        if (listing.Total > listing.Entries.Count)
        {
            var more = new TextBlock { Text = $"… y {Format.Items(listing.Total - listing.Entries.Count)} más", Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
            more.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
            rows.Children.Add(more);
        }
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return root;
    }

    private FrameworkElement InfoCard(FileItem item, ImageSource icon, string note = null)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20) };
        sp.Children.Add(new Image { Source = icon, Width = 180, Height = 180 });
        var n = new TextBlock { Text = item.DisplayName, FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 4), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        n.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        sp.Children.Add(n);
        var k = new TextBlock { Text = item.KindText + (item.IsDirectory ? "" : " — " + item.SizeText), HorizontalAlignment = HorizontalAlignment.Center };
        k.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        sp.Children.Add(k);
        var m = new TextBlock { Text = "Modificado: " + Format.Date(item.Modified), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        m.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        sp.Children.Add(m);
        if (item.IsDirectory && item.Special == ItemSpecial.None)
        {
            var count = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0), Text = "Calculando…" };
            count.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
            sp.Children.Add(count);
            var token = _token;
            Task.Run(() => Views.FolderSize.Compute(item.Path, CancellationToken.None, 5)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                if (token != _token) return;
                var (c, b) = t.Result;
                count.Text = $"{Format.Items(c)}" + (b >= 0 ? $", {Format.Size(b)}" : "");
            }));
        }
        if (note != null)
        {
            var t = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0), FontSize = 12 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
            sp.Children.Add(t);
        }
        return sp;
    }
}
