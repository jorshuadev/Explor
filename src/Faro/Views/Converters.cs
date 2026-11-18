using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Faro.Views;

public static class Converters
{
    public static readonly IValueConverter LeftMargin = new Fn(v => new Thickness(v is double d ? d : 0, 0, 0, 0));
    public static readonly IValueConverter Not = new Fn(v => v is bool b ? !b : v);
    public static readonly IValueConverter NotToVis = new Fn(v => v is bool b && b ? Visibility.Collapsed : Visibility.Visible);
    public static readonly IValueConverter NullToCollapsed = new Fn(v => v == null || (v is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible);

    /// <summary>Fracción 0–1 → ancho proporcional (barra de espacio ocupado de un disco).</summary>
    public static readonly IValueConverter Star = new Fn(v => new GridLength(Math.Max(v is double d ? d : 0, 0.0001), GridUnitType.Star));
    public static readonly IValueConverter StarRest = new Fn(v => new GridLength(Math.Max(1 - (v is double d ? d : 0), 0.0001), GridUnitType.Star));

    private sealed class Fn : IValueConverter
    {
        private readonly Func<object, object> _f;
        public Fn(Func<object, object> f) { _f = f; }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => _f(value);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
