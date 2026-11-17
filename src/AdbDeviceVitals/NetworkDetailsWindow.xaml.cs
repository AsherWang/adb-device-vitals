using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AdbDeviceVitals.Models;

namespace AdbDeviceVitals
{
    /// <summary>
    /// 「蜂窝网络」与「网速」两张卡片共用的详情弹窗：主卡片只保留结论，原始参数、
    /// 出口 IP 和手机侧共享明细都在这里按分组展开；打开期间由 MainWindow 每次采集后
    /// 调用 Apply 实时刷新。从「网速」的 ⓘ 进来时滚到「设备与网络共享」分组。
    /// </summary>
    public partial class NetworkDetailsWindow
    {
        private string _ipv4 = "";
        private string _ipv6 = "";

        public NetworkDetailsWindow()
        {
            InitializeComponent();
        }

        public void Apply(DeviceSnapshot snapshot, string ipv4, string ipv6, DateTime ipCheckedAt)
        {
            if (snapshot == null) return;
            _ipv4 = ipv4 ?? "";
            _ipv6 = ipv6 ?? "";

            NetworkTypeText.Text = Text(snapshot.NetworkType);
            OperatorText.Text = Text(snapshot.OperatorName);
            RsrpText.Text = Unit(snapshot.RsrpDbm, " dBm");
            RsrqText.Text = Unit(snapshot.RsrqDb, " dB");
            SinrText.Text = Unit(snapshot.SinrDb, " dB");
            SignalLevelText.Text = snapshot.SignalLevel.HasValue
                ? snapshot.SignalLevel.Value + " / 4"
                : "—";

            ModelText.Text = Text(snapshot.DeviceModel);
            SerialText.Text = Text(snapshot.DeviceSerial);
            UpstreamText.Text = Text(snapshot.UpstreamInterface);
            TetherModeText.Text = TetherMode(snapshot);
            TetherDetailText.Text = TetherDetail(snapshot);

            Ipv4Text.Text = _ipv4.Length > 0 ? _ipv4 : "—";
            Ipv6Text.Text = _ipv6.Length > 0 ? _ipv6 : "—";
            IpTimeText.Text = ipCheckedAt == DateTime.MinValue
                ? "尚未查询"
                : ipCheckedAt.ToString("HH:mm:ss");
            IpTimeText.ToolTip = "启动 / 网络地址变化 / 每 10 分钟各刷新一次";
            SampleTimeText.Text = snapshot.Timestamp.ToString("HH:mm:ss");
            SampleTimeText.ToolTip = "最近一次采集时间 · " + (snapshot.Connected ? "设备在线" : "设备离线");
        }

        /// <summary>从「网速」卡片的 ⓘ 进入时，把网络共享分组滚到可视区域。</summary>
        public void FocusTethering()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                TetheringCard.BringIntoView();
            }));
        }

        private static string TetherMode(DeviceSnapshot value)
        {
            if (value.UsbTetheringActive && value.HotspotActive) return "USB + Wi-Fi";
            if (value.UsbTetheringActive) return "USB 网络共享";
            if (value.HotspotActive) return "Wi-Fi 热点";
            return "未开启";
        }

        private static string TetherDetail(DeviceSnapshot value)
        {
            if (!value.UsbTetheringActive && !value.HotspotActive) return "未检测到网络共享";

            List<string> details = new List<string>();
            if (!string.IsNullOrWhiteSpace(value.UpstreamInterface)) details.Add("上游 " + value.UpstreamInterface);
            if (value.UsbTetheringActive && value.HotspotActive)
            {
                details.Add("USB " + value.UsbTetheringClients + " 台");
                details.Add("Wi-Fi " + value.ConnectedClients + " 台");
            }
            else if (value.UsbTetheringActive)
            {
                details.Add(value.UsbTetheringClients + " 台设备");
            }
            else
            {
                details.Add(SafeText(value.HotspotBand, "Wi-Fi"));
                details.Add(value.ConnectedClients + " 台设备");
            }
            return string.Join("  ·  ", details.ToArray());
        }

        private static string SafeText(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string Text(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "—" : value;
        }

        private static string Unit(int? value, string unit)
        {
            return value.HasValue ? value.Value + unit : "—";
        }

        private void CopyIp_Click(object sender, RoutedEventArgs e)
        {
            StringBuilder text = new StringBuilder();
            if (_ipv4.Length > 0) text.AppendLine("IPv4 " + _ipv4);
            if (_ipv6.Length > 0) text.AppendLine("IPv6 " + _ipv6);
            if (text.Length == 0) return;
            try
            {
                Clipboard.SetText(text.ToString().TrimEnd());
                Button button = (Button)sender;
                button.Content = "已复制";
                DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += delegate { timer.Stop(); button.Content = "复制出口 IP"; };
                timer.Start();
            }
            catch { }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
