using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

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
        SubclassWindowForMouseBackButton();
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

    // MARK: mouse back button (Win32)
    //
    // A PointerPressed handler (even with handledEventsToo) can't stop this: WinUI's gesture
    // recognizer (Tapped/ItemClick, which is what actually activates a home-page card) is driven
    // off the platform's own pointer/gesture pipeline, generated independently of the Handled
    // flag on the routed PointerPressed event — marking it Handled there doesn't stop XAML from
    // separately recognizing a tap. So instead this subclasses the window's WndProc and eats the
    // side-button messages at the Win32 level, before Windows ever turns them into pointer input
    // XAML can see.

    private const int GWLP_WNDPROC = -4;
    private const uint WM_XBUTTONDOWN = 0x020B;
    private const uint WM_XBUTTONUP = 0x020C;
    private const uint WM_XBUTTONDBLCLK = 0x020D;
    private const uint WM_APPCOMMAND = 0x0319;
    private const int XBUTTON1 = 1;
    private const int APPCOMMAND_BROWSER_BACKWARD = 1;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Kept rooted for the process lifetime: SetWindowLongPtr hands Windows a native function
    // pointer into this delegate's marshaled thunk, and nothing else holds a managed reference
    // to it — without this field the GC could collect it and leave the window pointing at freed
    // memory the next time a message arrives.
    private WndProc? mouseBackWndProc;
    private IntPtr originalWndProc;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProc newProc);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr prevWndProc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void SubclassWindowForMouseBackButton()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        mouseBackWndProc = MouseBackWndProc;
        originalWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, mouseBackWndProc);
    }

    private IntPtr MouseBackWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            // Both buttons, all three variants (down/up/dblclk) are swallowed unconditionally —
            // returning without calling the original proc stops DefWindowProc from ever
            // synthesizing a WM_APPCOMMAND out of the press, and since XAML's input pipeline sits
            // downstream of that same proc chain, it never sees the click at all. XBUTTON1 (back)
            // navigates on the up-click, matching a normal button press; XBUTTON2 (forward) is
            // swallowed too (so it can't fall through to a card either) but never navigates.
            case WM_XBUTTONDOWN or WM_XBUTTONUP or WM_XBUTTONDBLCLK:
                var xButton = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
                if (msg == WM_XBUTTONUP && xButton == XBUTTON1) RunOnUiThread(GoBackIfPossible);
                return new IntPtr(1);

            // Some mice/drivers send the browser-back app command directly instead of (or as
            // well as) WM_XBUTTON*; handled the same way for keyboards with dedicated back keys.
            case WM_APPCOMMAND:
                var command = (int)((lParam.ToInt64() >> 16) & 0xFFF);
                if (command == APPCOMMAND_BROWSER_BACKWARD)
                {
                    RunOnUiThread(GoBackIfPossible);
                    return new IntPtr(1);
                }
                break;
        }
        return CallWindowProc(originalWndProc, hWnd, msg, wParam, lParam);
    }

    private void GoBackIfPossible()
    {
        if (RootFrame.CanGoBack) RootFrame.GoBack();
    }

    /// WndProc runs on the UI thread for this window, so HasThreadAccess is always true in
    /// practice — the dispatch is defensive, matching the same safe pattern OnSessionSignedOut
    /// uses below for a callback that genuinely can arrive off-thread.
    private void RunOnUiThread(Action action)
    {
        if (dispatcher.HasThreadAccess) action();
        else dispatcher.TryEnqueue(() => action());
    }
}
