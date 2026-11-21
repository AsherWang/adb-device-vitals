using System;
using System.Collections.Generic;
using System.Text;

namespace AdbDeviceVitals.Services
{
    /// <summary>余量卡支持的运营商。</summary>
    public enum TrafficCarrier
    {
        None = 0,
        Broadnet = 1,
        Telecom = 2
    }

    /// <summary>
    /// 设备序列号 → 运营商的映射。存成一个字符串字段（settings.json 用正则逐项解析，
    /// 不适合嵌套对象）。格式：serial|carrier;serial|carrier，carrier 取枚举数值。
    /// 序列号里不会出现 | 和 ;，所以不需要额外转义。
    /// </summary>
    internal static class CarrierMapping
    {
        private const char EntrySeparator = ';';
        private const char FieldSeparator = '|';

        internal static TrafficCarrier Get(string text, string serial)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(serial)) return TrafficCarrier.None;
            string[] entries = text.Split(EntrySeparator);
            foreach (string entry in entries)
            {
                int split = entry.IndexOf(FieldSeparator);
                if (split <= 0) continue;
                if (!string.Equals(entry.Substring(0, split), serial, StringComparison.OrdinalIgnoreCase)) continue;
                int code;
                if (int.TryParse(entry.Substring(split + 1), out code) && IsKnown(code))
                    return (TrafficCarrier)code;
            }
            return TrafficCarrier.None;
        }

        internal static string Set(string text, string serial, TrafficCarrier carrier)
        {
            Dictionary<string, TrafficCarrier> map = Parse(text);
            if (string.IsNullOrEmpty(serial)) return Serialize(map);
            if (carrier == TrafficCarrier.None) map.Remove(serial);
            else map[serial] = carrier;
            return Serialize(map);
        }

        internal static Dictionary<string, TrafficCarrier> Parse(string text)
        {
            Dictionary<string, TrafficCarrier> map = new Dictionary<string, TrafficCarrier>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return map;
            foreach (string entry in text.Split(EntrySeparator))
            {
                int split = entry.IndexOf(FieldSeparator);
                if (split <= 0) continue;
                string serial = entry.Substring(0, split).Trim();
                int code;
                if (serial.Length > 0 && int.TryParse(entry.Substring(split + 1), out code) && IsKnown(code))
                    map[serial] = (TrafficCarrier)code;
            }
            return map;
        }

        internal static string Serialize(Dictionary<string, TrafficCarrier> map)
        {
            if (map == null || map.Count == 0) return string.Empty;
            StringBuilder text = new StringBuilder();
            foreach (KeyValuePair<string, TrafficCarrier> pair in map)
            {
                if (text.Length > 0) text.Append(EntrySeparator);
                text.Append(pair.Key).Append(FieldSeparator).Append((int)pair.Value);
            }
            return text.ToString();
        }

        internal static string DisplayName(TrafficCarrier carrier)
        {
            switch (carrier)
            {
                case TrafficCarrier.Broadnet: return "中国广电";
                case TrafficCarrier.Telecom: return "中国电信";
                default: return "未设置";
            }
        }

        /// <summary>
        /// 从 adb shell getprop gsm.sim.operator.numeric 的返回值识别运营商。
        /// 返回的是逗号分隔的 PLMN（双卡有两段），取第一段有效的即可——USB 共享走的是数据卡，
        /// 双卡不同网时无法从属性判断哪张在跑，取第一张并允许用户在设置里手动覆盖。
        /// </summary>
        internal static TrafficCarrier FromPlmn(string numeric)
        {
            if (string.IsNullOrEmpty(numeric)) return TrafficCarrier.None;
            foreach (string part in numeric.Split(','))
            {
                string code = part.Trim();
                if (code.Length < 5) continue;
                switch (code)
                {
                    case "46015": return TrafficCarrier.Broadnet;   // 中国广电
                    case "46011": return TrafficCarrier.Telecom;    // 中国电信
                    default: continue;                               // 移动/联通/未知，一期不支持
                }
            }
            return TrafficCarrier.None;
        }

        /// <summary>
        /// 从 dumpsys phone 的日志里解析「数据卡 subId → 卡槽序号」映射。
        /// 日志形如 "subId changed for slot: 1, -1 -> 1"（slot 1 上的订阅变成 subId 1）。
        /// 同一条槽位可能出现多次变更，取最后一次，即当前生效的映射。
        /// 解析不到返回 false，让调用方回退到「取第一段 PLMN」。
        /// </summary>
        internal static bool TryGetDataSlot(string dumpsysPhone, int dataSubId, out int slot)
        {
            slot = -1;
            if (string.IsNullOrEmpty(dumpsysPhone) || dataSubId < 1) return false;
            const string marker = "subId changed for slot: ";
            const string arrow = " -> ";
            bool found = false;
            int cursor = 0;
            while (cursor < dumpsysPhone.Length)
            {
                int at = dumpsysPhone.IndexOf(marker, cursor, StringComparison.Ordinal);
                if (at < 0) break;
                int slotStart = at + marker.Length;
                int comma = dumpsysPhone.IndexOf(',', slotStart);
                int arrowAt = comma < 0 ? -1 : dumpsysPhone.IndexOf(arrow, comma, StringComparison.Ordinal);
                if (comma > slotStart && arrowAt > comma)
                {
                    int slotValue, subValue;
                    string slotText = dumpsysPhone.Substring(slotStart, comma - slotStart).Trim();
                    string subText = dumpsysPhone.Substring(arrowAt + arrow.Length).Split('\n')[0].Trim();
                    if (int.TryParse(slotText, out slotValue) && int.TryParse(subText, out subValue)
                        && slotValue >= 0 && subValue == dataSubId)
                    {
                        slot = slotValue;
                        found = true;
                    }
                }
                cursor = arrowAt < 0 ? slotStart : arrowAt + arrow.Length;
            }
            return found;
        }

        /// <summary>
        /// 从逗号分隔的多卡 PLMN 里取指定卡槽的一段（Android 按卡槽顺序排列）。
        /// 越界/无逗号时回退到第一段；取出的段再交给 FromPlmn 判运营商。
        /// </summary>
        internal static string PlmnAtSlot(string numeric, int slot)
        {
            if (string.IsNullOrEmpty(numeric)) return string.Empty;
            string[] parts = numeric.Split(',');
            if (parts.Length == 1 || slot < 0 || slot >= parts.Length)
                return parts[0].Trim();
            return parts[slot].Trim();
        }

        /// <summary>给服务层做目录名 / 文件名用，保证只含安全字符。</summary>
        internal static string CodeName(TrafficCarrier carrier)
        {
            switch (carrier)
            {
                case TrafficCarrier.Broadnet: return "broadnet";
                case TrafficCarrier.Telecom: return "telecom";
                default: return "none";
            }
        }

        private static bool IsKnown(int code)
        {
            return code == (int)TrafficCarrier.Broadnet || code == (int)TrafficCarrier.Telecom;
        }
    }
}
