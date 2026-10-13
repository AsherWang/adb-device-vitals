using System;
using System.Globalization;
using System.IO;
using System.Text;
using AdbDeviceVitals.Models;

namespace AdbDeviceVitals.Services
{
    public sealed class CsvLogger
    {
        public string LogDirectory { get; private set; }

        public CsvLogger()
        {
            LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbDeviceVitals", "logs");
        }

        public void Append(DeviceSnapshot value)
        {
            if (value == null || !value.Connected) return;
            Directory.CreateDirectory(LogDirectory);
            string file = Path.Combine(LogDirectory, value.Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
            bool header = !File.Exists(file);
            using (StreamWriter writer = new StreamWriter(file, true, new UTF8Encoding(false)))
            {
                if (header) writer.WriteLine("timestamp,battery_percent,temperature_c,voltage_v,power_source,network,rsrp_dbm,rsrq_db,sinr_db,download_mbps,upload_mbps,hotspot_clients,thermal_status");
                writer.WriteLine(string.Join(",", new string[]
                {
                    value.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                    Number(value.BatteryLevel), Number(value.BatteryTemperatureC), Number(value.BatteryVoltageV),
                    Csv(value.PowerSource), Csv(value.NetworkType), Number(value.RsrpDbm), Number(value.RsrqDb), Number(value.SinrDb),
                    value.DownloadMbps.ToString("0.###", CultureInfo.InvariantCulture), value.UploadMbps.ToString("0.###", CultureInfo.InvariantCulture),
                    value.ConnectedClients.ToString(CultureInfo.InvariantCulture), Number(value.ThermalStatus)
                }));
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

