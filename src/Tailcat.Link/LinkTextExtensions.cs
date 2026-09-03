// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text;

namespace Tailcat.Link;

/// <summary>
/// Text over a link, for the applications — most of them — whose commands and
/// answers are strings.
/// </summary>
/// <remarks>
/// These are extensions rather than members so that <see cref="ILink"/> stays
/// one thing: bytes in, bytes out. Anything richer than text, such as JSON,
/// is a serializer away and does not belong in this library either.
/// </remarks>
public static class LinkTextExtensions
{
    /// <summary>Sends UTF-8 text and returns the peer's answer as text.</summary>
    public static async Task<string> RequestAsync(
        this ILink link,
        string request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        byte[] answer = await link
            .RequestAsync(Encoding.UTF8.GetBytes(request), cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(answer);
    }

    /// <summary>Sends UTF-8 text the peer will not answer.</summary>
    public static Task NotifyAsync(this ILink link, string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        return link.NotifyAsync(Encoding.UTF8.GetBytes(message), cancellationToken);
    }

    /// <summary>Answers the peer's text requests with text.</summary>
    public static void OnRequest(this ILink link, Func<string, CancellationToken, Task<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(handler);
        link.OnRequest(async (request, ct) =>
            Encoding.UTF8.GetBytes(await handler(Encoding.UTF8.GetString(request.Span), ct).ConfigureAwait(false)));
    }

    /// <summary>Answers the peer's text requests with text, without the ceremony.</summary>
    public static void OnRequest(this ILink link, Func<string, string> handler)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(handler);
        link.OnRequest((request, _) => Task.FromResult<ReadOnlyMemory<byte>>(
            Encoding.UTF8.GetBytes(handler(Encoding.UTF8.GetString(request.Span)))));
    }
}
