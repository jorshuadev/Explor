using Faro.Services;

namespace Faro.Model;

public enum LocationKind { Folder, Recents, Apps, Computer, Trash, Tag, Search, Network }

/// <summary>Dónde está una pestaña: una carpeta real o un lugar virtual (Recientes, Aplicaciones...).</summary>
public sealed record Location(LocationKind Kind, string Path = null, string Tag = null, string Query = null, string SearchScope = null)
{
    public static Location Folder(string p) => new(LocationKind.Folder, Normalize(p));
    public static readonly Location Recents = new(LocationKind.Recents);
    public static readonly Location Apps = new(LocationKind.Apps);
    public static readonly Location Computer = new(LocationKind.Computer);
    public static readonly Location Trash = new(LocationKind.Trash);
    public static readonly Location Network = new(LocationKind.Network);
    public static Location ForTag(string t) => new(LocationKind.Tag, Tag: t);

    public static string Normalize(string p)
    {
        if (string.IsNullOrEmpty(p)) return p;
        if (NetworkService.IsServerRoot(p)) return p.TrimEnd('\\'); // GetFullPath no sabe qué hacer con \\servidor solo
        try { p = System.IO.Path.GetFullPath(p); } catch { }
        if (p.Length > 3) p = p.TrimEnd('\\');
        if (p.Length == 2 && p[1] == ':') p += "\\";
        return p;
    }

    public bool IsFolder => Kind == LocationKind.Folder;

    public string Title => Kind switch
    {
        LocationKind.Folder => DisplayNameOf(Path),
        LocationKind.Recents => "Recientes",
        LocationKind.Apps => "Aplicaciones",
        LocationKind.Computer => Environment.MachineName,
        LocationKind.Trash => "Papelera",
        LocationKind.Network => "Red",
        LocationKind.Tag => Tag,
        LocationKind.Search => $"Buscando «{Query}»",
        _ => ""
    };

    public static string DisplayNameOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (NetworkService.DisplayName(path) is string net) return net;
        if (path.Length <= 3)
        {
            Faro.Core.Native.TryGetVolumeInfo(path, out _, out var label, out _);
            return string.IsNullOrWhiteSpace(label) ? $"Disco local ({path.TrimEnd('\\')})" : $"{label} ({path.TrimEnd('\\')})";
        }
        if (string.Equals(path, KnownFolders.Desktop, StringComparison.OrdinalIgnoreCase)) return "Escritorio";
        if (string.Equals(path, KnownFolders.Documents, StringComparison.OrdinalIgnoreCase)) return "Documentos";
        if (string.Equals(path, KnownFolders.Downloads, StringComparison.OrdinalIgnoreCase)) return "Descargas";
        if (string.Equals(path, KnownFolders.Pictures, StringComparison.OrdinalIgnoreCase)) return "Imágenes";
        if (string.Equals(path, KnownFolders.Music, StringComparison.OrdinalIgnoreCase)) return "Música";
        if (string.Equals(path, KnownFolders.Videos, StringComparison.OrdinalIgnoreCase)) return "Vídeos";
        return FileItem.NameOf(path);
    }

    public FolderGlyph Glyph => Kind switch
    {
        LocationKind.Recents => FolderGlyph.Recents,
        LocationKind.Apps => FolderGlyph.Apps,
        LocationKind.Computer => FolderGlyph.Computer,
        LocationKind.Trash => FolderGlyph.Trash,
        LocationKind.Network => FolderGlyph.Network,
        LocationKind.Tag => FolderGlyph.Tag,
        LocationKind.Search => FolderGlyph.Search,
        _ => IconFactory.GlyphForFolder(Path)
    };

    public bool Equals(Location other) =>
        other is not null && Kind == other.Kind && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)
        && Tag == other.Tag && Query == other.Query && SearchScope == other.SearchScope;

    public override int GetHashCode() => HashCode.Combine(Kind, Path?.ToLowerInvariant(), Tag, Query);
}
