// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Diagnostics.CodeAnalysis;

namespace Tailcat.Link;

/// <summary>
/// The one thing that has to travel between the two machines by hand: a short
/// line of text — printable, and small enough for a barcode — that says where
/// a host can be reached and proves the bearer was invited.
/// </summary>
/// <remarks>
/// <para>
/// Two halves separated by a dot. The first is a <see cref="ConnBlob"/>: the
/// host's public key together with the relay region it listens in, which is
/// all a peer needs to find it and nothing a change of network can
/// invalidate. The second is the pairing token.
/// </para>
/// <para>
/// The address half is not a secret and cannot be made one — it is what the
/// host tells every relay it connects to, so the operator of a public relay
/// sees it. The token is the secret: it is random, it is checked before a
/// stranger is allowed to become the peer, and it stops being worth anything
/// once <see cref="LinkOptions.PairingWindow"/> has passed or the host has
/// paired. That is what keeps whoever can watch the relay from pairing with
/// an unclaimed host first.
/// </para>
/// </remarks>
public readonly record struct InvitationCode
{
    // Outside the base64url alphabet a ConnBlob is written in, so the two
    // halves can never run into each other however long either one is.
    private const char TokenSeparator = '.';

    private InvitationCode(ConnBlob address, string pairingToken)
    {
        Address = address;
        PairingToken = pairingToken;
    }

    /// <summary>The address the code points at.</summary>
    public ConnBlob Address { get; }

    /// <summary>The secret that buys one pairing with that address.</summary>
    public string PairingToken { get; }

    /// <summary>The code's text, as published.</summary>
    public string Value => IsEmpty ? "" : Address.Value + TokenSeparator + PairingToken;

    /// <summary>Whether this is the empty code.</summary>
    public bool IsEmpty => Address.IsEmpty;

    /// <summary>Puts together the code a host publishes.</summary>
    /// <param name="address">Where the host listens.</param>
    /// <param name="pairingToken">The secret the host will demand when it is joined.</param>
    /// <exception cref="ArgumentException">If either half is missing.</exception>
    public static InvitationCode ForAddress(ConnBlob address, string pairingToken)
    {
        if (address.IsEmpty)
        {
            throw new ArgumentException("an invitation code needs an address", nameof(address));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingToken);
        if (pairingToken.Contains(TokenSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"a pairing token may not contain '{TokenSeparator}'", nameof(pairingToken));
        }
        return new InvitationCode(address, pairingToken);
    }

    /// <summary>
    /// Reads a code the way a person will hand it over: possibly with spaces
    /// or a line break around it, possibly pasted out of a chat window.
    /// </summary>
    /// <exception cref="LinkException">If it is not a code at all.</exception>
    public static InvitationCode Parse(string text)
    {
        if (!TryParse(text, out InvitationCode code))
        {
            throw new LinkException(
                "that is not an invitation code; it should start with \"tc\" and carry a pairing token after a \".\"");
        }
        return code;
    }

    /// <summary>Reads a code, returning false rather than throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out InvitationCode code)
    {
        code = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Whitespace is what a copy-paste picks up, and a code is one token,
        // so removing all of it is safer than trimming the ends.
        string cleaned = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        int separator = cleaned.IndexOf(TokenSeparator, StringComparison.Ordinal);
        if (separator <= 0 || separator == cleaned.Length - 1)
        {
            return false;
        }

        ConnBlob address = new(cleaned[..separator]);
        if (!address.TryParse(out ConnInfo? info) || info.ServerPublic.IsZero)
        {
            return false;
        }

        code = new InvitationCode(address, cleaned[(separator + 1)..]);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => Value;
}
