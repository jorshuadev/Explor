using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Faro.Core;
using Faro.Model;

namespace Faro.Services;

public enum FolderGlyph { None, Desktop, Documents, Downloads, Pictures, Music, Videos, Home, Apps, Recents, Computer, Trash, Tag, Code, Cloud, Search, Network }

/// <summary>
/// Iconos vectoriales al estilo macOS: carpeta azul con el símbolo grabado, documento con
/// la extensión en una franja de color, insignias de Adobe (Ps, Ai, Id, Pr, Ae...), discos.
/// Se generan una vez por tipo y se congelan (se comparten entre miles de elementos).
/// </summary>
public static class IconFactory
{
    private static readonly Dictionary<string, ImageSource> Cache = new();
    private static readonly Typeface UiFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface UiSemi = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    public static readonly FontFamily SymbolFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Typeface SymFace = new(SymbolFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    static IconFactory() { Theme.Changed += () => Cache.Clear(); }

    public static ImageSource For(FileItem item)
    {
        if (item.Special == ItemSpecial.Drive) return Drive(item.DriveHint);
        if (item.Special == ItemSpecial.App) return App();
        if (item.Special == ItemSpecial.Server) return Server();
        if (item.IsDirectory) return Folder(GlyphForFolder(item.Path));
        return Document(item.Kind, item.Extension);
    }

    public static FolderGlyph GlyphForFolder(string path)
    {
        if (path == null) return FolderGlyph.None;
        if (NetworkService.IsShareRoot(path) || NetworkService.IsServerRoot(path)) return FolderGlyph.Network;
        foreach (var (p, g) in KnownFolders.Glyphs)
            if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return g;
        var name = System.IO.Path.GetFileName(path);
        if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals("src", StringComparison.OrdinalIgnoreCase)) return FolderGlyph.Code;
        return FolderGlyph.None;
    }

    public static string GlyphChar(FolderGlyph g) => g switch
    {
        FolderGlyph.Desktop => "",
        FolderGlyph.Documents => "",
        FolderGlyph.Downloads => "",
        FolderGlyph.Pictures => "",
        FolderGlyph.Music => "",
        FolderGlyph.Videos => "",
        FolderGlyph.Home => "",
        FolderGlyph.Apps => "",
        FolderGlyph.Recents => "",
        FolderGlyph.Computer => "",
        FolderGlyph.Trash => "",
        FolderGlyph.Tag => "",
        FolderGlyph.Code => "",
        FolderGlyph.Cloud => "",
        FolderGlyph.Search => "",
        FolderGlyph.Network => "",
        _ => ""
    };

    // ---------------- carpeta ----------------
    public static ImageSource Folder(FolderGlyph glyph = FolderGlyph.None)
    {
        var key = "folder|" + glyph;
        if (Cache.TryGetValue(key, out var c)) return c;

        var back = Geometry.Parse("M5,15 C5,12.2 6.6,11 9,11 L24.5,11 C26.4,11 27.4,11.8 28.6,13.4 L30,15.2 L55,15.2 C57.8,15.2 59,16.6 59,19.2 L59,49 C59,52 57.4,53.5 54.5,53.5 L9.5,53.5 C6.6,53.5 5,52 5,49 Z");
        var front = Geometry.Parse("M5,23.5 C5,20.9 6.4,19.6 9,19.6 L55,19.6 C57.6,19.6 59,20.9 59,23.5 L59,49 C59,52 57.4,53.5 54.5,53.5 L9.5,53.5 C6.6,53.5 5,52 5,49 Z");

        Color top = Theme.IsDark ? Color.FromRgb(0x6A, 0xC4, 0xFA) : Color.FromRgb(0x8C, 0xD2, 0xFB);
        Color bottom = Theme.IsDark ? Color.FromRgb(0x2B, 0x9C, 0xF0) : Color.FromRgb(0x56, 0xB6, 0xF1);
        Color backC = Theme.IsDark ? Color.FromRgb(0x23, 0x80, 0xD0) : Color.FromRgb(0x4B, 0xA7, 0xE6);
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(backC)), null, back));
        var grad = new LinearGradientBrush(top, bottom, 90);
        g.Children.Add(new GeometryDrawing(Frozen(grad), new Pen(Frozen(new SolidColorBrush(Color.FromArgb(40, 0x1D, 0x7F, 0xC9))), 0.6), front));
        // brillo superior del frente
        g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255))), null,
            Geometry.Parse("M5.6,23.5 C5.6,21.2 6.8,20.2 9,20.2 L55,20.2 C57.2,20.2 58.4,21.2 58.4,23.5 L58.4,24.3 L5.6,24.3 Z")));
        if (glyph != FolderGlyph.None)
        {
            var gl = TextGeometry(GlyphChar(glyph), SymFace, 17, new Point(32, 37));
            if (gl != null)
                g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(Theme.IsDark ? Color.FromArgb(200, 0x14, 0x5C, 0x9C) : Color.FromArgb(190, 0x2A, 0x7C, 0xC0))), null, gl));
        }
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 64, 64))));
        return Store(key, g);
    }

    // ---------------- documento ----------------
    public static ImageSource Document(KindInfo kind, string ext)
    {
        var key = "doc|" + kind.Category + "|" + ext + "|" + kind.Badge;
        if (Cache.TryGetValue(key, out var c)) return c;

        var g = new DrawingGroup();
        if (kind.Category == KindCategory.Adobe && kind.Accent != null)
        {
            // Insignia cuadrada redondeada estilo Creative Cloud, sobre una hoja
            AddPage(g);
            var bg = (Color)ColorConverter.ConvertFromString(kind.Color);
            var fg = (Color)ColorConverter.ConvertFromString(kind.Accent);
            var sq = new RectangleGeometry(new Rect(17, 22, 30, 29), 6.5, 6.5);
            g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(bg)), new Pen(Frozen(new SolidColorBrush(fg)), 1.4), sq));
            var t = TextGeometry(kind.Badge, UiFace, 14.5, new Point(32, 36.8));
            if (t != null) g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(fg)), null, t));
            AddLabel(g, ext.ToUpperInvariant(), Color.FromRgb(0x8E, 0x8E, 0x93), small: true);
        }
        else
        {
            AddPage(g);
            var col = (Color)ColorConverter.ConvertFromString(kind.Color);
            var sym = CategorySymbol(kind.Category);
            if (sym != null)
            {
                var gl = TextGeometry(sym, SymFace, 15, new Point(32, 31));
                if (gl != null) g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(Color.FromArgb(150, col.R, col.G, col.B))), null, gl));
            }
            if (!string.IsNullOrEmpty(ext)) AddLabel(g, kind.Badge.Length > 0 ? kind.Badge : ext.ToUpperInvariant(), col, small: false);
        }
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 64, 64))));
        return Store(key, g);
    }

    private static void AddPage(DrawingGroup g)
    {
        var page = Geometry.Parse("M14,6 L39,6 L52,19 L52,56 C52,57.7 50.7,59 49,59 L15,59 C13.3,59 12,57.7 12,56 L12,8 C12,6.9 12.9,6 14,6 Z");
        var fold = Geometry.Parse("M39,6 L39,16 C39,17.7 40.3,19 42,19 L52,19 Z");
        var paper = Theme.IsDark ? Color.FromRgb(0xEE, 0xEE, 0xF0) : Colors.White;
        g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(paper)), new Pen(Frozen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0))), 0.8), page));
        g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xE1))), new Pen(Frozen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0))), 0.6), fold));
    }

    private static void AddLabel(DrawingGroup g, string text, Color col, bool small)
    {
        if (text.Length > 5) text = text[..5];
        if (small)
        {
            var t = TextGeometry(text, UiSemi, 6.5, new Point(32, 55.2));
            if (t != null) g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(col)), null, t));
            return;
        }
        var band = new RectangleGeometry(new Rect(12, 41, 40, 11));
        g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(col)), null, band));
        double size = text.Length <= 3 ? 8.2 : text.Length == 4 ? 7.2 : 6.2;
        var tg = TextGeometry(text, UiFace, size, new Point(32, 46.6));
        if (tg != null) g.Children.Add(new GeometryDrawing(Brushes.White, null, tg));
    }

    private static string CategorySymbol(KindCategory c) => c switch
    {
        KindCategory.Image or KindCategory.RawPhoto => "",
        KindCategory.Video => "",
        KindCategory.Audio => "",
        KindCategory.Pdf => "",
        KindCategory.Text or KindCategory.Subtitle => "",
        KindCategory.Code or KindCategory.Web => "",
        KindCategory.Archive => "",
        KindCategory.DiskImage => "",
        KindCategory.Font => "",
        KindCategory.Executable => "",
        KindCategory.Model3D or KindCategory.Cad => "",
        KindCategory.Spreadsheet => "",
        KindCategory.Presentation => "",
        KindCategory.Database => "",
        KindCategory.Ebook => "",
        KindCategory.Vector or KindCategory.Design => "",
        KindCategory.Shortcut => "",
        _ => null
    };

    // ---------------- disco ----------------
    public static ImageSource Drive(DriveKindHint hint)
    {
        var key = "drive|" + hint;
        if (Cache.TryGetValue(key, out var c)) return c;
        var g = new DrawingGroup();
        var body = new RectangleGeometry(new Rect(6, 22, 52, 24), 5, 5);
        var c1 = hint == DriveKindHint.Removable ? Color.FromRgb(0xF2, 0xA6, 0x3B) : Color.FromRgb(0xD8, 0xD8, 0xDC);
        var c2 = hint == DriveKindHint.Removable ? Color.FromRgb(0xD9, 0x7E, 0x1A) : Color.FromRgb(0xA9, 0xA9, 0xAF);
        if (hint == DriveKindHint.Network) { c1 = Color.FromRgb(0x9F, 0xC7, 0xF5); c2 = Color.FromRgb(0x5B, 0x95, 0xD6); }
        g.Children.Add(new GeometryDrawing(Frozen(new LinearGradientBrush(c1, c2, 90)), new Pen(Frozen(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0))), 0.8), body));
        g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255))), null, new RectangleGeometry(new Rect(8, 24, 48, 6), 3, 3)));
        g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(hint == DriveKindHint.System ? Color.FromRgb(0x34, 0xC7, 0x59) : Color.FromRgb(0x5A, 0x5A, 0x60))), null, new EllipseGeometry(new Point(50, 40), 1.8, 1.8)));
        if (hint == DriveKindHint.System)
        {
            var w = TextGeometry("", SymFace, 9, new Point(15, 37.5)); // logo de ventana
            if (w != null) g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))), null, w));
        }
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 64, 64))));
        return Store(key, g);
    }

    /// <summary>Un equipo de la red: monitor plano, sin brillos.</summary>
    public static ImageSource Server()
    {
        const string key = "server";
        if (Cache.TryGetValue(key, out var c)) return c;
        var g = new DrawingGroup();
        var frame = Theme.IsDark ? Color.FromRgb(0x8E, 0x8E, 0x96) : Color.FromRgb(0x6E, 0x6E, 0x76);
        g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(frame)), null, new RectangleGeometry(new Rect(8, 12, 48, 32), 4, 4)));
        g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(Color.FromRgb(0x4B, 0xA7, 0xE6))), null, new RectangleGeometry(new Rect(11, 15, 42, 26), 2, 2)));
        g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(frame)), null, Geometry.Parse("M27,44 L37,44 L38.5,50 L25.5,50 Z")));
        g.Children.Add(new GeometryDrawing(Frozen(new SolidColorBrush(frame)), null, new RectangleGeometry(new Rect(20, 50, 24, 3), 1.5, 1.5)));
        var gl = TextGeometry("", SymFace, 14, new Point(32, 28));
        if (gl != null) g.Children.Add(new GeometryDrawing(Brushes.White, null, gl));
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 64, 64))));
        return Store(key, g);
    }

    public static ImageSource App()
    {
        const string key = "app";
        if (Cache.TryGetValue(key, out var c)) return c;
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Frozen(new LinearGradientBrush(Color.FromRgb(0x9A, 0x9A, 0xA2), Color.FromRgb(0x5E, 0x5E, 0x66), 90)), null,
            new RectangleGeometry(new Rect(8, 8, 48, 48), 11, 11)));
        var t = TextGeometry("", SymFace, 22, new Point(32, 32));
        if (t != null) g.Children.Add(new GeometryDrawing(Brushes.White, null, t));
        return Store(key, g);
    }

    /// <summary>Símbolo suelto (para barra lateral, barra de ruta...).</summary>
    public static Geometry TextGeometry(string text, Typeface face, double size, Point center)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, 1.0);
            var geo = ft.BuildGeometry(new Point(0, 0));
            if (geo.IsEmpty()) return null;
            var b = geo.Bounds;
            geo.Transform = new TranslateTransform(center.X - (b.Left + b.Width / 2), center.Y - (b.Top + b.Height / 2));
            var flat = geo.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
            flat.Freeze();
            return flat;
        }
        catch { return null; }
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static ImageSource Store(string key, DrawingGroup g)
    {
        g.Freeze();
        var img = new DrawingImage(g);
        img.Freeze();
        Cache[key] = img;
        return img;
    }
}

/// <summary>Carpetas conocidas del usuario (Escritorio, Documentos, Descargas...).</summary>
public static class KnownFolders
{
    public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string Desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    public static readonly string Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static readonly string Downloads = GetDownloads();
    public static readonly string Pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public static readonly string Music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public static readonly string Videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    public static readonly (string path, FolderGlyph glyph)[] Glyphs =
    {
        (Desktop, FolderGlyph.Desktop), (Documents, FolderGlyph.Documents), (Downloads, FolderGlyph.Downloads),
        (Pictures, FolderGlyph.Pictures), (Music, FolderGlyph.Music), (Videos, FolderGlyph.Videos), (Home, FolderGlyph.Home),
    };

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string GetDownloads()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
            {
                var s = System.Runtime.InteropServices.Marshal.PtrToStringUni(p);
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
                return s;
            }
        }
        catch { }
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
