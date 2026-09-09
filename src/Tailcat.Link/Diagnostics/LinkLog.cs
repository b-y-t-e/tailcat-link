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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "{Message}")]
    private static partial void LogSaid(ILogger logger, string message);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "{Message}")]
    private static partial void LogWarned(ILogger logger, string message);
}
