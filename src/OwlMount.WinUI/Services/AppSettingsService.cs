using Microsoft.UI.Xaml;
using Windows.Storage;

namespace OwlMount.WinUI.Services;

public sealed class AppSettingsService
{
    private const string ThemeKey = "AppTheme";
    private ApplicationDataContainer? _localSettings;
    private bool _isLoaded;

    public ElementTheme Theme { get; private set; } = ElementTheme.Default;
    public event EventHandler<ElementTheme>? ThemeChanged;

    public void Load()
    {
        if (_isLoaded)
            return;

        try
        {
            _localSettings ??= ApplicationData.Current.LocalSettings;
        }
        catch
        {
            _localSettings = null;
            Theme = ElementTheme.Default;
            _isLoaded = true;
            return;
        }

        Theme = ReadTheme();
        _isLoaded = true;
    }

    public void SetTheme(ElementTheme theme)
    {
        Load();
        Theme = theme;

        _localSettings?.Values[ThemeKey] = ThemeToString(theme);

        ThemeChanged?.Invoke(this, theme);
    }

    private ElementTheme ReadTheme()
    {
        if (_localSettings is null)
            return ElementTheme.Default;

        ApplicationDataContainer localSettings = _localSettings;
        if (localSettings.Values.TryGetValue(ThemeKey, out object? raw) && raw is string text)
            return ThemeFromString(text);

        return ElementTheme.Default;
    }

    private static ElementTheme ThemeFromString(string text) =>
        text.Trim().ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            "default" => ElementTheme.Default,
            _ => ElementTheme.Default,
        };

    private static string ThemeToString(ElementTheme theme) =>
        theme switch
        {
            ElementTheme.Light => "Light",
            ElementTheme.Dark => "Dark",
            _ => "Default",
        };
}
