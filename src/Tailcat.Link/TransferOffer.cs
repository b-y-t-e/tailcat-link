// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link;

/// <summary>
/// What a transfer says about itself before its first byte moves.
/// </summary>
/// <remarks>
/// All of it is advisory: the receiving machine decides what to do with the
/// bytes, and nothing here is trusted enough to be used as a path. See
/// <see cref="IncomingTransfer.SuggestedFileName"/> for the one field that
/// looks like it could be.
/// </remarks>
public sealed record TransferOffer
{
    /// <summary>What the content is called — a file name, usually.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The media type, when the application has one worth sending.</summary>
    public string ContentType { get; init; } = string.Empty;

    /// <summary>
    /// How many bytes are coming, or null when the sender does not know.
    /// </summary>
    /// <remarks>
    /// Only used for progress: a transfer of unknown length works exactly the
    /// same, it just cannot say how far along it is.
    /// </remarks>
    public long? Length { get; init; }

    /// <summary>Anything else the application wants to send with it.</summary>
    /// <remarks>
    /// Small — a few kilobytes at most, since it travels in the first frame —
    /// and opaque to this library. A hash of the content belongs here.
    /// </remarks>
    public ReadOnlyMemory<byte> Metadata { get; init; }
}

/// <summary>How far a transfer has got.</summary>
/// <param name="Transferred">Bytes that have reached the other machine.</param>
/// <param name="Total">The total, when <see cref="TransferOffer.Length"/> said.</param>
public readonly record struct TransferProgress(long Transferred, long? Total)
{
    /// <summary>The fraction done, or null when the total is unknown.</summary>
    public double? Fraction =>
        Total is > 0 ? Math.Min(1d, (double)Transferred / Total.Value) : null;
}

/// <summary>
/// Takes a transfer the peer is sending.
/// </summary>
/// <remarks>
/// Called once per transfer, however many reconnections it takes to deliver:
/// reading <see cref="IncomingTransfer.Content"/> blocks while the link is
/// down and continues when it comes back. The transfer is not acknowledged
/// until this returns, so the sender's <see cref="ILink.SendAsync"/> completing
/// means this handler completed — and a handler that throws fails the
/// sender's call rather than being retried.
/// </remarks>
/// <param name="transfer">What is arriving, and the stream to read it from.</param>
/// <param name="cancellationToken">Cancelled when the link is closing.</param>
public delegate Task LinkTransferHandler(IncomingTransfer transfer, CancellationToken cancellationToken);
