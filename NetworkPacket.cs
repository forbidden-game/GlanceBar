using System.Buffers.Binary;

namespace GlanceBar;

// Typed v0 kernel-network metadata; endpoint ownership is independent of whether bytes were sent or received.
internal readonly record struct NetworkPacket(int ProcessId, uint Bytes, bool IsUpload, bool IsTcp,
    NetworkEndpoint Local, NetworkEndpoint Remote)
{
    // Validate the event boundary once, then decode its connection-oriented local/remote endpoint pair.
    internal static bool TryRead(int id, int version, ReadOnlySpan<byte> payload, out NetworkPacket packet)
    {
        packet = default;
        var direction = id switch
        {
            10 or 14 or 26 or 30 or 42 or 58 => 1,
            11 or 27 or 43 or 59 => -1,
            _ => 0
        };
        var addressLength = id is 26 or 27 or 30 or 58 or 59 ? 16 : 4;
        var portOffset = 8 + addressLength * 2;
        if (direction == 0 || version != 0 || payload.Length < portOffset + 4)
        {
            return false;
        }
        var processId = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var bytes = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (processId <= 0 || bytes == 0)
        {
            return false;
        }

        // Measured on both IPv4 and IPv6: TCP receive events still place the LOCAL endpoint in saddr/sport.
        // Swapping these fields for receives hides app downloads and mistakes the proxy's response for upload.
        var local = new NetworkEndpoint(NetworkAddress.FromBytes(payload.Slice(8 + addressLength, addressLength)),
            BinaryPrimitives.ReadUInt16BigEndian(payload[(portOffset + 2)..]));
        var remote = new NetworkEndpoint(NetworkAddress.FromBytes(payload.Slice(8, addressLength)),
            BinaryPrimitives.ReadUInt16BigEndian(payload[portOffset..]));
        packet = new NetworkPacket(processId, bytes, direction > 0, id < 42, local, remote);
        return true;
    }
}
