using System.Collections;
using System.Reflection;
using System.Text;

// Fills a generated object graph with random values, putting nulls only where the schema allows them (upstream's
// [Bond.Type] attributes say which, e.g. List<Bond.Tag.nullable<Money>> for vector<nullable<Money>>).
public sealed class Filler(Random random, int maxItems)
{
    public T Create<T>() => (T)Value(typeof(T), typeof(T), 0);

    private object Value(Type clr, Type schema, int depth)
    {
        if (schema.IsGenericType && schema.GetGenericTypeDefinition() == typeof(Bond.Tag.nullable<>))
        {
            return random.Next(3) == 0 ? null : Value(Nullable.GetUnderlyingType(clr) ?? clr, schema.GetGenericArguments()[0], depth);
        }

        if (Nullable.GetUnderlyingType(clr) is { } underlying)
        {
            return random.Next(3) == 0 ? null : Value(underlying, underlying, depth);
        }

        if (clr == typeof(string)) return Text();
        if (clr == typeof(bool)) return random.Next(2) == 0;
        if (clr.IsEnum) return Enum.GetValues(clr).GetValue(random.Next(Enum.GetValues(clr).Length));
        if (clr == typeof(sbyte)) return (sbyte)random.Next(sbyte.MinValue, sbyte.MaxValue);
        if (clr == typeof(short)) return (short)random.Next(short.MinValue, short.MaxValue);
        if (clr == typeof(int)) return random.Next(-1_000_000, 1_000_000);
        if (clr == typeof(long)) return random.NextInt64(-1_000_000_000, 1_000_000_000);
        if (clr == typeof(byte)) return (byte)random.Next(256);
        if (clr == typeof(ushort)) return (ushort)random.Next(ushort.MaxValue);
        if (clr == typeof(uint)) return (uint)random.Next();
        if (clr == typeof(ulong)) return (ulong)random.NextInt64();
        if (clr == typeof(float)) return (float)(random.NextDouble() * 1000 - 500);
        if (clr == typeof(double)) return random.NextDouble() * 1e6 - 5e5;
        if (clr == typeof(ArraySegment<byte>))
        {
            var bytes = new byte[random.Next(64)];
            random.NextBytes(bytes);
            return new ArraySegment<byte>(bytes);
        }

        var args = clr.IsGenericType ? clr.GetGenericArguments() : [];
        var schemaArgs = schema.IsGenericType && schema != clr ? schema.GetGenericArguments() : args;
        var definition = clr.IsGenericType ? clr.GetGenericTypeDefinition() : null;
        if (definition == typeof(List<>) || definition == typeof(LinkedList<>) || definition == typeof(HashSet<>))
        {
            var collection = Activator.CreateInstance(clr);
            var add = clr.GetMethod(definition == typeof(LinkedList<>) ? "AddLast" : "Add", [args[0]]);
            for (var i = Count(depth); i > 0; i--)
            {
                add.Invoke(collection, [Value(args[0], schemaArgs[0], depth + 1)]);
            }

            return collection;
        }

        if (definition == typeof(Dictionary<,>))
        {
            var map = (IDictionary)Activator.CreateInstance(clr);
            for (var i = Count(depth); i > 0; i--)
            {
                var key = Value(args[0], schemaArgs[0], depth + 1);
                if (!map.Contains(key))
                {
                    map[key] = Value(args[1], schemaArgs[1], depth + 1);
                }
            }

            return map;
        }

        var instance = Activator.CreateInstance(clr);
        foreach (var property in clr.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<Bond.IdAttribute>() != null)
            {
                var schemaType = property.GetCustomAttributesData()
                    .FirstOrDefault(data => data.AttributeType == typeof(Bond.TypeAttribute))?.ConstructorArguments[0].Value as Type;
                property.SetValue(instance, Value(property.PropertyType, schemaType ?? property.PropertyType, depth + 1));
            }
        }

        return instance;
    }

    private int Count(int depth) => random.Next(10) == 0 ? 0 : random.Next(1, Math.Max(2, maxItems - depth / 2) + 1);

    private string Text()
    {
        var builder = new StringBuilder();
        for (var length = random.Next(3, 24); builder.Length < length;)
        {
            builder.Append(random.Next(20) switch
            {
                0 => (char)random.Next(0x80, 0x800),
                1 => (char)random.Next(0x800, 0xD800),
                _ => (char)random.Next(0x20, 0x7F)
            });
        }

        return builder.ToString();
    }
}
