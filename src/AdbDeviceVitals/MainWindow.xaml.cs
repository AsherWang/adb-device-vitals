using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdbDeviceVitals.Controls;
using AdbDeviceVitals.Models;
using AdbDeviceVitals.Parsing;
using AdbDeviceVitals.Services;
using WF = System.Windows.Forms;

namespace AdbDeviceVitals
{
    /// <summary>
    /// Win11 设置风格的主面板。业务逻辑与旧的 WinForms MainForm 一致：
    /// 采集、CSV、告警、守护、热点、短信、更新检查都保留，只是换成 WPF 控件
    /// 与 async/await（后台任务完成后天然回到 UI 线程，不再需要 BeginInvoke）。
    /// </summary>
    public partial class MainWindow
    {
        private static readonly Brush Muted = MakeBrush(0x6E, 0x6E, 0x6E);
        private static readonly Brush Normal = MakeBrush(0x1B, 0x1B, 0x1B);
        private static readonly Brush Green = MakeBrush(0x0F, 0x7B, 0x0F);
        private static readonly Brush Orange = MakeBrush(0xB0, 0x6A, 0x00);
        private static readonly Brush Red = MakeBrush(0xC4, 0x2B, 0x1C);

        private readonly Dictionary<string, CardLabels> _cards = new Dictionary<string, CardLabels>();
        private readonly AdbClient _adb = new AdbClient();
        private readonly DeviceMonitor _monitor;
        private readonly CsvLogger _logger = new CsvLogger();
        private readonly SmsInboxService _smsService;
        private readonly UsbTetherWatchdog _usbWatchdog = new UsbTetherWatchdog();
        private readonly DispatcherTimer _timer;

        private WF.NotifyIcon _tray;
        private SmsWindow _smsWindow;
        private bool _smsPollRunning;
        private bool _smsErrorNotified;
        private bool _backgroundStarted;
        private bool _shutdownDone;
        private bool _polling;
        private bool _allowExit;
        private bool _offlineNotified;
        private bool _needsDeviceChoice;
        private bool _tetherSwitchBusy;
        private bool _usbTetherRunning;
        private CancellationTokenSource _tetherCancellation;
        private bool _usbWatchdogRunning;
        private bool _usbWatchdogFailureNotified;
        private CancellationTokenSource _usbWatchdogCancellation;
        private bool _pcHotspotBusy;
        private bool _pcHotspotRunning;
        private bool _pcHotspotStatusBusy;
        private bool _pcHotspotChainPending;
        private CancellationTokenSource _pcHotspotCancellation;
        private DateTime _lastPcHotspotPoll = DateTime.MinValue;
        private bool _updateCheckRunning;
        private bool _exitingForUpdate;
        private DateTime _lastLog = DateTime.MinValue;
        private DateTime _lastThermalAlert = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();
            _monitor = new DeviceMonitor(_adb);
            _smsService = new SmsInboxService(_adb);

            _cards["network"] = new CardLabels(CardNetworkValue, CardNetworkDetail);
            _cards["hotspot"] = new CardLabels(CardHotspotValue, CardHotspotDetail);
            _cards["battery"] = new CardLabels(CardBatteryValue, CardBatteryDetail);
            _cards["thermal"] = new CardLabels(CardThermalValue, CardThermalDetail);
            _cards["traffic"] = new CardLabels(CardTrafficValue, CardTrafficDetail);
            _cards["device"] = new CardLabels(CardDeviceValue, CardDeviceDetail);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _timer.Tick += async delegate { await RefreshSnapshot(false); };

            Closing += OnClosing;
            RefreshSmsPreview();
        }

        /// <summary>托盘、定时器与首次采集由 App 在启动完成后调用；测试可只构造窗口不启动后台。</summary>
        public void StartBackgroundWork()
        {
            if (_backgroundStarted) return;
            _backgroundStarted = true;
            CreateTray();

            string configError = AppSettings.LastError;
            if (configError.Length > 0)
                Notify("配置文件", "无法读取或写入配置文件，已临时使用默认设置：" + configError, WF.ToolTipIcon.Warning);

            _timer.Start();
            RefreshSmsPreview();
            StartInitialWork();
        }

        private async void StartInitialWork()
        {
            await RefreshSnapshot(true);
            _pcHotspotChainPending = true;
            StartUsbTethering();
            RefreshPcHotspotStatus(true);
            await CheckForUpdates(false);
        }

        private void CreateTray()
        {
            _tray = new WF.NotifyIcon { Text = "ADB Device Vitals" };
            try { _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath); }
            catch { _tray.Icon = System.Drawing.SystemIcons.Application; }
            _tray.Visible = true;
            _tray.DoubleClick += delegate { RestoreWindow(); };

            WF.ContextMenuStrip menu = new WF.ContextMenuStrip();
            menu.Items.Add("显示面板", null, delegate { RestoreWindow(); });
            menu.Items.Add("立即刷新", null, async delegate { await RefreshSnapshot(true); });
            menu.Items.Add("近期短信", null, delegate { OpenSmsWindow(); });
            menu.Items.Add("复制最新验证码", null, delegate { CopyLatestSmsCode(); });
            menu.Items.Add("打开日志目录", null, delegate { OpenLogDirectory(); });
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { RequestExit(); });
            _tray.ContextMenuStrip = menu;
        }

        private void RequestExit()
        {
            _allowExit = true;
            Application.Current.Shutdown();
        }

        public void RestoreWindow()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>清理托盘、定时器与在途任务。App.OnExit 与关闭窗口都会调用，需幂等。</summary>
        public void Shutdown()
        {
            if (_shutdownDone) return;
            _shutdownDone = true;
            _timer.Stop();
            CancelUsbTethering();
            CancelUsbTetherWatchdog();
            CancelPcHotspot();
            if (_smsWindow != null)
            {
                try { _smsWindow.Close(); } catch { }
                _smsWindow = null;
            }
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!_allowExit)
            {
                e.Cancel = true;
                Hide();
                Notify("ADB Device Vitals", "程序仍在托盘中监控设备。", WF.ToolTipIcon.Info);
                return;
            }
            Shutdown();
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
                CheckUsbTetherWatchdog(snapshot);
                RefreshPcHotspotStatus(false);
                PollSmsAsync(snapshot);
            }
            catch (Exception ex)
            {
                ConnectionStatus.Text = "采集失败";
                ConnectionStatus.Foreground = Red;
                UpdatedText.Text = ex.Message;
            }
            finally { _polling = false; }
        }

        private void UpdateUi(DeviceSnapshot s)
        {
            UpdatedText.Text = "更新于 " + s.Timestamp.ToString("HH:mm:ss");
            _needsDeviceChoice = s.NeedsDeviceSelection;
            ConnectionStatus.Cursor = _needsDeviceChoice ? Cursors.Hand : Cursors.Arrow;
            if (!s.Connected)
            {
                ConnectionStatus.Text = s.NeedsDeviceSelection
                    ? (s.PreferredSerialMissing ? "预设设备未连接 · 点此重新选择" : "检测到多台设备 · 点此指定")
                    : s.Error;
                ConnectionStatus.Foreground = Orange;
                SetCard("device", "未连接",
                    s.NeedsDeviceSelection ? "请在设置中指定用于网络共享的设备" : "请检查 USB 调试和设备授权");
                SyncUsbSwitch(false, false);
                return;
            }

            ConnectionStatus.Text = "● 设备在线";
            ConnectionStatus.Foreground = Green;
            SetCard("network", SafeText(s.NetworkType, "未知网络"), SafeText(s.OperatorName, "未知运营商") + "  ·  " + FormatSignal(s));
            SetCard("hotspot", TetheringTitle(s), TetheringDetail(s));
            SyncUsbSwitch(true, s.UsbTetheringActive);
            SetCard("battery", s.BatteryLevel.HasValue ? s.BatteryLevel.Value + "%" : "—", s.BatteryStatus + "  ·  " + PowerSourceName(s) + "  ·  " + Voltage(s.BatteryVoltageV));
            SetCard("thermal", Temperature(s.BatteryTemperatureC), "系统热状态：" + ThermalName(s.ThermalStatus));
            SetCard("traffic", "↓ " + s.DownloadMbps.ToString("0.00", CultureInfo.InvariantCulture) + " Mbps",
                "↑ " + s.UploadMbps.ToString("0.00", CultureInfo.InvariantCulture) + " Mbps  ·  " + SafeText(s.UpstreamInterface, "自动接口"));
            string capacity = s.LearnedCapacityMah.HasValue
                ? s.LearnedCapacityMah.Value.ToString("0", CultureInfo.InvariantCulture) + " mAh 学习容量"
                : "容量数据不可用";
            SetCard("device", SafeText(s.DeviceModel, "Android Device"), capacity + "  ·  " + ShortSerial(s.DeviceSerial));
            Chart.AddSample(s.BatteryTemperatureC, s.RsrpDbm);
        }

        private void CheckAlerts(DeviceSnapshot s)
        {
            if (!s.Connected)
            {
                if (_usbTetherRunning || _offlineNotified) return;
                Notify(s.NeedsDeviceSelection ? "需要选择设备" : "设备已断开", s.Error, WF.ToolTipIcon.Warning);
                _offlineNotified = true;
                return;
            }
            _offlineNotified = false;
            if (DateTime.Now - _lastThermalAlert < TimeSpan.FromMinutes(15)) return;
            if (s.BatteryTemperatureC.HasValue && s.BatteryTemperatureC.Value >= 45)
            {
                Notify("手机温度过高", "电池温度为 " + s.BatteryTemperatureC.Value.ToString("0.0") + "°C", WF.ToolTipIcon.Error);
                _lastThermalAlert = DateTime.Now;
            }
            else if (s.ThermalStatus.HasValue && s.ThermalStatus.Value >= 3)
            {
                Notify("系统触发热限制", "当前热状态：" + ThermalName(s.ThermalStatus), WF.ToolTipIcon.Warning);
                _lastThermalAlert = DateTime.Now;
            }
        }

        private void Notify(string title, string body, WF.ToolTipIcon icon)
        {
            if (_tray == null) return;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = body;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(4000);
        }

        /// <summary>短信检查挂在每 2.5 秒的采集上，服务内部再节流到 10 秒；这里不 await。</summary>
        private async void PollSmsAsync(DeviceSnapshot snapshot)
        {
            if (!AppSettings.SmsSyncEnabled || !snapshot.Connected || _smsPollRunning) return;
            _smsPollRunning = true;
            try
            {
                SmsPollResult result = await _smsService.PollAsync(snapshot.DeviceSerial, true, DateTime.UtcNow);
                ApplySmsResult(result);
            }
            catch (Exception ex)
            {
                SetSmsPreview("短信监听失败：" + ex.Message, Orange);
            }
            finally { _smsPollRunning = false; }
        }

        private void ApplySmsResult(SmsPollResult result)
        {
            if (result.Error.Length > 0)
            {
                if (!_smsErrorNotified)
                {
                    Notify("短信同步", result.PermissionDenied
                        ? "手机拒绝了 ADB 读取短信的权限，请查看主窗口底部提示。"
                        : result.Error, WF.ToolTipIcon.Warning);
                    _smsErrorNotified = true;
                }
            }
            else
            {
                _smsErrorNotified = false;
                if (result.NewMessages.Count > 0)
                {
                    // 提醒里只放数量、发件人和「有没有验证码」，绝不拼正文或码值。
                    if (SmsSenderFilter.ShouldNotify(AppSettings.SmsSenderFilter, result.NewMessages))
                    {
                        SmsMessage first = result.NewMessages[0];
                        string title = "收到 " + result.NewMessages.Count + " 条新短信";
                        string text = result.NewMessages.Count == 1
                            ? "发件人 " + first.Address + "。"
                            : "来自 " + result.NewMessages.Count + " 条新消息。";
                        bool hasCode = false;
                        foreach (SmsMessage message in result.NewMessages)
                        {
                            if (SmsCodeExtractor.HasCode(message.Body)) { hasCode = true; break; }
                        }
                        text += hasCode
                            ? "含验证码，可用托盘菜单「复制最新验证码」。"
                            : "点击托盘菜单「近期短信」查看。";
                        bool shown = AppSettings.SmsToastEnabled && WindowsToastService.TryShow(title, text);
                        if (!shown) Notify(title, text, WF.ToolTipIcon.Info);
                    }
                    if (_smsWindow != null) _smsWindow.Reload();
                }
            }
            RefreshSmsPreview();
        }

        private void RefreshSmsPreview()
        {
            if (!AppSettings.SmsSyncEnabled)
            {
                SetSmsPreview("短信同步已关闭（可在设置中开启）", Muted);
                return;
            }
            string error = _smsService.LastError;
            if (error.Length > 0)
            {
                SetSmsPreview(error, Orange);
                return;
            }

            SmsMessage[] messages = _smsService.Snapshot();
            if (messages.Length == 0)
            {
                SetSmsPreview(_smsService.IsListening
                    ? "监听中 · 本次运行期间暂无新短信"
                    : "正在建立监听基线…", Muted);
                return;
            }

            SmsMessage latest = messages[messages.Length - 1];
            string time = latest.ReceivedAt == DateTime.MinValue
                ? string.Empty : latest.ReceivedAt.ToString("HH:mm:ss") + "  ";
            // 主窗口属于「路过就能看见」的地方，永不显示正文；只说有没有验证码。
            string preview = SmsCodeExtractor.HasCode(latest.Body)
                ? "含验证码 · 正文已隐藏" : "正文已隐藏 · 打开列表后点「显示正文」查看";
            SetSmsPreview(time + latest.Address + "  ·  " + preview, Normal);
        }

        private void SetSmsPreview(string text, Brush brush)
        {
            SmsPreviewText.Text = text;
            SmsPreviewText.Foreground = brush;
        }

        private void OpenSmsWindow()
        {
            if (_smsWindow != null)
            {
                _smsWindow.Reload();
                _smsWindow.Activate();
                return;
            }
            _smsWindow = new SmsWindow(
                delegate { return _smsService.Snapshot(); },
                delegate { _smsService.Clear(); RefreshSmsPreview(); });
            if (IsVisible) _smsWindow.Owner = this;
            _smsWindow.Closed += delegate { _smsWindow = null; };
            _smsWindow.Show();
        }

        private void CopyLatestSmsCode()
        {
            SmsMessage[] messages = _smsService.Snapshot();
            for (int i = messages.Length - 1; i >= 0; i--)
            {
                string code = SmsCodeExtractor.Extract(messages[i].Body);
                if (code.Length == 0) continue;
                try
                {
                    Clipboard.SetText(code);
                    Notify("验证码已复制", "来自 " + messages[i].Address + "，已放入剪贴板。", WF.ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    Notify("复制失败", ex.Message, WF.ToolTipIcon.Warning);
                }
                return;
            }
            Notify("没有可复制的验证码", "本次运行期间收到的短信里没有检测到验证码。", WF.ToolTipIcon.Info);
        }

        private void SetCard(string key, string value, string detail)
        {
            CardLabels card = _cards[key];
            card.Value.Text = value;
            card.Detail.Text = detail;
        }

        private void CheckUsbTetherWatchdog(DeviceSnapshot snapshot)
        {
            bool busy = _usbTetherRunning || _tetherSwitchBusy || _usbWatchdogRunning;
            bool recover = _usbWatchdog.Observe(snapshot, DateTime.UtcNow,
                AppSettings.AutoRecoverUsbTethering, AppSettings.AutoEnableUsbTethering, busy);
            if (recover) StartUsbTetherRecovery();
        }

        private async void StartUsbTetherRecovery()
        {
            if (_usbWatchdogRunning || _usbTetherRunning || _tetherSwitchBusy) return;
            _usbWatchdogRunning = true;
            CancellationTokenSource source = new CancellationTokenSource();
            _usbWatchdogCancellation = source;
            ShowTetherStatus("USB 网络共享已断开，正在尝试恢复…", Muted);
            Notify("USB 网络共享", "检测到网络共享已断开，正在尝试重新开启。", WF.ToolTipIcon.Info);
            try
            {
                UsbTetherResult result = await Task.Run(
                    delegate { return new UsbTetheringService(_adb).SetEnabledAsync(true, source.Token); });
                if (source.IsCancellationRequested || !ReferenceEquals(_usbWatchdogCancellation, source)) return;
                ShowUsbWatchdogResult(result);
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_usbWatchdogCancellation, source))
                    ShowUsbWatchdogResult(UsbTetherResult.Failed(ex.Message));
            }
        }

        private void ShowUsbWatchdogResult(UsbTetherResult result)
        {
            _usbWatchdogRunning = false;
            if (!result.Completed) return;
            if (result.Succeeded)
            {
                _usbWatchdogFailureNotified = false;
                ShowTetherStatus(result.Changed ? "已自动恢复 USB 网络共享。" : "USB 网络共享已恢复。", Green);
                if (result.Changed) Notify("USB 网络共享", "已自动恢复 USB 网络共享。", WF.ToolTipIcon.Info);
            }
            else
            {
                ShowTetherStatus("自动恢复 USB 网络共享失败：" + result.Message, Orange);
                if (!_usbWatchdogFailureNotified)
                {
                    Notify("USB 网络共享", "自动恢复失败：" + result.Message + "。稍后会再试。", WF.ToolTipIcon.Warning);
                    _usbWatchdogFailureNotified = true;
                }
            }
        }

        private void CancelUsbTetherWatchdog()
        {
            _usbWatchdogRunning = false;
            CancellationTokenSource source = _usbWatchdogCancellation;
            _usbWatchdogCancellation = null;
            if (source == null) return;
            try { source.Cancel(); } catch (ObjectDisposedException) { }
        }

        private async void StartUsbTethering()
        {
            CancelUsbTethering();
            if (!AppSettings.AutoEnableUsbTethering)
            {
                ShowTetherStatus(string.Empty, Muted);
                StartPcHotspotIfPending();
                return;
            }

            CancellationTokenSource source = new CancellationTokenSource();
            _tetherCancellation = source;
            _usbTetherRunning = true;
            ShowTetherStatus("正在检查 USB 网络共享…", Muted);
            try
            {
                UsbTetherResult result = await Task.Run(
                    delegate { return new UsbTetheringService(_adb).EnsureAsync(source.Token); });
                if (source.IsCancellationRequested || !ReferenceEquals(_tetherCancellation, source)) return;
                ShowTetherResult(result);
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_tetherCancellation, source)) ShowTetherResult(UsbTetherResult.Failed(ex.Message));
            }
        }

        private void ShowTetherResult(UsbTetherResult result)
        {
            _usbTetherRunning = false;
            if (!result.Completed) return;
            ShowTetherStatus(result.Message, result.Succeeded ? Green : Orange);
            if (!result.Succeeded) Notify("USB 网络共享", result.Message, WF.ToolTipIcon.Warning);
            else if (result.Changed) Notify("USB 网络共享", result.Message, WF.ToolTipIcon.Info);
            StartPcHotspotIfPending();
        }

        private void SyncUsbSwitch(bool connected, bool active)
        {
            if (_tetherSwitchBusy) return;
            // ToggleSwitch 只在用户点击时触发 Click，代码同步 IsChecked 不会误触发操作。
            UsbTetherSwitch.IsEnabled = connected;
            UsbTetherSwitch.IsChecked = connected && active;
        }

        private async void UsbTetherSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (_tetherSwitchBusy) return;
            bool enable = UsbTetherSwitch.IsChecked == true;
            // 手动关闭是用户明确的意思，守护不该立刻把共享抢回来；重新手动打开即解除暂停。
            if (enable) _usbWatchdog.NoteManualOn();
            else _usbWatchdog.NoteManualOff();
            CancelUsbTetherWatchdog();
            _tetherSwitchBusy = true;
            UsbTetherSwitch.IsEnabled = false;
            CancelUsbTethering();
            ShowTetherStatus(enable ? "正在开启 USB 网络共享…" : "正在关闭 USB 网络共享…", Muted);
            try
            {
                UsbTetherResult result = await Task.Run(
                    delegate { return new UsbTetheringService(_adb).SetEnabledAsync(enable, CancellationToken.None); });
                ShowTetherResult(result);
                await RefreshSnapshot(true);
            }
            catch (Exception ex)
            {
                ShowTetherStatus(ex.Message, Orange);
            }
            finally
            {
                _tetherSwitchBusy = false;
                UsbTetherSwitch.IsEnabled = true;
            }
        }

        private void ShowTetherStatus(string message, Brush brush)
        {
            TetherStatusText.Text = message;
            TetherStatusText.Foreground = brush;
        }

        private void CancelUsbTethering()
        {
            _usbTetherRunning = false;
            CancellationTokenSource source = _tetherCancellation;
            _tetherCancellation = null;
            if (source == null) return;
            try { source.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>开机链路上只在 USB 共享这一步落定后触发一次自动开热点。</summary>
        private void StartPcHotspotIfPending()
        {
            if (!_pcHotspotChainPending) return;
            _pcHotspotChainPending = false;
            StartPcHotspot();
        }

        private async void StartPcHotspot()
        {
            if (!AppSettings.AutoStartPcHotspot)
            {
                RefreshPcHotspotStatus(true);
                return;
            }
            if (_pcHotspotRunning) return;

            CancelPcHotspot();
            CancellationTokenSource source = new CancellationTokenSource();
            _pcHotspotCancellation = source;
            _pcHotspotRunning = true;
            ShowPcHotspotStatus("等待手机网络就绪…", Muted);
            try
            {
                PcHotspotResult result = await Task.Run(
                    delegate { return new PcHotspotService().EnsureAsync(source.Token); });
                if (source.IsCancellationRequested || !ReferenceEquals(_pcHotspotCancellation, source)) return;
                ShowPcHotspotResult(result);
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_pcHotspotCancellation, source))
                    ShowPcHotspotResult(PcHotspotResult.Failed(PcHotspotService.ExceptionMessage(ex)));
            }
        }

        private void ShowPcHotspotResult(PcHotspotResult result)
        {
            _pcHotspotRunning = false;
            if (result.Completed)
            {
                ShowPcHotspotStatus(result.Message, result.Succeeded ? Green : Orange);
                if (!result.Succeeded) Notify("电脑热点", result.Message, WF.ToolTipIcon.Warning);
                else if (result.Changed) Notify("电脑热点", result.Message, WF.ToolTipIcon.Info);
            }
            RefreshPcHotspotStatus(true);
        }

        private async void RefreshPcHotspotStatus(bool force)
        {
            if (_pcHotspotStatusBusy) return;
            if (!force && DateTime.UtcNow - _lastPcHotspotPoll < TimeSpan.FromSeconds(5)) return;
            _lastPcHotspotPoll = DateTime.UtcNow;
            _pcHotspotStatusBusy = true;
            try
            {
                PcHotspotSnapshot snapshot = await Task.Run(
                    delegate { return new PcHotspotService().Read(); });
                ApplyPcHotspotSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                ApplyPcHotspotSnapshot(PcHotspotSnapshot.Unavailable(PcHotspotService.ExceptionMessage(ex)));
            }
            finally { _pcHotspotStatusBusy = false; }
        }

        private void ApplyPcHotspotSnapshot(PcHotspotSnapshot snapshot)
        {
            if (!snapshot.Supported)
            {
                PcHotspotStateText.Text = "不可用";
                PcHotspotStateText.Foreground = Orange;
                SetPcHotspotDetail(snapshot.Error, Muted);
                SyncPcHotspotSwitch(false, false);
                return;
            }

            bool on = snapshot.State == PcHotspotState.On;
            if (on)
            {
                PcHotspotStateText.Text = "已开启";
                PcHotspotStateText.Foreground = Green;
            }
            else if (snapshot.State == PcHotspotState.Off)
            {
                PcHotspotStateText.Text = "未开启";
                PcHotspotStateText.Foreground = Normal;
            }
            else if (snapshot.State == PcHotspotState.Transitioning)
            {
                PcHotspotStateText.Text = "切换中";
                PcHotspotStateText.Foreground = Orange;
            }
            else
            {
                PcHotspotStateText.Text = "状态未知";
                PcHotspotStateText.Foreground = Muted;
            }

            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(snapshot.Ssid)) parts.Add(snapshot.Ssid);
            if (on) parts.Add(snapshot.ClientCount + "/" + snapshot.MaxClientCount + " 台设备");
            if (!string.IsNullOrEmpty(snapshot.UpstreamName)) parts.Add("上游 " + snapshot.UpstreamName);
            if (parts.Count == 0) parts.Add(on ? "已共享手机网络" : "未共享");
            SetPcHotspotDetail(string.Join("  ·  ", parts.ToArray()), Muted);
            SyncPcHotspotSwitch(true, on);
        }

        private void SetPcHotspotDetail(string text, Brush brush)
        {
            PcHotspotDetailText.Text = text;
            PcHotspotDetailText.Foreground = brush;
        }

        private void ShowPcHotspotStatus(string message, Brush brush)
        {
            SetPcHotspotDetail(message, brush);
        }

        private void SyncPcHotspotSwitch(bool supported, bool on)
        {
            PcHotspotSwitch.IsEnabled = supported && !_pcHotspotBusy;
            PcHotspotSwitch.IsChecked = supported && on;
        }

        private async void PcHotspotSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (_pcHotspotBusy) return;
            bool enable = PcHotspotSwitch.IsChecked == true;
            _pcHotspotBusy = true;
            PcHotspotSwitch.IsEnabled = false;
            CancelPcHotspot();
            ShowPcHotspotStatus(enable ? "正在开启电脑热点…" : "正在关闭电脑热点…", Muted);
            try
            {
                PcHotspotResult result = await Task.Run(
                    delegate { return new PcHotspotService().SetEnabled(enable); });
                ShowPcHotspotResult(result);
            }
            catch (Exception ex)
            {
                ShowPcHotspotStatus(ex.Message, Orange);
            }
            finally
            {
                _pcHotspotBusy = false;
                RefreshPcHotspotStatus(true);
            }
        }

        private void CancelPcHotspot()
        {
            _pcHotspotRunning = false;
            CancellationTokenSource source = _pcHotspotCancellation;
            _pcHotspotCancellation = null;
            if (source == null) return;
            try { source.Cancel(); } catch (ObjectDisposedException) { }
        }

        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            await OpenSettings();
        }

        private async void ConnectionStatus_Click(object sender, MouseButtonEventArgs e)
        {
            if (_needsDeviceChoice) await OpenSettings();
        }

        private void OpenSmsButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSmsWindow();
        }

        private async Task OpenSettings()
        {
            // 允许程序运行期间手改 settings.json，打开设置时重新读一次。
            AppSettings.Reload();
            bool recoverBefore = AppSettings.AutoRecoverUsbTethering;
            bool smsBefore = AppSettings.SmsSyncEnabled;
            SettingsWindow dialog = new SettingsWindow(_adb, delegate { return CheckForUpdates(true); });
            if (IsVisible) dialog.Owner = this;
            bool? saved = dialog.ShowDialog();
            if (saved != true || _exitingForUpdate) return;

            if (!recoverBefore && AppSettings.AutoRecoverUsbTethering) _usbWatchdog.NoteManualOn();
            if (!smsBefore && AppSettings.SmsSyncEnabled) _smsService.ResetBaseline();
            if (smsBefore && !AppSettings.SmsSyncEnabled) _smsErrorNotified = false;
            RefreshSmsPreview();
            _adb.ReloadPath();
            StartUsbTethering();
            if (AppSettings.AutoStartPcHotspot) StartPcHotspot();
            await RefreshSnapshot(true);
        }

        private async Task CheckForUpdates(bool manual)
        {
            string path = Environment.ProcessPath ?? string.Empty;
            if (!manual && !UpdateChecker.ShouldCheckAutomatically(path)) return;
            if (!manual)
            {
                DateTime? last = AppSettings.LastUpdateCheckUtc;
                if (last.HasValue && DateTime.UtcNow - last.Value < TimeSpan.FromHours(24)) return;
            }
            if (_updateCheckRunning)
            {
                if (manual) MessageBox.Show("正在检查更新，请稍候。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _updateCheckRunning = true;
            try
            {
                UpdateCheckResult result = await UpdateChecker.CheckAsync();
                if (_exitingForUpdate) return;
                if (!result.ReachedServer)
                {
                    if (manual) MessageBox.Show("检查更新失败：" + result.Error, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AppSettings.LastUpdateCheckUtc = DateTime.UtcNow;
                if (!result.UpdateAvailable)
                {
                    if (manual) MessageBox.Show("当前已是最新版本 " + UpdateChecker.CurrentVersionText + "。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                bool development = UpdateChecker.IsDevelopmentBuild(path);
                string prompt = development
                    ? "发现新版本 " + result.Tag + "。当前是开发目录中的程序，无法原地更新。是否打开发布页面？"
                    : "发现新版本 " + result.Tag + "，是否现在更新？";
                if (MessageBox.Show(prompt, "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                    return;
                if (development)
                {
                    UpdateChecker.OpenReleasePage(result.ReleasePage);
                    return;
                }

                Notify("检查更新", "正在下载 " + result.Tag + "…", WF.ToolTipIcon.Info);
                try
                {
                    await UpdateChecker.PrepareReplacementAsync(result.Tag);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("更新失败：" + ex.Message + "\n将打开发布页面。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                    UpdateChecker.OpenReleasePage(result.ReleasePage);
                    return;
                }

                ExitForUpdate();
            }
            catch (Exception ex)
            {
                if (manual) MessageBox.Show("检查更新失败：" + ex.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            Application.Current.Shutdown();
        }

        private void OpenLogDirectory()
        {
            System.IO.Directory.CreateDirectory(_logger.LogDirectory);
            Process.Start(new ProcessStartInfo(_logger.LogDirectory) { UseShellExecute = true });
        }

        private static Brush MakeBrush(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
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

        private static string PowerSourceName(DeviceSnapshot value)
        {
            if (value.UsbPowered) return "USB 供电";
            if (value.AcPowered) return "交流电充电";
            if (value.WirelessPowered) return "无线充电";
            return "电池供电";
        }

        private static string Temperature(double? value) { return value.HasValue ? value.Value.ToString("0.0") + " °C" : "—"; }
        private static string Voltage(double? value) { return value.HasValue ? value.Value.ToString("0.000") + " V" : "电压未知"; }
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
            public System.Windows.Controls.TextBlock Value { get; private set; }
            public System.Windows.Controls.TextBlock Detail { get; private set; }
            public CardLabels(System.Windows.Controls.TextBlock value, System.Windows.Controls.TextBlock detail)
            {
                Value = value;
                Detail = detail;
            }
        }
    }
}
