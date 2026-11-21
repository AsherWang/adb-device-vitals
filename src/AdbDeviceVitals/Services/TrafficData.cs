using System.Collections.Generic;

namespace AdbDeviceVitals.Services
{
    /// <summary>套餐余量查询结果（广电/电信通用）。类名是历史遗留，内容为通用数据。</summary>
    public sealed class BroadnetTraffic
    {
        public string Phone = string.Empty;
        public string Balance = string.Empty;       // 话费余额，元
        public string MonthlySpend = string.Empty;  // 当月消费，元
        public string MonthlyUsed = string.Empty;   // 本月已用流量，形如 "120.19 G"（各包之和）
        public double MonthlyUsedKb;                // 本月已用原始 KB（未四舍五入），供用量统计差分
        public List<BroadnetPackage> Packages = new List<BroadnetPackage>();
    }

    public sealed class BroadnetPackage
    {
        public string Name = string.Empty;
        public string Used = string.Empty;
        public string Remain = string.Empty;
        public string Total = string.Empty;
        public string Unit = string.Empty;
        // 原始 KB 数值（未格式化），只用于用量统计的按包差分。
        public double UsedKb;
        public double RemainKb;
        public double TotalKb;
    }
}
