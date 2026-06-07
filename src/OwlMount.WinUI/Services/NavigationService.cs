using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using OwlMount.WinUI.Views;

namespace OwlMount.WinUI.Services;

public sealed class NavigationService : INavigationService
{
    private Frame? _frame;

    public event EventHandler<bool>? BackButtonVisibilityChanged;

    public void Attach(Frame frame)
    {
        if (_frame is not null)
            _frame.Navigated -= Frame_Navigated;

        _frame = frame;
        _frame.Navigated += Frame_Navigated;
    }

    public void ShowHomePage() => Navigate(typeof(HomePage));

    public void ShowSettingsPage()
    {
        if (IsShowingSettingsPage())
            return;

        Navigate(typeof(SettingsPage));
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
            _frame.GoBack();
    }

    public bool IsShowingSettingsPage() => _frame?.Content is SettingsPage;

    private void Navigate(Type pageType)
    {
        if (_frame is null)
            return;

        if (_frame.Content?.GetType() == pageType)
            return;

        NavigationTransitionInfo transition = new EntranceNavigationTransitionInfo();
        _frame.Navigate(pageType, null, transition);
    }

    private void Frame_Navigated(object sender, NavigationEventArgs e)
        => BackButtonVisibilityChanged?.Invoke(this, _frame?.CanGoBack == true);
}
