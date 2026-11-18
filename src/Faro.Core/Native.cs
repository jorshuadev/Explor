using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Faro.Core;

/// <summary>Llamadas Win32 que necesita el núcleo (sin dependencias de UI).</summary>
public static class Native
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int StrCmpLogicalW(string a, string b);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlushFileBuffers(SafeFileHandle handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformationW(string rootPath, StringBuilder volumeName, int volumeNameSize,
        out uint serialNumber, out uint maxComponentLength, out uint flags, StringBuilder fsName, int fsNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathNameW(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle dev, uint code, ref StoragePropertyQuery inBuf, int inSize,
        out DeviceSeekPenaltyDescriptor outBuf, int outSize, out int returned, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery { public uint PropertyId; public uint QueryType; public byte Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor { public uint Version; public uint Size; [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string device, StringBuilder target, int max);

    /// <summary>true si la letra es un SUBST (alias de una carpeta), no un volumen real.</summary>
    public static bool IsSubst(string root)
    {
        if (root.Length < 2 || root[1] != ':') return false;
        var sb = new StringBuilder(1024);
        return QueryDosDeviceW(root[..2], sb, sb.Capacity) != 0 && sb.ToString().StartsWith(@"\??\");
    }

    public const uint DRIVE_REMOVABLE = 2, DRIVE_FIXED = 3, DRIVE_REMOTE = 4, DRIVE_CDROM = 5;

    /// <summary>Raíz de volumen real de una ruta (resuelve puntos de montaje). Con barra final.</summary>
    public static string VolumeRoot(string path)
    {
        try
        {
            var letterRoot = Path.GetPathRoot(path);
            if (letterRoot is { Length: >= 2 } && letterRoot[1] == ':' && IsSubst(letterRoot))
                return letterRoot.EndsWith('\\') ? letterRoot : letterRoot + "\\";
            var sb = new StringBuilder(1024);
            if (GetVolumePathNameW(path, sb, sb.Capacity)) return sb.ToString();
        }
        catch { }
        var r = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(r) ? path : (r.EndsWith('\\') ? r : r + "\\");
    }

    public static bool TryGetVolumeInfo(string root, out uint serial, out string label, out string fileSystem)
    {
        var name = new StringBuilder(261); var fs = new StringBuilder(261);
        uint old = SetErrorMode(0x0001 | 0x8000); // sin cuadros de "inserte un disco"
        try
        {
            if (GetVolumeInformationW(root, name, name.Capacity, out serial, out _, out _, fs, fs.Capacity))
            { label = name.ToString(); fileSystem = fs.ToString(); return true; }
        }
        finally { SetErrorMode(old); }
        serial = 0; label = ""; fileSystem = "";
        return false;
    }

    /// <summary>true si el disco de esa letra es rotacional (HDD). null si no se pudo saber.</summary>
    public static bool? HasSeekPenalty(string root)
    {
        try
        {
            if (root.StartsWith(@"\\") && !root.StartsWith(@"\\?\")) return null; // red
            var letter = root.TrimEnd('\\');
            using var h = CreateFileW(@"\\.\" + letter, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var q = new StoragePropertyQuery { PropertyId = 7, QueryType = 0 };
            if (DeviceIoControl(h, 0x2D1400, ref q, Marshal.SizeOf<StoragePropertyQuery>(), out var d,
                    Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(), out _, IntPtr.Zero))
                return d.IncursSeekPenalty;
        }
        catch { }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle dev, uint code, ref StoragePropertyQuery inBuf, int inSize,
        byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    /// <summary>true si el volumen está en un disco USB / tarjeta SD (aunque Windows lo diga "fijo").</summary>
    public static bool IsExternalBus(string root)
    {
        try
        {
            if (root.StartsWith(@"\\")) return false;
            using var h = CreateFileW(@"\\.\" + root.TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) return false;
            var q = new StoragePropertyQuery { PropertyId = 0, QueryType = 0 };
            var buf = new byte[1024];
            if (!DeviceIoControl(h, 0x2D1400, ref q, Marshal.SizeOf<StoragePropertyQuery>(), buf, buf.Length, out _, IntPtr.Zero)) return false;
            int bus = BitConverter.ToInt32(buf, 28);
            return bus is 7 or 4 or 0xC or 0xD; // USB, 1394, SD, MMC
        }
        catch { return false; }
    }

    // --- Papelera (SHFileOperation) ---
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public ushort fFlags; [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings; [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_NOCONFIRMMKDIR = 0x200;

    /// <summary>Envía rutas a la Papelera de reciclaje. Devuelve 0 si todo bien.</summary>
    public static int SendToRecycleBin(IEnumerable<string> paths)
    {
        var from = string.Join("\0", paths) + "\0\0";
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE, pFrom = from,
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR)
        };
        return SHFileOperationW(ref op);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string root, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string root, uint flags);

    public static (long items, long bytes) QueryRecycleBin()
    {
        var i = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        if (SHQueryRecycleBinW(null, ref i) == 0) return (i.i64NumItems, i.i64Size);
        return (0, 0);
    }

    public static void EmptyRecycleBin() => SHEmptyRecycleBinW(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);
}

public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    public int Compare(string a, string b) => Native.StrCmpLogicalW(a ?? "", b ?? "");
}
