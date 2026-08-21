using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using WinTube.Core.InnerTube;
using WinTube.Core.Player;

namespace WinTube.App.Views;

/// STAGE 0 SPIKE — throwaway. Answers: does MediaPlayerElement + AdaptiveMediaSource play
/// YouTube's HLS multivariant playlist? Deleted once PlayerPage exists (Task 16).
public sealed partial class SpikePage : Page
{
    private readonly StreamService streams = new(
        new InnerTubeClient(App.Http, App.Secrets), new VisitorDataStore(App.Http));

    public SpikePage() => InitializeComponent();

    private async void OnPlayHls(object sender, RoutedEventArgs e) => await Play(after: null);

    private async void OnPlayProgressive(object sender, RoutedEventArgs e) =>
        await Play(after: ClientKind.VisionOs);   // skips straight to ANDROID

    private async Task Play(ClientKind? after)
    {
        try
        {
            Status.Text = "resolving…";
            var stream = await streams.ResolveAsync(VideoIdBox.Text.Trim(), after);
            Status.Text = $"{stream.Client} adaptive={stream.IsAdaptive}";

            MediaSource source;
            if (stream.IsAdaptive)
            {
                var http = new Windows.Web.Http.HttpClient();
                http.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", stream.UserAgent);
                var result = await AdaptiveMediaSource.CreateFromUriAsync(
                    new Uri(stream.Url.ToString()), http);
                if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
                {
                    Status.Text = $"AdaptiveMediaSource failed: {result.Status}";
                    return;
                }
                source = MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
            }
            else
            {
                source = MediaSource.CreateFromUri(new Uri(stream.Url.ToString()));
            }

            Player.SetMediaPlayer(new MediaPlayer { AutoPlay = true });
            Player.MediaPlayer.Source = source;
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
        }
    }
}
