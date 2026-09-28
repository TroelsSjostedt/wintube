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
        // First Activated, not the constructor: WinUI 3's own child HWNDs (the content bridge
        // and its input site — see SubclassInputChildWindowsOnce) don't exist yet when the
        // constructor runs, only once the window's content has actually been realized.
        SubclassInputChildWindowsOnce();
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
    // separately recognizing a tap. Win32-level interception has to happen in two places:
    //  - the input child window WinUI 3 actually delivers WM_XBUTTON* to (SubclassInputChildWindowsOnce
    //    below) — this is the real fix, since eating it there stops WinUI's own recognizer from
    //    ever seeing the click;
    //  - this top-level window's own WndProc (SubclassWindowForMouseBackButton), kept as a
    //    fallback for WM_APPCOMMAND, which DefWindowProc can still synthesize and bubble up to
    //    the top level even when the child swallow below handles everything else.

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

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private delegate bool EnumChildWindowsProc(IntPtr hWnd, IntPtr lParam);

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
            // Believed unreachable in practice: pointer input, including WM_XBUTTON*, is
            // delivered to WinUI 3's own child HWNDs (see SubclassInputChildWindowsOnce), never
            // to this top-level one. Left in and swallowed the same way regardless, as a harmless
            // fallback in case a future WinUI version ever routes it here directly.
            case WM_XBUTTONDOWN or WM_XBUTTONUP or WM_XBUTTONDBLCLK:
                var xButton = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
                if (msg == WM_XBUTTONUP && xButton == XBUTTON1) RunOnUiThread(GoBackDebounced);
                return new IntPtr(1);

            // The real fallback: DefWindowProc synthesizes this from an unhandled WM_XBUTTON*, so
            // it only fires if the child-window swallow below doesn't catch the press first (or
            // for a driver that sends the app command directly, e.g. a keyboard's dedicated back
            // key). GoBackDebounced guards against both paths firing for one physical click.
            case WM_APPCOMMAND:
                var command = (int)((lParam.ToInt64() >> 16) & 0xFFF);
                if (command == APPCOMMAND_BROWSER_BACKWARD)
                {
                    RunOnUiThread(GoBackDebounced);
                    return new IntPtr(1);
                }
                break;
        }
        return CallWindowProc(originalWndProc, hWnd, msg, wParam, lParam);
    }

    private const string BridgeClassName = "Microsoft.UI.Content.DesktopChildSiteBridge";
    private const string InputSiteClassName = "InputSiteWindowClass";

    // One rooted delegate + original proc per subclassed input-child HWND (same GC-lifetime
    // reasoning as mouseBackWndProc above).
    private readonly Dictionary<IntPtr, (WndProc Proc, IntPtr Original)> subclassedInputWindows = new();
    private bool inputChildWindowsSubclassed;

    /// WinUI 3 hosts its content in child HWNDs of its own — a "Microsoft.UI.Content.
    /// DesktopChildSiteBridge" wrapping an inner "InputSiteWindowClass" — and pointer input,
    /// including WM_XBUTTON*, is delivered directly to those, never to the top-level window
    /// subclassed above (only the WM_APPCOMMAND DefWindowProc synthesizes from an unhandled one
    /// bubbles up that far). So the actual swallow has to happen down there. Which of the two
    /// class names is the one that actually receives the message isn't documented, so — per the
    /// coordinator's "if in doubt, subclass both" — every match of either gets subclassed.
    /// EnumChildWindows already walks the full descendant tree, not just direct children, so one
    /// call from the main HWND finds both levels.
    private void SubclassInputChildWindowsOnce()
    {
        if (inputChildWindowsSubclassed) return;
        inputChildWindowsSubclassed = true;

        var mainHwnd = WindowNative.GetWindowHandle(this);
        var matches = new List<IntPtr>();
        var classNameBuffer = new System.Text.StringBuilder(256);
        EnumChildWindows(mainHwnd, (hWnd, _) =>
        {
            classNameBuffer.Clear();
            GetClassName(hWnd, classNameBuffer, classNameBuffer.Capacity);
            var className = classNameBuffer.ToString();
            if (className is BridgeClassName or InputSiteClassName) matches.Add(hWnd);
            return true; // keep enumerating — there can be more than one match (multiple pages/popups).
        }, IntPtr.Zero);

        foreach (var hWnd in matches)
        {
            WndProc proc = InputChildWndProc;
            var original = SetWindowLongPtr(hWnd, GWLP_WNDPROC, proc);
            subclassedInputWindows[hWnd] = (proc, original);
        }
    }

    private IntPtr InputChildWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg is WM_XBUTTONDOWN or WM_XBUTTONUP or WM_XBUTTONDBLCLK)
        {
            // Swallowed unconditionally for both buttons, all three variants — this is the fix:
            // returning without forwarding to the original proc keeps WinUI's gesture recognizer
            // from ever seeing the click, so a card under the pointer can't activate. XBUTTON1
            // navigates on the up-click; XBUTTON2 is swallowed too but never navigates.
            var xButton = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
            if (msg == WM_XBUTTONUP && xButton == XBUTTON1) RunOnUiThread(GoBackDebounced);
            return new IntPtr(1);
        }
        var original = subclassedInputWindows[hWnd].Original;
        return CallWindowProc(original, hWnd, msg, wParam, lParam);
    }

    /// Debounced rather than a plain GoBackIfPossible: swallowing XBUTTONUP in the input child
    /// window should stop DefWindowProc from ever synthesizing the WM_APPCOMMAND the top-level
    /// fallback above reacts to, for one physical click — but that internal chain isn't
    /// documented, so this guards against both paths firing for the same press.
    private DateTime lastMouseBackAt = DateTime.MinValue;

    private void GoBackDebounced()
    {
        var now = DateTime.UtcNow;
        if (now - lastMouseBackAt < TimeSpan.FromMilliseconds(300)) return;
        lastMouseBackAt = now;
        if (RootFrame.CanGoBack) RootFrame.GoBack();
    }

    /// WndProc for the top-level window runs on the UI thread; the input child window's may run
    /// on an input thread instead, so this dispatch is required there and merely defensive here
    /// — either way it's safe to route every mouse-back trigger through the same helper.
    private void RunOnUiThread(Action action)
    {
        if (dispatcher.HasThreadAccess) action();
        else dispatcher.TryEnqueue(() => action());
    }
}
