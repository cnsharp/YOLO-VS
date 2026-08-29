using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

#pragma warning disable VSTHRD001 // Use SwitchToMainThreadAsync instead of Dispatcher (background detector -> UI marshal)
#pragma warning disable VSTHRD110 // Observe awaitable result (fire-and-forget marshal is intentional)
#pragma warning disable VSTHRD100 // Avoid async void (required for WPF event handlers)

namespace CnSharp.VSIX.Yolo
{
    /// <summary>
    /// WPF control hosted by <see cref="YoloOptionsPage"/> (via an ElementHost). Mirrors the
    /// IntelliJ AgentExtenderConfigurable: one merged table of agents (known/locked + custom),
    /// with skip-flag auto-fill, duplicate detection, PATH-based installed greying and Validate.
    /// </summary>
    public partial class YoloOptionsControl : UserControl
    {
        public YoloOptionsPage? Page { get; set; }

        public ObservableCollection<AgentRow> Rows { get; } = new ObservableCollection<AgentRow>();

        private bool _loading;
        private bool _autoFilling;

        public YoloOptionsControl()
        {
            InitializeComponent();
            AgentsGrid.ItemsSource = Rows;
            Rows.CollectionChanged += OnRowsChanged;
            // Keep the installed dots fresh as the cached detection set changes (no re-open needed).
            // Pair subscribe/unsubscribe with Loaded/Unloaded so a re-shown control re-subscribes
            // and a hidden one can't leak a reference through the static event (ElementHost does not
            // always raise Unloaded reliably, so the subscription must live for the visible span only).
            Loaded += (_, _) => InstalledAgents.CacheChanged += OnAgentCacheChanged;
            Unloaded += (_, _) => InstalledAgents.CacheChanged -= OnAgentCacheChanged;
        }

        private void OnAgentCacheChanged() => ApplyInstalledFromCache();

        // ── Load / save ─────────────────────────────────────────────

        public void LoadFromSettings()
        {
            _loading = true;
            try
            {
                var settings = YoloSettings.Instance;

                Rows.Clear();

                var ruleByBase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in settings.PermissionRules)
                    if (!string.IsNullOrWhiteSpace(r.AgentId))
                        ruleByBase[ExecutableNames.BaseName(r.AgentId)] = r.Flag ?? string.Empty;

                var resumeByBase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in settings.ResumeRules)
                    if (!string.IsNullOrWhiteSpace(r.AgentId))
                        resumeByBase[ExecutableNames.BaseName(r.AgentId)] = r.Flag ?? string.Empty;

                // ① Known agents (read-only ID/Command, editable skip/resume flag)
                foreach (var known in AgentRegistry.All)
                {
                    var baseCmd = ExecutableNames.BaseName(known.Command);
                    var saved = ruleByBase.TryGetValue(baseCmd, out var f) ? f : null;
                    var flag = saved ?? known.SkipFlag;
                    var savedR = resumeByBase.TryGetValue(baseCmd, out var rf) ? rf : null;
                    var resume = savedR ?? known.ResumeFlag;
                    Rows.Add(new AgentRow
                    {
                        Id = known.Id,
                        DisplayName = known.DisplayName,
                        Command = known.Command,
                        BaseArgs = string.Empty,
                        SkipFlag = flag,
                        ResumeFlag = resume,
                        IconPath = known.Icon ?? string.Empty,
                        IsLocked = true
                    });
                }

                // ② Custom tools (fully editable)
                foreach (var tool in settings.CustomTools)
                {
                    var baseCmd = ExecutableNames.BaseName(tool.Command);
                    var saved = ruleByBase.TryGetValue(baseCmd, out var f) ? f : null;
                    var savedR = resumeByBase.TryGetValue(baseCmd, out var rf) ? rf : null;
                    Rows.Add(new AgentRow
                    {
                        Id = tool.Id,
                        DisplayName = tool.DisplayName,
                        Command = tool.Command,
                        BaseArgs = tool.BaseArgs,
                        SkipFlag = saved ?? string.Empty,
                        ResumeFlag = savedR ?? string.Empty,
                        IconPath = tool.IconPath,
                        IsLocked = false
                    });
                }

                // Default agent selection removed: not part of the reference design.
                SetStatus(string.Empty, false);
                _ = RefreshInstalledAsync();
            }
            finally
            {
                _loading = false;
            }
        }

        public void SaveToSettings()
        {
            var settings = YoloSettings.Instance;

            // ① Permission (skip) rules — one per row that has both a command and a flag.
            var rules = new List<PermissionRule>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Rows)
            {
                var cmd = (row.Command ?? string.Empty).Trim();
                var flag = (row.SkipFlag ?? string.Empty).Trim();
                if (cmd.Length == 0 || flag.Length == 0) continue;
                var key = ExecutableNames.BaseName(cmd);
                if (!seen.Add(key)) continue; // de-dupe by executable base name
                rules.Add(new PermissionRule { AgentId = key, Flag = flag });
            }
            settings.PermissionRules = rules;

            // ② Resume rules — one per row that has both a command and a resume flag.
            var resumes = new List<PermissionRule>();
            var seenR = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Rows)
            {
                var cmd = (row.Command ?? string.Empty).Trim();
                var flag = (row.ResumeFlag ?? string.Empty).Trim();
                if (cmd.Length == 0 || flag.Length == 0) continue;
                var key = ExecutableNames.BaseName(cmd);
                if (!seenR.Add(key)) continue;
                resumes.Add(new PermissionRule { AgentId = key, Flag = flag });
            }
            settings.ResumeRules = resumes;

            // ③ Custom tools — skip locked (known) agents.
            settings.CustomTools = Rows
                .Where(r => !r.IsLocked)
                .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Command))
                .Select(r => new CustomTool
                {
                    Id = r.Id.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? r.Id.Trim() : r.DisplayName.Trim(),
                    Command = r.Command.Trim(),
                    BaseArgs = r.BaseArgs ?? string.Empty,
                    IconPath = r.IconPath ?? string.Empty
                })
                .ToList();

            settings.Save();
        }

        // ── Installed (PATH) detection ──────────────────────────────

        /// <summary>
        /// Renders installed state instantly from the persisted detection cache, then kicks a
        /// background rescan that refreshes the dots only when the detected set changes.
        /// </summary>
        private Task RefreshInstalledAsync()
        {
            ApplyInstalledFromCache();
            // Kick a background rescan so a freshly-installed tool shows up; UI updates arrive via CacheChanged.
            InstalledAgents.RescanCache(_ => { });
            return Task.CompletedTask;
        }

        private void ApplyInstalledFromCache()
        {
            foreach (var row in Rows)
            {
                var cmd = (row.Command ?? string.Empty).Trim();
                row.IsInstalled = cmd.Length > 0 && InstalledAgents.IsInstalled(cmd);
            }
        }

        // ── Row change handling ─────────────────────────────────────

        private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (AgentRow row in e.NewItems)
                    row.PropertyChanged += OnRowChanged;
            if (e.OldItems != null)
                foreach (AgentRow row in e.OldItems)
                    row.PropertyChanged -= OnRowChanged;
        }

        private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not AgentRow row) return;

            // Auto-fill the skip flag when a known agent id/command is typed and the flag is empty.
            if (!_autoFilling && (e.PropertyName == nameof(AgentRow.Id) || e.PropertyName == nameof(AgentRow.Command)))
            {
                if (string.IsNullOrWhiteSpace(row.SkipFlag))
                {
                    var guess = AgentRegistry.SkipFlagFor(row.Id);
                    if (string.IsNullOrWhiteSpace(guess)) guess = AgentRegistry.SkipFlagFor(row.Command);
                    if (!string.IsNullOrWhiteSpace(guess))
                    {
                        _autoFilling = true;
                        try { row.SkipFlag = guess; }
                        finally { _autoFilling = false; }
                    }
                }
                if (string.IsNullOrWhiteSpace(row.ResumeFlag))
                {
                    var rguess = AgentRegistry.ResumeFlagFor(row.Id);
                    if (string.IsNullOrWhiteSpace(rguess)) rguess = AgentRegistry.ResumeFlagFor(row.Command);
                    if (!string.IsNullOrWhiteSpace(rguess))
                    {
                        _autoFilling = true;
                        try { row.ResumeFlag = rguess; }
                        finally { _autoFilling = false; }
                    }
                }
            }

            ReportDuplicates();
            // Installed-detection updates must not mark the page dirty (it would light up Apply).
            if (!_loading && e.PropertyName != nameof(AgentRow.IsInstalled)) Page?.MarkDirty();
        }

        // ── Duplicate detection ─────────────────────────────────────

        private void ReportDuplicates()
        {
            var dupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dupCmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenCmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in Rows)
            {
                var id = (row.Id ?? string.Empty).Trim();
                var cmd = (row.Command ?? string.Empty).Trim();
                if (id.Length > 0 && !seenIds.Add(id)) dupIds.Add(id);
                if (cmd.Length > 0 && !seenCmds.Add(ExecutableNames.BaseName(cmd))) dupCmds.Add(cmd);
            }

            var problems = new List<string>();
            if (dupIds.Count > 0) problems.Add(string.Format(CnSharp.VSIX.Yolo.Resources.Settings_DupId, string.Join(", ", dupIds)));
            if (dupCmds.Count > 0) problems.Add(string.Format(CnSharp.VSIX.Yolo.Resources.Settings_DupCmd, string.Join(", ", dupCmds)));

            if (problems.Count > 0)
                SetStatus(string.Join("; ", problems), true);
            else if (!string.IsNullOrEmpty(StatusText.Text) && StatusText.Foreground == Brushes.Red)
                SetStatus(string.Empty, false);
        }

        // ── Buttons ─────────────────────────────────────────────────

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var row = new AgentRow { Id = string.Empty, DisplayName = string.Empty, Command = string.Empty, IsLocked = false };
            Rows.Add(row);
            AgentsGrid.SelectedItem = row;
            SetStatus(CnSharp.VSIX.Yolo.Resources.Settings_AddedCustomTool, false);
            if (!_loading) Page?.MarkDirty();
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (AgentsGrid.SelectedItem is not AgentRow row)
            {
                SetStatus(CnSharp.VSIX.Yolo.Resources.Settings_SelectRowToRemove, true);
                return;
            }
            if (row.IsLocked)
            {
                SetStatus(string.Format(CnSharp.VSIX.Yolo.Resources.Settings_CannotRemoveKnownAgent, row.Id), true);
                return;
            }
            Rows.Remove(row);
            SetStatus(CnSharp.VSIX.Yolo.Resources.Settings_RemovedCustomTool, false);
            if (!_loading) Page?.MarkDirty();
        }

        private async void ValidateButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = AgentsGrid.SelectedItems.Count > 0
                ? AgentsGrid.SelectedItems.Cast<AgentRow>().ToList()
                : Rows.ToList();
            if (rows.Count == 0)
            {
                SetStatus(CnSharp.VSIX.Yolo.Resources.Settings_NoToolToValidate, true);
                return;
            }
            SetStatus(CnSharp.VSIX.Yolo.Resources.Settings_Validating, false);
            await Task.Run(() =>
            {
                foreach (var row in rows)
                {
                    var cmd = (row.Command ?? string.Empty).Trim();
                    bool ok = cmd.Length > 0 && AgentDetector.CanExecute(cmd);
                    bool iconOk = string.IsNullOrWhiteSpace(row.IconPath) || IconPathValid(row.IconPath);
                    Dispatcher.InvokeAsync(() =>
                    {
                        row.IsInstalled = ok;
                        if (!ok)
                            SetStatus(string.Format(CnSharp.VSIX.Yolo.Resources.Settings_CommandNotFoundOnPath, cmd), true);
                        else if (!iconOk)
                            SetStatus(string.Format(CnSharp.VSIX.Yolo.Resources.Settings_IconPathInvalid, row.IconPath), true);
                        else
                            SetStatus(CnSharp.VSIX.Yolo.Resources.Settings_ValidationPassed, false);
                    });
                }
            });
        }

        private static bool IconPathValid(string path)
        {
            if (path.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Uri.TryCreate(path, UriKind.Absolute, out _);
            return File.Exists(path);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            if (AgentsGrid.SelectedItem is not AgentRow row || row.IsLocked)
            {
                SetStatus(CnSharp.VSIX.Yolo.Resources.Settings_SelectCustomToolForIcon, true);
                return;
            }
            var dlg = new OpenFileDialog
            {
                Filter = CnSharp.VSIX.Yolo.Resources.Settings_ImageFilter,
                Title = CnSharp.VSIX.Yolo.Resources.Settings_ChooseIconTitle
            };
            if (dlg.ShowDialog() != true) return;

            // Copy the chosen image into the user's AppData icon folder so the icon is stored
            // locally (persisted, machine-stable) and the original file location no longer matters.
            var stored = CopyIconToUserDir(dlg.FileName, row.Id);
            if (stored == null)
            {
                SetStatus(string.Format(CnSharp.VSIX.Yolo.Resources.Settings_IconCopyFailed, dlg.FileName), true);
                return;
            }

            row.IconPath = stored;
            IconPathBox.Text = stored;
            IconPreview.Source = AgentIconImage.FromPath(stored);
            if (!_loading) Page?.MarkDirty();
        }

        /// <summary>
        /// Copies <paramref name="sourceFile"/> into <c>%LocalAppData%/YoloVS/Icons</c> and returns
        /// the new path. The file name is derived from the tool id (sanitized) so re-picking an
        /// icon for the same tool replaces the old one; falls back to a GUID when the id is empty.
        /// </summary>
        private static string? CopyIconToUserDir(string sourceFile, string toolId)
        {
            try
            {
                var iconsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "YoloVS", "Icons");
                Directory.CreateDirectory(iconsDir);

                var ext = Path.GetExtension(sourceFile);
                if (string.IsNullOrEmpty(ext)) ext = ".png";
                var baseName = string.IsNullOrWhiteSpace(toolId)
                    ? Guid.NewGuid().ToString("N")
                    : new string(toolId.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray());
                if (string.IsNullOrEmpty(baseName)) baseName = Guid.NewGuid().ToString("N");

                var dest = Path.Combine(iconsDir, baseName + ext);
                File.Copy(sourceFile, dest, overwrite: true);
                return dest;
            }
            catch
            {
                return null;
            }
        }

        // ── Grid behavior ───────────────────────────────────────────

        private void AgentsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (e.Column == null || e.Row.Item is not AgentRow row || !row.IsLocked) return;
            // Locked (known) agents: ID and Command columns are read-only.
            if (e.Column.DisplayIndex == 1 || e.Column.DisplayIndex == 3)
                e.Cancel = true;
        }

        private void AgentsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AgentsGrid.SelectedItem is AgentRow row)
            {
                IconPathBox.Text = row.IconPath ?? string.Empty;
                IconPreview.Source = AgentIconImage.FromPath(row.IconPath);
            }
            else
            {
                IconPathBox.Text = string.Empty;
                IconPreview.Source = null;
            }
        }

        private void SetStatus(string text, bool warn)
        {
            StatusText.Text = text;
            StatusText.Foreground = warn ? Brushes.Red : Brushes.Green;
        }
    }

    /// <summary>One editable agent row in the options grid.</summary>
    public sealed class AgentRow : INotifyPropertyChanged
    {
        private string _id = string.Empty;
        private string _displayName = string.Empty;
        private string _command = string.Empty;
        private string _baseArgs = string.Empty;
        private string _skipFlag = string.Empty;
        private string _resumeFlag = string.Empty;
        private string _iconPath = string.Empty;
        private bool _isLocked;
        private bool _isInstalled;

        public string Id { get => _id; set => Set(ref _id, value); }
        public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
        public string Command { get => _command; set => Set(ref _command, value); }
        public string BaseArgs { get => _baseArgs; set => Set(ref _baseArgs, value); }
        public string SkipFlag { get => _skipFlag; set => Set(ref _skipFlag, value); }
        public string ResumeFlag { get => _resumeFlag; set => Set(ref _resumeFlag, value); }
        public string IconPath { get => _iconPath; set => Set(ref _iconPath, value); }
        public bool IsLocked { get => _isLocked; set => Set(ref _isLocked, value); }
        public bool IsInstalled { get => _isInstalled; set => Set(ref _isInstalled, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Green dot for installed agents, grey for not found on PATH.</summary>
    public sealed class BoolToInstalledBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b)
                ? new SolidColorBrush(Colors.LimeGreen)
                : new SolidColorBrush(Colors.Gray);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
