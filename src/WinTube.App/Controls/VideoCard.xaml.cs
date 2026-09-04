using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using WinTube.Core.Feed;
using WinTube.Core.Models;

namespace WinTube.App.Controls;

/// The reusable video tile: 16:9 thumbnail, duration badge, a red "how far in" progress line,
/// a two-line title, and a muted "Author · ViewCount · age" line split so the author half is
/// its own clickable/hoverable link into ChannelPage. Stateless beyond its two dependency
/// properties — callers (HomePage, Search/History/Channel) own the paging and progress-tracking
/// state, this just renders one VideoItem.
public sealed partial class VideoCard : UserControl
{
    private const double CardWidth = 320;

    public static readonly DependencyProperty VideoProperty = DependencyProperty.Register(
        nameof(Video), typeof(VideoItem), typeof(VideoCard),
        new PropertyMetadata(null, OnVideoChanged));

    /// 0 hides the line entirely; otherwise the fraction of the card width it spans.
    public static readonly DependencyProperty ProgressFractionProperty = DependencyProperty.Register(
        nameof(ProgressFraction), typeof(double), typeof(VideoCard),
        new PropertyMetadata(0.0, OnProgressFractionChanged));

    public VideoItem? Video
    {
        get => (VideoItem?)GetValue(VideoProperty);
        set => SetValue(VideoProperty, value);
    }

    public double ProgressFraction
    {
        get => (double)GetValue(ProgressFractionProperty);
        set => SetValue(ProgressFractionProperty, value);
    }

    /// Raised on tap/click; carries the video so callers don't need to re-read the property.
    public event EventHandler<VideoItem>? Clicked;

    /// Raised when the channel name (or the context menu's Go to channel) is picked.
    /// Only wired to cells that carried a ChannelId.
    public event EventHandler<VideoItem>? ChannelClicked;

    public VideoCard() => InitializeComponent();

    private static void OnVideoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((VideoCard)d).RenderVideo();

    private static void OnProgressFractionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((VideoCard)d).RenderProgress();

    private void RenderVideo()
    {
        if (Video is not { } video) return;

        Thumbnail.Source = new BitmapImage(
            new Uri(video.ThumbnailUrl ?? VideoItem.FallbackThumbnail(video.Id)));
        TitleText.Text = video.Title;

        DurationText.Text = video.Duration;
        DurationBadge.Visibility = video.Duration.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var hasChannel = video.ChannelId is not null && video.Author.Length > 0;
        AuthorText.Text = hasChannel ? video.Author : "";
        AuthorText.Visibility = hasChannel ? Visibility.Visible : Visibility.Collapsed;
        var rest = hasChannel ? ComposeSubtitle(video with { Author = "" }) : ComposeSubtitle(video);
        RestText.Text = hasChannel && rest.Length > 0 ? " · " + rest : rest;
        GoToChannelItem.IsEnabled = video.ChannelId is not null;
        RenderProgress();
    }

    private void RenderProgress()
    {
        var fraction = Math.Clamp(ProgressFraction, 0, 1);
        ProgressLine.Width = fraction * CardWidth;
        ProgressLine.Visibility = fraction > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// "Author · ViewCount · age", with empty parts (and a missing age) omitted rather than
    /// leaving stray separators.
    private static string ComposeSubtitle(VideoItem video)
    {
        var age = video.PublishedAt is { } published
            ? RelativeTime.Format(published, DateTimeOffset.UtcNow)
            : null;
        var parts = new[] { video.Author, video.ViewCount, age }
            .Where(part => !string.IsNullOrEmpty(part));
        return string.Join(" · ", parts);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Video is { } video) Clicked?.Invoke(this, video);
    }

    private void OnAuthorEntered(object sender, PointerRoutedEventArgs e) =>
        AuthorText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;

    private void OnAuthorExited(object sender, PointerRoutedEventArgs e) =>
        AuthorText.TextDecorations = Windows.UI.Text.TextDecorations.None;

    private void OnAuthorTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (Video is { } video) ChannelClicked?.Invoke(this, video);
    }

    private void OnGoToChannel(object sender, RoutedEventArgs e)
    {
        if (Video is { } video) ChannelClicked?.Invoke(this, video);
    }
}
