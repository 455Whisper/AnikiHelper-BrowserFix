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
                    closing = false;
                    IsOpen = true;
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
            effectText.Text = effects[effectIndex].Name;

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

            nowPlayingTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
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

        private void OnRendering(object sender, EventArgs e)
        {
            var now = clock.Elapsed.TotalSeconds;
            var delta = Math.Max(0.001, Math.Min(0.1, now - lastFrameSeconds));
            lastFrameSeconds = now;

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
            effectText.Text = effects[effectIndex].Name;
            effectToastText.Text = effects[effectIndex].Name;
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
                if (!IsOpen || window == null || state.IsFullPadState)
                {
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
                    e.Handled = true;
                    NextEffect(-1);
                    break;
                case Key.PageDown:
                    e.Handled = true;
                    NextEffect(1);
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

        private static void ActivateMainWindow()
        {
            try
            {
                var main = Application.Current?.MainWindow;
                main?.Activate();
                main?.Focus();
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

    <StackPanel VerticalAlignment='Center' HorizontalAlignment='Center' Margin='0,40,0,0'>
      <Border x:Name='CoverFrame' Width='437' Height='437' BorderBrush='#FFFFFF' BorderThickness='7'
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
