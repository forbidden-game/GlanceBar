using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TaskbarPulse;

internal static class NetworkHelper
{
    internal const string HelperArgument = "--network-helper";
    internal const string InstallArgument = "--install-network-helper";
    internal const string UninstallArgument = "--uninstall-network-helper";
    internal const string ScheduledTaskName = "TaskbarPulse Network Helper";
    private const string MutexName = @"Global\TaskbarPulse.NetworkHelper.6D9AA8A2";

    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarPulse");

    private static string SnapshotPath => Path.Combine(DataDirectory, "network-processes.tsv");

    internal static int Run()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            return 0;
        }

        using var sampler = new NetworkTraceSampler();
        if (!sampler.IsAvailable)
        {
            return 2;
        }

        Directory.CreateDirectory(DataDirectory);
        while (true)
        {
            Thread.Sleep(2000);
            WriteSnapshot(sampler.TakeTop(2.0));
        }
    }

    internal static int Install()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return 3;
        }

        var create = RunTaskScheduler(
            "/Create",
            "/TN", ScheduledTaskName,
            "/TR", $"\"{executable}\" {HelperArgument}",
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/F");
        if (create != 0)
        {
            return create;
        }

        return RunTaskScheduler("/Run", "/TN", ScheduledTaskName);
    }

    internal static int Uninstall()
    {
        return RunTaskScheduler("/Delete", "/TN", ScheduledTaskName, "/F");
    }

    private static int RunTaskScheduler(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return 4;
        }
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void WriteSnapshot(NetworkProcessUsage[] processes)
    {
        var text = new StringBuilder(512);
        text.Append(DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)).AppendLine();
        foreach (var process in processes)
        {
            text.Append(process.ProcessId).Append('\t')
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(process.Name))).Append('\t')
                .Append(process.DownloadBytesPerSecond.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                .Append(process.UploadBytesPerSecond.ToString("R", CultureInfo.InvariantCulture)).AppendLine();
        }

        var temporary = SnapshotPath + ".tmp";
        File.WriteAllText(temporary, text.ToString(), Encoding.UTF8);
        File.Move(temporary, SnapshotPath, overwrite: true);
    }

    internal static NetworkProcessUsage[] ReadSnapshot(out bool isFresh)
    {
        isFresh = false;
        try
        {
            var lines = File.ReadAllLines(SnapshotPath, Encoding.UTF8);
            if (lines.Length == 0 ||
                !long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ||
                DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromSeconds(6))
            {
                return [];
            }

            var result = new List<NetworkProcessUsage>(Math.Min(5, lines.Length - 1));
            for (var index = 1; index < lines.Length && result.Count < 5; index++)
            {
                var fields = lines[index].Split('\t');
                if (fields.Length != 4 ||
                    !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId) ||
                    !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var download) ||
                    !double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var upload))
                {
                    continue;
                }

                var name = Encoding.UTF8.GetString(Convert.FromBase64String(fields[1]));
                result.Add(new NetworkProcessUsage(processId, name, download, upload));
            }
            isFresh = true;
            return result.ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (FormatException)
        {
            return [];
        }
    }
}
