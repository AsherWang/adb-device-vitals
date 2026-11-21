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
    /// 中国电信网上营业厅（189.cn）的流量查询服务。设计沿用 BroadnetSessionService：
    ///
    /// - 接口 URL 带一次性动态签名 zgk12BhG，裸调 / 重放 / 自己 fetch 全部 401；
    ///   只能拦截站点自己发出的响应（2026-11-21 抓包实测）。
    /// - 登录态在 cookie + localStorage，靠 WebView2 userDataFolder 持久化；隐藏窗口保活。
    /// - 业务接口：queryMobileUsageInfo.do（余量）、queryMobileBalance.do（话费），
    ///   响应 data 字段是明文，值自带单位字符串（如 "138.85GB"）。
    ///
    /// 与广电共用 BroadnetTraffic / BroadnetPackage 数据类和登录窗口 BroadnetLoginWindow
    /// （接口名匹配列表由本服务替换）。账号密码 / 验证码全程人工，程序不代填。
    /// </summary>
    internal sealed class TelecomSessionService : ICarrierTrafficService
    {
        internal const string LoginUrl = "https://www.189.cn/web/";
        private const string DefaultPathPrefix = "telecom";
        private string _pathPrefix = DefaultPathPrefix;

        // 登录判据：主页渲染出账户区（"退出登录" + "账户余额"）即已登录。
        // 电信的登录态在 cookie / localStorage，没有广电那种 sessionStorage id。
        private const string DetectScript = @"(function(){
  var b = document.body ? (document.body.innerText||'') : '';
  var url = location.href;
  var logged = b.indexOf('退出登录')>=0 && /账户余额|余额/.test(b);
  var phone = (b.match(/1\d{2}\*{2,4}\d{3,4}/)||[])[0] || '';
  // 明确落在登录/认证页才算「可判定未登录」；首页账户区尚未渲染完时不算。
  var onLoginPage = /login|passport|signin|sso/i.test(url) ||
      (b.indexOf('退出登录')<0 && /短信登录|密码登录|验证码登录|立即登录/.test(b)) ? 1 : 0;
  return JSON.stringify({logged: logged?1:0, phone: phone, url: url, onLoginPage: onLoginPage});
})()";

        private const string DumpScript = @"(function(){
  var o={s:{},l:{}};
  try { for (var i=0;i<sessionStorage.length;i++){ var k=sessionStorage.key(i); if(k) o.s[k]=sessionStorage.getItem(k); } }
  catch(e){}
  try { for (var i=0;i<localStorage.length;i++){ var k=localStorage.key(i); if(k) o.l[k]=localStorage.getItem(k); } }
  catch(e){}
  return JSON.stringify(o);
})()";

        // DOM 兜底：首页账户区卡片，正则从 innerText 里剥数值。
        private const string TrafficExtractScript = @"(function(){
  var b = document.body ? (document.body.innerText||'') : '';
  var phone = (b.match(/1\d{2}\*{2,4}\d{3,4}/)||[])[0] || '';
  var balance = (b.match(/([\d.]+)\s*元\s*账户余额/)||[])[1] || '';
  var used = (b.match(/([\d.]+)\s*GB\s*已用通用流量/)||[])[1] || '';
  var remain = (b.match(/([\d.]+)\s*GB\s*剩余通用流量/)||[])[1] || '';
  var packages = [];
  if (remain) packages.push({name:'通用流量', used:used, remain:remain, total:'', unit:'G'});
  return JSON.stringify({phone:phone, balance:balance, spend:'', packages:packages});
})()";

        private static readonly string[] ApiPatterns =
            { "queryMobileUsageInfo", "queryMobileBalance" };

        private readonly object _gate = new object();
        private readonly DispatcherTimer _pollTimer;
        private readonly SemaphoreSlim _trafficGate = new SemaphoreSlim(1, 1);

        private BroadnetLoginWindow _window;
        private string _snapshotPath;
        private string _userDataFolder;
        private bool _loggedIn;
        private bool _lastLoggedForTrace;
        private bool _waiting;
        private string _phone = string.Empty;
        private string _status = "未检测";
        private TaskCompletionSource<bool> _loginCompletion;
        private bool _silentWindowCreated;

        internal TelecomSessionService()
        {
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _pollTimer.Tick += delegate { _ = DetectOnceAsync(false); };
        }

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
                _status = "无法创建电信数据目录：" + ex.Message;
            }
        }

        public bool IsLoggedIn { get { lock (_gate) return _loggedIn; } }
        internal string Phone { get { lock (_gate) return _phone; } }
        public string Status { get { lock (_gate) return _status; } }
        public bool HasSnapshot
        {
            get { return !string.IsNullOrEmpty(_snapshotPath) && File.Exists(_snapshotPath); }
        }
        public bool LastAttemptExpired { get; private set; }

        public async Task<bool> EnsureLoggedInAsync()
        {
            if (IsLoggedIn) return true;
            if (string.IsNullOrEmpty(_userDataFolder))
            {
                SetStatus("电信目录不可用，无法打开登录页");
                return false;
            }
            try
            {
                WriteTrace("EnsureLoggedIn start");
                if (_window == null)
                {
                    _window = CreateWindow();
                    Window owner = Application.Current != null ? Application.Current.MainWindow : null;
                    if (owner != null && owner.IsVisible) _window.Owner = owner;
                    SetStatus("正在初始化浏览器…");
                    _window.Show();
                    _window.Activate();
                    await _window.InitializeAsync(_userDataFolder);
                    WriteTrace("webview2 initialized");
                }
                else if (_silentWindowCreated && !_window.IsVisible)
                {
                    _window.Opacity = 1;
                    _window.Left = (SystemParameters.PrimaryScreenWidth - _window.Width) / 2;
                    _window.Top = (SystemParameters.PrimaryScreenHeight - _window.Height) / 2;
                    _window.Show();
                    _window.Activate();
                    WriteTrace("silent window restored for manual login");
                }

                SetStatus("正在打开电信网上营业厅…");
                WriteTrace("navigating " + LoginUrl);
                bool navigated = await _window.NavigateAndWaitAsync(LoginUrl);
                WriteTrace("navigation done=" + (navigated ? "1" : "0"));

                // 电信登录态靠 userDataFolder 里的 cookie / localStorage 持久化，
                // 快照只是额外补一份 storage 字面量，注入到同 origin 后再刷新。
                bool restored = await RestoreSnapshotAsync();
                WriteTrace("snapshot restored=" + restored);
                if (restored)
                {
                    _window.BeginApiCapture();
                    await _window.NavigateAndWaitAsync(LoginUrl + "?_t=" + DateTime.UtcNow.Ticks);
                    for (int i = 0; i < 12 && _window.GetApiBody("queryMobileUsageInfo") == null; i++)
                        await Task.Delay(500);
                    bool apiOk = HasValidTrafficData();
                    Detection check = ParseDetection(await _window.EvaluateAsync(DetectScript));
                    bool logged = apiOk || check.Logged;
                    WriteTrace("restored check logged=" + (check.Logged ? "1" : "0")
                        + " api=" + (apiOk ? "1" : "0") + " onLoginPage=" + (check.OnLoginPage ? "1" : "0"));
                    if (logged)
                    {
                        lock (_gate) { _loggedIn = true; _phone = check.Phone; }
                        _window.Phone = BroadnetSessionService.MaskPhone(check.Phone);
                        _window.Hide();
                        WriteTrace("restored login valid (api or dom), skip manual login");
                        return true;
                    }
                    // 只有明确落在登录页才认定过期；否则是页面未就绪，保留快照让用户继续。
                    if (check.OnLoginPage)
                    {
                        TryDeleteSnapshot();
                        SetStatus("上次登录已过期，请在窗口中重新登录 189.cn");
                    }
                    else
                    {
                        WriteTrace("restored not ready, keep snapshot for manual confirm");
                        SetStatus("请在窗口中确认电信登录态");
                    }
                }
                else
                {
                    SetStatus("请在窗口中登录 189.cn");
                }

                TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
                lock (_gate) { _loginCompletion = completion; _waiting = true; }
                _pollTimer.Start();
                return await completion.Task;
            }
            catch (Exception ex)
            {
                WriteTrace("ERROR " + ex);
                WriteErrorLog(ex);
                SetStatus("打开电信登录页失败：" + ex.GetType().Name + ": " + ex.Message);
                FinishWait(false, _status);
                return false;
            }
        }

        private BroadnetLoginWindow CreateWindow()
        {
            BroadnetLoginWindow window = new BroadnetLoginWindow();
            window.ApiKeyPatterns = ApiPatterns;
            window.TraceHook = delegate(string msg) { WriteTrace(msg); };
            window.UserClosed += delegate { FinishWait(false, "已取消登录"); };
            window.LoginConfirmed += delegate { _ = DetectOnceAsync(true); };
            return window;
        }

        public async Task<bool> EnsureLoggedInSerializedAsync()
        {
            await _trafficGate.WaitAsync();
            try { return await EnsureLoggedInAsync(); }
            finally { _trafficGate.Release(); }
        }

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

        public async Task<BroadnetTraffic> RefreshTrafficSilentlyAsync()
        {
            LastAttemptExpired = false;
            // 电信登录态主要靠 WebView 用户目录里的 cookie 持久化，快照只是 storage 补充。
            // 即使快照文件没了（例如旧版本误删），只要用户目录还在就尝试导航恢复，别直接跳过。
            bool hasUserData = !string.IsNullOrEmpty(_userDataFolder) && Directory.Exists(_userDataFolder);
            if (!HasSnapshot && !IsLoggedIn && !hasUserData)
            {
                WriteTrace("silent: skip, no snapshot / login / user data folder");
                return null;
            }
            await _trafficGate.WaitAsync();
            try
            {
                WriteTrace("silent: begin, hasSnapshot=" + HasSnapshot + " logged=" + IsLoggedIn);
                if (_window == null)
                {
                    _window = CreateWindow();
                    _silentWindowCreated = true;
                    _window.Opacity = 0;
                    _window.ShowInTaskbar = false;
                    _window.WindowStartupLocation = WindowStartupLocation.Manual;
                    _window.Left = -32000;
                    _window.Top = -32000;
                    _window.Show();
                    await _window.InitializeAsync(_userDataFolder);
                    WriteTrace("silent: webview2 initialized (offscreen)");
                }
                if (!IsLoggedIn) await RestoreSnapshotAsync();

                // 带时间戳重新导航首页，触发站点自己发出两个业务接口。
                string freshUrl = LoginUrl + "?_t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
                _window.BeginApiCapture();
                bool navigated = await _window.NavigateAndWaitAsync(freshUrl);
                for (int i = 0; i < 12 && _window.GetApiBody("queryMobileUsageInfo") == null; i++)
                    await Task.Delay(500);
                // 站点自己发出的业务接口带「有效套餐数据」= 登录态铁证。注意不能只看 body
                // 是否存在：站点风控/令牌失效时会返回同样 URL 的异常响应（实测 574B，正常 661B），
                // 那种 body 解析不出任何套餐，必须当作没数据，否则会误判「已登录但 0 包」。
                bool apiOk = HasValidTrafficData();
                Detection check = ParseDetection(await _window.EvaluateAsync(DetectScript));
                bool logged = apiOk || check.Logged;
                WriteTrace("silent: check logged=" + (check.Logged ? "1" : "0")
                    + " api=" + (apiOk ? "1" : "0") + " onLoginPage=" + (check.OnLoginPage ? "1" : "0")
                    + " navigated=" + (navigated ? "1" : "0"));
                if (!logged)
                {
                    _window.EndApiCapture();
                    // 只有明确落在登录页才判过期；导航超时或首页尚未渲染完一律保留快照，
                    // 等下个周期重试，绝不再因为「DOM 还没出字」就把登录态删掉。
                    if (!navigated || !check.OnLoginPage)
                    {
                        WriteTrace("silent: not ready or not on login page, keep snapshot for retry");
                        return null;
                    }
                    MarkExpired("登录态已过期");
                    return null;
                }

                lock (_gate) { _loggedIn = true; _phone = check.Phone; }
                _window.Phone = BroadnetSessionService.MaskPhone(check.Phone);
                await SaveSnapshotAsync();
                SetStatus("已登录" + (check.Phone.Length > 0 ? " · " + BroadnetSessionService.MaskPhone(check.Phone) : ""));
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
                if (_silentWindowCreated && _window != null && _window.IsVisible)
                {
                    try { _window.Hide(); } catch { }
                }
                _trafficGate.Release();
            }
        }

        private void MarkExpired(string reason)
        {
            LastAttemptExpired = true;
            TryDeleteSnapshot();
            lock (_gate) { _loggedIn = false; _phone = string.Empty; }
            SetStatus(reason + "，请点击余量卡片重新登录");
            WriteTrace("silent: " + reason + ", snapshot deleted");
        }

        public void Trace(string message) { WriteTrace(message); }

        internal async Task DetectOnceAsync(bool fromUserConfirm)
        {
            if (_window == null || _window.BrowserView.CoreWebView2 == null) return;
            try
            {
                Detection detection = ParseDetection(await _window.EvaluateAsync(DetectScript));
                if (detection.Logged != _lastLoggedForTrace || fromUserConfirm)
                {
                    _lastLoggedForTrace = detection.Logged;
                    WriteTrace("detect logged=" + (detection.Logged ? "1" : "0"));
                }
                if (!detection.Logged)
                {
                    if (fromUserConfirm) SetStatus("还没检测到登录态，请完成登录后重试");
                    return;
                }
                await SaveSnapshotAsync();
                lock (_gate) { _loggedIn = true; _phone = detection.Phone; }
                _window.Phone = BroadnetSessionService.MaskPhone(detection.Phone);
                _pollTimer.Stop();
                SetStatus("已登录" + (detection.Phone.Length > 0 ? " · " + BroadnetSessionService.MaskPhone(detection.Phone) : ""));
                _window.Status = _status;
                _window.Hide();
                FinishWait(true, _status);
            }
            catch (Exception ex)
            {
                if (fromUserConfirm) SetStatus("检测登录态失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 缓存的接口 body 能否解析出有效套餐。站点风控/令牌过期时接口 URL 不变，
        /// 但响应变成异常内容（实测两种接口都返回 574B，正常是 661B/360B），
        /// 只判断 body 非 null 会把这种坏响应当成登录有效。必须真正解析一次。
        /// </summary>
        private bool HasValidTrafficData()
        {
            string usage = _window.GetApiBody("queryMobileUsageInfo");
            if (usage == null) return false;
            BroadnetTraffic parsed = TelecomTrafficParser.ParseUsage(usage, _window.GetApiBody("queryMobileBalance"));
            return parsed != null && parsed.Packages.Count > 0;
        }

        public async Task<BroadnetTraffic> ReadTrafficAsync()
        {
            if (!IsLoggedIn || _window == null || _window.BrowserView.CoreWebView2 == null) return null;
            try
            {
                string usageBody = _window.GetApiBody("queryMobileUsageInfo");
                string balanceBody = _window.GetApiBody("queryMobileBalance");
                WriteTrace("traffic: cached usage=" + (usageBody == null ? "null" : usageBody.Length + "B")
                    + " balance=" + (balanceBody == null ? "null" : balanceBody.Length + "B"));
                // 缓存 body 存在但解析不出套餐（风控/令牌过期的异常响应）也要重抓，
                // 不能拿坏 body 直接 fallback 到 DOM。最多重试两轮，避免反复导航。
                BroadnetTraffic api = TelecomTrafficParser.ParseUsage(usageBody, balanceBody);
                if (api == null || api.Packages.Count == 0)
                {
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        WriteTrace("traffic: cached body invalid, re-navigate attempt=" + (attempt + 1));
                        _window.BeginApiCapture();
                        await _window.NavigateAndWaitAsync(LoginUrl + "?_t=" + DateTime.UtcNow.Ticks);
                        for (int i = 0; i < 10; i++)
                        {
                            if (HasValidTrafficData()) break;
                            await Task.Delay(500);
                        }
                        usageBody = _window.GetApiBody("queryMobileUsageInfo");
                        balanceBody = _window.GetApiBody("queryMobileBalance");
                        _window.EndApiCapture();
                        api = TelecomTrafficParser.ParseUsage(usageBody, balanceBody);
                        if (api != null && api.Packages.Count > 0) break;
                    }
                }
                if (api != null && api.Packages.Count > 0)
                {
                    WriteTrace("traffic via API: " + api.Packages.Count + " packages, balance=" + api.Balance
                        + ", used=" + api.MonthlyUsed);
                    return api;
                }
                WriteTrace("API unavailable, fallback to DOM");
                _window.EndApiCapture();
                BroadnetTraffic traffic = ParseTraffic(await _window.EvaluateAsync(TrafficExtractScript));
                WriteTrace(traffic == null ? "traffic DOM parse returned null"
                    : "traffic via DOM: " + traffic.Packages.Count + " packages");
                return traffic;
            }
            catch (Exception ex)
            {
                WriteTrace("ReadTrafficAsync ERROR " + ex);
                try
                {
                    _window.EndApiCapture();
                    return ParseTraffic(await _window.EvaluateAsync(TrafficExtractScript));
                }
                catch { return null; }
            }
        }

        private static BroadnetTraffic ParseTraffic(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            BroadnetTraffic result = new BroadnetTraffic();
            try
            {
                string json = Unwrap(raw);
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    JsonElement value;
                    if (root.TryGetProperty("phone", out value)) result.Phone = value.GetString() ?? string.Empty;
                    if (root.TryGetProperty("balance", out value)) result.Balance = value.GetString() ?? string.Empty;
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
                            double used = ParseG(pkg.Used), remain = ParseG(pkg.Remain), total = ParseG(pkg.Total);
                            pkg.UsedKb = used * 1048576.0;
                            pkg.RemainKb = remain * 1048576.0;
                            pkg.TotalKb = total * 1048576.0;
                            result.MonthlyUsedKb += pkg.UsedKb;
                            result.Packages.Add(pkg);
                        }
                    }
                    result.MonthlyUsed = G(result.MonthlyUsedKb);
                }
                return result;
            }
            catch { return null; }
        }

        private static double ParseG(string text)
        {
            double value;
            return double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value : 0;
        }

        private static string G(double kb)
        {
            return (kb / 1048576.0).ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static bool TryGet(JsonElement el, string name, out JsonElement value)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value)) return true;
            value = default(JsonElement);
            return false;
        }

        private static string GetString(JsonElement el, string name)
        {
            JsonElement value;
            if (!TryGet(el, name, out value)) return string.Empty;
            return value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty) : value.ToString();
        }

        private static string Unwrap(string raw)
        {
            if (raw.StartsWith("\"", StringComparison.Ordinal) && raw.EndsWith("\"", StringComparison.Ordinal))
                return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
            return raw;
        }

        private static Detection ParseDetection(string raw)
        {
            Detection result = new Detection();
            if (string.IsNullOrWhiteSpace(raw)) return result;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(Unwrap(raw)))
                {
                    JsonElement root = doc.RootElement;
                    JsonElement value;
                    if (root.TryGetProperty("logged", out value)) result.Logged = value.GetInt32() != 0;
                    if (root.TryGetProperty("phone", out value)) result.Phone = value.GetString() ?? string.Empty;
                    if (root.TryGetProperty("onLoginPage", out value)) result.OnLoginPage = value.GetInt32() != 0;
                }
            }
            catch { }
            return result;
        }

        private sealed class Detection
        {
            internal bool Logged;
            internal bool OnLoginPage;
            internal string Phone = string.Empty;
        }

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
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
                // 电信快照结构是 {s:{...},l:{...}}，分别注入 sessionStorage / localStorage。
                string script = @"(function(){var d=" + json +
                    @";try{if(d.s)for(var k in d.s)sessionStorage.setItem(k,d.s[k]);}catch(e){}" +
                    @"try{if(d.l)for(var k in d.l)localStorage.setItem(k,d.l[k]);}catch(e){}return 1;})()";
                await _window.EvaluateAsync(script);
                return true;
            }
            catch { return false; }
        }

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
            catch { }
        }

        private async Task SaveSnapshotAsync()
        {
            if (string.IsNullOrEmpty(_snapshotPath)) return;
            try
            {
                string raw = await _window.EvaluateAsync(DumpScript);
                string json = Unwrap(raw ?? string.Empty);
                if (json.Length == 0) return;
                File.WriteAllText(_snapshotPath, json, new System.Text.UTF8Encoding(false));
            }
            catch { }
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

        private void SetStatus(string text)
        {
            lock (_gate) _status = text ?? string.Empty;
            if (_window != null)
            {
                try { _window.Status = _status; } catch { }
            }
        }

        private void WriteTrace(string message)
        {
            try
            {
                string dir = string.IsNullOrEmpty(_snapshotPath)
                    ? AppSettings.ConfigDirectory : Path.GetDirectoryName(_snapshotPath);
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, _pathPrefix + "-trace.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
            catch { }
        }

        private void WriteErrorLog(Exception ex)
        {
            try
            {
                string path = Path.Combine(
                    string.IsNullOrEmpty(_snapshotPath) ? AppSettings.ConfigDirectory : Path.GetDirectoryName(_snapshotPath),
                    _pathPrefix + "-error.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
