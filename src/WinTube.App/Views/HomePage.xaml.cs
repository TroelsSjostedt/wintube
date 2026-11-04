using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using WinTube.Core.Models;
using WinTube.Core.Stores;

namespace WinTube.App.Views;

/// The signed-in landing page: a vertical stack of shelves, each a horizontal row of
/// VideoCards. Loads the first feed page on Loaded, pages a row sideways as it scrolls near
/// its end, and pages in more shelves via the "Load more" button. Never throws past its own
/// boundary — feed errors surface as an inline InfoBar with Retry instead.
public sealed partial class HomePage : Page
{
    private readonly ObservableCollection<ShelfViewModel> shelves = [];
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private string? pageContinuation;
    private int historyRowCount;
    private bool loadingMoreShelves;
    private Func<Task>? retryAction;

    public HomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Session.Progress.Changed += OnProgressChanged;
        App.Session.MyList.Changed += OnMyListChanged;
        await LoadHomeAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Session.Progress.Changed -= OnProgressChanged;
        App.Session.MyList.Changed -= OnMyListChanged;
    }

    // MARK: loading

    private async Task LoadHomeAsync()
    {
        ErrorBar.IsOpen = false;
        try
        {
            var page = await App.Session.RunAsync(
                t => App.Session.Feed.LoadCompositeHomeAsync(t, MyListRow()));
            shelves.Clear();
            foreach (var section in page.Sections) shelves.Add(new ShelfViewModel(section));
            historyRowCount = page.HistoryRowCount;
            pageContinuation = page.Continuation;
            LoadMoreButton.Visibility = pageContinuation is null ? Visibility.Collapsed : Visibility.Visible;
            App.Session.History.Remember(page.Sections.SelectMany(s => s.Items));
            RefreshAllProgress();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message, LoadHomeAsync);
        }
    }

    public const string MyListTitle = "My list";

    /// The local "My list" row — the merged watch-later/continue-watching list, latest-touched
    /// first, finished videos hidden — or null when empty (the row is omitted). Shorts are kept
    /// out of the landscape row but stay on the list page.
    private static FeedSection? MyListRow()
    {
        var items = MyListStore.Visible(App.Session.MyList.Items, App.Session.Progress.Entries)
            .Where(v => !v.IsShort).ToList();
        return items.Count == 0
            ? null
            : new FeedSection(Guid.NewGuid().ToString("N"), MyListTitle, items, null, IsShorts: false);
    }

    // MARK: local-row live refresh

    private void OnMyListChanged() => RefreshLocalRow(MyListTitle, MyListRow());

    /// Updates a local row's cards in place after its store changed — an in-place diff, not a
    /// shelf replacement, so the row keeps its scroll position and neighbours slide into a
    /// removed card's gap. A row not currently shown is NOT inserted — its position depends on
    /// the composite stitch, so it appears on the next full Home load instead.
    private void RefreshLocalRow(string title, FeedSection? section)
    {
        var index = -1;
        for (var i = 0; i < shelves.Count; i++)
            if (shelves[i].Title == title) { index = i; break; }
        if (index < 0) return;
        if (section is null)
        {
            shelves.RemoveAt(index);
            return;
        }
        var shelf = shelves[index];
        CardListSync.Apply(shelf.Items, section.Items,
            v => new VideoCardViewModel(v) { InMyList = true });
        RefreshProgress(shelf.Items);
    }

    private async void OnLoadMoreShelves(object sender, RoutedEventArgs e)
    {
        if (loadingMoreShelves || pageContinuation is not { } continuation) return;
        loadingMoreShelves = true;
        try
        {
            var page = await App.Session.RunAsync(t => App.Session.Feed.LoadMoreShelvesAsync(continuation, t));
            foreach (var section in page.Sections)
                shelves.Insert(shelves.Count - historyRowCount, new ShelfViewModel(section));
            pageContinuation = page.Continuation;
            LoadMoreButton.Visibility = pageContinuation is null ? Visibility.Collapsed : Visibility.Visible;
            App.Session.History.Remember(page.Sections.SelectMany(s => s.Items));
        }
        catch (Exception ex)
        {
            ShowError(ex.Message, () => { OnLoadMoreShelves(sender, e); return Task.CompletedTask; });
        }
        finally
        {
            loadingMoreShelves = false;
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
