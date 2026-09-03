// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

using System.Collections;
using System.Formats.Cbor;
using System.Reflection;
using Tailcat.Keys;

namespace Tailcat.Cbor;

/// <summary>
/// Encodes and decodes the tailcat wire types as CBOR maps, replacing Go's
/// fxamacker/cbor struct-tag reflection.
/// </summary>
/// <remarks>
/// It supports exactly what the wire types use: <see cref="int"/>,
/// <see cref="string"/>, <see cref="bool"/>, <see cref="NodePublic"/> and
/// <see cref="DiscoPublic"/> (as byte strings, matching Go's
/// BinaryMarshaler), and lists of other wire types. Fields are written in <see cref="CborPropertyAttribute.Order"/>
/// order, which is the wire format; unknown map keys are skipped on decode,
/// as Go's decoder does.
/// </remarks>
public static class CborMapper
{
    /// <summary>Encodes <paramref name="value"/> as a CBOR map.</summary>
    public static byte[] Serialize<T>(T value) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(value);
        CborWriter writer = new(CborConformanceMode.Lax);
        WriteObject(writer, value);
        return writer.Encode();
    }

    /// <summary>Decodes a CBOR map produced by <see cref="Serialize{T}"/>.</summary>
    /// <exception cref="CborContentException">If the data isn't well-formed CBOR.</exception>
    public static T Deserialize<T>(ReadOnlyMemory<byte> data) where T : new()
    {
        CborReader reader = new(data, CborConformanceMode.Lax);
        T value = (T)ReadObject(reader, typeof(T));
        if (reader.BytesRemaining != 0)
        {
            throw new CborContentException($"{reader.BytesRemaining} trailing bytes after CBOR value");
        }
        return value;
    }

    /// <summary>
    /// Returns the CBOR-mapped properties of <paramref name="type"/> in wire order.
    /// </summary>
    public static IReadOnlyList<CborField> FieldsOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, Attribute: p.GetCustomAttribute<CborPropertyAttribute>()))
            .Where(x => x.Attribute is not null)
            .OrderBy(x => x.Attribute!.Order)
            .Select(x => new CborField(x.Property, x.Attribute!))];
    }

    private static void WriteObject(CborWriter writer, object value)
    {
        List<(CborField Field, object? Value)> present = [];
        foreach (CborField f in FieldsOf(value.GetType()))
        {
            object? v = f.Property.GetValue(value);
            if (f.Attribute.OmitEmpty && IsEmpty(v))
            {
                continue;
            }
            present.Add((f, v));
        }

        writer.WriteStartMap(present.Count);
        foreach ((CborField f, object? v) in present)
        {
            writer.WriteTextString(f.Attribute.Name);
            WriteValue(writer, v);
        }
        writer.WriteEndMap();
    }

    private static void WriteValue(CborWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull();
                break;
            case int i:
                writer.WriteInt32(i);
                break;
            case string s:
                writer.WriteTextString(s);
                break;
            case bool b:
                writer.WriteBoolean(b);
                break;
            case NodePublic k:
                writer.WriteByteString(k.Raw32());
                break;
            case DiscoPublic k:
                writer.WriteByteString(k.Raw32());
                break;
            case IList list:
                writer.WriteStartArray(list.Count);
                foreach (object? item in list)
                {
                    if (item is null)
                    {
                        writer.WriteNull();
                    }
                    else
                    {
                        WriteObject(writer, item);
                    }
                }
                writer.WriteEndArray();
                break;
            default:
                throw new NotSupportedException($"CBOR encoding of {value.GetType()} isn't supported");
        }
    }

    private static object ReadObject(CborReader reader, Type type)
    {
        object value = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"can't instantiate {type}");
        Dictionary<string, CborField> byName = FieldsOf(type).ToDictionary(f => f.Attribute.Name, StringComparer.Ordinal);

        int? count = reader.ReadStartMap();
        for (int i = 0; count is null ? reader.PeekState() != CborReaderState.EndMap : i < count; i++)
        {
            if (reader.PeekState() != CborReaderState.TextString)
            {
                // A key of some other type can't name one of our fields.
                reader.SkipValue();
                reader.SkipValue();
                continue;
            }
            string name = reader.ReadTextString();
            if (!byName.TryGetValue(name, out CborField? field))
            {
                reader.SkipValue();
                continue;
            }
            field.Property.SetValue(value, ReadValue(reader, field.Property.PropertyType));
        }
        reader.ReadEndMap();
        return value;
    }

    // ReadInt32 reads an integer field, which the wire types all model as an
    // int. CBOR encodes an integer by value, so a blob can carry one wider
    // than that — Go's own wire types were widened to int64 when upstream
    // tailcfg changed region IDs — and CborReader.ReadInt32 signals that with
    // an OverflowException, which is not one of the malformed-input exceptions
    // callers expect. A value we can't represent is a malformed blob, so say
    // so in the vocabulary the decode already speaks.
    private static int ReadInt32(CborReader reader)
    {
        long value;
        try
        {
            value = reader.ReadInt64();
        }
        catch (OverflowException ex)
        {
            throw new CborContentException($"integer out of range: {ex.Message}", ex);
        }
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw new CborContentException($"integer {value} doesn't fit in an int");
        }
        return (int)value;
    }

    private static object? ReadValue(CborReader reader, Type type)
    {
        if (reader.PeekState() == CborReaderState.Null)
        {
            reader.ReadNull();
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
        if (type == typeof(int))
        {
            return ReadInt32(reader);
        }
        if (type == typeof(string))
        {
            return reader.ReadTextString();
        }
        if (type == typeof(bool))
        {
            return reader.ReadBoolean();
        }
        if (type == typeof(NodePublic))
        {
            return NodePublic.FromRaw32(reader.ReadByteString());
        }
        if (type == typeof(DiscoPublic))
        {
            return DiscoPublic.FromRaw32(reader.ReadByteString());
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            Type elem = type.GetGenericArguments()[0];
            IList list = (IList)Activator.CreateInstance(type)!;
            int? count = reader.ReadStartArray();
            for (int i = 0; count is null ? reader.PeekState() != CborReaderState.EndArray : i < count; i++)
            {
                if (reader.PeekState() == CborReaderState.Null)
                {
                    reader.ReadNull();
                    list.Add(null);
                    continue;
                }
                list.Add(ReadObject(reader, elem));
            }
            reader.ReadEndArray();
            return list;
        }
        throw new NotSupportedException($"CBOR decoding of {type} isn't supported");
    }

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        int i => i == 0,
        string s => s.Length == 0,
        bool b => !b,
        IList list => list.Count == 0,
        NodePublic k => k.IsZero,
        DiscoPublic k => k.IsZero,
        _ => false,
    };
}

/// <summary>A CBOR-mapped property of a wire type.</summary>
/// <param name="Property">The reflected property.</param>
/// <param name="Attribute">Its <see cref="CborPropertyAttribute"/>.</param>
public sealed record CborField(PropertyInfo Property, CborPropertyAttribute Attribute);
