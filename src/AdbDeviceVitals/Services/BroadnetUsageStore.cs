using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AdbDeviceVitals.Services
{
    /// <summary>
    /// 广电套餐用量的时间序列存储与统计。
    ///
    /// 数据来源：每 10 分钟静默刷新拿到的「本月累计已用」（站点 addupValue，单位 KB）。
    /// 落盘格式：usage/yyyy-MM.jsonl，一行一次采样（JSON Lines，避免包名里的逗号/引号
    /// 破坏 CSV 转义）。跨月按文件隔离，天然处理站点每月 1 号把累计清零的问题。
    ///
    /// 统计口径：
    /// - 相邻采样的同名包差值之和即为这段时间真实用量；包消失（结转包过期）不记负增量。
    /// - 「本月已用」以站点累计值为准；按差值累加得到的「本月记录」只覆盖程序在跑且
    ///   登录态有效的时段，二者有差距是正常现象（见 UI 说明）。
    /// - 采样间隔超过 30 分钟标记为空洞：这段用量被摊到恢复后第一次采样上，无法归到具体时段。
    /// </summary>
    internal sealed class BroadnetUsageStore
    {
        private static readonly TimeSpan GapThreshold = TimeSpan.FromMinutes(30);
        private static readonly object Gate = new object();
        private const int Attempts = 4;

        public string UsageDirectory { get; private set; }

        public BroadnetUsageStore()
            : this(Path.Combine(AppSettings.ConfigDirectory, "usage")) { }

        /// <summary>
        /// 按运营商隔离用量数据。广电沿用原 usage/ 目录（保留历史采样），
        /// 其他运营商用 usage/{code}/ 子目录，避免两家数据混在一起。
        /// </summary>
        public BroadnetUsageStore(TrafficCarrier carrier)
            : this(carrier == TrafficCarrier.Broadnet
                ? Path.Combine(AppSettings.ConfigDirectory, "usage")
                : Path.Combine(AppSettings.ConfigDirectory, "usage", CarrierMapping.CodeName(carrier))) { }

        /// <summary>测试可注入临时目录。</summary>
        public BroadnetUsageStore(string directory)
        {
            UsageDirectory = directory;
        }

        /// <summary>追加一次采样。packages 是「包名 → 已用 KB」；失败只影响本次采样，不抛异常。</summary>
        internal void Append(DateTime timestamp, double usedKb, string balance,
            IList<KeyValuePair<string, double>> packages)
        {
            if (string.IsNullOrEmpty(UsageDirectory) || usedKb < 0) return;
            UsageRecord record = new UsageRecord
            {
                t = timestamp.ToString("o", CultureInfo.InvariantCulture),
                u = usedKb,
                b = balance ?? string.Empty,
                p = new List<UsagePackageRecord>()
            };
            if (packages != null)
            {
                foreach (KeyValuePair<string, double> pair in packages)
                    record.p.Add(new UsagePackageRecord { n = pair.Key ?? string.Empty, u = pair.Value });
            }
            string line = JsonSerializer.Serialize(record);
            string file = PathForMonth(timestamp.Year, timestamp.Month);
            lock (Gate)
            {
                for (int attempt = 0; attempt < Attempts; attempt++)
                {
                    try
                    {
                        Directory.CreateDirectory(UsageDirectory);
                        using (FileStream stream = new FileStream(file, FileMode.Append, FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete))
                        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        {
                            writer.WriteLine(line);
                        }
                        return;
                    }
                    catch (IOException)
                    {
                        if (attempt == Attempts - 1) return;
                    }
                    catch (Exception)
                    {
                        return;
                    }
                    Thread.Sleep(10 * (attempt + 1));
                }
            }
        }

        internal List<UsageSample> ReadMonth(int year, int month)
        {
            List<UsageSample> result = new List<UsageSample>();
            string file = PathForMonth(year, month);
            if (!File.Exists(file)) return result;
            lock (Gate)
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        UsageRecord record = JsonSerializer.Deserialize<UsageRecord>(line);
                        DateTime timestamp;
                        if (record == null || !DateTime.TryParse(record.t, CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out timestamp)) continue;
                        UsageSample sample = new UsageSample
                        {
                            Timestamp = timestamp,
                            UsedKb = record.u,
                            Balance = record.b ?? string.Empty
                        };
                        if (record.p != null)
                        {
                            foreach (UsagePackageRecord pkg in record.p)
                                sample.Packages.Add(new UsagePackageSample
                                {
                                    Name = pkg.n ?? string.Empty,
                                    UsedKb = pkg.u
                                });
                        }
                        result.Add(sample);
                    }
                    catch
                    {
                        // 单行损坏（写一半 / 外部编辑）跳过，不影响整月其它采样。
                    }
                }
            }
            return result;
        }

        internal List<UsageSample> ReadCurrentMonth(DateTime now)
        {
            return ReadMonth(now.Year, now.Month);
        }

        private string PathForMonth(int year, int month)
        {
            return Path.Combine(UsageDirectory,
                year.ToString("0000", CultureInfo.InvariantCulture) + "-" +
                month.ToString("00", CultureInfo.InvariantCulture) + ".jsonl");
        }

        /// <summary>把原始 KB 换算成 G（与卡片口径一致，保留两位）。</summary>
        internal static double ToG(double kb)
        {
            return kb / 1048576.0;
        }

        /// <summary>
        /// 核心差分逻辑，纯函数便于单测：samples 可以是乱序的（内部按时间排序）。
        /// </summary>
        internal static UsageStats Analyze(IReadOnlyList<UsageSample> samples, DateTime now)
        {
            UsageStats stats = new UsageStats();
            if (samples == null || samples.Count == 0) return stats;

            // 防御：跳过坏读数（站点风控时曾把「已登录但 0 个包、累计 0」写进历史）。
            // 这类采样会把站点恢复后的累计跳变全算成单时段用量，必须在聚合前剔除。
            List<UsageSample> sorted = new List<UsageSample>();
            foreach (UsageSample sample in samples)
            {
                if (sample == null || sample.UsedKb <= 0 || sample.Packages.Count == 0) continue;
                sorted.Add(sample);
            }
            if (sorted.Count == 0) return stats;
            sorted.Sort(delegate(UsageSample a, UsageSample b) { return a.Timestamp.CompareTo(b.Timestamp); });
            stats.SampleCount = sorted.Count;
            stats.LastSampleAt = sorted[sorted.Count - 1].Timestamp;
            stats.MonthAuthoritativeG = ToG(sorted[sorted.Count - 1].UsedKb);

            HashSet<DateTime> coveredDays = new HashSet<DateTime>();
            foreach (UsageSample sample in sorted) coveredDays.Add(sample.Timestamp.Date);
            stats.CoveredDays = coveredDays.Count;

            DateTime today = now.Date;
            UsageSample previous = null;
            foreach (UsageSample current in sorted)
            {
                if (previous != null)
                {
                    double deltaKb = DeltaBetween(previous, current);
                    if (deltaKb > 0)
                    {
                        bool gap = current.Timestamp - previous.Timestamp > GapThreshold;
                        stats.Deltas.Add(new UsageDelta
                        {
                            Timestamp = current.Timestamp,
                            DeltaKb = deltaKb,
                            Gap = gap
                        });
                        double deltaG = ToG(deltaKb);
                        stats.MonthRecordedG += deltaG;
                        if (gap) stats.HasGap = true;
                        if (current.Timestamp.Date == today) stats.TodayUsedG += deltaG;
                        stats.HourlyG[current.Timestamp.Hour] += deltaG;
                        int day = current.Timestamp.Day;
                        if (day >= 1 && day <= 31) stats.DailyG[day] += deltaG;
                    }
                }
                previous = current;
            }
            stats.DailyAverageG = stats.CoveredDays > 0
                ? stats.MonthRecordedG / stats.CoveredDays : 0;
            return stats;
        }

        /// <summary>
        /// 按包名差分；任一同名包都用「当前 - 上次」，负增量（重置/包过期）忽略。
        /// 完全没有可比的包时退回总量差分（兼容早期只有累计值的采样）。
        /// </summary>
        private static double DeltaBetween(UsageSample previous, UsageSample current)
        {
            double byPackage = 0;
            bool anyComparable = false;
            foreach (UsagePackageSample pkg in current.Packages)
            {
                UsagePackageSample prior = null;
                foreach (UsagePackageSample old in previous.Packages)
                {
                    if (string.Equals(old.Name, pkg.Name, StringComparison.Ordinal))
                    {
                        prior = old;
                        break;
                    }
                }
                if (prior == null) continue;
                anyComparable = true;
                if (pkg.UsedKb > prior.UsedKb) byPackage += pkg.UsedKb - prior.UsedKb;
            }
            if (anyComparable) return byPackage;
            return current.UsedKb > previous.UsedKb ? current.UsedKb - previous.UsedKb : 0;
        }

        private sealed class UsageRecord
        {
            public string t { get; set; } = string.Empty;
            public double u { get; set; }
            public string b { get; set; } = string.Empty;
            public List<UsagePackageRecord> p { get; set; } = new List<UsagePackageRecord>();
        }

        private sealed class UsagePackageRecord
        {
            public string n { get; set; } = string.Empty;
            public double u { get; set; }
        }
    }

    internal sealed class UsageSample
    {
        internal DateTime Timestamp;
        internal double UsedKb;
        internal string Balance = string.Empty;
        internal List<UsagePackageSample> Packages = new List<UsagePackageSample>();
    }

    internal sealed class UsagePackageSample
    {
        internal string Name = string.Empty;
        internal double UsedKb;
    }

    internal sealed class UsageDelta
    {
        internal DateTime Timestamp;
        internal double DeltaKb;
        internal bool Gap;
    }

    internal sealed class UsageStats
    {
        internal int SampleCount;
        internal int CoveredDays;
        internal double TodayUsedG;
        internal double MonthRecordedG;
        internal double MonthAuthoritativeG;
        internal double DailyAverageG;
        internal bool HasGap;
        internal DateTime LastSampleAt;
        internal readonly List<UsageDelta> Deltas = new List<UsageDelta>();
        internal readonly double[] HourlyG = new double[24];
        internal readonly double[] DailyG = new double[32];
    }
}
