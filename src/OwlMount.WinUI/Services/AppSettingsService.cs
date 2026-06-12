using Microsoft.UI.Xaml;
using Windows.Storage;

namespace OwlMount.WinUI.Services;

public sealed class AppSettingsService
{
    private const string ThemeKey = "AppTheme";
    private const string DefaultBlockCacheSizeKey = "DefaultBlockCacheSize";
    private const string EnableBlockCacheKey = "EnableBlockCache";
    private const string DefaultProviderKey = "DefaultProvider";
    private const string DefaultBackendKey = "DefaultBackend";
    private const long DefaultBlockCacheSize = 256 * 1024; // 256 KiB default
    private readonly Dictionary<string, object> _defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        [ThemeKey] = ElementTheme.Default,
        [DefaultBlockCacheSizeKey] = DefaultBlockCacheSize,
        [EnableBlockCacheKey] = true,
        [DefaultProviderKey] = "memory",
        [DefaultBackendKey] = "winfsp",
    };
    private ApplicationDataContainer? _localSettings;
    private bool _isLoaded;

    public event EventHandler<AppSettingChangedEventArgs>? SettingChanged;

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
            _isLoaded = true;
            return;
        }

        _isLoaded = true;
    }

    public void SetSetting<T>(string key, T value)
    {
        Load();

        object stored = NormalizeValue(key, value);
        if (_localSettings is not null)
            _localSettings.Values[key] = stored;

        SettingChanged?.Invoke(this, new AppSettingChangedEventArgs(key, stored));
    }

    public T GetSetting<T>(string key)
    {
        Load();

        if (_localSettings is not null && _localSettings.Values.TryGetValue(key, out object? raw))
        {
            if (raw is T typed)
                return typed;

            object? converted = ConvertSetting(raw, typeof(T));
            if (converted is T convertedTyped)
                return convertedTyped;
        }

        if (_defaults.TryGetValue(key, out object? defaultValue) && defaultValue is T defaultTyped)
            return defaultTyped;

        if (key.Equals(DefaultProviderKey, StringComparison.OrdinalIgnoreCase))
            return (T)(object)"memory";
        if (key.Equals(DefaultBackendKey, StringComparison.OrdinalIgnoreCase))
            return (T)(object)"winfsp";
        if (key.Equals(ThemeKey, StringComparison.OrdinalIgnoreCase))
            return (T)(object)ElementTheme.Default;
        if (key.Equals(DefaultBlockCacheSizeKey, StringComparison.OrdinalIgnoreCase))
            return (T)(object)DefaultBlockCacheSize;
        if (key.Equals(EnableBlockCacheKey, StringComparison.OrdinalIgnoreCase))
            return (T)(object)true;

        return default!;
    }

    private static object NormalizeValue<T>(string key, T value)
    {
        if (key.Equals(ThemeKey, StringComparison.OrdinalIgnoreCase) && value is ElementTheme theme)
            return theme.ToString();

        if (key.Equals(DefaultProviderKey, StringComparison.OrdinalIgnoreCase) ||
            key.Equals(DefaultBackendKey, StringComparison.OrdinalIgnoreCase))
        {
            return (value?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
        }

        if (key.Equals(DefaultBlockCacheSizeKey, StringComparison.OrdinalIgnoreCase))
        {
            if (value is long longValue) return longValue;
            if (value is int intValue) return (long)intValue;
        }

        return value is null ? (object)string.Empty : value;
    }

    private static object? ConvertSetting(object raw, Type targetType)
    {
        if (targetType == typeof(string))
            return raw.ToString();

        if (targetType == typeof(long))
        {
            if (raw is long longValue) return longValue;
            if (raw is int intValue) return (long)intValue;
            if (long.TryParse(raw.ToString(), out long parsedLong)) return parsedLong;
        }

        if (targetType == typeof(bool))
        {
            if (raw is bool boolValue) return boolValue;
            if (bool.TryParse(raw.ToString(), out bool parsedBool)) return parsedBool;
        }

        if (targetType == typeof(ElementTheme))
        {
            if (raw is string text)
                return ThemeFromString(text);
        }

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
}

public sealed class AppSettingChangedEventArgs(string key, object? value) : EventArgs
{
    public string Key { get; } = key;
    public object? Value { get; } = value;
}
