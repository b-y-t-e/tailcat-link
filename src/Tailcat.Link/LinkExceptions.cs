// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link;

/// <summary>The other machine would not have this one.</summary>
/// <remarks>
/// It is worth telling apart from the network failing, because the answer is
/// different: a refused pairing is repaired by a fresh invitation code, and
/// nothing else. The browser client raises <c>PairingRefusedError</c> for the
/// same case.
/// </remarks>
public class PairingRefusedException : LinkException
{
    /// <summary>Creates an exception with no message.</summary>
    public PairingRefusedException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    public PairingRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an underlying cause.</summary>
    public PairingRefusedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The invitation is past the moment it stopped being able to pair.</summary>
/// <remarks>
/// Raised on the hosting end, which is the only end that knows: a machine
/// arriving with an invitation whose window has closed is refused in exactly
/// the words a wrong token gets, so that nothing on the wire helps anybody
/// search for a code that would work. The host raises this to itself instead,
/// where it reaches the operator's log through
/// <see cref="LinkOptions.LoggerFactory"/> — the one refusal with an obvious
/// cure, which is a fresh invitation. The browser client has no equivalent,
/// because the end that joined is never told why.
/// </remarks>
public class InvitationExpiredException : LinkException
{
    /// <summary>Creates an exception with no message.</summary>
    public InvitationExpiredException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    public InvitationExpiredException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an underlying cause.</summary>
    public InvitationExpiredException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Nothing came back inside the time the caller was willing to wait.</summary>
/// <remarks>
/// A link that is merely down does not raise this: a request waits through a
/// reconnection. This is what is left when the waiting itself ran out.
/// </remarks>
public class LinkTimeoutException : LinkException
{
    /// <summary>Creates an exception with no message.</summary>
    public LinkTimeoutException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    public LinkTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an underlying cause.</summary>
    public LinkTimeoutException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The link, the peer or the channel has been closed for good.</summary>
public class LinkClosedException : LinkException
{
    /// <summary>Creates an exception with no message.</summary>
    public LinkClosedException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    public LinkClosedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an underlying cause.</summary>
    public LinkClosedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
