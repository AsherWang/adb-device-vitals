using System;
using System.Collections.Generic;
using AdbDeviceVitals.Models;

namespace AdbDeviceVitals.Parsing
{
    /// <summary>
    /// 发件人过滤：只决定「要不要弹提醒」，不决定收不收短信，列表始终保留全部消息。
    /// 空过滤串表示全部提醒；多个关键字用逗号、分号、竖线或空格分隔，命中任意一个即可。
    /// </summary>
    public static class SmsSenderFilter
    {
        private static readonly char[] Separators = new char[]
        {
            ',', '，', ';', '；', '|', ' ', '\t', '\r', '\n'
        };

        public static string[] Keywords(string filter)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(filter)) return result.ToArray();
            foreach (string part in filter.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string keyword = part.Trim().Trim('"', '\'').Trim();
                if (keyword.Length > 0) result.Add(keyword);
            }
            return result.ToArray();
        }

        public static bool Matches(string filter, string address)
        {
            string[] keywords = Keywords(filter);
            if (keywords.Length == 0) return true;
            if (string.IsNullOrEmpty(address)) return false;
            foreach (string keyword in keywords)
            {
                if (address.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>一批新消息里只要有一条来自关注的发件人，就值得提醒一次。</summary>
        public static bool ShouldNotify(string filter, IList<SmsMessage> messages)
        {
            if (messages == null || messages.Count == 0) return false;
            foreach (SmsMessage message in messages)
            {
                if (message != null && Matches(filter, message.Address)) return true;
            }
            return false;
        }
    }
}
