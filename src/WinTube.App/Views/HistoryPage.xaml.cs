using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using WinTube.Core.Models;
using WinTube.Core.Stores;

namespace WinTube.App.Views;

/// A wrapping GridView of VideoCards, newest first, capped at 60: local watches and progress
/// folded with the account's own history. Draws from what is on disk immediately, then layers
/// in the account's history and fills in placeholder cards with looked-up metadata — ordered,
/// not raced, so every card a lookup would otherwise pay for arrives finished instead. Never
/// throws past its own boundary; a feed failure is silent and best-effort, like tvOS.
public sealed partial class HistoryPage : Page
{
    private const int MaxCards = 60;

    private readonly ObservableCollection<VideoCardViewModel> cards = [];
    private readonly Dictionary<string, int> indexById = [];
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

    private CancellationTokenSource? loadCts;
    private bool unloaded;

    public HistoryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        unloaded = false;
        App.Session.Progress.Changed += OnProgressChanged;
        loadCts = new CancellationTokenSource();
        try
        {
            await LoadHistoryAsync(loadCts.Token);
        }
        catch
        {
            // Guarded async void handler; LoadHistoryAsync already contains its own failures.
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        unloaded = true;
        App.Session.Progress.Changed -= OnProgressChanged;
        loadCts?.Cancel();
        loadCts?.Dispose();
        loadCts = null;
    }

    // MARK: load sequence

    private async Task LoadHistoryAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var history = App.Session.History;
        var progress = App.Session.Progress;

        // Step 1: draw immediately from what is local.
        var ids = HistoryBuilder.Ordered(
            HistoryBuilder.WatchTimes(progress.Entries, history.WatchedHere, [], now)).Take(MaxCards).ToList();
        Render(ids);
        if (unloaded) return;

        // Step 2: fold in the account's own history, best-effort — a feed failure skips the
        // rest of the sequence silently and leaves the local half rendered.
        IReadOnlyList<string> accountHistoryIds;
        try
        {
            var page = await App.Session.RunAsync(t => App.Session.Feed.LoadHistoryFeedAsync(t));
            if (unloaded) return;
            var items = page.Sections.SelectMany(s => s.Items).ToList();
            history.Remember(items);
            accountHistoryIds = items.Select(i => i.Id).ToList();
        }
        catch
        {
            return;
        }

        ids = HistoryBuilder.Ordered(
            HistoryBuilder.WatchTimes(progress.Entries, history.WatchedHere, accountHistoryIds, now))
            .Take(MaxCards).ToList();
        Render(ids);
        if (unloaded) return;

        // Step 3: fill in placeholder cards with looked-up metadata, four at a time.
        var placeholderIds = ids.Where(id => history.Card(id) is null).ToList();
        await FillPlaceholdersAsync(placeholderIds, ct);
        if (unloaded) return;

        // Step 4: keep only what is still on the page.
        history.Prune(ids.ToHashSet());
    }

    private async Task FillPlaceholdersAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(ids.Select(id => FillOneAsync(id, gate, ct)));
    }

    private async Task FillOneAsync(string id, SemaphoreSlim gate, CancellationToken ct)
    {
        try { await gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }

        try
        {
            var item = await App.Session.Metadata.LoadAsync(id, ct);
            if (unloaded || item is null) return;
            App.Session.History.Remember([item]);
            UpdateCard(id, item);
        }
        catch (OperationCanceledException)
        {
            // Unloaded mid-lookup; abandon this one.
        }
        catch
        {
            // A single failed lookup just leaves that card's placeholder in place.
        }
        finally
        {
            gate.Release();
        }
    }

    // MARK: rendering

    private void Render(IReadOnlyList<string> ids)
    {
        var history = App.Session.History;
        cards.Clear();
        indexById.Clear();
        foreach (var id in ids)
        {
            indexById[id] = cards.Count;
            cards.Add(new VideoCardViewModel(history.Card(id) ?? Placeholder(id)));
        }
        RefreshProgress();
        EmptyText.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCard(string id, VideoItem item)
    {
        if (!indexById.TryGetValue(id, out var index) || index >= cards.Count) return;
        cards[index] = new VideoCardViewModel(item) { ProgressFraction = cards[index].ProgressFraction };
    }

    private static VideoItem Placeholder(string id) =>
        new() { Id = id, Title = "", ThumbnailUrl = VideoItem.FallbackThumbnail(id) };

    // MARK: progress

    private void OnProgressChanged()
    {
        if (dispatcher.HasThreadAccess) RefreshProgress();
        else dispatcher.TryEnqueue(RefreshProgress);
    }

    private void RefreshProgress()
    {
        var entries = App.Session.Progress.Entries;
        foreach (var card in cards)
        {
            card.ProgressFraction =
                entries.TryGetValue(card.Video.Id, out var entry) &&
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
