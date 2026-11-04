using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinTube.Core.Models;

namespace WinTube.App.Views;

/// Mutable per-row state HomePage needs on top of an immutable FeedSection: the accumulating
/// item list, the row's own paging continuation, an in-flight guard for row paging, and the
/// row's ScrollViewer once its ListView is realized (so the ViewChanged handler is wired once).
public sealed class ShelfViewModel
{
    private readonly FeedSection section;

    public string Id { get; }
    public string Title { get; }
    public bool IsShorts { get; }
    public Visibility TitleVisibility => Title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility VideoRowVisibility => IsShorts ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ShortRowVisibility => IsShorts ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<VideoCardViewModel> Items { get; } = [];
    public string? Continuation { get; set; }
    public bool IsLoadingMore { get; set; }
    public ScrollViewer? ScrollViewer { get; set; }

    public ShelfViewModel(FeedSection section)
    {
        this.section = section;
        Id = section.Id;
        Title = section.Title;
        IsShorts = section.IsShorts;
        Continuation = section.Continuation;
        var inMyList = section.Title == HomePage.MyListTitle;
        foreach (var item in section.Items)
            Items.Add(new VideoCardViewModel(item) { InMyList = inMyList });
    }

    /// Shapes a continuation page's items to what this row shows - see FeedSection.Admitting.
    public IReadOnlyList<VideoItem> Admit(IReadOnlyList<VideoItem> items) => section.Admitting(items);
}

/// Wraps a VideoItem with the mutable progress fraction VideoCard's binding needs — VideoItem
/// itself is an immutable record, so per-card UI state (just progress, for now) lives here.
public sealed class VideoCardViewModel(VideoItem video) : INotifyPropertyChanged
{
    public VideoItem Video { get; } = video;

    /// True for cards sitting in the My list row/page — there the corner toggle is a cross;
    /// elsewhere it is a plus shown only while the video is NOT on the list.
    public bool InMyList { get; init; }

    private double progressFraction;
    public double ProgressFraction
    {
        get => progressFraction;
        set
        {
            if (progressFraction == value) return;
            progressFraction = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressFraction)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// In-place diff of a card collection to a target item order. Replacing the collection (or the
/// shelf) makes WinUI rebuild the list and reset its scroll position; removing/inserting/moving
/// items instead keeps the viewport where it was and lets neighbours slide into the gap.
public static class CardListSync
{
    public static void Apply(
        ObservableCollection<VideoCardViewModel> cards, IReadOnlyList<VideoItem> target,
        Func<VideoItem, VideoCardViewModel> make)
    {
        var wanted = target.Select(v => v.Id).ToHashSet();
        for (var i = cards.Count - 1; i >= 0; i--)
            if (!wanted.Contains(cards[i].Video.Id)) cards.RemoveAt(i);
        for (var i = 0; i < target.Count; i++)
        {
            var at = -1;
            for (var j = 0; j < cards.Count; j++)
                if (cards[j].Video.Id == target[i].Id) { at = j; break; }
            if (at < 0) cards.Insert(i, make(target[i]));
            else if (at != i) cards.Move(at, i);
        }
    }
}
