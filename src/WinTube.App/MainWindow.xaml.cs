using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinTube.App;

/// The window shell: a NavigationView whose pane routes between Home/Search/History and whose
/// footer shows the signed-in profile with a sign-out flyout. Not signed in lands on LoginPage
/// in the same content Frame; LoginPage calls back into OnSignedIn() once it completes.
public sealed partial class MainWindow : Window
{
    /// Guards NavigationView.SelectedItem assignments that are just resyncing the pane's
    /// highlight (sign-in, sign-out) from also triggering OnSelectionChanged's navigation.
    private bool suppressSelectionNavigation;

    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

    /// Throttles the Activated-triggered sync so rapid focus churn doesn't hammer the backend.
    private DateTimeOffset lastSyncTrigger;

    public MainWindow()
    {
        InitializeComponent();
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
    }

    public Frame Frame => RootFrame;

    /// Called by LoginPage right after CompleteSignInAsync, before it navigates the frame to
    /// HomePage itself — refreshes the footer and resets the pane highlight to Home.
    public void OnSignedIn()
    {
        ProfileName.Text = App.Session.Profile?.Name ?? "";
        suppressSelectionNavigation = true;
        Nav.SelectedItem = HomeItem;
        suppressSelectionNavigation = false;
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (suppressSelectionNavigation) return;
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string;
        var target = tag switch
        {
            "Search" => typeof(Views.SearchPage),
            "History" => typeof(Views.HistoryPage),
            _ => typeof(Views.HomePage),
        };
        if (RootFrame.SourcePageType != target) RootFrame.Navigate(target);
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
        suppressSelectionNavigation = true;
        Nav.SelectedItem = HomeItem;
        suppressSelectionNavigation = false;
    }
}
