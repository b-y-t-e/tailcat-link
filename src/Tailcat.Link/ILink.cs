// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;

namespace Tailcat.Link;

/// <summary>
/// Answers a request that arrived from the machine at the other end.
/// </summary>
/// <param name="request">What the peer sent.</param>
/// <param name="cancellationToken">Cancelled when the link is closing.</param>
/// <returns>The answer to send back.</returns>
public delegate Task<ReadOnlyMemory<byte>> LinkRequestHandler(
    ReadOnlyMemory<byte> request,
    CancellationToken cancellationToken);

/// <summary>
/// A link between two machines that stays up: it re-establishes itself after
/// a network change, a relay outage, or either machine rebooting, without
/// anybody being there to help.
/// </summary>
/// <remarks>
/// <para>
/// Both ends are equal once paired: each can send a request and each can
/// answer one. The link is created by <see cref="TailcatLink.HostAsync"/> on
/// the machine that publishes an <see cref="InvitationCode"/>, and by
/// <see cref="TailcatLink.JoinAsync"/> on the machine that is given it.
/// </para>
/// <para>
/// Nothing here needs to be called in a particular order, and a request sent
/// while the link happens to be down is not an error: it waits for the link
/// to come back, up to <see cref="LinkOptions.RequestDeadline"/>.
/// </para>
/// </remarks>
public interface ILink : IAsyncDisposable
{
    /// <summary>
    /// The code that pairs the other machine with this one — this machine's
    /// address when hosting, and the code that was used to join otherwise.
    /// </summary>
    /// <remarks>
    /// It does not change: not when the machine moves network, and not when
    /// it reboots. Publishing it once, as text or as a barcode, is enough.
    /// </remarks>
    InvitationCode InvitationCode { get; }

    /// <summary>
    /// When <see cref="InvitationCode"/> stops being able to pair a machine,
    /// or null when it has nothing left to buy — this machine is already
    /// paired, or it is the end that joined.
    /// </summary>
    /// <remarks>
    /// Worth watching on a machine that shows its code on a screen: past this
    /// moment the code is refused, and <see cref="RenewInvitationAsync"/> is
    /// what replaces it.
    /// </remarks>
    DateTimeOffset? InvitationExpiresAt { get; }

    /// <summary>Whether a session to the peer is up right now.</summary>
    /// <remarks>
    /// Worth showing in a UI, but not worth branching on before sending:
    /// <see cref="RequestAsync"/> waits for the link by itself.
    /// </remarks>
    bool IsConnected { get; }

    /// <summary>
    /// The machine this one is paired with, or the zero key before the first
    /// pairing has happened.
    /// </summary>
    NodePublic Peer { get; }

    /// <summary>Raised when a session comes up, including every re-established one.</summary>
    event Action? Connected;

    /// <summary>Raised when a session goes down, with the reason it ended.</summary>
    event Action<string>? Disconnected;

    /// <summary>
    /// Sets what answers requests from the peer. Replaces any previous
    /// handler; a link without one refuses requests with an error.
    /// </summary>
    void OnRequest(LinkRequestHandler handler);

    /// <summary>
    /// Sends a request and waits for the peer's answer, waiting through a
    /// reconnection if one is needed.
    /// </summary>
    /// <remarks>
    /// A request that has to cross a reconnection is sent again, but it is not
    /// run again: it carries an id, and a peer that has already answered it
    /// replies from memory rather than calling its handler a second time. So a
    /// request that succeeds was handled exactly once. The one case outside
    /// that promise is the peer's process ending mid-request — nothing on this
    /// machine can know how far a handler got before the other machine died.
    /// </remarks>
    /// <exception cref="LinkException">
    /// If no answer arrives within <see cref="LinkOptions.RequestDeadline"/>,
    /// or if the peer's handler failed.
    /// </exception>
    Task<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message the peer is not expected to answer. It reaches the
    /// peer's handler like a request, whose answer is discarded.
    /// </summary>
    Task NotifyAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the invitation code worth publishing now, minting a fresh one
    /// if the last has run out.
    /// </summary>
    /// <remarks>
    /// A hosting machine that has been up longer than
    /// <see cref="LinkOptions.PairingWindow"/> without being paired is
    /// showing a code it would itself refuse; this is how an application that
    /// can publish a code again — printing it, drawing a barcode, sending it
    /// somewhere — gets one without restarting the process. Calling it while
    /// the current code is still good returns that same code, so renewing on
    /// a timer never invalidates one somebody is reading. On the machine that
    /// joined there is nothing to renew, and the code it was given comes back
    /// unchanged.
    /// </remarks>
    Task<InvitationCode> RenewInvitationAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits until a session is up.</summary>
    Task WaitUntilConnectedAsync(CancellationToken cancellationToken = default);
}
