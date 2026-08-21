using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinTube.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Interim routing until Task 15 adds HomePage: signed-in still lands on SpikePage.
        RootFrame.Navigate(App.Session.IsSignedIn ? typeof(Views.SpikePage) : typeof(Views.LoginPage));
    }

    public Frame Frame => RootFrame;
}
