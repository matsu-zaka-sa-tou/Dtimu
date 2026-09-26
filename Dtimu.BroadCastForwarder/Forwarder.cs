using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MVCDtimu
{
    public class Forwarder : IDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();

        private const int ListenPort = 26926;
        private const int TargetBroadcastPort = 26212;

        private const string DiscoverMessage = "DISCOVER_MY_APP";
        private const string CrossSubnetDiscoverMessage = "DISCOVER_MY_APP_CROSS_SUBNETS";

        private const int ResponseTimeoutMs = 1500;

        // ============ 网卡缓存 ============
        private List<BroadcastIface> _cachedIfaces = new();
        private readonly object _ifaceLock = new();

        private static string BuildPayloadJson(List<string> items)
        {
            var sb = new StringBuilder(items.Count * 80 + 32);
            sb.Append("{\"Responses\":[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(EscapeJsonString(items[i])).Append('"');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string EscapeJsonString(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static void Log(string msg)
        {
            try
            {
                Console.WriteLine($"[Fwd {DateTime.Now:HH:mm:ss.fff} T{Thread.CurrentThread.ManagedThreadId:D2}] {msg}");
                Console.Out.Flush();
            }
            catch { }
        }

        public Forwarder()
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.EnableBroadcast = true;
            _listener.Bind(new IPEndPoint(IPAddress.Any, ListenPort));
            Log($"listener bound on 0.0.0.0:{ListenPort}");
        }

        public void Start()
        {
            Log("Start() called");
            Task.Run(() => ListenLoop(_cts.Token));

            // 后台预热网卡缓存，避免第一次请求卡住
            Task.Run(() =>
            {
                Log("iface cache warmup begin");
                var t = Stopwatch.StartNew();
                try
                {
                    var list = GetAllBroadcastInterfaces().ToList();
                    lock (_ifaceLock) _cachedIfaces = list;
                    Log($"iface cache warmed up: {list.Count} iface(s) in {t.ElapsedMilliseconds}ms");
                    foreach (var i in list)
                        Log($"   cached {i.LocalIp} -> {i.BroadcastIp}");
                }
                catch (Exception ex)
                {
                    Log($"iface cache warmup FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        public void Dispose()
        {
            Log("Dispose()");
            _cts.Cancel();
            try { _listener.Close(); } catch { }
            _cts.Dispose();
        }

        private async Task ListenLoop(CancellationToken token)
        {
            var buffer = new byte[65535];
            Log("ListenLoop entered");

            while (!token.IsCancellationRequested)
            {
                EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                int received;
                try
                {
                    received = _listener.ReceiveFrom(buffer, ref remoteEP);
                }
                catch (SocketException ex)
                {
                    Log($"ReceiveFrom err: {ex.SocketErrorCode}");
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    Log("listener disposed, ListenLoop exit");
                    return;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, received);
                Log($"recv from {remoteEP} len={received} msg=\"{message}\"");

                if (message == CrossSubnetDiscoverMessage)
                {
                    var requester = (IPEndPoint)remoteEP;
                    Log($"===> spawn handler for {requester}");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await HandleCrossSubnetRequestAsync(requester, token);
                        }
                        catch (Exception ex)
                        {
                            Log($"!!! UNOBSERVED handler exception for {requester}: {ex.GetType().FullName}: {ex.Message}");
                            Log($"!!!   stack: {ex.StackTrace}");
                        }
                    }, token);
                }
            }
        }

        // ============ 取网卡：优先用缓存，没有再带超时枚举 ============
        private async Task<List<BroadcastIface>> GetIfacesAsync()
        {
            lock (_ifaceLock)
            {
                if (_cachedIfaces.Count > 0)
                    return _cachedIfaces;
            }

            Log("iface cache empty, doing timeout-guarded enumeration");
            var task = Task.Run(() => GetAllBroadcastInterfaces().ToList());
            var done = await Task.WhenAny(task, Task.Delay(5000));
            if (done != task)
            {
                Log("!!! GetAllBroadcastInterfaces TIMEOUT after 5s, using empty list");
                return new List<BroadcastIface>();
            }

            var list = await task;
            lock (_ifaceLock) _cachedIfaces = list;
            Log($"iface enumeration done, {list.Count} iface(s)");
            return list;
        }

        private async Task HandleCrossSubnetRequestAsync(IPEndPoint requester, CancellationToken token)
        {
            var tag = $"[{requester}]";
            var sw = Stopwatch.StartNew();
            Log($"{tag} handler START");

            try
            {
                var responses = new ConcurrentBag<string>();
                var probeBytes = Encoding.UTF8.GetBytes(DiscoverMessage);

                Log($"{tag} getting iface list (cached)");
                var ifaces = await GetIfacesAsync();
                Log($"{tag} got {ifaces.Count} iface(s)");

                foreach (var i in ifaces)
                    Log($"{tag}   iface {i.LocalIp} -> {i.BroadcastIp}");

                if (ifaces.Count == 0)
                {
                    Log($"{tag} no ifaces, sending empty reply");
                    var empty = Encoding.UTF8.GetBytes("{\"Responses\":[]}");
                    SendResponseToRequester(requester, empty);
                    return;
                }

                var collectTasks = new List<Task>();

                foreach (var iface in ifaces)
                {
                    if (token.IsCancellationRequested) break;

                    Socket probeSocket;
                    try
                    {
                        probeSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        probeSocket.EnableBroadcast = true;
                        probeSocket.Bind(new IPEndPoint(iface.LocalIp, 0));
                    }
                    catch (SocketException ex)
                    {
                        Log($"{tag} bind FAILED on {iface.LocalIp}: {ex.SocketErrorCode}");
                        continue;
                    }

                    var sock = probeSocket;
                    var ifaceLocal = iface.LocalIp;

                    collectTasks.Add(Task.Run(() =>
                    {
                        var recvBuf = new byte[2048];
                        EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                        var deadline = DateTime.UtcNow.AddMilliseconds(ResponseTimeoutMs);
                        int count = 0;
                        try
                        {
                            while (!token.IsCancellationRequested)
                            {
                                var remainMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                                if (remainMs <= 0) break;
                                if (!sock.Poll(remainMs * 1000, SelectMode.SelectRead)) break;

                                try
                                {
                                    int n = sock.ReceiveFrom(recvBuf, ref ep);
                                    if (n > 0)
                                    {
                                        var s = Encoding.UTF8.GetString(recvBuf, 0, n);
                                        responses.Add(s);
                                        count++;
                                        Log($"{tag}   [{ifaceLocal}] <- {s}");
                                    }
                                }
                                catch (SocketException ex)
                                {
                                    Log($"{tag}   [{ifaceLocal}] recv err {ex.SocketErrorCode}");
                                }
                            }
                        }
                        finally
                        {
                            Log($"{tag}   [{ifaceLocal}] collect done, {count} reply(ies)");
                            try { sock.Close(); } catch { }
                        }
                    }, token));

                    try
                    {
                        sock.SendTo(probeBytes, new IPEndPoint(iface.BroadcastIp, TargetBroadcastPort));
                        Log($"{tag}   probe -> {iface.BroadcastIp}:{TargetBroadcastPort} (src {iface.LocalIp})");
                    }
                    catch (SocketException ex)
                    {
                        Log($"{tag}   probe send err on {iface.LocalIp}: {ex.SocketErrorCode}");
                    }
                }

                Log($"{tag} all probes sent, waiting {collectTasks.Count} task(s)");
                if (collectTasks.Count > 0)
                {
                    try { await Task.WhenAll(collectTasks); }
                    catch (Exception ex) { Log($"{tag} WhenAll threw: {ex.Message}"); }
                }

                Log($"{tag} all tasks done, cancelled={token.IsCancellationRequested}, responses={responses.Count}, elapsed={sw.ElapsedMilliseconds}ms");

                if (token.IsCancellationRequested) return;

                var snapshot = responses.ToList();
                var payload = BuildPayloadJson(snapshot);
                var payloadBytes = Encoding.UTF8.GetBytes(payload);
                Log($"{tag} total {snapshot.Count} device(s), payload={payloadBytes.Length} bytes");

                SendResponseToRequester(requester, payloadBytes);
                Log($"{tag} handler END, elapsed={sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                Log($"{tag} !!! HANDLER EXCEPTION");
                Log($"{tag} !!!   {ex.GetType().FullName}: {ex.Message}");
                Log($"{tag} !!!   stack: {ex.StackTrace}");
            }
        }

        private void SendResponseToRequester(IPEndPoint requester, byte[] payload)
        {
            var tag = $"[{requester}]";
            var localIp = FindLocalIpForRemote(requester.Address);
            Log($"{tag} matching local iface => {(localIp == null ? "<none>" : localIp.ToString())}");

            if (localIp == null)
            {
                try
                {
                    int sent = _listener.SendTo(payload, requester);
                    Log($"{tag} reply SENT via _listener fallback, {sent} bytes");
                }
                catch (Exception ex)
                {
                    Log($"{tag} fallback SEND FAILED: {ex.Message}");
                }
                return;
            }

            Socket sender = null;
            try
            {
                sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sender.Bind(new IPEndPoint(localIp, 0));
                int sent = sender.SendTo(payload, requester);
                Log($"{tag} reply SENT via {localIp} -> {requester}, {sent} bytes");
            }
            catch (Exception ex)
            {
                Log($"{tag} reply FAILED via {localIp}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { sender?.Close(); } catch { }
            }
        }

        private static IPAddress FindLocalIpForRemote(IPAddress remote)
        {
            if (remote.AddressFamily != AddressFamily.InterNetwork) return null;
            var rBytes = remote.GetAddressBytes();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.IPv4Mask == null) continue;

                    var ipBytes = ua.Address.GetAddressBytes();
                    var maskBytes = ua.IPv4Mask.GetAddressBytes();
                    if (ipBytes.Length != 4 || maskBytes.Length != 4) continue;

                    bool same = true;
                    for (int i = 0; i < 4; i++)
                    {
                        if ((ipBytes[i] & maskBytes[i]) != (rBytes[i] & maskBytes[i]))
                        {
                            same = false;
                            break;
                        }
                    }
                    if (same) return ua.Address;
                }
            }
            return null;
        }

        private sealed class BroadcastIface
        {
            public IPAddress LocalIp { get; init; }
            public IPAddress BroadcastIp { get; init; }
        }

        private static IEnumerable<BroadcastIface> GetAllBroadcastInterfaces()
        {
            var result = new List<BroadcastIface>();
            var seen = new HashSet<string>();

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

                    var bc = new IPAddress(bcBytes);
                    if (!seen.Add(bc.ToString())) continue;

                    result.Add(new BroadcastIface
                    {
                        LocalIp = ua.Address,
                        BroadcastIp = bc,
                    });
                }
            }
            return result;
        }
    }
}