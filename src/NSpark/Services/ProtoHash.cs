using System.Buffers.Binary;
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using NSpark.Exceptions;

namespace NSpark.Services;

/// <summary>
/// The operators' deterministic hash of a protobuf message (<c>spark/common/protohash</c>, an
/// object hash over field numbers), which V3 token transactions are signed and identified by.
/// </summary>
/// <remarks>
/// <para>
/// A message hashes as <c>SHA256("d" ‖ key ‖ value ‖ …)</c> over its fields in field-number
/// order, each key being <c>SHA256("i" ‖ field number as big-endian u64)</c>. Values hash by
/// type: integers and enums <c>SHA256("i" ‖ big-endian u64)</c>, <c>true</c>
/// <c>SHA256("b" ‖ "1")</c>, strings <c>SHA256("u" ‖ UTF-8)</c>, bytes <c>SHA256("r" ‖ bytes)</c>,
/// doubles <c>SHA256("f" ‖ IEEE-754 bits)</c>, repeated fields <c>SHA256("l" ‖ element hashes)</c>
/// in order, nested messages recursively, and a Timestamp or Duration as the list of its seconds
/// and nanos. A scalar field holding its default (0, false, "", no bytes) or an empty repeated
/// field is left out, even when it was set explicitly; a message field that is set is always
/// hashed, even when empty.
/// </para>
/// <para>
/// Only what Spark's hashed messages use is supported: maps, groups and well-known types other
/// than Timestamp and Duration throw rather than risk a hash the operators would not compute.
/// </para>
/// </remarks>
internal static class ProtoHash
{
    private const string Operation = "protohash";

    public static byte[] Hash(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return HashMessage(message);
    }

    private static byte[] HashMessage(IMessage message)
    {
        switch (message)
        {
            case Timestamp timestamp:
                return List([Int(timestamp.Seconds), Int(timestamp.Nanos)]);
            case Duration duration:
                return List([Int(duration.Seconds), Int(duration.Nanos)]);
        }

        var descriptor = message.Descriptor;
        if (descriptor.FullName.StartsWith("google.protobuf.", StringComparison.Ordinal))
        {
            throw new SparkConfigurationException(Operation, $"protohash: {descriptor.FullName} is not supported.");
        }

        using var data = new MemoryStream();
        data.WriteByte((byte)'d');
        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            var hash = HashField(message, field);
            if (hash is null)
            {
                continue;
            }

            data.Write(Uint((ulong)field.FieldNumber));
            data.Write(hash);
        }

        return SHA256.HashData(data.ToArray());
    }

    /// <summary>The hash of one field's value, or <c>null</c> when protohash leaves the field out.</summary>
    private static byte[]? HashField(IMessage message, FieldDescriptor field)
    {
        if (field.IsMap)
        {
            throw new SparkConfigurationException(Operation, $"protohash: map field {field.FieldNumber} is not supported.");
        }

        if (field.FieldType == FieldType.Group)
        {
            throw new SparkConfigurationException(Operation, $"protohash: group field {field.FieldNumber} is not supported.");
        }

        var accessor = field.Accessor;
        if (field.IsRepeated)
        {
            var list = (IList)accessor.GetValue(message);
            if (list.Count == 0)
            {
                return null;
            }

            var elements = new List<byte[]>(list.Count);
            foreach (var element in list)
            {
                elements.Add(field.FieldType == FieldType.Message
                    ? HashMessage((IMessage)element)
                    : HashRepeatedScalar(field, element));
            }

            return List(elements);
        }

        if (field.FieldType == FieldType.Message)
        {
            // A set message is hashed even when it is empty.
            return accessor.HasValue(message) ? HashMessage((IMessage)accessor.GetValue(message)) : null;
        }

        // A scalar left at its default is not hashed, even when set explicitly (or the active
        // case of a oneof).
        var value = accessor.GetValue(message);
        return HashSingularScalar(field, value);
    }

    private static byte[]? HashSingularScalar(FieldDescriptor field, object? value)
    {
        switch (field.FieldType)
        {
            case FieldType.Double:
            {
                var d = (double)value!;
                return d != 0 ? Double(d) : null;
            }
            case FieldType.Float:
            {
                var f = (float)value!;
                return f != 0 ? Double(f) : null;
            }
            case FieldType.Int64:
            case FieldType.SInt64:
            case FieldType.SFixed64:
            {
                var v = (long)value!;
                return v != 0 ? Int(v) : null;
            }
            case FieldType.Int32:
            case FieldType.SInt32:
            case FieldType.SFixed32:
            {
                var v = (int)value!;
                return v != 0 ? Int(v) : null;
            }
            case FieldType.UInt64:
            case FieldType.Fixed64:
            {
                var v = (ulong)value!;
                return v != 0 ? Uint(v) : null;
            }
            case FieldType.UInt32:
            case FieldType.Fixed32:
            {
                var v = (uint)value!;
                return v != 0 ? Uint(v) : null;
            }
            case FieldType.Bool:
                return (bool)value! ? Tagged("b", "1"u8) : null;
            case FieldType.String:
            {
                var s = (string?)value;
                return string.IsNullOrEmpty(s) ? null : Tagged("u", Encoding.UTF8.GetBytes(s));
            }
            case FieldType.Bytes:
            {
                var b = (ByteString?)value;
                return b is null || b.IsEmpty ? null : Tagged("r", b.Span);
            }
            case FieldType.Enum:
            {
                var v = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                return v != 0 ? Int(v) : null;
            }
            default:
                throw new SparkConfigurationException(Operation, $"protohash: field type {field.FieldType} is not supported.");
        }
    }

    /// <summary>A repeated field's element: every element is hashed, defaults included.</summary>
    private static byte[] HashRepeatedScalar(FieldDescriptor field, object value) => field.FieldType switch
    {
        FieldType.Double => Double((double)value),
        FieldType.Float => Double((float)value),
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => Int((long)value),
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => Int((int)value),
        FieldType.UInt64 or FieldType.Fixed64 => Uint((ulong)value),
        FieldType.UInt32 or FieldType.Fixed32 => Uint((uint)value),
        FieldType.Bool => Tagged("b", (bool)value ? "1"u8 : "0"u8),
        FieldType.String => Tagged("u", Encoding.UTF8.GetBytes((string)value)),
        FieldType.Bytes => Tagged("r", ((ByteString)value).Span),
        FieldType.Enum => Int(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
        _ => throw new SparkConfigurationException(Operation, $"protohash: field type {field.FieldType} is not supported."),
    };

    internal static byte[] Tagged(string tag, ReadOnlySpan<byte> bytes)
    {
        var data = new byte[tag.Length + bytes.Length];
        Encoding.ASCII.GetBytes(tag, data);
        bytes.CopyTo(data.AsSpan(tag.Length));
        return SHA256.HashData(data);
    }

    internal static byte[] Int(long value) => Uint(unchecked((ulong)value));

    internal static byte[] Uint(ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return Tagged("i", bytes);
    }

    internal static byte[] Double(double value)
    {
        // -0.0 as 0.0, and every NaN as Go's math.NaN().
        var bits = double.IsNaN(value)
            ? 0x7FF8_0000_0000_0001UL
            : value == 0 ? 0UL : unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, bits);
        return Tagged("f", bytes);
    }

    internal static byte[] List(IReadOnlyList<byte[]> elements)
    {
        using var data = new MemoryStream();
        data.WriteByte((byte)'l');
        foreach (var element in elements)
        {
            data.Write(element);
        }

        return SHA256.HashData(data.ToArray());
    }
}
