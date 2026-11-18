using Faro.Model;

namespace Faro.Services;

/// <summary>Una sugerencia de la barra de ruta: carpeta del historial, subcarpeta o recurso de un servidor.</summary>
public sealed record PathSuggestion(string Path, bool FromHistory, bool IsServer = false);

/// <summary>
/// Últimas carpetas visitadas (como las rutas escritas del Explorador, pero de todas las visitas)
/// y las sugerencias al escribir una ruta: historial + subcarpetas + recursos de \\servidor.
/// </summary>
public static class PathHistory
{
    private const int Max = 60;
    private static readonly Dictionary<string, (DateTime when, List<string> shares)> ShareCache = new(StringComparer.OrdinalIgnoreCase);

    public static void Add(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var list = Settings.Current.RecentFolders;
        int i = list.FindIndex(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        if (i == 0) return;
        if (i > 0) list.RemoveAt(i);
        list.Insert(0, path);
        if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
        Settings.Current.Save();
    }

    public static void Remove(string path)
    {
        Settings.Current.RecentFolders.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        Settings.Current.Save();
    }

    /// <summary>~, %VARIABLES%, barras «/», smb://… y una IP suelta → ruta de Windows.</summary>
    public static string Expand(string t)
    {
        t = (t ?? "").Trim().Trim('"');
        if (t.StartsWith('~')) t = KnownFolders.Home + t[1..];
        t = Environment.ExpandEnvironmentVariables(t);
        // la barra final importa: «\\servidor\» pide sus carpetas, «\\servi» completa el nombre
        var net = NetworkService.NormalizeInput(t);
        if (net != null && (t.EndsWith('\\') || t.EndsWith('/')) && !net.EndsWith('\\')) net += "\\";
        return net ?? t.Replace('/', '\\');
    }

    /// <summary>
    /// Sugerencias para lo escrito. <paramref name="historyOnly"/>: recién abierta la edición (el texto es la
    /// carpeta actual) se ofrece sólo el historial, como el desplegable del Explorador.
    /// </summary>
    public static async Task<List<PathSuggestion>> SuggestAsync(string text, bool historyOnly, CancellationToken ct)
    {
        var res = new List<PathSuggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(PathSuggestion s) { if (seen.Add(s.Path.TrimEnd('\\'))) res.Add(s); }
        var history = Settings.Current.RecentFolders.ToList();
        var raw = (text ?? "").Trim();
        if (historyOnly || raw.Length == 0)
        {
            foreach (var h in history.Take(15)) Add(new PathSuggestion(h, true));
            return res;
        }
        var t = Expand(raw);
        foreach (var h in history.Where(h => h.Contains(raw, StringComparison.OrdinalIgnoreCase) || h.StartsWith(t, StringComparison.OrdinalIgnoreCase)).Take(6))
            if (!string.Equals(h, t.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) Add(new PathSuggestion(h, true));

        // \\192.168.1… → servidores conocidos que empiezan así
        if (NetworkService.IsUnc(t) && t.TrimEnd('\\').IndexOf('\\', 2) < 0 &&!t.EndsWith('\\'))
        {
            foreach (var srv in Settings.Current.Servers.Concat(NetworkService.Found.Select(c => c.Path)))
                if (srv.StartsWith(t, StringComparison.OrdinalIgnoreCase)) Add(new PathSuggestion(srv, false, true));
            return res;
        }

        var dir = t.EndsWith('\\') ? t.TrimEnd('\\') : SafeParent(t);
        var prefix = t.EndsWith('\\') ? "" : System.IO.Path.GetFileName(t);
        if (string.IsNullOrEmpty(dir)) return res;
        if (dir.Length == 2 && dir[1] == ':') dir += "\\";
        List<string> children;
        try
        {
            var work = Task.Run(() => NetworkService.IsServerRoot(dir) ? Shares(dir) : Subfolders(dir), ct);
            // en red una carpeta puede tardar: no se espera más de 4 s por sugerencias
            if (await Task.WhenAny(work, Task.Delay(NetworkService.IsUnc(dir) ? 4000 : 1500, ct)) != work) return res;
            children = work.Result;
        }
        catch { return res; }
        foreach (var c in children.Where(c => System.IO.Path.GetFileName(c.TrimEnd('\\')).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(40))
            Add(new PathSuggestion(c, false));
        return res;
    }

    private static string SafeParent(string t)
    {
        if (NetworkService.IsShareRoot(t)) return NetworkService.ServerOf(t);
        try { return System.IO.Path.GetDirectoryName(t); } catch { return null; }
    }

    private static List<string> Subfolders(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return new();
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | (Settings.Current.ShowHidden ? 0 : FileAttributes.Hidden) };
            return Directory.EnumerateDirectories(dir, "*", opts).Take(400).OrderBy(d => d, NaturalComparerUi.Instance).ToList();
        }
        catch { return new(); }
    }

    private static List<string> Shares(string server)
    {
        lock (ShareCache)
            if (ShareCache.TryGetValue(server, out var c) && DateTime.UtcNow - c.when < TimeSpan.FromMinutes(2)) return c.shares;
        var list = NetworkService.ListShares(server).Select(n => server + "\\" + n).ToList();
        lock (ShareCache) ShareCache[server] = (DateTime.UtcNow, list);
        return list;
    }

    private sealed class NaturalComparerUi : IComparer<string>
    {
        public static readonly NaturalComparerUi Instance = new();
        public int Compare(string a, string b) => Faro.Core.Native.StrCmpLogicalW(System.IO.Path.GetFileName(a) ?? "", System.IO.Path.GetFileName(b) ?? "");
    }
}
