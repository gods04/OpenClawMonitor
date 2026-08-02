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
using Microsoft.Win32;

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
        private readonly int[] _refreshSteps = new[] { 500, 1000, 2000, 5000, 10000 };
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

        private TextBlock _intervalText;
        private TextBlock _effectiveText;
        private TextBlock _clockText;
        private TextBlock _statusText;
        private CheckBox _autoCheckBox;

        private TextBox _ubuntuHostBox;
        private TextBox _ubuntuPortBox;
        private TextBox _ubuntuUserBox;
        private TextBox _ubuntuKeyBox;
        private TextBox _lmUrlBox;
        private TextBox _lmTokenBox;

        public MainWindow()
        {
            _settingsStore = new SettingsStore();
            _settings = _settingsStore.Load();
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
            PopulateSettingsFields();
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
        }

        private UIElement BuildLayout()
        {
            var root = new DockPanel();
            root.Background = Theme.Background;

            var topBar = BuildTopBar();
            DockPanel.SetDock(topBar, Dock.Top);
            root.Children.Add(topBar);

            var main = new Grid();
            main.Margin = new Thickness(10, 0, 10, 10);
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.65, GridUnitType.Star) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });

            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
            left.Margin = new Thickness(0, 0, 6, 0);

            _cpuPanel = new MetricPanel("WINDOWS CPU", Theme.CyanBrush);
            _memoryPanel = new MetricPanel("WINDOWS MEMORY", Theme.GreenBrush);
            _gpuPanel = new MetricPanel("NVIDIA GPU", Theme.MagentaBrush);
            Grid.SetRow(_cpuPanel, 0);
            Grid.SetRow(_memoryPanel, 1);
            Grid.SetRow(_gpuPanel, 2);
            left.Children.Add(_cpuPanel);
            left.Children.Add(_memoryPanel);
            left.Children.Add(_gpuPanel);

            var right = new Grid();
            right.Margin = new Thickness(6, 0, 0, 0);
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.05, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.05, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.95, GridUnitType.Star) });

            _ubuntuPanel = new MetricPanel("UBUNTU LAN", Theme.YellowBrush);
            _lmPanel = new MetricPanel("LM STUDIO", Theme.BlueBrush);
            var settingsPanel = BuildSettingsPanel();
            Grid.SetRow(_ubuntuPanel, 0);
            Grid.SetRow(_lmPanel, 1);
            Grid.SetRow(settingsPanel, 2);
            right.Children.Add(_ubuntuPanel);
            right.Children.Add(_lmPanel);
            right.Children.Add(settingsPanel);

            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 1);
            main.Children.Add(left);
            main.Children.Add(right);
            root.Children.Add(main);

            return root;
        }

        private UIElement BuildTopBar()
        {
            var bar = new Grid();
            bar.Height = 50;
            bar.Margin = new Thickness(10, 8, 10, 6);
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel();
            titleStack.Orientation = Orientation.Vertical;
            titleStack.VerticalAlignment = VerticalAlignment.Center;

            var title = new TextBlock();
            title.Text = "OPENCLAW MONITOR";
            title.FontSize = 18;
            title.FontWeight = FontWeights.Bold;
            title.Foreground = Theme.TextBrush;
            titleStack.Children.Add(title);

            _statusText = new TextBlock();
            _statusText.Text = "BOOT";
            _statusText.FontSize = 11;
            _statusText.Foreground = Theme.MutedBrush;
            titleStack.Children.Add(_statusText);

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

            Grid.SetColumn(titleStack, 0);
            Grid.SetColumn(center, 1);
            Grid.SetColumn(controls, 2);
            bar.Children.Add(titleStack);
            bar.Children.Add(center);
            bar.Children.Add(controls);
            return bar;
        }

        private UIElement BuildSettingsPanel()
        {
            var panel = new Border();
            panel.Margin = new Thickness(0, 6, 0, 0);
            panel.Padding = new Thickness(10);
            panel.BorderThickness = new Thickness(1);
            panel.BorderBrush = Theme.BorderBrush;
            panel.Background = Theme.PanelBrush;
            panel.SnapsToDevicePixels = true;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock();
            title.Text = "SSH / API CONFIG";
            title.FontSize = 13;
            title.FontWeight = FontWeights.Bold;
            title.Foreground = Theme.YellowBrush;
            title.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(title, 0);
            Grid.SetColumnSpan(title, 3);
            grid.Children.Add(title);

            _ubuntuHostBox = AddSettingRow(grid, 1, "HOST");
            _ubuntuPortBox = AddSettingRow(grid, 2, "PORT");
            _ubuntuUserBox = AddSettingRow(grid, 3, "USER");
            _ubuntuKeyBox = AddSettingRow(grid, 4, "KEY");

            var browse = UiFactory.TinyButton("...");
            browse.Click += delegate { BrowseKeyFile(); };
            Grid.SetRow(browse, 4);
            Grid.SetColumn(browse, 2);
            grid.Children.Add(browse);

            _lmUrlBox = AddSettingRow(grid, 5, "LM API");
            _lmTokenBox = AddSettingRow(grid, 6, "TOKEN");

            var actions = new StackPanel();
            actions.Orientation = Orientation.Horizontal;
            actions.HorizontalAlignment = HorizontalAlignment.Right;
            actions.Margin = new Thickness(0, 8, 0, 0);
            var save = UiFactory.TextButton("SAVE");
            save.Click += delegate { SaveSettingsFromUi(); };
            actions.Children.Add(save);
            var ping = UiFactory.TextButton("TEST");
            ping.Margin = new Thickness(8, 0, 0, 0);
            ping.Click += delegate { ForceRemotePoll(); };
            actions.Children.Add(ping);
            Grid.SetRow(actions, 7);
            Grid.SetColumnSpan(actions, 3);
            grid.Children.Add(actions);

            panel.Child = grid;
            return panel;
        }

        private TextBox AddSettingRow(Grid grid, int row, string labelText)
        {
            var label = new TextBlock();
            label.Text = labelText;
            label.FontSize = 11;
            label.Foreground = Theme.MutedBrush;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 3, 6, 3);
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var box = UiFactory.InputBox();
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            return box;
        }

        private void PopulateSettingsFields()
        {
            _ubuntuHostBox.Text = _settings.UbuntuHost;
            _ubuntuPortBox.Text = _settings.UbuntuPort.ToString(CultureInfo.InvariantCulture);
            _ubuntuUserBox.Text = _settings.UbuntuUser;
            _ubuntuKeyBox.Text = _settings.UbuntuKeyPath;
            _lmUrlBox.Text = _settings.LmStudioBaseUrl;
            _lmTokenBox.Text = _settings.LmStudioApiToken;
        }

        private void BrowseKeyFile()
        {
            var dialog = new OpenFileDialog();
            dialog.Title = "Select SSH key";
            dialog.CheckFileExists = true;
            dialog.Filter = "SSH keys|id_*;*.pem;*.key;*.*";
            if (!string.IsNullOrWhiteSpace(_ubuntuKeyBox.Text))
            {
                var dir = Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(_ubuntuKeyBox.Text));
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    dialog.InitialDirectory = dir;
                }
            }
            if (dialog.ShowDialog(this) == true)
            {
                _ubuntuKeyBox.Text = dialog.FileName;
            }
        }

        private void SaveSettingsFromUi()
        {
            int port;
            _settings.UbuntuHost = (_ubuntuHostBox.Text ?? string.Empty).Trim();
            _settings.UbuntuUser = (_ubuntuUserBox.Text ?? string.Empty).Trim();
            _settings.UbuntuKeyPath = (_ubuntuKeyBox.Text ?? string.Empty).Trim();
            if (int.TryParse((_ubuntuPortBox.Text ?? string.Empty).Trim(), out port) && port > 0 && port < 65536)
            {
                _settings.UbuntuPort = port;
            }
            else
            {
                _ubuntuPortBox.Text = _settings.UbuntuPort.ToString(CultureInfo.InvariantCulture);
            }

            _settings.LmStudioBaseUrl = (_lmUrlBox.Text ?? string.Empty).Trim();
            _settings.LmStudioApiToken = (_lmTokenBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(_settings.LmStudioBaseUrl))
            {
                _settings.LmStudioBaseUrl = "http://localhost:1234";
                _lmUrlBox.Text = _settings.LmStudioBaseUrl;
            }

            _settingsStore.Save(_settings);
            _nextUbuntuPollUtc = DateTime.MinValue;
            _nextLmPollUtc = DateTime.MinValue;
            _lmStudioService.ResetLogTailer();
            SetStatus("CONFIG SAVED");
        }

        private void ChangeRefreshStep(int delta)
        {
            var index = 0;
            for (int i = 0; i < _refreshSteps.Length; i++)
            {
                if (_refreshSteps[i] == _settings.RefreshMs)
                {
                    index = i;
                    break;
                }
            }
            index = Math.Max(0, Math.Min(_refreshSteps.Length - 1, index + delta));
            _settings.RefreshMs = _refreshSteps[index];
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
                return _lastUbuntuOnline ? _settings.RefreshMs : Math.Max(5000, _settings.RefreshMs);
            }
            if (!_isWindowActive || !_lastUbuntuOnline)
            {
                return Math.Max(10000, _settings.RefreshMs * 5);
            }
            return _settings.RefreshMs;
        }

        private int GetLmPollMs()
        {
            if (!_settings.AutoMode)
            {
                return Math.Max(1000, _settings.RefreshMs);
            }
            if (!_isWindowActive)
            {
                return 5000;
            }
            return Math.Max(1000, _settings.RefreshMs);
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
            SaveSettingsFromUi();
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
            _cpuPanel.SetMetric(1, "PKG W", Format.Watts(snapshot.CpuPackagePowerWatts), "POWER", Theme.ValueBrush);
            _cpuPanel.SetMetric(2, "CORES", snapshot.LogicalProcessorCount.ToString(CultureInfo.InvariantCulture), "LOGICAL", Theme.TextBrush);
            _cpuPanel.SetMetric(3, "SAMPLE", snapshot.CapturedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture), "LOCAL", Theme.MutedBrush);
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
        }

        private void ApplyUbuntuSnapshot(UbuntuSnapshot snapshot)
        {
            _ubuntuPanel.SetStatus(snapshot.Online ? "ONLINE" : "OFFLINE", snapshot.Online ? Theme.GreenBrush : Theme.RedBrush);
            _ubuntuPanel.SetMetric(0, "CPU", Format.Percent(snapshot.CpuPercent), "REMOTE", Theme.PercentBrush(snapshot.CpuPercent));
            _ubuntuPanel.SetMetric(1, "MEM", Format.Percent(snapshot.MemoryPercent), Format.MemoryPairMb(snapshot.MemoryUsedMb, snapshot.MemoryTotalMb), Theme.PercentBrush(snapshot.MemoryPercent));
            _ubuntuPanel.SetMetric(2, "POWER", Format.Watts(snapshot.PowerWatts), "RAPL", Theme.ValueBrush);
            _ubuntuPanel.SetMetric(3, "RTT", snapshot.Online ? snapshot.LatencyMs.ToString("0", CultureInfo.InvariantCulture) + "ms" : "N/A", ShortError(snapshot.Error), snapshot.Online ? Theme.TextBrush : Theme.RedBrush);
            _ubuntuPanel.AddSample(snapshot.CpuPercent);
            _ubuntuPanel.SetBar(snapshot.CpuPercent);
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
            Margin = new Thickness(0, 0, 0, 6);
            Padding = new Thickness(10);
            BorderThickness = new Thickness(1);
            BorderBrush = Theme.BorderBrush;
            Background = Theme.PanelBrush;
            SnapsToDevicePixels = true;

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
            _sparkline.MinHeight = 56;
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
            BorderThickness = new Thickness(1);
            BorderBrush = Theme.DarkBorderBrush;
            Background = Theme.InnerPanelBrush;
            Padding = new Thickness(6, 5, 6, 5);
            MinHeight = 58;

            var stack = new StackPanel();
            stack.Orientation = Orientation.Vertical;

            _label = new TextBlock();
            _label.FontSize = 10;
            _label.Foreground = Theme.MutedBrush;
            _label.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(_label);

            _value = new TextBlock();
            _value.FontSize = 18;
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

            var step = w / Math.Max(1, Capacity - 1);
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                bool started = false;
                for (int i = 0; i < _samples.Count; i++)
                {
                    var sample = _samples[i];
                    if (!sample.HasValue)
                    {
                        started = false;
                        continue;
                    }

                    var x = (Capacity - _samples.Count + i) * step;
                    var y = h - (sample.Value / 100.0 * (h - 6.0)) - 3.0;
                    if (!started)
                    {
                        ctx.BeginFigure(new Point(x, y), false, false);
                        started = true;
                    }
                    else
                    {
                        ctx.LineTo(new Point(x, y), true, false);
                    }
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, _accentPen, geometry);

            var latest = last[last.Count - 1].Value;
            var latestX = (Capacity - 1) * step;
            var latestY = h - (latest / 100.0 * (h - 6.0)) - 3.0;
            dc.DrawRectangle(_accentBrush, null, new Rect(Math.Max(0, latestX - 2), latestY - 2, 4, 4));
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
                string.IsNullOrWhiteSpace(settings.UbuntuUser) ||
                string.IsNullOrWhiteSpace(settings.UbuntuKeyPath))
            {
                snapshot.Error = "CONFIG";
                return snapshot;
            }

            var keyPath = Environment.ExpandEnvironmentVariables(settings.UbuntuKeyPath);
            if (!File.Exists(keyPath))
            {
                snapshot.Error = "KEY MISSING";
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

            var json = ExtractJson(result.StdOut);
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
                snapshot.Error = string.Empty;
                return snapshot;
            }
            catch (Exception ex)
            {
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

        private const string RemotePython = @"
import json
import os
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

print(json.dumps({
    'cpu_percent': cpu_percent,
    'memory_percent': memory_percent,
    'memory_used_mb': mem_used / 1024.0,
    'memory_total_mb': mem_total / 1024.0,
    'power_watts': power_watts
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
        public string UbuntuHost { get; set; }
        public int UbuntuPort { get; set; }
        public string UbuntuUser { get; set; }
        public string UbuntuKeyPath { get; set; }
        public int RefreshMs { get; set; }
        public bool AutoMode { get; set; }
        public string LmStudioBaseUrl { get; set; }
        public string LmStudioApiToken { get; set; }

        public static MonitorSettings Default()
        {
            return new MonitorSettings
            {
                UbuntuHost = "",
                UbuntuPort = 22,
                UbuntuUser = "",
                UbuntuKeyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519"),
                RefreshMs = 1000,
                AutoMode = true,
                LmStudioBaseUrl = "http://localhost:1234",
                LmStudioApiToken = ""
            };
        }

        public MonitorSettings Normalize()
        {
            if (UbuntuPort <= 0 || UbuntuPort >= 65536)
            {
                UbuntuPort = 22;
            }
            if (RefreshMs != 500 && RefreshMs != 1000 && RefreshMs != 2000 && RefreshMs != 5000 && RefreshMs != 10000)
            {
                RefreshMs = 1000;
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

        public MonitorSettings Clone()
        {
            return new MonitorSettings
            {
                UbuntuHost = UbuntuHost,
                UbuntuPort = UbuntuPort,
                UbuntuUser = UbuntuUser,
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
