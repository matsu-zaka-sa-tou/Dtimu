using Dtimu.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Networking;

namespace Dtimu.Utils
{
    public class IPProfile
    {
        /// <summary>
        /// 点分十进制/冒号16进制 IP 地址
        /// </summary>
        public string Adress { get; set; }

        /// <summary>
        /// 前缀长度
        /// </summary>
        public byte PrefixLength { get; set; }

        /// <summary>
        /// 网关
        /// </summary>
        public string GateWay { get; set; }
    }

    public static class RemoteUtil
    {
        // 普通发现：设备上的 BroadcastServer 监听端口
        private const int NormalDiscoverPort = 26212;

        // 跨网段发现：三层交换机上的 Forwarder 监听端口
        private const int ForwarderPort = 26926;

        private const string NormalDiscoverMessage = "DISCOVER_MY_APP";
        private const string CrossSubnetMessage = "DISCOVER_MY_APP_CROSS_SUBNETS";

        // 本网段收集窗口
        private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(2);

        // 跨网段收集窗口（Forwarder 内部要等 1.5s，留更宽）
        private static readonly TimeSpan CrossSubnetTimeout = TimeSpan.FromSeconds(5);

        // ===================== IP 工具（保留原有扩展方法） =====================

        public static byte[] GetAddressBytes(this IPProfile ip)
        {
            if (string.IsNullOrWhiteSpace(ip?.Adress))
                throw new ArgumentException("IP 地址不能为空");

            if (!IPAddress.TryParse(ip.Adress, out var address))
                throw new ArgumentException("IP 地址格式错误");

            return address.GetAddressBytes();
        }

        public static string GetBroadcast(this IPProfile ip)
        {
            if (string.IsNullOrWhiteSpace(ip?.Adress))
                throw new ArgumentException("IP 地址不能为空");

            if (!IPAddress.TryParse(ip.Adress, out var address))
                throw new ArgumentException("IP 地址格式错误");

            if (address.AddressFamily != AddressFamily.InterNetwork)
                throw new NotSupportedException("IPv6 没有广播地址");

            var ipBytes = address.GetAddressBytes();

            uint ipUint =
                ((uint)ipBytes[0] << 24) |
                ((uint)ipBytes[1] << 16) |
                ((uint)ipBytes[2] << 8) |
                ipBytes[3];

            uint mask = PrefixToMask(ip.PrefixLength);
            uint broadcast = ipUint | ~mask;

            var bytes = new byte[]
            {
                (byte)(broadcast >> 24),
                (byte)(broadcast >> 16),
                (byte)(broadcast >> 8),
                (byte)(broadcast),
            };

            return new IPAddress(bytes).ToString();
        }

        private static uint PrefixToMask(int prefixLength)
        {
            if (prefixLength < 0 || prefixLength > 32)
                throw new ArgumentOutOfRangeException(nameof(prefixLength));

            return prefixLength == 0
                ? 0
                : uint.MaxValue << (32 - prefixLength);
        }

        // ===================== 程序入口 =====================

        /// <summary>
        /// 发现服务入口：
        ///   1. 向本机所有网段广播地址发 DISCOVER_MY_APP:26212，收本网段设备
        ///   2. 如果一条回包都没收到，再发 DISCOVER_MY_APP_CROSS_SUBNETS:26926，
        ///      让本网段的 Forwarder 去查其他二层网段
        ///   3. 最后合并 GlobalConfigs.RecordedDevices 里手动记录过的设备
        /// </summary>
        public static async Task StartFindServers()
        {
            var broadcasts = GetLocalBroadcasts();
            if (broadcasts.Count == 0)
                return;

            // ---- 阶段一：本网段普通发现 ----
            // 每个网段一个独立 UdpClient，并行跑
            int receivedCount = await RunStageAsync(
                broadcasts,
                NormalDiscoverMessage,
                NormalDiscoverPort,
                LocalTimeout);

            // ---- 阶段二：本网段一个回包都没收到，才让 Forwarder 跨网段找 ----
            if (receivedCount == 0)
            {
                await RunStageAsync(
                    broadcasts,
                    CrossSubnetMessage,
                    ForwarderPort,
                    CrossSubnetTimeout);
            }

            // ---- 合并手动记录过的设备（去重） ----
            foreach (var item in GlobalConfigs.RecordedDevices)
            {
                if (GlobalConfigs.ServerInstances.Any(s => s.IPAddress == item))
                    continue;

                GlobalConfigs.ServerInstances.Add(new ServerInstance
                {
                    IPAddress = item,
                    HostName = item,
                });
            }
        }

        // ===================== 网段信息 =====================

        public sealed class LocalBroadcast
        {
            public string LocalIp { get; set; }
            public string BroadcastIp { get; set; }
            public byte PrefixLength { get; set; }
        }

        /// <summary>
        /// 基于 UWP 的 NetworkInformation 取本机所有 IPv4 网段信息。
        /// - 跳过 IPv6
        /// - 跳过 APIPA 169.254.x.x
        /// - 同一广播地址去重
        /// </summary>
        private static List<LocalBroadcast> GetLocalBroadcasts()
        {
            var result = new List<LocalBroadcast>();
            var seen = new HashSet<string>();

            foreach (var item in Windows.Networking.Connectivity.NetworkInformation.GetHostNames())
            {
                if (item.IPInformation == null) continue;
                if (item.Type == HostNameType.Ipv6) continue;

                var ipStr = item.DisplayName;

                // 只跳过真正的 APIPA 段
                if (ipStr.StartsWith("169.254.", StringComparison.Ordinal)) continue;

                var ipp = new IPProfile
                {
                    Adress = ipStr,
                    PrefixLength = (byte)item.IPInformation.PrefixLength,
                };

                string bc;
                try { bc = ipp.GetBroadcast(); }
                catch { continue; }

                if (!seen.Add(bc)) continue;

                result.Add(new LocalBroadcast
                {
                    LocalIp = ipStr,
                    BroadcastIp = bc,
                    PrefixLength = ipp.PrefixLength,
                });
            }

            return result;
        }

        // ===================== 一轮发现：每个网段独立 UdpClient =====================

        /// <summary>
        /// 对每个网段并行发一条 message（指定端口），
        /// 每个网段用独立的 UdpClient，避免多网卡共享一个 socket 导致只发一个网段。
        /// 返回本轮收到的回包总数（用于判断是否进入阶段二）。
        /// </summary>
        private static async Task<int> RunStageAsync(
            List<LocalBroadcast> broadcasts,
            string message,
            int port,
            TimeSpan timeout)
        {
            var tasks = new List<Task<int>>();
            foreach (var bc in broadcasts)
            {
                tasks.Add(DiscoverOnOneSubnetAsync(bc, message, port, timeout));
            }

            var results = await Task.WhenAll(tasks);
            return results.Sum();
        }

        /// <summary>
        /// 对单个网段：一个独立 UdpClient 发出广播并收集回包。
        /// </summary>
        private static async Task<int> DiscoverOnOneSubnetAsync(
            LocalBroadcast bc,
            string message,
            int port,
            TimeSpan timeout)
        {
            int received = 0;
            UdpClient udp = null;

            try
            {
                udp = new UdpClient();
                udp.EnableBroadcast = true;

                var data = Encoding.UTF8.GetBytes(message);
                var target = new IPEndPoint(IPAddress.Parse(bc.BroadcastIp), port);

                await udp.SendAsync(data, data.Length, target);

                var deadline = DateTime.UtcNow + timeout;

                while (true)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;

                    var receiveTask = udp.ReceiveAsync();
                    var timeoutTask = Task.Delay(remaining);

                    var completed = await Task.WhenAny(receiveTask, timeoutTask);
                    if (completed == timeoutTask) break;

                    try
                    {
                        var result = receiveTask.Result;
                        var msg = Encoding.UTF8.GetString(result.Buffer);
                        ParseAndStore(msg);
                        received++;
                    }
                    catch
                    {
                        // 单个包解析失败，继续等
                    }
                }
            }
            catch
            {
                // 该网段整体失败不影响其它网段
            }
            finally
            {
                udp?.Dispose();
            }

            return received;
        }

        // ===================== 解析回包 =====================

        /// <summary>
        /// 同时兼容两种回包格式：
        ///   1) 普通设备直接回：{ "IP": "192.168.1.10", "MachineName": "PC1", "HttpPort": 26131 }
        ///   2) Forwarder 打包回：{ "Responses": [ "{...}", "{...}" ] }
        /// </summary>
        private static void ParseAndStore(string msg)
        {
            try
            {
                if (!JsonObject.TryParse(msg, out var root))
                    return;

                // ---- Forwarder 打包格式 ----
                if (root.ContainsKey("Responses") &&
                    root["Responses"].ValueType == JsonValueType.Array)
                {
                    foreach (var item in root["Responses"].GetArray())
                    {
                        if (item.ValueType != JsonValueType.String) continue;
                        var inner = item.GetString();
                        TryAddDevice(inner);
                    }
                    return;
                }

                // ---- 普通设备直接格式 ----
                TryAddDevice(msg);
            }
            catch
            {
                // 单包异常忽略
            }
        }

        /// <summary>
        /// 解析单条设备 JSON，写进 GlobalConfigs.ServerInstances（去重）。
        /// </summary>
        private static void TryAddDevice(string json)
        {
            try
            {
                if (!JsonObject.TryParse(json, out var root))
                    return;

                string ip = null;
                string machineName = null;
                int httpPort = 0;

                if (root.ContainsKey("IP") && root["IP"].ValueType == JsonValueType.String)
                    ip = root["IP"].GetString();

                if (root.ContainsKey("MachineName") && root["MachineName"].ValueType == JsonValueType.String)
                    machineName = root["MachineName"].GetString();

                if (root.ContainsKey("HttpPort") && root["HttpPort"].ValueType == JsonValueType.Number)
                    httpPort = (int)root["HttpPort"].GetNumber();

                if (string.IsNullOrWhiteSpace(ip))
                    return;

                var endpoint = $"{ip}:{httpPort}";

                // 去重
                if (GlobalConfigs.ServerInstances.Any(s => s.IPAddress == endpoint))
                    return;

                GlobalConfigs.ServerInstances.Add(new ServerInstance
                {
                    IPAddress = endpoint,
                    HostName = machineName ?? ip,
                });
            }
            catch
            {
                // 忽略单条解析错误
            }
        }
    }
}