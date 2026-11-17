using System;
using System.Globalization;
using System.Windows;
using AdbDeviceVitals.Models;

namespace AdbDeviceVitals
{
    /// <summary>
    /// 「电池」卡片右上角 ⓘ 打开的详情弹窗。主卡片只保留电量结论，
    /// 充电状态、供电方式、电压、温度、热状态、健康度和学习容量都在这里按分组展开；
    /// 打开期间由 MainWindow 每次采集后调用 Apply 实时刷新。
    /// </summary>
    public partial class BatteryDetailsWindow
    {
        public BatteryDetailsWindow()
        {
            InitializeComponent();
        }

        public void Apply(DeviceSnapshot snapshot)
        {
            if (snapshot == null) return;

            LevelText.Text = snapshot.BatteryLevel.HasValue
                ? snapshot.BatteryLevel.Value + "%"
                : "—";
            StatusText.Text = BatteryFormat.Text(snapshot.BatteryStatus);
            PowerText.Text = BatteryFormat.PowerSourceName(snapshot);
            VoltageText.Text = BatteryFormat.Voltage(snapshot.BatteryVoltageV);
            HealthText.Text = BatteryFormat.Text(snapshot.BatteryHealth);
            TemperatureText.Text = BatteryFormat.Temperature(snapshot.BatteryTemperatureC);
            ThermalText.Text = BatteryFormat.ThermalName(snapshot.ThermalStatus);
            CapacityText.Text = BatteryFormat.Capacity(snapshot.LearnedCapacityMah);
            SampleTimeText.Text = snapshot.Timestamp.ToString("HH:mm:ss");
            SampleTimeText.ToolTip = "最近一次采集时间 · " + (snapshot.Connected ? "设备在线" : "设备离线");
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    /// <summary>
    /// 电池字段的统一格式化。主窗口的温度告警和详情弹窗共用一份文案，
    /// 避免「主界面 / 弹窗」两处维护出不同说法。
    /// </summary>
    internal static class BatteryFormat
    {
        public static string PowerSourceName(DeviceSnapshot value)
        {
            if (value.UsbPowered) return "USB 供电";
            if (value.AcPowered) return "交流电充电";
            if (value.WirelessPowered) return "无线充电";
            return "电池供电";
        }

        public static string Temperature(double? value)
        {
            return value.HasValue
                ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) + " °C"
                : "—";
        }

        public static string Voltage(double? value)
        {
            return value.HasValue
                ? value.Value.ToString("0.000", CultureInfo.InvariantCulture) + " V"
                : "电压未知";
        }

        public static string Capacity(double? value)
        {
            return value.HasValue
                ? value.Value.ToString("0", CultureInfo.InvariantCulture) + " mAh"
                : "容量数据不可用";
        }

        public static string ThermalName(int? value)
        {
            if (!value.HasValue) return "未知";
            string[] names = { "正常", "轻微", "中等", "严重", "临界", "紧急", "即将关机" };
            return value.Value >= 0 && value.Value < names.Length
                ? names[value.Value]
                : value.Value.ToString(CultureInfo.InvariantCulture);
        }

        public static string Text(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "—" : value;
        }
    }
}
