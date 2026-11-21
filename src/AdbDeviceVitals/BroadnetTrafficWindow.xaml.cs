using System;
using System.Collections.Generic;
using System.Windows;
using AdbDeviceVitals.Services;

namespace AdbDeviceVitals
{
    /// <summary>
    /// 「剩余流量」卡片右上角 ⓘ 打开的详情弹窗：号码 / 话费 / 当月消费 + 每个流量包明细。
    /// 由 MainWindow 在读到广电余量后调用 Apply 填充；「刷新」回到主窗口那条登录 / 查询链路。
    /// </summary>
    public partial class BroadnetTrafficWindow
    {
        private readonly Action _refresh;

        public BroadnetTrafficWindow(Action refresh)
        {
            InitializeComponent();
            _refresh = refresh;
        }

        /// <summary>打开窗口前由主窗口设置运营商名，标题和引导文案跟着走。</summary>
        internal void SetCarrier(string name)
        {
            string carrier = string.IsNullOrEmpty(name) ? "运营商" : name;
            WindowTitle.Title = carrier + "套餐余量";
            Title = carrier + "套餐余量";
            SummaryText.Text = "点「刷新」从" + carrier + "营业厅读取；首次使用会弹出窗口让你自己登录。";
        }

        internal void Apply(BroadnetTraffic traffic)
        {
            Apply(traffic, null);
        }

        internal void Apply(BroadnetTraffic traffic, UsageStats stats)
        {
            if (traffic == null)
            {
                ApplyCore("", "", "", "", "");
                ApplyUsageCore(null);
                return;
            }
            List<string> rows = new List<string>();
            foreach (BroadnetPackage pkg in traffic.Packages)
            {
                // 用 | 分隔字段、\n 分隔行：测试可以用基础类型字符串反射填样例，
                // 不必在渲染测试项目里构造 internal 的 BroadnetTraffic。
                string name = (pkg.Name ?? "").Replace("|", "／").Replace("\n", " ");
                rows.Add(string.Join("|", name, pkg.Remain ?? "", pkg.Unit ?? "", pkg.Total ?? "", pkg.Used ?? ""));
            }
            // 号码统一打码，和登录窗 / 状态栏保持一致，详情窗不显示完整手机号。
            ApplyCore(BroadnetSessionService.MaskPhone(traffic.Phone ?? ""), traffic.Balance ?? "", traffic.MonthlySpend ?? "",
                traffic.MonthlyUsed ?? "", string.Join("\n", rows.ToArray()));
            ApplyUsage(stats);
        }

        private void ApplyUsage(UsageStats stats)
        {
            if (stats == null || stats.SampleCount == 0)
            {
                ApplyUsageCore(null);
                return;
            }
            ApplyUsageCore(stats);
        }

        /// <summary>
        /// 用量统计区填充。参数是基础类型（today/month/avg + 24 小时数组 + 采样说明），
        /// 便于渲染测试用反射注入样例，不必构造 internal 的 UsageStats。
        /// </summary>
        internal void ApplyUsageCore(UsageStats stats)
        {
            if (stats == null || stats.SampleCount == 0)
            {
                UsageTodayText.Text = "—";
                UsageMonthText.Text = "—";
                UsageAvgText.Text = "—";
                UsageChart.SetValues(null);
                UsageHintText.Text = "每 10 分钟采样一次，按小时汇总。程序未运行或登录过期时不采样。";
                return;
            }
            UsageTodayText.Text = FormatG(stats.TodayUsedG);
            UsageMonthText.Text = FormatG(stats.MonthRecordedG);
            UsageAvgText.Text = FormatG(stats.DailyAverageG) + "/天";
            UsageChart.SetValues(stats.HourlyG);
            string hint = "每 10 分钟采样一次，按小时汇总；本月记录只统计程序在跑且登录有效的时段。"
                + "共 " + stats.SampleCount + " 次采样";
            if (stats.HasGap) hint += "，含登录中断档（恢复后用量计入首次采样）";
            UsageHintText.Text = hint + "。";
        }

        /// <summary>纯基础类型重载，供渲染测试反射注入统计样例。</summary>
        internal void ApplyUsageNumbers(double today, double month, double avg, double[] hourly,
            int sampleCount, bool hasGap)
        {
            UsageTodayText.Text = FormatG(today);
            UsageMonthText.Text = FormatG(month);
            UsageAvgText.Text = FormatG(avg) + "/天";
            UsageChart.SetValues(hourly);
            string hint = "每 10 分钟采样一次，按小时汇总；本月记录只统计程序在跑且登录有效的时段。"
                + "共 " + sampleCount + " 次采样";
            if (hasGap) hint += "，含登录中断档（恢复后用量计入首次采样）";
            UsageHintText.Text = hint + "。";
        }

        private static string FormatG(double g)
        {
            return g.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " G";
        }

        /// <summary>参数是基础类型，便于渲染测试用反射直接填充套餐样例。</summary>
        internal void ApplyCore(string phone, string balance, string spend, string used, string rows)
        {
            PhoneText.Text = string.IsNullOrEmpty(phone) ? "—" : phone;
            BalanceText.Text = string.IsNullOrEmpty(balance) ? "—" : balance + " 元";
            SpendText.Text = string.IsNullOrEmpty(spend) ? "—" : spend + " 元";
            UsedText.Text = string.IsNullOrEmpty(used) ? "—" : used;

            List<PackageRow> list = new List<PackageRow>();
            if (!string.IsNullOrEmpty(rows))
            {
                string[] lines = rows.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    string[] fields = line.Split('|');
                    if (fields.Length < 5) continue;
                    list.Add(new PackageRow(fields[0], fields[1], fields[2], fields[3], fields[4]));
                }
            }
            PackageList.ItemsSource = list;
            EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_refresh != null) _refresh();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        internal sealed class PackageRow
        {
            public string Name { get; private set; }
            public string Remain { get; private set; }
            public string Unit { get; private set; }
            public string Total { get; private set; }
            public string Used { get; private set; }

            internal PackageRow(string name, string remain, string unit, string total, string used)
            {
                Name = name;
                Remain = remain;
                Unit = unit;
                Total = total;
                Used = used;
            }
        }
    }
}
