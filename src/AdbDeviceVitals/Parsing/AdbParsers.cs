using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using AdbDeviceVitals.Models;

namespace AdbDeviceVitals.Parsing
{
    public static class AdbParsers
    {
        private const int InvalidSignal = 2147483647;

        public static void ParseBattery(string output, DeviceSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(output)) return;
            string[] lines = output.Replace("\r", string.Empty).Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string key = line.Substring(0, colon).Trim().ToLowerInvariant();
                string value = line.Substring(colon + 1).Trim();
                int number;
                bool boolean;

                if (key == "level" && int.TryParse(value, out number)) snapshot.BatteryLevel = number;
                else if (key == "temperature" && int.TryParse(value, out number)) snapshot.BatteryTemperatureC = number / 10.0;
                else if (key == "voltage" && int.TryParse(value, out number)) snapshot.BatteryVoltageV = number / 1000.0;
                else if (key == "status" && int.TryParse(value, out number)) snapshot.BatteryStatus = BatteryStatusName(number);
                else if (key == "health" && int.TryParse(value, out number)) snapshot.BatteryHealth = BatteryHealthName(number);
                else if (key == "usb powered" && bool.TryParse(value, out boolean)) snapshot.UsbPowered = boolean;
                else if (key == "ac powered" && bool.TryParse(value, out boolean)) snapshot.AcPowered = boolean;
                else if (key == "wireless powered" && bool.TryParse(value, out boolean)) snapshot.WirelessPowered = boolean;
            }
        }

        public static void ParseBatteryStats(string output, DeviceSnapshot snapshot)
        {
            Match match = Regex.Match(output ?? string.Empty, @"Last learned battery capacity:\s*([0-9.]+)\s*mAh", RegexOptions.IgnoreCase);
            if (!match.Success)
                match = Regex.Match(output ?? string.Empty, @"Estimated battery capacity:\s*([0-9.]+)\s*mAh", RegexOptions.IgnoreCase);
            double value;
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                snapshot.LearnedCapacityMah = value;
        }

        public static void ParseThermal(string output, DeviceSnapshot snapshot)
        {
            Match match = Regex.Match(output ?? string.Empty, @"Thermal Status:\s*(\d+)", RegexOptions.IgnoreCase);
            int value;
            if (match.Success && int.TryParse(match.Groups[1].Value, out value)) snapshot.ThermalStatus = value;
        }

        public static void ParseRadioProperties(string networkType, string operatorName, DeviceSnapshot snapshot)
        {
            snapshot.NetworkType = CleanProperty(networkType);
            snapshot.OperatorName = CleanProperty(operatorName);
            int comma = snapshot.NetworkType.IndexOf(',');
            if (comma >= 0) snapshot.NetworkType = snapshot.NetworkType.Substring(0, comma);
            comma = snapshot.OperatorName.IndexOf(',');
            if (comma >= 0) snapshot.OperatorName = snapshot.OperatorName.Substring(0, comma);
        }

        public static void ParseTelephony(string output, DeviceSnapshot snapshot)
        {
            string text = output ?? string.Empty;
            Match nr = Regex.Match(text, @"mNr=CellSignalStrengthNr:\{([^}]*)\}", RegexOptions.IgnoreCase);
            if (nr.Success)
            {
                string block = nr.Groups[1].Value;
                snapshot.RsrpDbm = ValidSignal(ReadInt(block, @"ssRsrp\s*=\s*(-?\d+)"));
                snapshot.RsrqDb = ValidSignal(ReadInt(block, @"ssRsrq\s*=\s*(-?\d+)"));
                snapshot.SinrDb = ValidSignal(ReadInt(block, @"ssSinr\s*=\s*(-?\d+)"));
                snapshot.SignalLevel = ReadInt(block, @"(?:miuiLevel|level)\s*=\s*(\d+)");
                if (snapshot.RsrpDbm.HasValue && string.IsNullOrEmpty(snapshot.NetworkType)) snapshot.NetworkType = "NR";
            }

            if (!snapshot.RsrpDbm.HasValue)
            {
                Match lte = Regex.Match(text, @"mLte=CellSignalStrengthLte:\s*([^,}]+(?:[,][^,}]+)*)", RegexOptions.IgnoreCase);
                if (lte.Success)
                {
                    string block = lte.Groups[1].Value;
                    snapshot.RsrpDbm = ValidSignal(ReadInt(block, @"rsrp=(-?\d+)"));
                    snapshot.RsrqDb = ValidSignal(ReadInt(block, @"rsrq=(-?\d+)"));
                    int? rssnr = ValidSignal(ReadInt(block, @"rssnr=(-?\d+)"));
                    if (rssnr.HasValue) snapshot.SinrDb = (int)Math.Round(rssnr.Value / 10.0);
                    snapshot.SignalLevel = ReadInt(block, @"(?:miuiLevel|level)=(\d+)");
                    if (snapshot.RsrpDbm.HasValue && string.IsNullOrEmpty(snapshot.NetworkType)) snapshot.NetworkType = "LTE";
                }
            }

            Match display = Regex.Match(text, @"mTelephonyDisplayInfo=TelephonyDisplayInfo\s*\{network=([^,}]+)", RegexOptions.IgnoreCase);
            if (display.Success && (string.IsNullOrEmpty(snapshot.NetworkType) || snapshot.NetworkType == "Unknown"))
                snapshot.NetworkType = display.Groups[1].Value.Trim();
        }

        public static void ParseTethering(string output, DeviceSnapshot snapshot)
        {
            string text = output ?? string.Empty;
            snapshot.HotspotActive = Regex.IsMatch(text, @"^\s*(?:(?:wlan|softap)\d+|ap_br_(?:wlan|softap)\d*)\s+-\s+TetheredState(?:\s+-\s+lastError\s*=\s*0)?", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            snapshot.UsbTetheringActive = Regex.IsMatch(text, @"^\s*(?:rndis|usb|ncm)\d+\s+-\s+TetheredState(?:\s+-\s+lastError\s*=\s*0)?", RegexOptions.IgnoreCase | RegexOptions.Multiline);

            Match upstream = Regex.Match(text, @"Current upstream interface\(s\):\s*\[([^\]]+)\]", RegexOptions.IgnoreCase);
            snapshot.UpstreamInterface = upstream.Success ? upstream.Groups[1].Value.Trim().Split(',')[0].Trim() : string.Empty;

            int wifiClients = 0;
            int usbClients = 0;
            string[] lines = text.Replace("\r", string.Empty).Split('\n');
            foreach (string line in lines)
            {
                if (line.IndexOf("state=TetheredState", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int clients = Regex.Matches(line, @"client:\s*/", RegexOptions.IgnoreCase).Count;
                if (Regex.IsMatch(line, @"name=(?:(?:wlan|softap)\d+|ap_br_(?:wlan|softap)\d*)", RegexOptions.IgnoreCase))
                    wifiClients += clients;
                else if (Regex.IsMatch(line, @"name=(?:rndis|usb|ncm)\d+", RegexOptions.IgnoreCase))
                    usbClients += clients;
            }
            snapshot.ConnectedClients = wifiClients;
            snapshot.UsbTetheringClients = usbClients;
        }

        public static void ParseWifi(string output, DeviceSnapshot snapshot)
        {
            string text = output ?? string.Empty;
            Match current = Regex.Match(text, @"mCurrentSoftApInfoMap[^\r\n]*frequency=\s*(\d+)", RegexOptions.IgnoreCase);
            int frequency;
            if (current.Success && int.TryParse(current.Groups[1].Value, out frequency))
            {
                snapshot.HotspotFrequencyMhz = frequency;
                snapshot.HotspotBand = frequency >= 4900 ? "5 GHz" : "2.4 GHz";
                snapshot.HotspotActive = true;
            }

            MatchCollection clientMatches = Regex.Matches(text, @"num_connected_clients=(\d+)", RegexOptions.IgnoreCase);
            if (clientMatches.Count > 0 && snapshot.ConnectedClients == 0)
            {
                int clients;
                if (int.TryParse(clientMatches[clientMatches.Count - 1].Groups[1].Value, out clients)) snapshot.ConnectedClients = clients;
            }
        }

        public static IDictionary<string, InterfaceCounters> ParseNetworkStats(string output)
        {
            Dictionary<string, InterfaceCounters> result = new Dictionary<string, InterfaceCounters>(StringComparer.OrdinalIgnoreCase);
            string[] lines = (output ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            foreach (string line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string name = line.Substring(0, colon).Trim();
                string[] values = Regex.Split(line.Substring(colon + 1).Trim(), @"\s+");
                long rx;
                long tx;
                if (values.Length >= 9 && long.TryParse(values[0], out rx) && long.TryParse(values[8], out tx))
                    result[name] = new InterfaceCounters(rx, tx);
            }
            return result;
        }

        private static string CleanProperty(string value)
        {
            string result = (value ?? string.Empty).Trim();
            if (result.StartsWith("[") && result.EndsWith("]") && result.Length > 1)
                result = result.Substring(1, result.Length - 2);
            return result.Trim();
        }

        private static int? ReadInt(string text, string pattern)
        {
            Match match = Regex.Match(text ?? string.Empty, pattern, RegexOptions.IgnoreCase);
            int value;
            return match.Success && int.TryParse(match.Groups[1].Value, out value) ? (int?)value : null;
        }

        private static int? ValidSignal(int? value)
        {
            return value.HasValue && value.Value != InvalidSignal ? value : null;
        }

        private static string BatteryStatusName(int value)
        {
            if (value == 2) return "Charging";
            if (value == 3) return "Discharging";
            if (value == 4) return "Not charging";
            if (value == 5) return "Full";
            return "Unknown";
        }

        private static string BatteryHealthName(int value)
        {
            if (value == 2) return "Good";
            if (value == 3) return "Overheat";
            if (value == 4) return "Dead";
            if (value == 5) return "Over voltage";
            if (value == 7) return "Cold";
            return "Unknown";
        }
    }

    public sealed class InterfaceCounters
    {
        public long ReceiveBytes { get; private set; }
        public long TransmitBytes { get; private set; }

        public InterfaceCounters(long receiveBytes, long transmitBytes)
        {
            ReceiveBytes = receiveBytes;
            TransmitBytes = transmitBytes;
        }
    }
}

