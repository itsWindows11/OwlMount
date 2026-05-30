using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml;

namespace OwlMount.WinUI.Converters;

public sealed class ElementThemeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            ElementTheme.Default => "System default",
            ElementTheme.Light => "Light",
            ElementTheme.Dark => "Dark",
            _ => "System default",
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            string s when s == "Light" => ElementTheme.Light,
            string s when s == "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
}
