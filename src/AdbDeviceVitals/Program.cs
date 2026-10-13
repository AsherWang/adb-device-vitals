using System;
using System.Threading;
using System.Windows.Forms;

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

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(activationEvent));
            }
        }
    }
}

