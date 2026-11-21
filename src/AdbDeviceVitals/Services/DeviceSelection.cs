using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AdbDeviceVitals.Services
{
    /// <summary>
    /// 决定「这一轮该操作哪一台设备」。监控采集和 USB 共享服务共用这份判定，
    /// 避免出现界面看着 A 手机、开关却切了 B 手机的 USB 模式。
    /// </summary>
    public sealed class DeviceSelection
    {
        private static readonly object ActiveGate = new object();
        private static string _activeSerial = string.Empty;

        private DeviceSelection(AdbDevice device, string message, bool needsSelection, bool preferredMissing,
            string preferredSerial, int authorizedCount, int unauthorizedCount,
            string previousSerial = "", bool justSwitched = false, bool switchBack = false)
        {
            Device = device;
            Message = message ?? string.Empty;
            NeedsSelection = needsSelection;
            PreferredMissing = preferredMissing;
            PreferredSerial = preferredSerial ?? string.Empty;
            AuthorizedCount = authorizedCount;
            UnauthorizedCount = unauthorizedCount;
            PreviousSerial = previousSerial ?? string.Empty;
            JustSwitched = justSwitched;
            SwitchBack = switchBack;
        }

        /// <summary>选中的设备；需要用户先做选择或没有可用设备时为 null。</summary>
        public AdbDevice Device { get; private set; }

        /// <summary>面向使用者的中文说明，可直接显示在连接状态或托盘提示里。</summary>
        public string Message { get; private set; }

        /// <summary>需要用户到设置里指定设备（多设备、或预设设备已不在列表中）。</summary>
        public bool NeedsSelection { get; private set; }

        /// <summary>设置里记录的设备序列号当前不在已授权设备列表中。</summary>
        public bool PreferredMissing { get; private set; }

        public string PreferredSerial { get; private set; }
        public int AuthorizedCount { get; private set; }
        public int UnauthorizedCount { get; private set; }

        /// <summary>本次从哪台设备切过来（短序列号由界面自行格式化）。</summary>
        public string PreviousSerial { get; private set; }

        /// <summary>本轮刚因预设设备掉线而切到备用机；只在切换当轮为真，避免重复提示。</summary>
        public bool JustSwitched { get; private set; }

        /// <summary>本轮预设设备重新上线，已从备用机切回。</summary>
        public bool SwitchBack { get; private set; }

        /// <summary>
        /// 运行时实际生效的设备序列号。故障转移期间它与配置里的预设设备不同；
        /// 预设设备回来后自动恢复。设置页保存会显式重置，让新配置立刻生效。
        /// </summary>
        public static string ActiveSerial
        {
            get { lock (ActiveGate) { return _activeSerial; } }
        }

        public static string EffectiveSerial
        {
            get
            {
                string active = ActiveSerial;
                return active.Length > 0 ? active : AppSettings.TargetDeviceSerial;
            }
        }

        public static void SetActiveSerial(string serial)
        {
            string value = IsValidSerial(serial) ? serial.Trim() : string.Empty;
            lock (ActiveGate) { _activeSerial = value; }
        }

        public static void ResetActiveSerial()
        {
            lock (ActiveGate) { _activeSerial = string.Empty; }
        }

        public static DeviceSelection Resolve(IList<AdbDevice> devices, string preferredSerial)
        {
            string preferred = (preferredSerial ?? string.Empty).Trim();
            List<AdbDevice> authorized = Authorized(devices);
            int unauthorized = CountUnauthorized(devices);

            if (authorized.Count == 0)
            {
                string message = unauthorized > 0
                    ? "请在手机上允许这台电脑进行 USB 调试。"
                    : "未找到已授权的 Android 设备。";
                return new DeviceSelection(null, message, false, false, preferred, 0, unauthorized);
            }

            if (preferred.Length > 0)
            {
                foreach (AdbDevice device in authorized)
                {
                    if (!string.Equals(device.Serial, preferred, StringComparison.Ordinal)) continue;
                    return new DeviceSelection(device, string.Empty, false, false, preferred,
                        authorized.Count, unauthorized);
                }

                // 绝不退回到「列表里的第一台」：那可能是一台只用来调试的手机。
                // 尤其是预设设备换掉之后，静默改目标会切错设备的 USB 功能组合。
                string missing = "预设的设备（" + ShortSerial(preferred) + "）未连接，请在设置中从当前设备列表重新选择。";
                return new DeviceSelection(null, missing, true, true, preferred,
                    authorized.Count, unauthorized);
            }

            if (authorized.Count == 1)
                return new DeviceSelection(authorized[0], string.Empty, false, false, preferred, 1, unauthorized);

            string ambiguous = "检测到 " + authorized.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " 台 Android 设备，请在设置中指定用于网络共享的设备。";
            return new DeviceSelection(null, ambiguous, true, false, preferred,
                authorized.Count, unauthorized);
        }

        /// <summary>
        /// 带故障转移的解析：预设设备掉线时，若只剩一台已授权手机（或此前已经切到
        /// 某台备用机且它仍在线），就临时由它接管；预设设备回来后立刻切回。
        /// 多台备用机时仍然要求用户去设置里选，绝不随机挑一台。
        /// </summary>
        public static DeviceSelection ResolveWithFailover(IList<AdbDevice> devices, string preferredSerial,
            string activeSerial)
        {
            string preferred = (preferredSerial ?? string.Empty).Trim();
            string active = IsValidSerial(activeSerial) ? activeSerial.Trim() : string.Empty;
            DeviceSelection strict = Resolve(devices, preferred);

            if (preferred.Length == 0 || strict.Device != null)
            {
                if (strict.Device == null || active.Length == 0 || Same(active, preferred)) return strict;
                return new DeviceSelection(strict.Device, string.Empty, false, false, preferred,
                    strict.AuthorizedCount, strict.UnauthorizedCount, active, false, true);
            }

            // 预设设备不在线：先看之前接管的那台是否还在，避免插上第三台时被挤掉。
            if (active.Length > 0)
            {
                AdbDevice current = Find(devices, active);
                if (current != null)
                {
                    return new DeviceSelection(current, string.Empty, false, true, preferred,
                        strict.AuthorizedCount, strict.UnauthorizedCount);
                }
            }

            // 首次掉线且只有一台候选：切过去，并记下来源用于提示。
            if (strict.AuthorizedCount == 1)
            {
                AdbDevice only = Find(devices, null);
                return new DeviceSelection(only, string.Empty, false, true, preferred, 1,
                    strict.UnauthorizedCount, active.Length > 0 ? active : preferred, true, false);
            }

            return strict;
        }

        private static AdbDevice Find(IList<AdbDevice> devices, string serial)
        {
            if (devices == null) return null;
            foreach (AdbDevice device in devices)
            {
                if (!IsAuthorized(device)) continue;
                if (serial == null || Same(device.Serial, serial)) return device;
            }
            return null;
        }

        private static List<AdbDevice> Authorized(IList<AdbDevice> devices)
        {
            List<AdbDevice> result = new List<AdbDevice>();
            if (devices == null) return result;
            foreach (AdbDevice device in devices)
            {
                if (IsAuthorized(device)) result.Add(device);
            }
            return result;
        }

        private static int CountUnauthorized(IList<AdbDevice> devices)
        {
            int count = 0;
            if (devices == null) return count;
            foreach (AdbDevice device in devices)
            {
                if (device != null && !IsAuthorized(device)) count++;
            }
            return count;
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAuthorized(AdbDevice device)
        {
            return device != null && string.Equals(device.State, "device", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>界面上显示的短序列号，长序列号中间省略。</summary>
        public static string ShortSerial(string serial)
        {
            string value = serial ?? string.Empty;
            if (value.Length <= 16) return value;
            return value.Substring(0, 8) + "…" + value.Substring(value.Length - 4);
        }

        /// <summary>与 AdbClient.RunShellAsync 的校验保持一致，防止手写注册表值时拼出非法命令。</summary>
        public static bool IsValidSerial(string serial)
        {
            return !string.IsNullOrWhiteSpace(serial)
                && Regex.IsMatch(serial.Trim(), @"^[A-Za-z0-9._:-]+$");
        }
    }
}
