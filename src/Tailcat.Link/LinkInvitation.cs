// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link;

/// <summary>
/// One invitation a host is making: the code to publish, when it stops being
/// worth publishing, and what the operator called it.
/// </summary>
/// <remarks>
/// A host may be making several of these at once — one per device it is
/// waiting for — each with its own expiry, and each revocable on its own
/// through <see cref="ILinkHost.RevokeInvitationAsync"/>.
/// </remarks>
/// <param name="Id">Names this invitation, so it can be revoked.</param>
/// <param name="Code">The code to show, print or draw as a barcode.</param>
/// <param name="ExpiresAt">When the code stops being able to pair a machine.</param>
/// <param name="Label">
/// What the operator called it. It never goes on the wire; it is here so that
/// a list of pending invitations reads as something other than identifiers.
/// </param>
/// <param name="SingleUse">Whether admitting one machine spends it.</param>
public sealed record LinkInvitation(
    Guid Id,
    InvitationCode Code,
    DateTimeOffset ExpiresAt,
    string? Label,
    bool SingleUse);
