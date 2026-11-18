using System.Diagnostics;
using System.Security.Cryptography;
using Faro.Core;
using Faro.Core.Search;
using Faro.Core.Transfer;

// Banco de pruebas del núcleo de Faro (motor de copia, búsqueda, tipos).
// Uso: Faro.Tests <comando> ...
var cmd = args.Length > 0 ? args[0] : "help";
switch (cmd)
{
    case "gen-small": GenSmall(args[1], int.Parse(args[2])); break;
    case "gen-large": GenLarge(args[1], int.Parse(args[2]), int.Parse(args[3])); break;
    case "copy": await RunJob(TransferKind.Copy, args[1], args[2]); break;
    case "move": await RunJob(TransferKind.Move, args[1], args[2]); break;
    case "dup": await RunJob(TransferKind.Duplicate, args[1], Path.GetDirectoryName(args[1])); break;
    case "verify": return Verify(args[1], args[2], args.Length > 3 && args[3] == "hash") ? 0 : 1;
    case "resume": await ResumeAll(); break;
    case "sniff": foreach (var f in args.Skip(1)) Console.WriteLine($"{f}: {FileKinds.SniffExtension(f) ?? "?"} / {FileKinds.ForPath(f, false).Name}"); break;
    case "scan":
    {
        var sw = Stopwatch.StartNew(); int n = 0;
        await FolderScanner.ScanAsync(args[1], new SearchQuery(string.Join(' ', args.Skip(2))), b => Interlocked.Add(ref n, b.Count), CancellationToken.None);
        Console.WriteLine($"{n} resultados en {sw.ElapsedMilliseconds} ms");
        break;
    }
    case "everything":
    {
        EverythingClient.Debug = m => Console.WriteLine("  [dbg] " + m);
        var q = new SearchQuery(string.Join(' ', args.Skip(1)));
        var s = EverythingClient.Translate(q, null);
        var sw = Stopwatch.StartNew();
        var r = await EverythingClient.QueryAsync(s, 100, CancellationToken.None);
        Console.WriteLine($"[{s}] → {(r == null ? "no disponible" : r.Count + " resultados")} en {sw.ElapsedMilliseconds} ms");
        if (r != null) foreach (var x in r.Take(5)) Console.WriteLine("  " + x.path + (x.dir ? "\\" : ""));
        break;
    }
    case "winsearch":
    {
        var sw = Stopwatch.StartNew();
        var r = WindowsSearchClient.Query(new SearchQuery(string.Join(' ', args.Skip(1))), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 50, CancellationToken.None);
        Console.WriteLine($"{(r == null ? "no disponible" : r.Count + " resultados")} en {sw.ElapsedMilliseconds} ms");
        if (r != null) foreach (var x in r.Take(5)) Console.WriteLine("  " + x.Path);
        break;
    }
    case "names":
    {
        var d = Directory.CreateTempSubdirectory("faro-names").FullName;
        File.WriteAllText(Path.Combine(d, "foto.jpg"), "x");
        Console.WriteLine(Path.GetFileName(TransferJob.UniqueName(d, "foto.jpg", false, true)));
        File.WriteAllText(Path.Combine(d, "foto copia.jpg"), "x");
        Console.WriteLine(Path.GetFileName(TransferJob.UniqueName(d, "foto.jpg", false, true)));
        Console.WriteLine(Path.GetFileName(TransferJob.UniqueName(d, "foto.jpg", false, false)));
        Console.WriteLine(Format.Size(30_000_000) + " | " + Format.Size(1234) + " | " + Format.Size(30L * 1000 * 1000 * 1000) + " | " + Format.Date(DateTime.Now.AddDays(-1)));
        Directory.Delete(d, true);
        break;
    }
    default:
        Console.WriteLine("gen-small DIR N | gen-large DIR N MB | copy SRC DST | move SRC DST | dup SRC | verify A B [hash] | resume | sniff F.. | scan DIR q | everything q | winsearch q | names");
        break;
}
return 0;

static void GenSmall(string dir, int count)
{
    var sw = Stopwatch.StartNew();
    var rnd = new Random(42);
    int perDir = 1000;
    Parallel.For(0, (count + perDir - 1) / perDir, d =>
    {
        var sub = Path.Combine(dir, $"carpeta {d:000}", d % 3 == 0 ? "anidada" : "");
        Directory.CreateDirectory(sub);
        var buf = new byte[300];
        for (int i = 0; i < perDir && d * perDir + i < count; i++)
        {
            new Random(d * perDir + i).NextBytes(buf);
            File.WriteAllBytes(Path.Combine(sub, $"archivo {i}.dat"), buf);
        }
    });
    Console.WriteLine($"{count} archivos generados en {sw.Elapsed.TotalSeconds:0.0}s");
}

static void GenLarge(string dir, int count, int mb)
{
    Directory.CreateDirectory(dir);
    var buf = new byte[4 * 1024 * 1024];
    for (int i = 0; i < count; i++)
    {
        using var fs = new FileStream(Path.Combine(dir, $"video {i}.mov"), FileMode.Create, FileAccess.Write, FileShare.None, 1);
        var rnd = new Random(i);
        for (long w = 0; w < (long)mb * 1024 * 1024; w += buf.Length) { rnd.NextBytes(buf); fs.Write(buf); }
    }
    Console.WriteLine($"{count} archivos de {mb} MB");
}

static async Task RunJob(TransferKind kind, string src, string dst)
{
    var sources = src.Split('|');
    var job = new TransferJob(kind, sources, dst, new AutoPrompt());
    Console.WriteLine($"Trabajo {job.Id}");
    var t = job.RunAsync();
    var last = TransferState.Preparing;
    while (!t.IsCompleted)
    {
        await Task.WhenAny(t, Task.Delay(1000));
        job.Sample();
        if (job.State != last) { Console.WriteLine($"  estado → {job.State} {job.Message}"); last = job.State; }
        Console.WriteLine($"  {job.State,-16} {Format.Count(job.DoneFiles)}/{Format.Count(job.TotalFiles)} arch  {Format.Size(job.DoneBytes)}/{Format.Size(job.TotalBytes)}  {Format.Speed(job.BytesPerSecond)}  hilos={job.Parallelism}  {job.Message}");
    }
    Console.WriteLine($"FIN: {job.State} en {job.Elapsed.Elapsed.TotalSeconds:0.00}s  {job.Message}");
    foreach (var i in job.Issues.Take(10)) Console.WriteLine($"  ! {i.Path}: {i.Message}");
}

static async Task ResumeAll()
{
    var pending = TransferJournal.LoadPending();
    Console.WriteLine($"{pending.Count} pendiente(s)");
    foreach (var j in pending)
    {
        Console.WriteLine($"Reanudando {j.Kind} {string.Join(",", j.Sources)} → {j.Destination} (parcial: {j.PartialFile} @ {j.PartialFlushedOffset})");
        var job = TransferJob.Resume(j, new AutoPrompt());
        await job.RunAsync();
        Console.WriteLine($"FIN: {job.State} en {job.Elapsed.Elapsed.TotalSeconds:0.00}s {job.Message}");
    }
}

static bool Verify(string a, string b, bool hash)
{
    var sw = Stopwatch.StartNew();
    var fa = Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(a, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var fb = Directory.EnumerateFiles(b, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(b, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    bool ok = fa.SetEquals(fb);
    Console.WriteLine($"archivos: {fa.Count} vs {fb.Count} {(ok ? "OK" : "DIFIEREN")}");
    int bad = 0;
    Parallel.ForEach(fa.Intersect(fb), r =>
    {
        var x = new FileInfo(Path.Combine(a, r)); var y = new FileInfo(Path.Combine(b, r));
        bool same = x.Length == y.Length && Math.Abs((x.LastWriteTimeUtc - y.LastWriteTimeUtc).TotalSeconds) < 2;
        if (same && (hash || x.Length < 4096))
        {
            using var s1 = File.OpenRead(x.FullName); using var s2 = File.OpenRead(y.FullName);
            same = SHA256.HashData(s1).AsSpan().SequenceEqual(SHA256.HashData(s2));
        }
        if (!same) { Interlocked.Increment(ref bad); Console.WriteLine("  distinto: " + r); }
    });
    Console.WriteLine($"contenido: {(bad == 0 ? "OK" : bad + " distintos")} ({sw.Elapsed.TotalSeconds:0.0}s)");
    return ok && bad == 0;
}
