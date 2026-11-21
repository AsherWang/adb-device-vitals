using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
        private static readonly Brush BarOff = MakeBrush(0xD2, 0xD2, 0xD2);

        private readonly Dictionary<string, CardLabels> _cards = new Dictionary<string, CardLabels>();
        private readonly AdbClient _adb = new AdbClient();
        private readonly CarrierDetector _carrierDetector;
        private readonly DeviceMonitor _monitor;
        private readonly CsvLogger _logger = new CsvLogger();
        private readonly SmsInboxService _smsService;
        private readonly UsbTetherWatchdog _usbWatchdog = new UsbTetherWatchdog();
        private readonly DispatcherTimer _timer;
        private readonly PublicIpService _publicIp = new PublicIpService();
        private readonly DispatcherTimer _publicIpTimer;
        private readonly DispatcherTimer _publicIpDebounce;
        private readonly DispatcherTimer _broadnetTimer;
        private readonly BroadnetSessionService _broadnet = new BroadnetSessionService();
        private readonly TelecomSessionService _telecom = new TelecomSessionService();
        // 当前生效的运营商服务：由「当前设备 → 运营商」映射决定，无映射时按广电兜底。
        private ICarrierTrafficService _trafficService;
        private TrafficCarrier _currentCarrier = TrafficCarrier.None;
        // 卡片/详情窗当前读数归属的设备与运营商。异步读取完成后必须核对，
        // 否则「启动时先按广电读、随后识别出电信」这种竞态会把上一家的数据填进当前卡。
        private string _trafficDeviceSerial = string.Empty;
        private string _trafficWindowSerial = string.Empty;
        private TrafficCarrier _trafficWindowCarrier = TrafficCarrier.None;
        private bool _broadnetAutoEnabled;
        // 设备 SIM 自动识别的运营商（仅内存，不写配置）。手动映射优先于此值。
        private TrafficCarrier _autoDetectedCarrier = TrafficCarrier.None;
        private string _autoDetectedSerial = string.Empty;
        private BroadnetUsageStore _usageStore = new BroadnetUsageStore();

        private WF.NotifyIcon _tray;
        private SmsWindow _smsWindow;
        private bool _smsPollRunning;
        private bool _smsErrorNotified;
        private bool _backgroundStarted;
        private bool _shutdownDone;
        private bool _polling;
        private bool _allowExit;
        private bool _deviceConnected;
        private bool _hasPolled;
        private bool _offlineNotified;
        private bool _needsDeviceChoice;
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
        private NetworkAddressChangedEventHandler _networkChangedHandler;
        private CancellationTokenSource _publicIpCancellation;
        private bool _publicIpQueryRunning;
        private string _publicIpv4 = "";
        private string _publicIpv6 = "";
        private DateTime _publicIpCheckedAt = DateTime.MinValue;
        private NetworkDetailsWindow _networkDetailsWindow;
        private BatteryDetailsWindow _batteryDetailsWindow;
        private BroadnetTrafficWindow _broadnetTrafficWindow;
        private BroadnetTraffic _lastBroadnetTraffic;
        private DateTime _broadnetCheckedAt = DateTime.MinValue;
        // 用量采样节流：静默刷新每 10 分钟一次，人工点刷新可能更密。
        // 同一分钟内不重复落盘，避免短时间内的重复读数污染差分（差分为 0 无副作用，但文件会膨胀）。
        private DateTime _lastUsageAppendedAt = DateTime.MinValue;
        private double _lastUsageAppendedKb = -1;
        private DeviceSnapshot _lastSnapshot;

        public MainWindow()
        {
            InitializeComponent();
            _carrierDetector = new CarrierDetector(_adb);
            _monitor = new DeviceMonitor(_adb);
            _smsService = new SmsInboxService(_adb);
            _broadnet.Initialize(AppSettings.ConfigDirectory, CarrierMapping.CodeName(TrafficCarrier.Broadnet));
            _telecom.Initialize(AppSettings.ConfigDirectory, CarrierMapping.CodeName(TrafficCarrier.Telecom));
            SelectTrafficService();

            _cards["traffic"] = new CardLabels(CardTrafficValue, CardTrafficDetail);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _timer.Tick += async delegate { await RefreshSnapshot(false); };

            // 出口 IP 与 ADB 采集解耦：低频轮询 + 网络地址变化后去抖 2 秒再查。
            _publicIpTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            _publicIpTimer.Tick += delegate { _ = RefreshPublicIpAsync(); };
            _publicIpDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _publicIpDebounce.Tick += delegate { _publicIpDebounce.Stop(); _ = RefreshPublicIpAsync(); };
            // 两家运营商都要定时续期：只跟着"当前选中的那一家"刷，会导致另一家登录态过期。
            _broadnetTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            _broadnetTimer.Tick += delegate { _ = RefreshAllTrafficSilentlyAsync(); };
            _networkChangedHandler = delegate
            {
                Dispatcher.InvokeAsync(delegate
                {
                    if (_shutdownDone) return;
                    _publicIpDebounce.Stop();
                    _publicIpDebounce.Start();
                });
            };
            NetworkChange.NetworkAddressChanged += _networkChangedHandler;

            Closing += OnClosing;
            // 按 Alt 会把焦点框画出来（见 App.RegisterFocusVisualPolicy）：构造后先清一次，
            // 窗口 Loaded 时由 App 的全局处理器再清一遍模板展开后的完整可视树。
            App.SuppressFocusVisuals(this);
            // 见 FixWindowBorderColor：Mica 自定义标题栏的顶部细边框是半透明的，
            // 且 DWM 会在窗口激活 / 最小化恢复时重新合成，所以这些时机都要重设一次。
            EventHandler refreshBorder = delegate { FixWindowBorderColor(); };
            SourceInitialized += refreshBorder;
            ContentRendered += refreshBorder;
            Activated += refreshBorder;
            Deactivated += refreshBorder;
            StateChanged += refreshBorder;
            // Alt 会让 DWM 重新合成边框，顺手再刷一次浅色边框，避免透明底色透出深色。
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.System &&
                    (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt))
                    FixWindowBorderColor();
            };
            RefreshSmsPreview();
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// ExtendsContentIntoTitleBar + Mica 时，DWM 在窗口最上沿保留的 1~2px 细边框是半透明的，
        /// 窗口背后有深色窗口或壁纸时会透出一条「黑线」，且随窗口激活 / 移动偶发。
        /// 把 DWMWA_BORDER_COLOR 显式刷成窗口底色（不透明浅灰），应用侧内容一行都不用改。
        /// 老系统不支持该属性时返回失败，直接忽略即可。
        /// </summary>
        private void FixWindowBorderColor()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int color = 0x00F3F3F3; // COLORREF 0x00BBGGRR，与浅色窗口背景一致
                DwmSetWindowAttribute(hwnd, 34, ref color, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        /// <summary>托盘、定时器与首次采集由 App 在启动完成后调用；测试可只构造窗口不启动后台。</summary>
        /// <param name="skipBroadnetAuto">自检模式：后台不自动驱动广电 WebView，交给自检链路独占。</param>
        public void StartBackgroundWork(bool skipBroadnetAuto = false)
        {
            if (_backgroundStarted) return;
            _backgroundStarted = true;
            CreateTray();

            string configError = AppSettings.LastError;
            if (configError.Length > 0)
                Notify("配置文件", "无法读取或写入配置文件，已临时使用默认设置：" + configError, WF.ToolTipIcon.Warning);

            _timer.Start();
            _publicIpTimer.Start();
            _broadnetAutoEnabled = !skipBroadnetAuto;
            if (_broadnetAutoEnabled) _broadnetTimer.Start();
            // 首次静默刷新挪到 StartInitialWork：等目标设备与运营商识别完成后再读，
            // 避免用默认广电服务读到旧快照，随后运营商切成电信时数据串卡。
            _ = RefreshPublicIpAsync();
            RefreshSmsPreview();
            StartInitialWork();
        }

        private async void StartInitialWork()
        {
            await RefreshSnapshot(true);
            // 此时 RefreshSnapshot 已完成 SIM 识别并选定服务；两家会话都刷一次，
            // 不依赖当前选中的运营商，也不因设备未连接而跳过。
            if (_broadnetAutoEnabled) _ = RefreshAllTrafficSilentlyAsync();
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
            menu.Items.Add("流量余量查询", null, delegate { _ = OpenBroadnetSafe(); });
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
            _publicIpTimer.Stop();
            _publicIpDebounce.Stop();
            _broadnetTimer.Stop();
            if (_networkChangedHandler != null)
            {
                NetworkChange.NetworkAddressChanged -= _networkChangedHandler;
                _networkChangedHandler = null;
            }
            if (_publicIpCancellation != null)
            {
                _publicIpCancellation.Cancel();
                _publicIpCancellation.Dispose();
                _publicIpCancellation = null;
            }
            CancelUsbTethering();
            CancelUsbTetherWatchdog();
            CancelPcHotspot();
            if (_smsWindow != null)
            {
                try { _smsWindow.Close(); } catch { }
                _smsWindow = null;
            }
            if (_networkDetailsWindow != null)
            {
                try { _networkDetailsWindow.Close(); } catch { }
                _networkDetailsWindow = null;
            }
            if (_batteryDetailsWindow != null)
            {
                try { _batteryDetailsWindow.Close(); } catch { }
                _batteryDetailsWindow = null;
            }
            if (_broadnetTrafficWindow != null)
            {
                try { _broadnetTrafficWindow.Close(); } catch { }
                _broadnetTrafficWindow = null;
            }
            _broadnet.Shutdown();
            _telecom.Shutdown();
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

        /// <summary>
        /// 预设设备掉线后备用机接管、或预设设备回来后切回：提示一次，并让 USB 共享在
        /// 新设备上重新走一遍 Ensure。配置里的预设设备不动，所以原机回来能自动恢复。
        /// </summary>
        private void HandleDeviceSwitch(DeviceSnapshot snapshot)
        {
            if (!snapshot.AutoSwitchedToFallback && !snapshot.SwitchedBackToPreferred) return;
            string from = string.IsNullOrEmpty(snapshot.SwitchFromSerial)
                ? "原设备" : DeviceSelection.ShortSerial(snapshot.SwitchFromSerial);
            string to = DeviceSelection.ShortSerial(snapshot.DeviceSerial);
            _trafficService.Trace("device switch " + from + " -> " + to
                + (snapshot.SwitchedBackToPreferred ? " (back to preferred)" : " (failover)"));
            Notify(snapshot.SwitchedBackToPreferred ? "已切回预设设备" : "已自动切换设备",
                from + " → " + to + (AppSettings.AutoEnableUsbTethering
                    ? "，正在重新开启 USB 网络共享。" : "。"),
                WF.ToolTipIcon.Info);
            StartUsbTethering();
        }

        /// <summary>
        /// 目标设备变了才读一次 SIM 识别运营商，避免每轮轮询都开 adb 进程。
        /// 已有手动映射时跳过（手动优先），识别不到保持 None（卡片提示去设置）。
        /// </summary>
        private async Task EnsureAutoCarrierAsync()
        {
            if (AppSettings.CurrentCarrier != TrafficCarrier.None) return;
            string serial = DeviceSelection.EffectiveSerial;
            if (serial.Length == 0) return;
            if (string.Equals(serial, _autoDetectedSerial, StringComparison.OrdinalIgnoreCase)) return;

            _autoDetectedSerial = serial;
            _carrierDetector.Invalidate(serial);
            _autoDetectedCarrier = await _carrierDetector.DetectAsync(serial).ConfigureAwait(true);
        }

        private async Task RefreshSnapshot(bool force)
        {
            if (_polling) return;
            _polling = true;
            try
            {
                bool wasConnected = _deviceConnected;
                DeviceSnapshot snapshot = await _monitor.PollAsync(force);
                HandleDeviceSwitch(snapshot);
                // 运营商跟着目标设备走：手动映射优先；没配时按设备 SIM 的 PLMN 自动识别。
                await EnsureAutoCarrierAsync();
                // 设备变了也要重建上下文：两台同运营商的手机不能沿用上一台读数。
                TrafficCarrier effective = EffectiveCarrier();
                string serial = DeviceSelection.EffectiveSerial;
                if (effective != _currentCarrier ||
                    !string.Equals(serial, _trafficDeviceSerial, StringComparison.OrdinalIgnoreCase))
                {
                    SelectTrafficService();
                }
                UpdateUi(snapshot);
                // 设备从无到有时立刻补一次：拔线期间只在后台续期、不更新卡片，
                // 回来后不用等下一个 10 分钟周期就能把余量显示出来。
                if (_hasPolled && snapshot.Connected && !wasConnected && _broadnetAutoEnabled)
                    _ = RefreshAllTrafficSilentlyAsync();
                _hasPolled = true;
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
                // 底部状态行已移除；采集异常保留在顶部状态的悬停提示里，方便排障。
                ConnectionStatus.ToolTip = ex.Message;
            }
            finally { _polling = false; }
        }

        private void UpdateUi(DeviceSnapshot s)
        {
            _lastSnapshot = s;
            _needsDeviceChoice = s.NeedsDeviceSelection;
            _deviceConnected = s.Connected;
            ConnectionStatus.Cursor = _needsDeviceChoice ? Cursors.Hand : Cursors.Arrow;
            ApplySignalSource(s);
            if (!s.Connected)
            {
                ApplyBroadnetDisconnected();
                ConnectionStatus.Text = s.NeedsDeviceSelection
                    ? (s.PreferredSerialMissing ? "预设设备未连接 · 点此重新选择" : "检测到多台设备 · 点此指定")
                    : s.Error;
                ConnectionStatus.Foreground = Orange;
                ApplySignal(GradeSignal(s));
                PushDetails();
                return;
            }

            ConnectionStatus.Text = "● 设备在线";
            ConnectionStatus.Foreground = Green;
            // 蜂窝网络卡片只给信号结论，型号 / 运营商 / RSRP / SINR / 出口 IP 都在详情弹窗里。
            ApplySignal(GradeSignal(s));
            // 电池卡片常驻电量与温度两个结论；供电、电压、热状态、健康度、学习容量在 ⓘ 打开的电池详情弹窗。
            CardBatteryValue.Text = s.BatteryLevel.HasValue ? s.BatteryLevel.Value + "%" : "—";
            CardTemperatureValue.Text = BatteryFormat.Temperature(s.BatteryTemperatureC);
            // 上下行速率并入「蜂窝网络」卡片；共享明细收进该卡右上角 ⓘ 打开的网络详情弹窗。
            // 界面按字节速率显示（KB/s、MB/s）；采集与 CSV 日志仍是 Mbps。
            SetCard("traffic", "↓ " + FormatSpeed(s.DownloadMbps), "↑ " + FormatSpeed(s.UploadMbps));
            Chart.AddSample(s.BatteryTemperatureC, s.RsrpDbm);
            PushDetails();
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
                Notify("系统触发热限制", "当前热状态：" + BatteryFormat.ThermalName(s.ThermalStatus), WF.ToolTipIcon.Warning);
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
                        ? "手机拒绝了 ADB 读取短信的权限，原因见主窗口「近期短信」一行。"
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

        /// <summary>
        /// 根据当前目标设备的运营商映射选用流量服务并重建用量存储。
        /// 无映射时按广电兜底（老用户升级后不用重新设置），但卡片文案提示可去设置指定。
        /// 切换运营商时重置卡片与已读缓存，避免显示上一家的数据。
        /// </summary>
        /// <summary>生效运营商：手动映射优先；没配则用设备 SIM 自动识别的结果。</summary>
        private TrafficCarrier EffectiveCarrier()
        {
            TrafficCarrier manual = AppSettings.CurrentCarrier;
            return manual != TrafficCarrier.None ? manual : _autoDetectedCarrier;
        }

        private void SelectTrafficService()
        {
            TrafficCarrier carrier = EffectiveCarrier();
            _currentCarrier = carrier;
            _trafficService = carrier == TrafficCarrier.Telecom
                ? (ICarrierTrafficService)_telecom : _broadnet;
            _trafficDeviceSerial = DeviceSelection.EffectiveSerial;
            _usageStore = new BroadnetUsageStore(carrier);
            _lastBroadnetTraffic = null;
            _lastUsageAppendedAt = DateTime.MinValue;
            _lastUsageAppendedKb = -1;
            ApplyBroadnetPrompt();
            if (_broadnetTrafficWindow != null && _broadnetTrafficWindow.IsVisible)
                _broadnetTrafficWindow.Close();
            _broadnetTrafficWindow = null;
            _trafficWindowSerial = string.Empty;
            _trafficWindowCarrier = TrafficCarrier.None;
            _trafficService.Trace("service switched to " + CarrierMapping.CodeName(carrier)
                + " serial=" + (string.IsNullOrEmpty(_trafficDeviceSerial) ? "auto" : _trafficDeviceSerial));
        }

        /// <summary>当前余量卡的运营商显示名（无映射时提示去设置）。</summary>
        private string CarrierDisplayName()
        {
            return _currentCarrier == TrafficCarrier.None
                ? "设置设备运营商后查询" : CarrierMapping.DisplayName(_currentCarrier);
        }

        /// <summary>
        /// 异步读数回来后核对上下文：服务实例、运营商和目标设备都得还是发起时的那一个。
        /// 设备切换/识别完成都可能导致上下文变化，旧读数必须丢弃，不能落到新卡上。
        /// </summary>
        private bool IsTrafficContextCurrent(ICarrierTrafficService service, TrafficCarrier carrier, string serial)
        {
            return !_shutdownDone
                && ReferenceEquals(service, _trafficService)
                && carrier == _currentCarrier
                && string.Equals(serial, DeviceSelection.EffectiveSerial, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 广电套餐余量入口。没有登录态时由服务弹出 WebView 让用户自己登录，
        /// 检测到登录后窗口自动关闭；拿到登录态后从营业厅当前页读文本。
        /// 这一步只做「能不能拿到登录态 + 页面原文」的闭环，解析与展示留到后续迭代。
        /// </summary>
        /// <summary>仅供 --broadnet-selftest 自检调用：与主菜单完全同一条链路。</summary>
        internal void RunBroadnetSelfTest()
        {
            _ = OpenBroadnetSafe();
        }

        /// <summary>
        /// 仅供 --broadnet-traffic-test 自检：等登录态就绪后读一次套餐余量，
        /// 把结果格式化写进 trace，便于无人值守地验证提取链路。
        /// </summary>
        /// <summary>自检专用：只确保登录态，不读流量，避免与 OpenBroadnetAsync 并发操作 WebView2。</summary>
        internal async System.Threading.Tasks.Task RunBroadnetLoginOnlyAsync()
        {
            _broadnet.Trace("login-selftest: ensuring login");
            bool ok = await _broadnet.EnsureLoggedInAsync();
            _broadnet.Trace("login-selftest: result=" + ok);
        }

        internal async System.Threading.Tasks.Task RunBroadnetTrafficTestAsync()
        {
            _broadnet.Trace("traffic-selftest: waiting for login");
            for (int i = 0; i < 40 && !_broadnet.IsLoggedIn; i++)
                await System.Threading.Tasks.Task.Delay(1000);
            if (!_broadnet.IsLoggedIn)
            {
                _broadnet.Trace("traffic-selftest: NOT logged in, abort");
                return;
            }
            _broadnet.Trace("traffic-selftest: calling ReadTrafficAsync");
            BroadnetTraffic traffic = await _broadnet.ReadTrafficAsync();
            _broadnet.Trace("traffic-selftest: got traffic=" + (traffic == null ? "null" : traffic.Packages.Count + "pkgs"));
            ApplyBroadnetTraffic(traffic);
            _broadnet.Trace("traffic-selftest: " + FormatBroadnetTraffic(traffic).Replace("\n", " | "));
        }

        private async Task OpenBroadnetSafe()
        {
            _trafficService.Trace("menu click -> OpenBroadnet");
            try
            {
                await OpenBroadnetAsync();
            }
            catch (Exception ex)
            {
                _trafficService.Trace("OpenBroadnetAsync THREW: " + ex);
                Notify(CarrierDisplayName() + "流量查询", "出错：" + ex.Message, WF.ToolTipIcon.Error);
            }
        }

        private async Task OpenBroadnetAsync()
        {
            // 捕获发起时的上下文；WebView 人工登录可能持续很久，期间用户可能改设备/运营商。
            ICarrierTrafficService service = _trafficService;
            TrafficCarrier carrier = _currentCarrier;
            string serial = DeviceSelection.EffectiveSerial;
            BroadnetTraffic traffic = await service.EnsureAndReadTrafficAsync();
            if (!IsTrafficContextCurrent(service, carrier, serial))
            {
                service.Trace("discard stale read after device/carrier switch");
                return;
            }
            if (!service.IsLoggedIn)
            {
                ApplyBroadnetPrompt();
                Notify(CarrierDisplayName() + "流量查询", service.Status, WF.ToolTipIcon.Info);
                return;
            }
            // 已登录但没解析出套餐（站点风控 / 令牌临期 / 页面改版）：别把「暂无余量」写进
            // 卡片冲掉上次的有效读数，也别弹「没读到套餐余量」这种像是查询成功的通知。
            if (traffic == null || traffic.Packages.Count == 0)
            {
                service.Trace("manual query: logged in but no packages, keep last card value");
                Notify(CarrierDisplayName() + "流量查询",
                    "登录有效，但暂时读不到套餐数据；可能是站点临时风控或页面改版，请稍后重试。",
                    WF.ToolTipIcon.Warning);
                return;
            }
            ApplyBroadnetTraffic(traffic);
            string summary = FormatBroadnetTraffic(traffic);
            if (!string.IsNullOrEmpty(summary)) Notify(CarrierDisplayName() + "套餐余量", summary, WF.ToolTipIcon.Info);
        }

        /// <summary>
        /// 每 10 分钟给广电和电信各做一次静默续期：与"当前选中哪台设备/哪家运营商"无关，
        /// 没选设备或设备拔线时照样跑，否则另一家的登录态会悄悄过期。两家服务各自有闸门，
        /// 这里串行调用，避免同时起两个离屏 WebView 抢资源。
        /// </summary>
        private async Task RefreshAllTrafficSilentlyAsync()
        {
            if (_shutdownDone) return;
            await RefreshOneTrafficSilentlyAsync(_broadnet);
            await RefreshOneTrafficSilentlyAsync(_telecom);
        }

        private async Task RefreshOneTrafficSilentlyAsync(ICarrierTrafficService service)
        {
            ICarrierTrafficService selected = _trafficService;
            TrafficCarrier carrier = _currentCarrier;
            string serial = DeviceSelection.EffectiveSerial;
            try
            {
                BroadnetTraffic traffic = await service.RefreshTrafficSilentlyAsync();
                // 非当前运营商：刷新只为续期，绝不把别家读数写进卡片。
                if (!ReferenceEquals(service, selected) ||
                    !IsTrafficContextCurrent(service, carrier, serial))
                {
                    service.Trace("silent keep-alive: session refreshed, not current carrier/device");
                    return;
                }
                // 拔线 / 未选设备期间只在后台续期，卡片维持"未连接"，等设备回来再补数据。
                if (!_deviceConnected)
                {
                    service.Trace("silent keep-alive: device offline, card stays disconnected");
                    return;
                }
                if (traffic != null && traffic.Packages.Count > 0)
                {
                    ApplyBroadnetTraffic(traffic);
                }
                else if (traffic != null && traffic.Packages.Count == 0)
                {
                    service.Trace("silent: no packages resolved, keep last card value");
                }
                else if (service.LastAttemptExpired)
                {
                    ApplyBroadnetPrompt();
                    service.Trace("silent refresh -> expired, card reset to prompt");
                }
                // 其它失败保留上次读数，等下一个周期。
            }
            catch (Exception ex)
            {
                service.Trace("RefreshOneTrafficSilentlyAsync THREW: " + ex);
            }
        }

        /// <summary>设备未连接/未选设备时的卡片状态：只标未连接，登录快照继续在后台续期。</summary>
        private void ApplyBroadnetDisconnected()
        {
            UpdateBroadnetCarrierLabel();
            ApplyBroadnetCard("未连接", "设备未连接 · 后台继续续期登录态");
            BroadnetUsedValue.Text = string.Empty;
            _lastBroadnetTraffic = null;
        }

        /// <summary>登录态不可用时卡片回到初始提示，引导用户点击走人工登录。</summary>
        private void ApplyBroadnetPrompt()
        {
            UpdateBroadnetCarrierLabel();
            ApplyBroadnetCard("点击查询", CarrierDisplayName() + " · 点击登录查询");
            BroadnetUsedValue.Text = string.Empty;
            _lastBroadnetTraffic = null;
            if (_broadnetTrafficWindow != null && _broadnetTrafficWindow.IsVisible)
                _broadnetTrafficWindow.Apply((BroadnetTraffic)null);
        }

        /// <summary>把读到的套餐余量写进第二张卡片，并同步刷新已打开的详情窗。</summary>
        private void ApplyBroadnetTraffic(BroadnetTraffic traffic)
        {
            if (traffic == null)
            {
                BroadnetRemainValue.Text = "读取失败";
                BroadnetBalanceValue.Text = _trafficService.Status;
                BroadnetUsedValue.Text = string.Empty;
                return;
            }
            _lastBroadnetTraffic = traffic;
            _broadnetCheckedAt = DateTime.Now;
            UpdateBroadnetCarrierLabel();
            ApplyBroadnetCard(
                BroadnetRemainSummary(traffic),
                "话费 " + (string.IsNullOrEmpty(traffic.Balance) ? "—" : traffic.Balance + " 元"));
            BroadnetUsedValue.Text = string.IsNullOrEmpty(traffic.MonthlyUsed)
                ? string.Empty : "本月已用 " + traffic.MonthlyUsed;
            RecordUsageSample(traffic);
            if (_broadnetTrafficWindow != null && _broadnetTrafficWindow.IsVisible)
                ApplyTrafficWindow(traffic);
        }

        /// <summary>
        /// 把一次成功读数落进用量时间序列。人工查询与 10 分钟静默刷新都经过 ApplyBroadnetTraffic，
        /// 因此这一个入口覆盖所有采样；同一分钟 + 同一累计值的重复读数跳过。
        /// </summary>
        private void RecordUsageSample(BroadnetTraffic traffic)
        {
            try
            {
                // 坏读数不落盘：站点风控/页面改版时会拿到「已登录但 0 个包」的结果，
                // 累计值是 0。若把它当采样写进去，站点恢复后 133G 的跳变会被差分
                // 全算成那 10 分钟的用量，统计直接报废。
                if (traffic.Packages.Count == 0 || traffic.MonthlyUsedKb <= 0)
                {
                    _trafficService.Trace("RecordUsageSample skipped: no valid packages / zero used");
                    return;
                }
                DateTime now = DateTime.Now;
                if (now - _lastUsageAppendedAt < TimeSpan.FromMinutes(1)
                    && Math.Abs(traffic.MonthlyUsedKb - _lastUsageAppendedKb) < 0.01) return;
                List<KeyValuePair<string, double>> packages = new List<KeyValuePair<string, double>>();
                foreach (BroadnetPackage pkg in traffic.Packages)
                {
                    string name = pkg.Name ?? string.Empty;
                    if (name.Length == 0) continue;
                    packages.Add(new KeyValuePair<string, double>(name, pkg.UsedKb));
                }
                _usageStore.Append(now, traffic.MonthlyUsedKb, traffic.Balance ?? string.Empty, packages);
                _lastUsageAppendedAt = now;
                _lastUsageAppendedKb = traffic.MonthlyUsedKb;
            }
            catch (Exception ex)
            {
                _trafficService.Trace("RecordUsageSample THREW: " + ex.Message);
            }
        }

        /// <summary>打开或刷新详情窗时，把当前余量与当月用量统计一起填进去。</summary>
        private void ApplyTrafficWindow(BroadnetTraffic traffic)
        {
            if (_broadnetTrafficWindow == null) return;
            // 详情窗是在某个设备/运营商下打开的；上下文变了就不往旧窗里填新数据。
            if (_trafficWindowCarrier != _currentCarrier ||
                !string.Equals(_trafficWindowSerial, DeviceSelection.EffectiveSerial, StringComparison.OrdinalIgnoreCase))
                return;
            _broadnetTrafficWindow.Apply(traffic, BuildUsageStats());
        }

        private UsageStats BuildUsageStats()
        {
            try
            {
                DateTime now = DateTime.Now;
                return BroadnetUsageStore.Analyze(_usageStore.ReadCurrentMonth(now), now);
            }
            catch
            {
                return new UsageStats();
            }
        }

        /// <summary>卡片主读数：只累计还能用的包，按 G 求和；显示成一行的「xx G」。</summary>
        internal static string BroadnetRemainSummary(BroadnetTraffic traffic)
        {
            if (traffic == null || traffic.Packages.Count == 0) return "暂无余量";
            double total = 0;
            bool any = false;
            foreach (BroadnetPackage pkg in traffic.Packages)
            {
                double value;
                // DOM 兜底读到的是带单位的文本（如 "85.4 G"），兼容 Android/网页两种格式。
                string raw = (pkg.Remain ?? string.Empty).Trim();
                int space = raw.IndexOf(' ');
                if (space > 0) raw = raw.Substring(0, space);
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value <= 0)
                    continue;
                total += value;
                any = true;
            }
            return any
                ? total.ToString("0.##", CultureInfo.InvariantCulture) + " G"
                : "0 G";
        }

        /// <summary>基础类型参数便于渲染测试用反射直接填样例文案。</summary>
        private void ApplyBroadnetCard(string remain, string balance)
        {
            BroadnetRemainValue.Text = remain;
            BroadnetBalanceValue.Text = balance;
        }

        /// <summary>卡片左上角标出读数归属的运营商，防止和当前设置错位时用户看不出来。</summary>
        private void UpdateBroadnetCarrierLabel()
        {
            BroadnetCarrierValue.Text = _currentCarrier == TrafficCarrier.None
                ? string.Empty : "· " + CarrierMapping.DisplayName(_currentCarrier);
        }

        /// <summary>把套餐余量拼成气泡可读摘要：只留有剩余的包 + 话费，避免文本过长。</summary>
        private static string FormatBroadnetTraffic(BroadnetTraffic traffic)
        {
            if (traffic == null || traffic.Packages.Count == 0)
                return "已登录，但暂时读不到套餐数据；可能是站点临时风控或页面改版，请稍后重试。";

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(traffic.MonthlyUsed))
                sb.Append("本月已用 ").Append(traffic.MonthlyUsed).Append('\n');
            if (!string.IsNullOrEmpty(traffic.Balance))
                sb.Append("话费余额 ").Append(traffic.Balance).Append(" 元\n");
            int shown = 0;
            foreach (BroadnetPackage pkg in traffic.Packages)
            {
                // 结转的过期包（剩余 0）不展示，只留真正还能用的流量。
                double remain;
                if (!double.TryParse(pkg.Remain, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out remain) || remain <= 0)
                    continue;
                string name = pkg.Name.Length > 16 ? pkg.Name.Substring(0, 16) + "…" : pkg.Name;
                sb.Append(name).Append(" 剩余 ").Append(pkg.Remain).Append(' ').Append(pkg.Unit)
                  .Append(" / 共 ").Append(pkg.Total).Append('\n');
                shown++;
            }
            if (shown == 0) sb.Append("当前没有可用流量包（剩余均为 0）\n");
            return sb.ToString().TrimEnd('\n');
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

        /// <summary>
        /// 出口 IP 独立于 ADB 采集：启动、网络地址变化、每 10 分钟各一次。
        /// 查询失败时保留上一次结果，避免离线时卡片来回闪烁。
        /// </summary>
        private async Task RefreshPublicIpAsync()
        {
            if (_publicIpQueryRunning || _shutdownDone) return;
            _publicIpQueryRunning = true;
            CancellationTokenSource source = new CancellationTokenSource();
            _publicIpCancellation = source;
            try
            {
                PublicIpResult result = await _publicIp.QueryAsync(source.Token);
                if (!source.IsCancellationRequested && !_shutdownDone)
                    ApplyPublicIp(result.Ipv4, result.Ipv6, result.CheckedAt);
            }
            catch (OperationCanceledException) { }
            catch { /* 离线或回显服务不可达：保留上一次结果 */ }
            finally
            {
                _publicIpQueryRunning = false;
                if (ReferenceEquals(_publicIpCancellation, source)) _publicIpCancellation = null;
                source.Dispose();
            }
        }

        /// <summary>参数是基础类型，便于渲染测试用反射直接填充样例地址。</summary>
        private void ApplyPublicIp(string ipv4, string ipv6, DateTime checkedAt)
        {
            _publicIpv4 = ipv4 ?? "";
            _publicIpv6 = ipv6 ?? "";
            _publicIpCheckedAt = checkedAt;
            PushDetails();
        }

        private void NetworkCard_MouseEnter(object sender, MouseEventArgs e)
        {
            NetworkInfoButton.Visibility = Visibility.Visible;
        }

        private void NetworkCard_MouseLeave(object sender, MouseEventArgs e)
        {
            NetworkInfoButton.Visibility = Visibility.Hidden;
        }

        private void BroadnetCard_MouseEnter(object sender, MouseEventArgs e)
        {
            BroadnetInfoButton.Visibility = Visibility.Visible;
        }

        private void BroadnetCard_MouseLeave(object sender, MouseEventArgs e)
        {
            BroadnetInfoButton.Visibility = Visibility.Hidden;
        }

        private void BatteryCard_MouseEnter(object sender, MouseEventArgs e)
        {
            BatteryInfoButton.Visibility = Visibility.Visible;
        }

        private void BatteryCard_MouseLeave(object sender, MouseEventArgs e)
        {
            BatteryInfoButton.Visibility = Visibility.Hidden;
        }

        private void NetworkInfo_Click(object sender, MouseButtonEventArgs e)
        {
            OpenNetworkDetails(false);
        }

        /// <summary>点卡片任意位置等同「广电流量查询」：无登录态弹 WebView，有则刷新读数。</summary>
        private void BroadnetCard_Click(object sender, MouseButtonEventArgs e)
        {
            _ = OpenBroadnetSafe();
        }

        private void BroadnetInfo_Click(object sender, MouseButtonEventArgs e)
        {
            OpenBroadnetTrafficDetails();
        }

        /// <summary>第二张卡片的 ⓘ 打开套餐明细；重复点击只激活已有窗口。</summary>
        private void OpenBroadnetTrafficDetails()
        {
            if (_broadnetTrafficWindow != null && _broadnetTrafficWindow.IsVisible)
            {
                _broadnetTrafficWindow.Activate();
                return;
            }
            _broadnetTrafficWindow = new BroadnetTrafficWindow(
                delegate { _ = OpenBroadnetSafe(); });
            _trafficWindowSerial = DeviceSelection.EffectiveSerial;
            _trafficWindowCarrier = _currentCarrier;
            _broadnetTrafficWindow.SetCarrier(CarrierDisplayName());
            if (IsVisible) _broadnetTrafficWindow.Owner = this;
            _broadnetTrafficWindow.Closed += delegate
            {
                _broadnetTrafficWindow = null;
                _trafficWindowSerial = string.Empty;
                _trafficWindowCarrier = TrafficCarrier.None;
            };
            _broadnetTrafficWindow.Show();
            if (_lastBroadnetTraffic != null) ApplyTrafficWindow(_lastBroadnetTraffic);
        }

        private void BatteryInfo_Click(object sender, MouseButtonEventArgs e)
        {
            OpenBatteryDetails();
        }

        /// <summary>「蜂窝网络」卡片的 ⓘ 打开网络详情；需要时定位到网络共享分组。</summary>
        private void OpenNetworkDetails(bool focusTethering)
        {
            if (_networkDetailsWindow != null && _networkDetailsWindow.IsVisible)
            {
                _networkDetailsWindow.Activate();
                if (focusTethering) _networkDetailsWindow.FocusTethering();
                return;
            }
            _networkDetailsWindow = new NetworkDetailsWindow { Owner = this };
            _networkDetailsWindow.Closed += delegate { _networkDetailsWindow = null; };
            _networkDetailsWindow.Show();
            PushDetails();
            if (focusTethering) _networkDetailsWindow.FocusTethering();
        }

        /// <summary>第三张卡片的 ⓘ 打开电池详情；重复点击只激活已有窗口。</summary>
        private void OpenBatteryDetails()
        {
            if (_batteryDetailsWindow != null && _batteryDetailsWindow.IsVisible)
            {
                _batteryDetailsWindow.Activate();
                return;
            }
            _batteryDetailsWindow = new BatteryDetailsWindow { Owner = this };
            _batteryDetailsWindow.Closed += delegate { _batteryDetailsWindow = null; };
            _batteryDetailsWindow.Show();
            PushDetails();
        }

        /// <summary>两个详情弹窗打开期间都跟随 2.5 秒采集和出口 IP 查询实时刷新。</summary>
        private void PushDetails()
        {
            if (_networkDetailsWindow != null && _networkDetailsWindow.IsVisible)
                _networkDetailsWindow.Apply(_lastSnapshot, _publicIpv4, _publicIpv6, _publicIpCheckedAt);
            if (_batteryDetailsWindow != null && _batteryDetailsWindow.IsVisible)
                _batteryDetailsWindow.Apply(_lastSnapshot);
        }

        /// <summary>信号格数默认指当前跑流量的那张卡；定位不到数据卡时不乱标。</summary>
        private void ApplySignalSource(DeviceSnapshot s)
        {
            SignalSourceText.Text = s != null && s.Connected && s.SignalPhoneId.HasValue
                ? "· 流量卡信号" : string.Empty;
        }

        private void ApplySignal(SignalVerdict verdict)
        {
            System.Windows.Controls.Border[] bars =
            {
                SignalBar1, SignalBar2, SignalBar3, SignalBar4, SignalBar5
            };
            for (int i = 0; i < bars.Length; i++)
                bars[i].Background = i < verdict.Bars ? verdict.Brush : BarOff;
        }

        /// <summary>
        /// 把 RSRP（有多强）与 SINR（有多干净）各映射到 0~4，取较差的一档，
        /// 避免「信号强但干扰大」被误判成好信号；缺一项时只看另一项。
        /// </summary>
        private static SignalVerdict GradeSignal(DeviceSnapshot s)
        {
            if (s == null || !s.Connected)
                return new SignalVerdict("设备未连接", Muted, 0);

            int? rsrp = s.RsrpDbm.HasValue ? (int?)RsrpScore(s.RsrpDbm.Value) : null;
            int? sinr = s.SinrDb.HasValue ? (int?)SinrScore(s.SinrDb.Value) : null;
            if (!rsrp.HasValue && !sinr.HasValue)
                return new SignalVerdict("信号未知", Muted, 0);

            int score = 4;
            if (rsrp.HasValue) score = Math.Min(score, rsrp.Value);
            if (sinr.HasValue) score = Math.Min(score, sinr.Value);
            switch (score)
            {
                case 4: return new SignalVerdict("信号极好", Green, 5);
                case 3: return new SignalVerdict("信号良好", Green, 4);
                case 2: return new SignalVerdict("信号一般", Orange, 3);
                case 1: return new SignalVerdict("信号较弱", Red, 2);
                default: return new SignalVerdict("信号很差", Red, 1);
            }
        }

        private static int RsrpScore(int dbm)
        {
            if (dbm >= -80) return 4;
            if (dbm >= -90) return 3;
            if (dbm >= -105) return 2;
            if (dbm >= -110) return 1;
            return 0;
        }

        private static int SinrScore(int db)
        {
            if (db >= 20) return 4;
            if (db >= 13) return 3;
            if (db >= 5) return 2;
            if (db >= 0) return 1;
            return 0;
        }

        private sealed class SignalVerdict
        {
            public readonly string Title;
            public readonly Brush Brush;
            public readonly int Bars;

            public SignalVerdict(string title, Brush brush, int bars)
            {
                Title = title;
                Brush = brush;
                Bars = bars;
            }
        }

        private void SetCard(string key, string value, string detail)
        {
            CardLabels card = _cards[key];
            card.Value.Text = value;
            card.Detail.Text = detail;
        }

        private void CheckUsbTetherWatchdog(DeviceSnapshot snapshot)
        {
            bool busy = _usbTetherRunning || _usbWatchdogRunning;
            bool recover = _usbWatchdog.Observe(snapshot, DateTime.UtcNow,
                AppSettings.AutoRecoverUsbTethering, AppSettings.AutoEnableUsbTethering, busy);
            if (recover) StartUsbTetherRecovery();
        }

        private async void StartUsbTetherRecovery()
        {
            if (_usbWatchdogRunning || _usbTetherRunning) return;
            _usbWatchdogRunning = true;
            CancellationTokenSource source = new CancellationTokenSource();
            _usbWatchdogCancellation = source;
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
                if (result.Changed) Notify("USB 网络共享", "已自动恢复 USB 网络共享。", WF.ToolTipIcon.Info);
            }
            else
            {
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
                StartPcHotspotIfPending();
                return;
            }

            CancellationTokenSource source = new CancellationTokenSource();
            _tetherCancellation = source;
            _usbTetherRunning = true;
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
            if (!result.Succeeded) Notify("USB 网络共享", result.Message, WF.ToolTipIcon.Warning);
            else if (result.Changed) Notify("USB 网络共享", result.Message, WF.ToolTipIcon.Info);
            StartPcHotspotIfPending();
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
            DeviceSelection.ResetActiveSerial(); // 设置里刚保存了新预设，清掉临时故障转移目标
            _carrierDetector.Invalidate(null); // 设置里可能换了设备/运营商，下轮重新识别
            _autoDetectedSerial = string.Empty;
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

        /// <summary>界面按字节速率显示（KB / MB 以 1024 进制）；采集和 CSV 日志仍是 Mbps。</summary>
        private static string FormatSpeed(double mbps)
        {
            // Mbps 是采集侧由「字节差 / 秒 × 8 / 1e6」算出的，这里还原成字节 / 秒。
            double bytesPerSecond = mbps * 1000000.0 / 8.0;
            if (bytesPerSecond >= 1024.0 * 1024.0)
                return (bytesPerSecond / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " MB/s";
            return (bytesPerSecond / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s";
        }

        // 电池字段的格式化统一放在 BatteryFormat（BatteryDetailsWindow.xaml.cs），
        // 温度告警与电池详情弹窗共用一份文案；手机侧共享状态的格式化在网络详情弹窗里。

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
