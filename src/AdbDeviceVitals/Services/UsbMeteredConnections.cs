using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AdbDeviceVitals.Models;

namespace AdbDeviceVitals.Services
{
    internal sealed class UsbMeteredConnections : IDisposable
    {
        private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(5));
        private volatile Dictionary<string, string> _status = new(StringComparer.OrdinalIgnoreCase);
        private volatile bool _stopping;
        private readonly Dictionary<Guid, DateTime> _retryAfter = new();

        public UsbMeteredConnections() => _ = Task.Run(MonitorAsync);
        public string Status(string serial) => _status.GetValueOrDefault(serial, "等待识别 USB 共享网卡");

        private async Task MonitorAsync()
        {
            do
            {
                try { if (!_stopping) Refresh(); }
                catch (Exception ex) { Trace.TraceWarning("USB 计费状态读取失败：" + ex.Message); }
            } while (await _timer.WaitForNextTickAsync().ConfigureAwait(false));
        }

        private void Refresh()
        {
            var status = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var active = new HashSet<Guid>();
            foreach (var nic in UsbTetherAdapter.Connected())
            {
                var device = nic.Device;
                if (!device.MeteredOnUsb) continue;
                var id = nic.Id;
                try
                {
                    active.Add(id);
                    var cost = ReadCost(id);
                    if (!cost.Metered && cost.Source != 1 && DateTime.UtcNow < _retryAfter.GetValueOrDefault(id)
                        && _status.TryGetValue(device.Serial, out var lastStatus))
                    {
                        status[device.Serial] = lastStatus;
                        continue;
                    }
                    if (!cost.Metered && cost.Source != 1 && !_stopping
                        && DateTime.UtcNow >= _retryAfter.GetValueOrDefault(id)
                        && ManagedDeviceStore.Find(AppSettings.ManagedDeviceList, device.Serial)?.MeteredOnUsb == true)
                    {
                        _retryAfter[id] = DateTime.UtcNow.AddMinutes(1);
                        EnableMetering(id);
                        cost = ReadCost(id);
                    }
                    if (cost.Metered) _retryAfter.Remove(id);
                    status[device.Serial] = nic.Name + (cost.Metered ? " · 已按流量计费"
                        : cost.Source == 1 ? " · 计费由 Windows 组策略管理" : " · 计费尚未生效，将自动重试");
                }
                catch (Exception ex)
                {
                    status[device.Serial] = nic.Name + " · 未能确认计费设置：" + ex.Message;
                }
            }
            foreach (var id in new List<Guid>(_retryAfter.Keys))
                if (!active.Contains(id)) _retryAfter.Remove(id);
            _status = status;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ConnectionCost
        {
            public uint Flags;
            public int Source;
            public bool Metered => (Flags & 6) != 0; // Fixed or Variable
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct EthernetProfile
        {
            public Guid Interface;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Profile;
        }

        private static ConnectionCost ReadCost(Guid id)
        {
            uint result = WcmQueryProperty(ref id, IntPtr.Zero, 4, IntPtr.Zero, out uint size, out var data);
            if (result != 0) throw new Win32Exception((int)result);
            try
            {
                if (data == IntPtr.Zero || size < Marshal.SizeOf<ConnectionCost>())
                    throw new InvalidOperationException("Windows 未返回连接计费状态");
                return Marshal.PtrToStructure<ConnectionCost>(data);
            }
            finally { WcmFreeMemory(data); }
        }

        private static void EnableMetering(Guid id)
        {
            var cost = new ConnectionCost { Flags = 2, Source = 2 };
            uint result = WcmSetProperty(ref id, IntPtr.Zero, 4, IntPtr.Zero, (uint)Marshal.SizeOf<ConnectionCost>(), ref cost);
            // Some Windows builds reject Ethernet in WCM. Use the per-interface user
            // setting in Data Usage, then verify the effective result through WCM.
            if (result == 87)
            {
                var profile = new EthernetProfile { Interface = id, Profile = string.Empty };
                cost.Source = 0;
                result = DusmSetUserCost(ref profile, ref cost);
            }
            if (result != 0) throw new Win32Exception((int)result);
        }

        [DllImport("wcmapi.dll")]
        private static extern uint WcmQueryProperty(ref Guid id, IntPtr profile, int property, IntPtr reserved, out uint size, out IntPtr data);
        [DllImport("wcmapi.dll")]
        private static extern uint WcmSetProperty(ref Guid id, IntPtr profile, int property, IntPtr reserved, uint size, ref ConnectionCost data);
        [DllImport("wcmapi.dll")]
        private static extern void WcmFreeMemory(IntPtr data);
        [DllImport("dusmapi.dll")]
        private static extern uint DusmSetUserCost(ref EthernetProfile profile, ref ConnectionCost cost);

        public void Dispose()
        {
            _stopping = true;
            _timer.Dispose();
        }
    }
}
