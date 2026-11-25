using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AdbDeviceVitals.Models;

namespace AdbDeviceVitals.Services
{
    internal sealed class AutoScreenOffService : IDisposable
    {
        private readonly AdbClient _adb;
        private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(15));
        private volatile bool _stopping;

        public AutoScreenOffService(AdbClient adb)
        {
            _adb = adb;
            _ = Task.Run(MonitorAsync);
        }

        private async Task MonitorAsync()
        {
            do
            {
                try
                {
                    var enabled = AppSettings.ManagedDeviceList.Where(d => d.AutoScreenOff)
                        .Select(d => d.Serial).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (_stopping || enabled.Count == 0 || !_adb.IsAvailable) continue;
                    var online = await _adb.ListDevicesAsync().ConfigureAwait(false);
                    await Task.WhenAll(online.Where(d => DeviceSelection.IsAuthorized(d) && enabled.Contains(d.Serial))
                        .Select(d => CheckAsync(d.Serial))).ConfigureAwait(false);
                }
                catch (Exception ex) { Trace.TraceWarning("自动息屏检查失败：" + ex.Message); }
            } while (await _timer.WaitForNextTickAsync().ConfigureAwait(false));
        }

        private async Task CheckAsync(string serial)
        {
            var power = await _adb.RunShellAsync(serial, "dumpsys power", 4000).ConfigureAwait(false);
            if (!power.Success || !ShouldSleep(power.Output) || _stopping
                || ManagedDeviceStore.Find(AppSettings.ManagedDeviceList, serial)?.AutoScreenOff != true) return;

            // KEYCODE_SLEEP is one-way. A phone that went to sleep during the check stays asleep.
            var sleep = await _adb.RunShellAsync(serial, "input keyevent 223", 3000).ConfigureAwait(false);
            if (!sleep.Success) Trace.TraceWarning("自动息屏失败：" + serial + " " + sleep.Error);
        }

        private static readonly Regex Awake = new(@"(?m)^[ \t]*mWakefulness=Awake[ \t]*\r?$");
        private static readonly Regex TimeField = new(
            @"^[ \t]*m?(LastWakeTime|LastUserActivityTime|LastUserActivityTimeNoChangeLights)=([^\r\n]*)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex Age = new(@"^\d+[ \t]+\((\d+) ms ago\)[ \t]*$");

        internal static bool ShouldSleep(string dump)
        {
            if (string.IsNullOrEmpty(dump) || !Awake.IsMatch(dump) || dump.Contains("mWakefulnessChanging=true")) return false;
            // Android reports these ages against its own uptime. Check every reported
            // display group and both activity clocks, including touches that leave brightness unchanged.
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match field in TimeField.Matches(dump))
            {
                var age = Age.Match(field.Groups[2].Value);
                if (!age.Success || !long.TryParse(age.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out long ms) || ms < 300_000) return false;
                fields.Add(field.Groups[1].Value);
            }
            return fields.Count == 3;
        }

        public void Dispose()
        {
            _stopping = true;
            _timer.Dispose();
        }
    }
}
