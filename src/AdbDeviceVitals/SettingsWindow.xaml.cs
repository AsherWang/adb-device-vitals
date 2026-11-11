using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdbDeviceVitals.Services;
using WF = System.Windows.Forms;

namespace AdbDeviceVitals
{
    /// <summary>
    /// Win11 设置风格：左侧自定义导航栏分五组，右侧分组卡片，每行「标题 + 说明 + 右侧控件」。
    /// 校验、设备列表与保存逻辑与旧 SettingsForm 完全一致。
    /// </summary>
    public partial class SettingsWindow
    {
        private static readonly Brush Muted = MakeBrush(0x6E, 0x6E, 0x6E);
        private static readonly Brush Warning = MakeBrush(0xB0, 0x48, 0x1C);

        private readonly AdbClient _adb;
        private readonly Func<Task> _checkForUpdates;
        private bool _loadingDevices;
        private bool _suppressChoiceEvents;
        private int _authorizedCount;
        private int _unauthorizedCount;

        public SettingsWindow(AdbClient adb, Func<Task> checkForUpdates)
        {
            InitializeComponent();
            _adb = adb;
            _checkForUpdates = checkForUpdates;

            AdbDirectoryBox.Text = AppSettings.AdbDirectory;
            CurrentAdbText.Text = string.IsNullOrEmpty(adb == null ? string.Empty : adb.AdbPath)
                ? "当前未检测到 ADB"
                : "当前使用：" + adb.AdbPath;
            RunAtStartupSwitch.IsChecked = AppSettings.RunAtStartup;
            AutoEnableUsbTetheringSwitch.IsChecked = AppSettings.AutoEnableUsbTethering;
            AutoRecoverUsbTetheringSwitch.IsChecked = AppSettings.AutoRecoverUsbTethering;
            AutoStartPcHotspotSwitch.IsChecked = AppSettings.AutoStartPcHotspot;
            SmsSyncEnabledSwitch.IsChecked = AppSettings.SmsSyncEnabled;
            SmsToastEnabledSwitch.IsChecked = AppSettings.SmsToastEnabled;
            SmsSenderFilterBox.Text = AppSettings.SmsSenderFilter;

            string version = "当前版本 " + UpdateChecker.CurrentVersionText;
            VersionText.Text = version;
            AboutVersionText.Text = version;
            AboutConfigPathText.Text = "配置文件：" + AppSettings.ConfigFilePath;

            _suppressChoiceEvents = true;
            TargetDeviceBox.Items.Add(new DeviceChoice(string.Empty, "自动选择（只连接一台时生效）", false));
            TargetDeviceBox.SelectedIndex = 0;
            _suppressChoiceEvents = false;
            UpdateDeviceHint();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await ReloadDevicesAsync();
        }

        private void NavItem_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement item = sender as FrameworkElement;
            if (item == null) return;
            int index;
            if (!int.TryParse(Convert.ToString(item.Tag), out index)) return;
            Page0.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            Page1.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            Page2.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
            Page3.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
            Page4.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task ReloadDevicesAsync()
        {
            if (_loadingDevices) return;
            _loadingDevices = true;
            RefreshDevicesButton.IsEnabled = false;
            try
            {
                IList<AdbDevice> devices = new List<AdbDevice>();
                string error = string.Empty;
                try
                {
                    if (_adb == null) error = "未检测到 ADB，无法读取设备列表。";
                    else if (!_adb.IsAvailable) error = "未找到 adb.exe，请先指定 ADB 目录并保存。";
                    else devices = await _adb.ListDevicesAsync();
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                FillDeviceChoices(devices, error);
            }
            finally
            {
                RefreshDevicesButton.IsEnabled = true;
                _loadingDevices = false;
            }
        }

        private void FillDeviceChoices(IList<AdbDevice> devices, string error)
        {
            string preferred = AppSettings.TargetDeviceSerial;
            _authorizedCount = 0;
            _unauthorizedCount = 0;

            _suppressChoiceEvents = true;
            TargetDeviceBox.Items.Clear();
            TargetDeviceBox.Items.Add(new DeviceChoice(string.Empty, "自动选择（只连接一台时生效）", false));
            int selected = 0;

            foreach (AdbDevice device in devices)
            {
                if (!DeviceSelection.IsAuthorized(device))
                {
                    _unauthorizedCount++;
                    continue;
                }
                _authorizedCount++;
                string label = (string.IsNullOrEmpty(device.Model) ? "Android 设备" : device.Model)
                    + "（" + DeviceSelection.ShortSerial(device.Serial) + "）";
                TargetDeviceBox.Items.Add(new DeviceChoice(device.Serial, label, false));
                if (preferred.Length > 0 && string.Equals(device.Serial, preferred, StringComparison.Ordinal))
                    selected = TargetDeviceBox.Items.Count - 1;
            }

            if (preferred.Length > 0 && selected == 0)
            {
                // 记下来的设备不在当前列表里：显式摆到下拉框里并默认选中，
                // 用户一打开设置就能看见，且保存时会被拦下，必须重新选一台。
                TargetDeviceBox.Items.Add(new DeviceChoice(preferred,
                    "⚠ 预设设备 " + DeviceSelection.ShortSerial(preferred) + " 未连接，请重新选择", true));
                selected = TargetDeviceBox.Items.Count - 1;
            }

            TargetDeviceBox.SelectedIndex = selected;
            _suppressChoiceEvents = false;
            DeviceHintText.Tag = error;
            UpdateDeviceHint();
        }

        private void TargetDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_suppressChoiceEvents) UpdateDeviceHint();
        }

        private void UpdateDeviceHint()
        {
            DeviceChoice choice = TargetDeviceBox.SelectedItem as DeviceChoice;
            string error = DeviceHintText.Tag as string;
            DeviceHintText.Foreground = Muted;

            if (choice != null && choice.Missing)
            {
                DeviceHintText.Foreground = Warning;
                DeviceHintText.Text = "记录的设备当前不在线。请从上面的列表重新选择一台已连接的设备，或选择「自动选择」，然后再保存。";
                return;
            }
            if (!string.IsNullOrEmpty(error))
            {
                DeviceHintText.Foreground = Warning;
                DeviceHintText.Text = "读取设备列表失败：" + error;
                return;
            }
            if (_authorizedCount > 1 && (choice == null || choice.Serial.Length == 0))
            {
                DeviceHintText.Foreground = Warning;
                DeviceHintText.Text = "当前有 " + _authorizedCount + " 台已授权设备，请明确指定其中一台，否则不会监控也不会开关共享。";
                return;
            }

            string text = "同时连接多台手机时，程序只对所选设备采集数据并开关 USB 网络共享；留「自动选择」时仅在只接一台设备的情况下生效。";
            if (_unauthorizedCount > 0)
                text += " 另有 " + _unauthorizedCount + " 台设备未授权 USB 调试。";
            DeviceHintText.Text = text;
        }

        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_checkForUpdates == null) return;
            CheckUpdatesButton.IsEnabled = false;
            try
            {
                await _checkForUpdates();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                CheckUpdatesButton.IsEnabled = true;
            }
        }

        private async void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
        {
            await ReloadDevicesAsync();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            using (WF.FolderBrowserDialog dialog = new WF.FolderBrowserDialog())
            {
                dialog.Description = "选择 Android platform-tools 目录";
                dialog.ShowNewFolderButton = false;
                if (Directory.Exists(AdbDirectoryBox.Text)) dialog.SelectedPath = AdbDirectoryBox.Text;
                if (dialog.ShowDialog() == WF.DialogResult.OK) AdbDirectoryBox.Text = dialog.SelectedPath;
            }
        }

        private void ClearAdbButton_Click(object sender, RoutedEventArgs e)
        {
            AdbDirectoryBox.Text = string.Empty;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string directory = (AdbDirectoryBox.Text ?? string.Empty).Trim().Trim('"');
            if (directory.Length > 0 && !File.Exists(Path.Combine(directory, "adb.exe")))
            {
                MessageBox.Show(this, "所选目录中没有找到 adb.exe。", "ADB 目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DeviceChoice choice = TargetDeviceBox.SelectedItem as DeviceChoice;
            if (choice != null && choice.Missing)
            {
                MessageBox.Show(this,
                    "之前记录的设备已不在线。请从下拉框中选择一台当前已连接的设备，或选择「自动选择」后再保存。",
                    "需要重新选择设备", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string serial = choice == null ? string.Empty : choice.Serial;
            if (serial.Length > 0 && !DeviceSelection.IsValidSerial(serial))
            {
                MessageBox.Show(this, "设备序列号不合法，请刷新列表后重新选择。", "设备无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // 一次写入整份配置，避免逐个属性各写一遍文件、留下中间状态。
                AppSettings.Save(new SettingsData
                {
                    AdbDirectory = directory,
                    TargetDeviceSerial = serial,
                    AutoEnableUsbTethering = AutoEnableUsbTetheringSwitch.IsChecked == true,
                    AutoRecoverUsbTethering = AutoRecoverUsbTetheringSwitch.IsChecked == true,
                    AutoStartPcHotspot = AutoStartPcHotspotSwitch.IsChecked == true,
                    SmsSyncEnabled = SmsSyncEnabledSwitch.IsChecked == true,
                    SmsToastEnabled = SmsToastEnabledSwitch.IsChecked == true,
                    SmsSenderFilter = (SmsSenderFilterBox.Text ?? string.Empty).Trim(),
                    RunAtStartup = RunAtStartupSwitch.IsChecked == true,
                    LastUpdateCheckUtc = AppSettings.LastUpdateCheckUtc
                });
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法保存设置：" + ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static Brush MakeBrush(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private sealed class DeviceChoice
        {
            public DeviceChoice(string serial, string text, bool missing)
            {
                Serial = serial ?? string.Empty;
                Text = text ?? string.Empty;
                Missing = missing;
            }

            public string Serial { get; private set; }
            public string Text { get; private set; }
            public bool Missing { get; private set; }

            public override string ToString()
            {
                return Text;
            }
        }
    }
}
