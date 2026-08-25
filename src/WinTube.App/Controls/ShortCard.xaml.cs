using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinTube.Core.Models;

namespace WinTube.App.Controls;

/// The portrait Shorts tile: a 150x267 thumbnail with an optional bottom-right channel
/// avatar. No title, stats, duration, or progress line - Shorts rows are just a scrollable
/// wall of thumbnails. Stateless beyond its Video dependency property, same as VideoCard.
public sealed partial class ShortCard : UserControl
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

        Thumbnail.Source = new BitmapImage(
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

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) =>
        HoverEdge.BorderBrush = HoverEdgeBrush;

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) =>
        HoverEdge.BorderBrush = IdleEdge;

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Video is { } video) Clicked?.Invoke(this, video);
    }
}
