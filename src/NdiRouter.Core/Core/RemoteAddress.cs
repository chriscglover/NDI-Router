using System.Net;
using System.Net.Sockets;

namespace NdiRouter.Core;

public static class RemoteAddress
{
    /// <summary>
    /// The address a TCP client connected from, without its port - what identifies a controller
    /// to the router's locks. An IPv4 address that arrived as IPv6 is shown as IPv4.
    /// </summary>
    public static string Of(TcpClient client)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint endPoint) return "unknown";
        var address = endPoint.Address.IsIPv4MappedToIPv6 ? endPoint.Address.MapToIPv4() : endPoint.Address;
        return address.ToString();
    }
}
