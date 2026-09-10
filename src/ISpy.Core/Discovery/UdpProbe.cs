using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ISpy.Core.Discovery;

/// <summary>One raw reply to a multicast probe.</summary>
public readonly record struct ProbeReply(IPAddress From, string Payload);

/// <summary>
/// Sends a multicast probe from every usable network interface and collects replies until a
/// deadline.
/// </summary>
/// <remarks>
/// Probing per-interface rather than once from the default route matters on real machines: a PC
/// with Wi-Fi plus Ethernet, a VPN adapter, or Hyper-V/WSL virtual switches will otherwise send the
/// probe out of the wrong adapter and find nothing. This is the single most common reason
/// discovery "doesn't work" in other clients.
/// </remarks>
public static class UdpProbe
{
    public static async Task<IReadOnlyList<ProbeReply>> BroadcastAsync(
        string multicastAddress,
        int port,
        string payload,
        TimeSpan timeout,
        Action<ProbeReply>? onReply = null,
        CancellationToken cancellationToken = default)
    {
        var group = IPAddress.Parse(multicastAddress);
        var datagram = Encoding.UTF8.GetBytes(payload);
        var replies = new List<ProbeReply>();
        var gate = new Lock();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var listeners = LocalAddresses()
            .Select(address => ListenAsync(address, group, port, datagram, reply =>
            {
                lock (gate) replies.Add(reply);
                onReply?.Invoke(reply);
            }, deadline.Token))
            .ToArray();

        if (listeners.Length == 0) return [];

        await Task.WhenAll(listeners).ConfigureAwait(false);

        lock (gate) return replies.ToArray();
    }

    private static async Task ListenAsync(
        IPAddress localAddress,
        IPAddress group,
        int port,
        byte[] datagram,
        Action<ProbeReply> onReply,
        CancellationToken cancellationToken)
    {
        UdpClient? client = null;

        try
        {
            client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(localAddress, 0));
            client.JoinMulticastGroup(group, localAddress);
            client.Ttl = 4;

            await client.SendAsync(datagram, new IPEndPoint(group, port), cancellationToken)
                .ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                onReply(new ProbeReply(
                    result.RemoteEndPoint.Address, Encoding.UTF8.GetString(result.Buffer)));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the probe window closed.
        }
        catch (SocketException)
        {
            // An adapter that cannot carry multicast (some VPN and virtual switches) is not an
            // error - the other adapters still do the work.
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>IPv4 addresses of every interface that is up and can actually carry a probe.</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var addresses = new List<IPAddress>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(info.Address)) continue;

                addresses.Add(info.Address);
            }
        }

        return addresses;
    }
}
