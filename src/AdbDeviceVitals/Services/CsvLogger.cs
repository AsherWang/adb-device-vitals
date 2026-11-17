using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AdbDeviceVitals.Models;

namespace AdbDeviceVitals.Services
{
    public sealed class CsvLogger
    {
        private const string Header = "timestamp,battery_percent,temperature_c,voltage_v,power_source,network,rsrp_dbm,rsrq_db,sinr_db,download_mbps,upload_mbps,hotspot_clients,thermal_status";
        // 采集每 2.5 秒写一次，和 tether.log 一样可能被外部程序瞬时独占；日志失败不能影响采集。
        private static readonly object Gate = new object();
        private const int Attempts = 4;

        public string LogDirectory { get; private set; }

        public CsvLogger()
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AdbDeviceVitals", "logs")) { }

        /// <summary>测试可注入临时目录。</summary>
        public CsvLogger(string directory)
        {
            LogDirectory = directory;
        }

        public void Append(DeviceSnapshot value)
        {
            if (value == null || !value.Connected || string.IsNullOrEmpty(LogDirectory)) return;
            string file = Path.Combine(LogDirectory,
                value.Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
            bool header = !File.Exists(file);
            string row = string.Join(",", new string[]
            {
                value.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                Number(value.BatteryLevel), Number(value.BatteryTemperatureC), Number(value.BatteryVoltageV),
                Csv(value.PowerSource), Csv(value.NetworkType), Number(value.RsrpDbm), Number(value.RsrqDb), Number(value.SinrDb),
                value.DownloadMbps.ToString("0.###", CultureInfo.InvariantCulture),
                value.UploadMbps.ToString("0.###", CultureInfo.InvariantCulture),
                value.ConnectedClients.ToString(CultureInfo.InvariantCulture), Number(value.ThermalStatus)
            });

            lock (Gate)
            {
                for (int attempt = 0; attempt < Attempts; attempt++)
                {
                    try
                    {
                        Directory.CreateDirectory(LogDirectory);
                        using (FileStream stream = new FileStream(file, FileMode.Append, FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete))
                        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        {
                            if (header) writer.WriteLine(Header);
                            writer.WriteLine(row);
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

        private static string Number(object value)
        {
            if (value == null) return string.Empty;
            IFormattable formattable = value as IFormattable;
            return formattable != null ? formattable.ToString(null, CultureInfo.InvariantCulture) : value.ToString();
        }

        private static string Csv(string value)
        {
            value = value ?? string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}

