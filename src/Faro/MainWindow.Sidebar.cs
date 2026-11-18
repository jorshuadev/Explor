using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Faro.Core;
using Faro.Core.Transfer;
using Faro.Model;
using Faro.Services;
using Faro.Shell;

namespace Faro;

/// <summary>Barra lateral: Favoritos, Ubicaciones (discos con botón de expulsar) y Etiquetas.</summary>
public partial class MainWindow
{
    private sealed class SideEntry
    {
        public Location Location;
        public Border Element;
        public string DropFolder;
    }

    private readonly List<SideEntry> _sideEntries = new();
    private static readonly HashSet<string> CollapsedSections = new();
    private static bool _trashFull;
    // Los discos se consultan en segundo plano: IsReady/etiqueta/tamaño/DeviceIoControl de una unidad USB
    // o virtual que falla se cuelgan decenas de segundos, y en el hilo de la ventana retrasaban su
    // primer pintado (aperturas de 23 s y 96 s en arranque.log).
    private static List<(FileItem Drive, bool Eject)> _drives = new();
    private static string _drivesKey = "";

    private Border _pinHint;
    private bool _sidebarDragHooked;
    private IDataObject _probedDrag;
    private bool _probedHasFolders;

    /// <summary>¿El arrastre trae alguna carpeta que aún no está fijada? Se calcula una vez por arrastre (DragOver llega sin parar).</summary>
    private bool DragHasNewFolders(DragEventArgs e)
    {
        if (!ReferenceEquals(_probedDrag, e.Data))
        {
            _probedDrag = e.Data;
            _probedHasFolders = e.Data.GetData(DataFormats.FileDrop) is string[] fs
                && fs.Any(f => !NetworkService.IsUnc(f) && Directory.Exists(f)) && !fs.All(IsPinned);
        }
        return _probedHasFolders;
    }

    /// <summary>
    /// Franja de arriba/abajo de un favorito = «fijar aquí» (como Finder: una línea entre filas), el centro = soltar
    /// dentro de la carpeta. Devuelve la posición en Favoritos o -1.
    /// </summary>
    private int PinSlot(Border row, string favorite, DragEventArgs e)
    {
        if (favorite == null || !DragHasNewFolders(e)) return -1;
        var y = e.GetPosition(row).Y;
        const double edge = 8;
        bool top = y < edge, bottom = y > row.ActualHeight - edge;
        if (!top && !bottom) return -1;
        var i = Settings.Current.Favorites.FindIndex(x => string.Equals(x, favorite, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? -1 : (bottom ? i + 1 : i);
    }

    private static List<string> DefaultFavorites() => new()
    {
        "faro:recents", "faro:apps", KnownFolders.Desktop, KnownFolders.Documents, KnownFolders.Downloads,
        KnownFolders.Pictures, KnownFolders.Music, KnownFolders.Videos, KnownFolders.Home
    };

    public void BuildSidebar()
    {
        SidebarStack.Children.Clear();
        _sideEntries.Clear();
        var fav = Settings.Current.Favorites ??= DefaultFavorites();

        // ---- Favoritos ----
        var favPanel = Section("Favoritos", allowFolderDrop: true);
        foreach (var f in fav)
        {
            switch (f)
            {
                case "faro:recents": AddSideItem(favPanel, "Recientes", "", Location.Recents, null, removable: f); break;
                case "faro:apps": AddSideItem(favPanel, "Aplicaciones", "", Location.Apps, null, removable: f); break;
                default:
                    // Una carpeta de red se muestra sin comprobarla: Directory.Exists en \servidor\… espera al
                    // servidor (7 s, y hasta 96 s recién encendida o fuera de la oficina) y retrasaba la ventana.
                    if (!f.StartsWith(@"\\") && !Directory.Exists(f)) continue;
                    var glyph = IconFactory.GlyphForFolder(f);
                    var g = glyph == FolderGlyph.None ? "" : IconFactory.GlyphChar(glyph);
                    var name = string.Equals(f, KnownFolders.Home, StringComparison.OrdinalIgnoreCase) ? Environment.UserName : Location.DisplayNameOf(f);
                    AddSideItem(favPanel, name, g, Location.Folder(f), f, removable: f);
                    break;
            }
        }

        // ---- Ubicaciones ----
        var locPanel = Section("Ubicaciones");
        AddSideItem(locPanel, Environment.MachineName, "", Location.Computer, null);
        foreach (var (d, eject) in _drives)
        {
            var glyph = d.DriveHint switch
            {
                DriveKindHint.Removable => "",
                DriveKindHint.Network => "",
                DriveKindHint.Optical => "",
                _ => ""
            };
            AddSideItem(locPanel, d.Name, glyph, Location.Folder(d.Path), d.Path, ejectRoot: eject ? d.Path : null, drive: d);
        }
        _ = Task.Run(() => Providers.LoadComputer()
            .Select(d => (Drive: d, Eject: d.DriveHint is DriveKindHint.Removable or DriveKindHint.Optical || (d.DriveHint == DriveKindHint.Fixed && Providers.IsEjectable(d.Path))))
            .ToList()).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            var key = string.Join("|", t.Result.Select(x => x.Drive.Path + x.Drive.Name + x.Eject + (x.Drive.Extra >> 30)));
            if (key == _drivesKey) return;
            _drives = t.Result; _drivesKey = key;
            BuildSidebar();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        // si la Papelera tiene algo se sabe en segundo plano (consultarla tardaba ~60 ms en cada reconstrucción)
        bool trashFull = _trashFull;
        AddSideItem(locPanel, "Papelera", trashFull ? "" : "", Location.Trash, null, trash: true);
        _ = Task.Run(() => TrashService.Info().items > 0).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result != _trashFull) { _trashFull = t.Result; BuildSidebar(); }
        }, TaskScheduler.FromCurrentSynchronizationContext());

        // ---- Red ----
        var netPanel = Section("Red");
        AddSideItem(netPanel, "Red", "", Location.Network, null);
        foreach (var srv in Settings.Current.Servers.Take(8))
            AddSideItem(netPanel, NetworkService.HostOf(srv), "", Location.Folder(srv), null, server: srv);
        AddSideItem(netPanel, "Conectar al servidor…", "", null, null, action: ShowConnectToServer);

        // ---- Etiquetas ----
        var tagPanel = Section("Etiquetas");
        foreach (var t in TagStore.Standard)
            AddSideItem(tagPanel, t.Name, null, Location.ForTag(t.Name), null, tagColor: t.Color);

        HighlightSidebar();
    }

    private StackPanel Section(string title, bool allowFolderDrop = false)
    {
        var header = new Grid { Margin = new Thickness(8, 10, 4, 3), Background = Brushes.Transparent, Cursor = Cursors.Arrow };
        var t = new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        var toggle = new TextBlock { Text = CollapsedSections.Contains(title) ? "Mostrar" : "Ocultar", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Hidden };
        toggle.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        header.Children.Add(t); header.Children.Add(toggle);
        var panel = new StackPanel { Visibility = CollapsedSections.Contains(title) ? Visibility.Collapsed : Visibility.Visible };
        header.MouseEnter += (_, _) => toggle.Visibility = Visibility.Visible;
        header.MouseLeave += (_, _) => toggle.Visibility = Visibility.Hidden;
        header.MouseLeftButtonUp += (_, _) =>
        {
            bool collapse = panel.Visibility == Visibility.Visible;
            AnimateCollapse(panel, !collapse);
            toggle.Text = collapse ? "Mostrar" : "Ocultar";
            if (collapse) CollapsedSections.Add(title); else CollapsedSections.Remove(title);
        };
        if (allowFolderDrop)
        {
            // arrastrar una carpeta al título "Favoritos" la añade a la barra lateral
            header.AllowDrop = true;
            header.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
            header.Drop += (_, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                    foreach (var f in files.Where(Directory.Exists)) Pin(f);
                e.Handled = true;
            };
        }
        SidebarStack.Children.Add(header);
        SidebarStack.Children.Add(panel);
        if (allowFolderDrop)
        {
            panel.AllowDrop = true; panel.Background = Brushes.Transparent;
            var hint = new Border { Height = 26, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 2, 0, 0), BorderThickness = new Thickness(1.2), Visibility = Visibility.Collapsed };
            hint.SetResourceReference(Border.BorderBrushProperty, "Accent");
            hint.SetResourceReference(Border.BackgroundProperty, "Bg.DropTarget");
            var ht = new TextBlock { Text = "Suelta aquí para fijar la carpeta", FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            ht.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Accent");
            hint.Child = ht;
            hint.AllowDrop = true;
            hint.DragOver += (_, e) => { e.Effects = DragDropEffects.Link; e.Handled = true; };
            hint.Drop += (_, e) => { hint.Visibility = Visibility.Collapsed; if (e.Data.GetData(DataFormats.FileDrop) is string[] fs) foreach (var f in fs.Where(Directory.Exists)) Pin(f); e.Handled = true; };
            SidebarStack.Children.Add(hint);
            _pinHint = hint;
            // BuildSidebar se repite (discos, Papelera, tema…): los eventos de SidebarStack se enganchan una sola vez,
            // si no se acumulaban copias que consultaban el disco en cada DragEnter
            if (!_sidebarDragHooked)
            {
                _sidebarDragHooked = true;
                SidebarStack.AllowDrop = true;
                SidebarStack.DragEnter += (_, e) => { if (DragHasNewFolders(e)) _pinHint.Visibility = Visibility.Visible; };
                // durante el arrastre IsMouseOver no se actualiza y DragLeave sube desde cada fila: se mira si el
                // cursor salió de verdad de la barra (antes la zona aparecía y desaparecía y movía las filas)
                SidebarStack.DragLeave += (_, e) =>
                {
                    var p = e.GetPosition(SidebarStack);
                    if (p.X < 0 || p.Y < 0 || p.X >= SidebarStack.ActualWidth || p.Y >= SidebarStack.ActualHeight) _pinHint.Visibility = Visibility.Collapsed;
                };
                SidebarStack.Drop += (_, _) => _pinHint.Visibility = Visibility.Collapsed;
            }
            panel.DragOver += (_, e) => { if (e.Handled) return; e.Effects = DragHasNewFolders(e) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
            panel.Drop += (_, e) =>
            {
                hint.Visibility = Visibility.Collapsed;
                if (e.Handled) return;
                if (e.Data.GetData(DataFormats.FileDrop) is string[] fs) foreach (var f in fs.Where(Directory.Exists)) Pin(f);
                e.Handled = true;
            };
        }
        return panel;
    }

    private void AddSideItem(StackPanel panel, string text, string glyph, Location loc, string dropFolder,
        string ejectRoot = null, string tagColor = null, string removable = null, bool trash = false,
        FileItem drive = null, string server = null, Action action = null)
    {
        var grid = new Grid { Height = drive is { IsDrive: true } ? 36 : 28 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (tagColor != null)
        {
            grid.Children.Add(new Ellipse { Width = 11, Height = 11, Fill = Theme.BrushFromHex(tagColor), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            var icon = new TextBlock { Text = glyph, FontFamily = IconFactory.SymbolFont, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Fg.SidebarIcon");
            grid.Children.Add(icon);
        }
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 0, 0), FontSize = 13 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        Grid.SetColumn(label, 1);
        if (drive is { IsDrive: true })
        {
            // como el Explorador: barrita de lo ocupado debajo del nombre (roja si queda menos del 10 %)
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) };
            label.Margin = new Thickness(0);
            stack.Children.Add(label);
            var track = new Grid { Height = 3, Margin = new Thickness(0, 4, 0, 0) };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(drive.UsedFraction, GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - drive.UsedFraction, GridUnitType.Star) });
            var back = new Border { CornerRadius = new CornerRadius(1.5) };
            back.SetResourceReference(Border.BackgroundProperty, "Line.Strong");
            Grid.SetColumnSpan(back, 2);
            var used = new Border { CornerRadius = new CornerRadius(1.5) };
            if (drive.IsAlmostFull) used.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
            else used.SetResourceReference(Border.BackgroundProperty, "Accent");
            track.Children.Add(back); track.Children.Add(used);
            stack.Children.Add(track);
            stack.ToolTip = drive.FreeText;
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);
        }
        else grid.Children.Add(label);
        if (ejectRoot != null)
        {
            var ej = new Button { Style = (Style)FindResource("ToolButton"), Height = 20, MinWidth = 20, Padding = new Thickness(0), ToolTip = "Expulsar" };
            var ejIcon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M5,0 L10,6 L0,6 Z M0,7.6 H10 V9.4 H0 Z"), Width = 10, Height = 10 };
            ejIcon.SetResourceReference(Shape.FillProperty, "Fg.Secondary");
            ej.Content = ejIcon;
            ej.Click += (_, e) => { e.Handled = true; Eject(ejectRoot); };
            Grid.SetColumn(ej, 2);
            grid.Children.Add(ej);
        }
        var border = new Border { Child = grid, CornerRadius = new CornerRadius(6), Padding = new Thickness(2, 0, 4, 0), Background = Brushes.Transparent, AllowDrop = dropFolder != null || trash || tagColor != null };
        var entry = new SideEntry { Location = loc, Element = border, DropFolder = dropFolder };
        if (loc != null) _sideEntries.Add(entry);
        if (action != null)
        {
            border.MouseLeftButtonUp += (_, _) => action();
            border.MouseEnter += (_, _) => border.SetResourceReference(Border.BackgroundProperty, "Bg.Hover");
            border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
            panel.Children.Add(border);
            return;
        }

        border.MouseLeftButtonUp += (_, e) =>
        {
            if (_tab == null) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _ = AddTab(loc, true);
            else _ = _tab.NavigateAsync(loc);
        };
        border.MouseEnter += (_, _) => { if (!IsCurrent(loc)) border.SetResourceReference(Border.BackgroundProperty, "Bg.Hover"); };
        border.MouseLeave += (_, _) => HighlightSidebar();
        border.MouseRightButtonUp += (_, e) =>
        {
            var menu = new ContextMenu();
            Add(menu, "Abrir en una pestaña nueva", () => _ = AddTab(loc, true));
            Add(menu, "Abrir en una ventana nueva", () => new MainWindow(loc).Show());
            if (dropFolder != null)
            {
                Add(menu, "Mostrar en el Explorador de Windows", () => ShellInterop.RevealInExplorer(dropFolder));
                Add(menu, "Obtener información", () => new Windows.InfoWindow(FileItem.FromPath(dropFolder) ?? new FileItem(dropFolder, text, true, -1, DateTime.Now, DateTime.Now, FileAttributes.Directory)) { Owner = this }.Show());
            }
            if (ejectRoot != null) { menu.Items.Add(new Separator()); Add(menu, $"Expulsar «{text}»", () => Eject(ejectRoot)); }
            if (server != null)
            {
                menu.Items.Add(new Separator());
                Add(menu, "Entrar con otro usuario…", () => ConnectAs(server));
                Add(menu, "Copiar la dirección", () => { try { Clipboard.SetText(server); } catch { } });
                Add(menu, "Olvidar este servidor", () => { NetworkService.Forget(server); foreach (var w in Application.Current.Windows.OfType<MainWindow>()) w.BuildSidebar(); });
            }
            if (trash) { menu.Items.Add(new Separator()); Add(menu, "Vaciar la Papelera…", () => EmptyTrash()); }
            if (removable != null)
            {
                menu.Items.Add(new Separator());
                Add(menu, "Quitar de la barra lateral", () =>
                {
                    Settings.Current.Favorites.RemoveAll(x => string.Equals(x, removable, StringComparison.OrdinalIgnoreCase));
                    Settings.Current.Save(); BuildSidebar();
                });
            }
            menu.PlacementTarget = border; menu.IsOpen = true;
            e.Handled = true;
        };
        // soltar archivos: en una carpeta/disco = copiar o mover; en la Papelera = trasladar; en una etiqueta = etiquetar
        void ClearInsertLine() { border.BorderThickness = new Thickness(0); border.Margin = new Thickness(0); }
        border.SetResourceReference(Border.BorderBrushProperty, "Accent");
        border.DragOver += (_, e) =>
        {
            e.Handled = true;
            if (PinSlot(border, removable, e) is var slot && slot >= 0)
            {
                // línea azul arriba o abajo de la fila: se fija ahí, no se mete dentro de la carpeta
                bool above = e.GetPosition(border).Y < border.ActualHeight / 2;
                // el margen negativo compensa el borde: las filas no se mueven bajo el cursor
                border.BorderThickness = above ? new Thickness(0, 2, 0, 0) : new Thickness(0, 0, 0, 2);
                border.Margin = above ? new Thickness(0, -2, 0, 0) : new Thickness(0, 0, 0, -2);
                HighlightSidebar();
                e.Effects = DragDropEffects.Link;
                return;
            }
            ClearInsertLine();
            if (trash) e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None;
            else if (tagColor != null) e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            else e.Effects = DragOverFolder(dropFolder, e);
            border.SetResourceReference(Border.BackgroundProperty, "Bg.DropTarget");
        };
        border.DragLeave += (_, _) => { ClearInsertLine(); HighlightSidebar(); };
        border.Drop += (_, e) =>
        {
            ClearInsertLine();
            HighlightSidebar();
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            e.Handled = true;
            if (PinSlot(border, removable, e) is var slot && slot >= 0)
            {
                var folders = files.Where(f => Directory.Exists(f) && !IsPinned(f)).ToList();
                // fuera del bucle del arrastre: Pin reconstruye la barra lateral
                Dispatcher.BeginInvoke(() => { for (int i = 0; i < folders.Count; i++) Pin(folders[i], slot + i); });
                e.Effects = DragDropEffects.Link;
                return;
            }
            if (trash) MoveToTrash(files.ToList());
            else if (tagColor != null) { foreach (var f in files) TagStore.Toggle(new[] { f }, text); }
            else if (NetworkService.IsUnc(dropFolder))
            {
                // un favorito de red está a un paso de la zona de fijar: se pregunta antes de copiar al servidor
                var effect = DragOverFolder(dropFolder, e);
                if (effect == DragDropEffects.None) return;
                var what = files.Length == 1 ? $"«{FileItem.NameOf(files[0])}»" : $"{files.Length} elementos";
                Dispatcher.BeginInvoke(() =>
                {
                    if (Dialogs.MacDialog.Confirm(this, $"¿Copiar {what} a «{text}» en el servidor?",
                            $"Se copiará a {dropFolder}. Si sólo querías fijar la carpeta en la barra lateral, pulsa Cancelar y suéltala en «Suelta aquí para fijar la carpeta».",
                            "Copiar"))
                        TransferManager.Instance.Start(effect == DragDropEffects.Move ? TransferKind.Move : TransferKind.Copy, files, dropFolder);
                });
                e.Effects = DragDropEffects.Copy;
            }
            else DropOnFolder(dropFolder, e);
        };
        panel.Children.Add(border);
    }

    private static void Add(ContextMenu m, string text, Action a, string gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = text, InputGestureText = gesture ?? "", IsEnabled = enabled };
        mi.Click += (_, _) => a();
        m.Items.Add(mi);
    }

    private bool IsCurrent(Location loc) => _tab?.Location != null && _tab.Location.Equals(loc);

    private void HighlightSidebar()
    {
        foreach (var e in _sideEntries)
        {
            if (IsCurrent(e.Location)) e.Element.SetResourceReference(Border.BackgroundProperty, "Bg.SidebarSelected");
            else e.Element.Background = Brushes.Transparent;
        }
    }

    private void Eject(string root)
    {
        // si alguna pestaña está dentro del disco, salir de él antes de expulsar
        foreach (var w in Application.Current.Windows.OfType<MainWindow>())
            foreach (var t in w.Tabs)
                if (t.Location?.Path?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true)
                    _ = t.NavigateAsync(Location.Computer);
        if (TransferManager.Instance.Jobs.Any(j => j.Job.Destination.StartsWith(root, StringComparison.OrdinalIgnoreCase) || j.Job.Sources.Any(s => s.StartsWith(root, StringComparison.OrdinalIgnoreCase))))
        {
            Dialogs.MacDialog.Error(this, "No se puede expulsar el disco ahora.", "Hay una copia en curso que usa este disco. Espera a que termine o detenla.");
            return;
        }
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(400);
            if (!ShellInterop.Eject(root))
                Dialogs.MacDialog.Error(this, $"No se pudo expulsar «{root.TrimEnd('\\')}».", "Puede que algún programa lo esté usando. Cierra los archivos abiertos de ese disco e inténtalo otra vez.");
            await Task.Delay(1500);
            BuildSidebar();
        });
    }
}
