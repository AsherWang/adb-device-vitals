using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

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
    /// .NET Framework 版本靠反射调用 WinRT；迁移到 .NET 10 之后改用 Windows SDK
    /// 投影（CsWinRT）的强类型 API，行为与原来的反射版本一致，但不再依赖
    /// ContentType=WindowsRuntime 那套 .NET 5 以后已移除的互操作。
    /// </summary>
    public sealed class PcHotspotService
    {
        // 开启/关闭后等待系统稳定到目标状态的时间。每轮重建 manager，避免缓存状态。
        private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ConfirmInterval = TimeSpan.FromSeconds(1);
        // 等待手机网络就绪（RNDIS 网卡拿到地址）的时间，要覆盖 USB 共享切换与网卡重枚举。
        private static readonly TimeSpan UpstreamWaitWindow = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan UpstreamWaitInterval = TimeSpan.FromSeconds(3);

        /// <summary>读取当前热点状态，不做任何修改。可在后台线程调用。</summary>
        public PcHotspotSnapshot Read()
        {
            try
            {
                Upstream upstream = FindUpstream();
                ConnectionProfile profile = upstream != null
                    ? upstream.Profile
                    : NetworkInformation.GetInternetConnectionProfile();
                if (profile == null) return PcHotspotSnapshot.Unavailable("找不到可共享的网络连接。");

                NetworkOperatorTetheringManager manager = CreateManager(profile);
                if (manager == null) return PcHotspotSnapshot.Unavailable("无法创建热点管理器。");

                return PcHotspotSnapshot.Available(
                    StateOf(manager.TetheringOperationalState),
                    ReadSsid(manager),
                    Math.Max(0, (int)manager.ClientCount),
                    Math.Max(0, (int)manager.MaxClientCount),
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
                    return await SetEnabledCoreAsync(true, cancellationToken, log).ConfigureAwait(false);
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
                PcHotspotResult result = SetEnabledCoreAsync(enable, CancellationToken.None, log)
                    .GetAwaiter().GetResult();
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

        private async Task<PcHotspotResult> SetEnabledCoreAsync(bool enable, CancellationToken cancellationToken, TetherLog log)
        {
            Upstream upstream = FindUpstream();
            ConnectionProfile profile = upstream != null
                ? upstream.Profile
                : NetworkInformation.GetInternetConnectionProfile();
            if (profile == null) return PcHotspotResult.Failed("找不到可共享的网络连接。");

            NetworkOperatorTetheringManager manager = CreateManager(profile);
            if (manager == null) return PcHotspotResult.Failed("无法创建热点管理器。");

            PcHotspotState state = StateOf(manager.TetheringOperationalState);
            if (enable && state == PcHotspotState.On) return PcHotspotResult.AlreadyActive();
            if (!enable && state == PcHotspotState.Off) return PcHotspotResult.AlreadyInactive();

            if (enable)
            {
                if (upstream == null)
                    return PcHotspotResult.Failed("未找到手机共享出来的网卡，请确认 USB 网络共享已开启。");
                if (!upstream.HasLink)
                    return PcHotspotResult.Failed("手机网络还没有就绪，请稍后再试。");

                TetheringCapability capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
                if (capability != TetheringCapability.Enabled)
                    return PcHotspotResult.Failed("系统不允许开启热点：" + CapabilityName((int)capability));
            }

            log.Write("hotspot command " + (enable ? "start" : "stop") + " state=" + state);
            Stopwatch watch = Stopwatch.StartNew();
            TetheringOperationStatus operationStatus;
            try
            {
                // CsWinRT 的 IAsyncOperation 可直接 await；取消只在下面的确认循环里生效。
                NetworkOperatorTetheringOperationResult result = enable
                    ? await manager.StartTetheringAsync()
                    : await manager.StopTetheringAsync();
                operationStatus = result.Status;
            }
            catch (OperationCanceledException)
            {
                return PcHotspotResult.Stopped();
            }
            catch (Exception ex)
            {
                return PcHotspotResult.Failed((enable ? "系统未能开启热点：" : "系统未能关闭热点：") + ExceptionMessage(ex));
            }
            watch.Stop();
            log.Write("hotspot command done status=" + operationStatus + " ms=" + watch.ElapsedMilliseconds);

            if (operationStatus != TetheringOperationStatus.Success)
                return PcHotspotResult.Failed((enable ? "系统未能开启热点：" : "系统未能关闭热点：") + OperationStatusName(operationStatus));

            PcHotspotState wanted = enable ? PcHotspotState.On : PcHotspotState.Off;
            DateTime deadline = DateTime.UtcNow + ConfirmWindow;
            while (DateTime.UtcNow < deadline)
            {
                if (cancellationToken.IsCancellationRequested) return PcHotspotResult.Stopped();
                try
                {
                    // 每轮重建 manager：WinRT 管理器会缓存旧状态，复用它容易一直读到 InTransition。
                    NetworkOperatorTetheringManager current = CreateManager(profile);
                    if (current != null && StateOf(current.TetheringOperationalState) == wanted)
                        return enable ? PcHotspotResult.Enabled() : PcHotspotResult.Disabled();
                }
                catch
                {
                }
                await Task.Delay(ConfirmInterval, cancellationToken).ConfigureAwait(false);
            }

            return PcHotspotResult.Failed(enable
                ? "系统已接受请求，但热点没有进入共享状态。"
                : "系统已接受请求，但热点仍在运行。");
        }

        private static NetworkOperatorTetheringManager CreateManager(ConnectionProfile profile)
        {
            if (profile == null) return null;
            try { return NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile); }
            catch { return null; }
        }

        private static string ReadSsid(NetworkOperatorTetheringManager manager)
        {
            try { return manager.GetCurrentAccessPointConfiguration().Ssid ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>
        /// 找到「手机通过 USB 共享出来的那块网卡」。不能直接用默认连接：
        /// 默认路由可能是别的网络，那样会把错误的上游共享出去。
        /// </summary>
        private static Upstream FindUpstream()
        {
            Upstream best = null;
            try
            {
                foreach (ConnectionProfile profile in NetworkInformation.GetConnectionProfiles())
                {
                    Guid adapterId;
                    try { adapterId = profile.NetworkAdapter.NetworkAdapterId; }
                    catch { continue; }

                    Adapter adapter = AdapterOf(adapterId);
                    if (!IsUsbSharedAdapter(adapter.Description)) continue;

                    int level;
                    try { level = (int)profile.GetNetworkConnectivityLevel(); }
                    catch { level = 0; }

                    Upstream candidate = new Upstream(profile, adapter.Name, level);
                    if (best == null || candidate.Rank > best.Rank) best = candidate;
                }
            }
            catch
            {
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

        private static PcHotspotState StateOf(TetheringOperationalState value)
        {
            switch (value)
            {
                case TetheringOperationalState.On: return PcHotspotState.On;
                case TetheringOperationalState.Off: return PcHotspotState.Off;
                case TetheringOperationalState.InTransition: return PcHotspotState.Transitioning;
                default: return PcHotspotState.Unknown;
            }
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

        private static string OperationStatusName(TetheringOperationStatus value)
        {
            switch (value)
            {
                case TetheringOperationStatus.Unknown: return "原因未知";
                case TetheringOperationStatus.MobileBroadbandDeviceOff: return "移动网络设备已关闭";
                case TetheringOperationStatus.WiFiDeviceOff: return "Wi-Fi 设备已关闭";
                case TetheringOperationStatus.EntitlementCheckTimeout: return "网络授权检查超时";
                case TetheringOperationStatus.EntitlementCheckFailure: return "网络授权检查未通过";
                case TetheringOperationStatus.OperationInProgress: return "已有其它操作正在进行";
                case TetheringOperationStatus.BluetoothDeviceOff: return "蓝牙设备已关闭";
                case TetheringOperationStatus.NetworkLimitedConnectivity: return "上游网络连通性受限";
                default: return "未知错误（代码 " + ((int)value).ToString(CultureInfo.InvariantCulture) + "）";
            }
        }

        internal static string ExceptionMessage(Exception ex)
        {
            Exception current = ex;
            while (current.InnerException != null) current = current.InnerException;
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
            public Upstream(ConnectionProfile profile, string name, int connectivityLevel)
            {
                Profile = profile;
                Name = string.IsNullOrEmpty(name) ? "USB 网卡" : name;
                ConnectivityLevel = connectivityLevel;
            }

            public ConnectionProfile Profile { get; private set; }
            public string Name { get; private set; }
            public int ConnectivityLevel { get; private set; }

            /// <summary>有链路层地址即视为可用；InternetAccess 只是更高的优先级。</summary>
            public bool HasLink { get { return ConnectivityLevel >= 1; } }

            public int Rank { get { return ConnectivityLevel; } }
        }
    }
}
