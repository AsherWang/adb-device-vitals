using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AdbDeviceVitals.Services
{
    /// <summary>
    /// 从手机的 SIM PLMN 自动识别流量运营商。
    /// gsm.sim.operator.numeric 是国际标准码（46011 电信 / 46015 广电），
    /// 比中文名可靠（不同 ROM 会返回「中国电信」「CHN-CT」等不同写法）。
    /// 结果按 serial 缓存一次，避免每次轮询都开 adb 进程。
    /// </summary>
    public sealed class CarrierDetector
    {
        private readonly AdbClient _adb;
        private readonly Dictionary<string, TrafficCarrier> _cache =
            new Dictionary<string, TrafficCarrier>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        public CarrierDetector(AdbClient adb)
        {
            _adb = adb;
        }

        /// <summary>读不到 / 不认识时返回 None，不抛异常（识别是锦上添花，不能阻断采集）。</summary>
        public Task<TrafficCarrier> DetectAsync(string serial)
        {
            return DetectAsync(serial, -1);
        }

        /// <summary>
        /// slot &gt;= 0 时按用户指定的卡槽取 PLMN（设置页配置口径，不切换手机数据卡）；
        /// slot &lt; 0 时沿用自动探测（双卡按活跃数据卡）。缓存按 serial+slot 分开。
        /// </summary>
        public async Task<TrafficCarrier> DetectAsync(string serial, int slot)
        {
            if (string.IsNullOrEmpty(serial) || _adb == null || !_adb.IsAvailable)
                return TrafficCarrier.None;

            string cacheKey = serial + "|" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
            lock (_gate)
            {
                TrafficCarrier cached;
                if (_cache.TryGetValue(cacheKey, out cached)) return cached;
            }

            TrafficCarrier detected = TrafficCarrier.None;
            try
            {
                CommandResult numericResult = await _adb.RunShellAsync(
                    serial, "getprop gsm.sim.operator.numeric", 4000).ConfigureAwait(false);
                string numeric = numericResult == null ? string.Empty : (numericResult.Output ?? string.Empty).Trim();
                if (numeric.Length == 0) return TrafficCarrier.None;

                if (slot >= 0)
                {
                    // 用户指定卡槽：直接取该槽 PLMN，与手机当前数据卡无关。
                    detected = CarrierMapping.FromPlmn(CarrierMapping.PlmnAtSlot(numeric, slot));
                }
                else
                {
                    // 双卡：按活跃数据卡的卡槽取对应那一段；任一步失败就回退到第一段。
                    if (numeric.IndexOf(',') >= 0)
                        detected = await DetectByDataSlotAsync(serial, numeric).ConfigureAwait(false);
                    if (detected == TrafficCarrier.None)
                        detected = CarrierMapping.FromPlmn(numeric);
                }
            }
            catch
            {
                // ADB 瞬断、设备掉线都按「未识别」处理，下轮可重试。
            }

            if (detected != TrafficCarrier.None)
                lock (_gate) { _cache[cacheKey] = detected; }
            return detected;
        }

        /// <summary>
        /// 双卡时按活跃数据卡识别：settings 拿 subId → dumpsys phone 拿该 subId 的卡槽 → 取该槽的 PLMN。
        /// dumpsys 输出约 40KB 且较慢，只在双卡、且按 serial 缓存未命中时读一次。
        /// </summary>
        private async Task<TrafficCarrier> DetectByDataSlotAsync(string serial, string numeric)
        {
            CommandResult subResult = await _adb.RunShellAsync(
                serial, "settings get global multi_sim_data_call", 4000).ConfigureAwait(false);
            int dataSubId;
            if (subResult == null || subResult.ExitCode != 0
                || !int.TryParse((subResult.Output ?? string.Empty).Trim(), out dataSubId)
                || dataSubId < 1) return TrafficCarrier.None;

            // 单卡机可能也返回 null/非法 subId，按未识别回退。
            CommandResult dumpResult = await _adb.RunShellAsync(
                serial, "dumpsys phone", 6000).ConfigureAwait(false);
            if (dumpResult == null || dumpResult.ExitCode != 0) return TrafficCarrier.None;

            int slot;
            if (!CarrierMapping.TryGetDataSlot(dumpResult.Output ?? string.Empty, dataSubId, out slot))
                return TrafficCarrier.None;

            return CarrierMapping.FromPlmn(CarrierMapping.PlmnAtSlot(numeric, slot));
        }

        /// <summary>
        /// 探测当前作为数据卡的物理卡槽号（0/1）；读不到返回 -1。
        /// 与 DetectByDataSlotAsync 用同一套 subId→slot 映射，供设置页填默认值。
        /// </summary>
        public async Task<int> DetectDataSlotAsync(string serial)
        {
            if (string.IsNullOrEmpty(serial) || _adb == null || !_adb.IsAvailable) return -1;
            try
            {
                CommandResult subResult = await _adb.RunShellAsync(
                    serial, "settings get global multi_sim_data_call", 4000).ConfigureAwait(false);
                int dataSubId;
                if (subResult == null || subResult.ExitCode != 0
                    || !int.TryParse((subResult.Output ?? string.Empty).Trim(), out dataSubId)
                    || dataSubId < 1) return -1;

                CommandResult dumpResult = await _adb.RunShellAsync(
                    serial, "dumpsys phone", 6000).ConfigureAwait(false);
                if (dumpResult == null || dumpResult.ExitCode != 0) return -1;

                int slot;
                return CarrierMapping.TryGetDataSlot(dumpResult.Output ?? string.Empty, dataSubId, out slot) ? slot : -1;
            }
            catch { return -1; }
        }

        /// <summary>换设备 / 重插卡后清缓存，让下一轮重新识别（含所有 slot 变体）。</summary>
        public void Invalidate(string serial)
        {
            lock (_gate)
            {
                if (string.IsNullOrEmpty(serial)) { _cache.Clear(); return; }
                List<string> stale = new List<string>();
                foreach (string key in _cache.Keys)
                    if (key.StartsWith(serial + "|", StringComparison.OrdinalIgnoreCase)) stale.Add(key);
                foreach (string key in stale) _cache.Remove(key);
            }
        }
    }
}
