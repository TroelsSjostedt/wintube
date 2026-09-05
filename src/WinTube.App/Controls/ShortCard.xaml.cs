using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private ListViewItem? listViewItem;
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
        listViewItem = FindListViewItemAncestor(this);
        if (listViewItem is { } item)
        {
            item.GotFocus += OnListViewItemGotFocus;
            item.LostFocus += OnListViewItemLostFocus;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (listViewItem is { } item)
        {
            item.GotFocus -= OnListViewItemGotFocus;
            item.LostFocus -= OnListViewItemLostFocus;
        }
        listViewItem = null;
        App.Previews.Cold(this);
    }

    private void OnListViewItemGotFocus(object sender, RoutedEventArgs e) => App.Previews.Warm(this);

    private void OnListViewItemLostFocus(object sender, RoutedEventArgs e) => App.Previews.Cold(this);

    private static ListViewItem? FindListViewItemAncestor(DependencyObject start)
    {
        for (var parent = VisualTreeHelper.GetParent(start); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ListViewItem item) return item;
        }
        return null;
    }
}
