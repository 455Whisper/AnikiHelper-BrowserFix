using System;
using System.Linq;
using System.Threading.Tasks;
using Playnite.SDK;
using Windows.Media.Control;

namespace AnikiHelper.Services.Spotify
{
    /// <summary>
    /// Watches Windows' media controls (the feed behind the volume-key media box) for the
    /// Spotify desktop app. Windows tells us the moment the song or play state changes, so no
    /// Spotify Web API requests are needed to notice a new song.
    /// </summary>
    internal sealed class LocalMediaWatcher : IDisposable
    {
        private readonly ILogger logger;
        private GlobalSystemMediaTransportControlsSessionManager manager;
        private GlobalSystemMediaTransportControlsSession session;
        private string lastKey;
        private bool disposed;

        /// <summary>Raised (on a background thread) when Spotify's song or play state changes.</summary>
        public event Action Changed;

        /// <summary>True while the Spotify desktop app has a media session on this PC.</summary>
        public bool HasSpotifySession
        {
            get { return session != null; }
        }

        public LocalMediaWatcher(ILogger logger)
        {
            this.logger = logger;
        }

        public async Task StartAsync()
        {
            try
            {
                manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                manager.SessionsChanged += (s, e) => PickSession();
                PickSession();
                logger?.Info("[AnikiHelper][Spotify] Windows media watcher started" + (session != null ? " (Spotify session found)." : " (no Spotify session yet)."));
            }
            catch (Exception ex)
            {
                // Older Windows or the API isn't available: callers fall back to Web API checks.
                logger?.Warn(ex, "[AnikiHelper][Spotify] Windows media watcher unavailable.");
                manager = null;
            }
        }

        private void PickSession()
        {
            if (disposed || manager == null)
            {
                return;
            }

            try
            {
                var found = manager.GetSessions()
                    .FirstOrDefault(x => (x.SourceAppUserModelId ?? string.Empty).IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0);
                if (found == session)
                {
                    return;
                }

                if (session != null)
                {
                    session.MediaPropertiesChanged -= OnMediaChanged;
                    session.PlaybackInfoChanged -= OnPlaybackChanged;
                }

                session = found;
                if (session != null)
                {
                    session.MediaPropertiesChanged += OnMediaChanged;
                    session.PlaybackInfoChanged += OnPlaybackChanged;
                }

                lastKey = null;
                Raise();
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Media session lookup failed.");
            }
        }

        private void OnMediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            CheckForChange();
        }

        private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            CheckForChange();
        }

        private async void CheckForChange()
        {
            var current = session;
            if (disposed || current == null)
            {
                return;
            }

            try
            {
                var props = await current.TryGetMediaPropertiesAsync();
                var playback = current.GetPlaybackInfo();
                var key = (props?.Title ?? string.Empty) + "|" + (props?.Artist ?? string.Empty) + "|" + playback?.PlaybackStatus;
                if (key == lastKey)
                {
                    return;
                }

                lastKey = key;
                Raise();
            }
            catch (Exception ex)
            {
                logger?.Debug("[AnikiHelper][Spotify] Media properties read failed: " + ex.Message);
            }
        }

        private void Raise()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Media change handler failed.");
            }
        }

        public void Dispose()
        {
            disposed = true;
            if (session != null)
            {
                session.MediaPropertiesChanged -= OnMediaChanged;
                session.PlaybackInfoChanged -= OnPlaybackChanged;
                session = null;
            }
        }
    }
}
