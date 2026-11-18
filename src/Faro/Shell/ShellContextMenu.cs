using System.Runtime.InteropServices;
using System.Text;

namespace Faro.Shell;

/// <summary>
/// Menú contextual nativo de Windows («Mostrar más opciones»): así funcionan 7-Zip, WinRAR,
/// «Abrir con Code», «Enviar a», antivirus, etc. sin que Faro tenga que conocerlos.
/// </summary>
public static class ShellContextMenu
{
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, ref uint eaten, out IntPtr pidl, ref uint attrs);
        void EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr reserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idcmd, uint uflags, IntPtr reserved, StringBuilder name, int cch);
    }

    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idcmd, uint uflags, IntPtr reserved, StringBuilder name, int cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr w, IntPtr l);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr w, IntPtr l, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize; public uint fMask; public IntPtr hwnd; public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string lpDirectory;
        public int nShow; public uint dwHotKey; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpTitleW;
        public POINT ptInvoke;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")] private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr p);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr h);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr h, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    private static IContextMenu3 _active3;

    /// <summary>Lo llama la ventana para que funcionen los submenús (Enviar a, Abrir con...).</summary>
    public static bool HandleMessage(int msg, IntPtr w, IntPtr l, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (_active3 == null) return false;
        // WM_INITMENUPOPUP, WM_DRAWITEM, WM_MEASUREITEM, WM_MENUCHAR
        if (msg is 0x0117 or 0x002B or 0x002C or 0x0120)
        {
            try { return _active3.HandleMenuMsg2((uint)msg, w, l, out result) == 0; } catch { }
        }
        return false;
    }

    public static void Show(IntPtr hwnd, IList<string> paths, bool extended)
    {
        if (paths.Count == 0) return;
        var pidls = new List<IntPtr>();
        IntPtr parentPtr = IntPtr.Zero, menuPtr = IntPtr.Zero, hMenu = IntPtr.Zero;
        try
        {
            foreach (var p in paths)
            {
                if (SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0) pidls.Add(pidl);
            }
            if (pidls.Count == 0) return;
            var iidSF = new Guid("000214E6-0000-0000-C000-000000000046");
            var children = new IntPtr[pidls.Count];
            for (int i = 0; i < pidls.Count; i++)
            {
                if (SHBindToParent(pidls[i], ref iidSF, out var pp, out var last) != 0) return;
                if (i == 0) parentPtr = pp; else Marshal.Release(pp);
                children[i] = last;
            }
            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
            var iidCM = new Guid("000214e4-0000-0000-c000-000000000046");
            if (folder.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidCM, IntPtr.Zero, out menuPtr) != 0) return;
            var cm = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
            try { _active3 = (IContextMenu3)Marshal.GetObjectForIUnknown(menuPtr); } catch { _active3 = null; }

            hMenu = CreatePopupMenu();
            const uint first = 1, lastId = 0x7FFF;
            uint flags = 0x0 /*CMF_NORMAL*/ | 0x4 /*CMF_EXPLORE*/ | (extended ? 0x100u : 0) /*CMF_EXTENDEDVERBS*/;
            cm.QueryContextMenu(hMenu, 0, first, lastId, flags);
            GetCursorPos(out var pt);
            int cmd = TrackPopupMenuEx(hMenu, 0x0100 /*TPM_RETURNCMD*/ | 0x0002 /*TPM_RIGHTBUTTON*/, pt.X, pt.Y, hwnd, IntPtr.Zero);
            if (cmd > 0)
            {
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(), fMask = 0x4000 /*UNICODE*/ | 0x20000000 /*PTINVOKE*/,
                    hwnd = hwnd, lpVerb = (IntPtr)(cmd - first), lpVerbW = (IntPtr)(cmd - first), nShow = 1, ptInvoke = pt,
                    lpDirectoryW = Path.GetDirectoryName(paths[0])
                };
                cm.InvokeCommand(ref info);
            }
        }
        catch { }
        finally
        {
            _active3 = null;
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
            foreach (var p in pidls) CoTaskMemFree(p);
        }
    }
}
