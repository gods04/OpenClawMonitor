using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
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

        private BtopCpuPanel _cpuPanel;
        private BtopMemoryPanel _memoryPanel;
        private BtopAuxPanel _auxPanel;
        private BtopNetPanel _netPanel;
        private ProcessPanel _processPanel;
        private Grid _bottomGrid;
        private Grid _leftResourceGrid;
        private UIElement _leftResourceGroup;
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
            ApplyMachineNames();
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
                _ubuntuService.Dispose();
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
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.42, GridUnitType.Star) });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.58, GridUnitType.Star) });

            var cpuGroup = BuildCpuGroup();
            _bottomGrid = new Grid();
            _bottomGrid.Margin = new Thickness(0, 8, 0, 0);
            _bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.82, GridUnitType.Star) });
            _bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            _bottomGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _leftResourceGroup = BuildLeftResourceGroup();
            _processGroup = BuildProcessGroup();
            Grid.SetColumn(_leftResourceGroup, 0);
            Grid.SetColumn(_processGroup, 1);
            _bottomGrid.Children.Add(_leftResourceGroup);
            _bottomGrid.Children.Add(_processGroup);

            Grid.SetRow(cpuGroup, 0);
            Grid.SetRow(_bottomGrid, 1);
            main.Children.Add(cpuGroup);
            main.Children.Add(_bottomGrid);

            root.Children.Add(main);
            ApplyResponsiveLayout();

            return root;
        }

        private UIElement BuildCpuGroup()
        {
            _cpuPanel = new BtopCpuPanel();
            var frame = new BtopFrame("1 cpu   menu preset *", Theme.GreenBrush);
            frame.SetContent(_cpuPanel);
            return frame;
        }

        private UIElement BuildLeftResourceGroup()
        {
            _leftResourceGrid = new Grid();
            _leftResourceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.48, GridUnitType.Star) });
            _leftResourceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.52, GridUnitType.Star) });

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.48, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.52, GridUnitType.Star) });

            _memoryPanel = new BtopMemoryPanel();
            var memFrame = new BtopFrame("2 mem", Theme.YellowBrush);
            memFrame.SetContent(_memoryPanel);

            _auxPanel = new BtopAuxPanel();
            var auxFrame = new BtopFrame("gpu / lm", Theme.GreenBrush);
            auxFrame.SetContent(_auxPanel);

            Grid.SetColumn(memFrame, 0);
            Grid.SetColumn(auxFrame, 1);
            top.Children.Add(memFrame);
            top.Children.Add(auxFrame);

            _netPanel = new BtopNetPanel();
            var netFrame = new BtopFrame("3 net   auto zero   <b local n>   <x remote n>", Theme.BlueBrush);
            netFrame.SetContent(_netPanel);

            Grid.SetRow(top, 0);
            Grid.SetRow(netFrame, 1);
            _leftResourceGrid.Children.Add(top);
            _leftResourceGrid.Children.Add(netFrame);
            return _leftResourceGrid;
        }

        private UIElement BuildProcessGroup()
        {
            _processPanel = new ProcessPanel();
            var frame = new BtopFrame("4 proc   filter        tree < cpu lazy >", Theme.RedMutedBrush);
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
            _footerText.Text = "Local Windows  |  CPU --  |  MEM --  |  GPU --  |  remote  |  CPU --  |  MEM --";
            border.Child = _footerText;
            return border;
        }

        private void ApplyResponsiveLayout()
        {
            var width = ActualWidth > 0 ? ActualWidth : Width;

            if (_bottomGrid != null && _leftResourceGroup != null && _processGroup != null)
            {
                _bottomGrid.ColumnDefinitions.Clear();
                _bottomGrid.RowDefinitions.Clear();
                if (width >= 1150)
                {
                    _bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.82, GridUnitType.Star) });
                    _bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
                    _bottomGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    Grid.SetColumn(_leftResourceGroup, 0);
                    Grid.SetRow(_leftResourceGroup, 0);
                    Grid.SetColumn(_processGroup, 1);
                    Grid.SetRow(_processGroup, 0);
                }
                else
                {
                    _bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    _bottomGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    _bottomGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    Grid.SetColumn(_leftResourceGroup, 0);
                    Grid.SetRow(_leftResourceGroup, 0);
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

        private void SaveSettingsValues(string remoteName, string remoteTarget, string password, string lmUrl)
        {
            _settings.RemoteDisplayName = CleanRemoteName(remoteName);
            _settings.UbuntuTarget = (remoteTarget ?? string.Empty).Trim();
            _settings.UbuntuPassword = password ?? string.Empty;
            _settings.ApplyUbuntuTarget();

            _settings.LmStudioBaseUrl = (lmUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(_settings.LmStudioBaseUrl))
            {
                _settings.LmStudioBaseUrl = "http://localhost:1234";
            }

            _settingsStore.Save(_settings);
            ApplyMachineNames();
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
                SaveSettingsValues(dialog.RemoteDisplayName, dialog.RemoteTarget, dialog.Password, dialog.LmStudioUrl);
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
                return _lastUbuntuOnline ? Math.Max(1000, _settings.RefreshMs) : Math.Max(3000, _settings.RefreshMs);
            }
            if (!_isWindowActive || !_lastUbuntuOnline)
            {
                return Math.Max(5000, _settings.RefreshMs * 5);
            }
            return Math.Max(1000, _settings.RefreshMs);
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
            _cpuPanel.SetLocal(snapshot);
            _memoryPanel.SetLocal(snapshot);
            _netPanel.SetLocal(snapshot);
            _auxPanel.SetLocal(snapshot);

            if (_processPanel != null)
            {
                _processPanel.RefreshLocalRows();
            }
            UpdateFooter(snapshot, null);
        }

        private void ApplyUbuntuSnapshot(UbuntuSnapshot snapshot)
        {
            UpdateRemoteSummary(snapshot.Online ? "ssh connected" : "ssh offline");
            _cpuPanel.SetUbuntu(snapshot);
            _memoryPanel.SetUbuntu(snapshot);
            _netPanel.SetUbuntu(snapshot);
            if (_processPanel != null)
            {
                _processPanel.SetUbuntuRows(snapshot.Processes, snapshot.Online, ShortError(snapshot.Error));
            }
            UpdateFooter(null, snapshot);
        }

        private void ApplyLmStudioSnapshot(LmStudioSnapshot snapshot)
        {
            _auxPanel.SetLm(snapshot);
        }

        private void ApplyMachineNames()
        {
            var remoteName = RemoteName();
            if (_cpuPanel != null)
            {
                _cpuPanel.SetRemoteName(remoteName);
            }
            if (_memoryPanel != null)
            {
                _memoryPanel.SetRemoteName(remoteName);
            }
            if (_netPanel != null)
            {
                _netPanel.SetRemoteName(remoteName);
            }
            if (_processPanel != null)
            {
                _processPanel.SetRemoteName(remoteName);
            }
            UpdateFooter(null, null);
        }

        private string RemoteName()
        {
            return CleanRemoteName(_settings == null ? null : _settings.RemoteDisplayName);
        }

        private static string CleanRemoteName(string value)
        {
            var name = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(name) ? "GPU Machine" : name;
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
            _remoteSummaryText.Text = state + " " + RemoteName() + " " + target;
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
                "  |  " + RemoteName() + "  |  " + (u != null && u.Online ? "ONLINE" : "OFFLINE") +
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
            ClipToBounds = true;

            _border = new Border();
            _border.Margin = new Thickness(0, 10, 0, 0);
            _border.Padding = new Thickness(8);
            _border.BorderThickness = new Thickness(1);
            _border.BorderBrush = accent;
            _border.Background = Theme.PanelBrush;
            _border.ClipToBounds = true;
            Children.Add(_border);

            _content = new ContentControl();
            _content.ClipToBounds = true;
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

    public sealed class BtopCpuPanel : Grid
    {
        private readonly HostCpuStrip _local;
        private readonly HostCpuStrip _ubuntu;
        private readonly TextBlock _summaryCpu;
        private readonly TextBlock _summaryUbuntu;
        private readonly TextBlock _summaryCpuPower;
        private readonly TextBlock _summaryGpuPower;
        private readonly TextBlock _summaryLatency;
        private string _remoteName;

        public BtopCpuPanel()
        {
            _remoteName = "GPU Machine";
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });

            var strips = new Grid();
            strips.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            strips.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _local = new HostCpuStrip("local windows", Theme.GreenBrush);
            _ubuntu = new HostCpuStrip(_remoteName, Theme.YellowBrush);
            Grid.SetRow(_local, 0);
            Grid.SetRow(_ubuntu, 1);
            strips.Children.Add(_local);
            strips.Children.Add(_ubuntu);

            var summary = new Border();
            summary.BorderThickness = new Thickness(1);
            summary.BorderBrush = Theme.DarkBorderBrush;
            summary.Background = Theme.ChartBackgroundBrush;
            summary.Padding = new Thickness(10);
            summary.Margin = new Thickness(10, 28, 0, 28);

            var stack = new StackPanel();
            _summaryCpu = SummaryLine(SummaryPair("local", "--"), Theme.GreenBrush, 15, true);
            _summaryUbuntu = SummaryLine(SummaryPair(_remoteName, "--"), Theme.YellowBrush, 15, true);
            _summaryCpuPower = SummaryLine(SummaryPair("cpu pwr", "--"), Theme.TextBrush, 13, false);
            _summaryGpuPower = SummaryLine(SummaryPair("gpu pwr", "--"), Theme.TextBrush, 13, false);
            _summaryLatency = SummaryLine(SummaryPair("ssh", "--"), Theme.MutedBrush, 13, false);
            stack.Children.Add(_summaryCpu);
            stack.Children.Add(_summaryUbuntu);
            stack.Children.Add(_summaryCpuPower);
            stack.Children.Add(_summaryGpuPower);
            stack.Children.Add(_summaryLatency);
            summary.Child = stack;

            Grid.SetColumn(strips, 0);
            Grid.SetColumn(summary, 1);
            Children.Add(strips);
            Children.Add(summary);
        }

        public void SetLocal(LocalSnapshot snapshot)
        {
            _local.Set(snapshot.CpuPercent, snapshot.LogicalProcessorCount.ToString(CultureInfo.InvariantCulture) + " threads", Format.Watts(snapshot.CpuPackagePowerWatts));
            _summaryCpu.Text = SummaryPair("local", Format.Percent(snapshot.CpuPercent));
            _summaryCpuPower.Text = SummaryPair("cpu pwr", Format.Watts(snapshot.CpuPackagePowerWatts));
            _summaryGpuPower.Text = SummaryPair("gpu pwr", Format.Watts(snapshot.GpuPowerWatts));
        }

        public void SetUbuntu(UbuntuSnapshot snapshot)
        {
            _ubuntu.Set(snapshot.CpuPercent, snapshot.Online ? "online" : "offline", Format.Watts(snapshot.PowerWatts));
            _summaryUbuntu.Text = SummaryPair(_remoteName, Format.Percent(snapshot.CpuPercent));
            _summaryUbuntu.Foreground = snapshot.Online ? Theme.YellowBrush : Theme.RedBrush;
            _summaryLatency.Text = SummaryPair("ssh", snapshot.Online ? snapshot.LatencyMs.ToString("0", CultureInfo.InvariantCulture) + "ms" : "offline");
            _summaryLatency.Foreground = snapshot.Online ? Theme.MutedBrush : Theme.RedBrush;
        }

        public void SetRemoteName(string name)
        {
            _remoteName = string.IsNullOrWhiteSpace(name) ? "GPU Machine" : name.Trim();
            _ubuntu.SetTitle(_remoteName);
            _summaryUbuntu.Text = SummaryPair(_remoteName, "--");
        }

        private static TextBlock SummaryLine(string text, Brush brush, double size, bool bold)
        {
            var block = new TextBlock();
            block.Text = text;
            block.Foreground = brush;
            block.FontSize = size;
            block.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
            block.Margin = new Thickness(0, 3, 0, 3);
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            return block;
        }

        private static string SummaryPair(string label, string value)
        {
            label = string.IsNullOrWhiteSpace(label) ? "--" : label.Trim();
            if (label.Length > 11)
            {
                label = label.Substring(0, 10) + ".";
            }
            return label.PadRight(12) + (value ?? "--");
        }
    }

    public sealed class HostCpuStrip : Grid
    {
        private readonly TextBlock _label;
        private readonly SparklineCanvas _spark;
        private readonly SegmentedBar _bar;
        private readonly TextBlock _value;
        private readonly TextBlock _sub;

        public HostCpuStrip(string title, Brush accent)
        {
            Margin = new Thickness(0, 3, 0, 3);
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });

            _label = new TextBlock();
            _label.Text = title;
            _label.Foreground = accent;
            _label.FontSize = 14;
            _label.FontWeight = FontWeights.Bold;
            _label.VerticalAlignment = VerticalAlignment.Top;

            _spark = new SparklineCanvas(accent, true);
            _spark.Margin = new Thickness(6, 0, 8, 0);

            var right = new StackPanel();
            right.VerticalAlignment = VerticalAlignment.Center;
            _value = new TextBlock();
            _value.Foreground = accent;
            _value.FontWeight = FontWeights.Bold;
            _value.FontSize = 22;
            _sub = new TextBlock();
            _sub.Foreground = Theme.MutedBrush;
            _sub.FontSize = 12;
            _bar = new SegmentedBar();
            _bar.Height = 14;
            _bar.Margin = new Thickness(0, 5, 0, 3);
            right.Children.Add(_value);
            right.Children.Add(_bar);
            right.Children.Add(_sub);

            Grid.SetColumn(_label, 0);
            Grid.SetColumn(_spark, 1);
            Grid.SetColumn(right, 2);
            Children.Add(_label);
            Children.Add(_spark);
            Children.Add(right);
        }

        public void Set(double? percent, string sub, string power)
        {
            _value.Text = Format.Percent(percent);
            _value.Foreground = Theme.PercentBrush(percent);
            _sub.Text = sub + "  " + power;
            _spark.Add(percent);
            _bar.Value = percent;
        }

        public void SetTitle(string title)
        {
            _label.Text = string.IsNullOrWhiteSpace(title) ? "remote" : title.Trim();
        }
    }

    public sealed class BtopMemoryPanel : Grid
    {
        private readonly HostMemoryBlock _local;
        private readonly HostMemoryBlock _ubuntu;

        public BtopMemoryPanel()
        {
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _local = new HostMemoryBlock("local", Theme.GreenBrush);
            _ubuntu = new HostMemoryBlock("GPU Machine", Theme.YellowBrush);
            Grid.SetRow(_local, 0);
            Grid.SetRow(_ubuntu, 1);
            Children.Add(_local);
            Children.Add(_ubuntu);
        }

        public void SetLocal(LocalSnapshot snapshot)
        {
            _local.Set(Format.Gigabytes(snapshot.MemoryTotalBytes) + " GiB", Format.Gigabytes(snapshot.MemoryUsedBytes) + " GiB", Format.Gigabytes(snapshot.MemoryAvailableBytes) + " GiB", snapshot.MemoryPercent);
        }

        public void SetUbuntu(UbuntuSnapshot snapshot)
        {
            var totalGb = snapshot.MemoryTotalMb.HasValue ? snapshot.MemoryTotalMb.Value / 1024.0 : (double?)null;
            var usedGb = snapshot.MemoryUsedMb.HasValue ? snapshot.MemoryUsedMb.Value / 1024.0 : (double?)null;
            var availGb = totalGb.HasValue && usedGb.HasValue ? Math.Max(0.0, totalGb.Value - usedGb.Value) : (double?)null;
            _ubuntu.Set(Format.Number(totalGb) + " GiB",
                Format.Number(usedGb) + " GiB",
                Format.Number(availGb) + " GiB",
                snapshot.MemoryPercent);
        }

        public void SetRemoteName(string name)
        {
            _ubuntu.SetTitle(name);
        }
    }

    public sealed class HostMemoryBlock : Grid
    {
        private readonly TextBlock _title;
        private readonly TextBlock _total;
        private readonly TextBlock _used;
        private readonly TextBlock _free;
        private readonly SegmentedBar _bar;

        public HostMemoryBlock(string title, Brush accent)
        {
            Margin = new Thickness(0, 4, 0, 4);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _title = Text(title, accent, 13, true);
            _total = Text("Total: --", Theme.TextBrush, 13, true);
            _used = Text("Used: --", Theme.TextBrush, 13, false);
            _free = Text("Avail: --", Theme.TextBrush, 13, false);
            _bar = new SegmentedBar();
            _bar.Height = 14;
            _bar.Margin = new Thickness(0, 4, 0, 0);

            Add(_title, 0);
            Add(_total, 1);
            Add(_used, 2);
            Add(_free, 3);
            Add(_bar, 4);
        }

        public void Set(string total, string used, string free, double? percent)
        {
            _total.Text = "Total: " + total;
            _used.Text = "Used:  " + used + "  " + Format.Percent(percent);
            _used.Foreground = Theme.PercentBrush(percent);
            _free.Text = "Avail: " + free;
            _bar.Value = percent;
        }

        public void SetTitle(string title)
        {
            _title.Text = string.IsNullOrWhiteSpace(title) ? "remote" : title.Trim();
        }

        private void Add(UIElement element, int row)
        {
            Grid.SetRow(element, row);
            Children.Add(element);
        }

        private static TextBlock Text(string value, Brush brush, double size, bool bold)
        {
            var block = new TextBlock();
            block.Text = value;
            block.Foreground = brush;
            block.FontSize = size;
            block.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            return block;
        }
    }

    public sealed class BtopNetPanel : Grid
    {
        private readonly HostNetBlock _local;
        private readonly HostNetBlock _ubuntu;

        public BtopNetPanel()
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _local = new HostNetBlock("local", Theme.BlueBrush);
            _ubuntu = new HostNetBlock("GPU Machine", Theme.MagentaBrush);
            Grid.SetColumn(_local, 0);
            Grid.SetColumn(_ubuntu, 1);
            Children.Add(_local);
            Children.Add(_ubuntu);
        }

        public void SetLocal(LocalSnapshot snapshot)
        {
            _local.Set(snapshot.NetworkRxBytesPerSec, snapshot.NetworkTxBytesPerSec, snapshot.NetworkRxTotalBytes, snapshot.NetworkTxTotalBytes, true);
        }

        public void SetUbuntu(UbuntuSnapshot snapshot)
        {
            _ubuntu.Set(snapshot.NetworkRxBytesPerSec, snapshot.NetworkTxBytesPerSec, snapshot.NetworkRxTotalBytes, snapshot.NetworkTxTotalBytes, snapshot.Online);
        }

        public void SetRemoteName(string name)
        {
            _ubuntu.SetTitle(name);
        }
    }

    public sealed class HostNetBlock : Grid
    {
        private readonly TextBlock _title;
        private readonly SparklineCanvas _spark;
        private readonly TextBlock _download;
        private readonly TextBlock _upload;
        private readonly TextBlock _total;

        public HostNetBlock(string title, Brush accent)
        {
            Margin = new Thickness(4);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _title = Text(title, accent, 13, true);
            _spark = new SparklineCanvas(accent);
            _spark.MinHeight = 70;
            _download = Text("down --", Theme.TextBrush, 13, false);
            _upload = Text("up   --", Theme.TextBrush, 13, false);
            _total = Text("total --", Theme.MutedBrush, 12, false);

            Add(_title, 0);
            Add(_spark, 1);
            Add(_download, 2);
            Add(_upload, 3);
            Add(_total, 4);
        }

        public void Set(double? rxRate, double? txRate, ulong rxTotal, ulong txTotal, bool online)
        {
            _download.Text = online ? "down " + Format.BytesPerSecond(rxRate) : "down offline";
            _upload.Text = online ? "up   " + Format.BytesPerSecond(txRate) : "up   offline";
            _total.Text = "total " + Format.Bytes(rxTotal + txTotal);
            _spark.Add(NormalizeRate(rxRate, txRate));
        }

        public void SetTitle(string title)
        {
            _title.Text = string.IsNullOrWhiteSpace(title) ? "remote" : title.Trim();
        }

        private static double? NormalizeRate(double? rxRate, double? txRate)
        {
            var value = Math.Max(rxRate ?? 0.0, txRate ?? 0.0);
            if (value <= 0)
            {
                return 0;
            }
            return Math.Min(100.0, Math.Log10(value + 1) * 14.0);
        }

        private void Add(UIElement element, int row)
        {
            Grid.SetRow(element, row);
            Children.Add(element);
        }

        private static TextBlock Text(string value, Brush brush, double size, bool bold)
        {
            var block = new TextBlock();
            block.Text = value;
            block.Foreground = brush;
            block.FontSize = size;
            block.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            return block;
        }
    }

    public sealed class BtopAuxPanel : Grid
    {
        private readonly TextBlock _gpuUtil;
        private readonly TextBlock _gpuTemp;
        private readonly TextBlock _gpuVram;
        private readonly TextBlock _gpuPower;
        private readonly TextBlock _lmStatus;
        private readonly TextBlock _lmModel;
        private readonly TextBlock _lmProcessing;
        private readonly TextBlock _lmTokensPerSecond;
        private readonly TextBlock _lmTokenTotal;

        public BtopAuxPanel()
        {
            ClipToBounds = true;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var gpuGroup = Group("gpu", Theme.BlueBrush);
            _gpuUtil = Line("util --", Theme.BlueBrush, 11, true);
            _gpuTemp = Line("temp --", Theme.TextBrush, 11, false);
            _gpuVram = Line("vram --", Theme.TextBrush, 11, false);
            _gpuPower = Line("pwr --", Theme.YellowBrush, 11, false);
            gpuGroup.Children.Add(_gpuUtil);
            gpuGroup.Children.Add(_gpuTemp);
            gpuGroup.Children.Add(_gpuVram);
            gpuGroup.Children.Add(_gpuPower);

            var lmGroup = Group("lm studio", Theme.MagentaBrush);
            _lmStatus = Line("offline", Theme.RedBrush, 11, true);
            _lmModel = Line("model --", Theme.TextBrush, 11, false);
            _lmProcessing = Line("proc --", Theme.TextBrush, 11, false);
            _lmTokensPerSecond = Line("tok/s --", Theme.TextBrush, 11, false);
            _lmTokenTotal = Line("tokens --", Theme.TextBrush, 11, false);
            lmGroup.Children.Add(_lmStatus);
            lmGroup.Children.Add(_lmModel);
            lmGroup.Children.Add(_lmProcessing);
            lmGroup.Children.Add(_lmTokensPerSecond);
            lmGroup.Children.Add(_lmTokenTotal);

            Grid.SetColumn(gpuGroup, 0);
            Grid.SetColumn(lmGroup, 1);
            Children.Add(gpuGroup);
            Children.Add(lmGroup);
        }

        public void SetLocal(LocalSnapshot snapshot)
        {
            _gpuUtil.Text = "util " + Format.Percent(snapshot.GpuUtilizationPercent);
            _gpuUtil.Foreground = snapshot.GpuAvailable ? Theme.BlueBrush : Theme.MutedBrush;
            _gpuTemp.Text = "temp " + Format.Temperature(snapshot.GpuTemperatureCelsius);
            _gpuVram.Text = "vram " + Format.MemoryPairMb(snapshot.GpuMemoryUsedMb, snapshot.GpuMemoryTotalMb) + "M";
            _gpuPower.Text = "pwr  " + Format.Watts(snapshot.GpuPowerWatts);
        }

        public void SetLm(LmStudioSnapshot snapshot)
        {
            var model = string.IsNullOrWhiteSpace(snapshot.ActiveModel) ? "N/A" : snapshot.ActiveModel.Trim();
            _lmStatus.Text = snapshot.ServerOnline ? "online" : "offline";
            _lmStatus.Foreground = snapshot.ServerOnline ? Theme.MagentaBrush : Theme.RedBrush;
            _lmModel.Text = "model " + ShortValue(model, 14);
            _lmModel.ToolTip = model;
            _lmProcessing.Text = "proc " + Format.Processing(snapshot.IsProcessing).ToLowerInvariant();
            _lmProcessing.Foreground = ProcessingBrush(snapshot.IsProcessing);
            _lmTokensPerSecond.Text = "tok/s " + Format.Number(snapshot.TokensPerSecond);
            _lmTokenTotal.Text = "tokens " + Format.TokenPair(snapshot.SessionInputTokens, snapshot.SessionOutputTokens);
        }

        private static StackPanel Group(string title, Brush brush)
        {
            var stack = new StackPanel();
            stack.Margin = new Thickness(4, 0, 4, 0);
            stack.Children.Add(Line(title, brush, 11, true));
            return stack;
        }

        private static TextBlock Line(string value, Brush brush, double size, bool bold)
        {
            var block = new TextBlock();
            block.Text = value;
            block.Foreground = brush;
            block.FontSize = size;
            block.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
            block.Margin = new Thickness(0, 1, 0, 1);
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            block.TextWrapping = TextWrapping.NoWrap;
            return block;
        }

        private static Brush ProcessingBrush(bool? processing)
        {
            if (!processing.HasValue)
            {
                return Theme.MutedBrush;
            }
            return processing.Value ? Theme.YellowBrush : Theme.GreenBrush;
        }

        private static string ShortValue(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "N/A";
            }
            value = value.Trim();
            if (value.Length <= max)
            {
                return value;
            }
            return value.Substring(0, Math.Max(0, max - 3)) + "...";
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
        private string _remoteName;
        private DateTime _nextLocalProcessRefreshUtc = DateTime.MinValue;

        public ProcessPanel()
        {
            _localSamples = new Dictionary<int, LocalProcessSample>();
            _localRows = new List<ProcessRow>();
            _ubuntuRows = new List<ProcessRow>();
            _ubuntuStatus = "WAIT";
            _remoteName = "GPU Machine";

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
            _localButton = UiFactory.TextButton("local");
            _localButton.MinWidth = 72;
            _localButton.Height = 24;
            _localButton.Padding = new Thickness(8, 2, 8, 2);
            _localButton.Margin = new Thickness(6, 2, 0, 2);
            _localButton.Click += delegate
            {
                _showUbuntu = false;
                Render();
            };
            _ubuntuButton = UiFactory.TextButton(RemoteButtonLabel());
            _ubuntuButton.MinWidth = 96;
            _ubuntuButton.Height = 24;
            _ubuntuButton.Padding = new Thickness(8, 2, 8, 2);
            _ubuntuButton.Margin = new Thickness(8, 2, 0, 2);
            _ubuntuButton.ToolTip = _remoteName;
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
            var now = DateTime.UtcNow;
            if (now < _nextLocalProcessRefreshUtc && _localRows.Count > 0)
            {
                return;
            }
            _nextLocalProcessRefreshUtc = now.AddMilliseconds(1000);
            _localRows = CaptureLocalRows();
            if (!_showUbuntu)
            {
                Render();
            }
        }

        public void SetRemoteName(string name)
        {
            _remoteName = string.IsNullOrWhiteSpace(name) ? "GPU Machine" : name.Trim();
            _ubuntuButton.Content = RemoteButtonLabel();
            _ubuntuButton.ToolTip = _remoteName;
            Render();
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
            _modeText.Text = _showUbuntu ? "proc < " + _remoteName + " >" : "proc < local >";
            _localButton.Foreground = _showUbuntu ? Theme.MutedBrush : Theme.GreenBrush;
            _ubuntuButton.Foreground = _showUbuntu ? Theme.GreenBrush : Theme.MutedBrush;

            if (_showUbuntu && !_ubuntuOnline && rows.Count == 0)
            {
                AddRow(1, "--", _remoteName + " offline", "--", "--", "--", _ubuntuStatus, Theme.RedBrush, true);
                _footer.Text = "source: " + RemoteSourceName() + "                                  Total: 0";
                return;
            }

            if (rows.Count == 0)
            {
                AddRow(1, "--", "No process data", "--", "--", "--", "--", Theme.MutedBrush, true);
                _footer.Text = "source: " + (_showUbuntu ? RemoteSourceName() : "Local Windows") + "                                  Total: 0";
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
            _footer.Text = "select < local > < " + RemoteButtonLabel() + " >                         source: " +
                (_showUbuntu ? RemoteSourceName() : "Local Windows") +
                "   Total: " + rows.Count.ToString(CultureInfo.InvariantCulture);
        }

        private string RemoteSourceName()
        {
            return _remoteName + " (SSH)";
        }

        private string RemoteButtonLabel()
        {
            if (string.IsNullOrWhiteSpace(_remoteName))
            {
                return "remote";
            }
            return _remoteName.Length <= 12 ? _remoteName : _remoteName.Substring(0, 9) + "...";
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
        private readonly TextBox _nameBox;
        private readonly TextBox _remoteBox;
        private readonly PasswordBox _passwordBox;
        private readonly TextBox _lmBox;

        public string RemoteDisplayName { get; private set; }
        public string RemoteTarget { get; private set; }
        public string Password { get; private set; }
        public string LmStudioUrl { get; private set; }

        public SettingsWindow(MonitorSettings settings)
        {
            Title = "OpenClaw Monitor Setup";
            Width = 520;
            Height = 292;
            MinWidth = 460;
            MinHeight = 260;
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
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _nameBox = AddTextRow(grid, 0, "NAME", settings.RemoteDisplayName);
            _remoteBox = AddTextRow(grid, 1, "REMOTE", settings.UbuntuTarget);
            _passwordBox = AddPasswordRow(grid, 2, "PASS", settings.UbuntuPassword);
            _lmBox = AddTextRow(grid, 3, "LM API", settings.LmStudioBaseUrl);

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
            Grid.SetRow(buttons, 5);
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
            RemoteDisplayName = _nameBox.Text;
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
        private readonly bool _mirrored;
        private const int Capacity = 96;

        public SparklineCanvas(Brush accent)
            : this(accent, false)
        {
        }

        public SparklineCanvas(Brush accent, bool mirrored)
        {
            _samples = new List<double?>();
            _accentBrush = accent;
            _mirrored = mirrored;
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
            if (_mirrored)
            {
                DrawMirroredSamples(dc, w, h, step, dot);
                return;
            }

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

        private void DrawMirroredSamples(DrawingContext dc, double w, double h, double step, double dot)
        {
            var centerY = h / 2.0;
            var centerPen = new Pen(Theme.DarkBorderBrush, 1);
            dc.DrawLine(centerPen, new Point(0, centerY), new Point(w, centerY));

            const double rowGap = 5.0;
            var halfRows = Math.Max(3, (int)Math.Floor((h / 2.0 - 5.0) / rowGap));
            for (int i = 0; i < _samples.Count; i++)
            {
                var sample = _samples[i];
                if (!sample.HasValue)
                {
                    continue;
                }

                var x = (Capacity - _samples.Count + i) * step;
                var activeRows = Math.Max(0, (int)Math.Round(sample.Value / 100.0 * halfRows));
                dc.DrawRectangle(_accentBrush, null, new Rect(x, centerY - dot / 2.0, dot, dot));

                for (int row = 1; row <= activeRows; row++)
                {
                    var yOffset = row * rowGap;
                    var brush = MirrorBrush(sample.Value, row, halfRows);
                    dc.DrawRectangle(brush, null, new Rect(x, centerY - yOffset - dot / 2.0, dot, dot));
                    dc.DrawRectangle(brush, null, new Rect(x, centerY + yOffset - dot / 2.0, dot, dot));
                }
            }
        }

        private Brush MirrorBrush(double value, int row, int halfRows)
        {
            var rowPosition = (double)row / Math.Max(1, halfRows);
            if (value >= 90 && rowPosition >= 0.72)
            {
                return Theme.RedBrush;
            }
            if (value >= 70 && rowPosition >= 0.54)
            {
                return Theme.YellowBrush;
            }
            return _accentBrush;
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
        private DateTime _lastNetworkUtc;
        private ulong _lastNetworkRxBytes;
        private ulong _lastNetworkTxBytes;
        private DateTime _nextCpuPowerReadUtc = DateTime.MinValue;
        private double? _cachedCpuPackagePowerWatts;
        private DateTime _nextGpuReadUtc = DateTime.MinValue;
        private bool _cachedGpuAvailable;
        private double? _cachedGpuUtilizationPercent;
        private double? _cachedGpuTemperatureCelsius;
        private double? _cachedGpuMemoryUsedMb;
        private double? _cachedGpuMemoryTotalMb;
        private double? _cachedGpuPowerWatts;

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

            ReadCpuPower(snapshot);
            ReadMemory(snapshot);
            ReadGpu(snapshot);
            ReadNetwork(snapshot);
            return snapshot;
        }

        private void ReadCpuPower(LocalSnapshot snapshot)
        {
            var now = DateTime.UtcNow;
            if (now >= _nextCpuPowerReadUtc)
            {
                _cachedCpuPackagePowerWatts = _cpuPowerReader.ReadWatts();
                _nextCpuPowerReadUtc = now.AddMilliseconds(1000);
            }
            snapshot.CpuPackagePowerWatts = _cachedCpuPackagePowerWatts;
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
            var now = DateTime.UtcNow;
            if (now < _nextGpuReadUtc)
            {
                ApplyCachedGpu(snapshot);
                return;
            }

            if (string.IsNullOrEmpty(_nvidiaSmiPath))
            {
                _cachedGpuAvailable = false;
                _nextGpuReadUtc = now.AddSeconds(5);
                ApplyCachedGpu(snapshot);
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
                _cachedGpuAvailable = false;
                _nextGpuReadUtc = now.AddSeconds(2);
                ApplyCachedGpu(snapshot);
                return;
            }

            var line = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line))
            {
                _cachedGpuAvailable = false;
                _nextGpuReadUtc = now.AddSeconds(2);
                ApplyCachedGpu(snapshot);
                return;
            }

            var fields = line.Split(',');
            if (fields.Length >= 5)
            {
                _cachedGpuAvailable = true;
                _cachedGpuUtilizationPercent = ParseNullableDouble(fields[0]);
                _cachedGpuTemperatureCelsius = ParseNullableDouble(fields[1]);
                _cachedGpuMemoryUsedMb = ParseNullableDouble(fields[2]);
                _cachedGpuMemoryTotalMb = ParseNullableDouble(fields[3]);
                _cachedGpuPowerWatts = ParseNullableDouble(fields[4]);
                _nextGpuReadUtc = now.AddMilliseconds(1000);
            }
            else
            {
                _cachedGpuAvailable = false;
                _nextGpuReadUtc = now.AddSeconds(2);
            }
            ApplyCachedGpu(snapshot);
        }

        private void ApplyCachedGpu(LocalSnapshot snapshot)
        {
            snapshot.GpuAvailable = _cachedGpuAvailable;
            snapshot.GpuUtilizationPercent = _cachedGpuUtilizationPercent;
            snapshot.GpuTemperatureCelsius = _cachedGpuTemperatureCelsius;
            snapshot.GpuMemoryUsedMb = _cachedGpuMemoryUsedMb;
            snapshot.GpuMemoryTotalMb = _cachedGpuMemoryTotalMb;
            snapshot.GpuPowerWatts = _cachedGpuPowerWatts;
        }

        private void ReadNetwork(LocalSnapshot snapshot)
        {
            try
            {
                ulong rx = 0;
                ulong tx = 0;
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }
                    var stats = nic.GetIPv4Statistics();
                    rx += stats.BytesReceived < 0 ? 0UL : (ulong)stats.BytesReceived;
                    tx += stats.BytesSent < 0 ? 0UL : (ulong)stats.BytesSent;
                }

                var now = DateTime.UtcNow;
                snapshot.NetworkRxTotalBytes = rx;
                snapshot.NetworkTxTotalBytes = tx;
                if (_lastNetworkUtc != default(DateTime))
                {
                    var elapsed = Math.Max(0.001, (now - _lastNetworkUtc).TotalSeconds);
                    snapshot.NetworkRxBytesPerSec = rx >= _lastNetworkRxBytes ? (rx - _lastNetworkRxBytes) / elapsed : 0;
                    snapshot.NetworkTxBytesPerSec = tx >= _lastNetworkTxBytes ? (tx - _lastNetworkTxBytes) / elapsed : 0;
                }
                _lastNetworkUtc = now;
                _lastNetworkRxBytes = rx;
                _lastNetworkTxBytes = tx;
            }
            catch
            {
                snapshot.NetworkRxBytesPerSec = null;
                snapshot.NetworkTxBytesPerSec = null;
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

    public sealed class UbuntuMonitorService : IDisposable
    {
        private readonly object _clientGate = new object();
        private SshClient _passwordClient;
        private string _passwordClientKey = string.Empty;
        private string _remoteStatsKey = string.Empty;
        private DateTime _lastRemoteStatsUtc = DateTime.MinValue;
        private double? _lastRemoteCpuIdle;
        private double? _lastRemoteCpuTotal;
        private double? _lastRemoteEnergyUj;
        private ulong _lastRemoteNetworkRxTotal;
        private ulong _lastRemoteNetworkTxTotal;

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

            TrackRemoteTarget(settings);

            if (!string.IsNullOrWhiteSpace(settings.UbuntuPassword))
            {
                return ReadWithPassword(settings, sw, snapshot);
            }

            DisposePasswordClient();
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
                lock (_clientGate)
                {
                    var client = EnsurePasswordClient(settings);
                    using (var command = client.CreateCommand("python3 - <<'PY'\n" + RemotePython + "\nPY"))
                    {
                        command.CommandTimeout = TimeSpan.FromSeconds(4);
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
                DisposePasswordClient();
                sw.Stop();
                snapshot.LatencyMs = sw.Elapsed.TotalMilliseconds;
                snapshot.Error = ex.Message;
                return snapshot;
            }
        }

        private SshClient EnsurePasswordClient(MonitorSettings settings)
        {
            var key = PasswordClientKey(settings);
            if (_passwordClient != null && _passwordClientKey == key && _passwordClient.IsConnected)
            {
                return _passwordClient;
            }

            DisposePasswordClientUnlocked();
            var client = new SshClient(settings.UbuntuHost, settings.UbuntuPort, settings.UbuntuUser, settings.UbuntuPassword);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(4);
            client.KeepAliveInterval = TimeSpan.FromSeconds(15);
            client.Connect();
            _passwordClient = client;
            _passwordClientKey = key;
            return _passwordClient;
        }

        private static string PasswordClientKey(MonitorSettings settings)
        {
            return (settings.UbuntuUser ?? string.Empty) + "@" +
                (settings.UbuntuHost ?? string.Empty) + ":" +
                settings.UbuntuPort.ToString(CultureInfo.InvariantCulture) + "|" +
                (settings.UbuntuPassword ?? string.Empty);
        }

        private void TrackRemoteTarget(MonitorSettings settings)
        {
            var key = (settings.UbuntuUser ?? string.Empty) + "@" +
                (settings.UbuntuHost ?? string.Empty) + ":" +
                settings.UbuntuPort.ToString(CultureInfo.InvariantCulture);
            if (key == _remoteStatsKey)
            {
                return;
            }
            _remoteStatsKey = key;
            ResetRemoteDeltas();
        }

        private void ResetRemoteDeltas()
        {
            _lastRemoteStatsUtc = DateTime.MinValue;
            _lastRemoteCpuIdle = null;
            _lastRemoteCpuTotal = null;
            _lastRemoteEnergyUj = null;
            _lastRemoteNetworkRxTotal = 0;
            _lastRemoteNetworkTxTotal = 0;
        }

        public void Dispose()
        {
            DisposePasswordClient();
        }

        private void DisposePasswordClient()
        {
            lock (_clientGate)
            {
                DisposePasswordClientUnlocked();
            }
        }

        private void DisposePasswordClientUnlocked()
        {
            if (_passwordClient != null)
            {
                try
                {
                    if (_passwordClient.IsConnected)
                    {
                        _passwordClient.Disconnect();
                    }
                }
                catch
                {
                }
                _passwordClient.Dispose();
                _passwordClient = null;
            }
            _passwordClientKey = string.Empty;
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

        private UbuntuSnapshot ParseRemoteJson(string output, UbuntuSnapshot snapshot)
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
                var nowUtc = DateTime.UtcNow;
                var cpuIdle = JsonHelper.GetDouble(root, "cpu_idle");
                var cpuTotal = JsonHelper.GetDouble(root, "cpu_total");
                var energyUj = JsonHelper.GetDouble(root, "energy_uj");
                var networkRx = JsonHelper.GetUlong(root, "network_rx_total");
                var networkTx = JsonHelper.GetUlong(root, "network_tx_total");

                snapshot.CpuPercent = CalculateRemoteCpuPercent(cpuIdle, cpuTotal);
                snapshot.MemoryPercent = JsonHelper.GetDouble(root, "memory_percent");
                snapshot.MemoryUsedMb = JsonHelper.GetDouble(root, "memory_used_mb");
                snapshot.MemoryTotalMb = JsonHelper.GetDouble(root, "memory_total_mb");
                snapshot.PowerWatts = CalculateRemotePowerWatts(energyUj);
                if (!snapshot.PowerWatts.HasValue)
                {
                    snapshot.PowerWatts = JsonHelper.GetDouble(root, "power_watts");
                }
                snapshot.NetworkRxTotalBytes = networkRx;
                snapshot.NetworkTxTotalBytes = networkTx;
                CalculateRemoteNetworkRates(nowUtc, networkRx, networkTx, snapshot);
                snapshot.Processes = ParseProcessRows(JsonHelper.GetArray(root, "processes"));
                snapshot.Error = string.Empty;

                _lastRemoteStatsUtc = nowUtc;
                _lastRemoteCpuIdle = cpuIdle;
                _lastRemoteCpuTotal = cpuTotal;
                _lastRemoteEnergyUj = energyUj;
                _lastRemoteNetworkRxTotal = networkRx;
                _lastRemoteNetworkTxTotal = networkTx;
                return snapshot;
            }
            catch (Exception ex)
            {
                snapshot.Error = ex.Message;
                return snapshot;
            }
        }

        private double? CalculateRemoteCpuPercent(double? cpuIdle, double? cpuTotal)
        {
            if (!cpuIdle.HasValue || !cpuTotal.HasValue || !_lastRemoteCpuIdle.HasValue || !_lastRemoteCpuTotal.HasValue)
            {
                return null;
            }
            var totalDelta = cpuTotal.Value - _lastRemoteCpuTotal.Value;
            var idleDelta = cpuIdle.Value - _lastRemoteCpuIdle.Value;
            if (totalDelta <= 0 || idleDelta < 0)
            {
                return null;
            }
            return Math.Max(0.0, Math.Min(100.0, (1.0 - idleDelta / totalDelta) * 100.0));
        }

        private double? CalculateRemotePowerWatts(double? energyUj)
        {
            if (!energyUj.HasValue || !_lastRemoteEnergyUj.HasValue || _lastRemoteStatsUtc == DateTime.MinValue)
            {
                return null;
            }
            var elapsed = Math.Max(0.001, (DateTime.UtcNow - _lastRemoteStatsUtc).TotalSeconds);
            var delta = energyUj.Value - _lastRemoteEnergyUj.Value;
            if (delta < 0)
            {
                return null;
            }
            return (delta / 1000000.0) / elapsed;
        }

        private void CalculateRemoteNetworkRates(DateTime nowUtc, ulong rx, ulong tx, UbuntuSnapshot snapshot)
        {
            if (_lastRemoteStatsUtc == DateTime.MinValue)
            {
                snapshot.NetworkRxBytesPerSec = 0;
                snapshot.NetworkTxBytesPerSec = 0;
                return;
            }

            var elapsed = Math.Max(0.001, (nowUtc - _lastRemoteStatsUtc).TotalSeconds);
            snapshot.NetworkRxBytesPerSec = rx >= _lastRemoteNetworkRxTotal ? (rx - _lastRemoteNetworkRxTotal) / elapsed : 0;
            snapshot.NetworkTxBytesPerSec = tx >= _lastRemoteNetworkTxTotal ? (tx - _lastRemoteNetworkTxTotal) / elapsed : 0;
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

cpu_idle, cpu_total = cpu_snapshot()
energy_path, energy_uj = read_energy_file()

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

def read_processes():
    rows = []
    try:
        out = subprocess.check_output(
            ['ps', '-eo', 'pid,user,comm,pcpu,rss,stat', '--sort=-pcpu'],
            stderr=subprocess.DEVNULL,
            text=True
        )
        for line in out.splitlines()[1:31]:
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

def net_snapshot():
    rx = 0
    tx = 0
    try:
        with open('/proc/net/dev', 'r') as f:
            for line in f.readlines()[2:]:
                if ':' not in line:
                    continue
                name, data = line.split(':', 1)
                name = name.strip()
                if name == 'lo':
                    continue
                parts = data.split()
                if len(parts) >= 16:
                    rx += int(parts[0])
                    tx += int(parts[8])
    except Exception:
        pass
    return rx, tx

net_rx, net_tx = net_snapshot()

print(json.dumps({
    'cpu_idle': cpu_idle,
    'cpu_total': cpu_total,
    'memory_percent': memory_percent,
    'memory_used_mb': mem_used / 1024.0,
    'memory_total_mb': mem_total / 1024.0,
    'energy_uj': energy_uj,
    'network_rx_total': net_rx,
    'network_tx_total': net_tx,
    'processes': read_processes()
}))
";
    }

    public sealed class LmStudioService : IDisposable
    {
        private LmsLogTailer _tailer = new LmsLogTailer();
        private DateTime _nextApiReadUtc = DateTime.MinValue;
        private string _apiCacheKey = string.Empty;
        private LmModelsResult _cachedApiResult;
        private DateTime _nextPsReadUtc = DateTime.MinValue;
        private bool? _cachedProcessing;

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

            var api = ReadModelsApiCached(settings);
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

            var ps = ReadLmsPsCached();
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
            _nextApiReadUtc = DateTime.MinValue;
            _nextPsReadUtc = DateTime.MinValue;
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

        private LmModelsResult ReadModelsApiCached(MonitorSettings settings)
        {
            var baseUrl = (settings.LmStudioBaseUrl ?? "http://localhost:1234").Trim().TrimEnd('/');
            var key = baseUrl + "|" + (settings.LmStudioApiToken ?? string.Empty);
            var now = DateTime.UtcNow;
            if (key == _apiCacheKey && now < _nextApiReadUtc)
            {
                return _cachedApiResult;
            }

            _apiCacheKey = key;
            _cachedApiResult = ReadModelsApi(settings);
            _nextApiReadUtc = now.AddMilliseconds(_cachedApiResult == null ? 3000 : 1000);
            return _cachedApiResult;
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

        private bool? ReadLmsPsCached()
        {
            var now = DateTime.UtcNow;
            if (now < _nextPsReadUtc)
            {
                return _cachedProcessing;
            }

            _cachedProcessing = ReadLmsPs();
            _nextPsReadUtc = now.AddMilliseconds(_cachedProcessing.HasValue ? 2000 : 5000);
            return _cachedProcessing;
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
        public string RemoteDisplayName { get; set; }
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
                RemoteDisplayName = "GPU Machine",
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
            RemoteDisplayName = string.IsNullOrWhiteSpace(RemoteDisplayName) ? "GPU Machine" : RemoteDisplayName.Trim();
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
                RemoteDisplayName = RemoteDisplayName,
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
        public double? NetworkRxBytesPerSec { get; set; }
        public double? NetworkTxBytesPerSec { get; set; }
        public ulong NetworkRxTotalBytes { get; set; }
        public ulong NetworkTxTotalBytes { get; set; }
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
        public double? NetworkRxBytesPerSec { get; set; }
        public double? NetworkTxBytesPerSec { get; set; }
        public ulong NetworkRxTotalBytes { get; set; }
        public ulong NetworkTxTotalBytes { get; set; }
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

        public static ulong GetUlong(Dictionary<string, object> dict, string key)
        {
            var value = GetDouble(dict, key);
            if (!value.HasValue || value.Value <= 0)
            {
                return 0;
            }
            return (ulong)value.Value;
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

        public static string Bytes(ulong bytes)
        {
            if (bytes >= 1024UL * 1024UL * 1024UL)
            {
                return ((double)bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " GiB";
            }
            if (bytes >= 1024UL * 1024UL)
            {
                return ((double)bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
            }
            if (bytes >= 1024UL)
            {
                return ((double)bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KiB";
            }
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        public static string BytesPerSecond(double? bytes)
        {
            if (!bytes.HasValue)
            {
                return "N/A";
            }
            if (bytes.Value >= 1024.0 * 1024.0)
            {
                return (bytes.Value / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " MiB/s";
            }
            if (bytes.Value >= 1024.0)
            {
                return (bytes.Value / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KiB/s";
            }
            return bytes.Value.ToString("0", CultureInfo.InvariantCulture) + " B/s";
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
        public static readonly SolidColorBrush RedMutedBrush = Brush("#A66A72");
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
