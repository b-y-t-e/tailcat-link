// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Link.Protocol;
using Tailcat.Link.Storage;
using Tailcat.Link.Transport;

namespace Tailcat.Link;

/// <summary>
/// The knobs, all of which have an answer that is right for nearly everyone.
/// </summary>
/// <remarks>
/// Passing no options at all is the intended way to use this library. What is
/// here is for the cases the defaults cannot cover: a service that keeps its
/// state somewhere unusual, an application that wants to see what the link is
/// doing, and tests.
/// <para>
/// It is a record so that one set of options can be adjusted for a second
/// machine with <c>options with { ... }</c> rather than restated.
/// </para>
/// </remarks>
public sealed record LinkOptions
{
    /// <summary>Where the identity and the pairing are kept between runs.</summary>
    public ILinkStore Store { get; init; } = new FileLinkStore();

    /// <summary>How nodes are brought up. Substituted by tests; otherwise left alone.</summary>
    public INodeGatewayFactory Gateway { get; init; } = new TailcatNodeGatewayFactory();

    /// <summary>
    /// How long an exchange may go without a single byte moving before it is
    /// given up on.
    /// </summary>
    /// <remarks>
    /// It measures silence, not the length of the exchange: a payload of
    /// several megabytes through a shared relay takes longer than any sane
    /// value here, and a total deadline would make it impossible to send at
    /// all — every retry would resend from the start and run out in the same
    /// place. What it does bound is how long a peer that has stopped
    /// answering can be waited for.
    /// </remarks>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a host's invitation code may be used to pair before it is
    /// worthless.
    /// </summary>
    /// <remarks>
    /// A code names the host by its public key and relay region, which is
    /// exactly what the relay it connects to already sees — so it cannot be
    /// kept from the relay's operator. The secret in it is the pairing token,
    /// and this is how long that token is worth stealing: after the window
    /// the code pairs with nobody, and the next <see cref="TailcatLink.HostAsync"/>
    /// mints a fresh one to show.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The window is not positive.</exception>
    public TimeSpan PairingWindow
    {
        get => _pairingWindow;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _pairingWindow = value;
        }
    }

    private readonly TimeSpan _pairingWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// How long <see cref="ILink.RequestAsync"/> keeps trying, across as many
    /// reconnections as fit inside it, before giving up on one request.
    /// </summary>
    /// <remarks>
    /// Bounded by how long the other machine remembers what it answered, less
    /// the time a retry spends on the way there: a retry that arrives after it
    /// has forgotten would run the handler a second time. That window is fixed
    /// by the protocol, so the bound is a property of the library and not of
    /// how the peer was configured.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The deadline is not positive, or is longer than a retry can be
    /// recognised as one.
    /// </exception>
    public TimeSpan RequestDeadline
    {
        get => _requestDeadline;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, LinkProtocol.LongestRequestDeadline);
            _requestDeadline = value;
        }
    }

    private readonly TimeSpan _requestDeadline = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often the link checks that the peer is still there.
    /// </summary>
    /// <remarks>
    /// This is what bounds how long a link can be dead without noticing. It
    /// is not free — each check is a round trip through a relay — so it trades
    /// traffic against how quickly a link that broke silently is rebuilt.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The interval is not positive.</exception>
    public TimeSpan HeartbeatInterval
    {
        get => _heartbeatInterval;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _heartbeatInterval = value;
        }
    }

    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a hosting machine may hear nothing at all before it rebuilds
    /// its node.
    /// </summary>
    /// <remarks>
    /// The hosting end waits to be connected to, and a peer that is away for
    /// a day is indistinguishable from a relay socket that died silently —
    /// which is what a laptop resumed from sleep behind another NAT leaves
    /// behind. Nobody is there to restart the machine that cannot be reached,
    /// so silence this long is treated as a failure and rebuilds are counted
    /// towards <see cref="RebuildNodeAfterFailures"/>. The rebuilt node keeps
    /// the same key and region, so the published code still points at it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive.</exception>
    public TimeSpan ListenSilenceTimeout
    {
        get => _listenSilenceTimeout;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _listenSilenceTimeout = value;
        }
    }

    private readonly TimeSpan _listenSilenceTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait before the first reconnection attempt.</summary>
    /// <remarks>
    /// Positive, not merely non-negative: the delay is what keeps a link that
    /// cannot be built from retrying in a tight loop against a public relay.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The delay is not positive.</exception>
    public TimeSpan MinReconnectDelay
    {
        get => _minReconnectDelay;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _minReconnectDelay = value;
        }
    }

    private readonly TimeSpan _minReconnectDelay = TimeSpan.FromSeconds(1);

    /// <summary>The longest the link will wait between attempts.</summary>
    /// <remarks>
    /// Half a minute: long enough that a machine which has been offline for a
    /// week is not hammering a public relay, short enough that nobody sits
    /// waiting for a link that could have come back.
    /// <para>
    /// Only checked for being positive, and not against
    /// <see cref="MinReconnectDelay"/>: the two are set in whatever order the
    /// initialiser lists them, so a pair-wise check would reject a valid pair
    /// half-written. A maximum below the minimum simply caps every wait at the
    /// maximum, which is what the caller asked for.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The delay is not positive.</exception>
    public TimeSpan MaxReconnectDelay
    {
        get => _maxReconnectDelay;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _maxReconnectDelay = value;
        }
    }

    private readonly TimeSpan _maxReconnectDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many attempts in a row may fail before the node itself is rebuilt
    /// from the stored identity.
    /// </summary>
    /// <remarks>
    /// A node repairs its own relay connection, but not every failure is one
    /// it can see: a laptop that resumed from sleep on a different network
    /// can hold a socket that will never receive anything again. Rebuilding
    /// is the blunt instrument that fixes all of those at once.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The count is below one.</exception>
    public int RebuildNodeAfterFailures
    {
        get => _rebuildNodeAfterFailures;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _rebuildNodeAfterFailures = value;
        }
    }

    private readonly int _rebuildNodeAfterFailures = 3;

    /// <summary>Where the link reports what it is doing. Nowhere, by default.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>The clock the link measures with; tests pass their own.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
