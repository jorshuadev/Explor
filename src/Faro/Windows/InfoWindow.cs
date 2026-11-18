using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Faro.Core;
using Faro.Model;
using Faro.Services;
using Faro.Shell;

namespace Faro.Windows;

/// <summary>Obtener información (Ctrl+I), con las secciones plegables de Finder.</summary>
public sealed class InfoWindow : Window
{
    private readonly FileItem _item;
    private readonly StackPanel _root = new() { Margin = new Thickness(16, 12, 16, 16) };
    private TextBlock _sizeText, _headerSize;
    private readonly CancellationTokenSource _cts = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(int flags, int str, string assoc, string extra, StringBuilder outBuf, ref int outLen);

    public InfoWindow(FileItem item)
    {
        _item = item;
        Title = $"Información de {item.DisplayName}";
        Width = 320; SizeToContent = SizeToContent.Height; MaxHeight = 860;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = (FontFamily)FindResource("UiFont"); FontSize = 12;
        SetResourceReference(BackgroundProperty, "Bg.Dialog");
        Content = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SourceInitialized += (_, _) => Theme.ApplyWindowEffects(this);
        Closed += (_, _) => _cts.Cancel();
        Loaded += (_, _) => MainWindow.FadeIn((UIElement)Content, 200, -8, 0.97);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape || (e.Key == Key.W && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) Close(); };
        Build();
    }

    private void Build()
    {
        var it = _item;
        // ---- cabecera ----
        var head = new Grid { Margin = new Thickness(0, 4, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(new Image { Source = it.Icon, Width = 48, Height = 48 });
        var hs = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = it.DisplayName, FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        _headerSize = Sec(it.IsDirectory && it.Special == ItemSpecial.None ? "Calculando…" : it.SizeText);
        hs.Children.Add(name); hs.Children.Add(_headerSize);
        hs.Children.Add(Sec("Modificado: " + Format.LongDate(it.Modified)));
        Grid.SetColumn(hs, 1); head.Children.Add(hs);
        _root.Children.Add(head);

        // ---- etiquetas ----
        if (it.Special is ItemSpecial.None)
        {
            var tags = new WrapPanel();
            foreach (var t in TagStore.Standard)
            {
                bool on = it.Tags.Contains(t.Name);
                var g = new Grid { Width = 22, Height = 22, Margin = new Thickness(0, 0, 4, 0), Background = Brushes.Transparent, ToolTip = t.Name, Cursor = Cursors.Hand };
                g.Children.Add(new Ellipse { Width = 15, Height = 15, Fill = Theme.BrushFromHex(t.Color) });
                var mark = new TextBlock { Text = "", FontFamily = IconFactory.SymbolFont, FontSize = 8, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = on ? Visibility.Visible : Visibility.Hidden };
                g.Children.Add(mark);
                var tag = t.Name;
                g.MouseLeftButtonUp += (_, _) =>
                {
                    TagStore.Toggle(new[] { it.Path }, tag);
                    it.RefreshTags();
                    mark.Visibility = it.Tags.Contains(tag) ? Visibility.Visible : Visibility.Hidden;
                };
                tags.Children.Add(g);
            }
            AddSection("Etiquetas", tags, true);
        }

        // ---- general ----
        var gen = new Grid();
        gen.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        gen.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Row(gen, "Tipo:", it.KindText);
        if (it.Special == ItemSpecial.Drive)
        {
            Row(gen, "Capacidad:", Format.Size(it.Capacity));
            Row(gen, "Disponible:", Format.Size(it.Extra));
            Row(gen, "Usado:", Format.Size(it.Capacity - it.Extra));
            if (Native.TryGetVolumeInfo(it.Path, out var serial, out _, out var fs)) { Row(gen, "Formato:", fs); Row(gen, "Nº de serie:", serial.ToString("X8")); }
        }
        else
        {
            _sizeText = Row(gen, "Tamaño:", it.IsDirectory ? "Calculando…" : $"{Format.ExactBytes(it.Size)} ({Format.Size(it.Size)})");
            Row(gen, it.Special == ItemSpecial.TrashEntry ? "Estaba en:" : "Ubicación:", it.Special == ItemSpecial.TrashEntry ? it.Location : System.IO.Path.GetDirectoryName(it.Path) ?? it.Path);
            Row(gen, "Creado:", Format.LongDate(it.Created));
            Row(gen, it.Special == ItemSpecial.TrashEntry ? "Eliminado:" : "Modificado:", Format.LongDate(it.Modified));
            try
            {
                var acc = it.IsDirectory ? Directory.GetLastAccessTime(it.Path) : File.GetLastAccessTime(it.Path);
                Row(gen, "Último acceso:", Format.LongDate(acc));
            }
            catch { }
        }
        var genPanel = new StackPanel();
        genPanel.Children.Add(gen);
        if (it.Special == ItemSpecial.None)
        {
            var locked = new CheckBox { Content = "Bloqueado (solo lectura)", Style = (Style)FindResource("MacCheck"), Margin = new Thickness(0, 8, 0, 0), FontSize = 12, IsChecked = (it.Attributes & FileAttributes.ReadOnly) != 0 };
            locked.Click += (_, _) => SetAttr(FileAttributes.ReadOnly, locked.IsChecked == true);
            var hidden = new CheckBox { Content = "Oculto", Style = (Style)FindResource("MacCheck"), Margin = new Thickness(0, 4, 0, 0), FontSize = 12, IsChecked = (it.Attributes & FileAttributes.Hidden) != 0 };
            hidden.Click += (_, _) => SetAttr(FileAttributes.Hidden, hidden.IsChecked == true);
            if (!it.IsDirectory) genPanel.Children.Add(locked);
            genPanel.Children.Add(hidden);
        }
        AddSection("General", genPanel, true);

        // ---- más información ----
        if (it.Special is ItemSpecial.None && !it.IsDirectory)
        {
            var more = new Grid();
            more.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            more.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddSection("Más información", more, true);
            _ = FillMore(more);
        }

        // ---- nombre y extensión ----
        if (it.Special == ItemSpecial.None)
        {
            var nameBox = new TextBox { Text = it.Name, Style = (Style)FindResource("MacTextBox"), Height = 24, FontSize = 12 };
            nameBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                var n = nameBox.Text.Trim();
                if (n.Length > 0 && n != it.Name && Owner is MainWindow mw) { mw.CommitRename(it, n); Title = $"Información de {it.DisplayName}"; }
            };
            var np = new StackPanel();
            np.Children.Add(nameBox);
            np.Children.Add(Sec("Pulsa Intro para renombrar."));
            AddSection("Nombre y extensión", np, false);
        }

        // ---- abrir con ----
        if (!it.IsDirectory && it.Special == ItemSpecial.None)
        {
            var ext = System.IO.Path.GetExtension(it.Path);
            var app = DefaultApp(ext);
            var sp = new StackPanel();
            sp.Children.Add(Prim(app ?? "No hay aplicación asignada"));
            var change = new Button { Content = "Cambiar…", Style = (Style)FindResource("MacButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Height = 24, MinWidth = 0 };
            change.Click += (_, _) => ShellInterop.OpenWith(it.Path, this);
            sp.Children.Add(change);
            AddSection("Abrir con", sp, false);
        }

        // ---- vista previa ----
        if (!it.IsDirectory && it.Special == ItemSpecial.None)
        {
            var img = new Image { Source = it.Icon, MaxHeight = 240, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
            AddSection("Vista previa", img, true);
            _ = LoadPreview(img);
        }

        // ---- permisos ----
        if (it.Special is ItemSpecial.None or ItemSpecial.Drive)
        {
            var b = new Button { Content = "Permisos y seguridad de Windows…", Style = (Style)FindResource("MacButton"), HorizontalAlignment = HorizontalAlignment.Left, Height = 24, MinWidth = 0, Margin = new Thickness(0) };
            b.Click += (_, _) => ShellInterop.ShowWindowsProperties(it.Path);
            AddSection("Compartir y permisos", b, false);
        }

        if (it.IsDirectory && it.Special == ItemSpecial.None) _ = ComputeFolder();
    }

    private void SetAttr(FileAttributes a, bool on)
    {
        try
        {
            var cur = File.GetAttributes(_item.Path);
            File.SetAttributes(_item.Path, on ? cur | a : cur & ~a);
        }
        catch (Exception ex) { Dialogs.MacDialog.Error(this, "No se pudo cambiar el atributo.", ex.Message); }
    }

    private async Task ComputeFolder()
    {
        var path = _item.Path;
        var dispatcher = Dispatcher;
        long lastUpdate = 0;
        var (count, bytes) = await Task.Run(() => Views.FolderSize.Compute(path, _cts.Token, progress: (c, b) =>
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastUpdate) < 200) return;
            Interlocked.Exchange(ref lastUpdate, now);
            dispatcher.BeginInvoke(() => { if (_sizeText != null) _sizeText.Text = $"Calculando… {Format.Size(b)} en {Format.Items(c)}"; });
        }));
        if (_cts.IsCancellationRequested) return;
        var text = $"{Format.ExactBytes(bytes)} ({Format.Size(bytes)}) en disco para {Format.Items(count)}";
        if (_sizeText != null) _sizeText.Text = text;
        _headerSize.Text = $"{Format.Size(bytes)} · {Format.Items(count)}";
        _item.SetFolderSize(bytes);
    }

    private async Task FillMore(Grid g)
    {
        var path = _item.Path;
        var (info, sniff) = await Task.Run(() =>
        {
            var list = new List<(string, string)>();
            var t = new Thread(() => list = ShellInterop.GetInfo(path)) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join(5000);
            return (list, FileKinds.SniffExtension(path));
        });
        foreach (var (l, v) in info) Row(g, l + ":", v);
        // tipo real por contenido (detecta archivos con la extensión equivocada)
        if (sniff != null)
        {
            var real = FileKinds.FromExtension(sniff);
            var declared = _item.Extension;
            bool mismatch = !string.Equals(sniff, declared, StringComparison.OrdinalIgnoreCase)
                            && !(sniff == "jpg" && declared is "jpeg" or "jpe" or "jfif")
                            && !(sniff == "tif" && declared is "tiff" or "dng" or "nef" or "arw" or "orf" or "rw2" or "pef" or "srw")
                            && !(sniff == "zip" && declared is "docx" or "xlsx" or "pptx" or "jar" or "apk" or "xd" or "idml" or "epub" or "odt" or "ods" or "odp" or "nupkg" or "whl" or "3mf" or "kmz" or "aab" or "xapk" or "ipa" or "crx" or "xpi" or "cbz" or "sketch" or "mogrt" or "fig" or "kra" or "usdz" or "msix" or "appx" or "vsdx")
                            && !(sniff == "doc" && declared is "xls" or "ppt" or "msi" or "msg" or "pub" or "vsd" or "dot" or "xlt" or "pot")
                            && !(sniff == "txt" && FileKinds.FromExtension(declared).Category is KindCategory.Text or KindCategory.Code or KindCategory.Web or KindCategory.Subtitle or KindCategory.Other)
                            && !(sniff == "gz" && declared is "prproj" or "tgz" or "svgz")
                            && !(sniff == "pdf" && declared is "ai")
                            && !(sniff == "mp4" && declared is "m4v" or "m4a" or "mov" or "3gp" or "heic" or "avif" or "m4b" or "f4v")
                            && !(sniff == "exe" && declared is "dll" or "sys" or "ocx" or "cpl" or "scr" or "mui" or "com")
                            && !(sniff == "xml" && FileKinds.FromExtension(declared).Category is KindCategory.Code or KindCategory.Web or KindCategory.Text or KindCategory.Design)
                            && !(sniff == "ttf" && declared is "ttc" or "otf");
            var tb = Row(g, "Contenido real:", real.Name);
            if (mismatch && declared.Length > 0)
            {
                tb.Text = $"{real.Name} (.{sniff}) — ¡no coincide con la extensión .{declared}!";
                tb.Foreground = Theme.BrushFromHex("#FF9500");
            }
        }
        if (g.RowDefinitions.Count == 0) Row(g, "", "Sin metadatos adicionales.");
    }

    private async Task LoadPreview(Image img)
    {
        var big = await ThumbnailLoader.LoadLargeAsync(_item.Path, 512, allowIcon: false);
        if (big != null) img.Source = big;
    }

    private string DefaultApp(string ext)
    {
        try
        {
            var sb = new StringBuilder(512); int len = sb.Capacity;
            if (AssocQueryStringW(0, 4 /*ASSOCSTR_FRIENDLYAPPNAME*/, ext, null, sb, ref len) == 0) return sb.ToString();
        }
        catch { }
        return null;
    }

    // ---------- piezas de UI ----------
    private void AddSection(string title, UIElement content, bool open)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var chev = new TextBlock { Text = "", FontFamily = IconFactory.SymbolFont, FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(open ? 90 : 0) };
        chev.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        header.Children.Add(chev); header.Children.Add(t);
        var body = new Border { Child = content, Margin = new Thickness(14, 0, 0, 4), Visibility = open ? Visibility.Visible : Visibility.Collapsed };
        header.MouseLeftButtonUp += (_, _) =>
        {
            bool o = body.Visibility != Visibility.Visible;
            body.Visibility = o ? Visibility.Visible : Visibility.Collapsed;
            chev.RenderTransform = new RotateTransform(o ? 90 : 0);
        };
        var sep = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 0) };
        sep.SetResourceReference(Border.BackgroundProperty, "Line.Separator");
        _root.Children.Add(sep);
        _root.Children.Add(header);
        _root.Children.Add(body);
    }

    private TextBlock Row(Grid g, string label, string value)
    {
        int r = g.RowDefinitions.Count;
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 2, 8, 2), FontWeight = FontWeights.SemiBold };
        l.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
        v.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
        g.Children.Add(l); g.Children.Add(v);
        return v;
    }

    private static TextBlock Sec(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        return t;
    }

    private static TextBlock Prim(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        return t;
    }
}
