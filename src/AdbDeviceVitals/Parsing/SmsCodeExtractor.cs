using System;
using System.Text.RegularExpressions;

namespace AdbDeviceVitals.Parsing
{
    /// <summary>
    /// 从短信正文里找验证码，只返回码本身，不做任何展示或复制。
    /// 纯函数，方便用构造样本覆盖中文/英文/纯数字三种常见格式。
    /// </summary>
    public static class SmsCodeExtractor
    {
        private static readonly Regex KeywordCode = new Regex(
            @"(?:验证码|校验码|动态码|动态口令|动态密码|一次性密码|口令|code|otp|pin)[^\d]{0,12}?(\d{4,8})(?!\d)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex StandaloneNumber = new Regex(
            @"(?<!\d)(\d{4,6})(?!\d)", RegexOptions.Compiled);

        // 余额、订单号、日期这类数字不该被当成验证码。
        private static readonly Regex MoneyOrDate = new Regex(
            @"余额|金额|订单|快递|日期|账单|积分|元|¥|￥|\d{4}[-/.年]\d{1,2}",
            RegexOptions.Compiled);

        public static string Extract(string body)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;

            Match keyword = KeywordCode.Match(body);
            if (keyword.Success) return keyword.Groups[1].Value;

            if (MoneyOrDate.IsMatch(body)) return string.Empty;

            // 没有任何提示语时，只在全文恰好出现一个独立数字块时才认，避免把多个数字串误判。
            MatchCollection numbers = StandaloneNumber.Matches(body);
            return numbers.Count == 1 ? numbers[0].Groups[1].Value : string.Empty;
        }

        public static bool HasCode(string body)
        {
            return Extract(body).Length > 0;
        }
    }
}
