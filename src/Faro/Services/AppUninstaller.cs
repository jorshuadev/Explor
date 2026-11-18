using System.Diagnostics;
using Microsoft.Win32;
using Faro.Model;
using Faro.Shell;

namespace Faro.Services;

/// <summary>
/// Acciones de «Aplicaciones»: a qué apunta cada acceso directo y cómo se desinstala. Lee las mismas
/// entradas que «Aplicaciones instaladas» de Windows (Uninstall en HKLM/HKCU, 64 y 32 bits) y las
/// empareja con el acceso directo por la ruta del ejecutable y, si no, por el nombre. Los juegos de
/// Steam (enlaces steam://rungameid/N) se desinstalan a través de Steam.
/// </summary>
public static class AppUninstaller
{
    public sealed record Entry(string DisplayName, string Command, bool ViaSteam = false);

    private sealed record Installed(string Name, string InstallLocation, string DisplayIcon, string Uninstall);

    /// <summary>Ejecutable o URL al que apunta el acceso directo (.lnk, .url, .appref-ms).</summary>
    public static string TargetOf(FileItem item)
    {
        var p = item.LaunchPath ?? item.Path;
        try
        {
            if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return ShellInterop.ResolveShortcut(p);
            if (p.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                return File.ReadLines(p).FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..].Trim();
        }
        catch { }
        return null;
    }

    public static bool IsExecutable(string target) =>
        target != null && (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                           || target.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".msc", StringComparison.OrdinalIgnoreCase));

    public static Entry Find(FileItem item)
    {
        var target = TargetOf(item);
        // Steam: steam://rungameid/1364780
        if (target != null && target.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase))
        {
            var id = new string(target["steam://rungameid/".Length..].TakeWhile(char.IsDigit).ToArray());
            if (id.Length > 0) return new Entry(item.DisplayName, "steam://uninstall/" + id, ViaSteam: true);
        }
        string exe = IsExecutable(target) ? target : null;
        // herramientas de Windows (Administrador de tareas, mmc…): no se desinstalan, y los desinstaladores
        // MSI viven en System32, así que emparejarían con cualquier programa
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (exe != null && exe.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        // un enlace web (http…) no es una app instalada: no se busca nada o «Steam Support Center» daría Steam
        if (target != null && target.Contains("://")) return null;
        var all = ReadInstalled();
        string exeDir = exe != null ? Path.GetDirectoryName(exe) : null;
        // Un acceso directo que abre un archivo tuyo con un programa (p. ej. AutoHotkey + tu script) no
        // ES ese programa: si los argumentos apuntan a un archivo fuera de la carpeta del programa, la ruta no cuenta.
        bool opensUserFile = exe != null && OpensFileOutside(item, exeDir);
        var n = Normalize(item.DisplayName);

        // Cada candidato suma puntos; gana el que más tenga. Antes ganaba la primera coincidencia por ruta y
        // «Steam» acababa en un juego que registra steam.exe como icono.
        Installed best = null; int bestScore = 0;
        foreach (var a in all)
        {
            int score = 0;
            var an = Normalize(a.Name);
            if (n.Length >= 3 && an == n) score += 100;
            else if (n.Length >= 3 && Close(an, n)) score += 40;
            if (exe != null && !opensUserFile)
            {
                if (exeDir != null && string.Equals(Path.GetDirectoryName(CleanPath(a.Uninstall) ?? ""), exeDir, StringComparison.OrdinalIgnoreCase)) score += 60;
                if (!string.IsNullOrEmpty(a.InstallLocation) && exe.StartsWith(a.InstallLocation.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    // el .exe en la carpeta de instalación o hasta 3 niveles por debajo (Office\root\Office16, Krita\bin)
                    // cuenta como «suyo»; más abajo es una carpeta de fabricante que contiene a varios (Intel\…)
                    int depth = exeDir[a.InstallLocation.TrimEnd('\\').Length..].Count(ch => ch == '\\');
                    score += depth == 0 ? 60 : depth <= 3 ? 45 : 20;
                }
                if (string.Equals(CleanPath(a.DisplayIcon), exe, StringComparison.OrdinalIgnoreCase)) score += 30;
            }
            if (score > bestScore) { best = a; bestScore = score; }
        }
        // sólo coincidencias claras: un parecido de nombre suelto (40) basta, un icono compartido (30) no
        return best != null && bestScore >= 40 ? new Entry(best.Name, best.Uninstall) : null;
    }

    private static bool Close(string a, string b) =>
        (a.StartsWith(b) || b.StartsWith(a)) && Math.Min(a.Length, b.Length) >= Math.Max(a.Length, b.Length) * 0.5;

    private static bool OpensFileOutside(FileItem item, string exeDir)
    {
        var p = item.LaunchPath ?? item.Path;
        if (!p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
        var args = ShellInterop.ShortcutArguments(p);
        if (string.IsNullOrWhiteSpace(args)) return false;
        // rutas entre comillas o sueltas («C:\…») que existen
        var candidates = System.Text.RegularExpressions.Regex.Matches(args, "\"([^\"]+)\"|([A-Za-z]:\\\\[^ ]+)")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return candidates.Any(f => File.Exists(f) && !f.StartsWith(exeDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                                   && !f.StartsWith(Path.GetDirectoryName(exeDir) + "\\", StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>Lanza el desinstalador (Windows pedirá permisos si hacen falta).</summary>
    public static void Run(Entry e)
    {
        if (e.ViaSteam) { Process.Start(new ProcessStartInfo(e.Command) { UseShellExecute = true }); return; }
        var (file, args) = SplitCommand(e.Command);
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
    }

    /// <summary>Página de Windows con todas las aplicaciones instaladas (si no se encuentra el desinstalador).</summary>
    public static void OpenWindowsAppsSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });

    /// <summary>Ejecuta como administrador (aparece el aviso de Control de cuentas).</summary>
    public static void RunAsAdmin(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });

    private static List<Installed> ReadInstalled()
    {
        var res = new List<Installed>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var un = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (un == null) continue;
                foreach (var sub in un.GetSubKeyNames())
                {
                    using var k = un.OpenSubKey(sub);
                    var name = k?.GetValue("DisplayName") as string;
                    var cmd = k?.GetValue("UninstallString") as string;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cmd)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    res.Add(new Installed(name.Trim(), k.GetValue("InstallLocation") as string, k.GetValue("DisplayIcon") as string, cmd.Trim()));
                }
            }
            catch { }
        }
        return res;
    }

    /// <summary>"C:\x\app.exe",0 → C:\x\app.exe ; "C:\x\unins000.exe" /SILENT → C:\x\unins000.exe</summary>
    private static string CleanPath(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith('"')) { int q = s.IndexOf('"', 1); return q > 1 ? s[1..q] : s.Trim('"'); }
        int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return s[..(exe + 4)];
        int comma = s.LastIndexOf(',');
        return comma > 0 ? s[..comma] : s;
    }

    private static (string file, string args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            int q = cmd.IndexOf('"', 1);
            if (q > 1) return (cmd[1..q], cmd[(q + 1)..].Trim());
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (cmd[..(exe + 4)], cmd[(exe + 4)..].Trim());
        return (cmd, "");
    }

    private static string Normalize(string s) =>
        new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
