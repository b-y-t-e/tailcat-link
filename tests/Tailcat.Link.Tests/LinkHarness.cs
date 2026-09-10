// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tailcat.Link.Storage;

namespace Tailcat.Link.Tests;

/// <summary>
/// What every end-to-end test in this project needs: options aimed at the
/// in-memory relay, and a deadline so a test that hangs fails rather than
/// running until the suite is killed.
/// </summary>
internal static class LinkHarness
{
    /// <summary>
    /// Deliberately impatient compared to the defaults: a test should spend
    /// its time reconnecting, not waiting to notice that it must.
    /// </summary>
    public static LinkOptions OptionsFor(FakeRelayGatewayFactory gateways, ILinkStore store) => new()
    {
        Store = store,
        Gateway = gateways,
        RequestTimeout = TimeSpan.FromSeconds(5),
        RequestDeadline = TimeSpan.FromSeconds(45),
        HeartbeatInterval = TimeSpan.FromSeconds(1),
        MinReconnectDelay = TimeSpan.FromMilliseconds(200),
        MaxReconnectDelay = TimeSpan.FromSeconds(2),
    };

    /// <summary>Gives the test <paramref name="limit"/> to finish in.</summary>
    public static CancellationTokenSource Deadline(TimeSpan limit)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(limit);
        return cts;
    }
}

/// <summary>
/// Keeps what the link said, and at what level.
/// </summary>
/// <remarks>
/// <see cref="LinkOptions.Log"/> would be enough to read the words, and that
/// is exactly why it is not enough here: the level is the part that decides
/// whether an operator ever sees a line, so a test about being told something
/// has to assert it.
/// </remarks>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _lines = new();

    /// <summary>Whether something was said at that level with those words in it.</summary>
    public bool Said(LogLevel level, string fragment) =>
        _lines.Any(line => line.Level == level && line.Message.Contains(fragment, StringComparison.Ordinal));

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new Sink(_lines);

    /// <inheritdoc/>
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    /// <summary>Everything said, for the message of a test that failed.</summary>
    public override string ToString() =>
        _lines.IsEmpty ? "nothing" : string.Join("; ", _lines.Select(line => $"[{line.Level}] {line.Message}"));

    private sealed class Sink(ConcurrentQueue<(LogLevel Level, string Message)> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lines.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
