using System.Globalization;
using System.ServiceProcess;
using System.Text;

namespace GlanceBar;

internal static class NetworkHelper
{
    internal const string ServiceArgument = "--network-service";
    internal const string ServiceName = "GlanceBarNetwork";

    internal static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GlanceBar");

    private static string SnapshotPath => Path.Combine(DataDirectory, "network-processes.tsv");

    internal static int RunService()
    {
        using var service = new NetworkService();
        ServiceBase.Run(service);
        return 0;
    }

    // Publish network usage and the latest CPU temperature as one atomic snapshot.
    internal static void WriteSnapshot(NetworkProcessUsage[] processes, double? cpuTemperature)
    {
        var text = new StringBuilder(512);
        text.Append(DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
            .Append(cpuTemperature?.ToString("R", CultureInfo.InvariantCulture) ?? "").AppendLine();
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

    // Reject stale snapshots so a stopped service cannot leave a misleading temperature.
    internal static NetworkProcessUsage[] ReadSnapshot(out bool isFresh, out double? cpuTemperature)
    {
        isFresh = false;
        cpuTemperature = null;
        try
        {
            // The service replaces snapshots atomically; readers must allow replacement.
            using var stream = new FileStream(SnapshotPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var header = reader.ReadLine()?.Split('\t');
            if (header is null || header.Length != 2 ||
                !long.TryParse(header[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks ||
                DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromSeconds(6))
            {
                return [];
            }
            if (double.TryParse(header[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) &&
                double.IsFinite(temperature))
            {
                cpuTemperature = temperature;
            }


            var result = new List<NetworkProcessUsage>(5);
            while (result.Count < 5 && reader.ReadLine() is { } line)
            {
                var fields = line.Split('\t');
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
