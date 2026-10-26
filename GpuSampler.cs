using System.Diagnostics;

namespace TaskbarPulse;

internal sealed class GpuSampler : IDisposable
{
    private const string CounterPattern = @"\GPU Engine(*)\Utilization Percentage";
    private static readonly long RefreshInterval = 30 * Stopwatch.Frequency;

    private nint _query;
    private CounterEntry[] _counters = [];
    private double[] _groupSums = [];
    private long _nextRefresh;
    private bool _disposed;
    private bool _hasBaseline;
    private double _lastValue = -1;

    internal double Sample()
    {
        var now = Stopwatch.GetTimestamp();
        if (_query == 0 || now >= _nextRefresh)
        {
            RefreshCounters(now);
        }

        if (_query == 0 || _counters.Length == 0)
        {
            return -1;
        }

        if (Native.PdhCollectQueryData(_query) != 0)
        {
            return _lastValue;
        }

        if (!_hasBaseline)
        {
            _hasBaseline = true;
            return _lastValue;
        }

        Array.Clear(_groupSums);
        const uint format = Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100;

        foreach (var counter in _counters)
        {
            var status = Native.PdhGetFormattedCounterValue(counter.Handle, format, out _, out var value);
            if (status != 0 || value.status != 0 || !double.IsFinite(value.doubleValue) || value.doubleValue <= 0)
            {
                continue;
            }

            _groupSums[counter.Group] += value.doubleValue;
        }

        var busiestEngine = 0.0;
        foreach (var group in _groupSums)
        {
            busiestEngine = Math.Max(busiestEngine, group);
        }

        _lastValue = Math.Clamp(busiestEngine, 0, 100);
        return _lastValue;
    }

    private void RefreshCounters(long now)
    {
        CloseQuery();
        _nextRefresh = now + RefreshInterval;

        if (Native.PdhOpenQueryW(null, 0, out _query) != 0)
        {
            _query = 0;
            return;
        }

        var length = 0u;
        var expandStatus = Native.PdhExpandWildCardPathW(null, CounterPattern, null, ref length, 0);
        if (expandStatus != Native.PDH_MORE_DATA || length == 0)
        {
            CloseQuery();
            return;
        }

        var pathsBuffer = new char[length];
        expandStatus = Native.PdhExpandWildCardPathW(null, CounterPattern, pathsBuffer, ref length, 0);
        if (expandStatus != 0)
        {
            CloseQuery();
            return;
        }

        var counters = new List<CounterEntry>(64);
        var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var position = 0;

        while (position < pathsBuffer.Length && pathsBuffer[position] != '\0')
        {
            var end = position;
            while (end < pathsBuffer.Length && pathsBuffer[end] != '\0')
            {
                end++;
            }

            var path = new string(pathsBuffer, position, end - position);
            if (Native.PdhAddEnglishCounterW(_query, path, 0, out var handle) == 0)
            {
                var groupKey = GetEngineKey(path);
                if (!groups.TryGetValue(groupKey, out var groupIndex))
                {
                    groupIndex = groups.Count;
                    groups.Add(groupKey, groupIndex);
                }
                counters.Add(new CounterEntry(handle, groupIndex));
            }

            position = end + 1;
        }

        _counters = counters.ToArray();
        _groupSums = new double[groups.Count];
        _hasBaseline = false;

        if (_counters.Length == 0)
        {
            CloseQuery();
            return;
        }

        Native.PdhCollectQueryData(_query);
    }

    private static string GetEngineKey(string path)
    {
        var start = path.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return path;
        }

        var end = path.IndexOf(')', start);
        return end > start ? path[start..end] : path[start..];
    }

    private void CloseQuery()
    {
        if (_query != 0)
        {
            Native.PdhCloseQuery(_query);
            _query = 0;
        }
        _counters = [];
        _groupSums = [];
        _hasBaseline = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseQuery();
    }

    private readonly record struct CounterEntry(nint Handle, int Group);
}
