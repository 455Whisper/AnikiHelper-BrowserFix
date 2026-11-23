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
        private readonly SpotifyNowPlayingToast nowPlayingToast = new SpotifyNowPlayingToast();
        private DispatcherTimer watchTimer;
        private bool watching;
        private string watchedTrackUri;
        private bool watchBaselineSet;
        private readonly ObservableCollection<SpotifyListItem> recentItems = new ObservableCollection<SpotifyListItem>();
        private readonly ObservableCollection<SpotifyListItem> playlistItems = new ObservableCollection<SpotifyListItem>();

        private Window window;
        private FrameworkElement root;
        private TranslateTransform slide;
        private Grid mainView;
        private StackPanel setupView;
        private TextBlock setupTitle;
        private TextBlock setupText;
        private Button setupPrimary;
        private Button setupSecondary;
        private Image art;
        private TextBlock titleText;
        private TextBlock artistText;
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
            RunOnUi(StartNowPlayingWatcher);
        }

        // ======================= song-change card =======================

        private void StartNowPlayingWatcher()
        {
            if (watchTimer != null)
            {
                return;
            }

            watchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
            watchTimer.Tick += async (s, e) => await WatchNowPlayingAsync();
            watchTimer.Start();
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
                var result = await api.SendAsync(HttpMethod.Get, "/me/player/currently-playing?additional_types=episode");
                var body = result.Body as JObject;
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

        public event Action<bool> OpenStateChanged;

        public bool IsOpen { get; private set; }

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

        public void Open(FrameworkElement anchor)
        {
            RunOnUi(() =>
            {
                if (IsOpen || signingIn)
                {
                    return;
                }

                try
                {
                    EnsureWindow();
                    api.Reload();
                    PositionWindow(anchor);
                    closing = false;
                    IsOpen = true;
                    window.Show();
                    window.Activate();
                    AcquireExclusiveController();
                    OpenStateChanged?.Invoke(true);

                    slide.Y = -window.Height;
                    var anim = new DoubleAnimation(-window.Height, 0, TimeSpan.FromMilliseconds(260))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    slide.BeginAnimation(TranslateTransform.YProperty, anim);

                    UpdateViewForAuthState();
                }
                catch (Exception ex)
                {
                    logger?.Error(ex, "[AnikiHelper][Spotify] Failed to open the Spotify panel.");
                    IsOpen = false;
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
                ReleaseExclusiveController();

                var anim = new DoubleAnimation(slide.Y, -window.Height, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                anim.Completed += (s, e) =>
                {
                    if (closing)
                    {
                        window.Hide();
                        closing = false;
                    }
                };
                slide.BeginAnimation(TranslateTransform.YProperty, anim);
                OpenStateChanged?.Invoke(false);
                ActivatePlayniteMainWindow();
            });
        }

        private void ActivatePlayniteMainWindow()
        {
            try
            {
                var main = Application.Current?.MainWindow;
                if (main != null && main != window)
                {
                    main.Activate();
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

            var centerX = screenLeft + screenWidth / 2;
            try
            {
                if (anchor != null && anchor.IsVisible && PresentationSource.FromVisual(anchor) != null)
                {
                    var source = PresentationSource.FromVisual(anchor);
                    var toDip = source.CompositionTarget.TransformFromDevice;
                    var center = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
                    centerX = toDip.Transform(center).X;
                }
            }
            catch
            {
            }

            var width = Math.Min(PanelWidth, screenWidth - 40);
            var height = Math.Min(Math.Max(560, screenHeight * 0.82), 1000);
            var left = Math.Max(screenLeft + 20, Math.Min(screenLeft + screenWidth - width - 20, centerX - width / 2));

            window.Width = width;
            window.Height = height;
            window.Left = left;
            window.Top = screenTop;
            root.Width = width;
            root.Height = height;
        }

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
            titleText = (TextBlock)root.FindName("TitleText");
            artistText = (TextBlock)root.FindName("ArtistText");
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
            toast = (Border)root.FindName("Toast");
            toastText = (TextBlock)root.FindName("ToastText");

            recentList.ItemsSource = recentItems;
            playlistList.ItemsSource = playlistItems;

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
                Title = "Spotify",
                Content = root
            };

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
                if (element != null && IsInside(element, listScroll))
                {
                    element.BringIntoView(new Rect(0, -60, element.ActualWidth, element.ActualHeight + 120));
                }
            };
            window.PreviewKeyDown += Window_PreviewKeyDown;
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

            pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
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

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
                case Key.MediaPlayPause:
                case Key.Space when !(Keyboard.FocusedElement is ButtonBase):
                    var ignored = TogglePlayAsync();
                    e.Handled = true;
                    break;
            }
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
            }
            catch
            {
                art.Source = null;
            }
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

        private Task PreviousAsync()
        {
            return PlayerCommandAsync(HttpMethod.Post, "/me/player/previous", null);
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
                ShowToast("Spotify refused that action (Premium is required for playback control).");
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

            var machine = Environment.MachineName;
            var pick =
                devices.FirstOrDefault(d => (string)d["id"] == preferredDeviceId) ??
                devices.FirstOrDefault(d => (bool?)d["is_active"] == true) ??
                devices.FirstOrDefault(d => string.Equals((string)d["type"], "Computer", StringComparison.OrdinalIgnoreCase) &&
                                            string.Equals((string)d["name"], machine, StringComparison.OrdinalIgnoreCase)) ??
                devices.FirstOrDefault(d => string.Equals((string)d["type"], "Computer", StringComparison.OrdinalIgnoreCase)) ??
                devices.FirstOrDefault();
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

                    var contextUri = (string)entry["context"]?["uri"];
                    var contextType = (string)entry["context"]?["type"];
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

                        var owner = (string)playlist["owner"]?["display_name"];
                        playlistItems.Add(new SpotifyListItem
                        {
                            Title = (string)playlist["name"],
                            Subtitle = "Playlist" + (string.IsNullOrEmpty(owner) ? string.Empty : " • " + owner),
                            ImageUrl = PickImage(playlist["images"] as JArray, 64),
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

        private static string PickImage(JArray images, int minimumWidth)
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

                if (state.BackPressed || state.ClosePressed)
                {
                    Close();
                    return;
                }

                if (state.DPadUpPressed) { Move(FocusNavigationDirection.Up); }
                if (state.DPadDownPressed) { Move(FocusNavigationDirection.Down); }
                if (state.DPadLeftPressed) { Move(FocusNavigationDirection.Left); }
                if (state.DPadRightPressed) { Move(FocusNavigationDirection.Right); }

                if (state.ActivatePressed)
                {
                    var button = Keyboard.FocusedElement as ButtonBase;
                    if (button != null && IsInside(button, root))
                    {
                        Invoke(button);
                    }
                    else
                    {
                        FocusLater(mainView.Visibility == Visibility.Visible ? playButton : setupPrimary);
                    }
                }

                if (mainView.Visibility != Visibility.Visible)
                {
                    return;
                }

                if (state.EnterPressed) { var t = TogglePlayAsync(); }
                if (state.PreviousPressed) { var t = PreviousAsync(); }
                if (state.NextPressed) { var t = NextAsync(); }
                if (state.AddressPressed) { var t = ToggleLikeAsync(); }
                if (state.KeyboardPressed) { var t = ToggleShuffleAsync(); }
            });
        }

        private void HandleSticks(WebBrowserGamepadInputState state)
        {
            // Left stick: menu navigation with repeat. Right stick: scroll the lists.
            var lx = state.LeftX / 32767.0;
            var ly = state.LeftY / 32767.0;
            var direction = 0;
            if (Math.Abs(ly) > 0.55 && Math.Abs(ly) >= Math.Abs(lx))
            {
                direction = ly > 0 ? 2 : 1;
            }
            else if (Math.Abs(lx) > 0.55)
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
                nextStickNavUtc = now.AddMilliseconds(first ? 320 : 120);
                Move(direction == 1 ? FocusNavigationDirection.Up
                    : direction == 2 ? FocusNavigationDirection.Down
                    : direction == 3 ? FocusNavigationDirection.Left
                    : FocusNavigationDirection.Right);
            }

            var ry = state.RightY / 32767.0;
            if (Math.Abs(ry) > 0.2 && listScroll != null && mainView.Visibility == Visibility.Visible)
            {
                var speed = Math.Sign(ry) * Math.Pow((Math.Abs(ry) - 0.2) / 0.8, 1.5) * 28;
                listScroll.ScrollToVerticalOffset(listScroll.VerticalOffset + speed);
            }
        }

        private void Move(FocusNavigationDirection direction)
        {
            if (!window.IsActive)
            {
                window.Activate();
            }

            var focused = Keyboard.FocusedElement as UIElement;
            if (focused == null || !IsInside(focused, root))
            {
                FocusLater(mainView.Visibility == Visibility.Visible ? playButton : setupPrimary);
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
        x:Name='Root' Background='#F7181818' CornerRadius='0,0,20,20'
        BorderBrush='#26FFFFFF' BorderThickness='1,0,1,1'
        TextElement.FontFamily='Segoe UI Variable Display, Segoe UI'
        KeyboardNavigation.DirectionalNavigation='Contained'
        KeyboardNavigation.TabNavigation='Cycle'>
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

  <Grid>
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
        </Grid.ColumnDefinitions>
        <Button x:Name='ShuffleBtn' Grid.Column='0' Style='{StaticResource IconBtn}' FontSize='28'/>
        <Button x:Name='PrevBtn' Grid.Column='1' Style='{StaticResource IconBtn}' FontSize='34'/>
        <Button x:Name='PlayBtn' Grid.Column='2' Style='{StaticResource PlayBtn}'/>
        <Button x:Name='NextBtn' Grid.Column='3' Style='{StaticResource IconBtn}' FontSize='34'/>
        <Button x:Name='RepeatBtn' Grid.Column='4' Style='{StaticResource IconBtn}' FontSize='28'/>
      </Grid>

      <Grid x:Name='ProgressTrack' Grid.Row='2' Height='4' Background='#3DFFFFFF'>
        <Rectangle x:Name='ProgressFill' HorizontalAlignment='Left' Fill='#FFFFFF' Width='0'/>
      </Grid>

      <ScrollViewer x:Name='ListScroll' Grid.Row='3' Focusable='False'
                    VerticalScrollBarVisibility='Hidden' HorizontalScrollBarVisibility='Disabled'
                    PanningMode='VerticalOnly'>
        <StackPanel Margin='24,18,24,28'>
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
