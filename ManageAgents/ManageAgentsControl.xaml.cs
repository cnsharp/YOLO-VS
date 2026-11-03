using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.PlatformUI;

#pragma warning disable VSTHRD001 // Use SwitchToMainThreadAsync instead of Dispatcher (background checker -> UI marshal)
#pragma warning disable VSTHRD100 // Avoid async void (required for WPF event handlers)

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// The Manage Agents document-tab content. Mirrors the IntelliJ / VSCode Agent Manager page:
    /// a grid of uniform 300x200 cards, each showing the agent icon, name, installed (current)
    /// and latest version, a status badge, and Install / Update / Website actions. Versions come
    /// from <see cref="AgentUpdateChecker"/> (cache-first, refreshed on "Check Updates").
    ///
    /// Because a document view outlives the tool window, the control subscribes to the background
    /// version checker while loaded and unsubscribes on <see cref="Shutdown"/> so a closed tab
    /// cannot leak a reference through the static event.
    /// </summary>
    public partial class ManageAgentsControl : UserControl
    {
        // Per-command busy flag so a single agent can't be installed/updated twice at once.
        private readonly HashSet<string> _busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _shutdown;

        // Theme-driven brushes (resolved once on Loaded).
        private Brush _bg = Brushes.White;
        private Brush _cardBg = Brushes.WhiteSmoke;
        private Brush _fg = Brushes.Black;
        private Brush _muted = Brushes.Gray;
        private Brush _border = new SolidColorBrush(Color.FromRgb(0x3e, 0x3e, 0x42));
        private Brush _badgeBg = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private Brush _badgeFg = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x4e));
        private Brush _warnFg = new SolidColorBrush(Color.FromRgb(0xf4, 0x87, 0x71));

        public ManageAgentsControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyVsTheme();
            AgentUpdateChecker.AddListener(OnUpdateResultsChanged);
            InstalledAgents.CacheChanged += OnAgentCacheChanged;
            // Show cached versions immediately (cache-first), then refresh from the background scan.
            AgentUpdateChecker.Seed();
            Rebuild();
            // Mirror VSCode: ensure a background version scan runs if it wasn't already auto-checked.
            AgentUpdateChecker.EnsureCheckedForUi();
        }

        /// <summary>Stop listening to background checks (called from the pane's Dispose).</summary>
        public void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            AgentUpdateChecker.RemoveListener(OnUpdateResultsChanged);
            InstalledAgents.CacheChanged -= OnAgentCacheChanged;
        }

        // ── theming ───────────────────────────────────────────────

        private void ApplyVsTheme()
        {
            // Pull the live VS theme colors; fall back to dark defaults if theming is unavailable.
            Color bgc = Color.FromRgb(0x25, 0x25, 0x26);
            Color fgc = Color.FromRgb(0xF1, 0xF1, 0xF1);
            try
            {
                var bg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
                bgc = Color.FromRgb(bg.R, bg.G, bg.B);
                var fg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
                fgc = Color.FromRgb(fg.R, fg.G, fg.B);
            }
            catch
            {
            }

            // Every surface is derived from the window background so the page tracks light AND dark
            // themes. Previously the card background was a hardcoded WhiteSmoke, so in dark theme the
            // cards stayed bright white and the (light) card text became unreadable.
            bool dark = IsDark(bgc);
            _bg = new SolidColorBrush(bgc);
            _fg = new SolidColorBrush(fgc);
            _muted = new SolidColorBrush(Mix(_fg, 0.45));
            _cardBg = Shade(bgc, dark ? 16 : -10);     // cards sit slightly raised from the window
            _border = Shade(bgc, dark ? 40 : -34);
            _badgeBg = Shade(bgc, dark ? 30 : -22);     // secondary-button + badge surface
            // _badgeFg (gold) and _warnFg (salmon) read on both themes; left as-is.

            Background = _bg;
            Toolbar.Background = _bg;
            Toolbar.BorderBrush = _border;
            StatusBorder.Background = _bg;
            StatusBorder.BorderBrush = _border;
            Heading.Foreground = _fg;
            StatusLine.Foreground = _muted;
        }

        // Perceived-luminance test (ITU-R BT.601) used to pick light-vs-dark shade directions.
        private static bool IsDark(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;

        // Lightens (delta>0) or darkens (delta<0) a color, clamping each channel to [0,255].
        private static SolidColorBrush Shade(Color c, int delta) =>
            new SolidColorBrush(Color.FromRgb(ClampByte(c.R + delta), ClampByte(c.G + delta), ClampByte(c.B + delta)));

        private static byte ClampByte(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        // Returns a grey derived from the foreground (for muted text) at the given alpha-ish mix.
        private static Color Mix(Brush fg, double grey)
        {
            if (fg is SolidColorBrush sb)
            {
                var c = sb.Color;
                byte v = (byte)(c.R * (1 - grey) + 128 * grey);
                return Color.FromRgb(v, v, v);
            }
            return Colors.Gray;
        }

        // ── event handlers ────────────────────────────────────────

        private void OnUpdateResultsChanged()
        {
            if (_shutdown) return;
            _ = Dispatcher.InvokeAsync(() =>
            {
                Rebuild();
                if (!AgentUpdateChecker.IsChecking)
                {
                    var avail = AgentUpdateChecker.Snapshot().Values.Count(v => v.Status == AgentUpdateChecker.Status.UPDATE_AVAILABLE);
                    SetStatus(avail > 0
                        ? string.Format(CnSharp.VSIX.Yolo.Resources.Manage_StatusUpdatesAvailable, avail)
                        : string.Empty);
                }
            });
        }

        private void OnAgentCacheChanged()
        {
            if (_shutdown) return;
            _ = Dispatcher.InvokeAsync(Rebuild);
        }

        private void OnCheckUpdates(object sender, RoutedEventArgs e)
        {
            if (AgentUpdateChecker.IsChecking) return;
            SetStatus(CnSharp.VSIX.Yolo.Resources.Manage_StatusChecking);
            AgentUpdateChecker.CheckAll();
        }

        private void OnUpdateAll(object sender, RoutedEventArgs e)
        {
            if (AgentUpdateChecker.IsChecking) return;
            foreach (var def in AgentRegistry.All)
            {
                var info = AgentUpdateChecker.InfoFor(def.Command);
                if (info?.Status == AgentUpdateChecker.Status.UPDATE_AVAILABLE && CanUpgrade(def))
                    InstallAgent(def, true);
            }
        }

        // ── card model ────────────────────────────────────────────

        private bool CanUpgrade(AgentDef def)
        {
            var spec = def.Install;
            return spec != null && (spec.HasUpdateCommand || !string.IsNullOrEmpty(def.Url));
        }

        private bool CanInstall(AgentDef def)
        {
            var spec = def.Install;
            if (spec == null) return false;
            return spec.Type == "url"
                ? !string.IsNullOrEmpty(spec.Url)
                : (spec.Automatable && !string.IsNullOrEmpty(spec.Target) && !InstalledAgents.IsInstalled(def.Command));
        }

        // ── rendering ──────────────────────────────────────────────

        private void Rebuild()
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(Rebuild);
                return;
            }

            CardPanel.Children.Clear();
            // Installed agents first, uninstalled after; stable alphabetical order within each group.
            var defs = AgentRegistry.All
                .OrderByDescending(def => InstalledAgents.IsInstalled(def.Command))
                .ThenBy(def => def.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (defs.Count == 0)
            {
                CardPanel.Children.Add(new TextBlock
                {
                    Text = CnSharp.VSIX.Yolo.Resources.Manage_Empty,
                    Foreground = _muted,
                    Margin = new Thickness(4)
                });
            }
            else
            {
                foreach (var def in defs)
                    CardPanel.Children.Add(BuildCard(def));
            }

            var updatable = defs.Count(def =>
            {
                var info = AgentUpdateChecker.InfoFor(def.Command);
                return info?.Status == AgentUpdateChecker.Status.UPDATE_AVAILABLE &&
                       CanUpgrade(def) && !_busy.Contains(def.Command);
            });
            UpdateAllBtn.Content = updatable > 0
                ? string.Format(CnSharp.VSIX.Yolo.Resources.Manage_UpdateAllCount, updatable)
                : CnSharp.VSIX.Yolo.Resources.Manage_UpdateAll;
            UpdateAllBtn.IsEnabled = !AgentUpdateChecker.IsChecking && updatable > 0;
            CheckUpdatesBtn.IsEnabled = !AgentUpdateChecker.IsChecking;
        }

        private Border BuildCard(AgentDef def)
        {
            var info = AgentUpdateChecker.InfoFor(def.Command);
            var status = info?.Status ?? AgentUpdateChecker.Status.NA;
            var current = info?.Current ?? string.Empty;
            var latest = info?.Latest ?? string.Empty;
            var installed = InstalledAgents.IsInstalled(def.Command);
            var cmd = def.Command;

            var card = new Border
            {
                Width = 300,
                Height = 200,
                Margin = new Thickness(0, 0, 12, 12),
                Background = _cardBg,
                BorderBrush = _border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // head: icon + name (+ update-available badge)
            var head = new DockPanel { LastChildFill = false };
            var icon = new Image
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Source = AgentIconImage.GetIcon(def.Id, true),
                VerticalAlignment = VerticalAlignment.Center
            };
            // High-quality downscaling keeps raster (PNG) icons crisp; vector (SVG) icons are
            // resolution-independent already.
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.Fant);
            DockPanel.SetDock(icon, Dock.Left);
            var name = new TextBlock
            {
                Text = def.DisplayName,
                FontWeight = FontWeights.SemiBold,
                Foreground = _fg,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(name, Dock.Left);
            head.Children.Add(icon);
            head.Children.Add(name);
            if (status == AgentUpdateChecker.Status.UPDATE_AVAILABLE)
            {
                var badge = new Border
                {
                    Background = _badgeBg,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badge.Child = new TextBlock
                {
                    Text = CnSharp.VSIX.Yolo.Resources.Manage_UpdateAvailable,
                    Foreground = _badgeFg,
                    FontSize = 11
                };
                DockPanel.SetDock(badge, Dock.Right);
                head.Children.Add(badge);
            }
            grid.Children.Add(head);
            Grid.SetRow(head, 0);

            // version rows
            var rows = new StackPanel
            {
                Margin = new Thickness(0, 12, 0, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            rows.Children.Add(VersionRow(CnSharp.VSIX.Yolo.Resources.Manage_CurrentVersion, CurrentText(installed, status, current)));
            rows.Children.Add(VersionRow(CnSharp.VSIX.Yolo.Resources.Manage_LatestVersion, LatestText(status, latest)));
            if (status == AgentUpdateChecker.Status.ERROR && !string.IsNullOrEmpty(info?.Error))
                rows.Children.Add(ErrorLine(info.Error));
            grid.Children.Add(rows);
            Grid.SetRow(rows, 1);

            // action buttons
            actionsNeedSpacing = false;
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            if (_busy.Contains(cmd))
            {
                actions.Children.Add(DisabledButton(CnSharp.VSIX.Yolo.Resources.Manage_Working));
            }
            else
            {
                if (status == AgentUpdateChecker.Status.UPDATE_AVAILABLE && CanUpgrade(def))
                    actions.Children.Add(PrimaryButton(CnSharp.VSIX.Yolo.Resources.Manage_Update, () => InstallAgent(def, true)));
                else if (!installed && CanInstall(def))
                    actions.Children.Add(Button(CnSharp.VSIX.Yolo.Resources.Manage_Install, () => InstallAgent(def, false)));
                else if (installed && CanUpgrade(def) &&
                         status != AgentUpdateChecker.Status.CHECKING &&
                         status != AgentUpdateChecker.Status.UP_TO_DATE)
                    actions.Children.Add(Button(CnSharp.VSIX.Yolo.Resources.Manage_Update, () => InstallAgent(def, true)));
            }
            if (!string.IsNullOrEmpty(def.Url))
                actions.Children.Add(Button(CnSharp.VSIX.Yolo.Resources.Manage_Website, () => AgentInstaller.OpenUrl(def.Url)));
            grid.Children.Add(actions);
            Grid.SetRow(actions, 2);

            card.Child = grid;
            return card;
        }

        private StackPanel VersionRow(string label, string value)
        {
            var p = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4)
            };
            p.Children.Add(new TextBlock
            {
                Text = label + ":",
                Foreground = _muted,
                Width = 110,
                FontSize = 12
            });
            p.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = _fg,
                FontSize = 12
            });
            return p;
        }

        private TextBlock ErrorLine(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = _warnFg,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
        }

        private string CurrentText(bool installed, AgentUpdateChecker.Status status, string current)
        {
            if (status == AgentUpdateChecker.Status.CHECKING && string.IsNullOrEmpty(current))
                return CnSharp.VSIX.Yolo.Resources.Manage_Checking;
            if (!string.IsNullOrEmpty(current)) return current;
            return installed ? "-" : CnSharp.VSIX.Yolo.Resources.Manage_NotInstalled;
        }

        private string LatestText(AgentUpdateChecker.Status status, string latest)
        {
            if (!string.IsNullOrEmpty(latest)) return latest;
            if (status == AgentUpdateChecker.Status.CHECKING) return CnSharp.VSIX.Yolo.Resources.Manage_Checking;
            return "-";
        }

        // ── buttons ───────────────────────────────────────────────

        private Button Button(string label, Action onClick)
        {
            var b = new Button
            {
                Content = label,
                Height = 26,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 0, 0),
                // Themed surface so the label stays readable in both light and dark themes (the
                // default WPF button chrome is always light, which hid light text in dark theme).
                Background = _badgeBg,
                BorderBrush = _border,
                BorderThickness = new Thickness(1),
                Foreground = _fg
            };
            if (actionsNeedSpacing) b.Margin = new Thickness(6, 0, 0, 0);
            b.Click += (_, __) => onClick();
            actionsNeedSpacing = true;
            return b;
        }

        // Tracks whether a previous action button was added, so subsequent ones get a left margin.
        private bool actionsNeedSpacing;

        private Button PrimaryButton(string label, Action onClick)
        {
            var b = Button(label, onClick);
            b.Background = new SolidColorBrush(Color.FromRgb(0x0e, 0x63, 0x9c));
            b.Foreground = Brushes.White;
            b.BorderThickness = new Thickness(0);
            return b;
        }

        private Button DisabledButton(string label)
        {
            var b = new Button
            {
                Content = label,
                Height = 26,
                Padding = new Thickness(10, 0, 10, 0),
                IsEnabled = false,
                Background = _badgeBg,
                BorderBrush = _border,
                BorderThickness = new Thickness(1),
                Foreground = _muted
            };
            return b;
        }

        // ── install / update actions ───────────────────────────────

        private void InstallAgent(AgentDef def, bool upgrade)
        {
            var spec = def.Install;
            var cmd = def.Command;
            if (spec == null || string.IsNullOrEmpty(cmd) || _busy.Contains(cmd)) return;
            // The agent's own UpdateCmd is first-class: an upgrade runs it regardless of install type.
            var selfUpdate = upgrade && !string.IsNullOrEmpty(spec.UpdateCmd);
            if (!selfUpdate)
            {
                if (spec.Type == "url")
                {
                    // Manual install/update opens the download page: prefer install.url, fall back to the homepage.
                    var target = !string.IsNullOrEmpty(spec.Url) ? spec.Url : def.Url;
                    if (!string.IsNullOrEmpty(target)) AgentInstaller.OpenUrl(target);
                    return;
                }
                if (!spec.Automatable || string.IsNullOrEmpty(spec.Target)) return;
            }

            _busy.Add(cmd);
            Rebuild();
            SetStatus(string.Format(CnSharp.VSIX.Yolo.Resources.Manage_StatusInstalling, def.DisplayName, spec.Target));
            _ = Task.Run(() =>
            {
                var command = upgrade
                    ? AgentInstaller.BuildUpdateCommand(spec)
                    : AgentInstaller.BuildInstallCommand(spec);
                var (ok, lastLine) = AgentInstaller.RunCommand(command);
                _ = Dispatcher.InvokeAsync(() =>
                {
                    _busy.Remove(cmd);
                    if (!ok)
                    {
                        SetStatus(string.Format(CnSharp.VSIX.Yolo.Resources.Manage_StatusInstallFailed, def.DisplayName, lastLine), true);
                        Rebuild();
                        return;
                    }
                    var verified = AgentDetector.CanExecute(cmd);
                    if (verified)
                    {
                        // Incrementally add just this command to the installed cache (no full PATH re-scan).
                        InstalledAgents.MarkInstalled(cmd);
                        SetStatus(string.Format(CnSharp.VSIX.Yolo.Resources.Manage_StatusInstalled, def.DisplayName));
                        if (upgrade) _ = Task.Run(() => AgentUpdateChecker.Refresh(cmd, spec));
                    }
                    else
                    {
                        SetStatus(string.Format(CnSharp.VSIX.Yolo.Resources.Manage_StatusUnverified, def.DisplayName, cmd), true);
                    }
                    Rebuild();
                });
            });
        }

        private void SetStatus(string text, bool warn = false)
        {
            StatusLine.Text = text;
            StatusLine.Foreground = warn ? _warnFg : _muted;
        }
    }
}
