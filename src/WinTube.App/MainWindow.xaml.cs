using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinTube.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RootFrame.Navigate(typeof(Views.SpikePage));   // Task 14 replaces this with login/home
    }

    public Frame Frame => RootFrame;
}
