using System.Text.Json;
using System.Text.Json.Serialization;
using Faro.Model;

namespace Faro.Services;

public enum ViewMode { Icons = 1, List = 2, Columns = 3, Gallery = 4 }
public enum SortField { Name, Kind, Modified, Created, Size, Location }
public enum ThemeMode { System, Light, Dark }

/// <summary>Preferencias de Faro (%APPDATA%\Faro\ajustes.json).</summary>
public sealed class Settings
{
    public static Settings Current { get; private set; } = Load();

    public ViewMode DefaultView { get; set; } = ViewMode.Icons;
    public double IconSize { get; set; } = 64;
    public SortField SortBy { get; set; } = SortField.Name;
    public bool SortDescending { get; set; }
    public bool FoldersFirst { get; set; } = false;
    public bool ShowAllExtensions { get; set; } = true;
    public bool ShowHidden { get; set; }
    public bool ShowPathBar { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;
    public bool ShowSidebar { get; set; } = true;
    public bool ShowPreviewPane { get; set; }
    public bool WarnBeforeEmptyTrash { get; set; } = true;
    public bool WarnChangeExtension { get; set; } = true;
    public bool SearchThisFolderByDefault { get; set; }
    public string NewWindowTarget { get; set; } = "recents";
    public int Version { get; set; }
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public double SidebarWidth { get; set; } = 210;
    /// <summary>Volumen del reproductor de la vista rápida (0–1); se recuerda entre vídeos.</summary>
    public double MediaVolume { get; set; } = 0.8;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 680;
    public bool WindowMaximized { get; set; }
    public List<string> Favorites { get; set; }
    public List<string> HiddenSidebarItems { get; set; } = new();
    public Dictionary<string, ViewMode> FolderViews { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> ListColumnWidths { get; set; } = new();
    /// <summary>Al abrir, volver a las pestañas y carpetas de la última vez (como Finder al reabrirse).</summary>
    public bool RestoreSession { get; set; } = true;
    public List<SavedTab> LastSession { get; set; } = new();
    /// <summary>Últimas carpetas visitadas o escritas (las sugiere la barra de ruta al escribir).</summary>
    public List<string> RecentFolders { get; set; } = new();
    /// <summary>Servidores (\equipo) a los que se ha entrado: salen en la barra lateral y en «Red».</summary>
    public List<string> Servers { get; set; } = new();
    public int LastSessionActive { get; set; }

    /// <summary>
    /// WindowLeft/Top empiezan en NaN y System.Text.Json no lo admite por defecto: el guardado fallaba en
    /// silencio en una instalación nueva hasta cerrar la ventana la primera vez.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.AllowReadingFromString
    };

    [JsonIgnore] public static string Folder
    {
        get
        {
            var d = Faro.Core.AppInfo.RoamingDir;
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string FilePath => Path.Combine(Folder, "ajustes.json");

    public static event Action Changed;
    public static void NotifyChanged() { Changed?.Invoke(); Current.Save(); }

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json);
                if (s != null)
                {
                    s.FolderViews = new Dictionary<string, ViewMode>(s.FolderViews ?? new(), StringComparer.OrdinalIgnoreCase);
                    s.HiddenSidebarItems ??= new();
                    s.ListColumnWidths ??= new();
                    s.LastSession ??= new();
                    s.RecentFolders ??= new();
                    s.Servers ??= new();
                    if (s.Version < 2) { s.NewWindowTarget = "recents"; s.Version = 2; }
                    return s;
                }
            }
        }
        catch { }
        return new Settings { Version = 2 };
    }

    private readonly object _saveLock = new();
    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                if (FolderViews.Count > 800)
                    foreach (var k in FolderViews.Keys.Take(200).ToList()) FolderViews.Remove(k);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, FilePath, true);
            }
            catch { }
        }
    }
}

/// <summary>Una pestaña de la sesión anterior.</summary>
public sealed class SavedTab
{
    public LocationKind Kind { get; set; }
    public string Path { get; set; }
    public string Tag { get; set; }
    public ViewMode Mode { get; set; }
}
