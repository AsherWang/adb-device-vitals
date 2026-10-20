using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using AdbDeviceVitals.Controls;
using AdbDeviceVitals.Models;
using AdbDeviceVitals.Services;

namespace AdbDeviceVitals
{
    public sealed class MainForm : Form
    {
        private readonly Color _background = Color.FromArgb(16, 21, 28);
        private readonly Color _card = Color.FromArgb(24, 30, 39);
        private readonly Color _muted = Color.FromArgb(147, 160, 176);
        private readonly Color _green = Color.FromArgb(76, 217, 138);
        private readonly Dictionary<string, CardLabels> _cards = new Dictionary<string, CardLabels>();
        private readonly AdbClient _adb;
        private readonly DeviceMonitor _monitor;
        private readonly CsvLogger _logger;
        private readonly Timer _timer;
        private readonly Timer _activationTimer;
        private readonly System.Threading.EventWaitHandle _activationEvent;
        private readonly NotifyIcon _tray;
        private readonly HistoryChart _chart;
        private bool _hideUntilRestored;
        private Label _connection;
        private Label _updated;
        private Label _tetherStatus;
        private CheckBox _usbTetherSwitch;
        private bool _suppressTetherSwitch;
        private bool _tetherSwitchBusy;
        private System.Threading.CancellationTokenSource _tetherCancellation;
        private bool _polling;
        private bool _allowExit;
        private bool _offlineNotified;
        private bool _backgroundStarted;
        private bool _usbTetherRunning;
        private bool _applyingStartupVisibility;
        private bool _updateCheckRunning;
        private bool _exitingForUpdate;
        private DateTime _lastLog = DateTime.MinValue;
        private DateTime _lastThermalAlert = DateTime.MinValue;

        public MainForm() : this(null, false)
        {
        }

        internal MainForm(System.Threading.EventWaitHandle activationEvent) : this(activationEvent, false)
        {
        }

        internal MainForm(System.Threading.EventWaitHandle activationEvent, bool startInTray)
        {
            _activationEvent = activationEvent;
            _hideUntilRestored = startInTray;
            Text = "ADB Device Vitals";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(880, 650);
            MinimumSize = new Size(760, 560);
            BackColor = _background;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            _adb = new AdbClient();
            _monitor = new DeviceMonitor(_adb);
            _logger = new CsvLogger();
            _chart = new HistoryChart();
            BuildLayout();

            _tray = new NotifyIcon();
            _tray.Icon = Icon ?? SystemIcons.Application;
            _tray.Text = "ADB Device Vitals";
            _tray.Visible = true;
            _tray.DoubleClick += delegate { RestoreWindow(); };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示面板", null, delegate { RestoreWindow(); });
            menu.Items.Add("立即刷新", null, async delegate { await RefreshSnapshot(true); });
            menu.Items.Add("打开日志目录", null, delegate { OpenLogDirectory(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { _allowExit = true; Close(); });
            _tray.ContextMenuStrip = menu;

            _timer = new Timer();
            _timer.Interval = 2500;
            _timer.Tick += async delegate { await RefreshSnapshot(false); };

            _activationTimer = new Timer();
            _activationTimer.Interval = 250;
            _activationTimer.Tick += delegate
            {
                if (_activationEvent != null && _activationEvent.WaitOne(0)) RestoreWindow();
            };

            HandleCreated += delegate { StartBackgroundWork(); };
            if (startInTray) ShowInTaskbar = false;
            if (IsHandleCreated) StartBackgroundWork();
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
            FormClosing += OnFormClosing;
        }

        protected override void SetVisibleCore(bool value)
        {
            if (_hideUntilRestored)
            {
                value = false;
                if (!_applyingStartupVisibility && !IsHandleCreated)
                {
                    _applyingStartupVisibility = true;
                    CreateHandle();
                    _applyingStartupVisibility = false;
                }
            }
            base.SetVisibleCore(value);
        }

        private async void StartBackgroundWork()
        {
            if (_backgroundStarted) return;
            _backgroundStarted = true;
            _timer.Start();
            if (_activationEvent != null) _activationTimer.Start();
            await RefreshSnapshot(true);
            StartUsbTethering();
            await CheckForUpdates(this, false);
        }

        private void BuildLayout()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(20);
            root.RowCount = 4;
            root.ColumnCount = 1;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(root);

            Panel header = new Panel { Dock = DockStyle.Fill };
            Label title = new Label { Text = "ADB DEVICE VITALS", AutoSize = true, Font = new Font("Segoe UI Semibold", 18f), Location = new Point(0, 15) };
            _connection = new Label { Text = "正在连接…", AutoSize = true, ForeColor = _muted, Anchor = AnchorStyles.Top | AnchorStyles.Right, TextAlign = ContentAlignment.MiddleRight };
            Label settings = new Label { Text = "\uE713", Width = 38, Height = 34, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, BackColor = _background, Anchor = AnchorStyles.Top | AnchorStyles.Right, Font = new Font("Segoe MDL2 Assets", 12f), AccessibleName = "设置", AccessibleRole = AccessibleRole.PushButton, Cursor = Cursors.Hand };
            settings.MouseEnter += delegate { settings.ForeColor = _green; };
            settings.MouseLeave += delegate { settings.ForeColor = Color.White; };
            settings.Click += async delegate { await OpenSettings(); };
            header.Controls.Add(title); header.Controls.Add(_connection); header.Controls.Add(settings);
            header.Resize += delegate
            {
                settings.Location = new Point(header.Width - settings.Width, 12);
                _connection.Location = new Point(settings.Left - _connection.Width - 14, 21);
            };
            root.Controls.Add(header, 0, 0);

            TableLayoutPanel cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = new Padding(0) };
            for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
            for (int i = 0; i < 2; i++) cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            AddCard(cards, 0, 0, "network", "蜂窝网络");
            AddCard(cards, 1, 0, "hotspot", "网络共享");
            AddCard(cards, 2, 0, "battery", "电池");
            AddCard(cards, 0, 1, "thermal", "温度与热状态");
            AddCard(cards, 1, 1, "traffic", "实时流量");
            AddCard(cards, 2, 1, "device", "设备");
            root.Controls.Add(cards, 0, 1);

            _chart.Dock = DockStyle.Fill;
            _chart.Margin = new Padding(4, 10, 4, 4);
            root.Controls.Add(_chart, 0, 2);

            Panel footer = new Panel { Dock = DockStyle.Fill };
            _updated = new Label { Text = "尚未采集", AutoSize = true, ForeColor = _muted, Location = new Point(0, 7) };
            _tetherStatus = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = _muted,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Location = new Point(240, 5),
                Height = 20
            };
            footer.Resize += delegate { _tetherStatus.Width = Math.Max(80, footer.Width - _tetherStatus.Left); };
            footer.Controls.Add(_updated);
            footer.Controls.Add(_tetherStatus);
            root.Controls.Add(footer, 0, 3);
        }

        private async Task OpenSettings()
        {
            using (SettingsForm dialog = new SettingsForm(_adb.AdbPath, delegate(IWin32Window owner) { return CheckForUpdates(owner, true); }))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || _exitingForUpdate) return;
            }

            if (_exitingForUpdate || IsDisposed) return;
            _adb.ReloadPath();
            StartUsbTethering();
            await RefreshSnapshot(true);
        }

        private void AddCard(TableLayoutPanel grid, int column, int row, string key, string title)
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, BackColor = _card, Margin = new Padding(4) };
            Label heading = new Label { Text = title.ToUpperInvariant(), AutoSize = true, ForeColor = _muted, Font = new Font("Segoe UI Semibold", 8.5f), Location = new Point(15, 13) };
            Label value = new Label { Text = "—", AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 18f), Location = new Point(13, 39) };
            Label detail = new Label { Text = "等待数据", AutoSize = false, ForeColor = _muted, Location = new Point(16, 79), Height = 42, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            panel.Controls.Add(heading); panel.Controls.Add(value); panel.Controls.Add(detail);
            panel.Resize += delegate { detail.Width = Math.Max(80, panel.Width - 30); };
            if (key == "hotspot")
            {
                _usbTetherSwitch = new CheckBox
                {
                    Text = "USB 共享",
                    AutoSize = true,
                    ForeColor = Color.White,
                    BackColor = _card,
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    Enabled = false
                };
                panel.Controls.Add(_usbTetherSwitch);
                panel.Resize += delegate { _usbTetherSwitch.Location = new Point(Math.Max(80, panel.Width - _usbTetherSwitch.Width - 10), 8); };
                _usbTetherSwitch.CheckedChanged += async delegate { await OnUsbTetherSwitchChanged(); };
            }
            grid.Controls.Add(panel, column, row);
            _cards[key] = new CardLabels(value, detail);
        }

        private async Task RefreshSnapshot(bool force)
        {
            if (_polling) return;
            _polling = true;
            try
            {
                DeviceSnapshot snapshot = await _monitor.PollAsync(force);
                UpdateUi(snapshot);
                if (snapshot.Connected && DateTime.Now - _lastLog >= TimeSpan.FromSeconds(10))
                {
                    _logger.Append(snapshot);
                    _lastLog = DateTime.Now;
                }
                CheckAlerts(snapshot);
            }
            catch (Exception ex)
            {
                _connection.Text = "采集失败";
                _connection.ForeColor = Color.FromArgb(255, 99, 99);
                _updated.Text = ex.Message;
            }
            finally { _polling = false; }
        }

        private void UpdateUi(DeviceSnapshot s)
        {
            _updated.Text = "更新于 " + s.Timestamp.ToString("HH:mm:ss");
            if (!s.Connected)
            {
                _connection.Text = s.Error;
                _connection.ForeColor = Color.FromArgb(255, 174, 66);
                SetCard("device", "未连接", "请检查 USB 调试和设备授权");
                SyncUsbSwitch(false, false);
                return;
            }

            _connection.Text = "● 设备在线";
            _connection.ForeColor = _green;
            SetCard("network", SafeText(s.NetworkType, "未知网络"), SafeText(s.OperatorName, "未知运营商") + "  ·  " + FormatSignal(s));
            SetCard("hotspot", TetheringTitle(s), TetheringDetail(s));
            SyncUsbSwitch(true, s.UsbTetheringActive);
            SetCard("battery", s.BatteryLevel.HasValue ? s.BatteryLevel.Value + "%" : "—", s.BatteryStatus + "  ·  " + s.PowerSource + "  ·  " + Voltage(s.BatteryVoltageV));
            SetCard("thermal", Temperature(s.BatteryTemperatureC), "系统热状态：" + ThermalName(s.ThermalStatus));
            SetCard("traffic", "↓ " + s.DownloadMbps.ToString("0.00", CultureInfo.InvariantCulture) + " Mbps", "↑ " + s.UploadMbps.ToString("0.00", CultureInfo.InvariantCulture) + " Mbps  ·  " + SafeText(s.UpstreamInterface, "自动接口"));
            string capacity = s.LearnedCapacityMah.HasValue ? s.LearnedCapacityMah.Value.ToString("0", CultureInfo.InvariantCulture) + " mAh 学习容量" : "容量数据不可用";
            SetCard("device", SafeText(s.DeviceModel, "Android Device"), capacity + "  ·  " + ShortSerial(s.DeviceSerial));
            _chart.AddSample(s.BatteryTemperatureC, s.RsrpDbm);
        }

        private void CheckAlerts(DeviceSnapshot s)
        {
            if (!s.Connected)
            {
                if (_usbTetherRunning || _offlineNotified) return;
                Notify("设备已断开", s.Error, ToolTipIcon.Warning);
                _offlineNotified = true;
                return;
            }
            _offlineNotified = false;
            if (DateTime.Now - _lastThermalAlert < TimeSpan.FromMinutes(15)) return;
            if (s.BatteryTemperatureC.HasValue && s.BatteryTemperatureC.Value >= 45)
            {
                Notify("手机温度过高", "电池温度为 " + s.BatteryTemperatureC.Value.ToString("0.0") + "°C", ToolTipIcon.Error);
                _lastThermalAlert = DateTime.Now;
            }
            else if (s.ThermalStatus.HasValue && s.ThermalStatus.Value >= 3)
            {
                Notify("系统触发热限制", "当前热状态：" + ThermalName(s.ThermalStatus), ToolTipIcon.Warning);
                _lastThermalAlert = DateTime.Now;
            }
        }

        private void Notify(string title, string body, ToolTipIcon icon)
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = body;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(4000);
        }

        private void SetCard(string key, string value, string detail)
        {
            CardLabels card = _cards[key];
            card.Value.Text = value;
            card.Detail.Text = detail;
        }

        private void RestoreWindow()
        {
            _hideUntilRestored = false;
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void StartUsbTethering()
        {
            CancelUsbTethering();
            if (!AppSettings.AutoEnableUsbTethering)
            {
                ShowTetherStatus(string.Empty, _muted);
                return;
            }

            System.Threading.CancellationTokenSource source = new System.Threading.CancellationTokenSource();
            _tetherCancellation = source;
            _usbTetherRunning = true;
            ShowTetherStatus("正在检查 USB 网络共享…", _muted);
            UsbTetheringService service = new UsbTetheringService(_adb);
            service.EnsureAsync(source.Token).ContinueWith(delegate(Task<UsbTetherResult> completed)
            {
                if (source.IsCancellationRequested || !object.ReferenceEquals(_tetherCancellation, source)) return;
                UsbTetherResult result = completed.IsFaulted
                    ? UsbTetherResult.Failed(completed.Exception.GetBaseException().Message)
                    : completed.Result;
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (IsDisposed || !object.ReferenceEquals(_tetherCancellation, source)) return;
                        ShowTetherResult(result);
                    }));
                }
                catch (InvalidOperationException)
                {
                }
            }, System.Threading.CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private void ShowTetherResult(UsbTetherResult result)
        {
            _usbTetherRunning = false;
            if (!result.Completed) return;
            ShowTetherStatus(result.Message, result.Succeeded ? _green : Color.FromArgb(255, 174, 66));
            if (!result.Succeeded)
                Notify("USB 网络共享", result.Message, ToolTipIcon.Warning);
            else if (result.Changed)
                Notify("USB 网络共享", result.Message, ToolTipIcon.Info);
        }

        private void SyncUsbSwitch(bool connected, bool active)
        {
            if (_usbTetherSwitch == null || _tetherSwitchBusy) return;
            _suppressTetherSwitch = true;
            _usbTetherSwitch.Enabled = connected;
            _usbTetherSwitch.Checked = connected && active;
            _suppressTetherSwitch = false;
        }

        private async Task OnUsbTetherSwitchChanged()
        {
            if (_suppressTetherSwitch || _usbTetherSwitch == null) return;
            bool enable = _usbTetherSwitch.Checked;
            _tetherSwitchBusy = true;
            _usbTetherSwitch.Enabled = false;
            CancelUsbTethering();
            ShowTetherStatus(enable ? "正在开启 USB 网络共享…" : "正在关闭 USB 网络共享…", _muted);
            try
            {
                UsbTetherResult result = await new UsbTetheringService(_adb).SetEnabledAsync(enable, System.Threading.CancellationToken.None);
                if (IsDisposed) return;
                ShowTetherResult(result);
                await RefreshSnapshot(true);
            }
            catch (Exception ex)
            {
                if (!IsDisposed) ShowTetherStatus(ex.Message, Color.FromArgb(255, 174, 66));
            }
            finally
            {
                _tetherSwitchBusy = false;
                if (!IsDisposed && _usbTetherSwitch != null) _usbTetherSwitch.Enabled = true;
            }
        }

        private void ShowTetherStatus(string message, Color color)
        {
            _tetherStatus.Text = message;
            _tetherStatus.ForeColor = color;
        }

        private void CancelUsbTethering()
        {
            _usbTetherRunning = false;
            System.Threading.CancellationTokenSource source = _tetherCancellation;
            _tetherCancellation = null;
            if (source == null) return;
            try { source.Cancel(); } catch (ObjectDisposedException) { }
        }

        private async Task CheckForUpdates(IWin32Window owner, bool manual)
        {
            if (!manual && !UpdateChecker.ShouldCheckAutomatically(Application.ExecutablePath)) return;
            if (!manual)
            {
                DateTime? last = AppSettings.LastUpdateCheckUtc;
                if (last.HasValue && DateTime.UtcNow - last.Value < TimeSpan.FromHours(24)) return;
            }
            if (_updateCheckRunning)
            {
                if (manual) MessageBox.Show(VisibleOwner(owner), "正在检查更新，请稍候。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _updateCheckRunning = true;
            try
            {
                UpdateCheckResult result = await UpdateChecker.CheckAsync();
                if (IsDisposed || _exitingForUpdate) return;
                if (!result.ReachedServer)
                {
                    if (manual) MessageBox.Show(VisibleOwner(owner), "检查更新失败：" + result.Error, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                AppSettings.LastUpdateCheckUtc = DateTime.UtcNow;
                if (!result.UpdateAvailable)
                {
                    if (manual) MessageBox.Show(VisibleOwner(owner), "当前已是最新版本 " + UpdateChecker.CurrentVersionText + "。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                bool development = UpdateChecker.IsDevelopmentBuild(Application.ExecutablePath);
                string prompt = development
                    ? "发现新版本 " + result.Tag + "。当前是开发目录中的程序，无法原地更新。是否打开发布页面？"
                    : "发现新版本 " + result.Tag + "，是否现在更新？";
                if (MessageBox.Show(VisibleOwner(owner), prompt, "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                    return;
                if (development)
                {
                    UpdateChecker.OpenReleasePage(result.ReleasePage);
                    return;
                }

                Notify("检查更新", "正在下载 " + result.Tag + "…", ToolTipIcon.Info);
                try
                {
                    await UpdateChecker.PrepareReplacementAsync(result.Tag);
                }
                catch (Exception ex)
                {
                    if (!IsDisposed) MessageBox.Show(VisibleOwner(owner), "更新失败：" + ex.Message + "\n将打开发布页面。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    UpdateChecker.OpenReleasePage(result.ReleasePage);
                    return;
                }

                ExitForUpdate();
            }
            catch (Exception ex)
            {
                if (manual && !IsDisposed) MessageBox.Show(VisibleOwner(owner), "检查更新失败：" + ex.Message, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _updateCheckRunning = false;
            }
        }

        private void ExitForUpdate()
        {
            _exitingForUpdate = true;
            _allowExit = true;
            Close();
        }

        private static IWin32Window VisibleOwner(IWin32Window owner)
        {
            Control control = owner as Control;
            if (control == null || !control.Visible || control.IsDisposed) return null;
            return owner;
        }

        private void OpenLogDirectory()
        {
            Directory.CreateDirectory(_logger.LogDirectory);
            Process.Start(_logger.LogDirectory);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                Notify("ADB Device Vitals", "程序仍在托盘中监控设备。", ToolTipIcon.Info);
                return;
            }
            CancelUsbTethering();
            _tray.Visible = false;
            _tray.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelUsbTethering();
                if (_timer != null) _timer.Dispose();
                if (_activationTimer != null) _activationTimer.Dispose();
                if (_chart != null) _chart.Dispose();
            }
            base.Dispose(disposing);
        }

        private static string FormatSignal(DeviceSnapshot s)
        {
            if (!s.RsrpDbm.HasValue) return "信号数据不可用";
            string result = s.RsrpDbm.Value + " dBm";
            if (s.SinrDb.HasValue) result += "  ·  SINR " + s.SinrDb.Value + " dB";
            return result;
        }

        private static string TetheringTitle(DeviceSnapshot value)
        {
            if (value.UsbTetheringActive && value.HotspotActive) return "USB + Wi-Fi";
            if (value.UsbTetheringActive) return "USB 网络共享";
            if (value.HotspotActive) return "Wi-Fi 热点";
            return "未开启";
        }

        private static string TetheringDetail(DeviceSnapshot value)
        {
            if (!value.UsbTetheringActive && !value.HotspotActive) return "未检测到网络共享";

            List<string> details = new List<string>();
            if (!string.IsNullOrWhiteSpace(value.UpstreamInterface)) details.Add("上游 " + value.UpstreamInterface);
            if (value.UsbTetheringActive && value.HotspotActive)
            {
                details.Add("USB " + value.UsbTetheringClients + " 台");
                details.Add("Wi-Fi " + value.ConnectedClients + " 台");
            }
            else if (value.UsbTetheringActive)
            {
                details.Add(value.UsbTetheringClients + " 台设备");
            }
            else
            {
                details.Add(SafeText(value.HotspotBand, "Wi-Fi"));
                details.Add(value.ConnectedClients + " 台设备");
            }
            return string.Join("  ·  ", details.ToArray());
        }

        private static string Temperature(double? value) { return value.HasValue ? value.Value.ToString("0.0") + " °C" : "—"; }
        private static string Voltage(double? value) { return value.HasValue ? value.Value.ToString("0.000") + " V" : "电压未知"; }
        private static string Frequency(int? value) { return value.HasValue ? value.Value + " MHz" : "频率未知"; }
        private static string SafeText(string value, string fallback) { return string.IsNullOrWhiteSpace(value) ? fallback : value; }
        private static string ShortSerial(string value) { return string.IsNullOrEmpty(value) ? "" : (value.Length <= 6 ? value : "…" + value.Substring(value.Length - 6)); }

        private static string ThermalName(int? value)
        {
            if (!value.HasValue) return "未知";
            string[] names = { "正常", "轻微", "中等", "严重", "临界", "紧急", "即将关机" };
            return value.Value >= 0 && value.Value < names.Length ? names[value.Value] : value.Value.ToString();
        }

        private sealed class CardLabels
        {
            public Label Value { get; private set; }
            public Label Detail { get; private set; }
            public CardLabels(Label value, Label detail) { Value = value; Detail = detail; }
        }
    }
}

