using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdbDeviceVitals.Parsing;

namespace AdbDeviceVitals.Services
{
    public sealed class UsbTetherResult
    {
        public bool Completed { get; private set; }
        public bool Succeeded { get; private set; }
        public bool Changed { get; private set; }
        public string Message { get; private set; }

        private UsbTetherResult(bool completed, bool succeeded, bool changed, string message)
        {
            Completed = completed;
            Succeeded = succeeded;
            Changed = changed;
            Message = message ?? string.Empty;
        }

        public static UsbTetherResult Stopped()
        {
            return new UsbTetherResult(false, false, false, string.Empty);
        }

        public static UsbTetherResult AlreadyActive()
        {
            return new UsbTetherResult(true, true, false, "USB 网络共享已开启。");
        }

        public static UsbTetherResult Enabled()
        {
            return new UsbTetherResult(true, true, true, "已开启 USB 网络共享。");
        }

        public static UsbTetherResult Failed(string message)
        {
            return new UsbTetherResult(true, false, false, message);
        }
    }

    public sealed class UsbTetheringService
    {
        private static readonly TimeSpan DiscoveryWindow = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ConfirmInterval = TimeSpan.FromSeconds(2);

        private readonly AdbClient _adb;

        public UsbTetheringService(AdbClient adb)
        {
            _adb = adb;
        }

        public async Task<UsbTetherResult> EnsureAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await EnsureCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return UsbTetherResult.Stopped();
            }
        }

        private async Task<UsbTetherResult> EnsureCoreAsync(CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow + DiscoveryWindow;
            string pendingReason = "未找到已授权的 Android 设备。";

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!AppSettings.AutoEnableUsbTethering) return UsbTetherResult.Stopped();

                if (!_adb.IsAvailable)
                {
                    pendingReason = "找不到 adb.exe。";
                }
                else
                {
                    IList<AdbDevice> devices = await _adb.ListDevicesAsync().ConfigureAwait(false);
                    AdbDevice device = FirstAuthorized(devices);
                    if (device == null)
                    {
                        pendingReason = HasUnauthorized(devices)
                            ? "设备未授权 USB 调试。"
                            : "未找到已授权的 Android 设备。";
                    }
                    else
                    {
                        Probe probe = await ProbeAsync(device.Serial).ConfigureAwait(false);
                        if (probe == Probe.Ready) return UsbTetherResult.AlreadyActive();
                        if (probe == Probe.Inactive)
                        {
                            CommandResult command = await _adb.RunShellAsync(device.Serial, "svc usb setFunctions rndis,adb", 8000).ConfigureAwait(false);
                            return await ConfirmAsync(device.Serial, command, cancellationToken).ConfigureAwait(false);
                        }
                        pendingReason = "无法读取网络共享状态。";
                    }
                }

                if (DateTime.UtcNow >= deadline) return UsbTetherResult.Failed(pendingReason);
                await Task.Delay(DiscoveryInterval, cancellationToken).ConfigureAwait(false);
            }

            return UsbTetherResult.Stopped();
        }

        private async Task<UsbTetherResult> ConfirmAsync(string serial, CommandResult command, CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow + ConfirmWindow;
            while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                if (!AppSettings.AutoEnableUsbTethering) return UsbTetherResult.Stopped();

                IList<AdbDevice> devices = await _adb.ListDevicesAsync().ConfigureAwait(false);
                if (ContainsSerial(devices, serial))
                {
                    Probe probe = await ProbeAsync(serial).ConfigureAwait(false);
                    if (probe == Probe.Ready) return UsbTetherResult.Enabled();
                }

                await Task.Delay(ConfirmInterval, cancellationToken).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested || !AppSettings.AutoEnableUsbTethering)
                return UsbTetherResult.Stopped();
            if (!command.Success && !IsTransportFailure(command))
                return UsbTetherResult.Failed(CommandFailureMessage(command));
            return UsbTetherResult.Failed("USB 功能已切换，但网络共享未进入 TetheredState。");
        }

        private async Task<Probe> ProbeAsync(string serial)
        {
            CommandResult dump = await _adb.RunShellAsync(serial, "dumpsys tethering", 12000).ConfigureAwait(false);
            if (!dump.Success) return Probe.Unreadable;
            return AdbParsers.IsUsbTetheringReady(dump.Output) ? Probe.Ready : Probe.Inactive;
        }

        private static AdbDevice FirstAuthorized(IList<AdbDevice> devices)
        {
            foreach (AdbDevice device in devices)
            {
                if (string.Equals(device.State, "device", StringComparison.OrdinalIgnoreCase)) return device;
            }
            return null;
        }

        private static bool HasUnauthorized(IList<AdbDevice> devices)
        {
            foreach (AdbDevice device in devices)
            {
                if (string.Equals(device.State, "unauthorized", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool ContainsSerial(IList<AdbDevice> devices, string serial)
        {
            foreach (AdbDevice device in devices)
            {
                if (string.Equals(device.State, "device", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(device.Serial, serial, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool IsTransportFailure(CommandResult result)
        {
            if (result.ExitCode == -2) return true;
            string text = (result.Error + " " + result.Output).ToLowerInvariant();
            return text.IndexOf("closed", StringComparison.Ordinal) >= 0
                || text.IndexOf("device offline", StringComparison.Ordinal) >= 0
                || text.IndexOf("device not found", StringComparison.Ordinal) >= 0
                || text.IndexOf("no devices", StringComparison.Ordinal) >= 0
                || text.IndexOf("disconnected", StringComparison.Ordinal) >= 0;
        }

        private static string CommandFailureMessage(CommandResult result)
        {
            string detail = ShortDetail(result.Error);
            if (detail.Length == 0) detail = ShortDetail(result.Output);
            if (detail.Length == 0) return "开启 USB 网络共享失败。";
            return "开启 USB 网络共享失败：" + detail;
        }

        private static string ShortDetail(string value)
        {
            string text = (value ?? string.Empty).Trim();
            int newline = text.IndexOf('\n');
            if (newline >= 0) text = text.Substring(0, newline).Trim();
            if (text.Length > 80) text = text.Substring(0, 80);
            return text;
        }

        private enum Probe
        {
            Ready,
            Inactive,
            Unreadable
        }
    }
}
