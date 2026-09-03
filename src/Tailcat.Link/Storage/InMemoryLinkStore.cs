// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Collections.Concurrent;

namespace Tailcat.Link.Storage;

/// <summary>
/// A store that forgets everything when the process ends.
/// </summary>
/// <remarks>
/// For tests, and for the rare application that pairs afresh every run. Using
/// it in a service defeats the point: an unpaired machine needs a human with
/// the code.
/// </remarks>
public sealed class InMemoryLinkStore : ILinkStore
{
    private readonly ConcurrentDictionary<string, LinkState> _states = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public Task<LinkState?> LoadAsync(string appName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        return Task.FromResult(_states.GetValueOrDefault(appName));
    }

    /// <inheritdoc/>
    public Task SaveAsync(string appName, LinkState state, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        ArgumentNullException.ThrowIfNull(state);
        _states[appName] = state;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string appName, CancellationToken cancellationToken = default)
    {
        _states.TryRemove(appName, out _);
        return Task.CompletedTask;
    }
}
