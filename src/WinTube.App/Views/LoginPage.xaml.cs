using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinTube.Core.Auth;

namespace WinTube.App.Views;

/// Device-flow sign-in: request a code, show it, poll until the user authorizes it in a
/// browser. Desktop's equivalent of the tvOS QR-code screen — opening the browser directly
/// on the machine the user is already using is strictly better than a QR code.
public sealed partial class LoginPage : Page
{
    private DeviceCode? code;

    public LoginPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // Retry path for a failed request/poll: tapping the error message reruns the flow.
        Status.Tapped += async (_, _) => await RunAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RunAsync();

    private async Task RunAsync()
    {
        try
        {
            await RequestAndPollAsync();
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
        }
    }

    private async Task RequestAndPollAsync()
    {
        Status.Text = "";
        OpenBrowser.IsEnabled = false;
        CopyCode.IsEnabled = false;
        CodeText.Text = "";
        Instructions.Text = "A code is being requested…";
        code = null;

        var requested = await App.Session.DeviceAuth.RequestCodeAsync();
        code = requested;

        CodeText.Text = requested.UserCode;
        Instructions.Text = $"Go to {requested.VerificationUrl} and enter the code below.";
        OpenBrowser.IsEnabled = true;
        CopyCode.IsEnabled = true;

        var tokens = await App.Session.DeviceAuth.PollAsync(requested.Code, requested.IntervalSeconds);

        await App.Session.CompleteSignInAsync(tokens);
        App.Window?.OnSignedIn();
        Frame.Navigate(typeof(HomePage));
    }

    private async void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        try
        {
            if (code is { } c) await Launcher.LaunchUriAsync(new Uri(c.VerificationUrl));
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
        }
    }

    private void OnCopyCode(object sender, RoutedEventArgs e)
    {
        try
        {
            if (code is not { } c) return;
            var package = new DataPackage();
            package.SetText(c.UserCode);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
        }
    }
}
