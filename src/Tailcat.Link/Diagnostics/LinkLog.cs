// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tailcat.Link.Diagnostics;

/// <summary>
/// Where the link says what it is doing: an <see cref="ILogger"/>, and the
/// plain string sink that was here before it.
/// </summary>
/// <remarks>
/// Both, because <see cref="LinkOptions.Log"/> is a shipped API and an
/// application that set it must keep working. A string with no level, no
/// category and no structure cannot be filtered, correlated or shipped
/// anywhere, which is why it is no longer the only way out.
/// </remarks>
internal sealed partial class LinkLog(ILogger? logger, Action<string>? sink)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>Reports something that happened in the ordinary course.</summary>
    public void Say(string message)
    {
        LogSaid(_logger, message);
        sink?.Invoke(message);
    }

    /// <summary>Reports something that went wrong but did not stop the link.</summary>
    public void Warn(string message)
    {
        LogWarned(_logger, message);
        sink?.Invoke(message);
    }

    /// <summary>The same, keeping the exception itself for whoever can use it.</summary>
    /// <remarks>
    /// For the blanket catches only, where what arrived is a surprise and the
    /// stack is the only thing that says where it came from. An expected end —
    /// a peer that re-dialled, a relay that dropped a record — is reported by
    /// its words instead, because a stack on every reconnection is noise that
    /// buries the one that matters. <see cref="LinkOptions.Log"/> still sees
    /// only the message: a string sink has nowhere to put an exception, which
    /// is why it is no longer the only way out of here.
    /// </remarks>
    public void Say(string message, Exception cause)
    {
        LogSaidAbout(_logger, message, cause);
        sink?.Invoke(message);
    }

    /// <inheritdoc cref="Say(string, Exception)"/>
    public void Warn(string message, Exception cause)
    {
        LogWarnedAbout(_logger, message, cause);
        sink?.Invoke(message);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "{Message}")]
    private static partial void LogSaid(ILogger logger, string message);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "{Message}")]
    private static partial void LogWarned(ILogger logger, string message);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "{Message}")]
    private static partial void LogSaidAbout(ILogger logger, string message, Exception cause);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "{Message}")]
    private static partial void LogWarnedAbout(ILogger logger, string message, Exception cause);
}
