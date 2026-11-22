using System;
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

        public BroadnetTrafficWindow(Action<TrafficCarrier> refresh)
        {
            InitializeComponent();
            _refresh = refresh;
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
