using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Faro.Core;
using Faro.Model;
using Faro.Services;
using Faro.Shell;

namespace Faro.Views;

/// <summary>
/// Panel de previsualización (columna final, galería y Ctrl+Shift+P): imagen grande,
/// nombre, tipo y tamaño, y la sección «Información» con fechas y metadatos (dimensiones,
/// duración, cámara...). Los documentos de texto se ven como una hoja con sus primeras líneas.
/// </summary>
public static class PreviewPane
{
    /// <summary>Piezas que <see cref="Fit"/> reajusta cuando cambia el hueco disponible.</summary>
    private sealed class Parts
    {
        public FrameworkElement Visual;
        public bool IsPage;
        public FrameworkElement Text;
        public Grid Outer;
    }

    public static FrameworkElement Build(FileItem item, double width, bool showImage = true)
    {
        var root = new StackPanel { Width = width, Margin = new Thickness(16, 18, 16, 12) };
        if (item == null) return Wrap(root, width);
        if (showImage)
        {
            double h = Math.Min(Math.Max(220, width * 0.62), width - 32);
            root.Children.Add(MakeVisual(item, h, width - 32, out _));
        }
        AddText(root, item, centered: false);
        return Wrap(root, width);
    }

    /// <summary>
    /// Previsualización que llena el hueco de la vista de columnas: imagen (u hoja de texto) a la escala del hueco,
    /// nombre y datos centrados en una columna de ancho legible, todo centrado en vertical. Se reajusta con <see cref="Fit"/>.
    /// Antes tenía ancho fijo: a pantalla completa quedaba un icono diminuto con los datos desperdigados y en
    /// ventana se salía por la derecha.
    /// </summary>
    public static FrameworkElement BuildFluid(FileItem item, double width, double height)
    {
        var parts = new Parts();
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        double size = VisualSize(width, height);
        parts.Visual = MakeVisual(item, size, size * 1.4, out parts.IsPage);
        stack.Children.Add(parts.Visual);
        var text = new StackPanel { Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        AddText(text, item, centered: true);
        parts.Text = text;
        stack.Children.Add(text);
        parts.Outer = new Grid();
        parts.Outer.Children.Add(stack);
        var sv = new ScrollViewer { Content = parts.Outer, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Tag = parts };
        Fit(sv, width, height);
        return sv;
    }

    /// <summary>Reajusta una previsualización de <see cref="BuildFluid"/> a un hueco nuevo (ventana o columnas redimensionadas).</summary>
    public static void Fit(FrameworkElement pane, double width, double height)
    {
        if (pane?.Tag is not Parts p) return;
        double size = VisualSize(width, height);
        p.Visual.Height = size;
        if (p.IsPage) p.Visual.Width = Math.Round(size * 0.78);
        else p.Visual.MaxWidth = Math.Max(80, Math.Min(width - 48, size * 1.4));
        p.Text.Width = Math.Clamp(width - 48, 160, 340);
        p.Outer.MinHeight = Math.Max(0, height);
    }

    /// <summary>La imagen ocupa como mucho la mitad del alto y cabe a lo ancho con margen.</summary>
    private static double VisualSize(double width, double height)
    {
        double byH = height > 0 ? height * 0.5 : 300;
        return Math.Round(Math.Clamp(Math.Min(byH, width - 64), 96, 440));
    }

    private static FrameworkElement MakeVisual(FileItem item, double size, double maxWidth, out bool isPage)
    {
        isPage = !item.IsDirectory && item.Special == ItemSpecial.None
            && item.Kind.Category is KindCategory.Text or KindCategory.Code or KindCategory.Web or KindCategory.Subtitle;
        if (isPage) return TextPage(item, size);
        var img = new Image { Source = item.Icon, MaxWidth = maxWidth, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, Height = size };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        if (!item.IsDirectory && item.Special == ItemSpecial.None) _ = LoadLarge(item.Path, img);
        return img;
    }

    /// <summary>Hoja blanca con las primeras líneas del archivo (como la miniatura de un documento en Finder).</summary>
    private static FrameworkElement TextPage(FileItem item, double size)
    {
        var lines = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 7.5, LineHeight = 10.5, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)) };
        var badge = new TextBlock { Text = (item.Extension ?? "").ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, 8) };
        var body = new Grid { ClipToBounds = true };
        body.Children.Add(new Border { Child = lines, Padding = new Thickness(12, 12, 12, 24) });
        // el texto se desvanece al pie en vez de cortarse a media línea
        body.Children.Add(new Border { Height = 40, VerticalAlignment = VerticalAlignment.Bottom, Background = new LinearGradientBrush(Color.FromArgb(0, 255, 255, 255), Colors.White, 90) });
        body.Children.Add(badge);
        var page = new Border
        {
            Child = body, Background = Brushes.White, CornerRadius = new CornerRadius(3), Height = size, Width = Math.Round(size * 0.78), HorizontalAlignment = HorizontalAlignment.Center,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), BorderThickness = new Thickness(0.5),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.3, Direction = 270 }
        };
        _ = LoadLines(item.Path, lines);
        return page;
    }

    private static async Task LoadLines(string path, TextBlock target)
    {
        target.Text = await Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buf = new byte[6000];
                int n = fs.Read(buf, 0, buf.Length);
                if (!FileKinds.LooksLikeText(buf.AsSpan(0, n))) return "";
                var enc = DetectEncoding(buf, n, out int skip);
                var s = enc.GetString(buf, skip, n - skip).Replace("\r\n", "\n").Replace('\t', ' ');
                return string.Join("\n", s.Split('\n').Take(70).Select(l => l.Length > 140 ? l[..140] : l));
            }
            catch { return ""; }
        });
    }

    private static System.Text.Encoding DetectEncoding(byte[] b, int n, out int skip)
    {
        skip = 0;
        if (n >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { skip = 3; return System.Text.Encoding.UTF8; }
        if (n >= 2 && b[0] == 0xFF && b[1] == 0xFE) { skip = 2; return System.Text.Encoding.Unicode; }
        if (n >= 2 && b[0] == 0xFE && b[1] == 0xFF) { skip = 2; return System.Text.Encoding.BigEndianUnicode; }
        // UTF-8 válido (sin contar los últimos bytes, que pueden ser un carácter cortado) o, si no, Latin-1
        try { new System.Text.UTF8Encoding(false, true).GetString(b, 0, Math.Max(0, n - 4)); return System.Text.Encoding.UTF8; }
        catch { return System.Text.Encoding.Latin1; }
    }

    /// <summary>Nombre, tipo, etiquetas y la sección «Información».</summary>
    private static void AddText(Panel root, FileItem item, bool centered)
    {
        var align = centered ? TextAlignment.Center : TextAlignment.Left;
        var name = new TextBlock { Text = item.DisplayName, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap, TextAlignment = align, Margin = new Thickness(0, centered ? 0 : 12, 0, 2) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        root.Children.Add(name);
        var sub = new TextBlock { Text = item.IsDirectory ? item.KindText : $"{item.KindText} - {item.SizeText}", FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = align };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        root.Children.Add(sub);

        if (item.HasTags)
        {
            var tags = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left };
            foreach (var t in item.Tags)
            {
                var chip = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 8, 1), Margin = new Thickness(0, 0, 4, 4) };
                chip.SetResourceReference(Border.BackgroundProperty, "Bg.Field");
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Theme.BrushFromHex(TagStore.ColorOf(t)), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                var tt = new TextBlock { Text = t, FontSize = 11.5 };
                tt.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
                sp.Children.Add(tt);
                chip.Child = sp;
                tags.Children.Add(chip);
            }
            root.Children.Add(tags);
        }

        if (centered)
        {
            var line = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 0) };
            line.SetResourceReference(Border.BackgroundProperty, "Line.Strong");
            root.Children.Add(line);
        }
        var header = new TextBlock { Text = "Información", FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, centered ? 12 : 16, 0, 6) };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        root.Children.Add(header);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(grid);
        if (item.Special != ItemSpecial.Drive)
        {
            AddRow(grid, "Creado", Format.Date(item.Created));
            AddRow(grid, item.Special == ItemSpecial.TrashEntry ? "Eliminado" : "Modificado", Format.Date(item.Modified));
        }
        else
        {
            AddRow(grid, "Capacidad", Format.Size(item.Capacity));
            AddRow(grid, "Disponible", Format.Size(item.Extra));
        }
        if (item.Location != null) AddRow(grid, "Ubicación", item.Location);
        if (item.IsDirectory && item.Special == ItemSpecial.None)
            _ = FillFolderInfo(item, grid);
        else if (item.Special == ItemSpecial.None)
            _ = FillShellInfo(item.Path, grid);
    }

    private static FrameworkElement Wrap(FrameworkElement content, double width)
    {
        var sv = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Width = width + 32 };
        return sv;
    }

    private static void AddRow(Grid g, string label, string value)
    {
        int r = g.RowDefinitions.Count;
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 2, 12, 2) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        var v = new TextBlock { Text = value, FontSize = 12, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
        v.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
        g.Children.Add(l); g.Children.Add(v);
    }

    private static async Task LoadLarge(string path, Image img)
    {
        var big = await ThumbnailLoader.LoadLargeAsync(path, 768, allowIcon: false);
        if (big == null) return;
        // una imagen diminuta se muestra a su tamaño real, sin ampliarla pixelada (como Finder)
        if (big.Width < img.MaxWidth && big.Height < img.Height) img.StretchDirection = StretchDirection.DownOnly;
        img.Source = big;
    }

    private static async Task FillShellInfo(string path, Grid grid)
    {
        // hilo STA propio sin bloquear uno del pool (antes esperaba con Join hasta 4 s dentro de Task.Run)
        var tcs = new TaskCompletionSource<List<(string, string)>>();
        var t = new Thread(() => { try { tcs.TrySetResult(ShellInterop.GetInfo(path)); } catch { tcs.TrySetResult(new()); } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA); t.Start();
        var info = await Task.WhenAny(tcs.Task, Task.Delay(4000)) == tcs.Task ? tcs.Task.Result : new List<(string, string)>();
        foreach (var (l, v) in info.Take(10)) AddRow(grid, l, v);
    }

    private static CancellationTokenSource _folderCts;

    private static async Task FillFolderInfo(FileItem item, Grid grid)
    {
        // sólo interesa la carpeta que se está viendo: al previsualizar otra, el cálculo anterior se cancela
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _folderCts, cts)?.Cancel();
        var (count, size) = await Task.Run(() => FolderSize.Compute(item.Path, cts.Token, maxSeconds: 4));
        if (cts.IsCancellationRequested) return;
        AddRow(grid, "Contenido", $"{Format.Items(count)}");
        if (size >= 0) AddRow(grid, "Tamaño", Format.Size(size));
    }
}

/// <summary>Tamaño de carpeta en paralelo (para Obtener información y la previsualización).</summary>
public static class FolderSize
{
    public static (long count, long bytes) Compute(string path, CancellationToken ct, double maxSeconds = double.MaxValue, Action<long, long> progress = null)
    {
        long count = 0, bytes = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 };
        var queue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        queue.Enqueue(path);
        int pending = 1;
        bool partial = false;
        // Hilos propios (LongRunning) y espera con Sleep: con SpinWait en el pool, 8 hilos girando hasta 4 s
        // dejaban sin hilos al resto de la app (abrir la siguiente carpeta tardaba 1,5 s en la Galería).
        var workers = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount / 2, 2, 4)).Select(_ => Task.Factory.StartNew(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                if (sw.Elapsed.TotalSeconds > maxSeconds) { partial = true; return; }
                if (!queue.TryDequeue(out var dir))
                {
                    if (Volatile.Read(ref pending) == 0) return;
                    Thread.Sleep(1); continue;
                }
                try
                {
                    var en = new System.IO.Enumeration.FileSystemEnumerable<(bool d, long l, bool rp, string n)>(dir,
                        (ref System.IO.Enumeration.FileSystemEntry e) => (e.IsDirectory, e.Length, (e.Attributes & FileAttributes.ReparsePoint) != 0, e.FileName.ToString()), opts);
                    foreach (var e in en)
                    {
                        Interlocked.Increment(ref count);
                        if (e.d) { if (!e.rp) { Interlocked.Increment(ref pending); queue.Enqueue(Path.Combine(dir, e.n)); } }
                        else Interlocked.Add(ref bytes, e.l);
                    }
                    progress?.Invoke(Interlocked.Read(ref count), Interlocked.Read(ref bytes));
                }
                catch { }
                finally { Interlocked.Decrement(ref pending); }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Task.WaitAll(workers);
        return (count, partial ? -1 : bytes);
    }
}
