using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Renci.SshNet;

namespace OpenClawMonitor
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Run(new MainWindow());
        }
    }

    public sealed class MainWindow : Window
    {
        private const int MinRefreshMs = 100;
        private const int MaxRefreshMs = 2000;
        private const int RefreshStepMs = 100;
        private readonly SettingsStore _settingsStore;
        private readonly LocalMonitorService _localService;
        private readonly UbuntuMonitorService _ubuntuService;
        private readonly LmStudioService _lmStudioService;
        private readonly DispatcherTimer _timer;

        private MonitorSettings _settings;
        private bool _isWindowActive = true;
        private bool _refreshInFlight;
        private bool _lastUbuntuOnline;
        private DateTime _nextUbuntuPollUtc = DateTime.MinValue;
        private DateTime _nextLmPollUtc = DateTime.MinValue;

        private MetricPanel _cpuPanel;
        private MetricPanel _memoryPanel;
        private MetricPanel _gpuPanel;
        private MetricPanel _ubuntuPanel;
        private MetricPanel _lmPanel;
        private ProcessPanel _processPanel;
        private UniformGrid _localGrid;
        private UniformGrid _ubuntuGrid;
        private Grid _bottomGrid;
        private UIElement _ubuntuGroup;
        private UIElement _processGroup;

        private TextBlock _intervalText;
        private TextBlock _effectiveText;
        private TextBlock _clockText;
        private TextBlock _statusText;
        private TextBlock _remoteSummaryText;
        private TextBlock _footerText;
        private CheckBox _autoCheckBox;

        public MainWindow()
        {
            _settingsStore = new SettingsStore();
            _settings = _settingsStore.Load();
            _settingsStore.Save(_settings);
            _localService = new LocalMonitorService();
            _ubuntuService = new UbuntuMonitorService();
            _lmStudioService = new LmStudioService();
            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Tick += OnTimerTick;

            Title = "OpenClaw Monitor";
            Width = 1280;
            Height = 780;
            MinWidth = 980;
            MinHeight = 620;
            Background = Theme.Background;
            FontFamily = Theme.MonoFont;
            Foreground = Theme.TextBrush;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Content = BuildLayout();
            UpdateRemoteSummary("ssh --");
            ApplyTimerInterval();
            UpdateRefreshLabels();

            Loaded += delegate
            {
                _timer.Start();
                RefreshNow();
            };
            Activated += delegate
            {
                _isWindowActive = true;
                ApplyTimerInterval();
            };
            Deactivated += delegate
            {
                _isWindowActive = false;
                ApplyTimerInterval();
            };
            Closed += delegate
            {
                _lmStudioService.Dispose();
            };
            SizeChanged += delegate { ApplyResponsiveLayout(); };
        }

        private UIElement BuildLayout()
        {
            var root = new DockPanel();
            root.Background = Theme.Background;

            var topBar = BuildTopBar();
            DockPanel.SetDock(topBar, Dock.Top);
            root.Children.Add(topBar);

            var footer = BuildFooter();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var main = new Grid();
            main.Margin = new Thickness(10, 0, 10, 8);
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.05, GridUnitType.Star) });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.95, GridUnitType.Star) });

            var localGroup = BuildLocalGroup();
            _bottomGrid = new Grid();
            _bottomGrid.Margin = new Thickness(0, 8, 0, 0);
            _bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.48, GridUnitType.Star) });
            _bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            _bottomGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _ubuntuGroup = BuildUbuntuGroup();
            _processGroup = BuildProcessGroup();
            Grid.SetColumn(_ubuntuGroup, 0);
            Grid.SetColumn(_processGroup, 1);
            _bottomGrid.Children.Add(_ubuntuGroup);
            _bottomGrid.Children.Add(_processGroup);

            Grid.SetRow(localGroup, 0);
            Grid.SetRow(_bottomGrid, 1);
            main.Children.Add(localGroup);
            main.Children.Add(_bottomGrid);

            root.Children.Add(main);
            ApplyResponsiveLayout();

            return root;
        }

        private UIElement BuildLocalGroup()
        {
            _localGrid = new UniformGrid();
            _localGrid.Rows = 1;
            _localGrid.Columns = 3;

            _cpuPanel = new MetricPanel("CPU", Theme.GreenBrush);
            _memoryPanel = new MetricPanel("Memory", Theme.GreenBrush);
            _gpuPanel = new MetricPanel("NVIDIA GPU", Theme.GreenBrush);
            _localGrid.Children.Add(_cpuPanel);
            _localGrid.Children.Add(_memoryPanel);
            _localGrid.Children.Add(_gpuPanel);

            var frame = new BtopFrame("Local Windows", Theme.GreenBrush);
            frame.SetContent(_localGrid);
            return frame;
        }

        private UIElement BuildUbuntuGroup()
        {
            _ubuntuGrid = new UniformGrid();
            _ubuntuGrid.Rows = 1;
            _ubuntuGrid.Columns = 2;

            _ubuntuPanel = new MetricPanel("CPU / Memory", Theme.CyanBrush);
            _lmPanel = new MetricPanel("LM Studio", Theme.MagentaBrush);
            _ubuntuGrid.Children.Add(_ubuntuPanel);
            _ubuntuGrid.Children.Add(_lmPanel);

            var frame = new BtopFrame("Ubuntu LAN (SSH)", Theme.GreenBrush);
            frame.SetContent(_ubuntuGrid);
            return frame;
        }

        private UIElement BuildProcessGroup()
        {
            _processPanel = new ProcessPanel();
            var frame = new BtopFrame("Top Processes (All)", Theme.YellowBrush);
            frame.SetContent(_processPanel);
            return frame;
        }

        private UIElement BuildFooter()
        {
            var border = new Border();
            border.Margin = new Thickness(10, 0, 10, 8);
            border.Padding = new Thickness(14, 7, 14, 7);
            border.BorderThickness = new Thickness(1);
            border.BorderBrush = Theme.DarkBorderBrush;
            border.Background = Theme.ChartBackgroundBrush;

            _footerText = new TextBlock();
            _footerText.FontSize = 12;
            _footerText.Foreground = Theme.MutedBrush;
            _footerText.Text = "Local Windows  |  CPU --  |  MEM --  |  GPU --  |  Ubuntu LAN (SSH)  |  CPU --  |  MEM --";
            border.Child = _footerText;
            return border;
        }

        private void ApplyResponsiveLayout()
        {
            var width = ActualWidth > 0 ? ActualWidth : Width;

            if (_localGrid != null)
            {
                _localGrid.Columns = width >= 1150 ? 3 : 1;
                _localGrid.Rows = width >= 1150 ? 1 : 3;
            }

            if (_ubuntuGrid != null)
            {
                _ubuntuGrid.Columns = width >= 760 ? 2 : 1;
                _ubuntuGrid.Rows = width >= 760 ? 1 : 2;
            }

            if (_bottomGrid != null && _ubuntuGroup != null && _processGroup != null)
            {
                _bottomGrid.ColumnDefinitions.Clear();
                _bottomGrid.RowDefinitions.Clear();
                if (width >= 1150)
                {
                    _bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.48, GridUnitType.Star) });
                    _bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
                    _bottomGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    Grid.SetColumn(_ubuntuGroup, 0);
                    Grid.SetRow(_ubuntuGroup, 0);
                    Grid.SetColumn(_processGroup, 1);
                    Grid.SetRow(_processGroup, 0);
                }
                else
                {
                    _bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    _bottomGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    _bottomGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    Grid.SetColumn(_ubuntuGroup, 0);
                    Grid.SetRow(_ubuntuGroup, 0);
                    Grid.SetColumn(_processGroup, 0);
                    Grid.SetRow(_processGroup, 1);
                }
            }
        }

        private UIElement BuildTopBar()
        {
            var shell = new Border();
            shell.Height = 46;
            shell.Margin = new Thickness(10, 8, 10, 6);
            shell.Padding = new Thickness(10, 0, 10, 0);
            shell.BorderBrush = Theme.DarkBorderBrush;
            shell.BorderThickness = new Thickness(1);
            shell.Background = Theme.HeaderBrush;

            var bar = new DockPanel();
            shell.Child = bar;

            var left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.VerticalAlignment = VerticalAlignment.Center;

            left.Children.Add(TopToken("openclaw", Theme.TextBrush, true));
            left.Children.Add(TopToken("local", Theme.GreenBrush, false));
            _remoteSummaryText = TopToken("ssh --", Theme.MutedBrush, false);
            left.Children.Add(_remoteSummaryText);

            _statusText = new TextBlock();
            _statusText.Text = "BOOT";
            _statusText.FontSize = 13;
            _statusText.FontWeight = FontWeights.Bold;
            _statusText.Foreground = Theme.MutedBrush;
            _statusText.Margin = new Thickness(14, 0, 0, 0);
            _statusText.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(_statusText);

            var center = new StackPanel();
            center.Orientation = Orientation.Horizontal;
            center.HorizontalAlignment = HorizontalAlignment.Center;
            center.VerticalAlignment = VerticalAlignment.Center;

            _clockText = new TextBlock();
            _clockText.FontSize = 13;
            _clockText.Foreground = Theme.MutedBrush;
            _clockText.Margin = new Thickness(0, 0, 18, 0);
            center.Children.Add(_clockText);

            _effectiveText = new TextBlock();
            _effectiveText.FontSize = 13;
            _effectiveText.Foreground = Theme.CyanBrush;
            _effectiveText.VerticalAlignment = VerticalAlignment.Center;
            center.Children.Add(_effectiveText);

            var controls = new StackPanel();
            controls.Orientation = Orientation.Horizontal;
            controls.VerticalAlignment = VerticalAlignment.Center;

            var minus = UiFactory.SmallButton("-");
            minus.Click += delegate { ChangeRefreshStep(-1); };
            controls.Children.Add(minus);

            _intervalText = new TextBlock();
            _intervalText.FontSize = 16;
            _intervalText.FontWeight = FontWeights.Bold;
            _intervalText.Foreground = Theme.TextBrush;
            _intervalText.VerticalAlignment = VerticalAlignment.Center;
            _intervalText.TextAlignment = TextAlignment.Center;
            _intervalText.Width = 92;
            controls.Children.Add(_intervalText);

            var plus = UiFactory.SmallButton("+");
            plus.Click += delegate { ChangeRefreshStep(1); };
            controls.Children.Add(plus);

            _autoCheckBox = new CheckBox();
            _autoCheckBox.Content = "AUTO";
            _autoCheckBox.IsChecked = _settings.AutoMode;
            _autoCheckBox.Foreground = Theme.TextBrush;
            _autoCheckBox.Margin = new Thickness(14, 0, 0, 0);
            _autoCheckBox.VerticalAlignment = VerticalAlignment.Center;
            _autoCheckBox.Checked += delegate { SetAutoMode(true); };
            _autoCheckBox.Unchecked += delegate { SetAutoMode(false); };
            controls.Children.Add(_autoCheckBox);

            var settings = UiFactory.TextButton("settings");
            settings.Margin = new Thickness(12, 0, 0, 0);
            settings.Click += delegate { ShowSettingsDialog(); };
            controls.Children.Add(settings);

            DockPanel.SetDock(left, Dock.Left);
            DockPanel.SetDock(controls, Dock.Right);
            bar.Children.Add(left);
            bar.Children.Add(controls);
            bar.Children.Add(center);
            return shell;
        }

        private TextBlock TopToken(string text, Brush brush, bool strong)
        {
            var block = new TextBlock();
            block.Text = text;
            block.FontSize = 14;
            block.FontWeight = strong ? FontWeights.Bold : FontWeights.Normal;
            block.Foreground = brush;
            block.Margin = new Thickness(0, 0, 18, 0);
            block.VerticalAlignment = VerticalAlignment.Center;
            return block;
        }

        private void SaveSettingsValues(string remoteTarget, string password, string lmUrl)
        {
            _settings.UbuntuTarget = (remoteTarget ?? string.Empty).Trim();
            _settings.UbuntuPassword = password ?? string.Empty;
            _settings.ApplyUbuntuTarget();

            _settings.LmStudioBaseUrl = (lmUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(_settings.LmStudioBaseUrl))
            {
                _settings.LmStudioBaseUrl = "http://localhost:1234";
            }

            _settingsStore.Save(_settings);
            _nextUbuntuPollUtc = DateTime.MinValue;
            _nextLmPollUtc = DateTime.MinValue;
            _lmStudioService.ResetLogTailer();
            UpdateRemoteSummary("ssh saved");
            SetStatus("CONFIG SAVED");
        }

        private void ShowSettingsDialog()
        {
            var dialog = new SettingsWindow(_settings);
            dialog.Owner = this;
            if (dialog.ShowDialog() == true)
            {
                SaveSettingsValues(dialog.RemoteTarget, dialog.Password, dialog.LmStudioUrl);
                RefreshNow();
            }
        }

        private void ChangeRefreshStep(int delta)
        {
            var next = _settings.RefreshMs + delta * RefreshStepMs;
            next = Math.Max(MinRefreshMs, Math.Min(MaxRefreshMs, next));
            _settings.RefreshMs = next;
            _settingsStore.Save(_settings);
            ApplyTimerInterval();
            UpdateRefreshLabels();
        }

        private void SetAutoMode(bool enabled)
        {
            _settings.AutoMode = enabled;
            _settingsStore.Save(_settings);
            ApplyTimerInterval();
            UpdateRefreshLabels();
        }

        private void ApplyTimerInterval()
        {
            var interval = GetEffectiveTickMs();
            _timer.Interval = TimeSpan.FromMilliseconds(interval);
            UpdateRefreshLabels();
        }

        private int GetEffectiveTickMs()
        {
            if (!_settings.AutoMode)
            {
                return _settings.RefreshMs;
            }
            if (!_isWindowActive)
            {
                return Math.Max(5000, _settings.RefreshMs);
            }
            return _settings.RefreshMs;
        }

        private int GetUbuntuPollMs()
        {
            if (!_settings.AutoMode)
            {
                return _lastUbuntuOnline ? _settings.RefreshMs : Math.Max(2000, _settings.RefreshMs);
            }
            if (!_isWindowActive || !_lastUbuntuOnline)
            {
                return Math.Max(2000, _settings.RefreshMs * 5);
            }
            return _settings.RefreshMs;
        }

        private int GetLmPollMs()
        {
            if (!_settings.AutoMode)
            {
                return Math.Max(500, _settings.RefreshMs);
            }
            if (!_isWindowActive)
            {
                return 2000;
            }
            return Math.Max(500, _settings.RefreshMs);
        }

        private void UpdateRefreshLabels()
        {
            if (_intervalText != null)
            {
                _intervalText.Text = _settings.RefreshMs.ToString(CultureInfo.InvariantCulture) + "ms";
            }
            if (_effectiveText != null)
            {
                _effectiveText.Text = "EFFECTIVE " + GetEffectiveTickMs().ToString(CultureInfo.InvariantCulture) + "ms";
            }
        }

        private void ForceRemotePoll()
        {
            _nextUbuntuPollUtc = DateTime.MinValue;
            _nextLmPollUtc = DateTime.MinValue;
            RefreshNow();
        }

        private void RefreshNow()
        {
            OnTimerTick(this, EventArgs.Empty);
        }

        private async void OnTimerTick(object sender, EventArgs e)
        {
            if (_refreshInFlight)
            {
                return;
            }

            _refreshInFlight = true;
            try
            {
                var startedUtc = DateTime.UtcNow;
                _clockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

                var localTask = Task.Factory.StartNew(() => _localService.Read());
                Task<UbuntuSnapshot> ubuntuTask = null;
                Task<LmStudioSnapshot> lmTask = null;

                if (startedUtc >= _nextUbuntuPollUtc)
                {
                    var settingsCopy = _settings.Clone();
                    ubuntuTask = Task.Factory.StartNew(() => _ubuntuService.Read(settingsCopy));
                }

                if (startedUtc >= _nextLmPollUtc)
                {
                    var settingsCopy = _settings.Clone();
                    lmTask = Task.Factory.StartNew(() => _lmStudioService.Read(settingsCopy));
                }

                var local = await localTask;
                ApplyLocalSnapshot(local);

                if (ubuntuTask != null)
                {
                    var ubuntu = await ubuntuTask;
                    _lastUbuntuOnline = ubuntu.Online;
                    ApplyUbuntuSnapshot(ubuntu);
                    _nextUbuntuPollUtc = DateTime.UtcNow.AddMilliseconds(GetUbuntuPollMs());
                }

                if (lmTask != null)
                {
                    var lm = await lmTask;
                    ApplyLmStudioSnapshot(lm);
                    _nextLmPollUtc = DateTime.UtcNow.AddMilliseconds(GetLmPollMs());
                }

                ApplyTimerInterval();
                SetStatus("OK " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                SetStatus("ERR " + ex.Message);
            }
            finally
            {
                _refreshInFlight = false;
            }
        }

        private void ApplyLocalSnapshot(LocalSnapshot snapshot)
        {
            _cpuPanel.SetStatus("LOCAL", Theme.CyanBrush);
            _cpuPanel.SetMetric(0, "USE", Format.Percent(snapshot.CpuPercent), "TOTAL", Theme.PercentBrush(snapshot.CpuPercent));
            _cpuPanel.SetMetric(1, "CLOCK", "N/A", "GHz", Theme.TextBrush);
            _cpuPanel.SetMetric(2, "CORES", snapshot.LogicalProcessorCount.ToString(CultureInfo.InvariantCulture), "LOGICAL", Theme.TextBrush);
            _cpuPanel.SetMetric(3, "PKG W", Format.Watts(snapshot.CpuPackagePowerWatts), "POWER", Theme.ValueBrush);
            _cpuPanel.AddSample(snapshot.CpuPercent);
            _cpuPanel.SetBar(snapshot.CpuPercent);

            _memoryPanel.SetStatus("LOCAL", Theme.GreenBrush);
            _memoryPanel.SetMetric(0, "USE", Format.Percent(snapshot.MemoryPercent), "RAM", Theme.PercentBrush(snapshot.MemoryPercent));
            _memoryPanel.SetMetric(1, "USED", Format.Gigabytes(snapshot.MemoryUsedBytes), "GB", Theme.TextBrush);
            _memoryPanel.SetMetric(2, "TOTAL", Format.Gigabytes(snapshot.MemoryTotalBytes), "GB", Theme.TextBrush);
            _memoryPanel.SetMetric(3, "FREE", Format.Gigabytes(snapshot.MemoryAvailableBytes), "GB", Theme.MutedBrush);
            _memoryPanel.AddSample(snapshot.MemoryPercent);
            _memoryPanel.SetBar(snapshot.MemoryPercent);

            _gpuPanel.SetStatus(snapshot.GpuAvailable ? "NVIDIA" : "N/A", snapshot.GpuAvailable ? Theme.MagentaBrush : Theme.MutedBrush);
            _gpuPanel.SetMetric(0, "USE", Format.Percent(snapshot.GpuUtilizationPercent), "GPU", Theme.PercentBrush(snapshot.GpuUtilizationPercent));
            _gpuPanel.SetMetric(1, "TEMP", Format.Temperature(snapshot.GpuTemperatureCelsius), "C", Theme.TempBrush(snapshot.GpuTemperatureCelsius));
            _gpuPanel.SetMetric(2, "VRAM", Format.MemoryPairMb(snapshot.GpuMemoryUsedMb, snapshot.GpuMemoryTotalMb), "MB", Theme.TextBrush);
            _gpuPanel.SetMetric(3, "POWER", Format.Watts(snapshot.GpuPowerWatts), "NVIDIA-SMI", Theme.ValueBrush);
            _gpuPanel.AddSample(snapshot.GpuUtilizationPercent);
            _gpuPanel.SetBar(snapshot.GpuUtilizationPercent);

            if (_processPanel != null)
            {
                _processPanel.RefreshLocalRows();
            }
            UpdateFooter(snapshot, null);
        }

        private void ApplyUbuntuSnapshot(UbuntuSnapshot snapshot)
        {
            _ubuntuPanel.SetStatus(snapshot.Online ? "ONLINE" : "OFFLINE", snapshot.Online ? Theme.GreenBrush : Theme.RedBrush);
            UpdateRemoteSummary(snapshot.Online ? "ssh connected" : "ssh offline");
            _ubuntuPanel.SetMetric(0, "CPU", Format.Percent(snapshot.CpuPercent), "REMOTE", Theme.PercentBrush(snapshot.CpuPercent));
            _ubuntuPanel.SetMetric(1, "MEM", Format.Percent(snapshot.MemoryPercent), Format.MemoryPairMb(snapshot.MemoryUsedMb, snapshot.MemoryTotalMb), Theme.PercentBrush(snapshot.MemoryPercent));
            _ubuntuPanel.SetMetric(2, "POWER", Format.Watts(snapshot.PowerWatts), "RAPL", Theme.ValueBrush);
            _ubuntuPanel.SetMetric(3, "RTT", snapshot.Online ? snapshot.LatencyMs.ToString("0", CultureInfo.InvariantCulture) + "ms" : "N/A", ShortError(snapshot.Error), snapshot.Online ? Theme.TextBrush : Theme.RedBrush);
            _ubuntuPanel.AddSample(snapshot.CpuPercent);
            _ubuntuPanel.SetBar(snapshot.CpuPercent);
            if (_processPanel != null)
            {
                _processPanel.SetUbuntuRows(snapshot.Processes, snapshot.Online, ShortError(snapshot.Error));
            }
            UpdateFooter(null, snapshot);
        }

        private void ApplyLmStudioSnapshot(LmStudioSnapshot snapshot)
        {
            _lmPanel.SetStatus(snapshot.ServerOnline ? "ONLINE" : "OFFLINE", snapshot.ServerOnline ? Theme.BlueBrush : Theme.RedBrush);
            _lmPanel.SetMetric(0, "MODEL", string.IsNullOrEmpty(snapshot.ActiveModel) ? "N/A" : snapshot.ActiveModel, snapshot.LoadedModelCount.ToString(CultureInfo.InvariantCulture) + " LOADED", snapshot.ServerOnline ? Theme.TextBrush : Theme.MutedBrush);
            _lmPanel.SetMetric(1, "PROC", Format.Processing(snapshot.IsProcessing), snapshot.Source, ProcessingBrush(snapshot.IsProcessing));
            _lmPanel.SetMetric(2, "TOK/S", Format.Number(snapshot.TokensPerSecond), "LAST", Theme.ValueBrush);
            _lmPanel.SetMetric(3, "TOKENS", Format.TokenPair(snapshot.SessionInputTokens, snapshot.SessionOutputTokens), "IN/OUT", Theme.TextBrush);
            _lmPanel.AddSample(snapshot.TokensPerSecond);
            _lmPanel.SetBar(snapshot.TokensPerSecond.HasValue ? Math.Min(100.0, snapshot.TokensPerSecond.Value) : (double?)null);
        }

        private Brush ProcessingBrush(bool? processing)
        {
            if (!processing.HasValue)
            {
                return Theme.MutedBrush;
            }
            return processing.Value ? Theme.YellowBrush : Theme.GreenBrush;
        }

        private string ShortError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return "OK";
            }
            error = error.Trim();
            if (error.Length > 28)
            {
                return error.Substring(0, 28);
            }
            return error;
        }

        private void SetStatus(string text)
        {
            if (_statusText != null)
            {
                _statusText.Text = text;
            }
        }

        private void UpdateRemoteSummary(string state)
        {
            if (_remoteSummaryText == null)
            {
                return;
            }

            var target = string.IsNullOrWhiteSpace(_settings.UbuntuTarget) ? "remote --" : _settings.UbuntuTarget;
            _remoteSummaryText.Text = state + " " + target;
            _remoteSummaryText.Foreground = state.IndexOf("connected", StringComparison.OrdinalIgnoreCase) >= 0 ? Theme.GreenBrush :
                (state.IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0 ? Theme.RedBrush : Theme.MutedBrush);
        }

        private LocalSnapshot _lastLocalForFooter;
        private UbuntuSnapshot _lastUbuntuForFooter;

        private void UpdateFooter(LocalSnapshot local, UbuntuSnapshot ubuntu)
        {
            if (local != null)
            {
                _lastLocalForFooter = local;
            }
            if (ubuntu != null)
            {
                _lastUbuntuForFooter = ubuntu;
            }
            if (_footerText == null)
            {
                return;
            }

            var l = _lastLocalForFooter;
            var u = _lastUbuntuForFooter;
            _footerText.Text =
                "Local Windows  |  CPU " + Format.Percent(l == null ? null : l.CpuPercent) +
                "  |  MEM " + Format.Percent(l == null ? null : l.MemoryPercent) +
                "  |  GPU " + Format.Percent(l == null ? null : l.GpuUtilizationPercent) +
                "  |  Ubuntu LAN (SSH)  |  " + (u != null && u.Online ? "ONLINE" : "OFFLINE") +
                "  |  CPU " + Format.Percent(u == null ? null : u.CpuPercent) +
                "  |  MEM " + Format.Percent(u == null ? null : u.MemoryPercent);
        }
    }

    public sealed class BtopFrame : Grid
    {
        private readonly Border _border;
        private readonly ContentControl _content;

        public BtopFrame(string title, Brush accent)
        {
            Margin = new Thickness(0);

            _border = new Border();
            _border.Margin = new Thickness(0, 10, 0, 0);
            _border.Padding = new Thickness(8);
            _border.BorderThickness = new Thickness(1);
            _border.BorderBrush = accent;
            _border.Background = Theme.PanelBrush;
            Children.Add(_border);

            _content = new ContentControl();
            _border.Child = _content;

            var label = new Border();
            label.Background = Theme.Background;
            label.Padding = new Thickness(8, 0, 8, 0);
            label.Margin = new Thickness(18, 0, 0, 0);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            label.VerticalAlignment = VerticalAlignment.Top;

            var text = new TextBlock();
            text.Text = title;
            text.Foreground = accent;
            text.FontSize = 13;
            text.FontWeight = FontWeights.Bold;
            label.Child = text;
            Children.Add(label);
        }

        public void SetContent(UIElement element)
        {
            _content.Content = element;
        }
    }

    public sealed class ProcessPanel : Grid
    {
        private readonly TextBlock _modeText;
        private readonly Button _localButton;
        private readonly Button _ubuntuButton;
        private readonly Grid _table;
        private readonly TextBlock _footer;
        private readonly Dictionary<int, LocalProcessSample> _localSamples;
        private List<ProcessRow> _localRows;
        private List<ProcessRow> _ubuntuRows;
        private bool _showUbuntu;
        private bool _ubuntuOnline;
        private string _ubuntuStatus;

        public ProcessPanel()
        {
            _localSamples = new Dictionary<int, LocalProcessSample>();
            _localRows = new List<ProcessRow>();
            _ubuntuRows = new List<ProcessRow>();
            _ubuntuStatus = "WAIT";

            Margin = new Thickness(4);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var modeBar = new DockPanel();
            modeBar.Margin = new Thickness(0, 0, 0, 6);
            _modeText = new TextBlock();
            _modeText.FontSize = 13;
            _modeText.FontWeight = FontWeights.Bold;
            _modeText.Foreground = Theme.YellowBrush;
            _modeText.VerticalAlignment = VerticalAlignment.Center;
            _modeText.Text = "proc < local >";

            var buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            _localButton = UiFactory.TinyButton("local");
            _localButton.Click += delegate
            {
                _showUbuntu = false;
                Render();
            };
            _ubuntuButton = UiFactory.TinyButton("ubuntu");
            _ubuntuButton.Margin = new Thickness(8, 2, 0, 2);
            _ubuntuButton.Click += delegate
            {
                _showUbuntu = true;
                Render();
            };
            buttons.Children.Add(_localButton);
            buttons.Children.Add(_ubuntuButton);

            DockPanel.SetDock(buttons, Dock.Right);
            modeBar.Children.Add(buttons);
            modeBar.Children.Add(_modeText);
            Children.Add(modeBar);

            _table = new Grid();
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            Grid.SetRow(_table, 1);
            Children.Add(_table);

            _footer = new TextBlock();
            _footer.Foreground = Theme.MutedBrush;
            _footer.FontSize = 12;
            _footer.Margin = new Thickness(0, 7, 0, 0);
            _footer.Text = "Press R to refresh";
            Grid.SetRow(_footer, 2);
            Children.Add(_footer);
            Render();
        }

        public void RefreshLocalRows()
        {
            _localRows = CaptureLocalRows();
            if (!_showUbuntu)
            {
                Render();
            }
        }

        public void SetUbuntuRows(IEnumerable<ProcessRow> rows, bool online, string status)
        {
            _ubuntuRows = rows == null ? new List<ProcessRow>() : rows.ToList();
            _ubuntuOnline = online;
            _ubuntuStatus = string.IsNullOrWhiteSpace(status) ? (online ? "ONLINE" : "OFFLINE") : status;
            if (_showUbuntu)
            {
                Render();
            }
        }

        private void Render()
        {
            _table.Children.Clear();
            _table.RowDefinitions.Clear();
            AddRow(0, "PID", "Process", "User", "CPU%", "MEM", "Status", Theme.MutedBrush, false);

            var rows = _showUbuntu ? _ubuntuRows : _localRows;
            _modeText.Text = _showUbuntu ? "proc < ubuntu >" : "proc < local >";
            _localButton.Foreground = _showUbuntu ? Theme.MutedBrush : Theme.GreenBrush;
            _ubuntuButton.Foreground = _showUbuntu ? Theme.GreenBrush : Theme.MutedBrush;

            if (_showUbuntu && !_ubuntuOnline && rows.Count == 0)
            {
                AddRow(1, "--", "Ubuntu offline", "--", "--", "--", _ubuntuStatus, Theme.RedBrush, true);
                _footer.Text = "source: Ubuntu LAN (SSH)                                  Total: 0";
                return;
            }

            if (rows.Count == 0)
            {
                AddRow(1, "--", "No process data", "--", "--", "--", "--", Theme.MutedBrush, true);
                _footer.Text = "source: " + (_showUbuntu ? "Ubuntu LAN (SSH)" : "Local Windows") + "                                  Total: 0";
                return;
            }

            var row = 1;
            foreach (var process in rows.Take(18))
            {
                AddRow(
                    row,
                    process.Pid,
                    process.Name,
                    process.User,
                    process.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture),
                    process.MemoryText,
                    process.Status,
                    row <= 5 ? Theme.GreenBrush : Theme.MutedGreenBrush,
                    true);
                row++;
            }
            _footer.Text = "select < local > < ubuntu >                         source: " +
                (_showUbuntu ? "Ubuntu LAN (SSH)" : "Local Windows") +
                "   Total: " + rows.Count.ToString(CultureInfo.InvariantCulture);
        }

        private void AddRow(int row, string pid, string name, string user, string cpu, string mem, string status, Brush valueBrush, bool dataRow)
        {
            _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(row, 0, pid, dataRow ? Theme.TextBrush : Theme.MutedBrush, TextAlignment.Right);
            AddCell(row, 1, name, valueBrush, TextAlignment.Left);
            AddCell(row, 2, user, Theme.TextBrush, TextAlignment.Left);
            AddCell(row, 3, cpu, Theme.GreenBrush, TextAlignment.Right);
            AddCell(row, 4, mem, Theme.BlueBrush, TextAlignment.Right);
            AddCell(row, 5, status, valueBrush, TextAlignment.Center);
        }

        private void AddCell(int row, int column, string text, Brush brush, TextAlignment alignment)
        {
            var block = new TextBlock();
            block.Text = text;
            block.Foreground = brush;
            block.FontSize = 12;
            block.Margin = new Thickness(4, 1, 4, 1);
            block.TextAlignment = alignment;
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(block, row);
            Grid.SetColumn(block, column);
            _table.Children.Add(block);
        }

        private List<ProcessRow> CaptureLocalRows()
        {
            var now = DateTime.UtcNow;
            var rows = new List<ProcessRow>();
            var seen = new HashSet<int>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var id = process.Id;
                    seen.Add(id);
                    var cpuTime = process.TotalProcessorTime;
                    var cpuPercent = 0.0;
                    LocalProcessSample prior;
                    if (_localSamples.TryGetValue(id, out prior))
                    {
                        var elapsed = Math.Max(0.001, (now - prior.TimestampUtc).TotalSeconds);
                        var cpuDelta = Math.Max(0.0, (cpuTime - prior.CpuTime).TotalSeconds);
                        cpuPercent = Math.Max(0.0, Math.Min(999.0, cpuDelta / elapsed / Math.Max(1, Environment.ProcessorCount) * 100.0));
                    }
                    _localSamples[id] = new LocalProcessSample { TimestampUtc = now, CpuTime = cpuTime };

                    rows.Add(new ProcessRow
                    {
                        Pid = id.ToString(CultureInfo.InvariantCulture),
                        Name = process.ProcessName,
                        User = "local",
                        CpuPercent = cpuPercent,
                        MemoryBytes = SafeWorkingSet(process),
                        MemoryText = Format.Megabytes(SafeWorkingSet(process)),
                        Status = SafeStatus(process)
                    });
                }
                catch
                {
                }
                finally
                {
                    try { process.Dispose(); }
                    catch { }
                }
            }

            var stale = _localSamples.Keys.Where(id => !seen.Contains(id)).ToList();
            foreach (var id in stale)
            {
                _localSamples.Remove(id);
            }

            return rows
                .OrderByDescending(row => row.CpuPercent)
                .ThenByDescending(row => row.MemoryBytes)
                .ThenBy(row => row.Name)
                .Take(30)
                .ToList();
        }

        private static long SafeWorkingSet(Process process)
        {
            try { return process.WorkingSet64; }
            catch { return 0; }
        }

        private static string SafeStatus(Process process)
        {
            try
            {
                return process.Responding ? "R" : "S";
            }
            catch
            {
                return "S";
            }
        }

        private sealed class LocalProcessSample
        {
            public DateTime TimestampUtc { get; set; }
            public TimeSpan CpuTime { get; set; }
        }
    }

    public sealed class SettingsWindow : Window
    {
        private readonly TextBox _remoteBox;
        private readonly PasswordBox _passwordBox;
        private readonly TextBox _lmBox;

        public string RemoteTarget { get; private set; }
        public string Password { get; private set; }
        public string LmStudioUrl { get; private set; }

        public SettingsWindow(MonitorSettings settings)
        {
            Title = "OpenClaw Monitor Setup";
            Width = 520;
            Height = 260;
            MinWidth = 460;
            MinHeight = 230;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = Theme.Background;
            Foreground = Theme.TextBrush;
            FontFamily = Theme.MonoFont;

            var frame = new BtopFrame("setup", Theme.CyanBrush);
            frame.Margin = new Thickness(12);
            var grid = new Grid();
            grid.Margin = new Thickness(8);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _remoteBox = AddTextRow(grid, 0, "REMOTE", settings.UbuntuTarget);
            _passwordBox = AddPasswordRow(grid, 1, "PASS", settings.UbuntuPassword);
            _lmBox = AddTextRow(grid, 2, "LM API", settings.LmStudioBaseUrl);

            var buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            var save = UiFactory.TextButton("save");
            save.Click += delegate { SaveAndClose(); };
            buttons.Children.Add(save);
            var cancel = UiFactory.TextButton("cancel");
            cancel.Margin = new Thickness(8, 0, 0, 0);
            cancel.Click += delegate { DialogResult = false; Close(); };
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 4);
            Grid.SetColumnSpan(buttons, 2);
            grid.Children.Add(buttons);

            frame.SetContent(grid);
            Content = frame;
        }

        private TextBox AddTextRow(Grid grid, int row, string label, string value)
        {
            AddLabel(grid, row, label);
            var box = UiFactory.InputBox();
            box.Text = value ?? string.Empty;
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            return box;
        }

        private PasswordBox AddPasswordRow(Grid grid, int row, string label, string value)
        {
            AddLabel(grid, row, label);
            var box = UiFactory.SecretInputBox();
            box.Password = value ?? string.Empty;
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            return box;
        }

        private void AddLabel(Grid grid, int row, string text)
        {
            var label = new TextBlock();
            label.Text = text;
            label.Foreground = Theme.MutedBrush;
            label.FontSize = 12;
            label.Margin = new Thickness(0, 6, 8, 6);
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);
        }

        private void SaveAndClose()
        {
            RemoteTarget = _remoteBox.Text;
            Password = _passwordBox.Password;
            LmStudioUrl = _lmBox.Text;
            DialogResult = true;
            Close();
        }
    }

    public sealed class MetricPanel : Border
    {
        private readonly List<MetricCell> _cells;
        private readonly SparklineCanvas _sparkline;
        private readonly SegmentedBar _bar;
        private readonly TextBlock _status;

        public MetricPanel(string title, Brush accent)
        {
            _cells = new List<MetricCell>();
            Margin = new Thickness(4);
            Padding = new Thickness(8);
            BorderThickness = new Thickness(1);
            BorderBrush = accent;
            Background = Theme.PanelBrush;
            SnapsToDevicePixels = true;
            MinHeight = 210;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Margin = new Thickness(0, 0, 0, 8);

            var titleBlock = new TextBlock();
            titleBlock.Text = title;
            titleBlock.Foreground = accent;
            titleBlock.FontWeight = FontWeights.Bold;
            titleBlock.FontSize = 14;
            titleBlock.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(titleBlock);

            _status = new TextBlock();
            _status.Text = "WAIT";
            _status.Foreground = Theme.Background;
            _status.Background = Theme.MutedBrush;
            _status.Padding = new Thickness(6, 1, 6, 1);
            _status.FontSize = 11;
            _status.FontWeight = FontWeights.Bold;
            _status.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_status, 1);
            header.Children.Add(_status);

            var metrics = new UniformGrid();
            metrics.Columns = 4;
            metrics.Rows = 1;
            metrics.Margin = new Thickness(0, 0, 0, 8);
            for (int i = 0; i < 4; i++)
            {
                var cell = new MetricCell();
                cell.Margin = new Thickness(i == 0 ? 0 : 4, 0, i == 3 ? 0 : 4, 0);
                _cells.Add(cell);
                metrics.Children.Add(cell);
            }

            _sparkline = new SparklineCanvas(accent);
            _sparkline.MinHeight = 68;
            _sparkline.Margin = new Thickness(0, 0, 0, 8);

            _bar = new SegmentedBar();
            _bar.Height = 16;

            Grid.SetRow(header, 0);
            Grid.SetRow(metrics, 1);
            Grid.SetRow(_sparkline, 2);
            Grid.SetRow(_bar, 3);
            grid.Children.Add(header);
            grid.Children.Add(metrics);
            grid.Children.Add(_sparkline);
            grid.Children.Add(_bar);
            Child = grid;
        }

        public void SetStatus(string text, Brush brush)
        {
            _status.Text = text;
            _status.Background = brush;
            _status.Foreground = Theme.Background;
        }

        public void SetMetric(int index, string label, string value, string sub, Brush brush)
        {
            if (index < 0 || index >= _cells.Count)
            {
                return;
            }
            _cells[index].Set(label, value, sub, brush);
        }

        public void AddSample(double? value)
        {
            _sparkline.Add(value);
        }

        public void SetBar(double? value)
        {
            _bar.Value = value;
        }
    }

    public sealed class MetricCell : Border
    {
        private readonly TextBlock _label;
        private readonly TextBlock _value;
        private readonly TextBlock _sub;

        public MetricCell()
        {
            BorderThickness = new Thickness(0);
            Background = Brushes.Transparent;
            Padding = new Thickness(4, 3, 4, 3);
            MinHeight = 50;

            var stack = new StackPanel();
            stack.Orientation = Orientation.Vertical;

            _label = new TextBlock();
            _label.FontSize = 10;
            _label.Foreground = Theme.MutedBrush;
            _label.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(_label);

            _value = new TextBlock();
            _value.FontSize = 17;
            _value.FontWeight = FontWeights.Bold;
            _value.Foreground = Theme.TextBrush;
            _value.TextTrimming = TextTrimming.CharacterEllipsis;
            _value.Margin = new Thickness(0, 1, 0, 0);
            stack.Children.Add(_value);

            _sub = new TextBlock();
            _sub.FontSize = 10;
            _sub.Foreground = Theme.MutedBrush;
            _sub.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(_sub);

            Child = stack;
        }

        public void Set(string label, string value, string sub, Brush brush)
        {
            _label.Text = label ?? string.Empty;
            _value.Text = value ?? "N/A";
            _value.Foreground = brush ?? Theme.TextBrush;
            _sub.Text = sub ?? string.Empty;
        }
    }

    public sealed class SparklineCanvas : FrameworkElement
    {
        private readonly List<double?> _samples;
        private readonly Brush _accentBrush;
        private readonly Pen _accentPen;
        private const int Capacity = 96;

        public SparklineCanvas(Brush accent)
        {
            _samples = new List<double?>();
            _accentBrush = accent;
            _accentPen = new Pen(accent, 1.25);
            SnapsToDevicePixels = true;
        }

        public void Add(double? value)
        {
            if (value.HasValue)
            {
                value = Math.Max(0, Math.Min(100, value.Value));
            }
            _samples.Add(value);
            while (_samples.Count > Capacity)
            {
                _samples.RemoveAt(0);
            }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var w = ActualWidth;
            var h = ActualHeight;
            if (w <= 0 || h <= 0)
            {
                return;
            }

            dc.DrawRectangle(Theme.ChartBackgroundBrush, null, new Rect(0, 0, w, h));

            for (double x = 3; x < w; x += 9)
            {
                for (double y = 3; y < h; y += 9)
                {
                    dc.DrawRectangle(Theme.DotBrush, null, new Rect(x, y, 1, 1));
                }
            }

            var last = _samples.Where(sample => sample.HasValue).ToList();
            if (last.Count < 2)
            {
                DrawNoSignal(dc, w, h);
                return;
            }

            var step = Math.Max(4.0, w / Math.Max(1, Capacity));
            var dot = Math.Max(1.6, Math.Min(2.2, step - 2.0));
            var rows = Math.Max(6, (int)Math.Floor(h / 6.0));
            for (int i = 0; i < _samples.Count; i++)
            {
                var sample = _samples[i];
                if (!sample.HasValue)
                {
                    continue;
                }

                var x = (Capacity - _samples.Count + i) * step;
                var activeRows = Math.Max(1, (int)Math.Round(sample.Value / 100.0 * rows));
                for (int row = 0; row < activeRows; row++)
                {
                    var y = h - 4 - row * 5.0;
                    dc.DrawRectangle(_accentBrush, null, new Rect(x, y, dot, dot));
                }
            }
        }

        private void DrawNoSignal(DrawingContext dc, double w, double h)
        {
            var formatted = new FormattedText(
                "NO SIGNAL",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(Theme.MonoFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                11,
                Theme.MutedBrush);
            dc.DrawText(formatted, new Point(Math.Max(0, (w - formatted.Width) / 2), Math.Max(0, (h - formatted.Height) / 2)));
        }
    }

    public sealed class SegmentedBar : FrameworkElement
    {
        private double? _value;
        public double? Value
        {
            get { return _value; }
            set
            {
                _value = value.HasValue ? Math.Max(0, Math.Min(100, value.Value)) : (double?)null;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var w = ActualWidth;
            var h = ActualHeight;
            if (w <= 0 || h <= 0)
            {
                return;
            }
            dc.DrawRectangle(Theme.ChartBackgroundBrush, null, new Rect(0, 0, w, h));

            const int segments = 32;
            const double gap = 2;
            var segW = Math.Max(2, (w - (segments - 1) * gap) / segments);
            var active = Value.HasValue ? (int)Math.Round(Value.Value / 100.0 * segments) : 0;
            for (int i = 0; i < segments; i++)
            {
                var rect = new Rect(i * (segW + gap), 1, segW, Math.Max(1, h - 2));
                var brush = i < active ? Theme.SegmentBrush((double)i / Math.Max(1, segments - 1)) : Theme.SegmentOffBrush;
                dc.DrawRectangle(brush, null, rect);
            }
        }
    }

    public sealed class LocalMonitorService
    {
        private readonly PerformanceCounter _cpuCounter;
        private readonly CpuPowerReader _cpuPowerReader;
        private readonly string _nvidiaSmiPath;

        public LocalMonitorService()
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue();
            }
            catch
            {
                _cpuCounter = null;
            }
            _cpuPowerReader = new CpuPowerReader();
            _nvidiaSmiPath = FindNvidiaSmi();
        }

        public LocalSnapshot Read()
        {
            var snapshot = new LocalSnapshot();
            snapshot.CapturedAt = DateTime.Now;
            snapshot.LogicalProcessorCount = Environment.ProcessorCount;

            try
            {
                if (_cpuCounter != null)
                {
                    snapshot.CpuPercent = Math.Max(0, Math.Min(100, _cpuCounter.NextValue()));
                }
            }
            catch
            {
                snapshot.CpuPercent = null;
            }

            snapshot.CpuPackagePowerWatts = _cpuPowerReader.ReadWatts();
            ReadMemory(snapshot);
            ReadGpu(snapshot);
            return snapshot;
        }

        private void ReadMemory(LocalSnapshot snapshot)
        {
            var memory = new MemoryStatusEx();
            if (NativeMethods.GlobalMemoryStatusEx(memory))
            {
                snapshot.MemoryTotalBytes = memory.ullTotalPhys;
                snapshot.MemoryAvailableBytes = memory.ullAvailPhys;
                snapshot.MemoryUsedBytes = memory.ullTotalPhys - memory.ullAvailPhys;
                snapshot.MemoryPercent = memory.ullTotalPhys == 0 ? (double?)null : (double)snapshot.MemoryUsedBytes / memory.ullTotalPhys * 100.0;
            }
        }

        private void ReadGpu(LocalSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(_nvidiaSmiPath))
            {
                snapshot.GpuAvailable = false;
                return;
            }

            var args = new[]
            {
                "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total,power.draw",
                "--format=csv,noheader,nounits"
            };
            var result = ProcessRunner.Run(_nvidiaSmiPath, args, 1800, null);
            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
            {
                snapshot.GpuAvailable = false;
                return;
            }

            var line = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line))
            {
                snapshot.GpuAvailable = false;
                return;
            }

            var fields = line.Split(',');
            if (fields.Length >= 5)
            {
                snapshot.GpuAvailable = true;
                snapshot.GpuUtilizationPercent = ParseNullableDouble(fields[0]);
                snapshot.GpuTemperatureCelsius = ParseNullableDouble(fields[1]);
                snapshot.GpuMemoryUsedMb = ParseNullableDouble(fields[2]);
                snapshot.GpuMemoryTotalMb = ParseNullableDouble(fields[3]);
                snapshot.GpuPowerWatts = ParseNullableDouble(fields[4]);
            }
        }

        private static string FindNvidiaSmi()
        {
            var common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
            if (File.Exists(common))
            {
                return common;
            }
            return "nvidia-smi.exe";
        }

        private static double? ParseNullableDouble(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            value = value.Trim();
            if (value.Equals("N/A", StringComparison.OrdinalIgnoreCase) || value.Equals("[N/A]", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : (double?)null;
        }
    }

    public sealed class CpuPowerReader
    {
        private readonly PerformanceCounter _powerCounter;

        public CpuPowerReader()
        {
            try
            {
                var categories = PerformanceCounterCategory.GetCategories();
                foreach (var category in categories)
                {
                    if (category.CategoryName.IndexOf("Power Meter", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var instances = category.GetInstanceNames();
                    if (instances == null || instances.Length == 0)
                    {
                        continue;
                    }

                    foreach (var instance in instances)
                    {
                        var counters = category.GetCounters(instance);
                        foreach (var counter in counters)
                        {
                            if (counter.CounterName.IndexOf("Power", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                _powerCounter = new PerformanceCounter(category.CategoryName, counter.CounterName, instance);
                                _powerCounter.NextValue();
                                return;
                            }
                        }
                    }
                }
            }
            catch
            {
                _powerCounter = null;
            }
        }

        public double? ReadWatts()
        {
            if (_powerCounter == null)
            {
                return null;
            }
            try
            {
                var value = _powerCounter.NextValue();
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    return null;
                }
                return value;
            }
            catch
            {
                return null;
            }
        }
    }

    public sealed class UbuntuMonitorService
    {
        public UbuntuSnapshot Read(MonitorSettings settings)
        {
            var sw = Stopwatch.StartNew();
            var snapshot = new UbuntuSnapshot();
            snapshot.Online = false;

            if (string.IsNullOrWhiteSpace(settings.UbuntuHost) ||
                string.IsNullOrWhiteSpace(settings.UbuntuUser))
            {
                snapshot.Error = "CONFIG";
                return snapshot;
            }

            if (!string.IsNullOrWhiteSpace(settings.UbuntuPassword))
            {
                return ReadWithPassword(settings, sw, snapshot);
            }

            var keyPath = Environment.ExpandEnvironmentVariables(settings.UbuntuKeyPath);
            if (!File.Exists(keyPath))
            {
                snapshot.Error = "PASS OR KEY";
                return snapshot;
            }

            var args = new List<string>();
            args.Add("-i");
            args.Add(keyPath);
            args.Add("-p");
            args.Add(settings.UbuntuPort.ToString(CultureInfo.InvariantCulture));
            args.Add("-o");
            args.Add("BatchMode=yes");
            args.Add("-o");
            args.Add("IdentitiesOnly=yes");
            args.Add("-o");
            args.Add("ConnectTimeout=3");
            args.Add("-o");
            args.Add("ServerAliveInterval=2");
            args.Add("-o");
            args.Add("ServerAliveCountMax=1");
            args.Add("-o");
            args.Add("StrictHostKeyChecking=accept-new");
            args.Add(settings.UbuntuUser + "@" + settings.UbuntuHost);
            args.Add("python3");
            args.Add("-");

            var result = ProcessRunner.Run("ssh.exe", args, 6500, RemotePython);
            sw.Stop();
            snapshot.LatencyMs = sw.Elapsed.TotalMilliseconds;

            if (!result.Success)
            {
                snapshot.Error = FirstUsefulLine(result.StdErr, result.StdOut, result.ErrorMessage);
                return snapshot;
            }

            return ParseRemoteJson(result.StdOut, snapshot);
        }

        private UbuntuSnapshot ReadWithPassword(MonitorSettings settings, Stopwatch sw, UbuntuSnapshot snapshot)
        {
            try
            {
                using (var client = new SshClient(settings.UbuntuHost, settings.UbuntuPort, settings.UbuntuUser, settings.UbuntuPassword))
                {
                    client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(4);
                    client.Connect();
                    using (var command = client.CreateCommand("python3 - <<'PY'\n" + RemotePython + "\nPY"))
                    {
                        command.CommandTimeout = TimeSpan.FromSeconds(7);
                        var output = command.Execute();
                        sw.Stop();
                        snapshot.LatencyMs = sw.Elapsed.TotalMilliseconds;
                        if (command.ExitStatus != 0)
                        {
                            snapshot.Error = FirstUsefulLine(command.Error, output, "REMOTE ERR");
                            return snapshot;
                        }
                        return ParseRemoteJson(output, snapshot);
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                snapshot.LatencyMs = sw.Elapsed.TotalMilliseconds;
                snapshot.Error = ex.Message;
                return snapshot;
            }
        }

        private static string ExtractJson(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal))
                {
                    return line;
                }
            }
            return null;
        }

        private static string FirstUsefulLine(params string[] values)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }
                var line = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line.Trim();
                }
            }
            return "OFFLINE";
        }

        private static UbuntuSnapshot ParseRemoteJson(string output, UbuntuSnapshot snapshot)
        {
            var json = ExtractJson(output);
            if (string.IsNullOrWhiteSpace(json))
            {
                snapshot.Error = "NO DATA";
                return snapshot;
            }

            try
            {
                var root = JsonHelper.Parse(json) as Dictionary<string, object>;
                if (root == null)
                {
                    snapshot.Error = "BAD JSON";
                    return snapshot;
                }
                snapshot.Online = true;
                snapshot.CpuPercent = JsonHelper.GetDouble(root, "cpu_percent");
                snapshot.MemoryPercent = JsonHelper.GetDouble(root, "memory_percent");
                snapshot.MemoryUsedMb = JsonHelper.GetDouble(root, "memory_used_mb");
                snapshot.MemoryTotalMb = JsonHelper.GetDouble(root, "memory_total_mb");
                snapshot.PowerWatts = JsonHelper.GetDouble(root, "power_watts");
                snapshot.Processes = ParseProcessRows(JsonHelper.GetArray(root, "processes"));
                snapshot.Error = string.Empty;
                return snapshot;
            }
            catch (Exception ex)
            {
                snapshot.Error = ex.Message;
                return snapshot;
            }
        }

        private static List<ProcessRow> ParseProcessRows(object[] rows)
        {
            var processes = new List<ProcessRow>();
            if (rows == null)
            {
                return processes;
            }

            foreach (var item in rows)
            {
                var dict = item as Dictionary<string, object>;
                if (dict == null)
                {
                    continue;
                }

                var memoryKb = JsonHelper.GetDouble(dict, "memory_kb");
                var bytes = memoryKb.HasValue ? (long)(memoryKb.Value * 1024.0) : 0;
                processes.Add(new ProcessRow
                {
                    Pid = JsonHelper.GetString(dict, "pid") ?? "--",
                    Name = JsonHelper.GetString(dict, "name") ?? "--",
                    User = JsonHelper.GetString(dict, "user") ?? "--",
                    CpuPercent = JsonHelper.GetDouble(dict, "cpu_percent") ?? 0.0,
                    MemoryBytes = bytes,
                    MemoryText = Format.Megabytes(bytes),
                    Status = JsonHelper.GetString(dict, "status") ?? "--"
                });
            }

            return processes
                .OrderByDescending(row => row.CpuPercent)
                .ThenByDescending(row => row.MemoryBytes)
                .Take(40)
                .ToList();
        }

        private const string RemotePython = @"
import json
import os
import subprocess
import time

def cpu_snapshot():
    with open('/proc/stat', 'r') as f:
        parts = f.readline().split()
    vals = [int(x) for x in parts[1:8]]
    idle = vals[3] + vals[4]
    total = sum(vals)
    return idle, total

def read_energy_file():
    roots = ['/sys/class/powercap']
    for root in roots:
        if not os.path.isdir(root):
            continue
        for current, dirs, files in os.walk(root):
            if 'energy_uj' in files:
                path = os.path.join(current, 'energy_uj')
                try:
                    with open(path, 'r') as f:
                        return path, int(f.read().strip())
                except Exception:
                    pass
    return None, None

idle1, total1 = cpu_snapshot()
energy_path, energy1 = read_energy_file()
t1 = time.time()
time.sleep(0.25)
idle2, total2 = cpu_snapshot()
t2 = time.time()

total_delta = max(1, total2 - total1)
idle_delta = max(0, idle2 - idle1)
cpu_percent = max(0.0, min(100.0, (1.0 - (float(idle_delta) / float(total_delta))) * 100.0))

mem = {}
with open('/proc/meminfo', 'r') as f:
    for line in f:
        parts = line.split()
        if len(parts) >= 2:
            mem[parts[0].rstrip(':')] = float(parts[1])

mem_total = mem.get('MemTotal', 0.0)
mem_avail = mem.get('MemAvailable', 0.0)
mem_used = max(0.0, mem_total - mem_avail)
memory_percent = (mem_used / mem_total * 100.0) if mem_total else None

power_watts = None
if energy_path and energy1 is not None:
    try:
        with open(energy_path, 'r') as f:
            energy2 = int(f.read().strip())
        delta = energy2 - energy1
        elapsed = max(0.001, t2 - t1)
        if delta >= 0:
            power_watts = (float(delta) / 1000000.0) / elapsed
    except Exception:
        power_watts = None

def read_processes():
    rows = []
    try:
        out = subprocess.check_output(
            ['ps', '-eo', 'pid,user,comm,pcpu,rss,stat', '--sort=-pcpu'],
            stderr=subprocess.DEVNULL,
            text=True
        )
        for line in out.splitlines()[1:41]:
            parts = line.split(None, 5)
            if len(parts) < 6:
                continue
            pid, user, name, pcpu, rss, stat = parts
            try:
                cpu = float(pcpu)
            except Exception:
                cpu = 0.0
            try:
                memory_kb = float(rss)
            except Exception:
                memory_kb = 0.0
            rows.append({
                'pid': pid,
                'user': user,
                'name': name,
                'cpu_percent': cpu,
                'memory_kb': memory_kb,
                'status': stat[:1] if stat else ''
            })
    except Exception:
        pass
    return rows

print(json.dumps({
    'cpu_percent': cpu_percent,
    'memory_percent': memory_percent,
    'memory_used_mb': mem_used / 1024.0,
    'memory_total_mb': mem_total / 1024.0,
    'power_watts': power_watts,
    'processes': read_processes()
}))
";
    }

    public sealed class LmStudioService : IDisposable
    {
        private LmsLogTailer _tailer = new LmsLogTailer();

        public LmStudioSnapshot Read(MonitorSettings settings)
        {
            _tailer.EnsureStarted();
            var tail = _tailer.GetSnapshot();
            var snapshot = new LmStudioSnapshot();
            snapshot.ServerOnline = false;
            snapshot.ActiveModel = string.Empty;
            snapshot.Source = tail.Source;
            snapshot.TokensPerSecond = tail.TokensPerSecond;
            snapshot.SessionInputTokens = tail.SessionInputTokens;
            snapshot.SessionOutputTokens = tail.SessionOutputTokens;
            snapshot.IsProcessing = null;

            var api = ReadModelsApi(settings);
            if (api != null)
            {
                snapshot.ServerOnline = true;
                snapshot.ActiveModel = api.ActiveModel;
                snapshot.LoadedModelCount = api.LoadedModelCount;
                snapshot.Source = CombineSource(api.Source, tail.Source);
            }
            else
            {
                snapshot.Error = "API OFFLINE";
            }

            var ps = ReadLmsPs();
            if (ps.HasValue)
            {
                snapshot.IsProcessing = ps.Value;
                snapshot.Source = CombineSource(snapshot.Source, "lms ps");
            }

            if (!snapshot.IsProcessing.HasValue && tail.ObservedStats)
            {
                snapshot.IsProcessing = false;
            }

            return snapshot;
        }

        public void ResetLogTailer()
        {
            if (_tailer != null)
            {
                _tailer.Dispose();
            }
            _tailer = new LmsLogTailer();
        }

        public void Dispose()
        {
            if (_tailer != null)
            {
                _tailer.Dispose();
                _tailer = null;
            }
        }

        private static string CombineSource(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a))
            {
                return string.IsNullOrWhiteSpace(b) ? "API" : b;
            }
            if (string.IsNullOrWhiteSpace(b) || a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return a;
            }
            return a + " + " + b;
        }

        private LmModelsResult ReadModelsApi(MonitorSettings settings)
        {
            var baseUrl = (settings.LmStudioBaseUrl ?? "http://localhost:1234").Trim().TrimEnd('/');
            var v1 = RequestJson(baseUrl + "/api/v1/models", settings.LmStudioApiToken);
            if (!string.IsNullOrWhiteSpace(v1))
            {
                var parsed = ParseModelsV1(v1);
                if (parsed != null)
                {
                    parsed.Source = "api/v1";
                    return parsed;
                }
            }

            var v0 = RequestJson(baseUrl + "/api/v0/models", settings.LmStudioApiToken);
            if (!string.IsNullOrWhiteSpace(v0))
            {
                var parsed = ParseModelsV0(v0);
                if (parsed != null)
                {
                    parsed.Source = "api/v0";
                    return parsed;
                }
            }
            return null;
        }

        private static string RequestJson(string url, string token)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 1400;
                request.ReadWriteTimeout = 1400;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token.Trim();
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                return null;
            }
        }

        private static LmModelsResult ParseModelsV1(string json)
        {
            try
            {
                var root = JsonHelper.Parse(json) as Dictionary<string, object>;
                if (root == null)
                {
                    return null;
                }
                var models = JsonHelper.GetArray(root, "models");
                if (models == null)
                {
                    return null;
                }

                var result = new LmModelsResult();
                foreach (var item in models)
                {
                    var model = item as Dictionary<string, object>;
                    if (model == null)
                    {
                        continue;
                    }
                    var loaded = JsonHelper.GetArray(model, "loaded_instances");
                    if (loaded != null && loaded.Length > 0)
                    {
                        result.LoadedModelCount += loaded.Length;
                        if (string.IsNullOrWhiteSpace(result.ActiveModel))
                        {
                            result.ActiveModel = JsonHelper.GetString(model, "display_name");
                            if (string.IsNullOrWhiteSpace(result.ActiveModel))
                            {
                                result.ActiveModel = JsonHelper.GetString(model, "key");
                            }
                        }
                    }
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static LmModelsResult ParseModelsV0(string json)
        {
            try
            {
                var root = JsonHelper.Parse(json) as Dictionary<string, object>;
                if (root == null)
                {
                    return null;
                }
                var models = JsonHelper.GetArray(root, "data");
                if (models == null)
                {
                    return null;
                }

                var result = new LmModelsResult();
                foreach (var item in models)
                {
                    var model = item as Dictionary<string, object>;
                    if (model == null)
                    {
                        continue;
                    }
                    var state = JsonHelper.GetString(model, "state");
                    if (state != null && state.IndexOf("loaded", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        result.LoadedModelCount++;
                        if (string.IsNullOrWhiteSpace(result.ActiveModel))
                        {
                            result.ActiveModel = JsonHelper.GetString(model, "id");
                        }
                    }
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        private bool? ReadLmsPs()
        {
            var result = ProcessRunner.Run("lms.exe", new[] { "ps", "--json" }, 1300, null);
            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
            {
                return null;
            }

            try
            {
                var root = JsonHelper.Parse(result.StdOut);
                return JsonHelper.FindProcessingStatus(root);
            }
            catch
            {
                return null;
            }
        }
    }

    public sealed class LmsLogTailer : IDisposable
    {
        private Process _process;
        private DateTime _nextStartAttemptUtc = DateTime.MinValue;
        private readonly object _gate = new object();
        private double? _tokensPerSecond;
        private long _sessionInputTokens;
        private long _sessionOutputTokens;
        private bool _observedStats;
        private string _source = "no stats";

        public void EnsureStarted()
        {
            lock (_gate)
            {
                if (_process != null && !_process.HasExited)
                {
                    return;
                }
                if (DateTime.UtcNow < _nextStartAttemptUtc)
                {
                    return;
                }
                _nextStartAttemptUtc = DateTime.UtcNow.AddSeconds(20);
            }

            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = "lms.exe";
                psi.Arguments = ProcessRunner.BuildArguments(new[] { "log", "stream", "--source", "model", "--filter", "output", "--json", "--stats" });
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;

                var process = new Process();
                process.StartInfo = psi;
                process.EnableRaisingEvents = true;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        ConsumeLine(e.Data);
                    }
                };
                process.ErrorDataReceived += delegate { };
                process.Exited += delegate
                {
                    lock (_gate)
                    {
                        _source = "lms log stopped";
                    }
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                lock (_gate)
                {
                    _process = process;
                    _source = "lms log";
                }
            }
            catch
            {
                lock (_gate)
                {
                    _source = "lms unavailable";
                    _nextStartAttemptUtc = DateTime.UtcNow.AddSeconds(30);
                }
            }
        }

        public LmsLogSnapshot GetSnapshot()
        {
            lock (_gate)
            {
                return new LmsLogSnapshot
                {
                    TokensPerSecond = _tokensPerSecond,
                    SessionInputTokens = _sessionInputTokens,
                    SessionOutputTokens = _sessionOutputTokens,
                    ObservedStats = _observedStats,
                    Source = _source
                };
            }
        }

        private void ConsumeLine(string line)
        {
            try
            {
                var root = JsonHelper.Parse(line);
                var tps = JsonHelper.FindNumber(root, "tokens_per_second");
                var input = JsonHelper.FindNumber(root, "input_tokens");
                var output = JsonHelper.FindNumber(root, "total_output_tokens");
                if (!output.HasValue)
                {
                    output = JsonHelper.FindNumber(root, "output_tokens");
                }

                lock (_gate)
                {
                    if (tps.HasValue)
                    {
                        _tokensPerSecond = tps;
                        _observedStats = true;
                    }
                    if (input.HasValue && input.Value >= 0)
                    {
                        _sessionInputTokens += (long)Math.Round(input.Value);
                        _observedStats = true;
                    }
                    if (output.HasValue && output.Value >= 0)
                    {
                        _sessionOutputTokens += (long)Math.Round(output.Value);
                        _observedStats = true;
                    }
                    if (_observedStats)
                    {
                        _source = "lms log stats";
                    }
                }
            }
            catch
            {
                var regex = new Regex(@"tokens[/_\s-]*sec(?:ond)?\D+([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
                var match = regex.Match(line);
                if (match.Success)
                {
                    double parsed;
                    if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    {
                        lock (_gate)
                        {
                            _tokensPerSecond = parsed;
                            _observedStats = true;
                            _source = "lms log text";
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        _process.Kill();
                    }
                }
                catch
                {
                }
                if (_process != null)
                {
                    _process.Dispose();
                    _process = null;
                }
            }
        }
    }

    public sealed class SettingsStore
    {
        private readonly string _path;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        public SettingsStore()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawMonitor");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _path = Path.Combine(dir, "settings.json");
        }

        public MonitorSettings Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var settings = _serializer.Deserialize<MonitorSettings>(json);
                    if (settings != null)
                    {
                        return settings.Normalize();
                    }
                }
            }
            catch
            {
            }
            return MonitorSettings.Default();
        }

        public void Save(MonitorSettings settings)
        {
            try
            {
                var json = _serializer.Serialize(settings.Normalize());
                File.WriteAllText(_path, json);
            }
            catch
            {
            }
        }
    }

    public sealed class MonitorSettings
    {
        public string UbuntuTarget { get; set; }
        public string UbuntuHost { get; set; }
        public int UbuntuPort { get; set; }
        public string UbuntuUser { get; set; }
        public string UbuntuPassword { get; set; }
        public string UbuntuKeyPath { get; set; }
        public int RefreshMs { get; set; }
        public bool AutoMode { get; set; }
        public string LmStudioBaseUrl { get; set; }
        public string LmStudioApiToken { get; set; }

        public static MonitorSettings Default()
        {
            return new MonitorSettings
            {
                UbuntuTarget = "gods@192.168.0.9",
                UbuntuHost = "192.168.0.9",
                UbuntuPort = 22,
                UbuntuUser = "gods",
                UbuntuPassword = "",
                UbuntuKeyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519"),
                RefreshMs = 1000,
                AutoMode = true,
                LmStudioBaseUrl = "http://localhost:1234",
                LmStudioApiToken = ""
            };
        }

        public MonitorSettings Normalize()
        {
            UbuntuTarget = UbuntuTarget ?? "";
            UbuntuPassword = UbuntuPassword ?? "";
            if (string.IsNullOrWhiteSpace(UbuntuTarget))
            {
                if (!string.IsNullOrWhiteSpace(UbuntuUser) && !string.IsNullOrWhiteSpace(UbuntuHost))
                {
                    UbuntuTarget = UbuntuUser + "@" + UbuntuHost + (UbuntuPort != 22 ? ":" + UbuntuPort.ToString(CultureInfo.InvariantCulture) : "");
                }
                else
                {
                    UbuntuTarget = "gods@192.168.0.9";
                }
            }
            ApplyUbuntuTarget();
            if (UbuntuPort <= 0 || UbuntuPort >= 65536)
            {
                UbuntuPort = 22;
            }
            if (RefreshMs < 100)
            {
                RefreshMs = 100;
            }
            if (RefreshMs > 2000)
            {
                RefreshMs = 2000;
            }
            if (RefreshMs % 100 != 0)
            {
                RefreshMs = (int)(Math.Round(RefreshMs / 100.0) * 100);
            }
            if (LmStudioBaseUrl == null || LmStudioBaseUrl.Trim().Length == 0)
            {
                LmStudioBaseUrl = "http://localhost:1234";
            }
            UbuntuHost = UbuntuHost ?? "";
            UbuntuUser = UbuntuUser ?? "";
            UbuntuKeyPath = UbuntuKeyPath ?? "";
            LmStudioApiToken = LmStudioApiToken ?? "";
            return this;
        }

        public void ApplyUbuntuTarget()
        {
            var target = (UbuntuTarget ?? "").Trim();
            if (target.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                target = target.Substring("ssh://".Length);
            }
            target = target.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            var user = UbuntuUser;
            var hostPort = target;
            var atParts = target.Split('@');
            if (atParts.Length >= 2)
            {
                user = atParts[0].Trim();
                hostPort = atParts[atParts.Length - 1].Trim();
            }

            var port = UbuntuPort;
            var host = hostPort;
            var colon = hostPort.LastIndexOf(':');
            if (colon > 0 && colon < hostPort.Length - 1 && hostPort.IndexOf(']') < 0)
            {
                int parsedPort;
                if (int.TryParse(hostPort.Substring(colon + 1), out parsedPort) && parsedPort > 0 && parsedPort < 65536)
                {
                    port = parsedPort;
                    host = hostPort.Substring(0, colon);
                }
            }

            UbuntuTarget = (string.IsNullOrWhiteSpace(user) ? "gods" : user) + "@" + (string.IsNullOrWhiteSpace(host) ? "192.168.0.9" : host) + (port != 22 ? ":" + port.ToString(CultureInfo.InvariantCulture) : "");
            UbuntuUser = string.IsNullOrWhiteSpace(user) ? "gods" : user;
            UbuntuHost = string.IsNullOrWhiteSpace(host) ? "192.168.0.9" : host;
            UbuntuPort = port <= 0 ? 22 : port;
        }

        public MonitorSettings Clone()
        {
            return new MonitorSettings
            {
                UbuntuTarget = UbuntuTarget,
                UbuntuHost = UbuntuHost,
                UbuntuPort = UbuntuPort,
                UbuntuUser = UbuntuUser,
                UbuntuPassword = UbuntuPassword,
                UbuntuKeyPath = UbuntuKeyPath,
                RefreshMs = RefreshMs,
                AutoMode = AutoMode,
                LmStudioBaseUrl = LmStudioBaseUrl,
                LmStudioApiToken = LmStudioApiToken
            };
        }
    }

    public sealed class LocalSnapshot
    {
        public DateTime CapturedAt { get; set; }
        public double? CpuPercent { get; set; }
        public double? CpuPackagePowerWatts { get; set; }
        public int LogicalProcessorCount { get; set; }
        public double? MemoryPercent { get; set; }
        public ulong MemoryTotalBytes { get; set; }
        public ulong MemoryAvailableBytes { get; set; }
        public ulong MemoryUsedBytes { get; set; }
        public bool GpuAvailable { get; set; }
        public double? GpuUtilizationPercent { get; set; }
        public double? GpuTemperatureCelsius { get; set; }
        public double? GpuMemoryUsedMb { get; set; }
        public double? GpuMemoryTotalMb { get; set; }
        public double? GpuPowerWatts { get; set; }
    }

    public sealed class UbuntuSnapshot
    {
        public bool Online { get; set; }
        public string Error { get; set; }
        public double LatencyMs { get; set; }
        public double? CpuPercent { get; set; }
        public double? MemoryPercent { get; set; }
        public double? MemoryUsedMb { get; set; }
        public double? MemoryTotalMb { get; set; }
        public double? PowerWatts { get; set; }
        public List<ProcessRow> Processes { get; set; }

        public UbuntuSnapshot()
        {
            Processes = new List<ProcessRow>();
        }
    }

    public sealed class ProcessRow
    {
        public string Pid { get; set; }
        public string Name { get; set; }
        public string User { get; set; }
        public double CpuPercent { get; set; }
        public long MemoryBytes { get; set; }
        public string MemoryText { get; set; }
        public string Status { get; set; }
    }

    public sealed class LmStudioSnapshot
    {
        public bool ServerOnline { get; set; }
        public string Error { get; set; }
        public string ActiveModel { get; set; }
        public int LoadedModelCount { get; set; }
        public bool? IsProcessing { get; set; }
        public double? TokensPerSecond { get; set; }
        public long SessionInputTokens { get; set; }
        public long SessionOutputTokens { get; set; }
        public string Source { get; set; }
    }

    public sealed class LmModelsResult
    {
        public string ActiveModel { get; set; }
        public int LoadedModelCount { get; set; }
        public string Source { get; set; }
    }

    public sealed class LmsLogSnapshot
    {
        public double? TokensPerSecond { get; set; }
        public long SessionInputTokens { get; set; }
        public long SessionOutputTokens { get; set; }
        public bool ObservedStats { get; set; }
        public string Source { get; set; }
    }

    public static class JsonHelper
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static object Parse(string json)
        {
            return Serializer.DeserializeObject(json);
        }

        public static double? GetDouble(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null)
            {
                return null;
            }
            return ToDouble(dict[key]);
        }

        public static string GetString(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null)
            {
                return null;
            }
            return Convert.ToString(dict[key], CultureInfo.InvariantCulture);
        }

        public static object[] GetArray(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null)
            {
                return null;
            }
            return dict[key] as object[];
        }

        public static double? FindNumber(object root, string key)
        {
            var normalized = NormalizeKey(key);
            return FindNumberInternal(root, normalized);
        }

        private static double? FindNumberInternal(object root, string normalizedKey)
        {
            if (root == null)
            {
                return null;
            }
            var dict = root as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (var pair in dict)
                {
                    var key = NormalizeKey(pair.Key);
                    if (key == normalizedKey || key.EndsWith(normalizedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        var value = ToDouble(pair.Value);
                        if (value.HasValue)
                        {
                            return value;
                        }
                    }
                    var nested = FindNumberInternal(pair.Value, normalizedKey);
                    if (nested.HasValue)
                    {
                        return nested;
                    }
                }
            }
            var array = root as object[];
            if (array != null)
            {
                foreach (var item in array)
                {
                    var nested = FindNumberInternal(item, normalizedKey);
                    if (nested.HasValue)
                    {
                        return nested;
                    }
                }
            }
            return null;
        }

        public static bool? FindProcessingStatus(object root)
        {
            bool foundIdle = false;
            bool foundActive = FindProcessingInternal(root, ref foundIdle);
            if (foundActive)
            {
                return true;
            }
            if (foundIdle)
            {
                return false;
            }
            return null;
        }

        private static bool FindProcessingInternal(object root, ref bool foundIdle)
        {
            if (root == null)
            {
                return false;
            }

            var dict = root as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (var pair in dict)
                {
                    var normalizedKey = NormalizeKey(pair.Key);
                    var number = ToDouble(pair.Value);
                    if (normalizedKey.IndexOf("queued", StringComparison.OrdinalIgnoreCase) >= 0 && number.HasValue && number.Value > 0)
                    {
                        return true;
                    }

                    var text = pair.Value as string;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var lower = text.ToLowerInvariant();
                        if ((normalizedKey.IndexOf("status", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             normalizedKey.IndexOf("generation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             normalizedKey.IndexOf("prediction", StringComparison.OrdinalIgnoreCase) >= 0) &&
                            (lower.IndexOf("generat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             lower.IndexOf("process", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             lower.IndexOf("predict", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             lower.IndexOf("busy", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            return true;
                        }
                        if (lower.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            foundIdle = true;
                        }
                    }

                    if (FindProcessingInternal(pair.Value, ref foundIdle))
                    {
                        return true;
                    }
                }
            }

            var array = root as object[];
            if (array != null)
            {
                foreach (var item in array)
                {
                    if (FindProcessingInternal(item, ref foundIdle))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static double? ToDouble(object value)
        {
            if (value == null)
            {
                return null;
            }
            if (value is double)
            {
                return (double)value;
            }
            if (value is float)
            {
                return (float)value;
            }
            if (value is int)
            {
                return (int)value;
            }
            if (value is long)
            {
                return (long)value;
            }
            if (value is decimal)
            {
                return (double)(decimal)value;
            }
            double parsed;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : (double?)null;
        }

        private static string NormalizeKey(string key)
        {
            if (key == null)
            {
                return "";
            }
            var sb = new StringBuilder();
            foreach (var ch in key)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
            }
            return sb.ToString();
        }
    }

    public static class ProcessRunner
    {
        public static ProcessResult Run(string fileName, IEnumerable<string> arguments, int timeoutMs, string stdin)
        {
            var result = new ProcessResult();
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = fileName;
                psi.Arguments = BuildArguments(arguments);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = stdin != null;
                psi.CreateNoWindow = true;

                using (var process = new Process())
                {
                    process.StartInfo = psi;
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                        {
                            stdout.AppendLine(e.Data);
                        }
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                        {
                            stderr.AppendLine(e.Data);
                        }
                    };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (stdin != null)
                    {
                        process.StandardInput.Write(stdin);
                        process.StandardInput.Close();
                    }

                    if (!process.WaitForExit(timeoutMs))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }
                        result.TimedOut = true;
                        result.ErrorMessage = "TIMEOUT";
                    }
                    else
                    {
                        result.ExitCode = process.ExitCode;
                    }
                }

                result.StdOut = stdout.ToString();
                result.StdErr = stderr.ToString();
                result.Success = !result.TimedOut && result.ExitCode == 0;
                return result;
            }
            catch (Exception ex)
            {
                result.StdOut = stdout.ToString();
                result.StdErr = stderr.ToString();
                result.ErrorMessage = ex.Message;
                result.Success = false;
                return result;
            }
        }

        public static string BuildArguments(IEnumerable<string> args)
        {
            if (args == null)
            {
                return string.Empty;
            }
            return string.Join(" ", args.Select(QuoteArgument).ToArray());
        }

        private static string QuoteArgument(string arg)
        {
            if (arg == null)
            {
                return "\"\"";
            }
            if (arg.Length == 0)
            {
                return "\"\"";
            }
            if (arg.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '"' }) < 0)
            {
                return arg;
            }

            var sb = new StringBuilder();
            sb.Append('"');
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                    continue;
                }
                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }
                sb.Append(c);
            }
            if (backslashes > 0)
            {
                sb.Append('\\', backslashes * 2);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    public sealed class ProcessResult
    {
        public bool Success { get; set; }
        public bool TimedOut { get; set; }
        public int ExitCode { get; set; }
        public string StdOut { get; set; }
        public string StdErr { get; set; }
        public string ErrorMessage { get; set; }
    }

    public static class UiFactory
    {
        public static Button SmallButton(string text)
        {
            var button = TextButton(text);
            button.Width = 32;
            button.Height = 28;
            button.FontSize = 16;
            button.Margin = new Thickness(2, 0, 2, 0);
            return button;
        }

        public static Button TinyButton(string text)
        {
            var button = TextButton(text);
            button.Width = 36;
            button.Height = 24;
            button.FontSize = 12;
            button.Margin = new Thickness(6, 2, 0, 2);
            return button;
        }

        public static Button TextButton(string text)
        {
            var button = new Button();
            button.Content = text;
            button.FontFamily = Theme.MonoFont;
            button.FontWeight = FontWeights.Bold;
            button.FontSize = 12;
            button.Foreground = Theme.TextBrush;
            button.Background = Theme.InnerPanelBrush;
            button.BorderBrush = Theme.BorderBrush;
            button.BorderThickness = new Thickness(1);
            button.Padding = new Thickness(10, 3, 10, 3);
            button.MinHeight = 26;
            button.Cursor = System.Windows.Input.Cursors.Hand;
            return button;
        }

        public static TextBox InputBox()
        {
            var box = new TextBox();
            box.FontFamily = Theme.MonoFont;
            box.FontSize = 12;
            box.Foreground = Theme.TextBrush;
            box.Background = Theme.ChartBackgroundBrush;
            box.BorderBrush = Theme.DarkBorderBrush;
            box.BorderThickness = new Thickness(1);
            box.Padding = new Thickness(5, 2, 5, 2);
            box.Margin = new Thickness(0, 2, 0, 2);
            box.MinHeight = 24;
            return box;
        }

        public static PasswordBox SecretInputBox()
        {
            var box = new PasswordBox();
            box.FontFamily = Theme.MonoFont;
            box.FontSize = 12;
            box.Foreground = Theme.TextBrush;
            box.Background = Theme.ChartBackgroundBrush;
            box.BorderBrush = Theme.DarkBorderBrush;
            box.BorderThickness = new Thickness(1);
            box.Padding = new Thickness(5, 2, 5, 2);
            box.Margin = new Thickness(0, 2, 0, 2);
            box.MinHeight = 24;
            return box;
        }
    }

    public static class Format
    {
        public static string Percent(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "N/A";
        }

        public static string Watts(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) + "W" : "N/A";
        }

        public static string Temperature(double? value)
        {
            return value.HasValue ? value.Value.ToString("0", CultureInfo.InvariantCulture) + "C" : "N/A";
        }

        public static string Gigabytes(ulong bytes)
        {
            if (bytes == 0)
            {
                return "N/A";
            }
            return ((double)bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture);
        }

        public static string Megabytes(long bytes)
        {
            if (bytes <= 0)
            {
                return "0B";
            }
            var mb = (double)bytes / 1024.0 / 1024.0;
            if (mb >= 1024)
            {
                return (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + "G";
            }
            return mb.ToString("0.0", CultureInfo.InvariantCulture) + "M";
        }

        public static string MemoryPairMb(double? used, double? total)
        {
            if (!used.HasValue || !total.HasValue || total.Value <= 0)
            {
                return "N/A";
            }
            return used.Value.ToString("0", CultureInfo.InvariantCulture) + "/" + total.Value.ToString("0", CultureInfo.InvariantCulture);
        }

        public static string Number(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "N/A";
        }

        public static string Processing(bool? processing)
        {
            if (!processing.HasValue)
            {
                return "UNKNOWN";
            }
            return processing.Value ? "YES" : "IDLE";
        }

        public static string TokenPair(long input, long output)
        {
            return input.ToString(CultureInfo.InvariantCulture) + "/" + output.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static class Theme
    {
        public static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas, Lucida Console");
        public static readonly SolidColorBrush Background = Brush("#000000");
        public static readonly SolidColorBrush HeaderBrush = Brush("#020303");
        public static readonly SolidColorBrush PanelBrush = Brush("#050607");
        public static readonly SolidColorBrush InnerPanelBrush = Brush("#071012");
        public static readonly SolidColorBrush ChartBackgroundBrush = Brush("#020404");
        public static readonly SolidColorBrush TextBrush = Brush("#E8FFF9");
        public static readonly SolidColorBrush ValueBrush = Brush("#B8FFF4");
        public static readonly SolidColorBrush MutedBrush = Brush("#6E8588");
        public static readonly SolidColorBrush BorderBrush = Brush("#1F6A73");
        public static readonly SolidColorBrush DarkBorderBrush = Brush("#12353A");
        public static readonly SolidColorBrush DotBrush = Brush("#123034");
        public static readonly SolidColorBrush SegmentOffBrush = Brush("#0D181A");
        public static readonly SolidColorBrush CyanBrush = Brush("#2DD4BF");
        public static readonly SolidColorBrush GreenBrush = Brush("#A3E635");
        public static readonly SolidColorBrush MutedGreenBrush = Brush("#76A98A");
        public static readonly SolidColorBrush YellowBrush = Brush("#FACC15");
        public static readonly SolidColorBrush RedBrush = Brush("#FB7185");
        public static readonly SolidColorBrush MagentaBrush = Brush("#F472B6");
        public static readonly SolidColorBrush BlueBrush = Brush("#60A5FA");
        public static readonly SolidColorBrush OrangeBrush = Brush("#F97316");

        public static SolidColorBrush Brush(string hex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public static Brush PercentBrush(double? value)
        {
            if (!value.HasValue)
            {
                return MutedBrush;
            }
            if (value.Value >= 90)
            {
                return RedBrush;
            }
            if (value.Value >= 70)
            {
                return YellowBrush;
            }
            return GreenBrush;
        }

        public static Brush TempBrush(double? value)
        {
            if (!value.HasValue)
            {
                return MutedBrush;
            }
            if (value.Value >= 84)
            {
                return RedBrush;
            }
            if (value.Value >= 72)
            {
                return YellowBrush;
            }
            return CyanBrush;
        }

        public static Brush SegmentBrush(double position)
        {
            if (position >= 0.88)
            {
                return RedBrush;
            }
            if (position >= 0.66)
            {
                return YellowBrush;
            }
            if (position >= 0.42)
            {
                return GreenBrush;
            }
            return CyanBrush;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public sealed class MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MemoryStatusEx()
        {
            dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
        }
    }

    public static class NativeMethods
    {
        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);
    }
}
