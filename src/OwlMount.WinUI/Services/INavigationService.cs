using Microsoft.UI.Xaml.Controls;

namespace OwlMount.WinUI.Services;

public interface INavigationService
{
    event EventHandler<bool>? BackButtonVisibilityChanged;

    void Attach(Frame frame);
    void ShowHomePage();
    void ShowSettingsPage();
    void GoBack();
    bool IsShowingSettingsPage();
}
