using System.Text.Json;

namespace Faro.Core;

public sealed record TagDef(string Name, string Color);

/// <summary>
/// Etiquetas de color al estilo Finder. Se guardan en %APPDATA%\Faro\etiquetas.json
/// (ruta → etiquetas) y se actualizan cuando Faro mueve o renombra algo.
/// </summary>
public static class TagStore
{
    public static readonly TagDef[] Standard =
    {
        new("Rojo", "#FF3B30"), new("Naranja", "#FF9500"), new("Amarillo", "#FFCC00"), new("Verde", "#34C759"),
        new("Azul", "#007AFF"), new("Morado", "#AF52DE"), new("Gris", "#8E8E93"),
    };

    private static readonly object Lock = new();
    private static Dictionary<string, List<string>> _map;
    private static Timer _saveTimer;

    public static event Action Changed;

    private static string FilePath
    {
        get
        {
            var d = AppInfo.RoamingDir;
            Directory.CreateDirectory(d);
            return Path.Combine(d, "etiquetas.json");
        }
    }

    private static Dictionary<string, List<string>> Map
    {
        get
        {
            if (_map != null) return _map;
            lock (Lock)
            {
                if (_map != null) return _map;
                try
                {
                    var m = File.Exists(FilePath)
                        ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FilePath))
                        : null;
                    _map = new Dictionary<string, List<string>>(m ?? new(), StringComparer.OrdinalIgnoreCase);
                }
                catch { _map = new(StringComparer.OrdinalIgnoreCase); }
                return _map;
            }
        }
    }

    public static bool HasAny => Map.Count > 0;

    public static IReadOnlyList<string> Get(string path)
    {
        var m = Map;
        lock (Lock) return m.TryGetValue(path, out var l) ? l.ToArray() : Array.Empty<string>();
    }

    public static string ColorOf(string tag) => Standard.FirstOrDefault(t => t.Name == tag)?.Color ?? "#8E8E93";

    public static void Toggle(IEnumerable<string> paths, string tag)
    {
        var list = paths.ToList();
        var m = Map;
        lock (Lock)
        {
            bool allHave = list.All(p => m.TryGetValue(p, out var l) && l.Contains(tag));
            foreach (var p in list)
            {
                if (!m.TryGetValue(p, out var l)) m[p] = l = new List<string>();
                if (allHave) l.Remove(tag);
                else if (!l.Contains(tag)) l.Add(tag);
                if (l.Count == 0) m.Remove(p);
            }
        }
        ScheduleSave();
    }

    public static void Set(string path, IEnumerable<string> tags)
    {
        var m = Map;
        lock (Lock)
        {
            var l = tags.Distinct().ToList();
            if (l.Count == 0) m.Remove(path); else m[path] = l;
        }
        ScheduleSave();
    }

    /// <summary>Al mover/renombrar: arrastra las etiquetas del elemento y de todo lo que contiene.</summary>
    public static void Relocate(string from, string to, bool keepOriginal = false)
    {
        var m = Map;
        bool changed = false;
        lock (Lock)
        {
            var prefix = from.TrimEnd('\\') + "\\";
            foreach (var k in m.Keys.Where(k => k.Equals(from, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var nk = to + k[from.Length..];
                m[nk] = m[k].ToList();
                if (!keepOriginal) m.Remove(k);
                changed = true;
            }
        }
        if (changed) ScheduleSave();
    }

    public static void Forget(string path)
    {
        var m = Map; bool changed;
        lock (Lock) changed = m.Remove(path);
        if (changed) ScheduleSave();
    }

    /// <summary>Elementos con esa etiqueta que siguen existiendo.</summary>
    public static List<string> PathsWith(string tag)
    {
        var m = Map;
        List<string> res;
        lock (Lock) res = m.Where(kv => kv.Value.Contains(tag)).Select(kv => kv.Key).ToList();
        return res.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
    }

    private static void ScheduleSave()
    {
        Changed?.Invoke();
        lock (Lock)
        {
            _saveTimer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(400, Timeout.Infinite);
        }
    }

    public static void Save()
    {
        try
        {
            string json;
            lock (Lock) json = JsonSerializer.Serialize(Map);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }
}
