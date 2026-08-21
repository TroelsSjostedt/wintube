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

    public MainWindow()
    {
        InitializeComponent();
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

    private void OnSignOut(object sender, RoutedEventArgs e)
    {
        App.Session.SignOut();
        ProfileName.Text = "";
        RootFrame.Navigate(typeof(Views.LoginPage));
        RootFrame.BackStack.Clear();
        suppressSelectionNavigation = true;
        Nav.SelectedItem = HomeItem;
        suppressSelectionNavigation = false;
    }
}
