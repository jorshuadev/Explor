using System.Diagnostics;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Faro.Shell;

/// <summary>
/// Las apps que pueden abrir un archivo: las que Windows recomienda para su tipo (lo mismo que lista
/// «Abrir con» en el Explorador) y, para texto y código, los editores instalados aunque no estén registrados
/// para esa extensión (VS Code no se registra para todas).
/// </summary>
public static class OpenWithApps
{
    public sealed record App(string Name, string ExePath, Action<string> Open);

    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IAssocHandler handler, out uint fetched);
    }

    [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAssocHandler
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);
        [PreserveSig] int IsRecommended();
        [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int Invoke(ComTypes.IDataObject data);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemMin
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHAssocEnumHandlers(string extra, int filter, out IEnumAssocHandlers handlers);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItemMin item);

    /// <summary>Apps recomendadas por Windows para la extensión del archivo (hilo de la ventana: usa COM).</summary>
    public static List<App> Recommended(string path)
    {
        var list = new List<App>();
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return list;
        try
        {
            if (SHAssocEnumHandlers(ext, 1 /* ASSOC_FILTER_RECOMMENDED */, out var en) != 0 || en == null) return list;
            while (list.Count < 14 && en.Next(1, out var h, out var n) == 0 && n == 1 && h != null)
            {
                h.GetUIName(out var ui);
                h.GetName(out var exe);
                // sin nombre registrado, Windows devuelve la ruta del .exe: mejor su descripción («WebStorm»)
                if (!string.IsNullOrWhiteSpace(ui) && ui.Contains('\\'))
                {
                    var file = ui;
                    try { ui = FileVersionInfo.GetVersionInfo(file).FileDescription; } catch { ui = null; }
                    if (string.IsNullOrWhiteSpace(ui)) ui = Path.GetFileNameWithoutExtension(file);
                }
                if (string.IsNullOrWhiteSpace(ui) || list.Any(a => string.Equals(a.Name, ui, StringComparison.OrdinalIgnoreCase))) continue;
                var handler = h;
                list.Add(new App(ui, exe, p => Launch(handler, exe, p)));
            }
        }
        catch { }
        return list;
    }

    /// <summary>Editores de código instalados (se ofrecen para texto, código y web aunque Windows no los recomiende).</summary>
    public static List<App> Editors()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var known = new (string Name, string[] Paths)[]
        {
            ("Visual Studio Code", new[] { Path.Combine(local, @"Programs\Microsoft VS Code\Code.exe"), Path.Combine(pf, @"Microsoft VS Code\Code.exe") }),
            ("Sublime Text", new[] { Path.Combine(pf, @"Sublime Text\sublime_text.exe"), Path.Combine(pf, @"Sublime Text 3\sublime_text.exe") }),
            ("Notepad++", new[] { Path.Combine(pf, @"Notepad++\notepad++.exe"), Path.Combine(pf86, @"Notepad++\notepad++.exe") }),
        };
        var list = new List<App>();
        foreach (var (name, paths) in known)
        {
            var exe = paths.FirstOrDefault(File.Exists);
            if (exe != null) list.Add(new App(name, exe, p => StartExe(exe, p)));
        }
        return list;
    }

    private static void Launch(IAssocHandler handler, string exe, string path)
    {
        // lo fiel es que la app se lance como la registró (argumentos propios, apps de la Tienda…)
        try
        {
            var iidItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidItem, out var item) == 0 && item != null)
            {
                var bhidData = new Guid("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");
                var iidData = new Guid("0000010e-0000-0000-C000-000000000046");
                if (item.BindToHandler(IntPtr.Zero, ref bhidData, ref iidData, out var data) == 0 && data is ComTypes.IDataObject d && handler.Invoke(d) == 0) return;
            }
        }
        catch { }
        if (exe != null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe)) StartExe(exe, path);
        else ShellInterop.OpenWith(path);
    }

    private static void StartExe(string exe, string path)
    {
        try { Process.Start(new ProcessStartInfo(exe, $"\"{path}\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) ?? "" }); }
        catch (Exception ex) { Dialogs.MacDialog.Error(null, $"No se pudo abrir «{Path.GetFileName(path)}».", ex.Message); }
    }
}
