using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Faro.Core;
using Faro.Core.Search;
using Faro.Services;

namespace Faro.Model;

/// <summary>
/// Estado de una pestaña: ubicación, historial, elementos (lista plana: en la vista de
/// lista los hijos de carpetas desplegadas van intercalados con Depth &gt; 0), orden,
/// vigilancia de cambios en disco y búsqueda.
/// </summary>
public sealed class BrowserTab : INotifyPropertyChanged
{
    public BulkObservableCollection<FileItem> Items { get; } = new();
    public HashSet<string> SelectedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Location> _back = new(), _forward = new();
    private CancellationTokenSource _loadCts, _searchCts;
    private FileSystemWatcher _watcher;
    private readonly DispatcherTimer _refreshTimer;
    private bool _pendingFullRefresh;

    public BrowserTab()
    {
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(280) };
        _refreshTimer.Tick += async (_, _) => { _refreshTimer.Stop(); await RefreshAsync(); };
    }

    private Location _location;
    public Location Location { get => _location; private set { _location = value; OnChanged(); OnChanged(nameof(Title)); } }

    /// <summary>Ubicación que había antes de empezar a buscar.</summary>
    public Location SearchOrigin { get; private set; }

    public string Title => Location?.Title ?? "";

    private ViewMode _mode = Settings.Current.DefaultView;
    public ViewMode Mode { get => _mode; set { if (_mode != value) { _mode = value; OnChanged(); } } }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set { _isLoading = value; OnChanged(); } }

    private string _error;
    public string Error { get => _error; private set { _error = value; OnChanged(); OnChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);
    /// <summary>La carpeta de red no dejó entrar sin usuario y contraseña (la ventana ofrece conectar).</summary>
    public bool NeedsLogin { get; private set; }

    private string _status = "";
    public string Status { get => _status; set { _status = value; OnChanged(); } }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool IsSearching => Location?.Kind == LocationKind.Search;

    /// <summary>Carpeta real donde crear / pegar (null en lugares virtuales).</summary>
    public string FolderPath => Location?.Kind == LocationKind.Folder ? Location.Path : null;

    public event Action Loaded;
    public event Action ItemsReset;

    // ================= navegación =================

    public Task NavigateAsync(Location loc, bool addHistory = true)
    {
        if (loc == null) return Task.CompletedTask;
        if (Location != null && addHistory && !Location.Equals(loc))
        {
            if (Location.Kind != LocationKind.Search) _back.Add(Location);
            _forward.Clear();
        }
        if (loc.Kind != LocationKind.Search) { SearchOrigin = null; CancelSearch(); }
        SelectedPaths.Clear();
        Location = loc;
        OnChanged(nameof(CanGoBack)); OnChanged(nameof(CanGoForward)); OnChanged(nameof(IsSearching));
        if (loc.Kind == LocationKind.Folder && Settings.Current.FolderViews.TryGetValue(loc.Path, out var vm)) Mode = vm;
        return LoadAsync();
    }

    public Task GoBackAsync()
    {
        if (IsSearching) return EndSearchAsync();
        if (_back.Count == 0) return Task.CompletedTask;
        var prev = _back[^1]; _back.RemoveAt(_back.Count - 1);
        _forward.Add(Location);
        return NavigateAsync(prev, false).ContinueWith(_ => { }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public Task GoForwardAsync()
    {
        if (_forward.Count == 0) return Task.CompletedTask;
        var next = _forward[^1]; _forward.RemoveAt(_forward.Count - 1);
        _back.Add(Location);
        return NavigateAsync(next, false);
    }

    public List<Location> BackList => _back.AsEnumerable().Reverse().Take(12).ToList();

    public async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        StopWatching();
        ThumbnailLoader.CancelPending();
        NeedsLogin = false;
        Error = null;
        IsLoading = true;
        var loc = Location;
        try
        {
            bool hidden = Settings.Current.ShowHidden;
            var cmp = Comparer();
            // leer y ordenar fuera del hilo de la interfaz: en carpetas grandes ordenar tardaba lo suyo
            var list = await Task.Run(() =>
            {
                var l = loc.Kind switch
                {
                    LocationKind.Folder => Providers.LoadFolder(loc.Path, hidden, cts.Token),
                    LocationKind.Computer => Providers.LoadComputer(),
                    LocationKind.Network => NetworkService.LoadNetwork(),
                    LocationKind.Recents => Providers.LoadRecents(cts.Token),
                    LocationKind.Apps => Providers.LoadApps(),
                    LocationKind.Trash => TrashService.LoadItems(),
                    LocationKind.Tag => Providers.LoadTag(loc.Tag),
                    _ => new List<FileItem>()
                };
                if (loc.Kind == LocationKind.Recents) l = l.OrderByDescending(i => i.Modified).ToList();
                else l.Sort(cmp);
                return l;
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            Items.ReplaceAll(list);
            ItemsReset?.Invoke();
            if (loc.Kind == LocationKind.Folder) Watch(loc.Path);
            // tras pintar lo visible, precargar el resto de miniaturas
            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() => { if (Location == loc) ThumbnailLoader.Prefetch(Items); }, DispatcherPriority.ContextIdle);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (loc.Kind == LocationKind.Folder && NetworkService.IsUnc(loc.Path) && NetworkService.IsLoginError(ex))
        {
            Items.ReplaceAll(Array.Empty<FileItem>());
            NeedsLogin = true;
            Error = $"«{loc.Title}» pide usuario y contraseña.";
        }
        catch (UnauthorizedAccessException)
        {
            Items.ReplaceAll(Array.Empty<FileItem>());
            Error = $"No tienes permiso para ver el contenido de «{loc.Title}».";
        }
        catch (Exception ex)
        {
            Items.ReplaceAll(Array.Empty<FileItem>());
            Error = ex is DirectoryNotFoundException or IOException ? $"No se puede abrir «{loc.Title}». {ex.Message}" : ex.Message;
        }
        finally
        {
            if (_loadCts == cts) IsLoading = false;
        }
        Loaded?.Invoke();
    }

    // ================= orden =================

    public Comparison<FileItem> Comparer()
    {
        var s = Settings.Current;
        int dir = s.SortDescending ? -1 : 1;
        var field = s.SortBy;
        bool foldersFirst = s.FoldersFirst;
        return (a, b) =>
        {
            if (foldersFirst && a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            int c = field switch
            {
                SortField.Kind => string.Compare(a.KindText, b.KindText, StringComparison.CurrentCultureIgnoreCase),
                SortField.Modified => a.Modified.CompareTo(b.Modified),
                SortField.Created => a.Created.CompareTo(b.Created),
                SortField.Size => a.Size.CompareTo(b.Size),
                SortField.Location => NaturalComparer.Instance.Compare(a.Location, b.Location),
                _ => 0
            };
            if (c == 0) c = NaturalComparer.Instance.Compare(a.Name, b.Name);
            return c * dir;
        };
    }

    public void SortList(List<FileItem> list) => list.Sort(Comparer());

    /// <summary>Reordena manteniendo las carpetas desplegadas con sus hijos debajo.</summary>
    public void Resort()
    {
        var roots = Items.Where(i => i.Depth == 0).ToList();
        SortList(roots);
        var flat = new List<FileItem>(Items.Count);
        foreach (var r in roots) AppendTree(r, flat);
        Items.ReplaceAll(flat);
        ItemsReset?.Invoke();
    }

    private void AppendTree(FileItem i, List<FileItem> flat)
    {
        flat.Add(i);
        if (i.IsExpanded && i.Children != null)
        {
            SortList(i.Children);
            foreach (var c in i.Children) AppendTree(c, flat);
        }
    }

    // ================= árbol (vista de lista) =================

    public async Task ExpandAsync(FileItem item, bool recursive = false)
    {
        if (!item.CanExpand || item.IsExpanded) return;
        List<FileItem> kids;
        try { kids = await Task.Run(() => Providers.LoadFolder(item.Path, Settings.Current.ShowHidden, CancellationToken.None)); }
        catch { kids = new List<FileItem>(); }
        SortList(kids);
        foreach (var k in kids) { k.Depth = item.Depth + 1; k.ParentRow = item; }
        item.Children = kids;
        int idx = Items.IndexOf(item);
        if (idx < 0) return;
        item.IsExpanded = true;
        if (kids.Count > 400)
        {
            var flat = Items.ToList();
            flat.InsertRange(idx + 1, kids);
            Items.ReplaceAll(flat);
            ItemsReset?.Invoke();
        }
        else
            for (int i = 0; i < kids.Count; i++) Items.Insert(idx + 1 + i, kids[i]);
        if (recursive)
            foreach (var k in kids.Where(k => k.IsDirectory).ToList()) await ExpandAsync(k, true);
    }

    public void Collapse(FileItem item)
    {
        if (!item.IsExpanded) return;
        item.IsExpanded = false;
        int idx = Items.IndexOf(item);
        if (idx < 0) return;
        int end = idx + 1;
        while (end < Items.Count && Items[end].Depth > item.Depth) end++;
        int count = end - idx - 1;
        if (count > 400)
        {
            var flat = Items.ToList();
            flat.RemoveRange(idx + 1, count);
            Items.ReplaceAll(flat);
            ItemsReset?.Invoke();
        }
        else
            for (int i = end - 1; i > idx; i--) Items.RemoveAt(i);
    }

    public void CollapseAll()
    {
        if (!Items.Any(i => i.Depth > 0)) return;
        foreach (var i in Items) i.IsExpanded = false;
        Items.ReplaceAll(Items.Where(i => i.Depth == 0).ToList());
        ItemsReset?.Invoke();
    }

    // ================= vigilancia de cambios =================

    private void Watch(string path)
    {
        try
        {
            if (path.StartsWith(@"\\") && path.Count(c => c == '\\') <= 3) return;
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false, InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes
            };
            FileSystemEventHandler h = (_, _) => ScheduleRefresh();
            _watcher.Created += h; _watcher.Deleted += h; _watcher.Changed += h;
            _watcher.Renamed += (_, _) => ScheduleRefresh();
            _watcher.Error += (_, _) => { _pendingFullRefresh = true; ScheduleRefresh(); };
            _watcher.EnableRaisingEvents = true;
        }
        catch { _watcher = null; }
    }

    private void StopWatching()
    {
        if (_watcher == null) return;
        try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } catch { }
        _watcher = null;
    }

    public void ScheduleRefresh()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
    }

    /// <summary>Relee la carpeta y aplica sólo las diferencias (mantiene selección y scroll).</summary>
    public async Task RefreshAsync()
    {
        var loc = Location;
        if (loc == null) return;
        if (loc.Kind != LocationKind.Folder) { if (loc.Kind != LocationKind.Search) await LoadAsync(); return; }
        if (_pendingFullRefresh) { _pendingFullRefresh = false; }
        List<FileItem> fresh;
        try { fresh = await Task.Run(() => Providers.LoadFolder(loc.Path, Settings.Current.ShowHidden, CancellationToken.None)); }
        catch (DirectoryNotFoundException)
        {
            // la carpeta desapareció (disco expulsado / borrada): subir a la primera que exista
            var p = loc.Path;
            while (!string.IsNullOrEmpty(p) && !Directory.Exists(p)) p = Path.GetDirectoryName(p);
            await NavigateAsync(string.IsNullOrEmpty(p) ? Location.Computer : Location.Folder(p), false);
            return;
        }
        catch { return; }
        if (!Equals(Location, loc)) return;

        var map = Items.Where(i => i.Depth == 0).ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var freshSet = new HashSet<string>(fresh.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var added = new List<FileItem>();
        foreach (var f in fresh)
        {
            if (map.TryGetValue(f.Path, out var existing)) existing.UpdateFrom(f);
            else added.Add(f);
        }
        var removed = map.Values.Where(i => !freshSet.Contains(i.Path)).ToList();
        if (added.Count <= 300)
        {
            foreach (var a in added) a.IsNew = true;
            var justAdded = added.ToList();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            t.Tick += (_, _) => { t.Stop(); foreach (var a in justAdded) a.IsNew = false; };
            t.Start();
        }
        if (added.Count == 0 && removed.Count == 0) { Loaded?.Invoke(); return; }

        if (added.Count + removed.Count > 300)
        {
            var roots = map.Values.Where(i => freshSet.Contains(i.Path)).Concat(added).ToList();
            SortList(roots);
            var flat = new List<FileItem>();
            foreach (var r in roots) AppendTree(r, flat);
            Items.ReplaceAll(flat);
            ItemsReset?.Invoke();
        }
        else
        {
            foreach (var r in removed)
            {
                if (r.IsExpanded) Collapse(r);
                Items.Remove(r);
            }
            var cmp = Comparer();
            foreach (var a in added)
            {
                int pos = Items.Count;
                for (int i = 0; i < Items.Count; i++)
                {
                    if (Items[i].Depth == 0 && cmp(a, Items[i]) < 0) { pos = i; break; }
                }
                Items.Insert(pos, a);
            }
        }
        Loaded?.Invoke();
    }

    // ================= búsqueda =================

    public string SearchText { get; private set; }
    public bool SearchThisFolder { get; set; } = Settings.Current.SearchThisFolderByDefault;
    private bool _searchRunning;
    public bool SearchRunning { get => _searchRunning; private set { _searchRunning = value; OnChanged(); } }
    public string SearchEngine { get; private set; }

    public async Task SearchAsync(string text)
    {
        SearchText = text;
        if (string.IsNullOrWhiteSpace(text)) { await EndSearchAsync(); return; }
        if (!IsSearching) SearchOrigin = Location;
        var scopeFolder = SearchThisFolder && SearchOrigin?.Kind == LocationKind.Folder ? SearchOrigin.Path : null;
        CancelSearch();
        var cts = _searchCts = new CancellationTokenSource();
        StopWatching();
        Location = new Location(LocationKind.Search, Query: text, SearchScope: scopeFolder ?? "*");
        OnChanged(nameof(IsSearching));
        Error = null;
        Items.ReplaceAll(Array.Empty<FileItem>());
        ItemsReset?.Invoke();
        SearchRunning = true;
        var q = new SearchQuery(text);
        if (q.IsEmpty) { SearchRunning = false; return; }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dispatcher = System.Windows.Application.Current.Dispatcher;

        void AddHits(IEnumerable<SearchHit> hits)
        {
            var batch = new List<FileItem>();
            foreach (var h in hits)
            {
                lock (seen) { if (!seen.Add(h.Path)) continue; }
                if (!Settings.Current.ShowHidden && IsHiddenPath(h.Path)) continue;
                batch.Add(new FileItem(h.Path, FileItem.NameOf(h.Path), h.IsDirectory, h.Size, h.Modified, h.Modified, h.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal)
                { Location = Path.GetDirectoryName(h.Path) });
            }
            if (batch.Count == 0) return;
            dispatcher.BeginInvoke(() =>
            {
                if (cts.IsCancellationRequested) return;
                Items.AddRangeReset(batch);
                ItemsReset?.Invoke();
                Status = $"{Format.Items(Items.Count)}";
            });
        }

        try
        {
            // 1) Everything (instantáneo en todo el equipo). Si tarda (p. ej. recién abierto), se busca a la vez por otra vía.
            var evTask = EverythingClient.QueryAsync(EverythingClient.Translate(q, scopeFolder), 3000, cts.Token);
            var first = await Task.WhenAny(evTask, Task.Delay(1500, cts.Token));
            var everything = first == evTask ? await evTask : null;
            if (everything != null)
            {
                SearchEngine = "Everything";
                var hits = await Task.Run(() => everything.Select(e => ToHit(e.path, e.dir)).Where(h => h != null).ToList(), cts.Token);
                AddHits(hits);
            }
            else
            {
                if (first != evTask)
                    _ = evTask.ContinueWith(t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion && t.Result != null && !cts.IsCancellationRequested)
                        { SearchEngine = "Everything"; AddHits(t.Result.Select(e => ToHit(e.path, e.dir)).Where(h => h != null).ToList()); }
                    }, TaskScheduler.Default);
                if (scopeFolder != null)
                {
                    // 2) Carpeta concreta: escaneo propio en paralelo
                    SearchEngine = Core.AppInfo.Name;
                    await FolderScanner.ScanAsync(scopeFolder, q, AddHits, cts.Token, Settings.Current.ShowHidden);
                }
                else
                {
                    // 3) Todo el equipo: índice de Windows; si no hay índice, escaneo de la carpeta personal y los demás discos
                    SearchEngine = "Windows Search";
                    var ws = await Task.Run(() => WindowsSearchClient.Query(q, null, 3000, cts.Token), cts.Token);
                    if (ws != null) AddHits(ws);
                    if (ws == null || ws.Count == 0)
                    {
                        SearchEngine = Core.AppInfo.Name;
                        await FolderScanner.ScanAsync(KnownFolders.Home, q, AddHits, cts.Token, Settings.Current.ShowHidden);
                        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
                        {
                            if (cts.IsCancellationRequested) break;
                            if (KnownFolders.Home.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase)) continue;
                            await FolderScanner.ScanAsync(d.Name, q, AddHits, cts.Token, Settings.Current.ShowHidden);
                        }
                    }
                }
                if (!evTask.IsCompleted) { try { await Task.WhenAny(evTask, Task.Delay(4000, cts.Token)); } catch { } }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = "No se pudo buscar: " + ex.Message; }
        finally
        {
            if (_searchCts == cts)
            {
                SearchRunning = false;
                await dispatcher.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    if (Items.Count == 0) Error = "Sin resultados.";
                    Status = Format.Items(Items.Count);
                    Loaded?.Invoke();
                });
            }
        }
    }

    private static SearchHit ToHit(string path, bool dir)
    {
        try
        {
            if (dir)
            {
                var di = new DirectoryInfo(path);
                return new SearchHit(path, true, -1, di.LastWriteTime);
            }
            var fi = new FileInfo(path);
            return new SearchHit(path, false, fi.Exists ? fi.Length : 0, fi.LastWriteTime);
        }
        catch { return new SearchHit(path, dir, -1, DateTime.MinValue); }
    }

    private static bool IsHiddenPath(string p)
    {
        foreach (var seg in p.Split('\\'))
            if (seg.Length > 1 && seg[0] == '.' ) return true;
        return p.Contains("\\AppData\\", StringComparison.OrdinalIgnoreCase) || p.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\Windows\\WinSxS\\", StringComparison.OrdinalIgnoreCase) || p.Contains("\\Windows\\servicing\\", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\Windows\\Installer\\", StringComparison.OrdinalIgnoreCase) || p.Contains("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || p.Contains("\\Windows\\SystemApps\\", StringComparison.OrdinalIgnoreCase) || p.Contains("\\Windows\\assembly\\", StringComparison.OrdinalIgnoreCase);
    }

    public void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        SearchRunning = false;
    }

    public async Task EndSearchAsync()
    {
        CancelSearch();
        var origin = SearchOrigin ?? Location.Folder(KnownFolders.Home);
        SearchOrigin = null;
        SearchText = null;
        await NavigateAsync(origin, false);
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        CancelSearch();
        StopWatching();
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnChanged([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
