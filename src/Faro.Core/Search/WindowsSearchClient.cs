namespace Faro.Core.Search;

/// <summary>
/// Consulta el índice de Windows Search (SystemIndex) por ADODB. Busca por nombre y,
/// como Finder, también por contenido en los archivos indexados.
/// </summary>
public static class WindowsSearchClient
{
    /// <summary>¿Existe y está activo el servicio de indexación? (si no, ADODB tarda 30 s en fallar)</summary>
    public static bool ServiceAvailable()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WSearch");
            if (k == null || k.GetValue("Start") is int s && s == 4) return false;
            return System.Diagnostics.Process.GetProcessesByName("SearchIndexer").Length > 0;
        }
        catch { return false; }
    }

    public static List<SearchHit> Query(SearchQuery q, string scopeFolder, int max, CancellationToken ct)
    {
        if (!ServiceAvailable()) return null;
        var hits = new List<SearchHit>();
        var connType = Type.GetTypeFromProgID("ADODB.Connection");
        if (connType == null) return null;
        dynamic conn = null, rs = null;
        try
        {
            conn = Activator.CreateInstance(connType);
            conn.ConnectionTimeout = 4;
            conn.CommandTimeout = 8;
            conn.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            var where = new List<string>();
            if (!string.IsNullOrEmpty(scopeFolder))
                where.Add($"SCOPE='file:{Esc(scopeFolder.Replace('\\', '/'))}'");
            foreach (var t in q.Terms)
            {
                var e = Esc(t);
                where.Add($"(System.FileName LIKE '%{e}%' OR CONTAINS(*, '\"{e.Replace("\"", "")}*\"'))");
            }
            if (q.Extensions.Count > 0)
                where.Add("(" + string.Join(" OR ", q.Extensions.Select(x => $"System.FileExtension = '.{Esc(x)}'")) + ")");
            if (q.KindGroup == "carpeta") where.Add("System.ItemType = 'Directory'");
            else if (q.KindGroup != null)
            {
                var exts = FileKinds.ExtensionsInGroup(q.KindGroup).Take(120);
                where.Add("(" + string.Join(" OR ", exts.Select(x => $"System.FileExtension = '.{Esc(x)}'")) + ")");
            }
            if (q.Wildcard != null)
            {
                var raw = System.Text.RegularExpressions.Regex.Match(q.Raw, @"\S*[\*\?]\S*").Value.Replace('*', '%').Replace('?', '_');
                where.Add($"System.FileName LIKE '{Esc(raw)}'");
            }
            if (where.Count == 0) return hits;
            var sql = $"SELECT TOP {max} System.ItemPathDisplay, System.ItemType, System.Size, System.DateModified FROM SystemIndex WHERE {string.Join(" AND ", where)}";
            rs = conn.Execute(sql);
            while (!(bool)rs.EOF && !ct.IsCancellationRequested)
            {
                string path = rs.Fields.Item(0).Value as string;
                string type = rs.Fields.Item(1).Value as string;
                object size = rs.Fields.Item(2).Value;
                object mod = rs.Fields.Item(3).Value;
                if (!string.IsNullOrEmpty(path))
                {
                    bool isDir = string.Equals(type, "Directory", StringComparison.OrdinalIgnoreCase);
                    long len = size is IConvertible c ? Convert.ToInt64(c) : -1;
                    var dt = mod is DateTime d ? d.ToLocalTime() : DateTime.MinValue;
                    hits.Add(new SearchHit(path, isDir, isDir ? -1 : len, dt));
                }
                rs.MoveNext();
            }
            return hits;
        }
        catch
        {
            return null; // el servicio de búsqueda está apagado o no hay índice
        }
        finally
        {
            try { rs?.Close(); } catch { }
            try { conn?.Close(); } catch { }
        }
    }

    private static string Esc(string s) => s.Replace("'", "''");
}
