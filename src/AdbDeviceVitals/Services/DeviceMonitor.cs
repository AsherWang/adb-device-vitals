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
        private static readonly TimeSpan SignalInterval = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan TetheringInterval = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan ThermalInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan CapacityInterval = TimeSpan.FromMinutes(30);

        private readonly AdbClient _adb;
        private DeviceSnapshot _last;
        private DateTime _lastSignalUpdate = DateTime.MinValue;
        private DateTime _lastTetheringUpdate = DateTime.MinValue;
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
                snapshot.Error = "未找到 adb.exe。";
                _last = snapshot;
                return snapshot;
            }

            IList<AdbDevice> devices = await _adb.ListDevicesAsync().ConfigureAwait(false);
            AdbDevice device = devices.FirstOrDefault(delegate(AdbDevice item) { return item.State == "device"; });
            if (device == null)
            {
                snapshot.Connected = false;
                snapshot.Error = devices.Any(delegate(AdbDevice item) { return item.State == "unauthorized"; })
                    ? "请在手机上允许这台电脑进行 USB 调试。"
                    : "未找到已授权的 Android 设备。";
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

            DateTime now = DateTime.Now;
            bool readCapacity = forceFull || now - _lastCapacityUpdate > CapacityInterval;
            bool readSignal = forceFull || now - _lastSignalUpdate > SignalInterval;
            bool readTethering = forceFull || now - _lastTetheringUpdate > TetheringInterval;
            bool readThermal = forceFull || now - _lastThermalUpdate > ThermalInterval;

            // 各分支之间没有依赖，并行执行。一轮耗时可从"所有命令相加"降到"最慢的那一条"，
            // 单条 dumpsys 卡住时也不会再拖住整轮刷新。
            PollParts parts = new PollParts();
            string serial = device.Serial;
            List<Task> tasks = new List<Task>();
            tasks.Add(CollectBatteryAsync(serial, snapshot, readCapacity, parts));
            if (readSignal) tasks.Add(CollectSignalAsync(serial, snapshot, parts));
            if (readTethering) tasks.Add(CollectTetheringAsync(serial, snapshot, parts));
            if (readThermal) tasks.Add(CollectThermalAsync(serial, snapshot, parts));
            await Task.WhenAll(tasks).ConfigureAwait(false);

            Merge(snapshot, parts);

            now = DateTime.Now;
            if (readCapacity) _lastCapacityUpdate = now;
            if (readSignal) _lastSignalUpdate = now;
            if (readTethering) _lastTetheringUpdate = now;
            if (readThermal) _lastThermalUpdate = now;

            // 放在合并之后执行，保证这批字节数按本轮最新的上游接口归属。
            await CollectTrafficAsync(serial, snapshot).ConfigureAwait(false);

            _last = snapshot;
            return snapshot;
        }

        private async Task CollectBatteryAsync(string serial, DeviceSnapshot seed, bool readCapacity, PollParts parts)
        {
            CommandResult battery = await _adb.RunShellAsync(serial, "dumpsys battery", 5000).ConfigureAwait(false);
            if (battery.Success)
            {
                DeviceSnapshot target = seed.Copy();
                AdbParsers.ParseBattery(battery.Output, target);
                parts.Battery = target;
            }

            if (!readCapacity) return;
            CommandResult stats = await _adb.RunShellAsync(serial, "dumpsys batterystats --charged", 12000).ConfigureAwait(false);
            if (!stats.Success) return;
            DeviceSnapshot capacity = new DeviceSnapshot();
            AdbParsers.ParseBatteryStats(stats.Output, capacity);
            if (capacity.LearnedCapacityMah.HasValue) parts.LearnedCapacityMah = capacity.LearnedCapacityMah;
        }

        private async Task CollectSignalAsync(string serial, DeviceSnapshot seed, PollParts parts)
        {
            CommandResult network = await _adb.RunShellAsync(serial, "getprop gsm.network.type", 3000).ConfigureAwait(false);
            CommandResult operatorName = await _adb.RunShellAsync(serial, "getprop gsm.operator.alpha", 3000).ConfigureAwait(false);
            DeviceSnapshot target = seed.Copy();
            AdbParsers.ParseRadioProperties(network.Output, operatorName.Output, target);
            CommandResult telephony = await _adb.RunShellAsync(serial, "dumpsys telephony.registry", 8000).ConfigureAwait(false);
            if (telephony.Success) AdbParsers.ParseTelephony(telephony.Output, target);
            parts.Signal = target;
        }

        private async Task CollectTetheringAsync(string serial, DeviceSnapshot seed, PollParts parts)
        {
            CommandResult tethering = await _adb.RunShellAsync(serial, "dumpsys tethering", 6000).ConfigureAwait(false);
            CommandResult wifi = await _adb.RunShellAsync(serial, "dumpsys wifi", 6000).ConfigureAwait(false);
            DeviceSnapshot target = seed.Copy();
            if (tethering.Success) AdbParsers.ParseTethering(tethering.Output, target);
            if (wifi.Success) AdbParsers.ParseWifi(wifi.Output, target);
            parts.Tethering = target;
        }

        private async Task CollectThermalAsync(string serial, DeviceSnapshot seed, PollParts parts)
        {
            CommandResult thermal = await _adb.RunShellAsync(serial, "dumpsys thermalservice", 6000).ConfigureAwait(false);
            if (!thermal.Success) return;
            DeviceSnapshot target = seed.Copy();
            AdbParsers.ParseThermal(thermal.Output, target);
            parts.Thermal = target;
        }

        private async Task CollectTrafficAsync(string serial, DeviceSnapshot snapshot)
        {
            CommandResult netdev = await _adb.RunShellAsync(serial, "cat /proc/net/dev", 4000).ConfigureAwait(false);
            if (!netdev.Success) return;

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

        private static void Merge(DeviceSnapshot target, PollParts parts)
        {
            if (parts.Battery != null)
            {
                target.BatteryLevel = parts.Battery.BatteryLevel;
                target.BatteryTemperatureC = parts.Battery.BatteryTemperatureC;
                target.BatteryVoltageV = parts.Battery.BatteryVoltageV;
                target.BatteryStatus = parts.Battery.BatteryStatus;
                target.BatteryHealth = parts.Battery.BatteryHealth;
                target.UsbPowered = parts.Battery.UsbPowered;
                target.AcPowered = parts.Battery.AcPowered;
                target.WirelessPowered = parts.Battery.WirelessPowered;
            }

            if (parts.LearnedCapacityMah.HasValue) target.LearnedCapacityMah = parts.LearnedCapacityMah;

            if (parts.Signal != null)
            {
                target.OperatorName = parts.Signal.OperatorName;
                target.NetworkType = parts.Signal.NetworkType;
                target.RsrpDbm = parts.Signal.RsrpDbm;
                target.RsrqDb = parts.Signal.RsrqDb;
                target.SinrDb = parts.Signal.SinrDb;
                target.SignalLevel = parts.Signal.SignalLevel;
            }

            if (parts.Tethering != null)
            {
                target.HotspotActive = parts.Tethering.HotspotActive;
                target.ConnectedClients = parts.Tethering.ConnectedClients;
                target.HotspotFrequencyMhz = parts.Tethering.HotspotFrequencyMhz;
                target.HotspotBand = parts.Tethering.HotspotBand;
                target.UsbTetheringActive = parts.Tethering.UsbTetheringActive;
                target.UsbTetheringClients = parts.Tethering.UsbTetheringClients;
                target.UpstreamInterface = parts.Tethering.UpstreamInterface;
            }

            if (parts.Thermal != null) target.ThermalStatus = parts.Thermal.ThermalStatus;
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

        // 每个分支把结果写在独立字段里，避免并发写同一份快照；失败时保持 null，沿用上一轮的值。
        private sealed class PollParts
        {
            public DeviceSnapshot Battery;
            public double? LearnedCapacityMah;
            public DeviceSnapshot Signal;
            public DeviceSnapshot Tethering;
            public DeviceSnapshot Thermal;
        }
    }
}
