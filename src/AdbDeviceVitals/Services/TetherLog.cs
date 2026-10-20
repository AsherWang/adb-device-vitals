using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AdbDeviceVitals.Services
{
    public sealed class TetherLog
    {
        private readonly object _gate = new object();
        private readonly string _path;

        public TetherLog()
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbDeviceVitals", "logs");
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "tether.log");
        }

        public string FilePath { get { return _path; } }

        public void Write(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + (message ?? string.Empty);
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }

        public void Command(string command, CommandResult result, long elapsedMs)
        {
            int exit = result == null ? -1 : result.ExitCode;
            string output = result == null ? string.Empty : result.Output;
            string error = result == null ? string.Empty : result.Error;
            Write("cmd " + command + " exit=" + exit.ToString(CultureInfo.InvariantCulture)
                + " ms=" + elapsedMs.ToString(CultureInfo.InvariantCulture)
                + " stdout=" + (output ?? string.Empty).Length.ToString(CultureInfo.InvariantCulture)
                + " stderr=" + (error ?? string.Empty).Length.ToString(CultureInfo.InvariantCulture));
            WriteBlock("stdout", output);
            WriteBlock("stderr", error);
        }

        private void WriteBlock(string name, string text)
        {
            string interesting = Interesting(text);
            if (interesting.Length == 0) return;
            Write(name + ": " + interesting.Replace("\r", " ").Replace("\n", " | "));
        }

        private static string Interesting(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string normalized = text.Replace("\r", string.Empty).Trim();
            if (normalized.Length <= 160 && normalized.IndexOf('\n') < 0) return normalized;

            StringBuilder builder = new StringBuilder();
            string[] lines = normalized.Split('\n');
            int kept = 0;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (!KeepLine(line)) continue;
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(line);
                kept++;
                if (kept >= 6) break;
            }
            if (builder.Length > 0) return builder.ToString();

            Match state = Regex.Match(
                normalized,
                @"(?:rndis|ncm|usb)\d+\s+-\s+\S*State\s+-\s+lastError\s*=\s*\d+",
                RegexOptions.IgnoreCase);
            if (state.Success) return state.Value;
            Match wanted = Regex.Match(normalized, @"Upstream wanted:\s*\w+", RegexOptions.IgnoreCase);
            if (wanted.Success) return wanted.Value;
            if (normalized.IndexOf("setCurrentFunctions", StringComparison.OrdinalIgnoreCase) >= 0) return "setCurrentFunctions";
            return string.Empty;
        }

        private static bool KeepLine(string line)
        {
            if (string.IsNullOrEmpty(line) || line.Length > 180) return false;
            if (line.IndexOf("setCurrentFunctions", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (line.IndexOf("Upstream wanted:", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return Regex.IsMatch(line, @"^(?:rndis|ncm|usb)\d+\s+-\s+\S*State\b", RegexOptions.IgnoreCase);
        }
    }
}
