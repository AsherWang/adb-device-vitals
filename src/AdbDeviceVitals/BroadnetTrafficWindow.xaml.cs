using System;
using System.Collections.Generic;
using System.Windows;

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

        internal void Apply(BroadnetTraffic traffic)
        {
            if (traffic == null)
            {
                ApplyCore("", "", "", "", "");
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
