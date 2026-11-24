using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AnikiHelper.Services.UI
{
    /// <summary>
    /// After any popup window (e.g. Playnite's metadata download progress) closes, puts Playnite's
    /// main window back into an active state with focus on what was selected before, and logs any
    /// large element that is dimmed or covered so the cause of a darkened library/hub shows up.
    /// </summary>
    internal sealed class DimWatchdog
    {
        private readonly ILogger logger;
        private WeakReference lastFocus;
        private DateTime lastActivatedScanUtc = DateTime.MinValue;

        public DimWatchdog(ILogger logger)
        {
            this.logger = logger;
        }

        public void Start()
        {
            try
            {
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnWindowUnloaded));
                EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnGotFocus));

                var main = Application.Current?.MainWindow;
                if (main != null)
                {
                    main.Activated += (s, e) =>
                    {
                        if ((DateTime.UtcNow - lastActivatedScanUtc).TotalSeconds > 30)
                        {
                            lastActivatedScanUtc = DateTime.UtcNow;
                            Later(4000, () => Scan("main window activated"));
                        }
                    };
                }

                Later(90000, () => Scan("baseline 90s after start"));
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][DimCheck] Could not start.");
            }
        }

        private void OnGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            try
            {
                var element = e.NewFocus as DependencyObject;
                var main = Application.Current?.MainWindow;
                if (element != null && main != null && !(element is Window) && Window.GetWindow(element) == main)
                {
                    lastFocus = new WeakReference(element);
                }
            }
            catch
            {
            }
        }

        private void OnWindowUnloaded(object sender, RoutedEventArgs e)
        {
            var closed = sender as Window;
            var main = Application.Current?.MainWindow;
            if (closed == null || main == null || closed == main)
            {
                return;
            }

            var description = closed.GetType().FullName + " '" + closed.Title + "'";

            // Only Playnite's own popups (progress, dialogs, text input) can leave the main window
            // dimmed. Our toasts/panels closing must not pull focus back to Playnite — that stole
            // focus from the web browser / visualizer.
            if (!closed.GetType().FullName.StartsWith("Playnite.", StringComparison.Ordinal))
            {
                return;
            }

            Later(600, () =>
            {
                try
                {
                    logger?.Info("[AnikiHelper][DimCheck] Window closed: " + description +
                                 " | MainActive=" + main.IsActive + " Focus=" + Describe(Keyboard.FocusedElement as DependencyObject));

                    if (!main.IsVisible || OtherLargeWindowOpen(main))
                    {
                        Later(1500, () => ClearStuckChildOpened(main));
                        return;
                    }

                    main.Activate();
                    var focused = Keyboard.FocusedElement as DependencyObject;
                    if (focused == null || focused is Window || Window.GetWindow(focused) != main)
                    {
                        var previous = lastFocus?.Target as UIElement;
                        if (previous != null && previous.IsVisible && previous.Focusable && previous.IsEnabled)
                        {
                            previous.Focus();
                            Keyboard.Focus(previous);
                        }
                        else
                        {
                            main.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn(ex, "[AnikiHelper][DimCheck] Restore failed.");
                }

                Later(1500, () =>
                {
                    ClearStuckChildOpened(main);
                    Scan("after " + description + " closed");
                });
                Later(3000, () => ClearStuckChildOpened(main));
            });
        }

        /// <summary>
        /// Playnite's fullscreen window shows a dark overlay (BorderDim) while its ChildOpened flag
        /// is set. Opening and closing several progress windows quickly (game import) can leave
        /// the flag stuck on with nothing open. Clear it when no Playnite popup is actually showing.
        /// </summary>
        /// <summary>A full-screen window of ours (browser, visualizer) is in front: leave focus alone.</summary>
        private static bool OtherLargeWindowOpen(Window main)
        {
            foreach (Window other in Application.Current.Windows)
            {
                if (other != main && other.IsVisible && other.ActualWidth >= main.ActualWidth * 0.6 && other.ActualHeight >= main.ActualHeight * 0.6)
                {
                    return true;
                }
            }

            return false;
        }

        private void ClearStuckChildOpened(Window main)
        {
            try
            {
                if (main == null || !main.IsVisible)
                {
                    return;
                }

                foreach (Window other in Application.Current.Windows)
                {
                    if (other != main && other.IsVisible && other.GetType().FullName.StartsWith("Playnite.", StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                // The overlay binds to {Binding ChildOpened} on the window's DataContext (view model).
                var model = main.DataContext;
                if (model == null)
                {
                    logger?.Info("[AnikiHelper][DimCheck] Main window has no DataContext.");
                    return;
                }

                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var property = model.GetType().GetProperty("ChildOpened", flags);
                if (property == null || property.PropertyType != typeof(bool))
                {
                    logger?.Info("[AnikiHelper][DimCheck] ChildOpened not found on " + model.GetType().FullName);
                    return;
                }

                if (!Equals(property.GetValue(model), true))
                {
                    return;
                }

                var setter = property.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(model, new object[] { false });
                }
                else
                {
                    var backing = model.GetType().GetField("<ChildOpened>k__BackingField", flags) ??
                                  model.GetType().GetField("childOpened", flags);
                    backing?.SetValue(model, false);
                }

                // Make sure bindings hear about it even if the setter does not notify.
                var notify = model.GetType().GetMethod("OnPropertyChanged", flags, null, new[] { typeof(string) }, null);
                try { notify?.Invoke(model, new object[] { "ChildOpened" }); } catch { }

                logger?.Info("[AnikiHelper][DimCheck] Cleared stuck ChildOpened on " + model.GetType().Name +
                             " (now " + property.GetValue(model) + ").");
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][DimCheck] Could not clear ChildOpened.");
            }
        }

        private void Scan(string reason)
        {
            try
            {
                var main = Application.Current?.MainWindow;
                if (main == null || !main.IsVisible || main.ActualWidth < 10)
                {
                    return;
                }

                var found = new List<string>();
                Walk(main, main, 1.0, found);
                var text = new StringBuilder();
                text.Append("[AnikiHelper][DimCheck] Scan (" + reason + "): MainActive=" + main.IsActive +
                            " MainOpacity=" + main.Opacity.ToString("0.00") +
                            " Focus=" + Describe(Keyboard.FocusedElement as DependencyObject) +
                            " | large dimmed/dark elements: " + found.Count);
                foreach (var line in found)
                {
                    text.Append("\n    " + line);
                }

                logger?.Info(text.ToString());
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][DimCheck] Scan failed.");
            }
        }

        private static void Walk(DependencyObject node, Window main, double inheritedOpacity, List<string> found)
        {
            if (found.Count >= 40)
            {
                return;
            }

            var element = node as FrameworkElement;
            var opacity = inheritedOpacity;
            if (element != null)
            {
                if (element.Visibility != Visibility.Visible || element.ActualWidth < 1)
                {
                    return;
                }

                opacity *= element.Opacity;
                var large = element.ActualWidth >= main.ActualWidth * 0.45 && element.ActualHeight >= main.ActualHeight * 0.3;
                if (large)
                {
                    var dark = DarkFill(element);
                    if (element.Opacity < 0.95 || dark != null || element.Effect != null)
                    {
                        found.Add(Describe(element) +
                                  " Opacity=" + element.Opacity.ToString("0.00") +
                                  " Effective=" + opacity.ToString("0.00") +
                                  (dark != null ? " DarkFill=" + dark : string.Empty) +
                                  (element.Effect != null ? " Effect=" + element.Effect.GetType().Name : string.Empty) +
                                  " Size=" + (int)element.ActualWidth + "x" + (int)element.ActualHeight);
                    }
                }
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i), main, opacity, found);
            }
        }

        private static string DarkFill(FrameworkElement element)
        {
            Brush brush = null;
            if (element is Panel)
            {
                brush = ((Panel)element).Background;
            }
            else if (element is Border)
            {
                brush = ((Border)element).Background;
            }
            else if (element is Shape)
            {
                brush = ((Shape)element).Fill;
            }
            else if (element is Control)
            {
                brush = ((Control)element).Background;
            }

            var solid = brush as SolidColorBrush;
            if (solid == null || brush.Opacity <= 0.05)
            {
                return null;
            }

            var c = solid.Color;
            var alpha = c.A / 255.0 * brush.Opacity;
            var brightness = (c.R + c.G + c.B) / 765.0;
            return alpha >= 0.15 && brightness < 0.25 ? c.ToString() + (brush.Opacity < 1 ? "@" + brush.Opacity.ToString("0.00") : string.Empty) : null;
        }

        private static string Describe(DependencyObject node)
        {
            if (node == null)
            {
                return "(none)";
            }

            var element = node as FrameworkElement;
            var name = element != null && !string.IsNullOrEmpty(element.Name) ? element.Name : "-";
            var parentName = "-";
            var parent = VisualTreeHelper.GetParent(node);
            for (var depth = 0; parent != null && depth < 6; depth++)
            {
                var fe = parent as FrameworkElement;
                if (fe != null && !string.IsNullOrEmpty(fe.Name))
                {
                    parentName = fe.Name;
                    break;
                }

                parent = VisualTreeHelper.GetParent(parent);
            }

            return node.GetType().Name + "#" + name + " (in " + parentName + ")";
        }

        private static void Later(int milliseconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                action();
            };
            timer.Start();
        }
    }
}
