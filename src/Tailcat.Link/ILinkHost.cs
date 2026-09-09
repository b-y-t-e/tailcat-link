// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link;

/// <summary>Answers a request that arrived from one of the host's peers.</summary>
/// <param name="peer">Which machine asked.</param>
/// <param name="request">What it sent.</param>
/// <param name="cancellationToken">Cancelled when the host is closing.</param>
/// <returns>The answer to send back.</returns>
public delegate Task<ReadOnlyMemory<byte>> LinkPeerRequestHandler(
    ILinkPeer peer,
    ReadOnlyMemory<byte> request,
    CancellationToken cancellationToken);

/// <summary>Takes a transfer that arrived from one of the host's peers.</summary>
/// <param name="peer">Which machine is sending it.</param>
/// <param name="transfer">The content, as it arrives.</param>
/// <param name="cancellationToken">Cancelled when the host is closing.</param>
public delegate Task LinkPeerTransferHandler(
    ILinkPeer peer,
    IncomingTransfer transfer,
    CancellationToken cancellationToken);

/// <summary>
/// A machine that publishes invitations and holds the peers that accept them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TailcatLink.HostAsync"/> is this with a bound of one peer,
/// behind the narrower <see cref="ILink"/>. An application with several
/// clients — a bridge for a handful of phones, say — uses this instead:
/// one identity, one stored file, one region measurement, one supervision
/// loop per peer, and a bound on how many there may be.
/// </para>
/// <para>
/// Handlers are registered here rather than per peer and are told which peer
/// each call came from.
/// </para>
/// </remarks>
public interface ILinkHost : IAsyncDisposable
{
    /// <summary>
    /// The code worth publishing now: the newest invitation's, or the one
    /// this machine joined with.
    /// </summary>
    InvitationCode InvitationCode { get; }

    /// <summary>
    /// When <see cref="InvitationCode"/> stops being able to pair a machine,
    /// or null when it has nothing left to buy.
    /// </summary>
    DateTimeOffset? InvitationExpiresAt { get; }

    /// <summary>Every invitation still live, oldest first.</summary>
    IReadOnlyList<LinkInvitation> Invitations { get; }

    /// <summary>The machines paired with this one, in the order they joined.</summary>
    IReadOnlyList<ILinkPeer> Peers { get; }

    /// <summary>
    /// How many machines may be paired at once.
    /// </summary>
    /// <remarks>
    /// A security bound, not a resource one: every admitted peer can send
    /// requests into the application's handler.
    /// </remarks>
    int MaxPeers { get; }

    /// <summary>Raised when a session with a peer comes up, including every re-established one.</summary>
    event EventHandler<PeerEventArgs>? PeerJoined;

    /// <summary>Raised when a session with a peer goes down, with the reason it ended.</summary>
    event EventHandler<PeerLeftEventArgs>? PeerLeft;

    /// <summary>
    /// Sets what answers requests from any peer, replacing any previous
    /// handler. A host without one refuses requests with an error.
    /// </summary>
    void SetRequestHandler(LinkPeerRequestHandler handler);

    /// <summary>
    /// Sets what takes transfers from any peer, replacing any previous
    /// handler. A host without one refuses transfers.
    /// </summary>
    void SetTransferHandler(LinkPeerTransferHandler handler);

    /// <summary>
    /// Sets what takes channels named <paramref name="name"/>, replacing any
    /// previous handler for that name.
    /// </summary>
    /// <remarks>
    /// Handlers are per name rather than one for all of them because a
    /// channel's name is what says what is on it: an application receiving
    /// audio and telemetry wants two pieces of code, not one with a switch.
    /// </remarks>
    void OnChannel(string name, LinkChannelHandler handler);

    /// <summary>Mints an invitation and starts offering it.</summary>
    /// <exception cref="ObjectDisposedException">If the host has been closed.</exception>
    /// <exception cref="LinkException">
    /// If the host is already holding as many machines as <see cref="LinkOptions.MaxPeers"/>
    /// allows. The code minted there could pair with nothing, and the device
    /// scanning it would be the only one told; forget a peer first.
    /// </exception>
    Task<LinkInvitation> InviteAsync(
        InvitationRequest? request = null,
        CancellationToken cancellationToken = default);

    /// <summary>Withdraws one invitation.</summary>
    /// <returns>Whether it was still live.</returns>
    Task<bool> RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unpairs one machine: it is dropped, forgotten, and refused if it comes
    /// back without a fresh invitation.
    /// </summary>
    /// <remarks>
    /// This is the "unpair this device" every application with a device list
    /// needs. <see cref="TailcatLink.ForgetAsync"/> is the other thing — it
    /// takes this machine's own identity with it.
    /// <para>
    /// The invitation the machine was admitted by is withdrawn with it, since
    /// it still holds that code and would otherwise be admitted again on its
    /// next reconnection. A reusable invitation shown to several devices goes
    /// with it, so invite the others again.
    /// </para>
    /// </remarks>
    Task ForgetPeerAsync(ILinkPeer peer, CancellationToken cancellationToken = default);

    /// <summary>Waits until at least one peer has a session up, and returns it.</summary>
    Task<ILinkPeer> WaitForPeerAsync(CancellationToken cancellationToken = default);
}
