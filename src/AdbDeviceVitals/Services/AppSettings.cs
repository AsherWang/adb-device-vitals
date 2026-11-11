using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace AdbDeviceVitals.Services
{
    /// <summary>
    /// 用户配置的唯一入口。配置保存在用户主目录下的 adb-device-vitals\settings.json，
    /// 不再放在注册表。轮询每 2.5 秒会读一次目标设备，因此这里必须做内存缓存，
    /// 只有启动、打开设置和保存时才碰磁盘。
    ///
    /// 注册表只剩两处用途：开机自启的 Run 项（Windows 的启动登记机制，不是配置），
    /// 以及旧键里的 MigratedTo 标记，用来保证迁移只做一次。
    /// </summary>
    public static class AppSettings
    {
        public const string StartupArgument = "--startup";

        private const string LegacyRegistryPath = @"Software\AdbDeviceVitals";
        private const string MigratedToValue = "MigratedTo";
        private const string ConfigFileName = "settings.json";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "AdbDeviceVitals";

        private static readonly object Gate = new object();
        private static SettingsData _current = new SettingsData();
        private static string _configDirectory = DefaultConfigDirectory();
        private static bool _initialized;
        private static string _lastError = string.Empty;

        /// <summary>配置目录。测试可以在启动前把它指到临时目录，避免碰到真实用户目录。</summary>
        public static string ConfigDirectory
        {
            get { lock (Gate) { return _configDirectory; } }
            set
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                lock (Gate) { _configDirectory = value; }
            }
        }

        public static string ConfigFilePath
        {
            get { return Path.Combine(ConfigDirectory, ConfigFileName); }
        }

        /// <summary>最近一次读写失败的原因；为空表示正常。启动失败时由托盘提示。</summary>
        public static string LastError
        {
            get { lock (Gate) { return _lastError; } }
        }

        /// <summary>启动时调用一次：加载文件，文件不存在则从旧注册表迁移并生成。</summary>
        public static void Initialize()
        {
            lock (Gate)
            {
                if (_initialized) return;
                _initialized = true;
                try
                {
                    Directory.CreateDirectory(_configDirectory);
                    string path = Path.Combine(_configDirectory, ConfigFileName);
                    if (File.Exists(path))
                    {
                        _current = SettingsFile.Parse(File.ReadAllText(path, Encoding.UTF8));
                    }
                    else
                    {
                        SettingsData migrated = ReadLegacySettings();
                        WriteFile(migrated);
                        MarkMigrated(path);
                        _current = migrated;
                    }
                    _lastError = string.Empty;
                }
                catch (Exception ex)
                {
                    // 目录不可写或文件损坏时不能阻断启动，退回内存默认值。
                    _lastError = ex.Message;
                    _current = new SettingsData();
                }

                // 顺带修好两件老问题：Run 项被清理、程序换过路径后登记还是旧地址。
                if (_current.RunAtStartup)
                {
                    try { ApplyStartupRegistration(true); }
                    catch (Exception ex) { _lastError = ex.Message; }
                }
            }
        }

        /// <summary>重新从磁盘读取，让程序运行期间手改配置文件也能生效。文件被删掉时不重建。</summary>
        public static void Reload()
        {
            lock (Gate)
            {
                try
                {
                    string path = Path.Combine(_configDirectory, ConfigFileName);
                    if (!File.Exists(path)) return;
                    _current = SettingsFile.Parse(File.ReadAllText(path, Encoding.UTF8));
                    _lastError = string.Empty;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                }
            }
        }

        /// <summary>设置页一次性写入整份配置，避免逐个属性写五遍文件。</summary>
        public static void Save(SettingsData data)
        {
            if (data == null) throw new ArgumentNullException("data");
            lock (Gate)
            {
                Commit(data.Copy(), true);
            }
        }

        public static string AdbDirectory
        {
            get { lock (Gate) { return _current.AdbDirectory; } }
            set { Update(delegate(SettingsData data) { data.AdbDirectory = (value ?? string.Empty).Trim(); }, false, true); }
        }

        public static bool RunAtStartup
        {
            get { lock (Gate) { return _current.RunAtStartup; } }
            set { Update(delegate(SettingsData data) { data.RunAtStartup = value; }, true, true); }
        }

        public static bool AutoEnableUsbTethering
        {
            get { lock (Gate) { return _current.AutoEnableUsbTethering; } }
            set { Update(delegate(SettingsData data) { data.AutoEnableUsbTethering = value; }, false, true); }
        }

        /// <summary>启动后自动开启电脑侧 Wi-Fi 热点，把手机网络转发给其他设备。</summary>
        public static bool AutoStartPcHotspot
        {
            get { lock (Gate) { return _current.AutoStartPcHotspot; } }
            set { Update(delegate(SettingsData data) { data.AutoStartPcHotspot = value; }, false, true); }
        }

        /// <summary>
        /// 锁定的设备序列号。留空表示自动模式：只接一台时直接用它，多台时要求用户先选。
        /// 监控采集与 USB 网络共享都以这个值为准，避免多设备时切错手机。
        /// </summary>
        public static string TargetDeviceSerial
        {
            get
            {
                lock (Gate)
                {
                    string value = _current.TargetDeviceSerial ?? string.Empty;
                    return DeviceSelection.IsValidSerial(value) ? value : string.Empty;
                }
            }
            set { Update(delegate(SettingsData data) { data.TargetDeviceSerial = (value ?? string.Empty).Trim(); }, false, true); }
        }

        public static DateTime? LastUpdateCheckUtc
        {
            get { lock (Gate) { return _current.LastUpdateCheckUtc; } }
            // 更新检查的时间戳属于运行时状态，写失败不该打扰用户，沿用原来的静默行为。
            set { Update(delegate(SettingsData data) { data.LastUpdateCheckUtc = value; }, false, false); }
        }

        /// <summary>开机自启登记。这是 Windows 的启动机制，配置真值仍在 settings.json 里。</summary>
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

        private static void Update(Action<SettingsData> change, bool applyStartup, bool throwOnFailure)
        {
            lock (Gate)
            {
                SettingsData next = _current.Copy();
                change(next);
                try
                {
                    Commit(next, applyStartup);
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    if (throwOnFailure) throw;
                }
            }
        }

        /// <summary>调用方已持有 Gate：先写盘成功、再更新缓存，设置页保存后立刻能读到新值。</summary>
        private static void Commit(SettingsData next, bool applyStartup)
        {
            WriteFile(next);
            if (applyStartup) ApplyStartupRegistration(next.RunAtStartup);
            _current = next;
            _lastError = string.Empty;
        }

        private static void WriteFile(SettingsData data)
        {
            Directory.CreateDirectory(_configDirectory);
            string path = Path.Combine(_configDirectory, ConfigFileName);
            string temp = path + ".tmp";
            File.WriteAllText(temp, SettingsFile.Serialize(data), new UTF8Encoding(false));
            try
            {
                // 同目录临时文件 + 替换，避免写到一半留下缺尾的 JSON。
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }

        /// <summary>
        /// 读旧版注册表值。旧键里已有 MigratedTo 标记时返回默认配置：
        /// 用户删掉 settings.json 想重置，不该被旧值重新灌回来。旧值本身保留着，
        /// 回滚到旧版本时仍然读得到。
        /// </summary>
        private static SettingsData ReadLegacySettings()
        {
            SettingsData data = new SettingsData();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(LegacyRegistryPath, false))
                {
                    if (key == null) return data;
                    if (key.GetValue(MigratedToValue, null) != null) return data;

                    data.AdbDirectory = Convert.ToString(key.GetValue("AdbDirectory", string.Empty)).Trim();
                    string serial = Convert.ToString(key.GetValue("TargetDeviceSerial", string.Empty)).Trim();
                    data.TargetDeviceSerial = DeviceSelection.IsValidSerial(serial) ? serial : string.Empty;
                    data.RunAtStartup = ReadLegacyBoolean(key, "RunAtStartup");
                    data.AutoEnableUsbTethering = ReadLegacyBoolean(key, "AutoEnableUsbTethering");
                    data.AutoStartPcHotspot = ReadLegacyBoolean(key, "AutoStartPcHotspot");
                    data.LastUpdateCheckUtc = ReadLegacyDate(key, "LastUpdateCheckUtc");
                }
            }
            catch
            {
            }
            return data;
        }

        private static bool ReadLegacyBoolean(RegistryKey key, string name)
        {
            object value = key.GetValue(name, 0);
            if (value is int) return (int)value != 0;
            int number;
            return int.TryParse(Convert.ToString(value), out number) && number != 0;
        }

        private static DateTime? ReadLegacyDate(RegistryKey key, string name)
        {
            string text = Convert.ToString(key.GetValue(name, string.Empty));
            DateTime value;
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value)) return null;
            return value.ToUniversalTime();
        }

        private static void MarkMigrated(string path)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(LegacyRegistryPath, true))
                {
                    if (key != null) key.SetValue(MigratedToValue, path, RegistryValueKind.String);
                }
            }
            catch
            {
            }
        }

        private static string DefaultConfigDirectory()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) home = Environment.ExpandEnvironmentVariables("%USERPROFILE%");
            return Path.Combine(home ?? string.Empty, "adb-device-vitals");
        }
    }
}
