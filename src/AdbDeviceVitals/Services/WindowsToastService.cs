using System;
using System.Runtime.InteropServices;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AdbDeviceVitals.Services
{
    /// <summary>
    /// Windows 通知中心样式的提醒。迁移到 .NET 10 之后改用 Windows SDK 投影
    /// （CsWinRT）的强类型 Toast API，不再依赖 ContentType=WindowsRuntime 反射。
    /// 不做点击激活（那需要注册 COM 激活器），只负责把「收到几条新短信」送进通知中心。
    ///
    /// 未打包的桌面程序需要显式 AppUserModelID；某些系统策略下 Toast 会创建失败。
    /// 任何一步失败都永久回退到托盘气泡，避免每来一条短信都抛一次异常。
    /// 传入的文案由调用方保证不含短信正文与验证码。
    /// </summary>
    public static class WindowsToastService
    {
        private const string AppUserModelId = "AsherWang.AdbDeviceVitals";
        private static readonly object Gate = new object();
        private static bool _appIdPrepared;
        private static bool _initialized;
        private static bool _unavailable;
        private static ToastNotifier _notifier;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        /// <summary>要在创建任何窗口之前调用，否则系统不会把 Toast 归到本进程。</summary>
        public static void PrepareAppId()
        {
            lock (Gate)
            {
                if (_appIdPrepared) return;
                _appIdPrepared = true;
                try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
                catch { _unavailable = true; }
            }
        }

        /// <summary>只做能力自检、不弹通知，供诊断与测试使用。</summary>
        public static bool CanTry
        {
            get
            {
                lock (Gate)
                {
                    if (_unavailable) return false;
                    try
                    {
                        if (!_initialized) Initialize();
                        return !_unavailable && _notifier != null;
                    }
                    catch
                    {
                        _unavailable = true;
                        return false;
                    }
                }
            }
        }

        /// <summary>成功返回 true；失败返回 false，调用方应改用托盘气泡。</summary>
        public static bool TryShow(string title, string message)
        {
            lock (Gate)
            {
                if (_unavailable) return false;
                try
                {
                    if (!_initialized) Initialize();
                    if (_unavailable || _notifier == null) return false;

                    XmlDocument xml = new XmlDocument();
                    xml.LoadXml(BuildToastXml(title, message));
                    _notifier.Show(new ToastNotification(xml));
                    return true;
                }
                catch
                {
                    _unavailable = true;
                    return false;
                }
            }
        }

        private static void Initialize()
        {
            _initialized = true;
            PrepareAppId();
            if (_unavailable) return;
            _notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
        }

        private static string BuildToastXml(string title, string message)
        {
            return "<toast><visual><binding template=\"ToastGeneric\"><text>"
                + Escape(title) + "</text><text>" + Escape(message)
                + "</text></binding></visual></toast>";
        }

        private static string Escape(string text)
        {
            return SecurityElement.Escape(text ?? string.Empty) ?? string.Empty;
        }
    }
}
