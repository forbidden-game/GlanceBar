using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlanceBar;

internal sealed class GpuSampler : IDisposable
{
    private const string CounterPattern = @"\GPU Engine(*)\Utilization Percentage";
    private static readonly long RetryInterval = 30 * Stopwatch.Frequency;

    private readonly Dictionary<string, double> _engineLoads = new(StringComparer.OrdinalIgnoreCase);
    private nint _query;
    private nint _counter;
    private nint _valuesBuffer;
    private uint _bufferCapacity;
    private long _nextAttempt;
    private bool _disposed;
    private bool _hasBaseline;

    // Sum processes sharing a physical engine, then report the busiest engine across adapters.
    internal unsafe double Sample()
    {
        if (_query == 0 && Stopwatch.GetTimestamp() >= _nextAttempt)
        {
            OpenQuery();
        }
        if (_query == 0)
        {
            return -1;
        }

        if (Native.PdhCollectQueryData(_query) != 0)
        {
            _hasBaseline = false;
            return -1;
        }
        if (!_hasBaseline)
        {
            _hasBaseline = true;
            return -1;
        }

        // A wildcard counter discovers new processes without closing the query or losing baselines.
        const uint format = Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100;
        var size = 0u;
        var status = Native.PdhGetFormattedCounterArrayW(_counter, format, ref size, out _, 0);
        if (status != Native.PDH_MORE_DATA || size == 0)
        {
            return -1;
        }
        if (size > _bufferCapacity)
        {
            _valuesBuffer = Marshal.ReAllocHGlobal(_valuesBuffer, checked((nint)size));
            _bufferCapacity = size;
        }
        size = _bufferCapacity;
        if (Native.PdhGetFormattedCounterArrayW(_counter, format, ref size, out var count, _valuesBuffer) != 0)
        {
            return -1;
        }

        foreach (var key in _engineLoads.Keys)
        {
            _engineLoads[key] = 0;
        }
        // Match names in the native buffer; allocate a key only when a new hardware engine appears.
        var loads = _engineLoads.GetAlternateLookup<ReadOnlySpan<char>>();
        var items = (Native.PDH_FMT_COUNTERVALUE_ITEM*)_valuesBuffer;
        var busiestEngine = 0.0;
        var hasValue = false;
        for (var index = 0; index < count; index++)
        {
            var item = items[index];
            var value = item.value.doubleValue;
            // Both VALID_DATA (0) and NEW_DATA (1) are valid PDH results.
            if (item.value.status > 1 || !double.IsFinite(value) || value < 0)
            {
                continue;
            }
            var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)item.name);
            var start = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                continue;
            }
            var key = name[start..];
            loads.TryGetValue(key, out var sum);
            sum += value;
            loads[key] = sum;
            busiestEngine = Math.Max(busiestEngine, sum);
            hasValue = true;
        }
        return hasValue ? Math.Clamp(busiestEngine, 0, 100) : -1;
    }

    // Register the English wildcard directly so localized Windows installations work too.
    private void OpenQuery()
    {
        _nextAttempt = Stopwatch.GetTimestamp() + RetryInterval;
        if (Native.PdhOpenQueryW(null, 0, out _query) != 0)
        {
            _query = 0;
            return;
        }
        if (Native.PdhAddEnglishCounterW(_query, CounterPattern, 0, out _counter) != 0)
        {
            CloseQuery();
        }
    }

    // Discard native counter handles and their collection baseline together.
    private void CloseQuery()
    {
        if (_query != 0)
        {
            Native.PdhCloseQuery(_query);
            _query = 0;
        }
        _counter = 0;
        _hasBaseline = false;
    }

    // Release the query and reusable native result buffer when the monitor closes.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseQuery();
        if (_valuesBuffer != 0)
        {
            Marshal.FreeHGlobal(_valuesBuffer);
            _valuesBuffer = 0;
        }
    }

}
