using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using WinTube.Core.Models;

namespace WinTube.App.Views;

/// Search: an AutoSuggestBox above a wrapping GridView of VideoCards. Each submitted query
/// replaces the current results; empty/whitespace queries are ignored. Never throws past its
/// own boundary — search errors surface as an inline InfoBar with Retry instead.
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

    private async void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        try
        {
            var query = args.QueryText?.Trim();
            if (string.IsNullOrWhiteSpace(query)) return;
            await RunSearchAsync(query);
        }
        catch
        {
            // OnQuerySubmitted must never throw past this boundary; RunSearchAsync already
            // routes failures to the InfoBar.
        }
    }

    private async Task RunSearchAsync(string query)
    {
        lastQuery = query;
        ErrorBar.IsOpen = false;
        try
        {
            var items = await App.Session.RunAsync(t => App.Session.Search.SearchAsync(query, t));
            results.Clear();
            foreach (var item in items) results.Add(new VideoCardViewModel(item));
            App.Session.History.Remember(items);
            RefreshProgress();
            EmptyText.Text = $"No results for “{query}”.";
            EmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
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
        Frame.Navigate(typeof(PlayerPage), video);
}
