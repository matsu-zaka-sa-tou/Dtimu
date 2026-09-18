using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MVCDtimu
{
    public class BroadcastServer
    {
        private static bool IsIpInSameSubnet(IPAddress remote, IPAddress local, IPAddress mask)
        {
            var remoteBytes = remote.GetAddressBytes();
            var localBytes = local.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();

            for (int i = 0; i < maskBytes.Length; i++)
            {
                if ((remoteBytes[i] & maskBytes[i]) != (localBytes[i] & maskBytes[i]))
                    return false;
            }

            return true;
        }
        public static IPAddress? FindLocalIpFromRemoteIP(IPAddress remote)
        {
            if (remote.AddressFamily != AddressFamily.InterNetwork)
                return null;

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                var ipProps = ni.GetIPProperties();

                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    // 必须有子网掩码
                    if (ua.IPv4Mask == null)
                        continue;

                    if (IsIpInSameSubnet(remote, ua.Address, ua.IPv4Mask))
                    {
                        return ua.Address;
                    }
                }
            }

            return null;
        }


        public void Start()
        {
            Task.Run(async () =>
            {
                var socket = new Socket(AddressFamily.InterNetwork,
                        SocketType.Dgram,
                        ProtocolType.Udp);

                // 绑定本地端口（监听广播端口）
                socket.Bind(new IPEndPoint(IPAddress.Any, 26212));

                var buffer = new byte[2048];

                while (true)
                {
                    EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

                    // 接收数据（阻塞）
                    var received = socket.ReceiveFrom(buffer, ref remoteEP);

                    var message = Encoding.UTF8.GetString(buffer, 0, received);

                    if (message == "DISCOVER_MY_APP")
                    {
                        var remoteIpEndPoint = (IPEndPoint)remoteEP;

                        var localIp  = FindLocalIpFromRemoteIP(remoteIpEndPoint.Address); 

                        var response = JsonSerializer.Serialize(new
                        {
                            IP = localIp.ToString(),
                            MachineName = Environment.MachineName,
                            HttpPort = 26131
                        });

                        var bytes = Encoding.UTF8.GetBytes(response);

                        // 关键：直接发回给发送方
                        socket.SendTo(bytes, remoteEP);
                    }
                }
            });
        }
    }
}
