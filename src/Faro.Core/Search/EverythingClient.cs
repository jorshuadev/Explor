using System.Runtime.InteropServices;
using System.Text;

namespace Faro.Core.Search;

/// <summary>
/// Cliente IPC de Everything (voidtools) sin DLL: habla con la ventana de Everything por
/// WM_COPYDATA. Si Everything está abierto, la búsqueda en todo el equipo es instantánea.
/// </summary>
public static class EverythingClient
{
    private const int WM_COPYDATA = 0x004A;
    private const int EVERYTHING_IPC_COPYDATAQUERYW = 2;
    private const uint ReplyId = 0x0FA20001;
    private static readonly string[] WindowClasses =
        { "EVERYTHING_TASKBAR_NOTIFICATION", "EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)" };

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    private delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string cls, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, ref COPYDATASTRUCT l, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);

    private static readonly object InitLock = new();
    private static IntPtr _hwnd;
    private static WndProc _proc; // referencia viva para el GC
    private static TaskCompletionSource<List<(string path, bool dir)>> _pending;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static IntPtr FindEverything()
    {
        foreach (var c in WindowClasses)
        {
            var h = FindWindowW(c, null);
            if (h != IntPtr.Zero) return h;
        }
        return IntPtr.Zero;
    }

    public static bool IsAvailable => FindEverything() != IntPtr.Zero;

    private static bool _launchTried;

    /// <summary>Si Everything está instalado pero su ventana no está abierta, lo arranca en segundo plano (bandeja).</summary>
    public static async Task<bool> EnsureRunningAsync()
    {
        if (IsAvailable) return true;
        if (_launchTried) return false;
        _launchTried = true;
        foreach (var exe in new[] { @"C:\Program Files\Everything\Everything.exe", @"C:\Program Files (x86)\Everything\Everything.exe",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Everything\Everything.exe") })
        {
            if (!File.Exists(exe)) continue;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-startup") { UseShellExecute = true }); }
            catch { return false; }
            for (int i = 0; i < 30 && !IsAvailable; i++) await Task.Delay(100);
            return IsAvailable;
        }
        return false;
    }

    private static void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero) return;
        lock (InitLock)
        {
            if (_hwnd != IntPtr.Zero) return;
            var ready = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                _proc = Proc;
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = _proc, hInstance = GetModuleHandleW(null),
                    lpszClassName = "FaroEverythingReply"
                };
                RegisterClassExW(ref wc);
                _hwnd = CreateWindowExW(0, "FaroEverythingReply", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                ready.Set();
                while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
            }) { IsBackground = true, Name = "Everything IPC" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            ready.Wait(3000);
        }
    }

    public static Action<string> Debug;
    private static IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        Debug?.Invoke($"msg 0x{msg:X}");
        if (msg == WM_COPYDATA)
        {
            var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(l);
            if ((uint)cds.dwData == ReplyId)
            {
                try { _pending?.TrySetResult(ParseList(cds.lpData)); }
                catch (Exception ex) { _pending?.TrySetException(ex); }
                return new IntPtr(1);
            }
        }
        return DefWindowProcW(h, msg, w, l);
    }

    private static List<(string, bool)> ParseList(IntPtr p)
    {
        // EVERYTHING_IPC_LISTW: 7 DWORD + items[numitems] de 3 DWORD
        int numItems = Marshal.ReadInt32(p, 20);
        var list = new List<(string, bool)>(numItems);
        for (int i = 0; i < numItems; i++)
        {
            int baseOff = 28 + i * 12;
            int flags = Marshal.ReadInt32(p, baseOff);
            int nameOff = Marshal.ReadInt32(p, baseOff + 4);
            int pathOff = Marshal.ReadInt32(p, baseOff + 8);
            var name = Marshal.PtrToStringUni(p + nameOff);
            var path = Marshal.PtrToStringUni(p + pathOff);
            bool isDir = (flags & 1) != 0 || (flags & 2) != 0;
            list.Add((string.IsNullOrEmpty(path) ? name : System.IO.Path.Combine(path, name), isDir));
        }
        return list;
    }

    /// <summary>Consulta con sintaxis de Everything. Devuelve null si Everything no está disponible.</summary>
    public static async Task<List<(string path, bool dir)>> QueryAsync(string search, int maxResults, CancellationToken ct)
    {
        await EnsureRunningAsync();
        var ev = FindEverything();
        if (ev == IntPtr.Zero) return null;
        EnsureWindow();
        if (_hwnd == IntPtr.Zero) return null;

        await Gate.WaitAsync(ct);
        try
        {
            _pending = new TaskCompletionSource<List<(string, bool)>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var strBytes = Encoding.Unicode.GetBytes(search + "\0");
            int size = 20 + strBytes.Length;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(buf, 0, (int)_hwnd.ToInt64());
                Marshal.WriteInt32(buf, 4, unchecked((int)ReplyId));
                Marshal.WriteInt32(buf, 8, 0);          // flags
                Marshal.WriteInt32(buf, 12, 0);         // offset
                Marshal.WriteInt32(buf, 16, maxResults);
                Marshal.Copy(strBytes, 0, buf + 20, strBytes.Length);
                var cds = new COPYDATASTRUCT { dwData = new IntPtr(EVERYTHING_IPC_COPYDATAQUERYW), cbData = size, lpData = buf };
                var ok = SendMessageTimeoutW(ev, WM_COPYDATA, _hwnd, ref cds, 0x0002, 3000, out var res);
                Debug?.Invoke($"send ok={ok} res={res} err={Marshal.GetLastWin32Error()} hwnd={_hwnd} ev={ev}");
                if (ok == IntPtr.Zero || res == IntPtr.Zero) return null;
            }
            finally { Marshal.FreeHGlobal(buf); }

            var done = await Task.WhenAny(_pending.Task, Task.Delay(8000, ct));
            if (done != _pending.Task) return null;
            return await _pending.Task;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Traduce una SearchQuery de Faro a la sintaxis de Everything.</summary>
    public static string Translate(SearchQuery q, string scopeFolder)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(scopeFolder))
            sb.Append('"').Append(scopeFolder.TrimEnd('\\')).Append("\\\" ");
        foreach (var t in q.Terms) sb.Append('"').Append(t.Replace("\"", "")).Append("\" ");
        if (q.Wildcard != null)
        {
            var raw = System.Text.RegularExpressions.Regex.Match(q.Raw, @"\S*[\*\?]\S*").Value;
            if (raw.Length > 0) sb.Append("wfn:").Append(raw).Append(' ');
        }
        if (q.Extensions.Count > 0) sb.Append("ext:").Append(string.Join(";", q.Extensions)).Append(' ');
        if (q.KindGroup == "carpeta") sb.Append("folder: ");
        else if (q.KindGroup != null)
        {
            var exts = FileKinds.ExtensionsInGroup(q.KindGroup);
            if (exts.Count > 0) sb.Append("ext:").Append(string.Join(";", exts)).Append(' ');
        }
        return sb.ToString().Trim();
    }
}
