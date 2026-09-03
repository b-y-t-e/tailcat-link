// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link.Storage;

/// <summary>
/// Where a machine keeps what it must remember between runs, one entry per
/// application name.
/// </summary>
/// <remarks>
/// It exists as an interface because the right place differs: a desktop
/// application wants a file in the user's profile, a service wants somewhere
/// its account can read, and a test wants memory. The default,
/// <see cref="FileLinkStore"/>, is the first of those.
/// </remarks>
public interface ILinkStore
{
    /// <summary>Reads the stored state, or null if this machine has none yet.</summary>
    /// <exception cref="LinkException">If something is stored but cannot be read back.</exception>
    Task<LinkState?> LoadAsync(string appName, CancellationToken cancellationToken = default);

    /// <summary>Stores the state, replacing whatever was there.</summary>
    Task SaveAsync(string appName, LinkState state, CancellationToken cancellationToken = default);

    /// <summary>Forgets everything about <paramref name="appName"/>, unpairing the machine.</summary>
    Task DeleteAsync(string appName, CancellationToken cancellationToken = default);
}
