using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Faro.Core;
using Faro.Services;

namespace Faro.Model;

public enum ItemSpecial { None, Drive, App, TrashEntry, Server }

/// <summary>Un elemento visible en cualquiera de las vistas.</summary>
public sealed class FileItem : INotifyPropertyChanged
{
    public string Path { get; private set; }
    public string Name { get; private set; }
    public bool IsDirectory { get; }
    public long Size { get; private set; }
    public DateTime Modified { get; private set; }
    public DateTime Created { get; private set; }
    public FileAttributes Attributes { get; private set; }
    public KindInfo Kind { get; private set; }
    public ItemSpecial Special { get; init; }

    /// <summary>Para resultados de búsqueda y la Papelera: carpeta donde está (o estaba).</summary>
    public string Location { get; init; }
    /// <summary>Para una unidad: espacio libre. Para la papelera: ruta física del $R.</summary>
    public long Extra { get; init; }
    /// <summary>Para una unidad: tamaño total (Size de una carpeta siempre es -1).</summary>
    public long Capacity { get; init; }
    public string ExtraPath { get; init; }
    public string LaunchPath { get; init; }
    public DriveKindHint DriveHint { get; init; }

    public FileItem(string path, string name, bool isDir, long size, DateTime modified, DateTime created, FileAttributes attrs)
    {
        Path = path; Name = name; IsDirectory = isDir; Size = isDir ? -1 : size;
        Modified = modified; Created = created; Attributes = attrs;
        Kind = FileKinds.ForPath(name, isDir);
    }

    public static FileItem FromPath(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            if (di.Exists) return new FileItem(path, NameOf(path), true, -1, di.LastWriteTime, di.CreationTime, di.Attributes);
            var fi = new FileInfo(path);
            if (fi.Exists) return new FileItem(path, fi.Name, false, fi.Length, fi.LastWriteTime, fi.CreationTime, fi.Attributes);
        }
        catch { }
        return null;
    }

    public static string NameOf(string path)
    {
        var n = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        return string.IsNullOrEmpty(n) ? path : n;
    }

    public string Extension => IsDirectory ? "" : System.IO.Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0 || (Name.StartsWith('.') && Name.Length > 1);
    public bool IsShortcut => !IsDirectory && Extension is "lnk" or "url" or "appref-ms";
    public bool IsNavigable => IsDirectory || Special == ItemSpecial.Drive;

    // ---------- textos para las columnas ----------
    public string DisplayName
    {
        get
        {
            if (Special == ItemSpecial.App && Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return Name[..^4];
            if (IsDirectory || Settings.Current.ShowAllExtensions || Special != ItemSpecial.None) return Name;
            if (Extension is "lnk" or "url" or "appref-ms") return System.IO.Path.GetFileNameWithoutExtension(Name);
            return Name;
        }
    }

    // ---------- espacio de un disco ----------
    public bool IsDrive => Special == ItemSpecial.Drive && Capacity > 0;
    public double UsedFraction => Capacity > 0 ? Math.Clamp((Capacity - Extra) / (double)Capacity, 0, 1) : 0;
    public bool IsAlmostFull => Capacity > 0 && Extra < Capacity * 0.1;
    public string FreeText => $"{Format.Size(Extra)} libres de {Format.Size(Capacity)}";

    public string SizeText => Special == ItemSpecial.Drive ? Format.Size(Capacity) : (IsDirectory ? (_folderSize >= 0 ? Format.Size(_folderSize) : "--") : Format.Size(Size));
    public string ModifiedText => Special == ItemSpecial.Drive ? "--" : Format.Date(Modified);
    public string CreatedText => Format.Date(Created);
    public string KindText => Special switch
    {
        ItemSpecial.Drive => DriveHint switch
        {
            DriveKindHint.Removable => "Disco externo",
            DriveKindHint.Network => "Volumen de red",
            DriveKindHint.Optical => "Unidad óptica",
            _ => "Volumen"
        } + (Capacity > 0 ? $" · {Format.Size(Extra)} libres" : ""),
        ItemSpecial.Server => Location ?? "Servidor",
        ItemSpecial.App => "Aplicación",
        _ => Kind.Name
    };
    public string LocationText => Location == null || Special == ItemSpecial.Server ? "" : FileItem.NameOf(Location);
    public double ItemOpacity => (IsHidden ? 0.55 : 1.0) * (_isCut ? 0.5 : 1.0);

    private long _folderSize = -1;
    public void SetFolderSize(long s) { _folderSize = s; OnChanged(nameof(SizeText)); }

    // ---------- icono / miniatura (perezoso) ----------
    private ImageSource _thumb;
    private int _thumbSize;
    private bool _thumbRequested;
    private ImageSource _placeholder;

    public ImageSource Icon
    {
        get
        {
            int want = ThumbnailLoader.CurrentSize;
            if ((!_thumbRequested || (_thumb != null && _thumbSize < want)) && ThumbnailLoader.Wants(this))
            {
                _thumbRequested = true;
                ThumbnailLoader.Enqueue(this, want);
            }
            return _thumb ?? (_placeholder ??= IconFactory.For(this));
        }
    }

    public bool HasThumbnail => _thumb != null;

    /// <summary>Para la precarga: marca como pedido (false si ya lo estaba).</summary>
    internal bool TryMarkRequested()
    {
        if (_thumbRequested) return false;
        _thumbRequested = true;
        return true;
    }

    private bool _thumbJustLoaded;
    /// <summary>Sólo es true unos instantes cuando llega la miniatura: la vista hace el fundido entonces (no al reciclar celdas).</summary>
    public bool ThumbJustLoaded { get => _thumbJustLoaded; set { if (_thumbJustLoaded != value) { _thumbJustLoaded = value; OnChanged(); } } }

    /// <summary>Apaga ThumbJustLoaded en lote con un solo temporizador.</summary>
    private static class ThumbFlash
    {
        private static readonly List<FileItem> Pending = new();
        private static System.Windows.Threading.DispatcherTimer _t;
        public static void Add(FileItem i)
        {
            Pending.Add(i);
            if (_t == null) { _t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) }; _t.Tick += (_, _) => { _t.Stop(); foreach (var p in Pending) p.ThumbJustLoaded = false; Pending.Clear(); }; }
            _t.Stop(); _t.Start();
        }
    }

    internal void SetThumbnail(ImageSource img, int size)
    {
        if (img == null) return;
        if (_thumb != null && _thumbSize > size) return;
        bool first = _thumb == null;
        _thumb = img; _thumbSize = size;
        if (first) { ThumbJustLoaded = true; ThumbFlash.Add(this); }
        OnChanged(nameof(Icon));
        OnChanged(nameof(HasThumbnail));
    }

    public void InvalidateThumbnail(bool hard = false)
    {
        _thumbRequested = false;
        if (hard) { _thumb = null; _thumbSize = 0; _placeholder = null; }
        OnChanged(nameof(Icon));
    }

    // ---------- etiquetas ----------
    private IReadOnlyList<string> _tags;
    public IReadOnlyList<string> Tags => _tags ??= TagStore.HasAny ? TagStore.Get(Path) : Array.Empty<string>();
    public IEnumerable<Brush> TagBrushes => Tags.Select(t => (Brush)Theme.BrushFromHex(TagStore.ColorOf(t)));
    public bool HasTags => Tags.Count > 0;
    public void RefreshTags() { _tags = null; OnChanged(nameof(Tags)); OnChanged(nameof(TagBrushes)); OnChanged(nameof(HasTags)); }

    // ---------- árbol en la vista de lista ----------
    public int Depth { get; set; }
    public double Indent => Depth * 16;
    public FileItem ParentRow { get; set; }
    public List<FileItem> Children { get; set; }
    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set { if (_isExpanded != value) { _isExpanded = value; OnChanged(); } } }
    public bool CanExpand => IsDirectory && Special != ItemSpecial.App;

    // ---------- estado de edición / cortado ----------
    private bool _isEditing;
    public bool IsEditing { get => _isEditing; set { if (_isEditing != value) { _isEditing = value; OnChanged(); } } }
    public string EditName { get; set; }

    private bool _isCut;
    public bool IsCut { get => _isCut; set { if (_isCut != value) { _isCut = value; OnChanged(nameof(ItemOpacity)); } } }

    private bool _isNew, _isRemoving;
    /// <summary>Recién aparecido en la carpeta: la vista lo anima al entrar.</summary>
    public bool IsNew { get => _isNew; set { if (_isNew != value) { _isNew = value; OnChanged(); } } }
    /// <summary>A punto de irse (papelera): la vista lo desvanece.</summary>
    public bool IsRemoving { get => _isRemoving; set { if (_isRemoving != value) { _isRemoving = value; OnChanged(); } } }

    private bool _isDropTarget;
    public bool IsDropTarget { get => _isDropTarget; set { if (_isDropTarget != value) { _isDropTarget = value; OnChanged(); } } }

    /// <summary>Actualiza datos tras un cambio en disco sin crear otro objeto (mantiene la selección).</summary>
    public bool UpdateFrom(FileItem other)
    {
        bool changed = Size != other.Size || Modified != other.Modified || Attributes != other.Attributes;
        if (!changed) return false;
        bool contentChanged = Size != other.Size || Modified != other.Modified;
        Size = other.Size; Modified = other.Modified; Created = other.Created; Attributes = other.Attributes;
        OnChanged(nameof(SizeText)); OnChanged(nameof(ModifiedText)); OnChanged(nameof(ItemOpacity));
        if (contentChanged) InvalidateThumbnail(true);
        return true;
    }

    public void Rename(string newPath)
    {
        Path = newPath;
        Name = NameOf(newPath);
        Kind = FileKinds.ForPath(Name, IsDirectory);
        _placeholder = null;
        InvalidateThumbnail(true);
        OnChanged(nameof(Name)); OnChanged(nameof(DisplayName)); OnChanged(nameof(KindText)); OnChanged(nameof(Path));
    }

    public void NotifyDisplay() { OnChanged(nameof(DisplayName)); }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnChanged([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    public override string ToString() => Name;
}

public enum DriveKindHint { Fixed, System, Removable, Network, Optical }
