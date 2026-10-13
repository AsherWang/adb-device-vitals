using System;
using Microsoft.Win32;

namespace AdbDeviceVitals.Services
{
    public static class AppSettings
    {
        private const string RegistryPath = @"Software\AdbDeviceVitals";
        private const string AdbDirectoryValue = "AdbDirectory";

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
    }
}
