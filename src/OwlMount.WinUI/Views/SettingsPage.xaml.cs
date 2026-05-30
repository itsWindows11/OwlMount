using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace OwlMount.WinUI.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; }

    public SettingsPage()
    {
        var app = (App)Microsoft.UI.Xaml.Application.Current;
        ViewModel = app.Services.GetRequiredService<SettingsPageViewModel>();
        ViewModel.SetWindowProvider(app.Services.GetRequiredService<MainWindow>);
        InitializeComponent();
    }

    private void BlockCacheSizeComboBox_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Initialize the ComboBox with the current value
        SelectBlockCacheSizeItem(ViewModel.DefaultBlockCacheSizeBytes);
    }

    private void SelectBlockCacheSizeItem(long sizeBytes)
    {
        foreach (var item in BlockCacheSizeComboBox.Items)
        {
            if (item is ComboBoxItem comboItem && comboItem.Tag is string tagStr)
            {
                if (long.TryParse(tagStr, out long itemSize) && itemSize == sizeBytes)
                {
                    BlockCacheSizeComboBox.SelectedItem = item;
                    return;
                }
            }
        }
        // Default to "256 KiB (default)" (third item, index 2)
        if (BlockCacheSizeComboBox.Items.Count > 2)
            BlockCacheSizeComboBox.SelectedIndex = 2;
    }

    private void BlockCacheSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item && item.Tag is string tagStr)
        {
            if (long.TryParse(tagStr, out long sizeBytes) && sizeBytes > 0)
            {
                ViewModel.DefaultBlockCacheSizeBytes = sizeBytes;
            }
        }
    }
}
