using System.IO.Enumeration;
using System.Security.Principal;
using System.Text;
using Faro.Core;
using Faro.Model;
using Faro.Shell;

namespace Faro.Services;

/// <summary>Lee el contenido de cada tipo de lugar.</summary>
public static class Providers
{
    public static List<FileItem> LoadFolder(string path, bool showHidden, CancellationToken ct)
    {
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
            AttributesToSkip = showHidden ? 0 : (FileAttributes.Hidden | FileAttributes.System), BufferSize = 64 * 1024
        };
        // \servidor no es una carpeta: se listan sus recursos compartidos
        if (NetworkService.IsServerRoot(path)) return NetworkService.LoadShares(path);
        // sin IgnoreInaccessible en la raíz: queremos el error de permisos de la carpeta en sí
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"La carpeta «{path}» ya no existe.");
        // Con IgnoreInaccessible una carpeta sin permiso (System Volume Information, Documents and Settings…)
        // salía vacía sin avisar. Las sobrecargas simples sí lanzan el error de acceso: se prueba con una.
        using (var probe = Directory.EnumerateFileSystemEntries(path).GetEnumerator()) probe.MoveNext();
        var en = new FileSystemEnumerable<FileItem>(path, (ref FileSystemEntry e) =>
        {
            var name = e.FileName.ToString();
            return new FileItem(Path.Join(path, name), name, e.IsDirectory, e.Length,
                e.LastWriteTimeUtc.LocalDateTime, e.CreationTimeUtc.LocalDateTime, e.Attributes);
        }, opts)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => showHidden || !(e.FileName.Length > 1 && e.FileName[0] == '.')
        };
        var list = new List<FileItem>();
        foreach (var i in en)
        {
            list.Add(i);
            if ((list.Count & 4095) == 0) ct.ThrowIfCancellationRequested();
        }
        // Probar acceso: si la carpeta no deja listar, FileSystemEnumerable lanza al empezar.
        return list;
    }

    public static List<FileItem> LoadComputer()
    {
        var list = new List<FileItem>();
        var sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                var hint = d.DriveType switch
                {
                    DriveType.Removable => DriveKindHint.Removable,
                    DriveType.Network => DriveKindHint.Network,
                    DriveType.CDRom => DriveKindHint.Optical,
                    _ => string.Equals(d.Name, sysRoot, StringComparison.OrdinalIgnoreCase) ? DriveKindHint.System
                        : Native.IsExternalBus(d.Name) ? DriveKindHint.Removable : DriveKindHint.Fixed
                };
                if (!d.IsReady)
                {
                    if (d.DriveType == DriveType.CDRom) continue;
                    continue;
                }
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (hint == DriveKindHint.System ? "Disco local" : "Unidad") : d.VolumeLabel;
                list.Add(new FileItem(d.Name, $"{label} ({d.Name.TrimEnd('\\')})", true, d.TotalSize, DateTime.MinValue, DateTime.MinValue, FileAttributes.Directory)
                {
                    Special = ItemSpecial.Drive, Extra = d.AvailableFreeSpace, Capacity = d.TotalSize, DriveHint = hint
                });
            }
            catch { }
        }
        return list;
    }

    public static bool IsEjectable(string root)
    {
        try
        {
            var d = new DriveInfo(root);
            return d.DriveType is DriveType.Removable or DriveType.CDRom || (d.DriveType == DriveType.Fixed && Native.IsExternalBus(root));
        }
        catch { return false; }
    }

    public static List<FileItem> LoadRecents(CancellationToken ct)
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        var res = new List<FileItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<FileInfo> links;
        try { links = new DirectoryInfo(dir).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc).Take(400).ToList(); }
        catch { return res; }
        foreach (var l in links)
        {
            if (ct.IsCancellationRequested) break;
            var target = ShellInterop.ResolveShortcut(l.FullName);
            if (string.IsNullOrEmpty(target) || !seen.Add(target)) continue;
            if (!File.Exists(target)) continue; // Finder "Recientes" muestra documentos, no carpetas
            var fi = new FileInfo(target);
            var opened = l.LastWriteTime > fi.LastWriteTime ? l.LastWriteTime : fi.LastWriteTime; // fecha de última apertura
            res.Add(new FileItem(target, fi.Name, false, fi.Length, opened, fi.CreationTime, fi.Attributes) { Location = fi.DirectoryName });
            if (res.Count >= 200) break;
        }
        // Como Finder: también lo que se ha creado o cambiado hace poco en las carpetas personales
        var since = DateTime.Now.AddDays(-30);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var root in new[] { KnownFolders.Desktop, KnownFolders.Documents, KnownFolders.Downloads, KnownFolders.Pictures, KnownFolders.Videos })
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                int scanned = 0;
                foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts))
                {
                    if (++scanned > 6000 || ct.IsCancellationRequested) break;
                    if (f.Name.StartsWith('.') || f.Name.StartsWith("~$") || f.Extension is ".tmp" or ".ini" or ".lnk" or ".crdownload" or ".part") continue;
                    if (f.LastWriteTime < since && f.CreationTime < since) continue;
                    if (!seen.Add(f.FullName)) continue;
                    var dt = f.LastWriteTime > f.CreationTime ? f.LastWriteTime : f.CreationTime; // una descarga conserva la fecha de origen
                    res.Add(new FileItem(f.FullName, f.Name, false, f.Length, dt, f.CreationTime, f.Attributes) { Location = f.DirectoryName });
                }
            }
            catch { }
        }
        return res.OrderByDescending(i => i.Modified).Take(400).ToList();
    }

    public static List<FileItem> LoadApps()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        };
        var byName = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in roots)
        {
            if (!Directory.Exists(r)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(r, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
            catch { continue; }
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".lnk" or ".url" or ".appref-ms")) continue;
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("desinstal", StringComparison.OrdinalIgnoreCase)) continue;
                if (byName.ContainsKey(name)) continue;
                var fi = new FileInfo(f);
                byName[name] = new FileItem(f, name, false, fi.Length, fi.LastWriteTime, fi.CreationTime, fi.Attributes)
                {
                    Special = ItemSpecial.App, LaunchPath = f
                };
            }
        }
        return byName.Values.ToList();
    }

    public static List<FileItem> LoadTag(string tag)
    {
        return TagStore.PathsWith(tag).Select(FileItem.FromPath).Where(i => i != null)
            .Select(i => new FileItem(i.Path, i.Name, i.IsDirectory, i.Size, i.Modified, i.Created, i.Attributes) { Location = Path.GetDirectoryName(i.Path) })
            .ToList();
    }
}

/// <summary>
/// Papelera: lista lo que hay en la Papelera de reciclaje de Windows (en todos los discos),
/// permite devolver elementos a su sitio, borrarlos o vaciarla. Es la misma Papelera de Windows.
/// </summary>
public static class TrashService
{
    public sealed record Entry(string OriginalPath, string RPath, string IPath, long Size, DateTime Deleted, bool IsDir);

    private static string Sid => WindowsIdentity.GetCurrent().User?.Value;

    public static List<Entry> List()
    {
        var res = new List<Entry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.Network) continue;
            var bin = Path.Combine(d.Name, "$Recycle.Bin", Sid);
            if (!Directory.Exists(bin)) continue;
            try
            {
                foreach (var i in Directory.EnumerateFiles(bin, "$I*"))
                {
                    var e = Parse(i);
                    if (e != null) res.Add(e);
                }
            }
            catch { }
        }
        return res;
    }

    private static Entry Parse(string iPath)
    {
        try
        {
            var b = File.ReadAllBytes(iPath);
            if (b.Length < 24) return null;
            long ver = BitConverter.ToInt64(b, 0);
            long size = BitConverter.ToInt64(b, 8);
            var deleted = DateTime.FromFileTime(BitConverter.ToInt64(b, 16));
            string path;
            if (ver >= 2)
            {
                int len = BitConverter.ToInt32(b, 24);
                path = Encoding.Unicode.GetString(b, 28, Math.Min(len * 2, b.Length - 28)).TrimEnd('\0');
            }
            else path = Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24)).Split('\0')[0];
            var rPath = Path.Combine(Path.GetDirectoryName(iPath)!, "$R" + Path.GetFileName(iPath)[2..]);
            bool isDir = Directory.Exists(rPath);
            if (!isDir && !File.Exists(rPath)) return null;
            return new Entry(path, rPath, iPath, size, deleted, isDir);
        }
        catch { return null; }
    }

    public static List<FileItem> LoadItems() =>
        List().Select(e => new FileItem(e.RPath, Path.GetFileName(e.OriginalPath), e.IsDir, e.Size, e.Deleted, e.Deleted, FileAttributes.Normal)
        {
            Special = ItemSpecial.TrashEntry, Location = Path.GetDirectoryName(e.OriginalPath), ExtraPath = e.OriginalPath
        }).ToList();

    /// <summary>Devuelve un elemento a su ubicación original. Devuelve la ruta final.</summary>
    public static string Restore(string rPath)
    {
        var e = List().FirstOrDefault(x => string.Equals(x.RPath, rPath, StringComparison.OrdinalIgnoreCase));
        if (e == null) throw new FileNotFoundException("El elemento ya no está en la Papelera.");
        var target = e.OriginalPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target) || Directory.Exists(target))
            target = Faro.Core.Transfer.TransferJob.UniqueName(Path.GetDirectoryName(target), Path.GetFileName(target), e.IsDir, false);
        if (e.IsDir) Directory.Move(e.RPath, target); else File.Move(e.RPath, target);
        try { File.Delete(e.IPath); } catch { }
        return target;
    }

    public static void DeleteForever(string rPath)
    {
        var iPath = Path.Combine(Path.GetDirectoryName(rPath)!, "$I" + Path.GetFileName(rPath)[2..]);
        if (Directory.Exists(rPath)) Directory.Delete(rPath, true); else if (File.Exists(rPath)) File.Delete(rPath);
        try { File.Delete(iPath); } catch { }
    }

    /// <summary>Busca en la Papelera lo que se acaba de enviar (para Deshacer).</summary>
    public static List<string> FindRecentlyTrashed(IEnumerable<string> originals, DateTime since)
    {
        var set = new HashSet<string>(originals, StringComparer.OrdinalIgnoreCase);
        return List().Where(e => set.Contains(e.OriginalPath) && e.Deleted >= since.AddSeconds(-5))
            .OrderByDescending(e => e.Deleted).GroupBy(e => e.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First().RPath).ToList();
    }

    public static (long items, long bytes) Info() => Native.QueryRecycleBin();
}
