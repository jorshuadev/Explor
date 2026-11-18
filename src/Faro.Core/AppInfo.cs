namespace Faro.Core;

/// <summary>Nombre visible de la app y carpetas de datos (el código interno sigue usando el espacio de nombres Faro).</summary>
public static class AppInfo
{
    public const string Name = "Explor";
    public static string RoamingDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);
    public static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    /// <summary>Nombres anteriores de la app, del más reciente al más antiguo.</summary>
    private static readonly string[] OldNames = { "Archivos", "Nido", "Faro" };

    /// <summary>Mueve los datos de una versión anterior (se llamó «Archivos», antes «Nido» y antes «Faro») a las carpetas nuevas.</summary>
    public static void MigrateOldData()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var baseDir = Environment.GetFolderPath(folder);
            var newDir = Path.Combine(baseDir, Name);
            foreach (var old in OldNames)
            {
                var oldDir = Path.Combine(baseDir, old);
                // %LOCALAPPDATA%\Programs\<nombre> es la instalación, no datos: no se toca
                try { if (Directory.Exists(oldDir) && !Directory.Exists(newDir)) Directory.Move(oldDir, newDir); } catch { }
            }
        }
    }
}
