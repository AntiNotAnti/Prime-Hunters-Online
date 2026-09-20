using System;
using System.Net;
using System.Net.Sockets;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// One-shot reachability check for a server address.
    ///
    /// Uses its own short-lived socket rather than NetSession, so pressing
    /// "Test connection" cannot disturb a session that is already running,
    /// and a failed probe leaves nothing behind.
    /// </summary>
    public static class NetProbe
    {
        public static (bool Ok, string Message) Probe(string address, int port,
                                                      int timeoutMs = 3000)
        {
            IPEndPoint endPoint;
            try
            {
                IPAddress[] resolved = Dns.GetHostAddresses(address);
                if (resolved.Length == 0)
                {
                    return (false, $"Could not resolve {address}.");
                }
                IPAddress? ipv4 = Array.Find(resolved,
                    a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 == null)
                {
                    return (false, $"{address} has no IPv4 address.");
                }
                endPoint = new IPEndPoint(ipv4, port);
            }
            catch (Exception ex)
            {
                return (false, $"Could not resolve {address}: {ex.Message}");
            }

            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.ReceiveTimeout = timeoutMs;
            try
            {
                socket.Send(new byte[] { (byte)PacketType.Hello, NetConfig.ProtocolVersion },
                    2, endPoint);
                // The server answers a Hello with a Welcome and then follows
                // up with match state, roster, and possibly an authority
                // notice. Those can arrive in any order, so read a few
                // packets rather than judging the session on the first one.
                var from = new IPEndPoint(IPAddress.Any, 0);
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    byte[] reply = socket.Receive(ref from);
                    if (reply.Length >= 2 && reply[0] == (byte)PacketType.Welcome)
                    {
                        // Leave immediately: the probe claimed a slot the
                        // moment the server answered, and holding it would
                        // block a real player until the timeout expires.
                        socket.Send(new byte[] { (byte)PacketType.Bye }, 1, endPoint);
                        return (true, $"Connected to {endPoint.Address}:{port} "
                            + $"-- server assigned slot {reply[1]}.");
                    }
                    // Any packet type this protocol defines still proves an
                    // MphRead server is listening.
                    if (reply.Length >= 1 && reply[0] >= (byte)PacketType.Hello
                        && reply[0] <= (byte)PacketType.Authority)
                    {
                        continue;
                    }
                    return (false, $"{endPoint.Address}:{port} replied, but not as an "
                        + "MphRead server.");
                }
                socket.Send(new byte[] { (byte)PacketType.Bye }, 1, endPoint);
                return (false, $"{endPoint.Address}:{port} answered but never "
                    + "assigned a slot.");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                return (false, $"No reply from {endPoint.Address}:{port}. The server "
                    + "may be down, or UDP {port} may be blocked by a firewall.");
            }
            catch (Exception ex)
            {
                return (false, $"Could not reach {endPoint.Address}:{port}: {ex.Message}");
            }
        }
    }
}
