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
        private DispatcherTimer hideTimer;
        private DispatcherTimer progressTimer;

        private double progressMs;
        private double durationMs;
        private bool playing;
        private DateTime stampUtc;

        public void Show(string title, string artist, string imageUrl, double progress, double duration, bool isPlaying)
        {
            EnsureWindow();
            titleText.Text = title ?? string.Empty;
            artistText.Text = artist ?? string.Empty;
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
            if (!window.IsVisible)
            {
                window.Show();
                MakeClickThrough();
            }

            if (!wasVisible)
            {
                card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)));
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(380))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }

            progressTimer.Start();
            hideTimer.Stop();
            hideTimer.Start();
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
                    window.Hide();
                }
            };
            card.BeginAnimation(UIElement.OpacityProperty, fade);
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 30, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            });
        }

        public void HideImmediately()
        {
            if (window == null)
            {
                return;
            }

            hideTimer.Stop();
            progressTimer.Stop();
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
            ((TextBlock)card.FindName("BrandGlyph")).Text = "";

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
            double left = 0, top = 0, width = SystemParameters.PrimaryScreenWidth;
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
                }
            }
            catch
            {
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
            remainingText.Text = durationMs > 0
                ? "-" + (int)remaining.TotalMinutes + ":" + remaining.Seconds.ToString("00")
                : string.Empty;
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
          <TextBlock x:Name='BrandGlyph' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='18'
                     Foreground='#FFFFFF' VerticalAlignment='Center' Margin='0,0,7,0'/>
          <TextBlock Text='Spotify' FontSize='17' FontWeight='SemiBold' Foreground='#FFFFFF' VerticalAlignment='Center'/>
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
