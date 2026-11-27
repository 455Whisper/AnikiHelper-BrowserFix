using AnikiHelper.Services.Spotify;
using AnikiHelper.Services.WebBrowser;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AnikiHelper.Services.Visualizer
{
    /// <summary>Full-screen element that draws the current effect every frame.</summary>
    internal sealed class EffectSurface : FrameworkElement
    {
        public VisualEffect Effect;
        public AudioFrame Audio;
        public double Time;
        public double Delta;

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (Effect != null && Audio != null && ActualWidth > 0)
            {
                Effect.Render(dc, new Size(ActualWidth, ActualHeight), Audio, Time, Delta);
            }
        }
    }

    /// <summary>
    /// Full-screen music visualizer: an audio-reactive effect behind the current Spotify
    /// track (cover, title, artist), with a slim top bar (Back, Spotify, Visualizer).
    /// </summary>
    internal sealed class AnikiVisualizerService : IDisposable
    {
        private readonly ILogger logger;
        private readonly AnikiSpotifyPanelService spotify;
        private readonly AudioSpectrumAnalyzer analyzer;
        private readonly AudioFrame audio = new AudioFrame();
        private readonly List<VisualEffect> effects = new List<VisualEffect>
        {
            new MeshGridEffect(),
            new RippleEffect(),
            new DotGridEffect(),
            new ParticleEffect()
        };

        private Window window;
        private FrameworkElement root;
        private EffectSurface surface;
        private Image cover;
        private Border coverFrame;
        private ScaleTransform coverScale;
        private TextBlock titleText;
        private TextBlock artistText;
        private TextBlock effectText;
        private TextBlock clockText;
        private Border effectToast;
        private TextBlock effectToastText;
        private Button backButton;
        private Button spotifyButton;
        private Button visualizerButton;
        private DispatcherTimer nowPlayingTimer;
        private DispatcherTimer clockTimer;
        private DispatcherTimer toastTimer;
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private double lastFrameSeconds;
        private bool rendering;
        private bool closing;
        private int effectIndex;
        private string coverUrl;
        private DateTime lastMoveUtc = DateTime.MinValue;
        private FocusNavigationDirection lastMoveDirection;
        private DateTime lastActivateUtc = DateTime.MinValue;
        private DateTime lastBackUtc = DateTime.MinValue;
        private DateTime lastEffectSwitchUtc = DateTime.MinValue;

        private object exclusiveController;
        private bool exclusivePrevious;
        private bool exclusiveActive;

        public AnikiVisualizerService(ILogger logger, AnikiSpotifyPanelService spotify)
        {
            this.logger = logger;
            this.spotify = spotify;
            spotify.NowPlayingChanged += async () => await RefreshNowPlayingAsync();
            analyzer = new AudioSpectrumAnalyzer(logger);
        }

        public event Action<bool> OpenStateChanged;

        public bool IsOpen { get; private set; }

        public void Toggle()
        {
            RunOnUi(() =>
            {
                if (IsOpen)
                {
                    Close();
                }
                else
                {
                    Open();
                }
            });
        }

        public void Open()
        {
            RunOnUi(() =>
            {
                if (IsOpen)
                {
                    return;
                }

                try
                {
                    EnsureWindow();
                    PositionWindow();
                    try
                    {
                        var main = Application.Current?.MainWindow;
                        var focused = Keyboard.FocusedElement as DependencyObject;
                        previousPlayniteFocus = main != null && focused != null && Window.GetWindow(focused) == main ? focused as UIElement : null;
                    }
                    catch
                    {
                    }

                    closing = false;
                    IsOpen = true;
                    lastTrackUri = null;
                    BoostPerformance(true);
                    window.Show();
                    window.Activate();
                    AcquireController();
                    OpenStateChanged?.Invoke(true);
                    root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));

                    analyzer.Start();
                    StartRendering();
                    nowPlayingTimer.Start();
                    clockTimer.Start();
                    UpdateClock();
                    var ignored = RefreshNowPlayingAsync();
                    FocusLater(visualizerButton);
                }
                catch (Exception ex)
                {
                    logger?.Error(ex, "[AnikiHelper][Visualizer] Failed to open.");
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
                BoostPerformance(false);
                yCardShown = false;
                songToast?.HideImmediately();
                nowPlayingTimer.Stop();
                clockTimer.Stop();
                ReleaseController();
                OpenStateChanged?.Invoke(false);
                if (spotify.IsOpen)
                {
                    spotify.Close();
                }

                var fade = new DoubleAnimation(root.Opacity, 0, TimeSpan.FromMilliseconds(250));
                fade.Completed += (s, e) =>
                {
                    if (!closing)
                    {
                        return;
                    }

                    closing = false;
                    StopRendering();
                    analyzer.Stop();
                    window.Hide();
                    ActivateMainWindow();
                };
                root.BeginAnimation(UIElement.OpacityProperty, fade);
            });
        }

        /// <summary>Brings the visualizer back to the front (e.g. after the Spotify panel closes over it).</summary>
        public void Reactivate()
        {
            RunOnUi(() =>
            {
                if (IsOpen && window != null)
                {
                    window.Activate();
                    FocusLater(spotifyButton);
                }
            });
        }

        // ======================= window =======================

        private void EnsureWindow()
        {
            if (window != null)
            {
                return;
            }

            root = (FrameworkElement)XamlReader.Parse(LayoutXaml);
            var surfaceHost = (Grid)root.FindName("SurfaceHost");
            surface = new EffectSurface { Audio = audio, Effect = effects[effectIndex] };
            surfaceHost.Children.Add(surface);

            cover = (Image)root.FindName("Cover");
            coverFrame = (Border)root.FindName("CoverFrame");
            coverScale = (ScaleTransform)root.FindName("CoverScale");
            titleText = (TextBlock)root.FindName("TitleText");
            infoPanel = (FrameworkElement)root.FindName("InfoPanel");
            artistText = (TextBlock)root.FindName("ArtistText");
            effectText = (TextBlock)root.FindName("EffectText");
            clockText = (TextBlock)root.FindName("ClockText");
            effectToast = (Border)root.FindName("EffectToast");
            effectToastText = (TextBlock)root.FindName("EffectToastText");
            backButton = (Button)root.FindName("BackBtn");
            spotifyButton = (Button)root.FindName("SpotifyBtn");
            visualizerButton = (Button)root.FindName("VisualizerBtn");

            backButton.Click += (s, e) => Close();
            spotifyButton.Click += (s, e) => spotify.Toggle(null);
            visualizerButton.Click += (s, e) => NextEffect(1);
            effectText.Text = EffectDisplayName(effects[effectIndex]);

            window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Background = Brushes.Black,
                Title = "Visualizer",
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

            window.PreviewKeyDown += Window_PreviewKeyDown;
            window.Closed += (s, e) =>
            {
                var wasOpen = IsOpen;
                IsOpen = false;
                closing = false;
                StopRendering();
                analyzer.Stop();
                ReleaseController();
                window = null;
                if (wasOpen)
                {
                    OpenStateChanged?.Invoke(false);
                }
            };

            nowPlayingTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(5) };
            nowPlayingTimer.Tick += async (s, e) => await RefreshNowPlayingAsync();
            clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            clockTimer.Tick += (s, e) => UpdateClock();
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            toastTimer.Tick += (s, e) =>
            {
                toastTimer.Stop();
                effectToast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(effectToast.Opacity, 0, TimeSpan.FromMilliseconds(400)));
            };
        }

        private void PositionWindow()
        {
            try
            {
                var main = Application.Current?.MainWindow;
                if (main == null)
                {
                    window.WindowState = WindowState.Maximized;
                    return;
                }

                var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(main).Handle);
                var toDip = PresentationSource.FromVisual(main)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var topLeft = toDip.Transform(new Point(screen.Bounds.Left, screen.Bounds.Top));
                var bottomRight = toDip.Transform(new Point(screen.Bounds.Right, screen.Bounds.Bottom));
                window.WindowState = WindowState.Normal;
                window.Left = topLeft.X;
                window.Top = topLeft.Y;
                window.Width = bottomRight.X - topLeft.X;
                window.Height = bottomRight.Y - topLeft.Y;

                // Designed for 1080 lines; scale the overlay text/cover on smaller screens.
                var scale = Math.Min(1.0, window.Height / 1080.0);
                var overlay = (FrameworkElement)root.FindName("Overlay");
                overlay.LayoutTransform = new ScaleTransform(scale, scale);
            }
            catch
            {
                window.WindowState = WindowState.Maximized;
            }
        }

        // ======================= drawing =======================

        private void StartRendering()
        {
            if (rendering)
            {
                return;
            }

            rendering = true;
            lastFrameSeconds = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopRendering()
        {
            if (!rendering)
            {
                return;
            }

            rendering = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private TimeSpan lastRenderingTime;
        private readonly List<double> refreshSamples = new List<double>();

        private void OnRendering(object sender, EventArgs e)
        {
            // Time each frame by the display's own vsync timestamps, and skip the duplicate
            // Rendering calls WPF sometimes makes within one frame. Motion then advances exactly
            // one refresh (e.g. 1/59.94 s) per frame on the TV, with no judder.
            var args = e as RenderingEventArgs;
            double delta;
            var now = clock.Elapsed.TotalSeconds;
            if (args != null)
            {
                if (args.RenderingTime == lastRenderingTime)
                {
                    return;
                }

                delta = (args.RenderingTime - lastRenderingTime).TotalSeconds;
                lastRenderingTime = args.RenderingTime;
                if (delta > 0.004 && delta < 0.05 && refreshSamples.Count < 240)
                {
                    refreshSamples.Add(delta);
                    if (refreshSamples.Count % 30 == 0)
                    {
                        var sorted = refreshSamples.OrderBy(v => v).ToList();
                        VisualEffect.FramePeriod = sorted[sorted.Count / 2];
                    }
                }

                // Snap to whole refreshes so a late frame doesn't make motion stutter.
                var period = VisualEffect.FramePeriod;
                delta = Math.Max(1, Math.Round(delta / period)) * period;
            }
            else
            {
                delta = now - lastFrameSeconds;
            }

            delta = Math.Max(0.001, Math.Min(0.1, delta));
            lastFrameSeconds = now;

            // Spotify panel open over the visualizer: still every frame (line drawing now runs on
            // all cores, leaving the UI thread room for the panel), just at a lower resolution.
            var shared = spotify.IsOpen;
            VisualEffect.Busy = shared;

            analyzer.Sample(audio, delta);
            surface.Time = now;
            surface.Delta = delta;
            surface.InvalidateVisual();

        }

        private void NextEffect(int step)
        {
            var now = DateTime.UtcNow;
            if ((now - lastEffectSwitchUtc).TotalMilliseconds < 250)
            {
                return;
            }

            lastEffectSwitchUtc = now;
            effectIndex = ((effectIndex + step) % effects.Count + effects.Count) % effects.Count;
            surface.Effect = effects[effectIndex];
            ShowEffectName();
        }

        private bool leftTriggerDown;
        private bool leftStickDown;
        private bool yDown;
        private bool yCardShown;
        private string songImageUrl;
        private double songProgressMs;
        private double songDurationMs;
        private bool songPlaying;
        private DateTime songFetchedUtc;
        private DateTime lastStyleSwitchUtc = DateTime.MinValue;
        private bool infoHidden;
        private FrameworkElement infoPanel;
        private string lastTrackUri;
        private SpotifyNowPlayingToast songToast;
        private bool rightTriggerDown;

        /// <summary>LT / RT: switch the current effect's look, if it has more than one.</summary>
        private void NextStyle(int step)
        {
            var effect = effects[effectIndex];
            if (effect.StyleCount < 2)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if ((now - lastStyleSwitchUtc).TotalMilliseconds < 200)
            {
                return;
            }

            lastStyleSwitchUtc = now;
            effect.Style = ((effect.Style + step) % effect.StyleCount + effect.StyleCount) % effect.StyleCount;
            logger?.Info("[AnikiHelper][Visualizer] Style -> " + effect.Name + " / " + effect.StyleName);
            ShowEffectName();
        }

        private string EffectDisplayName(VisualEffect effect)
        {
            return effect.StyleCount > 1 ? effect.Name + "  \u2022  " + effect.StyleName : effect.Name;
        }

        private void ShowEffectName()
        {
            var name = EffectDisplayName(effects[effectIndex]);
            effectText.Text = name;
            effectToastText.Text = name;
            effectToast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            toastTimer.Stop();
            toastTimer.Start();
        }

        // ======================= now playing =======================

        private async Task RefreshNowPlayingAsync()
        {
            if (!IsOpen)
            {
                return;
            }

            if (!spotify.IsSignedIn)
            {
                titleText.Text = "Spotify isn't connected";
                artistText.Text = "Open the Spotify panel to sign in";
                SetCover(null);
                return;
            }

            var body = await spotify.FetchCurrentlyPlayingAsync();
            var item = body?["item"] as JObject;
            if (item == null)
            {
                titleText.Text = "Nothing playing";
                artistText.Text = "Start something in Spotify";
                SetCover(null);
                return;
            }

            titleText.Text = (string)item["name"] ?? string.Empty;
            var artists = item["artists"] as JArray;
            artistText.Text = artists != null
                ? string.Join(", ", artists.Select(a => (string)a["name"]).Where(n => !string.IsNullOrEmpty(n)))
                : (string)item["show"]?["name"] ?? string.Empty;
            var images = (item["album"]?["images"] ?? item["images"] ?? item["show"]?["images"]) as JArray;
            SetCover(AnikiSpotifyPanelService.PickImage(images, 600));

            // Song changed while the visualizer is open: show the now-playing card bottom-left.
            var uri = (string)item["uri"];
            songImageUrl = AnikiSpotifyPanelService.PickImage(images, 300);
            songProgressMs = (double?)body["progress_ms"] ?? 0;
            songDurationMs = (double?)item["duration_ms"] ?? 0;
            songPlaying = (bool?)body["is_playing"] ?? false;
            songFetchedUtc = DateTime.UtcNow;
            if (string.IsNullOrEmpty(uri))
            {
                uri = titleText.Text + "|" + artistText.Text;
            }

            var changed = lastTrackUri != null && uri != lastTrackUri;
            if (!string.IsNullOrEmpty(uri))
            {
                lastTrackUri = uri;
            }

            if (changed)
            {
                VisualEffect.SongChanges++;
                logger?.Info("[AnikiHelper][Visualizer] Song changed -> " + titleText.Text);
                if (yCardShown)
                {
                    ShowHeldSongCard();
                }
            }

            // Not while the Spotify panel is open: it already shows the song, and raising the card
            // (owned by the visualizer) pulled the visualizer above the panel, hiding it.
            if (changed && IsOpen && !spotify.IsOpen && ((bool?)body["is_playing"] ?? false))
            {
                ShowSongToast(titleText.Text, artistText.Text, AnikiSpotifyPanelService.PickImage(images, 300),
                    (double?)body["progress_ms"] ?? 0, (double?)item["duration_ms"] ?? 0);
            }
        }

        private void ShowSongToast(string title, string artist, string imageUrl, double progress, double duration, bool playing = true)
        {
            try
            {
                if (songToast == null)
                {
                    songToast = new SpotifyNowPlayingToast(spotify.LogoPath, ToastCorner.BottomLeft) { AutoHideSeconds = 5 };
                }

                // Turned on with Y: stays up until Y turns it off. Otherwise (song change) it
                // closes by itself after 5 seconds.
                songToast.Pinned = yCardShown;
                songToast.OwnerWindow = window;
                songToast.Show(title, artist, imageUrl, progress, duration, playing);
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Visualizer] Now playing card failed.");
            }
        }

        private void ShowHeldSongCard()
        {
            if (string.IsNullOrEmpty(titleText?.Text))
            {
                return;
            }

            var progress = songProgressMs + (songPlaying ? (DateTime.UtcNow - songFetchedUtc).TotalMilliseconds : 0);
            if (songDurationMs > 0)
            {
                progress = Math.Min(progress, songDurationMs);
            }

            yCardShown = true;
            ShowSongToast(titleText.Text, artistText.Text, songImageUrl, progress, songDurationMs, songPlaying);
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

        private bool boosted;
        private System.Diagnostics.ProcessPriorityClass savedPriority;
        private System.Runtime.GCLatencyMode savedLatency;

        // While the visualizer is open, let Playnite use more of the PC for steadier frames:
        // higher process priority, 1 ms timer resolution, and garbage collection that avoids
        // long pauses. Everything is put back when it closes.
        private void BoostPerformance(bool on)
        {
            if (on == boosted)
            {
                return;
            }

            boosted = on;
            try
            {
                var process = System.Diagnostics.Process.GetCurrentProcess();
                if (on)
                {
                    savedPriority = process.PriorityClass;
                    savedLatency = System.Runtime.GCSettings.LatencyMode;
                    process.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
                    System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
                    timeBeginPeriod(1);
                }
                else
                {
                    process.PriorityClass = savedPriority;
                    System.Runtime.GCSettings.LatencyMode = savedLatency;
                    timeEndPeriod(1);
                }

                logger?.Info("[AnikiHelper][Visualizer] Performance boost " + (on ? "on" : "off") + ".");
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Visualizer] Performance boost failed.");
            }
        }

        // L3: hide or show the album cover, title and artist so only the visualizer is on screen.
        private void ToggleInfo()
        {
            if (infoPanel == null)
            {
                return;
            }

            infoHidden = !infoHidden;
            infoPanel.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(infoPanel.Opacity, infoHidden ? 0 : 1, TimeSpan.FromMilliseconds(300)));
        }

        private void SetCover(string url)
        {
            if (url == coverUrl)
            {
                return;
            }

            coverUrl = url;
            if (string.IsNullOrEmpty(url))
            {
                cover.Source = null;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(url);
                bitmap.DecodePixelWidth = 640;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                cover.BeginAnimation(UIElement.OpacityProperty, null);
                cover.Source = bitmap;
                cover.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)));
            }
            catch
            {
                cover.Source = null;
            }
        }

        private void UpdateClock()
        {
            clockText.Text = DateTime.Now.ToString("h:mm tt");
        }

        // ======================= controller / keys =======================

        public void ProcessControllerInput(WebBrowserGamepadInputState state)
        {
            RunOnUi(() =>
            {
                if (!IsOpen || window == null)
                {
                    return;
                }

                if (state.IsFullPadState)
                {
                    // Triggers switch the current effect's style (pressed past ~60%).
                    var left = state.LeftTrigger > 20000;
                    var right = state.RightTrigger > 20000;
                    if (left && !leftTriggerDown) { NextStyle(-1); }
                    if (right && !rightTriggerDown) { NextStyle(1); }
                    leftTriggerDown = state.LeftTrigger > 8000 && (left || leftTriggerDown);
                    rightTriggerDown = state.RightTrigger > 8000 && (right || rightTriggerDown);

                    // L3 toggles the cover, title and artist.
                    if (state.LeftStickHeld && !leftStickDown) { ToggleInfo(); }
                    leftStickDown = state.LeftStickHeld;

                    // Hold Y (while the song info is hidden): show the now-playing card for as
                    // long as it's held; it fades out when Y is released.
                    var y = (state.ButtonMask & (1 << 3)) != 0;
                    // Y toggles the now-playing card: on (stays until Y again) or off.
                    if (y && !yDown)
                    {
                        if (songToast != null && songToast.IsVisible)
                        {
                            yCardShown = false;
                            songToast.Pinned = false;
                            songToast.Hide();
                        }
                        else
                        {
                            ShowHeldSongCard();
                        }
                    }

                    yDown = y;
                    return;
                }

                if (!window.IsActive)
                {
                    window.Activate();
                }

                if (state.BackPressed || state.ClosePressed)
                {
                    Back();
                    return;
                }

                if (state.PreviousPressed) { NextEffect(-1); }
                if (state.NextPressed) { NextEffect(1); }
                if (state.DPadLeftPressed) { Move(FocusNavigationDirection.Left); }
                if (state.DPadRightPressed) { Move(FocusNavigationDirection.Right); }
                if (state.ActivatePressed) { Activate(); }
            });
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // The Spotify panel is open over the visualizer and gets the controller. A stray
            // Enter here (from A) would press the visualizer's "Spotify" button and close it.
            if (spotify.IsOpen)
            {
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                    e.Handled = true;
                    if (!e.IsRepeat && (e.Key == Key.Left || e.Key == Key.Right))
                    {
                        Move(e.Key == Key.Left ? FocusNavigationDirection.Left : FocusNavigationDirection.Right);
                    }

                    break;
                case Key.Enter:
                case Key.Space:
                    e.Handled = true;
                    if (!e.IsRepeat)
                    {
                        Activate();
                    }

                    break;
                case Key.Escape:
                case Key.Back:
                case Key.BrowserBack:
                    e.Handled = true;
                    Back();
                    break;
                case Key.PageUp:
                case Key.PageDown:
                case Key.Home:
                case Key.End:
                    // Playnite turns the triggers (and bumpers) into these keys for its own lists.
                    // The controller listener already handles LB/RB/LT/RT, so acting on these too
                    // made a trigger press also jump to the next effect. Swallow them.
                    e.Handled = true;
                    logger?.Info("[AnikiHelper][Visualizer] Ignored key " + e.Key + " (controller handles it).");
                    break;
            }
        }

        private void Back()
        {
            var now = DateTime.UtcNow;
            if ((now - lastBackUtc).TotalMilliseconds < 250)
            {
                return;
            }

            lastBackUtc = now;
            Close();
        }

        private void Move(FocusNavigationDirection direction)
        {
            var now = DateTime.UtcNow;
            if (direction == lastMoveDirection && (now - lastMoveUtc).TotalMilliseconds < 230)
            {
                return;
            }

            lastMoveDirection = direction;
            lastMoveUtc = now;

            var bar = new UIElement[] { backButton, spotifyButton, visualizerButton };
            var index = Array.IndexOf(bar, Keyboard.FocusedElement as UIElement);
            if (index < 0)
            {
                index = 2;
            }
            else
            {
                index = Math.Max(0, Math.Min(bar.Length - 1, index + (direction == FocusNavigationDirection.Right ? 1 : -1)));
            }

            bar[index].Focus();
            Keyboard.Focus(bar[index]);
        }

        private void Activate()
        {
            var now = DateTime.UtcNow;
            if ((now - lastActivateUtc).TotalMilliseconds < 250)
            {
                return;
            }

            lastActivateUtc = now;
            var button = Keyboard.FocusedElement as ButtonBase;
            if (button == null)
            {
                FocusLater(visualizerButton);
                return;
            }

            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        }

        private void FocusLater(UIElement element)
        {
            window?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsOpen)
                {
                    element.Focus();
                    Keyboard.Focus(element);
                }
            }), DispatcherPriority.Input);
        }

        // ======================= helpers =======================

        private void AcquireController()
        {
            try
            {
                var model = Application.Current?.MainWindow?.DataContext;
                var app = model?.GetType().GetProperty("App")?.GetValue(model, null);
                var controller = app?.GetType().GetProperty("GameController")?.GetValue(app, null);
                var property = controller?.GetType().GetProperty("StandardProcessingEnabled");
                if (property == null || !property.CanWrite)
                {
                    return;
                }

                if (!exclusiveActive)
                {
                    exclusivePrevious = property.GetValue(controller, null) is bool b && b;
                    exclusiveController = controller;
                    exclusiveActive = true;
                }

                property.SetValue(controller, false, null);
            }
            catch
            {
            }
        }

        private void ReleaseController()
        {
            if (!exclusiveActive)
            {
                return;
            }

            exclusiveActive = false;
            try
            {
                exclusiveController?.GetType().GetProperty("StandardProcessingEnabled")?.SetValue(exclusiveController, exclusivePrevious, null);
            }
            catch
            {
            }

            exclusiveController = null;
        }

        private UIElement previousPlayniteFocus;

        private void ActivateMainWindow()
        {
            try
            {
                var main = Application.Current?.MainWindow;
                main?.Activate();
                var previous = previousPlayniteFocus;
                previousPlayniteFocus = null;
                if (main != null && previous != null)
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

        public void Dispose()
        {
            try
            {
                StopRendering();
                analyzer.Dispose();
                ReleaseController();
                window?.Close();
            }
            catch
            {
            }
        }

        private const string LayoutXaml = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
      xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
      Background='Black' TextElement.FontFamily='Segoe UI Variable Display, Segoe UI'>
  <Grid.Resources>
    <Style x:Key='BarBtn' TargetType='Button'>
      <Setter Property='Foreground' Value='#B8C6E6'/>
      <Setter Property='FontSize' Value='24'/>
      <Setter Property='Margin' Value='0,0,46,0'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <StackPanel Background='Transparent'>
              <ContentPresenter x:Name='Text' HorizontalAlignment='Center' Margin='4,0'/>
              <Border x:Name='Line' Height='3' CornerRadius='1.5' Margin='0,7,0,0' Background='#B8C6E6' Opacity='0'/>
            </StackPanel>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'>
                <Setter Property='Foreground' Value='#FFFFFF'/>
              </Trigger>
              <Trigger Property='IsKeyboardFocused' Value='True'>
                <Setter Property='Foreground' Value='#FFFFFF'/>
                <Setter TargetName='Line' Property='Opacity' Value='1'/>
                <Setter TargetName='Line' Property='Background' Value='#FFFFFF'/>
              </Trigger>
              <Trigger Property='Tag' Value='Current'>
                <Setter TargetName='Line' Property='Opacity' Value='0.55'/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Grid.Resources>

  <Grid x:Name='SurfaceHost'/>

  <Grid x:Name='Overlay'>
    <Grid VerticalAlignment='Top' Height='96'>
      <Grid.Background>
        <LinearGradientBrush StartPoint='0,0' EndPoint='0,1'>
          <GradientStop Color='#CC000000' Offset='0'/>
          <GradientStop Color='#00000000' Offset='1'/>
        </LinearGradientBrush>
      </Grid.Background>
      <StackPanel Orientation='Horizontal' Margin='48,18,0,0' VerticalAlignment='Top'>
        <Button x:Name='BackBtn' Style='{StaticResource BarBtn}' Content='&#x2039;  Library'/>
        <Button x:Name='SpotifyBtn' Style='{StaticResource BarBtn}' Content='Spotify'/>
        <Button x:Name='VisualizerBtn' Style='{StaticResource BarBtn}' Content='Visualizer' Tag='Current'/>
      </StackPanel>
      <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Top' Margin='0,22,48,0'>
        <TextBlock Text='LB' FontSize='16' Foreground='#7F8FB0' VerticalAlignment='Center' Margin='0,0,10,0'/>
        <TextBlock x:Name='EffectText' FontSize='19' Foreground='#D6DEF0' VerticalAlignment='Center'/>
        <TextBlock Text='RB' FontSize='16' Foreground='#7F8FB0' VerticalAlignment='Center' Margin='10,0,34,0'/>
        <TextBlock x:Name='ClockText' FontSize='22' Foreground='#B8C6E6' VerticalAlignment='Center'/>
      </StackPanel>
    </Grid>

    <StackPanel x:Name='InfoPanel' VerticalAlignment='Center' HorizontalAlignment='Center' Margin='0,40,0,0'>
      <Border x:Name='CoverFrame' Width='481' Height='481' BorderThickness='0'
              Background='#14FFFFFF' RenderTransformOrigin='0.5,0.5'>
        <Border.RenderTransform>
          <ScaleTransform x:Name='CoverScale' ScaleX='1' ScaleY='1'/>
        </Border.RenderTransform>
        <Image x:Name='Cover' Stretch='UniformToFill' RenderOptions.BitmapScalingMode='HighQuality'/>
      </Border>
      <TextBlock x:Name='TitleText' FontSize='44' FontWeight='SemiBold' Foreground='#FFFFFF'
                 HorizontalAlignment='Center' Margin='0,30,0,0' TextTrimming='CharacterEllipsis' MaxWidth='1100'/>
      <TextBlock x:Name='ArtistText' FontSize='30' Foreground='#D9FFFFFF'
                 HorizontalAlignment='Center' Margin='0,6,0,0' TextTrimming='CharacterEllipsis' MaxWidth='1100'/>
    </StackPanel>

    <Border x:Name='EffectToast' VerticalAlignment='Bottom' HorizontalAlignment='Center' Margin='0,0,0,60'
            Background='#B3000000' CornerRadius='14' Padding='28,12' Opacity='0' IsHitTestVisible='False'>
      <TextBlock x:Name='EffectToastText' FontSize='26' Foreground='#FFFFFF'/>
    </Border>
  </Grid>
</Grid>";
    }
}
