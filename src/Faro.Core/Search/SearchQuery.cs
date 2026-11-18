using System.Globalization;
using System.Text.RegularExpressions;

namespace Faro.Core.Search;

public sealed record SearchHit(string Path, bool IsDirectory, long Size, DateTime Modified);

/// <summary>
/// Consulta de búsqueda. Admite:
///   palabras sueltas (todas deben aparecer en el nombre, sin importar mayúsculas ni tildes),
///   "frase exacta", comodines (*.psd, foto??.jpg),
///   tipo:imagen | video | audio | pdf | adobe | documento | codigo | comprimido | fuente | app | 3d | carpeta,
///   ext:psd (o ext:psd,ai).
/// </summary>
public sealed class SearchQuery
{
    public List<string> Terms { get; } = new();
    public string KindGroup { get; private set; }
    public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Regex Wildcard { get; private set; }
    public string Raw { get; }

    private static readonly CompareInfo Ci = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Opt = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public SearchQuery(string text)
    {
        Raw = text ?? "";
        foreach (Match m in Regex.Matches(Raw, "\"([^\"]+)\"|(\\S+)"))
        {
            var tok = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!m.Groups[1].Success)
            {
                var idx = tok.IndexOf(':');
                if (idx > 1)
                {
                    var key = tok[..idx].ToLowerInvariant();
                    var val = tok[(idx + 1)..];
                    if (key is "tipo" or "kind" or "clase")
                    {
                        if (FileKinds.SearchAliases.TryGetValue(val, out var g)) { KindGroup = g; continue; }
                    }
                    if (key is "ext" or "extension" or "extensión")
                    {
                        foreach (var e in val.Split(',', ';')) if (e.Length > 0) Extensions.Add(e.TrimStart('.'));
                        continue;
                    }
                }
                if (tok.Contains('*') || tok.Contains('?'))
                {
                    var pattern = "^" + Regex.Escape(tok).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    Wildcard = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                    continue;
                }
            }
            Terms.Add(tok);
        }
    }

    public bool IsEmpty => Terms.Count == 0 && KindGroup == null && Extensions.Count == 0 && Wildcard == null;

    public bool Matches(string name, bool isDir)
    {
        foreach (var t in Terms)
            if (Ci.IndexOf(name, t, Opt) < 0) return false;
        if (Wildcard != null && !Wildcard.IsMatch(name)) return false;
        if (Extensions.Count > 0)
        {
            if (isDir) return false;
            var ext = System.IO.Path.GetExtension(name).TrimStart('.');
            if (!Extensions.Contains(ext)) return false;
        }
        if (KindGroup != null)
        {
            if (KindGroup == "carpeta") return isDir;
            if (isDir) return false;
            var k = FileKinds.ForPath(name, false);
            if (FileKinds.GroupOf(k.Category) != KindGroup) return false;
        }
        return true;
    }
}

/// <summary>
/// Búsqueda propia en una carpeta: recorre el árbol con varios hilos a la vez y va
/// entregando resultados por lotes mientras busca (no espera a terminar).
/// </summary>
public static class FolderScanner
{
    public static Task ScanAsync(string root, SearchQuery q, Action<List<SearchHit>> onBatch, CancellationToken ct,
        bool includeHidden = false, int maxResults = 20000)
    {
        return Task.Run(() =>
        {
            var dirs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            dirs.Enqueue(root);
            int pending = 1;
            int found = 0;
            var results = new System.Collections.Concurrent.ConcurrentQueue<SearchHit>();
            var skip = includeHidden ? FileAttributes.None : (FileAttributes.Hidden | FileAttributes.System);
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = skip, ReturnSpecialDirectories = false,
                BufferSize = 64 * 1024
            };

            using var flusher = new Timer(_ =>
            {
                if (results.IsEmpty) return;
                var batch = new List<SearchHit>();
                while (results.TryDequeue(out var h)) batch.Add(h);
                if (batch.Count > 0 && !ct.IsCancellationRequested) onBatch(batch);
            }, null, 120, 150);

            int workers = Math.Clamp(Environment.ProcessorCount, 2, 12);
            var tasks = new Task[workers];
            for (int w = 0; w < workers; w++)
            {
                tasks[w] = Task.Factory.StartNew(() =>
                {
                    var spin = new SpinWait();
                    while (!ct.IsCancellationRequested && Volatile.Read(ref found) < maxResults)
                    {
                        if (!dirs.TryDequeue(out var dir))
                        {
                            if (Volatile.Read(ref pending) == 0) break;
                            spin.SpinOnce();
                            continue;
                        }
                        try
                        {
                            var en = new System.IO.Enumeration.FileSystemEnumerable<(string name, bool dir, bool reparse, long len, DateTime m)>(dir,
                                (ref System.IO.Enumeration.FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory,
                                    (e.Attributes & FileAttributes.ReparsePoint) != 0, e.Length, e.LastWriteTimeUtc.LocalDateTime), opts);
                            foreach (var e in en)
                            {
                                if (ct.IsCancellationRequested) break;
                                var full = System.IO.Path.Combine(dir, e.name);
                                if (e.dir && !e.reparse)
                                {
                                    Interlocked.Increment(ref pending);
                                    dirs.Enqueue(full);
                                }
                                if (q.Matches(e.name, e.dir))
                                {
                                    if (Interlocked.Increment(ref found) > maxResults) break;
                                    results.Enqueue(new SearchHit(full, e.dir, e.dir ? -1 : e.len, e.m));
                                }
                            }
                        }
                        catch { }
                        finally { Interlocked.Decrement(ref pending); }
                    }
                }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            try { Task.WaitAll(tasks); } catch { }
            flusher.Change(Timeout.Infinite, Timeout.Infinite);
            var last = new List<SearchHit>();
            while (results.TryDequeue(out var h)) last.Add(h);
            if (last.Count > 0 && !ct.IsCancellationRequested) onBatch(last);
        }, ct);
    }
}
