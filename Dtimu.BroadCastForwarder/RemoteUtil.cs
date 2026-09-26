using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dtimu.BroadCastForwarder
{
    public sealed class DiscoveredDevice
    {
        public string Address { get; set; }
        public string MachineName { get; set; }
        public int HttpPort { get; set; }
        public string Endpoint => $"{Address}:{HttpPort}";
    }

    public static class RemoteUtil
    {
        private const int ForwarderPort = 26926;
        private const string CrossSubnetDiscoverMessage = "DISCOVER_MY_APP_CROSS_SUBNETS";

        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(6);

        // ============================================================
        // 日志
        // ============================================================
        private static void Log(string msg)
        {
            Console.WriteLine($"[Cli {DateTime.Now:HH:mm:ss.fff} T{Thread.CurrentThread.ManagedThreadId:D2}] {msg}");
            Console.Out.Flush();
        }

        // ============================================================
        // 网卡广播地址枚举
        // ============================================================
        public sealed class LocalBroadcast
        {
            public string LocalIp { get; init; }
            public string BroadcastIp { get; init; }
            public byte PrefixLength { get; init; }
        }

        public static List<LocalBroadcast> GetLocalBroadcasts()
        {
            var result = new List<LocalBroadcast>();
            var seenBroadcast = new HashSet<string>();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ipProps = ni.GetIPProperties();

                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.IPv4Mask == null) continue;

                    var ipBytes = ua.Address.GetAddressBytes();
                    var maskBytes = ua.IPv4Mask.GetAddressBytes();
                    if (ipBytes.Length != 4 || maskBytes.Length != 4) continue;

                    if (ipBytes[0] == 169 && ipBytes[1] == 254) continue;

                    int prefix = 0;
                    foreach (var b in maskBytes)
                    {
                        byte bb = b;
                        while ((bb & 0x80) != 0) { prefix++; bb <<= 1; }
                    }
                    if (prefix >= 31) continue;

                    var bcBytes = new byte[4];
                    for (int i = 0; i < 4; i++)
                        bcBytes[i] = (byte)(ipBytes[i] | (~maskBytes[i] & 0xFF));

                    var bc = new IPAddress(bcBytes).ToString();
                    if (!seenBroadcast.Add(bc)) continue;

                    result.Add(new LocalBroadcast
                    {
                        LocalIp = ua.Address.ToString(),
                        BroadcastIp = bc,
                        PrefixLength = (byte)prefix,
                    });
                }
            }

            return result;
        }

        // ============================================================
        // 单播：向已知 Forwarder 请求
        // ============================================================
        public static async Task<List<DiscoveredDevice>> DiscoverViaForwarderAsync(
            IPAddress forwarderIp,
            CancellationToken cancellationToken = default)
        {
            var tag = $"[{forwarderIp}:{ForwarderPort}]";
            var found = new List<DiscoveredDevice>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            Log($"{tag} UNICAST query start, localEP={udp.Client.LocalEndPoint}");

            var requestBytes = Encoding.UTF8.GetBytes(CrossSubnetDiscoverMessage);
            var forwarderEP = new IPEndPoint(forwarderIp, ForwarderPort);

            try
            {
                await udp.SendAsync(requestBytes, requestBytes.Length, forwarderEP);
                Log($"{tag} UNICAST request SENT, {requestBytes.Length} bytes -> {forwarderEP}");
            }
            catch (SocketException ex)
            {
                Log($"{tag} UNICAST send FAILED: {ex.SocketErrorCode} {ex.Message}");
                return found;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ResponseTimeout);
            Log($"{tag} waiting response up to {ResponseTimeout.TotalSeconds}s");

            try
            {
                while (!timeoutCts.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(timeoutCts.Token);
                    var msg = Encoding.UTF8.GetString(result.Buffer);
                    Log($"{tag} <- RECV from {result.RemoteEndPoint} len={result.Buffer.Length}");
                    Log($"{tag}    raw: {msg}");

                    var batch = ParseForwarderResponse(msg);
                    Log($"{tag}    parsed {batch.Count} device(s)");
                    foreach (var d in batch)
                        Log($"{tag}    * {d.MachineName} @ {d.Endpoint}");

                    if (batch.Count > 0)
                    {
                        found.AddRange(batch);
                        Log($"{tag} UNICAST done (single batch), elapsed={sw.ElapsedMilliseconds}ms");
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log($"{tag} UNICAST receive TIMEOUT after {sw.ElapsedMilliseconds}ms");
            }
            catch (SocketException ex)
            {
                Log($"{tag} UNICAST recv err: {ex.SocketErrorCode} {ex.Message}");
            }

            Log($"{tag} UNICAST result: total {found.Count} device(s)");
            return found;
        }

        // ============================================================
        // 广播：发现本网段的 Forwarder
        // ============================================================
        public static async Task<List<DiscoveredDevice>> DiscoverViaBroadcastAsync(
            CancellationToken cancellationToken = default)
        {
            var found = new List<DiscoveredDevice>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var broadcasts = GetLocalBroadcasts();

            Log($"[BCAST] start, {broadcasts.Count} target(s)");
            foreach (var b in broadcasts)
                Log($"[BCAST]   target {b.BroadcastIp} (src {b.LocalIp}/{b.PrefixLength})");

            if (broadcasts.Count == 0)
            {
                Log("[BCAST] no broadcast target, skip");
                return found;
            }

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            Log($"[BCAST] localEP={udp.Client.LocalEndPoint}");

            var requestBytes = Encoding.UTF8.GetBytes(CrossSubnetDiscoverMessage);

            foreach (var bc in broadcasts)
            {
                try
                {
                    await udp.SendAsync(requestBytes, requestBytes.Length,
                        new IPEndPoint(IPAddress.Parse(bc.BroadcastIp), ForwarderPort));
                    Log($"[BCAST] request SENT to {bc.BroadcastIp}:{ForwarderPort}");
                }
                catch (SocketException ex)
                {
                    Log($"[BCAST] send to {bc.BroadcastIp} FAILED: {ex.SocketErrorCode} {ex.Message}");
                }
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ResponseTimeout);
            Log($"[BCAST] waiting responses up to {ResponseTimeout.TotalSeconds}s");

            try
            {
                while (!timeoutCts.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(timeoutCts.Token);
                    var msg = Encoding.UTF8.GetString(result.Buffer);
                    Log($"[BCAST] <- RECV from {result.RemoteEndPoint} len={result.Buffer.Length}");

                    var batch = ParseForwarderResponse(msg);
                    Log($"[BCAST]    parsed {batch.Count} device(s)");
                    foreach (var d in batch)
                        Log($"[BCAST]    * {d.MachineName} @ {d.Endpoint}");

                    found.AddRange(batch);
                }
            }
            catch (OperationCanceledException)
            {
                Log($"[BCAST] receive TIMEOUT after {sw.ElapsedMilliseconds}ms");
            }
            catch (SocketException ex)
            {
                Log($"[BCAST] recv err: {ex.SocketErrorCode} {ex.Message}");
            }

            Log($"[BCAST] result: total {found.Count} device(s)");
            return found;
        }

        // ============================================================
        // 解析
        // ============================================================
        private static List<DiscoveredDevice> ParseForwarderResponse(string json)
        {
            var list = new List<DiscoveredDevice>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    Log($"      ParseForwarderResponse: root kind={root.ValueKind}, not object");
                    return list;
                }
                if (!root.TryGetProperty("Responses", out var responses))
                {
                    Log($"      ParseForwarderResponse: no 'Responses' property");
                    return list;
                }
                if (responses.ValueKind != JsonValueKind.Array)
                {
                    Log($"      ParseForwarderResponse: 'Responses' kind={responses.ValueKind}, not array");
                    return list;
                }

                foreach (var item in responses.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        Log($"      skip non-string item kind={item.ValueKind}");
                        continue;
                    }
                    var inner = item.GetString();
                    if (string.IsNullOrWhiteSpace(inner)) continue;

                    var dev = ParseSingleDevice(inner);
                    if (dev != null) list.Add(dev);
                    else Log($"      ParseSingleDevice returned null for: {inner}");
                }
            }
            catch (JsonException ex)
            {
                Log($"      JsonException: {ex.Message}");
            }
            return list;
        }

        private static DiscoveredDevice ParseSingleDevice(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string ip = null;
                string machineName = null;
                int httpPort = 0;

                if (root.TryGetProperty("IP", out var ipEl) && ipEl.ValueKind == JsonValueKind.String)
                    ip = ipEl.GetString();
                if (root.TryGetProperty("MachineName", out var mnEl) && mnEl.ValueKind == JsonValueKind.String)
                    machineName = mnEl.GetString();
                if (root.TryGetProperty("HttpPort", out var hpEl) && hpEl.ValueKind == JsonValueKind.Number)
                    httpPort = hpEl.GetInt32();

                if (string.IsNullOrWhiteSpace(ip)) return null;

                return new DiscoveredDevice
                {
                    Address = ip,
                    MachineName = machineName ?? ip,
                    HttpPort = httpPort,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // ============================================================
        // 统一入口
        // ============================================================
        public static async Task<List<DiscoveredDevice>> StartFindServersAsync(
            IEnumerable<IPAddress> knownForwarders = null,
            CancellationToken cancellationToken = default)
        {
            Log("================ StartFindServersAsync BEGIN ================");
            var all = new List<DiscoveredDevice>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (knownForwarders != null)
            {
                var fwList = knownForwarders.ToList();
                Log($"knownForwarders: {fwList.Count}");

                var tasks = fwList
                    .Select(f => DiscoverViaForwarderAsync(f, cancellationToken))
                    .ToArray();

                var results = await Task.WhenAll(tasks);
                foreach (var r in results) all.AddRange(r);
            }
            else
            {
                Log("knownForwarders: null, skip unicast");
            }

            Log($"before broadcast, all={all.Count}, elapsed={sw.ElapsedMilliseconds}ms");

            var viaBroadcast = await DiscoverViaBroadcastAsync(cancellationToken);
            all.AddRange(viaBroadcast);

            Log($"after broadcast, all={all.Count}, elapsed={sw.ElapsedMilliseconds}ms");

            var deduped = all
                .GroupBy(d => d.Endpoint, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            Log($"deduped {all.Count} -> {deduped.Count} device(s)");
            foreach (var d in deduped)
                Log($"   FINAL: {d.MachineName} @ {d.Endpoint}");

            Log("================ StartFindServersAsync END ================");
            return deduped;
        }
    }
}