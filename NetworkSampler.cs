using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlanceBar;

// Count traffic once at the hardware boundary, including direct and VPN transport connections.
internal sealed class NetworkSampler
{
    private readonly Dictionary<ulong, InterfaceState> _interfaces = new();
    private int _generation;

    internal double DownloadBytesPerSecond { get; private set; }
    internal double UploadBytesPerSecond { get; private set; }
    internal string InterfaceNames { get; private set; } = "无已连接物理网卡";

    // Read one Windows interface snapshot; every adapter owns its baseline across resets and hot-plugging.
    internal unsafe void Sample()
    {
        DownloadBytesPerSecond = 0;
        UploadBytesPerSecond = 0;
        if (Native.GetIfTable2(out var buffer) != 0)
        {
            _interfaces.Clear();
            InterfaceNames = "网卡采集不可用";
            return;
        }

        try
        {
            var table = (Native.MIB_IF_TABLE2*)buffer;
            var rows = &table->first;
            var now = Stopwatch.GetTimestamp();
            var namesChanged = false;
            _generation++;
            for (var index = 0; index < table->numEntries; index++)
            {
                var row = rows + index;
                // Hardware=1, Filter=2, Endpoint=128. Filter rows repeat the same underlying traffic.
                if (row->operStatus != 1 || (row->interfaceAndOperStatusFlags & 0x83) != 1)
                {
                    continue;
                }

                var alias = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(row->alias);
                if (!_interfaces.TryGetValue(row->interfaceLuid, out var state))
                {
                    state = new InterfaceState(alias.ToString());
                    _interfaces.Add(row->interfaceLuid, state);
                    namesChanged = true;
                }
                else if (!alias.SequenceEqual(state.Name))
                {
                    state.Name = alias.ToString();
                    namesChanged = true;
                }

                if (state.Timestamp > 0 && now > state.Timestamp &&
                    row->inOctets >= state.BytesIn && row->outOctets >= state.BytesOut)
                {
                    var elapsed = (now - state.Timestamp) / (double)Stopwatch.Frequency;
                    DownloadBytesPerSecond += (row->inOctets - state.BytesIn) / elapsed;
                    UploadBytesPerSecond += (row->outOctets - state.BytesOut) / elapsed;
                }
                state.BytesIn = row->inOctets;
                state.BytesOut = row->outOctets;
                state.Timestamp = now;
                state.Generation = _generation;
            }

            // .NET permits removal during dictionary enumeration; missing/down adapters lose their baseline.
            foreach (var pair in _interfaces)
            {
                if (pair.Value.Generation != _generation)
                {
                    _interfaces.Remove(pair.Key);
                    namesChanged = true;
                }
            }
            if (_interfaces.Count == 0)
            {
                InterfaceNames = "无已连接物理网卡";
            }
            else if (namesChanged)
            {
                InterfaceNames = string.Join(" + ", _interfaces.Values.Select(state => state.Name));
            }
        }
        finally
        {
            Native.FreeMibTable(buffer);
        }
    }

    // Preserve counters by interface LUID, never by a route destination or an adapter's display name.
    private sealed class InterfaceState(string name)
    {
        internal string Name { get; set; } = name;
        internal ulong BytesIn { get; set; }
        internal ulong BytesOut { get; set; }
        internal long Timestamp { get; set; }
        internal int Generation { get; set; }
    }
}
