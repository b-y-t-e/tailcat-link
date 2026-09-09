// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link;

/// <summary>What the joining machine tells the host about itself.</summary>
public sealed record JoinRequest
{
    /// <summary>
    /// What to call this machine in the host's list of devices.
    /// </summary>
    /// <remarks>
    /// It is a hint and nothing more. It comes from this machine, the host
    /// has no way to check it, and nothing on either end should be keyed off
    /// it — the peer's public key is the only thing a session authenticates.
    /// </remarks>
    public string? DisplayName { get; init; }
}
