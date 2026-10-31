using System.Buffers.Binary;
using System.Collections.Frozen;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GlanceBar;

internal readonly record struct NetworkAddress(ulong High, ulong Low)
{
    private const ulong Ipv4MappedPrefix = 0x0000FFFF00000000UL;

    // Normalize both native IPv4 bytes and IPv4-mapped IPv6 bytes to the same value.
    internal static NetworkAddress FromBytes(ReadOnlySpan<byte> address)
    {
        if (address.Length == 4)
        {
            return new NetworkAddress(0, Ipv4MappedPrefix | BinaryPrimitives.ReadUInt32BigEndian(address));
        }
        if (address.Length == 16)
        {
            return new NetworkAddress(
                BinaryPrimitives.ReadUInt64BigEndian(address),
                BinaryPrimitives.ReadUInt64BigEndian(address[8..]));
        }
        throw new ArgumentException("A network address must contain either 4 or 16 bytes.", nameof(address));
    }

    // Distinguish normalized IPv4 addresses from native IPv6 addresses.
    internal bool IsIpv4Mapped => High == 0 && (Low & 0xFFFFFFFF00000000UL) == Ipv4MappedPrefix;

    // Recognize the complete IPv4 127/8 block and the IPv6 loopback address.
    internal bool IsLoopback =>
        (High == 0 && Low == 1) ||
        (IsIpv4Mapped && (byte)(Low >> 24) == 127);
}

internal readonly record struct NetworkEndpoint(NetworkAddress Address, ushort Port);

internal sealed class NetworkConnections : IDisposable
{
    private nint _ipv4Buffer;
    private uint _ipv4BufferSize;
    private nint _ipv6Buffer;
    private uint _ipv6BufferSize;
    private bool _disposed;
    private readonly List<TcpSocket> _sockets = new();

    // Capture both address families into an immutable ownership snapshot.
    internal NetworkConnectionSnapshot Capture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _sockets.Clear();
        var sockets = _sockets;
        var ipv4Length = ReadTable(ref _ipv4Buffer, ref _ipv4BufferSize, Native.AF_INET, "IPv4");
        AddIpv4Sockets(_ipv4Buffer, ipv4Length, sockets);
        var ipv6Length = ReadTable(ref _ipv6Buffer, ref _ipv6BufferSize, Native.AF_INET6, "IPv6");
        AddIpv6Sockets(_ipv6Buffer, ipv6Length, sockets);
        return new NetworkConnectionSnapshot(sockets);
    }

    // Reuse a caller-owned native buffer, growing it only when the TCP table no longer fits.
    private static uint ReadTable(ref nint buffer, ref uint capacity, uint addressFamily, string familyName)
    {
        while (true)
        {
            var requiredSize = capacity;
            var result = Native.GetExtendedTcpTable(buffer, ref requiredSize, false, addressFamily,
                Native.TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
            if (result == 0)
            {
                if (buffer == 0 || requiredSize < sizeof(uint))
                {
                    throw new InvalidDataException($"GetExtendedTcpTable returned an invalid {familyName} table.");
                }
                return requiredSize;
            }
            if (result != Native.ERROR_INSUFFICIENT_BUFFER)
            {
                throw new Win32Exception((int)result, $"GetExtendedTcpTable failed for {familyName}.");
            }

            if (requiredSize <= capacity)
            {
                requiredSize = capacity == 0 ? 4096u : checked(capacity * 2);
            }
            ResizeBuffer(ref buffer, ref capacity, requiredSize);
        }
    }

    // Resize an unmanaged table buffer without losing the prior allocation if allocation fails.
    private static void ResizeBuffer(ref nint buffer, ref uint capacity, uint requiredSize)
    {
        if (requiredSize > int.MaxValue)
        {
            throw new InvalidDataException("The native TCP table is too large to address.");
        }

        nint resized = buffer == 0
            ? Marshal.AllocHGlobal((int)requiredSize)
            : Marshal.ReAllocHGlobal(buffer, (nint)requiredSize);
        buffer = resized;
        capacity = requiredSize;
    }

    // Copy relevant IPv4 listener and established rows out of the reusable native buffer.
    private static unsafe void AddIpv4Sockets(nint buffer, uint byteLength, List<TcpSocket> sockets)
    {
        var count = GetRowCount<Native.MIB_TCPROW_OWNER_PID>(buffer, byteLength, "IPv4");
        var rows = (Native.MIB_TCPROW_OWNER_PID*)((byte*)buffer + sizeof(uint));
        for (var index = 0; index < count; index++)
        {
            var row = rows + index;
            if (row->state is not (Native.MIB_TCP_STATE_LISTEN or Native.MIB_TCP_STATE_ESTAB) ||
                row->owningProcessId is 0 or > int.MaxValue)
            {
                continue;
            }

            var localAddress = NetworkAddress.FromBytes(new ReadOnlySpan<byte>(&row->localAddress, sizeof(uint)));
            var remoteAddress = NetworkAddress.FromBytes(new ReadOnlySpan<byte>(&row->remoteAddress, sizeof(uint)));
            sockets.Add(new TcpSocket(
                (int)row->owningProcessId,
                row->state,
                new NetworkEndpoint(localAddress, FromNetworkPort(row->localPort)),
                new NetworkEndpoint(remoteAddress, FromNetworkPort(row->remotePort))));
        }
    }

    // Copy relevant IPv6 listener and established rows out of the reusable native buffer.
    private static unsafe void AddIpv6Sockets(nint buffer, uint byteLength, List<TcpSocket> sockets)
    {
        var count = GetRowCount<Native.MIB_TCP6ROW_OWNER_PID>(buffer, byteLength, "IPv6");
        var rows = (Native.MIB_TCP6ROW_OWNER_PID*)((byte*)buffer + sizeof(uint));
        for (var index = 0; index < count; index++)
        {
            var row = rows + index;
            if (row->state is not (Native.MIB_TCP_STATE_LISTEN or Native.MIB_TCP_STATE_ESTAB) ||
                row->owningProcessId is 0 or > int.MaxValue)
            {
                continue;
            }

            var localAddress = NetworkAddress.FromBytes(new ReadOnlySpan<byte>(row->localAddress, 16));
            var remoteAddress = NetworkAddress.FromBytes(new ReadOnlySpan<byte>(row->remoteAddress, 16));
            sockets.Add(new TcpSocket(
                (int)row->owningProcessId,
                row->state,
                new NetworkEndpoint(localAddress, FromNetworkPort(row->localPort)),
                new NetworkEndpoint(remoteAddress, FromNetworkPort(row->remotePort))));
        }
    }

    // Validate the native table header before exposing its row count to pointer iteration.
    private static unsafe int GetRowCount<TRow>(nint buffer, uint byteLength, string familyName)
        where TRow : unmanaged
    {
        if (buffer == 0 || byteLength < sizeof(uint))
        {
            throw new InvalidDataException($"The native {familyName} TCP table has no header.");
        }

        var count = *(uint*)buffer;
        var requiredSize = (ulong)sizeof(uint) + (ulong)count * (uint)sizeof(TRow);
        if (count > int.MaxValue || requiredSize > byteLength)
        {
            throw new InvalidDataException($"The native {familyName} TCP table is truncated.");
        }
        return (int)count;
    }

    // Convert the low 16 bits used by the native table from network to host byte order.
    private static ushort FromNetworkPort(uint port) => BinaryPrimitives.ReverseEndianness((ushort)port);

    // Release both reusable native buffers; snapshots remain independently readable.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_ipv4Buffer != 0)
        {
            Marshal.FreeHGlobal(_ipv4Buffer);
            _ipv4Buffer = 0;
            _ipv4BufferSize = 0;
        }
        if (_ipv6Buffer != 0)
        {
            Marshal.FreeHGlobal(_ipv6Buffer);
            _ipv6Buffer = 0;
            _ipv6BufferSize = 0;
        }
    }

    internal readonly record struct TcpSocket(
        int ProcessId,
        uint State,
        NetworkEndpoint Local,
        NetworkEndpoint Remote);
}

internal sealed class NetworkConnectionSnapshot
{
    private static readonly NetworkAddress Ipv4Any = new(0, 0x0000FFFF00000000UL);
    private static readonly NetworkAddress Ipv6Any = default;

    private readonly FrozenDictionary<NetworkEndpoint, int> _listenerOwners;
    private readonly FrozenSet<OwnedListener> _ownedListeners;
    private readonly FrozenDictionary<ConnectionTuple, int> _connectionOwners;
    private readonly FrozenSet<int> _externalProcesses;
    private readonly FrozenSet<ProcessEdge> _reachability;
    private readonly FrozenDictionary<int, int> _uniqueUpstreams;

    // Build lookup-only collections and precompute process relationships before publication.
    internal NetworkConnectionSnapshot(IReadOnlyList<NetworkConnections.TcpSocket> sockets)
    {
        var listenerOwners = new Dictionary<NetworkEndpoint, int>();
        var ownedListeners = new HashSet<OwnedListener>();
        var connectionOwners = new Dictionary<ConnectionTuple, int>();
        var externalProcesses = new HashSet<int>();

        foreach (var socket in sockets)
        {
            if (socket.State == Native.MIB_TCP_STATE_LISTEN)
            {
                AddOwner(listenerOwners, socket.Local, socket.ProcessId);
                ownedListeners.Add(new OwnedListener(socket.ProcessId, socket.Local));
                continue;
            }
            if (socket.State != Native.MIB_TCP_STATE_ESTAB)
            {
                continue;
            }

            AddOwner(connectionOwners, new ConnectionTuple(socket.Local, socket.Remote), socket.ProcessId);
            if (!socket.Local.Address.IsLoopback || !socket.Remote.Address.IsLoopback)
            {
                externalProcesses.Add(socket.ProcessId);
            }
        }

        _listenerOwners = listenerOwners.ToFrozenDictionary();
        _ownedListeners = ownedListeners.ToFrozenSet();
        _connectionOwners = connectionOwners.ToFrozenDictionary();
        _externalProcesses = externalProcesses.ToFrozenSet();

        var adjacency = new Dictionary<int, HashSet<int>>();
        var upstreamCandidates = new Dictionary<int, int>();
        foreach (var socket in sockets)
        {
            if (socket.State != Native.MIB_TCP_STATE_ESTAB ||
                !socket.Local.Address.IsLoopback || !socket.Remote.Address.IsLoopback)
            {
                continue;
            }

            var match = ResolveLocalServer(socket.ProcessId, socket.Local, socket.Remote, out var serverProcessId);
            if (match == ServerMatch.Found)
            {
                AddEdge(adjacency, socket.ProcessId, serverProcessId);
                AddUpstreamCandidate(upstreamCandidates, socket.ProcessId, serverProcessId);
            }
            else if (match == ServerMatch.Ambiguous)
            {
                upstreamCandidates[socket.ProcessId] = 0;
            }
        }

        _reachability = BuildReachability(adjacency);
        var uniqueUpstreams = new Dictionary<int, int>();
        foreach (var pair in upstreamCandidates)
        {
            if (pair.Value > 0 && !_externalProcesses.Contains(pair.Key) &&
                !_reachability.Contains(new ProcessEdge(pair.Value, pair.Key)))
            {
                uniqueUpstreams.Add(pair.Key, pair.Value);
            }
        }
        _uniqueUpstreams = uniqueUpstreams.ToFrozenDictionary();
    }

    // Resolve a loopback event's client side to one unambiguous listening process.
    internal int FindLocalServer(int clientProcessId, NetworkEndpoint local, NetworkEndpoint remote)
    {
        if (ResolveLocalServer(clientProcessId, local, remote, out var serverProcessId) != ServerMatch.Found)
        {
            return 0;
        }
        return _reachability.Contains(new ProcessEdge(serverProcessId, clientProcessId))
            ? 0
            : serverProcessId;
    }

    // Identify a loopback event endpoint owned by this process's actual listening socket.
    internal bool IsLocalServer(int processId, NetworkEndpoint local)
    {
        if (processId <= 0 || local.Port == 0 || !local.Address.IsLoopback)
        {
            return false;
        }
        if (_ownedListeners.Contains(new OwnedListener(processId, local)))
        {
            return true;
        }
        if (local.Address.IsIpv4Mapped &&
            _ownedListeners.Contains(new OwnedListener(processId, new NetworkEndpoint(Ipv4Any, local.Port))))
        {
            return true;
        }
        return _ownedListeners.Contains(new OwnedListener(processId, new NetworkEndpoint(Ipv6Any, local.Port)));
    }

    // Report whether an established socket owned by the process leaves the loopback host.
    internal bool HasExternalConnections(int processId) => _externalProcesses.Contains(processId);

    // Return a process-wide upstream only when every ownership constraint proves one safe edge.
    internal int GetUniqueUpstream(int processId) =>
        _uniqueUpstreams.TryGetValue(processId, out var upstreamProcessId) ? upstreamProcessId : 0;

    // Resolve orientation from listeners, then validate any live ownership tuples that are available.
    private ServerMatch ResolveLocalServer(
        int clientProcessId,
        NetworkEndpoint local,
        NetworkEndpoint remote,
        out int serverProcessId)
    {
        serverProcessId = 0;
        if (clientProcessId <= 0 || local.Port == 0 || remote.Port == 0 ||
            !local.Address.IsLoopback || !remote.Address.IsLoopback || HasAnyListener(local))
        {
            return ServerMatch.None;
        }

        var listener = MatchListener(remote);
        if (!listener.Found)
        {
            return ServerMatch.None;
        }
        if (listener.Ambiguous || listener.Owner == clientProcessId)
        {
            return ServerMatch.Ambiguous;
        }

        if (_connectionOwners.TryGetValue(new ConnectionTuple(local, remote), out var clientOwner) &&
            clientOwner != clientProcessId)
        {
            return ServerMatch.Ambiguous;
        }
        if (_connectionOwners.TryGetValue(new ConnectionTuple(remote, local), out var serverOwner) &&
            serverOwner != listener.Owner)
        {
            return ServerMatch.Ambiguous;
        }

        serverProcessId = listener.Owner;
        return ServerMatch.Found;
    }

    // Match exact, IPv4 wildcard, and IPv6 wildcard listeners without allocating on event callbacks.
    private ListenerMatch MatchListener(NetworkEndpoint endpoint)
    {
        var found = false;
        var ambiguous = false;
        var owner = 0;
        MergeListener(endpoint, ref found, ref ambiguous, ref owner);
        if (endpoint.Address.IsIpv4Mapped && endpoint.Address != Ipv4Any)
        {
            MergeListener(new NetworkEndpoint(Ipv4Any, endpoint.Port), ref found, ref ambiguous, ref owner);
        }
        if (endpoint.Address != Ipv6Any)
        {
            MergeListener(new NetworkEndpoint(Ipv6Any, endpoint.Port), ref found, ref ambiguous, ref owner);
        }
        return new ListenerMatch(found, ambiguous, owner);
    }

    // Fold one endpoint's owner into a listener match while preserving all ambiguity.
    private void MergeListener(NetworkEndpoint endpoint, ref bool found, ref bool ambiguous, ref int owner)
    {
        if (!_listenerOwners.TryGetValue(endpoint, out var endpointOwner))
        {
            return;
        }
        if (!found)
        {
            found = true;
            owner = endpointOwner;
            ambiguous = endpointOwner == 0;
            return;
        }
        if (endpointOwner == 0 || owner == 0 || endpointOwner != owner)
        {
            ambiguous = true;
            owner = 0;
        }
    }

    // Detect either unique or shared listeners at an event's local endpoint.
    private bool HasAnyListener(NetworkEndpoint endpoint) => MatchListener(endpoint).Found;

    // Preserve one owner or the ambiguity sentinel when native rows share the same tuple.
    private static void AddOwner<TKey>(Dictionary<TKey, int> owners, TKey key, int processId)
        where TKey : notnull
    {
        if (!owners.TryAdd(key, processId) && owners[key] != processId)
        {
            owners[key] = 0;
        }
    }

    // Add a deduplicated directed process edge for reachability analysis.
    private static void AddEdge(Dictionary<int, HashSet<int>> adjacency, int fromProcessId, int toProcessId)
    {
        if (!adjacency.TryGetValue(fromProcessId, out var targets))
        {
            targets = new HashSet<int>();
            adjacency.Add(fromProcessId, targets);
        }
        targets.Add(toProcessId);
    }

    // Retain a candidate only while every resolved loopback connection names the same upstream.
    private static void AddUpstreamCandidate(Dictionary<int, int> candidates, int processId, int upstreamProcessId)
    {
        if (!candidates.TryAdd(processId, upstreamProcessId) && candidates[processId] != upstreamProcessId)
        {
            candidates[processId] = 0;
        }
    }

    // Precompute transitive process reachability so event callbacks reject cycle-closing edges in O(1).
    private static FrozenSet<ProcessEdge> BuildReachability(Dictionary<int, HashSet<int>> adjacency)
    {
        var reachability = new HashSet<ProcessEdge>();
        foreach (var start in adjacency.Keys)
        {
            var visited = new HashSet<int>();
            var pending = new Stack<int>();
            foreach (var target in adjacency[start])
            {
                pending.Push(target);
            }

            while (pending.TryPop(out var current))
            {
                if (!visited.Add(current))
                {
                    continue;
                }
                reachability.Add(new ProcessEdge(start, current));
                if (adjacency.TryGetValue(current, out var targets))
                {
                    foreach (var target in targets)
                    {
                        pending.Push(target);
                    }
                }
            }
        }
        return reachability.ToFrozenSet();
    }

    private enum ServerMatch
    {
        None,
        Found,
        Ambiguous
    }

    private readonly record struct ListenerMatch(bool Found, bool Ambiguous, int Owner);
    private readonly record struct OwnedListener(int ProcessId, NetworkEndpoint Endpoint);
    private readonly record struct ConnectionTuple(NetworkEndpoint Local, NetworkEndpoint Remote);
    private readonly record struct ProcessEdge(int FromProcessId, int ToProcessId);
}
