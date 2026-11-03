using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using WinTube.Core.Models;

namespace WinTube.App.Views;

/// The full My list: a wrapping grid of every saved video, newest-added first (the Home row
/// shows the same list; this page is where a long list lives). Entirely store-backed —
/// no network, so no error surface. Cards' own context menu handles removal; the store's
/// Changed event keeps the grid current, including while the page is on screen.
public sealed partial class MyListPage : Page
{
    private readonly ObservableCollection<VideoCardViewModel> results = [];
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

    public MyListPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Session.MyList.Changed += OnMyListChanged;
        App.Session.Progress.Changed += OnProgressChanged;
        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Session.MyList.Changed -= OnMyListChanged;
        App.Session.Progress.Changed -= OnProgressChanged;
    }

    private void OnMyListChanged() => Rebuild();

    private void Rebuild()
    {
        results.Clear();
        foreach (var video in App.Session.MyList.Items)
            results.Add(new VideoCardViewModel(video));
        RefreshProgress();
        EmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: progress

    private void OnProgressChanged()
    {
        if (dispatcher.HasThreadAccess) RefreshProgress();
        else dispatcher.TryEnqueue(RefreshProgress);
    }

    private void RefreshProgress()
    {
        var entries = App.Session.Progress.Entries;
        foreach (var item in results)
        {
            item.ProgressFraction =
                entries.TryGetValue(item.Video.Id, out var entry) &&
                entry.DurationSeconds > 0
                    ? entry.PositionSeconds / entry.DurationSeconds
                    : 0;
        }
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
