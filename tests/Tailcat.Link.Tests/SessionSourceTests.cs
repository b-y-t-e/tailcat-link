// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat;
using Tailcat.Keys;
using Tailcat.Link.Protocol;
using Tailcat.Net;

namespace Tailcat.Link.Tests;

/// <summary>
/// Covers the one thing the two ends of a link do differently: where the next
/// session comes from, and therefore what a pause between attempts is for.
/// </summary>
/// <remarks>
/// The end that dials paces its own attempts, so a link that cannot be built
/// does not hammer a public relay. The end that is dialled paces nothing —
/// the session arrives from the host's accept loop, already handshaken — and
/// a pause there is dead time in which the machine at the other end is
/// connected, asking, and hearing nothing back.
/// </remarks>
public class SessionSourceTests
{
    /// <summary>
    /// The defect a field report was made of: with a session already waiting,
    /// the hosting end must take it rather than sit out a backoff that grows
    /// to half a minute.
    /// </summary>
    /// <remarks>
    /// Sitting it out is what turned a flap into a permanent one. Once the
    /// pause is longer than the far end's heartbeat plus its request timeout,
    /// that end gives up and dials again inside every pause, and each new dial
    /// replaces the session whose connection is still waiting here — so what
    /// this loop eventually picks up is one the node has already closed.
    /// </remarks>
    [Fact]
    public async Task AHostTakesASessionThatIsAlreadyWaitingRatherThanPausing()
    {
        PausesUntilTold clock = new();
        await using OfferedSessionSource source = new(clock);
        source.Offer(new StubConnection());

        // The pause is never told to elapse, so returning at all is the whole
        // of the assertion: a host that sat it out would hang here instead.
        await source.PauseAsync(
            TimeSpan.FromSeconds(30),
            SessionAttempt.TookASession,
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A session offered during the pause ends it, because that is when it
    /// happens: the peer re-dials, and the host is asleep.
    /// </summary>
    [Fact]
    public async Task ASessionOfferedDuringThePauseEndsIt()
    {
        PausesUntilTold clock = new();
        await using OfferedSessionSource source = new(clock);

        Task paused = source.PauseAsync(
            TimeSpan.FromSeconds(30),
            SessionAttempt.TookASession,
            TestContext.Current.CancellationToken);
        await clock.WaitUntilPausedAsync(TestContext.Current.CancellationToken);

        source.Offer(new StubConnection());

        await paused;
    }

    /// <summary>
    /// The case the pause was written for is untouched: nothing has been
    /// offered because building the node is what failed, and retrying at once
    /// would be a tight loop against a relay.
    /// </summary>
    [Fact]
    public async Task WithNothingOfferedTheHostStillWaits()
    {
        PausesUntilTold clock = new();
        await using OfferedSessionSource source = new(clock);

        Task paused = source.PauseAsync(
            TimeSpan.FromSeconds(30),
            SessionAttempt.TookASession,
            TestContext.Current.CancellationToken);
        await clock.WaitUntilPausedAsync(TestContext.Current.CancellationToken);

        // With no session offered and the clock standing still, nothing can
        // have ended the pause.
        Assert.False(paused.IsCompleted, "a host with nothing to take did not wait at all");

        clock.Elapse();
        await paused;
    }

    /// <summary>
    /// A pause after an attempt that never reached the source is waited out
    /// whole, whatever is queued.
    /// </summary>
    /// <remarks>
    /// The host is paused, the peer re-dials, and the accept loop queues the
    /// connection — and then building the node fails, because the network went
    /// away. Nothing takes that connection, so ending the pause on it would
    /// end every pause on it, and the loop would spin on a full core until the
    /// network came back.
    /// </remarks>
    [Fact]
    public async Task AnAttemptThatNeverReachedTheSourceStillWaits()
    {
        PausesUntilTold clock = new();
        await using OfferedSessionSource source = new(clock);
        source.Offer(new StubConnection());

        Task paused = source.PauseAsync(
            TimeSpan.FromSeconds(30),
            SessionAttempt.NeverReachedTheSource,
            TestContext.Current.CancellationToken);
        await clock.WaitUntilPausedAsync(TestContext.Current.CancellationToken);

        Assert.False(paused.IsCompleted, "a queued session nobody took shortened the pause");

        clock.Elapse();
        await paused;
    }

    /// <summary>
    /// The dialling end waits the whole of it whatever else is true, because
    /// there is no queue to consult: what it is pacing is its own dialling.
    /// </summary>
    [Fact]
    public async Task ThePausePacesTheEndThatDials()
    {
        PausesUntilTold clock = new();
        DialingSessionSource source = new(
            new ConnBlob("tcnowhere"),
            new LinkHello("token", null),
            TimeSpan.FromSeconds(5),
            clock);

        Task paused = source.PauseAsync(
            TimeSpan.FromSeconds(30),
            SessionAttempt.TookASession,
            TestContext.Current.CancellationToken);
        await clock.WaitUntilPausedAsync(TestContext.Current.CancellationToken);

        Assert.False(paused.IsCompleted, "the dialling end did not pace itself");

        clock.Elapse();
        await paused;
    }

    /// <summary>
    /// A clock whose pauses end only when the test says so.
    /// </summary>
    /// <remarks>
    /// <see cref="Tailcat.TestSupport.FakeTimeProvider"/> leaves timers on the
    /// real clock on purpose, which is exactly what a test about a pause must
    /// not do: measuring a wall-clock threshold makes a loaded machine the
    /// thing under test. Here a pause is a timer that never fires by itself,
    /// so what ends one is unambiguous.
    /// </remarks>
    private sealed class PausesUntilTold : TimeProvider
    {
        private readonly Lock _mu = new();
        private readonly List<PendingPause> _pending = [];
        private readonly TaskCompletionSource _asked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Waits until the code under test has asked for a pause.</summary>
        public async Task WaitUntilPausedAsync(CancellationToken cancellationToken) =>
            await _asked.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        /// <summary>Ends every pause, as the clock reaching its due time would.</summary>
        public void Elapse()
        {
            PendingPause[] waiting;
            lock (_mu)
            {
                waiting = [.. _pending];
                _pending.Clear();
            }
            foreach (PendingPause pause in waiting)
            {
                pause.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            PendingPause pause = new(callback, state);
            lock (_mu)
            {
                _pending.Add(pause);
            }
            _asked.TrySetResult();
            return pause;
        }

        private sealed class PendingPause(TimerCallback callback, object? state) : ITimer
        {
            private readonly Lock _mu = new();
            private bool _disposed;

            public void Fire()
            {
                lock (_mu)
                {
                    if (_disposed)
                    {
                        return;
                    }
                }
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
                lock (_mu)
                {
                    _disposed = true;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>A connection that is never used, only queued.</summary>
    private sealed class StubConnection : ITailcatConnection
    {
        public NodePublic Peer { get; } = NodePrivate.NewKey().Public();

        public PeerPath CurrentPath { get; } = new(PeerPathKind.Relay, null, null, default, 1024);

        public IReadOnlyList<PeerPath> Paths => [CurrentPath];

        public event Action<PeerPath>? PathChanged
        {
            add { }
            remove { }
        }

        public Task<Stream> OpenStreamAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AcceptStreamAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
