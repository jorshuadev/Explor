using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Faro.Windows;

/// <summary>
/// Visor de imágenes de la visualización rápida: zoom con la rueda (hacia el puntero), arrastrar
/// para moverse, doble clic para ver a tamaño real, girar la vista y copiar la imagen.
/// Girar solo cambia cómo se ve aquí; el archivo no se modifica.
/// </summary>
public sealed partial class QuickLookWindow
{
    private string _imagePath;
    private ImageSource _imageShown;
    private static bool _lightImageBackground; // se mantiene al pasar de una imagen a otra

    /// <summary>Tamaño real en píxeles (solo lee la cabecera), ya girado según la orientación EXIF.</summary>
    private static (int w, int h) ImageSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var f = dec.Frames[0];
            int w = f.PixelWidth, h = f.PixelHeight;
            if (f.Metadata is BitmapMetadata md && md.ContainsQuery("System.Photo.Orientation") && Convert.ToInt32(md.GetQuery("System.Photo.Orientation")) is 6 or 8)
                (w, h) = (h, w);
            return (w, h);
        }
        catch { return (0, 0); }
    }

    private FrameworkElement ImageView(ImageSource img, string path, int realWidth)
    {
        _imagePath = path; _imageShown = img;
        var image = new Image { Source = img, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var turn = new RotateTransform();
        image.LayoutTransform = turn;
        var scale = new ScaleTransform();
        var move = new TranslateTransform();
        var zoomBox = new Grid { RenderTransform = new TransformGroup { Children = { scale, move } } };
        // el fondo va pegado a la imagen (no a toda la ventana) para ver bien el borde de lo transparente
        var backing = new Border { Child = image, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        zoomBox.Children.Add(backing);
        var host = new Border { Background = Brushes.Transparent, ClipToBounds = true, Child = zoomBox, Margin = new Thickness(10, 0, 10, 12) };

        Button realBtn = null;
        void Clamp()
        {
            double s = scale.ScaleX;
            move.X = Math.Clamp(move.X, zoomBox.ActualWidth * (1 - s), 0);
            move.Y = Math.Clamp(move.Y, zoomBox.ActualHeight * (1 - s), 0);
        }
        void Updated()
        {
            bool zoomed = scale.ScaleX > 1.001;
            host.Cursor = zoomed ? Cursors.SizeAll : null;
            SetBtnText(realBtn, zoomed ? "Ajustar" : "Tamaño real");
            realBtn.ToolTip = zoomed ? "Ver la imagen entera" : "Ver a tamaño real (doble clic)";
        }
        void ZoomAt(Point p, double factor)
        {
            double s = scale.ScaleX, ns = Math.Clamp(s * factor, 1, 20);
            if (ns <= 1.001) { scale.ScaleX = scale.ScaleY = 1; move.X = move.Y = 0; Updated(); return; }
            // el punto bajo el ratón se queda quieto: p·s + t = p·s' + t'
            move.X += p.X * (s - ns); move.Y += p.Y * (s - ns);
            scale.ScaleX = scale.ScaleY = ns;
            Clamp(); Updated();
        }
        // cuánto hay que ampliar para que 1 píxel de la imagen sea 1 píxel de la pantalla
        double RealFactor()
        {
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double shown = (turn.Angle % 180 == 0 ? image.ActualWidth : image.ActualHeight) * dpi;
            int real = realWidth > 0 ? realWidth : (img as BitmapSource)?.PixelWidth ?? 0;
            double f = shown > 0 && real > 0 ? real / shown : 2;
            return f > 1.05 ? f : 2;
        }
        void ToggleReal(Point at)
        {
            if (scale.ScaleX > 1.001) ZoomAt(at, 0);
            else ZoomAt(at, RealFactor());
        }

        host.MouseWheel += (_, e) => { ZoomAt(e.GetPosition(zoomBox), e.Delta > 0 ? 1.25 : 0.8); e.Handled = true; };
        Point? dragFrom = null; double startX = 0, startY = 0;
        host.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { ToggleReal(e.GetPosition(zoomBox)); e.Handled = true; return; }
            if (scale.ScaleX <= 1.001) return;
            dragFrom = e.GetPosition(host); startX = move.X; startY = move.Y;
            host.CaptureMouse(); e.Handled = true;
        };
        host.MouseMove += (_, e) =>
        {
            if (dragFrom is not Point from) return;
            var p = e.GetPosition(host);
            move.X = startX + p.X - from.X; move.Y = startY + p.Y - from.Y;
            Clamp();
        };
        host.MouseLeftButtonUp += (_, _) => { if (dragFrom != null) { dragFrom = null; host.ReleaseMouseCapture(); } };
        host.SizeChanged += (_, _) => Clamp();

        realBtn = TextBtn("Tamaño real", "Ver a tamaño real (doble clic)", () =>
            ToggleReal(new Point(zoomBox.ActualWidth / 2, zoomBox.ActualHeight / 2)));
        _tools.Children.Add(realBtn);
        _tools.Children.Add(TextBtn("Girar", "Girar la vista 90° (el archivo no cambia)", () =>
        {
            turn.Angle = (turn.Angle + 90) % 360;
            scale.ScaleX = scale.ScaleY = 1; move.X = move.Y = 0; Updated();
        }));
        Button bgBtn = null;
        void ShowBackground()
        {
            backing.Background = _lightImageBackground ? Brushes.White : Brushes.Transparent;
            if (_lightImageBackground) bgBtn.SetResourceReference(BackgroundProperty, "Bg.Pressed"); else bgBtn.Background = Brushes.Transparent;
        }
        bgBtn = ToolBtn("", "Fondo claro detrás de la imagen, para ver los bordes y fallos de las transparencias", () =>
        {
            _lightImageBackground = !_lightImageBackground;
            ShowBackground();
        });
        ShowBackground();
        _tools.Children.Add(bgBtn);
        _tools.Children.Add(TextBtn("Copiar imagen", "Copiar la imagen para pegarla en otro programa (Ctrl+C)", CopyImage));
        return host;
    }

    /// <summary>Copia la imagen a tamaño completo (la vista usa una versión reducida).</summary>
    private void CopyImage()
    {
        BitmapSource bmp = null;
        if (_imagePath != null)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(_imagePath);
                bi.EndInit();
                bi.Freeze();
                bmp = ApplyExifRotation(_imagePath, bi);
            }
            catch { }
        }
        bmp ??= _imageShown as BitmapSource;
        if (bmp == null) return;
        try { Clipboard.SetImage(bmp); Notice("Imagen copiada"); }
        catch { Notice("No se pudo copiar la imagen"); }
    }
}
