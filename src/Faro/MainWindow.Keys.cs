using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Faro.Model;
using Faro.Services;

namespace Faro;

/// <summary>
/// Atajos de teclado de Finder traducidos a Windows: ⌘ = Ctrl, ⌥ = Alt, ⇧ = Mayús.
/// También se aceptan los de siempre de Windows (F2, F5, Supr, Alt+←...).
/// </summary>
public partial class MainWindow
{
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_tab == null) return; // la ventana aún no tiene pestaña (primer instante tras abrir)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        bool inText = Keyboard.FocusedElement is TextBox;

        // atajos que funcionan incluso escribiendo
        if (ctrl && !shift && !alt && key == Key.F) { SearchText.Focus(); SearchText.SelectAll(); e.Handled = true; return; }
        if (ctrl && !shift && key == Key.W) { if (_tab != null) CloseTab(_tab); e.Handled = true; return; }
        if (ctrl && !shift && !alt && key == Key.N) { new MainWindow(_tab?.Location?.IsFolder == true ? _tab.Location : DefaultLocation()).Show(); e.Handled = true; return; }
        if (ctrl && !shift && !alt && key == Key.T) { NewTab_Click(null, null); e.Handled = true; return; }
        if (ctrl && key == Key.Tab) { CycleTab(shift ? -1 : 1); e.Handled = true; return; }
        if (inText) return;
        // con un vídeo o audio en la vista rápida, las flechas lo adelantan/atrasan y suben/bajan el volumen
        if (QuickLook?.HandleMediaKey(key, mods) == true) { e.Handled = true; return; }

        bool handled = true;
        switch (key)
        {
            // ----- vistas -----
            case Key.D1 when ctrl && !alt: case Key.NumPad1 when ctrl && !alt: ApplyViewMode(ViewMode.Icons, true); break;
            case Key.D2 when ctrl && !alt: case Key.NumPad2 when ctrl && !alt: ApplyViewMode(ViewMode.List, true); break;
            case Key.D3 when ctrl && !alt: case Key.NumPad3 when ctrl && !alt: ApplyViewMode(ViewMode.Columns, true); break;
            case Key.D4 when ctrl && !alt: case Key.NumPad4 when ctrl && !alt: ApplyViewMode(ViewMode.Gallery, true); break;
            case Key.D1 when ctrl && alt: SortBy(SortField.Name); break;
            case Key.D2 when ctrl && alt: SortBy(SortField.Kind); break;
            case Key.D4 when ctrl && alt: SortBy(SortField.Modified); break;
            case Key.D5 when ctrl && alt: SortBy(SortField.Created); break;
            case Key.D6 when ctrl && alt: SortBy(SortField.Size); break;
            case Key.J when ctrl: ShowViewOptions(); break;
            case Key.OemPlus when ctrl: case Key.Add when ctrl: ZoomSlider.Value = Math.Min(256, ZoomSlider.Value + 16); break;
            case Key.OemMinus when ctrl: case Key.Subtract when ctrl: ZoomSlider.Value = Math.Max(32, ZoomSlider.Value - 16); break;

            // ----- navegación -----
            case Key.OemOpenBrackets when ctrl: _ = GoBack(); break;
            case Key.Oem6 when ctrl: _ = _tab?.GoForwardAsync(); break;
            case Key.Left when alt && !ctrl: _ = GoBack(); break;
            case Key.Right when alt && !ctrl && _tab?.Mode != ViewMode.List: _ = _tab?.GoForwardAsync(); break;
            case Key.Back when !ctrl && !shift: _ = GoBack(); break;
            case Key.BrowserBack: _ = GoBack(); break;
            case Key.BrowserForward: _ = _tab?.GoForwardAsync(); break;
            case Key.Up when ctrl || (alt && !shift): NavDirection = -1; GoUp(ctrl && alt); break;
            case Key.Down when ctrl: OpenItems(Selection); break;
            case Key.O when ctrl && !shift: OpenItems(Selection); break;
            case Key.Enter when ctrl: OpenItems(Selection); break;
            case Key.G when ctrl && shift: ShowGoTo(); break;
            case Key.K when ctrl && shift: _ = _tab.NavigateAsync(Location.Network); break;
            case Key.K when ctrl && !alt: ShowConnectToServer(); break;
            case Key.H when ctrl && shift: _ = _tab.NavigateAsync(Location.Folder(KnownFolders.Home)); break;
            case Key.D when ctrl && shift: _ = _tab.NavigateAsync(Location.Folder(KnownFolders.Desktop)); break;
            case Key.O when ctrl && shift: _ = _tab.NavigateAsync(Location.Folder(KnownFolders.Documents)); break;
            case Key.L when ctrl && alt: _ = _tab.NavigateAsync(Location.Folder(KnownFolders.Downloads)); break;
            case Key.A when ctrl && shift: _ = _tab.NavigateAsync(Location.Apps); break;
            case Key.C when ctrl && shift: _ = _tab.NavigateAsync(Location.Computer); break;
            case Key.F when ctrl && shift: _ = _tab.NavigateAsync(Location.Recents); break;
            case Key.F5: _ = _tab?.RefreshAsync(); break;
            case Key.R when ctrl && !alt && !shift: if (Selection.Any(s => s.IsShortcut)) ShowOriginal(); else _ = _tab?.LoadAsync(); break;
            case Key.R when ctrl && alt: if (Selection.Count > 0) _ = RevealInFaro(Selection[0].Path); break;

            // ----- archivos -----
            case Key.Enter when !ctrl && !alt && !shift: case Key.F2: BeginRename(); break;
            case Key.Space when !ctrl: ToggleQuickLook(); break;
            case Key.Y when ctrl: ToggleQuickLook(); break;
            case Key.I when ctrl: GetInfo(); break;
            case Key.N when ctrl && shift: NewFolder(); break;
            case Key.N when ctrl && alt: NewFolder(true); break;
            case Key.D when ctrl && !shift: Duplicate(); break;
            case Key.L when ctrl && !alt: MakeAlias(); break;
            case Key.C when ctrl && alt: CopyPathOfSelection(); break;
            case Key.C when ctrl: CopySelection(); break;
            case Key.X when ctrl: CopySelection(cut: true); break;
            case Key.V when ctrl && alt: Paste(forceMove: true); break;
            case Key.V when ctrl: Paste(); break;
            case Key.Z when ctrl && !shift: _ = Undo(); break;
            case Key.A when ctrl && !shift && !alt: _view?.SelectAll(); break;
            case Key.A when ctrl && alt: _view?.SelectItems(Array.Empty<FileItem>()); break;
            case Key.Delete when shift && ctrl: EmptyTrash(); break;
            case Key.Back when ctrl && shift: EmptyTrash(); break;
            case Key.Delete when shift: DeleteImmediately(); break;
            case Key.Back when ctrl && alt: DeleteImmediately(); break;
            case Key.Delete: case Key.Back when ctrl: MoveSelectionToTrash(); break;
            case Key.E when ctrl: { var d = Selection.FirstOrDefault(s => s.Special == ItemSpecial.Drive); if (d != null) Eject(d.Path); else if (_tab?.FolderPath is { Length: <= 3 } r) Eject(r); break; }
            case Key.T when ctrl && alt:
                { var dirs = Selection.Where(s => s.IsDirectory && s.Special == ItemSpecial.None).ToList(); if (dirs.Count == 0 && _tab?.FolderPath != null) Pin(_tab.FolderPath); foreach (var d in dirs) Pin(d.Path); }
                break;
            case Key.F10 when shift: case Key.Apps: OpenContextMenu(ViewHost, Selection); break;

            // ----- ventana -----
            case Key.OemPeriod when ctrl && shift: ToggleHidden(); break;
            case Key.S when ctrl && alt: ToggleSidebar(); break;
            case Key.P when ctrl && alt: Settings.Current.ShowPathBar = !Settings.Current.ShowPathBar; Settings.NotifyChanged(); break;
            case Key.P when ctrl && shift: Settings.Current.ShowPreviewPane = !Settings.Current.ShowPreviewPane; Settings.NotifyChanged(); break;
            case Key.Oem2 when ctrl: Settings.Current.ShowStatusBar = !Settings.Current.ShowStatusBar; Settings.NotifyChanged(); break;
            case Key.OemComma when ctrl: ShowPreferences(); break;
            case Key.F1: ShowShortcuts(); break;
            case Key.D when alt && !ctrl: BeginPathEdit(); break;
            case Key.M when ctrl: WindowState = WindowState.Minimized; break;
            case Key.F when ctrl && (Keyboard.IsKeyDown(Key.LWin) || alt): ToggleFullScreen(); break;
            case Key.F11: ToggleFullScreen(); break;
            case Key.Escape:
                if (_fullScreen) ToggleFullScreen();
                else if (_tab?.IsSearching == true) _ = EndSearch();
                else if (QuickLook?.IsVisible == true) ToggleQuickLook();
                else handled = false;
                break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }

    private void SortBy(SortField f)
    {
        var s = Settings.Current;
        s.SortBy = f;
        s.SortDescending = f is SortField.Modified or SortField.Created or SortField.Size;
        Settings.NotifyChanged();
        _tab?.Resort();
    }

    private void CycleTab(int dir)
    {
        if (Tabs.Count < 2) return;
        int i = (Tabs.IndexOf(_tab) + dir + Tabs.Count) % Tabs.Count;
        ActivateTab(Tabs[i]);
    }

    /// <summary>Ctrl+↑: carpeta contenedora, dejando seleccionada la carpeta de la que se sale.</summary>
    private async void GoUp(bool newWindow)
    {
        if (_tab?.Location == null) return;
        string from = _tab.Location.Kind == LocationKind.Folder ? _tab.Location.Path : null;
        if (from == null) return;
        var parent = Path.GetDirectoryName(from);
        var target = parent == null ? Location.Computer : Location.Folder(parent);
        if (newWindow) { new MainWindow(target).Show(); return; }
        await _tab.NavigateAsync(target);
        var it = _tab.Items.FirstOrDefault(i => string.Equals(i.Path, from, StringComparison.OrdinalIgnoreCase));
        if (it != null) _view?.SelectItems(new[] { it });
    }
}
