using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AdbDeviceVitals.Services
{
    public sealed class UpdateCheckResult
    {
        public bool ReachedServer { get; private set; }
        public bool UpdateAvailable { get; private set; }
        public string Tag { get; private set; }
        public string ReleasePage { get; private set; }
        public string Error { get; private set; }

        private UpdateCheckResult(bool reachedServer, bool updateAvailable, string tag, string releasePage, string error)
        {
            ReachedServer = reachedServer;
            UpdateAvailable = updateAvailable;
            Tag = tag ?? string.Empty;
            ReleasePage = releasePage ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public static UpdateCheckResult Available(string tag, string releasePage)
        {
            return new UpdateCheckResult(true, true, tag, releasePage, string.Empty);
        }

        public static UpdateCheckResult Current(string tag, string releasePage)
        {
            return new UpdateCheckResult(true, false, tag, releasePage, string.Empty);
        }

        public static UpdateCheckResult Failed(string error)
        {
            return new UpdateCheckResult(false, false, string.Empty, string.Empty, error);
        }
    }

    public static class UpdateChecker
    {
        public const string PackageFileName = "AdbDeviceVitals-win-x64.zip";
        private const string Repository = "AsherWang/adb-device-vitals";
        private const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
        private const int MaxPackageBytes = 30 * 1024 * 1024;

        public static Version CurrentVersion
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public static string CurrentVersionText
        {
            get
            {
                Version version = CurrentVersion;
                int build = version.Build < 0 ? 0 : version.Build;
                return version.Major + "." + version.Minor + "." + build;
            }
        }

        public static Task<UpdateCheckResult> CheckAsync()
        {
            return Task.Run(new Func<UpdateCheckResult>(Check));
        }

        public static Task PrepareReplacementAsync(string tag)
        {
            return Task.Run(delegate { PrepareReplacement(tag); });
        }

        public static bool ShouldCheckAutomatically(string path)
        {
            return string.Equals(Path.GetFileName(path), "AdbDeviceVitals.exe", StringComparison.OrdinalIgnoreCase)
                && !IsDevelopmentBuild(path);
        }

        public static bool IsDevelopmentBuild(string path)
        {
            string full;
            try { full = Path.GetFullPath(path ?? string.Empty); }
            catch { return false; }
            full = full.Replace('/', '\\');
            return full.IndexOf("\\bin\\Release\\", StringComparison.OrdinalIgnoreCase) >= 0
                || full.IndexOf("\\bin\\Debug\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsNewerVersion(string remoteTag, Version local)
        {
            Version remote;
            if (local == null || !TryParseReleaseVersion(remoteTag, out remote)) return false;
            if (remote.Major != local.Major) return remote.Major > local.Major;
            if (remote.Minor != local.Minor) return remote.Minor > local.Minor;
            int remoteBuild = remote.Build < 0 ? 0 : remote.Build;
            int localBuild = local.Build < 0 ? 0 : local.Build;
            return remoteBuild > localBuild;
        }

        public static bool TryParseReleaseVersion(string text, out Version version)
        {
            version = null;
            string value = (text ?? string.Empty).Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            if (!Regex.IsMatch(value, @"^\d+\.\d+\.\d+$")) return false;
            return Version.TryParse(value, out version);
        }

        public static bool HashMatches(string sumsText, string fileName, string actualHash)
        {
            string[] lines = (sumsText ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                string hash = line.Substring(0, space).Trim();
                string name = line.Substring(space).Trim().TrimStart('*').Trim();
                if (string.Equals(Path.GetFileName(name), fileName, StringComparison.OrdinalIgnoreCase))
                    return string.Equals(hash, actualHash, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        public static void OpenReleasePage(string releasePage)
        {
            if (string.IsNullOrWhiteSpace(releasePage)) return;
            Process.Start(releasePage);
        }

        private static UpdateCheckResult Check()
        {
            try
            {
                EnsureTls();
                string json = DownloadString(LatestReleaseUrl, 20000);
                Match tagMatch = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                if (!tagMatch.Success) return UpdateCheckResult.Failed("无法读取版本信息。");
                string tag = tagMatch.Groups[1].Value.Trim();
                Version ignored;
                if (!TryParseReleaseVersion(tag, out ignored)) return UpdateCheckResult.Failed("无法识别版本号 " + tag + "。");
                string page = ReleasePage(tag);
                return IsNewerVersion(tag, CurrentVersion)
                    ? UpdateCheckResult.Available(tag, page)
                    : UpdateCheckResult.Current(tag, page);
            }
            catch (Exception ex)
            {
                return UpdateCheckResult.Failed(ShortError(ex));
            }
        }

        private static void PrepareReplacement(string tag)
        {
            Version ignored;
            if (!TryParseReleaseVersion(tag, out ignored)) throw new InvalidOperationException("无法识别版本号。");

            string destination = Process.GetCurrentProcess().MainModule.FileName;
            if (IsDevelopmentBuild(destination)) throw new InvalidOperationException("开发目录中的程序不能原地更新。");
            EnsureWritable(destination);
            EnsureTls();

            string staging = Path.Combine(Path.GetTempPath(), "AdbDeviceVitals-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                string zipPath = Path.Combine(staging, PackageFileName);
                string sumsPath = Path.Combine(staging, "SHA256SUMS.txt");
                DownloadFile(DownloadUrl(tag, "SHA256SUMS.txt"), sumsPath, 20000);
                DownloadFile(DownloadUrl(tag, PackageFileName), zipPath, 60000);
                string actualHash = ComputeSha256(zipPath);
                string sums = File.ReadAllText(sumsPath, Encoding.UTF8);
                if (!HashMatches(sums, PackageFileName, actualHash))
                    throw new InvalidOperationException("更新包校验失败。");

                ExtractPackage(zipPath, staging);
                string newExecutable = Path.Combine(staging, "AdbDeviceVitals.exe");
                if (!File.Exists(newExecutable) || new FileInfo(newExecutable).Length == 0)
                    throw new InvalidOperationException("更新包中没有找到程序文件。");

                string scriptPath = Path.Combine(Path.GetTempPath(), "AdbDeviceVitals-update-" + Guid.NewGuid().ToString("N") + ".ps1");
                File.WriteAllText(scriptPath, BuildScript(Process.GetCurrentProcess().Id, destination, staging, scriptPath), new UTF8Encoding(true));
                StartReplacementScript(scriptPath);
            }
            catch
            {
                try { Directory.Delete(staging, true); } catch { }
                throw;
            }
        }

        private static void ExtractPackage(string zipPath, string staging)
        {
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = Path.GetFileName(entry.FullName);
                    if (!string.Equals(name, "AdbDeviceVitals.exe", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(name, "README.md", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(name, "LICENSE", StringComparison.OrdinalIgnoreCase))
                        continue;
                    entry.ExtractToFile(Path.Combine(staging, name), true);
                }
            }
        }

        private static string BuildScript(int processId, string destination, string staging, string scriptPath)
        {
            StringBuilder script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            script.AppendLine("try {");
            script.AppendLine("  Wait-Process -Id " + processId.ToString(CultureInfo.InvariantCulture) + " -ErrorAction SilentlyContinue");
            script.AppendLine("  Start-Sleep -Milliseconds 800");
            script.AppendLine("  $destination = " + PsQuote(destination));
            script.AppendLine("  $staging = " + PsQuote(staging));
            script.AppendLine("  $directory = Split-Path -Parent $destination");
            script.AppendLine("  Copy-Item -LiteralPath (Join-Path $staging 'AdbDeviceVitals.exe') -Destination $destination -Force");
            script.AppendLine("  foreach ($name in @('README.md', 'LICENSE')) {");
            script.AppendLine("    $source = Join-Path $staging $name");
            script.AppendLine("    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $directory $name) -Force }");
            script.AppendLine("  }");
            script.AppendLine("  Start-Process -FilePath $destination");
            script.AppendLine("} catch {");
            script.AppendLine("  Add-Type -AssemblyName System.Windows.Forms");
            script.AppendLine("  [System.Windows.Forms.MessageBox]::Show([string]$_.Exception.Message, 'ADB Device Vitals') | Out-Null");
            script.AppendLine("} finally {");
            script.AppendLine("  Remove-Item -LiteralPath " + PsQuote(staging) + " -Recurse -Force -ErrorAction SilentlyContinue");
            script.AppendLine("  Remove-Item -LiteralPath " + PsQuote(scriptPath) + " -Force -ErrorAction SilentlyContinue");
            script.AppendLine("}");
            return script.ToString();
        }

        private static void StartReplacementScript(string scriptPath)
        {
            string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(powershell)) throw new InvalidOperationException("无法启动更新程序。");
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = powershell;
            start.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + QuoteCommand(scriptPath);
            start.CreateNoWindow = true;
            start.UseShellExecute = false;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(start);
        }

        private static void EnsureWritable(string destination)
        {
            string probe = destination + ".new";
            try
            {
                using (FileStream stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(0);
                }
                File.Delete(probe);
            }
            catch
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch { }
                throw new InvalidOperationException("程序所在目录无法写入。");
            }
        }

        private static void EnsureTls()
        {
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        }

        private static string DownloadString(string url, int timeoutMs)
        {
            HttpWebRequest request = CreateRequest(url, timeoutMs);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void DownloadFile(string url, string path, int timeoutMs)
        {
            HttpWebRequest request = CreateRequest(url, timeoutMs);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                int total = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaxPackageBytes) throw new InvalidOperationException("更新包过大。");
                    output.Write(buffer, 0, read);
                }
            }
        }

        private static HttpWebRequest CreateRequest(string url, int timeoutMs)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "AdbDeviceVitals";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return request;
        }

        private static string ComputeSha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] hash = algorithm.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                foreach (byte item in hash) builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static string ReleasePage(string tag)
        {
            return "https://github.com/" + Repository + "/releases/tag/" + tag;
        }

        private static string DownloadUrl(string tag, string fileName)
        {
            return "https://github.com/" + Repository + "/releases/download/" + tag + "/" + fileName;
        }

        private static string PsQuote(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        private static string QuoteCommand(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string ShortError(Exception exception)
        {
            string message = exception == null ? string.Empty : exception.Message;
            message = (message ?? string.Empty).Trim();
            int newline = message.IndexOf('\n');
            if (newline >= 0) message = message.Substring(0, newline).Trim();
            if (message.Length > 120) message = message.Substring(0, 120);
            return message.Length == 0 ? "网络请求失败。" : message;
        }
    }
}
