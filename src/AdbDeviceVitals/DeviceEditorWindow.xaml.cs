using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AdbDeviceVitals.Models;
using AdbDeviceVitals.Services;

namespace AdbDeviceVitals
{
    /// <summary>
    /// 受管设备编辑弹窗：添加与编辑共用。添加时只允许选尚未加入的在线设备；
    /// 运营商/卡槽可自动探测作默认值也可手选，优先级为数字（越小越优先）。
    /// </summary>
    public partial class DeviceEditorWindow
    {
        private sealed class DeviceItem
        {
            public DeviceItem(string serial, string label) { Serial = serial; Label = label; }
            public string Serial { get; private set; }
            public string Label { get; private set; }
            public override string ToString() { return Label; }
        }

        private readonly CarrierDetector _detector;
        private readonly ManagedDevice _existing;

        /// <summary>保存后的设备；取消或移除时为 null。</summary>
        public ManagedDevice Result { get; private set; }
        /// <summary>用户在编辑弹窗里点了移除。</summary>
        public bool Removed { get; private set; }

        public DeviceEditorWindow(CarrierDetector detector, IList<AdbDevice> candidates,
            ManagedDevice existing, IList<string> existingSerials, TrafficCarrier detectedCarrier,
            int defaultPriority = 1)
        {
            InitializeComponent();
            _detector = detector;
            _existing = existing;
            bool editing = existing != null;
            MeteredUsbSwitch.IsChecked = existing?.MeteredOnUsb ?? true;

            WindowTitle.Title = editing ? "编辑设备" : "添加设备";
            Title = editing ? "编辑设备" : "添加设备";

            string serial = editing ? existing.Serial : string.Empty;

            if (editing)
            {
                DeviceBox.Visibility = Visibility.Collapsed;
                DeviceStaticText.Visibility = Visibility.Visible;
                string label = serial;
                if (candidates != null)
                {
                    foreach (AdbDevice device in candidates)
                    {
                        if (!string.Equals(device.Serial, serial, StringComparison.OrdinalIgnoreCase)) continue;
                        label = (string.IsNullOrEmpty(device.Model) ? "Android 设备" : device.Model)
                            + "（" + DeviceSelection.ShortSerial(serial) + "）";
                        break;
                    }
                }
                DeviceStaticText.Text = label;
                RemoveButton.Visibility = Visibility.Visible;
                CarrierBox.SelectedIndex = (int)existing.Carrier;
                SetSlot(existing.Slot);
                PriorityBox.Text = existing.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ProbeText.Text = editing ? "可点「重新探测」用当前 SIM 覆盖默认值。" : string.Empty;
            }
            else
            {
                foreach (AdbDevice device in candidates ?? new List<AdbDevice>())
                {
                    if (!DeviceSelection.IsAuthorized(device)) continue;
                    bool already = false;
                    if (existingSerials != null)
                        foreach (string used in existingSerials)
                            if (string.Equals(used, device.Serial, StringComparison.OrdinalIgnoreCase)) { already = true; break; }
                    if (already) continue; // 已加入的设备不重复列出
                    string name = string.IsNullOrEmpty(device.Model) ? "Android 设备" : device.Model;
                    DeviceBox.Items.Add(new DeviceItem(device.Serial,
                        name + "（" + DeviceSelection.ShortSerial(device.Serial) + "）"));
                }
                if (DeviceBox.Items.Count > 0) DeviceBox.SelectedIndex = 0;
                else OkButton.IsEnabled = false;

                // 自动探测的结果作为默认值，用户可改。
                CarrierBox.SelectedIndex = (int)detectedCarrier;
                SetSlot(ManagedDevice.AutoSlot);
                PriorityBox.Text = defaultPriority.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ProbeText.Text = DeviceBox.Items.Count == 0
                    ? "没有可添加的在线设备。已加入的设备不会重复出现。"
                    : "运营商与卡槽已按当前 SIM 自动填入，可手动修改。";
                if (DeviceBox.Items.Count > 0) _ = ProbeAsync();
            }
        }

        private void SetSlot(int slot)
        {
            int index = 0;
            for (int i = 0; i < SlotBox.Items.Count; i++)
            {
                ComboBoxItem item = SlotBox.Items[i] as ComboBoxItem;
                int tag;
                if (item != null && int.TryParse(Convert.ToString(item.Tag), out tag) && tag == slot) { index = i; break; }
                if (item != null && slot == ManagedDevice.AutoSlot && string.Equals(Convert.ToString(item.Tag), "-1")) index = i;
            }
            SlotBox.SelectedIndex = index;
        }

        private async Task ProbeAsync()
        {
            string serial = _existing != null ? _existing.Serial : SelectedSerial();
            if (string.IsNullOrEmpty(serial) || _detector == null) return;
            ProbeText.Text = "正在探测 SIM 与数据卡槽…";
            TrafficCarrier carrier = await _detector.DetectAsync(serial).ConfigureAwait(true);
            int slot = await _detector.DetectDataSlotAsync(serial).ConfigureAwait(true);
            if (carrier != TrafficCarrier.None) CarrierBox.SelectedIndex = (int)carrier;
            if (slot >= 0) SetSlot(slot);
            ProbeText.Text = carrier == TrafficCarrier.None && slot < 0
                ? "未能自动识别，请手动选择运营商与卡槽。"
                : "已探测：" + (carrier == TrafficCarrier.None ? "运营商未知" : CarrierMapping.DisplayName(carrier))
                  + "，" + (slot < 0 ? "卡槽未知" : "卡槽 " + slot.ToString(System.Globalization.CultureInfo.InvariantCulture))
                  + "；可手动修改。";
        }

        private string SelectedSerial()
        {
            DeviceItem item = DeviceBox.SelectedItem as DeviceItem;
            return item == null ? string.Empty : item.Serial;
        }

        private async void ProbeButton_Click(object sender, RoutedEventArgs e)
        {
            ProbeButton.IsEnabled = false;
            try { await ProbeAsync(); }
            finally { ProbeButton.IsEnabled = true; }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            string serial = _existing != null ? _existing.Serial : SelectedSerial();
            if (string.IsNullOrEmpty(serial))
            {
                MessageBox.Show(this, "请选择一台设备。", "设备未选择", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            int priority;
            if (!int.TryParse((PriorityBox.Text ?? string.Empty).Trim(), out priority) || priority < 1)
            {
                MessageBox.Show(this, "优先级需要是大于等于 1 的数字。", "优先级无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            int slot = ManagedDevice.AutoSlot;
            ComboBoxItem slotItem = SlotBox.SelectedItem as ComboBoxItem;
            int parsedSlot;
            if (slotItem != null && int.TryParse(Convert.ToString(slotItem.Tag), out parsedSlot)) slot = parsedSlot;
            TrafficCarrier carrier = (TrafficCarrier)Math.Max(0, Math.Min(2, CarrierBox.SelectedIndex));

            Result = new ManagedDevice(serial, slot, priority, carrier, MeteredUsbSwitch.IsChecked == true);
            DialogResult = true;
            Close();
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            Removed = true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
