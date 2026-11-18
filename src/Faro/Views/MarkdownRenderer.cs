using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Faro.Views;

/// <summary>
/// Markdown → FlowDocument para la vista rápida: títulos, párrafos, listas (anidadas y de tareas),
/// citas, bloques de código, tablas, líneas, enlaces, imágenes, negrita/cursiva/tachado/código.
/// Sin dependencias: lo justo para leer bien un README o unas notas, no un motor completo.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    public static FlowDocument Render(string text, string baseDir, double scale = 1)
    {
        var doc = new FlowDocument
        {
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = 14 * scale, LineHeight = 22 * scale, PagePadding = new Thickness(26, 14, 26, 24),
            TextAlignment = TextAlignment.Left, IsOptimalParagraphEnabled = true, IsHyphenationEnabled = false
        };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "Fg.Primary");
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        // front matter de YAML (--- … --- al principio): no es contenido
        if (lines.Count > 2 && lines[0].Trim() == "---")
        {
            int end = lines.FindIndex(1, l => l.Trim() is "---" or "...");
            if (end > 0) lines.RemoveRange(0, end + 1);
        }
        var ctx = new Ctx(baseDir, scale);
        foreach (var b in ParseBlocks(lines, ctx)) doc.Blocks.Add(b);
        return doc;
    }

    private sealed record Ctx(string BaseDir, double Scale);

    // ======================== bloques ========================

    private static readonly Regex Heading = new(@"^ {0,3}(#{1,6})\s+(.*?)\s*#*\s*$");
    private static readonly Regex Rule = new(@"^ {0,3}([-*_])(\s*\1){2,}\s*$");
    private static readonly Regex Fence = new(@"^ {0,3}(`{3,}|~{3,})\s*([\w+#.-]*)");
    private static readonly Regex Bullet = new(@"^( *)([-*+])\s+(.*)$");
    private static readonly Regex Ordered = new(@"^( *)(\d{1,9})[.)]\s+(.*)$");
    private static readonly Regex TableSep = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");

    private static List<Block> ParseBlocks(List<string> lines, Ctx ctx)
    {
        var blocks = new List<Block>();
        var para = new List<string>();
        void FlushPara()
        {
            if (para.Count == 0) return;
            blocks.Add(ParagraphOf(para, ctx));
            para.Clear();
        }

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { FlushPara(); continue; }

            // código cercado
            var fm = Fence.Match(line);
            if (fm.Success)
            {
                FlushPara();
                var marker = fm.Groups[1].Value;
                var code = new List<string>();
                for (i++; i < lines.Count && !lines[i].TrimStart().StartsWith(marker[..3]); i++) code.Add(lines[i]);
                blocks.Add(CodeBlock(code, ctx));
                continue;
            }
            // código con sangría (4 espacios), sólo fuera de un párrafo
            if (para.Count == 0 && (line.StartsWith("    ") || line.StartsWith('\t')) && !Bullet.IsMatch(line) && !Ordered.IsMatch(line))
            {
                var code = new List<string>();
                for (; i < lines.Count && (lines[i].StartsWith("    ") || lines[i].StartsWith('\t') || string.IsNullOrWhiteSpace(lines[i])); i++)
                    code.Add(lines[i].StartsWith('\t') ? lines[i][1..] : lines[i].Length >= 4 ? lines[i][4..] : "");
                i--;
                while (code.Count > 0 && string.IsNullOrWhiteSpace(code[^1])) code.RemoveAt(code.Count - 1);
                blocks.Add(CodeBlock(code, ctx));
                continue;
            }
            var hm = Heading.Match(line);
            if (hm.Success) { FlushPara(); blocks.Add(HeadingOf(hm.Groups[1].Length, hm.Groups[2].Value, ctx)); continue; }
            // título subrayado (=== o ---) bajo una línea de texto
            if (para.Count == 1 && Regex.IsMatch(line, @"^ {0,3}(=+|-+)\s*$"))
            {
                var t = para[0]; para.Clear();
                blocks.Add(HeadingOf(line.Trim()[0] == '=' ? 1 : 2, t.Trim(), ctx));
                continue;
            }
            if (Rule.IsMatch(line)) { FlushPara(); blocks.Add(RuleBlock()); continue; }
            // cita
            if (line.TrimStart().StartsWith('>'))
            {
                FlushPara();
                var inner = new List<string>();
                for (; i < lines.Count && (lines[i].TrimStart().StartsWith('>') || (!string.IsNullOrWhiteSpace(lines[i]) && inner.Count > 0 && !string.IsNullOrWhiteSpace(inner[^1]))); i++)
                {
                    var l = lines[i].TrimStart();
                    inner.Add(l.StartsWith('>') ? (l.Length > 1 && l[1] == ' ' ? l[2..] : l[1..]) : l);
                }
                i--;
                blocks.Add(QuoteBlock(inner, ctx));
                continue;
            }
            // tabla: fila | fila de separación | filas
            if (line.Contains('|') && i + 1 < lines.Count && TableSep.IsMatch(lines[i + 1]))
            {
                FlushPara();
                var rows = new List<string> { line };
                var sep = lines[i + 1];
                for (i += 2; i < lines.Count && lines[i].Contains('|') && !string.IsNullOrWhiteSpace(lines[i]); i++) rows.Add(lines[i]);
                i--;
                blocks.Add(TableBlock(rows, sep, ctx));
                continue;
            }
            // lista
            if ((Bullet.IsMatch(line) || Ordered.IsMatch(line)) && (para.Count == 0 || true))
            {
                FlushPara();
                int end = i;
                var listLines = new List<string>();
                int baseIndent = line.Length - line.TrimStart().Length;
                var kind = ItemKind(line);
                for (; end < lines.Count; end++)
                {
                    var l = lines[end];
                    // otra clase de lista al mismo nivel (viñetas → numerada → tareas) es otra lista
                    if (end > i && IsItem(l, baseIndent) && ItemKind(l) != kind) break;
                    if (string.IsNullOrWhiteSpace(l))
                    {
                        // un hueco sigue la lista si lo siguiente es otro elemento o va sangrado
                        int n = end + 1;
                        while (n < lines.Count && string.IsNullOrWhiteSpace(lines[n])) n++;
                        if (n < lines.Count && (IsItem(lines[n], baseIndent) || Indent(lines[n]) > baseIndent)) { listLines.Add(""); continue; }
                        break;
                    }
                    if (Indent(l) < baseIndent || (Indent(l) == baseIndent && !IsItem(l, baseIndent) && listLines.Count > 0 && string.IsNullOrWhiteSpace(listLines[^1]))) break;
                    if (Indent(l) <= baseIndent + 1 && !IsItem(l, -1) && (Heading.IsMatch(l) || Fence.IsMatch(l) || Rule.IsMatch(l))) break;
                    listLines.Add(l);
                }
                i = end - 1;
                blocks.Add(ListBlock(listLines, ctx));
                continue;
            }
            // HTML suelto de bloque (<p align=…>, <div>, comentarios): se quita la etiqueta
            para.Add(line);
        }
        FlushPara();
        return blocks;
    }

    private static string ItemKind(string l)
    {
        var b = Bullet.Match(l);
        if (b.Success) return Regex.IsMatch(b.Groups[3].Value, @"^\[[ xX]\]\s") ? "tarea" : b.Groups[2].Value;
        return "1";
    }

    private static int Indent(string l) { int n = 0; foreach (var c in l) { if (c == ' ') n++; else if (c == '\t') n += 4; else break; } return n; }
    private static bool IsItem(string l, int indent) => (Bullet.Match(l) is { Success: true } b && (indent < 0 || b.Groups[1].Length == indent))
                                                       || (Ordered.Match(l) is { Success: true } o && (indent < 0 || o.Groups[1].Length == indent));

    private static Paragraph ParagraphOf(List<string> lines, Ctx ctx)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 10 * ctx.Scale) };
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            bool hard = l.EndsWith("  ") || l.EndsWith('\\') || Regex.IsMatch(l, @"<br\s*/?>\s*$", RegexOptions.IgnoreCase);
            l = Regex.Replace(l, @"<br\s*/?>\s*$", "", RegexOptions.IgnoreCase).TrimEnd('\\').Trim();
            AddInlines(p.Inlines, l, ctx);
            if (i < lines.Count - 1) p.Inlines.Add(hard ? new LineBreak() : new Run(" "));
        }
        return p;
    }

    private static Block HeadingOf(int level, string text, Ctx ctx)
    {
        double[] sizes = { 26, 21, 17.5, 15.5, 14, 13 };
        var p = new Paragraph
        {
            FontSize = sizes[level - 1] * ctx.Scale, FontWeight = level <= 2 ? FontWeights.Bold : FontWeights.SemiBold,
            Margin = new Thickness(0, (level <= 2 ? 18 : 14) * ctx.Scale, 0, 8 * ctx.Scale), LineHeight = sizes[level - 1] * 1.3 * ctx.Scale
        };
        if (level <= 2)
        {
            p.BorderThickness = new Thickness(0, 0, 0, 1);
            p.Padding = new Thickness(0, 0, 0, 5 * ctx.Scale);
            p.SetResourceReference(Block.BorderBrushProperty, "Line.Strong");
        }
        if (level == 6) p.SetResourceReference(TextElement.ForegroundProperty, "Fg.Secondary");
        AddInlines(p.Inlines, text, ctx);
        return p;
    }

    private static Block RuleBlock()
    {
        var b = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
        b.SetResourceReference(Border.BackgroundProperty, "Line.Strong");
        return new BlockUIContainer(b);
    }

    private static Block CodeBlock(List<string> code, Ctx ctx)
    {
        // un TextBox de solo lectura: se puede seleccionar y copiar, y desplazar si es ancho
        var tb = new TextBox
        {
            Text = string.Join("\n", code), IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontFamily = Mono, FontSize = 12.5 * ctx.Scale, TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0)
        };
        tb.SetResourceReference(Control.ForegroundProperty, "Fg.Primary");
        var border = new Border { Child = tb, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 9, 12, 9) };
        border.SetResourceReference(Border.BackgroundProperty, "Bg.Control");
        return new BlockUIContainer(border) { Margin = new Thickness(0, 2, 0, 12 * ctx.Scale) };
    }

    private static Block QuoteBlock(List<string> inner, Ctx ctx)
    {
        var s = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2), Margin = new Thickness(0, 0, 0, 12 * ctx.Scale) };
        s.SetResourceReference(Block.BorderBrushProperty, "Line.Strong");
        s.SetResourceReference(TextElement.ForegroundProperty, "Fg.Secondary");
        foreach (var b in ParseBlocks(inner, ctx)) s.Blocks.Add(b);
        if (s.Blocks.LastBlock != null) s.Blocks.LastBlock.Margin = new Thickness(0);
        return s;
    }

    private static Block ListBlock(List<string> lines, Ctx ctx)
    {
        var first = lines[0];
        bool ordered = !Bullet.IsMatch(first) && Ordered.IsMatch(first);
        int indent = Indent(first);
        var list = new List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 10 * ctx.Scale), Padding = new Thickness(24 * ctx.Scale, 0, 0, 0)
        };
        if (ordered && int.TryParse(Ordered.Match(first).Groups[2].Value, out var start) && start > 1) list.StartIndex = start;

        // repartir las líneas por elemento; lo sangrado pertenece al elemento anterior
        var items = new List<List<string>>();
        foreach (var l in lines)
        {
            if (IsItem(l, indent) || (items.Count == 0))
            {
                var m = Bullet.Match(l);
                var content = m.Success && m.Groups[1].Length == indent ? m.Groups[3].Value : Ordered.Match(l).Groups[3].Value;
                items.Add(new List<string> { content });
            }
            else
            {
                var ll = Regex.Replace(l, @"^[ \t]+", m => m.Value.Replace("\t", "    "));
                items[^1].Add(ll[Math.Min(Indent(ll), indent + (ordered ? 3 : 2))..]);
            }
        }
        bool allTasks = items.All(it => Regex.IsMatch(it[0], @"^\[[ xX]\]\s"));
        if (allTasks) list.MarkerStyle = TextMarkerStyle.None;
        foreach (var it in items)
        {
            var li = new ListItem();
            var task = Regex.Match(it[0], @"^\[([ xX])\]\s+(.*)$");
            if (task.Success)
            {
                it[0] = task.Groups[2].Value;
                bool done = task.Groups[1].Value != " ";
                var blocks = ParseBlocks(it, ctx);
                if (blocks.FirstOrDefault() is Paragraph p0)
                {
                    var box = new Run(done ? "☑ " : "☐ ") { FontFamily = new FontFamily("Segoe UI Symbol") };
                    if (done) box.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                    p0.Inlines.InsertBefore(p0.Inlines.FirstInline, box);
                    if (done) foreach (var inl in p0.Inlines.Skip(1)) inl.SetResourceReference(TextElement.ForegroundProperty, "Fg.Secondary");
                }
                foreach (var b in blocks) { b.Margin = new Thickness(0, 0, 0, 3 * ctx.Scale); li.Blocks.Add(b); }
            }
            else
                foreach (var b in ParseBlocks(it, ctx)) { b.Margin = new Thickness(0, 0, 0, 3 * ctx.Scale); li.Blocks.Add(b); }
            if (li.Blocks.Count == 0) li.Blocks.Add(new Paragraph());
            list.ListItems.Add(li);
        }
        return list;
    }

    private static string[] Cells(string row)
    {
        var r = row.Trim();
        if (r.StartsWith('|')) r = r[1..];
        if (r.EndsWith('|') && !r.EndsWith("\\|")) r = r[..^1];
        // «\|» es una barra dentro de la celda
        return Regex.Split(r, @"(?<!\\)\|").Select(c => c.Trim().Replace("\\|", "|")).ToArray();
    }

    private static Block TableBlock(List<string> rows, string sep, Ctx ctx)
    {
        var aligns = Cells(sep).Select(c => c.StartsWith(':') && c.EndsWith(':') ? TextAlignment.Center : c.EndsWith(':') ? TextAlignment.Right : TextAlignment.Left).ToArray();
        var head = Cells(rows[0]);
        int cols = Math.Max(head.Length, aligns.Length);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 12 * ctx.Scale), BorderThickness = new Thickness(1, 1, 0, 0) };
        table.SetResourceReference(Block.BorderBrushProperty, "Line.Strong");
        for (int c = 0; c < cols; c++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (int r = 0; r < rows.Count; r++)
        {
            var cells = r == 0 ? head : Cells(rows[r]);
            var row = new TableRow();
            if (r == 0) row.FontWeight = FontWeights.SemiBold;
            if (r == 0 || r % 2 == 0) row.SetResourceReference(TableRow.BackgroundProperty, r == 0 ? "Bg.Control" : "Bg.RowAlt");
            for (int c = 0; c < cols; c++)
            {
                var p = new Paragraph { TextAlignment = c < aligns.Length ? aligns[c] : TextAlignment.Left, Margin = new Thickness(0), LineHeight = 19 * ctx.Scale };
                AddInlines(p.Inlines, c < cells.Length ? cells[c] : "", ctx);
                var cell = new TableCell(p) { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 1, 1) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "Line.Strong");
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        return table;
    }

    // ======================== en línea ========================

    private static void AddInlines(InlineCollection target, string s, Ctx ctx)
    {
        foreach (var i in ParseInlines(s, ctx)) target.Add(i);
    }

    private static readonly Regex HtmlTag = new(@"</?(?:[a-zA-Z][\w-]*)(?:\s[^<>]*)?/?>|<!--.*?-->");

    private static List<Inline> ParseInlines(string s, Ctx ctx)
    {
        var res = new List<Inline>();
        var buf = new StringBuilder();
        void Flush() { if (buf.Length > 0) { res.Add(new Run(System.Net.WebUtility.HtmlDecode(buf.ToString()))); buf.Clear(); } }

        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            // escape
            if (c == '\\' && i + 1 < s.Length && "\\`*_{}[]()#+-.!|~<>".Contains(s[i + 1])) { buf.Append(s[i + 1]); i += 2; continue; }
            // código
            if (c == '`')
            {
                int ticks = 0; while (i + ticks < s.Length && s[i + ticks] == '`') ticks++;
                var fence = new string('`', ticks);
                int end = s.IndexOf(fence, i + ticks, StringComparison.Ordinal);
                if (end > 0)
                {
                    Flush();
                    var run = new Run(s[(i + ticks)..end].Trim()) { FontFamily = Mono, FontSize = 12.5 * ctx.Scale };
                    run.SetResourceReference(TextElement.BackgroundProperty, "Bg.Control");
                    res.Add(run);
                    i = end + ticks; continue;
                }
            }
            // imagen ![alt](src)
            if (c == '!' && i + 1 < s.Length && s[i + 1] == '[' && TryLink(s, i + 1, out var alt, out var src, out var after))
            {
                Flush();
                res.Add(ImageInline(alt, src, ctx));
                i = after; continue;
            }
            // enlace [texto](url)
            if (c == '[' && TryLink(s, i, out var label, out var url, out var after2))
            {
                Flush();
                var h = new Hyperlink();
                foreach (var x in ParseInlines(label, ctx)) h.Inlines.Add(x);
                Wire(h, url, ctx);
                res.Add(h);
                i = after2; continue;
            }
            // <https://…> o etiquetas HTML
            if (c == '<')
            {
                var am = Regex.Match(s[i..], @"^<((?:https?|mailto):[^>\s]+)>");
                if (am.Success) { Flush(); var h = new Hyperlink(new Run(am.Groups[1].Value)); Wire(h, am.Groups[1].Value, ctx); res.Add(h); i += am.Length; continue; }
                var tm = HtmlTag.Match(s, i);
                if (tm.Success && tm.Index == i)
                {
                    if (Regex.IsMatch(tm.Value, @"^<br\s*/?>$", RegexOptions.IgnoreCase)) { Flush(); res.Add(new LineBreak()); }
                    else if (Regex.Match(tm.Value, @"^<img\s[^>]*src=""([^""]+)""", RegexOptions.IgnoreCase) is { Success: true } im)
                    { Flush(); res.Add(ImageInline("", im.Groups[1].Value, ctx)); }
                    i += tm.Length; continue;
                }
            }
            // enlaces sueltos
            if ((c == 'h' || c == 'w') && (i == 0 || !char.IsLetterOrDigit(s[i - 1])))
            {
                var um = Regex.Match(s[i..], @"^(https?://|www\.)[^\s<>()]+[^\s<>().,;:!?""')\]]");
                if (um.Success) { Flush(); var h = new Hyperlink(new Run(um.Value)); Wire(h, um.Value.StartsWith("www.") ? "https://" + um.Value : um.Value, ctx); res.Add(h); i += um.Length; continue; }
            }
            // ~~tachado~~, **negrita**, __negrita__, *cursiva*, _cursiva_
            if (c is '*' or '_' or '~')
            {
                int run = 0; while (i + run < s.Length && s[i + run] == c) run++;
                if (c == '~' && run != 2) { buf.Append(c, run); i += run; continue; }
                // _ dentro de una palabra (nombre_de_archivo) no es cursiva
                if (c == '_' && i > 0 && char.IsLetterOrDigit(s[i - 1])) { buf.Append(c, run); i += run; continue; }
                int n = Math.Min(run, 3);
                var delim = new string(c, n);
                int close = FindClose(s, i + n, delim);
                if (close > i + n && !char.IsWhiteSpace(s[i + n]))
                {
                    Flush();
                    var inner = ParseInlines(s[(i + n)..close], ctx);
                    Span span = c == '~' ? new Span() : n == 1 ? new Italic() : n == 2 ? new Bold() : new Bold(new Italic());
                    var host = span is Bold { Inlines.FirstInline: Italic it } ? (Span)it : span;
                    foreach (var x in inner) host.Inlines.Add(x);
                    if (c == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    res.Add(span);
                    i = close + n; continue;
                }
                buf.Append(c, run); i += run; continue;
            }
            buf.Append(c); i++;
        }
        Flush();
        return res;
    }

    private static int FindClose(string s, int from, string delim)
    {
        for (int j = from; j <= s.Length - delim.Length; j++)
        {
            if (s[j] == '\\') { j++; continue; }
            if (s[j] == '`') { int e = s.IndexOf('`', j + 1); if (e > 0) { j = e; continue; } }
            if (string.CompareOrdinal(s, j, delim, 0, delim.Length) == 0 && !char.IsWhiteSpace(s[j - 1])
                && (j + delim.Length >= s.Length || s[j + delim.Length] != delim[0]))
            {
                if (delim[0] == '_' && j + delim.Length < s.Length && char.IsLetterOrDigit(s[j + delim.Length])) continue;
                return j;
            }
        }
        return -1;
    }

    /// <summary>[texto](destino "título") empezando en <paramref name="at"/> (el «[»).</summary>
    private static bool TryLink(string s, int at, out string label, out string url, out int after)
    {
        label = url = null; after = at;
        int depth = 0, j = at;
        for (; j < s.Length; j++)
        {
            if (s[j] == '\\') { j++; continue; }
            if (s[j] == '[') depth++;
            else if (s[j] == ']' && --depth == 0) break;
        }
        if (j >= s.Length - 1 || s[j + 1] != '(') return false;
        int close = j + 2, paren = 1;
        for (; close < s.Length; close++)
        {
            if (s[close] == '(') paren++;
            else if (s[close] == ')' && --paren == 0) break;
        }
        if (close >= s.Length) return false;
        label = s[(at + 1)..j];
        url = s[(j + 2)..close].Trim();
        var title = Regex.Match(url, @"^(\S+)\s+[""'(].*[""')]$");
        if (title.Success) url = title.Groups[1].Value;
        url = url.Trim('<', '>');
        after = close + 1;
        return true;
    }

    private static void Wire(Hyperlink h, string url, Ctx ctx)
    {
        h.SetResourceReference(TextElement.ForegroundProperty, "Fg.Accent");
        h.TextDecorations = null;
        h.Cursor = System.Windows.Input.Cursors.Hand;
        h.ToolTip = url;
        h.MouseEnter += (_, _) => h.TextDecorations = TextDecorations.Underline;
        h.MouseLeave += (_, _) => h.TextDecorations = null;
        h.Click += (_, _) =>
        {
            if (url.StartsWith('#')) return; // ancla dentro del documento
            var target = Resolve(url, ctx.BaseDir);
            if (target == null) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        };
    }

    private static string Resolve(string url, string baseDir)
    {
        if (Regex.IsMatch(url, @"^[a-zA-Z][\w+.-]*:")) return url; // http:, mailto:, file:…
        try
        {
            var p = Uri.UnescapeDataString(url.Split('#')[0]).Replace('/', '\\');
            if (p.Length == 0) return null;
            return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(baseDir ?? "", p));
        }
        catch { return null; }
    }

    private static Inline ImageInline(string alt, string src, Ctx ctx)
    {
        try
        {
            var target = Resolve(src, ctx.BaseDir);
            if (target == null) return new Run(alt);
            bool remote = target.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            if (!remote && !File.Exists(target)) return new Italic(new Run($"[imagen: {(alt.Length > 0 ? alt : Path.GetFileName(target))}]"));
            if (target.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return new Italic(new Run($"[{(alt.Length > 0 ? alt : "imagen SVG")}]"));
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(target);
            bmp.CacheOption = remote ? BitmapCacheOption.Default : BitmapCacheOption.OnLoad; // local: no deja el archivo abierto
            bmp.DecodePixelWidth = 1400;
            bmp.EndInit();
            var img = new Image { Source = bmp, Stretch = Stretch.Uniform, MaxWidth = 760 * ctx.Scale, MaxHeight = 600 * ctx.Scale, ToolTip = alt.Length > 0 ? alt : null, Margin = new Thickness(0, 4, 0, 4) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return new InlineUIContainer(img) { BaselineAlignment = BaselineAlignment.Bottom };
        }
        catch { return new Run(alt); }
    }
}
