// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link.Protocol;

/// <summary>
/// What both machines must agree on, whatever either of them was configured
/// with.
/// </summary>
internal static class LinkProtocol
{
    /// <summary>
    /// How long a machine remembers what it answered, so that a retry of the
    /// same request is answered rather than run again.
    /// </summary>
    /// <remarks>
    /// It is fixed rather than an option because the two ends of it are on
    /// different machines: the window is opened by the sender's retrying and
    /// has to be honoured by the receiver. A host configured to remember for
    /// less than its peer retries would forget while that peer is still
    /// asking, and run the handler a second time — silently, since nothing on
    /// the wire carries the sender's deadline. <see cref="LinkOptions.RequestDeadline"/>
    /// is bounded by this instead, so no pair of configurations can lose the
    /// promise that a request runs once.
    /// </remarks>
    public static readonly TimeSpan ExchangeRetention = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The longest <see cref="LinkOptions.RequestDeadline"/> that still leaves
    /// the retention window room to be honoured.
    /// </summary>
    /// <remarks>
    /// A deadline equal to the retention would be spent: the last retry it
    /// allows is sent just before the window closes and arrives on the other
    /// machine after it, where the exchange has already been forgotten and the
    /// handler runs a second time. The margin is what that retry takes to
    /// travel — a relay round trip and a fresh session's handshake — so a
    /// retry sent inside the deadline is always recognised as one.
    /// </remarks>
    public static readonly TimeSpan LongestRequestDeadline =
        ExchangeRetention - TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a machine holds on to a transfer that stopped part-way, and
    /// to the answer of one it has finished.
    /// </summary>
    /// <remarks>
    /// The same bargain as <see cref="ExchangeRetention"/>, over a longer
    /// window because what is being kept is worth more: everything already
    /// received of a file, so that a link which went down for a few minutes
    /// resumes mid-file instead of sending twenty gigabytes again. It is a
    /// constant for the same reason too — the sending machine is the one that
    /// decides when to come back, and nothing on the wire tells the receiver
    /// how patient that sender was configured to be.
    /// </remarks>
    public static readonly TimeSpan TransferRetention = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The longest <see cref="LinkOptions.TransferStallTimeout"/> that the
    /// receiving machine will still be holding the transfer for.
    /// </summary>
    /// <remarks>
    /// The margin is what a resumed attempt takes to arrive: a sender that
    /// waited right up to the retention would find its transfer forgotten and
    /// start a second one from zero.
    /// </remarks>
    public static readonly TimeSpan LongestTransferStall =
        TransferRetention - TimeSpan.FromMinutes(1);
}
