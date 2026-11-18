using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Faro.Services;

/// <summary>
/// Al volver de la suspensión (o al desbloquear / cambiar de pantalla) la GPU puede perder la caché
/// de letras de WPF: los iconos (imágenes) siguen viéndose pero TODOS los textos desaparecen hasta
/// cerrar la app. Pasó el 2026-11-03 tras una suspensión por batería crítica.
/// Arreglo: regenerar la superficie de dibujo de cada ventana (un instante en software y de vuelta
/// a la GPU) y forzar que se vuelva a medir y pintar todo.
/// </summary>
public static class RenderRecovery
{
    private static DispatcherTimer _debounce;

    public static void Watch()
    {
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) Schedule("reanudar"); };
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
                Schedule("desbloquear");
        };
        SystemEvents.DisplaySettingsChanged += (_, _) => Schedule("pantalla");
    }

    /// <summary>Agrupa avisos seguidos (al reanudar llegan varios) y repara una sola vez.</summary>
    public static void Schedule(string why)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _debounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _debounce.Stop();
            _debounce.Tick -= OnTick;
            _debounce.Tick += OnTick;
            _debounce.Tag = why;
            _debounce.Start();
        });
    }

    private static void OnTick(object sender, EventArgs e)
    {
        _debounce.Stop();
        Repair(_debounce.Tag as string ?? "");
    }

    public static void Repair(string why)
    {
        var app = Application.Current;
        if (app == null) return;
        int n = 0;
        foreach (Window w in app.Windows)
        {
            try
            {
                if (PresentationSource.FromVisual(w) is not HwndSource src || src.CompositionTarget == null) continue;
                var target = src.CompositionTarget;
                target.RenderMode = RenderMode.SoftwareOnly;
                app.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        target.RenderMode = RenderMode.Default;
                        Refresh(w);
                    }
                    catch { }
                }, DispatcherPriority.Background);
                n++;
            }
            catch { }
        }
        try
        {
            var dir = Path.Combine(Core.AppInfo.LocalDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "pintado.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} repintar ({why}) {n} ventanas{Environment.NewLine}");
        }
        catch { }
    }

    private static void Refresh(DependencyObject d)
    {
        if (d is UIElement ui) { ui.InvalidateMeasure(); ui.InvalidateVisual(); }
        int c = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < c; i++) Refresh(VisualTreeHelper.GetChild(d, i));
    }
}
