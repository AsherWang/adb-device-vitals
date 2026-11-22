using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AdbDeviceVitals
{
    /// <summary>
    /// 中国广电网上营业厅的登录宿主。账号 / 图片验证码 / 滑块全部由用户手动完成，
    /// 本窗口只做两件事：把 10099 登录页交给 WebView2 显示，以及定时从页面里
    /// 读取 sessionStorage 判断登录是否成功。
    ///
    /// 关键：登录态在 sessionStorage（标签页级），关掉标签页就没了。所以登录成功后
    /// 主程序会 Hide 这个窗口而不是 Close —— WebView2 控件和标签页存活，登录态才在。
    /// 真正的销毁只在程序退出时进行。
    /// </summary>
    public partial class BroadnetLoginWindow
    {
        /// <summary>用户手动关闭窗口（不是自动隐藏）。主程序据此结束「等待登录」。</summary>
        internal event Action UserClosed;

        /// <summary>用户点了「我已登录」，主程序应立即复查一次 sessionStorage。</summary>
        internal event Action LoginConfirmed;

        private bool _allowClose;

        // 方案 B：拦截站点自己发出的业务接口响应。站点会用正确的 Access 头和加密体
        // 发请求，我们只读回来的 JSON，不需要破解签名。字典按规范化接口名缓存 body。
        private readonly System.Collections.Generic.Dictionary<string, string> _apiBodies =
            new System.Collections.Generic.Dictionary<string, string>();
        private volatile bool _captureApiResponses;

        /// <summary>
        /// 需要拦截的接口名（URL 路径里的关键词 → 缓存 key）。默认广电四个接口；
        /// 电信服务创建窗口时替换为自己的接口名。按 URL 路径片段匹配，与 host 无关。
        /// </summary>
        internal string[] ApiKeyPatterns = { "qryUserRes", "qryBillInfo", "qryBalanceFee", "qrySubscribeBusiness" };

        internal BroadnetLoginWindow()
        {
            InitializeComponent();
            Closing += OnClosing;
        }

        /// <summary>调试口：UI 自动化脚本会读这个状态文本，验证登录检测链路。</summary>
        internal string Status
        {
            get { return StatusText.Text; }
            set { StatusText.Text = value ?? string.Empty; }
        }

        internal string Phone
        {
            get { return PhoneText.Text; }
            set { PhoneText.Text = value ?? string.Empty; }
        }

        internal WebView2 BrowserView { get { return Browser; } }

        /// <summary>
        /// 用固定 userDataFolder 初始化内核：cookie（WAF 挑战）和标签页会话都落在这个目录，
        /// 下次启动不必重新过一遍阿里云 WAF。
        /// </summary>
        internal async Task InitializeAsync(string userDataFolder)
        {
            // WebView2 的 WPF 控件必须有真实 HWND 才能创建内核。窗口尚未 Show 时
            // 句柄可能还没生成，EnsureCoreWebView2Async 之后 CoreWebView2 仍是 null，
            // 后续任何访问都会抛 NullReferenceException。这里先强制创建句柄。
            new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: null);
            // 加超时：隐藏窗口上内核偶尔起不来，绝不能把后台刷新永久挂住（会连带占住闸门，
            // 用户点卡片也一起卡死）。超时后抛异常由上层的 catch 兜底。
            Task init = Browser.EnsureCoreWebView2Async(environment);
            if (await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(20))) != init)
                throw new TimeoutException("WebView2 内核初始化超时");
            await init;

            // 注册一次即可（窗口只创建一次，隐藏保活）。捕获开关由 ReadTrafficAsync
            // 在导航个人中心页前后打开/关闭，避免平时登录流程也在缓存响应。
            Browser.CoreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;
        }

        /// <summary>开始一次接口响应捕获：清空上次缓存并打开开关。导航个人中心页前调用。</summary>
        internal void BeginApiCapture()
        {
            lock (_apiBodies) _apiBodies.Clear();
            _captureApiResponses = true;
        }

        /// <summary>结束捕获，避免后续页面活动的响应继续写缓存。</summary>
        internal void EndApiCapture()
        {
            _captureApiResponses = false;
        }

        /// <summary>取缓存的接口响应 body（原始 JSON 字符串），没抓到返回 null。</summary>
        internal string GetApiBody(string key)
        {
            lock (_apiBodies)
            {
                string value;
                return _apiBodies.TryGetValue(key, out value) ? value : null;
            }
        }

        /// <summary>诊断回调：把拦截事件的关键节点写进主服务的 trace（由服务注入）。</summary>
        internal Action<string> TraceHook;

        private void OnWebResourceResponseReceived(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            string url = e.Request != null ? e.Request.Uri : string.Empty;
            string key = NormalizeApiKey(url);
            if (key == null) return;
            TraceHook?.Invoke("webresp hit key=" + key + " capture=" + (_captureApiResponses ? "1" : "0"));
            if (!_captureApiResponses) return;
            try
            {
                // GetContentAsync 是异步的，事件回调里 fire-and-forget 写入缓存。
                _ = CaptureBodyAsync(key, e.Response);
            }
            catch (Exception ex)
            {
                TraceHook?.Invoke("webresp ERR " + ex.Message);
            }
        }

        private string NormalizeApiKey(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string[] patterns = ApiKeyPatterns;
            if (patterns == null) return null;
            // 按接口函数名匹配 URL 路径，去掉 query（电信的签名全在 ?zgk12BhG= 里）。
            int query = url.IndexOf('?');
            string path = query > 0 ? url.Substring(0, query) : url;
            foreach (string pattern in patterns)
            {
                if (!string.IsNullOrEmpty(pattern) && path.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    return pattern;
            }
            return null;
        }

        private async Task CaptureBodyAsync(string key, CoreWebView2WebResourceResponseView response)
        {
            try
            {
                using (Stream stream = await response.GetContentAsync())
                using (StreamReader reader = new StreamReader(stream))
                {
                    string body = await reader.ReadToEndAsync();
                    lock (_apiBodies) _apiBodies[key] = body;
                    TraceHook?.Invoke("webresp cached " + key + " " + (body == null ? 0 : body.Length) + "B");
                }
            }
            catch (Exception ex)
            {
                TraceHook?.Invoke("webresp capture ERR " + ex.Message);
            }
        }

        internal void Navigate(string url)
        {
            Browser.Source = new Uri(url);
        }

        /// <summary>
        /// 导航并等待页面加载完成。注入 sessionStorage 快照前必须等登录页真正加载，
        /// 否则 sessionStorage 还属于 about:blank，写进去的值在跳到 10099 后就丢了。
        /// 登录页的 WAF 挑战会触发多次导航，只要有一次完成就返回。
        /// </summary>
        /// <summary>导航并等待完成；返回 false 表示 20 秒内没等到 NavigationCompleted（页面可能还在加载/超时）。</summary>
        internal async Task<bool> NavigateAndWaitAsync(string url)
        {
            CoreWebView2 core = Browser.CoreWebView2;
            if (core == null)
                throw new InvalidOperationException("WebView2 内核未就绪（CoreWebView2 为空）");

            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = null;
            handler = delegate(object sender, CoreWebView2NavigationCompletedEventArgs args)
            {
                core.NavigationCompleted -= handler;
                done.TrySetResult(true);
            };
            core.NavigationCompleted += handler;
            Navigate(url);
            // WAF 挑战 / 网络异常时可能一直等不到完成事件，设个上限，宁可让页面继续
            // 加载也不把「等待登录」永久挂住（定时检测仍会继续跑）。
            bool completed = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20))) == done.Task;
            if (!completed) core.NavigationCompleted -= handler; // 超时也要摘掉，避免反复导航累积订阅
            return completed;
        }

        /// <summary>异步执行 JS 并返回 WebView2 的 JSON 表示（字符串会带引号）。</summary>
        internal async Task<string> EvaluateAsync(string script)
        {
            return await Browser.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// 导出指定 URI 可见的 cookie（含 HttpOnly），用于跨进程保存运营商登录态。
        /// 电信的登录票据只在 cookie 里，sessionStorage/localStorage 快照救不了重开。
        /// </summary>
        internal async Task<string> GetCookiesJsonAsync(string uri)
        {
            CoreWebView2 core = Browser.CoreWebView2;
            if (core == null) return "[]";
            IReadOnlyList<CoreWebView2Cookie> cookies = await core.CookieManager.GetCookiesAsync(uri);
            var items = new List<Dictionary<string, object>>();
            foreach (CoreWebView2Cookie c in cookies)
            {
                items.Add(new Dictionary<string, object>
                {
                    { "domain", c.Domain },
                    { "name", c.Name },
                    { "value", c.Value },
                    { "path", c.Path },
                    { "httpOnly", c.IsHttpOnly },
                    { "secure", c.IsSecure },
                    { "sameSite", (int)c.SameSite },
                    // 站点可能下发会话 cookie（Expires=MinValue），快照里改写成 30 天后过期，
                    // 注入时才能落成持久 cookie，程序重开仍有效。
                    { "expires", c.Expires == DateTime.MinValue
                        ? DateTime.UtcNow.AddDays(30).ToString("o", CultureInfo.InvariantCulture)
                        : c.Expires.ToString("o", CultureInfo.InvariantCulture) }
                });
            }
            return JsonSerializer.Serialize(items);
        }

        /// <summary>
        /// 从快照恢复 cookie。必须在导航到目标 origin 之前调用；CookieManager 操作的是
        /// profile 级存储，与当前页面无关，about:blank 下也能注入。
        /// </summary>
        internal int RestoreCookies(string json)
        {
            CoreWebView2 core = Browser.CoreWebView2;
            if (core == null || string.IsNullOrWhiteSpace(json)) return 0;
            int count = 0;
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;
                foreach (JsonElement item in doc.RootElement.EnumerateArray())
                {
                    try
                    {
                        string domain = item.GetProperty("domain").GetString();
                        string name = item.GetProperty("name").GetString();
                        string value = item.GetProperty("value").GetString();
                        if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(name)) continue;
                        CoreWebView2Cookie cookie = core.CookieManager.CreateCookie(
                            name, value, domain, item.TryGetProperty("path", out JsonElement p) ? p.GetString() : "/");
                        if (item.TryGetProperty("httpOnly", out JsonElement h)) cookie.IsHttpOnly = h.GetBoolean();
                        if (item.TryGetProperty("secure", out JsonElement s)) cookie.IsSecure = s.GetBoolean();
                        if (item.TryGetProperty("sameSite", out JsonElement ss))
                            cookie.SameSite = (CoreWebView2CookieSameSiteKind)ss.GetInt32();
                        if (item.TryGetProperty("expires", out JsonElement ex) &&
                            DateTime.TryParse(ex.GetString(), CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out DateTime expires))
                            cookie.Expires = expires;
                        core.CookieManager.AddOrUpdateCookie(cookie);
                        count++;
                    }
                    catch { /* 单条坏 cookie 不影响其它条目 */ }
                }
            }
            return count;
        }

        /// <summary>退出程序时调用，允许真正销毁 WebView2。</summary>
        internal void Destroy()
        {
            _allowClose = true;
            try { Close(); } catch { }
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_allowClose) return;
            // 用户点右上角 X：隐藏而非销毁，保住 sessionStorage。
            e.Cancel = true;
            Hide();
            Action handler = UserClosed;
            if (handler != null) handler();
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            Action handler = LoginConfirmed;
            if (handler != null) handler();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // 走同样的 OnClosing 路径：隐藏并通知主程序，不销毁控件。
            Close();
        }
    }
}
