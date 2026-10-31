using System.Buffers.Binary;
using System.Net;

namespace GlanceBar;

public sealed class NetworkPacketTests
{
    // Captured kernel receive events retain saddr/sport as the local endpoint; they must still resolve to the proxy.
    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(26, true)]
    [InlineData(27, false)]
    public void BothDirectionsKeepApplicationUnderProxy(int eventId, bool upload)
    {
        var address = eventId >= 26 ? "::1" : "127.0.0.1";
        var app = Endpoint(address, 50001);
        var proxy = Endpoint(address, 57777);
        var topology = new NetworkConnectionSnapshot([
            new(1001, Native.MIB_TCP_STATE_LISTEN, proxy, default),
            new(1002, Native.MIB_TCP_STATE_ESTAB, app, proxy),
            new(1001, Native.MIB_TCP_STATE_ESTAB, proxy, app)
        ]);
        Assert.True(NetworkPacket.TryRead(eventId, 0, Payload(eventId, 1002, 50001, 57777), out var packet));
        Assert.Equal(upload, packet.IsUpload);
        Assert.Equal(1001, topology.FindLocalServer(packet.ProcessId, packet.Local, packet.Remote));
        Assert.False(topology.IsLocalServer(packet.ProcessId, packet.Local));
    }

    // The proxy's mirrored local response/request must not be published as additional upload or an orphan local row.
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(26)]
    [InlineData(27)]
    public void BothDirectionsRecognizeServerMirror(int eventId)
    {
        var address = eventId >= 26 ? "::1" : "127.0.0.1";
        var proxy = Endpoint(address, 57777);
        var topology = new NetworkConnectionSnapshot([new(1001, Native.MIB_TCP_STATE_LISTEN, proxy, default)]);
        Assert.True(NetworkPacket.TryRead(eventId, 0, Payload(eventId, 1001, 57777, 50001), out var packet));
        Assert.True(topology.IsLocalServer(packet.ProcessId, packet.Local));
    }

    // Unsupported versions, non-traffic event IDs, and incomplete headers cannot enter the traffic counters.
    [Fact]
    public void RejectsUnsupportedOrIncompleteMetadata()
    {
        var payload = Payload(10, 1002, 50001, 57777);
        Assert.False(NetworkPacket.TryRead(10, 1, payload, out _));
        Assert.False(NetworkPacket.TryRead(12, 0, payload, out _));
        Assert.False(NetworkPacket.TryRead(10, 0, payload.AsSpan(0, 19), out _));
    }

    // Encode the provider's measured local/remote connection header, independently of the packet's byte direction.
    private static byte[] Payload(int eventId, int processId, ushort localPort, ushort remotePort)
    {
        var address = IPAddress.Parse(eventId >= 26 ? "::1" : "127.0.0.1").GetAddressBytes();
        var portOffset = 8 + address.Length * 2;
        var bytes = new byte[portOffset + 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, processId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 65536);
        address.CopyTo(bytes, 8);
        address.CopyTo(bytes, 8 + address.Length);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(portOffset), remotePort);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(portOffset + 2), localPort);
        return bytes;
    }

    // Convert the controlled fixture's addresses to the same normalized endpoint representation as native ownership.
    private static NetworkEndpoint Endpoint(string address, ushort port) =>
        new(NetworkAddress.FromBytes(IPAddress.Parse(address).GetAddressBytes()), port);
}
