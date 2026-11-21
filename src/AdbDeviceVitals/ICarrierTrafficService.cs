using System.Threading.Tasks;
using AdbDeviceVitals.Services;

namespace AdbDeviceVitals
{
    /// <summary>
    /// 运营商流量查询服务的统一契约。广电与电信各自实现，主窗口只认这个接口，
    /// 根据当前设备的运营商映射选择实例。两家共用 BroadnetTraffic 数据类
    /// （名字是历史遗留，内容为通用套餐余量）。
    /// </summary>
    internal interface ICarrierTrafficService
    {
        /// <summary>启动初始化。pathPrefix 用于隔离两家的数据文件（快照 / WebView / 日志）。</summary>
        void Initialize(string configDirectory, string pathPrefix);

        bool IsLoggedIn { get; }
        string Status { get; }
        bool HasSnapshot { get; }
        bool LastAttemptExpired { get; }

        Task<bool> EnsureLoggedInAsync();
        Task<bool> EnsureLoggedInSerializedAsync();
        Task<BroadnetTraffic> EnsureAndReadTrafficAsync();
        Task<BroadnetTraffic> RefreshTrafficSilentlyAsync();
        Task<BroadnetTraffic> ReadTrafficAsync();
        void Trace(string message);
        void Shutdown();
    }
}
