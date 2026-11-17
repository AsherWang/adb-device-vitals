using System;
using System.IO;
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

        private static string NormalizeApiKey(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            // 接口都在 /contact-web/api/busi/ 下，按函数名规范化，避免依赖完整 host。
            if (url.IndexOf("/qryUserRes", StringComparison.OrdinalIgnoreCase) >= 0) return "qryUserRes";
            if (url.IndexOf("/qryBillInfo", StringComparison.OrdinalIgnoreCase) >= 0) return "qryBillInfo";
            if (url.IndexOf("/qryBalanceFee", StringComparison.OrdinalIgnoreCase) >= 0) return "qryBalanceFee";
            if (url.IndexOf("/qrySubscribeBusiness", StringComparison.OrdinalIgnoreCase) >= 0) return "qrySubscribeBusiness";
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
        internal async Task NavigateAndWaitAsync(string url)
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
            await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        }

        /// <summary>异步执行 JS 并返回 WebView2 的 JSON 表示（字符串会带引号）。</summary>
        internal async Task<string> EvaluateAsync(string script)
        {
            return await Browser.ExecuteScriptAsync(script);
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
