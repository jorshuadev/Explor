using System.Windows;
using System.Windows.Input;
using Faro.Model;
using Faro.Services;

namespace Faro;

/// <summary>
/// Órdenes internas para pruebas automáticas (llegan por la tubería con prefijo "@"):
/// view N · select nombre · ql · info · go ruta · search texto · key atajo · menu.
/// </summary>
public partial class MainWindow
{
    private void SizeTest()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out var r0);
        var log = new System.Text.StringBuilder();
        foreach (var cloak in new[] { false, true })
        {
            if (cloak) Faro.Shell.WindowZoom.Cloak(hwnd, true);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 1920, 1080, 0x4 | 0x200);
            long big = sw.ElapsedMilliseconds; sw.Restart();
            SetWindowPos(hwnd, IntPtr.Zero, r0.L, r0.T, r0.R - r0.L, r0.B - r0.T, 0x4 | 0x200);
            long small = sw.ElapsedMilliseconds;
            if (cloak) Faro.Shell.WindowZoom.Cloak(hwnd, false);
            log.Append($"cloak={cloak} grande={big} pequeño={small}; ");
        }
        File.AppendAllText(Path.Combine(Core.AppInfo.LocalDir, "logs", "transicion.log"), log + "\n");
    }

    /// <summary>
    /// Recorre cada entrada de la barra lateral en las cuatro vistas, como si se hiciera clic, y anota en
    /// logs\barralateral.log cuánto tarda en listar, cuánto hasta quedar la interfaz libre, cuántos
    /// elementos hay, si hubo error y si la barra lateral marca la entrada correcta.
    /// </summary>
    private async Task SideTest()
    {
        var sb = new System.Text.StringBuilder($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ActualWidth:0}x{ActualHeight:0}{Environment.NewLine}");
        sb.AppendLine("vista\tentrada\tlistar_ms\tlibre_ms\telementos\tvista_real\tmarcada\terror");
        foreach (var mode in new[] { ViewMode.Icons, ViewMode.List, ViewMode.Columns, ViewMode.Gallery })
        {
            foreach (var e in _sideEntries.ToList())
            {
                ApplyViewMode(mode, false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await _tab.NavigateAsync(e.Location);
                long load = sw.ElapsedMilliseconds;
                // hasta que WPF termina de medir, colocar y pintar
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                long idle = sw.ElapsedMilliseconds;
                sb.AppendLine($"{mode}\t{e.Location.Title}\t{load}\t{idle}\t{_tab.Items.Count}\t{_tab.Mode}\t{(IsCurrent(e.Location) ? "sí" : "NO")}\t{_tab.Error}");
                await Task.Delay(150);
            }
        }
        File.AppendAllText(Path.Combine(Core.AppInfo.LocalDir, "logs", "barralateral.log"), sb.ToString());
    }

    /// <summary>
    /// Igual que SideTest pero con las rutas de un archivo (una por línea). Comprueba además que lo que se ve
    /// es de verdad el contenido de esa carpeta (que no se haya colado el listado de otra).
    /// </summary>
    private async Task PathTest(string listFile)
    {
        var paths = File.ReadAllLines(listFile).Where(l => l.Trim().Length > 0).ToList();
        var sb = new System.Text.StringBuilder($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
        foreach (var mode in new[] { ViewMode.Icons, ViewMode.List, ViewMode.Columns, ViewMode.Gallery })
        {
            foreach (var p in paths)
            {
                ApplyViewMode(mode, false);
                var loc = Location.Folder(p);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await _tab.NavigateAsync(loc);
                long load = sw.ElapsedMilliseconds;
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                long idle = sw.ElapsedMilliseconds;
                int ajenos = _tab.Items.Count(i => i.Depth == 0 && !string.Equals(Path.GetDirectoryName(i.Path), loc.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                                                   && !string.Equals(Path.GetDirectoryName(i.Path), loc.Path, StringComparison.OrdinalIgnoreCase));
                sb.AppendLine($"{mode}\t{p}\t{load}\t{idle}\t{_tab.Items.Count}\tajenos={ajenos}\t{_tab.Error}");
                await Task.Delay(100);
            }
        }
        File.AppendAllText(Path.Combine(Core.AppInfo.LocalDir, "logs", "rutas.log"), sb.ToString());
    }

    /// <summary>
    /// Clics rápidos: navega a todas las rutas seguidas SIN esperar a que cargue cada una (como quien hace
    /// clic en la barra lateral una y otra vez). Al final debe verse la última y sólo su contenido.
    /// </summary>
    private async Task RapidTest(string listFile)
    {
        var paths = File.ReadAllLines(listFile).Where(l => l.Trim().Length > 0).ToList();
        var sb = new System.Text.StringBuilder();
        var rnd = new Random(7);
        for (int round = 0; round < 5; round++)
        {
            Location last = null;
            for (int i = 0; i < 25; i++)
            {
                last = Location.Folder(paths[rnd.Next(paths.Count)]);
                _ = _tab.NavigateAsync(last);
                await Task.Delay(rnd.Next(0, 60)); // a veces ni da tiempo a empezar a listar
            }
            // esperar a que todo se asiente
            for (int k = 0; k < 100 && _tab.IsLoading; k++) await Task.Delay(50);
            await Task.Delay(400);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var expected = Providers.LoadFolder(last.Path, Settings.Current.ShowHidden, CancellationToken.None).Count;
            int ajenos = _tab.Items.Count(i => i.Depth == 0 && !string.Equals(Path.GetDirectoryName(i.Path)?.TrimEnd('\\'), last.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            bool ok = _tab.Location.Equals(last) && ajenos == 0 && _tab.Items.Count(i => i.Depth == 0) == expected && !_tab.IsLoading;
            sb.AppendLine($"ronda {round}: {(ok ? "OK" : "FALLO")}  esperada={last.Path} vista={_tab.Location.Path} elementos={_tab.Items.Count}/{expected} ajenos={ajenos} cargando={_tab.IsLoading} marcada={_sideEntries.Count(e => IsCurrent(e.Location))}");
        }
        File.AppendAllText(Path.Combine(Core.AppInfo.LocalDir, "logs", "rapido.log"), $"=== {DateTime.Now:HH:mm:ss}{Environment.NewLine}{sb}");
    }

    public async void RunTestCommand(string cmd)
    {
        var parts = cmd.Split(' ', 2);
        var arg = parts.Length > 1 ? parts[1] : "";
        try
        {
            switch (parts[0])
            {
                case "view": ApplyViewMode((ViewMode)int.Parse(arg), false); break;
                case "select":
                    var it = _tab.Items.FirstOrDefault(i => i.Name.Equals(arg, StringComparison.OrdinalIgnoreCase));
                    if (it != null) _view.SelectItems(new[] { it });
                    break;
                case "ql": ToggleQuickLook(); break;
                case "qltext": QuickLook?.TestText(arg); break;
                case "qlkey": QuickLook?.HandleMediaKey(Enum.Parse<System.Windows.Input.Key>(arg), System.Windows.Input.ModifierKeys.None); break;
                case "info": GetInfo(); break;
                case "repintar": Services.RenderRecovery.Repair("prueba"); break;
                case "go": await _tab.NavigateAsync(arg == "recents" ? Location.Recents : arg == "computer" ? Location.Computer : arg == "trash" ? Location.Trash : arg == "apps" ? Location.Apps : arg == "network" ? Location.Network : Location.Folder(arg)); break;
                case "search": SearchText.Text = arg; break;
                case "expand": var e = _tab.Items.FirstOrDefault(i => i.Name == arg); if (e != null) ExpandToggle(e, true); break;
                case "menu": OpenContextMenu(ViewHost, Selection); break;
                case "theme": Settings.Current.Theme = arg == "light" ? ThemeMode.Light : arg == "dark" ? ThemeMode.Dark : ThemeMode.System; Theme.Apply(); break;
                case "tab": NewTab_Click(null, null); break;
                case "activate": Activate(); break;
                case "pathedit": BeginPathEdit(); break;
                case "pathtype": BeginPathEdit(); PathEdit.Text = arg; PathEdit.CaretIndex = arg.Length; break;
                case "connect": ShowConnectToServer(); break;
                case "pathsug":
                    File.AppendAllText(Path.Combine(Core.AppInfo.LocalDir, "logs", "estado.log"),
                        $"{DateTime.Now:HH:mm:ss} sugerencias abiertas={_suggestPopup?.IsOpen} texto=«{PathEdit.Text}» [{string.Join(" | ", _suggestList?.Items.OfType<System.Windows.Controls.ListBoxItem>().Select(i => ((PathSuggestion)i.Tag).Path) ?? Array.Empty<string>())}]{Environment.NewLine}");
                    break;
                case "mdshot":
                {
                    // «mdshot C:\x\nota.md» → nota.md.png con lo que pinta la vista rápida (sin abrir ventanas)
                    var md = new System.Windows.Controls.FlowDocumentScrollViewer
                    {
                        Document = Views.MarkdownRenderer.Render(File.ReadAllText(arg), Path.GetDirectoryName(arg)),
                        Width = 900, Height = 1300, IsToolBarVisible = false, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Hidden
                    };
                    var host = new System.Windows.Controls.Border { Child = md, Width = 900, Height = 1300 };
                    host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Bg.Window");
                    host.Resources.MergedDictionaries.Add(Application.Current.Resources);
                    host.Measure(new Size(900, 1300)); host.Arrange(new Rect(0, 0, 900, 1300)); host.UpdateLayout();
                    await Task.Delay(300);
                    host.UpdateLayout();
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(900, 1300, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bmp.Render(host);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using (var fs = File.Create(arg + ".png")) enc.Save(fs);
                    break;
                }
                case "winshot":
                {
                    // «winshot C:\x\captura.png» → la ventana tal como se ve, a doble resolución (no depende de la pantalla)
                    var el = (FrameworkElement)Content;
                    int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
                    var dv = new System.Windows.Media.DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle((System.Windows.Media.Brush)FindResource("Bg.Window"), null, new Rect(0, 0, w, h));
                        dc.DrawRectangle(new System.Windows.Media.VisualBrush(el), null, new Rect(0, 0, w, h));
                    }
                    var shot = new System.Windows.Media.Imaging.RenderTargetBitmap(w * 2, h * 2, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
                    shot.Render(dv);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
                    using (var fs = File.Create(arg)) png.Save(fs);
                    break;
                }
                case "full": ToggleFullScreen(); break;
                case "fullon": if (!_fullScreen) ToggleFullScreen(); break;
                case "fulloff": if (_fullScreen) ToggleFullScreen(); break;
                case "rename": BeginRename(); break;
                case "newfolder": NewFolder(); break;
                case "focusinfo": // ¿quién tiene el foco y qué se está renombrando? → estado.log
                {
                    var kf = Keyboard.FocusedElement;
                    var ed = string.Join(",", _tab.Items.Where(i => i.IsEditing).Select(i => i.Name));
                    var txt = kf is System.Windows.Controls.TextBox tb ? $" texto=«{tb.Text}»" : "";
                    File.AppendAllText(Path.Combine(Core.AppInfo.LocalDir, "logs", "estado.log"), $"{DateTime.Now:HH:mm:ss} focusinfo {arg} foco={kf?.GetType().Name}{txt} renombrando=[{ed}] activa={IsActive}{Environment.NewLine}");
                    break;
                }
                case "renameto": if (Keyboard.FocusedElement is System.Windows.Controls.TextBox rb) { rb.Text = arg; rb.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(rb), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); } break;
                case "endrename": foreach (var i in _tab.Items) i.IsEditing = false; break;
                case "sizetest": SizeTest(); break;
                case "sidetest": await SideTest(); break;
                case "pathtest": await PathTest(arg); break;
                case "trash": MoveSelectionToTrash(); break;
                case "count":
                    File.AppendAllText(Path.Combine(Core.AppInfo.LocalDir, "logs", "estado.log"),
                        $"{DateTime.Now:HH:mm:ss} {arg} elementos={_tab.Items.Count} invisibles={_tab.Items.Count(i => i.IsRemoving)} con «{arg}»={_tab.Items.Count(i => i.Name.Contains(arg, StringComparison.OrdinalIgnoreCase))}{Environment.NewLine}");
                    break;
                case "icontest":
                {
                    // tamaño real del icono de cada app a 256 px: <256 = era un icono pequeño agrandado a bloques
                    var apps = await Task.Run(Providers.LoadApps);
                    var lines = new List<string>();
                    await Task.Run(() =>
                    {
                        var th = new Thread(() =>
                        {
                            foreach (var a in apps.OrderBy(a => a.DisplayName))
                            {
                                var img = Faro.Shell.ShellInterop.GetImage(a.LaunchPath ?? a.Path, 256, Faro.Shell.ShellInterop.SIIGBF.IconOnly | Faro.Shell.ShellInterop.SIIGBF.BiggerSizeOk);
                                lines.Add($"{img?.PixelWidth ?? 0}\t{a.DisplayName}");
                                if (img != null && arg.Length > 0 && arg.Split(',').Any(k => a.DisplayName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                                {
                                    var dir = Path.Combine(Core.AppInfo.LocalDir, "logs", "iconos");
                                    Directory.CreateDirectory(dir);
                                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                                    using var fs = File.Create(Path.Combine(dir, string.Concat(a.DisplayName.Split(Path.GetInvalidFileNameChars())) + ".png"));
                                    enc.Save(fs);
                                }
                            }
                        });
                        th.SetApartmentState(ApartmentState.STA); th.Start(); th.Join();
                    });
                    File.WriteAllLines(Path.Combine(Core.AppInfo.LocalDir, "logs", "iconos.log"), lines);
                    break;
                }
                case "uninstalltest":
                {
                    // sólo BUSCA el desinstalador de cada app; no ejecuta nada
                    var apps = await Task.Run(Providers.LoadApps);
                    var lines = await Task.Run(() => apps.OrderBy(a => a.DisplayName).Select(a =>
                    {
                        var en = AppUninstaller.Find(a);
                        return $"{a.DisplayName}\t{AppUninstaller.TargetOf(a)}\t{(en == null ? "-" : en.DisplayName)}\t{en?.Command}";
                    }).ToList());
                    File.WriteAllLines(Path.Combine(Core.AppInfo.LocalDir, "logs", "desinstaladores.log"), lines);
                    break;
                }
                case "rapidtest": await RapidTest(arg); break;
                case "close": Close(); break;
                case "activatetab": ActivateTab(Tabs[int.Parse(arg)]); break;
                case "state":
                {
                    var sb = new System.Text.StringBuilder();
                    string side = SidebarVisible ? SidebarCol.ActualWidth.ToString("0") : "oculta";
                    sb.Append($"ventana={WindowState} {RestoreBounds.Width:0}x{RestoreBounds.Height:0} completa={_fullScreen} lateral={side} activa={Tabs.IndexOf(_tab)}");
                    foreach (var t in Tabs)
                    {
                        string err = t.HasError ? ":ERROR " + t.Error : "";
                        sb.Append($" | {t.Location?.Kind}:{t.Location?.Path ?? t.Location?.Tag}:{t.Mode}:{t.Items.Count}{err}");
                    }
                    File.AppendAllText(Path.Combine(Core.AppInfo.LocalDir, "logs", "estado.log"), $"{DateTime.Now:HH:mm:ss} {arg} {sb}{Environment.NewLine}");
                    break;
                }
                case "icondbg": { var ic = _tab.Items.FirstOrDefault(i => i.Name.StartsWith(arg)); var b = await Task.Run(() => { System.Windows.Media.Imaging.BitmapSource r = null; var th = new Thread(() => r = Faro.Shell.ShellInterop.GetImage(ic.LaunchPath ?? ic.Path, 96, Faro.Shell.ShellInterop.SIIGBF.IconOnly | Faro.Shell.ShellInterop.SIIGBF.BiggerSizeOk)); th.SetApartmentState(ApartmentState.STA); th.Start(); th.Join(); return r; }); int w = b.PixelWidth; var px = new byte[w * b.PixelHeight * 4]; b.CopyPixels(px, w * 4, 0); var sb = new System.Text.StringBuilder($"{b.Format} {w}x{b.PixelHeight}\n"); for (int y = 0; y < b.PixelHeight; y += 6) { for (int x = 0; x < w; x += 3) { int a = px[(y * w + x) * 4 + 3]; int l = px[(y * w + x) * 4 + 1]; sb.Append(a == 0 ? '.' : a < 160 ? '+' : l > 200 ? '#' : 'o'); } sb.Append('\n'); } File.WriteAllText(Path.Combine(Core.AppInfo.LocalDir, "logs", "icon.txt"), sb.ToString()); break; }
                case "dbg": File.AppendAllText(Path.Combine(Core.AppInfo.LocalDir, "logs", "scroll.log"), arg + ": " + _iconView.DebugScroll() + Environment.NewLine); break;
                case "scrolltest": foreach (var f in new[] { 1.0, 0.0, 1.0, 0.3, 1.0, 0.0, 0.8, 0.0 }) { _iconView.ScrollToFraction(f); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background); await Task.Delay(120); } break;
                case "type": // escribir para seleccionar, por la misma ruta que el teclado (TextInput en el elemento con foco)
                    if (Keyboard.FocusedElement is IInputElement fe) TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, fe, arg));
                    break;
                case "scroll":_iconView.ScrollToFraction(double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture)); break;
                case "max": WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
                case "copy": { var p = arg.Split('|'); TransferManager.Instance.Start(Faro.Core.Transfer.TransferKind.Copy, new[] { p[0] }, p[1]); break; }
            }
        }
        catch (Exception ex) { App.Log("test: " + ex); }
    }
}
