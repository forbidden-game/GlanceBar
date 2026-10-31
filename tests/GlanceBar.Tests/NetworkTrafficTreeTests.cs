using System.Net;

namespace GlanceBar;

public sealed class NetworkTrafficTreeTests
{
    // A local transfer belongs to the listening PID, not to the socket's server-side mirror.
    [Fact]
    public void ResolvesClientAndServerSides()
    {
        var sockets = new List<NetworkConnections.TcpSocket> { Listen(1001, 57777), External(1001) };
        Connect(sockets, 1002, 1001, 50001, 57777);
        var topology = new NetworkConnectionSnapshot(sockets);
        Assert.Equal(1001, topology.FindLocalServer(1002, Endpoint("127.0.0.1", 50001), Endpoint("127.0.0.1", 57777)));
        Assert.True(topology.IsLocalServer(1001, Endpoint("127.0.0.1", 57777)));
        Assert.Equal(0, topology.FindLocalServer(1001, Endpoint("127.0.0.1", 57777), Endpoint("127.0.0.1", 50001)));
    }

    // Native IPv4 and mapped IPv6 endpoints must identify the same local proxy connection.
    [Theory]
    [InlineData("127.0.0.1", "::ffff:127.0.0.1")]
    [InlineData("::1", "::1")]
    public void NormalizesBothIpFamilies(string tableAddress, string eventAddress)
    {
        var sockets = new List<NetworkConnections.TcpSocket> { Listen(1001, 57777, tableAddress) };
        Connect(sockets, 1002, 1001, 50001, 57777, tableAddress);
        var topology = new NetworkConnectionSnapshot(sockets);
        Assert.Equal(1001, topology.FindLocalServer(1002, Endpoint(eventAddress, 50001), Endpoint(eventAddress, 57777)));
    }

    // A wildcard listener's owner is usable when no conflicting listener or reverse socket owner exists.
    [Fact]
    public void ResolvesWildcardListenerWithoutGuessingConflictingOwners()
    {
        var listener = Listen(1001, 57777, "0.0.0.0");
        var topology = new NetworkConnectionSnapshot([listener]);
        Assert.Equal(1001, topology.FindLocalServer(1002, Endpoint("127.0.0.1", 50001), Endpoint("127.0.0.1", 57777)));
        topology = new NetworkConnectionSnapshot([listener, Listen(1003, 57777)]);
        Assert.Equal(0, topology.FindLocalServer(1002, Endpoint("127.0.0.1", 50001), Endpoint("127.0.0.1", 57777)));
    }

    // One application's direct bytes must not be moved into its separate proxy branch or added to proxy totals.
    [Fact]
    public void KeepsMixedDirectAndProxyTrafficSeparate()
    {
        var sockets = new List<NetworkConnections.TcpSocket> { Listen(1001, 57777), External(1001), External(1002) };
        Connect(sockets, 1002, 1001, 50001, 57777);
        var rows = new NetworkTrafficTree().Build([
            Sample(1001, 0, 950), Sample(1002, 1001, 900), Sample(1002, 0, 100)
        ], new NetworkConnectionSnapshot(sockets), 2);
        var proxy = Assert.Single(rows, row => row.ProcessId == 1001);
        Assert.Equal(475, proxy.DownloadBytesPerSecond);
        var appProxy = Assert.Single(rows, row => row.ProcessId == 1002 && row.Depth == 1);
        Assert.Equal(1001, appProxy.ParentProcessId);
        Assert.Equal(NetworkTrafficKind.Local, appProxy.Kind);
        Assert.Equal(450, appProxy.DownloadBytesPerSecond);
        var appDirect = Assert.Single(rows, row => row.ProcessId == 1002 && row.Depth == 0);
        Assert.Equal(NetworkTrafficKind.External, appDirect.Kind);
        Assert.Equal(50, appDirect.DownloadBytesPerSecond);
    }

    // A quiet external parent must remain visible while it serves an active local child.
    [Fact]
    public void RetainsParentWithoutSampledExternalBytes()
    {
        var sockets = new List<NetworkConnections.TcpSocket> { Listen(1001, 57777), External(1001) };
        Connect(sockets, 1002, 1001, 50001, 57777);
        var rows = new NetworkTrafficTree().Build([Sample(1002, 1001, 800)], new NetworkConnectionSnapshot(sockets), 2);
        Assert.Equal([1001, 1002], rows.Select(row => row.ProcessId));
        Assert.Equal(NetworkTrafficKind.External, rows[0].Kind);
        Assert.Equal(0, rows[0].DownloadBytesPerSecond);
        Assert.Equal(400, rows[1].DownloadBytesPerSecond);
    }

    // A local server with no proved external path is a container, not an invented direct-traffic reading.
    [Fact]
    public void MarksUnprovedParentAsUnknown()
    {
        var sockets = new List<NetworkConnections.TcpSocket> { Listen(1001, 57777) };
        Connect(sockets, 1002, 1001, 50001, 57777);
        var rows = new NetworkTrafficTree().Build([Sample(1002, 1001, 800)], new NetworkConnectionSnapshot(sockets), 2);
        Assert.Equal(NetworkTrafficKind.Unknown, rows[0].Kind);
        Assert.Equal(1001, rows[1].ParentProcessId);
    }

    // A unique local upstream permits a real multi-hop application-to-relay-to-proxy chain.
    [Fact]
    public void BuildsMultiHopProxyChain()
    {
        var sockets = new List<NetworkConnections.TcpSocket> { Listen(1001, 57777), Listen(1002, 57778), External(1001) };
        Connect(sockets, 1002, 1001, 50001, 57777);
        Connect(sockets, 1003, 1002, 50002, 57778);
        var rows = new NetworkTrafficTree().Build([
            Sample(1001, 0, 900), Sample(1002, 1001, 800), Sample(1003, 1002, 700)
        ], new NetworkConnectionSnapshot(sockets), 2);
        Assert.Equal([1001, 1002, 1003], rows.Select(row => row.ProcessId));
        Assert.Equal([0, 1, 2], rows.Select(row => row.Depth));
        Assert.Equal([0, 1001, 1002], rows.Select(row => row.ParentProcessId));
        Assert.Equal([450.0, 400.0, 350.0], rows.Select(row => row.DownloadBytesPerSecond));
    }

    // Children of a multi-upstream relay must not be duplicated or attributed to an arbitrary proxy.
    [Fact]
    public void LeavesAmbiguousRelayContextIndependent()
    {
        var sockets = new List<NetworkConnections.TcpSocket> {
            Listen(1001, 57777), Listen(1004, 57779), Listen(1002, 57778), External(1001), External(1004)
        };
        Connect(sockets, 1002, 1001, 50001, 57777);
        Connect(sockets, 1002, 1004, 50003, 57779);
        Connect(sockets, 1003, 1002, 50002, 57778);
        var rows = new NetworkTrafficTree().Build([
            Sample(1002, 1001, 500), Sample(1002, 1004, 300), Sample(1003, 1002, 600)
        ], new NetworkConnectionSnapshot(sockets), 2);
        var app = Assert.Single(rows, row => row.ProcessId == 1003);
        Assert.Equal(1002, app.ParentProcessId);
        Assert.Equal(1, app.Depth);
        Assert.Contains(rows, row => row.ProcessId == 1002 && row.Depth == 0 && row.Kind == NetworkTrafficKind.Unknown);
    }

    // Mutually connected local services cannot create a recursive ownership tree.
    [Fact]
    public void RejectsConnectionCycles()
    {
        var sockets = new List<NetworkConnections.TcpSocket> { Listen(1001, 57777), Listen(1002, 57778) };
        Connect(sockets, 1001, 1002, 50001, 57778);
        Connect(sockets, 1002, 1001, 50002, 57777);
        var topology = new NetworkConnectionSnapshot(sockets);
        Assert.Equal(0, topology.GetUniqueUpstream(1001));
        Assert.Equal(0, topology.GetUniqueUpstream(1002));
        Assert.Equal(0, topology.FindLocalServer(1001, Endpoint("127.0.0.1", 50001), Endpoint("127.0.0.1", 57778)));
    }

    // A reused builder must remove expired branches and must not carry bytes into a later interval.
    [Fact]
    public void ClearsExpiredBranchesBetweenIntervals()
    {
        var topology = new NetworkConnectionSnapshot([Listen(1001, 57777), External(1001)]);
        var tree = new NetworkTrafficTree();
        tree.Build([Sample(1001, 0, 900), Sample(1002, 1001, 800)], topology, 2);
        var rows = tree.Build([Sample(1004, 0, 100)], topology, 2);
        Assert.Equal(1004, Assert.Single(rows).ProcessId);
        Assert.Equal(50, rows[0].DownloadBytesPerSecond);
        Assert.Empty(tree.Build([], topology, 2));
    }

    // Truncation must retain the parent and visibly account for omitted descendants.
    [Fact]
    public void ReportsOmittedChildrenUnderTheirParent()
    {
        var samples = new List<NetworkTrafficSample> { Sample(1001, 0, 10000) };
        for (var index = 0; index < 12; index++) samples.Add(Sample(1100 + index, 1001, (ulong)(100 + index)));
        var rows = new NetworkTrafficTree().Build(samples,
            new NetworkConnectionSnapshot([Listen(1001, 57777), External(1001)]), 2);
        Assert.Equal(1001, rows[0].ProcessId);
        Assert.Equal(5, rows.Count(row => row.Kind == NetworkTrafficKind.Local));
        var summary = Assert.Single(rows, row => row.Kind == NetworkTrafficKind.Summary);
        Assert.Equal(1001, summary.ParentProcessId);
        Assert.Equal(1, summary.Depth);
        Assert.Contains("7", summary.Name);
    }

    // Unmatched local traffic remains visible rather than silently deleting the original application again.
    [Fact]
    public void KeepsUnclassifiedLocalActivity()
    {
        var rows = new NetworkTrafficTree().Build([Sample(1002, -1, 800)], new NetworkConnectionSnapshot([]), 2);
        var app = Assert.Single(rows);
        Assert.Equal(1002, app.ProcessId);
        Assert.Equal(0, app.Depth);
        Assert.Equal(NetworkTrafficKind.Local, app.Kind);
        Assert.Equal(400, app.DownloadBytesPerSecond);
    }

    // Concurrent connections to two proxies must preserve both application buckets without inventing a single parent.
    [Fact]
    public void SplitsOneApplicationAcrossTwoProxies()
    {
        var sockets = new List<NetworkConnections.TcpSocket> {
            Listen(1001, 57777), Listen(1004, 57779), External(1001), External(1004)
        };
        Connect(sockets, 1002, 1001, 50001, 57777);
        Connect(sockets, 1002, 1004, 50002, 57779);
        var rows = new NetworkTrafficTree().Build([
            Sample(1002, 1001, 400), Sample(1002, 1004, 600)
        ], new NetworkConnectionSnapshot(sockets), 2);
        var first = Assert.Single(rows, row => row.ProcessId == 1002 && row.ParentProcessId == 1001);
        var second = Assert.Single(rows, row => row.ProcessId == 1002 && row.ParentProcessId == 1004);
        Assert.Equal(200, first.DownloadBytesPerSecond);
        Assert.Equal(300, second.DownloadBytesPerSecond);
        Assert.DoesNotContain(rows, row => row.ProcessId == 1002 && row.Depth == 0);
    }

    // A long forwarding chain must fit the wire format without producing orphan children or invalid depth jumps.
    [Fact]
    public void BoundsDeepChainsWithoutOrphaningRows()
    {
        var sockets = new List<NetworkConnections.TcpSocket> { External(2000) };
        var samples = new List<NetworkTrafficSample> { Sample(2000, 0, 10000) };
        for (var index = 0; index < 40; index++)
        {
            sockets.Add(Listen(2000 + index, 30000 + index));
            Connect(sockets, 2001 + index, 2000 + index, 40000 + index, 30000 + index);
            samples.Add(Sample(2001 + index, 2000 + index, 9000));
        }
        var rows = new NetworkTrafficTree().Build(samples, new NetworkConnectionSnapshot(sockets), 2);
        Assert.InRange(rows.Length, 2, NetworkProcessUsage.MaxRows);
        Assert.Contains(rows, row => row.Kind == NetworkTrafficKind.Summary);
        var ancestors = new int[NetworkProcessUsage.MaxRows];
        var previousDepth = -1;
        foreach (var row in rows)
        {
            Assert.InRange(row.Depth, 0, previousDepth + 1);
            Assert.Equal(row.Depth == 0 ? 0 : ancestors[row.Depth - 1], row.ParentProcessId);
            ancestors[row.Depth] = row.ProcessId;
            previousDepth = row.Depth;
        }
    }

    // Build deterministic native-style endpoint values without opening live sockets.
    private static NetworkEndpoint Endpoint(string address, int port) =>
        new(NetworkAddress.FromBytes(IPAddress.Parse(address).GetAddressBytes()), (ushort)port);

    // Describe an owned listening socket, including wildcard and IPv6 bindings.
    private static NetworkConnections.TcpSocket Listen(int processId, int port, string address = "127.0.0.1") =>
        new(processId, Native.MIB_TCP_STATE_LISTEN, Endpoint(address, port), Endpoint("0.0.0.0", 0));

    // Describe a process with a genuine non-loopback socket, independently of its local proxy connections.
    private static NetworkConnections.TcpSocket External(int processId) =>
        new(processId, Native.MIB_TCP_STATE_ESTAB, Endpoint("192.0.2.10", 41000), Endpoint("198.51.100.20", 443));

    // Add both sides of one connected loopback tuple so ownership checks can validate the reverse peer.
    private static void Connect(List<NetworkConnections.TcpSocket> sockets, int client, int server,
        int clientPort, int serverPort, string address = "127.0.0.1")
    {
        var local = Endpoint(address, clientPort);
        var remote = Endpoint(address, serverPort);
        sockets.Add(new(client, Native.MIB_TCP_STATE_ESTAB, local, remote));
        sockets.Add(new(server, Native.MIB_TCP_STATE_ESTAB, remote, local));
    }

    // Supply one interval's application-side byte count to the production tree builder.
    private static NetworkTrafficSample Sample(int processId, int parent, ulong download) =>
        new(new NetworkTrafficKey(processId, parent), download, 0);
}
