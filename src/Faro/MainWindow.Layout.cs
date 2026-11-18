using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Faro.Services;

namespace Faro;

/// <summary>
/// Adaptación a la pantalla: la ventana nunca nace más grande que el monitor (el disco viaja
/// entre portátiles de 1920 y 1366 px), el margen al maximizar sale del monitor real (no un 7 fijo)
/// y la barra de herramientas, la barra lateral y la previsualización se compactan en ventanas estrechas.
/// </summary>
public partial class MainWindow
{
    /// <summary>La barra lateral está oculta sólo porque la ventana es estrecha (no se guarda en ajustes).</summary>
    private bool _sidebarAutoHidden;
    private const double SidebarAutoHideBelow = 700, SidebarAutoShowFrom = 780;

    private bool SidebarVisible => Settings.Current.ShowSidebar && !_sidebarAutoHidden;

    /// <summary>Hay sitio para el panel de previsualización sin estrujar la vista.</summary>
    private bool RoomForPreview => ContentArea.ActualWidth <= 0 || ContentArea.ActualWidth - PreviewWidth >= 520;

    private double PreviewWidth => Math.Clamp(ActualWidth * 0.26, 220, 290);

    private bool WorkArea(out MONRECT work, out DpiScale dpi)
    {
        work = default; dpi = new DpiScale(1, 1);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return false;
        work = mi.rcWork;
        dpi = VisualTreeHelper.GetDpi(this);
        return true;
    }

    /// <summary>
    /// Ajusta la ventana al área útil del monitor donde va a aparecer. Todo en píxeles reales: con
    /// varios monitores de distinta escala las coordenadas de WPF no son fiables antes de mostrarse.
    /// </summary>
    private void FitToMonitor()
    {
        if (WindowState != WindowState.Normal || _fullScreen) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!WorkArea(out var w, out _) || !GetWindowRect(hwnd, out var r)) return;
        int ww = w.R - w.L, wh = w.B - w.T;
        int width = r.R - r.L, height = r.B - r.T;
        bool tooBig = width > ww || height > wh;
        if (tooBig)
        {
            // viene de una pantalla más grande: un 90 % del área útil, como una ventana nueva
            width = Math.Min(width, (int)(ww * 0.9));
            height = Math.Min(height, (int)(wh * 0.9));
        }
        int x = r.L, y = r.T;
        // que se vea al menos la barra de título entera; si no, centrar
        bool offscreen = x < w.L - 8 || y < w.T || x + width > w.R + 8 || y + 60 > w.B;
        if (tooBig || offscreen)
        {
            x = w.L + (ww - width) / 2;
            y = w.T + (wh - height) / 2;
        }
        if (x != r.L || y != r.T || width != r.R - r.L || height != r.B - r.T)
            SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>Al maximizar, Windows deja el borde de redimensionado fuera de la pantalla: se compensa con lo que sobresale de verdad.</summary>
    private void FixMaximizedMargin()
    {
        if (WindowState != WindowState.Maximized) { Root.Margin = new Thickness(0); return; }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (WorkArea(out var w, out var dpi) && GetWindowRect(hwnd, out var r))
        {
            double l = Math.Max(0, w.L - r.L) / dpi.DpiScaleX, t = Math.Max(0, w.T - r.T) / dpi.DpiScaleY;
            double rr = Math.Max(0, r.R - w.R) / dpi.DpiScaleX, b = Math.Max(0, r.B - w.B) / dpi.DpiScaleY;
            Root.Margin = new Thickness(l, t, rr, b);
        }
        else Root.Margin = new Thickness(7);
    }

    /// <summary>Reparte el espacio según el ancho de la ventana.</summary>
    private void ApplyCompactLayout()
    {
        if (!IsLoaded) return;
        double width = ActualWidth;

        // barra lateral: se aparta sola en ventanas estrechas y vuelve al ensanchar
        if (Settings.Current.ShowSidebar)
        {
            if (!_sidebarAutoHidden && width < SidebarAutoHideBelow)
            {
                _sidebarAutoHidden = true;
                SidebarCol.Width = new GridLength(0);
            }
            else if (_sidebarAutoHidden && width >= SidebarAutoShowFrom)
            {
                _sidebarAutoHidden = false;
                AnimateColumn(SidebarCol, SidebarWidthFor(width));
            }
            else if (!_sidebarAutoHidden && SidebarCol.ActualWidth > SidebarWidthFor(width) + 1)
                SidebarCol.Width = new GridLength(SidebarWidthFor(width));
        }
        LightsInline.Visibility = SidebarVisible ? Visibility.Collapsed : Visibility.Visible;

        // barra de herramientas: Compartir/Etiquetas pasan al menú «Más», la búsqueda se acorta
        double main = width - (SidebarVisible ? SidebarCol.ActualWidth : 0);
        bool narrow = main < 640, tiny = main < 470;
        ShareBtn.Visibility = TagBtn.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        SortBtn.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
        _searchIdleWidth = tiny ? 110 : narrow ? 130 : 170;
        _searchFocusWidth = tiny ? 150 : narrow ? 190 : 250;
        if (!SearchText.IsKeyboardFocused && SearchText.Text.Length == 0) { SearchBox.BeginAnimation(WidthProperty, null); SearchBox.Width = _searchIdleWidth; }
        ZoomPanel.Visibility = _tab?.Mode == ViewMode.Icons && main >= 520 ? Visibility.Visible : Visibility.Hidden;

        // el título ocupa lo que quede, nunca pisa los botones de la derecha
        RightTools.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lights = LightsInline.Visibility == Visibility.Visible ? 80 : 0;
        double room = main - 18 - lights - 70 /* atrás/adelante */ - RightTools.DesiredSize.Width - 16;
        TitleText.MaxWidth = Math.Max(0, Math.Min(360, room));
        TitleText.Visibility = room < 50 ? Visibility.Collapsed : Visibility.Visible;

        UpdatePreviewPane();
    }

    private double SidebarWidthFor(double windowWidth) => Math.Clamp(Math.Min(Settings.Current.SidebarWidth, windowWidth * 0.3), 160, 420);

    private double _searchIdleWidth = 170, _searchFocusWidth = 250;
}
