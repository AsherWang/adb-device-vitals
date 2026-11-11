using System;
using System.Threading;
using System.Windows;
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

            _mainWindow.StartBackgroundWork();
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
