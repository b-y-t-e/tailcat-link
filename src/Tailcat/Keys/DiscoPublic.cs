// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Diagnostics.CodeAnalysis;

namespace Tailcat.Keys;

/// <summary>
/// A node's disco public key: the 32 raw bytes of a Curve25519 public
/// key used for NAT-traversal (disco) messages. Port of Go's
/// <c>key.DiscoPublic</c>.
/// </summary>
public readonly struct DiscoPublic : IEquatable<DiscoPublic>
{
    /// <summary>Length in bytes of a raw disco public key.</summary>
    public const int RawLen = 32;

    private const string TextPrefix = "discokey:";

    private readonly byte[]? _raw;

    private DiscoPublic(byte[] raw) => _raw = raw;

    /// <summary>Builds a key from its 32 raw bytes, which are copied.</summary>
    /// <exception cref="ArgumentException">If <paramref name="raw"/> isn't 32 bytes long.</exception>
    public static DiscoPublic FromRaw32(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != RawLen)
        {
            throw new ArgumentException($"DiscoPublic must be {RawLen} bytes, got {raw.Length}", nameof(raw));
        }
        return new DiscoPublic(raw.ToArray());
    }

    /// <summary>Reports whether this is the zero key.</summary>
    public bool IsZero
    {
        get
        {
            if (_raw is null)
            {
                return true;
            }
            foreach (byte b in _raw)
            {
                if (b != 0)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>Returns a copy of the key's 32 raw bytes.</summary>
    public byte[] Raw32() => _raw is null ? new byte[RawLen] : (byte[])_raw.Clone();

    /// <summary>Appends the key's 32 raw bytes to <paramref name="buffer"/>.</summary>
    public void AppendTo(List<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_raw is null)
        {
            buffer.AddRange(new byte[RawLen]);
        }
        else
        {
            buffer.AddRange(_raw);
        }
    }

    public bool Equals(DiscoPublic other) => Raw32().AsSpan().SequenceEqual(other.Raw32());

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is DiscoPublic other && Equals(other);

    public override int GetHashCode()
    {
        HashCode h = new();
        h.AddBytes(Raw32());
        return h.ToHashCode();
    }

    public static bool operator ==(DiscoPublic a, DiscoPublic b) => a.Equals(b);

    public static bool operator !=(DiscoPublic a, DiscoPublic b) => !a.Equals(b);

    /// <summary>Returns the key as "discokey:" followed by lowercase hex, as Go does.</summary>
    public override string ToString() => TextPrefix + Convert.ToHexStringLower(Raw32());
}
