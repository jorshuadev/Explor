using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Faro.Shell;

/// <summary>
/// Aloja los "controladores de vista previa" de Windows (los mismos del panel de vista previa
/// del Explorador): PDF, Word, Excel, PowerPoint, Illustrator/Acrobat, fuentes, etc.
/// </summary>
public sealed class PreviewHandlerHost : HwndHost
{
    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] uint TranslateAccelerator(IntPtr pmsg);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile { void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode); }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream { void Initialize(IStream stream, uint mode); }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attrs, bool create, IStream template, out IStream stream);

    private const string PreviewKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private IntPtr _hwnd;
    private IPreviewHandler _handler;
    private IStream _stream;
    private readonly string _path;

    public bool IsPreviewLoaded { get; private set; }

    public PreviewHandlerHost(string path) { _path = path; }

    public static Guid? FindHandler(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;
        string Read(string key)
        {
            using var k = Registry.ClassesRoot.OpenSubKey(key);
            return k?.GetValue(null) as string;
        }
        var clsid = Read($@"{ext}\ShellEx\{PreviewKey}") ?? Read($@"SystemFileAssociations\{ext}\ShellEx\{PreviewKey}");
        if (clsid == null)
        {
            var progId = Read(ext);
            if (progId != null) clsid = Read($@"{progId}\ShellEx\{PreviewKey}");
            if (clsid == null)
            {
                using var k = Registry.ClassesRoot.OpenSubKey(ext);
                var perceived = k?.GetValue("PerceivedType") as string;
                if (perceived != null) clsid = Read($@"SystemFileAssociations\{perceived}\ShellEx\{PreviewKey}");
            }
        }
        return Guid.TryParse(clsid, out var g) ? g : null;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowExW(0, "static", "", 0x40000000 | 0x10000000 | 0x02000000, 0, 0, 100, 100, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Dispatcher.BeginInvoke(Start, System.Windows.Threading.DispatcherPriority.Loaded);
        return new HandleRef(this, _hwnd);
    }

    private void Start()
    {
        try
        {
            var clsid = FindHandler(_path);
            if (clsid == null) return;
            var type = Type.GetTypeFromCLSID(clsid.Value);
            var obj = Activator.CreateInstance(type!);
            if (obj is IInitializeWithFile f) f.Initialize(_path, 0);
            else if (obj is IInitializeWithStream s)
            {
                if (SHCreateStreamOnFileEx(_path, 0x00000040 /*STGM_SHARE_DENY_NONE*/, 0, false, null, out _stream) != 0) return;
                s.Initialize(_stream, 0);
            }
            else return;
            _handler = (IPreviewHandler)obj;
            var r = CurrentRect();
            _handler.SetWindow(_hwnd, ref r);
            _handler.DoPreview();
            IsPreviewLoaded = true;
        }
        catch { _handler = null; }
    }

    private RECT CurrentRect()
    {
        var src = System.Windows.PresentationSource.FromVisual(this);
        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        return new RECT { Right = (int)(ActualWidth * sx), Bottom = (int)(ActualHeight * sy) };
    }

    protected override void OnRenderSizeChanged(System.Windows.SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_handler != null) { var r = CurrentRect(); try { _handler.SetRect(ref r); } catch { } }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        try { _handler?.Unload(); } catch { }
        if (_handler != null) try { Marshal.FinalReleaseComObject(_handler); } catch { }
        if (_stream != null) try { Marshal.FinalReleaseComObject(_stream); } catch { }
        _handler = null;
        DestroyWindow(hwnd.Handle);
    }
}
