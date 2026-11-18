using System.Windows;
using System.Windows.Controls;
using Faro.Services;

namespace Faro.Windows;

public partial class ProgressWindow : Window
{
    public ProgressWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Theme.ApplyWindowEffects(this);
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) MainWindow.FadeIn((UIElement)Content, 200, 8, 0.97); };
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TransferViewModel vm)
        {
            if (vm.Job.IsPaused) vm.Job.ResumeRun(); else vm.Job.Pause();
            vm.Refresh();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TransferViewModel vm) vm.Job.Cancel();
    }
}
