using Faro.Core;
using Faro.Core.Transfer;

namespace Faro.Services;

/// <summary>Deshacer (Ctrl+Z) para renombrar, mover, copiar, duplicar, nueva carpeta y trasladar a la papelera.</summary>
public static class UndoManager
{
    private sealed record Action(string Name, Func<Task> Undo);
    private static readonly Stack<Action> Undos = new();

    public static string NextName => Undos.Count > 0 ? Undos.Peek().Name : null;

    private static void Push(string name, Func<Task> undo)
    {
        Undos.Push(new Action(name, undo));
        while (Undos.Count > 50) { var l = Undos.ToList(); l.RemoveAt(l.Count - 1); Undos.Clear(); for (int i = l.Count - 1; i >= 0; i--) Undos.Push(l[i]); }
    }

    public static async Task<bool> UndoAsync()
    {
        if (Undos.Count == 0) return false;
        var a = Undos.Pop();
        await a.Undo();
        return true;
    }

    public static void RegisterRename(string oldPath, string newPath) =>
        Push("Deshacer renombrar", () =>
        {
            if (Directory.Exists(newPath)) Directory.Move(newPath, oldPath);
            else if (File.Exists(newPath)) File.Move(newPath, oldPath);
            TagStore.Relocate(newPath, oldPath);
            return Task.CompletedTask;
        });

    public static void RegisterNewFolder(string path) =>
        Push("Deshacer nueva carpeta", () =>
        {
            if (Directory.Exists(path))
            {
                if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
                else Native.SendToRecycleBin(new[] { path });
            }
            return Task.CompletedTask;
        });

    public static void RegisterTransfer(TransferKind kind, List<(string src, string dst)> placed)
    {
        if (kind == TransferKind.Move)
        {
            Push("Deshacer mover", () =>
            {
                // devolver cada elemento a su carpeta original
                foreach (var g in placed.GroupBy(p => Path.GetDirectoryName(p.src), StringComparer.OrdinalIgnoreCase))
                    TransferManager.Instance.Start(TransferKind.Move, g.Select(p => p.dst), g.Key, registerUndo: false);
                return Task.CompletedTask;
            });
        }
        else
        {
            Push(kind == TransferKind.Duplicate ? "Deshacer duplicar" : "Deshacer copiar", () =>
            {
                var existing = placed.Select(p => p.dst).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                if (existing.Count > 0) Native.SendToRecycleBin(existing);
                return Task.CompletedTask;
            });
        }
    }

    public static void RegisterTrash(List<string> originals, DateTime when) =>
        Push("Deshacer trasladar a la papelera", () => Task.Run(() =>
        {
            foreach (var r in TrashService.FindRecentlyTrashed(originals, when))
            {
                try { TrashService.Restore(r); } catch { }
            }
        }));

    public static void RegisterCreated(string path, string name) =>
        Push(name, () =>
        {
            if (File.Exists(path) || Directory.Exists(path)) Native.SendToRecycleBin(new[] { path });
            return Task.CompletedTask;
        });
}
