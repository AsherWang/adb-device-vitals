using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AdbDeviceVitals.Models;
using AdbDeviceVitals.Parsing;

namespace AdbDeviceVitals.Services
{
    public sealed class DeviceMonitor
    {
        private readonly AdbClient _adb;
        private DeviceSnapshot _last;
        private DateTime _lastSignalUpdate = DateTime.MinValue;
        private DateTime _lastHotspotUpdate = DateTime.MinValue;
        private DateTime _lastThermalUpdate = DateTime.MinValue;
        private DateTime _lastCapacityUpdate = DateTime.MinValue;
        private DateTime _lastTrafficSample = DateTime.MinValue;
        private long? _lastRx;
        private long? _lastTx;

        public DeviceMonitor(AdbClient adb)
        {
            _adb = adb;
            _last = new DeviceSnapshot();
        }

        public AdbClient Adb { get { return _adb; } }

        public async Task<DeviceSnapshot> PollAsync(bool forceFull)
        {
            DeviceSnapshot snapshot = _last.Copy();
            snapshot.Timestamp = DateTime.Now;
            snapshot.Error = string.Empty;

            if (!_adb.IsAvailable)
            {
                snapshot.Connected = false;
                snapshot.Error = "adb.exe was not found.";
                _last = snapshot;
                return snapshot;
            }

            IList<AdbDevice> devices = await _adb.ListDevicesAsync().ConfigureAwait(false);
            AdbDevice device = devices.FirstOrDefault(delegate(AdbDevice item) { return item.State == "device"; });
            if (device == null)
            {
                snapshot.Connected = false;
                snapshot.Error = devices.Any(delegate(AdbDevice item) { return item.State == "unauthorized"; })
                    ? "Please authorize USB debugging on the phone."
                    : "No authorized Android device found.";
                _last = snapshot;
                return snapshot;
            }

            if (!string.Equals(snapshot.DeviceSerial, device.Serial, StringComparison.Ordinal))
            {
                snapshot = new DeviceSnapshot();
                snapshot.DeviceSerial = device.Serial;
                snapshot.DeviceModel = device.Model;
                _lastRx = null;
                _lastTx = null;
                _lastTrafficSample = DateTime.MinValue;
                forceFull = true;
            }

            snapshot.Connected = true;
            snapshot.DeviceSerial = device.Serial;
            snapshot.DeviceModel = device.Model;

            CommandResult battery = await _adb.RunShellAsync(device.Serial, "dumpsys battery", 5000).ConfigureAwait(false);
            if (battery.Success) AdbParsers.ParseBattery(battery.Output, snapshot);

            if (forceFull || DateTime.Now - _lastCapacityUpdate > TimeSpan.FromMinutes(30))
            {
                CommandResult stats = await _adb.RunShellAsync(device.Serial, "dumpsys batterystats --charged", 12000).ConfigureAwait(false);
                if (stats.Success) AdbParsers.ParseBatteryStats(stats.Output, snapshot);
                _lastCapacityUpdate = DateTime.Now;
            }

            if (forceFull || DateTime.Now - _lastSignalUpdate > TimeSpan.FromSeconds(6))
            {
                CommandResult network = await _adb.RunShellAsync(device.Serial, "getprop gsm.network.type", 5000).ConfigureAwait(false);
                CommandResult operatorName = await _adb.RunShellAsync(device.Serial, "getprop gsm.operator.alpha", 5000).ConfigureAwait(false);
                AdbParsers.ParseRadioProperties(network.Output, operatorName.Output, snapshot);
                CommandResult telephony = await _adb.RunShellAsync(device.Serial, "dumpsys telephony.registry", 12000).ConfigureAwait(false);
                if (telephony.Success) AdbParsers.ParseTelephony(telephony.Output, snapshot);
                _lastSignalUpdate = DateTime.Now;
            }

            if (forceFull || DateTime.Now - _lastHotspotUpdate > TimeSpan.FromSeconds(12))
            {
                CommandResult tethering = await _adb.RunShellAsync(device.Serial, "dumpsys tethering", 12000).ConfigureAwait(false);
                if (tethering.Success) AdbParsers.ParseTethering(tethering.Output, snapshot);
                CommandResult wifi = await _adb.RunShellAsync(device.Serial, "dumpsys wifi", 12000).ConfigureAwait(false);
                if (wifi.Success) AdbParsers.ParseWifi(wifi.Output, snapshot);
                _lastHotspotUpdate = DateTime.Now;
            }

            if (forceFull || DateTime.Now - _lastThermalUpdate > TimeSpan.FromSeconds(15))
            {
                CommandResult thermal = await _adb.RunShellAsync(device.Serial, "dumpsys thermalservice", 8000).ConfigureAwait(false);
                if (thermal.Success) AdbParsers.ParseThermal(thermal.Output, snapshot);
                _lastThermalUpdate = DateTime.Now;
            }

            CommandResult netdev = await _adb.RunShellAsync(device.Serial, "cat /proc/net/dev", 5000).ConfigureAwait(false);
            if (netdev.Success)
            {
                IDictionary<string, InterfaceCounters> counters = AdbParsers.ParseNetworkStats(netdev.Output);
                InterfaceCounters active = null;
                if (!string.IsNullOrEmpty(snapshot.UpstreamInterface)) counters.TryGetValue(snapshot.UpstreamInterface, out active);
                if (active == null)
                    active = counters.Where(delegate(KeyValuePair<string, InterfaceCounters> pair) { return pair.Key.StartsWith("rmnet_data", StringComparison.OrdinalIgnoreCase); })
                        .OrderByDescending(delegate(KeyValuePair<string, InterfaceCounters> pair) { return pair.Value.ReceiveBytes + pair.Value.TransmitBytes; })
                        .Select(delegate(KeyValuePair<string, InterfaceCounters> pair) { return pair.Value; })
                        .FirstOrDefault();
                if (active != null) UpdateTraffic(snapshot, active);
            }

            _last = snapshot;
            return snapshot;
        }

        private void UpdateTraffic(DeviceSnapshot snapshot, InterfaceCounters current)
        {
            DateTime now = DateTime.Now;
            if (_lastRx.HasValue && _lastTx.HasValue && _lastTrafficSample != DateTime.MinValue)
            {
                double seconds = (now - _lastTrafficSample).TotalSeconds;
                if (seconds > 0)
                {
                    long rxDelta = Math.Max(0, current.ReceiveBytes - _lastRx.Value);
                    long txDelta = Math.Max(0, current.TransmitBytes - _lastTx.Value);
                    snapshot.DownloadMbps = rxDelta * 8.0 / seconds / 1000000.0;
                    snapshot.UploadMbps = txDelta * 8.0 / seconds / 1000000.0;
                }
            }
            snapshot.CellularRxBytes = current.ReceiveBytes;
            snapshot.CellularTxBytes = current.TransmitBytes;
            _lastRx = current.ReceiveBytes;
            _lastTx = current.TransmitBytes;
            _lastTrafficSample = now;
        }
    }
}

