using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AdbDeviceVitals.Services
{
    /// <summary>
    /// 决定「这一轮该操作哪一台设备」。监控采集和 USB 共享开关共用这份判定，
    /// 避免出现界面看着 A 手机、开关却切了 B 手机的 USB 模式。
    /// </summary>
    public sealed class DeviceSelection
    {
        private DeviceSelection(AdbDevice device, string message, bool needsSelection, bool preferredMissing,
            string preferredSerial, int authorizedCount, int unauthorizedCount)
        {
            Device = device;
            Message = message ?? string.Empty;
            NeedsSelection = needsSelection;
            PreferredMissing = preferredMissing;
            PreferredSerial = preferredSerial ?? string.Empty;
            AuthorizedCount = authorizedCount;
            UnauthorizedCount = unauthorizedCount;
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

        public static DeviceSelection Resolve(IList<AdbDevice> devices, string preferredSerial)
        {
            string preferred = (preferredSerial ?? string.Empty).Trim();
            List<AdbDevice> authorized = new List<AdbDevice>();
            int unauthorized = 0;
            if (devices != null)
            {
                foreach (AdbDevice device in devices)
                {
                    if (device == null) continue;
                    if (IsAuthorized(device)) authorized.Add(device);
                    else unauthorized++;
                }
            }

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
