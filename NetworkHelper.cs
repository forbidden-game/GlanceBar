using System.Globalization;
using System.ServiceProcess;
using System.Text;

namespace GlanceBar;

internal static class NetworkHelper
{
    internal const string ServiceArgument = "--network-service";
    internal const string ServiceName = "GlanceBarNetwork";
    private const string SnapshotVersion = "2";

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

    // Publish a bounded preorder traffic forest and CPU temperature as one versioned atomic snapshot.
    internal static void WriteSnapshot(NetworkProcessUsage[] processes, double? cpuTemperature)
    {
        var text = new StringBuilder(512);
        text.Append(SnapshotVersion).Append('\t')
            .Append(DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
            .Append(cpuTemperature?.ToString("R", CultureInfo.InvariantCulture) ?? "").AppendLine();
        foreach (var process in processes)
        {
            text.Append(process.ProcessId).Append('\t')
                .Append(process.ParentProcessId).Append('\t')
                .Append(process.Depth).Append('\t')
                .Append((int)process.Kind).Append('\t')
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(process.Name))).Append('\t')
                .Append(process.DownloadBytesPerSecond.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                .Append(process.UploadBytesPerSecond.ToString("R", CultureInfo.InvariantCulture)).AppendLine();
        }

        var temporary = SnapshotPath + ".tmp";
        File.WriteAllText(temporary, text.ToString(), Encoding.UTF8);
        File.Move(temporary, SnapshotPath, overwrite: true);
    }

    // Validate the complete preorder forest at the file boundary; never truncate a tree or guess a missing parent.
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
            if (header is null || header.Length != 3 || header[0] != SnapshotVersion ||
                !long.TryParse(header[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks ||
                DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromSeconds(6))
            {
                return [];
            }
            if (double.TryParse(header[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) &&
                double.IsFinite(temperature))
            {
                cpuTemperature = temperature;
            }


            var result = new List<NetworkProcessUsage>(NetworkProcessUsage.MaxRows);
            Span<int> ancestors = stackalloc int[NetworkProcessUsage.MaxRows];
            var previousDepth = -1;
            while (reader.ReadLine() is { } line)
            {
                var fields = line.Split('\t');
                if (result.Count == NetworkProcessUsage.MaxRows || fields.Length != 7 ||
                    !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId) ||
                    !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentId) ||
                    !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth) ||
                    !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kind) ||
                    kind < 0 || kind > (int)NetworkTrafficKind.Summary || processId < 0 ||
                    (processId == 0) != (kind == (int)NetworkTrafficKind.Summary) ||
                    depth < 0 || depth >= NetworkProcessUsage.MaxRows || depth > previousDepth + 1 ||
                    (depth == 0 ? parentId != 0 : parentId <= 0 || ancestors[depth - 1] != parentId) ||
                    !double.TryParse(fields[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var download) ||
                    !double.TryParse(fields[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var upload) ||
                    !double.IsFinite(download) || !double.IsFinite(upload) || download < 0 || upload < 0)
                {
                    cpuTemperature = null;
                    return [];
                }
                var name = Encoding.UTF8.GetString(Convert.FromBase64String(fields[4]));
                result.Add(new NetworkProcessUsage(processId, parentId, depth, name, download, upload,
                    (NetworkTrafficKind)kind));
                ancestors[depth] = processId;
                previousDepth = depth;
            }
            isFresh = true;
            return result.ToArray();
        }
        catch (IOException)
        {
            cpuTemperature = null;
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            cpuTemperature = null;
            return [];
        }
        catch (FormatException)
        {
            cpuTemperature = null;
            return [];
        }
    }
}
