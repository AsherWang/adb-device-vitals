using System;
using System.Threading;
using System.Windows.Forms;
using AdbDeviceVitals.Services;

namespace AdbDeviceVitals
{
    internal static class Program
    {
        private const string MutexName = @"Local\AdbDeviceVitals.Singleton";
        private const string ActivationEventName = @"Local\AdbDeviceVitals.Activate";

        [STAThread]
        private static void Main()
        {
            bool firstInstance;
            using (EventWaitHandle activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName))
            using (Mutex mutex = new Mutex(true, MutexName, out firstInstance))
            {
                if (!firstInstance)
                {
                    activationEvent.Set();
                    return;
                }

                // 只读一次配置文件；后续轮询命中内存缓存，不会每 2.5 秒碰一次磁盘。
                AppSettings.Initialize();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(activationEvent, IsStartupLaunch()));
            }
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

