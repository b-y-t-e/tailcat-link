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
/// Answers a request of any size, as content rather than as bytes in memory.
/// </summary>
/// <remarks>
/// <para>
/// The one handler that is not bounded by memory: <paramref name="request"/>
/// arrives as a stream, resuming across reconnections without the handler
/// knowing, and the answer goes back as content — bytes, a file, or a stream —
/// resumed the same way.
/// </para>
/// <para>
/// It runs once per request, however many times a session dies under it: a
/// request that arrives again is joined to the run already under way, and its
/// answer is sent again rather than made again.
/// </para>
/// </remarks>
/// <param name="request">What the other machine sent.</param>
/// <param name="cancellationToken">Cancelled when the link closes, not when a session drops.</param>
/// <returns>The answer, or <see cref="LinkContent.Empty"/> for none.</returns>
public delegate Task<LinkContent> LinkContentHandler(
    IncomingTransfer request,
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
/// <see cref="TailcatLink.JoinAsync(string, string?, LinkOptions?, CancellationToken)"/> on the machine that is given it.
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
    /// <see cref="RequestAsync(ReadOnlyMemory{byte}, CancellationToken)"/> waits for the link by itself.
    /// </remarks>
    bool IsConnected { get; }

    /// <summary>
    /// The machine this one is paired with, or the zero key before the first
    /// pairing has happened.
    /// </summary>
    NodePublic Peer { get; }

    /// <summary>Where this link stands, as one value a user interface can bind to.</summary>
    /// <remarks>
    /// <see cref="IsConnected"/> cannot say "trying", which is the state a
    /// link spends most of a bad afternoon in and the one an operator most
    /// wants to see.
    /// </remarks>
    LinkConnectionState State { get; }

    /// <summary>Raised when a session comes up, including every re-established one.</summary>
    event Action? Connected;

    /// <summary>Raised when a session goes down, with the reason it ended in words.</summary>
    /// <remarks>
    /// <see cref="SessionEnded"/> says the same thing in a form worth
    /// branching on; this one stays because applications written against it
    /// still work.
    /// </remarks>
    event Action<string>? Disconnected;

    /// <summary>
    /// Raised when a session goes down, with a reason code beside the words.
    /// </summary>
    /// <remarks>
    /// The structured form of <see cref="Disconnected"/>: showing a fresh
    /// invitation code on <see cref="LinkDisconnectReason.Refused"/> and
    /// nothing at all on <see cref="LinkDisconnectReason.NetworkLost"/> is the
    /// ordinary case, and matching on a message to tell them apart is what
    /// this exists to replace. Both are raised for an attempt that never
    /// became a session too, since being refused is exactly that; use
    /// <see cref="State"/> to tell a link that dropped from one that has not
    /// come up yet.
    /// </remarks>
    event EventHandler<DisconnectedEventArgs>? SessionEnded;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    event EventHandler<LinkStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Sets what answers requests from the peer. Replaces any previous
    /// handler, including one set as content; a link without one refuses
    /// requests with an error.
    /// </summary>
    /// <remarks>
    /// The request arrives whole in memory, which suits a command and not a
    /// file. <see cref="OnRequest(LinkContentHandler)"/> takes the same
    /// requests as a stream.
    /// </remarks>
    void OnRequest(LinkRequestHandler handler);

    /// <summary>
    /// Sets what answers requests from the peer, taking each one as content
    /// of any size. Replaces any previous handler, including one set as bytes.
    /// </summary>
    /// <seealso cref="RequestAsync(LinkContent, CancellationToken)"/>
    void OnRequest(LinkContentHandler handler);

    /// <summary>
    /// <see cref="OnRequest(LinkRequestHandler)"/>, under the name
    /// <see cref="ILinkHost"/> uses, so that moving from one peer to several
    /// renames nothing.
    /// </summary>
    void SetRequestHandler(LinkRequestHandler handler) => OnRequest(handler);

    /// <summary>
    /// <see cref="OnRequest(LinkContentHandler)"/>, under the name
    /// <see cref="ILinkHost"/> uses.
    /// </summary>
    void SetRequestHandler(LinkContentHandler handler) => OnRequest(handler);

    /// <summary>
    /// Sends a request and waits for the peer's answer, waiting through a
    /// reconnection if one is needed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no size limit. A request too large for one session to carry
    /// carries on from where it stopped on the next one, and so does its
    /// answer. Both are held in memory here because that is the shape of this
    /// method; <see cref="RequestAsync(LinkContent, CancellationToken)"/> is
    /// the same request without that.
    /// </para>
    /// <para>
    /// A request that has to cross a reconnection is not run again: it carries
    /// an id, and a peer that has already answered it sends that answer rather
    /// than calling its handler a second time. So a request that succeeds was
    /// handled exactly once. The one case outside that promise is the peer's
    /// process ending mid-request — nothing on this machine can know how far a
    /// handler got before the other machine died.
    /// </para>
    /// </remarks>
    /// <exception cref="LinkException">
    /// If nothing moved for <see cref="LinkOptions.RequestDeadline"/>, or if
    /// the peer's handler failed.
    /// </exception>
    Task<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a request of any size and returns the answer as it starts to
    /// arrive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one way to send anything: a kilobyte of JSON and twenty gigabytes of
    /// video go the same way. Neither machine holds more of either than a few
    /// megabytes, both directions carry on from where they stopped when a
    /// session dies, and the peer's handler runs once.
    /// </para>
    /// <para>
    /// It returns once the answer has begun, not once it has all arrived: the
    /// rest keeps coming through as many reconnections as it takes, and a read
    /// of <see cref="IncomingTransfer.Content"/> waits for it. Dispose the
    /// answer when done with it; disposing it early stops asking for the rest.
    /// <paramref name="cancellationToken"/> covers the whole exchange, the
    /// answer included.
    /// </para>
    /// <para>
    /// Carrying on after a session dies needs content that can be read again
    /// from the middle — bytes, a file, a seekable stream — on this machine,
    /// and for the answer on the other. Content that cannot works until the
    /// first session dies under it and then fails, rather than arriving with a
    /// hole in it.
    /// </para>
    /// </remarks>
    /// <exception cref="RemoteHandlerException">
    /// If the peer's handler failed, or the peer cannot take the request at
    /// all. Neither is retried.
    /// </exception>
    /// <exception cref="LinkException">
    /// If nothing moved for <see cref="LinkOptions.TransferStallTimeout"/>.
    /// </exception>
    Task<IncomingTransfer> RequestAsync(LinkContent request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message the peer is not expected to answer. It reaches the
    /// peer's handler like a request, whose answer is discarded.
    /// </summary>
    /// <remarks>
    /// Delivered once, through reconnections: it returns when the peer has the
    /// whole message, and its handler runs once however many sessions that
    /// took. A peer built before this could recognise a repeat gets it the way
    /// it always did — once, and lost if the session dies mid-message.
    /// </remarks>
    Task NotifyAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="NotifyAsync(ReadOnlyMemory{byte}, CancellationToken)"/> for
    /// content of any size.
    /// </summary>
    Task NotifyAsync(LinkContent message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets what takes transfers from the peer. Replaces any previous
    /// handler; a link without one refuses transfers.
    /// </summary>
    /// <seealso cref="SendAsync"/>
    void OnTransfer(LinkTransferHandler handler);

    /// <summary>
    /// Sends content of any size, in blocks, resuming through as many
    /// reconnections as it takes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A notification for the transfer handler: the same exchange as
    /// <see cref="RequestAsync(LinkContent, CancellationToken)"/>, delivered to
    /// <see cref="OnTransfer"/> rather than to the request handler, and
    /// finished when that handler has finished. Twenty gigabytes of video is an
    /// ordinary use of it, and neither machine ever holds more than a few
    /// megabytes of it. Nothing here has to be chunked by the caller.
    /// </para>
    /// <para>
    /// A transfer survives what the link survives. When a session dies
    /// mid-file, the next one is asked where the receiving machine got to and
    /// the content carries on from exactly there, into the same handler,
    /// which never learns that anything happened. That is what requires
    /// <paramref name="content"/> to be seekable — a file or an array is; a
    /// socket or a stream being generated as it is sent is not, and a
    /// transfer from one of those fails when its session does.
    /// </para>
    /// <para>
    /// Reading is what paces it. The bytes go no faster than the peer's
    /// handler consumes them, so sending from a fast disk to a slow one
    /// costs memory on neither machine.
    /// </para>
    /// </remarks>
    /// <param name="content">The bytes to send, read from where it now is.</param>
    /// <param name="offer">What to tell the other machine about them.</param>
    /// <param name="progress">Told after each block that reaches the peer.</param>
    /// <param name="cancellationToken">Gives up on the transfer.</param>
    /// <exception cref="RemoteHandlerException">
    /// If the peer is not receiving transfers, or its handler threw. Neither
    /// is retried.
    /// </exception>
    /// <exception cref="LinkException">
    /// If nothing moved for <see cref="LinkOptions.TransferStallTimeout"/>,
    /// or if the transfer would have to be resumed and its content cannot be
    /// rewound.
    /// </exception>
    Task SendAsync(
        Stream content,
        TransferOffer offer,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets what takes channels named <paramref name="name"/>, replacing any
    /// previous handler for that name.
    /// </summary>
    /// <remarks>
    /// Handlers are per name because a channel's name is what says what is on
    /// it: an application receiving audio and telemetry wants two pieces of
    /// code, not one with a switch.
    /// </remarks>
    /// <seealso cref="OpenChannelAsync"/>
    void OnChannel(string name, Func<ILinkChannelReader, CancellationToken, Task> handler);

    /// <summary>
    /// Opens an ordered, non-durable channel to the peer, into the handler it
    /// registered for <paramref name="name"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other shape beside <see cref="RequestAsync(LinkContent, CancellationToken)"/>.
    /// A request, of whatever size, promises to arrive and to survive a
    /// reconnection. A channel
    /// is the stream of the moment — audio, telemetry, input events — and
    /// promises neither: <b>ordered within the channel, and not durable</b>.
    /// It ends with the session that carries it, which is correct rather than
    /// a limitation, because a frame of audio from ten seconds ago is worth
    /// nothing.
    /// </para>
    /// <para>
    /// Unlike a request, this needs a session: there is nothing to resume a
    /// channel onto, so it waits for one rather than being buffered.
    /// </para>
    /// </remarks>
    /// <exception cref="RemoteHandlerException">If the peer has no handler for that name.</exception>
    /// <exception cref="LinkException">If no session could be had in time.</exception>
    Task<ILinkChannelWriter> OpenChannelAsync(string name, CancellationToken cancellationToken = default);

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
