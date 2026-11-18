using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Faro.Services;

namespace Faro.Dialogs;

public sealed record DialogButton(string Text, string Result, bool IsDefault = false, bool IsCancel = false);

/// <summary>
/// Cuadro de diálogo estilo alerta de macOS: icono arriba, título en negrita, texto,
/// casilla opcional ("Aplicar a todos") y botones con el principal en azul.
/// Se construye en código para poder usarlo desde cualquier sitio.
/// </summary>
public sealed class MacDialog : Window
{
    public string Result { get; private set; }
    public bool Checked => _check?.IsChecked == true;
    public string InputText => _input?.Text;
    private CheckBox _check;
    private Border _frame;
    private bool _closing;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closing && _frame != null) { e.Cancel = true; _closing = true; MainWindow.FadeOutThen(_frame, Close, 120, 0.95); return; }
        base.OnClosing(e);
    }
    private TextBox _input;

    public MacDialog(Window owner, string title, string message, IEnumerable<DialogButton> buttons,
        string checkText = null, ImageSource icon = null, string glyph = null, string inputDefault = null, FrameworkElement extra = null)
    {
        Owner = owner?.IsLoaded == true ? owner : null;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ShowInTaskbar = Owner == null;
        Title = title;
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        Topmost = Owner == null;

        var panel = new StackPanel { Width = 300, Margin = new Thickness(20, 22, 20, 18) };
        // icono
        var iconHost = new Grid { Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        if (icon != null) iconHost.Children.Add(new Image { Source = icon, Width = 64, Height = 64 });
        else
        {
            iconHost.Children.Add(new Image { Source = AppIcon(), Width = 64, Height = 64 });
            if (glyph != null)
            {
                var badge = new Border
                {
                    Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Theme.BrushFromHex("#FFCC00"),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -6, -4),
                    Child = new TextBlock { Text = glyph, FontFamily = IconFactory.SymbolFont, FontSize = 14, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                iconHost.Children.Add(badge);
            }
        }
        panel.Children.Add(iconHost);
        panel.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.Bold, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Foreground = (Brush)FindResource("Fg.Primary"), Margin = new Thickness(0, 0, 0, 8)
        });
        if (!string.IsNullOrEmpty(message))
            panel.Children.Add(new TextBlock
            {
                Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                Foreground = (Brush)FindResource("Fg.Primary"), Margin = new Thickness(0, 0, 0, 14)
            });
        if (extra != null) { extra.Margin = new Thickness(0, 0, 0, 14); panel.Children.Add(extra); }
        if (inputDefault != null)
        {
            _input = new TextBox { Text = inputDefault, Style = (Style)FindResource("MacTextBox"), Height = 26, Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(_input);
            Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
        }
        if (checkText != null)
        {
            _check = new CheckBox { Content = checkText, Style = (Style)FindResource("MacCheck"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14), FontSize = 12 };
            panel.Children.Add(_check);
        }

        var list = buttons.ToList();
        // Como macOS: con 2 botones van lado a lado a lo ancho; con más, apilados.
        if (list.Count <= 2)
        {
            var g = new UniformGrid { Columns = list.Count };
            foreach (var b in list.OrderBy(b => b.IsDefault ? 1 : 0)) g.Children.Add(MakeButton(b, new Thickness(4, 0, 4, 0)));
            panel.Children.Add(g);
        }
        else
        {
            foreach (var b in list.OrderByDescending(b => b.IsDefault))
                panel.Children.Add(MakeButton(b, new Thickness(0, 0, 0, 7)));
        }

        var border = _frame = new Border
        {
            Child = panel, CornerRadius = new CornerRadius(12), Background = (Brush)FindResource("Bg.Dialog"),
            BorderBrush = (Brush)FindResource("Line.Popup"), BorderThickness = new Thickness(0.5), Margin = new Thickness(24),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.35, Direction = 270 }
        };
        border.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        Content = border;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                var c = list.FirstOrDefault(b => b.IsCancel);
                if (c != null) { Result = c.Result; Close(); e.Handled = true; }
            }
        };
        SourceInitialized += (_, _) => Theme.ApplyWindowEffects(this);
        Loaded += (_, _) => MainWindow.FadeIn(border, 170, -10, 0.96);
    }

    private Button MakeButton(DialogButton b, Thickness margin)
    {
        var btn = new Button { Content = b.Text, Style = (Style)FindResource("MacButton"), IsDefault = b.IsDefault, IsCancel = false, Margin = margin, Height = 28 };
        btn.Click += (_, _) => { Result = b.Result; Close(); };
        return btn;
    }

    private static ImageSource _appIcon;
    public static ImageSource AppIcon()
    {
        if (_appIcon != null) return _appIcon;
        try
        {
            var dec = new System.Windows.Media.Imaging.IconBitmapDecoder(new Uri("pack://application:,,,/Assets/explor.ico"),
                System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            _appIcon = dec.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch { _appIcon = IconFactory.Folder(); }
        return _appIcon;
    }

    public string ShowFor() { ShowDialog(); return Result; }

    // ---------- atajos ----------
    public static void Error(Window owner, string title, string message) =>
        new MacDialog(owner, title, message, new[] { new DialogButton("Aceptar", "ok", true, true) }, glyph: "").ShowFor();

    public static void Info(Window owner, string title, string message) =>
        new MacDialog(owner, title, message, new[] { new DialogButton("Aceptar", "ok", true, true) }).ShowFor();

    public static bool Confirm(Window owner, string title, string message, string okText, bool destructive = false) =>
        new MacDialog(owner, title, message, new[] { new DialogButton("Cancelar", "cancel", false, true), new DialogButton(okText, "ok", true) },
            glyph: destructive ? "" : null).ShowFor() == "ok";

    public static string Ask(Window owner, string title, string message, string defaultText, string okText = "Aceptar")
    {
        var d = new MacDialog(owner, title, message, new[] { new DialogButton("Cancelar", "cancel", false, true), new DialogButton(okText, "ok", true) }, inputDefault: defaultText);
        return d.ShowFor() == "ok" ? d.InputText : null;
    }
}

/// <summary>UniformGrid de WPF sin depender de System.Windows.Controls.Primitives en el resto del código.</summary>
public sealed class UniformGrid : System.Windows.Controls.Primitives.UniformGrid { }
