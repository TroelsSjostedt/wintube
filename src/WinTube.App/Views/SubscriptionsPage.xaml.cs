using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.ObjectModel;
using WinTube.Core.Models;

namespace WinTube.App.Views;

/// The Subscriptions grid: a wrapping GridView of followed channels, in the account's own
/// order (no local sorting). Reloads on every navigation to the page - no cache, mirroring
/// how the FEchannels response can change between visits. Never throws past its own boundary
/// - a load failure surfaces as an inline InfoBar with Retry.
public sealed partial class SubscriptionsPage : Page
{
    private readonly ObservableCollection<SubscriptionItemViewModel> channels = [];

    public SubscriptionsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await LoadSubscriptionsAsync();
        }
        catch
        {
            // Guarded async-void override; LoadSubscriptionsAsync already routes failures to
            // the InfoBar.
        }
    }

    private async Task LoadSubscriptionsAsync()
    {
        ErrorBar.IsOpen = false;
        try
        {
            var listing = await App.Session.RunAsync(t => App.Session.Subscriptions.LoadSubscriptionsAsync(t));
            channels.Clear();
            foreach (var channel in listing.Channels) channels.Add(new SubscriptionItemViewModel(channel));
            EmptyText.Visibility = channels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadSubscriptionsAsync();
        }
        catch
        {
            // Guarded async void handler; LoadSubscriptionsAsync already routes failures to
            // the InfoBar.
        }
    }

    private void OnChannelClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SubscriptionItemViewModel channel) return;
        Frame.Navigate(typeof(ChannelPage), new ChannelRequest(channel.Id, channel.Title));
    }
}

/// Wraps a SubscribedChannel with the display bits its GridView tile needs: the themed
/// fallback fill when there is no avatar, and a name that falls back to the bare id.
public sealed class SubscriptionItemViewModel
{
    private static readonly Brush FallbackFill =
        (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];

    public string Id { get; }
    public string Title { get; }
    public string DisplayName { get; }
    public Brush AvatarFill { get; }

    public SubscriptionItemViewModel(SubscribedChannel channel)
    {
        Id = channel.Id;
        Title = channel.Title;
        DisplayName = channel.Title.Length > 0 ? channel.Title : channel.Id;
        AvatarFill = channel.AvatarUrl is { } avatarUrl
            ? new ImageBrush { ImageSource = new BitmapImage(new Uri(avatarUrl)), Stretch = Stretch.UniformToFill }
            : FallbackFill;
    }
}
