using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AdbDeviceVitals.Services;

namespace AdbDeviceVitals
{
    /// <summary>
    /// 中国广电网上营业厅的「登录态」管理。设计口径来自 2026-11-15 的抓包调研：
    ///
    /// - 登录态不在 cookie，而在 sessionStorage（标签页级，关标签即丢）；
    /// - 业务请求要带页面 JS 现算的 Access 头和加密请求体，导出 cookie 重放无效；
    /// - 所以必须保留一个真实的浏览器标签页，不能自己发 HTTP。
    ///
    /// 本类因此做成：持有一个 WebView2 登录窗口（隐藏即保活），人工登录一次后把
    /// sessionStorage 快照落盘；下次启动把快照注回去，省掉重复登录。全程不碰账号密码，
    /// 也不代填验证码 / 滑块——那一步必须人工。
    ///
    /// 注意本文件刻意放在项目根目录而不是 Services\：测试项目用通配符链接 Services\*.cs，
    /// 它没有 WebView2 引用，放进去会编译失败。
    /// </summary>
    internal sealed class BroadnetSessionService : ICarrierTrafficService
    {
        internal const string LoginUrl = "https://www.10099.com.cn/login.html";
        // 套餐余量在「个人中心」页，登录后跳到 index.html 只是首页，DOM 里没有流量数据。
        private const string PersonalCenterUrl = "https://www.10099.com.cn/personal-center-number-order.html";
        private const string DefaultPathPrefix = "broadnet";
        private string _pathPrefix = DefaultPathPrefix;

        // 登录判据：优先认 broadnetUserSessionId（调研确认登录后写入、登出时清除）。
        // 辅以 broadnetUserPhoneInfo.isLogin，再兜底扫描含 session/token/login 的键，
        // 防止站点改了键名就检测不到。返回 JSON 字符串。
        private const string DetectScript = @"(function(){
  var sid='', phone='', logged=false;
  var url = location.href;
  var onLoginPage = /login/i.test(url);
  try {
    sid = sessionStorage.getItem('broadnetUserSessionId') || '';
    var info = sessionStorage.getItem('broadnetUserPhoneInfo') || '';
    if (info) {
      try {
        var o = JSON.parse(info);
        if (o) {
          phone = String(o.phone || o.mobile || o.phoneNo || o.userPhone || '');
          var v = o.isLogin;
          if (v === true || v === 1 || v === '1' || v === 'true') logged = true;
          if (phone.length > 6) logged = true;
        }
      } catch(e) {
        if (/isLogin/i.test(info)) logged = true;
      }
    }
    // 只有 sessionId 还不够：恢复快照后 sessionStorage 里会有旧 sessionId，若服务端
    // session 已失效，页面仍停在登录页。必须等页面跳离登录页才算真正登录。
    if (sid.length > 8 && !onLoginPage) logged = true;
    if (!logged && !onLoginPage) {
      for (var i=0;i<sessionStorage.length;i++) {
        var k = sessionStorage.key(i) || '';
        var val = sessionStorage.getItem(k) || '';
        if (/^broadnet.*session.*id$/i.test(k) && val.length > 8) { logged=true; sid=val; break; }
      }
    }
  } catch(e) {}
  return JSON.stringify({logged: logged?1:0, phone: phone, sidLen: sid.length, url: url, onLoginPage: onLoginPage?1:0});
})()";

        private const string DumpScript = @"(function(){
  var o={};
  try { for (var i=0;i<sessionStorage.length;i++){ var k=sessionStorage.key(i); if(k) o[k]=sessionStorage.getItem(k); } }
  catch(e){}
  return JSON.stringify(o);
})()";

        private readonly object _gate = new object();
        private readonly DispatcherTimer _pollTimer;

        private BroadnetLoginWindow _window;
        private string _snapshotPath;
        private string _userDataFolder;
        private bool _loggedIn;
        private bool _lastLoggedForTrace;
        private bool _waiting;
        private bool _apiWarmed; // 是否已为本次会话预热过个人中心接口缓存
        private string _phone = string.Empty;
        private string _status = "未检测";
        private TaskCompletionSource<bool> _loginCompletion;
        // 后台静默刷新与人工登录共用同一个 WebView2，不能并发导航，否则内核会崩。
        private readonly SemaphoreSlim _trafficGate = new SemaphoreSlim(1, 1);
        private bool _silentWindowCreated; // 静默恢复时创建的隐藏窗口（尚未 Show）

        internal BroadnetSessionService()
        {
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _pollTimer.Tick += delegate { _ = DetectOnceAsync(false); };
        }

        /// <summary>由 App 启动后调用一次。目录不存在时创建，失败不阻断程序。</summary>
        public void Initialize(string configDirectory, string pathPrefix)
        {
            _pathPrefix = string.IsNullOrWhiteSpace(pathPrefix) ? DefaultPathPrefix : pathPrefix.Trim();
            try
            {
                Directory.CreateDirectory(configDirectory);
                _snapshotPath = Path.Combine(configDirectory, _pathPrefix + "-session.json");
                _userDataFolder = Path.Combine(configDirectory, _pathPrefix + "-webview");
                Directory.CreateDirectory(_userDataFolder);
            }
            catch (Exception ex)
            {
                _status = "无法创建广电数据目录：" + ex.Message;
            }
        }

        public bool IsLoggedIn { get { lock (_gate) return _loggedIn; } }
        internal string Phone { get { lock (_gate) return _phone; } }
        public string Status { get { lock (_gate) return _status; } }

        /// <summary>是否有可恢复的登录快照。没有就不该为「自动查询」初始化 WebView2。</summary>
        public bool HasSnapshot
        {
            get { return !string.IsNullOrEmpty(_snapshotPath) && File.Exists(_snapshotPath); }
        }

        /// <summary>最近一次静默刷新是否判定会话过期；主窗口据此把卡片退回「点击查询」。</summary>
        public bool LastAttemptExpired { get; private set; }

        /// <summary>
        /// 确保处于已登录状态：已登录直接返回 true；否则弹出 WebView 窗口交给用户人工登录，
        /// 检测到登录（或用户点「我已登录」）后自动隐藏窗口并返回 true；用户关闭窗口返回 false。
        /// 必须在 UI 线程调用。
        /// </summary>
        public async Task<bool> EnsureLoggedInAsync()
        {
            if (IsLoggedIn) return true;
            if (string.IsNullOrEmpty(_userDataFolder))
            {
                SetStatus("广电目录不可用，无法打开登录页");
                return false;
            }

            try
            {
                WriteTrace("EnsureLoggedIn start");
                if (_window == null)
                {
                    _window = new BroadnetLoginWindow();
                    _window.TraceHook = delegate(string msg) { WriteTrace(msg); };
                    // 只把「可见」的主窗口设为 Owner。程序常驻托盘时 MainWindow 是 Hide
                    // 状态，把隐藏窗口设为 Owner 会让登录窗口一 Show 就跟着不可见，表现成
                    // 「点了没反应」。渲染测试 / 无主窗口场景同理直接跳过。
                    Window owner = System.Windows.Application.Current != null
                        ? System.Windows.Application.Current.MainWindow : null;
                    if (owner != null && owner.IsVisible)
                        _window.Owner = owner;
                    _window.UserClosed += delegate { FinishWait(false, "已取消登录"); };
                    _window.LoginConfirmed += delegate { _ = DetectOnceAsync(true); };
                    // 先显示窗口给出即时反馈：点菜单立刻有响应，且 WebView2 内核在窗口
                    // 已有真实 HWND 后创建更可靠（隐藏窗口可能拿不到句柄）。
                    SetStatus("正在初始化浏览器…");
                    _window.Show();
                    _window.Activate();
                    WriteTrace("window shown");
                    await _window.InitializeAsync(_userDataFolder);
                    WriteTrace("webview2 initialized");
                }
                else if (_silentWindowCreated && !_window.IsVisible)
                {
                    // 这个窗口是后台静默刷新建的（离屏且透明）。轮到人工登录必须恢复成正常窗口：
                    // 居中、不透明、非离屏，否则用户点了卡片看不到登录页，表现成「又没反应」。
                    _window.Opacity = 1;
                    _window.Left = (System.Windows.SystemParameters.PrimaryScreenWidth - _window.Width) / 2;
                    _window.Top = (System.Windows.SystemParameters.PrimaryScreenHeight - _window.Height) / 2;
                    _window.Show();
                    _window.Activate();
                    WriteTrace("silent window restored for manual login");
                }

                // 顺序很关键：先让登录页在 10099 origin 下加载完成，再把快照注回
                // sessionStorage（注入到 about:blank 的值在跳域后会丢），最后刷新页面
                // 让站点脚本读到已登录的 sessionId。
                SetStatus("正在打开广电登录页…");
                WriteTrace("navigating " + LoginUrl);
                await _window.NavigateAndWaitAsync(LoginUrl);
                WriteTrace("navigation done");

                bool restored = await RestoreSnapshotAsync();
                WriteTrace("snapshot restored=" + restored);
                if (restored)
                {
                    SetStatus("已恢复上次的登录态，正在校验…");
                    // 关键：恢复后直接导航「个人中心」页，绝不能重新导航 login.html。
                    // 实测（2026-11-16）：登录页脚本会把刚注入 sessionStorage 的有效 sessionId
                    // 冲掉，且 WAF 挑战循环让 NavigationCompleted 一直不触发、吃满 20s 超时。
                    // 个人中心页：session 有效就直接渲染数据；失效才会被站点踢回 login.html。
                    // 导航前打开接口捕获：这次加载发出的 qryUserRes/qryBillInfo 响应正好供
                    // 随后的 ReadTrafficAsync 使用（重复导航同一 URL 不会再发接口）。
                    _window.BeginApiCapture();
                    await _window.NavigateAndWaitAsync(PersonalCenterUrl);
                    WriteTrace("restored navigation to personal center done");
                    string checkRaw = await _window.EvaluateAsync(DetectScript);
                    Detection check = ParseDetection(checkRaw);
                    WriteTrace("restored check logged=" + (check.Logged ? "1" : "0")
                        + " onLoginPage=" + (check.OnLoginPage ? "1" : "0")
                        + " resCached=" + (_window.GetApiBody("qryUserRes") == null ? "0" : "1"));
                    // 恢复路径已在导航个人中心时捕获了接口，标记为已预热，避免
                    // DetectOnceAsync 成功后再重复导航；缓存保留给 ReadTrafficAsync。
                    if (check.Logged && !check.OnLoginPage) _apiWarmed = true;
                    if (!check.Logged || check.OnLoginPage)
                    {
                        // 会话过期：删掉失效快照，窗口保持打开让用户重新登录，
                        // 否则下次启动还会注入同一个坏会话，陷入「秒关但没登录」。
                        TryDeleteSnapshot();
                        SetStatus("上次登录已过期，请在窗口中重新登录 10099");
                    }
                    // 恢复成功时什么都不用做：下面的 _pollTimer 下一次 tick 会在个人中心页
                    // 检测到 logged=1，走 DetectOnceAsync 的正常收尾（存快照 + 隐藏窗口 +
                    // 解除 EnsureLoggedIn 的等待）。
                }
                else
                {
                    SetStatus("请在窗口中登录 10099");
                }

                TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
                lock (_gate)
                {
                    _loginCompletion = completion;
                    _waiting = true;
                }
                _pollTimer.Start();
                return await completion.Task;
            }
            catch (Exception ex)
            {
                WriteTrace("ERROR " + ex);
                WriteErrorLog(ex);
                SetStatus("打开广电登录页失败：" + ex.GetType().Name + ": " + ex.Message
                    + "（详见 ~/adb-device-vitals/broadnet-error.log）");
                FinishWait(false, _status);
                return false;
            }
        }

        /// <summary>
        /// 人工登录入口（只登录不读流量）：与后台静默刷新串行化。
        /// </summary>
        public async Task<bool> EnsureLoggedInSerializedAsync()
        {
            await _trafficGate.WaitAsync();
            try { return await EnsureLoggedInAsync(); }
            finally { _trafficGate.Release(); }
        }

        /// <summary>
        /// 人工「点卡片查询」完整链路：登录 + 读余量整段持闸，杜绝中途被静默刷新插入
        /// 造成两次导航并发（WebView2 并发导航会直接崩进程）。
        /// </summary>
        public async Task<BroadnetTraffic> EnsureAndReadTrafficAsync()
        {
            await _trafficGate.WaitAsync();
            try
            {
                if (!await EnsureLoggedInAsync()) return null;
                return await ReadTrafficAsync();
            }
            finally { _trafficGate.Release(); }
        }

        /// <summary>
        /// 后台静默刷新：只在已有登录快照 / 已登录时工作，全程不弹窗。
        /// 用带时间戳的 URL 强制重新导航个人中心，让站点重发业务接口以拿到最新余量。
        /// 会话过期则删快照、清登录态，由调用方把卡片退回「点击查询」。
        /// 与人工登录共用 _trafficGate 串行化，避免并发导航搞崩 WebView2。
        /// </summary>
        public async Task<BroadnetTraffic> RefreshTrafficSilentlyAsync()
        {
            LastAttemptExpired = false;
            // 没有任何可复用的登录态：连 WebView2 都不初始化，直接放弃。
            if (!HasSnapshot && !IsLoggedIn)
            {
                WriteTrace("silent: skip, no snapshot and not logged in");
                return null;
            }
            await _trafficGate.WaitAsync();
            try
            {
                WriteTrace("silent: begin, hasSnapshot=" + HasSnapshot + " logged=" + IsLoggedIn);
                // 关键：完全没 Show 过的窗口，WebView2 内核起不来（实测卡死在 InitializeAsync）。
                // 用 Opacity=0 + 屏幕外位置 Show 一次拿到真实 HWND，用户看不见，初始化后立刻 Hide。
                if (_window == null)
                {
                    _window = new BroadnetLoginWindow();
                    _window.TraceHook = delegate(string msg) { WriteTrace(msg); };
                    _window.UserClosed += delegate { FinishWait(false, "已取消登录"); };
                    _window.LoginConfirmed += delegate { _ = DetectOnceAsync(true); };
                    _silentWindowCreated = true;
                    _window.Opacity = 0;
                    _window.ShowInTaskbar = false;
                    _window.WindowStartupLocation = WindowStartupLocation.Manual;
                    _window.Left = -32000;
                    _window.Top = -32000;
                    _window.Show();
                    WriteTrace("silent: offscreen window shown");
                    await _window.InitializeAsync(_userDataFolder);
                    WriteTrace("silent: webview2 initialized (offscreen)");
                }

                if (!IsLoggedIn)
                {
                    // 先在 10099 origin 下加载页面，再注入快照（注入 about:blank 会在跳域时丢）。
                    await _window.NavigateAndWaitAsync(LoginUrl);
                    bool restored = await RestoreSnapshotAsync();
                    WriteTrace("silent: snapshot restored=" + restored);
                    if (!restored) { MarkExpired("快照读取失败"); return null; }
                }

                // 强制刷新：时间戳参数让 WebView2 不走缓存，站点会重新发 qryUserRes / qryBillInfo。
                string freshUrl = PersonalCenterUrl + "?_t=" + DateTime.UtcNow.Ticks.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                _window.BeginApiCapture();
                bool navigated = await _window.NavigateAndWaitAsync(freshUrl);
                // qryBillInfo / qrySubscribeBusiness 等比 qryUserRes 晚到（异步 ajax），
                // 等一小会儿再收网，否则话费余额会因 capture 已关而丢失。
                for (int i = 0; i < 6 && _window.GetApiBody("qryBillInfo") == null; i++)
                    await Task.Delay(300);
                Detection check = ParseDetection(await _window.EvaluateAsync(DetectScript));
                WriteTrace("silent: check logged=" + (check.Logged ? "1" : "0")
                    + " onLoginPage=" + (check.OnLoginPage ? "1" : "0") + " navigated=" + (navigated ? "1" : "0"));
                if (!check.Logged || check.OnLoginPage)
                {
                    _window.EndApiCapture();
                    // 关键：导航超时（20s 没等到 NavigationCompleted）且页面不在登录页，
                    // 说明页面根本没加载出来（网络抖动/站点慢），此时 sessionStorage 读不到
                    // 是「没加载」而非「已过期」。保留快照，下个 10min 周期重试；
                    // 否则一次抖动就把仍有效的登录态删掉，逼用户重新登录（2026-11-20 21:24 实测）。
                    // 真正过期时站点会跳到 login.html → onLoginPage=1，照常 MarkExpired。
                    if (!navigated && !check.OnLoginPage)
                    {
                        WriteTrace("silent: navigation timed out before page load, keep snapshot for retry");
                        return null;
                    }
                    MarkExpired("登录态已过期");
                    return null;
                }

                _apiWarmed = true;
                lock (_gate) { _loggedIn = true; _phone = check.Phone; }
                _window.Phone = MaskPhone(check.Phone);
                await SaveSnapshotAsync();
                SetStatus("已登录" + (check.Phone.Length > 0 ? " · " + MaskPhone(check.Phone) : ""));
                _window.Status = _status;

                BroadnetTraffic traffic = await ReadTrafficAsync();
                WriteTrace("silent: result=" + (traffic == null ? "null" : traffic.Packages.Count + "pkgs"));
                return traffic;
            }
            catch (Exception ex)
            {
                WriteTrace("silent ERR " + ex);
                WriteErrorLog(ex);
                try { _window.EndApiCapture(); } catch { }
                return null;
            }
            finally
            {
                // 静默窗口用完即隐（sessionStorage 随窗口保活），不留透明离屏窗口。
                // 闸门保证此刻不会有人工登录在用这个窗口。
                if (_silentWindowCreated && _window != null && _window.IsVisible)
                {
                    try { _window.Hide(); } catch { }
                }
                _trafficGate.Release();
            }
        }

        /// <summary>静默刷新判定会话失效：删快照、清标记，让下次点击走人工登录。</summary>
        private void MarkExpired(string reason)
        {
            LastAttemptExpired = true;
            TryDeleteSnapshot();
            lock (_gate) { _loggedIn = false; _phone = string.Empty; }
            SetStatus(reason + "，请点击余量卡片重新登录");
            WriteTrace("silent: " + reason + ", snapshot deleted");
        }

        /// <summary>外部（托盘入口）也可记一笔，用于确认菜单点击是否真的进了流程。</summary>
        public void Trace(string message) { WriteTrace(message); }

        /// <summary>把阶段进展写到 trace 日志，便于「点了没反应」时定位卡在哪一步。</summary>
        private void WriteTrace(string message)
        {
            try
            {
                string dir = string.IsNullOrEmpty(_snapshotPath)
                    ? AppSettings.ConfigDirectory
                    : Path.GetDirectoryName(_snapshotPath);
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, _pathPrefix + "-trace.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
            catch
            {
            }
        }

        /// <summary>主动查一次登录态。窗口隐藏后也由定时器驱动，掉了就提示重新登录。</summary>
        internal async Task DetectOnceAsync(bool fromUserConfirm)
        {
            if (_window == null || _window.BrowserView.CoreWebView2 == null) return;
            try
            {
                string raw = await _window.EvaluateAsync(DetectScript);
                Detection detection = ParseDetection(raw);
                // 节流留痕：判定结果变化时才写，避免每 1.5 秒刷屏。定位「没登录就自动关」。
                if (detection.Logged != _lastLoggedForTrace || fromUserConfirm)
                {
                    _lastLoggedForTrace = detection.Logged;
                    WriteTrace("detect logged=" + (detection.Logged ? "1" : "0")
                        + " raw=" + (raw == null ? "null" : raw));
                }
                if (!detection.Logged)
                {
                    if (fromUserConfirm) SetStatus("还没检测到登录态，请完成登录后重试");
                    return;
                }

                // 登录成功：先存快照（sessionStorage 关标签即丢），再收尾。
                await SaveSnapshotAsync();
                lock (_gate)
                {
                    _loggedIn = true;
                    _phone = detection.Phone;
                }
                _window.Phone = MaskPhone(detection.Phone);
                _pollTimer.Stop();
                SetStatus("已登录" + (detection.Phone.Length > 0 ? " · " + MaskPhone(detection.Phone) : ""));
                _window.Status = _status;

                // 首次人工登录成功后，页面可能停在首页，没发出个人中心的业务接口。
                // 预热一次：开捕获 + 导航个人中心页，让 qryUserRes/qryBillInfo 进缓存，
                // 供随后的 ReadTrafficAsync 直接用（方案 B）。失败也不影响 DOM 兜底。
                // 恢复快照路径在 EnsureLoggedInAsync 里已预热，不要重复导航。
                if (!_apiWarmed)
                {
                    _apiWarmed = true;
                    try
                    {
                        _window.BeginApiCapture();
                        await _window.NavigateAndWaitAsync(PersonalCenterUrl);
                        await Task.Delay(1200); // 等 ajax 响应异步写进缓存
                        _window.EndApiCapture();
                        WriteTrace("api warmed: res=" + (_window.GetApiBody("qryUserRes") == null ? "null" : "ok"));
                    }
                    catch (Exception ex)
                    {
                        WriteTrace("api warm ERR " + ex.Message);
                        _window.EndApiCapture();
                    }
                }

                // 从用户视角「窗口关掉了」；实际是隐藏，保住标签页里的 sessionStorage。
                _window.Hide();
                FinishWait(true, _status);
            }
            catch (Exception ex)
            {
                if (fromUserConfirm) SetStatus("检测登录态失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 读取套餐余量，双通道：
        ///   B（优先）— 导航个人中心页时拦截站点自己发出的业务接口响应（qryUserRes /
        ///   qryBillInfo），从 JSON 直接解析。站点用正确的 Access 头+加密体发请求，我们只读
        ///   响应，不碰签名；数据比 DOM 干净（字节精确换算、话费直接是元，无显示精度损失）。
        ///   A（兜底）— 接口没抓到或解析失败时，回退到稳定的 CSS class DOM 提取。
        /// 两条路都拿不到才返回 null。字段/URL 来自 2026-11-16 抓包实测，站点改版需同步更新。
        /// </summary>
        public async Task<BroadnetTraffic> ReadTrafficAsync()
        {
            if (!IsLoggedIn || _window == null || _window.BrowserView.CoreWebView2 == null) return null;
            try
            {
                // 缓存优先：登录/恢复阶段导航个人中心页时就已经打开了捕获，接口响应
                // 那时已写进缓存。重复导航同一 URL 不会再发接口（WebView2 缓存 + WAF），
                // 所以先用现成缓存；确实没有才重新导航并等一次。
                string resBody = _window.GetApiBody("qryUserRes");
                string billBody = _window.GetApiBody("qryBillInfo");
                WriteTrace("traffic: cached res=" + (resBody == null ? "null" : resBody.Length + "B")
                    + " bill=" + (billBody == null ? "null" : billBody.Length + "B"));
                if (resBody == null)
                {
                    WriteTrace("traffic: capture on, navigating");
                    _window.BeginApiCapture();
                    await _window.NavigateAndWaitAsync(PersonalCenterUrl);
                    for (int i = 0; i < 6; i++)
                    {
                        if (_window.GetApiBody("qryUserRes") != null) break;
                        await Task.Delay(400);
                    }
                    resBody = _window.GetApiBody("qryUserRes");
                    billBody = _window.GetApiBody("qryBillInfo");
                    _window.EndApiCapture();
                    WriteTrace("traffic: after nav res=" + (resBody == null ? "null" : resBody.Length + "B"));
                }

                // 方案 B：接口 JSON
                BroadnetTraffic apiTraffic = ParseApiTraffic(resBody, billBody);
                if (apiTraffic != null && apiTraffic.Packages.Count > 0)
                {
                    WriteTrace("traffic via API: " + apiTraffic.Packages.Count + " packages, balance=" + apiTraffic.Balance
                        + ", used=" + apiTraffic.MonthlyUsed);
                    _window.EndApiCapture();
                    return apiTraffic;
                }
                WriteTrace("API path unavailable (res=" + (resBody == null ? "null" : "ok")
                    + ", bill=" + (billBody == null ? "null" : "ok") + "), fallback to DOM");

                // 方案 A：DOM 兜底（页面此刻已停在个人中心，DOM 里有渲染好的余量）。
                _window.EndApiCapture();
                string raw = await _window.EvaluateAsync(TrafficExtractScript);
                BroadnetTraffic traffic = ParseTraffic(raw);
                WriteTrace(traffic == null ? "traffic DOM parse returned null"
                    : "traffic via DOM: " + traffic.Packages.Count + " packages, balance=" + traffic.Balance);
                return traffic;
            }
            catch (Exception ex)
            {
                WriteTrace("ReadTrafficAsync ERROR " + ex);
                // 方案 B 抛错也不能丢数据：再尝试一次纯 DOM。
                try
                {
                    _window.EndApiCapture();
                    string raw = await _window.EvaluateAsync(TrafficExtractScript);
                    return ParseTraffic(raw);
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// 解析拦截到的业务接口 JSON。qryUserRes 的 userResList 每项：
        /// discntName 名称、highFee 总量(字节)、balance 剩余(字节)、addupValue 已用(字节)，
        /// 除以 1024*1024 得 G；qryBillInfo 的 balance 直接是「85.40」元。
        /// 任一关键字段结构不匹配就返回 null，让调用方回退 DOM。
        /// </summary>
        private static BroadnetTraffic ParseApiTraffic(string resBody, string billBody)
        {
            if (string.IsNullOrWhiteSpace(resBody)) return null;
            BroadnetTraffic result = new BroadnetTraffic();
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(resBody))
                {
                    JsonElement root = doc.RootElement;
                    JsonElement list;
                    // 兼容两种常见包裹：data.intfResultBean.userResList 或 data.userResList。
                    if (!TryGetProperty(root, "data", out JsonElement data)) return null;
                    if (!TryGetProperty(data, "intfResultBean", out JsonElement bean)) bean = data;
                    if (!TryGetProperty(bean, "userResList", out list) || list.ValueKind != JsonValueKind.Array)
                        return null;
                    double totalUsed = 0;
                    foreach (JsonElement item in list.EnumerateArray())
                    {
                        JsonElement nameEl, highEl, balEl, addEl;
                        string name = TryGetProperty(item, "discntName", out nameEl)
                            ? (nameEl.GetString() ?? string.Empty) : string.Empty;
                        if (name.Length == 0) continue;
                        double high = TryGetProperty(item, "highFee", out highEl) ? Num(highEl) : 0;
                        double bal = TryGetProperty(item, "balance", out balEl) ? Num(balEl) : 0;
                        if (high <= 0 && bal <= 0) continue;
                        // addupValue 是站点给出的「已用」（KB），实测恒等于 highFee-balance；
                        // 优先用它，缺失时退回差值，避免站点改口径后拿不到已用。
                        double used = TryGetProperty(item, "addupValue", out addEl) ? Num(addEl) : -1;
                        if (used < 0) used = high > bal ? high - bal : 0;
                        totalUsed += used;
                        result.Packages.Add(new BroadnetPackage
                        {
                            Name = name,
                            Total = G(high),
                            Remain = G(bal),
                            Used = G(used),
                            Unit = "G",
                            // 原始 KB 数值：只用于用量统计差分，不展示。G() 会四舍五入到
                            // 0.01 G（≈10 MB），10 分钟内的小额用量会被抹平。
                            TotalKb = high, RemainKb = bal, UsedKb = used
                        });
                    }
                    result.MonthlyUsed = G(totalUsed);
                    result.MonthlyUsedKb = totalUsed;
                }
            }
            catch
            {
                return null;
            }
            if (result.Packages.Count == 0) return null;

            if (!string.IsNullOrWhiteSpace(billBody))
            {
                try
                {
                    using (JsonDocument doc = JsonDocument.Parse(billBody))
                    {
                        JsonElement root = doc.RootElement;
                        if (TryGetProperty(root, "data", out JsonElement data))
                        {
                            if (TryGetProperty(data, "balance", out JsonElement bal))
                                result.Balance = bal.ValueKind == JsonValueKind.String
                                    ? (bal.GetString() ?? string.Empty) : Num(bal).ToString("0.00", CultureInfo.InvariantCulture);
                            if (TryGetProperty(data, "contentList", out JsonElement list2) && list2.ValueKind == JsonValueKind.Array)
                            {
                                // 实测结构：{title:"当月消费", list:[{name:"消费合计", value:"14.50"}]}
                                // title 和真正的 value 隔了一层 list 数组。
                                foreach (JsonElement item in list2.EnumerateArray())
                                {
                                    string label = TryGetProperty(item, "title", out JsonElement t) ? (t.GetString() ?? "") : "";
                                    if (label.IndexOf("消费", StringComparison.Ordinal) < 0) continue;
                                    if (!TryGetProperty(item, "list", out JsonElement inner)
                                        || inner.ValueKind != JsonValueKind.Array) continue;
                                    foreach (JsonElement sub in inner.EnumerateArray())
                                    {
                                        if (!TryGetProperty(sub, "value", out JsonElement v)) continue;
                                        result.MonthlySpend = v.ValueKind == JsonValueKind.String
                                            ? (v.GetString() ?? "") : Num(v).ToString("0.00", CultureInfo.InvariantCulture);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // 话费解析失败不影响流量数据。
                }
            }
            return result;
        }

        private static bool TryGetProperty(JsonElement el, string name, out JsonElement value)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value)) return true;
            value = default(JsonElement);
            return false;
        }

        private static double Num(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Number)
            {
                double d;
                return el.TryGetDouble(out d) ? d : 0;
            }
            if (el.ValueKind == JsonValueKind.String)
            {
                double d;
                return double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
            }
            return 0;
        }

        /// <summary>字节 → G（保留两位，去掉多余 0）。169869312 字节 = 162 G。</summary>
        private static string G(double bytes)
        {
            double g = bytes / 1048576.0;
            return g.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>从 DOM 文本（如 "58.3 G"）里剥出可解析的数字部分。</summary>
        private static string NumText(string raw)
        {
            string text = (raw ?? string.Empty).Trim();
            int space = text.IndexOf(' ');
            return space > 0 ? text.Substring(0, space) : text;
        }

        // 实测（2026-11-16）：个人中心页 .number-order-package-detail 区块，
        // 标题为「套餐余量」的那个里每个 li.clearfix 是一项流量包：
        //   .package-sort-name-text 名称 / .package-sort-used-num 已用 /
        //   .package-sort-remain-text[0] 剩余 [1] 总量 / 单位从「剩余 X G / 共 X G」取。
        // 话费余额与当月消费从页面 innerText 用正则兜底（DOM class 不稳定）。
        private const string TrafficExtractScript = @"(function(){
  function t(el){ return el ? (el.innerText||'').trim() : ''; }
  var body = document.body ? (document.body.innerText||'') : '';
  var phone = (body.match(/1\d{2}\*{4}\d{4}/)||[])[0] || '';
  var balance = '', spend = '';
  var m1 = body.match(/话费余额\s*([\d.]+)\s*元/); if (m1) balance = m1[1];
  var m2 = body.match(/当月消费\s*([\d.]+)\s*元/); if (m2) spend = m2[1];
  var packages = [];
  var secs = document.querySelectorAll('.number-order-package-detail');
  for (var i=0;i<secs.length;i++) {
    if (t(secs[i].querySelector('.package-detail-title')).indexOf('套餐余量') < 0) continue;
    var lis = secs[i].querySelectorAll('.package-detail-list li');
    for (var j=0;j<lis.length;j++) {
      var li = lis[j];
      var name = t(li.querySelector('.package-sort-name-text'));
      var used = t(li.querySelector('.package-sort-used-num'));
      var nums = li.querySelectorAll('.package-sort-remain-text');
      var remain = nums.length ? t(nums[0]) : '';
      var total = nums.length > 1 ? t(nums[1]) : '';
      var rm = t(li.querySelector('.package-sort-remain-num'));
      var um = rm.match(/([A-Za-z]+)/);
      var unit = um ? um[1] : 'G';
      if (name) packages.push({name:name, used:used, remain:remain, total:total, unit:unit});
    }
  }
  return JSON.stringify({phone:phone, balance:balance, spend:spend, packages:packages});
})()";

        /// <summary>退出程序时真正销毁 WebView2 控件与定时器。</summary>
        public void Shutdown()
        {
            _pollTimer.Stop();
            if (_window != null)
            {
                try { _window.Destroy(); } catch { }
                _window = null;
            }
            FinishWait(false, "程序退出");
        }

        private async Task<bool> RestoreSnapshotAsync()
        {
            if (string.IsNullOrEmpty(_snapshotPath) || !File.Exists(_snapshotPath)) return false;
            try
            {
                string json = File.ReadAllText(_snapshotPath, System.Text.Encoding.UTF8);
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
                }
                // 快照里的 JSON 对象字面量可直接当 JS 表达式注入。
                string script = "(function(){var d=" + json + ";try{for(var k in d){sessionStorage.setItem(k,d[k]);}}catch(e){}return 1;})()";
                await _window.EvaluateAsync(script);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>删除失效的登录快照，避免下次启动又注入同一个坏会话。</summary>
        private void TryDeleteSnapshot()
        {
            try
            {
                if (!string.IsNullOrEmpty(_snapshotPath) && File.Exists(_snapshotPath))
                {
                    File.Delete(_snapshotPath);
                    WriteTrace("deleted stale snapshot");
                }
            }
            catch
            {
            }
        }

        private async Task SaveSnapshotAsync()
        {
            if (string.IsNullOrEmpty(_snapshotPath)) return;
            try
            {
                string raw = await _window.EvaluateAsync(DumpScript);
                if (string.IsNullOrWhiteSpace(raw)) return;
                string json = raw;
                if (json.StartsWith("\"", StringComparison.Ordinal) && json.EndsWith("\"", StringComparison.Ordinal))
                    json = JsonSerializer.Deserialize<string>(json) ?? string.Empty;
                if (json.Length == 0) return;
                File.WriteAllText(_snapshotPath, json, new System.Text.UTF8Encoding(false));
            }
            catch
            {
                // 快照只是免重复登录的优化，写失败不影响本次登录。
            }
        }

        private void FinishWait(bool success, string status)
        {
            SetStatus(status);
            TaskCompletionSource<bool> completion;
            lock (_gate)
            {
                if (!_waiting) return;
                _waiting = false;
                completion = _loginCompletion;
                _loginCompletion = null;
            }
            _pollTimer.Stop();
            if (completion != null) completion.TrySetResult(success);
        }

        /// <summary>把异常连同堆栈写到配置文件目录，便于按截图无法定位时直接读日志。</summary>
        private void WriteErrorLog(Exception ex)
        {
            try
            {
                string path = Path.Combine(
                    string.IsNullOrEmpty(_snapshotPath) ? AppSettings.ConfigDirectory : Path.GetDirectoryName(_snapshotPath),
                    _pathPrefix + "-error.log");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine;
                File.AppendAllText(path, line, System.Text.Encoding.UTF8);
            }
            catch
            {
            }
        }

        private void SetStatus(string text)
        {
            lock (_gate) _status = text ?? string.Empty;
            if (_window != null)
            {
                try { _window.Status = _status; } catch { }
            }
        }

        internal static string MaskPhone(string phone)
        {
            if (string.IsNullOrEmpty(phone)) return string.Empty;
            string digits = string.Empty;
            foreach (char c in phone)
                if (c >= '0' && c <= '9') digits += c;
            if (digits.Length < 7) return digits;
            return digits.Substring(0, 3) + "****" + digits.Substring(digits.Length - 4);
        }

        private static BroadnetTraffic ParseTraffic(string raw)
        {
            BroadnetTraffic result = new BroadnetTraffic();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                string json = raw;
                // ExecuteScriptAsync 把字符串结果再包一层 JSON 引号。
                if (json.StartsWith("\"", StringComparison.Ordinal) && json.EndsWith("\"", StringComparison.Ordinal))
                    json = JsonSerializer.Deserialize<string>(json) ?? string.Empty;
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    JsonElement value;
                    if (root.TryGetProperty("phone", out value)) result.Phone = value.GetString() ?? string.Empty;
                    if (root.TryGetProperty("balance", out value)) result.Balance = value.GetString() ?? string.Empty;
                    if (root.TryGetProperty("spend", out value)) result.MonthlySpend = value.GetString() ?? string.Empty;
                    double totalUsed = 0;
                    double totalUsedKb = 0;
                    JsonElement packages;
                    if (root.TryGetProperty("packages", out value) && value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in value.EnumerateArray())
                        {
                            BroadnetPackage pkg = new BroadnetPackage();
                            if (item.TryGetProperty("name", out packages)) pkg.Name = packages.GetString() ?? string.Empty;
                            if (item.TryGetProperty("used", out packages)) pkg.Used = packages.GetString() ?? string.Empty;
                            if (item.TryGetProperty("remain", out packages)) pkg.Remain = packages.GetString() ?? string.Empty;
                            if (item.TryGetProperty("total", out packages)) pkg.Total = packages.GetString() ?? string.Empty;
                            if (item.TryGetProperty("unit", out packages)) pkg.Unit = packages.GetString() ?? string.Empty;
                            double used;
                            // DOM 文本是 G/M 数值（如 "103.7"），换算回 KB 供统计差分。
                            double factor = string.Equals(pkg.Unit, "M", StringComparison.OrdinalIgnoreCase)
                                ? 1024.0 : 1048576.0;
                            double usedKb = 0;
                            if (double.TryParse(NumText(pkg.Used), NumberStyles.Float, CultureInfo.InvariantCulture, out used)
                                && used > 0)
                            {
                                totalUsed += used;
                                usedKb = used * factor;
                                totalUsedKb += usedKb;
                            }
                            double parsed;
                            pkg.UsedKb = usedKb;
                            pkg.RemainKb = double.TryParse(NumText(pkg.Remain), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out parsed) ? parsed * factor : 0;
                            pkg.TotalKb = double.TryParse(NumText(pkg.Total), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out parsed) ? parsed * factor : 0;
                            result.Packages.Add(pkg);
                        }
                    }
                    result.MonthlyUsed = totalUsed > 0
                        ? totalUsed.ToString("0.##", CultureInfo.InvariantCulture) + " G"
                        : string.Empty;
                    result.MonthlyUsedKb = totalUsedKb;
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static Detection ParseDetection(string raw)
        {
            Detection result = new Detection();
            if (string.IsNullOrWhiteSpace(raw)) return result;
            try
            {
                string json = raw;
                // ExecuteScriptAsync 把字符串结果再包一层 JSON 引号。
                if (json.StartsWith("\"", StringComparison.Ordinal) && json.EndsWith("\"", StringComparison.Ordinal))
                    json = JsonSerializer.Deserialize<string>(json) ?? string.Empty;
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    JsonElement value;
                    if (root.TryGetProperty("logged", out value)) result.Logged = value.GetInt32() != 0;
                    if (root.TryGetProperty("phone", out value)) result.Phone = value.GetString() ?? string.Empty;
                    if (root.TryGetProperty("onLoginPage", out value)) result.OnLoginPage = value.GetInt32() != 0;
                }
            }
            catch
            {
            }
            return result;
        }

        private sealed class Detection
        {
            internal bool Logged;
            internal bool OnLoginPage;
            public string Phone = string.Empty;
        }
    }

}
