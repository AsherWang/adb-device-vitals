using System;
using System.Diagnostics;
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
