// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Tailcat.Keys;

/// <summary>
/// A node's disco private key. Port of Go's <c>key.DiscoPrivate</c>,
/// limited to generating a key, deriving one from a node key, and deriving
/// its public half.
/// </summary>
public readonly struct DiscoPrivate : IEquatable<DiscoPrivate>
{
    /// <summary>Length in bytes of a raw disco private key.</summary>
    public const int RawLen = 32;

    // The domain separator Go hashes the node key under. It is part of the
    // derivation, so both implementations must spell it identically.
    private static ReadOnlySpan<byte> DerivationLabel => "github.com/tailscale/tailcat disco key v1"u8;

    private readonly byte[]? _raw;

    private DiscoPrivate(byte[] raw) => _raw = raw;

    /// <summary>Generates a new random disco private key.</summary>
    public static DiscoPrivate NewKey()
    {
        byte[] raw = new byte[RawLen];
        new SecureRandom().NextBytes(raw);
        Clamp(raw);
        return new DiscoPrivate(raw);
    }

    /// <summary>
    /// Deterministically derives the disco key belonging to a node key, so a
    /// node needs to persist only its node key. Port of Go's
    /// <c>discoPrivateForNode</c>.
    /// </summary>
    /// <remarks>
    /// The two keys are deliberately unlinkable: disco frames carry the disco
    /// public key in the clear on a direct path, while knowing the node public
    /// key is what grants access to a server. Tailcat originally reused the
    /// node key's bytes as the disco key, which leaked one from the other.
    /// </remarks>
    /// <exception cref="InvalidOperationException">If <paramref name="node"/> is the zero key.</exception>
    public static DiscoPrivate ForNode(NodePrivate node)
    {
        if (node.IsZero)
        {
            throw new InvalidOperationException("can't derive a disco key from the zero NodePrivate");
        }
        byte[] raw = HMACSHA256.HashData(node.Raw32(), DerivationLabel);
        return FromRaw32(raw);
    }

    /// <summary>Builds a private key from its 32 raw bytes, which are copied and clamped.</summary>
    /// <exception cref="ArgumentException">If <paramref name="raw"/> isn't 32 bytes long.</exception>
    public static DiscoPrivate FromRaw32(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != RawLen)
        {
            throw new ArgumentException($"DiscoPrivate must be {RawLen} bytes, got {raw.Length}", nameof(raw));
        }
        byte[] copy = raw.ToArray();
        Clamp(copy);
        return new DiscoPrivate(copy);
    }

    /// <summary>Reports whether this is the zero (unset) key.</summary>
    public bool IsZero => _raw is null;

    /// <summary>Returns a copy of the key's 32 raw bytes.</summary>
    public byte[] Raw32() => _raw is null ? new byte[RawLen] : (byte[])_raw.Clone();

    /// <summary>Derives the corresponding disco public key.</summary>
    public DiscoPublic Public()
    {
        if (_raw is null)
        {
            throw new InvalidOperationException("can't take the public key of the zero DiscoPrivate");
        }
        X25519PrivateKeyParameters priv = new(_raw, 0);
        return DiscoPublic.FromRaw32(priv.GeneratePublicKey().GetEncoded());
    }

    private static void Clamp(byte[] raw)
    {
        raw[0] &= 248;
        raw[31] = (byte)((raw[31] & 127) | 64);
    }

    public bool Equals(DiscoPrivate other) => Raw32().AsSpan().SequenceEqual(other.Raw32());

    public override bool Equals(object? obj) => obj is DiscoPrivate other && Equals(other);

    public override int GetHashCode()
    {
        HashCode h = new();
        h.AddBytes(Raw32());
        return h.ToHashCode();
    }

    public static bool operator ==(DiscoPrivate a, DiscoPrivate b) => a.Equals(b);

    public static bool operator !=(DiscoPrivate a, DiscoPrivate b) => !a.Equals(b);

    /// <summary>Returns a redacted form; private key material is never printed.</summary>
    public override string ToString() => "DiscoPrivate(redacted)";
}
