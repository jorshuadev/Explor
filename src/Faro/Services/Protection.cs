using System.IO;

namespace Faro.Services;

/// <summary>
/// Elementos que Explor nunca mueve, renombra ni manda a la Papelera: raíces de disco, carpetas de
/// Windows y del perfil, y todo lo marcado como «sistema». Copiarlos sí se puede. Moverlos por un
/// arrastre accidental rompe Windows, programas instalados o rutas que usan los scripts del usuario.
/// </summary>
public static class Protection
{
    private static readonly Lazy<HashSet<string>> Fixed = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string p) { if (!string.IsNullOrEmpty(p)) set.Add(Path.TrimEndingDirectorySeparator(p)); }
        foreach (var f in new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu
        })
            Add(Environment.GetFolderPath(f));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            Add(Path.Combine(profile, "Downloads"));
            Add(Path.Combine(profile, "AppData"));
            Add(Path.GetDirectoryName(profile)); // C:\Users
        }
        var sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        foreach (var n in new[] { "Users", "Recovery", "PerfLogs", "Boot", "EFI", "Program Files (Arm)" }) Add(Path.Combine(sys, n));
        return set;
    });

    /// <summary>Motivo por el que no se puede mover/renombrar/borrar, o null si se puede.</summary>
    public static string Why(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return null; }
        var root = Path.GetPathRoot(full);
        if (root != null && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
            return "es un disco completo";
        if (Fixed.Value.Contains(full)) return "es una carpeta de Windows o de tu perfil";
        var name = Path.GetFileName(full);
        bool atRoot = string.Equals(Path.GetDirectoryName(full), root?.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase);
        if (atRoot && (name.StartsWith('$') || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)))
            return "es una carpeta interna de Windows";
        try
        {
            var a = File.GetAttributes(full);
            if ((a & FileAttributes.System) != 0) return "es un elemento del sistema";
        }
        catch { }
        return null;
    }

    public static bool IsProtected(string path) => Why(path) != null;

    /// <summary>
    /// Quita de la lista lo protegido y avisa con un diálogo. Devuelve lo que sí se puede tocar
    /// (vacío si no queda nada).
    /// </summary>
    public static List<string> Filter(IEnumerable<string> paths, string verb, System.Windows.Window owner)
    {
        var all = paths.ToList();
        var blocked = all.Select(p => (p, why: Why(p))).Where(x => x.why != null).ToList();
        if (blocked.Count == 0) return all;
        var ok = all.Except(blocked.Select(b => b.p), StringComparer.OrdinalIgnoreCase).ToList();
        var title = blocked.Count == 1
            ? $"No se puede {verb} «{NameOf(blocked[0].p)}»."
            : $"No se pueden {verb} {blocked.Count} elementos protegidos.";
        var detail = blocked.Count == 1
            ? char.ToUpper(blocked[0].why[0]) + blocked[0].why[1..] + ": Explor no lo mueve, renombra ni borra para no dañar Windows ni tus programas. Copiarlo (Ctrl al arrastrar) sí se puede."
            : string.Join("\n", blocked.Take(6).Select(b => $"• {NameOf(b.p)}: {b.why}."));
        if (blocked.Count > 6) detail += $"\n… y {blocked.Count - 6} más.";
        detail += ok.Count > 0 ? $"\n\nEl resto ({ok.Count}) sí se procesa." : "\n\nNo se ha hecho ningún cambio.";
        Dialogs.MacDialog.Info(owner ?? System.Windows.Application.Current.MainWindow, title, detail);
        return ok;
    }

    private static string NameOf(string p)
    {
        var t = Path.TrimEndingDirectorySeparator(p);
        var n = Path.GetFileName(t);
        return string.IsNullOrEmpty(n) ? p : n;
    }
}
