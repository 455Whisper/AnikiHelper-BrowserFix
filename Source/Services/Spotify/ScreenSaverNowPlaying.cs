using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace AnikiHelper.Services.Spotify
{
    /// <summary>
    /// Shows the Spotify now-playing card in the top-left corner of the AFK screen (the
    /// screensaver that cycles backgrounds and logos) for as long as music is playing.
    /// </summary>
    internal sealed class ScreenSaverNowPlaying
    {
        private readonly AnikiSpotifyPanelService spotify;
        private readonly ILogger logger;
        private SpotifyNowPlayingToast card;
        private DispatcherTimer pollTimer;
        private Window screenSaverWindow;
        private bool active;
        private bool refreshing;

        public ScreenSaverNowPlaying(AnikiSpotifyPanelService spotify, ILogger logger)
        {
            this.spotify = spotify;
            if (spotify != null)
            {
                spotify.NowPlayingChanged += async () => await RefreshAsync();
            }
            this.logger = logger;
        }

        public void SetActive(bool isActive, Window window)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => SetActive(isActive, window)));
                return;
            }

            active = isActive;
            screenSaverWindow = isActive ? window : null;
            if (!isActive)
            {
                pollTimer?.Stop();
                card?.HideImmediately();
                return;
            }

            if (spotify == null || !spotify.IsSignedIn)
            {
                return;
            }

            if (pollTimer == null)
            {
                pollTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(5) };
                pollTimer.Tick += async (s, e) => await RefreshAsync();
            }

            pollTimer.Start();
            var ignored = RefreshAsync();
        }

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            if (!active || refreshing)
            {
                return;
            }

            refreshing = true;
            try
            {
                var body = await spotify.FetchCurrentlyPlayingAsync();
                if (!active)
                {
                    return;
                }

                var item = body?["item"] as JObject;
                var isPlaying = (bool?)body?["is_playing"] ?? false;
                if (item == null || !isPlaying)
                {
                    card?.Hide();
                    return;
                }

                var title = (string)item["name"] ?? string.Empty;
                var artists = item["artists"] as JArray;
                var artist = artists != null
                    ? string.Join(", ", artists.Select(a => (string)a["name"]).Where(n => !string.IsNullOrEmpty(n)))
                    : (string)item["show"]?["name"] ?? string.Empty;
                var images = (item["album"]?["images"] ?? item["images"] ?? item["show"]?["images"]) as JArray;

                if (card == null)
                {
                    card = new SpotifyNowPlayingToast(spotify.LogoPath, ToastCorner.TopLeft) { Pinned = true };
                }

                card.OwnerWindow = screenSaverWindow;
                card.Show(title, artist, AnikiSpotifyPanelService.PickImage(images, 300),
                    (double?)body["progress_ms"] ?? 0, (double?)item["duration_ms"] ?? 0, true);
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][ScreenSaver] Now playing card failed.");
            }
            finally
            {
                refreshing = false;
            }
        }
    }
}
