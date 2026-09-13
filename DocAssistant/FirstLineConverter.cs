using System.Globalization;
using System.Windows.Data;

namespace DocAssistant;

public sealed class FirstLineConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string ?? "").Split(['\r', '\n'], 2)[0];
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
