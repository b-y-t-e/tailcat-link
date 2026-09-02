// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Tailcat.Keys;

/// <summary>
/// A node's private key. Port of Go's <c>key.NodePrivate</c>, limited to
/// what tailcat needs: generating a key and deriving the public key. The
/// node's disco key is derived from it by <see cref="DiscoPrivate.ForNode"/>.
/// </summary>
public readonly struct NodePrivate : IEquatable<NodePrivate>
{
    /// <summary>Length in bytes of a raw node private key.</summary>
    public const int RawLen = 32;

    private readonly byte[]? _raw;

    private NodePrivate(byte[] raw) => _raw = raw;

    /// <summary>Generates a new random node private key.</summary>
    public static NodePrivate NewKey()
    {
        byte[] raw = new byte[RawLen];
        new SecureRandom().NextBytes(raw);
        Clamp(raw);
        return new NodePrivate(raw);
    }

    /// <summary>Builds a private key from its 32 raw bytes, which are copied and clamped.</summary>
    /// <exception cref="ArgumentException">If <paramref name="raw"/> isn't 32 bytes long.</exception>
    public static NodePrivate FromRaw32(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != RawLen)
        {
            throw new ArgumentException($"NodePrivate must be {RawLen} bytes, got {raw.Length}", nameof(raw));
        }
        byte[] copy = raw.ToArray();
        Clamp(copy);
        return new NodePrivate(copy);
    }

    /// <summary>Reports whether this is the zero (unset) key.</summary>
    public bool IsZero => _raw is null;

    /// <summary>Returns a copy of the key's 32 raw bytes.</summary>
    public byte[] Raw32() => _raw is null ? new byte[RawLen] : (byte[])_raw.Clone();

    /// <summary>Derives the corresponding public key.</summary>
    public NodePublic Public()
    {
        if (_raw is null)
        {
            throw new InvalidOperationException("can't take the public key of the zero NodePrivate");
        }
        X25519PrivateKeyParameters priv = new(_raw, 0);
        return NodePublic.FromRaw32(priv.GeneratePublicKey().GetEncoded());
    }

    // Clamp applies the Curve25519 clamping that Go's key package applies at
    // generation time, so that a key round-tripped through raw bytes derives
    // the same public key.
    private static void Clamp(byte[] raw)
    {
        raw[0] &= 248;
        raw[31] = (byte)((raw[31] & 127) | 64);
    }

    public bool Equals(NodePrivate other) => Raw32().AsSpan().SequenceEqual(other.Raw32());

    public override bool Equals(object? obj) => obj is NodePrivate other && Equals(other);

    public override int GetHashCode()
    {
        HashCode h = new();
        h.AddBytes(Raw32());
        return h.ToHashCode();
    }

    public static bool operator ==(NodePrivate a, NodePrivate b) => a.Equals(b);

    public static bool operator !=(NodePrivate a, NodePrivate b) => !a.Equals(b);

    /// <summary>Returns a redacted form; private key material is never printed.</summary>
    public override string ToString() => "NodePrivate(redacted)";
}
