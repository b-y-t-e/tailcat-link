// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link;

/// <summary>
/// Something went wrong with a link in a way the caller should hear about:
/// a malformed invitation code, a request that went unanswered for too long,
/// or a handler on the other machine that threw.
/// </summary>
/// <remarks>
/// A session dropping is not one of these. It is the ordinary weather of a
/// link that lives for days, and is handled by reconnecting rather than by
/// telling the caller. The one subclass, <see cref="RemoteHandlerException"/>,
/// separates the failures that come from the other machine's application code
/// — which retrying cannot fix — from the ones that come from the link.
/// </remarks>
public class LinkException : Exception
{
    /// <summary>Creates an exception with no message.</summary>
    public LinkException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    public LinkException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an underlying cause.</summary>
    public LinkException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
