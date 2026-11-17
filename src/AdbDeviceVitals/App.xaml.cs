using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdbDeviceVitals.Services;

namespace AdbDeviceVitals
{
    /// <summary>
    /// WPF 入口。原来 Program.Main 里的单实例、启动参数和激活事件都搬到这里；
    /// 托盘由主窗口持有，Application 只负责生命周期，退出必须显式调用 Shutdown。
    /// </summary>
    public partial class App : Application
    {
        private const string MutexName = @"Local\AdbDeviceVitals.Singleton";
        private const string ActivationEventName = @"Local\AdbDeviceVitals.Activate";

        private Mutex _mutex;
        private EventWaitHandle _activationEvent;
        private DispatcherTimer _activationTimer;
        private MainWindow _mainWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            RegisterFocusVisualPolicy();

            bool firstInstance;
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _mutex = new Mutex(true, MutexName, out firstInstance);
            if (!firstInstance)
            {
                _activationEvent.Set();
                Shutdown();
                return;
            }

            // 只读一次配置文件；后续轮询命中内存缓存，不会每 2.5 秒碰一次磁盘。
            AppSettings.Initialize();
            // Toast 的 AppUserModelID 必须在建窗口之前设置。
            WindowsToastService.PrepareAppId();
            // 原生 WinForms 对话框（ADB 目录选择）跟随系统视觉样式。
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

            bool startInTray = IsStartupLaunch();
            _mainWindow = new MainWindow();
            MainWindow = _mainWindow;
            if (!startInTray) _mainWindow.Show();

            _activationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _activationTimer.Tick += delegate
            {
                if (_activationEvent != null && _activationEvent.WaitOne(0)) _mainWindow.RestoreWindow();
            };
            _activationTimer.Start();

            // 自检开关：带 --broadnet-selftest 启动时自动走一遍广电登录入口，
            // 便于无人值守地验证 WebView 窗口能否弹出、trace 走到哪一步。
            // --broadnet-traffic-test 则额外等登录就绪后读一次套餐余量写进 trace。
            bool trafficTest = false;
            bool selfTest = false;
            foreach (string arg in e.Args)
            {
                if (string.Equals(arg, "--broadnet-traffic-test", StringComparison.OrdinalIgnoreCase))
                    trafficTest = true;
                if (string.Equals(arg, "--broadnet-selftest", StringComparison.OrdinalIgnoreCase))
                    selfTest = true;
            }
            // 自检自己会驱动 WebView 登录 / 读流量；跳过后台静默刷新，避免两条链路并发导航。
            _mainWindow.StartBackgroundWork(skipBroadnetAuto: selfTest || trafficTest);

            if (selfTest)
                Dispatcher.BeginInvoke(new Action(delegate { _mainWindow.RunBroadnetSelfTest(); }));
            if (trafficTest)
            {
                // 先只做登录（快照有效则自动完成），再单独读一次流量。
                // 不能调 RunBroadnetSelfTest——它会走完整的 OpenBroadnetAsync（含读流量），
                // 与这里的 ReadTrafficAsync 并发操作同一个 WebView2 导致导航互相打断。
                Dispatcher.BeginInvoke(new Action(async delegate { await _mainWindow.RunBroadnetLoginOnlyAsync(); }));
                Dispatcher.BeginInvoke(new Action(async delegate { await _mainWindow.RunBroadnetTrafficTestAsync(); }));
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_activationTimer != null) _activationTimer.Stop();
            if (_mainWindow != null) _mainWindow.Shutdown();
            if (_activationEvent != null) _activationEvent.Dispose();
            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
            }
            base.OnExit(e);
        }

        /// <summary>
        /// 按下 Alt 时 WPF 会把「最近输入设备」切成键盘，并给当前焦点元素画上系统默认的
        /// 黑色焦点虚线框。焦点落在窗口 / 根容器 / ScrollViewer 这类几乎铺满客户区的元素上时，
        /// 这个框只有标题栏下方的上边看得见，表现就是「顶部突然出现一条黑条」。
        /// 本工具没有定义任何访问键，键盘提示框没有意义，统一把 FocusVisualStyle 关掉；
        /// 只影响视觉，Tab / Enter / 空格等键盘操作照常可用。
        /// 注意：按 Alt 时焦点元素未必变化，GotKeyboardFocus 不一定会再触发一次，
        /// 所以必须在窗口 Loaded 后把整棵可视树清一遍，不能只靠焦点事件。
        /// </summary>
        internal static void RegisterFocusVisualPolicy()
        {
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(delegate(object sender, RoutedEventArgs args)
                {
                    // 主窗口、设置页、短信列表、详情弹窗统一在模板展开后清理一次。
                    SuppressFocusVisuals(sender as DependencyObject);
                }));

            EventManager.RegisterClassHandler(
                typeof(FrameworkElement),
                Keyboard.PreviewGotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(delegate(object sender, KeyboardFocusChangedEventArgs args)
                {
                    // 之后动态创建的控件再兜一层。
                    FrameworkElement element = sender as FrameworkElement;
                    if (element != null) element.FocusVisualStyle = null;
                }),
                true);
        }

        /// <summary>构造完成后立刻清一遍整棵可视树；渲染冒烟测试也用它做断言。</summary>
        internal static void SuppressFocusVisuals(DependencyObject root)
        {
            if (root == null) return;
            FrameworkElement element = root as FrameworkElement;
            if (element != null) element.FocusVisualStyle = null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
                SuppressFocusVisuals(VisualTreeHelper.GetChild(root, i));
        }

        private static bool IsStartupLaunch()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], AppSettings.StartupArgument, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
