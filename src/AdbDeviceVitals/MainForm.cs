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
        private Label _connection;
        private Label _updated;
        private Label _adbPath;
        private bool _polling;
        private bool _allowExit;
        private bool _offlineNotified;
        private DateTime _lastLog = DateTime.MinValue;
        private DateTime _lastThermalAlert = DateTime.MinValue;

        public MainForm() : this(null)
        {
        }

        internal MainForm(System.Threading.EventWaitHandle activationEvent)
        {
            _activationEvent = activationEvent;
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

            Shown += async delegate
            {
                _timer.Start();
                if (_activationEvent != null) _activationTimer.Start();
                await RefreshSnapshot(true);
            };
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
            FormClosing += OnFormClosing;
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
            Label title = new Label { Text = "ADB DEVICE VITALS", AutoSize = true, Font = new Font("Segoe UI Semibold", 18f), Location = new Point(0, 2) };
            Label subtitle = new Label { Text = "Android 状态 · 本机读取 · 无需 Root", AutoSize = true, ForeColor = _muted, Location = new Point(2, 38) };
            _connection = new Label { Text = "正在连接…", AutoSize = true, ForeColor = _muted, Anchor = AnchorStyles.Top | AnchorStyles.Right, TextAlign = ContentAlignment.MiddleRight };
            Button refresh = new Button { Text = "刷新", Width = 72, Height = 30, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(38, 48, 61), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            refresh.FlatAppearance.BorderSize = 0;
            refresh.Click += async delegate { await RefreshSnapshot(true); };
            Button settings = new Button { Text = "设置", Width = 72, Height = 30, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(38, 48, 61), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            settings.FlatAppearance.BorderSize = 0;
            settings.Click += async delegate { await OpenSettings(); };
            header.Controls.Add(title); header.Controls.Add(subtitle); header.Controls.Add(_connection); header.Controls.Add(settings); header.Controls.Add(refresh);
            header.Resize += delegate
            {
                refresh.Location = new Point(header.Width - refresh.Width, 8);
                settings.Location = new Point(refresh.Left - settings.Width - 8, 8);
                _connection.Location = new Point(settings.Left - _connection.Width - 14, 15);
            };
            root.Controls.Add(header, 0, 0);

            TableLayoutPanel cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = new Padding(0) };
            for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
            for (int i = 0; i < 2; i++) cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            AddCard(cards, 0, 0, "network", "蜂窝网络");
            AddCard(cards, 1, 0, "hotspot", "热点");
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
            _adbPath = new Label { Text = AdbPathText(), AutoEllipsis = true, ForeColor = _muted, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            footer.Controls.Add(_updated); footer.Controls.Add(_adbPath);
            footer.Resize += delegate { _adbPath.SetBounds(Math.Max(240, footer.Width / 2), 3, Math.Max(100, footer.Width / 2), 22); };
            root.Controls.Add(footer, 0, 3);
        }

        private async Task OpenSettings()
        {
            using (SettingsForm dialog = new SettingsForm(_adb.AdbPath))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
            }

            _adb.ReloadPath();
            _adbPath.Text = AdbPathText();
            await RefreshSnapshot(true);
        }

        private string AdbPathText()
        {
            return _adb.IsAvailable ? "ADB: " + _adb.AdbPath : "ADB 未找到";
        }

        private void AddCard(TableLayoutPanel grid, int column, int row, string key, string title)
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, BackColor = _card, Margin = new Padding(4) };
            Label heading = new Label { Text = title.ToUpperInvariant(), AutoSize = true, ForeColor = _muted, Font = new Font("Segoe UI Semibold", 8.5f), Location = new Point(15, 13) };
            Label value = new Label { Text = "—", AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 18f), Location = new Point(13, 39) };
            Label detail = new Label { Text = "等待数据", AutoSize = false, ForeColor = _muted, Location = new Point(16, 79), Height = 42, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            panel.Controls.Add(heading); panel.Controls.Add(value); panel.Controls.Add(detail);
            panel.Resize += delegate { detail.Width = Math.Max(80, panel.Width - 30); };
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
                return;
            }

            _connection.Text = "● 设备在线";
            _connection.ForeColor = _green;
            SetCard("network", SafeText(s.NetworkType, "未知网络"), SafeText(s.OperatorName, "未知运营商") + "  ·  " + FormatSignal(s));
            SetCard("hotspot", s.HotspotActive ? SafeText(s.HotspotBand, "已开启") : "未开启", s.HotspotActive ? s.ConnectedClients + " 台设备  ·  " + Frequency(s.HotspotFrequencyMhz) : "未检测到 Wi-Fi 热点");
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
                if (!_offlineNotified)
                {
                    Notify("设备已断开", s.Error, ToolTipIcon.Warning);
                    _offlineNotified = true;
                }
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
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
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
            _tray.Visible = false;
            _tray.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
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

