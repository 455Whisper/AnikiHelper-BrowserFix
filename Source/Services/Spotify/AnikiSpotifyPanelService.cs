using AnikiHelper.Services.WebBrowser;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AnikiHelper.Services.Spotify
{
    internal sealed class SpotifyListItem
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string ImageUrl { get; set; }
        public ICommand Play { get; set; }

        /// <summary>Set on playlist rows (and Liked Songs) so X can open them.</summary>
        public string PlaylistId { get; set; }
        public string ContextUri { get; set; }
        public bool IsLiked { get; set; }
    }

    /// <summary>
    /// Drop-down Spotify panel for the fullscreen theme: now playing, transport
    /// controls, like button, Recently played and the user's playlists. Fully
    /// controller-driven (input is routed here by the overlay input listener).
    /// </summary>
    internal sealed class AnikiSpotifyPanelService : IDisposable
    {
        private const double PanelWidth = 820;
        private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
        private const string GlyphPlay = "";
        private const string GlyphPause = "";
        private const string GlyphPrevious = "";
        private const string GlyphNext = "";
        private const string GlyphShuffle = "";
        private const string GlyphRepeatAll = "";
        private const string GlyphRepeatOne = "";
        private const string GlyphHeart = "";
        private const string GlyphHeartFill = "";
        private const string GlyphChevronDown = "";
        private const string GlyphChevronRight = "";

        private static readonly Brush Green = Frozen(Color.FromRgb(0x1D, 0xB9, 0x54));
        private static readonly Brush IconNormal = Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

        private readonly ILogger logger;
        private readonly SpotifyApiClient api;
        private readonly Action<string> openInBrowser;
        private readonly Action closeBrowser;
        private readonly Func<bool> canShowNowPlaying;
        private readonly SpotifyNowPlayingToast nowPlayingToast;
        private DispatcherTimer watchTimer;
        private bool watching;
        private string watchedTrackUri;
        private bool watchBaselineSet;
        private readonly ObservableCollection<SpotifyListItem> recentItems = new ObservableCollection<SpotifyListItem>();
        private readonly ObservableCollection<SpotifyListItem> searchItems = new ObservableCollection<SpotifyListItem>();
        private Button searchButton;
        private TextBlock searchText;
        private FrameworkElement searchSection;
        private Button searchHeader;
        private TextBlock searchChevron;
        private ItemsControl searchList;
        private TextBlock searchStatus;
        private string lastSearch;
        private int searchVersion;
        private bool keyboardSessionOpen;

        /// <summary>
        /// Opens the Aniki on-screen keyboard: (initial text, on submit(text, _), on cancel).
        /// Set by the plugin; used by the search box.
        /// </summary>
        public Action<string, Action<string, bool>, Action> OpenKeyboard { get; set; }
        private readonly ObservableCollection<SpotifyListItem> playlistItems = new ObservableCollection<SpotifyListItem>();

        private Window window;
        private double targetTop;
        private FrameworkElement root;
        private TranslateTransform slide;
        private Grid mainView;
        private StackPanel setupView;
        private TextBlock setupTitle;
        private TextBlock setupText;
        private Button setupPrimary;
        private Button setupSecondary;
        private Image art;
        private Image backgroundArt;
        private Grid backgroundHost;
        private static ImageSource defaultBackground;
        private TextBlock titleText;
        private TextBlock artistText;
        private TextBlock deviceText;
        private Button heartButton;
        private Button shuffleButton;
        private Button previousButton;
        private Button playButton;
        private Button nextButton;
        private Button repeatButton;
        private Grid progressTrack;
        private Rectangle progressFill;
        private ScrollViewer listScroll;
        private Button recentHeader;
        private Button playlistsHeader;
        private TextBlock recentChevron;
        private TextBlock playlistsChevron;
        private ItemsControl recentList;
        private ItemsControl playlistList;
        private FrameworkElement libraryView;
        private FrameworkElement playlistView;
        private Image playlistCover;
        private TextBlock playlistTitle;
        private TextBlock playlistSubtitle;
        private TextBlock playlistStatus;
        private ItemsControl playlistTracksList;
        private readonly ObservableCollection<SpotifyListItem> playlistTracks = new ObservableCollection<SpotifyListItem>();
        private IInputElement playlistReturnFocus;
        private double playlistReturnOffset;
        private int playlistLoadVersion;
        private HashSet<string> hiddenPlaylists;
        private TextBlock listStatus;
        private Border toast;
        private TextBlock toastText;

        private DispatcherTimer pollTimer;
        private DispatcherTimer progressTimer;
        private DispatcherTimer toastTimer;
        private CancellationTokenSource signInCancellation;
        private bool polling;
        private bool closing;
        private bool listsLoaded;
        private bool signingIn;

        private string trackUri;
        private string currentImageUrl;
        private bool isPlaying;
        private bool shuffleOn;
        private string repeatState = "off";
        private bool liked;
        private double progressMs;
        private double durationMs;
        private DateTime progressStampUtc;
        private string userId;
        private string preferredDeviceId;

        private DateTime nextStickNavUtc = DateTime.MinValue;
        private int lastStickDirection;

        private object exclusiveGameController;
        private bool exclusivePreviousStandardProcessing;
        private bool exclusiveActive;

        public AnikiSpotifyPanelService(
            ILogger logger,
            string dataDirectory,
            Action<string> openInBrowser,
            Action closeBrowser,
            Func<bool> canShowNowPlaying)
        {
            this.logger = logger;
            this.openInBrowser = openInBrowser;
            this.closeBrowser = closeBrowser;
            this.canShowNowPlaying = canShowNowPlaying;
            api = new SpotifyApiClient(logger, dataDirectory);
            nowPlayingToast = new SpotifyNowPlayingToast(System.IO.Path.Combine(dataDirectory, "logo.png"));
            LogoPath = System.IO.Path.Combine(dataDirectory, "logo.png");
            RunOnUi(StartNowPlayingWatcher);

            // Song changes come from Windows' media controls instantly, so the Web API is only
            // asked once per change (plus a slow safety check).
            mediaWatcher = new LocalMediaWatcher(logger);
            mediaWatcher.Changed += () =>
            {
                currentlyPlayingStale = true;
                RunOnUi(() => NowPlayingChanged?.Invoke());
            };
            var ignoredStart = mediaWatcher.StartAsync();
        }

        private readonly LocalMediaWatcher mediaWatcher;
        private bool currentlyPlayingStale = true;

        /// <summary>Raised on the UI thread the moment Spotify's song or play state changes on this PC.</summary>
        public event Action NowPlayingChanged;

        // ======================= song-change card =======================

        private void StartNowPlayingWatcher()
        {
            if (watchTimer != null)
            {
                return;
            }

            watchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            watchTimer.Tick += async (s, e) => await WatchNowPlayingAsync();
            watchTimer.Start();
            NowPlayingChanged += async () => await WatchNowPlayingAsync();

            var prewarm = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(6) };
            prewarm.Tick += (s, e) =>
            {
                prewarm.Stop();
                try
                {
                    if (window == null && Application.Current?.MainWindow?.IsLoaded == true)
                    {
                        EnsureWindow();
                    }
                }
                catch
                {
                }
            };
            prewarm.Start();
        }

        private async Task WatchNowPlayingAsync()
        {
            if (watching)
            {
                return;
            }

            var allowed = false;
            try
            {
                allowed = !IsOpen && !signingIn && api.IsSignedIn && canShowNowPlaying?.Invoke() == true;
            }
            catch
            {
            }

            if (!allowed)
            {
                // Changes that happen elsewhere (in a game, the browser, this panel) are not announced later.
                watchBaselineSet = false;
                nowPlayingToast.HideImmediately();
                return;
            }

            watching = true;
            try
            {
                var body = await FetchCurrentlyPlayingAsync();
                var item = body?["item"] as JObject;
                var uri = (string)item?["uri"];
                var playingNow = (bool?)body?["is_playing"] ?? false;

                if (!watchBaselineSet)
                {
                    watchBaselineSet = true;
                    watchedTrackUri = uri;
                    return;
                }

                if (string.IsNullOrEmpty(uri) || uri == watchedTrackUri)
                {
                    return;
                }

                watchedTrackUri = uri;
                if (!playingNow || canShowNowPlaying?.Invoke() != true || IsOpen)
                {
                    return;
                }

                var artists = item["artists"] as JArray;
                var artist = artists != null
                    ? string.Join(", ", artists.Select(a => (string)a["name"]).Where(n => !string.IsNullOrEmpty(n)))
                    : (string)item["show"]?["name"];
                var images = (item["album"]?["images"] ?? item["images"] ?? item["show"]?["images"]) as JArray;

                nowPlayingToast.Show(
                    (string)item["name"],
                    artist,
                    PickImage(images, 300),
                    (double?)body["progress_ms"] ?? 0,
                    (double?)item["duration_ms"] ?? 0,
                    playingNow);
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Now playing check failed.");
            }
            finally
            {
                watching = false;
            }
        }

        // The user's logo.png for now-playing cards (used by the visualizer's own card).
        public string LogoPath { get; }

        public event Action<bool> OpenStateChanged;

        public bool IsOpen { get; private set; }

        public bool IsSignedIn
        {
            get { return api.IsSignedIn; }
        }

        /// <summary>The raw "currently playing" response, for other views (e.g. the visualizer).</summary>
        public async Task<JObject> FetchCurrentlyPlayingAsync()
        {
            if (!api.IsSignedIn)
            {
                return null;
            }

            // Shared by the library toast, the visualizer and the AFK screen. With the Spotify app
            // on this PC, Windows reports changes, so the answer is reused until something changes
            // (checked anyway once a minute); otherwise (e.g. the phone is playing) every 15 s.
            var age = (DateTime.UtcNow - currentlyPlayingFetchedUtc).TotalSeconds;
            var maxAge = mediaWatcher != null && mediaWatcher.HasSpotifySession ? 60 : 15;
            if (currentlyPlayingTask != null && !currentlyPlayingStale && age < maxAge)
            {
                return WithProgress(await currentlyPlayingTask);
            }

            if (currentlyPlayingTask != null && !currentlyPlayingTask.IsCompleted)
            {
                return WithProgress(await currentlyPlayingTask);
            }

            currentlyPlayingStale = false;
            currentlyPlayingFetchedUtc = DateTime.UtcNow;
            currentlyPlayingTask = FetchCurrentlyPlayingNowAsync();
            return await currentlyPlayingTask;
        }

        /// <summary>A reused answer, with the song position moved on by the time since it was fetched.</summary>
        private JObject WithProgress(JObject body)
        {
            if (body == null || !((bool?)body["is_playing"] ?? false) || body["progress_ms"] == null)
            {
                return body;
            }

            var copy = (JObject)body.DeepClone();
            var progress = (double)copy["progress_ms"] + (DateTime.UtcNow - currentlyPlayingFetchedUtc).TotalMilliseconds;
            var duration = (double?)copy["item"]?["duration_ms"];
            copy["progress_ms"] = duration.HasValue ? Math.Min(progress, duration.Value) : progress;
            return copy;
        }

        private Task<JObject> currentlyPlayingTask;
        private DateTime currentlyPlayingFetchedUtc;

        private async Task<JObject> FetchCurrentlyPlayingNowAsync()
        {
            var result = await api.SendAsync(HttpMethod.Get, "/me/player/currently-playing?additional_types=episode");
            return result.Body as JObject;
        }

        // ======================= open / close =======================

        public void Toggle(FrameworkElement anchor)
        {
            RunOnUi(() =>
            {
                if (IsOpen)
                {
                    Close();
                }
                else
                {
                    Open(anchor);
                }
            });
        }

        private bool audioOnly;
        private Action audioOnlyClosed;

        /// <summary>
        /// Opens only the Playback devices / Mixer box (no Spotify panel), e.g. from the web
        /// browser. <paramref name="onClosed"/> runs after it closes, to give focus back.
        /// </summary>
        public void OpenAudioOnly(Action onClosed)
        {
            RunOnUi(() =>
            {
                if (IsOpen || signingIn)
                {
                    return;
                }

                audioOnly = true;
                audioOnlyClosed = onClosed;
                Open(null);
            });
        }

        public void Open(FrameworkElement anchor)
        {
            RunOnUi(() =>
            {
                if (IsOpen || signingIn)
                {
                    return;
                }

                if (audioOnlyClosed == null)
                {
                    audioOnly = false;
                }

                try
                {
                    EnsureWindow();
                    if (window.Owner == null && Application.Current?.MainWindow?.IsLoaded == true && Application.Current.MainWindow != window)
                    {
                        try { window.Owner = Application.Current.MainWindow; } catch { }
                    }

                    api.Reload();
                    PositionWindow(anchor);
                    closing = false;
                    IsOpen = true;
                    RememberPlayniteFocus();

                    // The A/Enter press that opened the panel can also reach it (as a pad state
                    // and/or a key). Ignore activation briefly so it can't pause or start a song.
                    lastActivateUtc = DateTime.UtcNow.AddMilliseconds(450);
                    window.BeginAnimation(Window.TopProperty, null);
                    window.Top = targetTop - window.Height;
                    window.Show();
                    window.Activate();
                    AcquireExclusiveController();
                    OpenStateChanged?.Invoke(true);

                    // Slide the window itself: moving a window doesn't redraw its contents,
                    // so the motion stays smooth even though the panel is large.
                    slide.BeginAnimation(TranslateTransform.YProperty, null);
                    slide.Y = 0;
                    root.BeginAnimation(UIElement.OpacityProperty, null);
                    root.Opacity = 1;
                    var anim = new DoubleAnimation(targetTop - window.Height, targetTop, TimeSpan.FromMilliseconds(380))
                    {
                        EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
                    };
                    window.BeginAnimation(Window.TopProperty, anim);

                    if (audioOnly)
                    {
                        root.Visibility = Visibility.Hidden;
                        OpenSide();
                    }
                    else
                    {
                        root.Visibility = Visibility.Visible;
                        UpdateViewForAuthState();
                    }
                }
                catch (Exception ex)
                {
                    logger?.Error(ex, "[AnikiHelper][Spotify] Failed to open the Spotify panel.");
                    IsOpen = false;
                    audioOnly = false;
                    audioOnlyClosed = null;
                }
            });
        }

        public void Close()
        {
            RunOnUi(() =>
            {
                if (!IsOpen || window == null || closing)
                {
                    return;
                }

                closing = true;
                IsOpen = false;
                StopTimers();
                CloseSide(false);
                ReleaseExclusiveController();
                var returnTo = audioOnly ? audioOnlyClosed : null;
                var wasAudioOnly = audioOnly;
                audioOnly = false;
                audioOnlyClosed = null;

                var anim = new DoubleAnimation(window.Top, targetTop - window.Height, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                anim.Completed += (s, e) =>
                {
                    if (closing)
                    {
                        window.Hide();
                        window.BeginAnimation(Window.TopProperty, null);
                        closing = false;
                        if (wasAudioOnly)
                        {
                            try { returnTo?.Invoke(); } catch { }
                        }
                        else
                        {
                            ActivatePlayniteMainWindow();
                            LogWindowStateSoon();
                        }
                    }
                };
                window.BeginAnimation(Window.TopProperty, anim);
                OpenStateChanged?.Invoke(false);
                if (wasAudioOnly)
                {
                    try { returnTo?.Invoke(); } catch { }
                }
                else
                {
                    ActivatePlayniteMainWindow();
                }
            });
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        // Diagnostics for the "screen stays dark after closing" report.
        private async void LogWindowStateSoon()
        {
            try
            {
                await Task.Delay(1500);
                var main = Application.Current?.MainWindow;
                var foreground = GetForegroundWindow();
                var title = new System.Text.StringBuilder(256);
                GetWindowText(foreground, title, title.Capacity);
                uint pid;
                GetWindowThreadProcessId(foreground, out pid);
                string process = "?";
                try { process = Process.GetProcessById((int)pid).ProcessName; } catch { }
                var others = string.Join(", ", (Application.Current?.Windows.OfType<Window>() ?? Enumerable.Empty<Window>())
                    .Where(w => w != null && w != main && w.IsVisible)
                    .Select(w => w.GetType().Name + "('" + w.Title + "', opacity " + w.Opacity.ToString("0.00") + ")"));
                logger?.Info(
                    "[AnikiHelper][Spotify][CloseCheck] MainActive=" + (main?.IsActive) +
                    " MainOpacity=" + (main?.Opacity.ToString("0.00")) +
                    " MainEnabled=" + (main?.IsEnabled) +
                    " Foreground='" + title + "' (" + process + ")" +
                    " OtherVisibleWindows=[" + others + "]");
            }
            catch
            {
            }
        }

        private void ActivatePlayniteMainWindow()
        {
            try
            {
                var main = Application.Current?.MainWindow;
                if (main == null || main == window)
                {
                    return;
                }

                // The theme hides parts of the library/hub while Playnite's window is inactive,
                // so focus must come back to it (not to whatever window was behind the panel).
                var handle = new System.Windows.Interop.WindowInteropHelper(main).Handle;
                if (handle != IntPtr.Zero)
                {
                    SetForegroundWindow(handle);
                }

                main.Activate();

                // Put focus back on whatever was selected before the panel opened. Focusing the
                // window itself leaves nothing selected, and the theme dims the library/hub then.
                var previous = previousPlayniteFocus as UIElement;
                previousPlayniteFocus = null;
                if (previous != null)
                {
                    main.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (previous.IsVisible && previous.Focusable && previous.IsEnabled)
                        {
                            previous.Focus();
                            Keyboard.Focus(previous);
                        }
                    }), DispatcherPriority.Input);
                }
            }
            catch
            {
            }
        }

        private IInputElement previousPlayniteFocus;

        private void RememberPlayniteFocus()
        {
            try
            {
                var main = Application.Current?.MainWindow;
                var focused = Keyboard.FocusedElement as DependencyObject;
                if (main != null && focused != null && main != window && Window.GetWindow(focused) == main)
                {
                    previousPlayniteFocus = (IInputElement)focused;
                }
            }
            catch
            {
            }
        }

        private void PositionWindow(FrameworkElement anchor)
        {
            var main = Application.Current?.MainWindow;
            double screenLeft = 0, screenTop = 0, screenWidth = SystemParameters.PrimaryScreenWidth, screenHeight = SystemParameters.PrimaryScreenHeight;

            try
            {
                if (main != null)
                {
                    var handle = new System.Windows.Interop.WindowInteropHelper(main).Handle;
                    var screen = System.Windows.Forms.Screen.FromHandle(handle);
                    var source = PresentationSource.FromVisual(main);
                    var toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                    var topLeft = toDip.Transform(new Point(screen.Bounds.Left, screen.Bounds.Top));
                    var bottomRight = toDip.Transform(new Point(screen.Bounds.Right, screen.Bounds.Bottom));
                    screenLeft = topLeft.X;
                    screenTop = topLeft.Y;
                    screenWidth = bottomRight.X - topLeft.X;
                    screenHeight = bottomRight.Y - topLeft.Y;
                }
            }
            catch
            {
            }

            // Always centred horizontally on the screen, dropping down from the top edge.
            var centerX = screenLeft + screenWidth / 2;

            // Designed for a 1460x1000 area; shrink uniformly on smaller (or Windows-scaled) screens
            // so the panel and the volume box always fit.
            var scale = Math.Min(1.0, Math.Min(screenWidth / 1460.0, screenHeight / 1000.0));
            windowHost.LayoutTransform = new ScaleTransform(scale, scale);

            var width = PanelWidth;
            var height = Math.Min(1000, Math.Max(560, screenHeight * 0.85 / scale));
            var scaledWidth = width * scale;
            var left = Math.Max(screenLeft + 10, Math.Min(screenLeft + screenWidth - scaledWidth - 10, centerX - scaledWidth / 2));

            window.Width = (width + SideGap + SideWidth) * scale;
            window.Height = height * scale;
            window.Left = left;
            window.Top = screenTop;
            targetTop = screenTop;
            root.Width = width;
            root.Height = height;

            var sideTop = Math.Min(170, Math.Max(60, height - 620));
            sidePanel.Margin = new Thickness(width + SideGap, sideTop, 0, 0);
            sidePanel.Height = Math.Min(560, height - sideTop - 20);

            if (audioOnly)
            {
                // Just the volume box, dropping down at the top-left of the screen.
                window.Width = SideWidth * scale;
                window.Height = (sidePanel.Height + 40) * scale;
                window.Left = screenLeft + (40 * scale);
                sidePanel.Margin = new Thickness(0, 20, 0, 0);
            }
        }

        private Grid windowHost;
        private Border sidePanel;
        private TranslateTransform sideSlide;
        private TextBlock tabTitle;
        private TextBlock sideStatus;
        private ScrollViewer sideScroll;
        private ItemsControl audioList;
        private Button prevTabButton;
        private Button nextTabButton;
        private Button sideToggleButton;
        private DispatcherTimer sideRefreshTimer;
        private AudioMixerSource mixer;
        private readonly ObservableCollection<AudioEntry> audioEntries = new ObservableCollection<AudioEntry>();
        private bool showingMixer;

        private bool IsSideOpen
        {
            get { return sidePanel != null && sidePanel.Visibility == Visibility.Visible; }
        }

        private void BuildSidePanel()
        {
            sidePanel = (Border)XamlReader.Parse(SideXaml);
            sideSlide = (TranslateTransform)sidePanel.FindName("SideSlide");
            tabTitle = (TextBlock)sidePanel.FindName("TabTitle");
            sideStatus = (TextBlock)sidePanel.FindName("SideStatus");
            sideScroll = (ScrollViewer)sidePanel.FindName("SideScroll");
            audioList = (ItemsControl)sidePanel.FindName("AudioList");
            prevTabButton = (Button)sidePanel.FindName("PrevTabBtn");
            nextTabButton = (Button)sidePanel.FindName("NextTabBtn");
            audioList.ItemsSource = audioEntries;
            sidePanel.Visibility = Visibility.Collapsed;
            sidePanel.HorizontalAlignment = HorizontalAlignment.Left;
            sidePanel.VerticalAlignment = VerticalAlignment.Top;

            prevTabButton.Click += (s, e) => SwitchTab();
            nextTabButton.Click += (s, e) => SwitchTab();

            sideRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            sideRefreshTimer.Tick += (s, e) =>
            {
                if (IsSideOpen && mixer != null)
                {
                    mixer.RefreshValues(audioEntries);
                }
            };
        }

        private void ToggleSide()
        {
            if (IsSideOpen)
            {
                CloseSide(true);
            }
            else
            {
                OpenSide();
            }
        }

        private void OpenSide()
        {
            if (sidePanel == null || IsSideOpen)
            {
                return;
            }

            if (mixer == null)
            {
                mixer = new AudioMixerSource(logger, api.DataDirectory);
                mixer.DeviceVolumeChanged += (key, volume, muted) => RunOnUi(() =>
                {
                    var entry = audioEntries.FirstOrDefault(e => e.Key == key);
                    entry?.UpdateFromSystem(volume);
                    entry?.UpdateMuteFromSystem(muted);
                });
                mixer.DevicesChanged += () => RunOnUi(ScheduleDeviceListReload);
            }

            mixer.StartWatching();

            sidePanel.Visibility = Visibility.Visible;
            sideToggleButton.Content = "";
            sidePanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            sideSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-30, 0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            LoadSideList();
            sideRefreshTimer.Start();
        }

        private void CloseSide(bool focusToggle)
        {
            if (sidePanel == null || !IsSideOpen)
            {
                return;
            }

            sideRefreshTimer.Stop();
            mixer?.StopWatching();
            sidePanel.BeginAnimation(UIElement.OpacityProperty, null);
            sidePanel.Visibility = Visibility.Collapsed;
            sideToggleButton.Content = "";
            if (focusToggle)
            {
                FocusLater(sideToggleButton);
            }
        }

        private DispatcherTimer deviceReloadTimer;

        // Devices often fire several notifications at once (plug-in, then default change);
        // wait a moment and rebuild the list once.
        private void ScheduleDeviceListReload()
        {
            if (!IsSideOpen || showingMixer)
            {
                return;
            }

            if (deviceReloadTimer == null)
            {
                deviceReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                deviceReloadTimer.Tick += (s, e) =>
                {
                    deviceReloadTimer.Stop();
                    if (IsSideOpen && !showingMixer)
                    {
                        LoadSideList();
                    }
                };
            }

            deviceReloadTimer.Stop();
            deviceReloadTimer.Start();
        }

        private void SelectOutputDevice(AudioEntry entry)
        {
            if (entry == null || entry.SliderEnabled || string.IsNullOrEmpty(entry.DeviceId))
            {
                return;
            }

            if (mixer.SwitchOutputTo(entry.DeviceId))
            {
                LoadSideList();
                ShowToast("Sound now plays on " + entry.Name);
            }
            else
            {
                ShowToast("Windows wouldn't switch to that device.");
            }
        }

        private void SwitchTab()
        {
            showingMixer = !showingMixer;
            LoadSideList();
        }

        private void LoadSideList()
        {
            tabTitle.Text = showingMixer ? "Mixer" : "Playback devices";
            audioEntries.Clear();
            var entries = showingMixer ? mixer.GetApps() : mixer.GetDevices();
            foreach (var entry in entries)
            {
                if (entry.IsSelectable)
                {
                    var picked = entry;
                    entry.Select = new RelayCommand(() => SelectOutputDevice(picked));
                }

                audioEntries.Add(entry);
            }

            sideStatus.Text = showingMixer ? "No apps are playing sound." : "No playback devices found.";
            sideStatus.Visibility = audioEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            sideScroll.ScrollToTop();

            // Land on the first slider; fall back to the tab arrow.
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                var firstSlider = FindFirst<Slider>(audioList);
                FocusLater(firstSlider != null ? (UIElement)firstSlider : nextTabButton);
            }), DispatcherPriority.Loaded);
        }

        private static T FindFirst<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var match = child as T ?? FindFirst<T>(child);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        // ======================= devices / mixer side box =======================

        private const double SideWidth = 460;
        private const double SideGap = 18;

        private const string SideXaml = @"
<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Width='460' Background='#F2141414' BorderBrush='#F0FFFFFF' BorderThickness='3'
        CornerRadius='18' Padding='18,16,14,16'
        TextElement.FontFamily='Segoe UI Variable Display, Segoe UI'>
  <Border.RenderTransform>
    <TranslateTransform x:Name='SideSlide'/>
  </Border.RenderTransform>
  <Border.Resources>
    <Style x:Key='SmallIconBtn' TargetType='Button'>
      <Setter Property='Foreground' Value='#FFFFFF'/>
      <Setter Property='FontFamily' Value='Segoe Fluent Icons, Segoe MDL2 Assets'/>
      <Setter Property='FontSize' Value='22'/>
      <Setter Property='Width' Value='54'/>
      <Setter Property='Height' Value='54'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bd' Background='Transparent' CornerRadius='27' BorderThickness='3' BorderBrush='Transparent'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='#22FFFFFF'/>
              </Trigger>
              <Trigger Property='IsKeyboardFocused' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='#30FFFFFF'/>
                <Setter TargetName='Bd' Property='BorderBrush' Value='#1DB954'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='VolumeSlider' TargetType='Slider'>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Minimum' Value='0'/>
      <Setter Property='Maximum' Value='100'/>
      <Setter Property='SmallChange' Value='2'/>
      <Setter Property='LargeChange' Value='5'/>
      <Setter Property='IsMoveToPointEnabled' Value='True'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Slider'>
            <Border x:Name='Frame' Padding='10,2' CornerRadius='17' BorderThickness='2' BorderBrush='Transparent' Background='Transparent'>
              <Grid Height='30'>
                <Border Height='6' CornerRadius='3' Background='#33FFFFFF' VerticalAlignment='Center'/>
                <Track x:Name='PART_Track'>
                  <Track.DecreaseRepeatButton>
                    <RepeatButton Command='Slider.DecreaseLarge' Focusable='False' IsTabStop='False'>
                      <RepeatButton.Template>
                        <ControlTemplate TargetType='RepeatButton'>
                          <Grid Background='Transparent'>
                            <Border Height='6' CornerRadius='3' Background='#5EE3E8' VerticalAlignment='Center'/>
                          </Grid>
                        </ControlTemplate>
                      </RepeatButton.Template>
                    </RepeatButton>
                  </Track.DecreaseRepeatButton>
                  <Track.IncreaseRepeatButton>
                    <RepeatButton Command='Slider.IncreaseLarge' Focusable='False' IsTabStop='False'>
                      <RepeatButton.Template>
                        <ControlTemplate TargetType='RepeatButton'>
                          <Grid Background='Transparent'/>
                        </ControlTemplate>
                      </RepeatButton.Template>
                    </RepeatButton>
                  </Track.IncreaseRepeatButton>
                  <Track.Thumb>
                    <Thumb Focusable='False'>
                      <Thumb.Template>
                        <ControlTemplate TargetType='Thumb'>
                          <Ellipse Width='20' Height='20' Fill='#FFFFFF'/>
                        </ControlTemplate>
                      </Thumb.Template>
                    </Thumb>
                  </Track.Thumb>
                </Track>
              </Grid>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsKeyboardFocused' Value='True'>
                <Setter TargetName='Frame' Property='BorderBrush' Value='#1DB954'/>
                <Setter TargetName='Frame' Property='Background' Value='#1FFFFFFF'/>
              </Trigger>
              <Trigger Property='IsEnabled' Value='False'>
                <Setter TargetName='Frame' Property='Opacity' Value='0.3'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='DeviceNameBtn' TargetType='Button'>
      <Setter Property='Foreground' Value='#FFFFFF'/>
      <Setter Property='FontSize' Value='21'/>
      <Setter Property='HorizontalAlignment' Value='Center'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bd' Background='Transparent' CornerRadius='10' BorderThickness='2'
                    BorderBrush='Transparent' Padding='12,3'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <MultiTrigger>
                <MultiTrigger.Conditions>
                  <Condition Property='IsMouseOver' Value='True'/>
                  <Condition Property='IsEnabled' Value='True'/>
                </MultiTrigger.Conditions>
                <Setter TargetName='Bd' Property='Background' Value='#1FFFFFFF'/>
              </MultiTrigger>
              <Trigger Property='IsKeyboardFocused' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='#2EFFFFFF'/>
                <Setter TargetName='Bd' Property='BorderBrush' Value='#1DB954'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style TargetType='ScrollBar'>
      <Setter Property='Width' Value='8'/>
      <Setter Property='MinWidth' Value='8'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='ScrollBar'>
            <Grid>
              <Border CornerRadius='4' Background='#26FFFFFF'/>
              <Track x:Name='PART_Track' IsDirectionReversed='True'>
                <Track.Thumb>
                  <Thumb>
                    <Thumb.Template>
                      <ControlTemplate TargetType='Thumb'>
                        <Border CornerRadius='4' Background='#F2D94A'/>
                      </ControlTemplate>
                    </Thumb.Template>
                  </Thumb>
                </Track.Thumb>
              </Track>
            </Grid>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <DataTemplate x:Key='AudioRow'>
      <StackPanel Margin='0,6,0,12'>
        <Button Style='{StaticResource DeviceNameBtn}' Command='{Binding Select}'
                IsEnabled='{Binding IsSelectable}'>
          <TextBlock Text='{Binding Name}' TextTrimming='CharacterEllipsis'/>
        </Button>
        <Grid Margin='0,6,0,0'>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width='Auto'/>
            <ColumnDefinition Width='*'/>
            <ColumnDefinition Width='56'/>
          </Grid.ColumnDefinitions>
          <Button Command='{Binding ToggleMute}' IsEnabled='{Binding CanMute}' Focusable='False'
                  Cursor='Hand' VerticalAlignment='Stretch' Margin='0' Padding='4,0'>
            <Button.Template>
              <ControlTemplate TargetType='Button'>
                <Border Background='Transparent' Padding='{TemplateBinding Padding}'>
                  <TextBlock Text='{Binding IconGlyph}' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='20'
                             Width='24' Height='20' LineHeight='20' LineStackingStrategy='BlockLineHeight'
                             TextAlignment='Left' VerticalAlignment='Center' Foreground='{Binding IconBrush}'/>
                </Border>
              </ControlTemplate>
            </Button.Template>
          </Button>
          <Slider Grid.Column='1' Margin='6,0' Style='{StaticResource VolumeSlider}'
                  IsEnabled='{Binding SliderEnabled}'
                  Value='{Binding Volume, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}'/>
          <TextBlock Grid.Column='2' Text='{Binding Percent}' FontSize='17' Foreground='#CFCFCF'
                     VerticalAlignment='Center' TextAlignment='Right'/>
        </Grid>
      </StackPanel>
    </DataTemplate>
  </Border.Resources>
  <DockPanel>
    <Grid DockPanel.Dock='Top' Margin='0,0,4,10'>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width='Auto'/>
        <ColumnDefinition Width='*'/>
        <ColumnDefinition Width='Auto'/>
      </Grid.ColumnDefinitions>
      <Button x:Name='PrevTabBtn' Style='{StaticResource SmallIconBtn}' Content='&#xE76B;'/>
      <TextBlock x:Name='TabTitle' Grid.Column='1' FontSize='25' FontWeight='SemiBold' Foreground='#FFFFFF'
                 HorizontalAlignment='Center' VerticalAlignment='Center' Text='Playback devices'/>
      <Button x:Name='NextTabBtn' Grid.Column='2' Style='{StaticResource SmallIconBtn}' Content='&#xE76C;'/>
    </Grid>
    <TextBlock x:Name='SideStatus' DockPanel.Dock='Bottom' FontSize='16' Foreground='#9A9A9A'
               HorizontalAlignment='Center' Margin='0,8,0,0' Visibility='Collapsed'/>
    <ScrollViewer x:Name='SideScroll' Focusable='False' VerticalScrollBarVisibility='Visible'
                  HorizontalScrollBarVisibility='Disabled' PanningMode='VerticalOnly'>
      <ItemsControl x:Name='AudioList' Focusable='False' Margin='0,0,10,0'
                    ItemTemplate='{StaticResource AudioRow}'
                    KeyboardNavigation.DirectionalNavigation='Continue'/>
    </ScrollViewer>
  </DockPanel>
</Border>";

        // ======================= window construction =======================

        private void EnsureWindow()
        {
            if (window != null)
            {
                return;
            }

            root = (FrameworkElement)XamlReader.Parse(PanelXaml);
            slide = (TranslateTransform)root.FindName("Slide");
            mainView = (Grid)root.FindName("MainView");
            setupView = (StackPanel)root.FindName("SetupView");
            setupTitle = (TextBlock)root.FindName("SetupTitle");
            setupText = (TextBlock)root.FindName("SetupText");
            setupPrimary = (Button)root.FindName("SetupPrimary");
            setupSecondary = (Button)root.FindName("SetupSecondary");
            art = (Image)root.FindName("Art");
            backgroundArt = (Image)root.FindName("BgArt");
            backgroundHost = (Grid)root.FindName("BgHost");
            lastBackgroundSource = GetDefaultBackground();
            backgroundArt.Source = BlurToBitmap(lastBackgroundSource as BitmapSource) ?? lastBackgroundSource;
            root.SizeChanged += (s, e) =>
            {
                // Round only the bottom corners: push the top ones above the panel.
                var w = e.NewSize.Width;
                var h = e.NewSize.Height;
                backgroundHost.Clip = new RectangleGeometry(new Rect(0, -24, w, h + 24), 20, 20);
            };
            titleText = (TextBlock)root.FindName("TitleText");
            artistText = (TextBlock)root.FindName("ArtistText");
            deviceText = (TextBlock)root.FindName("DeviceText");
            heartButton = (Button)root.FindName("HeartBtn");
            shuffleButton = (Button)root.FindName("ShuffleBtn");
            previousButton = (Button)root.FindName("PrevBtn");
            playButton = (Button)root.FindName("PlayBtn");
            nextButton = (Button)root.FindName("NextBtn");
            repeatButton = (Button)root.FindName("RepeatBtn");
            progressTrack = (Grid)root.FindName("ProgressTrack");
            progressFill = (Rectangle)root.FindName("ProgressFill");
            listScroll = (ScrollViewer)root.FindName("ListScroll");
            recentHeader = (Button)root.FindName("RecentHeader");
            playlistsHeader = (Button)root.FindName("PlaylistsHeader");
            recentChevron = (TextBlock)root.FindName("RecentChevron");
            playlistsChevron = (TextBlock)root.FindName("PlaylistsChevron");
            recentList = (ItemsControl)root.FindName("RecentList");
            playlistList = (ItemsControl)root.FindName("PlaylistList");
            listStatus = (TextBlock)root.FindName("ListStatus");
            libraryView = (FrameworkElement)root.FindName("LibraryView");
            playlistView = (FrameworkElement)root.FindName("PlaylistView");
            playlistCover = (Image)root.FindName("PlaylistCover");
            playlistTitle = (TextBlock)root.FindName("PlaylistTitle");
            playlistSubtitle = (TextBlock)root.FindName("PlaylistSubtitle");
            playlistStatus = (TextBlock)root.FindName("PlaylistStatus");
            playlistTracksList = (ItemsControl)root.FindName("PlaylistTracks");
            playlistTracksList.ItemsSource = playlistTracks;
            toast = (Border)root.FindName("Toast");
            toastText = (TextBlock)root.FindName("ToastText");

            recentList.ItemsSource = recentItems;
            playlistList.ItemsSource = playlistItems;
            searchButton = (Button)root.FindName("SearchBtn");
            searchText = (TextBlock)root.FindName("SearchText");
            searchSection = (FrameworkElement)root.FindName("SearchSection");
            searchHeader = (Button)root.FindName("SearchHeader");
            searchChevron = (TextBlock)root.FindName("SearchChevron");
            searchList = (ItemsControl)root.FindName("SearchList");
            searchStatus = (TextBlock)root.FindName("SearchStatus");
            searchList.ItemsSource = searchItems;
            searchChevron.Text = GlyphChevronDown;
            searchButton.Click += (s, e) => OpenSearchKeyboard();
            searchHeader.Click += (s, e) => ToggleSection(searchList, searchChevron);

            heartButton.Content = GlyphHeart;
            shuffleButton.Content = GlyphShuffle;
            previousButton.Content = GlyphPrevious;
            playButton.Content = GlyphPlay;
            nextButton.Content = GlyphNext;
            repeatButton.Content = GlyphRepeatAll;
            recentChevron.Text = GlyphChevronDown;
            playlistsChevron.Text = GlyphChevronDown;

            heartButton.Click += async (s, e) => await ToggleLikeAsync();
            shuffleButton.Click += async (s, e) => await ToggleShuffleAsync();
            previousButton.Click += async (s, e) => await PreviousAsync();
            playButton.Click += async (s, e) => await TogglePlayAsync();
            nextButton.Click += async (s, e) => await NextAsync();
            repeatButton.Click += async (s, e) => await CycleRepeatAsync();
            recentHeader.Click += (s, e) => ToggleSection(recentList, recentChevron);
            playlistsHeader.Click += (s, e) => ToggleSection(playlistList, playlistsChevron);
            setupPrimary.Click += (s, e) => OnSetupPrimary();
            setupSecondary.Click += (s, e) => OnSetupSecondary();
            progressTrack.SizeChanged += (s, e) => UpdateProgressBar();

            window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowActivated = true,
                Title = "Spotify"
            };

            sideToggleButton = (Button)root.FindName("SideToggleBtn");
            sideToggleButton.Content = "\uE76C";
            sideToggleButton.Click += (s, e) => ToggleSide();
            BuildSidePanel();
            root.HorizontalAlignment = HorizontalAlignment.Left;
            root.VerticalAlignment = VerticalAlignment.Top;
            windowHost = new Grid();
            KeyboardNavigation.SetDirectionalNavigation(windowHost, KeyboardNavigationMode.Contained);
            KeyboardNavigation.SetTabNavigation(windowHost, KeyboardNavigationMode.Cycle);
            windowHost.Children.Add(root);
            windowHost.Children.Add(sidePanel);
            window.Content = windowHost;

            try
            {
                var main = Application.Current?.MainWindow;
                if (main != null && main.IsLoaded)
                {
                    window.Owner = main;
                }
            }
            catch
            {
            }

            window.GotKeyboardFocus += (s, e) =>
            {
                var element = e.NewFocus as FrameworkElement;
                if (element != null && (IsInside(element, listScroll) || IsInside(element, sideScroll)))
                {
                    element.BringIntoView(new Rect(0, -60, element.ActualWidth, element.ActualHeight + 120));
                }
            };
            window.PreviewKeyDown += Window_PreviewKeyDown;
            if (!keyboardHookAttached)
            {
                // Keys pressed while another Playnite window (the library, the visualizer) has
                // keyboard focus never reach the panel. Catch them app-wide while it's open.
                keyboardHookAttached = true;
                InputManager.Current.PreProcessInput += OnAppPreProcessInput;
            }

            window.Closed += (s, e) =>
            {
                // Window was closed for real (Alt+F4 or Playnite shutting down): reset.
                var wasOpen = IsOpen;
                IsOpen = false;
                closing = false;
                StopTimers();
                ReleaseExclusiveController();
                window = null;
                if (wasOpen)
                {
                    OpenStateChanged?.Invoke(false);
                }
            };

            pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            pollTimer.Tick += async (s, e) => await RefreshPlayerAsync();
            progressTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
            progressTimer.Tick += (s, e) => UpdateProgressBar();
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            toastTimer.Tick += (s, e) =>
            {
                toastTimer.Stop();
                toast.Visibility = Visibility.Collapsed;
            };
        }

        private static bool IsInside(DependencyObject element, DependencyObject container)
        {
            var current = element;
            while (current != null)
            {
                if (current == container)
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
            }

            return false;
        }

        // Playnite/Steam also turn the controller into arrow/Enter key presses. Handle those here
        // through the same de-duplicated path as controller events, so one press = one action
        // (otherwise WPF's own key navigation moves a second time and buttons click twice).
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up:
                case Key.Down:
                case Key.Left:
                case Key.Right:
                    e.Handled = true;
                    if (!e.IsRepeat)
                    {
                        Move(e.Key == Key.Up ? FocusNavigationDirection.Up
                            : e.Key == Key.Down ? FocusNavigationDirection.Down
                            : e.Key == Key.Left ? FocusNavigationDirection.Left
                            : FocusNavigationDirection.Right);
                    }

                    break;
                case Key.Enter:
                case Key.Space:
                    e.Handled = true;
                    if (!e.IsRepeat)
                    {
                        ActivateFocused();
                    }

                    break;
                case Key.Escape:
                case Key.Back:
                case Key.BrowserBack:
                    e.Handled = true;
                    KeyboardBack();
                    break;
                case Key.MediaPlayPause:
                    e.Handled = true;
                    var ignored = TogglePlayAsync();
                    break;
            }
        }

        private DateTime lastActivateUtc = DateTime.MinValue;
        private DateTime lastBackUtc = DateTime.MinValue;
        private bool keyboardHookAttached;

        // Esc / Backspace: step back (playlist -> list, side tab -> main), then close.
        private void KeyboardBack()
        {
            if ((DateTime.UtcNow - lastBackUtc).TotalMilliseconds <= 250)
            {
                return;
            }

            lastBackUtc = DateTime.UtcNow;
            if (audioOnly)
            {
                Close();
            }
            else if (IsPlaylistViewOpen && !IsSideFocused())
            {
                ClosePlaylistView();
            }
            else if (IsSideOpen)
            {
                CloseSide(true);
            }
            else
            {
                Close();
            }
        }

        private void OnAppPreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            try
            {
                var key = e.StagingItem.Input as KeyEventArgs;
                if (key == null || key.RoutedEvent != Keyboard.PreviewKeyDownEvent || !IsOpen || window == null || closing || keyboardSessionOpen)
                {
                    return;
                }

                // Focus is inside the panel: its own key handler takes care of it.
                var focused = Keyboard.FocusedElement as DependencyObject;
                if (focused != null && Window.GetWindow(focused) == window)
                {
                    return;
                }

                var k = key.Key == Key.System ? key.SystemKey : key.Key;
                switch (k)
                {
                    case Key.Escape:
                    case Key.Back:
                    case Key.BrowserBack:
                        e.Cancel();
                        if (!key.IsRepeat)
                        {
                            KeyboardBack();
                        }

                        break;
                    case Key.Up:
                    case Key.Down:
                    case Key.Left:
                    case Key.Right:
                    case Key.Enter:
                    case Key.Space:
                        // Pull keyboard focus back into the panel so the next press works there.
                        e.Cancel();
                        window.Activate();
                        if (!(Keyboard.FocusedElement is DependencyObject now && Window.GetWindow(now) == window))
                        {
                            windowHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Keyboard hook failed.");
            }
        }

        private void ActivateFocused()
        {
            var now = DateTime.UtcNow;
            if ((now - lastActivateUtc).TotalMilliseconds < 250)
            {
                return;
            }

            lastActivateUtc = now;
            // A on a volume slider toggles mute (same as clicking the speaker icon).
            var focusedSlider = Keyboard.FocusedElement as Slider;
            var sliderEntry = focusedSlider?.DataContext as AudioEntry;
            if (sliderEntry != null && sliderEntry.CanMute && IsInside(focusedSlider, windowHost))
            {
                sliderEntry.SetMuted(!sliderEntry.IsMuted);
                return;
            }

            var button = Keyboard.FocusedElement as ButtonBase;
            if (button != null && IsInside(button, windowHost))
            {
                Invoke(button);
            }
            else
            {
                FocusLater(mainView.Visibility == Visibility.Visible ? playButton : setupPrimary);
            }
        }

        // ======================= search =======================

        private void OpenSearchKeyboard()
        {
            if (OpenKeyboard == null)
            {
                ShowToast("Keyboard isn't available");
                return;
            }

            keyboardSessionOpen = true;
            try
            {
                OpenKeyboard(
                    lastSearch ?? string.Empty,
                    (text, ignored) => RunOnUi(async () =>
                    {
                        keyboardSessionOpen = false;
                        RefocusPanel(searchButton);
                        await SearchAsync(text);
                    }),
                    () => RunOnUi(() =>
                    {
                        keyboardSessionOpen = false;
                        RefocusPanel(searchButton);
                    }));
            }
            catch (Exception ex)
            {
                keyboardSessionOpen = false;
                logger?.Warn(ex, "[AnikiHelper][Spotify] Could not open the keyboard for search.");
            }
        }

        private void RefocusPanel(IInputElement element)
        {
            if (window == null || !IsOpen)
            {
                return;
            }

            try
            {
                window.Activate();
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    element?.Focus();
                    if (element != null)
                    {
                        Keyboard.Focus(element);
                    }
                }), DispatcherPriority.Loaded);
            }
            catch
            {
            }
        }

        private async Task SearchAsync(string query)
        {
            query = (query ?? string.Empty).Trim();
            if (query.Length == 0)
            {
                return;
            }

            var version = ++searchVersion;
            lastSearch = query;
            searchText.Text = query;
            searchText.Foreground = Brushes.White;
            searchSection.Visibility = Visibility.Visible;
            searchList.Visibility = Visibility.Visible;
            searchChevron.Text = GlyphChevronDown;
            searchItems.Clear();
            searchStatus.Text = "Searching...";
            searchStatus.Visibility = Visibility.Visible;

            try
            {
                var result = await api.SendAsync(HttpMethod.Get,
                    "/search?q=" + Uri.EscapeDataString(query) + "&type=track,artist,album,playlist&limit=10");
                if (version != searchVersion)
                {
                    return;
                }

                if (!result.Ok)
                {
                    searchStatus.Text = "Search failed. Try again.";
                    logger?.Warn("[AnikiHelper][Spotify] Search failed for '" + query + "'.");
                    return;
                }

                var body = result.Body;
                foreach (var track in Items(body?["tracks"]).Take(8))
                {
                    var uri = (string)track["uri"];
                    var artists = track["artists"] as JArray;
                    searchItems.Add(new SpotifyListItem
                    {
                        Title = (string)track["name"],
                        Subtitle = "Song • " + (artists != null ? string.Join(", ", artists.Select(a => (string)a["name"])) : string.Empty),
                        ImageUrl = PickImage(track["album"]?["images"] as JArray, 64),
                        Play = new RelayCommand(async () => await PlayUrisAsync(uri))
                    });
                }

                foreach (var artist in Items(body?["artists"]).Take(3))
                {
                    var uri = (string)artist["uri"];
                    searchItems.Add(new SpotifyListItem
                    {
                        Title = (string)artist["name"],
                        Subtitle = "Artist",
                        ImageUrl = PickImage(artist["images"] as JArray, 64),
                        ContextUri = uri,
                        Play = new RelayCommand(async () => await PlayContextAsync(uri, null))
                    });
                }

                foreach (var album in Items(body?["albums"]).Take(4))
                {
                    var uri = (string)album["uri"];
                    var artists = album["artists"] as JArray;
                    searchItems.Add(new SpotifyListItem
                    {
                        Title = (string)album["name"],
                        Subtitle = "Album • " + (artists != null ? string.Join(", ", artists.Select(a => (string)a["name"])) : string.Empty),
                        ImageUrl = PickImage(album["images"] as JArray, 64),
                        ContextUri = uri,
                        Play = new RelayCommand(async () => await PlayContextAsync(uri, null))
                    });
                }

                foreach (var playlist in Items(body?["playlists"]))
                {
                    // Spotify's own playlists can't be opened by apps any more; skip them.
                    var ownerId = (string)playlist["owner"]?["id"];
                    if (string.Equals(ownerId, "spotify", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var uri = (string)playlist["uri"];
                    searchItems.Add(new SpotifyListItem
                    {
                        Title = (string)playlist["name"],
                        Subtitle = "Playlist • " + ((string)playlist["owner"]?["display_name"] ?? string.Empty),
                        ImageUrl = PickImage(playlist["images"] as JArray, 64),
                        ContextUri = uri,
                        PlaylistId = (string)playlist["id"],
                        Play = new RelayCommand(async () => await PlayContextAsync(uri, null))
                    });

                    if (searchItems.Count(i => i.PlaylistId != null) >= 4)
                    {
                        break;
                    }
                }

                if (searchItems.Count == 0)
                {
                    searchStatus.Text = "No results for \"" + query + "\"";
                    return;
                }

                searchStatus.Visibility = Visibility.Collapsed;
                window?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var container = searchList.ItemContainerGenerator.ContainerFromIndex(0) as DependencyObject;
                    var button = container != null ? FindChild<ButtonBase>(container) : null;
                    if (button != null)
                    {
                        button.Focus();
                        Keyboard.Focus(button);
                        button.BringIntoView();
                    }
                }), DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                if (version == searchVersion)
                {
                    searchStatus.Text = "Search failed. Try again.";
                }

                logger?.Warn(ex, "[AnikiHelper][Spotify] Search failed.");
            }
        }

        // Search result lists can contain nulls (removed items); skip them.
        private static IEnumerable<JObject> Items(JToken section)
        {
            return ((section?["items"] as JArray) ?? new JArray()).OfType<JObject>();
        }

        private void ToggleSection(ItemsControl list, TextBlock chevron)
        {
            var show = list.Visibility != Visibility.Visible;
            list.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = show ? GlyphChevronDown : GlyphChevronRight;
        }

        // ======================= auth / setup =======================

        private void UpdateViewForAuthState()
        {
            if (!api.HasClientId)
            {
                ShowSetup(
                    "Connect Spotify",
                    "One-time setup:\n" +
                    "1. On developer.spotify.com/dashboard, create an app.\n" +
                    "2. Redirect URI:  " + SpotifyApiClient.RedirectUri + "\n" +
                    "    API:  Web API\n" +
                    "3. Paste the app's Client ID into this file and save it:\n" +
                    api.ClientIdFilePath + "\n" +
                    "4. Press Retry.",
                    "Retry",
                    "Open folder");
                return;
            }

            if (!api.IsSignedIn)
            {
                ShowSetup(
                    "Sign in to Spotify",
                    "Opens Spotify's sign-in page in the Aniki browser.\n" +
                    "Press X there to type with the on-screen keyboard.\n" +
                    "When it says Spotify is connected, this panel comes back.",
                    "Sign in",
                    "Open folder");
                return;
            }

            setupView.Visibility = Visibility.Collapsed;
            mainView.Visibility = Visibility.Visible;
            FocusLater(playButton);
            StartTimers();
            var ignored = RefreshPlayerAsync();
            if (!listsLoaded)
            {
                var ignored2 = LoadListsAsync();
            }
        }

        private void ShowSetup(string title, string text, string primary, string secondary)
        {
            StopTimers();
            mainView.Visibility = Visibility.Collapsed;
            setupView.Visibility = Visibility.Visible;
            setupTitle.Text = title;
            setupText.Text = text;
            setupPrimary.Content = primary;
            setupSecondary.Content = secondary;
            FocusLater(setupPrimary);
        }

        private void OnSetupPrimary()
        {
            if (!api.HasClientId)
            {
                api.Reload();
                UpdateViewForAuthState();
                return;
            }

            if (!api.IsSignedIn)
            {
                var ignored = SignInAsync();
            }
        }

        private void OnSetupSecondary()
        {
            try
            {
                Process.Start("explorer.exe", "/select,\"" + api.ClientIdFilePath + "\"");
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Could not open the Spotify settings folder.");
            }
        }

        private async Task SignInAsync()
        {
            if (signingIn)
            {
                return;
            }

            signingIn = true;
            Close();
            signInCancellation?.Cancel();
            signInCancellation = new CancellationTokenSource();
            bool success;
            try
            {
                success = await api.SignInAsync(url => RunOnUi(() => openInBrowser?.Invoke(url)), signInCancellation.Token);
            }
            finally
            {
                signingIn = false;
            }

            RunOnUi(() =>
            {
                try
                {
                    closeBrowser?.Invoke();
                }
                catch
                {
                }
            });

            await Task.Delay(600);
            listsLoaded = false;
            Open(null);
            if (!success)
            {
                ShowToast("Spotify sign-in didn't finish. Try again.");
            }
        }

        // ======================= player state =======================

        private void StartTimers()
        {
            pollTimer?.Start();
            progressTimer?.Start();
        }

        private void StopTimers()
        {
            pollTimer?.Stop();
            progressTimer?.Stop();
        }

        private async Task RefreshPlayerAsync()
        {
            if (polling || !IsOpen || !api.IsSignedIn)
            {
                return;
            }

            polling = true;
            try
            {
                var result = await api.SendAsync(HttpMethod.Get, "/me/player?additional_types=episode");
                if (result.Status == 401 && !api.IsSignedIn)
                {
                    UpdateViewForAuthState();
                    return;
                }

                var body = result.Body as JObject;
                if (!result.Ok || body == null)
                {
                    if (result.Status == 204 || result.Ok)
                    {
                        ShowNothingPlaying();
                    }

                    return;
                }

                isPlaying = (bool?)body["is_playing"] ?? false;
                shuffleOn = (bool?)body["shuffle_state"] ?? false;
                repeatState = (string)body["repeat_state"] ?? "off";
                progressMs = (double?)body["progress_ms"] ?? 0;
                progressStampUtc = DateTime.UtcNow;
                var deviceName = (string)body["device"]?["name"];
                deviceText.Text = string.IsNullOrEmpty(deviceName) ? string.Empty : "Playing on " + deviceName;
                var deviceId = (string)body["device"]?["id"];
                if (!string.IsNullOrEmpty(deviceId))
                {
                    preferredDeviceId = deviceId;
                }

                var item = body["item"] as JObject;
                if (item == null)
                {
                    ShowNothingPlaying();
                    UpdateControlVisuals();
                    return;
                }

                durationMs = (double?)item["duration_ms"] ?? 0;
                var uri = (string)item["uri"];
                titleText.Text = (string)item["name"] ?? string.Empty;
                var artists = item["artists"] as JArray;
                artistText.Text = artists != null
                    ? string.Join(", ", artists.Select(a => (string)a["name"]).Where(n => !string.IsNullOrEmpty(n)))
                    : (string)item["show"]?["name"] ?? string.Empty;

                var images = (item["album"]?["images"] ?? item["images"] ?? item["show"]?["images"]) as JArray;
                SetArt(PickImage(images, 300));

                if (uri != trackUri)
                {
                    trackUri = uri;
                    liked = false;
                    await RefreshLikedAsync();
                }

                UpdateControlVisuals();
            }
            finally
            {
                polling = false;
            }
        }

        private void ShowNothingPlaying()
        {
            isPlaying = false;
            trackUri = null;
            durationMs = 0;
            progressMs = 0;
            titleText.Text = "Nothing playing";
            deviceText.Text = string.Empty;
            artistText.Text = "Pick something below, or press play";
            SetArt(null);
            UpdateControlVisuals();
        }

        private void SetArt(string url)
        {
            if (url == currentImageUrl)
            {
                return;
            }

            currentImageUrl = url;
            if (string.IsNullOrEmpty(url))
            {
                art.Source = null;
                CrossfadeBackground(GetDefaultBackground());
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(url);
                bitmap.DecodePixelWidth = 220;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                art.Source = bitmap;
                CrossfadeBackground(bitmap);
            }
            catch
            {
                art.Source = null;
                CrossfadeBackground(GetDefaultBackground());
            }
        }

        private ImageSource lastBackgroundSource;

        private void CrossfadeBackground(ImageSource source)
        {
            if (backgroundArt == null || ReferenceEquals(lastBackgroundSource, source))
            {
                return;
            }

            lastBackgroundSource = source;
            var bitmap = source as BitmapImage;
            if (bitmap != null && bitmap.IsDownloading)
            {
                // Blur once the cover has finished downloading.
                bitmap.DownloadCompleted += (s, e) =>
                {
                    if (ReferenceEquals(lastBackgroundSource, source))
                    {
                        FadeToBackground(BlurToBitmap(bitmap) ?? source);
                    }
                };
                bitmap.DownloadFailed += (s, e) => FadeToBackground(BlurToBitmap(GetDefaultBackground() as BitmapSource));
                return;
            }

            FadeToBackground(BlurToBitmap(source as BitmapSource) ?? source);
        }

        /// <summary>
        /// Renders the cover enlarged and blurred into a small bitmap once, so the panel never
        /// has to blur live (which made the slide-in animation stutter).
        /// </summary>
        private static BitmapSource BlurToBitmap(BitmapSource source)
        {
            if (source == null)
            {
                return null;
            }

            try
            {
                const int width = 280;
                const int height = 320;
                var image = new Image
                {
                    Source = source,
                    Stretch = Stretch.UniformToFill,
                    Width = width,
                    Height = height,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(1.8, 1.8),
                    Effect = new System.Windows.Media.Effects.BlurEffect
                    {
                        Radius = 30,
                        KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                        RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
                    }
                };
                var host = new Grid { Width = width, Height = height, ClipToBounds = true, Background = Brushes.Black };
                host.Children.Add(image);
                host.Measure(new Size(width, height));
                host.Arrange(new Rect(0, 0, width, height));
                host.UpdateLayout();
                var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                target.Render(host);
                target.Freeze();
                return target;
            }
            catch
            {
                return null;
            }
        }

        private void FadeToBackground(ImageSource source)
        {
            if (backgroundArt == null || source == null)
            {
                return;
            }

            var fadeOut = new DoubleAnimation(backgroundArt.Opacity, 0.35, TimeSpan.FromMilliseconds(220));
            fadeOut.Completed += (s, e) =>
            {
                backgroundArt.Source = source;
                backgroundArt.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(420)));
            };
            backgroundArt.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        internal static ImageSource GetDefaultBackground()
        {
            if (defaultBackground != null)
            {
                return defaultBackground;
            }

            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(AnikiSpotifyPanelService).Assembly.Location) ?? string.Empty,
                    "Assets", "Spotify", "default_bg.png");
                if (System.IO.File.Exists(path))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(path);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    defaultBackground = bitmap;
                }
            }
            catch
            {
            }

            return defaultBackground;
        }

        private async Task RefreshLikedAsync()
        {
            if (string.IsNullOrEmpty(trackUri) || !trackUri.StartsWith("spotify:track:", StringComparison.Ordinal))
            {
                liked = false;
                return;
            }

            var result = await api.SendAsync(HttpMethod.Get, "/me/library/contains?uris=" + Uri.EscapeDataString(trackUri));
            var array = result.Body as JArray;
            liked = result.Ok && array != null && array.Count > 0 && (bool)array[0];
        }

        private void UpdateControlVisuals()
        {
            playButton.Content = isPlaying ? GlyphPause : GlyphPlay;
            shuffleButton.Foreground = shuffleOn ? Green : IconNormal;
            repeatButton.Content = repeatState == "track" ? GlyphRepeatOne : GlyphRepeatAll;
            repeatButton.Foreground = repeatState == "off" ? IconNormal : Green;
            heartButton.Content = liked ? GlyphHeartFill : GlyphHeart;
            heartButton.Foreground = liked ? Green : IconNormal;
            UpdateProgressBar();
        }

        private void UpdateProgressBar()
        {
            if (progressFill == null || progressTrack == null)
            {
                return;
            }

            var current = progressMs;
            if (isPlaying)
            {
                current += (DateTime.UtcNow - progressStampUtc).TotalMilliseconds;
            }

            var fraction = durationMs > 0 ? Math.Max(0, Math.Min(1, current / durationMs)) : 0;
            progressFill.Width = progressTrack.ActualWidth * fraction;
        }

        // ======================= actions =======================

        private async Task TogglePlayAsync()
        {
            if (isPlaying)
            {
                isPlaying = false;
                progressMs += (DateTime.UtcNow - progressStampUtc).TotalMilliseconds;
                progressStampUtc = DateTime.UtcNow;
                UpdateControlVisuals();
                await PlayerCommandAsync(HttpMethod.Put, "/me/player/pause", null);
            }
            else
            {
                isPlaying = true;
                progressStampUtc = DateTime.UtcNow;
                UpdateControlVisuals();
                await PlayerCommandAsync(HttpMethod.Put, "/me/player/play", null);
            }
        }

        private Task NextAsync()
        {
            return PlayerCommandAsync(HttpMethod.Post, "/me/player/next", null);
        }

        private async Task PreviousAsync()
        {
            var result = await api.SendAsync(HttpMethod.Post, "/me/player/previous");
            if (result.Status == 403)
            {
                // Spotify sometimes refuses "previous" (e.g. at the start of a radio/autoplay);
                // do what its own app does and restart the current song.
                await api.SendAsync(HttpMethod.Put, "/me/player/seek?position_ms=0");
            }
            else if (result.Status == 404)
            {
                await PlayerCommandAsync(HttpMethod.Post, "/me/player/previous", null);
                return;
            }

            await RefreshPlayerSoonAsync();
        }

        private async Task ToggleShuffleAsync()
        {
            shuffleOn = !shuffleOn;
            UpdateControlVisuals();
            await PlayerCommandAsync(HttpMethod.Put, "/me/player/shuffle?state=" + (shuffleOn ? "true" : "false"), null);
        }

        private async Task CycleRepeatAsync()
        {
            repeatState = repeatState == "off" ? "context" : repeatState == "context" ? "track" : "off";
            UpdateControlVisuals();
            await PlayerCommandAsync(HttpMethod.Put, "/me/player/repeat?state=" + repeatState, null);
        }

        private async Task ToggleLikeAsync()
        {
            if (string.IsNullOrEmpty(trackUri) || !trackUri.StartsWith("spotify:track:", StringComparison.Ordinal))
            {
                return;
            }

            var target = !liked;
            liked = target;
            UpdateControlVisuals();
            var result = await api.SendAsync(
                target ? HttpMethod.Put : HttpMethod.Delete,
                "/me/library?uris=" + Uri.EscapeDataString(trackUri));
            if (!result.Ok)
            {
                liked = !target;
                UpdateControlVisuals();
                ShowToast("Couldn't update Liked Songs.");
            }
            else
            {
                ShowToast(target ? "Added to Liked Songs" : "Removed from Liked Songs");
            }
        }

        private Task PlayContextAsync(string contextUri, string offsetUri)
        {
            var body = new JObject { ["context_uri"] = contextUri };
            if (!string.IsNullOrEmpty(offsetUri))
            {
                body["offset"] = new JObject { ["uri"] = offsetUri };
            }

            return PlayerCommandAsync(HttpMethod.Put, "/me/player/play", body);
        }

        private Task PlayUrisAsync(params string[] uris)
        {
            return PlayerCommandAsync(HttpMethod.Put, "/me/player/play", new JObject { ["uris"] = new JArray(uris) });
        }

        /// <summary>Sends a player command; if no device is active, wakes the desktop app and retries on it.</summary>
        private async Task PlayerCommandAsync(HttpMethod method, string path, JObject body)
        {
            // Starting playback always targets the Spotify app on this PC, so it never lands
            // on a silent device (a stale web player, a sleeping phone, etc.).
            if (path.StartsWith("/me/player/play", StringComparison.Ordinal))
            {
                var pcDevice = await EnsureDesktopDeviceAsync();
                if (pcDevice == null)
                {
                    ShowToast("Couldn't find Spotify on this PC. Open the Spotify app, then try again.");
                    await RefreshPlayerSoonAsync();
                    return;
                }

                path += (path.Contains("?") ? "&" : "?") + "device_id=" + Uri.EscapeDataString(pcDevice);
            }

            var result = await api.SendAsync(method, path, body);
            if (result.Status == 404)
            {
                var deviceId = await EnsureDesktopDeviceAsync();
                if (deviceId == null)
                {
                    ShowToast("Open Spotify on this PC, then try again.");
                    await RefreshPlayerSoonAsync();
                    return;
                }

                var separator = path.Contains("?") ? "&" : "?";
                result = await api.SendAsync(method, path + separator + "device_id=" + Uri.EscapeDataString(deviceId), body);
            }

            if (result.Status == 403)
            {
                var reason = (string)result.Body?["error"]?["message"] ?? string.Empty;
                ShowToast(reason.IndexOf("Restriction", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "Spotify doesn't allow that right now."
                    : "Spotify refused that action.");
            }
            else if (!result.Ok && result.Status != 404)
            {
                ShowToast(result.Status == 0 ? "Couldn't reach Spotify." : "Spotify error " + result.Status + ".");
            }

            await RefreshPlayerSoonAsync();
        }

        private async Task RefreshPlayerSoonAsync()
        {
            await Task.Delay(400);
            await RefreshPlayerAsync();
        }

        private async Task<string> EnsureDesktopDeviceAsync()
        {
            var deviceId = await FindDesktopDeviceAsync();
            if (deviceId != null)
            {
                return deviceId;
            }

            try
            {
                Process.Start("spotify:");
                ShowToast("Starting Spotify...");
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Could not start the Spotify app.");
                return null;
            }

            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                deviceId = await FindDesktopDeviceAsync();
                if (deviceId != null)
                {
                    // Bring Playnite back in front of the Spotify window.
                    RunOnUi(() =>
                    {
                        if (IsOpen)
                        {
                            window.Activate();
                        }
                        else
                        {
                            ActivatePlayniteMainWindow();
                        }
                    });
                    return deviceId;
                }
            }

            return null;
        }

        private async Task<string> FindDesktopDeviceAsync()
        {
            var result = await api.SendAsync(HttpMethod.Get, "/me/player/devices");
            var devices = result.Body?["devices"] as JArray;
            if (!result.Ok || devices == null || devices.Count == 0)
            {
                return null;
            }

            // Only the Spotify desktop app on this PC: a Computer device that isn't a web player.
            var machine = Environment.MachineName ?? string.Empty;
            var computers = devices
                .Where(d => string.Equals((string)d["type"], "Computer", StringComparison.OrdinalIgnoreCase))
                .Where(d => ((string)d["name"] ?? string.Empty).IndexOf("Web Player", StringComparison.OrdinalIgnoreCase) < 0)
                .Where(d => (bool?)d["is_restricted"] != true)
                .ToList();
            Func<JToken, bool> isThisPc = d =>
            {
                var name = (string)d["name"] ?? string.Empty;
                return name.Equals(machine, StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith(machine, StringComparison.OrdinalIgnoreCase) ||
                       (machine.Length > 0 && machine.StartsWith(name, StringComparison.OrdinalIgnoreCase) && name.Length >= 6);
            };
            var pick = computers.FirstOrDefault(isThisPc) ??
                       computers.FirstOrDefault(d => (bool?)d["is_active"] == true) ??
                       computers.FirstOrDefault();
            return (string)pick?["id"];
        }

        // ======================= lists =======================

        private async Task LoadListsAsync()
        {
            listsLoaded = true;
            listStatus.Text = "Loading...";
            listStatus.Visibility = Visibility.Visible;
            recentItems.Clear();
            playlistItems.Clear();

            try
            {
                var me = await api.SendAsync(HttpMethod.Get, "/me");
                userId = (string)me.Body?["id"];

                var recent = await api.SendAsync(HttpMethod.Get, "/me/player/recently-played?limit=40");
                var seen = new HashSet<string>();
                foreach (var entry in (recent.Body?["items"] as JArray) ?? new JArray())
                {
                    var track = entry["track"] as JObject;
                    var uri = (string)track?["uri"];
                    if (track == null || string.IsNullOrEmpty(uri) || !seen.Add(uri))
                    {
                        continue;
                    }

                    var contextUri = (string)(entry["context"] as JObject)?["uri"];
                    var contextType = (string)(entry["context"] as JObject)?["type"];
                    var artists = track["artists"] as JArray;
                    var trackUriCopy = uri;
                    var playable = !string.IsNullOrEmpty(contextUri) &&
                                   (contextType == "playlist" || contextType == "album");
                    recentItems.Add(new SpotifyListItem
                    {
                        Title = (string)track["name"],
                        Subtitle = artists != null ? string.Join(", ", artists.Select(a => (string)a["name"])) : string.Empty,
                        ImageUrl = PickImage(track["album"]?["images"] as JArray, 64),
                        Play = new RelayCommand(async () =>
                        {
                            if (playable)
                            {
                                await PlayContextAsync(contextUri, trackUriCopy);
                            }
                            else
                            {
                                await PlayUrisAsync(trackUriCopy);
                            }
                        })
                    });

                    if (recentItems.Count >= 20)
                    {
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(userId))
                {
                    var likedContext = "spotify:user:" + userId + ":collection";
                    playlistItems.Add(new SpotifyListItem
                    {
                        Title = "Liked Songs",
                        Subtitle = "Your library",
                        ImageUrl = null,
                        ContextUri = likedContext,
                        IsLiked = true,
                        Play = new RelayCommand(async () => await PlayContextAsync(likedContext, null))
                    });
                }

                string next = "/me/playlists?limit=50";
                var pages = 0;
                while (!string.IsNullOrEmpty(next) && pages < 6)
                {
                    pages++;
                    var page = await api.SendAsync(HttpMethod.Get, next);
                    if (!page.Ok)
                    {
                        break;
                    }

                    foreach (var playlist in (page.Body?["items"] as JArray) ?? new JArray())
                    {
                        var uri = (string)playlist?["uri"];
                        if (string.IsNullOrEmpty(uri))
                        {
                            continue;
                        }

                        // Playlists Spotify makes (Discover Weekly, Daily Mix, editorial) can't be
                        // opened by developer apps, so they are left out; so are ones that failed before.
                        var ownerId = (string)playlist["owner"]?["id"];
                        var playlistId = (string)playlist["id"];
                        if (string.Equals(ownerId, "spotify", StringComparison.OrdinalIgnoreCase) ||
                            HiddenPlaylists.Contains(playlistId ?? string.Empty))
                        {
                            continue;
                        }

                        var owner = (string)playlist["owner"]?["display_name"];
                        playlistItems.Add(new SpotifyListItem
                        {
                            Title = (string)playlist["name"],
                            Subtitle = "Playlist" + (string.IsNullOrEmpty(owner) ? string.Empty : " • " + owner),
                            ImageUrl = PickImage(playlist["images"] as JArray, 64),
                            PlaylistId = playlistId,
                            ContextUri = uri,
                            Play = new RelayCommand(async () => await PlayContextAsync(uri, null))
                        });
                    }

                    next = (string)page.Body?["next"];
                }

                listStatus.Visibility = recentItems.Count == 0 && playlistItems.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                listStatus.Text = "Nothing here yet.";
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Failed to load lists.");
                listStatus.Text = "Couldn't load your Spotify lists.";
                listStatus.Visibility = Visibility.Visible;
                listsLoaded = false;
            }
        }

        // ======================= playlist contents =======================

        private bool IsSideFocused()
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            return IsSideOpen && focused != null && sidePanel != null && IsInside(focused, sidePanel);
        }

        private bool IsPlaylistViewOpen
        {
            get { return playlistView != null && playlistView.Visibility == Visibility.Visible; }
        }

        private string HiddenPlaylistsFile
        {
            get { return System.IO.Path.Combine(api.DataDirectory ?? string.Empty, "hidden_playlists.json"); }
        }

        private HashSet<string> HiddenPlaylists
        {
            get
            {
                if (hiddenPlaylists == null)
                {
                    try
                    {
                        hiddenPlaylists = System.IO.File.Exists(HiddenPlaylistsFile)
                            ? new HashSet<string>(Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(System.IO.File.ReadAllText(HiddenPlaylistsFile)) ?? new List<string>())
                            : new HashSet<string>();
                    }
                    catch
                    {
                        hiddenPlaylists = new HashSet<string>();
                    }
                }

                return hiddenPlaylists;
            }
        }

        private void HidePlaylist(SpotifyListItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.PlaylistId))
            {
                return;
            }

            HiddenPlaylists.Add(item.PlaylistId);
            try
            {
                System.IO.File.WriteAllText(HiddenPlaylistsFile, Newtonsoft.Json.JsonConvert.SerializeObject(HiddenPlaylists.ToList()));
            }
            catch
            {
            }

            playlistItems.Remove(item);
        }

        /// <summary>X on a playlist row: show its songs in place of the library lists.</summary>
        private bool TryOpenFocusedPlaylist()
        {
            var focused = Keyboard.FocusedElement as FrameworkElement;
            if (focused == null || !(IsInside(focused, playlistList) || IsInside(focused, searchList)))
            {
                return false;
            }

            var item = focused.DataContext as SpotifyListItem;
            if (item == null || string.IsNullOrEmpty(item.ContextUri) || (!item.IsLiked && string.IsNullOrEmpty(item.PlaylistId)))
            {
                return false;
            }

            playlistReturnFocus = focused;
            playlistReturnOffset = listScroll.VerticalOffset;
            var t = OpenPlaylistAsync(item);
            return true;
        }

        private async Task OpenPlaylistAsync(SpotifyListItem playlist)
        {
            var version = ++playlistLoadVersion;
            playlistTracks.Clear();
            playlistTitle.Text = playlist.Title;
            playlistSubtitle.Text = "Loading...";
            playlistStatus.Visibility = Visibility.Collapsed;
            playlistCover.Source = null;
            if (!string.IsNullOrEmpty(playlist.ImageUrl))
            {
                try
                {
                    playlistCover.Source = new BitmapImage(new Uri(playlist.ImageUrl));
                }
                catch
                {
                }
            }

            libraryView.Visibility = Visibility.Collapsed;
            playlistView.Visibility = Visibility.Visible;
            listScroll.ScrollToVerticalOffset(0);

            var next = playlist.IsLiked
                ? "/me/tracks?limit=50"
                : "/playlists/" + playlist.PlaylistId + "/items?limit=50&additional_types=track,episode";
            var pages = 0;
            var total = 0;
            try
            {
                while (!string.IsNullOrEmpty(next) && pages < 40)
                {
                    pages++;
                    var page = await api.SendAsync(HttpMethod.Get, next);
                    if (version != playlistLoadVersion || !IsPlaylistViewOpen)
                    {
                        return;
                    }

                    if (!page.Ok)
                    {
                        logger?.Warn("[AnikiHelper][Spotify] Opening playlist '" + playlist.Title + "' failed: HTTP " + page.Status);
                        if (pages == 1 && !playlist.IsLiked && (page.Status == 403 || page.Status == 404))
                        {
                            // Spotify won't let this app read it: hide it so it isn't offered again.
                            ClosePlaylistView();
                            var focusTarget = NeighbourOf(playlist);
                            HidePlaylist(playlist);
                            ShowToast("Spotify doesn't allow opening that playlist, so it was hidden.");
                            if (focusTarget != null)
                            {
                                FocusLater(focusTarget);
                            }

                            return;
                        }

                        if (pages == 1)
                        {
                            playlistSubtitle.Text = string.Empty;
                            playlistStatus.Text = "Couldn't load this playlist.";
                            playlistStatus.Visibility = Visibility.Visible;
                            return;
                        }

                        break;
                    }

                    total = (int?)page.Body?["total"] ?? total;
                    foreach (var entry in (page.Body?["items"] as JArray) ?? new JArray())
                    {
                        var track = (entry["item"] as JObject) ?? (entry["track"] as JObject);
                        var trackUri = (string)track?["uri"];
                        if (track == null || string.IsNullOrEmpty(trackUri))
                        {
                            continue;
                        }

                        var artists = track["artists"] as JArray;
                        var subtitle = artists != null && artists.Count > 0
                            ? string.Join(", ", artists.Select(a => (string)a["name"]))
                            : (string)track["show"]?["name"] ?? string.Empty;
                        var images = (track["album"]?["images"] as JArray) ?? (track["images"] as JArray);
                        var position = playlistTracks.Count;
                        var isLocal = (bool?)track["is_local"] == true;
                        var context = playlist.ContextUri;
                        playlistTracks.Add(new SpotifyListItem
                        {
                            Title = (string)track["name"],
                            Subtitle = subtitle,
                            ImageUrl = PickImage(images, 64),
                            Play = new RelayCommand(async () =>
                            {
                                if (isLocal)
                                {
                                    await PlayContextAtPositionAsync(context, position);
                                }
                                else
                                {
                                    await PlayContextAsync(context, trackUri);
                                }
                            })
                        });
                    }

                    playlistSubtitle.Text = (total > 0 ? total : playlistTracks.Count) + " songs  •  B to go back";
                    if (pages == 1)
                    {
                        FocusFirstTrackSoon();
                    }

                    next = RelativeApiPath((string)page.Body?["next"]);
                }

                if (playlistTracks.Count == 0)
                {
                    playlistStatus.Text = "This playlist is empty.";
                    playlistStatus.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Failed to load playlist contents.");
                if (version == playlistLoadVersion && IsPlaylistViewOpen)
                {
                    playlistStatus.Text = "Couldn't load this playlist.";
                    playlistStatus.Visibility = Visibility.Visible;
                }
            }
        }

        private static string RelativeApiPath(string next)
        {
            if (string.IsNullOrEmpty(next))
            {
                return null;
            }

            const string prefix = "https://api.spotify.com/v1";
            return next.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? next.Substring(prefix.Length) : next;
        }

        private void FocusFirstTrackSoon()
        {
            window?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsPlaylistViewOpen || playlistTracks.Count == 0)
                {
                    return;
                }

                var container = playlistTracksList.ItemContainerGenerator.ContainerFromIndex(0) as DependencyObject;
                var button = container != null ? FindChild<ButtonBase>(container) : null;
                if (button != null)
                {
                    button.Focus();
                    Keyboard.Focus(button);
                    button.BringIntoView();
                }
            }), DispatcherPriority.Loaded);
        }

        private UIElement NeighbourOf(SpotifyListItem item)
        {
            var index = playlistItems.IndexOf(item);
            var target = index + 1 < playlistItems.Count ? index + 1 : index - 1;
            if (target < 0)
            {
                return null;
            }

            var container = playlistList.ItemContainerGenerator.ContainerFromIndex(target) as DependencyObject;
            return container != null ? FindChild<ButtonBase>(container) : null;
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var match = child as T ?? FindChild<T>(child);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        /// <summary>B in the playlist view: back to the library, on the playlist that was opened.</summary>
        private void ClosePlaylistView()
        {
            if (!IsPlaylistViewOpen)
            {
                return;
            }

            playlistLoadVersion++;
            playlistView.Visibility = Visibility.Collapsed;
            libraryView.Visibility = Visibility.Visible;
            playlistTracks.Clear();

            var offset = playlistReturnOffset;
            var returnTo = playlistReturnFocus as UIElement;
            playlistReturnFocus = null;
            window?.Dispatcher.BeginInvoke(new Action(() =>
            {
                listScroll.ScrollToVerticalOffset(offset);
                if (returnTo != null && returnTo.IsVisible)
                {
                    returnTo.Focus();
                    Keyboard.Focus(returnTo);
                }
            }), DispatcherPriority.Loaded);
        }

        private Task PlayContextAtPositionAsync(string contextUri, int position)
        {
            var body = new JObject
            {
                ["context_uri"] = contextUri,
                ["offset"] = new JObject { ["position"] = position }
            };
            return PlayerCommandAsync(HttpMethod.Put, "/me/player/play", body);
        }

        internal static string PickImage(JArray images, int minimumWidth)
        {
            if (images == null || images.Count == 0)
            {
                return null;
            }

            var sorted = images
                .Select(i => new { Url = (string)i["url"], Width = (int?)i["width"] ?? 0 })
                .Where(i => !string.IsNullOrEmpty(i.Url))
                .OrderBy(i => i.Width == 0 ? int.MaxValue : i.Width)
                .ToList();
            var match = sorted.FirstOrDefault(i => i.Width >= minimumWidth) ?? sorted.LastOrDefault();
            return match?.Url;
        }

        // ======================= controller =======================

        public void ProcessControllerInput(WebBrowserGamepadInputState state)
        {
            RunOnUi(() =>
            {
                if (!IsOpen || window == null)
                {
                    return;
                }

                if (!window.IsActive && (state.ActivatePressed || state.DPadUpPressed || state.DPadDownPressed ||
                                         state.DPadLeftPressed || state.DPadRightPressed))
                {
                    window.Activate();
                }

                if (state.IsFullPadState)
                {
                    HandleSticks(state);
                    return;
                }

                if (state.BackPressed && IsPlaylistViewOpen && !IsSideFocused())
                {
                    if ((DateTime.UtcNow - lastBackUtc).TotalMilliseconds > 250)
                    {
                        lastBackUtc = DateTime.UtcNow;
                        ClosePlaylistView();
                    }

                    return;
                }

                if ((state.BackPressed || state.ClosePressed) && audioOnly)
                {
                    if ((DateTime.UtcNow - lastBackUtc).TotalMilliseconds > 250)
                    {
                        lastBackUtc = DateTime.UtcNow;
                        Close();
                    }

                    return;
                }

                if (state.BackPressed && IsSideOpen)
                {
                    if ((DateTime.UtcNow - lastBackUtc).TotalMilliseconds > 250)
                    {
                        lastBackUtc = DateTime.UtcNow;
                        CloseSide(true);
                    }

                    return;
                }

                if (state.BackPressed || state.ClosePressed)
                {
                    // Same press may also arrive as an Escape key; only act once.
                    if ((DateTime.UtcNow - lastBackUtc).TotalMilliseconds > 250)
                    {
                        lastBackUtc = DateTime.UtcNow;
                        Close();
                    }

                    return;
                }

                if (state.DPadUpPressed) { Move(FocusNavigationDirection.Up); }
                if (state.DPadDownPressed) { Move(FocusNavigationDirection.Down); }
                if (state.DPadLeftPressed) { Move(FocusNavigationDirection.Left); }
                if (state.DPadRightPressed) { Move(FocusNavigationDirection.Right); }

                if (state.ActivatePressed)
                {
                    ActivateFocused();
                }

                if (audioOnly || mainView.Visibility != Visibility.Visible)
                {
                    return;
                }

                if (state.EnterPressed) { var t = TogglePlayAsync(); }
                if (state.PreviousPressed) { var t = PreviousAsync(); }
                if (state.NextPressed) { var t = NextAsync(); }
                if (state.AddressPressed) { var t = ToggleLikeAsync(); }
                if (state.KeyboardPressed && !TryOpenFocusedPlaylist()) { var t = ToggleShuffleAsync(); }
            });
        }

        private void HandleSticks(WebBrowserGamepadInputState state)
        {
            // Left stick: menu navigation with repeat. Right stick: scroll the lists.
            var lx = state.LeftX / 32767.0;
            var ly = state.LeftY / 32767.0;
            // Hysteresis: a direction engages past 0.65 and only releases below 0.35,
            // so the stick can't flicker between "pushed" and "released" at the edge.
            var engage = lastStickDirection == 0 ? 0.65 : 0.35;
            var direction = 0;
            if (Math.Abs(ly) > engage && Math.Abs(ly) >= Math.Abs(lx))
            {
                direction = ly > 0 ? 2 : 1;
            }
            else if (Math.Abs(lx) > engage)
            {
                direction = lx > 0 ? 4 : 3;
            }

            var now = DateTime.UtcNow;
            if (direction == 0)
            {
                lastStickDirection = 0;
            }
            else if (direction != lastStickDirection || now >= nextStickNavUtc)
            {
                var first = direction != lastStickDirection;
                lastStickDirection = direction;
                // One step per push; holding repeats after a pause, at a readable pace.
                nextStickNavUtc = now.AddMilliseconds(first ? 500 : 220);
                Move(direction == 1 ? FocusNavigationDirection.Up
                    : direction == 2 ? FocusNavigationDirection.Down
                    : direction == 3 ? FocusNavigationDirection.Left
                    : FocusNavigationDirection.Right);
            }

            var ry = state.RightY / 32767.0;
            if (Math.Abs(ry) > 0.2 && listScroll != null && mainView.Visibility == Visibility.Visible)
            {
                var speed = Math.Sign(ry) * Math.Pow((Math.Abs(ry) - 0.2) / 0.8, 1.5) * 28;
                var focusedElement = Keyboard.FocusedElement as DependencyObject;
                var target = IsSideOpen && focusedElement != null && IsInside(focusedElement, sidePanel) ? sideScroll : listScroll;
                target.ScrollToVerticalOffset(target.VerticalOffset + speed);
            }
        }

        private FocusNavigationDirection lastMoveDirection;
        private DateTime lastMoveUtc = DateTime.MinValue;

        private void Move(FocusNavigationDirection direction)
        {
            // The same press can arrive from two input sources a few ms apart; count it once.
            var now = DateTime.UtcNow;
            if (direction == lastMoveDirection && (now - lastMoveUtc).TotalMilliseconds < 230)
            {
                return;
            }

            lastMoveDirection = direction;
            lastMoveUtc = now;

            if (!window.IsActive)
            {
                window.Activate();
            }

            var focused = Keyboard.FocusedElement as UIElement;
            if (focused == null || !IsInside(focused, windowHost))
            {
                FocusLater(mainView.Visibility == Visibility.Visible ? playButton : setupPrimary);
                return;
            }

            // The controls row has a fixed left-to-right order, so left/right never depends on
            // WPF's position-based search (which could get stuck on the big play button).
            if (direction == FocusNavigationDirection.Left || direction == FocusNavigationDirection.Right)
            {
                var row = new UIElement[] { shuffleButton, previousButton, playButton, nextButton, repeatButton, sideToggleButton };
                var index = Array.IndexOf(row, focused);
                if (index >= 0)
                {
                    var next = index + (direction == FocusNavigationDirection.Right ? 1 : -1);
                    if (next >= 0 && next < row.Length)
                    {
                        row[next].Focus();
                        Keyboard.Focus(row[next]);
                    }
                    else if (next >= row.Length && IsSideOpen)
                    {
                        // Past the arrow: continue into the volume box.
                        focused.MoveFocus(new TraversalRequest(direction));
                    }

                    return;
                }
            }

            var slider = focused as Slider;
            if (slider != null && (direction == FocusNavigationDirection.Left || direction == FocusNavigationDirection.Right))
            {
                slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum,
                    slider.Value + (direction == FocusNavigationDirection.Right ? 5 : -5)));
                return;
            }

            focused.MoveFocus(new TraversalRequest(direction));
        }

        private static void Invoke(ButtonBase button)
        {
            if (!button.IsEnabled)
            {
                return;
            }

            if (button.Command != null && button.Command.CanExecute(button.CommandParameter))
            {
                button.Command.Execute(button.CommandParameter);
                return;
            }

            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        }

        private void FocusLater(UIElement element)
        {
            if (element == null || window == null)
            {
                return;
            }

            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsOpen)
                {
                    element.Focus();
                    Keyboard.Focus(element);
                }
            }), DispatcherPriority.Input);
        }

        private void ShowToast(string message)
        {
            RunOnUi(() =>
            {
                if (toast == null)
                {
                    return;
                }

                toastText.Text = message;
                toast.Visibility = Visibility.Visible;
                toastTimer.Stop();
                toastTimer.Start();
            });
        }

        // ======================= Playnite controller ownership =======================

        private void AcquireExclusiveController()
        {
            try
            {
                var model = Application.Current?.MainWindow?.DataContext;
                var app = model?.GetType().GetProperty("App")?.GetValue(model, null);
                var controller = app?.GetType().GetProperty("GameController")?.GetValue(app, null);
                var property = controller?.GetType().GetProperty("StandardProcessingEnabled");
                if (property == null || !property.CanRead || !property.CanWrite)
                {
                    return;
                }

                if (!exclusiveActive)
                {
                    var current = property.GetValue(controller, null);
                    exclusivePreviousStandardProcessing = current is bool b && b;
                    exclusiveGameController = controller;
                    exclusiveActive = true;
                }

                property.SetValue(controller, false, null);
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Could not take controller input from Playnite.");
            }
        }

        private void ReleaseExclusiveController()
        {
            if (!exclusiveActive)
            {
                return;
            }

            exclusiveActive = false;
            try
            {
                var property = exclusiveGameController?.GetType().GetProperty("StandardProcessingEnabled");
                property?.SetValue(exclusiveGameController, exclusivePreviousStandardProcessing, null);
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Could not return controller input to Playnite.");
            }

            exclusiveGameController = null;
        }

        // ======================= helpers =======================

        private static void RunOnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(action);
            }
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public void Dispose()
        {
            try
            {
                signInCancellation?.Cancel();
                StopTimers();
                ReleaseExclusiveController();
                if (window != null)
                {
                    window.Close();
                }
            }
            catch
            {
            }

            api.Dispose();
        }

        // ======================= layout =======================

        private const string PanelXaml = @"
<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        x:Name='Root' Background='#FF141414' CornerRadius='0,0,20,20'
        BorderBrush='#26FFFFFF' BorderThickness='1,0,1,1'
        TextElement.FontFamily='Segoe UI Variable Display, Segoe UI'
        KeyboardNavigation.DirectionalNavigation='Continue'
        KeyboardNavigation.TabNavigation='Continue'>
  <Border.RenderTransform>
    <TranslateTransform x:Name='Slide'/>
  </Border.RenderTransform>
  <Border.Resources>
    <Style x:Key='IconBtn' TargetType='Button'>
      <Setter Property='Foreground' Value='#E6FFFFFF'/>
      <Setter Property='FontFamily' Value='Segoe Fluent Icons, Segoe MDL2 Assets'/>
      <Setter Property='FontSize' Value='30'/>
      <Setter Property='Width' Value='76'/>
      <Setter Property='Height' Value='76'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bd' Background='Transparent' CornerRadius='38' BorderThickness='3' BorderBrush='Transparent'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='#22FFFFFF'/>
              </Trigger>
              <Trigger Property='IsKeyboardFocused' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='#30FFFFFF'/>
                <Setter TargetName='Bd' Property='BorderBrush' Value='#1DB954'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='PlayBtn' TargetType='Button'>
      <Setter Property='Foreground' Value='#121212'/>
      <Setter Property='FontFamily' Value='Segoe Fluent Icons, Segoe MDL2 Assets'/>
      <Setter Property='FontSize' Value='38'/>
      <Setter Property='Width' Value='104'/>
      <Setter Property='Height' Value='104'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Grid>
              <Ellipse x:Name='Ring' Stroke='Transparent' StrokeThickness='4'/>
              <Ellipse x:Name='Disc' Fill='#F2F2F2' Margin='8'/>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='Disc' Property='Fill' Value='#FFFFFF'/>
              </Trigger>
              <Trigger Property='IsKeyboardFocused' Value='True'>
                <Setter TargetName='Ring' Property='Stroke' Value='#1DB954'/>
                <Setter TargetName='Disc' Property='Fill' Value='#FFFFFF'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='RowBtn' TargetType='Button'>
      <Setter Property='Foreground' Value='#FFFFFF'/>
      <Setter Property='Background' Value='Transparent'/>
      <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bd' Background='{TemplateBinding Background}' CornerRadius='10' BorderThickness='2' BorderBrush='Transparent' Padding='12,8'>
              <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='#1FFFFFFF'/>
              </Trigger>
              <Trigger Property='IsKeyboardFocused' Value='True'>
                <Setter TargetName='Bd' Property='Background' Value='#2EFFFFFF'/>
                <Setter TargetName='Bd' Property='BorderBrush' Value='#1DB954'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='PillBtn' TargetType='Button' BasedOn='{StaticResource RowBtn}'>
      <Setter Property='FontSize' Value='22'/>
      <Setter Property='FontWeight' Value='SemiBold'/>
      <Setter Property='MinWidth' Value='180'/>
      <Setter Property='Height' Value='60'/>
      <Setter Property='Margin' Value='0,0,16,0'/>
      <Setter Property='HorizontalContentAlignment' Value='Center'/>
    </Style>
    <DataTemplate x:Key='RowTemplate'>
      <Button Style='{StaticResource RowBtn}' Command='{Binding Play}' Margin='0,2'>
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width='64'/>
            <ColumnDefinition Width='*'/>
          </Grid.ColumnDefinitions>
          <Border Width='60' Height='60' CornerRadius='6' Background='#2A2A2A' ClipToBounds='True'>
            <Image Source='{Binding ImageUrl, TargetNullValue={x:Null}}' Stretch='UniformToFill' RenderOptions.BitmapScalingMode='HighQuality'/>
          </Border>
          <StackPanel Grid.Column='1' Margin='18,0,0,0' VerticalAlignment='Center'>
            <TextBlock Text='{Binding Title}' FontSize='22' FontWeight='SemiBold' Foreground='#FFFFFF' TextTrimming='CharacterEllipsis'/>
            <TextBlock Text='{Binding Subtitle}' FontSize='17' Foreground='#A7A7A7' TextTrimming='CharacterEllipsis' Margin='0,2,0,0'/>
          </StackPanel>
        </Grid>
      </Button>
    </DataTemplate>
  </Border.Resources>

  <Grid x:Name='RootGrid'>
    <Grid x:Name='BgHost' IsHitTestVisible='False'>
      <Image x:Name='BgArt' Stretch='Fill' RenderOptions.BitmapScalingMode='Linear'/>
      <Border Background='#73000000'/>
    </Grid>
    <Grid x:Name='MainView'>
      <Grid.RowDefinitions>
        <RowDefinition Height='Auto'/>
        <RowDefinition Height='Auto'/>
        <RowDefinition Height='Auto'/>
        <RowDefinition Height='*'/>
      </Grid.RowDefinitions>

      <Grid Grid.Row='0' Margin='40,36,32,0'>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width='Auto'/>
          <ColumnDefinition Width='*'/>
          <ColumnDefinition Width='Auto'/>
        </Grid.ColumnDefinitions>
        <Border Width='120' Height='120' CornerRadius='6' Background='#2A2A2A' ClipToBounds='True'>
          <Image x:Name='Art' Stretch='UniformToFill' RenderOptions.BitmapScalingMode='HighQuality'/>
        </Border>
        <StackPanel Grid.Column='1' Margin='30,0,16,0' VerticalAlignment='Center'>
          <TextBlock x:Name='TitleText' FontSize='36' FontWeight='Bold' Foreground='#FFFFFF' TextTrimming='CharacterEllipsis' Text='Spotify'/>
          <TextBlock x:Name='ArtistText' FontSize='24' Foreground='#B3B3B3' TextTrimming='CharacterEllipsis' Margin='0,6,0,0'/>
          <TextBlock x:Name='DeviceText' FontSize='17' Foreground='#1DB954' TextTrimming='CharacterEllipsis' Margin='0,6,0,0'/>
        </StackPanel>
        <Button x:Name='HeartBtn' Grid.Column='2' Style='{StaticResource IconBtn}' FontSize='34' VerticalAlignment='Center'/>
      </Grid>

      <Grid Grid.Row='1' Margin='40,28,40,26'>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width='*'/>
          <ColumnDefinition Width='*'/>
          <ColumnDefinition Width='Auto'/>
          <ColumnDefinition Width='*'/>
          <ColumnDefinition Width='*'/>
          <ColumnDefinition Width='Auto'/>
        </Grid.ColumnDefinitions>
        <Button x:Name='ShuffleBtn' Grid.Column='0' Style='{StaticResource IconBtn}' FontSize='28'/>
        <Button x:Name='PrevBtn' Grid.Column='1' Style='{StaticResource IconBtn}' FontSize='34'/>
        <Button x:Name='PlayBtn' Grid.Column='2' Style='{StaticResource PlayBtn}'/>
        <Button x:Name='NextBtn' Grid.Column='3' Style='{StaticResource IconBtn}' FontSize='34'/>
        <Button x:Name='RepeatBtn' Grid.Column='4' Style='{StaticResource IconBtn}' FontSize='28'/>
        <Button x:Name='SideToggleBtn' Grid.Column='5' Style='{StaticResource IconBtn}'
                Width='52' Height='76' FontSize='24' Foreground='#FFFFFF' Margin='0,0,-16,0'/>
      </Grid>

      <Grid x:Name='ProgressTrack' Grid.Row='2' Height='4' Background='#3DFFFFFF'>
        <Rectangle x:Name='ProgressFill' HorizontalAlignment='Left' Fill='#FFFFFF' Width='0'/>
      </Grid>

      <ScrollViewer x:Name='ListScroll' Grid.Row='3' Focusable='False'
                    VerticalScrollBarVisibility='Hidden' HorizontalScrollBarVisibility='Disabled'
                    PanningMode='VerticalOnly'>
        <Grid>
        <StackPanel x:Name='PlaylistView' Margin='24,18,24,28' Visibility='Collapsed'>
          <Grid Margin='12,0,12,14'>
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width='Auto'/>
              <ColumnDefinition Width='*'/>
            </Grid.ColumnDefinitions>
            <Border Width='96' Height='96' CornerRadius='8' Background='#2A2A2A' ClipToBounds='True'>
              <Image x:Name='PlaylistCover' Stretch='UniformToFill' RenderOptions.BitmapScalingMode='HighQuality'/>
            </Border>
            <StackPanel Grid.Column='1' Margin='20,0,0,0' VerticalAlignment='Center'>
              <TextBlock x:Name='PlaylistTitle' FontSize='30' FontWeight='Bold' Foreground='#FFFFFF' TextTrimming='CharacterEllipsis'/>
              <TextBlock x:Name='PlaylistSubtitle' FontSize='18' Foreground='#A7A7A7' Margin='0,4,0,0' TextTrimming='CharacterEllipsis'/>
            </StackPanel>
          </Grid>
          <ItemsControl x:Name='PlaylistTracks' Focusable='False' ItemTemplate='{StaticResource RowTemplate}'
                        KeyboardNavigation.DirectionalNavigation='Continue'/>
          <TextBlock x:Name='PlaylistStatus' Margin='12,16,0,0' FontSize='20' Foreground='#A7A7A7' Visibility='Collapsed'/>
        </StackPanel>
        <StackPanel x:Name='LibraryView' Margin='24,18,24,28'>
          <Button x:Name='SearchBtn' Style='{StaticResource RowBtn}' Margin='0,0,0,12'>
            <Border Background='#2A2A2A' CornerRadius='26' Padding='20,12'>
              <StackPanel Orientation='Horizontal'>
                <TextBlock Text='&#xE721;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='22'
                           Foreground='#B3B3B3' VerticalAlignment='Center'/>
                <TextBlock x:Name='SearchText' Text='What do you want to play?' FontSize='22' Foreground='#B3B3B3'
                           Margin='14,0,0,0' VerticalAlignment='Center' TextTrimming='CharacterEllipsis'/>
              </StackPanel>
            </Border>
          </Button>
          <StackPanel x:Name='SearchSection' Visibility='Collapsed' Margin='0,0,0,18'>
            <Button x:Name='SearchHeader' Style='{StaticResource RowBtn}'>
              <Grid>
                <TextBlock Text='Search results' FontSize='28' FontWeight='Bold' Foreground='#FFFFFF'/>
                <TextBlock x:Name='SearchChevron' HorizontalAlignment='Right' VerticalAlignment='Center'
                           FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='24' Foreground='#FFFFFF'/>
              </Grid>
            </Button>
            <ItemsControl x:Name='SearchList' Focusable='False' ItemTemplate='{StaticResource RowTemplate}'
                          KeyboardNavigation.DirectionalNavigation='Continue'/>
            <TextBlock x:Name='SearchStatus' Margin='12,10,0,0' FontSize='20' Foreground='#A7A7A7' Visibility='Collapsed'/>
          </StackPanel>
          <Button x:Name='RecentHeader' Style='{StaticResource RowBtn}'>
            <Grid>
              <TextBlock Text='Recently played' FontSize='28' FontWeight='Bold' Foreground='#FFFFFF'/>
              <TextBlock x:Name='RecentChevron' HorizontalAlignment='Right' VerticalAlignment='Center'
                         FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='24' Foreground='#FFFFFF'/>
            </Grid>
          </Button>
          <ItemsControl x:Name='RecentList' Focusable='False' ItemTemplate='{StaticResource RowTemplate}'
                        KeyboardNavigation.DirectionalNavigation='Continue'/>
          <Button x:Name='PlaylistsHeader' Style='{StaticResource RowBtn}' Margin='0,18,0,0'>
            <Grid>
              <TextBlock Text='Your playlists' FontSize='28' FontWeight='Bold' Foreground='#FFFFFF'/>
              <TextBlock x:Name='PlaylistsChevron' HorizontalAlignment='Right' VerticalAlignment='Center'
                         FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='24' Foreground='#FFFFFF'/>
            </Grid>
          </Button>
          <ItemsControl x:Name='PlaylistList' Focusable='False' ItemTemplate='{StaticResource RowTemplate}'
                        KeyboardNavigation.DirectionalNavigation='Continue'/>
          <TextBlock x:Name='ListStatus' Margin='12,16,0,0' FontSize='20' Foreground='#A7A7A7' Visibility='Collapsed'/>
        </StackPanel>
        </Grid>
      </ScrollViewer>
    </Grid>

    <StackPanel x:Name='SetupView' Margin='48,44,48,32' Visibility='Collapsed'>
      <TextBlock x:Name='SetupTitle' FontSize='36' FontWeight='Bold' Foreground='#FFFFFF'/>
      <TextBlock x:Name='SetupText' FontSize='21' Foreground='#CFCFCF' TextWrapping='Wrap' Margin='0,18,0,28' LineHeight='34'/>
      <StackPanel Orientation='Horizontal'>
        <Button x:Name='SetupPrimary' Style='{StaticResource PillBtn}' Background='#1DB954'/>
        <Button x:Name='SetupSecondary' Style='{StaticResource PillBtn}'/>
      </StackPanel>
      <TextBlock Margin='0,28,0,0' FontSize='18' Foreground='#8A8A8A' TextWrapping='Wrap'
                 Text='Controller: D-pad / left stick to move, A to select, B to close.'/>
    </StackPanel>

    <Border x:Name='Toast' VerticalAlignment='Bottom' HorizontalAlignment='Center' Margin='0,0,0,26'
            Background='#F02A2A2A' CornerRadius='12' Padding='22,12' Visibility='Collapsed' IsHitTestVisible='False'>
      <TextBlock x:Name='ToastText' FontSize='20' Foreground='#FFFFFF'/>
    </Border>
  </Grid>
</Border>";
    }
}
