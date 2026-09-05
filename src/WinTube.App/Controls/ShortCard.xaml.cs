using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Playback;
using WinTube.Core.Models;

namespace WinTube.App.Controls;

/// The portrait Shorts tile: a 150x267 thumbnail with an optional bottom-right channel
/// avatar. No title, stats, duration, or progress line - Shorts rows are just a scrollable
/// wall of thumbnails. Stateless beyond its Video dependency property, same as VideoCard.
public sealed partial class ShortCard : UserControl, IPreviewHost
{
    private static readonly Brush IdleEdge = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    private static readonly Brush HoverEdgeBrush = new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.8 };

    public static readonly DependencyProperty VideoProperty = DependencyProperty.Register(
        nameof(Video), typeof(VideoItem), typeof(ShortCard),
        new PropertyMetadata(null, OnVideoChanged));

    public VideoItem? Video
    {
        get => (VideoItem?)GetValue(VideoProperty);
        set => SetValue(VideoProperty, value);
    }

    /// Raised on tap/click; carries the video so callers don't need to re-read the property.
    public event EventHandler<VideoItem>? Clicked;

    public ShortCard() => InitializeComponent();

    private static void OnVideoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ShortCard)d).RenderVideo();

    private void RenderVideo()
    {
        // Container recycling can rebind Video on a live card (scroll while hovering, no
        // PointerExited/Unloaded first) — cold the OLD identity before the new one takes over,
        // or the coordinator's hosts/gate stay stuck on an id this card can no longer report.
        App.Previews.Detach(this);
        if (Video is not { } video) return;

        ThumbnailBrush.ImageSource = new BitmapImage(
            new Uri(video.ThumbnailUrl ?? VideoItem.FallbackThumbnail(video.Id)));

        if (video.ChannelAvatarUrl is { } avatarUrl)
        {
            AvatarBrush.ImageSource = new BitmapImage(new Uri(avatarUrl));
            Avatar.Visibility = Visibility.Visible;
        }
        else
        {
            AvatarBrush.ImageSource = null;
            Avatar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        HoverEdge.BorderBrush = HoverEdgeBrush;
        App.Previews.Warm(this);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        HoverEdge.BorderBrush = IdleEdge;
        App.Previews.Cold(this);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Video is { } video) Clicked?.Invoke(this, video);
    }

    // MARK: hover/focus preview (IPreviewHost)

    private SelectorItem? selectorItem;
    private MediaPlayer? previewPlayer;

    string IPreviewHost.PreviewVideoId => Video?.Id ?? "";

    void IPreviewHost.ShowPreview(MediaPlayer player)
    {
        previewPlayer = player;
        player.PlaybackSession.PlaybackStateChanged += OnPreviewPlaybackStateChanged;
    }

    void IPreviewHost.HidePreview()
    {
        PreviewSurface.Opacity = 0;
        PreviewSurface.SetMediaPlayer(null);
        if (previewPlayer is { } player) player.PlaybackSession.PlaybackStateChanged -= OnPreviewPlaybackStateChanged;
        previewPlayer = null;
    }

    /// Fires on a background thread; marshal before touching the surface. The thumbnail stays
    /// standing until there is a real frame to show.
    private void OnPreviewPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        if (sender.PlaybackState != MediaPlaybackState.Playing) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (previewPlayer is not { } player || player.PlaybackSession != sender) return;
            PreviewSurface.SetMediaPlayer(player);
            PreviewSurface.Opacity = 1;
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // A re-Loaded without an intervening Unloaded (rare, but container reuse can do it)
        // must not double-subscribe.
        if (selectorItem is { } stale)
        {
            stale.GotFocus -= OnSelectorItemGotFocus;
            stale.LostFocus -= OnSelectorItemLostFocus;
        }

        selectorItem = FindSelectorItemAncestor(this);
        if (selectorItem is { } item)
        {
            item.GotFocus += OnSelectorItemGotFocus;
            item.LostFocus += OnSelectorItemLostFocus;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (selectorItem is { } item)
        {
            item.GotFocus -= OnSelectorItemGotFocus;
            item.LostFocus -= OnSelectorItemLostFocus;
        }
        selectorItem = null;
        App.Previews.Detach(this);
    }

    private void OnSelectorItemGotFocus(object sender, RoutedEventArgs e) => App.Previews.Warm(this);

    private void OnSelectorItemLostFocus(object sender, RoutedEventArgs e) => App.Previews.Cold(this);

    /// GridView (SearchPage/HistoryPage) hosts cards in GridViewItem, ListView in ListViewItem —
    /// both derive from SelectorItem, so matching that base is what makes keyboard focus wire up
    /// in a GridView host too instead of only in a ListView one.
    private static SelectorItem? FindSelectorItemAncestor(DependencyObject start)
    {
        for (var parent = VisualTreeHelper.GetParent(start); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is SelectorItem item) return item;
        }
        return null;
    }
}
