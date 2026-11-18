using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Faro.Core;
using Faro.Model;
using Faro.Services;
using Faro.Shell;
using Faro.Views;

namespace Faro;

/// <summary>Ventana de Faro: barra lateral, barra de herramientas, pestañas, vistas, ruta y estado.</summary>
public partial class MainWindow : Window, IFileViewHost
{
    public ObservableCollection<BrowserTab> Tabs { get; } = new();
    private bool _closeAnimated;
    /// <summary>Dirección de la próxima transición: -1 atrás (entra desde la izquierda), 1 adelante, 0 fundido.</summary>
    internal int NavDirection;
    private BrowserTab _tab;
    public BrowserTab Tab => _tab;
    /// <summary>Se completa cuando la ventana ya tiene su primera pestaña activa.</summary>
    private readonly TaskCompletionSource _firstTab = new();

    // las vistas se crean al usarlas por primera vez: crear las cuatro al abrir costaba ~200 ms de arranque
    private IconView _iconViewInst;
    private ListModeView _listViewInst;
    private ColumnView _columnViewInst;
    private GalleryView _galleryViewInst;
    private IconView _iconView => _iconViewInst ??= new();
    private ListModeView _listView => _listViewInst ??= new();
    private ColumnView _columnView => _columnViewInst ??= new();
    private GalleryView _galleryView => _galleryViewInst ??= new();
    private IFileView _view;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _deviceTimer;

    public MainWindow(Location start = null, bool restoreSession = false)
    {
        InitializeComponent();
        App.MarkStartup("xaml");
        BuildLights();
        var s = Settings.Current;
        Width = s.WindowWidth; Height = s.WindowHeight;
        if (!double.IsNaN(s.WindowLeft) && Application.Current.Windows.OfType<MainWindow>().Count(w => w != this) == 0)
        {
            Left = s.WindowLeft; Top = s.WindowTop;
            EnsureOnScreen();
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (s.WindowMaximized && Application.Current.Windows.OfType<MainWindow>().Count() <= 1) WindowState = WindowState.Maximized;
        SidebarCol.Width = new GridLength(s.ShowSidebar ? Math.Clamp(s.SidebarWidth, 160, 420) : 0);
        ApplyBars();
        ZoomSlider.Value = s.IconSize;

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RunSearch(); };
        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _deviceTimer.Tick += (_, _) => { _deviceTimer.Stop(); BuildSidebar(); if (_tab?.Location?.Kind == LocationKind.Computer) _ = _tab.LoadAsync(); };

        Loaded += (_, _) => RefreshThumbSize();
        DpiChanged += (_, _) => RefreshThumbSize();
        SourceInitialized += (_, _) =>
        {
            Theme.ApplyWindowEffects(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
            FitToMonitor();
            App.MarkStartup("hwnd");
        };
        SizeChanged += (_, _) => { ApplyCompactLayout(); if (WindowState == WindowState.Maximized) FixMaximizedMargin(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(FixMaximizedMargin, DispatcherPriority.Loaded);
        Loaded += (_, _) => ApplyCompactLayout();
        StateChanged += (_, _) => FixMaximizedMargin();
        Activated += (_, _) => { UpdateTitleOpacity(true); SetLightsActive(true); };
        Deactivated += (_, _) => { UpdateTitleOpacity(false); SetLightsActive(false); };
        Loaded += (_, _) => { App.MarkStartup("cargada"); FadeIn(Root, 260, 0, 0.965); };
        Closing += (_, e) =>
        {
            if (!_closeAnimated && QuickLook?.IsVisible == true && !QuickLook.ConfirmLeave()) { e.Cancel = true; return; }
            if (!_closeAnimated) { e.Cancel = true; _closeAnimated = true; QuickLook?.Close(); FadeOutThen(Root, Close, 150, 0.965); return; }
            // la última ventana que se cierra deja apuntadas sus pestañas para la próxima vez
            if (Application.Current.Windows.OfType<MainWindow>().Count(w => w != this) == 0) SaveAll();
            else SaveWindowState();
        };
        ContentRendered += (_, _) => App.MarkStartup("ventana");
        Closed += (_, _) => { foreach (var t in Tabs) t.Dispose(); QuickLook?.Close(); App.CheckExit(); };
        PreviewKeyDown += OnPreviewKeyDown;
        TagStore.Changed += () => Dispatcher.BeginInvoke(() => { BuildSidebar(); foreach (var i in _tab?.Items ?? Enumerable.Empty<FileItem>()) i.RefreshTags(); });
        Settings.Changed += () => Dispatcher.BeginInvoke(ApplyBars);
        Theme.Changed += () => { BuildSidebar(); SetLightsActive(IsActive); _ = _tab?.LoadAsync(); };

        BuildSidebar();
        App.MarkStartup("lateral");
        // Primero se pinta la ventana y después se llena: si la carpeta llegaba antes del primer pintado,
        // WPF creaba todas las celdas dentro de él y la ventana tardaba ~2 s en aparecer.
        ContentRendered += OpenInitialTabs;
        void OpenInitialTabs(object sender, EventArgs e)
        {
            ContentRendered -= OpenInitialTabs;
            Dispatcher.BeginInvoke(() =>
            {
                if (start != null || !restoreSession || !RestoreSession())
                    _ = AddTab(start ?? DefaultLocation(), true);
            }, DispatcherPriority.Loaded);
        }
    }

    public static Location DefaultLocation() => Settings.Current.NewWindowTarget switch
    {
        "recents" => Location.Recents,
        "computer" => Location.Computer,
        "desktop" => Location.Folder(KnownFolders.Desktop),
        "documents" => Location.Folder(KnownFolders.Documents),
        "downloads" => Location.Folder(KnownFolders.Downloads),
        _ => Location.Folder(KnownFolders.Home)
    };

    // ======================== ventana ========================

    private void Close_Click(object s, RoutedEventArgs e) => Close();
    private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;


    private void UpdateTitleOpacity(bool active)
    {
        TitleText.Opacity = active ? 1 : 0.55;
        foreach (var p in new[] { Lights, LightsInline })
            foreach (Button b in p.Children.OfType<Button>()) b.Opacity = active ? 1 : 0.35;
    }

    private void EnsureOnScreen()
    {
        var vl = SystemParameters.VirtualScreenLeft; var vt = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth; var vh = SystemParameters.VirtualScreenHeight;
        if (Left < vl || Top < vt || Left + 200 > vl + vw || Top + 100 > vt + vh) { Left = vl + 80; Top = vt + 60; }
        if (Width > vw) Width = vw - 40;
        if (Height > vh) Height = vh - 40;
    }

    internal void SaveWindowState()
    {
        var s = Settings.Current;
        // en pantalla completa se guarda la ventana de antes, no el monitor entero
        s.WindowMaximized = _fullScreen ? _stateBeforeFull == WindowState.Maximized : WindowState == WindowState.Maximized;
        var b = _fullScreen ? _boundsBeforeFull : RestoreBounds;
        if (!b.IsEmpty) { s.WindowLeft = b.Left; s.WindowTop = b.Top; s.WindowWidth = b.Width; s.WindowHeight = b.Height; }
        if (SidebarCol.ActualWidth > 40 && !_sidebarAutoHidden) s.SidebarWidth = SidebarCol.ActualWidth;
        s.Save();
    }

    private void Splitter_DragCompleted(object s, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (SidebarCol.ActualWidth < 90) ToggleSidebar(false);
        else { Settings.Current.SidebarWidth = SidebarCol.ActualWidth; Settings.Current.Save(); }
    }

    public void ToggleSidebar(bool? show = null)
    {
        var s = Settings.Current;
        // si estaba apartada por falta de sitio, el atajo la muestra (y viceversa)
        s.ShowSidebar = show ?? !SidebarVisible;
        _sidebarAutoHidden = false;
        AnimateColumn(SidebarCol, s.ShowSidebar ? SidebarWidthFor(ActualWidth) : 0);
        LightsInline.Visibility = s.ShowSidebar ? Visibility.Collapsed : Visibility.Visible;
        s.Save();
    }

    private void ApplyBars()
    {
        var s = Settings.Current;
        PathBar.Visibility = s.ShowPathBar ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Visibility = s.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
        LightsInline.Visibility = SidebarVisible ? Visibility.Collapsed : Visibility.Visible;
        UpdatePreviewPane();
    }

    private const int WM_DEVICECHANGE = 0x0219;
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE) { _deviceTimer.Stop(); _deviceTimer.Start(); }
        if (Shell.ShellContextMenu.HandleMessage(msg, w, l, out var r)) { handled = true; return r; }
        return IntPtr.Zero;
    }

    // ======================== pestañas ========================

    public async Task AddTab(Location loc, bool activate, ViewMode? mode = null)
    {
        var tab = new BrowserTab();
        if (mode is ViewMode m && m != 0) tab.Mode = m;
        tab.PropertyChanged += Tab_PropertyChanged;
        tab.Loaded += () => { if (tab == _tab) OnTabLoaded(); };
        Tabs.Add(tab);
        RebuildTabStrip();
        if (activate) ActivateTab(tab);
        await tab.NavigateAsync(loc);
        // la vista con la que se dejó la pestaña gana a la recordada para esa carpeta
        if (mode is ViewMode m2 && m2 != 0 && tab.Mode != m2) tab.Mode = m2;
    }

    public void ActivateTab(BrowserTab tab)
    {
        if (_tab == tab) return;
        if (_tab != null && _tab.Mode == ViewMode.List) { /* se conservan las carpetas desplegadas */ }
        int dir = _tab == null ? 0 : Math.Sign(Tabs.IndexOf(tab) - Tabs.IndexOf(_tab));
        _tab = tab;
        _firstTab.TrySetResult();
        ApplyViewMode(tab.Mode, false);
        _lastShown = tab.Location;
        FadeIn(ViewHost, 170, 0, 1, dir * 22);
        RebuildTabStrip();
        UpdateChrome();
        OnTabLoaded();
    }

    public void CloseTab(BrowserTab tab)
    {
        int i = Tabs.IndexOf(tab);
        if (i < 0) return;
        if (Tabs.Count == 1) { Close(); return; }
        tab.Dispose();
        Tabs.RemoveAt(i);
        if (_tab == tab) { _tab = null; ActivateTab(Tabs[Math.Min(i, Tabs.Count - 1)]); }
        RebuildTabStrip();
    }

    private void NewTab_Click(object s, RoutedEventArgs e) => _ = AddTab(_tab?.Location?.IsFolder == true ? _tab.Location : DefaultLocation(), true);

    private void RebuildTabStrip()
    {
        var showTabs = Tabs.Count > 1;
        if (showTabs && TabBar.Visibility != Visibility.Visible) FadeIn(TabBar, 160, -4);
        TabBar.Visibility = showTabs ? Visibility.Visible : Visibility.Collapsed;
        var elems = new List<UIElement>();
        foreach (var t in Tabs)
        {
            var tab = t;
            bool active = tab == _tab;
            var title = new TextBlock { Text = tab.Title, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(22, 0, 22, 0) };
            title.SetResourceReference(TextBlock.ForegroundProperty, active ? "Fg.Primary" : "Fg.Secondary");
            var close = new Button { Style = (Style)FindResource("ToolButton"), Height = 18, MinWidth = 18, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(5, 0, 0, 0), Visibility = Visibility.Hidden };
            close.Content = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 8 };
            close.Click += (_, _) => CloseTab(tab);
            var grid = new Grid { Background = Brushes.Transparent };
            grid.Children.Add(title); grid.Children.Add(close);
            var border = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 1, 0), AllowDrop = true };
            border.SetResourceReference(Border.BackgroundProperty, active ? "Bg.TabActive" : "Bg.Tab");
            border.SetResourceReference(Border.BorderBrushProperty, "Line.Separator");
            border.MouseEnter += (_, _) => close.Visibility = Visibility.Visible;
            border.MouseLeave += (_, _) => close.Visibility = Visibility.Hidden;
            border.MouseLeftButtonUp += (_, _) => ActivateTab(tab);
            border.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };
            border.DragEnter += (_, _) => ActivateTab(tab);
            tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BrowserTab.Title)) title.Text = tab.Title; };
            elems.Add(border);
        }
        TabStrip.ItemsSource = elems;
    }

    private void Tab_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender != _tab) return;
        switch (e.PropertyName)
        {
            case nameof(BrowserTab.Location):
                ScheduleSessionSave();
                goto case nameof(BrowserTab.CanGoBack);
            case nameof(BrowserTab.CanGoBack):
            case nameof(BrowserTab.CanGoForward):
                UpdateChrome();
                break;
            case nameof(BrowserTab.Mode):
                ApplyViewMode(_tab.Mode, false);
                ScheduleSessionSave();
                break;
            case nameof(BrowserTab.Error):
            case nameof(BrowserTab.IsLoading):
            case nameof(BrowserTab.SearchRunning):
                UpdateOverlay();
                break;
        }
    }

    private Location _lastShown;
    private void OnTabLoaded()
    {
        if (_tab?.IsLoading == false) App.MarkStartup("contenido");
        bool navigated = false;
        if (_tab?.Location != null && !_tab.Location.Equals(_lastShown))
        {
            navigated = true;
            _lastShown = _tab.Location;
            if (NavDirection != 0) FadeIn(ViewHost, 200, 0, 1, NavDirection * 28); else FadeIn(ViewHost, 180, 6);
            NavDirection = 0;
        }
        UpdateOverlay();
        UpdateStatus();
        UpdatePathBar();
        UpdatePreviewPane();
        RecordVisit();
        OfferLogin();
        // sólo al LLEGAR a una carpeta: los refrescos de la vigilancia (cada archivo copiado, la carpeta
        // nueva que aparece) no deben tocar el foco. Antes reactivaba la ventana y tapaba la de progreso,
        // y le quitaba el foco al cuadro de renombrar (la carpeta nueva se quedaba con «carpeta sin título»).
        if (_view != null && navigated && !SearchText.IsKeyboardFocused
            && Keyboard.FocusedElement is not System.Windows.Controls.TextBox) _view.FocusView();
    }

    // ======================== vistas ========================

    private void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && int.TryParse(fe.Tag as string, out var m)) ApplyViewMode((ViewMode)m, true);
    }

    public void ApplyViewMode(ViewMode mode, bool remember)
    {
        if (_tab == null) return;
        if (mode != ViewMode.List) _tab.CollapseAll();
        var keepSel = _tab.SelectedPaths.ToList();
        _tab.Mode = mode;
        IFileView v = mode switch
        {
            ViewMode.List => _listView,
            ViewMode.Columns => _columnView,
            ViewMode.Gallery => _galleryView,
            _ => _iconView
        };
        foreach (var other in new IFileView[] { _iconViewInst, _listViewInst, _columnViewInst, _galleryViewInst }) if (other != null && other != v) other.Attach(null);
        _tab.SelectedPaths.Clear(); foreach (var p in keepSel) _tab.SelectedPaths.Add(p);
        _view = v;
        ThumbnailLoader.CurrentSize = ThumbSizeFor(mode);
        if (ViewHost.Content != v) FadeIn(ViewHost, 160, 0, 0.985);
        ViewHost.Content = v;
        v.Attach(_tab);
        ViewIcons.IsChecked = mode == ViewMode.Icons;
        ViewList.IsChecked = mode == ViewMode.List;
        ViewColumns.IsChecked = mode == ViewMode.Columns;
        ViewGallery.IsChecked = mode == ViewMode.Gallery;
        ZoomPanel.Visibility = mode == ViewMode.Icons ? Visibility.Visible : Visibility.Hidden;
        ApplyCompactLayout();
        if (remember)
        {
            Settings.Current.DefaultView = mode;
            if (_tab.Location?.Kind == LocationKind.Folder) Settings.Current.FolderViews[_tab.Location.Path] = mode;
            Settings.Current.Save();
        }
        UpdatePreviewPane();
        Dispatcher.BeginInvoke(() => v.FocusView(), DispatcherPriority.Input);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    /// <summary>Escala real del monitor (antes de mostrarse la ventana, la del sistema: 125 % = 1,25).</summary>
    private double DpiScale()
    {
        if (PresentationSource.FromVisual(this) != null) return VisualTreeHelper.GetDpi(this).DpiScaleX;
        try { return GetDpiForSystem() / 96.0; } catch { return 1; }
    }

    private static readonly int[] ThumbTiers = { 32, 48, 64, 96, 128, 192, 256, 384, 512 };

    /// <summary>
    /// Tamaño en píxeles reales que se pide al sistema: el siguiente tamaño "nativo" por encima
    /// de lo que se ve, así la imagen siempre se reduce (nítida) y nunca se estira (pixelada).
    /// </summary>
    private int ThumbSizeFor(ViewMode mode)
    {
        double logical = mode switch
        {
            ViewMode.Icons => Settings.Current.IconSize,
            ViewMode.Gallery => 66,
            _ => 16
        };
        double px = logical * DpiScale() * (mode == ViewMode.Icons ? 1.0 : 1.5);
        foreach (var t in ThumbTiers) if (t >= px) return t;
        return 512;
    }

    /// <summary>Vuelve a pedir sólo las miniaturas que quedaron por debajo del tamaño necesario.</summary>
    private void RefreshThumbSize()
    {
        if (_tab == null) return;
        int want = ThumbSizeFor(_tab.Mode);
        if (want == ThumbnailLoader.CurrentSize) return;
        bool bigger = want > ThumbnailLoader.CurrentSize;
        ThumbnailLoader.CurrentSize = want;
        if (bigger) foreach (var i in _tab.Items) i.InvalidateThumbnail();
    }
    private void Zoom_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        var size = Math.Round(e.NewValue / 8) * 8;
        Settings.Current.IconSize = size;
        _iconViewInst?.SetIconSize(size);
        if (_tab?.Mode == ViewMode.Icons) RefreshThumbSize();
    }

    // ======================== cromo: título, botones, ruta, estado ========================

    private void UpdateChrome()
    {
        if (_tab == null) return;
        var loc = _tab.Location;
        TitleText.Text = loc?.Title ?? "";
        Title = TitleText.Text + " — " + Core.AppInfo.Name;
        BackBtn.IsEnabled = _tab.CanGoBack || _tab.IsSearching;
        ForwardBtn.IsEnabled = _tab.CanGoForward;
        HighlightSidebar();
        if (_tab.IsSearching && ScopeBar.Visibility != Visibility.Visible) FadeIn(ScopeBar, 160, -6);
        ScopeBar.Visibility = _tab.IsSearching ? Visibility.Visible : Visibility.Collapsed;
        if (_tab.IsSearching)
        {
            var origin = _tab.SearchOrigin;
            ScopeFolder.Visibility = origin?.Kind == LocationKind.Folder ? Visibility.Visible : Visibility.Collapsed;
            ScopeFolder.Content = origin?.Kind == LocationKind.Folder ? $"«{origin.Title}»" : "";
            ScopeAll.IsChecked = !_tab.SearchThisFolder || origin?.Kind != LocationKind.Folder;
            ScopeFolder.IsChecked = !ScopeAll.IsChecked;
        }
        else if (!SearchText.IsKeyboardFocused && SearchText.Text.Length > 0)
        {
            _suppressSearch = true; SearchText.Text = ""; _suppressSearch = false;
            UpdateSearchHint();
        }
        RebuildTabStrip();
        UpdatePathBar();
    }

    private void UpdateOverlay()
    {
        if (_tab == null) return;
        ErrorText.Text = _tab.Error ?? "";
        ErrorText.Visibility = _tab.HasError ? Visibility.Visible : Visibility.Collapsed;
        if (_tab.IsSearching)
            SearchStatus.Text = _tab.SearchRunning ? "Buscando…" : $"{Format.Items(_tab.Items.Count)}" + (_tab.SearchEngine != null ? $" · {_tab.SearchEngine}" : "");
        if (!_tab.HasError && _tab.Location?.Kind == LocationKind.Folder && !_tab.IsLoading && _tab.Items.Count == 0)
        {
            ErrorText.Text = "";
        }
    }

    public void UpdateStatus()
    {
        if (_tab == null) return;
        int n = _tab.Items.Count(i => i.Depth == 0);
        int sel = _view?.SelectedItems.Count ?? 0;
        string free = "";
        var path = _view?.ActiveFolder ?? _tab.FolderPath;
        if (path != null) free = FreeSpaceText(path);
        if (_tab.Location?.Kind == LocationKind.Trash)
        {
            var (items, bytes) = TrashService.Info();
            StatusText.Text = sel > 0 ? $"{sel} de {Format.Items(n)} seleccionados" : $"{Format.Items(n)} · {Format.Size(bytes)}";
            return;
        }
        StatusText.Text = sel > 0 ? $"{Format.Count(sel)} de {Format.Items(n)} seleccionados{free}" : $"{Format.Items(n)}{free}";
    }

    private void UpdatePathBar()
    {
        PathStack.Children.Clear();
        if (_tab?.Location == null) return;
        string path = null;
        var sel = _view?.SelectedItems;
        if (_tab.Location.Kind == LocationKind.Folder) path = _view?.ActiveFolder ?? _tab.Location.Path;
        if (sel is { Count: 1 } && (_tab.Location.Kind != LocationKind.Folder || _tab.Mode == ViewMode.Columns))
            path = sel[0].Special == ItemSpecial.TrashEntry ? null : sel[0].Path;
        if (path == null)
        {
            AddPathSegment(_tab.Location.Title, IconFactory.Folder(_tab.Location.Glyph), null, false, true);
            return;
        }
        var parts = new List<string>();
        for (var p = path; !string.IsNullOrEmpty(p); p = NetworkService.IsShareRoot(p) ? NetworkService.ServerOf(p) : NetworkService.IsServerRoot(p) ? null : Path.GetDirectoryName(p)) parts.Insert(0, p);
        bool unc = NetworkService.IsUnc(path);
        if (unc)
        {
            // Red › servidor › recurso › …
            var net = AddPathSegment("Red", IconFactory.Folder(FolderGlyph.Network), null, false, false);
            net.Cursor = Cursors.Hand;
            net.MouseEnter += (_, _) => net.SetResourceReference(Border.BackgroundProperty, "Bg.Hover");
            net.MouseLeave += (_, _) => net.Background = Brushes.Transparent;
            net.MouseLeftButtonUp += (_, e) => { e.Handled = true; _ = _tab.NavigateAsync(Location.Network); };
            net.MouseLeftButtonDown += (_, e) => e.Handled = true;
        }
        var lastSel = sel is { Count: 1 } && string.Equals(sel[0].Path, path, StringComparison.OrdinalIgnoreCase) ? sel[0] : null;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            bool last = i == parts.Count - 1;
            // sólo el último puede ser un archivo; en red no se pregunta al servidor (congelaba la ventana)
            bool isDir = !last || (lastSel != null ? lastSel.IsNavigable : unc || Directory.Exists(p));
            var item = isDir ? null : lastSel ?? FileItem.FromPath(p);
            var icon = NetworkService.IsServerRoot(p) ? IconFactory.Server()
                : p.Length <= 3 ? IconFactory.Drive(DriveKindHint.Fixed) : isDir ? IconFactory.Folder(IconFactory.GlyphForFolder(p)) : (item != null ? IconFactory.For(item) : IconFactory.Folder());
            AddPathSegment(Location.DisplayNameOf(p), icon, p, isDir, last);
        }
    }

    private Border AddPathSegment(string text, ImageSource icon, string path, bool isDir, bool last)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, AllowDrop = isDir };
        sp.Children.Add(new Image { Source = icon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 4, 0) });
        var t = new TextBlock { Text = text, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, last ? "Fg.Primary" : "Fg.Secondary");
        sp.Children.Add(t);
        var chip = new Border { Child = sp, CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 1, 4, 1), Background = Brushes.Transparent, Cursor = path != null ? Cursors.Hand : Cursors.Arrow };
        if (path != null)
        {
            chip.ToolTip = path;
            chip.MouseEnter += (_, _) => chip.SetResourceReference(Border.BackgroundProperty, "Bg.Hover");
            chip.MouseLeave += (_, _) => chip.Background = Brushes.Transparent;
            // un clic: ir a esa carpeta (o mostrar el archivo seleccionado)
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (isDir) { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _ = AddTab(Location.Folder(path), true); else _ = _tab.NavigateAsync(Location.Folder(path)); }
                else _ = RevealInFaro(path);
            };
            chip.MouseLeftButtonDown += (_, e) => e.Handled = true; // que no active la edición
            chip.MouseRightButtonUp += (_, e) => { PathMenu(chip, path, isDir); e.Handled = true; };
            if (isDir)
            {
                chip.AllowDrop = true;
                chip.DragOver += (_, e) => { e.Effects = DragOverFolder(path, e); chip.SetResourceReference(Border.BackgroundProperty, "Bg.DropTarget"); e.Handled = true; };
                chip.DragLeave += (_, _) => chip.Background = Brushes.Transparent;
                chip.Drop += (_, e) => { chip.Background = Brushes.Transparent; DropOnFolder(path, e); e.Handled = true; };
            }
        }
        PathStack.Children.Add(chip);
        if (!last)
        {
            var arrow = new TextBlock { Text = "\uE76C", FontFamily = IconFactory.SymbolFont, FontSize = 8, Margin = new Thickness(3, 1, 3, 0), VerticalAlignment = VerticalAlignment.Center };
            arrow.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Tertiary");
            PathStack.Children.Add(arrow);
        }
        else Dispatcher.BeginInvoke(() => PathScroll.ScrollToRightEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
        return chip;
    }

    private void PathMenu(UIElement target, string path, bool isDir)
    {
        var m = new ContextMenu();
        var name = Location.DisplayNameOf(path);
        if (isDir)
        {
            m.Items.Add(Item("Abrir", () => _ = _tab.NavigateAsync(Location.Folder(path))));
            m.Items.Add(Item("Abrir en una pestaña nueva", () => _ = AddTab(Location.Folder(path), true)));
            m.Items.Add(Item("Abrir en una ventana nueva", () => new MainWindow(Location.Folder(path)).Show()));
        }
        else
        {
            m.Items.Add(Item("Abrir", () => ShellInterop.Open(path, this)));
            m.Items.Add(Item("Mostrar en la carpeta contenedora", () => _ = RevealInFaro(path)));
        }
        m.Items.Add(new Separator());
        m.Items.Add(Item($"Copiar «{(name.Length > 26 ? name[..24] + "…" : name)}» como nombre de ruta", () => { try { Clipboard.SetText(path); } catch { } }, "Ctrl+Alt+C"));
        m.Items.Add(Item("Copiar ruta entre comillas", () => { try { Clipboard.SetText("\"" + path + "\""); } catch { } }));
        m.Items.Add(Item("Copiar sólo el nombre", () => { try { Clipboard.SetText(Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path); } catch { } }));
        m.Items.Add(Item("Editar la ruta…", BeginPathEdit, "Doble clic"));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Obtener información", () => { var it = FileItem.FromPath(path); if (it != null) new Windows.InfoWindow(it) { Owner = this }.Show(); }, "Ctrl+I"));
        m.Items.Add(Item("Mostrar en el Explorador de Windows", () => ShellInterop.RevealInExplorer(path)));
        if (isDir)
        {
            m.Items.Add(Item("Abrir en Terminal", () => ShellInterop.OpenTerminal(path)));
            m.Items.Add(IsPinned(path) ? Item("Quitar de la barra lateral", () => Unpin(path)) : Item("Fijar en la barra lateral", () => Pin(path)));
        }
        m.PlacementTarget = target; m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint; m.IsOpen = true;
    }

    // ----- escribir la ruta -----
    private void PathBar_DoubleClick(object s, MouseButtonEventArgs e) => BeginPathEdit();

    public void BeginPathEdit()
    {
        PathEdit.Text = _view?.ActiveFolder ?? _tab?.FolderPath ?? "";
        PathEdit.Visibility = Visibility.Visible;
        PathScroll.Visibility = Visibility.Hidden;
        PathEdit.Focus(); PathEdit.SelectAll();
        FadeIn(PathEdit, 130, 0, 0.98);
        ShowSuggestions(fresh: true);
    }

    private void EndPathEdit()
    {
        HideSuggestions();
        PathEdit.Visibility = Visibility.Collapsed;
        PathScroll.Visibility = Visibility.Visible;
    }

    private void PathEdit_KeyDown(object s, KeyEventArgs e)
    {
        if (SuggestKey(e)) { e.Handled = true; return; }
        if (e.Key == Key.Escape) { EndPathEdit(); _view?.FocusView(); e.Handled = true; }
        else if (e.Key == Key.Down) { ShowSuggestions(fresh: false); e.Handled = true; }
        else if (e.Key == Key.Enter)
        {
            GoToTypedPath(PathEdit.Text);
            e.Handled = true;
        }
    }

    private void PathEdit_Lost(object s, KeyboardFocusChangedEventArgs e) => EndPathEdit();
    private void UpdatePreviewPane()
    {
        bool show = Settings.Current.ShowPreviewPane && _tab != null && _tab.Mode is ViewMode.Icons or ViewMode.List && RoomForPreview;
        double pw = PreviewWidth;
        if (Math.Abs(PreviewCol.ActualWidth - (show ? pw : 0)) > 1) AnimateColumn(PreviewCol, show ? pw : 0, 180);
        PreviewHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) { PreviewHost.Child = null; return; }
        var sel = _view?.SelectedItems;
        PreviewHost.Child = sel is { Count: 1 } ? PreviewPane.Build(sel[0], pw - 38) : null;
        if (PreviewHost.Child != null) FadeIn(PreviewHost.Child, 150, 0, 1, 10);
    }

    public void OnSelectionChanged()
    {
        UpdateStatus();
        UpdatePathBar();
        UpdatePreviewPane();
        if (_tab?.Mode == ViewMode.Columns)
        {
            var f = _view.ActiveFolder;
            TitleText.Text = f != null ? Location.DisplayNameOf(f) : _tab.Title;
        }
        QuickLook?.Follow(_view?.SelectedItems);
    }

    // ======================== navegación ========================

    private void Back_Click(object s, RoutedEventArgs e) => _ = GoBack();
    private void Forward_Click(object s, RoutedEventArgs e) { NavDirection = 1; _ = _tab?.GoForwardAsync(); }

    private async Task GoBack()
    {
        if (_tab == null) return;
        if (_tab.IsSearching) { await EndSearch(); return; }
        var from = _tab.Location;
        NavDirection = -1;
        await _tab.GoBackAsync();
        // como Finder: al volver, queda seleccionada la carpeta de la que venías
        if (from?.Kind == LocationKind.Folder)
        {
            var it = _tab.Items.FirstOrDefault(i => string.Equals(i.Path, from.Path, StringComparison.OrdinalIgnoreCase));
            if (it != null) _view?.SelectItems(new[] { it });
        }
    }

    private void Back_RightClick(object s, MouseButtonEventArgs e)
    {
        if (_tab == null) return;
        var menu = new ContextMenu();
        foreach (var l in _tab.BackList)
        {
            var mi = new MenuItem { Header = MenuHeader(l.Title, IconFactory.Folder(l.Glyph)) };
            var loc = l;
            mi.Click += (_, _) => _ = _tab.NavigateAsync(loc);
            menu.Items.Add(mi);
        }
        if (menu.Items.Count > 0) { menu.PlacementTarget = BackBtn; menu.IsOpen = true; }
    }

    private void Title_RightClick(object s, MouseButtonEventArgs e)
    {
        // Como Cmd+clic en el título de Finder: menú con las carpetas contenedoras
        if (_tab?.Location?.Kind != LocationKind.Folder) return;
        var menu = new ContextMenu();
        for (var p = _tab.Location.Path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            var path = p;
            var mi = new MenuItem { Header = MenuHeader(Location.DisplayNameOf(p), p.Length <= 3 ? IconFactory.Drive(DriveKindHint.Fixed) : IconFactory.Folder(IconFactory.GlyphForFolder(p))) };
            mi.Click += (_, _) => _ = _tab.NavigateAsync(Location.Folder(path));
            menu.Items.Add(mi);
        }
        var pc = new MenuItem { Header = MenuHeader(Environment.MachineName, IconFactory.Folder(FolderGlyph.Computer)) };
        pc.Click += (_, _) => _ = _tab.NavigateAsync(Location.Computer);
        menu.Items.Add(pc);
        menu.PlacementTarget = TitleText; menu.IsOpen = true;
    }

    public static FrameworkElement MenuHeader(string text, ImageSource icon)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null) sp.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    // ======================== búsqueda ========================

    private bool _suppressSearch;

    private void SearchText_TextChanged(object s, TextChangedEventArgs e)
    {
        UpdateSearchHint();
        if (_suppressSearch) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void UpdateSearchHint()
    {
        SearchHint.Visibility = SearchText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchClear.Visibility = SearchText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchText_Focus(object s, KeyboardFocusChangedEventArgs e)
    {
        AnimateWidth(SearchBox, _searchFocusWidth);
        SearchBox.SetResourceReference(Border.BackgroundProperty, "Bg.FieldFocus");
        SearchBox.BorderThickness = new Thickness(1);
        SearchBox.SetResourceReference(Border.BorderBrushProperty, "Accent");
    }

    private void SearchText_Blur(object s, KeyboardFocusChangedEventArgs e)
    {
        if (SearchText.Text.Length == 0) AnimateWidth(SearchBox, _searchIdleWidth);
        SearchBox.SetResourceReference(Border.BackgroundProperty, "Bg.Field");
        SearchBox.BorderThickness = new Thickness(0);
    }

    private void SearchText_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _ = EndSearch(); e.Handled = true; }
        else if (e.Key is Key.Enter or Key.Down) { _searchTimer.Stop(); _ = RunSearch(); _view?.FocusView(); e.Handled = true; }
    }

    private void SearchClear_Click(object s, RoutedEventArgs e) => _ = EndSearch();

    private async Task RunSearch()
    {
        if (_tab == null) return;
        var text = SearchText.Text;
        if (string.IsNullOrWhiteSpace(text)) { if (_tab.IsSearching) await _tab.EndSearchAsync(); return; }
        if (_tab.Mode is ViewMode.Columns) ApplyViewMode(ViewMode.List, false);
        await _tab.SearchAsync(text);
        UpdateChrome();
        UpdateOverlay();
    }

    private async Task EndSearch()
    {
        _suppressSearch = true; SearchText.Text = ""; _suppressSearch = false;
        UpdateSearchHint();
        AnimateWidth(SearchBox, _searchIdleWidth);
        if (_tab?.IsSearching == true) await _tab.EndSearchAsync();
        _view?.FocusView();
    }

    private void Scope_Click(object s, RoutedEventArgs e)
    {
        if (_tab == null) return;
        _tab.SearchThisFolder = ScopeFolder.IsChecked == true;
        _ = RunSearch();
    }

    private void SearchHelp_Click(object s, RoutedEventArgs e)
    {
        Dialogs.MacDialog.Info(this, "Cómo buscar",
            "• Escribe parte del nombre: «factura 2026».\n" +
            "• Comodines: «*.psd», «foto??.jpg».\n" +
            "• Por tipo: «tipo:imagen», «tipo:video», «tipo:audio», «tipo:pdf», «tipo:adobe», «tipo:documento», «tipo:codigo», «tipo:comprimido», «tipo:fuente», «tipo:3d», «tipo:carpeta».\n" +
            "• Por extensión: «ext:psd,ai».\n\n" +
            "Si Everything está abierto, la búsqueda en todo el equipo es instantánea. Si no, se usa el índice de Windows o un escaneo rápido en paralelo.");
    }
}
