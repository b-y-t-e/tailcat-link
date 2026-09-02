// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Diagnostics.CodeAnalysis;

namespace Tailcat.Keys;

/// <summary>
/// A node's public key: the 32 raw bytes of a Curve25519 public key.
/// This is the port of Go's <c>key.NodePublic</c>, minus the parts
/// tailcat never uses (sealing, comparison ordering).
/// </summary>
/// <remarks>
/// The zero value is the "zero key", which <see cref="IsZero"/> reports and
/// which tailcat uses as "unset" (as Go does).
/// </remarks>
public readonly struct NodePublic : IEquatable<NodePublic>
{
    /// <summary>Length in bytes of a raw node public key.</summary>
    public const int RawLen = 32;

    private const string TextPrefix = "nodekey:";

    private readonly byte[]? _raw;

    private NodePublic(byte[] raw) => _raw = raw;

    /// <summary>Builds a key from its 32 raw bytes, which are copied.</summary>
    /// <exception cref="ArgumentException">If <paramref name="raw"/> isn't 32 bytes long.</exception>
    public static NodePublic FromRaw32(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != RawLen)
        {
            throw new ArgumentException($"NodePublic must be {RawLen} bytes, got {raw.Length}", nameof(raw));
        }
        return new NodePublic(raw.ToArray());
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

    public bool Equals(NodePublic other) => Raw32().AsSpan().SequenceEqual(other.Raw32());

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is NodePublic other && Equals(other);

    public override int GetHashCode()
    {
        HashCode h = new();
        h.AddBytes(Raw32());
        return h.ToHashCode();
    }

    public static bool operator ==(NodePublic a, NodePublic b) => a.Equals(b);

    public static bool operator !=(NodePublic a, NodePublic b) => !a.Equals(b);

    /// <summary>Returns the key as "nodekey:" followed by lowercase hex, as Go does.</summary>
    public override string ToString() => TextPrefix + Convert.ToHexStringLower(Raw32());
}
