using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        public static UsbTetherResult Disabled()
        {
            return new UsbTetherResult(true, true, true, "已关闭 USB 网络共享。");
        }

        public static UsbTetherResult AlreadyInactive()
        {
            return new UsbTetherResult(true, true, false, "USB 网络共享已关闭。");
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
        private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan ConfirmInterval = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan UnchangedFunctionLimit = TimeSpan.FromSeconds(12);
        private const string EnableFunctionCommand = "svc usb setFunctions rndis";
        private const string DisableFunctionCommand = "svc usb setFunctions mtp";
        private const string FunctionStateCommand = "getprop sys.usb.state";
        private const string TetheringDumpCommand = "dumpsys tethering";

        private readonly AdbClient _adb;

        public UsbTetheringService(AdbClient adb)
        {
            _adb = adb;
        }

        public async Task<UsbTetherResult> EnsureAsync(CancellationToken cancellationToken)
        {
            TetherLog log = new TetherLog();
            log.Write("auto-enable start");
            try
            {
                UsbTetherResult result = await EnsureCoreAsync(cancellationToken, log).ConfigureAwait(false);
                log.Write("auto-enable result " + result.Message);
                return result;
            }
            catch (OperationCanceledException)
            {
                log.Write("auto-enable cancelled");
                return UsbTetherResult.Stopped();
            }
        }

        public async Task<UsbTetherResult> SetEnabledAsync(bool enable, CancellationToken cancellationToken)
        {
            TetherLog log = new TetherLog();
            log.Write("manual " + (enable ? "enable" : "disable") + " log=" + log.FilePath);
            try
            {
                UsbTetherResult result = await SetEnabledCoreAsync(enable, cancellationToken, log).ConfigureAwait(false);
                log.Write("manual result " + result.Message);
                return result;
            }
            catch (OperationCanceledException)
            {
                log.Write("manual cancelled");
                return UsbTetherResult.Stopped();
            }
            catch (Exception ex)
            {
                log.Write("manual exception " + ex);
                return UsbTetherResult.Failed(ex.Message);
            }
        }

        private async Task<UsbTetherResult> EnsureCoreAsync(CancellationToken cancellationToken, TetherLog log)
        {
            DateTime deadline = DateTime.UtcNow + DiscoveryWindow;
            string pendingReason = "未找到已授权的 Android 设备。";

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!AppSettings.AutoEnableUsbTethering)
                {
                    log.Write("auto-enable setting off");
                    return UsbTetherResult.Stopped();
                }

                if (!_adb.IsAvailable)
                {
                    pendingReason = "找不到 adb.exe。";
                    log.Write(pendingReason);
                }
                else
                {
                    IList<AdbDevice> devices = await ListLoggedAsync(log).ConfigureAwait(false);
                    AdbDevice device = FirstAuthorized(devices);
                    if (device == null)
                    {
                        pendingReason = HasUnauthorized(devices)
                            ? "设备未授权 USB 调试。"
                            : "未找到已授权的 Android 设备。";
                        log.Write(pendingReason);
                    }
                    else
                    {
                        ProbeSnapshot probe = await ProbeAsync(device.Serial, log).ConfigureAwait(false);
                        if (probe.State == Probe.Ready) return UsbTetherResult.AlreadyActive();
                        if (probe.State == Probe.Inactive)
                        {
                            CommandResult command = await RunLoggedAsync(log, device.Serial, EnableFunctionCommand, 8000).ConfigureAwait(false);
                            return await ConfirmAsync(device.Serial, command, cancellationToken, true, true, log).ConfigureAwait(false);
                        }
                        pendingReason = "无法读取网络共享状态。";
                        log.Write(pendingReason);
                    }
                }

                if (DateTime.UtcNow >= deadline) return UsbTetherResult.Failed(pendingReason);
                await Task.Delay(DiscoveryInterval, cancellationToken).ConfigureAwait(false);
            }

            return UsbTetherResult.Stopped();
        }

        private async Task<UsbTetherResult> SetEnabledCoreAsync(bool enable, CancellationToken cancellationToken, TetherLog log)
        {
            if (!_adb.IsAvailable) return UsbTetherResult.Failed("找不到 adb.exe。");
            IList<AdbDevice> devices = await ListLoggedAsync(log).ConfigureAwait(false);
            AdbDevice device = FirstAuthorized(devices);
            if (device == null)
            {
                return UsbTetherResult.Failed(HasUnauthorized(devices)
                    ? "设备未授权 USB 调试。"
                    : "未找到已授权的 Android 设备。");
            }

            log.Write("device " + device.Serial + " " + device.Model);
            ProbeSnapshot probe = await ProbeAsync(device.Serial, log).ConfigureAwait(false);
            if (enable && probe.State == Probe.Ready) return UsbTetherResult.AlreadyActive();
            if (!enable && probe.State == Probe.Inactive) return UsbTetherResult.AlreadyInactive();

            string command = enable ? EnableFunctionCommand : DisableFunctionCommand;
            CommandResult result = await RunLoggedAsync(log, device.Serial, command, 8000).ConfigureAwait(false);
            return await ConfirmAsync(device.Serial, result, cancellationToken, enable, false, log).ConfigureAwait(false);
        }

        private async Task<UsbTetherResult> ConfirmAsync(string serial, CommandResult command, CancellationToken cancellationToken, bool enable, bool stopWhenSettingOff, TetherLog log)
        {
            DateTime deadline = DateTime.UtcNow + ConfirmWindow;
            DateTime unchangedSince = DateTime.MinValue;
            while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                if (stopWhenSettingOff && !AppSettings.AutoEnableUsbTethering) return UsbTetherResult.Stopped();

                IList<AdbDevice> devices = await ListLoggedAsync(log).ConfigureAwait(false);
                if (ContainsSerial(devices, serial))
                {
                    string functions = await ReadFunctionsAsync(serial, log).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(functions))
                    {
                        log.Write("functions empty");
                    }
                    else if (enable == HasTetherFunction(functions))
                    {
                        unchangedSince = DateTime.MinValue;
                        ProbeSnapshot probe = await ReadTetherStateAsync(serial, functions, log).ConfigureAwait(false);
                        if (enable && probe.State == Probe.Ready) return UsbTetherResult.Enabled();
                        if (!enable && probe.State == Probe.Inactive) return UsbTetherResult.Disabled();
                    }
                    else
                    {
                        if (unchangedSince == DateTime.MinValue) unchangedSince = DateTime.UtcNow;
                        else if (DateTime.UtcNow - unchangedSince >= UnchangedFunctionLimit)
                            return UsbTetherResult.Failed("USB 功能没有切换，仍是 " + functions + "。");
                    }
                }
                else
                {
                    unchangedSince = DateTime.MinValue;
                    log.Write("device " + serial + " not online");
                }

                await Task.Delay(ConfirmInterval, cancellationToken).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested || (stopWhenSettingOff && !AppSettings.AutoEnableUsbTethering))
                return UsbTetherResult.Stopped();
            if (enable && (command.Success || IsTransportFailure(command) || IsUsbFunctionChangeAcknowledged(command.Output, command.Error)))
                return UsbTetherResult.Failed("USB 功能已切换，但网络共享未进入 TetheredState。");
            if (!enable)
                return UsbTetherResult.Failed("已切换 USB 功能，但网络共享仍处于 TetheredState。");
            return UsbTetherResult.Failed(CommandFailureMessage(command));
        }

        private async Task<IList<AdbDevice>> ListLoggedAsync(TetherLog log)
        {
            IList<AdbDevice> devices = await _adb.ListDevicesAsync().ConfigureAwait(false);
            if (devices.Count == 0) log.Write("devices empty");
            foreach (AdbDevice device in devices)
                log.Write("list " + device.Serial + " state=" + device.State + " model=" + device.Model);
            return devices;
        }

        private async Task<CommandResult> RunLoggedAsync(TetherLog log, string serial, string command, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            CommandResult result = await _adb.RunShellAsync(serial, command, timeoutMs).ConfigureAwait(false);
            watch.Stop();
            log.Command(command, result, watch.ElapsedMilliseconds);
            return result;
        }

        private async Task<ProbeSnapshot> ProbeAsync(string serial, TetherLog log)
        {
            string functions = await ReadFunctionsAsync(serial, log).ConfigureAwait(false);
            return await ReadTetherStateAsync(serial, functions, log).ConfigureAwait(false);
        }

        private async Task<string> ReadFunctionsAsync(string serial, TetherLog log)
        {
            CommandResult functions = await RunLoggedAsync(log, serial, FunctionStateCommand, 3000).ConfigureAwait(false);
            return OneLine(CombinedOutput(functions));
        }

        private async Task<ProbeSnapshot> ReadTetherStateAsync(string serial, string functions, TetherLog log)
        {
            CommandResult dump = await RunLoggedAsync(log, serial, TetheringDumpCommand, 12000).ConfigureAwait(false);
            string text = CombinedOutput(dump);
            Probe probe = AdbParsers.IsUsbTetheringReady(text) ? Probe.Ready : (dump.Success ? Probe.Inactive : Probe.Unreadable);
            log.Write("probe " + probe + " functions=" + functions);
            return new ProbeSnapshot(probe, functions);
        }

        private static bool HasTetherFunction(string functions)
        {
            string[] parts = (functions ?? string.Empty).Split(',');
            foreach (string part in parts)
            {
                string token = part.Trim();
                if (token.Equals("rndis", StringComparison.OrdinalIgnoreCase) || token.Equals("ncm", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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

        public static bool IsUsbFunctionChangeAcknowledged(string output, string error)
        {
            string text = CombinedText(output, error);
            if (text.IndexOf("setCurrentFunctions opId:", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (text.IndexOf("Error communicating", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (text.IndexOf("Unknown command", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private static bool IsTransportFailure(CommandResult result)
        {
            if (result.ExitCode == -2 || result.ExitCode == 137 || result.ExitCode == 255) return true;
            string text = CombinedOutput(result).ToLowerInvariant();
            return text.IndexOf("closed", StringComparison.Ordinal) >= 0
                || text.IndexOf("device offline", StringComparison.Ordinal) >= 0
                || text.IndexOf("device not found", StringComparison.Ordinal) >= 0
                || text.IndexOf("no devices", StringComparison.Ordinal) >= 0
                || text.IndexOf("disconnected", StringComparison.Ordinal) >= 0;
        }

        private static string CombinedOutput(CommandResult result)
        {
            return result == null ? string.Empty : CombinedText(result.Output, result.Error);
        }

        private static string CombinedText(string output, string error)
        {
            return (output ?? string.Empty) + "\n" + (error ?? string.Empty);
        }

        private static string CommandFailureMessage(CommandResult result)
        {
            string detail = ShortDetail(result.Error);
            if (detail.Length == 0) detail = ShortDetail(result.Output);
            if (detail.Length == 0) return "开启 USB 网络共享失败。";
            return "开启 USB 网络共享失败：" + detail;
        }

        private static string OneLine(string value)
        {
            string text = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 120) text = text.Substring(0, 120);
            return text;
        }

        private static string ShortDetail(string value)
        {
            string text = (value ?? string.Empty).Trim();
            int newline = text.IndexOf('\n');
            if (newline >= 0) text = text.Substring(0, newline).Trim();
            if (text.StartsWith("setCurrentFunctions opId:", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (text.Length > 80) text = text.Substring(0, 80);
            return text;
        }

        private enum Probe
        {
            Ready,
            Inactive,
            Unreadable
        }

        private sealed class ProbeSnapshot
        {
            public ProbeSnapshot(Probe state, string functions)
            {
                State = state;
                Functions = functions ?? string.Empty;
            }

            public Probe State { get; private set; }
            public string Functions { get; private set; }
        }
    }
}
