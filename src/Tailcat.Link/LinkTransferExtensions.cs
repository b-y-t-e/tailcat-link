// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Runtime.InteropServices;
using Tailcat.Link.Protocol;

namespace Tailcat.Link;

/// <summary>
/// The two things most transfers actually are: a file, and an array that is
/// too big to be a request.
/// </summary>
/// <remarks>
/// Extensions rather than members for the same reason as
/// <see cref="LinkTextExtensions"/>: <see cref="ILink"/> stays one shape —
/// a stream and what to say about it — and the convenience sits beside it.
/// </remarks>
public static class LinkTransferExtensions
{
    /// <summary>Sends a file, whatever its size.</summary>
    /// <remarks>
    /// The file is read as it is sent and never held in memory, and it is
    /// opened for shared reading so that a transfer taking an hour does not
    /// lock it against the rest of the machine.
    /// </remarks>
    /// <param name="link">The link to send it over.</param>
    /// <param name="path">The file to send.</param>
    /// <param name="offer">
    /// What to tell the other machine. By default the file's name and its
    /// length; a caller that gives its own is still given the length, since
    /// this end knows it.
    /// </param>
    /// <param name="progress">Told after each block that reaches the peer.</param>
    /// <param name="cancellationToken">Gives up on the transfer.</param>
    public static async Task SendFileAsync(
        this ILink link,
        string path,
        TransferOffer? offer = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentException.ThrowIfNullOrEmpty(path);

        FileStream file = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            TransferFrame.BlockBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (file.ConfigureAwait(false))
        {
            TransferOffer sending = (offer ?? new TransferOffer { Name = Path.GetFileName(path) })
                with
            { Length = file.Length };
            await link.SendAsync(file, sending, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends bytes already in memory, however many there are.</summary>
    /// <remarks>
    /// For anything past a megabyte or two this is the method to reach for
    /// rather than <see cref="ILink.NotifyAsync"/>: a notification is a
    /// message, capped and held whole at both ends, while this is a stream
    /// and has no size limit at all.
    /// </remarks>
    /// <param name="link">The link to send them over.</param>
    /// <param name="content">The bytes to send. Not copied.</param>
    /// <param name="offer">What to tell the other machine about them.</param>
    /// <param name="progress">Told after each block that reaches the peer.</param>
    /// <param name="cancellationToken">Gives up on the transfer.</param>
    public static async Task SendBytesAsync(
        this ILink link,
        ReadOnlyMemory<byte> content,
        TransferOffer? offer = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        // Over the array itself where there is one, so that sending two
        // gigabytes does not first copy two gigabytes.
        byte[] buffer;
        int offset;
        if (MemoryMarshal.TryGetArray(content, out ArraySegment<byte> array) && array.Array is not null)
        {
            (buffer, offset) = (array.Array, array.Offset);
        }
        else
        {
            (buffer, offset) = (content.ToArray(), 0);
        }
        using MemoryStream stream = new(buffer, offset, content.Length, writable: false);
        TransferOffer sending = (offer ?? new TransferOffer()) with { Length = content.Length };
        await link.SendAsync(stream, sending, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes every transfer the peer sends and writes it to a directory.</summary>
    /// <remarks>
    /// The file is named from <see cref="IncomingTransfer.SuggestedFileName"/>
    /// and not from what the peer sent, which could be a path into anywhere.
    /// Existing files are replaced.
    /// </remarks>
    /// <param name="link">The link to receive on.</param>
    /// <param name="directory">Where the files go. Created if it is not there.</param>
    /// <param name="progress">Told as each transfer moves.</param>
    public static void SaveTransfersTo(
        this ILink link,
        string directory,
        IProgress<TransferProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentException.ThrowIfNullOrEmpty(directory);

        Directory.CreateDirectory(directory);
        link.OnTransfer((transfer, ct) =>
            transfer.SaveToAsync(Path.Combine(directory, transfer.SuggestedFileName), progress, ct));
    }
}
