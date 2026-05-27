using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OwlMount.WinUI.Services;
using OwlMount.WinUI.Views;
using Microsoft.UI.Windowing;

namespace OwlMount.WinUI;

public sealed partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }
    public SettingsPageViewModel SettingsViewModel { get; }
    public AppSettingsService AppSettings { get; }
    private INavigationService Navigation { get; }

    public MainWindow()
    {
        var app = (App)Application.Current;
        AppSettings = app.AppSettings;
        Navigation = app.Services.GetRequiredService<INavigationService>();
        ViewModel = app.Services.GetRequiredService<MainWindowViewModel>();
        SettingsViewModel = app.Services.GetRequiredService<SettingsPageViewModel>();
        InitializeComponent();
        RootGrid.RequestedTheme = AppSettings.Theme;
        AppSettings.ThemeChanged += OnThemeChanged;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 980;
            presenter.PreferredMinimumHeight = 720;
        }
        Navigation.BackButtonVisibilityChanged += Navigation_BackButtonVisibilityChanged;
        Navigation.Attach(ContentFrame);
        Navigation.ShowHomePage();

        Activated += OnFirstActivated;
    }

    private bool _overlaySetup;

    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_overlaySetup) return;
        _overlaySetup = true;
        SetupOverlayAnimation();
    }

    private void SetupOverlayAnimation()
    {
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectionActionsVisibility))
                DispatcherQueue.TryEnqueue(() => AnimateOverlay(ViewModel.SelectionActionsVisibility));
        };
    }

    private void AnimateOverlay(Visibility target)
    {
        var transform = (TranslateTransform)SelectionOverlay.RenderTransform;

        if (target == Visibility.Visible)
        {
            SelectionOverlay.Visibility = Visibility.Visible;
            SelectionOverlay.IsHitTestVisible = true;

            var sb = new Storyboard();
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(fade, SelectionOverlay);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);
            var move = new DoubleAnimation { From = 8, To = 0, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(move, transform);
            Storyboard.SetTargetProperty(move, "Y");
            sb.Children.Add(move);
            sb.Begin();
        }
        else
        {
            SelectionOverlay.IsHitTestVisible = false;

            var sb = new Storyboard();
            var fade = new DoubleAnimation { From = SelectionOverlay.Opacity, To = 0, Duration = TimeSpan.FromMilliseconds(160), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(fade, SelectionOverlay);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);
            var move = new DoubleAnimation { From = transform.Y, To = 8, Duration = TimeSpan.FromMilliseconds(160), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(move, transform);
            Storyboard.SetTargetProperty(move, "Y");
            sb.Children.Add(move);
            sb.Completed += (_, _) => SelectionOverlay.Visibility = Visibility.Collapsed;
            sb.Begin();
        }
    }

    public void RefreshMountsFromService() => ViewModel.RefreshMountsFromService();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Navigation.ShowSettingsPage();
    }

    public void SetExternalStatus(string message) => ViewModel.SetStatus(message);

    private void AppTitleBar_BackButtonClick(object sender, RoutedEventArgs e)
    {
        Navigation.GoBack();
    }

    private void OnThemeChanged(object? sender, ElementTheme theme)
    {
        RootGrid.RequestedTheme = theme;
    }

    private void Navigation_BackButtonVisibilityChanged(object? sender, bool canGoBack)
    {
        AppTitleBar.IsBackButtonVisible = canGoBack;
        SettingsButton.Visibility = Navigation.IsShowingSettingsPage() ? Visibility.Collapsed : Visibility.Visible;
    }
}
