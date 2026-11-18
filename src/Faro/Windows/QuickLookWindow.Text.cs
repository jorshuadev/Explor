using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Faro.Dialogs;
using Faro.Model;
using Faro.Services;

namespace Faro.Windows;

/// <summary>
/// Visor de texto de la visualización rápida: seleccionar y copiar, buscar (Ctrl+F), ajustar
/// líneas, tamaño de letra (Ctrl+rueda) y edición con Guardar (Ctrl+S). Al guardar se respeta la
/// codificación y el tipo de salto de línea del archivo original.
/// </summary>
public sealed partial class QuickLookWindow
{
    private const int MaxTextBytes = 1_000_000;

    /// <summary>Texto leído y cómo estaba escrito, para guardarlo igual.</summary>
    private sealed record TextFile(string Path, string Text, Encoding Encoding, byte[] Preamble, string NewLine, bool Truncated, DateTime WriteTimeUtc);

    private static double _textFontScale = 1; // Ctrl+rueda; se mantiene mientras Explor esté abierto
    private TextBox _text;
    private TextFile _textFile;
    private FileItem _textItem;
    private bool _editing, _dirty;
    private Border _textFrame;
    private Grid _findBar;
    private TextBox _find;
    private TextBlock _findCount;
    private Button _editBtn, _saveBtn, _doneBtn;
    private string _textTitle;
    private Border _findMark;
    // Markdown: se muestra con formato; «Ver texto» (o Editar/Buscar) pasa al texto plano
    private FlowDocumentScrollViewer _mdView;
    private Button _mdBtn;
    private ToggleButton _wrapBtn;
    private int _markAt = -1, _markLen;

    private void PlaceMark()
    {
        if (_findMark == null) return;
        if (_markAt < 0 || _text == null || _findBar?.Visibility != Visibility.Visible || _markAt + _markLen > _text.Text.Length)
        { _findMark.Visibility = Visibility.Collapsed; return; }
        var a = _text.GetRectFromCharacterIndex(_markAt);
        var b = _text.GetRectFromCharacterIndex(_markAt + _markLen - 1, true);
        if (a.IsEmpty || b.IsEmpty) { _findMark.Visibility = Visibility.Collapsed; return; }
        double right = Math.Abs(b.Top - a.Top) < 1 ? b.Right : a.Left + 40; // si la palabra se parte de línea, marcar el principio
        Canvas.SetLeft(_findMark, a.Left - 2); Canvas.SetTop(_findMark, a.Top);
        _findMark.Width = Math.Max(4, right - a.Left + 4); _findMark.Height = a.Height;
        _findMark.Visibility = Visibility.Visible;
    }

    private static TextFile ReadText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = (int)Math.Min(fs.Length, MaxTextBytes);
            var buf = new byte[len];
            int n = fs.Read(buf, 0, len);
            bool truncated = fs.Length > len;
            var (enc, pre) = DetectEncoding(buf, n, truncated);
            var text = enc.GetString(buf, pre.Length, n - pre.Length);
            if (truncated) text += "\n\n… (archivo truncado en la vista previa)";
            string nl = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : Environment.NewLine;
            return new TextFile(path, text, enc, pre, nl, truncated, File.GetLastWriteTimeUtc(path));
        }
        catch { return null; }
    }

    private static (Encoding enc, byte[] preamble) DetectEncoding(byte[] b, int n, bool truncated)
    {
        if (n >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (new UTF8Encoding(false), new byte[] { 0xEF, 0xBB, 0xBF });
        if (n >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (new UnicodeEncoding(false, false), new byte[] { 0xFF, 0xFE });
        if (n >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (new UnicodeEncoding(true, false), new byte[] { 0xFE, 0xFF });
        // si se cortó a 1 MB, el último carácter UTF-8 puede quedar a medias: no contarlo al validar
        int k = n;
        if (truncated)
        {
            while (k > 0 && (b[k - 1] & 0xC0) == 0x80) k--;
            if (k > 0 && b[k - 1] >= 0xC0) k--;
        }
        try { new UTF8Encoding(false, true).GetString(b, 0, k); return (new UTF8Encoding(false), Array.Empty<byte>()); }
        catch { return (Encoding.Latin1, Array.Empty<byte>()); }
    }

    private FrameworkElement TextView(TextFile f, FileItem item, bool code)
    {
        _textFile = f; _textItem = item; _editing = false; _dirty = false;
        _mdView = null; _mdBtn = null;
        double baseSize = code ? 12.5 : 13.5;
        var tb = _text = new TextBox
        {
            IsUndoEnabled = false, Text = f.Text, IsReadOnly = true, IsReadOnlyCaretVisible = false, IsInactiveSelectionHighlightEnabled = true,
            AcceptsReturn = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontFamily = code ? (FontFamily)FindResource("MonoFont") : (FontFamily)FindResource("UiFont"), FontSize = baseSize * _textFontScale,
            TextWrapping = code ? TextWrapping.NoWrap : TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18, 6, 18, 14)
        };
        tb.IsUndoEnabled = true;
        tb.SetResourceReference(TextBox.ForegroundProperty, "Fg.Primary");
        tb.SetResourceReference(TextBoxBase.CaretBrushProperty, "Fg.Primary");
        tb.SetResourceReference(TextBoxBase.SelectionBrushProperty, "Accent");
        // la coincidencia de «Buscar» debe verse aunque el foco esté en el cuadro de búsqueda
        tb.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = FindResource("Accent");
        tb.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Brushes.White;
        tb.ContextMenu = TextMenu(tb);
        tb.TextChanged += (_, _) => { if (_editing && !_dirty) { _dirty = true; UpdateEditUi(); } };
        tb.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            _textFontScale = Math.Clamp(_textFontScale * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.6, 3);
            tb.FontSize = baseSize * _textFontScale;
            e.Handled = true;
        };

        int lines = 1;
        foreach (var c in f.Text) if (c == '\n') lines++;
        if (f.Text.EndsWith('\n')) lines--; // el salto final no abre otra línea
        _textTitle = $"{_titleText}  —  {lines:N0} línea{(lines == 1 ? "" : "s")}";
        SetTitle(_textTitle);

        // barra de búsqueda (oculta hasta Ctrl+F o «Buscar»)
        _findBar = FindBar();
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(_findBar);
        Grid.SetRow(tb, 1); grid.Children.Add(tb);
        // marca amarilla sobre la coincidencia: la selección del cuadro no se ve mientras se escribe en «Buscar»
        _findMark = new Border { Background = new SolidColorBrush(Color.FromArgb(0x78, 0xFF, 0xD6, 0x0A)), CornerRadius = new CornerRadius(3), Visibility = Visibility.Collapsed };
        var marks = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        marks.Children.Add(_findMark);
        Grid.SetRow(marks, 1); grid.Children.Add(marks);
        _markAt = -1;
        tb.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => PlaceMark()));
        tb.SizeChanged += (_, _) => PlaceMark();
        tb.TextChanged += (_, _) => { _markAt = -1; PlaceMark(); };

        if (item.Extension is "md" or "markdown" or "mdown" or "mkd")
        {
            _mdView = new FlowDocumentScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                IsToolBarVisible = false, Zoom = 100 * _textFontScale, MinZoom = 60, MaxZoom = 300, Background = Brushes.Transparent
            };
            _mdView.PreviewMouseWheel += (_, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                _textFontScale = Math.Clamp(_textFontScale * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.6, 3);
                _mdView.Zoom = 100 * _textFontScale;
                tb.FontSize = baseSize * _textFontScale;
                e.Handled = true;
            };
            RenderMarkdown();
            Grid.SetRow(_mdView, 1); grid.Children.Add(_mdView);
        }

        var frame = _textFrame = new Border { Child = grid, Margin = new Thickness(10, 0, 10, 12), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent };
        frame.SetResourceReference(Border.BackgroundProperty, "Bg.Window");

        // botones de la barra
        // buscar y copiar se entienden por el icono; el resto lleva nombre
        _tools.Children.Add(ToolBtn("", "Buscar en el texto (Ctrl+F)", ShowFind));
        _tools.Children.Add(ToolBtn("", "Copiar todo el texto", () =>
        {
            try { Clipboard.SetText(tb.Text); Notice("Texto copiado"); } catch { }
        }));
        var wrap = new ToggleButton { Style = (Style)FindResource("ToolButton"), Height = 26, MinWidth = 0, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 2, 0),
            ToolTip = "Partir las líneas largas para que quepan en la ventana", IsChecked = tb.TextWrapping == TextWrapping.Wrap,
            Content = new TextBlock { Text = "Ajustar líneas", FontSize = 12, VerticalAlignment = VerticalAlignment.Center } };
        void ShowWrap()
        {
            tb.TextWrapping = wrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
            if (wrap.IsChecked == true) wrap.SetResourceReference(BackgroundProperty, "Bg.Pressed"); else wrap.Background = Brushes.Transparent;
        }
        wrap.Click += (_, _) => ShowWrap();
        ShowWrap();
        _tools.Children.Add(wrap);
        _wrapBtn = wrap;
        if (_mdView != null)
        {
            _mdBtn = TextBtn("Ver texto", "Ver el Markdown tal cual está escrito", () => ShowMarkdown(_mdView.Visibility != Visibility.Visible));
            _tools.Children.Insert(_tools.Children.IndexOf(wrap), _mdBtn);
            ShowMarkdown(true);
        }

        _editBtn = TextBtn("Editar", "Editar el texto; se guarda con Ctrl+S", BeginEdit);
        var why = WhyNotEditable(f, item);
        if (why != null) { _editBtn.IsEnabled = false; _editBtn.ToolTip = why; }
        // en edición: «Cancelar» (deja el archivo como estaba) y «Guardar» en azul (guarda y termina)
        _doneBtn = new Button { Style = (Style)FindResource("MacButton"), Content = "Cancelar", ToolTip = "Dejar de editar sin guardar (Esc)", Height = 24, MinWidth = 0, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(6, 0, 0, 0), Focusable = false };
        _doneBtn.Click += (_, _) => CancelEdit();
        _saveBtn = new Button { Style = (Style)FindResource("MacButton"), Content = "Guardar", ToolTip = "Guardar y terminar de editar (Ctrl+S guarda sin salir)", Height = 24, MinWidth = 0, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(6, 0, 4, 0), Focusable = false, Foreground = Brushes.White };
        _saveBtn.SetResourceReference(BackgroundProperty, "Accent");
        _saveBtn.Click += (_, _) => { if (!_dirty || SaveText()) ExitEdit(); };
        _tools.Children.Add(_editBtn); _tools.Children.Add(_doneBtn); _tools.Children.Add(_saveBtn);
        UpdateEditUi();
        return frame;
    }

    private static string WhyNotEditable(TextFile f, FileItem item)
    {
        if (f.Truncated) return "Es demasiado grande para editarlo aquí (más de 1 MB). Ábrelo con un editor.";
        if (item.Special == ItemSpecial.TrashEntry) return "Está en la Papelera: sácalo de ahí para editarlo.";
        if (Protection.IsProtected(f.Path)) return "Es un archivo del sistema: Explor no lo modifica.";
        try { if ((File.GetAttributes(f.Path) & FileAttributes.ReadOnly) != 0) return "El archivo es de solo lectura (Obtener información para cambiarlo)."; }
        catch { return "No se puede acceder al archivo."; }
        return null;
    }

    private ContextMenu TextMenu(TextBox tb)
    {
        var m = new ContextMenu();
        MenuItem Cmd(string header, ICommand c, string keys) => new MenuItem { Header = header, Command = c, CommandTarget = tb, InputGestureText = keys };
        m.Items.Add(Cmd("Deshacer", ApplicationCommands.Undo, "Ctrl+Z"));
        m.Items.Add(new Separator());
        m.Items.Add(Cmd("Cortar", ApplicationCommands.Cut, "Ctrl+X"));
        m.Items.Add(Cmd("Copiar", ApplicationCommands.Copy, "Ctrl+C"));
        m.Items.Add(Cmd("Pegar", ApplicationCommands.Paste, "Ctrl+V"));
        m.Items.Add(new Separator());
        m.Items.Add(Cmd("Seleccionar todo", ApplicationCommands.SelectAll, "Ctrl+A"));
        var find = new MenuItem { Header = "Buscar…", InputGestureText = "Ctrl+F" };
        find.Click += (_, _) => ShowFind();
        m.Items.Add(find);
        // en solo lectura, Deshacer/Cortar/Pegar no aplican: se ocultan en vez de verse grises
        m.Opened += (_, _) =>
        {
            foreach (var o in m.Items)
                if (o is MenuItem mi && (mi.Command == ApplicationCommands.Undo || mi.Command == ApplicationCommands.Cut || mi.Command == ApplicationCommands.Paste))
                    mi.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
            ((Separator)m.Items[1]).Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        };
        return m;
    }

    // ---------- edición ----------

    private void BeginEdit()
    {
        if (_text == null || !_editBtn.IsEnabled) return;
        ShowMarkdown(false);
        _editing = true; _dirty = false;
        _text.IsReadOnly = false; _text.AcceptsTab = true; _text.IsReadOnlyCaretVisible = true;
        _textFrame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        UpdateEditUi();
        Activate();
        _text.Focus();
    }

    /// <summary>«Cancelar» o Esc: deja el archivo como estaba (si hay cambios, pide confirmarlo).</summary>
    private void CancelEdit()
    {
        if (!_editing) return;
        if (_dirty && !MacDialog.Confirm(this, "¿Descartar los cambios?", "El archivo se queda como estaba antes de editarlo.", "Descartar", true)) return;
        if (_dirty) RestoreText();
        ExitEdit();
    }

    private void ExitEdit()
    {
        _editing = false; _dirty = false;
        _text.IsReadOnly = true; _text.AcceptsTab = false; _text.IsReadOnlyCaretVisible = false;
        _textFrame.BorderBrush = Brushes.Transparent;
        UpdateEditUi();
        // tras editar un Markdown se vuelve a ver con formato (ya con los cambios)
        if (_mdView != null) { RenderMarkdown(); ShowMarkdown(true); }
    }

    private void RenderMarkdown()
    {
        if (_mdView == null) return;
        try { _mdView.Document = Views.MarkdownRenderer.Render(_text.Text, Path.GetDirectoryName(_textFile.Path)); }
        catch (Exception ex) { App.Log("markdown: " + ex.Message); _mdView.Document = null; }
    }

    private void ShowMarkdown(bool formatted)
    {
        if (_mdView == null) return;
        if (_mdView.Document == null) formatted = false;
        _mdView.Visibility = formatted ? Visibility.Visible : Visibility.Collapsed;
        _text.Visibility = formatted ? Visibility.Hidden : Visibility.Visible;
        if (_wrapBtn != null) _wrapBtn.Visibility = formatted ? Visibility.Collapsed : Visibility.Visible;
        if (_mdBtn != null)
        {
            SetBtnText(_mdBtn, formatted ? "Ver texto" : "Ver con formato");
            _mdBtn.ToolTip = formatted ? "Ver el Markdown tal cual está escrito" : "Ver el documento con títulos, listas y tablas";
            _mdBtn.Visibility = _editing ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void RestoreText()
    {
        bool editing = _editing; _editing = false;
        _text.IsUndoEnabled = false; _text.Text = _textFile.Text; _text.IsUndoEnabled = true;
        _editing = editing; _dirty = false;
    }

    private void UpdateEditUi()
    {
        if (_editBtn == null) return;
        _editBtn.Visibility = _editing ? Visibility.Collapsed : Visibility.Visible;
        _doneBtn.Visibility = _saveBtn.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        SetTitle(_dirty ? "• " + _textTitle + "  (sin guardar)" : _editing ? _textTitle + "  (editando)" : _textTitle);
    }

    private string AskSave() =>
        new MacDialog(this, $"¿Guardar los cambios en «{_textItem?.DisplayName}»?", "Si no los guardas, se pierden.",
            new[] { new DialogButton("Guardar", "save", true), new DialogButton("No guardar", "discard"), new DialogButton("Cancelar", "cancel", false, true) }).ShowFor() ?? "cancel";

    /// <summary>
    /// Antes de cambiar de archivo, abrirlo o cerrar la vista rápida: si hay texto sin guardar,
    /// pregunta. Devuelve false si el usuario prefiere quedarse.
    /// </summary>
    public bool ConfirmLeave()
    {
        if (!_editing || !_dirty) return true;
        var r = AskSave();
        if (r == "save") return SaveText();
        if (r == "discard") { RestoreText(); return true; }
        return false;
    }

    private bool SaveText()
    {
        if (!_editing || _text == null || _textFile == null) return false;
        var f = _textFile;
        var name = _textItem?.DisplayName ?? Path.GetFileName(f.Path);
        try
        {
            if (File.Exists(f.Path) && File.GetLastWriteTimeUtc(f.Path) != f.WriteTimeUtc &&
                !MacDialog.Confirm(this, $"«{name}» cambió mientras lo editabas.",
                    "Otro programa lo modificó después de abrirlo aquí. Si guardas, sus cambios se reemplazan por los tuyos.", "Guardar igualmente"))
                return false;
            var shown = _text.Text;
            var text = f.NewLine == "\n" ? shown.Replace("\r\n", "\n") : shown;
            var enc = f.Encoding; var pre = f.Preamble;
            // Latin-1 no puede guardar letras fuera de su tabla (emojis, comillas tipográficas…): pasar a UTF-8
            if (enc.CodePage == 28591 && text.Any(c => c > 0xFF)) { enc = new UTF8Encoding(false); pre = Array.Empty<byte>(); }
            var body = enc.GetBytes(text);
            var bytes = new byte[pre.Length + body.Length];
            pre.CopyTo(bytes, 0); body.CopyTo(bytes, pre.Length);
            WriteReplacing(f.Path, bytes);
            _textFile = f with { Text = shown, Encoding = enc, Preamble = pre, WriteTimeUtc = File.GetLastWriteTimeUtc(f.Path) };
            _dirty = false;
            UpdateEditUi();
            Notice("Guardado");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            MacDialog.Error(this, $"No se pudo guardar «{name}».", "No tienes permiso para modificar este archivo. Puedes copiar el texto (Copiar todo) y guardarlo en otra carpeta.");
        }
        catch (Exception ex)
        {
            MacDialog.Error(this, $"No se pudo guardar «{name}».", "Puede que otro programa lo tenga abierto o que el disco ya no esté conectado.\n\n" + ex.Message);
        }
        return false;
    }

    /// <summary>
    /// Escribe en un archivo temporal de la misma carpeta y lo cambia por el original de una vez:
    /// si algo falla a mitad, el archivo original queda intacto.
    /// </summary>
    private static void WriteReplacing(string path, byte[] bytes)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".archivos-guardando");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            try { File.SetAttributes(tmp, FileAttributes.Hidden); } catch { }
            File.Replace(tmp, path, null, true);
            return;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
        // discos que no admiten el reemplazo (algunos USB o carpetas de red): escritura directa
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Órdenes de prueba «@qltext …» (ver MainWindow.Test).</summary>
    public void TestText(string cmd)
    {
        var parts = cmd.Split(' ', 2);
        var arg = parts.Length > 1 ? parts[1] : "";
        switch (parts[0])
        {
            case "edit": BeginEdit(); break;
            case "append": if (_editing) { _text.CaretIndex = _text.Text.Length; _text.SelectedText = arg; } break;
            case "save": SaveText(); break;
            case "cancel": CancelEdit(); break;
            case "find": ShowFind(); _find.Text = arg; break;
            case "next": FindNext(true); break;
            case "select": _text?.Focus(); _text?.SelectAll(); break;
            case "click": // pulsa un botón de la barra por su texto: «click Girar»
                foreach (var b in _tools.Children.OfType<ButtonBase>())
                    if ((b.Content is TextBlock t && t.Text == arg) || (b.ToolTip as string)?.StartsWith(arg) == true) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break; }
                break;
        }
    }

    // ---------- búsqueda ----------

    private Grid FindBar()
    {
        // [🔍 campo ........ 2 de 5] [▲] [▼]           [Listo]  — con una línea debajo que la separa del texto
        var g = new Grid { Visibility = Visibility.Collapsed };
        var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 7, 8, 7) };
        line.SetResourceReference(Border.BorderBrushProperty, "Line.Popup");
        g.Children.Add(line);
        var row = new DockPanel { LastChildFill = false };
        line.Child = row;

        var field = new Border { CornerRadius = new CornerRadius(6), Width = 300, Height = 26, BorderThickness = new Thickness(1) };
        field.SetResourceReference(Border.BackgroundProperty, "Bg.Field");
        var fieldGrid = new Grid();
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glass = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 11, Margin = new Thickness(8, 0, 2, 0), Opacity = 0.6 };
        _find = new TextBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(2, 0, 4, 0),
            ToolTip = "Intro: siguiente · Mayús+Intro: anterior · Esc: cerrar" };
        _find.SetResourceReference(ForegroundProperty, "Fg.Primary");
        _find.SetResourceReference(TextBoxBase.CaretBrushProperty, "Fg.Primary");
        _find.SetResourceReference(TextBoxBase.SelectionBrushProperty, "Accent");
        _find.GotKeyboardFocus += (_, _) => field.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _find.LostKeyboardFocus += (_, _) => field.BorderBrush = Brushes.Transparent;
        _findCount = new TextBlock { Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
        _findCount.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        Grid.SetColumn(_find, 1); Grid.SetColumn(_findCount, 2);
        fieldGrid.Children.Add(glass); fieldGrid.Children.Add(_find); fieldGrid.Children.Add(_findCount);
        field.Child = fieldGrid;

        var prev = ToolBtn("", "Anterior (Mayús+Intro)", () => FindNext(false));
        var next = ToolBtn("", "Siguiente (Intro)", () => FindNext(true));
        prev.Margin = new Thickness(6, 0, 0, 0);
        var close = ToolBtn("", "Cerrar la búsqueda (Esc)", HideFind);
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(field); row.Children.Add(prev); row.Children.Add(next); row.Children.Add(close);
        _find.TextChanged += (_, _) => FindFrom(_text.SelectionStart, true);
        _find.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { FindNext((Keyboard.Modifiers & ModifierKeys.Shift) == 0); e.Handled = true; }
        };
        return g;
    }

    private void ShowFind()
    {
        if (_findBar == null) return;
        ShowMarkdown(false);
        _findBar.Visibility = Visibility.Visible;
        Activate();
        if (_text.SelectionLength is > 0 and < 200 && !_text.SelectedText.Contains('\n')) _find.Text = _text.SelectedText;
        _find.Focus(); _find.SelectAll();
    }

    private void HideFind()
    {
        if (_findBar == null) return;
        _findBar.Visibility = Visibility.Collapsed;
        PlaceMark();
        _text.Focus();
    }

    private void FindNext(bool forward)
    {
        int from = forward ? _text.SelectionStart + _text.SelectionLength : _text.SelectionStart - 1;
        FindFrom(from, forward);
    }

    private void FindFrom(int from, bool forward)
    {
        var q = _find.Text;
        var t = _text.Text;
        _findCount.ClearValue(TextBlock.ForegroundProperty);
        _findCount.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        _markAt = -1; PlaceMark();
        if (string.IsNullOrEmpty(q)) { _findCount.Text = ""; return; }
        const StringComparison cmp = StringComparison.CurrentCultureIgnoreCase;
        int i;
        if (forward) { i = from <= t.Length ? t.IndexOf(q, Math.Max(0, from), cmp) : -1; if (i < 0) i = t.IndexOf(q, cmp); }
        else { i = from >= 0 ? t.LastIndexOf(q, Math.Min(from, t.Length - 1), cmp) : -1; if (i < 0) i = t.LastIndexOf(q, cmp); }
        if (i < 0)
        {
            _findCount.Text = "Sin resultados";
            _findCount.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
            return;
        }
        int total = 0, ordinal = 0;
        for (int p = t.IndexOf(q, cmp); p >= 0 && total < 9999; p = t.IndexOf(q, p + Math.Max(1, q.Length), cmp))
        {
            total++;
            if (p == i) ordinal = total;
        }
        _findCount.Text = $"{ordinal} de {total}";
        _text.Select(i, q.Length);
        _text.ScrollToLine(Math.Max(0, _text.GetLineIndexFromCharacterIndex(i) - 3));
        var r = _text.GetRectFromCharacterIndex(i);
        if (!r.IsEmpty && (r.Left < 0 || r.Left > _text.ViewportWidth - 40))
            _text.ScrollToHorizontalOffset(Math.Max(0, _text.HorizontalOffset + r.Left - 60));
        _markAt = i; _markLen = q.Length;
        Dispatcher.BeginInvoke(PlaceMark, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
