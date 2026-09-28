using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace WinTube.App;

/// The window shell: a NavigationView whose pane routes between Home/Search/History and whose
/// footer shows the signed-in profile with a sign-out flyout. Not signed in lands on LoginPage
/// in the same content Frame; LoginPage calls back into OnSignedIn() once it completes.
public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

    /// Throttles the Activated-triggered sync so rapid focus churn doesn't hammer the backend.
    private DateTimeOffset lastSyncTrigger;

    private readonly UpdateService updates = new();

    /// Captured only when fullscreen is entered from a maximized window, so exiting restores
    /// maximized rather than dropping to the presenter's own default (normal/restored) size.
    private bool wasMaximizedBeforeFullScreen;

    public MainWindow()
    {
        InitializeComponent();
        // The exe icon covers Explorer; the window object needs its own for taskbar/title bar.
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "wintube.ico"));
        // The pane's own back arrow is THE back button everywhere; it lights up whenever the
        // frame has somewhere to go back to (player, channel chains, subscriptions drill-ins).
        RootFrame.Navigated += (_, _) => Nav.IsBackEnabled = RootFrame.CanGoBack;
        // Mouse back button (XButton1), app-wide: handledEventsToo so it still fires over the
        // player's own handlers (Tapped/DoubleTapped on PlayerSlot, not PointerPressed, don't
        // mark it handled anyway). MpvPlayerHost renders into a child SwapChainPanel rather than
        // a separate HWND, so pointer input stays in the XAML tree and bubbles here normally.
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnBackPointerPressed), true);
        App.Session.SignedOut += OnSessionSignedOut;
        Activated += OnActivated;
        Closed += OnClosed;
        ProfileName.Text = App.Session.Profile?.Name ?? "";
        if (App.Session.IsSignedIn)
        {
            RootFrame.Navigate(typeof(Views.HomePage));
            Nav.SelectedItem = HomeItem;
        }
        else
        {
            RootFrame.Navigate(typeof(Views.LoginPage));
        }
        installedVersion = updates.InstalledVersion();
        VersionLink.Text = installedVersion is null ? "dev" : $"v{installedVersion}";
        CheckForUpdatesAsync();
    }

    // MARK: version link

    /// Null on a dev run; the link then points at the latest release instead of a tag.
    private readonly string? installedVersion;

    private void OnVersionEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        VersionLink.TextDecorations = Windows.UI.Text.TextDecorations.Underline;

    private void OnVersionExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        VersionLink.TextDecorations = Windows.UI.Text.TextDecorations.None;

    private async void OnVersionTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri(UpdateService.ReleaseUrl(installedVersion)));

    /// Fire-and-forget: a dev run or any failure is a silent no-op (see UpdateService), so
    /// there's nothing to await or report on here.
    private async void CheckForUpdatesAsync()
    {
        var version = await updates.CheckAsync();
        if (version is null) return;
        UpdateBar.Message = $"Update ready — restart for v{version}";
        UpdateBar.IsOpen = true;
    }

    private void OnRestartForUpdate(object sender, RoutedEventArgs e) => updates.ApplyAndRestart();

    public Frame Frame => RootFrame;

    /// Fullscreen video: the window presenter flips and the shell chrome (pane, back arrow)
    /// collapses so the player page is the only thing on screen. Restoring captures whether the
    /// window was maximized going in, so exiting lands back on maximized instead of always
    /// dropping to a normal-sized window.
    public void SetPlayerFullScreen(bool on)
    {
        if (on)
        {
            wasMaximizedBeforeFullScreen = AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
                { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized };
            AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
        }
        else
        {
            AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
            if (wasMaximizedBeforeFullScreen && AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter restored)
                restored.Maximize();
        }

        Nav.IsPaneVisible = !on;
        Nav.IsBackButtonVisible = on ? NavigationViewBackButtonVisible.Collapsed
                                     : NavigationViewBackButtonVisible.Visible;
    }

    /// Entry point for a wintube:// activation or a command-line argument: signed-out is a
    /// no-op (LoginPage is already showing), otherwise fronts the window and navigates
    /// straight to the player with a placeholder VideoItem — the player fills in the rest.
    public void OpenVideo(string videoId, TimeSpan? startAt)
    {
        if (!App.Session.IsSignedIn) return;
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        this.Activate();
        RootFrame.Navigate(typeof(Views.PlayerPage), new PlayerRequest(
            new Core.Models.VideoItem
            {
                Id = videoId,
                Title = "",
                ThumbnailUrl = Core.Models.VideoItem.FallbackThumbnail(videoId),
            },
            startAt));
    }

    /// Called by LoginPage right after CompleteSignInAsync, before it navigates the frame to
    /// HomePage itself — refreshes the footer and resets the pane highlight to Home.
    public void OnSignedIn()
    {
        ProfileName.Text = App.Session.Profile?.Name ?? "";
        Nav.SelectedItem = HomeItem;
    }

    /// ItemInvoked rather than SelectionChanged: it fires on EVERY click, including on the
    /// already-selected item — so clicking "Home" from deep inside a channel or player under
    /// Home still lands on the section's root. A nav click is always "take me to the root",
    /// so the back stack is cleared too.
    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        var tag = (args.InvokedItemContainer as NavigationViewItem)?.Tag as string;
        var target = tag switch
        {
            "Search" => typeof(Views.SearchPage),
            "Subscriptions" => typeof(Views.SubscriptionsPage),
            "History" => typeof(Views.HistoryPage),
            _ => typeof(Views.HomePage),
        };
        if (RootFrame.SourcePageType != target) RootFrame.Navigate(target);
        RootFrame.BackStack.Clear();
        Nav.IsBackEnabled = false;
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (RootFrame.CanGoBack) RootFrame.GoBack();
    }

    /// No forward equivalent — matches the pane's own back arrow, which has none either.
    private void OnBackAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Always handled, even with nowhere to go back to, so Alt+Left never leaks through as
        // a plain Left and hits PlayerPage's -10s seek accelerator.
        args.Handled = true;
        if (RootFrame.CanGoBack) RootFrame.GoBack();
    }

    private void OnBackPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(RootGrid).Properties;
        if (!props.IsXButton1Pressed && !props.IsXButton2Pressed) return;
        // Swallow both side buttons unconditionally, even with nowhere to go back to —
        // otherwise an unhandled press on Home (CanGoBack false) falls through to whatever's
        // underneath and activates a card. XButton2 (forward) is swallowed too so it can't
        // activate a card either, but it never navigates — there's no forward stack to go to.
        e.Handled = true;
        if (props.IsXButton1Pressed && RootFrame.CanGoBack) RootFrame.GoBack();
    }

    private void OnSignOut(object sender, RoutedEventArgs e) => App.Session.SignOut();

    /// Coming back into focus is a good moment to pull in anything synced from another
    /// device — throttled so switching apps back and forth doesn't hammer the backend.
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated) return;
        var now = DateTimeOffset.UtcNow;
        if (now - lastSyncTrigger <= TimeSpan.FromSeconds(60)) return;
        lastSyncTrigger = now;
        App.Session.ProgressSync?.Sync();
    }

    private void OnClosed(object sender, WindowEventArgs e) => App.Session.ProgressSync?.FlushNow();

    /// Session.SignOut() raises this both for the manual sign-out above and for RunAsync
    /// giving up on a permanently failed refresh (which can happen from an async
    /// continuation) — either way, land back on LoginPage and reset the shell's chrome.
    private void OnSessionSignedOut()
    {
        if (dispatcher.HasThreadAccess) NavigateToLogin();
        else dispatcher.TryEnqueue(NavigateToLogin);
    }

    private void NavigateToLogin()
    {
        ProfileName.Text = "";
        if (RootFrame.SourcePageType != typeof(Views.LoginPage))
            RootFrame.Navigate(typeof(Views.LoginPage));
        RootFrame.BackStack.Clear();
        Nav.IsBackEnabled = false;
        Nav.SelectedItem = HomeItem;
    }
}
