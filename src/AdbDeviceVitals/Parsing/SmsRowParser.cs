using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AdbDeviceVitals.Models;

namespace AdbDeviceVitals.Parsing
{
    /// <summary>
    /// 解析 `adb shell content query --uri content://sms/inbox` 的输出。
    ///
    /// 输出形如 `Row: 0 _id=10632, address=10099, date=1793353114426, body=正文`，
    /// 但正文里可能带逗号、等号甚至换行。因此：
    /// 1. 以 `Row: &lt;序号&gt;` 作为记录边界，边界之间的行全部并回上一条正文；
    /// 2. 把 body 放在 --projection 的最后一项，第一个 `body=` 之后的整段都算正文，
    ///    不再按逗号切，正文里有逗号也不会被截断。
    /// </summary>
    public static class SmsRowParser
    {
        private static readonly Regex RowStart = new Regex(@"^Row:\s*\d+\s*", RegexOptions.Compiled);
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static IList<SmsMessage> Parse(string output)
        {
            List<SmsMessage> messages = new List<SmsMessage>();
            if (string.IsNullOrEmpty(output) || IsEmptyResult(output)) return messages;

            string[] lines = output.Replace("\r", string.Empty).Split('\n');
            StringBuilder current = null;
            foreach (string line in lines)
            {
                if (RowStart.IsMatch(line))
                {
                    AddRow(messages, current);
                    current = new StringBuilder(RowStart.Replace(line, string.Empty));
                }
                else if (current != null)
                {
                    // 正文里的真实换行；提交时只裁掉尾部多余空行。
                    current.Append('\n').Append(line);
                }
            }
            AddRow(messages, current);
            return messages;
        }

        /// <summary>content query 查不到行时输出的是这句，不是空字符串。</summary>
        public static bool IsEmptyResult(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return true;
            string text = output.Trim();
            return string.Equals(text, "No result found.", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 部分 ROM 不允许 shell 读短信。这类失败必须显式提示，不能静默当成「没有新短信」。
        /// </summary>
        public static bool IsPermissionDenied(string output, string error)
        {
            string text = (output ?? string.Empty) + "\n" + (error ?? string.Empty);
            return text.IndexOf("Permission Denial", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("READ_SMS", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("SecurityException", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("permission denied", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>列表里只放一行：把换行和连续空白压成单空格。</summary>
        public static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            StringBuilder result = new StringBuilder(text.Length);
            bool pendingSpace = false;
            foreach (char character in text)
            {
                if (char.IsWhiteSpace(character))
                {
                    pendingSpace = true;
                    continue;
                }
                if (pendingSpace && result.Length > 0) result.Append(' ');
                pendingSpace = false;
                result.Append(character);
            }
            return result.ToString();
        }

        public static DateTime ToLocalTime(long dateMs)
        {
            try
            {
                if (dateMs <= 0) return DateTime.MinValue;
                return UnixEpoch.AddMilliseconds(dateMs).ToLocalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.MinValue;
            }
        }

        private static void AddRow(List<SmsMessage> messages, StringBuilder row)
        {
            if (row == null) return;
            string text = row.ToString().TrimEnd('\n');
            int marker = text.IndexOf("body=", StringComparison.Ordinal);
            if (marker < 0) return;

            string prefix = text.Substring(0, marker).Trim().TrimEnd(',').Trim();
            string body = text.Substring(marker + 5);
            long id = 0;
            long dateMs = 0;
            string address = string.Empty;

            foreach (string part in prefix.Split(','))
            {
                string item = part.Trim();
                int equals = item.IndexOf('=');
                if (equals <= 0) continue;
                string key = item.Substring(0, equals).Trim();
                string value = item.Substring(equals + 1).Trim();
                if (string.Equals(key, "_id", StringComparison.Ordinal))
                    long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id);
                else if (string.Equals(key, "date", StringComparison.Ordinal))
                    long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out dateMs);
                else if (string.Equals(key, "address", StringComparison.Ordinal))
                    address = value;
            }

            // 没有 _id 就没法去重，宁可丢这一条也不要反复弹同一条通知。
            if (id <= 0) return;
            messages.Add(new SmsMessage(id, address, body, dateMs, ToLocalTime(dateMs)));
        }
    }
}
