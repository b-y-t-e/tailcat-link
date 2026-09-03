// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Collections.Concurrent;
using System.Net.Quic;
using System.Net.Sockets;
using System.Text;
using Tailcat.Derp;
using Tailcat.Keys;
using Tailcat.Tailcfg;

namespace Tailcat.Net.Tests;

/// <summary>
/// Covers the shape of an unattended remote-control deployment: an agent
/// installed on a machine nobody can reach, printing its address once, and an
/// operator connecting to it from wherever they happen to be — for days, with
/// nobody available to restart anything.
/// </summary>
/// <remarks>
/// <para>
/// A node reconnects to its <em>relay</em> by itself, but a session is not
/// resurrected: once a <see cref="ITailcatConnection"/> is dead it stays dead.
/// Staying connected for days is therefore the application's job, and this is
/// what that job looks like — <see cref="RemoteAgent"/> accepts sessions
/// forever, <see cref="ControlClient"/> re-dials whenever a command fails.
/// The tests then take the link apart in each of the ways the deployment
/// actually breaks and require it to come back with no intervention.
/// </para>
/// <para>
/// Two properties make it work and are asserted here rather than assumed: the
/// agent's address is a function of its key and pinned home region, so the
/// address printed once stays valid across restarts and network moves; and a
/// peer is addressed by that key rather than by any IP, so either end can
/// change network without the other being told.
/// </para>
/// <para>
/// All of it runs offline against the in-memory relay. Two processes on one
/// machine cannot prove NAT traversal, but that is not what is being tested:
/// what is tested is that every disruption is recovered from without a human,
/// which is the part that a live test against a public relay could only ever
/// show by accident.
/// </para>
/// </remarks>
public class UnattendedLinkTests
{
    private const int RegionId = 901;

    // Short enough that a test spends its time reconnecting rather than
    // waiting, long enough that a loaded CI machine is not mistaken for a
    // dead peer.
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandDeadline = TimeSpan.FromSeconds(90);

    /// <summary>
    /// A node pinned to one region and one key: the two things that make an
    /// address survive a restart.
    /// </summary>
    /// <remarks>
    /// The region is pinned deliberately. A node that measures its closest
    /// region instead would choose a different one after moving far enough,
    /// and its address — key <em>and</em> region — would change with it,
    /// invalidating the code the operator scanned.
    /// </remarks>
    private static TailcatNodeOptions OptionsFor(FakeDerpRelay relay, NodePrivate key) => new()
    {
        PrivateKey = key,
        DerpMap = new DerpMap
        {
            Regions =
            {
                [RegionId] = new DerpRegion
                {
                    RegionID = RegionId,
                    Nodes = [new DerpNode { Name = "fake", HostName = "relay.invalid" }],
                },
            },
        },
        HomeRegionId = RegionId,
        // Both nodes are on loopback and already know every address the other
        // can reach them at; a STUN server that isn't there would only cost
        // the handshake its timeout.
        StunServers = [],
        HandshakeTimeout = HandshakeTimeout,
        ConnectRelay = async (_, token) => await DerpClient.ConnectOverStreamAsync(
            await relay.DialAsync(token), key, relay.PublicKey, token),
    };

    /// <summary>
    /// The application installed on the far machine: it accepts whatever
    /// session turns up, serves commands on it, and can push a signal back.
    /// </summary>
    /// <remarks>
    /// The accept loop is what makes it unattended. A serving loop ends every
    /// time the operator's session dies, but the accept loop does not, so the
    /// next session is served without anybody logging in to restart anything.
    /// </remarks>
    private sealed class RemoteAgent : IAsyncDisposable
    {
        private readonly TailcatNode _node;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly Lock _mu = new();
        private ITailcatConnection? _current;
        private int _commandsServed;
        private bool _disposed;

        private RemoteAgent(TailcatNode node)
        {
            _node = node;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        /// <summary>Starts the agent under a key that outlives the process.</summary>
        public static async Task<RemoteAgent> StartAsync(FakeDerpRelay relay, NodePrivate key, CancellationToken ct) =>
            new(await TailcatNode.CreateAsync(OptionsFor(relay, key), ct));

        /// <summary>What the agent prints, or shows as a barcode, exactly once.</summary>
        public ConnBlob Address => _node.Address;

        /// <summary>How many commands have been answered, across all sessions.</summary>
        public int CommandsServed => Volatile.Read(ref _commandsServed);

        /// <summary>Pushes an unsolicited signal to whoever is connected.</summary>
        public async Task PushSignalAsync(string signal, CancellationToken ct)
        {
            ITailcatConnection? conn;
            lock (_mu)
            {
                conn = _current;
            }
            if (conn is null)
            {
                throw new InvalidOperationException("no operator is connected");
            }

            Stream stream = await conn.OpenStreamAsync(ct);
            await using (stream)
            {
                await WriteLineAsync(stream, signal, ct);
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (ITailcatConnection conn in _node.AcceptConnectionsAsync(ct))
                {
                    ITailcatConnection? previous;
                    lock (_mu)
                    {
                        previous = _current;
                        _current = conn;
                    }

                    // The operator only ever has one session; an older one is
                    // the corpse of a link that died when their laptop moved,
                    // and holding it would leak a QUIC connection per move.
                    if (previous is not null)
                    {
                        await previous.DisposeAsync();
                    }
                    _ = Task.Run(() => ServeAsync(conn, ct), ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task ServeAsync(ITailcatConnection conn, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Stream stream = await conn.AcceptStreamAsync(ct);
                    _ = Task.Run(() => ServeCommandAsync(stream, ct), ct);
                }
            }
            catch (Exception ex) when (ex is QuicException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The ordinary end of a session: the operator moved network,
                // rebooted, or closed the laptop lid. Nothing to do — the
                // accept loop is still running and will serve the next one.
            }
        }

        private async Task ServeCommandAsync(Stream stream, CancellationToken ct)
        {
            await using (stream)
            {
                try
                {
                    string command = await ReadLineAsync(stream, ct);
                    Interlocked.Increment(ref _commandsServed);
                    await WriteLineAsync(stream, Answer(command), ct);
                }
                catch (Exception ex) when (ex is QuicException or IOException or EndOfStreamException
                    or ObjectDisposedException or OperationCanceledException)
                {
                    // The session died mid-command; the operator will retry it
                    // on the next one.
                }
            }
        }

        private string Answer(string command) => command switch
        {
            "ping" => "pong",
            "served" => CommandsServed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => $"unknown: {command}",
        };

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            await _cts.CancelAsync();
            ITailcatConnection? conn;
            lock (_mu)
            {
                conn = _current;
                _current = null;
            }
            if (conn is not null)
            {
                await conn.DisposeAsync();
            }
            try
            {
                await _acceptLoop;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
            await _node.DisposeAsync();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// The operator's side: it keeps a session to one address alive, rebuilding
    /// whatever is needed — session, node, or both — whenever a command fails.
    /// </summary>
    /// <remarks>
    /// The retry is where "no intervention" lives. Every disruption in these
    /// tests surfaces the same way, as a command that does not come back, so
    /// the recovery is the same for all of them: drop the dead session, dial
    /// the address again, run the command.
    /// </remarks>
    private sealed class ControlClient : IAsyncDisposable
    {
        private readonly Func<CancellationToken, Task<TailcatNode>> _newNode;
        private readonly ConnBlob _address;
        private readonly SemaphoreSlim _mu = new(1, 1);
        private TailcatNode? _node;
        private ITailcatConnection? _connection;
        private int _sessionsOpened;
        private bool _disposed;

        public ControlClient(FakeDerpRelay relay, NodePrivate key, ConnBlob address)
        {
            // The key comes from disk, not from this object: the same operator
            // coming back after a reboot must still be the same peer.
            _newNode = ct => TailcatNode.CreateAsync(OptionsFor(relay, key), ct);
            _address = address;
        }

        /// <summary>Signals the agent pushed, in arrival order.</summary>
        public ConcurrentQueue<string> Signals { get; } = new();

        /// <summary>How many sessions it took to keep the link up.</summary>
        public int SessionsOpened => Volatile.Read(ref _sessionsOpened);

        /// <summary>Sends a command and returns the answer, retrying across reconnections.</summary>
        public async Task<string> SendCommandAsync(string command, CancellationToken ct)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(CommandDeadline);

            Exception? last = null;
            while (!deadline.IsCancellationRequested)
            {
                ITailcatConnection? conn = null;
                try
                {
                    conn = await EnsureConnectedAsync(deadline.Token);

                    // A dead session does not fail a write, it swallows it, so
                    // the timeout is the only thing that tells the difference
                    // between a slow relay and a peer that is gone.
                    using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    attempt.CancelAfter(CommandTimeout);

                    Stream stream = await conn.OpenStreamAsync(attempt.Token);
                    await using (stream)
                    {
                        await WriteLineAsync(stream, command, attempt.Token);
                        return await ReadLineAsync(stream, attempt.Token);
                    }
                }
                catch (Exception ex) when (ex is QuicException or IOException or EndOfStreamException or SocketException
                    or TailcatException or ObjectDisposedException or OperationCanceledException
                    && !ct.IsCancellationRequested)
                {
                    last = ex;
                    if (conn is not null)
                    {
                        await DropAsync(conn);
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"'{command}' went unanswered for {CommandDeadline}", last);
        }

        /// <summary>
        /// Everything the operator's machine had, gone: a different Wi-Fi
        /// network, or a reboot. Only the key on disk survives.
        /// </summary>
        public async Task MoveToAnotherNetworkAsync()
        {
            await _mu.WaitAsync(CancellationToken.None);
            try
            {
                if (_connection is not null)
                {
                    await _connection.DisposeAsync();
                    _connection = null;
                }
                if (_node is not null)
                {
                    await _node.DisposeAsync();
                    _node = null;
                }
            }
            finally
            {
                _mu.Release();
            }
        }

        private async Task<ITailcatConnection> EnsureConnectedAsync(CancellationToken ct)
        {
            await _mu.WaitAsync(ct);
            try
            {
                if (_connection is not null)
                {
                    return _connection;
                }

                TimeSpan backoff = TimeSpan.FromMilliseconds(200);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        _node ??= await _newNode(ct);
                        ITailcatConnection conn = await _node.ConnectAsync(_address, ct);
                        _connection = conn;
                        Interlocked.Increment(ref _sessionsOpened);
                        _ = Task.Run(() => SignalLoopAsync(conn, ct), CancellationToken.None);
                        return conn;
                    }
                    catch (Exception ex) when (ex is TailcatException or QuicException or IOException or SocketException
                        or ObjectDisposedException && !ct.IsCancellationRequested)
                    {
                        // The agent may be down, its relay connection may be
                        // mid-reconnect, or this machine may have no network
                        // yet. None of that is fatal; waiting is the answer.
                        if (ex is ObjectDisposedException && _node is not null)
                        {
                            await _node.DisposeAsync();
                            _node = null;
                        }
                        await Task.Delay(backoff, ct);
                        backoff = backoff < TimeSpan.FromSeconds(4) ? backoff * 2 : backoff;
                    }
                }
            }
            finally
            {
                _mu.Release();
            }
        }

        private async Task SignalLoopAsync(ITailcatConnection conn, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Stream stream = await conn.AcceptStreamAsync(ct);
                    await using (stream)
                    {
                        Signals.Enqueue(await ReadLineAsync(stream, ct));
                    }
                }
            }
            catch (Exception ex) when (ex is QuicException or IOException or EndOfStreamException
                or ObjectDisposedException or OperationCanceledException)
            {
                // This session is over; the next one starts its own loop.
            }
        }

        private async Task DropAsync(ITailcatConnection dead)
        {
            await _mu.WaitAsync(CancellationToken.None);
            try
            {
                // Only if it is still the current one: a concurrent reconnect
                // may already have replaced it, and disposing that would take
                // down a session that works.
                if (!ReferenceEquals(_connection, dead))
                {
                    return;
                }
                _connection = null;
            }
            finally
            {
                _mu.Release();
            }
            await dead.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
            if (_node is not null)
            {
                await _node.DisposeAsync();
                _node = null;
            }
            _mu.Dispose();
        }
    }

    /// <summary>
    /// The baseline: commands go out and answers come back, and the agent can
    /// speak first on the same session without being asked.
    /// </summary>
    [Fact]
    public async Task CommandsAndSignalsCrossTheLinkInBothDirections()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using RemoteAgent agent = await RemoteAgent.StartAsync(relay, NodePrivate.NewKey(), ct);
        await using ControlClient operatorSide = new(relay, NodePrivate.NewKey(), agent.Address);

        Assert.Equal("pong", await operatorSide.SendCommandAsync("ping", ct));

        // The command round trip proves the agent holds this session, so it
        // has somewhere to push to.
        await agent.PushSignalAsync("disk-full", ct);
        await WaitUntilAsync(
            () => operatorSide.Signals.Contains("disk-full"), "the pushed signal should have arrived", ct);

        // Two: the ping, and this query, which the agent counts before
        // answering it.
        Assert.Equal("2", await operatorSide.SendCommandAsync("served", ct));
    }

    /// <summary>
    /// The relay hanging up on both nodes — a relay restart, or a NAT dropping
    /// the TCP mapping — costs no session and no intervention.
    /// </summary>
    [Fact]
    public async Task TheLinkSurvivesTheRelayHangingUpOnBothNodes()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        NodePrivate agentKey = NodePrivate.NewKey();
        NodePrivate operatorKey = NodePrivate.NewKey();
        await using RemoteAgent agent = await RemoteAgent.StartAsync(relay, agentKey, ct);
        await using ControlClient operatorSide = new(relay, operatorKey, agent.Address);

        Assert.Equal("pong", await operatorSide.SendCommandAsync("ping", ct));

        relay.DisconnectClient(agentKey.Public());
        relay.DisconnectClient(operatorKey.Public());

        // Both nodes log back in on their own; nothing above the relay is
        // told, and the command that follows must simply work.
        await relay.WaitForClientAsync(agentKey.Public(), ct);
        await relay.WaitForClientAsync(operatorKey.Public(), ct);

        Assert.Equal("pong", await operatorSide.SendCommandAsync("ping", ct));
        Assert.Equal(2, agent.CommandsServed);
    }

    /// <summary>
    /// The operator's laptop moving to another network — or rebooting — brings
    /// the link back by itself, because the agent is addressed by key rather
    /// than by anything the move changed.
    /// </summary>
    [Fact]
    public async Task TheOperatorReconnectsUnaidedAfterMovingNetwork()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        await using RemoteAgent agent = await RemoteAgent.StartAsync(relay, NodePrivate.NewKey(), ct);
        await using ControlClient operatorSide = new(relay, NodePrivate.NewKey(), agent.Address);

        Assert.Equal("pong", await operatorSide.SendCommandAsync("ping", ct));

        // New network: new socket, new addresses, new relay connection. The
        // stored address is all that carries over.
        await operatorSide.MoveToAnotherNetworkAsync();

        Assert.Equal("pong", await operatorSide.SendCommandAsync("ping", ct));
        Assert.Equal(2, operatorSide.SessionsOpened);

        // And the new session is a working one in both directions, not just
        // one that answered once.
        await agent.PushSignalAsync("moved", ct);
        await WaitUntilAsync(
            () => operatorSide.Signals.Contains("moved"), "the agent should push over the new session", ct);
    }

    /// <summary>
    /// The agent restarting — a reboot of the machine nobody can reach — keeps
    /// the address it published, so the operator reconnects to the code they
    /// already have.
    /// </summary>
    [Fact]
    public async Task TheAgentKeepsItsAddressAcrossARestart()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(3));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();

        // The key is the agent's identity across restarts: generated once at
        // install time and kept on disk.
        NodePrivate agentKey = NodePrivate.NewKey();
        RemoteAgent agent = await RemoteAgent.StartAsync(relay, agentKey, ct);
        ConnBlob published = agent.Address;

        await using ControlClient operatorSide = new(relay, NodePrivate.NewKey(), published);
        Assert.Equal("pong", await operatorSide.SendCommandAsync("ping", ct));

        await agent.DisposeAsync();
        await using RemoteAgent restarted = await RemoteAgent.StartAsync(relay, agentKey, ct);

        // The barcode the operator scanned once is still the right one: the
        // address is the key plus the pinned region, and neither moved.
        Assert.Equal(published, restarted.Address);

        Assert.Equal("pong", await operatorSide.SendCommandAsync("ping", ct));

        // Served by the new process, which knows nothing of the old session.
        Assert.Equal(1, restarted.CommandsServed);
    }

    /// <summary>
    /// Days of running, compressed: relay drops, operator moves, and agent
    /// restarts, one after another, with every command answered and nobody
    /// touching either machine.
    /// </summary>
    [Fact]
    public async Task TheLinkKeepsWorkingAcrossRepeatedDisruptions()
    {
        using CancellationTokenSource cts = Deadline(TimeSpan.FromMinutes(5));
        CancellationToken ct = cts.Token;

        await using FakeDerpRelay relay = new();
        NodePrivate agentKey = NodePrivate.NewKey();
        NodePrivate operatorKey = NodePrivate.NewKey();

        RemoteAgent agent = await RemoteAgent.StartAsync(relay, agentKey, ct);
        ConnBlob published = agent.Address;
        await using ControlClient operatorSide = new(relay, operatorKey, published);
        try
        {
            for (int round = 1; round <= 3; round++)
            {
                Assert.Equal("pong", await operatorSide.SendCommandAsync("ping", ct));

                switch (round)
                {
                    case 1:
                        relay.DisconnectClient(agentKey.Public());
                        relay.DisconnectClient(operatorKey.Public());
                        await relay.WaitForClientAsync(agentKey.Public(), ct);
                        break;
                    case 2:
                        await operatorSide.MoveToAnotherNetworkAsync();
                        break;
                    default:
                        await agent.DisposeAsync();
                        agent = await RemoteAgent.StartAsync(relay, agentKey, ct);
                        Assert.Equal(published, agent.Address);
                        break;
                }
            }

            Assert.Equal("pong", await operatorSide.SendCommandAsync("ping", ct));
        }
        finally
        {
            await agent.DisposeAsync();
        }
    }

    private static CancellationTokenSource Deadline(TimeSpan limit)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(limit);
        return cts;
    }

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
        await stream.FlushAsync(ct);
    }

    // Byte at a time: a control channel carries a handful of short lines, and
    // buffering would mean owning the leftovers across calls for no gain.
    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        List<byte> line = [];
        byte[] one = new byte[1];
        while (await stream.ReadAsync(one, ct) == 1)
        {
            if (one[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString([.. line]);
            }
            line.Add(one[0]);
        }
        throw new EndOfStreamException("the peer closed the stream mid-line");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, CancellationToken ct)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            try
            {
                await Task.Delay(50, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Assert.Fail(because);
            }
        }
    }
}
