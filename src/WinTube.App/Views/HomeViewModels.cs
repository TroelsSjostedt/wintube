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
        foreach (var item in section.Items) Items.Add(new VideoCardViewModel(item));
    }

    /// Shapes a continuation page's items to what this row shows - see FeedSection.Admitting.
    public IReadOnlyList<VideoItem> Admit(IReadOnlyList<VideoItem> items) => section.Admitting(items);
}

/// Wraps a VideoItem with the mutable progress fraction VideoCard's binding needs — VideoItem
/// itself is an immutable record, so per-card UI state (just progress, for now) lives here.
public sealed class VideoCardViewModel(VideoItem video) : INotifyPropertyChanged
{
    public VideoItem Video { get; } = video;

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
