using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Faro.Model;
using Faro.Services;

namespace Faro.Windows;

/// <summary>Base de las ventanitas tipo "hoja" de macOS (sin marco, redondeadas, con sombra).</summary>
public abstract class SheetWindow : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(20, 18, 20, 16) };
    private Border _frame;
    private bool _closing;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closing && _frame != null) { e.Cancel = true; _closing = true; MainWindow.FadeOutThen(_frame, Close, 120, 0.95, -6); return; }
        base.OnClosing(e);
    }

    protected SheetWindow(double width)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.Height; Width = width + 48;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        FontFamily = (FontFamily)FindResource("UiFont"); FontSize = 13;
        var b = _frame = new Border
        {
            Child = Body, CornerRadius = new CornerRadius(12), Margin = new Thickness(24), BorderThickness = new Thickness(0.5),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.35, Direction = 270 }
        };
        b.SetResourceReference(Border.BackgroundProperty, "Bg.Dialog");
        b.SetResourceReference(Border.BorderBrushProperty, "Line.Popup");
        b.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Border) try { DragMove(); } catch { } };
        Content = b;
        Loaded += (_, _) => MainWindow.FadeIn(b, 170, -10, 0.97);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
        SourceInitialized += (_, _) => Theme.ApplyWindowEffects(this);
    }

    protected TextBlock Label(string text, bool bold = false, bool secondary = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "Fg.Secondary" : "Fg.Primary");
        return t;
    }

    protected CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Style = (Style)FindResource("MacCheck"), Margin = new Thickness(0, 4, 0, 4) };
        c.Click += (_, _) => set(c.IsChecked == true);
        return c;
    }

    protected StackPanel Buttons(params Button[] buttons)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in buttons) sp.Children.Add(b);
        return sp;
    }

    protected Button Btn(string text, Action a, bool isDefault = false)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("MacButton"), IsDefault = isDefault };
        b.Click += (_, _) => a();
        return b;
    }

    protected ComboBox Combo(IEnumerable<(string text, object value)> options, object current, Action<object> set)
    {
        var cb = new ComboBox { Margin = new Thickness(0, 2, 0, 8), MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (t, v) in options) { var i = new ComboBoxItem { Content = t, Tag = v }; cb.Items.Add(i); if (Equals(v, current)) cb.SelectedItem = i; }
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem i) set(i.Tag); };
        return cb;
    }
}

/// <summary>Ir a la carpeta (Ctrl+Mayús+G) con autocompletado; acepta ~, %VARIABLES% y rutas de red.</summary>
public sealed class GoToWindow : SheetWindow
{
    public string Result { get; private set; }
    private readonly TextBox _box;
    private readonly ListBox _list;

    public GoToWindow(string start) : base(440)
    {
        Body.Children.Add(Label("Ir a la carpeta:", bold: true));
        _box = new TextBox { Style = (Style)FindResource("MacTextBox"), Height = 28, Text = start.EndsWith('\\') ? start : start + "\\" };
        Body.Children.Add(_box);
        _list = new ListBox { MaxHeight = 220, Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Visibility = Visibility.Collapsed };
        Body.Children.Add(_list);
        Body.Children.Add(Buttons(Btn("Cancelar", () => DialogResult = false), Btn("Ir", Go, true)));
        _box.TextChanged += (_, _) => Suggest();
        _box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab && _list.Items.Count > 0) { Complete(_list.SelectedItem as string ?? (string)_list.Items[0]); e.Handled = true; }
            else if (e.Key == Key.Down && _list.Items.Count > 0) { _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1); e.Handled = true; }
            else if (e.Key == Key.Up && _list.Items.Count > 0) { _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1); e.Handled = true; }
            else if (e.Key == Key.Enter) { if (_list.SelectedItem is string s) Complete(s); Go(); e.Handled = true; }
        };
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is string s) { Complete(s); Go(); } };
        Loaded += (_, _) => { _box.Focus(); _box.CaretIndex = _box.Text.Length; };
    }

    // historial + subcarpetas + recursos de \\servidor (lo mismo que la barra de ruta)
    private CancellationTokenSource _cts;
    private async void Suggest()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        List<PathSuggestion> list;
        try { list = await PathHistory.SuggestAsync(_box.Text, false, cts.Token); } catch { return; }
        if (cts.IsCancellationRequested) return;
        _list.Items.Clear();
        foreach (var s in list) _list.Items.Add(s.Path);
        _list.Visibility = _list.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Complete(string s) { _box.Text = s.TrimEnd('\\') + "\\"; _box.CaretIndex = _box.Text.Length; }

    private void Go()
    {
        var t = PathHistory.Expand(_box.Text);
        if (t.Length > 3) t = t.TrimEnd('\\');
        // una ruta de red no se comprueba aquí: esperaría al servidor con la ventana congelada
        if (NetworkService.IsUnc(t) || Directory.Exists(t) || File.Exists(t)) { Result = t; DialogResult = true; }
        else Dialogs.MacDialog.Error(this, "No se puede encontrar la carpeta.", t);
    }
}

/// <summary>
/// Conectar al servidor (Ctrl+K), como en Finder: se escribe «192.168.1.10», «\\servidor\recurso» o
/// «smb://…»; debajo, los servidores recientes y los equipos encontrados en la red.
/// </summary>
public sealed class ConnectServerWindow : SheetWindow
{
    public string Result { get; private set; }
    private readonly TextBox _box;
    private readonly ListBox _list;

    public ConnectServerWindow() : base(440)
    {
        Body.Children.Add(Label("Conectar al servidor", bold: true));
        Body.Children.Add(Label(@"Dirección (por ejemplo 192.168.1.10 o \\192.168.1.10\Compartida):", secondary: true));
        _box = new TextBox { Style = (Style)FindResource("MacTextBox"), Height = 28, Text = Settings.Current.Servers.FirstOrDefault() ?? @"\\" };
        Body.Children.Add(_box);
        Body.Children.Add(new TextBlock { Height = 8 });
        Body.Children.Add(Label("Servidores recientes y equipos de la red:", secondary: true));
        _list = new ListBox { Height = 170, BorderThickness = new Thickness(0.5), Background = Brushes.Transparent };
        _list.SetResourceReference(Control.BorderBrushProperty, "Line.Separator");
        Body.Children.Add(_list);
        var hint = Label("Si el servidor pide usuario y contraseña, Windows los preguntará (y puede recordarlos).", secondary: true);
        hint.FontSize = 11.5; hint.Margin = new Thickness(0, 6, 0, 0);
        Body.Children.Add(hint);
        Body.Children.Add(Buttons(Btn("Explorar la red", () => { Result = "faro:network"; DialogResult = true; }), Btn("Cancelar", () => DialogResult = false), Btn("Conectar", Go, true)));
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is ListBoxItem { Tag: string p }) _box.Text = p; };
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is ListBoxItem { Tag: string }) Go(); };
        Fill();
        NetworkService.Discovered += Fill;
        Closed += (_, _) => NetworkService.Discovered -= Fill;
        NetworkService.StartDiscovery();
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    private void Fill()
    {
        _list.Items.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Settings.Current.Servers) if (seen.Add(s)) _list.Items.Add(Row(s, "\uE81C", s));
        foreach (var c in NetworkService.Found)
            if (seen.Add(c.Path)) _list.Items.Add(Row(c.Path, "\uE968", c.Address != null && c.Address != c.Name ? $"{c.Name}  ({c.Address})" : c.Name));
        if (NetworkService.Scanning)
        {
            var wait = new ListBoxItem { Content = "Buscando equipos en la red…", IsEnabled = false };
            wait.SetResourceReference(Control.ForegroundProperty, "Fg.Secondary");
            _list.Items.Add(wait);
        }
    }

    private static ListBoxItem Row(string path, string glyph, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var g = new TextBlock { Text = glyph, FontFamily = IconFactory.SymbolFont, FontSize = 12, Width = 22, VerticalAlignment = VerticalAlignment.Center };
        g.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        sp.Children.Add(g); sp.Children.Add(t);
        return new ListBoxItem { Content = sp, Tag = path, Padding = new Thickness(4, 3, 4, 3) };
    }

    private void Go()
    {
        var t = NetworkService.NormalizeInput(_box.Text);
        if (t == null || t.Length <= 2)
        {
            Dialogs.MacDialog.Error(this, "Esa dirección no parece de un servidor.", @"Escribe una IP (192.168.1.10), un nombre de equipo con \\ delante (\\SERVIDOR) o smb://servidor/carpeta.");
            return;
        }
        Result = t; DialogResult = true;
    }
}

/// <summary>Ajustes de Faro (Ctrl+,).</summary>
public sealed class PreferencesWindow : SheetWindow
{
    public PreferencesWindow() : base(420)
    {
        var s = Settings.Current;
        Body.Children.Add(Label($"Ajustes de {Core.AppInfo.Name}", bold: true));
        Body.Children.Add(Label("Las ventanas nuevas muestran:", secondary: true));
        Body.Children.Add(Combo(new (string, object)[]
        {
            (Environment.UserName + " (carpeta personal)", "home"), ("Recientes", "recents"), ("Este equipo", "computer"),
            ("Escritorio", "desktop"), ("Documentos", "documents"), ("Descargas", "downloads")
        }, s.NewWindowTarget, v => { s.NewWindowTarget = (string)v; s.Save(); }));
        Body.Children.Add(Label("Apariencia:", secondary: true));
        Body.Children.Add(Combo(new (string, object)[] { ("Igual que Windows", ThemeMode.System), ("Claro", ThemeMode.Light), ("Oscuro", ThemeMode.Dark) },
            s.Theme, v => { s.Theme = (ThemeMode)v; s.Save(); Theme.Apply(); }));
        Body.Children.Add(Check("Mostrar todas las extensiones de archivo", s.ShowAllExtensions, v => { s.ShowAllExtensions = v; Settings.NotifyChanged(); RefreshAll(); }));
        Body.Children.Add(Check("Avisar antes de cambiar una extensión", s.WarnChangeExtension, v => { s.WarnChangeExtension = v; s.Save(); }));
        Body.Children.Add(Check("Avisar antes de vaciar la Papelera", s.WarnBeforeEmptyTrash, v => { s.WarnBeforeEmptyTrash = v; s.Save(); }));
        Body.Children.Add(Check("Mantener las carpetas arriba al ordenar por nombre", s.FoldersFirst, v => { s.FoldersFirst = v; Settings.NotifyChanged(); RefreshAll(); }));
        Body.Children.Add(Check("Mostrar archivos ocultos", s.ShowHidden, v => { s.ShowHidden = v; Settings.NotifyChanged(); RefreshAll(); }));
        Body.Children.Add(Check("Al buscar, buscar en la carpeta actual (si no, en todo el equipo)", s.SearchThisFolderByDefault, v => { s.SearchThisFolderByDefault = v; s.Save(); }));
        Body.Children.Add(Label(Faro.Core.Search.EverythingClient.IsAvailable
            ? "✓ Everything está abierto: la búsqueda en todo el equipo es instantánea."
            : "Consejo: si abres Everything (voidtools), la búsqueda en todo el equipo será instantánea.", secondary: true));
        Body.Children.Add(Buttons(Btn("Listo", () => Close(), true)));
    }

    private static void RefreshAll()
    {
        foreach (var w in Application.Current.Windows.OfType<MainWindow>())
            foreach (var t in w.Tabs) { _ = t.LoadAsync(); }
    }
}

/// <summary>Opciones de visualización (Ctrl+J).</summary>
public sealed class ViewOptionsWindow : SheetWindow
{
    public ViewOptionsWindow(MainWindow owner) : base(300)
    {
        var s = Settings.Current;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = owner.Left + owner.ActualWidth - 380; Top = owner.Top + 70;
        Body.Children.Add(Label("Opciones de visualización", bold: true));
        Body.Children.Add(Label("Tamaño de los iconos:", secondary: true));
        var slider = new Slider { Minimum = 32, Maximum = 256, Value = s.IconSize, Style = (Style)FindResource("MiniSlider"), Margin = new Thickness(0, 4, 0, 10) };
        slider.ValueChanged += (_, e) => owner.ZoomSlider.Value = e.NewValue;
        Body.Children.Add(slider);
        Body.Children.Add(Label("Ordenar por:", secondary: true));
        Body.Children.Add(Combo(new (string, object)[] { ("Nombre", SortField.Name), ("Tipo", SortField.Kind), ("Fecha de modificación", SortField.Modified), ("Fecha de creación", SortField.Created), ("Tamaño", SortField.Size) },
            s.SortBy, v => { s.SortBy = (SortField)v; Settings.NotifyChanged(); owner.Tab?.Resort(); }));
        Body.Children.Add(Check("Orden descendente", s.SortDescending, v => { s.SortDescending = v; Settings.NotifyChanged(); owner.Tab?.Resort(); }));
        Body.Children.Add(Check("Carpetas arriba", s.FoldersFirst, v => { s.FoldersFirst = v; Settings.NotifyChanged(); owner.Tab?.Resort(); }));
        Body.Children.Add(Check("Mostrar previsualización", s.ShowPreviewPane, v => { s.ShowPreviewPane = v; Settings.NotifyChanged(); }));
        Body.Children.Add(Check("Barra de ruta", s.ShowPathBar, v => { s.ShowPathBar = v; Settings.NotifyChanged(); }));
        Body.Children.Add(Check("Barra de estado", s.ShowStatusBar, v => { s.ShowStatusBar = v; Settings.NotifyChanged(); }));
        Body.Children.Add(Buttons(Btn("Listo", () => Close(), true)));
        Deactivated += (_, _) => { try { Close(); } catch { } };
    }
}
