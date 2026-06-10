using Microsoft.UI.Xaml;
using Windows.Storage;

namespace OwlMount.WinUI.Services;

public sealed class AppSettingsService
{
    private const string ThemeKey = "AppTheme";
    private const string DefaultBlockCacheSizeKey = "DefaultBlockCacheSize";
    private const string EnableBlockCacheKey = "EnableBlockCache";
    private const string DokanyPathKey = "DokanyPath";
    private const string WinFspPathKey = "WinFspPath";
    private const long DefaultBlockCacheSize = 256 * 1024; // 256 KiB default
    private ApplicationDataContainer? _localSettings;
    private bool _isLoaded;

    public ElementTheme Theme { get; private set; } = ElementTheme.Default;
    public long DefaultBlockCacheSizeBytes { get; private set; } = DefaultBlockCacheSize;
    public bool EnableBlockCache { get; private set; } = true;
    public string? DokanyPath { get; private set; }
    public string? WinFspPath { get; private set; }

    public event EventHandler<ElementTheme>? ThemeChanged;
    public event EventHandler<long>? DefaultBlockCacheSizeChanged;
    public event EventHandler<bool>? EnableBlockCacheChanged;

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
            DefaultBlockCacheSizeBytes = DefaultBlockCacheSize;
            EnableBlockCache = true;
            _isLoaded = true;
            return;
        }

        Theme = ReadTheme();
        DefaultBlockCacheSizeBytes = ReadBlockCacheSize();
        EnableBlockCache = ReadEnableBlockCache();
        DokanyPath = ReadStringValue(DokanyPathKey);
        WinFspPath = ReadStringValue(WinFspPathKey);
        _isLoaded = true;
    }

    public void SetTheme(ElementTheme theme)
    {
        Load();
        Theme = theme;

        _localSettings?.Values[ThemeKey] = ThemeToString(theme);

        ThemeChanged?.Invoke(this, theme);
    }

    public void SetDefaultBlockCacheSize(long sizeBytes)
    {
        Load();
        DefaultBlockCacheSizeBytes = sizeBytes;

        _localSettings?.Values[DefaultBlockCacheSizeKey] = sizeBytes;

        DefaultBlockCacheSizeChanged?.Invoke(this, sizeBytes);
    }

    public void SetEnableBlockCache(bool enabled)
    {
        Load();
        EnableBlockCache = enabled;

        _localSettings?.Values[EnableBlockCacheKey] = enabled;

        EnableBlockCacheChanged?.Invoke(this, enabled);
    }

    public void SetDokanyPath(string? path)
    {
        Load();
        DokanyPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();

        if (DokanyPath is not null)
            _localSettings?.Values[DokanyPathKey] = DokanyPath;
        else
            _localSettings?.Values.Remove(DokanyPathKey);
    }

    public void SetWinFspPath(string? path)
    {
        Load();
        WinFspPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();

        if (WinFspPath is not null)
            _localSettings?.Values[WinFspPathKey] = WinFspPath;
        else
            _localSettings?.Values.Remove(WinFspPathKey);
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

    private long ReadBlockCacheSize()
    {
        if (_localSettings is null)
            return DefaultBlockCacheSize;

        ApplicationDataContainer localSettings = _localSettings;
        if (localSettings.Values.TryGetValue(DefaultBlockCacheSizeKey, out object? raw))
        {
            if (raw is long longValue)
                return longValue > 0 ? longValue : DefaultBlockCacheSize;
            if (raw is int intValue)
                return intValue > 0 ? intValue : DefaultBlockCacheSize;
        }

        return DefaultBlockCacheSize;
    }

    private bool ReadEnableBlockCache()
    {
        if (_localSettings is null)
            return true;

        ApplicationDataContainer localSettings = _localSettings;
        if (localSettings.Values.TryGetValue(EnableBlockCacheKey, out object? raw))
        {
            if (raw is bool boolValue)
                return boolValue;
        }

        return true;
    }

    private string? ReadStringValue(string key)
    {
        if (_localSettings is null)
            return null;

        if (_localSettings.Values.TryGetValue(key, out object? raw) && raw is string text
            && !string.IsNullOrWhiteSpace(text))
            return text;

        return null;
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
