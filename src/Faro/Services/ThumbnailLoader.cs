using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Faro.Core;
using Faro.Model;
using Faro.Shell;

namespace Faro.Services;

/// <summary>
/// Carga miniaturas en hilos de fondo (STA, como exige el Shell), pidiendo primero lo
/// último que se mostró (lo que está en pantalla). Con caché LRU limitada por memoria.
/// </summary>
public static class ThumbnailLoader
{
    /// <summary>Tamaño en píxeles físicos que necesita la vista activa.</summary>
    public static int CurrentSize { get; set; } = 64;

    private sealed record Req(FileItem Item, int Size, int Generation);

    private static readonly LinkedList<Req> Stack = new();
    /// <summary>Precarga (lo que aún no se ve): sólo se atiende cuando no hay nada visible pendiente.</summary>
    private static readonly Queue<Req> Background = new();
    private static readonly object Lock = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static int _generation;
    private static bool _started;
    private static Dispatcher _ui;

    // caché
    private static readonly Dictionary<string, LinkedListNode<(string key, BitmapSource img, long bytes)>> CacheMap = new();
    private static readonly LinkedList<(string key, BitmapSource img, long bytes)> CacheList = new();
    private static long _cacheBytes;
    private const long CacheBudget = 220L * 1024 * 1024;

    private static void Start()
    {
        if (_started) return;
        _started = true;
        _ui = Application.Current.Dispatcher;
        int n = Math.Clamp(Environment.ProcessorCount - 2, 3, 8);
        for (int i = 0; i < n; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = "Miniaturas " + i, Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    public static bool Wants(FileItem item)
    {
        if (item.Special == ItemSpecial.Drive) return false;
        if (item.Special == ItemSpecial.App) return true;
        if (item.IsDirectory) return false;
        return Mode(item) != null;
    }

    private static ShellInterop.SIIGBF? Mode(FileItem item)
    {
        if (item.Special == ItemSpecial.App) return ShellInterop.SIIGBF.IconOnly;
        switch (item.Kind.Category)
        {
            case KindCategory.Image:
            case KindCategory.RawPhoto:
            case KindCategory.Video:
            case KindCategory.Pdf:
            case KindCategory.Adobe:
            case KindCategory.Design:
            case KindCategory.Vector:
            case KindCategory.Font:
            case KindCategory.Model3D:
            case KindCategory.Presentation:
            case KindCategory.Document:
            case KindCategory.Spreadsheet:
            case KindCategory.Ebook:
            case KindCategory.Cad:
            case KindCategory.Audio:
                return ShellInterop.SIIGBF.ThumbnailOnly;
            case KindCategory.Executable:
            case KindCategory.Shortcut:
                return ShellInterop.SIIGBF.IconOnly;
            case KindCategory.Other:
                return item.Extension.Length > 0 ? ShellInterop.SIIGBF.IconOnly : ShellInterop.SIIGBF.MemoryOnly; // sin extensión: se mira el contenido
            default:
                if (item.Extension is "url" or "ico" or "cur") return ShellInterop.SIIGBF.IconOnly;
                return null;
        }
    }

    public static void Enqueue(FileItem item, int size)
    {
        Start();
        var key = Key(item, size);
        lock (Lock)
        {
            if (CacheMap.TryGetValue(key, out var node))
            {
                CacheList.Remove(node); CacheList.AddFirst(node);
                var img = node.Value.img;
                _ui.BeginInvoke(DispatcherPriority.Render, () => item.SetThumbnail(img, size));
                return;
            }
            Stack.AddFirst(new Req(item, size, _generation));
            if (Stack.Count > 4000) Stack.RemoveLast();
        }
        Signal.Release();
    }

    /// <summary>Encola en segundo plano las miniaturas de toda la carpeta (las primeras N).</summary>
    public static void Prefetch(IEnumerable<FileItem> items, int max = 800)
    {
        Start();
        int size = CurrentSize, added = 0;
        lock (Lock)
        {
            Background.Clear();
            foreach (var i in items)
            {
                if (added >= max) break;
                if (!Wants(i) || !i.TryMarkRequested()) continue;
                Background.Enqueue(new Req(i, size, _generation));
                added++;
            }
        }
        for (int k = 0; k < added; k++) Signal.Release();
    }

    /// <summary>Al cambiar de carpeta: lo pendiente ya no interesa.</summary>
    public static void CancelPending()
    {
        lock (Lock) { Stack.Clear(); Background.Clear(); _generation++; }
    }

    private static string Key(FileItem i, int size) => $"{i.Path}|{size}|{i.Modified.Ticks}|{i.Size}";

    private static void Worker()
    {
        while (true)
        {
            Signal.Wait();
            Req r;
            lock (Lock)
            {
                if (Stack.Count > 0) { r = Stack.First.Value; Stack.RemoveFirst(); }
                else if (Background.Count > 0) r = Background.Dequeue();
                else continue;
                if (r.Generation != _generation) continue;
            }
            var mode = Mode(r.Item);
            if (mode == null) continue;
            var path = r.Item.Special == ItemSpecial.App && r.Item.LaunchPath != null ? r.Item.LaunchPath : r.Item.Path;
            BitmapSource img = mode == ShellInterop.SIIGBF.MemoryOnly
                ? DecodeBySniff(path, r.Size)
                : ShellInterop.GetImage(path, r.Size, mode.Value | ShellInterop.SIIGBF.BiggerSizeOk);
            if (img == null && r.Item.Extension is "pdf" or "ai") img = PdfRenderer.FirstPage(path, r.Size);
            if (img == null && mode == ShellInterop.SIIGBF.ThumbnailOnly && r.Item.Kind.Category is KindCategory.Audio or KindCategory.Font)
                continue;
            if (img == null) continue;
            img = Shrink(img, r.Size);
            var key = Key(r.Item, r.Size);
            lock (Lock)
            {
                long bytes = (long)img.PixelWidth * img.PixelHeight * 4;
                if (!CacheMap.ContainsKey(key))
                {
                    var node = CacheList.AddFirst((key, img, bytes));
                    CacheMap[key] = node;
                    _cacheBytes += bytes;
                    while (_cacheBytes > CacheBudget && CacheList.Last != null)
                    {
                        var last = CacheList.Last; CacheList.RemoveLast(); CacheMap.Remove(last.Value.key); _cacheBytes -= last.Value.bytes;
                    }
                }
            }
            var item = r.Item; int size = r.Size;
            _ui.BeginInvoke(DispatcherPriority.Background, () => item.SetThumbnail(img, size));
        }
    }

    /// <summary>
    /// Con BiggerSizeOk Windows a veces devuelve la miniatura de 1024 px para una celda de 64:
    /// se reduce al tamaño pedido para no llenar la memoria (Recientes llegaba a 900 MB).
    /// </summary>
    private static BitmapSource Shrink(BitmapSource img, int size)
    {
        int longest = Math.Max(img.PixelWidth, img.PixelHeight);
        if (longest <= size * 1.5) return img;
        try
        {
            // a mitades: reducir de golpe 16:1 con el escalado de WPF deja dientes de sierra
            BitmapSource cur = img;
            while (longest > size)
            {
                double k = longest / 2 >= size ? 0.5 : (double)size / longest;
                cur = new WriteableBitmap(new TransformedBitmap(cur, new System.Windows.Media.ScaleTransform(k, k)));
                longest = Math.Max(cur.PixelWidth, cur.PixelHeight);
            }
            cur.Freeze();
            return cur;
        }
        catch { return img; }
    }

    /// <summary>Archivo sin extensión: si por dentro es una imagen, se decodifica igual.</summary>
    private static BitmapSource DecodeBySniff(string path, int size)
    {
        var ext = FileKinds.SniffExtension(path);
        if (ext == null || !FileKinds.IsWpfImage(ext)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = size;
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    /// <summary>Imagen grande para la galería / vista previa (síncrona; llamar en hilo STA de fondo).</summary>
    public static Task<BitmapSource> LoadLargeAsync(string path, int px, bool allowIcon = true)
    {
        var tcs = new TaskCompletionSource<BitmapSource>();
        var t = new Thread(() =>
        {
            try
            {
                var img = ShellInterop.GetImage(path, px, ShellInterop.SIIGBF.ThumbnailOnly | ShellInterop.SIIGBF.BiggerSizeOk);
                var e = Path.GetExtension(path).ToLowerInvariant();
                if (img == null && e is ".pdf" or ".ai") img = PdfRenderer.FirstPage(path, px);
                if (img == null && allowIcon) img = ShellInterop.GetImage(path, Math.Min(px, 256), ShellInterop.SIIGBF.IconOnly | ShellInterop.SIIGBF.BiggerSizeOk);
                tcs.SetResult(img);
            }
            catch { tcs.SetResult(null); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}
