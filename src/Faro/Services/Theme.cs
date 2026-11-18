using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Faro.Services;

/// <summary>Claro / oscuro (sigue a Windows por defecto) y efectos de ventana de Windows 11.</summary>
public static class Theme
{
    public static bool IsDark { get; private set; }
    public static event Action Changed;

    public static void Apply()
    {
        bool dark = Settings.Current.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemPrefersDark()
        };
        IsDark = dark;
        var app = Application.Current;
        var dicts = app.Resources.MergedDictionaries;
        var uri = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        var existing = dicts.FirstOrDefault(d => d.Source != null && (d.Source.OriginalString.EndsWith("Light.xaml") || d.Source.OriginalString.EndsWith("Dark.xaml")));
        var nd = new ResourceDictionary { Source = uri };
        if (existing != null) dicts[dicts.IndexOf(existing)] = nd; else dicts.Insert(0, nd);
        foreach (Window w in app.Windows) ApplyWindowEffects(w);
        Changed?.Invoke();
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void WatchSystem()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Settings.Current.Theme == ThemeMode.System)
                Application.Current.Dispatcher.BeginInvoke(Apply);
        };
    }

    public static SolidColorBrush BrushFromHex(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static Color Color(string key) => Application.Current.TryFindResource(key) is SolidColorBrush b ? b.Color : Colors.Gray;

    // ----- DWM -----
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyWindowEffects(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);            // DWMWA_USE_IMMERSIVE_DARK_MODE
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);           // DWMWA_WINDOW_CORNER_PREFERENCE = redondeado
        int border = unchecked((int)0xFFFFFFFE);                 // DWMWA_COLOR_NONE: sin borde de color
        DwmSetWindowAttribute(hwnd, 34, ref border, 4);
    }
}
