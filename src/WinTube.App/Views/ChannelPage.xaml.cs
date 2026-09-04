using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.ObjectModel;
using WinTube.Core.Models;

namespace WinTube.App.Views;

/// A channel's browse page: header (banner, avatar, title, subscribe toggle) above the same
/// shelf-row layout HomePage uses, first page of shelves only. Never throws past its own
/// boundary — load and subscribe-toggle failures surface as an inline InfoBar.
public sealed partial class ChannelPage : Page
{
    private readonly ObservableCollection<ShelfViewModel> shelves = [];
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private Func<Task>? retryAction;
    private string? channelId;

    public ChannelPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var request = (ChannelRequest)e.Parameter;
        channelId = request.ChannelId;
        TitleText.Text = request.FallbackTitle;
        SubscribeButton.Visibility = Visibility.Collapsed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Session.Progress.Changed += OnProgressChanged;
        if (channelId is not null) await LoadChannelAsync(channelId);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        App.Session.Progress.Changed -= OnProgressChanged;

    // MARK: loading

    private async Task LoadChannelAsync(string id)
    {
        ErrorBar.IsOpen = false;
        try
        {
            var page = await App.Session.RunAsync(t => App.Session.Feed.LoadChannelAsync(id, t));

            TitleText.Text = page.Title.Length > 0 ? page.Title : TitleText.Text;

            if (page.BannerUrl is { } bannerUrl)
            {
                Banner.Source = new BitmapImage(new Uri(bannerUrl));
                Banner.Visibility = Visibility.Visible;
            }
            else
            {
                Banner.Visibility = Visibility.Collapsed;
            }

            AvatarBrush.ImageSource = page.AvatarUrl is { } avatarUrl
                ? new BitmapImage(new Uri(avatarUrl))
                : null;

            if (page.IsSubscribed is { } isSubscribed)
            {
                SubscribeButton.Visibility = Visibility.Visible;
                SubscribeButton.IsChecked = isSubscribed;
                SubscribeButton.Content = isSubscribed ? "Subscribed ✓" : "Subscribe";
            }
            else
            {
                SubscribeButton.Visibility = Visibility.Collapsed;
            }

            shelves.Clear();
            foreach (var section in page.Feed.Sections) shelves.Add(new ShelfViewModel(section));
            EmptyText.Visibility = shelves.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            App.Session.History.Remember(page.Feed.Sections.SelectMany(s => s.Items));
            RefreshAllProgress();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message, () => LoadChannelAsync(id));
        }
    }

    // MARK: row (sideways) paging

    /// Wires each row's inner ScrollViewer once it exists in the visual tree — ListView doesn't
    /// expose ViewChanged itself, only the ScrollViewer part inside its template does.
    private void OnRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView listView || listView.Tag is not ShelfViewModel shelf) return;
        if (listView.Visibility != Visibility.Visible) return;   // the row's other (hidden) ListView
        if (shelf.ScrollViewer is not null) return;   // already wired
        if (FindScrollViewer(listView) is not { } scrollViewer) return;
        shelf.ScrollViewer = scrollViewer;
        scrollViewer.ViewChanged += async (_, _) => await OnRowScrolled(shelf, scrollViewer);
    }

    private async Task OnRowScrolled(ShelfViewModel shelf, ScrollViewer scrollViewer)
    {
        if (shelf.IsLoadingMore || shelf.Continuation is not { } continuation) return;
        if (scrollViewer.HorizontalOffset <= scrollViewer.ScrollableWidth - 800) return;

        shelf.IsLoadingMore = true;
        try
        {
            var rowPage = await App.Session.RunAsync(t => App.Session.Feed.LoadMoreItemsAsync(continuation, t));
            foreach (var item in shelf.Admit(rowPage.Items)) shelf.Items.Add(new VideoCardViewModel(item));
            shelf.Continuation = rowPage.Continuation;
            App.Session.History.Remember(rowPage.Items);
            RefreshProgress(shelf.Items);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message, () => OnRowScrolled(shelf, scrollViewer));
        }
        finally
        {
            shelf.IsLoadingMore = false;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scrollViewer) return scrollViewer;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    // MARK: progress

    private void OnProgressChanged()
    {
        if (dispatcher.HasThreadAccess) RefreshAllProgress();
        else dispatcher.TryEnqueue(RefreshAllProgress);
    }

    private void RefreshAllProgress()
    {
        foreach (var shelf in shelves) RefreshProgress(shelf.Items);
    }

    private static void RefreshProgress(IEnumerable<VideoCardViewModel> items)
    {
        var entries = App.Session.Progress.Entries;
        foreach (var item in items)
        {
            item.ProgressFraction =
                entries.TryGetValue(item.Video.Id, out var entry) &&
                entry.DurationSeconds > 0
                    ? entry.PositionSeconds / entry.DurationSeconds
                    : 0;
        }
    }

    // MARK: subscribe toggle

    private async void OnSubscribeToggled(object sender, RoutedEventArgs e)
    {
        if (channelId is not { } id) return;
        var subscribing = SubscribeButton.IsChecked == true;
        SubscribeButton.Content = subscribing ? "Subscribed ✓" : "Subscribe";
        try
        {
            if (subscribing)
                await App.Session.RunAsync(async t => { await App.Session.Subscriptions.SubscribeAsync(id, t); return true; });
            else
                await App.Session.RunAsync(async t => { await App.Session.Subscriptions.UnsubscribeAsync(id, t); return true; });
        }
        catch (Exception ex)
        {
            SubscribeButton.IsChecked = !subscribing;
            SubscribeButton.Content = !subscribing ? "Subscribed ✓" : "Subscribe";
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            retryAction = null;
        }
    }

    // MARK: errors

    private void ShowError(string message, Func<Task> retry)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        retryAction = retry;
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        ErrorBar.IsOpen = false;
        if (retryAction is { } retry) await retry();
    }

    // MARK: card interaction

    private void OnVideoCardClicked(object sender, VideoItem video) =>
        Frame.Navigate(typeof(PlayerPage), new PlayerRequest(video));

    private void OnChannelClicked(object sender, VideoItem video)
    {
        if (video.ChannelId is not { } channelId) return;
        Frame.Navigate(typeof(ChannelPage), new ChannelRequest(channelId, video.Author));
    }
}
