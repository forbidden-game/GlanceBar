using System.Diagnostics;
using System.Net.NetworkInformation;

namespace GlanceBar;

internal sealed class MetricsSampler : IDisposable
{
    private readonly GpuSampler _gpu = new();
    private readonly ProcessDetailsSampler _details = new();
    private NetworkInterface? _activeInterface;
    private string _activeInterfaceId = string.Empty;
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private long _previousBytesIn;
    private long _previousBytesOut;
    private long _previousTimestamp;
    private int _networkRefreshCountdown;
    private bool _hasNetworkBaseline;
    private bool _disposed;

    internal double CpuPercent { get; private set; }
    internal double? CpuTemperature => _details.CpuTemperature;
    internal double GpuPercent { get; private set; } = -1;
    internal uint MemoryPercent { get; private set; }
    internal ulong TotalMemoryBytes { get; private set; }
    internal ulong UsedMemoryBytes { get; private set; }
    internal double DownloadBytesPerSecond { get; private set; }
    internal double UploadBytesPerSecond { get; private set; }
    internal string NetworkInterfaceName => _activeInterface?.Name ?? "无可用网络";
    internal CpuProcessUsage[] TopCpu => _details.TopCpu;
    internal MemoryProcessUsage[] TopMemory => _details.TopMemory;
    internal NetworkProcessUsage[] TopNetwork => _details.TopNetwork;
    internal bool NetworkDetailsAvailable => _details.NetworkDetailsAvailable;

    internal MetricsSampler()
    {
        CaptureCpuBaseline();
        CaptureNetworkBaseline();
        _previousTimestamp = Stopwatch.GetTimestamp();
        UpdateMemory();
    }

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
        UpdateNetwork(elapsedSeconds);
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

    private void CaptureNetworkBaseline()
    {
        RefreshNetworkInterface();
        if (TryReadNetworkTotals(out var bytesIn, out var bytesOut))
        {
            _previousBytesIn = bytesIn;
            _previousBytesOut = bytesOut;
            _hasNetworkBaseline = true;
        }
    }

    private void UpdateNetwork(double elapsedSeconds)
    {
        if (--_networkRefreshCountdown <= 0)
        {
            RefreshNetworkInterface();
        }

        if (!TryReadNetworkTotals(out var bytesIn, out var bytesOut))
        {
            DownloadBytesPerSecond = 0;
            UploadBytesPerSecond = 0;
            return;
        }

        if (_hasNetworkBaseline && bytesIn >= _previousBytesIn && bytesOut >= _previousBytesOut)
        {
            DownloadBytesPerSecond = (bytesIn - _previousBytesIn) / elapsedSeconds;
            UploadBytesPerSecond = (bytesOut - _previousBytesOut) / elapsedSeconds;
        }
        else
        {
            DownloadBytesPerSecond = 0;
            UploadBytesPerSecond = 0;
        }

        _previousBytesIn = bytesIn;
        _previousBytesOut = bytesOut;
        _hasNetworkBaseline = true;
    }

    private void RefreshNetworkInterface()
    {
        _networkRefreshCountdown = 15;
        if (Native.GetBestInterface(0x08080808, out var interfaceIndex) != 0)
        {
            SetActiveInterface(null);
            return;
        }

        NetworkInterface? selected = null;
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (networkInterface.OperationalStatus == OperationalStatus.Up &&
                    networkInterface.GetIPProperties().GetIPv4Properties()?.Index == interfaceIndex)
                {
                    selected = networkInterface;
                    break;
                }
            }
            catch (NetworkInformationException)
            {
                // An interface can disappear while Windows is enumerating adapters.
            }
        }
        SetActiveInterface(selected);
    }

    private void SetActiveInterface(NetworkInterface? networkInterface)
    {
        var newId = networkInterface?.Id ?? string.Empty;
        if (newId != _activeInterfaceId)
        {
            _activeInterfaceId = newId;
            _hasNetworkBaseline = false;
            DownloadBytesPerSecond = 0;
            UploadBytesPerSecond = 0;
        }
        _activeInterface = networkInterface;
    }

    private bool TryReadNetworkTotals(out long bytesIn, out long bytesOut)
    {
        bytesIn = 0;
        bytesOut = 0;
        if (_activeInterface is null)
        {
            return false;
        }

        try
        {
            var statistics = _activeInterface.GetIPStatistics();
            bytesIn = statistics.BytesReceived;
            bytesOut = statistics.BytesSent;
            return true;
        }
        catch (NetworkInformationException)
        {
            _networkRefreshCountdown = 0;
            return false;
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
