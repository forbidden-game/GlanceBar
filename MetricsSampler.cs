using System.Diagnostics;

namespace GlanceBar;

internal sealed class MetricsSampler : IDisposable
{
    private readonly GpuSampler _gpu = new();
    private readonly ProcessDetailsSampler _details = new();
    private readonly NetworkSampler _network = new();
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private long _previousTimestamp;
    private bool _disposed;

    internal double CpuPercent { get; private set; }
    internal double? CpuTemperature => _details.CpuTemperature;
    internal double GpuPercent { get; private set; } = -1;
    internal uint MemoryPercent { get; private set; }
    internal ulong TotalMemoryBytes { get; private set; }
    internal ulong UsedMemoryBytes { get; private set; }
    internal double DownloadBytesPerSecond => _network.DownloadBytesPerSecond;
    internal double UploadBytesPerSecond => _network.UploadBytesPerSecond;
    internal string NetworkInterfaceName => _network.InterfaceNames;
    internal CpuProcessUsage[] TopCpu => _details.TopCpu;
    internal MemoryProcessUsage[] TopMemory => _details.TopMemory;
    internal NetworkProcessUsage[] TopNetwork => _details.TopNetwork;
    internal bool NetworkDetailsAvailable => _details.NetworkDetailsAvailable;

    // Establish CPU and physical-interface baselines before calculating interval readings.
    internal MetricsSampler()
    {
        CaptureCpuBaseline();
        _network.Sample();
        _previousTimestamp = Stopwatch.GetTimestamp();
        UpdateMemory();
    }

    // Sample system metrics; network intervals and topology are owned by the physical-interface sampler.
    internal void Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = (now - _previousTimestamp) / (double)Stopwatch.Frequency;
        if (elapsedSeconds <= 0)
        {
            return;
        }

        UpdateCpu();
        UpdateMemory();
        _network.Sample();
        GpuPercent = _gpu.Sample();
        _details.Sample(elapsedSeconds);
        _previousTimestamp = now;
    }

    private void CaptureCpuBaseline()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return;
        }

        _previousIdle = idle.Value;
        _previousKernel = kernel.Value;
        _previousUser = user.Value;
    }

    private void UpdateCpu()
    {
        if (!Native.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return;
        }

        var idle = idleTime.Value;
        var kernel = kernelTime.Value;
        var user = userTime.Value;
        var idleDelta = idle - _previousIdle;
        var totalDelta = (kernel - _previousKernel) + (user - _previousUser);

        if (totalDelta > 0)
        {
            CpuPercent = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
        }

        _previousIdle = idle;
        _previousKernel = kernel;
        _previousUser = user;
    }

    private static Native.MEMORYSTATUSEX NewMemoryStatus()
    {
        var status = new Native.MEMORYSTATUSEX();
        status.length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>();
        return status;
    }

    private void UpdateMemory()
    {
        var status = NewMemoryStatus();
        if (Native.GlobalMemoryStatusEx(ref status))
        {
            MemoryPercent = status.memoryLoad;
            TotalMemoryBytes = status.totalPhysical;
            UsedMemoryBytes = status.totalPhysical - status.availablePhysical;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _details.Dispose();
        _gpu.Dispose();
    }
}
