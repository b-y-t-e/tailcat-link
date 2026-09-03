// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link;

/// <summary>
/// The request arrived, and the other machine's handler threw. The message is
/// that handler's.
/// </summary>
/// <remarks>
/// This is the one failure a link must not retry. Everything else it can see
/// — a dead session, a peer that has not come back yet — is worth trying
/// again, but a handler that refused a command will refuse it just as firmly
/// on the next session, and retrying would turn a clear error into a wait
/// that ends in a timeout with the reason thrown away.
/// </remarks>
public sealed class RemoteHandlerException : LinkException
{
    /// <summary>Creates an exception with no message.</summary>
    public RemoteHandlerException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    public RemoteHandlerException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an underlying cause.</summary>
    public RemoteHandlerException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
