using System.Diagnostics;

namespace TaskbarPulse;

internal readonly record struct CpuProcessUsage(int ProcessId, string Name, double Percent);
internal readonly record struct MemoryProcessUsage(int ProcessId, string Name, long WorkingSetBytes);

internal sealed class ProcessDetailsSampler : IDisposable
{
    private const int TopCount = 5;
    private readonly Dictionary<int, ProcessState> _states = new();
    private int _generation;

    internal CpuProcessUsage[] TopCpu { get; private set; } = [];
    internal MemoryProcessUsage[] TopMemory { get; private set; } = [];
    internal NetworkProcessUsage[] TopNetwork { get; private set; } = [];
    internal bool NetworkDetailsAvailable { get; private set; }

    internal void Sample(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0)
        {
            return;
        }

        _generation++;
        var topCpu = new CpuProcessUsage[TopCount];
        var topMemory = new MemoryProcessUsage[TopCount];
        var cpuCount = 0;
        var memoryCount = 0;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var processId = process.Id;
                    if (processId == Environment.ProcessId)
                    {
                        continue;
                    }

                    var cpuTicks = process.TotalProcessorTime.Ticks;
                    var workingSet = process.WorkingSet64;
                    if (!_states.TryGetValue(processId, out var state))
                    {
                        state = new ProcessState(process.ProcessName, cpuTicks, _generation);
                        _states.Add(processId, state);
                    }

                    var cpuPercent = Math.Max(0,
                        (cpuTicks - state.CpuTicks) /
                        (elapsedSeconds * TimeSpan.TicksPerSecond * Environment.ProcessorCount) * 100.0);
                    state.CpuTicks = cpuTicks;
                    state.Generation = _generation;

                    InsertCpu(topCpu, ref cpuCount, new CpuProcessUsage(processId, state.Name, cpuPercent));
                    InsertMemory(topMemory, ref memoryCount, new MemoryProcessUsage(processId, state.Name, workingSet));
                }
                catch
                {
                    // Protected and exiting processes are expected while taking a snapshot.
                }
            }
        }

        TopCpu = topCpu[..cpuCount];
        TopMemory = topMemory[..memoryCount];
        TopNetwork = NetworkHelper.ReadSnapshot(out var isFresh);
        NetworkDetailsAvailable = isFresh;

        if (_generation % 30 == 0)
        {
            var stale = _states.Where(pair => pair.Value.Generation < _generation - 2)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var processId in stale)
            {
                _states.Remove(processId);
            }
        }
    }

    private static void InsertCpu(CpuProcessUsage[] values, ref int count, CpuProcessUsage candidate)
    {
        var index = count;
        while (index > 0 && values[index - 1].Percent < candidate.Percent)
        {
            index--;
        }
        if (index >= values.Length)
        {
            return;
        }

        var upper = Math.Min(count, values.Length - 1);
        for (var move = upper; move > index; move--)
        {
            values[move] = values[move - 1];
        }
        values[index] = candidate;
        if (count < values.Length)
        {
            count++;
        }
    }

    private static void InsertMemory(MemoryProcessUsage[] values, ref int count, MemoryProcessUsage candidate)
    {
        var index = count;
        while (index > 0 && values[index - 1].WorkingSetBytes < candidate.WorkingSetBytes)
        {
            index--;
        }
        if (index >= values.Length)
        {
            return;
        }

        var upper = Math.Min(count, values.Length - 1);
        for (var move = upper; move > index; move--)
        {
            values[move] = values[move - 1];
        }
        values[index] = candidate;
        if (count < values.Length)
        {
            count++;
        }
    }

    public void Dispose()
    {
    }

    private sealed class ProcessState(string name, long cpuTicks, int generation)
    {
        internal string Name { get; } = name;
        internal long CpuTicks { get; set; } = cpuTicks;
        internal int Generation { get; set; } = generation;
    }
}
