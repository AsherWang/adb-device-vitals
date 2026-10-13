using System;

namespace AdbDeviceVitals.Models
{
    public sealed class DeviceSnapshot
    {
        public DateTime Timestamp { get; set; }
        public bool Connected { get; set; }
        public string DeviceSerial { get; set; }
        public string DeviceModel { get; set; }
        public string Error { get; set; }

        public int? BatteryLevel { get; set; }
        public double? BatteryTemperatureC { get; set; }
        public double? BatteryVoltageV { get; set; }
        public double? LearnedCapacityMah { get; set; }
        public string BatteryStatus { get; set; }
        public string BatteryHealth { get; set; }
        public bool UsbPowered { get; set; }
        public bool AcPowered { get; set; }
        public bool WirelessPowered { get; set; }
        public int? ThermalStatus { get; set; }

        public string OperatorName { get; set; }
        public string NetworkType { get; set; }
        public int? RsrpDbm { get; set; }
        public int? RsrqDb { get; set; }
        public int? SinrDb { get; set; }
        public int? SignalLevel { get; set; }

        public bool HotspotActive { get; set; }
        public int ConnectedClients { get; set; }
        public int? HotspotFrequencyMhz { get; set; }
        public string HotspotBand { get; set; }
        public string UpstreamInterface { get; set; }

        public long? CellularRxBytes { get; set; }
        public long? CellularTxBytes { get; set; }
        public double DownloadMbps { get; set; }
        public double UploadMbps { get; set; }

        public DeviceSnapshot()
        {
            Timestamp = DateTime.Now;
            DeviceSerial = string.Empty;
            DeviceModel = string.Empty;
            Error = string.Empty;
            BatteryStatus = "Unknown";
            BatteryHealth = "Unknown";
            OperatorName = string.Empty;
            NetworkType = string.Empty;
            HotspotBand = string.Empty;
            UpstreamInterface = string.Empty;
        }

        public DeviceSnapshot Copy()
        {
            return (DeviceSnapshot)MemberwiseClone();
        }

        public string PowerSource
        {
            get
            {
                if (UsbPowered) return "USB";
                if (AcPowered) return "AC";
                if (WirelessPowered) return "Wireless";
                return "Battery";
            }
        }
    }
}

