using System;
using System.Globalization;
using System.Text.Json;

namespace AdbDeviceVitals.Services
{
    /// <summary>
    /// 电信 189.cn 接口响应的纯解析（无 WebView2 依赖，测试项目可直接链接）。
    /// 实测响应（2026-11-21）：
    ///   queryMobileUsageInfo.do → data.remainingGeneralData "138.85GB" / usedGeneralData "16.15GB" /
    ///                             remainingDedicatedData "4.52GB" / usedDedicatedData "26.80GB"
    ///   queryMobileBalance.do   → data.mobileBalance "57.09"
    /// 值自带单位字符串，统一换算成 KB 数值供差分统计。
    /// </summary>
    internal static class TelecomTrafficParser
    {
        internal static BroadnetTraffic ParseUsage(string usageBody, string balanceBody)
        {
            if (string.IsNullOrWhiteSpace(usageBody)) return null;
            BroadnetTraffic result = new BroadnetTraffic();
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(usageBody))
                {
                    JsonElement root = doc.RootElement;
                    if (!TryGet(root, "data", out JsonElement data)) return null;
                    double usedGeneral = UnitToKb(GetString(data, "usedGeneralData"));
                    double remainGeneral = UnitToKb(GetString(data, "remainingGeneralData"));
                    double usedDedicated = UnitToKb(GetString(data, "usedDedicatedData"));
                    double remainDedicated = UnitToKb(GetString(data, "remainingDedicatedData"));
                    if (remainGeneral <= 0 && remainDedicated <= 0 && usedGeneral <= 0 && usedDedicated <= 0)
                        return null;
                    if (usedGeneral > 0 || remainGeneral > 0)
                        result.Packages.Add(MakePackage("通用流量", usedGeneral, remainGeneral));
                    if (usedDedicated > 0 || remainDedicated > 0)
                        result.Packages.Add(MakePackage("专用流量", usedDedicated, remainDedicated));
                    result.MonthlyUsedKb = usedGeneral + usedDedicated;
                    result.MonthlyUsed = G(result.MonthlyUsedKb);
                }
            }
            catch { return null; }
            if (result.Packages.Count == 0) return null;

            if (!string.IsNullOrWhiteSpace(balanceBody))
            {
                try
                {
                    using (JsonDocument doc = JsonDocument.Parse(balanceBody))
                    {
                        JsonElement root = doc.RootElement;
                        if (TryGet(root, "data", out JsonElement data))
                        {
                            string bal = GetString(data, "mobileBalance");
                            if (bal.Length == 0) bal = GetString(data, "generalCallBalance");
                            if (bal.Length > 0) result.Balance = bal;
                        }
                    }
                }
                catch { }
            }
            return result;
        }

        private static BroadnetPackage MakePackage(string name, double usedKb, double remainKb)
        {
            return new BroadnetPackage
            {
                Name = name,
                Used = G(usedKb),
                Remain = G(remainKb),
                Total = G(usedKb + remainKb),
                Unit = "G",
                UsedKb = usedKb,
                RemainKb = remainKb,
                TotalKb = usedKb + remainKb
            };
        }

        /// <summary>"138.85GB" / "0.00KB" → KB 数值。电信字段自带单位。</summary>
        internal static double UnitToKb(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string text = raw.Trim();
            int i = 0;
            while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
            double value;
            if (!double.TryParse(text.Substring(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return 0;
            string unit = text.Substring(i).Trim();
            if (unit.StartsWith("KB", StringComparison.OrdinalIgnoreCase)) return value;
            if (unit.StartsWith("MB", StringComparison.OrdinalIgnoreCase)) return value * 1024.0;
            if (unit.StartsWith("GB", StringComparison.OrdinalIgnoreCase)) return value * 1048576.0;
            if (unit.StartsWith("TB", StringComparison.OrdinalIgnoreCase)) return value * 1073741824.0;
            return value * 1048576.0; // 无单位按 G 处理
        }

        private static string G(double kb)
        {
            return (kb / 1048576.0).ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static bool TryGet(JsonElement el, string name, out JsonElement value)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value)) return true;
            value = default(JsonElement);
            return false;
        }

        private static string GetString(JsonElement el, string name)
        {
            JsonElement value;
            if (!TryGet(el, name, out value)) return string.Empty;
            return value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty) : value.ToString();
        }
    }
}
