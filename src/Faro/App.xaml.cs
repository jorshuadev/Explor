using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Faro.Core.Transfer;
using Faro.Dialogs;
using Faro.Model;
using Faro.Services;

namespace Faro;

/// <summary>
/// Arranque: una sola instancia (si ya está abierta, la segunda le pasa la ruta y se cierra
/// al instante — abrir otra ventana es inmediato), tema, y reanudar copias interrumpidas.
/// </summary>
public partial class App : Application
{
    private static readonly string PipeName = Core.AppInfo.Name + ".Abrir." + Environment.UserName;
    private Mutex _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        var target = e.Args.FirstOrDefault(a => !a.StartsWith("--"));
        _mutex = new Mutex(true, Core.AppInfo.Name + ".Instancia." + Environment.UserName, out bool first);
        if (!first)
        {
            if (SendToRunning(target ?? "")) { Shutdown(); return; }
        }

        Core.AppInfo.MigrateOldData();
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log(ex.ExceptionObject?.ToString());
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log(ex.Exception.ToString()); ex.SetObserved(); };

        Theme.Apply();
        // los menús aparecen con un pequeño fundido y desplazamiento, como en macOS
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent,
            new RoutedEventHandler((s, _) => Faro.MainWindow.FadeIn((UIElement)s, 120, -4, 0.98)));
        Theme.WatchSystem();
        RenderRecovery.Watch(); // tras suspender, los textos desaparecían (caché de letras de la GPU perdida)
        ListenForOtherInstances();

        TransferManager.Instance.Completed += _ => CheckExit();
        _ = Task.Run(Core.Search.EverythingClient.EnsureRunningAsync); // búsqueda instantánea lista desde el principio
        // si Windows se apaga con la app abierta, no llegan los Closing de las ventanas: guardar aquí
        SessionEnding += (_, _) => { foreach (var mw in Current.Windows.OfType<MainWindow>().Take(1)) { try { mw.SaveAll(); } catch { } } };
        MarkStartup("inicio");
        OpenWindow(target);
        Dispatcher.BeginInvoke(OfferResume, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Se cierra la app sólo cuando no quedan ventanas ni copias en curso.</summary>
    public static void CheckExit()
    {
        Current.Dispatcher.BeginInvoke(() =>
        {
            if (Current.Windows.OfType<MainWindow>().Any()) return;
            if (TransferManager.Instance.Jobs.Count > 0) { TransferManager.Instance.ShowWindow(); return; }
            Current.Shutdown();
        }, DispatcherPriority.Background);
    }

    public static void OpenWindow(string target)
    {
        Location loc = null;
        if (!string.IsNullOrWhiteSpace(target))
        {
            target = target.Trim('"');
            if (Directory.Exists(target)) loc = Location.Folder(target);
            else if (File.Exists(target)) loc = Location.Folder(Path.GetDirectoryName(target));
        }
        // abrir la app «a secas» (menú Inicio, escritorio) vuelve a donde estabas; con una ruta, abre esa ruta
        bool first = !Current.Windows.OfType<MainWindow>().Any();
        var w = new MainWindow(loc, restoreSession: loc == null && first);
        w.Show();
        w.Activate();
        if (target != null && File.Exists(target)) _ = w.RevealInFaro(target);
    }

    private static bool SendToRunning(string arg)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            c.Connect(1500);
            using var w = new StreamWriter(c);
            w.Write(string.IsNullOrEmpty(arg) ? "<nueva>" : arg);
            w.Flush();
            return true;
        }
        catch { return false; }
    }

    private void ListenForOtherInstances()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    s.WaitForConnection();
                    using var r = new StreamReader(s);
                    var msg = r.ReadToEnd();
                    if (msg.StartsWith("@")) Dispatcher.BeginInvoke(() => (Current.Windows.OfType<MainWindow>().FirstOrDefault())?.RunTestCommand(msg[1..]));
                    else Dispatcher.BeginInvoke(() => OpenWindow(msg == "<nueva>" ? null : msg));
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "Faro pipe" };
        t.Start();
    }

    private void OfferResume()
    {
        var pending = TransferJournal.LoadPending();
        if (pending.Count == 0) return;
        var desc = string.Join("\n", pending.Take(5).Select(j =>
            $"• {(j.Kind == TransferKind.Move ? "Mover" : "Copiar")} {(j.Sources.Count == 1 ? "«" + Path.GetFileName(j.Sources[0].TrimEnd('\\')) + "»" : j.Sources.Count + " elementos")} a «{Path.GetFileName(j.Destination.TrimEnd('\\'))}»"));
        var r = new MacDialog(MainWindow, pending.Count == 1 ? "Hay una copia que no terminó" : $"Hay {pending.Count} copias que no terminaron",
            desc + $"\n\n{Core.AppInfo.Name} puede continuarlas donde se quedaron: lo que ya se copió no se vuelve a copiar.",
            new[] { new DialogButton("Descartar", "no", false, true), new DialogButton("Más tarde", "later"), new DialogButton("Reanudar", "yes", true) }).ShowFor();
        if (r == "yes") foreach (var j in pending) TransferManager.Instance.Resume(j);
        else if (r == "no") foreach (var j in pending) j.Delete();
    }

    private bool _showingError;
    private DateTime _lastDialogAt;
    private readonly Dictionary<string, DateTime> _recentErrors = new();

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        // Un error que se repite (p. ej. en cada pasada de layout) no debe llenar el disco ni apilar
        // diálogos: el diálogo modal vuelve a disparar el layout y la app quedaba congelada.
        // Se agrupa por tipo + primer marco (no por el texto entero: dos errores que se alternan
        // burlaban la comprobación y el registro llegó a 60 MB).
        var ex = e.Exception;
        var firstFrame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
        string signature = ex.GetType().FullName + "|" + firstFrame;
        var now = DateTime.Now;
        if (_recentErrors.TryGetValue(signature, out var seen) && now - seen < TimeSpan.FromMinutes(5)) return;
        _recentErrors[signature] = now;
        Log(ex.ToString());
        if (ex is OutOfMemoryException || _showingError || now - _lastDialogAt < TimeSpan.FromMinutes(1)) return;
        _showingError = true; _lastDialogAt = now;
        try { MacDialog.Error(MainWindow, "Algo salió mal", ex.Message + $"\n\n{Core.AppInfo.Name} sigue funcionando. El detalle quedó en el registro."); } catch { }
        finally { _showingError = false; }
    }

    // ---------- tiempos de arranque (logs\arranque.log) ----------
    private static readonly HashSet<string> StartupMarks = new();
    private static readonly System.Text.StringBuilder StartupLine = new();

    /// <summary>
    /// Anota cuánto tardó cada fase desde que Windows creó el proceso: «inicio» (runtime + WPF cargados),
    /// «xaml», «lateral», «hwnd», «cargada» (pasos de la ventana antes de pintarse), «ventana» (primer
    /// pintado) y «contenido» (la carpeta ya listada). Una línea por arranque.
    /// </summary>
    public static void MarkStartup(string phase)
    {
        if (!StartupMarks.Add(phase) || StartupMarks.Contains("hecho")) return;
        double ms = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        StartupLine.Append($"  {phase} {ms:0} ms");
        // se escribe cuando ya hay ventana pintada Y carpeta listada (el orden entre las dos varía)
        if (!StartupMarks.Contains("ventana") || !StartupMarks.Contains("contenido")) return;
        StartupMarks.Add("hecho");
        try
        {
            var dir = Path.Combine(Core.AppInfo.LocalDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "arranque.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{StartupLine}  (encendido hace {TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"h\:mm\:ss")}){Environment.NewLine}");
        }
        catch { }
    }

    public static void Log(string text)
    {
        try
        {
            var dir = Path.Combine(Core.AppInfo.LocalDir, "logs");
            Directory.CreateDirectory(dir);
            var file = new FileInfo(Path.Combine(dir, "errores.log"));
            if (file.Exists && file.Length > 5_000_000) file.MoveTo(file.FullName + ".old", true);
            File.AppendAllText(Path.Combine(dir, "errores.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Core.TagStore.Save();
        Settings.Current.Save();
        base.OnExit(e);
    }
}
