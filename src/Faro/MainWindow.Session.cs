using System.Windows;
using Faro.Model;
using Faro.Services;

namespace Faro;

/// <summary>
/// Sesión: al cerrar la última ventana (o al apagar Windows con la app abierta) se guardan sus
/// pestañas; al volver a abrir la app sin ruta, se reabren donde estaban.
/// </summary>
public partial class MainWindow
{
    /// <summary>Guarda las pestañas de esta ventana como «la última sesión».</summary>
    internal void SaveSession()
    {
        var s = Settings.Current;
        var list = new List<SavedTab>();
        int active = 0;
        foreach (var t in Tabs)
        {
            // una búsqueda se guarda como la carpeta desde la que se buscaba
            var l = t.IsSearching ? t.SearchOrigin : t.Location;
            if (l == null || l.Kind == LocationKind.Search) continue;
            if (t == _tab) active = list.Count;
            list.Add(new SavedTab { Kind = l.Kind, Path = l.Path, Tag = l.Tag, Mode = t.Mode });
        }
        s.LastSession = list;
        s.LastSessionActive = active;
    }

    private System.Windows.Threading.DispatcherTimer _sessionTimer;

    /// <summary>
    /// Autoguardado 3 s después de cada cambio de carpeta o vista: si la batería se agota o la app se
    /// cierra de golpe, al volver se recupera lo último (sólo la primera ventana, que es la que se reabre).
    /// </summary>
    private void ScheduleSessionSave()
    {
        if (Application.Current.Windows.OfType<MainWindow>().FirstOrDefault() != this) return;
        if (_sessionTimer == null)
        {
            _sessionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _sessionTimer.Tick += (_, _) => { _sessionTimer.Stop(); if (IsLoaded) SaveAll(); };
        }
        _sessionTimer.Stop(); _sessionTimer.Start();
    }

    /// <summary>Guarda ventana y sesión de golpe (al cerrar o al apagar el equipo).</summary>
    internal void SaveAll()
    {
        SaveSession();
        SaveWindowState();
    }

    /// <summary>
    /// Pasa una pestaña guardada a una ubicación que exista HOY: el disco viaja entre equipos y
    /// los discos externos van y vienen, así que una carpeta puede faltar. Se sube a la primera
    /// carpeta contenedora que exista; si ni el disco está, la pestaña se descarta.
    /// </summary>
    private static Location Resolve(SavedTab t)
    {
        switch (t.Kind)
        {
            case LocationKind.Recents: return Location.Recents;
            case LocationKind.Apps: return Location.Apps;
            case LocationKind.Computer: return Location.Computer;
            case LocationKind.Trash: return Location.Trash;
            case LocationKind.Network: return Location.Network;
            case LocationKind.Tag: return string.IsNullOrEmpty(t.Tag) ? null : Location.ForTag(t.Tag);
            case LocationKind.Folder:
                var p = t.Path;
                // de red: no se comprueba (esperaría al servidor antes de pintar); si no responde, la pestaña lo dice
                if (NetworkService.IsUnc(p)) return Location.Folder(p);
                try
                {
                    // un disco que no está: no se toca (Directory.Exists en una unidad de red caída puede tardar)
                    var root = Path.GetPathRoot(p);
                    if (string.IsNullOrEmpty(root) || (!root.StartsWith(@"\\") && !new DriveInfo(root).IsReady)) return null;
                    while (!string.IsNullOrEmpty(p) && !Directory.Exists(p)) p = Path.GetDirectoryName(p);
                }
                catch { return null; }
                return string.IsNullOrEmpty(p) ? null : Location.Folder(p);
            default: return null;
        }
    }

    /// <summary>Abre las pestañas de la última sesión. Devuelve false si no había nada que abrir.</summary>
    private bool RestoreSession()
    {
        var s = Settings.Current;
        if (!s.RestoreSession || s.LastSession is not { Count: > 0 }) return false;
        var tabs = s.LastSession.Select(t => (loc: Resolve(t), mode: t.Mode)).ToList();
        int active = Math.Clamp(s.LastSessionActive, 0, tabs.Count - 1);
        // si justo la activa ya no existe: la de al lado (antes la anterior, como al cerrar una pestaña)
        if (tabs[active].loc == null)
        {
            int prev = tabs.FindLastIndex(active, t => t.loc != null);
            active = prev >= 0 ? prev : tabs.FindIndex(t => t.loc != null);
        }
        if (active < 0) return false;
        for (int i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].loc == null) continue;
            _ = AddTab(tabs[i].loc, i == active, tabs[i].mode);
        }
        return true;
    }
}
