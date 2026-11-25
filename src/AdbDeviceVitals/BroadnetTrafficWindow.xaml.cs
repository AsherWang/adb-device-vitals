using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AdbDeviceVitals.Controls;
using AdbDeviceVitals.Services;

namespace AdbDeviceVitals
{
    /// <summary>
    /// 「剩余流量」卡片右上角 ⓘ 打开的详情弹窗：广电 / 电信两个 tab，
    /// 各自显示账户 / 套餐余量 / 用量统计。由 MainWindow 读到数据后调用 Apply 填充。
    /// 「刷新」按当前 tab 对应的运营商走主窗口那条登录 / 查询链路。
    /// </summary>
    public partial class BroadnetTrafficWindow
    {
        private readonly Action<TrafficCarrier> _refresh;

        private readonly CarrierDetector _detector;
        private readonly CancellationTokenSource _closed = new();

        public BroadnetTrafficWindow(Action<TrafficCarrier> refresh, CarrierDetector detector = null)
        {
            InitializeComponent();
            _refresh = refresh;
            _detector = detector;
            BroadnetPanel.AppUsageButton.Click += Usage_Click;
            TelecomPanel.AppUsageButton.Click += Usage_Click;
            Closed += delegate { _closed.Cancel(); };
        }

        private async void Usage_Click(object sender, RoutedEventArgs e)
        {
            var anchor = (Button)sender;
            var carrier = SelectedCarrier;
            SetUsageButtonsEnabled(false);
            try
            {
                var matches = new List<UsbTetherAdapter>();
                foreach (var adapter in await Task.Run(UsbTetherAdapter.Connected))
                {
                    var phone = adapter.Device;
                    var assigned = phone.Carrier;
                    if (assigned == TrafficCarrier.None && _detector != null)
                        assigned = await _detector.DetectAsync(phone.Serial, phone.Slot);
                    if (assigned == carrier) matches.Add(adapter);
                }
                if (_closed.IsCancellationRequested || carrier != SelectedCarrier) return;
                if (matches.Count == 0)
                {
                    MessageBox.Show(this, "未找到" + CarrierMapping.DisplayName(carrier)
                        + "的共享网卡。请确认手机已开启 USB 网络共享，并在设备设置中核对运营商。", "查看应用流量");
                    return;
                }
                if (matches.Count == 1) await OpenUsageAsync(matches[0]);
                else
                {
                    var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                    foreach (var adapter in matches)
                    {
                        var item = new MenuItem { Header = adapter.Device.Serial + " · " + adapter.Name };
                        item.Click += async delegate
                        {
                            SetUsageButtonsEnabled(false);
                            try { await OpenUsageAsync(adapter); }
                            finally { SetUsageButtonsEnabled(true); }
                        };
                        menu.Items.Add(item);
                    }
                    menu.IsOpen = true;
                }
            }
            catch (Exception ex) { ShowUsageError(ex.Message); }
            finally { SetUsageButtonsEnabled(true); }
        }

        private async Task OpenUsageAsync(UsbTetherAdapter adapter)
        {
            try
            {
                if (!await WindowsDataUsagePage.OpenAsync(adapter, _closed.Token))
                    ShowUsageError("未能确认 Windows 已选中「" + adapter.Name + "」。请在网络设置中选择该网卡的「数据使用量」。");
            }
            catch (Exception ex) { ShowUsageError(ex.Message); }
        }

        private void SetUsageButtonsEnabled(bool enabled)
        {
            BroadnetPanel.AppUsageButton.IsEnabled = TelecomPanel.AppUsageButton.IsEnabled = enabled;
        }

        private void ShowUsageError(string detail)
        {
            if (!_closed.IsCancellationRequested)
                MessageBox.Show(this, detail, "查看应用流量", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>运营商显示名，用于两个 tab 的标题与各自面板的引导文案。</summary>
        internal void SetCarriers(string broadnet, string telecom)
        {
            BroadnetTab.Header = string.IsNullOrEmpty(broadnet) ? "中国广电" : broadnet;
            TelecomTab.Header = string.IsNullOrEmpty(telecom) ? "中国电信" : telecom;
            BroadnetPanel.SetCarrier(BroadnetTab.Header.ToString());
            TelecomPanel.SetCarrier(TelecomTab.Header.ToString());
        }

        /// <summary>打开时默认选中当前设备对应的运营商 tab。</summary>
        internal void SelectCarrier(TrafficCarrier carrier)
        {
            CarrierTabs.SelectedIndex = carrier == TrafficCarrier.Telecom ? 1 : 0;
        }

        internal TrafficCarrier SelectedCarrier
        {
            get { return CarrierTabs.SelectedIndex == 1 ? TrafficCarrier.Telecom : TrafficCarrier.Broadnet; }
        }

        /// <summary>填充某个运营商 tab 的数据。null traffic 表示清空该 tab。</summary>
        internal void Apply(TrafficCarrier carrier, BroadnetTraffic traffic, UsageStats stats)
        {
            PanelFor(carrier).Apply(traffic, stats);
        }

        private TrafficCarrierPanel PanelFor(TrafficCarrier carrier)
        {
            return carrier == TrafficCarrier.Telecom ? TelecomPanel : BroadnetPanel;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_refresh != null) _refresh(SelectedCarrier);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
