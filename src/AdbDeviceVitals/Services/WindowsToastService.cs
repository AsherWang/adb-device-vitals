using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;

namespace AdbDeviceVitals.Services
{
    /// <summary>
    /// Windows 通知中心样式的提醒。项目是单 exe、零 NuGet，靠反射调用 WinRT：
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
        private static Type _toastType;
        private static Type _xmlType;
        private static MethodInfo _loadXml;
        private static MethodInfo _show;
        private static object _notifier;

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
                        return !_unavailable;
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
                    if (_unavailable) return false;

                    object xml = Activator.CreateInstance(_xmlType);
                    _loadXml.Invoke(xml, new object[] { BuildToastXml(title, message) });
                    object toast = Activator.CreateInstance(_toastType, new object[] { xml });
                    _show.Invoke(_notifier, new object[] { toast });
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

            Type managerType = Type.GetType(
                "Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime", true);
            _toastType = Type.GetType(
                "Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType=WindowsRuntime", true);
            _xmlType = Type.GetType(
                "Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType=WindowsRuntime", true);
            _loadXml = FindMethod(_xmlType, "LoadXml", 1, false);

            MethodInfo create = null;
            foreach (MethodInfo method in managerType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name == "CreateToastNotifier" && method.GetParameters().Length == 1)
                {
                    create = method;
                    break;
                }
            }
            if (create == null) throw new MissingMethodException("ToastNotificationManager.CreateToastNotifier");

            _notifier = create.Invoke(null, new object[] { AppUserModelId });
            // 运行时对象是 __ComObject，方法要从声明的返回类型上找（与热点服务的坑一样）。
            _show = FindMethod(create.ReturnType, "Show", 1, false);
        }

        private static MethodInfo FindMethod(Type type, string name, int parameterCount, bool isStatic)
        {
            BindingFlags flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            foreach (MethodInfo method in type.GetMethods(flags))
            {
                if (method.Name != name) continue;
                if (method.GetParameters().Length != parameterCount) continue;
                return method;
            }
            throw new MissingMethodException(type.Name + "." + name);
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
