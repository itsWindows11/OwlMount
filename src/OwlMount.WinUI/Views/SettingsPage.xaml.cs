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
}
