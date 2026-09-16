// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Tailcat.Keys;

/// <summary>
/// An optional 256-bit WireGuard pre-shared key, as carried in a tailcat
/// address. Port of Go's <c>tailcat.PresharedKey</c>.
/// </summary>
/// <remarks>
/// Go mixes it into every WireGuard handshake, so that a relay operator who
/// sees a server's node public key still cannot join the tunnel. This port
/// meets peers over QUIC and never uses it; it carries the key so that an
/// address written by Go survives parsing and re-encoding here intact.
/// </remarks>
public readonly struct PresharedKey : IEquatable<PresharedKey>
{
    /// <summary>Length in bytes of a raw pre-shared key.</summary>
    public const int RawLen = 32;

    private const string TextPrefix = "psk:";

    private readonly byte[]? _raw;

    private PresharedKey(byte[] raw) => _raw = raw;

    /// <summary>Returns a new, cryptographically random, non-zero key.</summary>
    public static PresharedKey New()
    {
        PresharedKey k;
        do
        {
            k = new PresharedKey(RandomNumberGenerator.GetBytes(RawLen));
        }
        while (k.IsZero);
        return k;
    }

    /// <summary>Builds a key from its 32 raw bytes, which are copied.</summary>
    /// <exception cref="ArgumentException">If <paramref name="raw"/> isn't 32 bytes long.</exception>
    public static PresharedKey FromRaw32(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != RawLen)
        {
            throw new ArgumentException($"invalid WireGuard pre-shared key length {raw.Length}, want {RawLen}", nameof(raw));
        }
        return new PresharedKey(raw.ToArray());
    }

    /// <summary>Reports whether this is the zero key, which means none.</summary>
    public bool IsZero => _raw is null || CryptographicOperations.FixedTimeEquals(_raw, new byte[RawLen]);

    /// <summary>Returns a copy of the key's 32 raw bytes.</summary>
    public byte[] Raw32() => _raw is null ? new byte[RawLen] : (byte[])_raw.Clone();

    // Constant time, as in Go: the key is a secret.
    public bool Equals(PresharedKey other) => CryptographicOperations.FixedTimeEquals(Raw32(), other.Raw32());

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is PresharedKey other && Equals(other);

    public override int GetHashCode()
    {
        HashCode h = new();
        h.AddBytes(Raw32());
        return h.ToHashCode();
    }

    public static bool operator ==(PresharedKey a, PresharedKey b) => a.Equals(b);

    public static bool operator !=(PresharedKey a, PresharedKey b) => !a.Equals(b);

    /// <summary>Returns the key as "psk:" followed by lowercase hex, as Go's text form does.</summary>
    public override string ToString() => TextPrefix + Convert.ToHexStringLower(Raw32());
}
