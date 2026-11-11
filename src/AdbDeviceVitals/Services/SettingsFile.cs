using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AdbDeviceVitals.Services
{
    /// <summary>程序的用户配置，字段名与旧注册表值名保持一致，迁移时不需要做映射。</summary>
    public sealed class SettingsData
    {
        public SettingsData()
        {
            Schema = 1;
            AdbDirectory = string.Empty;
            TargetDeviceSerial = string.Empty;
            SmsSenderFilter = string.Empty;
            // 读短信属于需要显式授权的能力：默认关闭，旧配置文件缺这个字段也按关闭处理。
            SmsSyncEnabled = false;
            // Windows 通知在部分系统上不可用，失败时会自动回退到托盘气泡。
            SmsToastEnabled = true;
        }

        public int Schema;
        public string AdbDirectory;
        public string TargetDeviceSerial;
        public bool RunAtStartup;
        public bool AutoEnableUsbTethering;
        public bool AutoStartPcHotspot;
        public bool AutoRecoverUsbTethering;
        public bool SmsSyncEnabled;
        public bool SmsToastEnabled;
        public string SmsSenderFilter;
        public DateTime? LastUpdateCheckUtc;

        public SettingsData Copy()
        {
            return new SettingsData
            {
                Schema = Schema,
                AdbDirectory = AdbDirectory,
                TargetDeviceSerial = TargetDeviceSerial,
                RunAtStartup = RunAtStartup,
                AutoEnableUsbTethering = AutoEnableUsbTethering,
                AutoStartPcHotspot = AutoStartPcHotspot,
                AutoRecoverUsbTethering = AutoRecoverUsbTethering,
                SmsSyncEnabled = SmsSyncEnabled,
                SmsToastEnabled = SmsToastEnabled,
                SmsSenderFilter = SmsSenderFilter,
                LastUpdateCheckUtc = LastUpdateCheckUtc
            };
        }
    }

    /// <summary>
    /// settings.json 的编解码。只处理本程序的固定字段，用正则逐项提取，不引入 JSON 库：
    /// 项目靠 Windows 自带的 csc 编译，不能为了一个配置文件增加程序集引用。
    /// 解析是宽容的——未知字段忽略，写坏的字段跳过，坏一项不至于丢掉整份配置。
    /// </summary>
    public static class SettingsFile
    {
        private static readonly Regex StringPair = new Regex(
            @"""(?<key>[A-Za-z0-9_]+)""\s*:\s*""(?<value>(?:\\.|[^""\\])*)""",
            RegexOptions.Compiled);

        private static readonly Regex BoolPair = new Regex(
            @"""(?<key>[A-Za-z0-9_]+)""\s*:\s*(?<value>true|false)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SchemaPair = new Regex(
            @"""schema""\s*:\s*(?<value>\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static SettingsData Parse(string text)
        {
            SettingsData data = new SettingsData();
            if (string.IsNullOrEmpty(text)) return data;

            Match schema = SchemaPair.Match(text);
            int schemaValue;
            if (schema.Success && int.TryParse(schema.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out schemaValue))
                data.Schema = schemaValue > 0 ? schemaValue : 1;

            foreach (Match match in BoolPair.Matches(text))
                ApplyBoolean(data, match.Groups["key"].Value,
                    string.Equals(match.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase));

            foreach (Match match in StringPair.Matches(text))
                ApplyString(data, match.Groups["key"].Value, Unescape(match.Groups["value"].Value));

            return data;
        }

        public static string Serialize(SettingsData value)
        {
            SettingsData data = value ?? new SettingsData();
            StringBuilder text = new StringBuilder();
            text.AppendLine("{");
            text.AppendLine("  \"schema\": " + (data.Schema > 0 ? data.Schema : 1).ToString(CultureInfo.InvariantCulture) + ",");
            text.AppendLine("  \"AdbDirectory\": \"" + Escape(data.AdbDirectory) + "\",");
            text.AppendLine("  \"TargetDeviceSerial\": \"" + Escape(data.TargetDeviceSerial) + "\",");
            text.AppendLine("  \"RunAtStartup\": " + BooleanText(data.RunAtStartup) + ",");
            text.AppendLine("  \"AutoEnableUsbTethering\": " + BooleanText(data.AutoEnableUsbTethering) + ",");
            text.AppendLine("  \"AutoStartPcHotspot\": " + BooleanText(data.AutoStartPcHotspot) + ",");
            text.AppendLine("  \"AutoRecoverUsbTethering\": " + BooleanText(data.AutoRecoverUsbTethering) + ",");
            text.AppendLine("  \"SmsSyncEnabled\": " + BooleanText(data.SmsSyncEnabled) + ",");
            text.AppendLine("  \"SmsToastEnabled\": " + BooleanText(data.SmsToastEnabled) + ",");
            text.AppendLine("  \"SmsSenderFilter\": \"" + Escape(data.SmsSenderFilter) + "\",");
            text.AppendLine("  \"LastUpdateCheckUtc\": " + (data.LastUpdateCheckUtc.HasValue
                ? "\"" + data.LastUpdateCheckUtc.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "\""
                : "null"));
            text.AppendLine("}");
            return text.ToString();
        }

        private static void ApplyBoolean(SettingsData data, string key, bool value)
        {
            if (string.Equals(key, "RunAtStartup", StringComparison.OrdinalIgnoreCase)) data.RunAtStartup = value;
            else if (string.Equals(key, "AutoEnableUsbTethering", StringComparison.OrdinalIgnoreCase)) data.AutoEnableUsbTethering = value;
            else if (string.Equals(key, "AutoStartPcHotspot", StringComparison.OrdinalIgnoreCase)) data.AutoStartPcHotspot = value;
            else if (string.Equals(key, "AutoRecoverUsbTethering", StringComparison.OrdinalIgnoreCase)) data.AutoRecoverUsbTethering = value;
            else if (string.Equals(key, "SmsSyncEnabled", StringComparison.OrdinalIgnoreCase)) data.SmsSyncEnabled = value;
            else if (string.Equals(key, "SmsToastEnabled", StringComparison.OrdinalIgnoreCase)) data.SmsToastEnabled = value;
        }

        private static void ApplyString(SettingsData data, string key, string value)
        {
            if (string.Equals(key, "AdbDirectory", StringComparison.OrdinalIgnoreCase))
            {
                data.AdbDirectory = (value ?? string.Empty).Trim();
            }
            else if (string.Equals(key, "TargetDeviceSerial", StringComparison.OrdinalIgnoreCase))
            {
                data.TargetDeviceSerial = (value ?? string.Empty).Trim();
            }
            else if (string.Equals(key, "SmsSenderFilter", StringComparison.OrdinalIgnoreCase))
            {
                data.SmsSenderFilter = (value ?? string.Empty).Trim();
            }
            else if (string.Equals(key, "LastUpdateCheckUtc", StringComparison.OrdinalIgnoreCase))
            {
                DateTime parsed;
                if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
                    data.LastUpdateCheckUtc = parsed.ToUniversalTime();
            }
        }

        private static string BooleanText(bool value)
        {
            return value ? "true" : "false";
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            StringBuilder text = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\b': text.Append("\\b"); break;
                    case '\f': text.Append("\\f"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (character < ' ')
                            text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            text.Append(character);
                        break;
                }
            }
            return text.ToString();
        }

        private static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            StringBuilder text = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (character != '\\' || i + 1 >= value.Length)
                {
                    text.Append(character);
                    continue;
                }

                char next = value[++i];
                switch (next)
                {
                    case '"': text.Append('"'); break;
                    case '\\': text.Append('\\'); break;
                    case '/': text.Append('/'); break;
                    case 'b': text.Append('\b'); break;
                    case 'f': text.Append('\f'); break;
                    case 'n': text.Append('\n'); break;
                    case 'r': text.Append('\r'); break;
                    case 't': text.Append('\t'); break;
                    case 'u':
                        int code;
                        if (i + 4 < value.Length
                            && int.TryParse(value.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            text.Append((char)code);
                            i += 4;
                        }
                        else
                        {
                            text.Append('u');
                        }
                        break;
                    default: text.Append(next); break;
                }
            }
            return text.ToString();
        }
    }
}
