using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Faro.Services;
using Faro.Shell;

namespace Faro;

/// <summary>
/// Botones de ventana al estilo macOS (rojo cerrar, amarillo minimizar, verde pantalla completa):
/// 14 px, símbolos dibujados que aparecen al pasar el ratón por el grupo, grises si la ventana
/// no está activa. El verde pone la ventana en pantalla completa (tapa la barra de tareas);
/// Alt+clic en el verde o doble clic en la barra la maximizan.
/// </summary>
public partial class MainWindow
{
    private sealed record Light(Ellipse Dot, UIElement Glyph, Color Fill, Color Stroke);
    private readonly List<Light> _lights = new();
    private bool _fullScreen;
    private Rect _boundsBeforeFull;
    private WindowState _stateBeforeFull;

    private void BuildLights()
    {
        foreach (var panel in new[] { Lights, LightsInline })
        {
            panel.Children.Clear();
            var glyphs = new List<UIElement>();
            AddLight(panel, glyphs, "#FF5F57", "#E2463F", "#4D0000", "M4,4 L10,10 M10,4 L4,10", false, "Cerrar", () => Close());
            AddLight(panel, glyphs, "#FEBC2E", "#E1A116", "#985712", "M3.5,7 H10.5", false, "Minimizar", () => WindowState = WindowState.Minimized);
            AddLight(panel, glyphs, "#28C840", "#1DAD2B", "#0B650D", "M4.3,8.9 L4.3,4.3 L8.9,4.3 Z M9.7,5.1 L9.7,9.7 L5.1,9.7 Z", true, "Pantalla completa (Alt+clic: maximizar)", () =>
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { if (_fullScreen) ToggleFullScreen(); Maximize_Click(null, null); }
                else ToggleFullScreen();
            });
            panel.MouseEnter += (_, _) => { foreach (var g in glyphs) Animate(g, 1, 90); };
            panel.MouseLeave += (_, _) => { foreach (var g in glyphs) Animate(g, 0, 140); };
        }
    }

    private void AddLight(StackPanel panel, List<UIElement> glyphs, string fill, string stroke, string glyphColor, string geo, bool filled, string tip, Action click)
    {
        var fc = (Color)ColorConverter.ConvertFromString(fill);
        var sc = (Color)ColorConverter.ConvertFromString(stroke);
        var gc = new SolidColorBrush((Color)ColorConverter.ConvertFromString(glyphColor));
        var dot = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(fc), Stroke = new SolidColorBrush(sc), StrokeThickness = 0.7 };
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(geo), Width = 14, Height = 14, Opacity = 0, IsHitTestVisible = false,
            Stroke = filled ? null : gc, StrokeThickness = 1.4, Fill = filled ? gc : null,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        };
        var press = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(Color.FromArgb(55, 0, 0, 0)), Opacity = 0, IsHitTestVisible = false };
        var g = new Grid { Width = 14, Height = 14, Margin = new Thickness(0, 0, 9, 0), Background = Brushes.Transparent, ToolTip = tip, Cursor = Cursors.Arrow, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(dot); g.Children.Add(press); g.Children.Add(path);
        g.MouseLeftButtonDown += (_, e) => { press.Opacity = 1; g.CaptureMouse(); e.Handled = true; };
        g.MouseLeftButtonUp += (_, e) =>
        {
            press.Opacity = 0; g.ReleaseMouseCapture();
            if (g.IsMouseOver) click();
            e.Handled = true;
        };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(g, true);
        panel.Children.Add(g);
        glyphs.Add(path);
        _lights.Add(new Light(dot, path, fc, sc));
    }

    private void SetLightsActive(bool active)
    {
        foreach (var l in _lights)
        {
            var inactive = Theme.IsDark ? Color.FromRgb(0x4A, 0x4A, 0x4C) : Color.FromRgb(0xCF, 0xCF, 0xD1);
            l.Dot.Fill = new SolidColorBrush(active ? l.Fill : inactive);
            l.Dot.Stroke = new SolidColorBrush(active ? l.Stroke : Color.FromArgb(40, 0, 0, 0));
        }
    }

    // ----- pantalla completa -----

    [StructLayout(LayoutKind.Sequential)] private struct MONRECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public MONRECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_NOOWNERZORDER = 0x200;
    private MONRECT _pixelsBeforeFull;

    /// <summary>
    /// Pantalla completa sin parpadeo: un único SetWindowPos en píxeles (sin pasar por
    /// "restaurar" ni por varios redimensionados) y un asentado suave del contenido que
    /// nunca baja de 0,9 de opacidad.
    /// </summary>
    private bool _transitioning;

    public void ToggleFullScreen()
    {
        try { ToggleFullScreenCore(); }
        finally
        {
            // en pantalla completa, la barra de tareas aparece al llevar el ratón a su borde (como el Dock)
            _reveal ??= new TaskbarReveal(new WindowInteropHelper(this).Handle, () => { Activate(); });
            if (_fullScreen) _reveal.Start(); else _reveal.Stop();
        }
    }

    private TaskbarReveal _reveal;

    private void ToggleFullScreenCore()
    {
        if (_transitioning) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        // rectángulo final calculado de antemano para que la animación empiece al instante
        var mon = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mon);
        MONRECT target = !_fullScreen ? mon.rcMonitor : _stateBeforeFull == WindowState.Maximized ? mon.rcWork : _pixelsBeforeFull;
        AnimatedResize(new WindowZoom.RECT { L = target.L, T = target.T, R = target.R, B = target.B }, () =>
        {
            if (!_fullScreen)
            {
                _stateBeforeFull = WindowState;
                GetWindowRect(hwnd, out _pixelsBeforeFull);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi);
                if (WindowState == WindowState.Maximized)
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    _boundsBeforeFull = RestoreBounds;
                    Left = mi.rcMonitor.L / dpi.DpiScaleX; Top = mi.rcMonitor.T / dpi.DpiScaleY;
                    Width = (mi.rcMonitor.R - mi.rcMonitor.L) / dpi.DpiScaleX; Height = (mi.rcMonitor.B - mi.rcMonitor.T) / dpi.DpiScaleY;
                    WindowState = WindowState.Normal;
                }
                else _boundsBeforeFull = new Rect(Left, Top, Width, Height);
                _fullScreen = true;
                ResizeMode = ResizeMode.NoResize;
                SetCorners(hwnd, false);
                SetWindowPos(hwnd, IntPtr.Zero, mi.rcMonitor.L, mi.rcMonitor.T, mi.rcMonitor.R - mi.rcMonitor.L, mi.rcMonitor.B - mi.rcMonitor.T,
                    SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
            }
            else
            {
                _fullScreen = false;
                ResizeMode = ResizeMode.CanResize;
                SetCorners(hwnd, true);
                if (_stateBeforeFull == WindowState.Maximized)
                {
                    WindowState = WindowState.Maximized;
                    Left = _boundsBeforeFull.Left; Top = _boundsBeforeFull.Top; Width = _boundsBeforeFull.Width; Height = _boundsBeforeFull.Height;
                }
                else
                {
                    var r = _pixelsBeforeFull;
                    SetWindowPos(hwnd, IntPtr.Zero, r.L, r.T, r.R - r.L, r.B - r.T, SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
                }
            }
        });
    }

    /// <summary>
    /// Transición tipo macOS: foto de la ventana antes y después del cambio de tamaño; una capa
    /// por encima anima el marco del rectángulo viejo al nuevo y funde una foto en la otra.
    /// La ventana real ya está en su tamaño final debajo, así que no se ve ningún reajuste.
    /// </summary>
    private void AnimatedResize(WindowZoom.RECT target, Action resize)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out var r);
        _transitioning = true;
        var from = new WindowZoom.RECT { L = r.L, T = r.T, R = r.R, B = r.B };
        WindowZoom.Run(hwnd, from, target, resize, Dispatcher, 300, roundFrom: !_fullScreen, roundTo: _fullScreen);
        // al terminar, se vuelve a permitir otra transición
        _ = Task.Delay(900).ContinueWith(_ => _transitioning = false);
    }
    private BitmapSource Snapshot()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)Math.Round(Root.ActualWidth * dpi.DpiScaleX), h = (int)Math.Round(Root.ActualHeight * dpi.DpiScaleY);
            if (w < 10 || h < 10) return null;
            var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            // fondo de la ventana + contenido
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Background, null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(Root), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
            }
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        catch { return null; }
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out MONRECT r);

    private static void SetCorners(IntPtr hwnd, bool round)
    {
        int pref = round ? 2 : 1; // 2 = redondeadas, 1 = rectas (en pantalla completa)
        DwmSetWindowAttribute(hwnd, 33, ref pref, 4);
    }

    /// <summary>Asentado tras cambiar de tamaño: escala 0,985→1 y opacidad 0,9→1 (sin destello).</summary>
    private void Settle()
    {
        var st = new ScaleTransform(0.985, 0.985);
        Root.RenderTransform = st;
        Root.RenderTransformOrigin = new Point(0.5, 0.5);
        var d = TimeSpan.FromMilliseconds(260);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1, d) { EasingFunction = Ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, d) { EasingFunction = Ease });
        Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.9, 1, d) { EasingFunction = Ease });
    }
    // ----- animaciones -----

    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static void Animate(UIElement e, double opacity, int ms)
    {
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });
    }

    /// <summary>Aparece con fundido y un pequeño desplazamiento (al navegar, como Finder).</summary>
    public static void FadeIn(UIElement e, int ms = 180, double fromY = 6, double fromScale = 1, double fromX = 0)
    {
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });
        if (fromY != 0 || fromScale != 1 || fromX != 0)
        {
            var tg = new TransformGroup();
            var st = new ScaleTransform(fromScale, fromScale);
            var tt = new TranslateTransform(fromX, fromY);
            tg.Children.Add(st); tg.Children.Add(tt);
            e.RenderTransform = tg;
            e.RenderTransformOrigin = new Point(0.5, 0.5);
            var d = TimeSpan.FromMilliseconds(ms + 40);
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, d) { EasingFunction = Ease });
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, d) { EasingFunction = Ease });
            if (fromScale != 1)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, 1, d) { EasingFunction = Ease });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, 1, d) { EasingFunction = Ease });
            }
        }
    }

    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

    /// <summary>Desaparece (fundido + encogerse un poco) y luego ejecuta la acción. Para cerrar ventanas y diálogos.</summary>
    public static void FadeOutThen(UIElement e, Action then, int ms = 140, double toScale = 0.96, double toY = 0)
    {
        var st = new ScaleTransform(1, 1);
        var tt = new TranslateTransform(0, 0);
        var tg = new TransformGroup(); tg.Children.Add(st); tg.Children.Add(tt);
        e.RenderTransform = tg; e.RenderTransformOrigin = new Point(0.5, 0.5);
        var d = TimeSpan.FromMilliseconds(ms);
        var fade = new DoubleAnimation(e.Opacity, 0, d) { EasingFunction = EaseIn };
        bool done = false;
        fade.Completed += (_, _) => { if (!done) { done = true; then(); } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, toScale, d) { EasingFunction = EaseIn });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, toScale, d) { EasingFunction = EaseIn });
        if (toY != 0) tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, toY, d) { EasingFunction = EaseIn });
        e.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Plegar / desplegar con animación de altura (secciones de la barra lateral, Información).</summary>
    public static void AnimateCollapse(FrameworkElement e, bool show, int ms = 180)
    {
        e.BeginAnimation(FrameworkElement.HeightProperty, null);
        if (show)
        {
            e.Visibility = Visibility.Visible;
            e.Measure(new Size(e.ActualWidth > 0 ? e.ActualWidth : 300, double.PositiveInfinity));
            double h = e.DesiredSize.Height;
            var a = new DoubleAnimation(0, h, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease };
            a.Completed += (_, _) => { e.BeginAnimation(FrameworkElement.HeightProperty, null); e.Height = double.NaN; };
            e.BeginAnimation(FrameworkElement.HeightProperty, a);
            FadeIn(e, ms, 0);
        }
        else
        {
            var a = new DoubleAnimation(e.ActualHeight, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseIn };
            a.Completed += (_, _) => { e.Visibility = Visibility.Collapsed; e.BeginAnimation(FrameworkElement.HeightProperty, null); e.Height = double.NaN; };
            e.BeginAnimation(FrameworkElement.HeightProperty, a);
        }
    }

    /// <summary>Anima un ancho de columna (barra lateral, panel de previsualización).</summary>
    public static void AnimateColumn(ColumnDefinition col, double to, int ms = 200)
    {
        double from = col.ActualWidth;
        var start = DateTime.Now;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(12) };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / ms);
            double k = 1 - Math.Pow(1 - t, 3);
            col.Width = new GridLength(from + (to - from) * k);
            if (t >= 1) timer.Stop();
        };
        timer.Start();
    }

    public static void AnimateWidth(FrameworkElement e, double to, int ms = 180)
    {
        e.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });
    }
}
