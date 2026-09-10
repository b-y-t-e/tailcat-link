// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;

namespace Tailcat.Link;

/// <summary>
/// One machine a host is paired with: everything an application does
/// <em>to</em> a peer, as opposed to everything it does to the host.
/// </summary>
/// <remarks>
/// <para>
/// This is the per-peer half of <see cref="ILink"/>. Handlers are not here —
/// they are registered once on <see cref="ILinkHost"/> and told which peer
/// each call came from — because an application has one piece of logic and
/// several correspondents, not the other way round.
/// </para>
/// <para>
/// A peer outlives its sessions. It exists from the moment it pairs until it
/// is forgotten, and is simply not connected in between.
/// </para>
/// </remarks>
public interface ILinkPeer
{
    /// <summary>
    /// The machine's public key, which is the only thing about it that a
    /// session authenticates.
    /// </summary>
    NodePublic Key { get; }

    /// <summary>
    /// What the machine called itself when it joined, or null if it said
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <b>Nothing authenticates this.</b> It is a hint sent by the other
    /// machine, which may say anything and may say something different next
    /// time. Show it; do not key anything off it. <see cref="Key"/> is what
    /// identifies a peer.
    /// </remarks>
    string? Name { get; }

    /// <summary>When this machine first paired with it.</summary>
    DateTimeOffset PairedAt { get; }

    /// <summary>When a session with it was last established.</summary>
    DateTimeOffset LastSeen { get; }

    /// <summary>Whether a session to it is up right now.</summary>
    bool IsConnected { get; }

    /// <summary>Where it stands, as one value a user interface can bind to.</summary>
    LinkConnectionState State { get; }

    /// <summary>Raised when a session with it comes up, including every re-established one.</summary>
    event EventHandler<EventArgs>? Connected;

    /// <summary>Raised when a session with it goes down, with the reason it ended.</summary>
    /// <remarks>
    /// Also raised for an attempt that never became a session, because the
    /// most useful reason of all — <see cref="LinkDisconnectReason.Refused"/>,
    /// which is answered by a fresh invitation and nothing else — happens
    /// only there. <see cref="State"/> is what separates the two: a peer still
    /// building its first session stays <see cref="LinkConnectionState.Connecting"/>.
    /// </remarks>
    event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    event EventHandler<LinkStateChangedEventArgs>? StateChanged;

    /// <inheritdoc cref="ILink.RequestAsync"/>
    Task<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ILink.NotifyAsync"/>
    Task NotifyAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ILink.SendAsync"/>
    Task SendAsync(
        Stream content,
        TransferOffer offer,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an ordered, non-durable channel to this peer, into the handler it
    /// registered for <paramref name="name"/>.
    /// </summary>
    /// <remarks>
    /// Unlike a request, this needs a session: there is nothing to resume a
    /// channel onto, so it waits for one rather than being buffered.
    /// </remarks>
    /// <param name="name">Which of the peer's channel handlers to reach.</param>
    /// <param name="cancellationToken">Gives up on opening it.</param>
    /// <exception cref="RemoteHandlerException">If the peer has no handler for that name.</exception>
    /// <exception cref="LinkException">If no session could be had in time.</exception>
    Task<ILinkChannelWriter> OpenChannelAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Waits until a session with this peer is up.</summary>
    Task WaitUntilConnectedAsync(CancellationToken cancellationToken = default);
}
