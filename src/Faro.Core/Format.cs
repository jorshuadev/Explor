using System.Globalization;

namespace Faro.Core;

/// <summary>Formatos al estilo Finder (unidades decimales, fechas relativas "Hoy, 10:42").</summary>
public static class Format
{
    public static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    /// <summary>Tamaño en unidades decimales como macOS: 1 KB = 1000 bytes.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0) return "--";
        if (bytes == 0) return "Cero bytes";
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] u = { "KB", "MB", "GB", "TB", "PB" };
        double v = bytes; int i = -1;
        do { v /= 1000; i++; } while (v >= 999.95 && i < u.Length - 1);
        var fmt = v >= 100 || i == 0 ? "0" : "0.#";
        return v.ToString(fmt, Es) + " " + u[i];
    }

    /// <summary>"1.234.567 bytes" para Obtener información.</summary>
    public static string ExactBytes(long bytes) => bytes.ToString("N0", Es) + " bytes";

    public static string Count(long n) => n.ToString("N0", Es);

    public static string Items(long n) => n == 1 ? "1 elemento" : Count(n) + " elementos";

    public static string Date(DateTime local)
    {
        if (local == DateTime.MinValue || local.Year < 1981) return "--";
        var today = DateTime.Today;
        var time = local.ToString("H:mm", Es);
        if (local.Date == today) return "Hoy, " + time;
        if (local.Date == today.AddDays(-1)) return "Ayer, " + time;
        return local.ToString("d MMM yyyy", Es).Replace(".", "") + ", " + time;
    }

    public static string LongDate(DateTime local)
    {
        if (local == DateTime.MinValue || local.Year < 1981) return "--";
        return local.ToString("dddd, d 'de' MMMM 'de' yyyy, H:mm", Es);
    }

    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>"Quedan unos 3 minutos" como el diálogo de copia de Finder.</summary>
    public static string Remaining(TimeSpan t)
    {
        if (t.TotalSeconds < 5) return "Quedan unos segundos";
        if (t.TotalSeconds < 60) return $"Quedan unos {(int)Math.Ceiling(t.TotalSeconds / 5) * 5} segundos";
        if (t.TotalMinutes < 2) return "Queda alrededor de 1 minuto";
        if (t.TotalMinutes < 60) return $"Quedan unos {(int)Math.Round(t.TotalMinutes)} minutos";
        if (t.TotalHours < 2) return $"Queda alrededor de 1 hora y {(int)t.Minutes} min";
        return $"Quedan unas {(int)Math.Round(t.TotalHours)} horas";
    }

    public static string Speed(double bytesPerSec) => Size((long)bytesPerSec) + "/s";
}
