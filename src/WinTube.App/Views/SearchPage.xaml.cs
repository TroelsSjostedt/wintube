using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.ObjectModel;
using WinTube.Core.Models;

namespace WinTube.App.Views;

/// Search results: a wrapping GridView of VideoCards for the query MainWindow's global search
/// bar navigated here with. Each query replaces the current results. Never throws past its own
/// boundary — search errors surface as an inline InfoBar with Retry instead.
public sealed partial class SearchPage : Page
{
    private readonly ObservableCollection<VideoCardViewModel> results = [];
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private string? lastQuery;

    public SearchPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        App.Session.Progress.Changed += OnProgressChanged;

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        App.Session.Progress.Changed -= OnProgressChanged;

    // MARK: search

    /// The query behind the results currently shown; MainWindow mirrors it into the global
    /// search bar when this page is reached without a query of its own.
    public string? ActiveQuery => lastQuery;

    /// MainWindow's search bar hands the query over as the navigation parameter (it has already
    /// routed pasted YouTube links to the player), so a string parameter runs immediately. The
    /// page is cached, so Back/Forward re-deliver the entry's original query: re-run only when
    /// the results on screen are for a different one. No parameter (nav-menu click) keeps the
    /// cached results as they are.
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not string query || string.IsNullOrWhiteSpace(query)) return;
        if (e.NavigationMode != NavigationMode.New && query == lastQuery) return;
        _ = RunSearchAsync(query);
    }

    private async Task RunSearchAsync(string query)
    {
        lastQuery = query;
        ErrorBar.IsOpen = false;
        try
        {
            var items = await App.Session.RunAsync(t => App.Session.Search.SearchAsync(query, t));
            // A newer query started while this one was in flight; its results win.
            if (query != lastQuery) return;
            results.Clear();
            foreach (var item in items) results.Add(new VideoCardViewModel(item));
            App.Session.History.Remember(items);
            RefreshProgress();
            EmptyText.Text = $"No results for “{query}”.";
            EmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (query == lastQuery) ShowError(ex.Message);
        }
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

    // MARK: errors

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        try
        {
            ErrorBar.IsOpen = false;
            if (lastQuery is { } query) await RunSearchAsync(query);
        }
        catch
        {
            // Guarded async void handler; RunSearchAsync already routes failures to the InfoBar.
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
