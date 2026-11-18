using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Faro.Shell;

/// <summary>
/// En pantalla completa, como el Dock de macOS: al llevar el ratón al borde donde está la barra de
/// tareas, se trae al frente; al volver al contenido, la ventana recupera el frente y la barra se esconde.
/// Funciona con la barra abajo, arriba o a un lado (se lee su posición real).
/// </summary>
public sealed class TaskbarReveal
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint msg; public uint edge; public RECT rc; public IntPtr lParam; }

    [DllImport("shell32.dll")] private static extern uint SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string cls, string name);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);

    private const uint ABM_GETTASKBARPOS = 5;
    private const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;

    private readonly IntPtr _window;
    private readonly Action _reactivate;
    private readonly DispatcherTimer _timer;
    private int _edgeTicks;
    private bool _shown;

    public TaskbarReveal(IntPtr window, Action reactivate)
    {
        _window = window;
        _reactivate = reactivate;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() { _edgeTicks = 0; _shown = false; _timer.Start(); }
    public void Stop() { _timer.Stop(); _shown = false; _edgeTicks = 0; }

    private RECT _full;

    private void Tick()
    {
        if (!GetCursorPos(out var p)) return;
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) == 0) return;
        var bar = abd.rc;

        if (!_shown)
        {
            GetWindowRect(_window, out var w);
            // ¿el ratón está pegado al borde de la pantalla donde vive la barra?
            bool atEdge = abd.edge switch
            {
                ABE_BOTTOM => p.Y >= w.B - 2 && p.X >= bar.L && p.X <= bar.R,
                ABE_TOP => p.Y <= w.T + 1 && p.X >= bar.L && p.X <= bar.R,
                ABE_LEFT => p.X <= w.L + 1 && p.Y >= bar.T && p.Y <= bar.B,
                ABE_RIGHT => p.X >= w.R - 2 && p.Y >= bar.T && p.Y <= bar.B,
                _ => false
            };
            // un instante de espera (como el Dock) para no sacarla por pasar el ratón de largo
            _edgeTicks = atEdge && GetForegroundWindow() == _window ? _edgeTicks + 1 : 0;
            if (_edgeTicks >= 3)
            {
                // Windows esconde la barra sólo mientras una ventana cubre la pantalla AL PÍXEL.
                // Encoger la ventana 1 px (en el lado contrario a la barra) hace que la barra salga encima.
                _full = w;
                int L = w.L, T = w.T, R = w.R, B = w.B;
                switch (abd.edge) { case ABE_BOTTOM: T += 1; break; case ABE_TOP: B -= 1; break; case ABE_LEFT: R -= 1; break; default: L += 1; break; }
                SetWindowPos(_window, IntPtr.Zero, L, T, R - L, B - T, 0x4 | 0x10 | 0x200);
                _shown = true;
            }
            return;
        }

        // visible: al volver al contenido (lejos de la barra), recuperar el píxel y la barra se esconde sola
        const int margin = 40;
        bool nearBar = p.X >= bar.L - margin && p.X <= bar.R + margin && p.Y >= bar.T - margin && p.Y <= bar.B + margin;
        if (!nearBar && GetForegroundWindow() == _window) Hide();
    }

    private void Hide()
    {
        if (!_shown) return;
        SetWindowPos(_window, IntPtr.Zero, _full.L, _full.T, _full.R - _full.L, _full.B - _full.T, 0x4 | 0x10 | 0x200);
        _shown = false;
        _edgeTicks = 0;
    }
}