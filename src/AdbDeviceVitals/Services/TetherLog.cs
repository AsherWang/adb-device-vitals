using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AdbDeviceVitals.Services
{
    /// <summary>
    /// 诊断日志。USB 共享、电脑热点和 ADB daemon 恢复会从不同后台线程同时写同一个文件，
    /// 所以互斥锁必须是进程级的（旧版每个实例一把锁，等于没锁）；同时 File.AppendAllText
    /// 只给 FileShare.Read，并发写会抛 IOException 并冒泡成「USB 网络共享」错误气泡。
    /// 现在统一用 FileShare.ReadWrite 打开 + 短重试，日志写失败只丢这一行，绝不阻断业务。
    /// </summary>
    public sealed class TetherLog
    {
        private static readonly object Gate = new object();
        private const int Attempts = 4;
        private readonly string _path;

        public TetherLog() : this(DefaultPath()) { }

        /// <summary>测试可注入临时路径，避免碰用户真实的日志目录。</summary>
        public TetherLog(string path)
        {
            _path = path;
        }

        public string FilePath { get { return _path ?? string.Empty; } }

        public void Write(string message)
        {
            if (string.IsNullOrEmpty(_path)) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " " + (message ?? string.Empty);
            lock (Gate)
            {
                for (int attempt = 0; attempt < Attempts; attempt++)
                {
                    try
                    {
                        string directory = Path.GetDirectoryName(_path);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                        using (FileStream stream = new FileStream(_path, FileMode.Append, FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete))
                        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        {
                            writer.WriteLine(line);
                        }
                        return;
                    }
                    catch (IOException)
                    {
                        // 杀软扫描、旧版本进程或用户正用记事本查看时可能瞬时独占，重试几次。
                        if (attempt == Attempts - 1) return;
                    }
                    catch (Exception)
                    {
                        // 日志永远不能让 USB 共享 / 热点 / 采集失败。
                        return;
                    }
                    Thread.Sleep(10 * (attempt + 1));
                }
            }
        }

        private static string DefaultPath()
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AdbDeviceVitals", "logs");
                Directory.CreateDirectory(directory);
                return Path.Combine(directory, "tether.log");
            }
            catch
            {
                return null;
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
