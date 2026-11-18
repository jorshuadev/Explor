namespace Faro.Core;

public enum KindCategory
{
    Folder, Image, RawPhoto, Vector, Video, Audio, Pdf, Adobe, Document, Spreadsheet, Presentation,
    Text, Code, Web, Archive, DiskImage, Font, Executable, Shortcut, Model3D, Cad, Ebook, Database,
    Subtitle, Design, System, Other
}

public sealed record KindInfo(string Name, KindCategory Category, string Badge, string Color, string Accent = null)
{
    /// <summary>Texto del filtro de búsqueda "tipo:xxx" que coincide con esta categoría.</summary>
    public string SearchGroup => FileKinds.GroupOf(Category);
}

/// <summary>
/// Catálogo de tipos de archivo con nombres en español al estilo de Finder
/// ("Imagen JPEG", "Documento de Adobe Photoshop", "Película MPEG-4"...) y
/// detección por contenido (firmas mágicas) para archivos sin extensión o mal nombrados.
/// </summary>
public static class FileKinds
{
    private static readonly Dictionary<string, KindInfo> Map = new(StringComparer.OrdinalIgnoreCase);

    public static readonly KindInfo FolderKind = new("Carpeta", KindCategory.Folder, "", "#3FA9F5");
    public static readonly KindInfo DriveKind = new("Volumen", KindCategory.Folder, "", "#8E8E93");
    public static readonly KindInfo Unknown = new("Documento", KindCategory.Other, "", "#8E8E93");

    static FileKinds()
    {
        // ---- Imágenes ----
        Add(KindCategory.Image, "#34A853", ("jpg", "Imagen JPEG"), ("jpeg", "Imagen JPEG"), ("jpe", "Imagen JPEG"), ("jfif", "Imagen JPEG"),
            ("png", "Imagen PNG"), ("gif", "Imagen GIF"), ("bmp", "Imagen BMP de Windows"), ("dib", "Imagen BMP de Windows"),
            ("tif", "Imagen TIFF"), ("tiff", "Imagen TIFF"), ("webp", "Imagen WebP"), ("heic", "Imagen HEIF"), ("heif", "Imagen HEIF"),
            ("avif", "Imagen AVIF"), ("ico", "Icono de Windows"), ("icns", "Icono de Apple"), ("cur", "Cursor de Windows"),
            ("tga", "Imagen Targa"), ("jp2", "Imagen JPEG 2000"), ("j2k", "Imagen JPEG 2000"), ("jxl", "Imagen JPEG XL"),
            ("exr", "Imagen OpenEXR"), ("hdr", "Imagen HDR de Radiance"), ("dds", "Textura DirectDraw"), ("pcx", "Imagen PCX"),
            ("ppm", "Imagen PPM"), ("pgm", "Imagen PGM"), ("pbm", "Imagen PBM"), ("wdp", "Imagen JPEG XR"), ("jxr", "Imagen JPEG XR"),
            ("emf", "Metarchivo mejorado de Windows"), ("wmf", "Metarchivo de Windows"), ("xcf", "Imagen de GIMP"),
            ("kra", "Documento de Krita"), ("clip", "Archivo de Clip Studio Paint"), ("procreate", "Documento de Procreate"));
        // ---- RAW de cámara ----
        Add(KindCategory.RawPhoto, "#2E7D32", ("dng", "Imagen RAW DNG de Adobe"), ("cr2", "Imagen RAW de Canon"), ("cr3", "Imagen RAW de Canon"),
            ("crw", "Imagen RAW de Canon"), ("nef", "Imagen RAW de Nikon"), ("nrw", "Imagen RAW de Nikon"), ("arw", "Imagen RAW de Sony"),
            ("srf", "Imagen RAW de Sony"), ("sr2", "Imagen RAW de Sony"), ("orf", "Imagen RAW de Olympus"), ("rw2", "Imagen RAW de Panasonic"),
            ("raf", "Imagen RAW de Fujifilm"), ("pef", "Imagen RAW de Pentax"), ("srw", "Imagen RAW de Samsung"), ("x3f", "Imagen RAW de Sigma"),
            ("3fr", "Imagen RAW de Hasselblad"), ("iiq", "Imagen RAW de Phase One"), ("raw", "Imagen RAW"), ("rwl", "Imagen RAW de Leica"));
        // ---- Vectoriales ----
        Add(KindCategory.Vector, "#F57C00", ("svg", "Imagen SVG"), ("svgz", "Imagen SVG comprimida"), ("eps", "Documento EPS"),
            ("cdr", "Dibujo de CorelDRAW"), ("cmx", "Intercambio de Corel"), ("wmz", "Metarchivo comprimido"), ("dxf", "Dibujo DXF"));
        // ---- Vídeo ----
        Add(KindCategory.Video, "#7B61FF", ("mp4", "Película MPEG-4"), ("m4v", "Vídeo MPEG-4"), ("mov", "Película QuickTime"),
            ("qt", "Película QuickTime"), ("avi", "Película AVI"), ("mkv", "Vídeo Matroska"), ("webm", "Vídeo WebM"), ("wmv", "Vídeo de Windows Media"),
            ("flv", "Vídeo Flash"), ("f4v", "Vídeo Flash MP4"), ("mpg", "Película MPEG"), ("mpeg", "Película MPEG"), ("m2v", "Vídeo MPEG-2"),
            ("mts", "Vídeo AVCHD"), ("m2ts", "Vídeo AVCHD"),("vob", "Vídeo de DVD"), ("3gp", "Película 3GPP"),
            ("3g2", "Película 3GPP2"), ("ogv", "Vídeo Ogg"), ("mxf", "Vídeo MXF"), ("r3d", "Vídeo RED RAW"), ("braw", "Vídeo Blackmagic RAW"),
            ("prores", "Vídeo Apple ProRes"), ("dv", "Vídeo DV"), ("asf", "Vídeo ASF"), ("rm", "Vídeo RealMedia"), ("rmvb", "Vídeo RealMedia"),
            ("divx", "Vídeo DivX"), ("y4m", "Vídeo YUV4MPEG"));
        // ---- Audio ----
        Add(KindCategory.Audio, "#FF2D55", ("mp3", "Audio MP3"), ("m4a", "Audio MPEG-4"), ("aac", "Audio AAC"), ("wav", "Audio WAV"),
            ("wave", "Audio WAV"), ("aif", "Audio AIFF"), ("aiff", "Audio AIFF"), ("aifc", "Audio AIFF-C"), ("flac", "Audio FLAC"),
            ("ogg", "Audio Ogg Vorbis"), ("oga", "Audio Ogg"), ("opus", "Audio Opus"), ("wma", "Audio de Windows Media"), ("alac", "Audio Apple Lossless"),
            ("mid", "Secuencia MIDI"), ("midi", "Secuencia MIDI"), ("amr", "Audio AMR"), ("ape", "Audio Monkey's Audio"), ("wv", "Audio WavPack"),
            ("caf", "Audio Core Audio"), ("m4r", "Tono de llamada"), ("dsf", "Audio DSD"), ("dff", "Audio DSD"), ("mka", "Audio Matroska"),
            ("als", "Proyecto de Ableton Live"), ("flp", "Proyecto de FL Studio"), ("logicx", "Proyecto de Logic Pro"), ("ptx", "Sesión de Pro Tools"),
            ("cpr", "Proyecto de Cubase"), ("rpp", "Proyecto de REAPER"), ("band", "Proyecto de GarageBand"), ("sesx", "Sesión de Adobe Audition"));
        // ---- PDF ----
        Add(KindCategory.Pdf, "#E53935", ("pdf", "Documento PDF"), ("xps", "Documento XPS"), ("oxps", "Documento OpenXPS"), ("djvu", "Documento DjVu"));
        // ---- Adobe (insignia con las siglas de la app) ----
        AddAdobe("psd", "Documento de Adobe Photoshop", "Ps", "#001E36", "#31A8FF");
        AddAdobe("psb", "Documento grande de Adobe Photoshop", "Ps", "#001E36", "#31A8FF");
        AddAdobe("pdd", "Documento de Adobe Photoshop", "Ps", "#001E36", "#31A8FF");
        AddAdobe("abr", "Pinceles de Adobe Photoshop", "Ps", "#001E36", "#31A8FF");
        AddAdobe("atn", "Acciones de Adobe Photoshop", "Ps", "#001E36", "#31A8FF");
        AddAdobe("pat", "Motivos de Adobe Photoshop", "Ps", "#001E36", "#31A8FF");
        AddAdobe("asl", "Estilos de Adobe Photoshop", "Ps", "#001E36", "#31A8FF");
        AddAdobe("grd", "Degradados de Adobe Photoshop", "Ps", "#001E36", "#31A8FF");
        AddAdobe("ai", "Documento de Adobe Illustrator", "Ai", "#330000", "#FF9A00");
        AddAdobe("ait", "Plantilla de Adobe Illustrator", "Ai", "#330000", "#FF9A00");
        AddAdobe("indd", "Documento de Adobe InDesign", "Id", "#49021F", "#FF3366");
        AddAdobe("indt", "Plantilla de Adobe InDesign", "Id", "#49021F", "#FF3366");
        AddAdobe("indb", "Libro de Adobe InDesign", "Id", "#49021F", "#FF3366");
        AddAdobe("idml", "Documento IDML de Adobe InDesign", "Id", "#49021F", "#FF3366");
        AddAdobe("inx", "Intercambio de Adobe InDesign", "Id", "#49021F", "#FF3366");
        AddAdobe("idlk", "Bloqueo de Adobe InDesign", "Id", "#49021F", "#FF3366");
        AddAdobe("prproj", "Proyecto de Adobe Premiere Pro", "Pr", "#00005B", "#9999FF");
        AddAdobe("prel", "Proyecto de Adobe Premiere Elements", "Pr", "#00005B", "#9999FF");
        AddAdobe("mogrt", "Plantilla de gráficos animados", "Pr", "#00005B", "#9999FF");
        AddAdobe("aep", "Proyecto de Adobe After Effects", "Ae", "#00005B", "#9999FF");
        AddAdobe("aet", "Plantilla de Adobe After Effects", "Ae", "#00005B", "#9999FF");
        AddAdobe("aepx", "Proyecto XML de Adobe After Effects", "Ae", "#00005B", "#9999FF");
        AddAdobe("ffx", "Ajuste preestablecido de After Effects", "Ae", "#00005B", "#9999FF");
        AddAdobe("xd", "Documento de Adobe XD", "Xd", "#470137", "#FF61F6");
        AddAdobe("lrcat", "Catálogo de Adobe Lightroom", "Lr", "#001E36", "#31A8FF");
        AddAdobe("lrtemplate", "Ajuste de Adobe Lightroom", "Lr", "#001E36", "#31A8FF");
        AddAdobe("xmp", "Metadatos XMP de Adobe", "Lr", "#001E36", "#31A8FF");
        AddAdobe("fla", "Documento de Adobe Animate", "An", "#00005B", "#9999FF");
        AddAdobe("xfl", "Documento de Adobe Animate", "An", "#00005B", "#9999FF");
        AddAdobe("swf", "Película Flash", "An", "#3D0000", "#FF7C7C");
        AddAdobe("aup", "Proyecto de Audition", "Au", "#00005B", "#9999FF");
        AddAdobe("dn", "Documento de Adobe Dimension", "Dn", "#002B1F", "#3DEDA3");
        AddAdobe("sbsar", "Material de Substance", "Sa", "#002B1F", "#3DEDA3");
        AddAdobe("spp", "Proyecto de Substance Painter", "Pt", "#002B1F", "#3DEDA3");
        AddAdobe("aco", "Muestras de color de Adobe", "Ps", "#001E36", "#31A8FF");
        AddAdobe("ase", "Muestras de color de Adobe", "Ai", "#330000", "#FF9A00");
        AddAdobe("cube", "Tabla de consulta de color (LUT)", "Pr", "#00005B", "#9999FF");
        AddAdobe("dcp", "Perfil de cámara de Adobe", "Lr", "#001E36", "#31A8FF");
        AddAdobe("pdfxml", "Formulario de Adobe", "Ac", "#2D0000", "#FA0F00");
        // ---- Diseño (no Adobe) ----
        Add(KindCategory.Design, "#A259FF", ("fig", "Documento de Figma"), ("sketch", "Documento de Sketch"), ("afdesign", "Documento de Affinity Designer"),
            ("afphoto", "Documento de Affinity Photo"), ("afpub", "Documento de Affinity Publisher"), ("cdrx", "Dibujo de CorelDRAW"),
            ("pxm", "Imagen de Pixelmator"), ("pxd", "Imagen de Pixelmator Pro"), ("qxp", "Documento de QuarkXPress"), ("pub", "Documento de Publisher"),
            ("vsd", "Dibujo de Visio"), ("vsdx", "Dibujo de Visio"), ("drawio", "Diagrama de draw.io"), ("xmind", "Mapa mental de XMind"),
            ("canva", "Diseño de Canva"));
        // ---- Documentos ----
        Add(KindCategory.Document, "#2B579A", ("doc", "Documento de Microsoft Word"), ("docx", "Documento de Microsoft Word"),
            ("docm", "Documento de Word con macros"), ("dot", "Plantilla de Microsoft Word"), ("dotx", "Plantilla de Microsoft Word"),
            ("rtf", "Documento de texto enriquecido"), ("odt", "Documento de texto OpenDocument"), ("pages", "Documento de Pages"),
            ("wpd", "Documento de WordPerfect"), ("wps", "Documento de Works"), ("hwp", "Documento de Hangul"), ("one", "Bloc de notas de OneNote"));
        Add(KindCategory.Spreadsheet, "#217346", ("xls", "Hoja de cálculo de Microsoft Excel"), ("xlsx", "Hoja de cálculo de Microsoft Excel"),
            ("xlsm", "Hoja de Excel con macros"), ("xlsb", "Hoja de cálculo binaria de Excel"), ("xlt", "Plantilla de Excel"), ("xltx", "Plantilla de Excel"),
            ("csv", "Valores separados por comas (CSV)"), ("tsv", "Valores separados por tabulaciones"), ("ods", "Hoja de cálculo OpenDocument"),
            ("numbers", "Hoja de cálculo de Numbers"));
        Add(KindCategory.Presentation, "#D24726", ("ppt", "Presentación de Microsoft PowerPoint"), ("pptx", "Presentación de Microsoft PowerPoint"),
            ("pptm", "Presentación de PowerPoint con macros"), ("pps", "Presentación de PowerPoint"), ("ppsx", "Presentación de PowerPoint"),
            ("pot", "Plantilla de PowerPoint"), ("potx", "Plantilla de PowerPoint"), ("odp", "Presentación OpenDocument"), ("key", "Presentación de Keynote"));
        Add(KindCategory.Ebook, "#8D6E63", ("epub", "Libro electrónico EPUB"), ("mobi", "Libro de Kindle"), ("azw", "Libro de Kindle"),
            ("azw3", "Libro de Kindle"), ("fb2", "Libro FictionBook"), ("cbz", "Cómic CBZ"), ("cbr", "Cómic CBR"), ("ibooks", "Libro de Apple Books"));
        // ---- Texto ----
        Add(KindCategory.Text, "#6E6E73", ("txt", "Documento de texto sin formato"), ("text", "Documento de texto sin formato"), ("md", "Documento Markdown"),
            ("markdown", "Documento Markdown"), ("log", "Archivo de registro"), ("nfo", "Archivo de información"), ("readme", "Léame"),
            ("tex", "Documento LaTeX"), ("bib", "Bibliografía BibTeX"), ("rst", "Documento reStructuredText"), ("org", "Documento Org"),
            ("diff", "Archivo de diferencias"), ("patch", "Parche"));
        Add(KindCategory.Subtitle, "#6E6E73", ("srt", "Subtítulos SubRip"), ("vtt", "Subtítulos WebVTT"), ("ass", "Subtítulos ASS"),
            ("ssa", "Subtítulos SSA"), ("sub", "Subtítulos"), ("lrc", "Letras sincronizadas"));
        // ---- Web ----
        Add(KindCategory.Web, "#E44D26", ("html", "Página HTML"), ("htm", "Página HTML"), ("xhtml", "Página XHTML"), ("mht", "Archivo web MHTML"),
            ("mhtml", "Archivo web MHTML"), ("url", "Acceso directo a Internet"), ("webloc", "Ubicación web"), ("css", "Hoja de estilos CSS"),
            ("scss", "Hoja de estilos Sass"), ("sass", "Hoja de estilos Sass"), ("less", "Hoja de estilos Less"));
        // ---- Código ----
        Add(KindCategory.Code, "#5856D6", ("cs", "Código fuente C#"), ("csproj", "Proyecto de C#"), ("sln", "Solución de Visual Studio"),
            ("xaml", "Marcado XAML"), ("c", "Código fuente C"), ("h", "Encabezado C"), ("cpp", "Código fuente C++"), ("cc", "Código fuente C++"),
            ("cxx", "Código fuente C++"), ("hpp", "Encabezado C++"), ("m", "Código fuente Objective-C"), ("mm", "Código Objective-C++"),
            ("swift", "Código fuente Swift"), ("java", "Código fuente Java"), ("class", "Clase de Java compilada"), ("jar", "Archivo Java (JAR)"),
            ("kt", "Código fuente Kotlin"), ("kts", "Script de Kotlin"), ("gradle", "Script de Gradle"), ("py", "Script de Python"),
            ("pyw", "Script de Python"), ("ipynb", "Cuaderno de Jupyter"), ("js", "JavaScript"), ("mjs", "Módulo de JavaScript"), ("cjs", "JavaScript"),
            ("jsx", "JavaScript JSX"), ("ts", "TypeScript"), ("tsx", "TypeScript JSX"), ("vue", "Componente de Vue"), ("svelte", "Componente de Svelte"),
            ("php", "Script de PHP"), ("rb", "Script de Ruby"), ("go", "Código fuente Go"), ("rs", "Código fuente Rust"), ("dart", "Código fuente Dart"),
            ("lua", "Script de Lua"), ("pl", "Script de Perl"), ("r", "Script de R"), ("scala", "Código fuente Scala"), ("sql", "Script SQL"),
            ("sh", "Script de shell"), ("bash", "Script de Bash"), ("zsh", "Script de Zsh"), ("ps1", "Script de PowerShell"), ("psm1", "Módulo de PowerShell"),
            ("bat", "Archivo por lotes"), ("cmd", "Script de comandos"), ("vbs", "Script de VBScript"), ("ahk", "Script de AutoHotkey"),
            ("json", "Documento JSON"), ("jsonc", "Documento JSON"), ("xml", "Documento XML"), ("yaml", "Documento YAML"), ("yml", "Documento YAML"),
            ("toml", "Documento TOML"), ("ini", "Configuración"), ("cfg", "Configuración"), ("conf", "Configuración"), ("env", "Variables de entorno"),
            ("gitignore", "Reglas de Git"), ("gitattributes", "Atributos de Git"), ("editorconfig", "Configuración del editor"), ("dockerfile", "Dockerfile"),
            ("makefile", "Makefile"), ("cmake", "Script de CMake"), ("asm", "Código ensamblador"), ("s", "Código ensamblador"), ("hs", "Código Haskell"),
            ("ex", "Código Elixir"), ("exs", "Script de Elixir"), ("erl", "Código Erlang"), ("clj", "Código Clojure"), ("fs", "Código F#"),
            ("vb", "Código Visual Basic"), ("glsl", "Sombreador GLSL"), ("hlsl", "Sombreador HLSL"), ("shader", "Sombreador"), ("gd", "Script de Godot"),
            ("lock", "Archivo de bloqueo"), ("map", "Mapa de código fuente"), ("proto", "Protocol Buffers"), ("graphql", "Esquema GraphQL"),
            ("reg", "Entradas del Registro"), ("inf", "Información de instalación"));
        // ---- Comprimidos ----
        Add(KindCategory.Archive, "#8E8E93", ("zip", "Archivo ZIP"), ("rar", "Archivo RAR"), ("7z", "Archivo 7-Zip"), ("tar", "Archivo tar"),
            ("gz", "Archivo gzip"), ("tgz", "Archivo tar comprimido"), ("bz2", "Archivo bzip2"), ("tbz2", "Archivo tar comprimido"), ("xz", "Archivo XZ"),
            ("txz", "Archivo tar comprimido"), ("zst", "Archivo Zstandard"), ("lz", "Archivo lzip"), ("lzma", "Archivo LZMA"), ("cab", "Archivador de Windows"),
            ("arj", "Archivo ARJ"), ("z", "Archivo comprimido"), ("sit", "Archivo StuffIt"), ("sitx", "Archivo StuffIt"), ("apk", "Paquete de Android"),
            ("aab", "Paquete de Android"), ("xapk", "Paquete de Android"), ("ipa", "Aplicación de iOS"), ("whl", "Paquete de Python"),
            ("nupkg", "Paquete NuGet"), ("deb", "Paquete Debian"), ("rpm", "Paquete RPM"), ("crx", "Extensión de Chrome"), ("xpi", "Extensión de Firefox"));
        Add(KindCategory.DiskImage, "#636366", ("iso", "Imagen de disco"), ("img", "Imagen de disco"), ("dmg", "Imagen de disco de Apple"),
            ("vhd", "Disco duro virtual"), ("vhdx", "Disco duro virtual"), ("vmdk", "Disco virtual de VMware"), ("vdi", "Disco virtual de VirtualBox"),
            ("qcow2", "Disco virtual QEMU"), ("wim", "Imagen de Windows"), ("esd", "Imagen de Windows"), ("bin", "Imagen binaria"), ("cue", "Hoja CUE"),
            ("nrg", "Imagen de Nero"), ("mdf", "Imagen de disco"), ("ova", "Aplicación virtual"), ("ovf", "Aplicación virtual"));
        // ---- Fuentes ----
        Add(KindCategory.Font, "#1C1C1E", ("ttf", "Fuente TrueType"), ("otf", "Fuente OpenType"), ("ttc", "Colección de fuentes TrueType"),
            ("woff", "Fuente web WOFF"), ("woff2", "Fuente web WOFF2"), ("fon", "Fuente de mapa de bits"), ("pfb", "Fuente PostScript Type 1"),
            ("pfm", "Métricas de fuente PostScript"), ("eot", "Fuente Embedded OpenType"), ("dfont", "Fuente de Mac"));
        // ---- Ejecutables ----
        Add(KindCategory.Executable, "#3A3A3C", ("exe", "Aplicación"), ("msi", "Paquete de Windows Installer"), ("msix", "Paquete de aplicación"),
            ("msixbundle", "Paquete de aplicación"), ("appx", "Paquete de aplicación"), ("appxbundle", "Paquete de aplicación"), ("com", "Aplicación MS-DOS"),
            ("scr", "Protector de pantalla"), ("dll", "Biblioteca de aplicación"), ("sys", "Archivo de sistema"), ("ocx", "Control ActiveX"),
            ("cpl", "Elemento del Panel de control"), ("msc", "Consola de administración"), ("app", "Aplicación de Mac"), ("appimage", "Aplicación de Linux"),
            ("run", "Instalador de Linux"), ("pkg", "Paquete de instalación"), ("gadget", "Gadget"));
        Add(KindCategory.Shortcut, "#3A3A3C", ("lnk", "Alias"), ("appref-ms", "Acceso directo de aplicación"), ("pif", "Acceso directo MS-DOS"));
        // ---- 3D / CAD ----
        Add(KindCategory.Model3D, "#FF9500", ("blend", "Archivo de Blender"), ("blend1", "Copia de seguridad de Blender"), ("fbx", "Modelo FBX"),
            ("obj", "Modelo OBJ"), ("mtl", "Materiales OBJ"), ("stl", "Modelo STL"), ("3mf", "Modelo 3MF"), ("gltf", "Modelo glTF"), ("glb", "Modelo glTF binario"),
            ("usd", "Escena USD"), ("usda", "Escena USD"), ("usdz", "Escena USD comprimida"), ("dae", "Modelo COLLADA"), ("3ds", "Modelo de 3ds Max"),
            ("max", "Escena de 3ds Max"), ("ma", "Escena de Maya"), ("mb", "Escena de Maya"), ("c4d", "Escena de Cinema 4D"), ("abc", "Caché Alembic"),
            ("ply", "Modelo PLY"), ("ztl", "Herramienta de ZBrush"), ("zpr", "Proyecto de ZBrush"), ("skp", "Modelo de SketchUp"), ("hip", "Escena de Houdini"),
            ("uasset", "Recurso de Unreal Engine"), ("umap", "Mapa de Unreal Engine"), ("unity", "Escena de Unity"), ("prefab", "Prefab de Unity"),
            ("gcode", "Instrucciones G-code"));
        Add(KindCategory.Cad, "#FF9500", ("dwg", "Dibujo de AutoCAD"), ("dwt", "Plantilla de AutoCAD"), ("dgn", "Dibujo de MicroStation"),
            ("step", "Modelo STEP"), ("stp", "Modelo STEP"), ("iges", "Modelo IGES"), ("igs", "Modelo IGES"), ("sldprt", "Pieza de SolidWorks"),
            ("sldasm", "Ensamblaje de SolidWorks"), ("slddrw", "Dibujo de SolidWorks"), ("ipt", "Pieza de Inventor"), ("iam", "Ensamblaje de Inventor"),
            ("f3d", "Diseño de Fusion 360"), ("rvt", "Modelo de Revit"), ("rfa", "Familia de Revit"), ("ifc", "Modelo BIM IFC"), ("3dm", "Modelo de Rhino"),
            ("catpart", "Pieza de CATIA"), ("prt", "Pieza CAD"), ("scad", "Modelo de OpenSCAD"), ("kicad_pcb", "Placa de KiCad"), ("sch", "Esquemático"));
        // ---- Bases de datos ----
        Add(KindCategory.Database, "#AF52DE", ("db", "Base de datos"), ("sqlite", "Base de datos SQLite"), ("sqlite3", "Base de datos SQLite"),
            ("mdb", "Base de datos de Access"), ("accdb", "Base de datos de Access"), ("dbf", "Base de datos dBASE"), ("frm", "Tabla de MySQL"),
            ("ibd", "Tabla InnoDB"), ("bak", "Copia de seguridad"), ("realm", "Base de datos Realm"), ("parquet", "Datos Parquet"), ("feather", "Datos Feather"),
            ("h5", "Datos HDF5"), ("pkl", "Datos serializados de Python"), ("npy", "Matriz de NumPy"), ("mat", "Datos de MATLAB"), ("sav", "Datos de SPSS"),
            ("dta", "Datos de Stata"), ("kml", "Mapa KML"), ("kmz", "Mapa KMZ"), ("gpx", "Ruta GPS"), ("geojson", "Mapa GeoJSON"), ("shp", "Shapefile de GIS"));
        // ---- Sistema ----
        Add(KindCategory.System, "#8E8E93", ("tmp", "Archivo temporal"), ("temp", "Archivo temporal"), ("dmp", "Volcado de memoria"),
            ("etl", "Seguimiento de eventos"), ("evtx", "Registro de eventos"), ("cat", "Catálogo de seguridad"), ("mui", "Recursos de idioma"),
            ("ds_store", "Ajustes de carpeta de Mac"), ("crdownload", "Descarga de Chrome sin terminar"), ("part", "Descarga sin terminar"),
            ("partial", "Descarga sin terminar"), ("torrent", "Archivo torrent"), ("cer", "Certificado"), ("crt", "Certificado"), ("pem", "Certificado PEM"),
            ("pfx", "Certificado personal"), ("p12", "Certificado personal"), ("key", "Clave"), ("pub", "Clave pública"), ("ics", "Evento de calendario"),
            ("vcf", "Tarjeta de contacto"), ("eml", "Mensaje de correo"), ("msg", "Mensaje de Outlook"), ("pst", "Datos de Outlook"), ("ost", "Datos de Outlook"),
            ("ttx", "Fuente XML"), ("theme", "Tema de Windows"), ("deskthemepack", "Tema de Windows"), ("icm", "Perfil de color ICC"), ("icc", "Perfil de color ICC"));
    }

    private static void Add(KindCategory cat, string color, params (string ext, string name)[] items)
    {
        foreach (var (ext, name) in items)
            Map.TryAdd(ext, new KindInfo(name, cat, BadgeFor(ext), color));
    }

    private static void AddAdobe(string ext, string name, string badge, string bg, string fg)
        => Map[ext] = new KindInfo(name, KindCategory.Adobe, badge, bg, fg);

    private static string BadgeFor(string ext) => ext.Length <= 4 ? ext.ToUpperInvariant() : ext[..4].ToUpperInvariant();

    /// <summary>Tipo según extensión (sin el punto). Nunca null.</summary>
    public static KindInfo FromExtension(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return Unknown;
        if (ext[0] == '.') ext = ext[1..];
        if (Map.TryGetValue(ext, out var k)) return k;
        return new KindInfo("Documento " + ext.ToUpperInvariant(), KindCategory.Other, BadgeFor(ext), "#8E8E93");
    }

    public static KindInfo ForPath(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return FromExtension("app");
            return FolderKind;
        }
        var name = Path.GetFileName(path);
        if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) return FromExtension("dockerfile");
        if (name.Equals("Makefile", StringComparison.OrdinalIgnoreCase)) return FromExtension("makefile");
        if (name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)) return FromExtension("ds_store");
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext) || ext.Length == name.Length && name.StartsWith('.') && Map.ContainsKey(name[1..]))
            return string.IsNullOrEmpty(ext) ? Unknown : FromExtension(name[1..]);
        return FromExtension(ext);
    }

    public static List<string> ExtensionsInGroup(string group) =>
        Map.Where(kv => GroupOf(kv.Value.Category) == group).Select(kv => kv.Key).ToList();

    public static bool IsKnown(string ext) => !string.IsNullOrEmpty(ext) && Map.ContainsKey(ext.TrimStart('.'));

    public static string GroupOf(KindCategory c) => c switch
    {
        KindCategory.Folder => "carpeta",
        KindCategory.Image or KindCategory.RawPhoto or KindCategory.Vector => "imagen",
        KindCategory.Video => "video",
        KindCategory.Audio => "audio",
        KindCategory.Pdf => "pdf",
        KindCategory.Adobe or KindCategory.Design => "adobe",
        KindCategory.Document or KindCategory.Spreadsheet or KindCategory.Presentation or KindCategory.Ebook or KindCategory.Text => "documento",
        KindCategory.Code or KindCategory.Web => "codigo",
        KindCategory.Archive or KindCategory.DiskImage => "comprimido",
        KindCategory.Font => "fuente",
        KindCategory.Executable or KindCategory.Shortcut => "app",
        KindCategory.Model3D or KindCategory.Cad => "3d",
        _ => "otro"
    };

    /// <summary>Grupos aceptados por el filtro "tipo:" de la búsqueda (con sinónimos).</summary>
    public static readonly Dictionary<string, string> SearchAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["carpeta"] = "carpeta", ["carpetas"] = "carpeta", ["folder"] = "carpeta",
        ["imagen"] = "imagen", ["imagenes"] = "imagen", ["imágenes"] = "imagen", ["foto"] = "imagen", ["fotos"] = "imagen", ["image"] = "imagen",
        ["video"] = "video", ["vídeo"] = "video", ["videos"] = "video", ["pelicula"] = "video", ["película"] = "video", ["movie"] = "video",
        ["audio"] = "audio", ["musica"] = "audio", ["música"] = "audio", ["music"] = "audio",
        ["pdf"] = "pdf",
        ["adobe"] = "adobe", ["diseño"] = "adobe", ["diseno"] = "adobe",
        ["documento"] = "documento", ["documentos"] = "documento", ["doc"] = "documento", ["texto"] = "documento",
        ["codigo"] = "codigo", ["código"] = "codigo", ["code"] = "codigo",
        ["comprimido"] = "comprimido", ["zip"] = "comprimido", ["archivo"] = "comprimido",
        ["fuente"] = "fuente", ["fuentes"] = "fuente", ["font"] = "fuente",
        ["app"] = "app", ["aplicacion"] = "app", ["aplicación"] = "app", ["programa"] = "app",
        ["3d"] = "3d", ["cad"] = "3d", ["modelo"] = "3d",
    };

    // ------------------------------------------------------------------
    // Detección por contenido (firmas). Se usa en Obtener información y en
    // archivos sin extensión; no en el listado (sería abrir cada archivo).
    // ------------------------------------------------------------------

    /// <summary>Lee la cabecera del archivo y devuelve la extensión real detectada, o null.</summary>
    public static string SniffExtension(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            var buf = new byte[4096];
            int n = fs.Read(buf, 0, buf.Length);
            var ext = Sniff(buf.AsSpan(0, n));
            if (ext == null && fs.Length > 32774)
            {
                fs.Position = 32769; var iso = new byte[5];
                if (fs.Read(iso, 0, 5) == 5 && System.Text.Encoding.ASCII.GetString(iso) == "CD001") return "iso";
            }
            return ext;
        }
        catch { return null; }
    }

    public static string Sniff(ReadOnlySpan<byte> span) => Sniff(span.ToArray());

    public static string Sniff(byte[] b)
    {
        bool S(int off, params byte[] sig) => b.Length >= off + sig.Length && b.AsSpan(off, sig.Length).SequenceEqual(sig);
        bool A(int off, string ascii) => S(off, System.Text.Encoding.ASCII.GetBytes(ascii));

        if (S(0, 0xFF, 0xD8, 0xFF)) return "jpg";
        if (S(0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return "png";
        if (A(0, "GIF87a") || A(0, "GIF89a")) return "gif";
        if (A(0, "8BPS")) return b.Length > 5 && b[5] == 2 ? "psb" : "psd";
        if (A(0, "%PDF"))
        {
            var head = System.Text.Encoding.ASCII.GetString(b);
            return head.Contains("Illustrator", StringComparison.Ordinal) ? "ai" : "pdf";
        }
        if (A(0, "%!PS-Adobe"))
        {
            var head = System.Text.Encoding.ASCII.GetString(b);
            if (head.Contains("Illustrator", StringComparison.Ordinal)) return "ai";
            return "eps";
        }
        if (S(0, 0xC5, 0xD0, 0xD3, 0xC6)) return "eps";
        if (S(0, 0x06, 0x06, 0xED, 0xF5, 0xD8, 0x1D, 0x46, 0xE5, 0xBD, 0x31, 0xEF, 0xE7, 0xFE, 0x74, 0xB7, 0x1D)) return "indd";
        if (A(0, "RIFX") && A(8, "Egg!")) return "aep";
        if (A(0, "RIFF"))
        {
            if (A(8, "WEBP")) return "webp";
            if (A(8, "WAVE")) return "wav";
            if (A(8, "AVI ")) return "avi";
        }
        if (A(4, "ftyp"))
        {
            var brand = b.Length >= 12 ? System.Text.Encoding.ASCII.GetString(b.AsSpan(8, 4)) : "";
            return brand switch
            {
                "heic" or "heix" or "hevc" or "heim" or "heis" or "mif1" or "msf1" => "heic",
                "avif" or "avis" => "avif",
                "qt  " => "mov",
                "M4A " or "M4B " => "m4a",
                "M4V " or "M4VH" or "M4VP" => "m4v",
                "crx " => "cr3",
                "3gp4" or "3gp5" or "3gp6" or "3ge6" => "3gp",
                _ => "mp4"
            };
        }
        if (S(0, 0x1A, 0x45, 0xDF, 0xA3))
        {
            var head = System.Text.Encoding.ASCII.GetString(b);
            return head.Contains("webm", StringComparison.Ordinal) ? "webm" : "mkv";
        }
        if (A(0, "II*\0") || A(0, "MM\0*"))
        {
            if (A(8, "CR")) return "cr2";
            return "tif";
        }
        if (A(0, "BM") && b.Length > 14) return "bmp";
        if (A(0, "ID3") || S(0, 0xFF, 0xFB) || S(0, 0xFF, 0xF3) || S(0, 0xFF, 0xF2)) return "mp3";
        if (A(0, "fLaC")) return "flac";
        if (A(0, "OggS")) return "ogg";
        if (A(0, "FORM") && (A(8, "AIFF") || A(8, "AIFC"))) return "aif";
        if (A(0, "MThd")) return "mid";
        if (S(0, 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11)) return "wmv";
        if (A(0, "FLV")) return "flv";
        if (S(0, 0x00, 0x00, 0x01, 0xBA) || S(0, 0x00, 0x00, 0x01, 0xB3)) return "mpg";
        if (A(0, "PK\x03\x04"))
        {
            var head = System.Text.Encoding.ASCII.GetString(b);
            if (head.Contains("word/", StringComparison.Ordinal)) return "docx";
            if (head.Contains("xl/", StringComparison.Ordinal)) return "xlsx";
            if (head.Contains("ppt/", StringComparison.Ordinal)) return "pptx";
            if (head.Contains("mimetypeapplication/epub+zip", StringComparison.Ordinal)) return "epub";
            if (head.Contains("mimetypeapplication/vnd.adobe.indesign-idml-package", StringComparison.Ordinal)) return "idml";
            if (head.Contains("mimetypeapplication/vnd.adobe.sparkler", StringComparison.Ordinal) || head.Contains("artwork/", StringComparison.Ordinal)) return "xd";
            if (head.Contains("AndroidManifest.xml", StringComparison.Ordinal)) return "apk";
            if (head.Contains("META-INF/MANIFEST.MF", StringComparison.Ordinal)) return "jar";
            if (head.Contains("mimetypeapplication/vnd.oasis.opendocument.text", StringComparison.Ordinal)) return "odt";
            return "zip";
        }
        if (A(0, "Rar!\x1A\x07")) return "rar";
        if (S(0, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return "7z";
        if (S(0, 0x1F, 0x8B))
        {
            return "gz"; // .prproj también es gzip; sin extensión no se distingue barato
        }
        if (S(0, 0x42, 0x5A, 0x68)) return "bz2";
        if (S(0, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00)) return "xz";
        if (S(0, 0x28, 0xB5, 0x2F, 0xFD)) return "zst";
        if (A(0, "MSCF")) return "cab";
        if (A(0, "MZ")) return "exe";
        if (S(0, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1)) return "doc"; // OLE: doc/xls/ppt/msi
        if (A(0, "SQLite format 3")) return "sqlite";
        if (A(0, "{\\rtf")) return "rtf";
        if (A(0, "BLENDER")) return "blend";
        if (A(0, "Kaydara FBX Binary")) return "fbx";
        if (A(0, "glTF")) return "glb";
        if (A(0, "solid ") ) return "stl";
        if (S(0, 0x00, 0x01, 0x00, 0x00, 0x00) || A(0, "true")) return "ttf";
        if (A(0, "OTTO")) return "otf";
        if (A(0, "wOFF")) return "woff";
        if (A(0, "wOF2")) return "woff2";
        if (A(0, "ttcf")) return "ttc";
        if (S(0, 0x00, 0x00, 0x01, 0x00)) return "ico";
        if (A(0, "8BIM") || A(0, "8BAM")) return "abr";
        if (A(0, "AC10")) return "dwg";
        if (A(0, "vhdxfile")) return "vhdx";
        if (A(0, "conectix")) return "vhd";
        if (A(0, "KDM")) return "vmdk";
        if (A(0, "MSWIM")) return "wim";
        if (A(0, "koly")) return "dmg";
        if (A(0, "\0\0\0\fjP  ")) return "jp2";
        if (S(0, 0xFF, 0x0A) || S(0, 0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20)) return "jxl";
        if (A(0, "gimp xcf")) return "xcf";
        if (A(0, "L\0\0\0\x01\x14\x02\0")) return "lnk";

        // Texto: detectar tipos por contenido
        if (LooksLikeText(b))
        {
            var t = System.Text.Encoding.UTF8.GetString(b).TrimStart('﻿', ' ', '\r', '\n', '\t');
            if (t.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
                return t.Contains("<svg", StringComparison.OrdinalIgnoreCase) ? "svg" : "xml";
            if (t.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)) return "svg";
            if (t.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || t.StartsWith("<html", StringComparison.OrdinalIgnoreCase)) return "html";
            if (t.StartsWith("{") || t.StartsWith("[")) return "json";
            if (t.StartsWith("#!/bin/bash") || t.StartsWith("#!/bin/sh") || t.StartsWith("#!/usr/bin/env bash")) return "sh";
            if (t.StartsWith("#!/usr/bin/env python") || t.StartsWith("#!/usr/bin/python")) return "py";
            if (t.StartsWith("WEBVTT")) return "vtt";
            return "txt";
        }
        return null;
    }

    public static bool LooksLikeText(ReadOnlySpan<byte> b)
    {
        if (b.Length == 0) return true;
        int ctrl = 0;
        int n = Math.Min(b.Length, 2048);
        for (int i = 0; i < n; i++)
        {
            byte c = b[i];
            if (c == 0) return false;
            if (c < 7 || (c > 13 && c < 32 && c != 27)) ctrl++;
        }
        return ctrl < n / 50 + 1;
    }

    // ---- extensiones con vista previa nativa (Visualización rápida) ----
    public static bool IsWpfImage(string ext) => ext is "jpg" or "jpeg" or "jpe" or "jfif" or "png" or "gif" or "bmp" or "dib" or "tif" or "tiff" or "ico" or "wdp" or "jxr";
    public static bool IsPlayable(KindCategory c) => c is KindCategory.Video or KindCategory.Audio;
}
