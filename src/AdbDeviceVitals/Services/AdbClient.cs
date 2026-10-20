using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AdbDeviceVitals.Services
{
    public sealed class AdbClient
    {
        private readonly object _gate = new object();
        public string AdbPath { get; private set; }

        public AdbClient()
        {
            AdbPath = FindAdb();
        }

        public void ReloadPath()
        {
            AdbPath = FindAdb();
        }

        public bool IsAvailable
        {
            get { return !string.IsNullOrEmpty(AdbPath) && File.Exists(AdbPath); }
        }

        public async Task<IList<AdbDevice>> ListDevicesAsync()
        {
            DeviceListResult listed = await QueryDevicesAsync().ConfigureAwait(false);
            return listed.Devices;
        }

        public async Task<DeviceListResult> QueryDevicesAsync()
        {
            if (!IsAvailable) return new DeviceListResult(new CommandResult(-1, string.Empty, "adb.exe was not found."), new List<AdbDevice>());
            CommandResult result = await RunAsync("devices -l", 8000).ConfigureAwait(false);
            return new DeviceListResult(result, ParseDevices(result.Output));
        }

        public static IList<AdbDevice> ParseDevices(string output)
        {
            List<AdbDevice> devices = new List<AdbDevice>();
            string[] lines = (output ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
                Match match = Regex.Match(line, @"^(\S+)\s+(device|unauthorized|offline)(?:\s+(.*))?$");
                if (!match.Success) continue;
                string details = match.Groups[3].Value;
                Match model = Regex.Match(details, @"(?:^|\s)model:(\S+)");
                devices.Add(new AdbDevice(match.Groups[1].Value, match.Groups[2].Value, model.Success ? model.Groups[1].Value : string.Empty));
            }
            return devices;
        }

        public Task<CommandResult> RunShellAsync(string serial, string command, int timeoutMs)
        {
            if (!Regex.IsMatch(serial ?? string.Empty, @"^[A-Za-z0-9._:-]+$"))
                throw new ArgumentException("Invalid device serial.", "serial");
            return RunAsync("-s " + Quote(serial) + " shell " + command, timeoutMs);
        }

        public Task<CommandResult> RunAsync(string arguments, int timeoutMs)
        {
            return Task.Run(delegate { return RunWithRecovery(arguments, timeoutMs); });
        }

        private CommandResult RunWithRecovery(string arguments, int timeoutMs)
        {
            lock (_gate)
            {
                CommandResult result = Execute(arguments, timeoutMs);
                if (IsStartServer(arguments) || !IsDaemonFault(result)) return result;

                TetherLog log = new TetherLog();
                log.Write("daemon fault adb=" + AdbPath + " args=" + arguments + " exit=" + result.ExitCode + " " + Brief(result));
                Execute("start-server", 10000);
                Thread.Sleep(400);
                result = Execute(arguments, timeoutMs);
                if (!IsDaemonFault(result))
                {
                    log.Write("daemon retry ok args=" + arguments);
                    return result;
                }

                Thread.Sleep(800);
                result = Execute(arguments, timeoutMs);
                log.Write("daemon retry exit=" + result.ExitCode + " " + Brief(result));
                return result;
            }
        }

        private CommandResult Execute(string arguments, int timeoutMs)
        {
            if (!IsAvailable) return new CommandResult(-1, string.Empty, "adb.exe was not found.");
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = AdbPath;
            start.Arguments = arguments;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;

            using (Process process = Process.Start(start))
            {
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    return new CommandResult(-2, outputTask.Result, "ADB command timed out.");
                }
                Task.WaitAll(outputTask, errorTask);
                return new CommandResult(process.ExitCode, outputTask.Result, errorTask.Result);
            }
        }

        private static bool IsStartServer(string arguments)
        {
            return (arguments ?? string.Empty).IndexOf("start-server", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsDaemonFault(CommandResult result)
        {
            if (result != null && result.ExitCode == 0) return false;
            string text = ((result == null ? string.Empty : result.Error) + "\n" + (result == null ? string.Empty : result.Output)).ToLowerInvariant();
            if (text.IndexOf("daemon started successfully", StringComparison.Ordinal) >= 0) return false;
            return text.IndexOf("protocol fault", StringComparison.Ordinal) >= 0
                || text.IndexOf("connection reset", StringComparison.Ordinal) >= 0
                || text.IndexOf("cannot connect to daemon", StringComparison.Ordinal) >= 0
                || text.IndexOf("failed to check server version", StringComparison.Ordinal) >= 0;
        }

        private static string Brief(CommandResult result)
        {
            string text = ((result.Error ?? string.Empty) + " " + (result.Output ?? string.Empty)).Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 180) text = text.Substring(0, 180);
            return text;
        }

        private static string FindAdb()
        {
            List<string> candidates = new List<string>();
            string configuredDirectory = AppSettings.AdbDirectory;
            if (!string.IsNullOrWhiteSpace(configuredDirectory))
                candidates.Add(Path.Combine(configuredDirectory, "adb.exe"));
            candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe"));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local)) candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(directory))
                    candidates.Add(Path.Combine(directory.Trim().Trim('"'), "adb.exe"));
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch { }
            }
            return string.Empty;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }

    public sealed class DeviceListResult
    {
        public DeviceListResult(CommandResult command, IList<AdbDevice> devices)
        {
            Command = command;
            Devices = devices ?? new List<AdbDevice>();
        }

        public CommandResult Command { get; private set; }
        public IList<AdbDevice> Devices { get; private set; }
    }

    public sealed class AdbDevice
    {
        public string Serial { get; private set; }
        public string State { get; private set; }
        public string Model { get; private set; }

        public AdbDevice(string serial, string state, string model)
        {
            Serial = serial;
            State = state;
            Model = model;
        }
    }

    public sealed class CommandResult
    {
        public int ExitCode { get; private set; }
        public string Output { get; private set; }
        public string Error { get; private set; }

        public CommandResult(int exitCode, string output, string error)
        {
            ExitCode = exitCode;
            Output = output ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public bool Success { get { return ExitCode == 0; } }
    }
}

