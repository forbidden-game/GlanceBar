using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace GlanceBar;


internal sealed class NetworkTraceSampler : IDisposable
{
    private static readonly Guid ProviderId = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private const ulong NetworkKeywords = 0x30;

    private readonly object _sync = new();
    private readonly Dictionary<NetworkTrafficKey, Counters> _counters = new();
    private readonly List<NetworkTrafficSample> _samples = new();
    private readonly NetworkConnections _connectionReader = new();
    private readonly NetworkTrafficTree _tree = new();
    private NetworkConnectionSnapshot? _connections;
    private readonly TraceEventSession? _session;
    private readonly Thread? _thread;
    private volatile bool _disposed;
    private Exception? _failure;

    internal bool IsAvailable { get; }
    internal Exception? Failure => Volatile.Read(ref _failure);

    // Bootstrap current socket ownership before listening, so existing proxy connections are classified immediately.
    internal NetworkTraceSampler()
    {
        try
        {
            _connections = _connectionReader.Capture();
            const string sessionName = "GlanceBar.Network";
            _session = new TraceEventSession(sessionName) { StopOnDispose = true };
            _session.EnableProvider(ProviderId, TraceEventLevel.Informational, NetworkKeywords);
            _session.Source.Dynamic.All += OnNetworkEvent;
            _thread = new Thread(ProcessEvents)
            {
                IsBackground = true,
                Name = "GlanceBar network trace"
            };
            _thread.Start();
            IsAvailable = true;
        }
        catch (Exception exception)
        {
            _failure = exception;
            _session?.Dispose();
            _connectionReader.Dispose();
            _session = null;
        }
    }

    private void ProcessEvents()
    {
        try
        {
            _session!.Source.Process();
            if (!_disposed)
            {
                Volatile.Write(ref _failure, new IOException("The network trace session ended unexpectedly."));
            }
        }
        catch (Exception exception) when (!_disposed)
        {
            Volatile.Write(ref _failure, exception);
        }
        catch (Exception) when (_disposed)
        {
            // Closing the trace session interrupts its blocking reader.
        }
    }

    // Preserve app-side local proxy traffic separately from external traffic; suppress the server's mirrored copy.
    private unsafe void OnNetworkEvent(TraceEvent data)
    {
        var payload = new ReadOnlySpan<byte>((void*)data.DataStart, data.EventDataLength);
        if (!NetworkPacket.TryRead((int)data.ID, data.Version, payload, out var packet) ||
            packet.ProcessId == Environment.ProcessId)
        {
            return;
        }
        var parentProcessId = 0;
        if (packet.Local.Address.IsLoopback || packet.Remote.Address.IsLoopback)
        {
            var connections = Volatile.Read(ref _connections)!;
            // Only TCP has both listening ownership and connected peer tuples in this topology snapshot.
            if (packet.IsTcp)
            {
                if (connections.IsLocalServer(packet.ProcessId, packet.Local))
                {
                    return;
                }
                parentProcessId = connections.FindLocalServer(packet.ProcessId, packet.Local, packet.Remote);
            }
            if (parentProcessId == 0)
            {
                // Keep unclassified local activity visible rather than hiding an application or guessing a proxy.
                parentProcessId = -1;
            }
        }
        lock (_sync)
        {
            ref var counters = ref CollectionsMarshal.GetValueRefOrAddDefault(_counters,
                new NetworkTrafficKey(packet.ProcessId, parentProcessId), out _);
            if (packet.IsUpload)
            {
                counters.Upload = unchecked(counters.Upload + packet.Bytes);
            }
            else
            {
                counters.Download = unchecked(counters.Download + packet.Bytes);
            }
        }
    }

    // Capture current topology and transfer one counter interval into a bounded, parent-first process forest.
    internal NetworkProcessUsage[] TakeTop(double elapsedSeconds)
    {
        if (!IsAvailable || elapsedSeconds <= 0)
        {
            return [];
        }
        var connections = _connectionReader.Capture();
        Volatile.Write(ref _connections, connections);
        _samples.Clear();
        lock (_sync)
        {
            foreach (var pair in _counters)
            {
                _samples.Add(new NetworkTrafficSample(pair.Key, pair.Value.Download, pair.Value.Upload));
            }
            _counters.Clear();
        }
        return _tree.Build(_samples, connections, elapsedSeconds);
    }

    // Stop event delivery before releasing the reusable native socket-table buffers.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _session?.Dispose();
        if (_thread is { IsAlive: true })
        {
            _thread.Join(1000);
        }
        _connectionReader.Dispose();
    }

    private struct Counters
    {
        internal ulong Download;
        internal ulong Upload;
    }
}
