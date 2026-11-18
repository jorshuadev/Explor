using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using Faro.Model;

namespace Faro.Services;

/// <summary>
/// Carpetas compartidas: \\servidor (lista sus recursos), conectar pidiendo usuario y contraseña,
/// equipos encontrados en la red y espacio libre de un recurso. Todo lo que toca la red va fuera
/// del hilo de la ventana: un servidor que no responde tarda hasta 20 s en dar error.
/// </summary>
public static class NetworkService
{
    // ---------------- rutas ----------------

    public static bool IsUnc(string p) => p != null && p.StartsWith(@"\\") && !p.StartsWith(@"\\?\") && !p.StartsWith(@"\\.\");

    /// <summary>\\servidor (sin recurso compartido).</summary>
    public static bool IsServerRoot(string p) => IsUnc(p) && p.TrimEnd('\\').IndexOf('\\', 2) < 0 && p.TrimEnd('\\').Length > 2;

    /// <summary>\\servidor\recurso (la raíz de una carpeta compartida).</summary>
    public static bool IsShareRoot(string p)
    {
        if (!IsUnc(p)) return false;
        var t = p.TrimEnd('\\');
        int i = t.IndexOf('\\', 2);
        return i > 0 && t.IndexOf('\\', i + 1) < 0;
    }

    public static string ServerOf(string p)
    {
        if (!IsUnc(p)) return null;
        var t = p.TrimEnd('\\');
        int i = t.IndexOf('\\', 2);
        return i < 0 ? t : t[..i];
    }

    public static string HostOf(string p) => ServerOf(p)?[2..];

    /// <summary>
    /// Lo que se escribe para llegar a un servidor: «192.168.1.10», «\\192.168.1.10\Compartida»,
    /// «smb://servidor/recurso», «//servidor». Devuelve una ruta \\… o null si no parece de red.
    /// </summary>
    public static string NormalizeInput(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return null;
        t = t.Trim().Trim('"');
        bool smb = t.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("cifs://", StringComparison.OrdinalIgnoreCase);
        if (smb) t = "//" + Uri.UnescapeDataString(t[(t.IndexOf("://") + 3)..].TrimStart('/'));
        t = t.Replace('/', '\\');
        if (t.StartsWith(@"\\")) return t.Length > 2 ? t.TrimEnd('\\') : null;
        // una IP suelta: 192.168.1.10 o 192.168.1.10\Compartida
        var host = t.Split('\\')[0];
        if (host.Count(c => c == '.') == 3 && IPAddress.TryParse(host, out _))
            return @"\\" + t.TrimEnd('\\');
        return null;
    }

    public static string DisplayName(string p)
    {
        if (IsServerRoot(p)) return HostOf(p);
        if (IsShareRoot(p)) return p.TrimEnd('\\')[(p.TrimEnd('\\').LastIndexOf('\\') + 1)..];
        return null;
    }

    // ---------------- recursos compartidos de un servidor ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1 { public string netname; public uint type; public string remark; }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string server, int level, out IntPtr buf, int prefMaxLen, out int entriesRead, out int totalEntries, ref int resume);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buf);

    /// <summary>¿Responde el puerto de carpetas compartidas (445)? Sirve para no esperar 20 s a un servidor apagado.</summary>
    public static bool Reachable(string host, int timeoutMs = 1500)
    {
        try
        {
            using var c = new TcpClient();
            var t = Observe(c.ConnectAsync(host, 445));
            return t.Wait(timeoutMs) && c.Connected;
        }
        catch { return false; }
    }

    /// <summary>Recursos de disco de \\servidor (sin los ocultos «$»). Lanza UnauthorizedAccessException si pide credenciales.</summary>
    public static List<string> ListShares(string server)
    {
        var host = HostOf(server);
        if (!Reachable(host)) throw new IOException($"«{host}» no responde. Comprueba que estás en la misma red (o la VPN) y que el equipo está encendido.");
        int resume = 0;
        int r = NetShareEnum(@"\\" + host, 1, out var buf, -1, out int read, out _, ref resume);
        try
        {
            if (r is 5 or 1326 or 86 or 1244 or 1272 or 2221) throw new UnauthorizedAccessException($"«{host}» pide usuario y contraseña.");
            if (r != 0 && r != 234) throw new IOException($"No se pudo leer la lista de carpetas de «{host}» (error {r}).");
            var list = new List<string>();
            int size = Marshal.SizeOf<SHARE_INFO_1>();
            for (int i = 0; i < read; i++)
            {
                var s = Marshal.PtrToStructure<SHARE_INFO_1>(buf + i * size);
                if ((s.type & 0xFFFF) != 0 || (s.type & 0x80000000) != 0 || s.netname.EndsWith('$')) continue; // sólo carpetas, sin las administrativas
                list.Add(s.netname);
            }
            return list;
        }
        finally { if (buf != IntPtr.Zero) NetApiBufferFree(buf); }
    }

    /// <summary>Un intento de conexión que vence por tiempo falla después, al cerrarse: que no acabe en errores.log.</summary>
    private static Task Observe(Task t)
    {
        t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return t;
    }

    public static List<FileItem> LoadShares(string server)
    {
        server = ServerOf(server);
        return ListShares(server).Select(n => new FileItem(server + "\\" + n, n, true, -1, DateTime.MinValue, DateTime.MinValue, FileAttributes.Directory)).ToList();
    }

    // ---------------- conectar con usuario y contraseña ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class NETRESOURCE
    {
        public int dwScope, dwType, dwDisplayType, dwUsage;
        public string lpLocalName, lpRemoteName, lpComment, lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection3W(IntPtr hwnd, NETRESOURCE res, string password, string user, int flags);

    public static bool IsLoginError(Exception ex) => ex is UnauthorizedAccessException
        || (ex is IOException && (ex.HResult & 0xFFFF) is 5 or 1326 or 86 or 1244 or 1272 or 1909 or 1331 or 1907);

    /// <summary>
    /// Pide a Windows que conecte con el recurso mostrando su ventana de usuario y contraseña (con
    /// «Recordar mis credenciales»). Para \\servidor solo se conecta a IPC$, que es lo que deja listar.
    /// Devuelve true si quedó conectado; false si se canceló o falló (mensaje en <paramref name="error"/>).
    /// </summary>
    public static bool ConnectInteractive(string path, IntPtr hwnd, out string error, bool forcePrompt = false)
    {
        error = null;
        var remote = IsServerRoot(path) ? ServerOf(path) + @"\IPC$" : ShareRootOf(path);
        var res = new NETRESOURCE { dwType = IsServerRoot(path) ? 0 : 1, lpRemoteName = remote };
        const int CONNECT_INTERACTIVE = 0x8, CONNECT_PROMPT = 0x10;
        int r = WNetAddConnection3W(hwnd, res, null, null, CONNECT_INTERACTIVE | (forcePrompt ? CONNECT_PROMPT : 0));
        if (r is 0 or 1219) return true; // 1219: ya hay una conexión con otras credenciales y sirve
        if (r == 1223) return false;     // canceló
        error = r switch
        {
            1326 or 86 => "El usuario o la contraseña no son correctos.",
            53 or 67 or 1231 => $"No se encuentra «{HostOf(path)}» en la red.",
            1272 => "Windows bloquea el acceso de invitado a este servidor: hay que entrar con un usuario del servidor.",
            _ => new System.ComponentModel.Win32Exception(r).Message
        };
        return false;
    }

    public static string ShareRootOf(string p)
    {
        if (!IsUnc(p)) return null;
        var parts = p.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? @"\\" + parts[0] + "\\" + parts[1] : @"\\" + parts[0];
    }

    // ---------------- espacio libre ----------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string dir, out ulong freeForUser, out ulong total, out ulong totalFree);

    /// <summary>Libre/total de la unidad o carpeta compartida (también \\servidor\recurso). Puede tardar en red.</summary>
    public static (long free, long total)? Space(string path)
    {
        try
        {
            var root = IsUnc(path) ? ShareRootOf(path) + "\\" : Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || IsServerRoot(root)) return null;
            return GetDiskFreeSpaceExW(root, out var free, out var total, out _) ? ((long)free, (long)total) : null;
        }
        catch { return null; }
    }

    // ---------------- servidores recordados ----------------

    public static void Remember(string path)
    {
        var server = ServerOf(path);
        if (server == null) return;
        var s = Settings.Current;
        s.Servers.RemoveAll(x => string.Equals(x, server, StringComparison.OrdinalIgnoreCase));
        s.Servers.Insert(0, server);
        if (s.Servers.Count > 12) s.Servers.RemoveRange(12, s.Servers.Count - 12);
        s.Save();
    }

    public static void Forget(string server)
    {
        Settings.Current.Servers.RemoveAll(x => string.Equals(x, server, StringComparison.OrdinalIgnoreCase));
        Settings.Current.Save();
    }

    // ---------------- equipos de la red ----------------

    public sealed record Computer(string Path, string Name, string Address);

    private static List<Computer> _found = new();
    private static DateTime _lastScan = DateTime.MinValue;
    private static bool _scanning;
    /// <summary>Se lanza (en el hilo de la ventana) cuando cambia la lista de equipos encontrados.</summary>
    public static event Action Discovered;
    public static bool Scanning => _scanning;
    public static IReadOnlyList<Computer> Found => _found;

    /// <summary>Lo que se muestra en «Red»: servidores recordados + equipos encontrados.</summary>
    public static List<FileItem> LoadNetwork()
    {
        StartDiscovery();
        var list = new List<FileItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var srv in Settings.Current.Servers.ToList())
            if (seen.Add(srv)) list.Add(ServerItem(srv, HostOf(srv), "Servidor recordado"));
        foreach (var c in _found)
            if (seen.Add(c.Path) && (c.Address == null || seen.Add(@"\\" + c.Address)))
                list.Add(ServerItem(c.Path, c.Name, c.Address != null && c.Address != c.Name ? c.Address : "Equipo de la red"));
        return list;
    }

    private static FileItem ServerItem(string path, string name, string kind) =>
        new(path, name, true, -1, DateTime.MinValue, DateTime.MinValue, FileAttributes.Directory) { Special = ItemSpecial.Server, Location = kind };

    /// <summary>Busca equipos en segundo plano (como mucho una vez cada 30 s).</summary>
    public static void StartDiscovery(bool force = false)
    {
        if (_scanning || (!force && DateTime.UtcNow - _lastScan < TimeSpan.FromSeconds(30))) return;
        _scanning = true;
        var disp = Application.Current?.Dispatcher;
        Task.Run(async () =>
        {
            List<Computer> res;
            try { res = await DiscoverAsync(); }
            catch { res = new(); }
            _lastScan = DateTime.UtcNow;
            _scanning = false;
            var before = string.Join("|", _found.Select(c => c.Path));
            var after = string.Join("|", res.Select(c => c.Path));
            _found = res;
            if (before != after) disp?.BeginInvoke(() => Discovered?.Invoke());
        });
    }

    private static async Task<List<Computer>> DiscoverAsync()
    {
        // 1) Lo que ve el Explorador en «Red» (WSD/NetBIOS): necesita un hilo STA para el COM del shell.
        var shellTask = Task.Run(ShellNetworkComputers);
        // 2) Vecinos de la tabla ARP con el puerto 445 abierto: encuentra servidores por IP aunque
        //    la «detección de redes» esté apagada (lo normal en la red de la oficina).
        var arpTask = Task.Run(ArpSmbHosts);
        await Task.WhenAll(Task.WhenAny(shellTask, Task.Delay(12000)), arpTask);
        var list = new List<Computer>();
        var byIp = new HashSet<string>();
        if (shellTask.IsCompletedSuccessfully)
            foreach (var name in shellTask.Result)
            {
                var ip = await ResolveAsync(name);
                if (ip != null) byIp.Add(ip);
                list.Add(new Computer(@"\\" + name, name, ip));
            }
        foreach (var (ip, name) in arpTask.Result)
            if (byIp.Add(ip)) list.Add(new Computer(@"\\" + ip, name ?? ip, ip));
        return list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<string> ResolveAsync(string name)
    {
        try
        {
            var t = Dns.GetHostAddressesAsync(name);
            if (await Task.WhenAny(t, Task.Delay(800)) != t) return null;
            return t.Result.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch { return null; }
    }

    private static List<string> ShellNetworkComputers()
    {
        var names = new List<string>();
        var th = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                dynamic shell = Activator.CreateInstance(type);
                dynamic folder = shell.NameSpace(18); // ssfNETWORK
                if (folder == null) return;
                dynamic items = folder.Items();
                int n = items.Count;
                for (int i = 0; i < n; i++)
                {
                    dynamic it = items.Item(i);
                    string path = it.Path;
                    if (path != null && IsServerRoot(path)) lock (names) names.Add(path[2..]);
                }
            }
            catch { }
        }) { IsBackground = true };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join(12000);
        lock (names) return names.ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPNETROW
    {
        public int dwIndex, dwPhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] bPhysAddr;
        public uint dwAddr; public int dwType;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

    private static List<(string ip, string name)> ArpSmbHosts()
    {
        var ips = new List<string>();
        int size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0) return new();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buf, ref size, false) != 0) return new();
            int n = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MIB_IPNETROW>();
            for (int i = 0; i < n; i++)
            {
                var row = Marshal.PtrToStructure<MIB_IPNETROW>(buf + 4 + i * rowSize);
                if (row.dwType is not (3 or 4)) continue; // dinámica o estática
                var b = BitConverter.GetBytes(row.dwAddr);
                if (b[0] >= 224 || b[3] is 0 or 255) continue; // multidifusión / difusión
                bool priv = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
                if (!priv) continue;
                ips.Add(new IPAddress(b).ToString());
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        var mine = new HashSet<string>(Dns.GetHostAddresses(Dns.GetHostName()).Select(a => a.ToString()));
        var tasks = ips.Distinct().Where(ip => !mine.Contains(ip)).Select(async ip =>
        {
            using var c = new TcpClient();
            try
            {
                var t = Observe(c.ConnectAsync(ip, 445));
                if (await Task.WhenAny(t, Task.Delay(500)) != t || !c.Connected) return default;
            }
            catch { return default; }
            string name = null;
            try
            {
                var h = Dns.GetHostEntryAsync(ip);
                if (await Task.WhenAny(h, Task.Delay(700)) == h && h.IsCompletedSuccessfully)
                {
                    var hn = h.Result.HostName;
                    if (!string.IsNullOrEmpty(hn) && hn != ip) name = hn.Split('.')[0].ToUpperInvariant();
                }
            }
            catch { }
            return (ip, name);
        }).ToList();
        Task.WaitAll(tasks.ToArray<Task>(), 4000);
        return tasks.Where(t => t.IsCompletedSuccessfully && t.Result.ip != null).Select(t => t.Result).ToList();
    }
}
