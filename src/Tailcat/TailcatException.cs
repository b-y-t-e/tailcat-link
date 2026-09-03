// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat;

/// <summary>
/// The error type for tailcat's own failures: malformed connection blobs,
/// unusable DERP maps, and the like. It stands in for the errors Go's
/// tailcat package returns.
/// </summary>
public class TailcatException : Exception
{
    /// <summary>Creates an exception with no message.</summary>
    public TailcatException()
    {
    }

    /// <summary>Creates an exception with the given message.</summary>
    public TailcatException(string message) : base(message)
    {
    }

    /// <summary>Creates an exception wrapping an underlying cause.</summary>
    public TailcatException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
