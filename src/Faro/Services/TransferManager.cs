using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Faro.Core;
using Faro.Core.Transfer;
using Faro.Dialogs;

namespace Faro.Services;

/// <summary>
/// Lanza las operaciones de copia/movimiento, muestra la ventana de progreso cuando tardan,
/// contesta los conflictos con diálogos y registra el Deshacer.
/// </summary>
public sealed class TransferManager : ITransferPrompt
{
    public static TransferManager Instance { get; } = new();

    public ObservableCollection<TransferViewModel> Jobs { get; } = new();
    private Windows.ProgressWindow _window;
    private readonly DispatcherTimer _timer;

    private TransferManager()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
    }

    public TransferJob Start(TransferKind kind, IEnumerable<string> sources, string destination, bool registerUndo = true)
    {
        var list = sources.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // última barrera: lo protegido (discos, carpetas de Windows, elementos de sistema) nunca se mueve,
        // venga de arrastrar, cortar y pegar o «nueva carpeta con la selección». El Deshacer no pasa por aquí.
        if (kind == TransferKind.Move && registerUndo) list = Protection.Filter(list, "mover", _window?.IsVisible == true ? _window : Application.Current.MainWindow);
        if (list.Count == 0) return null;
        var job = new TransferJob(kind, list, destination, this);
        Run(job, registerUndo);
        return job;
    }

    public void Resume(TransferJournal j) => Run(TransferJob.Resume(j, this), false);

    private void Run(TransferJob job, bool registerUndo)
    {
        var vm = new TransferViewModel(job);
        Jobs.Add(vm);
        var placed = new List<(string src, string dst)>();
        job.ItemPlaced += (s, d) =>
        {
            lock (placed) placed.Add((s, d));
            if (job.Kind == TransferKind.Move) TagStore.Relocate(s, d);
            else TagStore.Relocate(s, d, keepOriginal: true);
        };
        _timer.Start();
        // la ventana sólo aparece si la operación dura más de medio segundo (como Finder)
        var show = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        show.Tick += (_, _) => { show.Stop(); if (!job.IsFinished) ShowWindow(); };
        show.Start();

        job.RunAsync().ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            vm.Refresh();
            if (registerUndo && job.State == TransferState.Completed && placed.Count > 0)
                UndoManager.RegisterTransfer(job.Kind, placed.ToList());
            if (job.State == TransferState.Failed)
                MacDialog.Error(_window ?? Application.Current.MainWindow, $"No se pudo completar la operación.", job.Message);
            else if (job.State == TransferState.Completed && !job.Issues.IsEmpty)
                ShowIssues(job);
            Jobs.Remove(vm);
            if (Jobs.Count == 0) { _timer.Stop(); _window?.Hide(); }
            Completed?.Invoke(job);
        }));
    }

    public event Action<TransferJob> Completed;

    /// <summary>Nombre para los diálogos: las carpetas llegan con «\» final y GetFileName devolvía «» vacío.</summary>
    private static string NameOf(string path)
    {
        var n = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path ?? ""));
        return string.IsNullOrEmpty(n) ? path : n;
    }

    private void ShowIssues(TransferJob job)
    {
        var issues = job.Issues.ToList();
        var text = string.Join("\n", issues.Take(8).Select(i => $"• {NameOf(i.Path)}: {i.Message}"));
        if (issues.Count > 8) text += $"\n… y {issues.Count - 8} más.";
        MacDialog.Info(_window?.IsVisible == true ? _window : Application.Current.MainWindow,
            issues.Count == 1 ? "Un elemento no se pudo procesar" : $"{issues.Count} elementos no se pudieron procesar", text);
    }

    public void ShowWindow()
    {
        if (Jobs.Count == 0) return;
        if (_window == null)
        {
            _window = new Windows.ProgressWindow { DataContext = this };
            _window.Closing += (_, e) => { e.Cancel = true; _window.Hide(); };
        }
        if (!_window.IsVisible) _window.Show();
        _window.Activate();
    }

    private void Tick()
    {
        foreach (var j in Jobs) j.Refresh();
    }

    // ---------------- ITransferPrompt ----------------

    public Task<ConflictAnswer> ResolveConflictAsync(TransferJob job, ConflictRequest r)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var verb = job.Kind == TransferKind.Move ? "moviendo" : "copiando";
            var title = $"Ya existe un elemento llamado «{r.Name}» en esta ubicación. ¿Quieres reemplazarlo por el que estás {verb}?";
            var details = new StackPanel();
            details.Children.Add(Line("Original", r.DestinationIsDirectory ? "Carpeta" : Format.Size(r.DestinationSize), r.DestinationModified));
            details.Children.Add(Line("Nuevo", r.SourceIsDirectory ? "Carpeta" : Format.Size(r.SourceSize), r.SourceModified));
            var buttons = new List<DialogButton>();
            buttons.Add(new DialogButton("Reemplazar", "replace", true));
            if (r.CanMerge) buttons.Add(new DialogButton("Combinar", "merge"));
            buttons.Add(new DialogButton("Conservar ambos", "keep"));
            buttons.Add(new DialogButton("Omitir", "skip"));
            buttons.Add(new DialogButton("Detener", "stop", false, true));
            var d = new MacDialog(_window?.IsVisible == true ? _window : Application.Current.MainWindow, title,
                r.CanMerge ? "«Combinar» junta el contenido de las dos carpetas; si hay archivos con el mismo nombre te preguntará." : null,
                buttons, checkText: "Aplicar a todos", glyph: "", extra: details);
            var res = d.ShowFor() ?? "stop";
            var choice = res switch
            {
                "replace" => ConflictChoice.Replace,
                "merge" => ConflictChoice.Merge,
                "keep" => ConflictChoice.KeepBoth,
                "skip" => ConflictChoice.Skip,
                _ => ConflictChoice.Stop
            };
            return new ConflictAnswer(choice, d.Checked);
        }).Task;
    }

    private static FrameworkElement Line(string label, string size, DateTime modified)
    {
        var t = new TextBlock { FontSize = 11, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 1, 0, 1) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        t.Text = $"{label}: {size} · modificado {Format.Date(modified)}";
        return t;
    }

    public Task<ErrorChoice> ResolveErrorAsync(TransferJob job, ErrorRequest r)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var verb = job.Kind == TransferKind.Move ? "mover" : "copiar";
            var d = new MacDialog(_window?.IsVisible == true ? _window : Application.Current.MainWindow,
                $"No se pudo {verb} «{NameOf(r.Path)}».", r.Message,
                new[]
                {
                    new DialogButton("Reintentar", "retry", true), new DialogButton("Omitir", "skip"),
                    new DialogButton("Omitir todos los errores", "skipall"), new DialogButton("Detener", "stop", false, true)
                }, glyph: "");
            return (d.ShowFor() ?? "stop") switch
            {
                "retry" => ErrorChoice.Retry,
                "skip" => ErrorChoice.Skip,
                "skipall" => ErrorChoice.SkipAll,
                _ => ErrorChoice.Stop
            };
        }).Task;
    }
}

/// <summary>Lo que muestra cada fila de la ventana de progreso.</summary>
public sealed class TransferViewModel : System.ComponentModel.INotifyPropertyChanged
{
    public TransferJob Job { get; }
    public TransferViewModel(TransferJob job)
    {
        Job = job;
        var first = Model.FileItem.FromPath(job.Sources[0]);
        Icon = first != null ? IconFactory.For(first) : IconFactory.Folder();
    }

    public System.Windows.Media.ImageSource Icon { get; }

    public string Title { get; private set; }
    public string Detail { get; private set; }
    public double Percent { get; private set; }
    public bool IsIndeterminate { get; private set; }
    public bool IsPaused => Job.State == TransferState.Paused;
    public bool IsWaiting => Job.State == TransferState.WaitingForDevice;
    public string PauseGlyph => IsPaused ? "" : "";

    public void Refresh()
    {
        Job.Sample();
        long tf = Job.TotalFiles;
        var what = Job.Sources.Count == 1 ? $"«{System.IO.Path.GetFileName(Job.Sources[0].TrimEnd('\\'))}»" : $"{Format.Count(Job.Sources.Count)} elementos";
        Title = Job.State switch
        {
            TransferState.Preparing => $"Preparando para {(Job.Kind == TransferKind.Move ? "mover" : "copiar")} {what}…",
            _ => $"{Job.Verb} {what} a «{Job.DestinationName}»"
        };
        IsIndeterminate = Job.State is TransferState.Preparing || (Job.TotalBytes == 0 && tf == 0);
        Percent = Job.Fraction * 100;
        string detail;
        switch (Job.State)
        {
            case TransferState.Preparing:
                detail = Job.PreparedItems > 0 ? $"{Format.Count(Job.PreparedItems)} elementos encontrados…" : "Calculando…";
                break;
            case TransferState.WaitingForDevice:
            case TransferState.Finishing:
                detail = Job.Message;
                break;
            case TransferState.Paused:
                detail = $"En pausa — {Format.Size(Job.DoneBytes)} de {Format.Size(Job.TotalBytes)}";
                break;
            case TransferState.WaitingForUser:
                detail = "Esperando tu respuesta…";
                break;
            default:
                var rem = Job.Remaining;
                detail = Job.TotalBytes > 0
                    ? $"{Format.Size(Job.DoneBytes)} de {Format.Size(Job.TotalBytes)}" + (Job.BytesPerSecond > 0 ? $" ({Format.Speed(Job.BytesPerSecond)})" : "")
                    : $"{Format.Count(Job.DoneFiles)} de {Format.Count(tf)}";
                if (tf > 1 && Job.TotalBytes > 0) detail = $"{Format.Count(Job.DoneFiles)} de {Format.Count(tf)} elementos — " + detail;
                if (rem != null) detail += " — " + Format.Remaining(rem.Value);
                break;
        }
        Detail = detail;
        foreach (var p in new[] { nameof(Title), nameof(Detail), nameof(Percent), nameof(IsIndeterminate), nameof(IsPaused), nameof(IsWaiting), nameof(PauseGlyph) })
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
    }

    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
}
