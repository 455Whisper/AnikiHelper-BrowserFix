using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace AnikiHelper.Services.WebBrowser
{
    /// <summary>
    /// Chrome-style "Extensions" drop-down for the Aniki browser (top-right, rounded). Lists
    /// the loaded extensions with their icons; A opens an extension's popup (or its settings),
    /// the gear opens its settings page, and the folder button opens the extensions folder.
    /// Popups/settings are shown in a small embedded web view inside the drop-down.
    /// </summary>
    internal sealed class AnikiExtensionsOverlay
    {
        private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

        private static readonly Brush PanelBrush = Frozen(Color.FromRgb(0x29, 0x2A, 0x2D));
        private static readonly Brush HoverBrush = Frozen(Color.FromRgb(0x3C, 0x40, 0x43));
        private static readonly Brush FocusBrush = Frozen(Color.FromRgb(0x4A, 0x4F, 0x55));
        private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xE8, 0xEA, 0xED));
        private static readonly Brush SubtleBrush = Frozen(Color.FromRgb(0x9A, 0xA0, 0xA6));
        private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x8A, 0xB4, 0xF8));

        private readonly ILogger logger;
        private readonly Func<CoreWebView2> mainCore;
        private readonly Func<CoreWebView2Environment> environment;
        private readonly Func<string> folder;
        private readonly Func<string, string> directoryForId;
        private readonly Func<string, Task<bool>> removeExtension;

        private readonly Border root;
        private readonly StackPanel list;
        private readonly TextBlock status;
        private readonly Button folderButton;
        private readonly Grid listView;
        private readonly Grid popupView;
        private readonly TextBlock popupTitle;
        private readonly Grid popupHost;
        private WebView2CompositionControl popupWeb;
        private readonly List<List<Button>> rows = new List<List<Button>>();
        private int loadVersion;

        public AnikiExtensionsOverlay(
            ILogger logger,
            Func<CoreWebView2> mainCore,
            Func<CoreWebView2Environment> environment,
            Func<string> folder,
            Func<string, string> directoryForId,
            Func<string, Task<bool>> removeExtension)
        {
            this.removeExtension = removeExtension;
            this.logger = logger;
            this.mainCore = mainCore;
            this.environment = environment;
            this.folder = folder;
            this.directoryForId = directoryForId;

            // ---- list view ----
            var header = new Grid { Margin = new Thickness(20, 16, 12, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = "Extensions",
                FontSize = 21,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            folderButton = MakeIconButton("", "Open extensions folder");
            folderButton.Click += (s, e) => OpenFolder();
            Grid.SetColumn(folderButton, 1);
            header.Children.Add(folderButton);

            list = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };
            status = new TextBlock
            {
                Margin = new Thickness(20, 4, 20, 16),
                FontSize = 16,
                Foreground = SubtleBrush,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };

            var listStack = new StackPanel();
            listStack.Children.Add(header);
            listStack.Children.Add(new Border { Height = 1, Background = HoverBrush, Margin = new Thickness(0, 0, 0, 6) });
            listStack.Children.Add(status);
            listStack.Children.Add(new ScrollViewer
            {
                Content = list,
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Focusable = false
            });
            listView = new Grid();
            listView.Children.Add(listStack);

            // ---- popup / settings view ----
            popupView = new Grid { Visibility = Visibility.Collapsed };
            popupView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            popupView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var popupHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 12, 16, 10) };
            popupHeader.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily(IconFont),
                FontSize = 16,
                Foreground = SubtleBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            popupTitle = new TextBlock
            {
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 380
            };
            popupHeader.Children.Add(popupTitle);
            popupHeader.Children.Add(new TextBlock
            {
                Text = "   B to go back",
                FontSize = 14,
                Foreground = SubtleBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            popupView.Children.Add(popupHeader);
            popupHost = new Grid { Width = 460, Height = 600, Margin = new Thickness(8, 0, 8, 8), ClipToBounds = true, Background = Brushes.White };
            Grid.SetRow(popupHost, 1);
            popupView.Children.Add(popupHost);

            var content = new Grid();
            content.Children.Add(listView);
            content.Children.Add(popupView);

            root = new Border
            {
                Width = 476,
                CornerRadius = new CornerRadius(14),
                Background = PanelBrush,
                BorderBrush = Frozen(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 14, 18, 0),
                Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black },
                Child = content
            };

            // Rounded clipping for the embedded page too.
            content.SizeChanged += (s, e) =>
            {
                content.Clip = new RectangleGeometry(new Rect(0, 0, content.ActualWidth, content.ActualHeight), 13, 13);
            };
        }

        public FrameworkElement Element
        {
            get { return root; }
        }

        public bool IsOpen
        {
            get { return root.Visibility == Visibility.Visible; }
        }

        public bool IsPopupOpen
        {
            get { return IsOpen && popupView.Visibility == Visibility.Visible; }
        }

        public void Open()
        {
            if (IsOpen)
            {
                return;
            }

            ShowList();
            root.Visibility = Visibility.Visible;
            root.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            var t = LoadAsync();
        }

        public void Close()
        {
            ClosePopup();
            root.Visibility = Visibility.Collapsed;
        }

        // ---------------- navigation ----------------

        /// <summary>Returns false when the move should close the drop-down (D-pad right at the edge).</summary>
        public bool Move(FocusNavigationDirection direction)
        {
            var all = AllButtons();
            if (all.Count == 0)
            {
                return direction != FocusNavigationDirection.Right;
            }

            var focused = Keyboard.FocusedElement as Button;
            int r = -1, c = -1;
            for (var i = 0; i < all.Count && r < 0; i++)
            {
                c = all[i].IndexOf(focused);
                if (c >= 0)
                {
                    r = i;
                }
            }

            if (r < 0)
            {
                FocusButton(all[0][0]);
                return true;
            }

            switch (direction)
            {
                case FocusNavigationDirection.Up:
                    if (r > 0)
                    {
                        FocusButton(all[r - 1][Math.Min(c, all[r - 1].Count - 1)]);
                    }

                    return true;
                case FocusNavigationDirection.Down:
                    if (r < all.Count - 1)
                    {
                        FocusButton(all[r + 1][Math.Min(c, all[r + 1].Count - 1)]);
                    }

                    return true;
                case FocusNavigationDirection.Left:
                    if (c > 0)
                    {
                        FocusButton(all[r][c - 1]);
                    }

                    return true;
                case FocusNavigationDirection.Right:
                    if (c < all[r].Count - 1)
                    {
                        FocusButton(all[r][c + 1]);
                        return true;
                    }

                    return false;
            }

            return true;
        }

        public void ActivateFocused()
        {
            var focused = Keyboard.FocusedElement as Button;
            if (focused != null && AllButtons().Any(row => row.Contains(focused)))
            {
                focused.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, focused));
            }
        }

        public void FocusFirst()
        {
            var all = AllButtons();
            if (all.Count > 0)
            {
                FocusButton(all.Count > 1 ? all[1][0] : all[0][0]);
            }
        }

        private List<List<Button>> AllButtons()
        {
            var all = new List<List<Button>> { new List<Button> { folderButton } };
            all.AddRange(rows);
            return all;
        }

        private static void FocusButton(Button button)
        {
            button.Focus();
            Keyboard.Focus(button);
            button.BringIntoView();
        }

        // ---------------- list ----------------

        private void ShowList()
        {
            popupView.Visibility = Visibility.Collapsed;
            listView.Visibility = Visibility.Visible;
        }

        private async Task LoadAsync()
        {
            var version = ++loadVersion;
            list.Children.Clear();
            rows.Clear();
            status.Text = "Loading…";
            status.Visibility = Visibility.Visible;

            var core = mainCore();
            if (core == null)
            {
                status.Text = "Open a web page first.";
                return;
            }

            IReadOnlyList<CoreWebView2BrowserExtension> extensions;
            try
            {
                extensions = await core.Profile.GetBrowserExtensionsAsync();
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][WebBrowser][Extensions] Could not list extensions.");
                status.Text = "Couldn't list extensions.";
                return;
            }

            if (version != loadVersion)
            {
                return;
            }

            foreach (var extension in extensions)
            {
                // Built-in Edge components, not user extensions.
                var builtIn = extension.Name ?? string.Empty;
                if (builtIn.StartsWith("Microsoft Clipboard", StringComparison.OrdinalIgnoreCase) ||
                    builtIn.StartsWith("Microsoft Edge PDF", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = ExtensionInfo.Read(directoryForId(extension.Id), extension.Id, extension.Name);
                AddRow(extension, info);
            }

            status.Text = "No extensions yet. Put unpacked extensions (folders with a manifest.json) in the extensions folder, then reopen the browser.";
            status.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            root.Dispatcher.BeginInvoke(new Action(FocusFirst), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void AddRow(CoreWebView2BrowserExtension extension, ExtensionInfo info)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            main.Children.Add(MakeIcon(info));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            text.Children.Add(new TextBlock
            {
                Text = info.Name,
                FontSize = 18,
                Foreground = extension.IsEnabled ? TextBrush : SubtleBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!extension.IsEnabled)
            {
                text.Children.Add(new TextBlock { Text = "Turned off", FontSize = 13, Foreground = SubtleBrush });
            }

            Grid.SetColumn(text, 1);
            main.Children.Add(text);

            var mainButton = MakeRowButton(main);
            mainButton.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(info.PopupPath))
                {
                    var t = ShowPageAsync(info.Name, info.PageUrl(info.PopupPath));
                }
                else if (!string.IsNullOrEmpty(info.OptionsPath))
                {
                    var t = ShowPageAsync(info.Name + " – Settings", info.PageUrl(info.OptionsPath));
                }
            };
            row.Children.Add(mainButton);

            var buttons = new List<Button> { mainButton };
            if (!string.IsNullOrEmpty(info.OptionsPath))
            {
                var gear = MakeIconButton("", "Settings");
                gear.Click += (s, e) => { var t = ShowPageAsync(info.Name + " – Settings", info.PageUrl(info.OptionsPath)); };
                Grid.SetColumn(gear, 1);
                row.Children.Add(gear);
                buttons.Add(gear);
            }

            // Remove: press once to arm ("Remove?"), again within 3 seconds to remove.
            var trash = MakeIconButton("\uE74D", "Remove");
            var armedUntil = DateTime.MinValue;
            var trashGlyph = (TextBlock)trash.Content;
            trash.Click += async (s, e) =>
            {
                if (DateTime.UtcNow > armedUntil)
                {
                    armedUntil = DateTime.UtcNow.AddSeconds(3);
                    trashGlyph.Text = "Remove?";
                    trashGlyph.FontFamily = new FontFamily("Segoe UI");
                    trashGlyph.FontSize = 14;
                    trashGlyph.Foreground = Frozen(Color.FromRgb(0xF2, 0x8B, 0x82));
                    trash.Width = 86;
                    var reset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    reset.Tick += (s2, e2) =>
                    {
                        reset.Stop();
                        trashGlyph.Text = "\uE74D";
                        trashGlyph.FontFamily = new FontFamily(IconFont);
                        trashGlyph.FontSize = 20;
                        trashGlyph.Foreground = TextBrush;
                        trash.Width = 46;
                    };
                    reset.Start();
                    return;
                }

                armedUntil = DateTime.MinValue;
                var removed = removeExtension != null && await removeExtension(extension.Id);
                if (removed)
                {
                    await LoadAsync();
                }
            };
            Grid.SetColumn(trash, 2);
            row.Children.Add(trash);
            buttons.Add(trash);

            list.Children.Add(row);
            rows.Add(buttons);
        }

        private static FrameworkElement MakeIcon(ExtensionInfo info)
        {
            if (!string.IsNullOrEmpty(info.IconFile) && File.Exists(info.IconFile))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(info.IconFile);
                    bitmap.DecodePixelWidth = 64;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return new Image
                    {
                        Source = bitmap,
                        Width = 28,
                        Height = 28,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
                catch
                {
                }
            }

            // No icon: Chrome-like letter tile.
            return new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Background = HoverBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = string.IsNullOrEmpty(info.Name) ? "?" : info.Name.Substring(0, 1).ToUpperInvariant(),
                    Foreground = TextBrush,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        // ---------------- popup / settings ----------------

        private async Task ShowPageAsync(string title, string url)
        {
            try
            {
                if (popupWeb == null)
                {
                    var env = environment();
                    if (env == null)
                    {
                        return;
                    }

                    popupWeb = new WebView2CompositionControl
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch
                    };
                    popupHost.Children.Add(popupWeb);
                    await popupWeb.EnsureCoreWebView2Async(env);
                }

                popupTitle.Text = title;
                listView.Visibility = Visibility.Collapsed;
                popupView.Visibility = Visibility.Visible;
                popupWeb.CoreWebView2.Navigate(url);
                popupWeb.Focus();
                logger?.Info("[AnikiHelper][WebBrowser][Extensions] Opened " + url);
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][WebBrowser][Extensions] Could not open " + url);
                status.Text = "Couldn't open that page.";
                status.Visibility = Visibility.Visible;
                ShowList();
            }
        }

        /// <summary>B while a popup/settings page is shown: back to the list.</summary>
        public void ClosePopup()
        {
            if (popupView.Visibility != Visibility.Visible)
            {
                return;
            }

            try { popupWeb?.CoreWebView2?.Navigate("about:blank"); } catch { }
            ShowList();
            root.Dispatcher.BeginInvoke(new Action(FocusFirst), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void OpenFolder()
        {
            try
            {
                var path = folder();
                Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][WebBrowser][Extensions] Could not open the extensions folder.");
            }
        }

        // ---------------- visuals ----------------

        private static Button MakeRowButton(UIElement content)
        {
            return new Button
            {
                Content = content,
                Template = RowTemplate(new Thickness(12, 9, 12, 9)),
                FocusVisualStyle = null,
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
        }

        private static Button MakeIconButton(string glyph, string tooltip)
        {
            return new Button
            {
                Content = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily(IconFont),
                    FontSize = 20,
                    Foreground = TextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                ToolTip = tooltip,
                Width = 46,
                Height = 46,
                Template = RowTemplate(new Thickness(0)),
                FocusVisualStyle = null,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static ControlTemplate RowTemplate(Thickness padding)
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "Bg");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.PaddingProperty, padding);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, HoverBrush, "Bg"));
            template.Triggers.Add(hover);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BackgroundProperty, FocusBrush, "Bg"));
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, AccentBrush, "Bg"));
            template.Triggers.Add(focus);
            return template;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>What the overlay needs from an extension's manifest.json.</summary>
        private sealed class ExtensionInfo
        {
            public string Id;
            public string Name;
            public string IconFile;
            public string PopupPath;
            public string OptionsPath;

            public string PageUrl(string path)
            {
                return "chrome-extension://" + Id + "/" + path.TrimStart('/');
            }

            public static ExtensionInfo Read(string directory, string id, string fallbackName)
            {
                var info = new ExtensionInfo { Id = id, Name = fallbackName };
                if (string.IsNullOrEmpty(directory))
                {
                    return info;
                }

                try
                {
                    var manifest = JObject.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
                    var name = Localize(directory, manifest, (string)manifest["name"]);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        info.Name = name;
                    }

                    var action = (manifest["action"] ?? manifest["browser_action"] ?? manifest["page_action"]) as JObject;
                    info.PopupPath = (string)action?["default_popup"];
                    info.OptionsPath = (string)manifest["options_ui"]?["page"] ?? (string)manifest["options_page"];
                    info.IconFile = PickIcon(directory, manifest["icons"] as JObject) ??
                                    PickIcon(directory, action?["default_icon"] as JObject) ??
                                    IconPath(directory, (string)(action?["default_icon"] as JValue));
                }
                catch
                {
                }

                return info;
            }

            private static string PickIcon(string directory, JObject icons)
            {
                if (icons == null)
                {
                    return null;
                }

                var best = icons.Properties()
                    .Select(p => new { Size = int.TryParse(p.Name, out var size) ? size : 0, Path = (string)p.Value })
                    .Where(p => !string.IsNullOrEmpty(p.Path))
                    .OrderBy(p => p.Size >= 32 ? p.Size : 1000 + (32 - p.Size))
                    .FirstOrDefault();
                return best == null ? null : IconPath(directory, best.Path);
            }

            private static string IconPath(string directory, string relative)
            {
                if (string.IsNullOrEmpty(relative))
                {
                    return null;
                }

                var path = Path.Combine(directory, relative.TrimStart('/').Replace('/', '\\'));
                return File.Exists(path) ? path : null;
            }

            private static string Localize(string directory, JObject manifest, string value)
            {
                if (value == null || !value.StartsWith("__MSG_", StringComparison.Ordinal) || !value.EndsWith("__", StringComparison.Ordinal))
                {
                    return value;
                }

                var key = value.Substring(6, value.Length - 8);
                var locales = new[] { (string)manifest["default_locale"], "en", "en_US" };
                foreach (var locale in locales.Where(l => !string.IsNullOrEmpty(l)))
                {
                    try
                    {
                        var file = Path.Combine(directory, "_locales", locale, "messages.json");
                        if (!File.Exists(file))
                        {
                            continue;
                        }

                        var messages = JObject.Parse(File.ReadAllText(file));
                        var match = messages.Properties().FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                        var message = (string)match?.Value?["message"];
                        if (!string.IsNullOrEmpty(message))
                        {
                            return message;
                        }
                    }
                    catch
                    {
                    }
                }

                return value;
            }
        }
    }
}
