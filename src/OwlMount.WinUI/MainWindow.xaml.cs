using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OwlMount.WinUI.Services;
using OwlMount.WinUI.Views;
using Microsoft.UI.Windowing;
using Windows.Graphics;

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
            presenter.PreferredMinimumWidth = 700;
            presenter.PreferredMinimumHeight = 500;
        }
        Navigation.BackButtonVisibilityChanged += Navigation_BackButtonVisibilityChanged;
        Navigation.Attach(ContentFrame);
        Navigation.ShowHomePage();

        AppTitleBar.Loaded += AppTitleBar_Loaded;
        Activated += OnFirstActivated;
    }

    private void AppTitleBar_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateTitleBarDragRects();
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarDragRects();
        SettingsButton.SizeChanged += (_, _) => UpdateTitleBarDragRects();

        // Update drag rects when back button visibility changes
        AppTitleBar.RegisterPropertyChangedCallback(
            CommunityToolkit.WinUI.Controls.TitleBar.IsBackButtonVisibleProperty,
            (_, _) => UpdateTitleBarDragRects());
    }

    private void UpdateTitleBarDragRects()
    {
        if (!AppTitleBar.IsLoaded) return;

        double scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        double titleBarHeight = AppTitleBar.ActualHeight;

        // Full title bar = caption (draggable).
        var captionRect = new RectInt32(
            0, 0,
            (int)(AppTitleBar.ActualWidth * scale),
            (int)(titleBarHeight * scale));

        // Collect passthrough rects for every interactive element in the title bar.
        var passthroughRects = new System.Collections.Generic.List<RectInt32>();
        foreach (var element in GetTitleBarInteractiveElements())
        {
            var bounds = element.TransformToVisual(AppTitleBar)
                                .TransformBounds(new Windows.Foundation.Rect(0, 0,
                                    element.ActualWidth, element.ActualHeight));
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            passthroughRects.Add(new RectInt32(
                (int)(bounds.X      * scale),
                (int)(bounds.Y      * scale),
                (int)(bounds.Width  * scale),
                (int)(bounds.Height * scale)));
        }

        var nonClientSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        nonClientSource.SetRegionRects(NonClientRegionKind.Caption,     [captionRect]);
        nonClientSource.SetRegionRects(NonClientRegionKind.Passthrough, [.. passthroughRects]);
    }

    /// <summary>
    /// Returns every interactive element inside the TitleBar that should not trigger window drag:
    /// the settings button (in the Footer) and the back button (found by name in the template).
    /// </summary>
    private IEnumerable<FrameworkElement> GetTitleBarInteractiveElements()
    {
        // Settings button is directly named in our XAML.
        if (SettingsButton.Visibility == Visibility.Visible)
            yield return SettingsButton;

        // Back button is inside the CommunityToolkit TitleBar template — find it by its PART_ name.
        if (AppTitleBar.IsBackButtonVisible &&
            FindDescendantByName(AppTitleBar, "PART_BackButton") is FrameworkElement backBtn)
            yield return backBtn;
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject parent, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Name == name)
                return fe;
            var result = FindDescendantByName(child, name);
            if (result is not null) return result;
        }
        return null;
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
        // Force layout update before recalculating drag rects
        SettingsButton.UpdateLayout();
        UpdateTitleBarDragRects();
    }
}
