using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Bond.Parser.Tests.Fuzzing;

/// <summary>
/// Fills generated objects with random values, biased towards the edges of the wire format: defaults, varint
/// boundaries, -0.0 and NaN, empty, long and invalid text, undefined enum values, blobs in the middle of an array.
/// Nulls only go where the schema allows them: nullable types, which upstream's [Bond.Type] attributes mark, and
/// defaults of nothing, which the constructor leaves null.
/// </summary>
internal sealed class RandomValues(Random random)
{
    private static readonly int[] Powers = [0, 1, 6, 7, 8, 13, 14, 15, 16, 20, 21, 27, 28, 31, 32, 34, 35, 48, 49, 55, 56, 62, 63];

    public T Create<T>() => (T)Struct(typeof(T), depth: 0);

    private object Struct(Type type, int depth)
    {
        var instance = Activator.CreateInstance(type)!;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // A quarter of the fields keep their default, which optional fields don't write.
            if (property.GetCustomAttribute<global::Bond.IdAttribute>() == null || random.Next(4) == 0)
            {
                continue;
            }

            var schema = property.GetCustomAttributesData()
                .FirstOrDefault(data => data.AttributeType == typeof(global::Bond.TypeAttribute))?
                .ConstructorArguments[0].Value as Type ?? property.PropertyType;
            var maybe = !property.PropertyType.IsValueType && property.GetValue(instance) == null;
            property.SetValue(instance, maybe && random.Next(3) == 0 ? null : Value(property.PropertyType, schema, depth + 1));
        }

        return instance;
    }

    private object? Value(Type clr, Type schema, int depth, bool key = false)
    {
        if (schema.IsGenericType && schema.GetGenericTypeDefinition() == typeof(global::Bond.Tag.nullable<>))
        {
            if (random.Next(3) == 0)
            {
                // A null nullable<blob> is a default segment, as a dictionary of them can't hold null.
                return clr.IsValueType && Nullable.GetUnderlyingType(clr) == null ? Activator.CreateInstance(clr) : null;
            }

            schema = schema.GetGenericArguments()[0];
        }

        if (Nullable.GetUnderlyingType(clr) is { } underlying)
        {
            if (random.Next(3) == 0)
            {
                return null;
            }

            clr = underlying;
        }

        if (clr == typeof(string)) return Text(valid: key);
        if (clr == typeof(bool)) return random.Next(2) == 0;
        if (clr.IsEnum) return Enum(clr);
        if (clr == typeof(sbyte)) return unchecked((sbyte)Signed());
        if (clr == typeof(short)) return unchecked((short)Signed());
        if (clr == typeof(int)) return unchecked((int)Signed());
        if (clr == typeof(long)) return Signed();
        if (clr == typeof(byte)) return unchecked((byte)Unsigned());
        if (clr == typeof(ushort)) return unchecked((ushort)Unsigned());
        if (clr == typeof(uint)) return unchecked((uint)Unsigned());
        if (clr == typeof(ulong)) return Unsigned();
        if (clr == typeof(float)) return Float();
        if (clr == typeof(double)) return Double();
        if (clr == typeof(ArraySegment<byte>)) return Blob();

        var definition = clr.IsGenericType ? clr.GetGenericTypeDefinition() : null;
        var args = clr.IsGenericType ? clr.GetGenericArguments() : [];
        var schemaArgs = schema.IsGenericType && schema.GetGenericArguments().Length == args.Length ? schema.GetGenericArguments() : args;
        if (definition == typeof(global::Bond.IBonded<>))
        {
            return Activator.CreateInstance(typeof(global::Bond.Bonded<>).MakeGenericType(args), Struct(args[0], depth));
        }

        if (definition == typeof(List<>) || definition == typeof(LinkedList<>) || definition == typeof(HashSet<>))
        {
            var collection = Activator.CreateInstance(clr)!;
            var add = clr.GetMethod(definition == typeof(LinkedList<>) ? "AddLast" : "Add", [args[0]])!;
            for (var i = Count(args[0], depth); i > 0; i--)
            {
                add.Invoke(collection, [Value(args[0], schemaArgs[0], depth + 1, key: definition == typeof(HashSet<>))]);
            }

            return collection;
        }

        if (definition == typeof(Dictionary<,>))
        {
            var map = (IDictionary)Activator.CreateInstance(clr)!;
            for (var i = Count(args[1], depth); i > 0; i--)
            {
                var entry = Value(args[0], schemaArgs[0], depth + 1, key: true)!;
                if (!map.Contains(entry))
                {
                    map[entry] = Value(args[1], schemaArgs[1], depth + 1);
                }
            }

            return map;
        }

        return Struct(clr, depth);
    }

    // Mostly a few elements; sometimes none, or many numbers, which vector reads and writes treat in bulk.
    private int Count(Type element, int depth) => random.Next(12) switch
    {
        0 => 0,
        1 when depth < 4 && (element.IsPrimitive || element.IsEnum) => random.Next(20, 300),
        _ => random.Next(1, depth < 6 ? 4 : 2)
    };

    // A power of two, or one either side of it, where varints change length; or random bits of a random length.
    private ulong Unsigned()
    {
        if (random.Next(3) == 0)
        {
            var power = Powers[random.Next(Powers.Length)];
            return (1UL << power) + (ulong)random.Next(-1, 2);
        }

        Span<byte> bytes = stackalloc byte[8];
        random.NextBytes(bytes);
        return BitConverter.ToUInt64(bytes) >> random.Next(64);
    }

    private long Signed()
    {
        var value = (long)Unsigned();
        return random.Next(2) == 0 ? value : -value;
    }

    private double Double() => random.Next(6) switch
    {
        0 => Pick([0.0, -0.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, double.MaxValue, 1.5, 0.1]),
        1 => random.Next(-100, 100),
        2 => BitConverter.Int64BitsToDouble((long)Unsigned()),
        _ => (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-10, 20))
    };

    private float Float() => random.Next(6) switch
    {
        0 => Pick([0f, -0f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.Epsilon, float.MaxValue, 1.5f, 0.1f]),
        1 => random.Next(-100, 100),
        2 => BitConverter.Int32BitsToSingle((int)Unsigned()),
        _ => (float)((random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-10, 20)))
    };

    private object Enum(Type type)
    {
        var values = System.Enum.GetValues(type);
        return random.Next(6) == 0
            ? System.Enum.ToObject(type, unchecked((int)Signed()))
            : values.GetValue(random.Next(values.Length))!;
    }

    // Unpaired surrogates are written as U+FFFD, so only keys and set elements, which they could make equal, avoid them.
    private string Text(bool valid)
    {
        var builder = new StringBuilder();
        var length = random.Next(10) switch
        {
            0 => 0,
            1 => random.Next(100, 400),
            _ => random.Next(1, 16)
        };
        while (builder.Length < length)
        {
            builder.Append(random.Next(30) switch
            {
                0 => (char)random.Next(0x80, 0x800),
                1 => (char)random.Next(0x800, 0xD800),
                2 => "😀",
                3 when !valid => (char)random.Next(0xD800, 0xE000),
                4 => (char)random.Next(0xE000, 0x10000),
                _ => (char)random.Next(0x20, 0x7F)
            });
        }

        return builder.ToString();
    }

    // Sometimes 16 KB or more, which reads copy differently, and sometimes in the middle of a larger array.
    private ArraySegment<byte> Blob()
    {
        var length = random.Next(50) == 0 ? random.Next(16 * 1024, 20 * 1024) : random.Next(40);
        var offset = random.Next(3) == 0 ? random.Next(1, 5) : 0;
        var array = new byte[offset + length + random.Next(3)];
        random.NextBytes(array);
        return new ArraySegment<byte>(array, offset, length);
    }

    private T Pick<T>(IReadOnlyList<T> items) => items[random.Next(items.Count)];
}
