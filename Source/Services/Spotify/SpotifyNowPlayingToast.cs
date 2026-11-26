using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AnikiHelper.Services.Spotify
{
    /// <summary>
    /// "Now playing" card shown for a few seconds when the Spotify track changes.
    /// The background is the album art, enlarged and heavily blurred, so the card
    /// takes on the colors of each cover. Never takes focus or mouse input.
    /// </summary>
    internal sealed class SpotifyNowPlayingToast
    {
        private const double CardWidth = 420;
        private const double CardHeight = 160;
        private const double ShowSeconds = 6;

        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        private readonly string logoPath;
        private Image brandLogo;
        private TextBlock brandGlyph;
        private bool logoLoaded;
        private Window window;
        private FrameworkElement card;
        private TranslateTransform slide;
        private Image backgroundArt;
        private Image art;
        private TextBlock titleText;
        private TextBlock artistText;
        private Grid progressTrack;
        private Rectangle progressFill;
        private TextBlock remainingText;
        private WaveText titleWave;
        private WaveText artistWave;
        private WaveText brandWave;
        private WaveText remainingWave;
        private bool waving;
        private readonly System.Diagnostics.Stopwatch waveClock = System.Diagnostics.Stopwatch.StartNew();
        private DispatcherTimer hideTimer;
        private DispatcherTimer progressTimer;

        private double progressMs;
        private double durationMs;
        private bool playing;
        private DateTime stampUtc;

        public SpotifyNowPlayingToast(string logoPath, bool bottomLeft = false)
        {
            this.logoPath = logoPath;
            this.bottomLeft = bottomLeft;
        }

        // Bottom-left placement (used over the visualizer); slides in from the left.
        private readonly bool bottomLeft;

        /// <summary>Window the card stays above (the visualizer), if any.</summary>
        public Window OwnerWindow { get; set; }

        private double SlideFrom => bottomLeft ? -40 : 40;

        public void Show(string title, string artist, string imageUrl, double progress, double duration, bool isPlaying)
        {
            EnsureWindow();
            TryLoadLogo();
            titleWave.SetText(title ?? string.Empty);
            artistWave.SetText(artist ?? string.Empty);
            brandWave.SetText("Spotify");
            progressMs = progress;
            durationMs = duration;
            playing = isPlaying;
            stampUtc = DateTime.UtcNow;

            var image = Load(imageUrl, 300);
            art.Source = image;
            backgroundArt.Source = (ImageSource)image ?? AnikiSpotifyPanelService.GetDefaultBackground();

            Position();
            UpdateProgress();

            var wasVisible = window.IsVisible && card.Opacity > 0.5;
            if (!window.IsVisible && OwnerWindow != null && window.Owner != OwnerWindow)
            {
                try { window.Owner = OwnerWindow; } catch { }
            }

            if (!window.IsVisible)
            {
                window.Show();
                MakeClickThrough();
            }

            if (!wasVisible)
            {
                card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)));
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(SlideFrom, 0, TimeSpan.FromMilliseconds(380))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }

            progressTimer.Start();
            StartWave();
            hideTimer.Stop();
            hideTimer.Start();
        }

        private void StartWave()
        {
            if (waving)
            {
                return;
            }

            waving = true;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopWave()
        {
            if (!waving)
            {
                return;
            }

            waving = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            var t = waveClock.Elapsed.TotalSeconds;
            titleWave.Animate(t);
            artistWave.Animate(t);
            brandWave.Animate(t);
            remainingWave.Animate(t);
        }

        public void Hide()
        {
            if (window == null || !window.IsVisible)
            {
                return;
            }

            hideTimer.Stop();
            var fade = new DoubleAnimation(card.Opacity, 0, TimeSpan.FromMilliseconds(450));
            fade.Completed += (s, e) =>
            {
                if (card.Opacity <= 0.01)
                {
                    progressTimer.Stop();
                    StopWave();
                    window.Hide();
                }
            };
            card.BeginAnimation(UIElement.OpacityProperty, fade);
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, bottomLeft ? -30 : 30, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            });
        }

        /// <summary>Keep a visible card up: restarts its auto-hide countdown.</summary>
        public void Hold()
        {
            if (window != null && window.IsVisible && hideTimer != null)
            {
                hideTimer.Stop();
                hideTimer.Start();
            }
        }

        public bool IsShowing => window != null && window.IsVisible && card != null && card.Opacity > 0.5;

        public void HideImmediately()
        {
            if (window == null)
            {
                return;
            }

            hideTimer.Stop();
            progressTimer.Stop();
            StopWave();
            card.BeginAnimation(UIElement.OpacityProperty, null);
            card.Opacity = 0;
            window.Hide();
        }

        private void EnsureWindow()
        {
            if (window != null)
            {
                return;
            }

            card = (FrameworkElement)XamlReader.Parse(CardXaml);
            slide = (TranslateTransform)card.FindName("Slide");
            backgroundArt = (Image)card.FindName("BgArt");
            art = (Image)card.FindName("Art");
            titleText = (TextBlock)card.FindName("TitleText");
            artistText = (TextBlock)card.FindName("ArtistText");
            progressTrack = (Grid)card.FindName("ProgressTrack");
            progressFill = (Rectangle)card.FindName("ProgressFill");
            remainingText = (TextBlock)card.FindName("RemainingText");
            brandGlyph = (TextBlock)card.FindName("BrandGlyph");
            brandGlyph.Text = "";
            brandLogo = (Image)card.FindName("BrandLogo");
            titleWave = new WaveText(titleText, 262, 2.6, 0.0);
            artistWave = new WaveText(artistText, 262, 1.95, 0.35);
            brandWave = new WaveText((TextBlock)card.FindName("BrandText"), 120, 1.6, 0.7);
            remainingWave = new WaveText(remainingText, 80, 1.3, 1.05);

            var clipHost = (FrameworkElement)card.FindName("ClipHost");
            clipHost.Clip = new RectangleGeometry(new Rect(0, 0, CardWidth, CardHeight), 18, 18);

            window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                IsHitTestVisible = false,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                Width = CardWidth + 40,
                Height = CardHeight + 40,
                Content = card,
                Title = "Spotify now playing"
            };

            hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ShowSeconds) };
            hideTimer.Tick += (s, e) => Hide();
            progressTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
            progressTimer.Tick += (s, e) => UpdateProgress();
        }

        // Uses the user's own logo.png from the Spotify settings folder when present.
        private void TryLoadLogo()
        {
            if (logoLoaded || brandLogo == null || string.IsNullOrEmpty(logoPath) || !System.IO.File.Exists(logoPath))
            {
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(logoPath);
                bitmap.DecodePixelWidth = 64;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                brandLogo.Source = bitmap;
                brandLogo.Visibility = Visibility.Visible;
                brandGlyph.Visibility = Visibility.Collapsed;
                logoLoaded = true;
            }
            catch
            {
            }
        }

        private void MakeClickThrough()
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                var style = GetWindowLong(handle, GwlExStyle);
                SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
            }
            catch
            {
            }
        }

        private void Position()
        {
            double left = 0, top = 0, width = SystemParameters.PrimaryScreenWidth, height = SystemParameters.PrimaryScreenHeight;
            try
            {
                var main = Application.Current?.MainWindow;
                if (main != null)
                {
                    var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(main).Handle);
                    var toDip = PresentationSource.FromVisual(main)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                    var topLeft = toDip.Transform(new Point(screen.Bounds.Left, screen.Bounds.Top));
                    var bottomRight = toDip.Transform(new Point(screen.Bounds.Right, screen.Bounds.Bottom));
                    left = topLeft.X;
                    top = topLeft.Y;
                    width = bottomRight.X - topLeft.X;
                    height = bottomRight.Y - topLeft.Y;
                }
            }
            catch
            {
            }

            if (bottomLeft)
            {
                window.Left = left + 24;
                window.Top = top + height - window.Height - 24;
                return;
            }

            // Top-right corner, just below the theme's top bar.
            window.Left = left + width - window.Width - 24;
            window.Top = top + 84;
        }

        private void UpdateProgress()
        {
            if (progressTrack == null)
            {
                return;
            }

            var current = progressMs + (playing ? (DateTime.UtcNow - stampUtc).TotalMilliseconds : 0);
            if (durationMs > 0)
            {
                current = Math.Min(current, durationMs);
            }

            var fraction = durationMs > 0 ? Math.Max(0, current / durationMs) : 0;
            progressFill.Width = progressTrack.ActualWidth * fraction;
            var remaining = TimeSpan.FromMilliseconds(Math.Max(0, durationMs - current));
            remainingWave?.SetText(durationMs > 0
                ? "-" + (int)remaining.TotalMinutes + ":" + remaining.Seconds.ToString("00")
                : string.Empty);
        }

        private static BitmapImage Load(string url, int decodeWidth)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(url);
                bitmap.DecodePixelWidth = decodeWidth;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Replaces a TextBlock with one TextBlock per character and moves them in a
        /// travelling wave (bob + slight tilt), like bouncy animated title text.
        /// </summary>
        private sealed class WaveText
        {
            private const double PeriodSeconds = 1.5;
            private const double PhaseStep = 0.55;

            private readonly TextBlock template;
            private readonly StackPanel panel;
            private readonly double maxWidth;
            private readonly double amplitude;
            private readonly double phaseOffset;
            private string text;

            public WaveText(TextBlock source, double maxWidth, double amplitude, double phaseOffset)
            {
                template = source;
                this.maxWidth = maxWidth;
                this.amplitude = amplitude;
                this.phaseOffset = phaseOffset;
                panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = source.Margin,
                    HorizontalAlignment = source.HorizontalAlignment,
                    VerticalAlignment = source.VerticalAlignment,
                    IsHitTestVisible = false
                };
                Grid.SetRow(panel, Grid.GetRow(source));
                Grid.SetColumn(panel, Grid.GetColumn(source));
                Grid.SetColumnSpan(panel, Grid.GetColumnSpan(source));

                var parent = source.Parent as Panel;
                if (parent != null)
                {
                    parent.Children.Insert(parent.Children.IndexOf(source) + 1, panel);
                }

                source.Visibility = Visibility.Collapsed;
            }

            public void SetText(string value)
            {
                value = Fit(value ?? string.Empty);
                if (value == text)
                {
                    return;
                }

                text = value;
                panel.Children.Clear();
                foreach (var ch in value)
                {
                    var group = new TransformGroup();
                    group.Children.Add(new RotateTransform());
                    group.Children.Add(new TranslateTransform());
                    panel.Children.Add(new TextBlock
                    {
                        Text = ch.ToString(),
                        FontFamily = template.FontFamily,
                        FontSize = template.FontSize,
                        FontWeight = template.FontWeight,
                        Foreground = template.Foreground,
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        RenderTransform = group
                    });
                }
            }

            public void Animate(double seconds)
            {
                var omega = 2 * Math.PI / PeriodSeconds;
                for (var i = 0; i < panel.Children.Count; i++)
                {
                    var letter = (TextBlock)panel.Children[i];
                    var group = (TransformGroup)letter.RenderTransform;
                    var phase = omega * seconds - (i * PhaseStep) - phaseOffset;
                    ((TranslateTransform)group.Children[1]).Y = -amplitude * Math.Sin(phase);
                    ((RotateTransform)group.Children[0]).Angle = 2.6 * Math.Cos(phase);
                }
            }

            private string Fit(string value)
            {
                if (Measure(value) <= maxWidth)
                {
                    return value;
                }

                for (var length = value.Length - 1; length > 0; length--)
                {
                    var candidate = value.Substring(0, length).TrimEnd() + "…";
                    if (Measure(candidate) <= maxWidth)
                    {
                        return candidate;
                    }
                }

                return "…";
            }

            private double Measure(string value)
            {
                var typeface = new Typeface(template.FontFamily, FontStyles.Normal, template.FontWeight, FontStretches.Normal);
#pragma warning disable 618
                var formatted = new FormattedText(
                    value,
                    System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    template.FontSize,
                    Brushes.White);
#pragma warning restore 618
                return formatted.WidthIncludingTrailingWhitespace;
            }
        }

        private const string CardXaml = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
      xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
      Width='420' Height='160' Margin='20' Opacity='0'
      TextElement.FontFamily='Segoe UI Variable Display, Segoe UI'>
  <Grid.RenderTransform>
    <TranslateTransform x:Name='Slide'/>
  </Grid.RenderTransform>
  <Grid x:Name='ClipHost' Background='#FF202020'>
    <Image x:Name='BgArt' Stretch='UniformToFill' RenderTransformOrigin='0.5,0.5'
           RenderOptions.BitmapScalingMode='LowQuality'>
      <Image.RenderTransform>
        <ScaleTransform ScaleX='1.8' ScaleY='1.8'/>
      </Image.RenderTransform>
      <Image.Effect>
        <BlurEffect Radius='70' KernelType='Gaussian' RenderingBias='Performance'/>
      </Image.Effect>
    </Image>
    <Border Background='#5C000000'/>
    <Border BorderBrush='#26FFFFFF' BorderThickness='1' CornerRadius='18'/>
    <Grid Margin='18,18,20,14'>
      <Grid.RowDefinitions>
        <RowDefinition Height='*'/>
        <RowDefinition Height='Auto'/>
      </Grid.RowDefinitions>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width='Auto'/>
        <ColumnDefinition Width='*'/>
      </Grid.ColumnDefinitions>
      <Border Width='96' Height='96' CornerRadius='8' Background='#33000000' ClipToBounds='True' VerticalAlignment='Top'>
        <Image x:Name='Art' Stretch='UniformToFill' RenderOptions.BitmapScalingMode='HighQuality'/>
      </Border>
      <Grid Grid.Column='1' Margin='18,0,0,0'>
        <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Top'>
          <Image x:Name='BrandLogo' Width='20' Height='20' Margin='0,0,4,0' VerticalAlignment='Center'
                 Visibility='Collapsed' RenderOptions.BitmapScalingMode='HighQuality'/>
          <TextBlock x:Name='BrandGlyph' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='18'
                     Foreground='#FFFFFF' VerticalAlignment='Center' Margin='0,0,7,0'/>
          <TextBlock x:Name='BrandText' Text='Spotify' FontSize='17' FontWeight='SemiBold' Foreground='#FFFFFF' VerticalAlignment='Center'/>
        </StackPanel>
        <StackPanel VerticalAlignment='Center' Margin='0,26,0,0'>
          <TextBlock x:Name='TitleText' FontSize='23' FontWeight='Bold' Foreground='#FFFFFF' TextTrimming='CharacterEllipsis'/>
          <TextBlock x:Name='ArtistText' FontSize='18' Foreground='#D9FFFFFF' TextTrimming='CharacterEllipsis' Margin='0,4,0,0'/>
        </StackPanel>
      </Grid>
      <Grid Grid.Row='1' Grid.ColumnSpan='2' Margin='0,12,0,0'>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width='*'/>
          <ColumnDefinition Width='Auto'/>
        </Grid.ColumnDefinitions>
        <Grid x:Name='ProgressTrack' Height='5' VerticalAlignment='Center'>
          <Border Background='#4DFFFFFF' CornerRadius='2.5'/>
          <Rectangle x:Name='ProgressFill' HorizontalAlignment='Left' Fill='#FFFFFF' RadiusX='2.5' RadiusY='2.5' Width='0'/>
        </Grid>
        <TextBlock x:Name='RemainingText' Grid.Column='1' FontSize='16' Foreground='#FFFFFF' Margin='16,0,0,0' VerticalAlignment='Center'/>
      </Grid>
    </Grid>
  </Grid>
</Grid>";
    }
}
