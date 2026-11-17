using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AdbDeviceVitals.Services
{
    /// <summary>
    /// 读取电脑当前上网的公网出口地址，供「蜂窝网络」卡片展示。
    ///
    /// IPv4 经过运营商 NAT，本机只能看到 192.168.x 之类的内网地址，必须靠外部回显
    /// 服务才能知道出口；IPv6 通常没有 NAT，本机默认网卡上 Preferred 的全局地址就是
    /// 出站源地址，所以直接读网卡，离线或 IPv6 回显服务不可达时也能显示。
    ///
    /// 该查询独立于 2.5 秒的 ADB 采集循环，由 MainWindow 低频触发
    /// （启动 / 网络地址变化 / 每 10 分钟），避免高频把公网 IP 报给第三方。
    /// </summary>
    internal sealed class PublicIpService
    {
        private static readonly string[] EchoUrls =
        {
            "https://ipinfo.io/ip",
            "https://api.ipify.org"
        };

        private static readonly HttpClient Client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6)
        };

        public async Task<PublicIpResult> QueryAsync(CancellationToken token)
        {
            string ipv4 = await QueryIpv4Async(token).ConfigureAwait(false);
            string ipv6 = FindOutboundIpv6();
            return new PublicIpResult(ipv4, ipv6, DateTime.Now);
        }

        /// <summary>依次尝试回显服务，返回第一个合法的 IPv4 文本；全部失败返回空串。</summary>
        private static async Task<string> QueryIpv4Async(CancellationToken token)
        {
            foreach (string url in EchoUrls)
            {
                try
                {
                    string text = (await Client.GetStringAsync(url, token).ConfigureAwait(false)).Trim();
                    IPAddress parsed;
                    if (IPAddress.TryParse(text, out parsed) &&
                        parsed.AddressFamily == AddressFamily.InterNetwork)
                        return text;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { /* 超时、无网络或返回非 IPv4：换下一个服务 */ }
            }
            return "";
        }

        /// <summary>
        /// 让系统按 IPv6 路由表直接选出站源地址：UDP 的 Connect 只设置默认目标、
        /// 不发送任何报文，LocalEndPoint 就是操作系统实际会用的源地址。
        /// 这比猜「哪个是临时地址」可靠——Windows 上 IsTransient 对 RA 生成的
        /// 临时地址会返回 False，网卡上还残留大量 Deprecated 的旧前缀。
        /// 没有 IPv6 默认路由时（探测抛 SocketException）再退回网卡枚举。
        /// </summary>
        private static string FindOutboundIpv6()
        {
            try
            {
                using (Socket probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect(new IPEndPoint(IPAddress.Parse("2400:3200::1"), 53));
                    IPEndPoint local = probe.LocalEndPoint as IPEndPoint;
                    if (local != null && IsGlobalIpv6(local.Address))
                        return local.Address.ToString();
                }
            }
            catch { /* 无 IPv6 默认路由或地址族不可用：继续用网卡枚举 */ }

            try
            {
                List<UnicastIPAddressInformation> candidates = new List<UnicastIPAddressInformation>();
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                        if (info.DuplicateAddressDetectionState != DuplicateAddressDetectionState.Preferred) continue;
                        if (!IsGlobalIpv6(info.Address)) continue;
                        candidates.Add(info);
                    }
                }

                // /128 + 随机后缀是 Windows 的临时出站地址；/64 是稳定地址。
                UnicastIPAddressInformation picked =
                    candidates.FirstOrDefault(a => a.PrefixLength >= 128 && a.SuffixOrigin == SuffixOrigin.Random)
                    ?? candidates.FirstOrDefault(a => a.PrefixLength >= 128)
                    ?? candidates.FirstOrDefault();
                return picked == null ? "" : picked.Address.ToString();
            }
            catch { return ""; }
        }

        private static bool IsGlobalIpv6(IPAddress address)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                address.IsIPv4MappedToIPv6 || address.IsIPv6Teredo || IPAddress.IsLoopback(address))
                return false;
            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length != 16) return false;
            if ((bytes[0] & 0xFE) == 0xFC) return false; // fc00::/7 唯一本地地址
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
                return false; // 2001:db8::/32 文档示例地址
            return true;
        }
    }

    internal sealed class PublicIpResult
    {
        public string Ipv4 { get; private set; }
        public string Ipv6 { get; private set; }
        public DateTime CheckedAt { get; private set; }

        public PublicIpResult(string ipv4, string ipv6, DateTime checkedAt)
        {
            Ipv4 = ipv4 ?? "";
            Ipv6 = ipv6 ?? "";
            CheckedAt = checkedAt;
        }
    }
}
