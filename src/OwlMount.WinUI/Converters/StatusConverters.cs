using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace OwlMount.WinUI;

public sealed class BoolToStatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isEnabled = value is true;
        // Green for running, Red for disabled
        return isEnabled
            ? new SolidColorBrush(Microsoft.UI.Colors.LimeGreen)
            : new SolidColorBrush(Microsoft.UI.Colors.Crimson);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public sealed class BoolToStatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isEnabled = value is true;
        return isEnabled ? "Running" : "Disabled";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
