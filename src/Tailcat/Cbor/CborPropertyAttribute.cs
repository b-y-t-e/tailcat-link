// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Cbor;

/// <summary>
/// Marks a property of a wire type as a CBOR map entry, standing in for
/// Go's <c>cbor:"n,omitempty"</c> struct tag.
/// </summary>
/// <remarks>
/// <see cref="Order"/> replaces the implicit field order of a Go struct:
/// fxamacker/cbor encodes fields in declaration order by default, and the
/// ConnBlob wire format depends on it, so the order is spelled out here.
/// </remarks>
/// <param name="name">The short CBOR field name. This is wire format: never change one.</param>
/// <param name="order">The position of the field in the encoded map, matching Go's declaration order.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CborPropertyAttribute(string name, int order) : Attribute
{
    /// <summary>The short CBOR field name, such as "p".</summary>
    public string Name { get; } = name;

    /// <summary>The field's position in the encoded map, ascending.</summary>
    public int Order { get; } = order;

    /// <summary>Whether the field is omitted when it holds its zero value, as Go's "omitempty" does.</summary>
    public bool OmitEmpty { get; init; }
}
