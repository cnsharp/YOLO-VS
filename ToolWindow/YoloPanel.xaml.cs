using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.PlatformUI;

// Intentional fire-and-forget marshaling: these BeginInvoke calls marshal work back onto the
// WPF Dispatcher and the awaitable result is deliberately not observed (matches the terminal files).
#pragma warning disable VSTHRD001 // Use SwitchToMainThreadAsync instead of Dispatcher
#pragma warning disable VSTHRD110 // Observe awaitable result

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// Interaction logic for YoloPanel.xaml
    /// </summary>
    public partial class YoloPanel : UserControl
    {
        /// <summary>
        /// One terminal session = one tab. A session is either a plain shell or an
        /// agent run (in which case <see cref="AgentName"/> is set). Mirrors IDEA's
        /// behaviour of opening a fresh terminal per agent launch.
        /// </summary>
        private sealed class Session
        {
            public TabItem Tab = null!;
            public WpfTerminalView View = null!;
            public ConPtyTerminal Terminal = null!;
            public string? AgentName;   // null => plain shell
            public string DisplayName = "Terminal";
        }

        private InstalledAgents _installedAgents = null!;
        private readonly List<Session> _sessions = new List<Session>();
        private DTE? _dte;

        /// <summary>
        /// Set while the Y/R toggles are being restored from settings at startup, so restoring a
        /// previously-enabled Y does not pop the caution dialog (the dialog only shows on a real user action).
        /// </summary>
        private bool _restoringToggles;

        /// <summary>Current solution directory, cached (off the UI thread the link filter must not call DTE).</summary>
        private string? _solutionDir;

        /// <summary>
        /// Single shared link filter for every terminal tab. Paints spans AND hit-tests clicks, so a painted
        /// link can never disagree with a click. The type/member gate reads the solution file-name snapshot,
        /// which must be queried off the UI thread — hence the lazy <c>() => YoloProjectTypes.For(_solutionDir)</c>.
        /// Assigned in the constructor because a field initializer cannot capture another instance field.
        /// </summary>
        private readonly YoloLinkFilters _linkFilters;

        private string _selectedAgent = string.Empty;
        /// <summary>Re-entrancy guard while programmatically syncing the tab switcher / tab strip.</summary>
        private bool _tabSwitchSyncing;

        /// <summary>Combo-box row: agent id + display name + rendered icon.</summary>
        private sealed class AgentComboItem
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public System.Windows.Media.ImageSource? Icon { get; set; }
        }

        /// <summary>Right-hand tab-switcher row: session index + display name + icon.</summary>
        private sealed class TabSwitchItem
        {
            public int Index { get; set; }
            public string Name { get; set; } = string.Empty;
            public System.Windows.Media.ImageSource? Icon { get; set; }
        }

        // Icon colours mirror IDEA's toggle states exactly.
        private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x6C, 0x70, 0x7E));
        private static readonly Brush Red  = new SolidColorBrush(Color.FromRgb(0xDB, 0x3B, 0x4B));
        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));

        /// <summary>
        /// Re-reads the active VS theme colours and pushes them into the named brushes declared
        /// in YoloPanel.xaml. Because the XAML binds to those brushes via DynamicResource, every
        /// chrome element (panel, toolbar, dropdown, tab headers, status bar) recolours at once.
        /// Reads from <see cref="EnvironmentColors"/>, which is what VS itself uses for its chrome.
        /// </summary>
        private void ApplyVsTheme()
        {
            try
            {
                var bg     = ToMedia(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey));
                var fg     = ToMedia(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey));
                var border = ToMedia(VSColorTheme.GetThemedColor(EnvironmentColors.PanelBorderColorKey));
                var combo  = ToMedia(VSColorTheme.GetThemedColor(EnvironmentColors.ComboBoxBackgroundColorKey));

                // Colours for the OPEN drop-down list. These differ from the closed control:
                // ComboBoxBackgroundColorKey is the collapsed box, ComboBoxListBackgroundColorKey
                // is the popup list that drops down below it.
                var listBg      = ToMedia(VSColorTheme.GetThemedColor(CommonControlsColors.ComboBoxListBackgroundColorKey));
                var listFg      = ToMedia(VSColorTheme.GetThemedColor(CommonControlsColors.ComboBoxListItemTextColorKey));
                var listHover   = ToMedia(VSColorTheme.GetThemedColor(CommonControlsColors.ComboBoxListItemBackgroundHoverColorKey));
                var listHoverFg = ToMedia(VSColorTheme.GetThemedColor(CommonControlsColors.ComboBoxListItemTextHoverColorKey));

                // The terminal itself is a real console (its own ANSI colours) and is deliberately
                // left untouched — only the WPF chrome around it follows the theme.
                SetBrush("PanelBg", bg);
                SetBrush("ToolbarBg", bg);
                SetBrush("TabBg", bg);
                SetBrush("PanelFg", fg);
                SetBrush("ComboFg", fg);
                SetBrush("PanelBorder", border);
                SetBrush("ComboBg", combo);

                // Terminal-tab chrome. Derive a dark/light palette from the panel background
                // luminance so the active tab is never white-on-white (or black-on-black).
                bool isDark = (bg.R + bg.G + bg.B) / 3 < 128;
                SetBrush("TabActiveBg", isDark ? Color.FromRgb(0x1e, 0x1e, 0x1e) : Colors.White);
                SetBrush("TabInactiveBg", isDark ? Color.FromRgb(0x2d, 0x2d, 0x2d) : Color.FromRgb(0xe6, 0xe6, 0xe6));
                SetBrush("TabFgActive", fg);
                SetBrush("TabFgInactive", isDark ? Color.FromRgb(0x9b, 0x9b, 0x9b) : Color.FromRgb(0x59, 0x59, 0x59));

                // Re-point the SystemColors keys that WPF's ComboBox template uses for the
                // drop-down popup. They are replaced (not mutated) so DynamicResource in the
                // template picks up the change immediately when the theme switches.
                AgentComboBox.Resources[SystemColors.WindowBrushKey] = new SolidColorBrush(listBg);
                AgentComboBox.Resources[SystemColors.WindowTextBrushKey] = new SolidColorBrush(listFg);
                AgentComboBox.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(listHover);
                AgentComboBox.Resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush(listHoverFg);
            }
            catch (Exception ex)
            {
                Log.Write($"YoloPanel.ApplyVsTheme failed: {ex}");
            }
        }

        private void SetBrush(string key, System.Windows.Media.Color color)
        {
            if (Resources[key] is SolidColorBrush brush)
                brush.Color = color;
        }

        private static System.Windows.Media.Color ToMedia(System.Drawing.Color c) =>
            System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B);

        private void OnVsThemeChanged(ThemeChangedEventArgs e) =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyVsTheme();
                RefreshAgentIcons();
            }));

        /// <summary>
        /// Re-renders the agent icons after a theme switch. A bundled icon can ship a dark twin
        /// (<c>codex.svg</c> / <c>codex_dark.svg</c>, see <see cref="AgentIconImage"/>), and both the
        /// dropdown rows and the tab switcher hold an already-rendered ImageSource, so they must be
        /// rebuilt — mutating one in place would not notify the binding.
        /// </summary>
        private void RefreshAgentIcons()
        {
            var selectedId = (AgentComboBox.SelectedItem as AgentComboItem)?.Id;
            for (int i = 0; i < AgentComboBox.Items.Count; i++)
            {
                if (!(AgentComboBox.Items[i] is AgentComboItem item)) continue;
                AgentComboBox.Items[i] = new AgentComboItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    Icon = AgentIconImage.GetIcon(item.Id, darkVariant: true)
                };
            }
            if (selectedId != null)
                foreach (AgentComboItem item in AgentComboBox.Items)
                    if (item.Id == selectedId) { AgentComboBox.SelectedItem = item; break; }

            RefreshTabSwitcher();
        }

        public YoloPanel()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            InitializeComponent();
            _linkFilters = new YoloLinkFilters(() => YoloProjectTypes.For(_solutionDir));
            // Paint the chrome with the active VS theme before any agent/terminal work,
            // and keep it in sync when the user switches themes (dark/light/blue) at runtime.
            ApplyVsTheme();
            VSColorTheme.ThemeChanged += OnVsThemeChanged;
            InitializeComponents();
            // When the tool window is actually shown the panel is loaded into the frame; that
            // is the moment to cd the terminal(s) into the solution directory and refresh agents.
            Loaded += (_, _) => OnVisible();
        }

        private void InitializeComponents()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Log.Reset();
            _installedAgents = new InstalledAgents();

            // Restore the global Y / R toggles from settings (IDEA persists these too).
            _restoringToggles = true;
            try
            {
                YToggle.IsChecked = YoloSettings.Instance.SkipEnabled;
                RToggle.IsChecked = YoloSettings.Instance.ResumeEnabled;
            }
            finally
            {
                _restoringToggles = false;
            }
            ApplyToggleVisuals();

            // Restore the last-used agent so the dropdown opens on it (falls back to the first
            // installed agent in LoadAgentsAsync when it is no longer available).
            _selectedAgent = YoloSettings.Instance.LastAgent ?? string.Empty;

            // No terminal is opened on startup — the panel starts empty and a tab is created
            // only when the user launches an agent (Launch / the dropdown).

            // Load agents asynchronously so the detection scans never block the UI thread.
            _ = LoadAgentsAsync();

            // Keep the dropdown in sync with the cached installed set — when a background rescan
            // detects a change (e.g. a newly installed agent), repopulate without re-opening.
            InstalledAgents.CacheChanged += OnAgentCacheChanged;

            // Re-root the terminal(s) into the solution directory when a solution is opened
            // after the window already exists (VS loads solutions asynchronously).
            SubscribeSolutionEvents();

            // Keep keyboard focus in the selected tab's terminal (a freshly created tab may not
            // receive focus on its own, and switching tabs drops focus too — without this, typing
            // into the terminal does nothing).
            SessionTabs.SelectionChanged += OnSessionTabSelectionChanged;
        }

        private void SubscribeSolutionEvents()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE)) is DTE dte)
                {
                    _dte = dte;
                    dte.Events.SolutionEvents.Opened += OnSolutionOpened;
                }
            }
            catch
            {
                // Best-effort; never fault the panel over event wiring.
            }
        }

        private void OnSolutionOpened()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ApplySolutionDirectory();
        }

        /// <summary>Give keyboard focus to the terminal of whichever tab is now selected, so the
        /// user can type immediately. Deferred to Input priority so the tab content is laid out first.</summary>
        private void OnSessionTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SessionTabs.SelectedItem is not TabItem tab) return;
            var session = _sessions.Find(s => s.Tab == tab);
            if (session == null) return;

            // Keep the tab switcher's selected entry in step with the strip (without re-selecting
            // the tab, which would loop). Detaching the handler avoids the re-entrant callback.
            if (!_tabSwitchSyncing && TabSwitchButton.Visibility == Visibility.Visible && SessionTabs.SelectedIndex >= 0)
            {
                TabSwitchList.SelectionChanged -= OnTabSwitchSelected;
                TabSwitchList.SelectedIndex = SessionTabs.SelectedIndex;
                TabSwitchList.SelectionChanged += OnTabSwitchSelected;
            }

            Dispatcher.BeginInvoke(new Action(() => session.View?.FocusTerminal()), DispatcherPriority.Input);
        }

        /// <summary>
        /// Rebuilds the right-hand tab-switcher list from the current sessions. The chevron button
        /// is only shown once there is more than one tab open (so a single terminal needs no
        /// switcher); selecting an entry in the popup flips the active tab.
        /// </summary>
        private void RefreshTabSwitcher()
        {
            TabSwitchList.SelectionChanged -= OnTabSwitchSelected;
            TabSwitchList.Items.Clear();
            for (int i = 0; i < _sessions.Count; i++)
            {
                var agentName = _sessions[i].AgentName;
                TabSwitchList.Items.Add(new TabSwitchItem
                {
                    Index = i,
                    Name = _sessions[i].DisplayName,
                    Icon = agentName != null
                        ? AgentIconImage.GetIcon(agentName, darkVariant: true)
                        : AgentIconImage.GetTerminalFallback()
                });
            }
            TabSwitchButton.Visibility = _sessions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (_sessions.Count <= 1)
                TabSwitchButton.IsChecked = false; // collapse the popup if we just dropped below two tabs
            if (SessionTabs.SelectedIndex >= 0)
                TabSwitchList.SelectedIndex = SessionTabs.SelectedIndex;
            TabSwitchList.SelectionChanged += OnTabSwitchSelected;
        }

        /// <summary>When the chevron opens (IsChecked -> true binds Popup.IsOpen), highlight the
        /// currently active tab in the list and focus it for keyboard navigation.</summary>
        private void OnTabSwitchOpened(object sender, RoutedEventArgs e)
        {
            if (SessionTabs.SelectedIndex >= 0)
                TabSwitchList.SelectedIndex = SessionTabs.SelectedIndex;
            TabSwitchList.Focus();
        }

        /// <summary>Keep the chevron in sync when the popup is dismissed by an outside click
        /// (StaysOpen=False closes it, but IsChecked would otherwise stay true).</summary>
        private void OnTabSwitchPopupClosed(object sender, EventArgs e)
        {
            TabSwitchButton.IsChecked = false;
        }

        private void OnTabSwitchSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_tabSwitchSyncing) return;
            if (TabSwitchList.SelectedItem is TabSwitchItem item && item.Index >= 0 && item.Index < _sessions.Count)
            {
                _tabSwitchSyncing = true;
                SessionTabs.SelectedIndex = item.Index;
                _tabSwitchSyncing = false;
            }
            // Dismiss the popup after a choice (IsChecked=false -> Popup.IsOpen=false via binding).
            TabSwitchButton.IsChecked = false;
        }

        private void OnAgentCacheChanged()
        {
            // CacheChanged is raised on the UI thread; refresh the dropdown from the cache.
            _ = LoadAgentsAsync();
        }

        /// <summary>
        /// Creates a new tab hosting its own <see cref="WpfTerminalView"/> +
        /// <see cref="ConPtyTerminal"/>. For agent sessions the agent is launched into that
        /// terminal (a fresh shell, then the agent command is buffered until the pty starts).
        /// </summary>
        private Session CreateSession(string? agentName, AgentConfig? cfg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = new WpfTerminalView();
            var terminal = new ConPtyTerminal(view);
            string? solutionDir = GetSolutionDirectory();
            terminal.WorkingDirectory = solutionDir;

            // Hyperlink support: paint + hit-test spans via the shared filter; resolve relative paths in
            // clicked links against the agent's CWD (the solution directory at launch time).
            view.LinkFilter = _linkFilters;
            view.BaseDirectory = solutionDir;
            if (!string.IsNullOrEmpty(solutionDir) &&
                !string.Equals(solutionDir, _solutionDir, StringComparison.OrdinalIgnoreCase))
            {
                _solutionDir = solutionDir;
                YoloProjectTypes.Invalidate();
            }

            var session = new Session
            {
                View = view,
                Terminal = terminal,
                AgentName = agentName,
                DisplayName = cfg?.DisplayName ?? "Terminal"
            };

            var tab = new TabItem
            {
                // Header shows the agent icon + the name + a × close button.
                Header = BuildTabHeader(session.DisplayName, agentName, () => CloseSession(session))
            };
            tab.Content = view;
            SessionTabs.Items.Add(tab);
            SessionTabs.SelectedItem = tab;
            session.Tab = tab;

            _sessions.Add(session);
            RefreshTabSwitcher();

            // Launch the shell on a background thread; the pty starts once the view reports
            // a real cell grid (deferred internally). For agent sessions the agent command is
            // buffered by ConPtyTerminal until the pty is live, then flushed.
            _ = System.Threading.Tasks.Task.Run(() => terminal.Launch());

            if (cfg != null)
                LaunchAgentInSession(session, cfg);

            return session;
        }

        /// <summary>
        /// Builds the tab header: agent icon + label + a small × button that closes the tab.
        /// </summary>
        private StackPanel BuildTabHeader(string title, string? agentName, Action onClose)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
            // Same source as the tab switcher: the agent's icon, or the terminal glyph for a
            // plain shell session with no agent.
            var icon = agentName != null
                ? AgentIconImage.GetIcon(agentName, darkVariant: true)
                : AgentIconImage.GetTerminalFallback();
            if (icon != null)
            {
                panel.Children.Add(new Image
                {
                    Source = icon,
                    Width = 14,
                    Height = 14,
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    SnapsToDevicePixels = true
                });
            }
            // NB: do NOT set Foreground on the title — the title inherits the TabItem's Foreground,
            // which the TabItem template sets per state (TabFgActive / TabFgInactive) so the title
            // stays readable in both dark and light themes.
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
            var close = new Button
            {
                Content = "×",
                Width = 16,
                Height = 16,
                Margin = new Thickness(6, 0, 0, 0),
                // Shift the glyph up 3px without shrinking its content box: a negative top +
                // equal positive bottom padding moves the centred content area up by exactly
                // that amount (bottom-only padding lifts by only half, because it also shrinks
                // the box). × sits on the font baseline, so a "centred" 16px box renders it
                // visually low against the hover square.
                Padding = new Thickness(0, -3, 0, 3),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            // The × must NOT inherit the TabItem Foreground: an inactive tab is set to
            // TabFgInactive (#9b9b9b in the dark theme), which made the close button nearly
            // invisible on the #2d2d2d tab strip. Pin it to the themed active text colour —
            // TabFgActive is driven by ApplyVsTheme, so it follows light themes too.
            close.SetResourceReference(Control.ForegroundProperty, "TabFgActive");
            close.Click += (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (ConfirmCloseTab())
                    onClose();
            };
            panel.Children.Add(close);
            return panel;
        }

        /// <summary>
        /// Tears down a single session (disposes its pty + reader thread) and removes its tab.
        /// </summary>
        private void CloseSession(Session session)
        {
            try { session.Terminal?.Dispose(); } catch { /* ignore */ }
            SessionTabs.Items.Remove(session.Tab);
            _sessions.Remove(session);
            RefreshTabSwitcher();
        }

        /// <summary>
        /// Mirror IDEA's confirmCloseTab(): ask before tearing down a terminal, since closing it
        /// kills the running PTY process. Returns true only when the user picks Yes.
        /// </summary>
        private bool ConfirmCloseTab()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                int result = VsShellUtilities.ShowMessageBox(
                    Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider,
                    CnSharp.VSIX.Yolo.Resources.Panel_CloseTabConfirm,
                    Constants.ProductName,
                    OLEMSGICON.OLEMSGICON_QUERY,
                    OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
                return result == 6; // IDYES
            }
            catch (Exception ex)
            {
                // If the shell dialog can't be shown, fail safe to closing (the exception is
                // logged rather than crashing the close handler).
                Log.Write("ConfirmCloseTab failed: " + ex.Message);
                return true;
            }
        }

        /// <summary>
        /// Scans for installed agents on a background thread, then populates the combo box
        /// back on the UI thread. Keeps tool-window construction responsive.
        /// </summary>
        private async System.Threading.Tasks.Task LoadAgentsAsync()
        {
            // Only show agents that are actually installed. "Installed" is detected by
            // PATH lookup OR by successfully launching the command (see AgentDetector),
            // so agents that resolve through the shell/app-launcher also appear.
            var agents = await System.Threading.Tasks.Task.Run(
                () => _installedAgents.GetInstalledAgents());

            // Marshal back to the UI thread to update the combo box
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            AgentComboBox.Items.Clear();

            // Add each installed agent (with its icon, reused from IDEA's asset files)
            foreach (var agent in agents)
            {
                AgentComboBox.Items.Add(new AgentComboItem
                {
                    Id = agent.Name,
                    Name = agent.DisplayName,
                    Icon = AgentIconImage.GetIcon(agent.Name, darkVariant: true)
                });
            }

            // Select the previously chosen agent (preserved across async refreshes); otherwise
            // fall back to the first installed agent so the dropdown always shows a runnable choice.
            if (!string.IsNullOrEmpty(_selectedAgent))
            {
                foreach (AgentComboItem item in AgentComboBox.Items)
                    if (item.Id == _selectedAgent) { AgentComboBox.SelectedItem = item; return; }
            }
            if (AgentComboBox.Items.Count > 0)
                AgentComboBox.SelectedIndex = 0;
        }

        private void OnAgentSelected(object sender, SelectionChangedEventArgs e)
        {
            if (AgentComboBox.SelectedItem is AgentComboItem selectedItem)
            {
                _selectedAgent = selectedItem.Id;

                if (!string.IsNullOrEmpty(selectedItem.Id))
                {
                    // Surface selection feedback in the status bar, NOT in the terminal.
                    var cfg = _installedAgents.GetAgent(selectedItem.Id);
                    string resolved = cfg != null ? $"{cfg.Command} {cfg.BaseArgs}".Trim() : selectedItem.Id;
                    SetStatus(string.Format(CnSharp.VSIX.Yolo.Resources.Panel_AgentSelected, selectedItem.Id, resolved));
                }
            }
        }

        // ── Y (skip) / R (resume) global toggles ──────────────────────────────────────

        private void OnYToggleChecked(object sender, RoutedEventArgs e)
        {
            // Caution the user the first time they enable Y (skip permissions). If they tick
            // "don't show again", persist the suppression so the warning is skipped thereafter.
            if (!_restoringToggles && !YoloSettings.Instance.SuppressSkipWarning)
            {
                var dlg = new SkipWarningDialog
                {
                    Owner = System.Windows.Application.Current?.MainWindow
                };
                bool proceed = dlg.ShowDialog() == true;
                if (dlg.Suppress)
                {
                    YoloSettings.Instance.SuppressSkipWarning = true;
                    YoloSettings.Instance.Save();
                }
                if (!proceed)
                {
                    // Revert the toggle without re-prompting; the Unchecked handler persists false.
                    YToggle.IsChecked = false;
                    return;
                }
            }

            YoloSettings.Instance.SkipEnabled = true;
            YoloSettings.Instance.Save();
            ApplyToggleVisuals();
            SetStatus(CnSharp.VSIX.Yolo.Resources.Panel_SkipOn);
        }

        private void OnYToggleUnchecked(object sender, RoutedEventArgs e)
        {
            YoloSettings.Instance.SkipEnabled = false;
            YoloSettings.Instance.Save();
            ApplyToggleVisuals();
            SetStatus(CnSharp.VSIX.Yolo.Resources.Panel_SkipOff);
        }

        private void OnRToggleChecked(object sender, RoutedEventArgs e)
        {
            YoloSettings.Instance.ResumeEnabled = true;
            YoloSettings.Instance.Save();
            ApplyToggleVisuals();
            SetStatus(CnSharp.VSIX.Yolo.Resources.Panel_ResumeOn);
        }

        private void OnRToggleUnchecked(object sender, RoutedEventArgs e)
        {
            YoloSettings.Instance.ResumeEnabled = false;
            YoloSettings.Instance.Save();
            ApplyToggleVisuals();
            SetStatus(CnSharp.VSIX.Yolo.Resources.Panel_ResumeOff);
        }

        /// <summary>Colors the Y/R icons to match their on/off state (gray / red / green).</summary>
        private void ApplyToggleVisuals()
        {
            YIcon.Stroke = YToggle.IsChecked == true ? Red : Gray;
            RIcon.Stroke = RToggle.IsChecked == true ? Green : Gray;
        }

        /// <summary>
        /// Launches the selected agent as a REAL process inside a new terminal tab. The agent's
        /// configured command is resolved from <see cref="InstalledAgents"/>; when Y is on its
        /// skip-permission flag (and any skip env var) is injected, and when R is on its resume
        /// flag is appended — mirroring IDEA's TerminalSkipFlagCustomizer / ResumeAction.
        /// </summary>
        private void OnRunAgentClicked(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(_selectedAgent))
            {
                SetStatus(CnSharp.VSIX.Yolo.Resources.Panel_SelectAgentFirst, true);
                return;
            }

            var cfg = _installedAgents.GetAgent(_selectedAgent);
            if (cfg == null)
            {
                SetStatus(string.Format(CnSharp.VSIX.Yolo.Resources.Panel_AgentConfigNotFound, _selectedAgent), true);
                return;
            }

            if (!cfg.IsInstalled())
            {
                SetStatus(string.Format(CnSharp.VSIX.Yolo.Resources.Panel_AgentNotInstalled, cfg.DisplayName, cfg.Command), true);
                return;
            }

            // Remember the launched agent so the dropdown re-opens on it next session.
            YoloSettings.Instance.LastAgent = cfg.Name;
            YoloSettings.Instance.Save();

            // This creates a brand-new tab + terminal for the run (IDEA opens a new terminal).
            var session = CreateSession(cfg.Name, cfg);

            SetStatus(string.Format(CnSharp.VSIX.Yolo.Resources.Panel_Running, cfg.Command, cfg.DisplayName));
            // FocusTerminal happens after launch inside CreateSession's tab selection; ensure focus.
            session.View.FocusTerminal();
        }

        /// <summary>
        /// Buffers the agent launch (command + optional env) into the session's terminal. The
        /// ConPtyTerminal defers writing until the pty actually starts, so nothing is dropped.
        /// </summary>
        private void LaunchAgentInSession(Session session, AgentConfig cfg)
        {
            var settings = YoloSettings.Instance;

            // Y (skip) => auto-approve. Two bypass mechanisms (mirrors the IntelliJ plugin):
            //   1. CLI flag — appended to the launch command (most agents).
            //   2. Env var — set in the shell before launch for agents that don't take a flag
            //      (e.g. goose: GOOSE_MODE=auto). Cannot be a command-line argument.
            var parts = new List<string> { cfg.Command };
            if (!string.IsNullOrWhiteSpace(cfg.BaseArgs))
                parts.Add(cfg.BaseArgs.Trim());

            if (settings.SkipEnabled && !string.IsNullOrWhiteSpace(cfg.SkipFlag))
                parts.Add(cfg.SkipFlag!.Trim());
            if (settings.ResumeEnabled && !string.IsNullOrWhiteSpace(cfg.ResumeFlag))
                parts.Add(cfg.ResumeFlag!.Trim());

            if (settings.SkipEnabled)
            {
                var env = DefaultSkipEnvs.Get(cfg.Name)
                          ?? DefaultSkipEnvs.Get(ExecutableNames.BaseName(cfg.Command));
                if (env != null)
                {
                    foreach (var kv in env)
                        session.Terminal.SetEnvVar(kv.Key, kv.Value);
                }
            }

            session.Terminal.RunCommand(string.Join(" ", parts));
        }

        /// <summary>Opens the YOLO settings dialog directly (agent flags, custom tools, icons).</summary>
        private void OnSettingsClicked(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dlg = new YoloSettingsDialog();
                if (dlg.ShowDialog() == true)
                {
                    // Settings were saved: refresh the agent dropdown and detection cache.
                    _installedAgents.RescanAgents();
                    _ = LoadAgentsAsync();
                    SetStatus(CnSharp.VSIX.Yolo.Resources.Panel_SettingsSaved, false);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"YoloSettingsDialog failed: {ex}");
                SetStatus(CnSharp.VSIX.Yolo.Resources.Panel_SettingsOpenFailed, true);
            }
        }

        /// <summary>
        /// The tool window no longer shows a status bar. Errors are still mirrored to the
        /// activity log so feedback is not lost entirely. (Per-user request: no status bar.)
        /// </summary>
        private void SetStatus(string message, bool isError = false)
        {
            if (isError)
                Log.Write($"YoloPanel: {message}");
        }

        // Called when the tool window becomes visible
        public void OnVisible()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Re-read the unified agent registry (AgentRegistry + settings) so edits made in
            // the options page are reflected here too.
            _installedAgents.RescanAgents();
            _ = LoadAgentsAsync();
            ApplySolutionDirectory();

            // The first tab (created when the user launches an agent) may have been wired up
            // before this window was visible, so its input box never received focus. Defer to
            // Input priority so focus lands once the window is live.
            var first = _sessions.Count > 0 ? _sessions[0] : null;
            if (first != null)
                Dispatcher.BeginInvoke(new Action(() => first.View?.FocusTerminal()), DispatcherPriority.Input);
        }

        /// <summary>
        /// Roots every plain-shell tab in the current solution directory. Agent tabs already
        /// launch in <see cref="ConPtyTerminal.WorkingDirectory"/> at creation, so we leave their
        /// running TUI alone (re-issuing cd there could clobber a buffered agent command).
        /// This is what stops the terminal from inheriting VS's CWD (the IDE install folder).
        /// </summary>
        private void ApplySolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dir = GetSolutionDirectory();
            if (!string.IsNullOrEmpty(dir) &&
                !string.Equals(dir, _solutionDir, StringComparison.OrdinalIgnoreCase))
            {
                // Solution root changed: drop the stale type/index snapshot so the link gate rebuilds for
                // the new solution. For() rebuilds on a background thread and never blocks the render path.
                _solutionDir = dir;
                YoloProjectTypes.Invalidate();
            }
            if (string.IsNullOrEmpty(dir)) return;
            foreach (var s in _sessions)
            {
                s.Terminal.WorkingDirectory = dir;
                if (s.AgentName == null)
                    s.Terminal.RunCommand($"cd \"{dir}\"");
            }
        }

        /// <summary>
        /// Tears down all terminals. Called when the tool window is destroyed — without this
        /// the shell processes and ConPTY reader threads would outlive the window, leaking a
        /// pwsh.exe per open tab each time the pane is closed and reopened.
        /// </summary>
        public void Shutdown()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            InstalledAgents.CacheChanged -= OnAgentCacheChanged;
            VSColorTheme.ThemeChanged -= OnVsThemeChanged;
            if (_dte != null)
            {
                try { _dte.Events.SolutionEvents.Opened -= OnSolutionOpened; } catch { /* ignore */ }
                _dte = null;
            }
            foreach (var s in _sessions)
            {
                try { s.Terminal?.Dispose(); }
                catch (Exception ex) { Log.Write("YoloPanel.Shutdown failed: " + ex.Message); }
            }
            _sessions.Clear();
        }

        /// <summary>
        /// Resolves the current solution's directory via DTE, so the terminal and any agent
        /// launched from it start in the user's project folder rather than the VS install dir.
        /// Returns null when no solution is open.
        /// </summary>
        private static string? GetSolutionDirectory()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE)) as DTE;
                var solutionFile = dte?.Solution?.FullName as string;
                if (string.IsNullOrEmpty(solutionFile))
                    return null;
                return Path.GetDirectoryName(solutionFile);
            }
            catch
            {
                return null;
            }
        }
    }
}
