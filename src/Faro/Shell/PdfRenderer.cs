using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Faro.Shell;

/// <summary>
/// Renderiza PDF con el motor PDF integrado en Windows 10/11 (sin Acrobat). También abre los
/// .ai de Illustrator guardados con compatibilidad PDF (la opción por defecto).
/// </summary>
public static class PdfRenderer
{
    public static async Task<PdfDocument> OpenAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            return await PdfDocument.LoadFromFileAsync(file);
        }
        catch { return null; }
    }

    public static async Task<BitmapSource> RenderPageAsync(PdfDocument doc, uint index, uint width)
    {
        try
        {
            using var page = doc.GetPage(index);
            using var stream = new InMemoryRandomAccessStream();
            var opts = new PdfPageRenderOptions { DestinationWidth = width };
            await page.RenderToStreamAsync(stream, opts);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = stream.AsStream();
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    /// <summary>Primera página como miniatura (síncrono, para hilos de fondo).</summary>
    public static BitmapSource FirstPage(string path, int width)
    {
        try
        {
            var doc = OpenAsync(path).GetAwaiter().GetResult();
            if (doc == null || doc.PageCount == 0) return null;
            return RenderPageAsync(doc, 0, (uint)width).GetAwaiter().GetResult();
        }
        catch { return null; }
    }
}
