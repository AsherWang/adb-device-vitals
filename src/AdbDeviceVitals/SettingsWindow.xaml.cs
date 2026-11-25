using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdbDeviceVitals.Models;
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
        private static readonly Brush Warning = MakeBrush(0xB0, 0x48, 0x1C);

        private readonly AdbClient _adb;
        private readonly CarrierDetector _carrierDetector;
        private readonly Func<Task> _checkForUpdates;
        private readonly Dictionary<string, TrafficCarrier> _detectedCarriers =
            new Dictionary<string, TrafficCarrier>(StringComparer.OrdinalIgnoreCase);
        // 主动添加式：受管设备是唯一真值，主列表只展示这些设备。
        private readonly List<ManagedDevice> _managed = new List<ManagedDevice>();
        private IList<AdbDevice> _onlineDevices = new List<AdbDevice>();
        private bool _loadingDevices;
        private bool _adbDirectoryEdited;

        /// <summary>主列表的一行：标题、详情、状态。</summary>
        private sealed class ManagedDeviceRow
        {
            public ManagedDeviceRow(ManagedDevice device, string title, string status)
            {
                Device = device; Title = title; Status = status;
            }
            public ManagedDevice Device { get; private set; }
            public string Title { get; private set; }
            public string Status { get; private set; }
        }

        public SettingsWindow(AdbClient adb, Func<Task> checkForUpdates)
        {
            InitializeComponent();
            _adb = adb;
            _carrierDetector = new CarrierDetector(adb);
            _checkForUpdates = checkForUpdates;

            AdbDirectoryBox.Text = AppSettings.AdbDirectory;
            if (string.IsNullOrEmpty(AdbDirectoryBox.Text) && !string.IsNullOrEmpty(adb?.AdbPath))
                AdbDirectoryBox.Text = Path.GetDirectoryName(adb.AdbPath);
            AdbDirectoryBox.TextChanged += delegate { _adbDirectoryEdited = true; };
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

            foreach (ManagedDevice device in AppSettings.ManagedDeviceList) _managed.Add(device);
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

                // 顺带从 SIM 的 PLMN 自动识别每台设备的运营商（读不到不影响手选）。
                _detectedCarriers.Clear();
                List<Task> probes = new List<Task>();
                foreach (AdbDevice device in devices)
                {
                    if (!DeviceSelection.IsAuthorized(device)) continue;
                    probes.Add(ProbeCarrierAsync(device.Serial));
                }
                await Task.WhenAll(probes).ConfigureAwait(true);

                FillDeviceChoices(devices, error);
            }
            finally
            {
                RefreshDevicesButton.IsEnabled = true;
                _loadingDevices = false;
            }
        }

        private async Task ProbeCarrierAsync(string serial)
        {
            TrafficCarrier detected = await _carrierDetector.DetectAsync(serial).ConfigureAwait(true);
            lock (_detectedCarriers)
            {
                _detectedCarriers[serial] = detected;
            }
        }

        private void FillDeviceChoices(IList<AdbDevice> devices, string error)
        {
            _onlineDevices = devices ?? new List<AdbDevice>();
            int unauthorized = 0;
            foreach (var device in _onlineDevices)
                if (!DeviceSelection.IsAuthorized(device)) unauthorized++;
            DeviceStatusText.Text = !string.IsNullOrEmpty(error) ? "读取设备列表失败：" + error
                : unauthorized > 0 ? unauthorized + " 台设备未授权" : string.Empty;
            DeviceStatusText.Foreground = Warning;
            RefreshManagedList();
        }

        /// <summary>主列表只渲染 _managed：每台一行，显示型号/序列、配置与状态。</summary>
        private void RefreshManagedList()
        {
            string active = DeviceSelection.EffectiveSerial;
            object selected = ManagedDeviceListBox.SelectedItem;
            ManagedDeviceListBox.Items.Clear();

            // 按优先级数字升序展示（_managed 已排序）。
            foreach (ManagedDevice device in _managed)
            {
                string model = null;
                foreach (AdbDevice online in _onlineDevices)
                {
                    if (string.Equals(online.Serial, device.Serial, StringComparison.OrdinalIgnoreCase))
                    { model = online.Model; break; }
                }
                string name = string.IsNullOrEmpty(model) ? "Android 设备" : model;
                string title = "P" + device.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " · " + name + "（" + DeviceSelection.ShortSerial(device.Serial) + "）";
                bool isOnline = false;
                foreach (AdbDevice item in _onlineDevices)
                    if (DeviceSelection.IsAuthorized(item)
                        && string.Equals(item.Serial, device.Serial, StringComparison.OrdinalIgnoreCase)) { isOnline = true; break; }
                bool isActive = isOnline && string.Equals(active, device.Serial, StringComparison.OrdinalIgnoreCase);
                if (isActive) title = "✓ " + title;
                string status = isOnline ? "在线" : "未连接";
                if (device.MeteredOnUsb && (Application.Current as App)?.UsbMetering is UsbMeteredConnections metering)
                    status += " · " + metering.Status(device.Serial);
                ManagedDeviceRow row = new ManagedDeviceRow(device, title, status);
                ManagedDeviceListBox.Items.Add(row);
                if (selected is ManagedDeviceRow
                    && string.Equals(((ManagedDeviceRow)selected).Device.Serial, device.Serial, StringComparison.OrdinalIgnoreCase))
                    ManagedDeviceListBox.SelectedItem = row;
            }

            EmptyDeviceText.Text = _managed.Count == 0
                ? "还没有添加设备。点右上角「＋ 添加设备」从当前连接的手机里挑选。"
                : string.Empty;
            AddDeviceButton.IsEnabled = true;
        }

        private void AddDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            List<string> existing = new List<string>();
            foreach (ManagedDevice device in _managed) existing.Add(device.Serial);
            // 新增优先级默认取现有最大值 + 1，避免和已添加设备撞优先级。
            int nextPriority = 1;
            foreach (ManagedDevice device in _managed) nextPriority = Math.Max(nextPriority, device.Priority + 1);

            DeviceEditorWindow editor = new DeviceEditorWindow(_carrierDetector, _onlineDevices,
                null, existing, TrafficCarrier.None, nextPriority);
            editor.Owner = this;
            if (editor.ShowDialog() != true || editor.Result == null) return;

            ManagedDevice added = editor.Result;
            _managed.Add(added);
            _managed.Sort(delegate(ManagedDevice a, ManagedDevice b) { return a.Priority.CompareTo(b.Priority); });
            RefreshManagedList();
        }

        private void EditManagedDevice_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            ManagedDeviceRow row = button == null ? null : button.DataContext as ManagedDeviceRow;
            if (row == null) return;

            List<string> existing = new List<string>();
            foreach (ManagedDevice device in _managed) existing.Add(device.Serial);
            TrafficCarrier detected;
            lock (_detectedCarriers)
                _detectedCarriers.TryGetValue(row.Device.Serial, out detected);

            DeviceEditorWindow editor = new DeviceEditorWindow(_carrierDetector, _onlineDevices,
                row.Device, existing, detected);
            editor.Owner = this;
            if (editor.ShowDialog() != true) return;

            if (editor.Removed)
            {
                _managed.Remove(row.Device);
            }
            else if (editor.Result != null)
            {
                int index = _managed.IndexOf(row.Device);
                if (index >= 0) _managed[index] = editor.Result;
                _managed.Sort(delegate(ManagedDevice a, ManagedDevice b) { return a.Priority.CompareTo(b.Priority); });
            }
            RefreshManagedList();
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
            if (!_adbDirectoryEdited) directory = AppSettings.AdbDirectory;
            if (directory.Length > 0 && !File.Exists(Path.Combine(directory, "adb.exe")))
            {
                MessageBox.Show(this, "所选目录中没有找到 adb.exe。", "ADB 目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 受管设备是唯一真值；旧的预设设备/优先级/运营商映射由 SettingsFile.Normalize 派生。
            foreach (ManagedDevice device in _managed)
            {
                if (!DeviceSelection.IsValidSerial(device.Serial))
                {
                    MessageBox.Show(this, "设备序列号不合法，请刷新列表后重新选择。", "设备无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            try
            {
                // 一次写入整份配置，避免逐个属性各写一遍文件、留下中间状态。
                AppSettings.Save(new SettingsData
                {
                    AdbDirectory = directory,
                    ManagedDevices = ManagedDeviceStore.Serialize(_managed),
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
