using System.Text.Json;

namespace Faro.Core.Transfer;

/// <summary>
/// Diario en disco de una operación. Si la app se cierra, se cuelga o se va la luz,
/// al volver a abrir Faro se ofrece reanudar: lo ya copiado (mismo tamaño y fecha) se
/// salta y el archivo grande que quedó a medias continúa desde el último punto confirmado.
/// </summary>
public sealed class TransferJournal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TransferKind Kind { get; set; }
    public List<string> Sources { get; set; } = new();
    public string Destination { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public ConflictChoice? DefaultConflict { get; set; }
    public string PartialFile { get; set; }
    public long PartialFlushedOffset { get; set; }
    public long PartialSourceSize { get; set; }
    public DateTime PartialSourceModifiedUtc { get; set; }
    public long TotalBytes { get; set; }
    public long DoneBytes { get; set; }

    public static string Folder
    {
        get
        {
            var d = Path.Combine(AppInfo.LocalDir, "jobs");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private string FilePath => Path.Combine(Folder, Id.ToString("N") + ".json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };
    private readonly object _lock = new();

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
                File.Move(tmp, FilePath, true);
            }
            catch { /* el diario es una ayuda, nunca debe tumbar la copia */ }
        }
    }

    public void Delete()
    {
        lock (_lock)
        {
            try { File.Delete(FilePath); } catch { }
        }
    }

    public static List<TransferJournal> LoadPending()
    {
        var list = new List<TransferJournal>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(Folder, "*.json"))
            {
                try
                {
                    var j = JsonSerializer.Deserialize<TransferJournal>(File.ReadAllText(f));
                    if (j != null && j.Sources.Count > 0) list.Add(j);
                    else File.Delete(f);
                }
                catch { try { File.Delete(f); } catch { } }
            }
        }
        catch { }
        return list;
    }
}
