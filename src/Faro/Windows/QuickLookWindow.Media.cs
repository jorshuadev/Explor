using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Faro.Core;
using Faro.Model;
using Faro.Services;

namespace Faro.Windows;

/// <summary>
/// Reproductor de vídeo y audio de la vista rápida: barra de progreso con la parte vista en azul y bola visible
/// (clic = saltar ahí, al pasar el ratón dice a qué minuto), ±10 s, velocidad, volumen y pantalla completa.
/// Teclado (también desde la ventana principal mientras suena): Espacio/K pausa, ←/→ 5 s, J/L 10 s,
/// ↑/↓ volumen, M silencio, F pantalla completa, Inicio/Fin, 0–9 = saltar a ese 10 %.
/// </summary>
public sealed partial class QuickLookWindow
{
    /// <summary>Teclas del reproductor activo; null si no se está mostrando vídeo ni audio.</summary>
    private Func<Key, ModifierKeys, bool> _mediaKeys;

    /// <summary>La ventana principal le pasa las teclas mientras suena algo (la vista rápida no tiene el foco).</summary>
    public bool HandleMediaKey(Key key, ModifierKeys mods) => IsVisible && _mediaKeys != null && _mediaKeys(key, mods);

    private Button MediaBtn(string glyph, string tip, Action a, double size = 13)
    {
        var b = new Button { Style = (Style)FindResource("ToolButton"), ToolTip = tip, Height = 30, MinWidth = 32, Focusable = false };
        b.Content = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = size };
        b.Click += (_, _) => a();
        return b;
    }

    private Button MediaTextBtn(string text, string tip, Action a)
    {
        var b = new Button { Style = (Style)FindResource("ToolButton"), ToolTip = tip, Height = 30, MinWidth = 0, Padding = new Thickness(7, 0, 7, 0), Focusable = false };
        b.Content = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => a();
        return b;
    }

    private static string VolumeGlyph(double v, bool muted) => muted || v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";

    private FrameworkElement MediaView(string path, bool audio, FileItem item)
    {
        var grid = new Grid { Margin = new Thickness(10, 0, 10, 8), Background = Brushes.Transparent };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var media = _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, ScrubbingEnabled = true,
            Volume = Math.Clamp(Settings.Current.MediaVolume, 0, 1), Source = new Uri(path)
        };

        // zona de imagen (o carátula del audio) con el aviso flotante de «+5 s», «Volumen 70 %»…
        var stage = new Grid { Background = audio ? Brushes.Transparent : Brushes.Black, ClipToBounds = true };
        if (audio)
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new Image { Source = item.Icon, Width = 200, Height = 200 });
            var info = new TextBlock { Text = item.DisplayName, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0), FontSize = 14 };
            info.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
            sp.Children.Add(info);
            stage.Children.Add(sp);
            stage.Children.Add(new Border { Child = media, Width = 0, Height = 0 });
        }
        else stage.Children.Add(media);
        var osdText = new TextBlock { Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold };
        var osd = new Border
        {
            Child = osdText, Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 7, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 18, 0, 0), Opacity = 0, IsHitTestVisible = false
        };
        stage.Children.Add(osd);
        var stageFrame = new Border { Child = stage, CornerRadius = new CornerRadius(4), ClipToBounds = true };
        grid.Children.Add(stageFrame);

        void Osd(string text)
        {
            osdText.Text = text;
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100))));
            osd.BeginAnimation(OpacityProperty, a);
        }

        // ---- controles ----
        var playGlyph = new TextBlock { Text = "", Style = (Style)FindResource("Glyph"), FontSize = 15 };
        var play = new Button { Style = (Style)FindResource("ToolButton"), ToolTip = "Reproducir / pausar (Espacio)", Height = 30, MinWidth = 34, Focusable = false, Content = playGlyph };
        var progress = new Slider { Style = (Style)FindResource("MediaSlider"), Minimum = 0, Maximum = 1, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var now = new TextBlock { Text = "0:00", VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, MinWidth = 38, TextAlignment = TextAlignment.Right, Margin = new Thickness(4, 0, 0, 0) };
        var total = new TextBlock { Text = "0:00", VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, MinWidth = 38, Margin = new Thickness(0, 0, 4, 0) };
        now.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Primary");
        total.SetResourceReference(TextBlock.ForegroundProperty, "Fg.Secondary");
        foreach (var t in new[] { now, total }) System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);

        bool playing = true, fromTimer = false, dragging = false;
        TimeSpan Duration() => media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        void UpdateTimes() { now.Text = Format.Duration(media.Position); total.Text = Format.Duration(Duration()); }
        // en vídeo los controles flotan sobre la imagen y se esconden solos; «wake» los vuelve a mostrar
        Action wake = () => { };
        void SetPlaying(bool p)
        {
            playing = p;
            wake();
            if (p) media.Play(); else media.Pause();
            playGlyph.Text = p ? "" : "";
        }
        void TogglePlay() { SetPlaying(!playing); Osd(playing ? "Reproduciendo" : "Pausa"); }
        void SeekTo(double seconds, bool showOsd = false)
        {
            var d = Duration().TotalSeconds;
            if (d <= 0) return;
            seconds = Math.Clamp(seconds, 0, Math.Max(0, d - 0.05));
            media.Position = TimeSpan.FromSeconds(seconds);
            fromTimer = true; progress.Value = seconds; fromTimer = false;
            UpdateTimes();
            if (showOsd) Osd($"{Format.Duration(TimeSpan.FromSeconds(seconds))} / {Format.Duration(Duration())}");
        }
        void SeekBy(double s) { wake(); SeekTo(media.Position.TotalSeconds + s); Osd((s > 0 ? "+" : "−") + $"{Math.Abs(s):0} s   ·   {Format.Duration(media.Position)}"); }

        play.Click += (_, _) => TogglePlay();
        var back = MediaTextBtn("−10", "Retroceder 10 s (J)", () => SeekBy(-10));
        var fwd = MediaTextBtn("+10", "Adelantar 10 s (L)", () => SeekBy(10));

        // velocidad (1× → 1,25× → 1,5× → 2× → 0,5×)
        double[] speeds = { 1, 1.25, 1.5, 2, 0.5 };
        int speed = 0;
        Button speedBtn = null;
        speedBtn = MediaTextBtn("1×", "Velocidad de reproducción", () =>
        {
            speed = (speed + 1) % speeds.Length;
            media.SpeedRatio = speeds[speed];
            var label = $"{speeds[speed]:0.##}×".Replace('.', ',');
            SetBtnText(speedBtn, label);
            Osd("Velocidad " + label);
        });

        // volumen: botón (silenciar) + barra; se recuerda para el siguiente vídeo
        var volGlyph = new TextBlock { Style = (Style)FindResource("Glyph"), FontSize = 14 };
        var volBtn = new Button { Style = (Style)FindResource("ToolButton"), ToolTip = "Silenciar (M)", Height = 30, MinWidth = 32, Focusable = false, Content = volGlyph };
        var volume = new Slider { Style = (Style)FindResource("MediaSlider"), Minimum = 0, Maximum = 1, Width = 78, Value = media.Volume, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Volumen (↑ / ↓)" };
        void ShowVolume() => volGlyph.Text = VolumeGlyph(media.Volume, media.IsMuted);
        void SetVolume(double v, bool showOsd)
        {
            v = Math.Round(Math.Clamp(v, 0, 1), 2);
            media.Volume = v; media.IsMuted = false;
            if (Math.Abs(volume.Value - v) > 0.001) volume.Value = v;
            Settings.Current.MediaVolume = v;
            ShowVolume();
            if (showOsd) Osd($"Volumen {Math.Round(v * 100)} %");
        }
        void ToggleMute() { media.IsMuted = !media.IsMuted; ShowVolume(); Osd(media.IsMuted ? "Sin sonido" : $"Volumen {Math.Round(media.Volume * 100)} %"); }
        volume.ValueChanged += (_, e) => { if (Math.Abs(e.NewValue - media.Volume) > 0.001) SetVolume(e.NewValue, false); };
        volBtn.Click += (_, _) => ToggleMute();
        ShowVolume();

        void ToggleFull() { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        var fullBtn = MediaBtn("", "Pantalla completa (F)", ToggleFull, 12);

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(back); left.Children.Add(play); left.Children.Add(fwd); left.Children.Add(now);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(total); right.Children.Add(speedBtn); right.Children.Add(volBtn); right.Children.Add(volume);
        if (!audio) right.Children.Add(fullBtn);
        var controls = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(left, Dock.Left); DockPanel.SetDock(right, Dock.Right);
        controls.Children.Add(left); controls.Children.Add(right); controls.Children.Add(progress);
        if (audio)
        {
            Grid.SetRow(controls, 1);
            grid.Children.Add(controls);
        }
        else
        {
            // Vídeo: barra flotante sobre la imagen (siempre oscura, como en cualquier reproductor). Se esconde
            // sola a los 2,5 s sin mover el ratón mientras reproduce; vuelve al moverlo, al pausar o al saltar.
            controls.Margin = new Thickness(0);
            var bar = new Border
            {
                Child = controls, Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x1C, 0x1C, 0x1E)), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(12, 0, 12, 12), VerticalAlignment = VerticalAlignment.Bottom
            };
            bar.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Themes/Dark.xaml", UriKind.Relative) });
            grid.Children.Add(bar);

            bool shown = true;
            var lastMouse = new Point(-1, -1);
            var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            hideTimer.Tick += (_, _) =>
            {
                hideTimer.Stop();
                if (_media != media || !playing) return;
                if (dragging || bar.IsMouseOver) { hideTimer.Start(); return; }
                shown = false;
                bar.IsHitTestVisible = false;
                bar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(350)));
                stage.Cursor = Cursors.None;
            };
            wake = () =>
            {
                hideTimer.Stop();
                if (!shown)
                {
                    shown = true;
                    bar.IsHitTestVisible = true;
                    bar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
                    stage.Cursor = null;
                }
                if (playing && _media == media) hideTimer.Start();
            };
            // WPF repite MouseMove cuando cambia lo que hay bajo el cursor aunque el ratón esté quieto: sólo cuenta si se movió
            grid.MouseMove += (_, e) =>
            {
                var p = e.GetPosition(grid);
                if (Math.Abs(p.X - lastMouse.X) + Math.Abs(p.Y - lastMouse.Y) < 3) return;
                lastMouse = p;
                wake();
            };
        }

        // ---- barra de progreso: clic o arrastre = saltar; al pasar el ratón, a qué minuto se iría ----
        progress.ValueChanged += (_, e) => { if (!fromTimer) { media.Position = TimeSpan.FromSeconds(e.NewValue); UpdateTimes(); } };
        progress.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        progress.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => dragging = false));
        var hoverText = new TextBlock { Foreground = Brushes.White, FontSize = 11.5 };
        var hoverTip = new Popup
        {
            PlacementTarget = progress, Placement = PlacementMode.Relative, AllowsTransparency = true, IsHitTestVisible = false,
            Child = new Border { Child = hoverText, Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x20, 0x22)), CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 3, 7, 3) }
        };
        progress.MouseMove += (_, e) =>
        {
            var d = Duration().TotalSeconds;
            if (d <= 0 || progress.ActualWidth <= 14) return;
            double x = e.GetPosition(progress).X;
            double r = Math.Clamp((x - 7) / (progress.ActualWidth - 14), 0, 1);
            hoverText.Text = Format.Duration(TimeSpan.FromSeconds(r * d));
            hoverTip.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            hoverTip.HorizontalOffset = x - hoverTip.Child.DesiredSize.Width / 2;
            hoverTip.VerticalOffset = -hoverTip.Child.DesiredSize.Height - 4;
            hoverTip.IsOpen = true;
        };
        progress.MouseLeave += (_, _) => hoverTip.IsOpen = false;

        // clic en la imagen = pausa / sigue; doble clic = pantalla completa
        stage.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) ToggleFull(); else TogglePlay(); e.Handled = true; };

        media.MediaOpened += (_, _) => { if (media.NaturalDuration.HasTimeSpan) progress.Maximum = media.NaturalDuration.TimeSpan.TotalSeconds; UpdateTimes(); };
        media.MediaEnded += (_, _) => { media.Position = TimeSpan.Zero; SetPlaying(false); fromTimer = true; progress.Value = 0; fromTimer = false; UpdateTimes(); };
        media.MediaFailed += (_, _) =>
        {
            // sin códec: mostrar la miniatura y la ficha
            Dispatcher.BeginInvoke(async () =>
            {
                _mediaKeys = null;
                var big = await ThumbnailLoader.LoadLargeAsync(path, 1024, allowIcon: true);
                _tools.Children.Clear();
                _content.Child = InfoCard(item, big ?? item.Icon, "Windows no tiene el códec para reproducir este archivo aquí. Ábrelo con tu reproductor (por ejemplo VLC).");
            });
        };
        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _mediaTimer.Tick += (_, _) =>
        {
            if (_media == null) return;
            if (!dragging) { fromTimer = true; progress.Value = media.Position.TotalSeconds; fromTimer = false; }
            UpdateTimes();
        };
        _mediaTimer.Start();

        _mediaKeys = (key, mods) =>
        {
            if (_media != media) return false;
            if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
            bool shift = (mods & ModifierKeys.Shift) != 0;
            switch (key)
            {
                case Key.Space: case Key.K: case Key.MediaPlayPause: TogglePlay(); return true;
                case Key.Left: SeekBy(shift ? -1 : -5); return true;
                case Key.Right: SeekBy(shift ? 1 : 5); return true;
                case Key.J: SeekBy(-10); return true;
                case Key.L: SeekBy(10); return true;
                case Key.Up: SetVolume(media.Volume + 0.05, true); return true;
                case Key.Down: SetVolume(media.Volume - 0.05, true); return true;
                case Key.M: ToggleMute(); return true;
                case Key.F when !audio: ToggleFull(); return true;
                case Key.Home: SeekTo(0, true); return true;
                case Key.End: SeekTo(Duration().TotalSeconds, true); return true;
                case >= Key.D0 and <= Key.D9: SeekTo(Duration().TotalSeconds * (key - Key.D0) / 10.0, true); return true;
                case >= Key.NumPad0 and <= Key.NumPad9: SeekTo(Duration().TotalSeconds * (key - Key.NumPad0) / 10.0, true); return true;
                default: return false;
            }
        };

        media.Play();
        wake();
        return grid;
    }
}
