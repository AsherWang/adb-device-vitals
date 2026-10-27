using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace AdbDeviceVitals.Services
{
    public enum PcHotspotState
    {
        Off,
        On,
        Transitioning,
        Unknown
    }

    public sealed class PcHotspotSnapshot
    {
        public bool Supported { get; private set; }
        public PcHotspotState State { get; private set; }
        public string Ssid { get; private set; }
        public int ClientCount { get; private set; }
        public int MaxClientCount { get; private set; }
        public string UpstreamName { get; private set; }
        public string Error { get; private set; }

        private PcHotspotSnapshot(bool supported, PcHotspotState state, string ssid, int clients, int maxClients, string upstream, string error)
        {
            Supported = supported;
            State = state;
            Ssid = ssid ?? string.Empty;
            ClientCount = clients;
            MaxClientCount = maxClients;
            UpstreamName = upstream ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public static PcHotspotSnapshot Available(PcHotspotState state, string ssid, int clients, int maxClients, string upstream)
        {
            return new PcHotspotSnapshot(true, state, ssid, clients, maxClients, upstream, string.Empty);
        }

        public static PcHotspotSnapshot Unavailable(string error)
        {
            return new PcHotspotSnapshot(false, PcHotspotState.Unknown, string.Empty, 0, 0, string.Empty, error);
        }
    }

    public sealed class PcHotspotResult
    {
        public bool Completed { get; private set; }
        public bool Succeeded { get; private set; }
        public bool Changed { get; private set; }
        public string Message { get; private set; }

        private PcHotspotResult(bool completed, bool succeeded, bool changed, string message)
        {
            Completed = completed;
            Succeeded = succeeded;
            Changed = changed;
            Message = message ?? string.Empty;
        }

        public static PcHotspotResult Stopped()
        {
            return new PcHotspotResult(false, false, false, string.Empty);
        }

        public static PcHotspotResult AlreadyActive()
        {
            return new PcHotspotResult(true, true, false, "电脑热点已在运行。");
        }

        public static PcHotspotResult AlreadyInactive()
        {
            return new PcHotspotResult(true, true, false, "电脑热点已关闭。");
        }

        public static PcHotspotResult Enabled()
        {
            return new PcHotspotResult(true, true, true, "已开启电脑热点。");
        }

        public static PcHotspotResult Disabled()
        {
            return new PcHotspotResult(true, true, true, "已关闭电脑热点。");
        }

        public static PcHotspotResult Failed(string message)
        {
            return new PcHotspotResult(true, false, false, message);
        }
    }

    /// <summary>
    /// 控制 Windows 自带的移动热点（与「设置 - 移动热点」开关等价）。
    /// 通过反射调用 WinRT 的 NetworkOperatorTetheringManager，避免引入新依赖。
    /// </summary>
    public sealed class PcHotspotService
    {
        // 开启/关闭后等待系统稳定到目标状态的时间。系统从 InTransition 落到 On/Off
        // 通常一秒内完成，但网卡释放可能慢一些；查询本身有成本，间隔取 1 秒。
        private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ConfirmInterval = TimeSpan.FromSeconds(1);
        // 等待手机网络就绪（RNDIS 网卡拿到地址）的时间，要覆盖 USB 共享切换与网卡重枚举。
        private static readonly TimeSpan UpstreamWaitWindow = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan UpstreamWaitInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan AsyncPollInterval = TimeSpan.FromMilliseconds(120);

        /// <summary>读取当前热点状态，不做任何修改。可在后台线程调用。</summary>
        public PcHotspotSnapshot Read()
        {
            string loadError;
            if (!WinRt.TryLoad(out loadError)) return PcHotspotSnapshot.Unavailable(loadError);

            try
            {
                Upstream upstream = FindUpstream();
                object manager = CreateManager(upstream != null ? upstream.Profile : WinRt.InternetProfile());
                if (manager == null) return PcHotspotSnapshot.Unavailable("无法创建热点管理器。");

                int clients = WinRt.ReadInt(manager, "ClientCount");
                int maxClients = WinRt.ReadInt(manager, "MaxClientCount");
                PcHotspotState state = StateOf(WinRt.ReadInt(manager, "TetheringOperationalState"));
                return PcHotspotSnapshot.Available(
                    state,
                    ReadSsid(manager),
                    Math.Max(0, clients),
                    Math.Max(0, maxClients),
                    upstream == null ? string.Empty : upstream.Name);
            }
            catch (Exception ex)
            {
                return PcHotspotSnapshot.Unavailable(ExceptionMessage(ex));
            }
        }

        /// <summary>等待手机网络就绪后开启热点。调用方负责在后台线程执行。</summary>
        public async Task<PcHotspotResult> EnsureAsync(CancellationToken cancellationToken)
        {
            TetherLog log = new TetherLog();
            log.Write("hotspot auto-enable start");
            DateTime deadline = DateTime.UtcNow + UpstreamWaitWindow;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!AppSettings.AutoStartPcHotspot)
                {
                    log.Write("hotspot auto-enable setting off");
                    return PcHotspotResult.Stopped();
                }

                PcHotspotSnapshot snapshot = Read();
                if (!snapshot.Supported)
                {
                    log.Write("hotspot unsupported " + snapshot.Error);
                    return PcHotspotResult.Failed(snapshot.Error);
                }
                if (snapshot.State == PcHotspotState.On)
                {
                    log.Write("hotspot already on");
                    return PcHotspotResult.AlreadyActive();
                }

                Upstream upstream = FindUpstream();
                if (upstream != null && upstream.HasLink)
                {
                    log.Write("hotspot upstream ready " + upstream.Name + " level=" + upstream.ConnectivityLevel);
                    return SetEnabledCore(true, cancellationToken, log);
                }

                if (DateTime.UtcNow >= deadline)
                {
                    log.Write("hotspot upstream wait timeout");
                    return PcHotspotResult.Failed("等待手机网络就绪超时，未开启电脑热点。");
                }
                await Task.Delay(UpstreamWaitInterval, cancellationToken).ConfigureAwait(false);
            }

            return PcHotspotResult.Stopped();
        }

        /// <summary>手动开/关热点。调用方负责在后台线程执行。</summary>
        public PcHotspotResult SetEnabled(bool enable)
        {
            TetherLog log = new TetherLog();
            log.Write("hotspot manual " + (enable ? "enable" : "disable"));
            try
            {
                PcHotspotResult result = SetEnabledCore(enable, CancellationToken.None, log);
                log.Write("hotspot manual result " + result.Message);
                return result;
            }
            catch (OperationCanceledException)
            {
                return PcHotspotResult.Stopped();
            }
            catch (Exception ex)
            {
                log.Write("hotspot manual exception " + ExceptionMessage(ex));
                return PcHotspotResult.Failed(ExceptionMessage(ex));
            }
        }

        private PcHotspotResult SetEnabledCore(bool enable, CancellationToken cancellationToken, TetherLog log)
        {
            string loadError;
            if (!WinRt.TryLoad(out loadError)) return PcHotspotResult.Failed(loadError);

            Upstream upstream = FindUpstream();
            object profile = upstream != null ? upstream.Profile : WinRt.InternetProfile();
            if (profile == null) return PcHotspotResult.Failed("找不到可共享的网络连接。");

            object manager = CreateManager(profile);
            if (manager == null) return PcHotspotResult.Failed("无法创建热点管理器。");

            PcHotspotState state = StateOf(WinRt.ReadInt(manager, "TetheringOperationalState"));
            if (enable && state == PcHotspotState.On) return PcHotspotResult.AlreadyActive();
            if (!enable && state == PcHotspotState.Off) return PcHotspotResult.AlreadyInactive();

            if (enable)
            {
                if (upstream == null)
                    return PcHotspotResult.Failed("未找到手机共享出来的网卡，请确认 USB 网络共享已开启。");
                if (!upstream.HasLink)
                    return PcHotspotResult.Failed("手机网络还没有就绪，请稍后再试。");

                int capability = WinRt.ReadCapability(profile);
                if (capability != 0) return PcHotspotResult.Failed("系统不允许开启热点：" + CapabilityName(capability));
            }

            log.Write("hotspot command " + (enable ? "start" : "stop") + " state=" + state);
            Stopwatch watch = Stopwatch.StartNew();
            int operationStatus = enable ? WinRt.Start(manager) : WinRt.Stop(manager);
            watch.Stop();
            log.Write("hotspot command done status=" + operationStatus + " ms=" + watch.ElapsedMilliseconds);

            if (operationStatus != 0)
                return PcHotspotResult.Failed((enable ? "系统未能开启热点：" : "系统未能关闭热点：") + OperationStatusName(operationStatus));

            PcHotspotState wanted = enable ? PcHotspotState.On : PcHotspotState.Off;
            DateTime deadline = DateTime.UtcNow + ConfirmWindow;
            while (DateTime.UtcNow < deadline)
            {
                if (cancellationToken.IsCancellationRequested) return PcHotspotResult.Stopped();
                object current = null;
                try { current = CreateManager(profile); }
                catch { }
                if (current != null && StateOf(WinRt.ReadInt(current, "TetheringOperationalState")) == wanted)
                    return enable ? PcHotspotResult.Enabled() : PcHotspotResult.Disabled();
                Thread.Sleep(ConfirmInterval);
            }

            return PcHotspotResult.Failed(enable
                ? "系统已接受请求，但热点没有进入共享状态。"
                : "系统已接受请求，但热点仍在运行。");
        }

        private static object CreateManager(object profile)
        {
            if (profile == null) return null;
            return WinRt.CreateFromProfile(profile);
        }

        private string ReadSsid(object manager)
        {
            try
            {
                object config = WinRt.CurrentAccessPointConfiguration(manager);
                if (config == null) return string.Empty;
                object ssid = config.GetType().GetProperty("Ssid").GetValue(config, null);
                return Convert.ToString(ssid, CultureInfo.InvariantCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 找到「手机通过 USB 共享出来的那块网卡」。不能直接用默认连接：
        /// 默认路由可能是别的网络，那样会把错误的上游共享出去。
        /// </summary>
        private static Upstream FindUpstream()
        {
            IEnumerable profiles = WinRt.ConnectionProfiles();
            if (profiles == null) return null;

            Upstream best = null;
            foreach (object profile in profiles)
            {
                Guid adapterId;
                if (!WinRt.TryReadAdapterId(profile, out adapterId)) continue;
                Adapter adapter = AdapterOf(adapterId);
                if (!IsUsbSharedAdapter(adapter.Description)) continue;

                int level = WinRt.ReadConnectivityLevel(profile);
                Upstream candidate = new Upstream(profile, adapter.Name, level);
                if (best == null || candidate.Rank > best.Rank) best = candidate;
            }
            return best;
        }

        private static Adapter AdapterOf(Guid adapterId)
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    Guid id;
                    if (!Guid.TryParse(nic.Id, out id) || id != adapterId) continue;
                    return new Adapter(nic.Name ?? string.Empty, nic.Description ?? string.Empty);
                }
            }
            catch
            {
            }
            return new Adapter(string.Empty, string.Empty);
        }

        private static bool IsUsbSharedAdapter(string description)
        {
            if (string.IsNullOrEmpty(description)) return false;
            string text = description.ToLowerInvariant();
            return text.IndexOf("ndis", StringComparison.Ordinal) >= 0
                || text.IndexOf("rndis", StringComparison.Ordinal) >= 0
                || text.IndexOf("ncm", StringComparison.Ordinal) >= 0
                || text.IndexOf("usb ethernet", StringComparison.Ordinal) >= 0
                || text.IndexOf("usb 以太网", StringComparison.Ordinal) >= 0;
        }

        private static PcHotspotState StateOf(int value)
        {
            // TetheringOperationalState: Unknown=0, On=1, Off=2, InTransition=3
            if (value == 1) return PcHotspotState.On;
            if (value == 2) return PcHotspotState.Off;
            if (value == 3) return PcHotspotState.Transitioning;
            return PcHotspotState.Unknown;
        }

        private static string CapabilityName(int value)
        {
            switch (value)
            {
                case 1: return "该连接被系统禁用";
                case 2: return "组策略禁止使用移动热点";
                case 3: return "硬件不支持";
                case 4: return "运营商限制";
                case 5: return "系统版本限制";
                case 6: return "缺少必要的应用";
                default: return "原因未知（代码 " + value.ToString(CultureInfo.InvariantCulture) + "）";
            }
        }

        private static string OperationStatusName(int value)
        {
            switch (value)
            {
                case 0: return "成功";
                case 1: return "原因未知";
                case 2: return "移动网络设备已关闭";
                case 3: return "Wi-Fi 设备已关闭";
                case 4: return "网络授权检查超时";
                case 5: return "网络授权检查未通过";
                case 6: return "已有其它操作正在进行";
                case 7: return "蓝牙设备已关闭";
                case 8: return "上游网络连通性受限";
            }

            string async = WinRt.DescribeAsyncFailure(value);
            if (!string.IsNullOrEmpty(async)) return async;
            if (value < 0) return "系统调用失败（代码 " + value.ToString(CultureInfo.InvariantCulture) + "）";
            return "未知错误（代码 " + value.ToString(CultureInfo.InvariantCulture) + "）";
        }

        internal static string ExceptionMessage(Exception ex)
        {
            Exception current = ex;
            while (current.InnerException != null) current = current.InnerException;
            if (current is TargetInvocationException && current.InnerException != null) current = current.InnerException;
            return string.IsNullOrEmpty(current.Message) ? current.GetType().Name : current.Message;
        }

        private sealed class Adapter
        {
            public Adapter(string name, string description)
            {
                Name = name ?? string.Empty;
                Description = description ?? string.Empty;
            }

            public string Name { get; private set; }
            public string Description { get; private set; }
        }

        private sealed class Upstream
        {
            public Upstream(object profile, string name, int connectivityLevel)
            {
                Profile = profile;
                Name = string.IsNullOrEmpty(name) ? "USB 网卡" : name;
                ConnectivityLevel = connectivityLevel;
            }

            public object Profile { get; private set; }
            public string Name { get; private set; }
            public int ConnectivityLevel { get; private set; }

            /// <summary>有链路层地址即视为可用；InternetAccess 只是更高的优先级。</summary>
            public bool HasLink { get { return ConnectivityLevel >= 1; } }

            public int Rank { get { return ConnectivityLevel; } }
        }

        /// <summary>
        /// WinRT 反射入口。只在这里处理投影类型，其余代码不接触反射细节。
        /// Status 定义在 IAsyncInfo 上，必须单独取接口，不能从 IAsyncOperation 上找到。
        /// </summary>
        private static class WinRt
        {
            // 异步操作自身（非业务结果）的失败码，避免与 TetheringOperationStatus 的取值混淆。
            private const int OperationCanceled = -2;
            private const int OperationFaulted = -3;
            private const int OperationTimedOut = -4;

            private static readonly object Gate = new object();
            private static bool _loaded;
            private static string _error = string.Empty;
            private static string _lastAsyncError = string.Empty;
            private static Type _networkInformation;
            private static Type _managerType;
            private static Type _asyncInfo;
            private static MethodInfo _getProfiles;
            private static MethodInfo _getInternetProfile;
            private static MethodInfo _createFromProfile;
            private static MethodInfo _capabilityFromProfile;
            private static MethodInfo _startTethering;
            private static MethodInfo _stopTethering;

            public static bool TryLoad(out string error)
            {
                lock (Gate)
                {
                    if (!_loaded)
                    {
                        _loaded = true;
                        try
                        {
                            _networkInformation = Type.GetType(
                                "Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime", true);
                            _managerType = Type.GetType(
                                "Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime", true);
                            _asyncInfo = Type.GetType(
                                "Windows.Foundation.IAsyncInfo, Windows.Foundation, ContentType=WindowsRuntime", true);

                            _getProfiles = FindMethod(_networkInformation, "GetConnectionProfiles", 0, true);
                            _getInternetProfile = FindMethod(_networkInformation, "GetInternetConnectionProfile", 0, true);
                            _createFromProfile = FindMethod(_managerType, "CreateFromConnectionProfile", 1, true);
                            _capabilityFromProfile = FindMethod(_managerType, "GetTetheringCapabilityFromConnectionProfile", 1, true);
                            _startTethering = FindMethod(_managerType, "StartTetheringAsync", 0, false);
                            _stopTethering = FindMethod(_managerType, "StopTetheringAsync", 0, false);
                        }
                        catch (Exception ex)
                        {
                            _error = "当前系统不支持移动热点接口：" + ExceptionMessage(ex);
                        }
                    }
                    error = _error;
                    return _error.Length == 0;
                }
            }

            public static IEnumerable ConnectionProfiles()
            {
                return _getProfiles.Invoke(null, null) as IEnumerable;
            }

            public static object InternetProfile()
            {
                try { return _getInternetProfile.Invoke(null, null); }
                catch { return null; }
            }

            public static object CreateFromProfile(object profile)
            {
                return _createFromProfile.Invoke(null, new object[] { profile });
            }

            public static int ReadCapability(object profile)
            {
                try { return ToInt(_capabilityFromProfile.Invoke(null, new object[] { profile })); }
                catch { return 0; }
            }

            public static int Start(object manager)
            {
                return Complete(_startTethering.Invoke(manager, null), _startTethering.ReturnType);
            }

            public static int Stop(object manager)
            {
                return Complete(_stopTethering.Invoke(manager, null), _stopTethering.ReturnType);
            }

            public static int ReadInt(object target, string property)
            {
                try
                {
                    PropertyInfo info = target.GetType().GetProperty(property);
                    return info == null ? -1 : ToInt(info.GetValue(target, null));
                }
                catch
                {
                    return -1;
                }
            }

            public static object CurrentAccessPointConfiguration(object manager)
            {
                MethodInfo method = FindMethod(manager.GetType(), "GetCurrentAccessPointConfiguration", 0, false);
                return method.Invoke(manager, null);
            }

            public static bool TryReadAdapterId(object profile, out Guid id)
            {
                id = Guid.Empty;
                try
                {
                    object adapter = profile.GetType().GetProperty("NetworkAdapter").GetValue(profile, null);
                    if (adapter == null) return false;
                    object value = adapter.GetType().GetProperty("NetworkAdapterId").GetValue(adapter, null);
                    if (value is Guid)
                    {
                        id = (Guid)value;
                        return true;
                    }
                }
                catch
                {
                }
                return false;
            }

            public static int ReadConnectivityLevel(object profile)
            {
                try
                {
                    MethodInfo method = FindMethod(profile.GetType(), "GetNetworkConnectivityLevel", 0, false);
                    return ToInt(method.Invoke(profile, null));
                }
                catch
                {
                    return 0;
                }
            }

            /// <summary>轮询 IAsyncInfo.Status 直到完成，返回结果对象里的 Status 枚举值（0 为成功）。</summary>
            private static int Complete(object operation, Type operationType)
            {
                PropertyInfo status = _asyncInfo.GetProperty("Status");
                int code = -1;
                // AsyncStatus: 0=Started 1=Completed 2=Canceled 3=Error
                for (int i = 0; i < 600; i++)
                {
                    code = ToInt(status.GetValue(operation, null));
                    if (code != 0) break;
                    Thread.Sleep(AsyncPollInterval);
                }

                if (code == 0) return OperationTimedOut;
                if (code == 2) return OperationCanceled;
                if (code != 1)
                {
                    _lastAsyncError = ReadAsyncError(operation);
                    return OperationFaulted;
                }

                // 运行时的对象是 __ComObject，没有 GetResults，必须按接口类型查找。
                MethodInfo results = FindMethod(operationType, "GetResults", 0, false);
                object result = results.Invoke(operation, null);
                if (result == null) return 0;
                PropertyInfo resultStatus = operationType.GetGenericArguments()[0].GetProperty("Status");
                return resultStatus == null ? 0 : ToInt(resultStatus.GetValue(result, null));
            }

            /// <summary>异步操作本身的失败原因，用于补充提示。</summary>
            public static string DescribeAsyncFailure(int code)
            {
                if (code == OperationCanceled) return "操作已被系统取消。";
                if (code == OperationTimedOut) return "操作超时，系统没有响应。";
                if (code == OperationFaulted && !string.IsNullOrEmpty(_lastAsyncError)) return _lastAsyncError;
                return string.Empty;
            }

            private static string ReadAsyncError(object operation)
            {
                try
                {
                    object error = _asyncInfo.GetProperty("ErrorCode").GetValue(operation, null);
                    Exception exception = error as Exception;
                    return exception == null ? string.Empty : ExceptionMessage(exception);
                }
                catch
                {
                    return string.Empty;
                }
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

            private static int ToInt(object value)
            {
                if (value == null) return -1;
                try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
                catch { return -1; }
            }
        }
    }
}
