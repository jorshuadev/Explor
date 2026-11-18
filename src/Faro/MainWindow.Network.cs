using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Faro.Core;
using Faro.Model;
using Faro.Services;

namespace Faro;

/// <summary>
/// Red y barra de ruta: historial de carpetas con sugerencias al escribir, entrar en \\servidor
/// pidiendo usuario y contraseña cuando hace falta, y «Conectar al servidor» (Ctrl+K).
/// </summary>
public partial class MainWindow
{
    private static bool _netHooked;

    /// <summary>Tras cargar una carpeta: al historial (y si es de red, el servidor a «Red»).</summary>
    private void RecordVisit()
    {
        if (!_netHooked)
        {
            _netHooked = true;
            NetworkService.Discovered += () =>
            {
                foreach (var w in Application.Current.Windows.OfType<MainWindow>())
                    foreach (var t in w.Tabs) if (t.Location?.Kind == LocationKind.Network) _ = t.LoadAsync();
            };
        }
        if (_tab?.Location?.Kind != LocationKind.Folder || _tab.HasError || _tab.IsLoading) return;
        var p = _tab.Location.Path;
        PathHistory.Add(p);
        if (NetworkService.IsUnc(p))
        {
            bool isNew = !Settings.Current.Servers.Contains(NetworkService.ServerOf(p), StringComparer.OrdinalIgnoreCase);
            NetworkService.Remember(p);
            if (isNew) foreach (var w in Application.Current.Windows.OfType<MainWindow>()) w.BuildSidebar();
        }
    }

    // ======================== espacio libre (barra de estado) ========================

    private static readonly Dictionary<string, (DateTime when, string text)> FreeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// «, 120 GB disponibles de 476 GB». En una carpeta compartida se pregunta al servidor en segundo
    /// plano y se repinta al llegar (preguntar en el hilo de la ventana la congelaba si tarda).
    /// </summary>
    private string FreeSpaceText(string path)
    {
        var key = NetworkService.IsUnc(path) ? NetworkService.ShareRootOf(path) : Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(key) || NetworkService.IsServerRoot(key)) return "";
        if (FreeCache.TryGetValue(key, out var c) && DateTime.UtcNow - c.when < TimeSpan.FromSeconds(NetworkService.IsUnc(key) ? 30 : 5)) return c.text;
        if (!NetworkService.IsUnc(key))
        {
            var sp = NetworkService.Space(key);
            var t = sp is { } v ? $", {Format.Size(v.free)} disponibles de {Format.Size(v.total)}" : "";
            FreeCache[key] = (DateTime.UtcNow, t);
            return t;
        }
        FreeCache[key] = (DateTime.UtcNow, c.text ?? ""); // que no se lance otra consulta mientras tanto
        _ = Task.Run(() => NetworkService.Space(key)).ContinueWith(r =>
        {
            var sp = r.Status == TaskStatus.RanToCompletion ? r.Result : null;
            FreeCache[key] = (DateTime.UtcNow, sp is { } v ? $", {Format.Size(v.free)} disponibles de {Format.Size(v.total)}" : "");
            UpdateStatus();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return c.text ?? "";
    }

    // ======================== usuario y contraseña ========================

    private string _loginAsked;

    /// <summary>La carpeta de red pidió credenciales: se muestra la ventana de Windows (una vez por intento).</summary>
    private void OfferLogin()
    {
        var tab = _tab;
        if (tab?.NeedsLogin != true || tab.Location?.Path == null) return;
        var path = tab.Location.Path;
        if (string.Equals(_loginAsked, path, StringComparison.OrdinalIgnoreCase)) return;
        _loginAsked = path;
        Dispatcher.BeginInvoke(() =>
        {
            if (_tab != tab || tab.Location?.Path != path) return;
            if (NetworkService.ConnectInteractive(path, new WindowInteropHelper(this).Handle, out var err))
            {
                _ = tab.LoadAsync().ContinueWith(_ => _loginAsked = null, TaskScheduler.FromCurrentSynchronizationContext());
            }
            else if (err != null) Dialogs.MacDialog.Error(this, $"No se pudo entrar en «{Location.DisplayNameOf(path)}».", err);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Entrar con otro usuario (menú de la carpeta de red).</summary>
    public void ConnectAs(string path)
    {
        if (NetworkService.ConnectInteractive(path, new WindowInteropHelper(this).Handle, out var err, forcePrompt: true))
            _ = _tab.NavigateAsync(Location.Folder(path));
        else if (err != null) Dialogs.MacDialog.Error(this, $"No se pudo entrar en «{Location.DisplayNameOf(path)}».", err);
    }

    public void ShowConnectToServer()
    {
        var w = new Windows.ConnectServerWindow { Owner = this };
        if (w.ShowDialog() == true && w.Result != null)
        {
            if (w.Result == "faro:network") _ = _tab.NavigateAsync(Location.Network);
            else { _loginAsked = null; _ = _tab.NavigateAsync(Location.Folder(w.Result)); }
        }
    }

    // ======================== sugerencias de la barra de ruta ========================

    private Popup _suggestPopup;
    private ListBox _suggestList;
    private CancellationTokenSource _suggestCts;
    private bool _suggestFresh; // recién abierta: el texto es la carpeta actual → sólo historial

    private void EnsureSuggestPopup()
    {
        if (_suggestPopup != null) return;
        _suggestList = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Focusable = false, MaxHeight = 320 };
        ScrollViewer.SetHorizontalScrollBarVisibility(_suggestList, ScrollBarVisibility.Disabled);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(FocusableProperty, false));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
        _suggestList.ItemContainerStyle = style;
        var border = new Border { Child = _suggestList, CornerRadius = new CornerRadius(8), Padding = new Thickness(4), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 4) };
        border.SetResourceReference(Border.BackgroundProperty, "Bg.Popup");
        border.SetResourceReference(Border.BorderBrushProperty, "Line.Popup");
        _suggestPopup = new Popup
        {
            Child = border, PlacementTarget = PathEdit, Placement = PlacementMode.Top, StaysOpen = true,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade
        };
    }

    private void ShowSuggestions(bool fresh)
    {
        _suggestFresh = fresh;
        RefreshSuggestions();
    }

    private async void RefreshSuggestions()
    {
        if (PathEdit.Visibility != Visibility.Visible) return;
        EnsureSuggestPopup();
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        List<PathSuggestion> list;
        try { list = await PathHistory.SuggestAsync(PathEdit.Text, _suggestFresh, cts.Token); }
        catch { return; }
        if (cts.IsCancellationRequested || PathEdit.Visibility != Visibility.Visible) return;
        var current = _tab?.FolderPath;
        _suggestList.Items.Clear();
        foreach (var s in list)
        {
            if (_suggestFresh && string.Equals(s.Path, current, StringComparison.OrdinalIgnoreCase)) continue;
            _suggestList.Items.Add(SuggestionRow(s));
        }
        if (_suggestList.Items.Count == 0) { _suggestPopup.IsOpen = false; return; }
        _suggestPopup.Width = Math.Max(PathEdit.ActualWidth, 320);
        _suggestPopup.IsOpen = true;
    }

    private ListBoxItem SuggestionRow(PathSuggestion s)
    {
        var glyph = s.IsServer || NetworkService.IsServerRoot(s.Path) ? "" : s.FromHistory ? "" : "";
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new TextBlock { Text = glyph, FontFamily = IconFactory.SymbolFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        var text = new TextBlock { Text = s.Path, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        Grid.SetColumn(text, 1);
        g.Children.Add(icon); g.Children.Add(text);
        var item = new ListBoxItem { Content = g, Tag = s, ToolTip = s.FromHistory ? s.Path + "\n(Mayús+Supr la quita del historial)" : s.Path };
        // sin foco: hacer clic no le quita el foco al cuadro (si no, se cerraría la edición antes del clic)
        item.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; GoToTypedPath(s.Path); };
        return item;
    }

    private void HideSuggestions()
    {
        _suggestCts?.Cancel();
        if (_suggestPopup != null) _suggestPopup.IsOpen = false;
    }

    private void PathEdit_TextChanged(object s, TextChangedEventArgs e)
    {
        if (PathEdit.Visibility != Visibility.Visible) return;
        _suggestFresh = false;
        RefreshSuggestions();
    }

    /// <summary>Teclas de la lista de sugerencias; devuelve true si las ha usado.</summary>
    private bool SuggestKey(KeyEventArgs e)
    {
        if (_suggestPopup?.IsOpen != true || _suggestList.Items.Count == 0) return false;
        var sel = (_suggestList.SelectedItem as ListBoxItem)?.Tag as PathSuggestion;
        switch (e.Key)
        {
            case Key.Down:
                _suggestList.SelectedIndex = Math.Min(_suggestList.Items.Count - 1, _suggestList.SelectedIndex + 1);
                _suggestList.ScrollIntoView(_suggestList.SelectedItem);
                return true;
            case Key.Up:
                _suggestList.SelectedIndex = Math.Max(-1, _suggestList.SelectedIndex - 1);
                if (_suggestList.SelectedItem != null) _suggestList.ScrollIntoView(_suggestList.SelectedItem);
                return true;
            case Key.Tab:
                var pick = sel ?? ((ListBoxItem)_suggestList.Items[0]).Tag as PathSuggestion;
                PathEdit.Text = pick.Path.TrimEnd('\\') + "\\";
                PathEdit.CaretIndex = PathEdit.Text.Length;
                return true;
            case Key.Enter when sel != null:
                GoToTypedPath(sel.Path);
                return true;
            case Key.Delete when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && sel?.FromHistory == true:
                PathHistory.Remove(sel.Path);
                _suggestList.Items.Remove(_suggestList.SelectedItem);
                if (_suggestList.Items.Count == 0) _suggestPopup.IsOpen = false;
                return true;
            case Key.Escape when _suggestList.SelectedIndex >= 0:
                _suggestList.SelectedIndex = -1;
                return true;
        }
        return false;
    }

    /// <summary>Ir a lo escrito en la barra de ruta. Devuelve false (y suena) si no existe.</summary>
    private bool GoToTypedPath(string typed)
    {
        var t = PathHistory.Expand(typed);
        if (t.Length > 3) t = t.TrimEnd('\\');
        if (NetworkService.IsUnc(t))
        {
            // de red no se comprueba aquí (esperaría al servidor con la ventana congelada): la pestaña avisa si no hay acceso
            _loginAsked = null;
            _ = _tab.NavigateAsync(Location.Folder(t));
        }
        else if (Directory.Exists(t)) _ = _tab.NavigateAsync(Location.Folder(t));
        else if (File.Exists(t)) _ = RevealInFaro(t);
        else { System.Media.SystemSounds.Beep.Play(); return false; }
        HideSuggestions();
        EndPathEdit();
        return true;
    }
}
