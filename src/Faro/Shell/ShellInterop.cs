using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Faro.Shell;

/// <summary>Miniaturas, iconos, abrir/abrir con, accesos directos, expulsar y propiedades del sistema.</summary>
public static class ShellInterop
{
    // ================== Miniaturas (IShellItemImageFactory) ==================

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [Flags]
    public enum SIIGBF { ResizeToFit = 0, BiggerSizeOk = 1, MemoryOnly = 2, IconOnly = 4, ThumbnailOnly = 8, InCacheOnly = 0x10, CropToSquare = 0x20, ScaleUp = 0x100 }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public ushort biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    private static Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>Imagen del Shell (miniatura o icono) a tamaño px. Llamar desde un hilo STA de fondo.</summary>
    public static BitmapSource GetImage(string path, int px, SIIGBF flags)
    {
        IntPtr ppv = IntPtr.Zero, hbm = IntPtr.Zero;
        try
        {
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref IID_IShellItemImageFactory, out ppv) != 0 || ppv == IntPtr.Zero) return null;
            var f = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ppv);
            int hr = f.GetImage(new SIZE { cx = px, cy = px }, flags, out hbm);
            Marshal.ReleaseComObject(f);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            var bmp = FromHBitmap(hbm);
            // Iconos sin versión grande: Windows los devuelve chicos dentro de un recuadro.
            // En ese caso se pide el tamaño nativo (48) y se escala con suavizado, sin recuadro.
            if (bmp != null && (flags & SIIGBF.IconOnly) != 0 && px > 64 && IsFramedSmallIcon(bmp) && LargeIconFromSource(path, px) is BitmapSource big)
                bmp = big;
            else if (bmp != null && (flags & SIIGBF.IconOnly) != 0 && px > 64 && IsFramedSmallIcon(bmp))
            {
                DeleteObject(hbm); hbm = IntPtr.Zero;
                var f2 = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ppv);
                if (f2.GetImage(new SIZE { cx = 48, cy = 48 }, SIIGBF.IconOnly, out hbm) == 0 && hbm != IntPtr.Zero) bmp = FromHBitmap(hbm) ?? bmp;
                Marshal.ReleaseComObject(f2);
            }
            // Iconos que sólo traen 32/48 px: Windows los agranda repitiendo píxeles (en bloques) y se ven
            // pixelados. Se recupera el original pequeño y lo amplía WPF con suavizado.
            else if (bmp != null && (flags & SIIGBF.IconOnly) != 0 && px > 64 && UndoBlockyUpscale(bmp) is BitmapSource small)
            {
                // Con .url (Steam) y algunos .lnk Windows usa el fotograma de 48 aunque el icono traiga uno de 256:
                // primero se intenta leer el grande del archivo de origen; si no hay, el pequeño suavizado.
                bmp = LargeIconFromSource(path, px) ?? small;
            }
            return bmp;
        }
        catch { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (ppv != IntPtr.Zero) Marshal.Release(ppv);
        }
    }

    /// <summary>¿Es un icono pequeño centrado dentro de un marco (borde opaco y anillo interior transparente)?</summary>
    /// <summary>
    /// Si la imagen es un icono pequeño agrandado «a bloques» (vecino más próximo), devuelve el icono a su
    /// tamaño original; si no, null. Señal: en una ampliación así los cambios de color sólo ocurren en las
    /// costuras de una rejilla de n×n celdas; en un icono grande de verdad también ocurren dentro.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIconW(string file, int index, uint flags, out IntPtr large, out IntPtr small, uint sizes);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    /// <summary>
    /// Icono grande leído del origen de un acceso directo (IconFile del .url, IconLocation del .lnk o el
    /// propio destino): fotograma más grande de un .ico, o extracción a px de un .exe/.dll. Null si el
    /// origen no tiene nada mejor que 64 px.
    /// </summary>
    public static BitmapSource LargeIconFromSource(string shortcut, int px)
    {
        try
        {
            string file = null; int index = 0;
            var ext = Path.GetExtension(shortcut).ToLowerInvariant();
            if (ext == ".url")
            {
                foreach (var l in File.ReadLines(shortcut))
                {
                    if (l.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) file = l[9..].Trim();
                    else if (l.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase)) int.TryParse(l[10..].Trim(), out index);
                }
            }
            else if (ext == ".lnk")
            {
                var link = (IShellLinkW)new CShellLink();
                ((IPersistFile)link).Load(shortcut, 0);
                var sb = new StringBuilder(1024);
                link.GetIconLocation(sb, sb.Capacity, out index);
                file = sb.ToString();
                if (string.IsNullOrWhiteSpace(file)) { file = ResolveShortcut(shortcut); index = 0; }
            }
            else file = shortcut;
            if (string.IsNullOrWhiteSpace(file)) return null;
            file = Environment.ExpandEnvironmentVariables(file.Trim('"'));
            if (!File.Exists(file)) return null;

            BitmapSource best = null;
            if (file.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(file);
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                best = dec.Frames.OrderByDescending(f => f.PixelWidth).ThenByDescending(f => f.Format.BitsPerPixel).FirstOrDefault();
            }
            else if (SHDefExtractIconW(file, index, 0, out var hBig, out var hSmall, (uint)((16 << 16) | Math.Min(px, 256))) == 0 && hBig != IntPtr.Zero)
            {
                try { best = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hBig, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
                finally { DestroyIcon(hBig); if (hSmall != IntPtr.Zero) DestroyIcon(hSmall); }
            }
            if (best == null || best.PixelWidth <= 64) return null;
            var conv = new FormatConvertedBitmap(best, PixelFormats.Pbgra32, null, 0);
            var copy = new WriteableBitmap(conv);
            // si al extraerlo Windows también lo agrandó a bloques, no es mejor que el pequeño
            if (UndoBlockyUpscale(copy) != null) return null;
            copy.Freeze();
            return copy;
        }
        catch { return null; }
    }

    internal static BitmapSource UndoBlockyUpscale(BitmapSource b)
    {
        if (b.Format != PixelFormats.Pbgra32 && b.Format != PixelFormats.Bgr32) return null;
        int w = b.PixelWidth, h = b.PixelHeight;
        if (w != h || w < 96) return null;
        var px = new uint[w * h];
        b.CopyPixels(px, w * 4, 0);
        foreach (int n in new[] { 16, 24, 32, 48, 64 })
        {
            if (n * 2 > w) break;
            bool Seam(int x) => x * n / w != (x + 1) * n / w; // entre x y x+1 cambia de celda
            long inside = 0, total = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w - 1; x++)
                {
                    if (px[row + x] == px[row + x + 1]) continue;
                    total++;
                    if (!Seam(x)) inside++;
                }
            }
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h - 1; y++)
                {
                    if (px[y * w + x] == px[(y + 1) * w + x]) continue;
                    total++;
                    if (!Seam(y)) inside++;
                }
            // icono casi liso: no hay nada que corregir
            if (total < w) return null;
            if (inside > total * 0.02) continue;
            // es una ampliación de n×n: tomar el píxel central de cada celda
            var small = new uint[n * n];
            for (int cy = 0; cy < n; cy++)
                for (int cx = 0; cx < n; cx++)
                {
                    int sx = Math.Min(w - 1, (cx * w + w / 2) / n), sy = Math.Min(h - 1, (cy * h + h / 2) / n);
                    small[cy * n + cx] = px[sy * w + sx];
                }
            var res = BitmapSource.Create(n, n, 96, 96, b.Format, null, small, n * 4);
            res.Freeze();
            return res;
        }
        return null;
    }

    private static bool IsFramedSmallIcon(BitmapSource b)
    {
        if (b.Format != PixelFormats.Pbgra32) return false;
        int w = b.PixelWidth, h = b.PixelHeight;
        var px = new byte[w * h * 4];
        b.CopyPixels(px, w * 4, 0);
        bool Opaque(int x, int y) => px[(y * w + x) * 4 + 3] > 24; // el marco de Windows es semitransparente
        // caja que encierra lo opaco
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (Opaque(x, y)) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        if (x1 - x0 < w / 2 || y1 - y0 < h / 2) return false;
        int edge = 0, edgeOn = 0, ring = 0, ringOn = 0, inset = Math.Max(4, w / 16);
        for (int x = x0; x <= x1; x++) { edge += 2; if (Opaque(x, y0)) edgeOn++; if (Opaque(x, y1)) edgeOn++; }
        for (int x = x0 + inset; x <= x1 - inset; x++) { ring += 2; if (Opaque(x, y0 + inset)) ringOn++; if (Opaque(x, y1 - inset)) ringOn++; }
        return edgeOn > edge * 0.8 && ringOn < ring * 0.15;
    }

    private static BitmapSource FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        if (w <= 0 || h <= 0) return null;
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bits = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbm, 0, (uint)h, bits, ref bi, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        bool hasAlpha = false;
        for (int i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { hasAlpha = true; break; }
        var fmt = hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        var src = BitmapSource.Create(w, h, 96, 96, fmt, null, bits, w * 4);
        src.Freeze();
        return src;
    }

    // ================== Abrir ==================

    public static bool Open(string path, Window owner = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1155) // sin aplicación asociada
        {
            return OpenWith(path, owner);
        }
        catch (Exception ex)
        {
            Dialogs.MacDialog.Error(owner, $"No se puede abrir «{Path.GetFileName(path)}».", ex.Message);
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO { public string pcszFile; public string pcszClass; public int oaifInFlags; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHOpenWithDialog(IntPtr hwnd, ref OPENASINFO info);

    public static bool OpenWith(string path, Window owner = null)
    {
        var info = new OPENASINFO { pcszFile = path, oaifInFlags = 0x1 | 0x4 | 0x2 }; // ALLOW_REGISTRATION | EXEC | REGISTER_EXT
        var hwnd = owner != null ? new System.Windows.Interop.WindowInteropHelper(owner).Handle : IntPtr.Zero;
        return SHOpenWithDialog(hwnd, ref info) == 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize; public uint fMask; public IntPtr hwnd; public string lpVerb; public string lpFile; public string lpParameters;
        public string lpDirectory; public int nShow; public IntPtr hInstApp; public IntPtr lpIDList; public string lpClass; public IntPtr hkeyClass;
        public uint dwHotKey; public IntPtr hIcon; public IntPtr hProcess;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO i);

    /// <summary>Ventana de Propiedades de Windows (seguridad, compatibilidad, etc.).</summary>
    public static void ShowWindowsProperties(string path)
    {
        var i = new SHELLEXECUTEINFO { cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(), lpVerb = "properties", lpFile = path, nShow = 1, fMask = 0x0000000C };
        ShellExecuteExW(ref i);
    }

    public static void RevealInExplorer(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    public static void OpenTerminal(string folder)
    {
        try
        {
            var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
            if (File.Exists(wt)) Process.Start(new ProcessStartInfo(wt, $"-d \"{folder.TrimEnd('\\')}\\.\"") { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = true });
        }
        catch { }
    }

    // ================== Accesos directos (alias) ==================

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string rel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    /// <summary>Argumentos de un .lnk («"C:\scripts\x.ahk"» en un acceso directo que abre un script con AutoHotkey).</summary>
    public static string ShortcutArguments(string lnk)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(2048);
            link.GetArguments(sb, sb.Capacity);
            Marshal.ReleaseComObject(link);
            return Environment.ExpandEnvironmentVariables(sb.ToString());
        }
        catch { return ""; }
    }

    public static string ResolveShortcut(string lnk)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4); // SLGP_RAWPATH
            var p = Environment.ExpandEnvironmentVariables(sb.ToString());
            Marshal.ReleaseComObject(link);
            return string.IsNullOrEmpty(p) ? null : p;
        }
        catch { return null; }
    }

    public static void CreateShortcut(string target, string lnkPath)
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
        ((IPersistFile)link).Save(lnkPath, true);
        Marshal.ReleaseComObject(link);
    }

    // ================== Expulsar ==================

    public static bool Eject(string driveRoot)
    {
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(t);
            dynamic ns = shell.NameSpace(17); // Este equipo
            dynamic item = ns.ParseName(driveRoot.TrimEnd('\\') + "\\");
            if (item == null) return false;
            item.InvokeVerb("Eject");
            return true;
        }
        catch { return false; }
    }

    // ================== Propiedades del sistema (dimensiones, duración...) ==================

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY { public Guid fmtid; public uint pid; public PROPERTYKEY(string g, uint p) { fmtid = new Guid(g); pid = p; } }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint c);
        void GetAt(uint i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, IntPtr pv);
        void SetValue(ref PROPERTYKEY key, IntPtr pv);
        void Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr pbc, int flags, ref Guid riid, out IPropertyStore store);
    [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
    private static extern int PSFormatForDisplayAlloc(ref PROPERTYKEY key, IntPtr pv, int flags, out IntPtr str);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(IntPtr pv);

    public static readonly (string label, PROPERTYKEY key)[] InfoKeys =
    {
        ("Dimensiones", new("6444048F-4C8B-11D1-8B70-080036B11A03", 13)),
        ("Ancho", new("64440491-4C8B-11D1-8B70-080036B11A03", 3)),
        ("Alto", new("64440491-4C8B-11D1-8B70-080036B11A03", 4)),
        ("Duración", new("64440490-4C8B-11D1-8B70-080036B11A03", 3)),
        ("Fotogramas por segundo", new("64440491-4C8B-11D1-8B70-080036B11A03", 6)),
        ("Velocidad de datos", new("64440491-4C8B-11D1-8B70-080036B11A03", 2)),
        ("Tasa de bits", new("64440490-4C8B-11D1-8B70-080036B11A03", 4)),
        ("Canales", new("64440490-4C8B-11D1-8B70-080036B11A03", 7)),
        ("Frecuencia de muestreo", new("64440490-4C8B-11D1-8B70-080036B11A03", 5)),
        ("Resolución", new("6444048F-4C8B-11D1-8B70-080036B11A03", 5)),
        ("Profundidad de bits", new("6444048F-4C8B-11D1-8B70-080036B11A03", 7)),
        ("Fabricante de cámara", new("14B81DA1-0135-4D31-96D9-6CBFC9671A99", 271)),
        ("Modelo de cámara", new("14B81DA1-0135-4D31-96D9-6CBFC9671A99", 272)),
        ("Fecha de captura", new("14B81DA1-0135-4D31-96D9-6CBFC9671A99", 36867)),
        ("Distancia focal", new("14B81DA1-0135-4D31-96D9-6CBFC9671A99", 37386)),
        ("Apertura", new("14B81DA1-0135-4D31-96D9-6CBFC9671A99", 33437)),
        ("Exposición", new("14B81DA1-0135-4D31-96D9-6CBFC9671A99", 33434)),
        ("ISO", new("14B81DA1-0135-4D31-96D9-6CBFC9671A99", 34855)),
        ("Título", new("F29F85E0-4FF9-1068-AB91-08002B27B3D9", 2)),
        ("Autores", new("F29F85E0-4FF9-1068-AB91-08002B27B3D9", 4)),
        ("Artista", new("56A3372E-CE9C-11D2-9F0E-006097C686F6", 2)),
        ("Álbum", new("56A3372E-CE9C-11D2-9F0E-006097C686F6", 4)),
        ("Año", new("56A3372E-CE9C-11D2-9F0E-006097C686F6", 5)),
        ("Género", new("56A3372E-CE9C-11D2-9F0E-006097C686F6", 11)),
        ("Páginas", new("F29F85E0-4FF9-1068-AB91-08002B27B3D9", 14)),
        ("Aplicación", new("F29F85E0-4FF9-1068-AB91-08002B27B3D9", 18)),
        ("Descripción", new("0CEF7D53-FA64-11D1-A203-0000F81FEDEE", 3)),
        ("Versión", new("0CEF7D53-FA64-11D1-A203-0000F81FEDEE", 4)),
        ("Empresa", new("D5CDD502-2E9C-101B-9397-08002B2CF9AE", 15)),
        ("Copyright", new("64440492-4C8B-11D1-8B70-080036B11A03", 11)),
    };

    /// <summary>Metadatos legibles (lo que Finder pone en "Más información").</summary>
    public static List<(string label, string value)> GetInfo(string path)
    {
        var res = new List<(string, string)>();
        var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        IPropertyStore store = null;
        try
        {
            if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0x0 /*GPS_DEFAULT*/, ref iid, out store) != 0 || store == null) return res;
            var pv = Marshal.AllocCoTaskMem(24);
            try
            {
                bool hasDims = false;
                foreach (var (label, key0) in InfoKeys)
                {
                    var key = key0;
                    for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                    if (store.GetValue(ref key, pv) != 0) continue;
                    short vt = Marshal.ReadInt16(pv);
                    if (vt == 0) continue;
                    if (PSFormatForDisplayAlloc(ref key, pv, 0, out var s) == 0 && s != IntPtr.Zero)
                    {
                        var text = Marshal.PtrToStringUni(s)?.Trim('‎', '‏', ' ');
                        Marshal.FreeCoTaskMem(s);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (label == "Dimensiones") hasDims = true;
                            if ((label == "Ancho" || label == "Alto") && hasDims) { PropVariantClear(pv); continue; }
                            res.Add((label, text));
                        }
                    }
                    PropVariantClear(pv);
                }
            }
            finally { Marshal.FreeCoTaskMem(pv); }
        }
        catch { }
        finally { if (store != null) Marshal.ReleaseComObject(store); }
        return res;
    }

    // ================== Iconos de unidades / tipo genérico ==================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint dwAttributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfoW(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    /// <summary>Nombre de tipo que da Windows (para extensiones que Faro no conoce).</summary>
    public static string ShellTypeName(string path)
    {
        var info = new SHFILEINFO();
        SHGetFileInfoW(path, 0x80, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x400 | 0x10); // TYPENAME | USEFILEATTRIBUTES
        return info.szTypeName;
    }
}
