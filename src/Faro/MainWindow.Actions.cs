using System.Collections.Specialized;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Faro.Core;
using Faro.Core.Transfer;
using Faro.Dialogs;
using Faro.Model;
using Faro.Services;
using Faro.Shell;

namespace Faro;

/// <summary>Operaciones sobre archivos (lo que hacen los menús, atajos y arrastrar/soltar).</summary>
public partial class MainWindow
{
    public Windows.QuickLookWindow QuickLook { get; private set; }

    private IList<FileItem> Selection => _view?.SelectedItems ?? new List<FileItem>();
    private string ActiveFolder => _view?.ActiveFolder ?? _tab?.FolderPath;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHAddToRecentDocs(uint flags, string path);

    // ======================== abrir ========================

    public void OpenItems(IList<FileItem> items, bool inNewTab = false)
    {
        if (items == null || items.Count == 0) return;
        var folders = items.Where(i => i.IsNavigable && i.Special != ItemSpecial.TrashEntry).ToList();
        var files = items.Where(i => !i.IsNavigable || i.Special == ItemSpecial.TrashEntry).ToList();

        if (files.Any(f => f.Special == ItemSpecial.TrashEntry) || folders.Any(f => f.Special == ItemSpecial.TrashEntry))
        {
            var r = new MacDialog(this, "Para abrir este elemento, primero sácalo de la Papelera.", null,
                new[] { new DialogButton("Cancelar", "c", false, true), new DialogButton("Devolver", "r", true) }).ShowFor();
            if (r == "r") RestoreFromTrash(items.Where(i => i.Special == ItemSpecial.TrashEntry).ToList());
            return;
        }
        if (files.Count > 12 && !MacDialog.Confirm(this, $"¿Seguro que quieres abrir {files.Count} elementos a la vez?", null, "Abrir")) return;

        foreach (var f in files)
        {
            var path = f.Special == ItemSpecial.App ? f.LaunchPath ?? f.Path : f.Path;
            if (f.Extension == "lnk" && f.Special != ItemSpecial.App)
            {
                var target = ShellInterop.ResolveShortcut(f.Path);
                if (target != null && Directory.Exists(target)) { folders.Add(FileItem.FromPath(target)); continue; }
            }
            if (ShellInterop.Open(path, this) && f.Special == ItemSpecial.None) { try { SHAddToRecentDocs(3, path); } catch { } }
        }
        if (folders.Count == 1 && !inNewTab) { NavDirection = 1; _ = _tab.NavigateAsync(Location.Folder(folders[0].Path)); }
        else foreach (var d in folders) _ = AddTab(Location.Folder(d.Path), folders.Count == 1);
    }

    public void SpringLoad(FileItem folder)
    {
        if (folder?.IsNavigable == true) _ = _tab.NavigateAsync(Location.Folder(folder.Path));
    }

    public void ExpandToggle(FileItem item, bool? expand = null, bool recursive = false)
    {
        if (_tab == null || item == null) return;
        bool doExpand = expand ?? !item.IsExpanded;
        if (doExpand) _ = _tab.ExpandAsync(item, recursive);
        else _tab.Collapse(item);
    }

    // ======================== renombrar ========================

    public void BeginRename()
    {
        var sel = Selection;
        if (sel.Count != 1 || sel[0].Special is ItemSpecial.Drive or ItemSpecial.TrashEntry) return;
        if (Protection.Filter(new[] { sel[0].Path }, "renombrar", this).Count == 0) return;
        _view.BeginRename(sel[0]);
    }

    public void CommitRename(FileItem item, string newName)
    {
        if (Protection.Filter(new[] { item.Path }, "renombrar", this).Count == 0) return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MacDialog.Error(this, $"El nombre «{newName}» no se puede usar.", "Los nombres no pueden contener \\ / : * ? \" < > |");
            return;
        }
        if (item.Special == ItemSpecial.App)
        {
            newName = newName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? newName : newName + ".lnk";
        }
        if (!item.IsDirectory && Settings.Current.WarnChangeExtension)
        {
            var oldExt = Path.GetExtension(item.Name); var newExt = Path.GetExtension(newName);
            if (!string.Equals(oldExt, newExt, StringComparison.OrdinalIgnoreCase) && oldExt.Length > 0)
            {
                var r = new MacDialog(this, $"¿Seguro que quieres cambiar la extensión de «{oldExt}» a «{(newExt.Length > 0 ? newExt : "(ninguna)")}»?",
                    "Si cambias la extensión, el archivo podría abrirse con otra aplicación.",
                    new[] { new DialogButton($"Conservar {oldExt}", "keep", true), new DialogButton($"Usar {(newExt.Length > 0 ? newExt : "sin extensión")}", "use") }).ShowFor();
                if (r != "use")
                {
                    if (r == "keep") newName = Path.GetFileNameWithoutExtension(newName) + oldExt;
                    else return;
                    if (newName == item.Name) return;
                }
            }
        }
        var dir = Path.GetDirectoryName(item.Path);
        var newPath = Path.Combine(dir!, newName);
        bool caseOnly = string.Equals(newPath, item.Path, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(newPath) || Directory.Exists(newPath)))
        {
            MacDialog.Error(this, $"El nombre «{newName}» ya está en uso. Elige otro nombre.", null);
            return;
        }
        try
        {
            var old = item.Path;
            if (item.IsDirectory)
            {
                if (caseOnly) { var tmp = old + ".faro-tmp"; Directory.Move(old, tmp); Directory.Move(tmp, newPath); }
                else Directory.Move(old, newPath);
            }
            else File.Move(old, newPath);
            item.Rename(newPath);
            TagStore.Relocate(old, newPath);
            UndoManager.RegisterRename(old, newPath);
            _tab.SelectedPaths.Clear(); _tab.SelectedPaths.Add(newPath);
            _tab.Resort();
        }
        catch (Exception ex)
        {
            MacDialog.Error(this, $"No se pudo renombrar «{item.Name}».", TransferJob.Explain(ex));
        }
    }

    // ======================== crear ========================

    public void NewFolder(bool withSelection = false)
    {
        var folder = ActiveFolder;
        if (folder == null) { System.Media.SystemSounds.Beep.Play(); return; }
        var sel = withSelection ? Selection.Where(i => i.Special == ItemSpecial.None).ToList() : null;
        var path = TransferJob.UniqueName(folder, withSelection ? "Nueva carpeta con elementos" : "carpeta sin título", true, false);
        var basePath = Path.Combine(folder, withSelection ? "Nueva carpeta con elementos" : "carpeta sin título");
        if (!Directory.Exists(basePath)) path = basePath;
        try
        {
            Directory.CreateDirectory(path);
            UndoManager.RegisterNewFolder(path);
            if (sel is { Count: > 0 })
                TransferManager.Instance.Start(TransferKind.Move, sel.Select(s => s.Path), path);
        }
        catch (Exception ex) { MacDialog.Error(this, "No se pudo crear la carpeta.", TransferJob.Explain(ex)); return; }
        _ = SelectWhenAppears(path, rename: true);
    }

    /// <summary>Espera a que el elemento aparezca (vigilancia de carpeta) y lo selecciona.</summary>
    private async Task SelectWhenAppears(string path, bool rename = false)
    {
        for (int i = 0; i < 30; i++)
        {
            var it = _tab.Items.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            if (it == null && i == 3) await _tab.RefreshAsync();
            if (it != null)
            {
                _view.SelectItems(new[] { it });
                if (rename) _view.BeginRename(it);
                return;
            }
            await Task.Delay(60);
        }
    }

    public void NewTextFile()
    {
        var folder = ActiveFolder;
        if (folder == null) return;
        var path = TransferJob.UniqueName(folder, "Documento sin título.txt", false, false);
        var basePath = Path.Combine(folder, "Documento sin título.txt");
        if (!File.Exists(basePath)) path = basePath;
        try { File.WriteAllText(path, ""); UndoManager.RegisterCreated(path, "Deshacer nuevo documento"); }
        catch (Exception ex) { MacDialog.Error(this, "No se pudo crear el documento.", ex.Message); return; }
        _ = SelectWhenAppears(path, rename: true);
    }

    // ======================== papelera ========================

    public void MoveSelectionToTrash()
    {
        var sel = Selection.Where(i => i.Special == ItemSpecial.None || i.Special == ItemSpecial.App).ToList();
        if (_tab?.Location?.Kind == LocationKind.Trash) { DeleteForever(Selection.ToList()); return; }
        if (sel.Count == 0) return;
        // lo protegido se avisa ANTES de desvanecerlo: si no, parecía borrado mientras el aviso estaba abierto
        var allowed = new HashSet<string>(Protection.Filter(sel.Select(i => i.Path), "trasladar a la Papelera", this), StringComparer.OrdinalIgnoreCase);
        sel = sel.Where(i => allowed.Contains(i.Path)).ToList();
        if (sel.Count == 0) return;
        foreach (var s in sel) s.IsRemoving = true; // se desvanecen antes de irse
        MoveToTrash(sel.Select(i => i.Path).ToList());
    }

    public void MoveToTrash(List<string> paths)
    {
        if (paths.Count == 0) return;
        var allowed = Protection.Filter(paths, "trasladar a la Papelera", this);
        if (allowed.Count < paths.Count) SettleRemoved(paths.Except(allowed).ToList()); // lo protegido vuelve a verse
        paths = allowed;
        if (paths.Count == 0) return;
        // en discos extraíbles o de red no hay Papelera: Windows los borraría para siempre
        var noBin = paths.Where(p => !HasRecycleBin(p)).ToList();
        if (noBin.Count > 0)
        {
            var name = noBin.Count == 1 ? $"«{Path.GetFileName(noBin[0])}»" : $"estos {noBin.Count} elementos";
            if (!MacDialog.Confirm(this, $"¿Seguro que quieres eliminar {name} inmediatamente?",
                    "Este disco no tiene Papelera: no podrás deshacer esta acción.", "Eliminar", destructive: true))
            {
                SettleRemoved(paths); // cancelado: lo que se estaba desvaneciendo vuelve a verse
                return;
            }
        }
        var when = DateTime.Now;
        int idx = _tab.Items.IndexOf(Selection.FirstOrDefault());
        Task.Run(() =>
        {
            int r = Native.SendToRecycleBin(paths);
            Dispatcher.BeginInvoke(() =>
            {
                if (r != 0 && paths.Any(p => File.Exists(p) || Directory.Exists(p)))
                    MacDialog.Error(this, "No se pudo trasladar a la Papelera.", "Puede que un archivo esté en uso o que no tengas permiso.");
                foreach (var p in paths) TagStore.Forget(p);
                SettleRemoved(paths);
                if (noBin.Count < paths.Count) UndoManager.RegisterTrash(paths.Except(noBin).ToList(), when);
                BuildSidebar();
                _ = SelectAfterRemoval(idx);
            });
        });
    }

    /// <summary>
    /// Tras intentar borrar: lo que ya no existe se quita de todas las pestañas abiertas y lo que sigue
    /// existiendo (cancelado, en uso, sin permiso) vuelve a verse. En Aplicaciones, Recientes o una
    /// etiqueta no hay vigilante de carpeta que lo quite: el elemento se quedaba invisible y dejaba un hueco.
    /// </summary>
    private static void SettleRemoved(List<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var w in Application.Current.Windows.OfType<MainWindow>())
            foreach (var t in w.Tabs)
            {
                var hits = t.Items.Where(i => set.Contains(i.Path)).ToList();
                foreach (var it in hits)
                {
                    if (File.Exists(it.Path) || Directory.Exists(it.Path)) { it.IsRemoving = false; continue; }
                    if (it.IsExpanded) t.Collapse(it);
                    t.Items.Remove(it);
                }
            }
    }

    private async Task SelectAfterRemoval(int idx)
    {
        await Task.Delay(350);
        if (_tab.Items.Count == 0 || idx < 0) return;
        var it = _tab.Items[Math.Min(idx, _tab.Items.Count - 1)];
        _view.SelectItems(new[] { it });
    }

    private static bool HasRecycleBin(string p)
    {
        try
        {
            if (p.StartsWith(@"\\")) return false;
            var d = new DriveInfo(Path.GetPathRoot(p)!);
            return d.DriveType == DriveType.Fixed && !Native.IsExternalBus(d.Name);
        }
        catch { return false; }
    }

    public void DeleteImmediately()
    {
        var sel = Selection.Where(i => i.Special is ItemSpecial.None or ItemSpecial.TrashEntry).ToList();
        if (sel.Count == 0) return;
        if (sel.Any(s => s.Special == ItemSpecial.TrashEntry)) { DeleteForever(sel); return; }
        var name = sel.Count == 1 ? $"«{sel[0].Name}»" : $"estos {sel.Count} elementos";
        if (!MacDialog.Confirm(this, $"¿Seguro que quieres eliminar {name} inmediatamente?", "No podrás deshacer esta acción.", "Eliminar", destructive: true)) return;
        Task.Run(() =>
        {
            var errors = new List<string>();
            foreach (var s in sel)
            {
                try
                {
                    if (Directory.Exists(s.Path)) { ClearReadOnlyTree(s.Path); Directory.Delete(s.Path, true); }
                    else { File.SetAttributes(s.Path, FileAttributes.Normal); File.Delete(s.Path); }
                    TagStore.Forget(s.Path);
                }
                catch (Exception ex) { errors.Add($"{s.Name}: {TransferJob.Explain(ex)}"); }
            }
            Dispatcher.BeginInvoke(() =>
            {
                SettleRemoved(sel.Select(s => s.Path).ToList());
                if (errors.Count > 0) MacDialog.Error(this, "Algunos elementos no se pudieron eliminar.", string.Join("\n", errors.Take(6)));
            });
        });
    }

    private static void ClearReadOnlyTree(string dir)
    {
        foreach (var f in Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            try { var a = File.GetAttributes(f); if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, a & ~FileAttributes.ReadOnly); } catch { }
        }
    }

    private void DeleteForever(List<FileItem> items)
    {
        var trashItems = items.Where(i => i.Special == ItemSpecial.TrashEntry).ToList();
        if (trashItems.Count == 0) return;
        var name = trashItems.Count == 1 ? $"«{trashItems[0].Name}»" : $"estos {trashItems.Count} elementos";
        if (!MacDialog.Confirm(this, $"¿Seguro que quieres eliminar {name} para siempre?", "No podrás deshacer esta acción.", "Eliminar", destructive: true)) return;
        Task.Run(() =>
        {
            foreach (var t in trashItems) { try { TrashService.DeleteForever(t.Path); } catch { } }
            Dispatcher.BeginInvoke(async () => { await _tab.LoadAsync(); BuildSidebar(); });
        });
    }

    public void RestoreFromTrash(List<FileItem> items)
    {
        var errors = new List<string>();
        foreach (var t in items.Where(i => i.Special == ItemSpecial.TrashEntry))
        {
            try { TrashService.Restore(t.Path); }
            catch (Exception ex) { errors.Add($"{t.Name}: {ex.Message}"); }
        }
        if (errors.Count > 0) MacDialog.Error(this, "No se pudieron devolver algunos elementos.", string.Join("\n", errors));
        _ = _tab.LoadAsync();
        BuildSidebar();
    }

    public void EmptyTrash()
    {
        var (items, bytes) = TrashService.Info();
        if (items == 0) return;
        if (Settings.Current.WarnBeforeEmptyTrash &&
            !MacDialog.Confirm(this, "¿Seguro que quieres eliminar para siempre los elementos de la Papelera?",
                $"{Format.Items(items)} ({Format.Size(bytes)}). No podrás deshacer esta acción.", "Vaciar la Papelera", destructive: true)) return;
        Task.Run(() =>
        {
            Native.EmptyRecycleBin();
            Dispatcher.BeginInvoke(() =>
            {
                BuildSidebar();
                if (_tab?.Location?.Kind == LocationKind.Trash) _ = _tab.LoadAsync();
            });
        });
    }

    // ======================== portapapeles ========================

    private static readonly List<FileItem> CutItems = new();

    public void CopySelection(bool cut = false)
    {
        var sel = Selection.Where(i => i.Special != ItemSpecial.TrashEntry).ToList();
        if (sel.Count == 0) return;
        var data = new DataObject();
        var list = new StringCollection();
        list.AddRange(sel.Select(s => s.Special == ItemSpecial.App ? s.LaunchPath ?? s.Path : s.Path).ToArray());
        data.SetFileDropList(list);
        data.SetText(string.Join(Environment.NewLine, list.Cast<string>()));
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        try { Clipboard.SetDataObject(data, true); } catch { }
        foreach (var c in CutItems) c.IsCut = false;
        CutItems.Clear();
        if (cut) foreach (var s in sel) { s.IsCut = true; CutItems.Add(s); }
    }

    /// <summary>Pegar. move=true es Ctrl+Alt+V (como Cmd+Opción+V: mover aquí).</summary>
    public void Paste(bool forceMove = false)
    {
        var folder = ActiveFolder;
        if (folder == null) { System.Media.SystemSounds.Beep.Play(); return; }
        IDataObject data;
        try { data = Clipboard.GetDataObject(); } catch { return; }
        if (data == null) return;
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            bool move = forceMove;
            if (!move && data.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
            {
                var b = new byte[4]; ms.Position = 0; ms.Read(b, 0, 4);
                move = (BitConverter.ToInt32(b, 0) & 2) != 0;
            }
            TransferManager.Instance.Start(move ? TransferKind.Move : TransferKind.Copy, files, folder);
            if (move)
            {
                foreach (var c in CutItems) c.IsCut = false;
                CutItems.Clear();
                try { Clipboard.Clear(); } catch { }
            }
            return;
        }
        // pegar una imagen o un texto crea un archivo (útil para capturas de pantalla)
        try
        {
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                var path = TransferJob.UniqueName(folder, "Imagen pegada.png", false, false);
                if (!File.Exists(Path.Combine(folder, "Imagen pegada.png"))) path = Path.Combine(folder, "Imagen pegada.png");
                using (var fs = File.Create(path))
                {
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                    enc.Save(fs);
                }
                _ = SelectWhenAppears(path);
            }
            else if (Clipboard.ContainsText())
            {
                var path = TransferJob.UniqueName(folder, "Texto pegado.txt", false, false);
                if (!File.Exists(Path.Combine(folder, "Texto pegado.txt"))) path = Path.Combine(folder, "Texto pegado.txt");
                File.WriteAllText(path, Clipboard.GetText());
                _ = SelectWhenAppears(path);
            }
        }
        catch (Exception ex) { MacDialog.Error(this, "No se pudo pegar.", ex.Message); }
    }

    public void CopyPathOfSelection()
    {
        var sel = Selection;
        var text = sel.Count > 0 ? string.Join(Environment.NewLine, sel.Select(s => s.Special == ItemSpecial.TrashEntry ? s.ExtraPath : s.Path)) : ActiveFolder;
        if (text != null) try { Clipboard.SetText(text); } catch { }
    }

    // ======================== duplicar / alias / comprimir ========================

    public void Duplicate()
    {
        var sel = Selection.Where(i => i.Special == ItemSpecial.None).ToList();
        if (sel.Count == 0) return;
        foreach (var g in sel.GroupBy(s => Path.GetDirectoryName(s.Path), StringComparer.OrdinalIgnoreCase))
        {
            var job = TransferManager.Instance.Start(TransferKind.Duplicate, g.Select(s => s.Path), g.Key!);
            if (job != null) job.ItemPlaced += (src, d) => Dispatcher.BeginInvoke(() => { _ = SelectWhenAppears(d); });
        }
    }

    public void MakeAlias()
    {
        var sel = Selection.Where(i => i.Special == ItemSpecial.None).ToList();
        if (sel.Count == 0) return;
        string last = null;
        foreach (var s in sel)
        {
            try
            {
                var dir = ActiveFolder ?? Path.GetDirectoryName(s.Path);
                var lnk = TransferJob.UniqueName(dir, s.Name + " (alias).lnk", false, false);
                var basePath = Path.Combine(dir!, s.Name + " (alias).lnk");
                if (!File.Exists(basePath)) lnk = basePath;
                ShellInterop.CreateShortcut(s.Path, lnk);
                UndoManager.RegisterCreated(lnk, "Deshacer crear alias");
                last = lnk;
            }
            catch (Exception ex) { MacDialog.Error(this, $"No se pudo crear el alias de «{s.Name}».", ex.Message); }
        }
        if (last != null) _ = SelectWhenAppears(last);
    }

    public void ShowOriginal()
    {
        var sel = Selection.FirstOrDefault(i => i.IsShortcut);
        if (sel == null) return;
        var target = ShellInterop.ResolveShortcut(sel.Path);
        if (target == null || !(File.Exists(target) || Directory.Exists(target)))
        {
            MacDialog.Error(this, $"No se encontró el original de «{sel.DisplayName}».", target);
            return;
        }
        _ = RevealInFaro(target);
    }

    public async Task RevealInFaro(string path)
    {
        await _firstTab.Task; // al abrir la app con un archivo, la pestaña se crea tras el primer pintado
        var parent = Path.GetDirectoryName(path);
        if (parent == null) { await _tab.NavigateAsync(Location.Folder(path)); return; }
        await _tab.NavigateAsync(Location.Folder(parent));
        var it = _tab.Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (it != null) _view.SelectItems(new[] { it });
    }

    public void Compress()
    {
        var sel = Selection.Where(i => i.Special == ItemSpecial.None).ToList();
        if (sel.Count == 0) return;
        var dir = Path.GetDirectoryName(sel[0].Path)!;
        var zipName = sel.Count == 1 ? sel[0].Name + ".zip" : "Archivo.zip";
        var zip = Path.Combine(dir, zipName);
        if (File.Exists(zip)) zip = TransferJob.UniqueName(dir, zipName, false, false);
        var paths = sel.Select(s => s.Path).ToList();
        var progressTitle = sel.Count == 1 ? $"«{sel[0].Name}»" : $"{sel.Count} elementos";
        Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(zip, FileMode.CreateNew);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
                foreach (var p in paths)
                {
                    if (Directory.Exists(p))
                    {
                        var root = Path.GetDirectoryName(p)!;
                        foreach (var f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                            archive.CreateEntryFromFile(f, Path.GetRelativePath(root, f).Replace('\\', '/'), CompressionLevel.Optimal);
                        foreach (var d in Directory.EnumerateDirectories(p, "*", SearchOption.AllDirectories).Where(d => !Directory.EnumerateFileSystemEntries(d).Any()))
                            archive.CreateEntry(Path.GetRelativePath(root, d).Replace('\\', '/') + "/");
                    }
                    else archive.CreateEntryFromFile(p, Path.GetFileName(p), CompressionLevel.Optimal);
                }
                Dispatcher.BeginInvoke(() => { UndoManager.RegisterCreated(zip, "Deshacer comprimir"); _ = SelectWhenAppears(zip); });
            }
            catch (Exception ex)
            {
                try { File.Delete(zip); } catch { }
                Dispatcher.BeginInvoke(() => MacDialog.Error(this, $"No se pudo comprimir {progressTitle}.", ex.Message));
            }
        });
    }

    public void Extract(FileItem zipItem)
    {
        var dir = Path.GetDirectoryName(zipItem.Path)!;
        var folder = Archives.FolderNameFor(zipItem.Name);
        var target = Path.Combine(dir, folder);
        if (Directory.Exists(target) || File.Exists(target)) target = TransferJob.UniqueName(dir, folder, true, false);
        Task.Run(() =>
        {
            try
            {
                Archives.Extract(zipItem.Path, target);
                Dispatcher.BeginInvoke(() => { UndoManager.RegisterCreated(target, "Deshacer descomprimir"); _ = SelectWhenAppears(target); });
            }
            catch (Exception ex)
            {
                // que no quede una carpeta vacía de un intento fallido
                try { if (Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any()) Directory.Delete(target); } catch { }
                Dispatcher.BeginInvoke(() => MacDialog.Error(this, $"No se pudo descomprimir «{zipItem.Name}».", ex.Message));
            }
        });
    }

    // ======================== arrastrar y soltar ========================

    public DragDropEffects DragOverFolder(string targetFolder, DragEventArgs e)
    {
        if (targetFolder == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return DragDropEffects.None;
        // no se puede soltar una carpeta dentro de sí misma
        if (files.Any(f => TransferJob.IsInside(targetFolder, f))) return DragDropEffects.None;
        var mods = e.KeyStates;
        if ((mods & DragDropKeyStates.AltKey) != 0) return DragDropEffects.Link;
        if ((mods & DragDropKeyStates.ControlKey) != 0) return DragDropEffects.Copy;
        // lo protegido (discos, carpetas de Windows, elementos de sistema) sólo se copia con Ctrl o se enlaza con Alt
        if (files.Any(Protection.IsProtected)) return DragDropEffects.None;
        if ((mods & DragDropKeyStates.ShiftKey) != 0) return DragDropEffects.Move;
        // si ya está en esa carpeta no hace nada
        if (files.All(f => string.Equals(Path.GetDirectoryName(f.TrimEnd('\\')), targetFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return DragDropEffects.None;
        return SameVolume(files[0], targetFolder) ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    private static bool IsAtDriveRoot(string p)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            return string.Equals(Path.GetDirectoryName(full), Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool SameVolume(string a, string b)
    {
        // con rutas de red se compara \\servidor\recurso como texto: GetVolumePathName pregunta al servidor y,
        // si no está a la vista, tarda ~21 s… en CADA DragOver (Explor «no responde» al pasar por un favorito de red)
        if (NetworkService.IsUnc(a) || NetworkService.IsUnc(b))
            return string.Equals(ShareRoot(a), ShareRoot(b), StringComparison.OrdinalIgnoreCase);
        try { return string.Equals(Native.VolumeRoot(a), Native.VolumeRoot(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string ShareRoot(string p)
    {
        if (!NetworkService.IsUnc(p)) return Path.GetPathRoot(p) ?? p;
        var parts = p.TrimStart('\\').Split('\\');
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : p;
    }

    public void DropOnFolder(string targetFolder, DragEventArgs e)
    {
        var effect = DragOverFolder(targetFolder, e);
        if (effect == DragDropEffects.None || e.Data.GetData(DataFormats.FileDrop) is not string[] files) { e.Effects = DragDropEffects.None; return; }
        if (effect == DragDropEffects.Link)
        {
            foreach (var f in files)
            {
                try
                {
                    var name = FileItem.NameOf(f) + " (alias).lnk";
                    var lnk = File.Exists(Path.Combine(targetFolder, name)) ? TransferJob.UniqueName(targetFolder, name, false, false) : Path.Combine(targetFolder, name);
                    ShellInterop.CreateShortcut(f, lnk);
                }
                catch { }
            }
        }
        else if (effect == DragDropEffects.Move && (e.KeyStates & DragDropKeyStates.ShiftKey) == 0 && files.Any(IsAtDriveRoot))
        {
            // mover algo que cuelga directamente de un disco (C:\dev, C:\Herramientas…) rompe rutas de scripts y programas:
            // se pregunta, fuera del bucle del arrastre para no bloquearlo
            Dispatcher.BeginInvoke(() =>
            {
                var what = files.Length == 1 ? $"«{FileItem.NameOf(files[0])}»" : $"{files.Length} elementos";
                if (MacDialog.Confirm(this, $"¿Mover {what} a «{FileItem.NameOf(targetFolder)}»?",
                        "Está en la raíz del disco: los programas, accesos directos o scripts que lo usen dejarán de encontrarlo. Si lo arrastraste sin querer, pulsa Cancelar.",
                        "Mover"))
                    TransferManager.Instance.Start(TransferKind.Move, files, targetFolder);
            });
        }
        else
        {
            TransferManager.Instance.Start(effect == DragDropEffects.Move ? TransferKind.Move : TransferKind.Copy, files, targetFolder);
        }
        // al origen se le dice "copiado": así el Explorador nunca borra nada por su cuenta, Faro hace el movimiento.
        e.Effects = DragDropEffects.Copy;
    }

    private bool _dragging;
    public void StartDrag(UIElement source, IList<FileItem> items)
    {
        if (_dragging) return;
        var paths = items.Where(i => i.Special is ItemSpecial.None or ItemSpecial.App or ItemSpecial.Drive).Select(i => i.Special == ItemSpecial.App ? i.LaunchPath ?? i.Path : i.Path).ToArray();
        if (paths.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        // a nadie (tampoco al Explorador) se le ofrece «mover» si hay algo protegido
        var allowed = paths.Any(Protection.IsProtected) ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
        _dragging = true;
        try { DragDrop.DoDragDrop(source, data, allowed); }
        catch { }
        finally { _dragging = false; }
    }

    // ======================== información / vistas previas ========================

    public void GetInfo()
    {
        var sel = Selection;
        if (sel.Count == 0)
        {
            var f = ActiveFolder;
            if (f != null) new Windows.InfoWindow(FileItem.FromPath(f) ?? new FileItem(f, FileItem.NameOf(f), true, -1, DateTime.Now, DateTime.Now, FileAttributes.Directory)) { Owner = this }.Show();
            return;
        }
        int offset = 0;
        foreach (var s in sel.Take(10))
        {
            var w = new Windows.InfoWindow(s) { Owner = this };
            w.Left = Left + Width - 340 + offset; w.Top = Top + 60 + offset;
            w.Show();
            offset += 24;
        }
    }

    public void ToggleQuickLook()
    {
        // con texto sin guardar en la vista rápida, ConfirmLeave pregunta; «Cancelar» la deja abierta
        if (QuickLook != null && QuickLook.IsVisible) { if (QuickLook.ConfirmLeave()) { QuickLook.Close(); QuickLook = null; } return; }
        var sel = Selection;
        if (sel.Count == 0) return;
        QuickLook = new Windows.QuickLookWindow(this);
        QuickLook.Closed += (_, _) => { if (QuickLook != null && !QuickLook.IsVisible) QuickLook = null; };
        QuickLook.ShowItems(sel, 0);
    }

    public void ShowContextMenu(UIElement target, IList<FileItem> items) => OpenContextMenu(target, items);
}
