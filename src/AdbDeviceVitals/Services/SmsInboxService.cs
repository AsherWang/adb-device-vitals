using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using AdbDeviceVitals.Models;
using AdbDeviceVitals.Parsing;

namespace AdbDeviceVitals.Services
{
    /// <summary>
    /// 本次运行期间的新短信。启动时读一次手机时钟作为基线，之前的短信一行都不读；
    /// 之后按 10 秒间隔只查 `date &gt; 基线` 的收件箱记录，按 _id 去重后留在内存里。
    ///
    /// 短信是事件流，不进 DeviceSnapshot / CSV / 图表；不落盘、不上传、不复制。
    /// 程序退出、设备切换、或者用户在设置里重新打开开关，都会重新建立基线。
    /// </summary>
    public sealed class SmsInboxService
    {
        public const int MaxMessages = 100;

        /// <summary>不需要实时推送，10 秒查一次足够看验证码，也不会给 ADB 添太多命令。</summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        /// <summary>游标往前推时留 2 秒重叠，配合 _id 去重，避免同一毫秒的多条被跳过。</summary>
        private const long OverlapMs = 2000;

        public const string PermissionMessage =
            "无法读取短信：手机拒绝了 ADB 的短信读取权限。部分系统需要在开发者选项里打开「USB 调试（安全设置）」，或执行 adb shell appops set com.android.shell READ_SMS allow。";

        private readonly AdbClient _adb;
        private readonly object _gate = new object();
        private readonly List<SmsMessage> _messages = new List<SmsMessage>();
        private readonly HashSet<long> _seen = new HashSet<long>();
        private string _serial = string.Empty;
        private long _baselineMs;
        private DateTime _lastPollUtc = DateTime.MinValue;
        private bool _running;
        private string _lastError = string.Empty;

        public SmsInboxService(AdbClient adb)
        {
            _adb = adb;
        }

        public int Count
        {
            get { lock (_gate) { return _messages.Count; } }
        }

        public bool IsListening
        {
            get { lock (_gate) { return _baselineMs > 0; } }
        }

        public string LastError
        {
            get { lock (_gate) { return _lastError; } }
        }

        /// <summary>列表窗口按「最新在上」渲染，这里返回收到的先后顺序。</summary>
        public SmsMessage[] Snapshot()
        {
            lock (_gate) { return _messages.ToArray(); }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _messages.Clear();
                _seen.Clear();
            }
        }

        /// <summary>开关重新打开时调用：关闭期间到达的短信不补报，已收到的列表保留。</summary>
        public void ResetBaseline()
        {
            lock (_gate)
            {
                _baselineMs = 0;
                _lastPollUtc = DateTime.MinValue;
                _lastError = string.Empty;
            }
        }

        /// <summary>
        /// 每 2.5 秒由主窗口喂一次，节流和并发保护都在这里。返回的新消息由界面决定怎么提示。
        /// </summary>
        public Task<SmsPollResult> PollAsync(string serial, bool enabled, DateTime utcNow)
        {
            if (!enabled) return Task.FromResult(SmsPollResult.Off());
            if (string.IsNullOrEmpty(serial)) return Task.FromResult(SmsPollResult.Empty(false, 0));

            lock (_gate)
            {
                if (_running) return Task.FromResult(SmsPollResult.Empty(_baselineMs > 0, _messages.Count));

                if (!string.Equals(_serial, serial, StringComparison.Ordinal))
                {
                    // 换了一台手机：重新建立基线，也不把上一台手机的短信混进列表。
                    _serial = serial;
                    _baselineMs = 0;
                    _messages.Clear();
                    _seen.Clear();
                    _lastError = string.Empty;
                }

                if (_baselineMs > 0 && utcNow - _lastPollUtc < PollInterval)
                    return Task.FromResult(SmsPollResult.Empty(true, _messages.Count));

                _running = true;
                _lastPollUtc = utcNow;
            }

            return Task.Run(delegate { return PollCore(serial); });
        }

        private SmsPollResult PollCore(string serial)
        {
            try
            {
                long baseline;
                lock (_gate) { baseline = _baselineMs; }

                if (baseline <= 0)
                {
                    CommandResult clock = _adb.RunShellAsync(serial, "date +%s", 5000).Result;
                    long seconds;
                    if (!clock.Success || !long.TryParse(clock.Output.Trim(), NumberStyles.None,
                        CultureInfo.InvariantCulture, out seconds) || seconds <= 0)
                    {
                        return Fail("无法读取手机时钟，短信监听暂时不可用。", false);
                    }

                    long start = seconds * 1000L;
                    lock (_gate) { _baselineMs = start; }
                    // 基线建立这一轮故意不查询：启动之前收到的短信一条都不看。
                    return SmsPollResult.Empty(true, 0);
                }

                // body 必须放最后，正文里出现逗号时也不会被切错。
                // where 里的单引号由设备端 shell 解析，Windows 侧不会把 > 当成重定向。
                string command = "content query --uri content://sms/inbox"
                    + " --projection _id:address:date:body"
                    + " --where 'date>" + baseline.ToString(CultureInfo.InvariantCulture) + "'"
                    + " --sort '_id DESC'";
                CommandResult result = _adb.RunShellAsync(serial, command, 10000).Result;
                string output = result.Output ?? string.Empty;
                string error = result.Error ?? string.Empty;

                if (SmsRowParser.IsPermissionDenied(output, error))
                    return Fail(PermissionMessage, true);
                if (!result.Success)
                    return Fail("读取短信失败：" + Brief(error.Length > 0 ? error : output), false);

                IList<SmsMessage> parsed = SmsRowParser.Parse(output);
                List<SmsMessage> fresh = new List<SmsMessage>();
                long maxDate = baseline;
                int total;
                lock (_gate)
                {
                    foreach (SmsMessage message in parsed)
                    {
                        if (message.DateMs > maxDate) maxDate = message.DateMs;
                        if (_seen.Add(message.Id)) fresh.Add(message);
                    }

                    // 游标只前进不后退，留 2 秒重叠窗口；重叠带里的旧 _id 已被 _seen 挡掉。
                    long next = Math.Max(baseline, maxDate - OverlapMs);
                    _baselineMs = next;

                    foreach (SmsMessage message in fresh) _messages.Add(message);
                    while (_messages.Count > MaxMessages)
                    {
                        _seen.Remove(_messages[0].Id);
                        _messages.RemoveAt(0);
                    }
                    total = _messages.Count;
                    _lastError = string.Empty;
                }
                return SmsPollResult.Received(fresh, total);
            }
            catch (Exception ex)
            {
                return Fail("读取短信失败：" + ex.Message, false);
            }
            finally
            {
                lock (_gate) { _running = false; }
            }
        }

        private SmsPollResult Fail(string message, bool permissionDenied)
        {
            lock (_gate) { _lastError = message; }
            return SmsPollResult.Failed(message, permissionDenied);
        }

        private static string Brief(string text)
        {
            string value = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length > 160 ? value.Substring(0, 160) : value;
        }
    }

    public sealed class SmsPollResult
    {
        private SmsPollResult(IList<SmsMessage> messages, string error, bool permissionDenied,
            bool disabled, bool listening, int total)
        {
            NewMessages = messages ?? new List<SmsMessage>();
            Error = error ?? string.Empty;
            PermissionDenied = permissionDenied;
            Disabled = disabled;
            Listening = listening;
            TotalCount = total;
        }

        public IList<SmsMessage> NewMessages { get; private set; }
        public string Error { get; private set; }
        public bool PermissionDenied { get; private set; }
        public bool Disabled { get; private set; }
        public bool Listening { get; private set; }
        public int TotalCount { get; private set; }

        public static SmsPollResult Empty(bool listening, int total)
        {
            return new SmsPollResult(null, string.Empty, false, false, listening, total);
        }

        public static SmsPollResult Off()
        {
            return new SmsPollResult(null, string.Empty, false, true, false, 0);
        }

        public static SmsPollResult Failed(string error, bool permissionDenied)
        {
            return new SmsPollResult(null, error, permissionDenied, false, false, 0);
        }

        public static SmsPollResult Received(IList<SmsMessage> messages, int total)
        {
            return new SmsPollResult(messages, string.Empty, false, false, true, total);
        }
    }
}
