using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Faro.Services;

/// <summary>
/// Comprimidos: ver qué traen y descomprimirlos. ZIP (y sus parientes: apk, jar, nupkg…) con .NET;
/// 7z, rar, tar, gz y demás con 7-Zip si está instalado.
/// </summary>
public static class Archives
{
    public sealed record Entry(string Name, long Size, bool IsFolder);
    public sealed record Listing(List<Entry> Entries, int Total, long Bytes);

    private static readonly HashSet<string> ZipLike = new(StringComparer.OrdinalIgnoreCase)
        { "zip", "apk", "xapk", "aab", "ipa", "jar", "whl", "nupkg", "xpi" };
    private static readonly HashSet<string> SevenZipOnly = new(StringComparer.OrdinalIgnoreCase)
        { "7z", "rar", "tar", "gz", "tgz", "bz2", "tbz2", "xz", "txz", "zst", "lzma", "cab", "arj", "z", "deb", "rpm", "iso", "wim" };

    private static readonly Lazy<string> SevenZipPath = new(() =>
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"7-Zip\7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"7-Zip\7z.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    });

    public static bool HasSevenZip => SevenZipPath.Value != null;
    public static bool IsKnown(string ext) => ZipLike.Contains(ext) || SevenZipOnly.Contains(ext);
    public static bool CanOpen(string ext) => ZipLike.Contains(ext) || (HasSevenZip && SevenZipOnly.Contains(ext));

    /// <summary>Nombre de la carpeta de destino: «fotos.zip» → «fotos», «datos.tar.gz» → «datos».</summary>
    public static string FolderNameFor(string fileName)
    {
        var n = Path.GetFileNameWithoutExtension(fileName);
        if (n.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) && n.Length > 4) n = n[..^4];
        return n.Length == 0 ? "Comprimido" : n;
    }

    /// <summary>Lo que trae el comprimido (hasta <paramref name="max"/> entradas); null si no se pudo leer.</summary>
    public static Listing TryList(string path, int max)
    {
        try
        {
            var ext = Path.GetExtension(path).TrimStart('.');
            var entries = new List<Entry>();
            int total = 0; long bytes = 0;
            if (ZipLike.Contains(ext))
            {
                using var zip = ZipFile.OpenRead(path);
                foreach (var e in zip.Entries)
                {
                    bool folder = e.FullName.EndsWith('/');
                    total++; bytes += e.Length;
                    if (entries.Count < max) entries.Add(new Entry(e.FullName.TrimEnd('/').Replace('/', '\\'), e.Length, folder));
                }
                return new Listing(entries, total, bytes);
            }
            if (!HasSevenZip) return null;
            var (code, output, _) = Run($"l -slt -ba -sccUTF-8 \"{path}\"", 20000);
            if (code != 0) return null;
            string name = null; long size = 0; bool dir = false;
            void Flush()
            {
                if (name == null) return;
                total++; bytes += size;
                if (entries.Count < max) entries.Add(new Entry(name, size, dir));
                name = null; size = 0; dir = false;
            }
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("Path = ")) { Flush(); name = line[7..]; }
                else if (line.StartsWith("Size = ")) long.TryParse(line[7..], out size);
                else if (line.StartsWith("Folder = ")) dir = line.EndsWith('+');
                else if (line.StartsWith("Attributes = ") && line.Length > 13 && line[13] == 'D') dir = true;
            }
            Flush();
            return new Listing(entries, total, bytes);
        }
        catch { return null; }
    }

    /// <summary>Descomprime en <paramref name="target"/> (carpeta nueva). Lanza con el motivo si falla.</summary>
    public static void Extract(string path, string target)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        if (ZipLike.Contains(ext)) { ZipFile.ExtractToDirectory(path, target); return; }
        if (!HasSevenZip) throw new InvalidOperationException("Para este tipo de comprimido hace falta 7-Zip (7-zip.org).");
        SevenZipExtract(path, target);
        // .tar.gz, .tgz, .tar.xz…: dentro viene un .tar; se abre también y se quita
        var inside = Directory.GetFileSystemEntries(target);
        if (inside.Length == 1 && File.Exists(inside[0]) && inside[0].EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            SevenZipExtract(inside[0], target);
            try { File.Delete(inside[0]); } catch { }
        }
    }

    private static void SevenZipExtract(string path, string target)
    {
        var (code, output, error) = Run($"x -y -bd -sccUTF-8 \"-o{target}\" \"{path}\"", Timeout.Infinite);
        if (code == 0) return;
        var all = error + "\n" + output;
        if (all.Contains("password", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("El comprimido tiene contraseña.");
        var reason = all.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) ?? "7-Zip no pudo abrir el archivo.";
        throw new InvalidOperationException(reason);
    }

    private static (int code, string output, string error) Run(string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(SevenZipPath.Value, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Close(); // si pide contraseña, que falle en vez de quedarse esperando
        var err = p.StandardError.ReadToEndAsync();
        var outText = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "", "tiempo agotado"); }
        return (p.ExitCode, outText.Result, err.Result);
    }
}
