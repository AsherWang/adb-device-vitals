using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using AdbDeviceVitals.Models;
using Microsoft.Win32;

namespace AdbDeviceVitals.Services
{
    internal sealed record UsbTetherAdapter(Guid Id, string Name, ManagedDevice Device)
    {
        public static List<UsbTetherAdapter> Connected()
        {
            var devices = AppSettings.ManagedDeviceList;
            var connected = new List<UsbTetherAdapter>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet
                        || !SharingDescriptions.Any(s => nic.Description.Contains(s, StringComparison.OrdinalIgnoreCase))
                        || nic.GetIPProperties().UnicastAddresses.Count == 0 || !Guid.TryParse(nic.Id, out var id)) continue;
                    var phone = FindPhone(id, devices);
                    if (phone != null) connected.Add(new(id, nic.Name, phone));
                }
                catch (Exception ex) { Trace.TraceWarning("USB 网卡识别失败：" + ex.Message); }
            }
            return connected;
        }

        private static readonly string[] SharingDescriptions = { "RNDIS", "Remote NDIS", "NCM", "Internet Sharing" };

        private static ManagedDevice FindPhone(Guid adapter, IList<ManagedDevice> devices)
        {
            const string networkClass = @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\";
            using var key = Registry.LocalMachine.OpenSubKey(networkClass + adapter.ToString("B") + @"\Connection");
            if (key?.GetValue("PnpInstanceID") is not string instance ||
                CM_Locate_DevNodeW(out uint node, instance, 0) != 0) return null;
            var buffer = new StringBuilder(201);
            for (int depth = 0; depth < 8; depth++)
            {
                if (CM_Get_Device_IDW(node, buffer, buffer.Capacity, 0) != 0) break;
                string serial = PhoneSerial(buffer.ToString());
                if (serial != null) return ManagedDeviceStore.Find(devices, serial);
                if (CM_Get_Parent(out uint parent, node, 0) != 0) break;
                node = parent;
                buffer.Clear();
            }
            return null;
        }

        internal static string PhoneSerial(string instance)
        {
            var parts = instance.Split('\\');
            return parts.Length == 3 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase)
                && parts[1].StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                && parts[1].Contains("&PID_", StringComparison.OrdinalIgnoreCase)
                && !parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase) ? parts[2] : null;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Locate_DevNodeW(out uint node, string instance, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Get_Device_IDW(uint node, StringBuilder buffer, int size, uint flags);
        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    }
}
