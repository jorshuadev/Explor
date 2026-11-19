using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Faro.Core;
using Faro.Model;
using Faro.Services;
using Faro.Shell;

namespace Faro;

/// <summary>Menús contextuales y menús de la barra de herramientas, con los textos de Finder.</summary>
public partial class MainWindow
{
    private static MenuItem Item(string text, Action a, string gesture = null, bool enabled = true, bool isChecked = false)
    {
        var mi = new MenuItem { Header = text, InputGestureText = gesture ?? "", IsEnabled = enabled, IsChecked = isChecked };
        mi.Click += (_, _) => a();
        return mi;
    }

    private void OpenContextMenu(UIElement target, IList<FileItem> items)
    {
        var m = new ContextMenu();
        var loc = _tab?.Location;
        bool inTrash = loc?.Kind == LocationKind.Trash;

        if (items.Count == 0)
        {
            // ---- fondo de la carpeta ----
            if (inTrash)
            {
                m.Items.Add(Item("Vaciar la Papelera", EmptyTrash, "Ctrl+Mayús+Supr"));
            }
            else
            {
                bool canCreate = ActiveFolder != null;
                m.Items.Add(Item("Nueva carpeta", () => NewFolder(), "Ctrl+Mayús+N", canCreate));
                m.Items.Add(Item("Nuevo documento de texto", NewTextFile, null, canCreate));
                m.Items.Add(new Separator());
                m.Items.Add(Item("Obtener información", GetInfo, "Ctrl+I"));
                m.Items.Add(new Separator());
                m.Items.Add(Item("Pegar", () => Paste(), "Ctrl+V", canCreate && (Clipboard.ContainsFileDropList() || Clipboard.ContainsImage() || Clipboard.ContainsText())));
                m.Items.Add(new Separator());
                m.Items.Add(ViewSubmenu());
                m.Items.Add(SortSubmenu());
                m.Items.Add(Item("Mostrar opciones de visualización", ShowViewOptions, "Ctrl+J"));
                m.Items.Add(new Separator());
                if (ActiveFolder != null)
                {
                    m.Items.Add(Item("Abrir en Terminal", () => ShellInterop.OpenTerminal(ActiveFolder)));
                    m.Items.Add(Item("Abrir en el Explorador de Windows", () => System.Diagnostics.Process.Start("explorer.exe", $"\"{ActiveFolder}\"")));
                    m.Items.Add(Item("Mostrar más opciones", () => ShellContextMenu.Show(new WindowInteropHelper(this).Handle, new[] { ActiveFolder }, false)));
                }
            }
            Show(m, target);
            return;
        }

        var first = items[0];
        bool single = items.Count == 1;
        string name = single ? $"«{first.DisplayName}»" : $"{items.Count} elementos";

        if (inTrash)
        {
            m.Items.Add(Item("Devolver", () => RestoreFromTrash(items.ToList())));
            m.Items.Add(Item("Eliminar inmediatamente…", () => DeleteForever(items.ToList()), "Supr"));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Vaciar la Papelera", EmptyTrash));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Obtener información", GetInfo, "Ctrl+I"));
            m.Items.Add(Item("Visualización rápida", ToggleQuickLook, "Espacio"));
            Show(m, target);
            return;
        }

        if (items.All(i => i.Special == ItemSpecial.App))
        {
            AppMenu(m, items, first, single);
            Show(m, target);
            return;
        }

        m.Items.Add(Item("Abrir", () => OpenItems(items), "Ctrl+O"));
        if (first.IsNavigable)
            m.Items.Add(Item("Abrir en una pestaña nueva", () => { foreach (var i in items.Where(x => x.IsNavigable)) _ = AddTab(Location.Folder(i.Path), true); }));
        if (single && !first.IsDirectory && first.Special == ItemSpecial.None)
            m.Items.Add(OpenWithSubmenu(first));
        m.Items.Add(new Separator());
        if (first.Special != ItemSpecial.Drive)
            m.Items.Add(Item("Trasladar a la Papelera", MoveSelectionToTrash, "Supr"));
        else
            m.Items.Add(Item($"Expulsar {name}", () => Eject(first.Path), "Ctrl+E", Providers.IsEjectable(first.Path)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Obtener información", GetInfo, "Ctrl+I"));
        if (first.Special == ItemSpecial.None)
        {
            m.Items.Add(Item("Renombrar", BeginRename, "Intro", single));
            m.Items.Add(Item($"Comprimir {name}", Compress));
            if (single && !first.IsDirectory && Archives.CanOpen(first.Extension)) m.Items.Add(Item("Descomprimir aquí", () => Extract(first)));
            m.Items.Add(Item("Duplicar", Duplicate, "Ctrl+D"));
            m.Items.Add(Item("Crear alias", MakeAlias, "Ctrl+L"));
            if (single && first.IsShortcut) m.Items.Add(Item("Mostrar original", ShowOriginal, "Ctrl+R"));
        }
        m.Items.Add(Item($"Visualización rápida", ToggleQuickLook, "Espacio"));
        m.Items.Add(new Separator());
        m.Items.Add(Item($"Copiar", () => CopySelection(), "Ctrl+C"));
        if (first.Special == ItemSpecial.None) m.Items.Add(Item("Cortar", () => CopySelection(true), "Ctrl+X"));
        m.Items.Add(Item(single ? $"Copiar «{Trim(first.DisplayName)}» como nombre de ruta" : "Copiar como nombres de ruta", CopyPathOfSelection, "Ctrl+Alt+C"));
        m.Items.Add(new Separator());
        m.Items.Add(TagRow(items));
        m.Items.Add(new Separator());
        if (first.Special == ItemSpecial.None && items.Count > 1)
            m.Items.Add(Item($"Nueva carpeta con la selección ({items.Count} elementos)", () => NewFolder(true), "Ctrl+Alt+N"));
        if (loc?.Kind is LocationKind.Search or LocationKind.Recents or LocationKind.Tag)
            m.Items.Add(Item("Mostrar en la carpeta contenedora", () => _ = RevealInFaro(first.Path), "Ctrl+Alt+R"));
        if (single && first.IsDirectory && first.Special == ItemSpecial.None)
        {
            m.Items.Add(IsPinned(first.Path)
                ? Item("Quitar de la barra lateral", () => Unpin(first.Path))
                : Item("Fijar en la barra lateral", () => Pin(first.Path), "Ctrl+Alt+T"));
            m.Items.Add(Item("Abrir en Terminal", () => ShellInterop.OpenTerminal(first.Path)));
        }
        m.Items.Add(Item("Mostrar en el Explorador de Windows", () => ShellInterop.RevealInExplorer(first.Path)));
        m.Items.Add(Item("Propiedades de Windows…", () => ShellInterop.ShowWindowsProperties(first.Path)));
        m.Items.Add(Item("Mostrar más opciones", () =>
        {
            var paths = items.Select(i => i.Special == ItemSpecial.App ? i.LaunchPath ?? i.Path : i.Path).ToList();
            Dispatcher.BeginInvoke(() => ShellContextMenu.Show(new WindowInteropHelper(this).Handle, paths, System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)));
        }, "Mayús+F10"));
        Show(m, target);
    }

    private static string Trim(string s) => s.Length > 28 ? s[..26] + "…" : s;

    private static void Show(ContextMenu m, UIElement target)
    {
        m.PlacementTarget = target;
        m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        m.IsOpen = true;
    }

    private MenuItem OpenWithSubmenu(FileItem item)
    {
        var mi = new MenuItem { Header = "Abrir con" };
        mi.Items.Add(Item("Aplicación predeterminada", () => ShellInterop.Open(item.Path, this)));
        mi.Items.Add(new Separator());
        // Las apps se buscan al desplegar el submenú (preguntar a Windows en cada clic derecho lo haría lento).
        var loading = new MenuItem { Header = "Buscando aplicaciones…", IsEnabled = false };
        mi.Items.Add(loading);
        bool filled = false;
        mi.SubmenuOpened += (_, e) =>
        {
            if (filled || !ReferenceEquals(e.OriginalSource, mi)) return;
            filled = true;
            mi.Items.Remove(loading);
            foreach (var app in OpenWithAppsFor(item))
            {
                var a = app;
                var entry = Item(a.Name, () => a.Open(item.Path));
                try
                {
                    if (a.ExePath != null && File.Exists(a.ExePath) && ShellInterop.GetImage(a.ExePath, 16, ShellInterop.SIIGBF.IconOnly) is { } icon)
                        entry.Icon = new Image { Source = icon, Width = 16, Height = 16 };
                }
                catch { }
                mi.Items.Add(entry);
            }
            if (mi.Items.Count > 2) mi.Items.Add(new Separator());
            mi.Items.Add(Item("Otra aplicación…", () => ShellInterop.OpenWith(item.Path, this)));
        };
        return mi;
    }

    /// <summary>Apps para «Abrir con»: las que recomienda Windows y, en texto/código/web, los editores instalados y el Bloc de notas.</summary>
    private static List<OpenWithApps.App> OpenWithAppsFor(FileItem item)
    {
        var apps = OpenWithApps.Recommended(item.Path);
        if (item.Kind.Category is KindCategory.Text or KindCategory.Code or KindCategory.Web or KindCategory.Subtitle or KindCategory.Other)
        {
            bool Has(string exe, string name) => apps.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)
                || (a.ExePath != null && string.Equals(System.IO.Path.GetFileName(a.ExePath), System.IO.Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase)));
            foreach (var ed in OpenWithApps.Editors())
                if (!Has(ed.ExePath, ed.Name)) apps.Add(ed);
            var notepad = System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe");
            if (!Has(notepad, "Bloc de notas"))
                apps.Add(new OpenWithApps.App("Bloc de notas", notepad, p => System.Diagnostics.Process.Start("notepad.exe", $"\"{p}\"")));
        }
        return apps;
    }

    /// <summary>La fila de círculos de colores de Finder para poner/quitar etiquetas.</summary>
    private MenuItem TagRow(IList<FileItem> items)
    {
        var mi = new MenuItem { StaysOpenOnClick = true, IsEnabled = items.All(i => i.Special is ItemSpecial.None or ItemSpecial.App) };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-14, 2, 0, 2) };
        var paths = items.Select(i => i.Path).ToList();
        foreach (var t in TagStore.Standard)
        {
            bool all = items.All(i => i.Tags.Contains(t.Name));
            var dot = new Grid { Width = 20, Height = 20, Margin = new Thickness(0, 0, 4, 0), Background = Brushes.Transparent, ToolTip = t.Name };
            dot.Children.Add(new Ellipse { Width = 15, Height = 15, Fill = Theme.BrushFromHex(t.Color) });
            if (all) dot.Children.Add(new TextBlock { Text = "", FontFamily = IconFactory.SymbolFont, FontSize = 8, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var tag = t.Name;
            dot.MouseLeftButtonUp += (_, e) =>
            {
                TagStore.Toggle(paths, tag);
                foreach (var i in items) i.RefreshTags();
                if (mi.Parent is ContextMenu cm) cm.IsOpen = false;
                e.Handled = true;
            };
            sp.Children.Add(dot);
        }
        mi.Header = sp;
        return mi;
    }

    private MenuItem ViewSubmenu()
    {
        var mi = new MenuItem { Header = "Visualización" };
        var mode = _tab?.Mode ?? ViewMode.Icons;
        mi.Items.Add(Item("como iconos", () => ApplyViewMode(ViewMode.Icons, true), "Ctrl+1", isChecked: mode == ViewMode.Icons));
        mi.Items.Add(Item("como lista", () => ApplyViewMode(ViewMode.List, true), "Ctrl+2", isChecked: mode == ViewMode.List));
        mi.Items.Add(Item("como columnas", () => ApplyViewMode(ViewMode.Columns, true), "Ctrl+3", isChecked: mode == ViewMode.Columns));
        mi.Items.Add(Item("como galería", () => ApplyViewMode(ViewMode.Gallery, true), "Ctrl+4", isChecked: mode == ViewMode.Gallery));
        return mi;
    }

    private MenuItem SortSubmenu()
    {
        var mi = new MenuItem { Header = "Ordenar por" };
        foreach (var it in SortItems()) mi.Items.Add(it);
        return mi;
    }

    private IEnumerable<object> SortItems()
    {
        var s = Settings.Current;
        void Set(SortField f) { s.SortBy = f; s.SortDescending = f is SortField.Modified or SortField.Created or SortField.Size; Settings.NotifyChanged(); _tab?.Resort(); }
        yield return Item("Nombre", () => Set(SortField.Name), "Ctrl+Alt+1", isChecked: s.SortBy == SortField.Name);
        yield return Item("Tipo", () => Set(SortField.Kind), "Ctrl+Alt+2", isChecked: s.SortBy == SortField.Kind);
        yield return Item("Fecha de modificación", () => Set(SortField.Modified), "Ctrl+Alt+4", isChecked: s.SortBy == SortField.Modified);
        yield return Item("Fecha de creación", () => Set(SortField.Created), "Ctrl+Alt+5", isChecked: s.SortBy == SortField.Created);
        yield return Item("Tamaño", () => Set(SortField.Size), "Ctrl+Alt+6", isChecked: s.SortBy == SortField.Size);
        yield return new Separator();
        yield return Item("Orden ascendente", () => { s.SortDescending = false; Settings.NotifyChanged(); _tab?.Resort(); }, null, isChecked: !s.SortDescending);
        yield return Item("Orden descendente", () => { s.SortDescending = true; Settings.NotifyChanged(); _tab?.Resort(); }, null, isChecked: s.SortDescending);
        yield return new Separator();
        yield return Item("Mantener las carpetas arriba", () => { s.FoldersFirst = !s.FoldersFirst; Settings.NotifyChanged(); _tab?.Resort(); }, null, isChecked: s.FoldersFirst);
    }

    // ---------------- menús de la barra de herramientas ----------------

    // ---- menús de los botones de la barra (⋯, ordenar, compartir, etiquetas) ----
    // Cada clic creaba un menú nuevo: tocar el botón con el menú abierto lo cerraba y lo volvía a abrir,
    // y un toque del touchpad que llega como dos clics abría dos menús seguidos (parpadeo).
    // Ahora el botón funciona como interruptor y se ignoran los clics repetidos.
    private object _menuOwner;
    private ContextMenu _openMenu;
    private DateTime _menuOpenedAt, _menuClosedAt;

    /// <summary>true si el clic sólo debe cerrar el menú de ese botón (o es un clic duplicado).</summary>
    private bool ButtonMenuToggled(object sender)
    {
        if (!ReferenceEquals(sender, _menuOwner)) return false;
        var now = DateTime.Now;
        bool open = _openMenu?.IsOpen == true;
        // clic duplicado justo después de abrir: el menú se queda (si su pulsación ya lo cerró, se reabre)
        if (now - _menuOpenedAt < TimeSpan.FromMilliseconds(300)) return open;
        if (open) { _openMenu.IsOpen = false; return true; }
        // el mismo clic que cierra el menú (al pulsar fuera de él) llega luego al botón
        return now - _menuClosedAt < TimeSpan.FromMilliseconds(500);
    }

    private void ShowButtonMenu(ContextMenu m, object sender)
    {
        if (_openMenu?.IsOpen == true) _openMenu.IsOpen = false;
        _menuOwner = sender; _openMenu = m; _menuOpenedAt = DateTime.Now;
        m.Closed += (_, _) => { if (ReferenceEquals(_openMenu, m)) { _menuClosedAt = DateTime.Now; _openMenu = null; } };
        m.PlacementTarget = (UIElement)sender; m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; m.IsOpen = true;
    }

    private void SortMenu_Click(object sender, RoutedEventArgs e)
    {
        if (e != null && ButtonMenuToggled(sender)) return;
        var m = new ContextMenu();
        foreach (var it in SortItems()) m.Items.Add(it);
        ShowButtonMenu(m, sender);
    }

    private void ShareMenu_Click(object sender, RoutedEventArgs e)
    {
        if (e != null && ButtonMenuToggled(sender)) return;
        var sel = Selection;
        var m = new ContextMenu();
        m.Items.Add(Item("Copiar archivos (para pegar en otra app)", () => CopySelection(), "Ctrl+C", sel.Count > 0));
        m.Items.Add(Item("Copiar ruta", CopyPathOfSelection, "Ctrl+Alt+C"));
        m.Items.Add(Item("Enviar por correo (Outlook / app de correo)…", () => ShareMail(sel), null, sel.Count > 0 && sel.All(s => !s.IsDirectory)));
        m.Items.Add(Item("Comprimir para enviar", Compress, null, sel.Count > 0));
        ShowButtonMenu(m, sender);
    }

    private void ShareMail(IList<FileItem> sel)
    {
        // "Enviar a > Destinatario de correo" del sistema
        var sendTo = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Mail Recipient.MAPIMail");
        if (File.Exists(sendTo)) ShellInterop.Open(sendTo, this);
        else ShellContextMenu.Show(new WindowInteropHelper(this).Handle, sel.Select(s => s.Path).ToList(), false);
    }

    private void TagMenu_Click(object sender, RoutedEventArgs e)
    {
        if (e != null && ButtonMenuToggled(sender)) return;
        var sel = Selection;
        var m = new ContextMenu();
        if (sel.Count == 0) m.Items.Add(new MenuItem { Header = "Selecciona elementos para etiquetarlos", IsEnabled = false });
        else
            foreach (var t in TagStore.Standard)
            {
                bool all = sel.All(i => i.Tags.Contains(t.Name));
                var tag = t.Name;
                var mi = new MenuItem { IsChecked = all };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new Ellipse { Width = 11, Height = 11, Fill = Theme.BrushFromHex(t.Color), Margin = new Thickness(0, 0, 8, 0) });
                sp.Children.Add(new TextBlock { Text = t.Name });
                mi.Header = sp;
                mi.Click += (_, _) => { TagStore.Toggle(sel.Select(s => s.Path), tag); foreach (var i in sel) i.RefreshTags(); };
                m.Items.Add(mi);
            }
        ShowButtonMenu(m, sender);
    }

    private void MoreMenu_Click(object sender, RoutedEventArgs e)
    {
        if (e != null && ButtonMenuToggled(sender)) return;
        var s = Settings.Current;
        var m = new ContextMenu();
        bool hasSel = Selection.Count > 0;
        // en ventanas estrechas estos botones no caben en la barra: se ofrecen aquí
        if (ShareBtn.Visibility != Visibility.Visible || SortBtn.Visibility != Visibility.Visible)
        {
            if (SortBtn.Visibility != Visibility.Visible) m.Items.Add(Item("Ordenar…", () => SortMenu_Click(MoreBtn, null)));
            m.Items.Add(Item("Compartir…", () => ShareMenu_Click(MoreBtn, null)));
            m.Items.Add(Item("Etiquetas…", () => TagMenu_Click(MoreBtn, null)));
            m.Items.Add(new Separator());
        }
        m.Items.Add(Item("Nueva carpeta", () => NewFolder(), "Ctrl+Mayús+N", ActiveFolder != null));
        m.Items.Add(Item("Nueva pestaña", () => NewTab_Click(null, null), "Ctrl+T"));
        m.Items.Add(Item("Nueva ventana", () => new MainWindow(_tab?.Location).Show(), "Ctrl+N"));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Obtener información", GetInfo, "Ctrl+I"));
        m.Items.Add(Item("Renombrar", BeginRename, "Intro", Selection.Count == 1));
        m.Items.Add(Item("Duplicar", Duplicate, "Ctrl+D", hasSel));
        m.Items.Add(Item("Comprimir", Compress, null, hasSel));
        m.Items.Add(Item("Visualización rápida", ToggleQuickLook, "Espacio", hasSel));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Ir a la carpeta…", ShowGoTo, "Ctrl+Mayús+G"));
        m.Items.Add(Item("Conectar al servidor…", ShowConnectToServer, "Ctrl+K"));
        m.Items.Add(Item("Red", () => _ = _tab.NavigateAsync(Location.Network), "Ctrl+Mayús+K"));
        if (_tab?.FolderPath is string cur)
            m.Items.Add(IsPinned(cur) ? Item($"Quitar «{Location.DisplayNameOf(cur)}» de la barra lateral", () => Unpin(cur))
                                      : Item($"Fijar «{Location.DisplayNameOf(cur)}» en la barra lateral", () => Pin(cur), "Ctrl+Alt+T"));
        m.Items.Add(Item(UndoManager.NextName ?? "Deshacer", () => _ = Undo(), "Ctrl+Z", UndoManager.NextName != null));
        m.Items.Add(new Separator());
        m.Items.Add(Item(s.ShowSidebar ? "Ocultar barra lateral" : "Mostrar barra lateral", () => ToggleSidebar(), "Ctrl+Alt+S"));
        m.Items.Add(Item(s.ShowPreviewPane ? "Ocultar previsualización" : "Mostrar previsualización", () => { s.ShowPreviewPane = !s.ShowPreviewPane; Settings.NotifyChanged(); }, "Ctrl+Mayús+P"));
        m.Items.Add(Item(s.ShowPathBar ? "Ocultar barra de ruta" : "Mostrar barra de ruta", () => { s.ShowPathBar = !s.ShowPathBar; Settings.NotifyChanged(); }, "Ctrl+Alt+P"));
        m.Items.Add(Item(s.ShowStatusBar ? "Ocultar barra de estado" : "Mostrar barra de estado", () => { s.ShowStatusBar = !s.ShowStatusBar; Settings.NotifyChanged(); }, "Ctrl+/"));
        m.Items.Add(Item(s.ShowHidden ? "Ocultar archivos ocultos" : "Mostrar archivos ocultos", ToggleHidden, "Ctrl+Mayús+."));
        m.Items.Add(Item("Opciones de visualización…", ShowViewOptions, "Ctrl+J"));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Mostrar operaciones de copia", () => TransferManager.Instance.ShowWindow(), null, TransferManager.Instance.Jobs.Count > 0));
        m.Items.Add(Item("Vaciar la Papelera…", EmptyTrash, "Ctrl+Mayús+Supr"));
        m.Items.Add(Item($"Ajustes de {Core.AppInfo.Name}…", ShowPreferences, "Ctrl+,"));
        m.Items.Add(Item("Atajos de teclado", ShowShortcuts, "F1"));
        ShowButtonMenu(m, sender);
    }

    public bool IsPinned(string path) => (Settings.Current.Favorites ?? new()).Contains(path, StringComparer.OrdinalIgnoreCase);

    public void Pin(string path, int index = -1)
    {
        Settings.Current.Favorites ??= new();
        var fav = Settings.Current.Favorites;
        if (!IsPinned(path) && (NetworkService.IsUnc(path) || Directory.Exists(path)))
        {
            if (index >= 0 && index <= fav.Count) fav.Insert(index, path); else fav.Add(path);
        }
        Settings.Current.Save();
        foreach (var w in Application.Current.Windows.OfType<MainWindow>()) w.BuildSidebar();
    }

    public void Unpin(string path)
    {
        Settings.Current.Favorites?.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        Settings.Current.Save();
        foreach (var w in Application.Current.Windows.OfType<MainWindow>()) w.BuildSidebar();
    }

    private void ToggleHidden()
    {
        Settings.Current.ShowHidden = !Settings.Current.ShowHidden;
        Settings.NotifyChanged();
        foreach (var w in Application.Current.Windows.OfType<MainWindow>())
            foreach (var t in w.Tabs) if (t.Location?.Kind == LocationKind.Folder) _ = t.LoadAsync();
    }

    private void ShowViewOptions() => new Windows.ViewOptionsWindow(this) { Owner = this }.Show();
    private void ShowPreferences() => new Windows.PreferencesWindow { Owner = this }.ShowDialog();
    private void ShowGoTo()
    {
        var w = new Windows.GoToWindow(ActiveFolder ?? KnownFolders.Home) { Owner = this };
        if (w.ShowDialog() == true && w.Result != null)
        {
            if (Directory.Exists(w.Result)) _ = _tab.NavigateAsync(Location.Folder(w.Result));
            else if (File.Exists(w.Result)) _ = RevealInFaro(w.Result);
        }
    }

    private async Task Undo()
    {
        try
        {
            if (!await UndoManager.UndoAsync()) System.Media.SystemSounds.Beep.Play();
            await Task.Delay(300);
            await (_tab?.RefreshAsync() ?? Task.CompletedTask);
            BuildSidebar();
        }
        catch (Exception ex) { Dialogs.MacDialog.Error(this, "No se pudo deshacer.", ex.Message); }
    }

    private void ShowShortcuts()
    {
        Dialogs.MacDialog.Info(this, "Atajos de teclado (Ctrl = ⌘ de Mac, Alt = ⌥)",
            "Ctrl+1/2/3/4  Iconos / Lista / Columnas / Galería\n" +
            "Espacio  Visualización rápida      Intro / F2  Renombrar\n" +
            "Ctrl+O / Ctrl+↓  Abrir      Ctrl+↑  Carpeta contenedora\n" +
            "Ctrl+[ / Ctrl+]  Atrás / Adelante      Ctrl+F  Buscar\n" +
            "Ctrl+Mayús+N  Nueva carpeta      Ctrl+D  Duplicar\n" +
            "Ctrl+C / Ctrl+V  Copiar / Pegar      Ctrl+Alt+V  Mover aquí\n" +
            "Ctrl+X  Cortar      Ctrl+Z  Deshacer      Ctrl+L  Crear alias\n" +
            "Supr / Ctrl+Retroceso  Papelera      Mayús+Supr  Eliminar ya\n" +
            "Ctrl+Mayús+Supr  Vaciar Papelera      Ctrl+I  Obtener información\n" +
            "Ctrl+Mayús+G  Ir a la carpeta      Ctrl+Mayús+H  Inicio\n" +
            "Ctrl+Mayús+D  Escritorio      Ctrl+Mayús+O  Documentos\n" +
            "Ctrl+Alt+L  Descargas      Ctrl+Mayús+A  Aplicaciones\n" +
            "Ctrl+Mayús+C  Este equipo      Ctrl+Mayús+F  Recientes\n" +
            "Ctrl+K  Conectar al servidor      Ctrl+Mayús+K  Red\n" +
            "Ctrl+Mayús+.  Archivos ocultos      Ctrl+Alt+S  Barra lateral\n" +
            "Ctrl+Mayús+P  Previsualización      Ctrl+Alt+P  Barra de ruta\n" +
            "Ctrl+T / Ctrl+W  Pestaña nueva / cerrar      Ctrl+N  Ventana\n" +
            "Ctrl+Alt+C  Copiar ruta      Ctrl+E  Expulsar      Ctrl+J  Opciones\n" +
            "→ / ←  Desplegar / plegar carpeta (lista)      Alt+→  Todo");
    }

    /// <summary>
    /// Menú de una aplicación (lo que hay en «Aplicaciones» son accesos directos del menú Inicio):
    /// abrir, ejecutar como administrador, ir a donde está instalada y desinstalarla.
    /// </summary>
    private void AppMenu(ContextMenu m, IList<FileItem> items, FileItem first, bool single)
    {
        var target = single ? AppUninstaller.TargetOf(first) : null;
        bool exe = AppUninstaller.IsExecutable(target);
        m.Items.Add(Item("Abrir", () => OpenItems(items), "Ctrl+O"));
        if (single && exe)
            m.Items.Add(Item("Ejecutar como administrador", () => { try { AppUninstaller.RunAsAdmin(first.LaunchPath ?? first.Path); } catch { /* aviso UAC cancelado */ } }));
        m.Items.Add(new Separator());
        if (single && exe && File.Exists(target))
            m.Items.Add(Item("Mostrar dónde está instalada", () => _ = RevealInFaro(target)));
        if (single)
            m.Items.Add(Item("Mostrar el acceso directo", () => _ = RevealInFaro(first.LaunchPath ?? first.Path)));
        m.Items.Add(new Separator());
        if (single) m.Items.Add(Item("Desinstalar…", () => UninstallApp(first)));
        m.Items.Add(Item(single ? "Quitar del menú Inicio" : $"Quitar {items.Count} del menú Inicio", MoveSelectionToTrash, "Supr"));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Obtener información", GetInfo, "Ctrl+I"));
        m.Items.Add(Item("Copiar ruta", CopyPathOfSelection, "Ctrl+Alt+C"));
        m.Items.Add(new Separator());
        if (single)
        {
            m.Items.Add(Item("Propiedades de Windows…", () => ShellInterop.ShowWindowsProperties(first.LaunchPath ?? first.Path)));
            m.Items.Add(Item("Mostrar más opciones", () =>
                Dispatcher.BeginInvoke(() => ShellContextMenu.Show(new WindowInteropHelper(this).Handle, new List<string> { first.LaunchPath ?? first.Path }, false)), "Mayús+F10"));
        }
    }

    private void UninstallApp(FileItem app)
    {
        AppUninstaller.Entry entry;
        try { entry = AppUninstaller.Find(app); } catch { entry = null; }
        if (entry == null)
        {
            if (Dialogs.MacDialog.Confirm(this, $"No encontré el desinstalador de «{app.DisplayName}».",
                    "Puede ser una herramienta de Windows, un enlace a una web o algo instalado sin desinstalador. ¿Abrir «Aplicaciones instaladas» de Windows para buscarla allí?",
                    "Abrir"))
                AppUninstaller.OpenWindowsAppsSettings();
            return;
        }
        // siempre se dice QUÉ programa se va a desinstalar: el acceso directo y el programa pueden llamarse distinto
        var detail = entry.ViaSteam
            ? "Se abrirá Steam para desinstalar el juego."
            : $"Se abrirá el desinstalador de «{entry.DisplayName}». El acceso directo desaparecerá cuando termine.";
        if (!Dialogs.MacDialog.Confirm(this, $"¿Desinstalar «{entry.DisplayName}»?", detail, "Desinstalar", destructive: true)) return;
        try { AppUninstaller.Run(entry); }
        catch (System.ComponentModel.Win32Exception) { /* el usuario canceló el aviso de permisos */ }
        catch (Exception ex) { Dialogs.MacDialog.Error(this, "No se pudo abrir el desinstalador.", ex.Message); }
    }}
