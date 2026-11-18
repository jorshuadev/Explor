using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Faro.Shell;

/// <summary>
/// Transición de tamaño de ventana estilo macOS hecha por el compositor de Windows (DWM):
/// una ventanita auxiliar en su propio hilo muestra una miniatura EN VIVO de la ventana real
/// y la anima del rectángulo viejo al nuevo, sincronizada con cada refresco (DwmFlush).
/// La ventana real se oculta ("cloak") mientras tanto y se recoloca sin que se vea.
/// Nada de esto pasa por WPF, así que no se bloquea aunque el diseño tarde.
/// </summary>
public static class WindowZoom
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    private struct THUMBPROPS { public int dwFlags; public RECT dest; public RECT src; public byte opacity; public int visible; public int clientOnly; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr h; public uint m; public IntPtr w, l; public uint t; public int x, y; }
    private delegate IntPtr WndProcD(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS { public uint style; public WndProcD proc; public int ce, we; public IntPtr inst, icon, cursor, bg; public string menu, cls; }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref THUMBPROPS p);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr lp);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG m, IntPtr h, uint a, uint b, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string n);

    [StructLayout(LayoutKind.Sequential)] private struct PAINTSTRUCT { public IntPtr hdc; public int erase; public RECT rc; public int restore, incUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] reserved; }
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, int key, byte alpha, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);

    // imagen congelada que pinta la capa mientras la ventana real se reajusta
    private static IntPtr _frozen;
    private static int _frozenW, _frozenH;

    private static IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (_frozen != IntPtr.Zero)
        {
            if (m == 0x0014) return new IntPtr(1); // WM_ERASEBKGND
            if (m == 0x000F) // WM_PAINT
            {
                var dc = BeginPaint(h, out var ps);
                var mem = CreateCompatibleDC(dc);
                var old = SelectObject(mem, _frozen);
                BitBlt(dc, 0, 0, _frozenW, _frozenH, mem, 0, 0, 0x00CC0020);
                SelectObject(mem, old); DeleteDC(mem);
                EndPaint(h, ref ps);
                return IntPtr.Zero;
            }
        }
        return DefWindowProcW(h, m, w, l);
    }

    private static WndProcD _proc;
    private static int _newReady;
    private static bool _registered;

    public static void Cloak(IntPtr hwnd, bool on) { int v = on ? 1 : 0; DwmSetWindowAttribute(hwnd, 13, ref v, 4); }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);

    /// <summary>
    /// 1) Foto de la ventana (PrintWindow) y capa encima con esa foto. 2) La ventana real se oculta
    /// y se redimensiona YA, en paralelo. 3) La capa anima la foto del rectángulo viejo al nuevo,
    /// a ritmo del compositor. 4) Cuando la real está pintada, se muestra debajo y la capa se funde.
    /// </summary>
    public static bool Run(IntPtr hwnd, RECT from, RECT to, Action applyResize, System.Windows.Threading.Dispatcher ui, int durationMs = 360, bool roundFrom = true, bool roundTo = true)
    {
        int fw = from.R - from.L, fh = from.B - from.T;
        // foto de la ventana tal como está
        var sdc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(sdc);
        var shot = CreateCompatibleBitmap(sdc, fw, fh);
        var prev = SelectObject(memDc, shot);
        bool printed = PrintWindow(hwnd, memDc, 2 /*PW_RENDERFULLCONTENT*/);
        SelectObject(memDc, prev); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, sdc);
        if (!printed) { DeleteObject(shot); applyResize(); return false; }
        _frozen = shot; _frozenW = fw; _frozenH = fh;

        var ready = new ManualResetEventSlim();
        bool ok = false;
        var t = new Thread(() =>
        {
            IntPtr overlay = IntPtr.Zero, src = IntPtr.Zero, thumb = IntPtr.Zero;
            try
            {
                if (!_registered)
                {
                    _proc = Proc;
                    var wc = new WNDCLASS { proc = _proc, inst = GetModuleHandleW(null), cls = "ExplorZoomOverlay", bg = CreateSolidBrush(0x1E1E1E) };
                    RegisterClassW(ref wc);
                    _registered = true;
                }
                // ventana fuente (oculta con "cloak", pero el compositor la sigue dibujando) con la foto a tamaño real
                src = CreateWindowExW(0x80 | 0x08000000, "ExplorZoomOverlay", "", 0x80000000, from.L, from.T, fw, fh, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
                int cloak = 1; DwmSetWindowAttribute(src, 13, ref cloak, 4);
                ShowWindow(src, 4); UpdateWindow(src);
                // capa visible: muestra la foto escalada por el compositor (GPU, con suavizado)
                overlay = CreateWindowExW(0x80 | 0x8 | 0x08000000 | 0x20 | 0x80000, "ExplorZoomOverlay", "", 0x80000000,
                    from.L, from.T, fw, fh, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
                if (src == IntPtr.Zero || overlay == IntPtr.Zero || DwmRegisterThumbnail(overlay, src, out thumb) != 0) { ready.Set(); return; }
                SetLayeredWindowAttributes(overlay, 0, 255, 2);
                int corner = roundFrom ? 2 : 1;
                DwmSetWindowAttribute(overlay, 33, ref corner, 4);
                Update(thumb, fw, fh, fw, fh, 255);
                ShowWindow(overlay, 4);
                DwmFlush();
                ok = true;
                ready.Set();

                // Regla: se REDUCE siempre la imagen grande, nunca se amplía la pequeña (ampliar pixela).
                //  • Creciendo (entrar en pantalla completa): se espera a que la ventana real esté dibujada a
                //    su tamaño final y se anima ESA imagen (nítida) desde el rectángulo pequeño hasta el grande.
                //  • Encogiendo (salir): se anima la foto de la ventana grande hacia el rectángulo pequeño; al
                //    llegar, se cambia a la ventana real.
                IntPtr thumbNew = IntPtr.Zero;
                int tw = to.R - to.L, th = to.B - to.T;
                bool growing = (long)tw * th > (long)fw * fh;

                bool NewSurfaceReady()
                {
                    // «lista» + unos fotogramas: WPF termina de dibujar en su propio hilo
                    var wait = Stopwatch.StartNew();
                    while (Volatile.Read(ref _newReady) == 0 && wait.ElapsedMilliseconds < 1500) { Pump(); DwmFlush(); }
                    for (int i = 0; i < 4; i++) { Pump(); DwmFlush(); }
                    return DwmRegisterThumbnail(overlay, hwnd, out thumbNew) == 0;
                }

                if (growing && NewSurfaceReady())
                {
                    // la imagen grande (nítida) reducida al rectángulo de partida, encima de la foto
                    Update(thumbNew, fw, fh, tw, th, 255);
                    Update(thumb, fw, fh, fw, fh, 0);
                    DwmFlush();
                }

                var sw = Stopwatch.StartNew();
                while (true)
                {
                    double p = Math.Min(1, sw.Elapsed.TotalMilliseconds / durationMs);
                    double k = 1 - Math.Pow(1 - p, 3.2); // arranque decidido, frenado suave
                    int L = (int)Math.Round(from.L + (to.L - from.L) * k), T = (int)Math.Round(from.T + (to.T - from.T) * k);
                    int R = (int)Math.Round(from.R + (to.R - from.R) * k), B = (int)Math.Round(from.B + (to.B - from.B) * k);
                    SetWindowPos(overlay, new IntPtr(-1), L, T, R - L, B - T, 0x10);
                    if (thumbNew != IntPtr.Zero) Update(thumbNew, R - L, B - T, tw, th, 255);
                    else Update(thumb, R - L, B - T, fw, fh, 255);
                    Pump();
                    DwmFlush();
                    if (p >= 1) break;
                }
                if (thumbNew == IntPtr.Zero && NewSurfaceReady())
                {
                    // encogiendo: ya en su sitio, la ventana real sustituye a la foto (mismo tamaño, sin escalar)
                    Update(thumbNew, tw, th, tw, th, 255);
                    Update(thumb, tw, th, fw, fh, 0);
                    DwmFlush();
                }                int cornerTo = roundTo ? 2 : 1;
                DwmSetWindowAttribute(overlay, 33, ref cornerTo, 4);
                DwmFlush();
                // la capa muestra ya exactamente la ventana real: se descubre la real y se retira la capa
                ui.Invoke(() => Cloak(hwnd, false));
                for (int i = 0; i < 2; i++) { Pump(); DwmFlush(); }
                if (thumbNew != IntPtr.Zero) DwmUnregisterThumbnail(thumbNew);
            }            catch { ready.Set(); }
            finally
            {
                if (thumb != IntPtr.Zero) DwmUnregisterThumbnail(thumb);
                if (overlay != IntPtr.Zero) DestroyWindow(overlay);
                if (src != IntPtr.Zero) DestroyWindow(src);
                if (_frozen != IntPtr.Zero) { DeleteObject(_frozen); _frozen = IntPtr.Zero; }
                if (!ok) { try { ui.Invoke(() => Cloak(hwnd, false)); } catch { } }
            }
        }) { IsBackground = true, Name = "Transición de ventana" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait(1000);
        if (!ok) { applyResize(); return false; }
        Cloak(hwnd, true);
        Volatile.Write(ref _newReady, 0);
        applyResize(); // en paralelo con la animación
        // avisar a la animación cuando la ventana ya está maquetada y dibujada a su tamaño nuevo
        ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
                ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => { Volatile.Write(ref _newReady, 1); }))))));
        return true;
    }
    private static void Pump()
    {
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
    }

    private static void Update(IntPtr thumb, int w, int h, int srcW, int srcH, byte opacity)
    {
        var p = new THUMBPROPS
        {
            dwFlags = 1 | 2 | 4 | 8 | 0x10, dest = new RECT { L = 0, T = 0, R = w, B = h }, src = new RECT { L = 0, T = 0, R = srcW, B = srcH },
            opacity = opacity, visible = 1, clientOnly = 0
        };
        DwmUpdateThumbnailProperties(thumb, ref p);
    }
}
