using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SoundVault.App.Converters;

/// <summary>double（像素）→ GridLength，用于列表视图名称列的宽度绑定。</summary>
public sealed class PxToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double px = value switch
        {
            double d => d,
            int i => i,
            _ => 220
        };
        return new GridLength(Math.Clamp(px, 90, 620));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is GridLength g ? g.Value : 220d;
}
