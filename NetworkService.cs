using System.Diagnostics;
using System.ServiceProcess;

namespace GlanceBar;

internal sealed class NetworkService : ServiceBase
{
    private readonly ManualResetEventSlim _stop = new(false);
    private NetworkTraceSampler? _sampler;
    private Task? _worker;

    internal NetworkService()
    {
        ServiceName = NetworkHelper.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        try
        {
            // Setup creates this directory with SYSTEM/Admin write and Users read permissions.
            Directory.CreateDirectory(NetworkHelper.DataDirectory);
            _sampler = new NetworkTraceSampler();
            if (!_sampler.IsAvailable)
            {
                throw new InvalidOperationException("Cannot start the Windows kernel network trace.", _sampler.Failure);
            }
            _stop.Reset();
            _worker = Task.Run(SampleLoop);
        }
        catch (Exception exception)
        {
            WriteFailure(exception);
            _sampler?.Dispose();
            _sampler = null;
            throw;
        }
    }

    private void SampleLoop()
    {
        var timestamp = Stopwatch.GetTimestamp();
        try
        {
            while (!_stop.Wait(TimeSpan.FromSeconds(2)))
            {
                var sampler = _sampler!;
                if (sampler.Failure is { } failure)
                {
                    throw new InvalidOperationException("The kernel network trace stopped unexpectedly.", failure);
                }
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now - timestamp) / (double)Stopwatch.Frequency;
                NetworkHelper.WriteSnapshot(sampler.TakeTop(elapsed));
                timestamp = now;
            }
        }
        catch (Exception exception)
        {
            WriteFailure(exception);
            // A failed collector must not appear healthy to SCM or leave fresh-looking stale data.
            _sampler?.Dispose();
            Environment.Exit(1);
        }
    }

    protected override void OnStop()
    {
        _stop.Set();
        _worker?.GetAwaiter().GetResult();
        _worker = null;
        _sampler?.Dispose();
        _sampler = null;
    }

    protected override void OnShutdown() => OnStop();

    private static void WriteFailure(Exception exception)
    {
        try
        {
            File.AppendAllText(Path.Combine(NetworkHelper.DataDirectory, "service-error.log"),
                $"{DateTimeOffset.Now:O} {exception}\r\n");
        }
        catch (IOException)
        {
            // SCM still receives the failed start or nonzero process exit if logging is unavailable.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            OnStop();
            _stop.Dispose();
        }
        base.Dispose(disposing);
    }
}
