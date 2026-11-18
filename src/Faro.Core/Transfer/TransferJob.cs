using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Faro.Core.Transfer;

/// <summary>
/// Una operación de copiar / mover / duplicar.
///
/// Estrategia:
///  • Mover dentro del mismo volumen = renombrar (instantáneo, da igual 100.000 archivos).
///  • Entre volúmenes: se arma el plan completo, se crean las carpetas y luego:
///      – archivos pequeños (&lt; 16 MB) con varios hilos en paralelo (SSD: 8, disco mecánico: 2);
///      – archivos grandes uno a uno, por bloques de 8 MB con lectura y escritura solapadas,
///        confirmando a disco (FlushFileBuffers) cada 128 MB.
///  • Si un disco se desconecta: todos los hilos se detienen, el estado pasa a
///    «Esperando a que se conecte…», y al volver (aunque cambie de letra: se reconoce
///    por el número de serie del volumen) se reanuda desde el último bloque confirmado.
///  • Al mover entre volúmenes los originales se borran SOLO al final, después de
///    confirmar a disco cada copia. Nunca se borra un original sin copia completa.
///  • Diario en disco para reanudar tras un cierre inesperado.
/// </summary>
public sealed class TransferJob : INotifyPropertyChanged
{
    public const long LargeFileThreshold = 16L * 1024 * 1024;
    private const int ChunkSize = 8 * 1024 * 1024;
    private const long FlushEvery = 128L * 1024 * 1024;

    private readonly ITransferPrompt _prompt;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _pauseGate = new(true);
    private readonly ManualResetEventSlim _deviceGate = new(true);
    private readonly object _deviceLock = new();
    private bool _deviceWaiting;
    private readonly SemaphoreSlim _promptLock = new(1, 1);
    private readonly TransferJournal _journal;
    private readonly bool _isResume;

    private ConflictChoice? _fileConflictDefault;
    private bool _skipAllErrors;
    private DateTime _lastJournalSave = DateTime.MinValue;

    // Raíces (para detectar desconexión y remapear si cambia la letra)
    private VolumeRef _src, _dst;

    public Guid Id => _journal.Id;
    public TransferKind Kind { get; }
    public IReadOnlyList<string> Sources { get; }
    public string Destination { get; }
    public string DestinationName { get; }

    // ---- progreso (se lee desde la UI con un temporizador) ----
    private long _totalBytes, _doneBytes, _totalFiles, _doneFiles, _preparedItems;
    public long TotalBytes => Interlocked.Read(ref _totalBytes);
    public long DoneBytes => Interlocked.Read(ref _doneBytes);
    public long TotalFiles => Interlocked.Read(ref _totalFiles);
    public long DoneFiles => Interlocked.Read(ref _doneFiles);
    public long PreparedItems => Interlocked.Read(ref _preparedItems);
    public volatile string CurrentItem;
    public int Parallelism { get; private set; } = 1;

    private TransferState _state = TransferState.Preparing;
    private TransferState _stateBeforePause;
    public TransferState State
    {
        get => _state;
        private set { if (_state != value) { _state = value; OnChanged(nameof(State)); StateChanged?.Invoke(this); } }
    }

    private string _message = "";
    public string Message { get => _message; private set { _message = value; OnChanged(nameof(Message)); } }

    public ConcurrentQueue<TransferIssue> Issues { get; } = new();
    public List<string> CreatedTopLevel { get; } = new();
    public Stopwatch Elapsed { get; } = new();

    public event PropertyChangedEventHandler PropertyChanged;
    public event Action<TransferJob> StateChanged;
    /// <summary>(origen, destino) de cada elemento de nivel superior cuando termina; sirve para etiquetas y deshacer.</summary>
    public event Action<string, string> ItemPlaced;

    public bool IsFinished => State is TransferState.Completed or TransferState.Canceled or TransferState.Failed;
    public bool IsPaused => State == TransferState.Paused;

    public TransferJob(TransferKind kind, IEnumerable<string> sources, string destination, ITransferPrompt prompt)
        : this(new TransferJournal { Kind = kind, Sources = sources.ToList(), Destination = destination }, prompt, false) { }

    private TransferJob(TransferJournal journal, ITransferPrompt prompt, bool resume)
    {
        _journal = journal;
        _prompt = prompt;
        _isResume = resume;
        Kind = journal.Kind;
        Sources = journal.Sources;
        Destination = journal.Destination;
        DestinationName = Path.GetFileName(Destination.TrimEnd('\\')) is { Length: > 0 } n ? n : Destination;
        if (resume && journal.DefaultConflict is { } dc) _fileConflictDefault = dc;
    }

    public static TransferJob Resume(TransferJournal journal, ITransferPrompt prompt) => new(journal, prompt, true);

    public string Verb => Kind switch
    {
        TransferKind.Move => "Moviendo",
        TransferKind.Duplicate => "Duplicando",
        _ => "Copiando"
    };

    // ======================= control =======================

    public void Pause()
    {
        if (IsFinished || State == TransferState.Paused) return;
        _stateBeforePause = State;
        _pauseGate.Reset();
        State = TransferState.Paused;
    }

    public void ResumeRun()
    {
        if (State != TransferState.Paused) return;
        State = _stateBeforePause == TransferState.Paused ? TransferState.Running : _stateBeforePause;
        _pauseGate.Set();
    }

    public void Cancel()
    {
        _cts.Cancel();
        _pauseGate.Set();
    }

    private CancellationToken Ct => _cts.Token;

    private void Checkpoint()
    {
        Ct.ThrowIfCancellationRequested();
        if (!_pauseGate.IsSet) _pauseGate.Wait(Ct);
        if (!_deviceGate.IsSet) _deviceGate.Wait(Ct);
        Ct.ThrowIfCancellationRequested();
    }

    public Task RunAsync() => Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // ======================= ejecución =======================

    private sealed class Top
    {
        public string Src, Dst; public bool IsDir; public bool Replace; public bool Merge;
    }

    private sealed class Entry
    {
        public string Src, Dst; public long Size; public bool IsDir; public FileAttributes Attr;
        public DateTime CreatedUtc, ModifiedUtc; public bool InMerge; public bool Copied;
    }

    private void Run()
    {
        Elapsed.Start();
        try
        {
            _journal.Save();
            State = TransferState.Preparing;
            Message = "Preparando…";

            _src = VolumeRef.For(Sources[0]);
            _dst = VolumeRef.For(Destination);

            if (!Directory.Exists(Destination))
            {
                if (!WaitUntil(() => Directory.Exists(_dst.Map(Destination)), "el destino"))
                    return;
            }

            var tops = ResolveTopLevel();
            if (tops == null) return; // detenido
            if (tops.Count == 0) { Finish(); return; }

            bool sameVolume = _src.SameVolumeAs(_dst) && Sources.All(s => VolumeRef.For(s).SameVolumeAs(_dst));
            if (Kind == TransferKind.Move && sameVolume)
                MoveSameVolume(tops);
            else
                CopyAcross(tops, deleteSources: Kind == TransferKind.Move);

            Finish();
        }
        catch (OperationCanceledException)
        {
            State = TransferState.Canceled;
            Message = "Cancelado";
            _journal.Delete();
        }
        catch (StopException)
        {
            State = TransferState.Canceled;
            Message = "Detenido";
            _journal.Delete();
        }
        catch (Exception ex)
        {
            Issues.Enqueue(new TransferIssue(Destination, ex.Message));
            Message = ex.Message;
            State = TransferState.Failed;
            Log(ex.ToString());
        }
        finally
        {
            Elapsed.Stop();
        }
    }

    private void Finish()
    {
        _journal.Delete();
        Message = Issues.IsEmpty ? "Listo" : $"Terminado con {Issues.Count} problema(s)";
        State = TransferState.Completed;
    }

    private sealed class StopException : Exception { }
    private sealed class SkipException : Exception { }

    // ---------- nivel superior: nombres, conflictos, "dentro de sí mismo" ----------

    private List<Top> ResolveTopLevel()
    {
        var tops = new List<Top>();
        foreach (var raw in Sources)
        {
            Checkpoint();
            var s = raw.Length > 3 ? raw.TrimEnd('\\') : raw;
            bool isDir = Directory.Exists(s);
            if (!isDir && !File.Exists(s))
            {
                if (!_isResume) Issues.Enqueue(new TransferIssue(s, "No se encontró el elemento original."));
                continue;
            }
            var name = Path.GetFileName(s);
            if (string.IsNullOrEmpty(name)) // raíz de unidad
            {
                Native.TryGetVolumeInfo(s, out _, out var label, out _);
                name = string.IsNullOrWhiteSpace(label) ? "Unidad " + s.TrimEnd('\\', ':') : label;
            }
            var parent = Path.GetDirectoryName(s);
            bool sameFolder = parent != null && PathEq(parent, Destination);

            if (isDir && IsInside(Destination, s))
            {
                Issues.Enqueue(new TransferIssue(s, $"No se puede {(Kind == TransferKind.Move ? "mover" : "copiar")} «{name}» dentro de sí mismo."));
                continue;
            }
            if (Kind == TransferKind.Move && sameFolder) continue; // ya está ahí

            var target = Path.Combine(Destination, name);
            var top = new Top { Src = s, Dst = target, IsDir = isDir };

            if (Kind == TransferKind.Duplicate || (Kind == TransferKind.Copy && sameFolder))
            {
                top.Dst = UniqueName(Destination, name, isDir, copyStyle: true);
            }
            else if (Exists(target))
            {
                bool destIsDir = Directory.Exists(target);
                if (_isResume)
                {
                    top.Merge = isDir && destIsDir;
                    top.Replace = !top.Merge && !isDir;
                    if (!isDir && destIsDir) { top.Dst = UniqueName(Destination, name, false, false); top.Replace = false; }
                }
                else
                {
                    var ans = Ask(new ConflictRequest
                    {
                        SourcePath = s, DestinationPath = target, SourceIsDirectory = isDir, DestinationIsDirectory = destIsDir,
                        SourceSize = isDir ? -1 : new FileInfo(s).Length, DestinationSize = destIsDir ? -1 : new FileInfo(target).Length,
                        SourceModified = isDir ? Directory.GetLastWriteTime(s) : File.GetLastWriteTime(s),
                        DestinationModified = destIsDir ? Directory.GetLastWriteTime(target) : File.GetLastWriteTime(target),
                        IsTopLevel = true
                    }, topLevel: true);
                    switch (ans)
                    {
                        case ConflictChoice.Stop: throw new StopException();
                        case ConflictChoice.Skip: continue;
                        case ConflictChoice.KeepBoth: top.Dst = UniqueName(Destination, name, isDir, copyStyle: false); break;
                        case ConflictChoice.Replace: top.Replace = true; break;
                        case ConflictChoice.Merge: top.Merge = isDir && destIsDir; top.Replace = !top.Merge; break;
                    }
                }
            }
            tops.Add(top);
        }
        return tops;
    }

    private ConflictChoice? _topConflictDefault;

    private ConflictChoice Ask(ConflictRequest req, bool topLevel)
    {
        ref ConflictChoice? def = ref topLevel ? ref _topConflictDefault : ref _fileConflictDefault;
        if (def is { } d && (d != ConflictChoice.Merge || req.CanMerge)) return d;
        _promptLock.Wait(Ct);
        try
        {
            if (def is { } d2 && (d2 != ConflictChoice.Merge || req.CanMerge)) return d2;
            var prev = State;
            State = TransferState.WaitingForUser;
            var ans = _prompt.ResolveConflictAsync(this, req).GetAwaiter().GetResult();
            State = prev == TransferState.WaitingForUser ? TransferState.Running : prev;
            if (ans.ApplyToAll)
            {
                def = ans.Choice;
                if (!topLevel) { _journal.DefaultConflict = ans.Choice; _journal.Save(); }
            }
            return ans.Choice;
        }
        finally { _promptLock.Release(); }
    }

    // ---------- mover dentro del mismo volumen (renombrar) ----------

    private void MoveSameVolume(List<Top> tops)
    {
        State = TransferState.Running;
        Interlocked.Exchange(ref _totalFiles, tops.Count);
        foreach (var t in tops)
        {
            Checkpoint();
            CurrentItem = Path.GetFileName(t.Src);
            try
            {
                if (t.Replace) RemoveExisting(t.Dst);
                if (t.Merge) MergeMove(t.Src, t.Dst);
                else
                    Io(t.Src, () =>
                    {
                        if (t.IsDir) Directory.Move(_src.Map(t.Src), _dst.Map(t.Dst));
                        else File.Move(_src.Map(t.Src), _dst.Map(t.Dst));
                        return 0;
                    });
                ItemPlaced?.Invoke(t.Src, t.Dst);
                lock (CreatedTopLevel) CreatedTopLevel.Add(t.Dst);
            }
            catch (SkipException) { }
            Interlocked.Increment(ref _doneFiles);
        }
    }

    private void MergeMove(string src, string dst)
    {
        Io(dst, () => { Directory.CreateDirectory(_dst.Map(dst)); return 0; });
        foreach (var child in Directory.EnumerateFileSystemEntries(_src.Map(src)).ToList())
        {
            Checkpoint();
            var name = Path.GetFileName(child);
            var target = Path.Combine(dst, name);
            bool isDir = Directory.Exists(child);
            try
            {
                if (Exists(target))
                {
                    bool destIsDir = Directory.Exists(_dst.Map(target));
                    if (isDir && destIsDir) { MergeMove(child, target); continue; }
                    var choice = Ask(ConflictFor(child, target, isDir, destIsDir), false);
                    if (choice == ConflictChoice.Stop) throw new StopException();
                    if (choice == ConflictChoice.Skip) continue;
                    if (choice == ConflictChoice.KeepBoth) target = UniqueName(dst, name, isDir, false);
                    else RemoveExisting(target);
                }
                Io(child, () =>
                {
                    if (isDir) Directory.Move(child, _dst.Map(target)); else File.Move(child, _dst.Map(target));
                    return 0;
                });
            }
            catch (SkipException) { }
        }
        try { if (!Directory.EnumerateFileSystemEntries(_src.Map(src)).Any()) Directory.Delete(_src.Map(src)); } catch { }
    }

    private ConflictRequest ConflictFor(string src, string dst, bool srcIsDir, bool dstIsDir)
    {
        var s = _src.Map(src); var d = _dst.Map(dst);
        return new ConflictRequest
        {
            SourcePath = s, DestinationPath = d, SourceIsDirectory = srcIsDir, DestinationIsDirectory = dstIsDir,
            SourceSize = srcIsDir ? -1 : SafeLen(s), DestinationSize = dstIsDir ? -1 : SafeLen(d),
            SourceModified = SafeTime(s, srcIsDir), DestinationModified = SafeTime(d, dstIsDir)
        };
    }

    private static long SafeLen(string p) { try { return new FileInfo(p).Length; } catch { return -1; } }
    private static DateTime SafeTime(string p, bool dir) { try { return dir ? Directory.GetLastWriteTime(p) : File.GetLastWriteTime(p); } catch { return DateTime.MinValue; } }

    // ---------- copiar (o mover entre volúmenes) ----------

    private void CopyAcross(List<Top> tops, bool deleteSources)
    {
        // 1) Plan
        var dirs = new List<Entry>();
        var files = new List<Entry>();
        foreach (var t in tops)
        {
            Checkpoint();
            if (t.IsDir)
            {
                var root = new Entry { Src = t.Src, Dst = t.Dst, IsDir = true, InMerge = t.Merge };
                FillTimes(root);
                dirs.Add(root);
                Walk(t.Src, t.Dst, t.Merge, dirs, files);
            }
            else
            {
                var fi = new FileInfo(t.Src);
                files.Add(new Entry
                {
                    Src = t.Src, Dst = t.Dst, Size = fi.Length, Attr = fi.Attributes,
                    CreatedUtc = fi.CreationTimeUtc, ModifiedUtc = fi.LastWriteTimeUtc
                });
            }
        }
        long total = files.Sum(f => f.Size);
        Interlocked.Exchange(ref _totalBytes, total);
        Interlocked.Exchange(ref _totalFiles, files.Count + dirs.Count);
        _journal.TotalBytes = total;
        _journal.Save();

        // 2) Comprobaciones previas (espacio y límite de 4 GB de FAT32)
        CheckSpaceAndLimits(files, tops);

        // 3) Reemplazos de nivel superior
        foreach (var t in tops.Where(t => t.Replace))
        {
            Checkpoint();
            try { RemoveExisting(t.Dst); } catch (SkipException) { }
        }

        State = TransferState.Running;
        Message = "";

        // 4) Carpetas
        foreach (var d in dirs)
        {
            Checkpoint();
            try
            {
                Io(d.Dst, () =>
                {
                    var p = _dst.Map(d.Dst);
                    if (File.Exists(p)) // un archivo con el nombre de la carpeta
                    {
                        var choice = Ask(ConflictFor(d.Src, d.Dst, true, false), false);
                        if (choice == ConflictChoice.Stop) throw new StopException();
                        if (choice == ConflictChoice.Skip) throw new SkipException();
                        RemoveExisting(d.Dst);
                    }
                    Directory.CreateDirectory(p);
                    return 0;
                });
            }
            catch (SkipException) { }
            Interlocked.Increment(ref _doneFiles);
        }

        // 5) Archivos pequeños en paralelo, grandes en serie
        var small = files.Where(f => f.Size < LargeFileThreshold).ToList();
        var large = files.Where(f => f.Size >= LargeFileThreshold).ToList();
        Parallelism = ChooseParallelism();

        if (small.Count > 0)
        {
            var queue = new ConcurrentQueue<Entry>(small);
            var workers = Enumerable.Range(0, Math.Min(Parallelism, Math.Max(1, small.Count))).Select(_ => Task.Factory.StartNew(() =>
            {
                while (queue.TryDequeue(out var e))
                {
                    Checkpoint();
                    CopyOne(e);
                }
            }, Ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            try { Task.WaitAll(workers); }
            catch (AggregateException ae)
            {
                var inner = ae.Flatten().InnerExceptions;
                if (inner.Any(x => x is StopException)) throw new StopException();
                if (inner.Any(x => x is OperationCanceledException)) throw new OperationCanceledException();
                throw inner[0];
            }
        }
        foreach (var e in large)
        {
            Checkpoint();
            CopyOne(e);
        }

        // 6) Fechas de carpetas (de dentro hacia fuera)
        State = TransferState.Finishing;
        Message = deleteSources ? "Finalizando: confirmando copias…" : "Finalizando…";
        for (int i = dirs.Count - 1; i >= 0; i--)
        {
            var d = dirs[i];
            if (d.InMerge) continue;
            try
            {
                var p = _dst.Map(d.Dst);
                Directory.SetCreationTimeUtc(p, d.CreatedUtc);
                Directory.SetLastWriteTimeUtc(p, d.ModifiedUtc);
                if ((d.Attr & (FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System)) != 0)
                    File.SetAttributes(p, FileAttributes.Directory | (d.Attr & (FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System)));
            }
            catch { }
        }

        // 7) Si es mover: confirmar a disco y borrar originales
        if (deleteSources)
        {
            var copied = files.Where(f => f.Copied).ToList();
            var copiedSet = new HashSet<string>(copied.Select(c => c.Src), StringComparer.OrdinalIgnoreCase);
            Parallel.ForEach(copied, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = Ct }, f =>
            {
                if (f.Size >= LargeFileThreshold) return; // ya confirmados al copiarlos
                try
                {
                    using var h = File.OpenHandle(_dst.Map(f.Dst), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    Native.FlushFileBuffers(h);
                }
                catch { }
            });
            Message = "Finalizando: eliminando originales…";
            Unwrap(() => Parallel.ForEach(copied, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = Ct }, f =>
            {
                try
                {
                    Io(f.Src, () =>
                    {
                        var p = _src.Map(f.Src);
                        var a = File.GetAttributes(p);
                        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(p, a & ~FileAttributes.ReadOnly);
                        File.Delete(p);
                        return 0;
                    });
                }
                catch (SkipException) { }
            }));
            // carpetas vacías de dentro hacia fuera; si quedó algo (omitido) no se borra
            for (int i = dirs.Count - 1; i >= 0; i--)
            {
                try
                {
                    var p = _src.Map(dirs[i].Src);
                    if (Directory.Exists(p) && !Directory.EnumerateFileSystemEntries(p).Any())
                    {
                        var a = File.GetAttributes(p);
                        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(p, a & ~FileAttributes.ReadOnly);
                        Directory.Delete(p);
                    }
                }
                catch { }
            }
        }

        foreach (var t in tops)
        {
            ItemPlaced?.Invoke(t.Src, t.Dst);
            lock (CreatedTopLevel) CreatedTopLevel.Add(t.Dst);
        }
    }

    private void Walk(string srcDir, string dstDir, bool inMerge, List<Entry> dirs, List<Entry> files)
    {
        var stack = new Stack<(string s, string d)>();
        stack.Push((srcDir, dstDir));
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };
        while (stack.Count > 0)
        {
            Checkpoint();
            var (s, d) = stack.Pop();
            List<(string name, FileAttributes attr, long len, DateTime c, DateTime m, bool dir)> children;
            try
            {
                children = Io(s, () =>
                {
                    var list = new List<(string, FileAttributes, long, DateTime, DateTime, bool)>();
                    var en = new System.IO.Enumeration.FileSystemEnumerable<(string, FileAttributes, long, DateTime, DateTime, bool)>(
                        _src.Map(s),
                        (ref System.IO.Enumeration.FileSystemEntry e) => (e.FileName.ToString(), e.Attributes, e.Length,
                            e.CreationTimeUtc.UtcDateTime, e.LastWriteTimeUtc.UtcDateTime, e.IsDirectory), opts);
                    list.AddRange(en);
                    return list;
                });
            }
            catch (SkipException) { continue; }

            var subdirs = new List<(string, string)>();
            foreach (var c in children)
            {
                var cs = Path.Combine(s, c.name);
                var cd = Path.Combine(d, c.name);
                if (c.dir)
                {
                    if ((c.attr & FileAttributes.ReparsePoint) != 0)
                    {
                        Issues.Enqueue(new TransferIssue(cs, "Se omitió un enlace simbólico / unión de carpeta."));
                        continue;
                    }
                    dirs.Add(new Entry { Src = cs, Dst = cd, IsDir = true, Attr = c.attr, CreatedUtc = c.c, ModifiedUtc = c.m, InMerge = inMerge });
                    subdirs.Add((cs, cd));
                }
                else
                {
                    files.Add(new Entry { Src = cs, Dst = cd, Size = c.len, Attr = c.attr, CreatedUtc = c.c, ModifiedUtc = c.m, InMerge = inMerge });
                }
                long n = Interlocked.Increment(ref _preparedItems);
                if ((n & 1023) == 0) Message = $"Preparando {Format.Count(n)} elementos…";
            }
            // orden natural: se recorre en el mismo orden que se ve en pantalla
            for (int i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]);
        }
    }

    private static void FillTimes(Entry e)
    {
        try
        {
            var di = new DirectoryInfo(e.Src);
            e.Attr = di.Attributes; e.CreatedUtc = di.CreationTimeUtc; e.ModifiedUtc = di.LastWriteTimeUtc;
        }
        catch { e.CreatedUtc = e.ModifiedUtc = DateTime.UtcNow; }
    }

    private void CheckSpaceAndLimits(List<Entry> files, List<Top> tops)
    {
        try
        {
            var root = _dst.CurrentRoot;
            Native.TryGetVolumeInfo(root, out _, out _, out var fs);
            if (fs.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) && !fs.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
            {
                var big = files.FirstOrDefault(f => f.Size >= 4L * 1024 * 1024 * 1024);
                if (big != null)
                    throw new IOException($"«{Path.GetFileName(big.Src)}» pesa {Format.Size(big.Size)} y el disco de destino usa {fs}, que no admite archivos de 4 GB o más. Formatea el disco en exFAT o NTFS para poder copiarlo.");
            }
            if (!_isResume)
            {
                long needed = files.Sum(f => f.Size);
                // los reemplazos liberan espacio, pero si van a la papelera no; se calcula sin contarlos
                var free = new DriveInfo(root).AvailableFreeSpace;
                if (needed > free + 1024 * 1024)
                    throw new IOException($"No hay suficiente espacio en «{_dst.Label}». Se necesitan {Format.Size(needed - free)} más (hay {Format.Size(free)} libres y la copia ocupa {Format.Size(needed)}).");
            }
        }
        catch (IOException) { throw; }
        catch { }
    }

    private int ChooseParallelism()
    {
        bool network = _src.IsNetwork || _dst.IsNetwork;
        bool hdd = _src.Rotational == true || _dst.Rotational == true;
        if (hdd) return 2;
        if (network) return 4;
        return Math.Clamp(Environment.ProcessorCount, 4, 8);
    }

    // ---------- copia de un archivo ----------

    private void CopyOne(Entry e)
    {
        CurrentItem = Path.GetFileName(e.Src);
        try
        {
            var dst = _dst.Map(e.Dst);
            if (File.Exists(dst))
            {
                if (IsSameCopy(e, dst))
                {
                    Interlocked.Add(ref _doneBytes, e.Size);
                    e.Copied = true;
                    Interlocked.Increment(ref _doneFiles);
                    return;
                }
                bool isOurPartial = _isResume && _journal.PartialFile != null && PathEq(_journal.PartialFile, e.Dst);
                if (!isOurPartial)
                {
                    if (e.InMerge || _isResume)
                    {
                        var choice = Ask(ConflictFor(e.Src, e.Dst, false, false), false);
                        if (choice == ConflictChoice.Stop) throw new StopException();
                        if (choice == ConflictChoice.Skip) { Interlocked.Add(ref _doneBytes, e.Size); Interlocked.Increment(ref _doneFiles); return; }
                        if (choice == ConflictChoice.KeepBoth)
                            e.Dst = UniqueName(Path.GetDirectoryName(e.Dst), Path.GetFileName(e.Dst), false, false);
                    }
                    ClearReadOnly(_dst.Map(e.Dst));
                }
            }

            if (e.Size >= LargeFileThreshold) CopyLarge(e);
            else CopySmall(e);
            e.Copied = true;
        }
        catch (SkipException) { Interlocked.Add(ref _doneBytes, e.Size); }
        Interlocked.Increment(ref _doneFiles);
    }

    private static bool IsSameCopy(Entry e, string dst)
    {
        try
        {
            var fi = new FileInfo(dst);
            return fi.Length == e.Size && Math.Abs((fi.LastWriteTimeUtc - e.ModifiedUtc).TotalSeconds) <= 2;
        }
        catch { return false; }
    }

    private static void ClearReadOnly(string p)
    {
        try
        {
            var a = File.GetAttributes(p);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(p, a & ~FileAttributes.ReadOnly);
        }
        catch { }
    }

    private void CopySmall(Entry e)
    {
        Io(e.Src, () =>
        {
            var src = _src.Map(e.Src);
            var dst = _dst.Map(e.Dst);
            File.Copy(src, dst, true);
            try { File.SetCreationTimeUtc(dst, e.CreatedUtc); } catch { }
            return 0;
        });
        Interlocked.Add(ref _doneBytes, e.Size);
    }

    private void CopyLarge(Entry e)
    {
        long confirmed = 0;
        if (_isResume && _journal.PartialFile != null && PathEq(_journal.PartialFile, e.Dst)
            && _journal.PartialSourceSize == e.Size && _journal.PartialSourceModifiedUtc == e.ModifiedUtc)
        {
            try
            {
                var len = new FileInfo(_dst.Map(e.Dst)).Length;
                confirmed = Math.Min(_journal.PartialFlushedOffset, len);
            }
            catch { confirmed = 0; }
        }
        Interlocked.Add(ref _doneBytes, confirmed);
        long counted = confirmed; // bytes de este archivo ya sumados al progreso

        _journal.PartialFile = e.Dst;
        _journal.PartialSourceSize = e.Size;
        _journal.PartialSourceModifiedUtc = e.ModifiedUtc;
        _journal.PartialFlushedOffset = confirmed;
        _journal.Save();

        byte[] a = ArrayPool<byte>.Shared.Rent(ChunkSize);
        byte[] b = ArrayPool<byte>.Shared.Rent(ChunkSize);
        int deviceRetries = 0;
        try
        {
            while (true)
            {
                Checkpoint();
                try
                {
                    using (var src = File.OpenHandle(_src.Map(e.Src), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.SequentialScan))
                    using (var dst = File.OpenHandle(_dst.Map(e.Dst), confirmed > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write, FileShare.Read, FileOptions.None))
                    {
                        long srcLen = RandomAccess.GetLength(src);
                        if (confirmed == 0 && srcLen > 0)
                        {
                            try { RandomAccess.SetLength(dst, srcLen); } catch (IOException ex) when (IsDiskFull(ex)) { throw; } catch { }
                        }
                        long pos = confirmed;
                        long sinceFlush = 0;
                        int n = RandomAccess.Read(src, a.AsSpan(0, ChunkSize), pos);
                        while (n > 0)
                        {
                            Checkpoint();
                            long writePos = pos;
                            long nextPos = pos + n;
                            var bb = b;
                            Task<int> next = Task.Run(() => RandomAccess.Read(src, bb.AsSpan(0, ChunkSize), nextPos));
                            try
                            {
                                RandomAccess.Write(dst, new ReadOnlySpan<byte>(a, 0, n), writePos);
                            }
                            catch
                            {
                                try { next.Wait(); } catch { }
                                throw;
                            }
                            pos = nextPos;
                            Interlocked.Add(ref _doneBytes, n);
                            counted += n;
                            sinceFlush += n;
                            if (sinceFlush >= FlushEvery)
                            {
                                Native.FlushFileBuffers(dst);
                                confirmed = pos;
                                sinceFlush = 0;
                                _journal.PartialFlushedOffset = confirmed;
                                _journal.DoneBytes = DoneBytes;
                                SaveJournalThrottled();
                            }
                            n = next.GetAwaiter().GetResult();
                            (a, b) = (b, a);
                        }
                        if (RandomAccess.GetLength(dst) != pos) RandomAccess.SetLength(dst, pos);
                        // fechas por el handle abierto (no por ruta: si el disco cambió de letra la ruta ya no sirve)
                        File.SetCreationTimeUtc(dst, e.CreatedUtc);
                        File.SetLastWriteTimeUtc(dst, e.ModifiedUtc);
                        if (!Native.FlushFileBuffers(dst))
                            throw new IOException("No se pudo confirmar la escritura en el disco de destino.", System.Runtime.InteropServices.Marshal.GetHRForLastWin32Error());
                        confirmed = pos;
                        if (pos != e.Size) Interlocked.Add(ref _totalBytes, pos - e.Size); // el archivo cambió mientras se copiaba
                    }
                    try
                    {
                        var keep = e.Attr & (FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System);
                        if (keep != 0) File.SetAttributes(_dst.Map(e.Dst), keep | FileAttributes.Archive);
                    }
                    catch { }
                    _journal.PartialFile = null;
                    _journal.PartialFlushedOffset = 0;
                    _journal.Save();
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not SkipException)
                {
                    // volver al último punto confirmado
                    Interlocked.Add(ref _doneBytes, confirmed - counted);
                    counted = confirmed;
                    if (IsDeviceProblem(ex) && deviceRetries++ < 50)
                    {
                        Log($"Dispositivo no disponible copiando {e.Src}: {ex.Message}. Reanudará desde {confirmed}.");
                        WaitForDevices();
                        continue;
                    }
                    if (IsTransient(ex) && deviceRetries++ < 5) { Thread.Sleep(300 * deviceRetries); continue; }
                    var choice = AskError(e.Src, ex);
                    if (choice == ErrorChoice.Retry) continue;
                    // omitir: borrar el parcial
                    try { File.Delete(_dst.Map(e.Dst)); } catch { }
                    _journal.PartialFile = null;
                    Interlocked.Add(ref _doneBytes, -counted); // CopyOne suma el tamaño completo al omitir
                    throw new SkipException();
                }
                catch (OperationCanceledException)
                {
                    // cancelado a medias: el archivo parcial no sirve
                    try { File.Delete(_dst.Map(e.Dst)); } catch { }
                    throw;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(a);
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    private static void Unwrap(Action a)
    {
        try { a(); }
        catch (AggregateException ae)
        {
            var inner = ae.Flatten().InnerExceptions;
            if (inner.Any(x => x is StopException)) throw new StopException();
            if (inner.Any(x => x is OperationCanceledException)) throw new OperationCanceledException();
            throw inner[0];
        }
    }

    private void SaveJournalThrottled()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastJournalSave).TotalSeconds < 2) return;
        _lastJournalSave = now;
        _journal.Save();
    }

    // ---------- E/S resistente ----------

    private T Io<T>(string path, Func<T> op)
    {
        int deviceRetries = 0, transientRetries = 0;
        while (true)
        {
            Checkpoint();
            try { return op(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (IsDeviceProblem(ex) && deviceRetries++ < 50)
                {
                    WaitForDevices();
                    continue;
                }
                if (IsTransient(ex) && transientRetries++ < 4) { Thread.Sleep(250 * transientRetries); continue; }
                var choice = AskError(path, ex);
                if (choice == ErrorChoice.Retry) { deviceRetries = transientRetries = 0; continue; }
                throw new SkipException();
            }
        }
    }

    private ErrorChoice AskError(string path, Exception ex)
    {
        var msg = Explain(ex);
        if (IsDiskFull(ex))
        {
            // sin espacio no tiene sentido seguir: se pregunta igual, pero "omitir" no ayuda
            msg = $"El disco de destino se quedó sin espacio. {msg}";
        }
        if (_skipAllErrors)
        {
            Issues.Enqueue(new TransferIssue(path, msg));
            return ErrorChoice.Skip;
        }
        _promptLock.Wait(Ct);
        try
        {
            if (_skipAllErrors) { Issues.Enqueue(new TransferIssue(path, msg)); return ErrorChoice.Skip; }
            var prev = State;
            State = TransferState.WaitingForUser;
            var c = _prompt.ResolveErrorAsync(this, new ErrorRequest { Path = path, Message = msg, Exception = ex }).GetAwaiter().GetResult();
            State = prev == TransferState.WaitingForUser ? TransferState.Running : prev;
            switch (c)
            {
                case ErrorChoice.Stop: throw new StopException();
                case ErrorChoice.SkipAll: _skipAllErrors = true; Issues.Enqueue(new TransferIssue(path, msg)); return ErrorChoice.Skip;
                case ErrorChoice.Skip: Issues.Enqueue(new TransferIssue(path, msg)); return ErrorChoice.Skip;
                default: return ErrorChoice.Retry;
            }
        }
        finally { _promptLock.Release(); }
    }

    public static string Explain(Exception ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code switch
        {
            5 => "No tienes permiso para acceder a este elemento.",
            32 => "El archivo está en uso por otra aplicación.",
            33 => "Otra aplicación tiene bloqueada una parte del archivo.",
            39 or 112 => "No queda espacio en el disco.",
            206 => "El nombre o la ruta es demasiado largo para el destino.",
            123 => "El nombre contiene caracteres que el destino no admite.",
            1920 => "El sistema no puede acceder al archivo.",
            _ => ex.Message
        };
    }

    private static bool IsDiskFull(Exception ex) => (ex.HResult & 0xFFFF) is 39 or 112;

    private static readonly HashSet<int> DeviceCodes = new()
    {
        2, 3,     // no encontrado (si además falta la raíz)
        15,       // ERROR_INVALID_DRIVE
        21,       // ERROR_NOT_READY
        31,       // ERROR_GEN_FAILURE
        53, 51, 59, 64, 67, 1231, // red
        55,       // ERROR_DEV_NOT_EXIST
        121,      // ERROR_SEM_TIMEOUT
        483,      // ERROR_DEVICE_HARDWARE_ERROR
        1006,     // ERROR_FILE_INVALID (volumen desmontado)
        1117,     // ERROR_IO_DEVICE
        1167,     // ERROR_DEVICE_NOT_CONNECTED
        6,        // ERROR_INVALID_HANDLE (handle de un volumen que desapareció)
    };

    private bool IsDeviceProblem(Exception ex)
    {
        int code = ex.HResult & 0xFFFF;
        bool rootsOk = _src.IsAvailable() && _dst.IsAvailable();
        if (!rootsOk) return true;
        if (code is 2 or 3) return false; // la raíz está; es que el archivo no existe
        return DeviceCodes.Contains(code);
    }

    private static bool IsTransient(Exception ex) => (ex.HResult & 0xFFFF) is 32 or 33 or 1224 or 170;

    /// <summary>Detiene a todos los hilos hasta que vuelvan a estar disponibles los dos volúmenes.</summary>
    private void WaitForDevices()
    {
        bool owner = false;
        lock (_deviceLock)
        {
            if (!_deviceWaiting) { _deviceWaiting = true; owner = true; _deviceGate.Reset(); }
        }
        if (!owner)
        {
            _deviceGate.Wait(Ct);
            return;
        }
        var prev = State == TransferState.WaitingForDevice ? TransferState.Running : State;
        try
        {
            if (!(_src.IsAvailable() && _dst.IsAvailable()))
            {
                State = TransferState.WaitingForDevice;
                while (true)
                {
                    bool s = _src.IsAvailable() || _src.TryRemap();
                    bool d = _dst.IsAvailable() || _dst.TryRemap();
                    if (s && d) break;
                    var who = !s ? _src : _dst;
                    Message = $"Esperando a que se vuelva a conectar «{who.Label}»… La operación seguirá sola donde se quedó.";
                    if (Ct.WaitHandle.WaitOne(750)) Ct.ThrowIfCancellationRequested();
                    if (!_pauseGate.IsSet) _pauseGate.Wait(Ct);
                }
                Message = "Disco reconectado. Reanudando…";
                Log($"Volúmenes disponibles de nuevo ({_src.CurrentRoot} → {_dst.CurrentRoot}).");
                Ct.WaitHandle.WaitOne(1500); // dar tiempo a que Windows termine de montarlo
            }
            else
            {
                Ct.WaitHandle.WaitOne(400);
            }
        }
        finally
        {
            if (State == TransferState.WaitingForDevice) State = prev;
            Message = "";
            lock (_deviceLock) { _deviceWaiting = false; _deviceGate.Set(); }
        }
    }

    private bool WaitUntil(Func<bool> cond, string what)
    {
        var sw = Stopwatch.StartNew();
        State = TransferState.WaitingForDevice;
        while (!cond())
        {
            _dst.TryRemap();
            Message = $"Esperando a que esté disponible {what}…";
            if (Ct.WaitHandle.WaitOne(750)) { Ct.ThrowIfCancellationRequested(); }
            if (sw.Elapsed.TotalMinutes > 60) throw new IOException("El destino no está disponible.");
        }
        State = TransferState.Preparing;
        return true;
    }

    // ---------- utilidades ----------

    private void RemoveExisting(string path)
    {
        Io(path, () =>
        {
            var p = _dst.Map(path);
            bool canRecycle = _dst.DriveType == Native.DRIVE_FIXED;
            if (canRecycle && Native.SendToRecycleBin(new[] { p }) == 0 && !Exists(path)) return 0;
            if (Directory.Exists(p)) Directory.Delete(p, true);
            else if (File.Exists(p)) { ClearReadOnly(p); File.Delete(p); }
            return 0;
        });
    }

    private bool Exists(string path)
    {
        var p = _dst?.Map(path) ?? path;
        return File.Exists(p) || Directory.Exists(p);
    }

    /// <summary>
    /// copyStyle: "foto copia.jpg", "foto copia 2.jpg" (duplicar);
    /// si no: "foto 2.jpg", "foto 3.jpg" (conservar ambos).
    /// </summary>
    public static string UniqueName(string dir, string name, bool isDir, bool copyStyle)
    {
        string stem = name, ext = "";
        if (!isDir)
        {
            ext = Path.GetExtension(name);
            if (ext.Length > 0 && ext.Length < name.Length) stem = name[..^ext.Length]; else ext = "";
        }
        if (copyStyle)
        {
            var baseStem = stem;
            var m = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*) copia( \d+)?$");
            if (m.Success) baseStem = m.Groups[1].Value;
            var cand = Path.Combine(dir, baseStem + " copia" + ext);
            for (int i = 2; File.Exists(cand) || Directory.Exists(cand); i++)
                cand = Path.Combine(dir, $"{baseStem} copia {i}{ext}");
            return cand;
        }
        else
        {
            var baseStem = stem;
            int start = 2;
            var m = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*) (\d+)$");
            if (m.Success) { baseStem = m.Groups[1].Value; start = int.Parse(m.Groups[2].Value) + 1; }
            string cand;
            int i = start;
            do cand = Path.Combine(dir, $"{baseStem} {i++}{ext}"); while (File.Exists(cand) || Directory.Exists(cand));
            return cand;
        }
    }

    public static bool PathEq(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>true si <paramref name="child"/> es <paramref name="parent"/> o está dentro.</summary>
    public static bool IsInside(string child, string parent)
    {
        var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child)) + "\\";
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + "\\";
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly object LogLock = new();
    private void Log(string text)
    {
        try
        {
            var dir = Path.Combine(AppInfo.LocalDir, "logs");
            Directory.CreateDirectory(dir);
            lock (LogLock)
                File.AppendAllText(Path.Combine(dir, "transferencias.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Id.ToString()[..8]}] {text}{Environment.NewLine}");
        }
        catch { }
    }

    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    // ---------- velocidad / tiempo restante ----------
    private long _lastBytes, _lastFiles; private double _lastT;
    private double _bps, _fps;
    public double BytesPerSecond => _bps;

    /// <summary>La UI lo llama ~4 veces por segundo; actualiza la media de velocidad.</summary>
    public void Sample()
    {
        double t = Elapsed.Elapsed.TotalSeconds;
        double dt = t - _lastT;
        if (dt < 0.2) return;
        long b = DoneBytes, f = DoneFiles;
        if (State == TransferState.Running)
        {
            double ib = (b - _lastBytes) / dt, iff = (f - _lastFiles) / dt;
            _bps = _bps == 0 ? ib : _bps * 0.85 + ib * 0.15;
            _fps = _fps == 0 ? iff : _fps * 0.85 + iff * 0.15;
        }
        _lastBytes = b; _lastFiles = f; _lastT = t;
    }

    public TimeSpan? Remaining
    {
        get
        {
            if (State != TransferState.Running || Elapsed.Elapsed.TotalSeconds < 2) return null;
            double byBytes = _bps > 1 ? (TotalBytes - DoneBytes) / _bps : 0;
            double byFiles = _fps > 0.5 ? (TotalFiles - DoneFiles) / _fps : 0;
            double s = Math.Max(byBytes, byFiles);
            if (s <= 0 || double.IsInfinity(s) || double.IsNaN(s)) return null;
            return TimeSpan.FromSeconds(Math.Min(s, 360000));
        }
    }

    public double Fraction
    {
        get
        {
            long tb = TotalBytes, tf = TotalFiles;
            if (tb > 0)
            {
                // mezcla bytes y número de archivos para que 100.000 archivos pequeños también avancen
                double fb = (double)DoneBytes / tb;
                double ff = tf > 0 ? (double)DoneFiles / tf : fb;
                return Math.Clamp(fb * 0.7 + ff * 0.3, 0, 1);
            }
            return tf > 0 ? Math.Clamp((double)DoneFiles / tf, 0, 1) : 0;
        }
    }
}

/// <summary>
/// Referencia a un volumen que sobrevive a que se desconecte y vuelva con otra letra.
/// </summary>
internal sealed class VolumeRef
{
    public string OriginalRoot { get; private set; }
    public string CurrentRoot { get; private set; }
    public uint Serial { get; private set; }
    public string Label { get; private set; }
    public uint DriveType { get; private set; }
    public bool? Rotational { get; private set; }
    public bool IsNetwork => DriveType == Native.DRIVE_REMOTE || OriginalRoot.StartsWith(@"\\");

    /// <summary>Ruta que debe existir en el volumen para aceptar un remapeo (evita confundir discos).</summary>
    public string Anchor { get; private set; }
    public bool IsSubstDrive { get; private set; }

    public static VolumeRef For(string path)
    {
        var root = Native.VolumeRoot(path);
        var v = new VolumeRef { OriginalRoot = root, CurrentRoot = root, Anchor = path, IsSubstDrive = Native.IsSubst(root) };
        if (Native.TryGetVolumeInfo(root, out var serial, out var label, out _))
        {
            v.Serial = serial;
            v.Label = string.IsNullOrWhiteSpace(label) ? root.TrimEnd('\\') : label;
        }
        else v.Label = root.TrimEnd('\\');
        v.DriveType = Native.GetDriveTypeW(root);
        v.Rotational = Native.HasSeekPenalty(root);
        return v;
    }

    public bool SameVolumeAs(VolumeRef o) =>
        string.Equals(OriginalRoot, o.OriginalRoot, StringComparison.OrdinalIgnoreCase) && Serial == o.Serial;

    public string Map(string path)
    {
        if (ReferenceEquals(CurrentRoot, OriginalRoot) || string.Equals(CurrentRoot, OriginalRoot, StringComparison.OrdinalIgnoreCase))
            return path;
        if (path.StartsWith(OriginalRoot, StringComparison.OrdinalIgnoreCase))
            return CurrentRoot + path[OriginalRoot.Length..];
        return path;
    }

    /// <summary>Disponible y es el MISMO disco (no otro que tomó la misma letra).</summary>
    public bool IsAvailable()
    {
        if (!Directory.Exists(CurrentRoot)) return false;
        if (Serial == 0) return true;
        return Native.TryGetVolumeInfo(CurrentRoot, out var s, out _, out _) && s == Serial;
    }

    /// <summary>Busca el mismo volumen (por número de serie) bajo otra letra.</summary>
    public bool TryRemap()
    {
        if (Serial == 0 || IsNetwork || IsSubstDrive) return false;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (Native.IsSubst(d.Name)) continue;
                if (Native.TryGetVolumeInfo(d.Name, out var s, out _, out _) && s == Serial)
                {
                    var anchorThere = Anchor != null && Anchor.StartsWith(OriginalRoot, StringComparison.OrdinalIgnoreCase)
                        ? d.Name + Anchor[OriginalRoot.Length..] : d.Name;
                    if (!File.Exists(anchorThere) && !Directory.Exists(anchorThere)) continue;
                    if (!string.Equals(d.Name, CurrentRoot, StringComparison.OrdinalIgnoreCase))
                        CurrentRoot = d.Name;
                    return true;
                }
            }
            catch { }
        }
        return false;
    }
}
