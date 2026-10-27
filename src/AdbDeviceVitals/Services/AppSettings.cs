using System;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace AdbDeviceVitals.Services
{
    public static class AppSettings
    {
        public const string StartupArgument = "--startup";

        private const string RegistryPath = @"Software\AdbDeviceVitals";
        private const string AdbDirectoryValue = "AdbDirectory";
        private const string RunAtStartupValue = "RunAtStartup";
        private const string AutoEnableUsbTetheringValue = "AutoEnableUsbTethering";
        private const string AutoStartPcHotspotValue = "AutoStartPcHotspot";
        private const string LastUpdateCheckValue = "LastUpdateCheckUtc";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "AdbDeviceVitals";

        public static string AdbDirectory
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
                    {
                        return key == null ? string.Empty : Convert.ToString(key.GetValue(AdbDirectoryValue, string.Empty));
                    }
                }
                catch
                {
                    return string.Empty;
                }
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                {
                    if (string.IsNullOrWhiteSpace(value))
                        key.DeleteValue(AdbDirectoryValue, false);
                    else
                        key.SetValue(AdbDirectoryValue, value.Trim(), RegistryValueKind.String);
                }
            }
        }

        public static bool RunAtStartup
        {
            get { return ReadBoolean(RunAtStartupValue); }
            set
            {
                WriteBoolean(RunAtStartupValue, value);
                ApplyStartupRegistration(value);
            }
        }

        public static bool AutoEnableUsbTethering
        {
            get { return ReadBoolean(AutoEnableUsbTetheringValue); }
            set { WriteBoolean(AutoEnableUsbTetheringValue, value); }
        }

        /// <summary>启动后自动开启电脑侧 Wi-Fi 热点，把手机网络转发给其他设备。</summary>
        public static bool AutoStartPcHotspot
        {
            get { return ReadBoolean(AutoStartPcHotspotValue); }
            set { WriteBoolean(AutoStartPcHotspotValue, value); }
        }

        public static DateTime? LastUpdateCheckUtc
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
                    {
                        if (key == null) return null;
                        string text = Convert.ToString(key.GetValue(LastUpdateCheckValue, string.Empty));
                        DateTime value;
                        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value)) return null;
                        return value.ToUniversalTime();
                    }
                }
                catch
                {
                    return null;
                }
            }
            set
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                    {
                        if (!value.HasValue)
                            key.DeleteValue(LastUpdateCheckValue, false);
                        else
                            key.SetValue(LastUpdateCheckValue, value.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), RegistryValueKind.String);
                    }
                }
                catch
                {
                }
            }
        }

        public static void ApplyStartupRegistration(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (!enabled)
                {
                    key.DeleteValue(RunValueName, false);
                    return;
                }

                string executable = Process.GetCurrentProcess().MainModule.FileName;
                key.SetValue(RunValueName, "\"" + executable + "\" " + StartupArgument, RegistryValueKind.String);
            }
        }

        private static bool ReadBoolean(string name)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
                {
                    if (key == null) return false;
                    object value = key.GetValue(name, 0);
                    if (value is int) return (int)value != 0;
                    int number;
                    return int.TryParse(Convert.ToString(value), out number) && number != 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void WriteBoolean(string name, bool value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
            }
        }
    }
}
