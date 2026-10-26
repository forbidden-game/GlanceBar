using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace TaskbarPulse;

internal readonly record struct NetworkProcessUsage(int ProcessId, string Name, double DownloadBytesPerSecond, double UploadBytesPerSecond);

internal sealed class NetworkTraceSampler : IDisposable
{
    private static readonly Guid ProviderId = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private const ulong NetworkKeywords = 0x30;
    private const int TopCount = 5;

    private readonly object _sync = new();
    private readonly Dictionary<int, Counters> _counters = new();
    private readonly Dictionary<int, string> _processNames = new();
    private readonly TraceEventSession? _session;
    private readonly Thread? _thread;
    private bool _disposed;

    internal bool IsAvailable { get; }

    internal NetworkTraceSampler()
    {
        try
        {
            var sessionName = $"TaskbarPulse.Network.{Environment.ProcessId}";
            _session = new TraceEventSession(sessionName) { StopOnDispose = true };
            _session.EnableProvider(ProviderId, TraceEventLevel.Informational, NetworkKeywords);
            _session.Source.Dynamic.All += OnNetworkEvent;
            _thread = new Thread(() => _session.Source.Process())
            {
                IsBackground = true,
                Name = "TaskbarPulse network trace"
            };
            _thread.Start();
            IsAvailable = true;
        }
        catch
        {
            _session?.Dispose();
            _session = null;
        }
    }

    private void OnNetworkEvent(TraceEvent data)
    {
        var direction = ((int)data.ID) switch
        {
            10 or 14 or 26 or 30 or 42 or 58 => 1,
            11 or 27 or 43 or 59 => -1,
            _ => 0
        };
        if (direction == 0 || data.PayloadNames.Length < 2)
        {
            return;
        }

        int processId;
        ulong bytes;
        try
        {
            processId = Convert.ToInt32(data.PayloadValue(0));
            bytes = Convert.ToUInt64(data.PayloadValue(1));
        }
        catch
        {
            return;
        }

        if (processId <= 0 || bytes == 0 || processId == Environment.ProcessId)
        {
            return;
        }

        lock (_sync)
        {
            if (!_counters.TryGetValue(processId, out var counters))
            {
                counters = new Counters();
                _counters.Add(processId, counters);
            }

            if (direction > 0)
            {
                counters.Upload = unchecked(counters.Upload + bytes);
            }
            else
            {
                counters.Download = unchecked(counters.Download + bytes);
            }
        }
    }

    internal NetworkProcessUsage[] TakeTop(double elapsedSeconds)
    {
        if (!IsAvailable || elapsedSeconds <= 0)
        {
            return [];
        }

        Span<int> ids = stackalloc int[TopCount];
        Span<ulong> downloads = stackalloc ulong[TopCount];
        Span<ulong> uploads = stackalloc ulong[TopCount];
        Span<ulong> totals = stackalloc ulong[TopCount];
        var count = 0;

        lock (_sync)
        {
            foreach (var pair in _counters)
            {
                var total = pair.Value.Download + pair.Value.Upload;
                if (total == 0)
                {
                    continue;
                }

                var insertAt = count;
                while (insertAt > 0 && totals[insertAt - 1] < total)
                {
                    insertAt--;
                }
                if (insertAt >= TopCount)
                {
                    pair.Value.Reset();
                    continue;
                }

                var upper = Math.Min(count, TopCount - 1);
                for (var index = upper; index > insertAt; index--)
                {
                    ids[index] = ids[index - 1];
                    downloads[index] = downloads[index - 1];
                    uploads[index] = uploads[index - 1];
                    totals[index] = totals[index - 1];
                }
                ids[insertAt] = pair.Key;
                downloads[insertAt] = pair.Value.Download;
                uploads[insertAt] = pair.Value.Upload;
                totals[insertAt] = total;
                if (count < TopCount)
                {
                    count++;
                }
                pair.Value.Reset();
            }
        }

        var result = new NetworkProcessUsage[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = new NetworkProcessUsage(
                ids[index],
                ResolveProcessName(ids[index]),
                downloads[index] / elapsedSeconds,
                uploads[index] / elapsedSeconds);
        }
        return result;
    }

    private string ResolveProcessName(int processId)
    {
        if (_processNames.TryGetValue(processId, out var name))
        {
            return name;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch
        {
            name = $"PID {processId}";
        }
        _processNames[processId] = name;
        return name;
    }

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
    }

    private sealed class Counters
    {
        internal ulong Download;
        internal ulong Upload;

        internal void Reset()
        {
            Download = 0;
            Upload = 0;
        }
    }
}
