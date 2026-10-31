using System.ComponentModel;
using System.Diagnostics;

namespace GlanceBar;

internal enum NetworkTrafficKind
{
    External,
    Local,
    Unknown,
    Summary
}

// A bounded preorder forest: depth establishes placement; parent and child rates describe different hops.
internal readonly record struct NetworkProcessUsage(int ProcessId, int ParentProcessId, int Depth, string Name,
    double DownloadBytesPerSecond, double UploadBytesPerSecond, NetworkTrafficKind Kind)
{
    internal const int MaxRows = 32;
}

// Parent 0 denotes external traffic; -1 denotes local traffic whose listener could not be identified.
internal readonly record struct NetworkTrafficKey(int ProcessId, int ParentProcessId);
internal readonly record struct NetworkTrafficSample(NetworkTrafficKey Key, ulong Download, ulong Upload);

// Keep separate traffic buckets for direct connections and each local upstream, then select complete tree branches.
internal sealed class NetworkTrafficTree
{
    private const int TopCount = 5;
    private readonly Dictionary<NetworkTrafficKey, Node> _nodes = new();
    private readonly HashSet<int> _externalProcesses = new();
    private readonly Dictionary<int, string> _names = new();
    private readonly List<Node> _roots = new();
    private readonly List<NetworkProcessUsage> _result = new(NetworkProcessUsage.MaxRows);
    private int _generation;
    private double _elapsedSeconds;

    // Build from one collection interval; ambiguous upstreams remain independent rather than receiving a guessed parent.
    internal NetworkProcessUsage[] Build(IReadOnlyList<NetworkTrafficSample> samples,
        NetworkConnectionSnapshot connections, double elapsedSeconds)
    {
        _generation++;
        _elapsedSeconds = elapsedSeconds;
        _externalProcesses.Clear();
        _roots.Clear();
        _result.Clear();
        _names.Clear();
        foreach (var node in _nodes.Values)
        {
            node.Download = 0;
            node.Upload = 0;
            node.Parent = null;
            node.Children.Clear();
        }
        foreach (var sample in samples)
        {
            if (sample.Key.ParentProcessId == 0)
            {
                _externalProcesses.Add(sample.Key.ProcessId);
            }
        }
        foreach (var sample in samples)
        {
            if (sample.Download == 0 && sample.Upload == 0)
            {
                continue;
            }
            var node = GetNode(sample.Key, connections);
            node.Download += sample.Download;
            node.Upload += sample.Upload;
        }
        foreach (var pair in _nodes)
        {
            if (pair.Value.Generation != _generation)
            {
                _nodes.Remove(pair.Key);
            }
            else if (pair.Value.Parent is null)
            {
                _roots.Add(pair.Value);
            }
        }
        foreach (var root in _roots)
        {
            Rank(root);
        }
        _roots.Sort(CompareNodes);

        var selected = Math.Min(TopCount, _roots.Count);
        var omittedRoots = _roots.Count - selected;
        for (var index = 0; index < selected; index++)
        {
            // Reserve one row for every later root and the final omission notice.
            var budget = NetworkProcessUsage.MaxRows - _result.Count - (selected - index - 1) -
                (omittedRoots > 0 ? 1 : 0);
            Append(_roots[index], 0, 0, budget);
        }
        if (omittedRoots > 0)
        {
            _result.Add(new NetworkProcessUsage(0, 0, 0, $"另有 {omittedRoots} 组", 0, 0, NetworkTrafficKind.Summary));
        }
        return _result.ToArray();
    }

    // Place a confirmed client under its listener; only a unique, non-mixed upstream can extend that listener's chain.
    private Node GetNode(NetworkTrafficKey key, NetworkConnectionSnapshot connections)
    {
        if (!_nodes.TryGetValue(key, out var node))
        {
            node = new Node(key);
            _nodes.Add(key, node);
        }
        if (node.Generation == _generation)
        {
            return node;
        }
        node.Generation = _generation;
        node.Kind = key.ParentProcessId == 0 ? NetworkTrafficKind.External : NetworkTrafficKind.Local;
        if (key.ParentProcessId > 0)
        {
            var server = key.ParentProcessId;
            var upstream = _externalProcesses.Contains(server) ? 0 : connections.GetUniqueUpstream(server);
            var parent = GetNode(new NetworkTrafficKey(server, upstream), connections);
            node.Parent = parent;
            parent.Children.Add(node);
        }
        if (key.ParentProcessId == 0 && !_externalProcesses.Contains(key.ProcessId) &&
            !connections.HasExternalConnections(key.ProcessId))
        {
            node.Kind = NetworkTrafficKind.Unknown;
        }
        return node;
    }

    // Rank by the larger of a node's own hop and its descendants, never by adding both copies of a transfer.
    private static void Rank(Node node)
    {
        var descendants = 0.0;
        node.RowCount = 1;
        foreach (var child in node.Children)
        {
            Rank(child);
            descendants += child.Score;
            node.RowCount += child.RowCount;
        }
        node.Score = Math.Max((double)node.Download + node.Upload, descendants);
        node.Children.Sort(CompareNodes);
    }

    // Order equally active processes deterministically so the popup does not shuffle on every refresh.
    private static int CompareNodes(Node first, Node second)
    {
        var score = second.Score.CompareTo(first.Score);
        if (score != 0) return score;
        var process = first.Key.ProcessId.CompareTo(second.Key.ProcessId);
        return process != 0 ? process : first.Key.ParentProcessId.CompareTo(second.Key.ParentProcessId);
    }

    // Emit a whole parent before children, retaining an explicit count whenever space or the per-group limit omits rows.
    private int Append(Node node, int depth, int parentProcessId, int budget)
    {
        var name = ResolveName(node.Key.ProcessId);
        if (budget == 1 && node.Children.Count > 0)
        {
            name += $"（另有 {node.RowCount - 1} 项）";
        }
        _result.Add(new NetworkProcessUsage(node.Key.ProcessId, parentProcessId, depth, name,
            node.Download / _elapsedSeconds, node.Upload / _elapsedSeconds, node.Kind));
        var used = 1;
        var emitted = 0;
        var selected = Math.Min(TopCount, node.Children.Count);
        while (emitted < selected && budget - used > 1)
        {
            used += Append(node.Children[emitted], depth + 1, node.Key.ProcessId, budget - used - 1);
            emitted++;
        }
        if (emitted < node.Children.Count && used < budget)
        {
            var omitted = 0;
            for (var index = emitted; index < node.Children.Count; index++)
            {
                omitted += node.Children[index].RowCount;
            }
            _result.Add(new NetworkProcessUsage(0, node.Key.ProcessId, depth + 1,
                $"另有 {omitted} 项", 0, 0, NetworkTrafficKind.Summary));
            used++;
        }
        return used;
    }

    // Resolve each selected PID once per snapshot; no persistent PID-only cache can mislabel a reused process ID.
    private string ResolveName(int processId)
    {
        if (_names.TryGetValue(processId, out var name)) return name;
        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            name = $"PID {processId}";
        }
        catch (InvalidOperationException)
        {
            name = $"PID {processId}";
        }
        catch (Win32Exception)
        {
            name = $"PID {processId}";
        }
        _names.Add(processId, name);
        return name;
    }

    // Reuse active graph nodes and their child lists between samples; published rows remain immutable values.
    private sealed class Node(NetworkTrafficKey key)
    {
        internal NetworkTrafficKey Key { get; } = key;
        internal List<Node> Children { get; } = new();
        internal Node? Parent;
        internal ulong Download;
        internal ulong Upload;
        internal NetworkTrafficKind Kind;
        internal int Generation;
        internal int RowCount;
        internal double Score;
    }
}
