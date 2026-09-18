using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BondTools.Models;

/// <summary>A model with generated compact-summary support.</summary>
public interface IGeneratedSummary
{
    /// <summary>The schema type's display name.</summary>
    string SummaryName { get; }

    /// <summary>Captures immediate fields, including inherited fields, without materializing bonded values.</summary>
    ModelDebugField[] GetSummaryFields();
}

/// <summary>Bounded, cycle-safe summaries without serialization or member discovery.</summary>
public static class ModelSummary
{
    /// <summary>Formats at most four levels, 16 items per container, 160 characters per string, and 2048 characters total.</summary>
    public static string Format(object? value) => new Writer().Render(value);

    /// <summary>Marks a value as opaque; only its CLR type name is captured.</summary>
    public static object? Opaque(object? value) => value is null ? null : new OpaqueValue(value.GetType().Name);

    private sealed class OpaqueValue(string name)
    {
        internal string Name { get; } = name;
    }

    private sealed class Writer
    {
        private const int MaxDepth = 4;
        private const int MaxItems = 16;
        private const int MaxStringLength = 160;
        private const int MaxLength = 2048;
        private readonly StringBuilder _text = new();
        private readonly HashSet<object> _path = new(ReferenceEqualityComparer.Instance);

        private bool Full => _text.Length >= MaxLength;

        internal string Render(object? value)
        {
            Write(value, 0);
            if (Full)
            {
                _text.Length = MaxLength - 3;
                _text.Append("...");
            }
            return _text.ToString();
        }

        private void Append(string value)
        {
            var count = Math.Min(value.Length, MaxLength - _text.Length);
            _text.Append(value, 0, count);
        }

        private void Append(char value)
        {
            if (!Full) _text.Append(value);
        }

        private void Write(object? value, int depth)
        {
            if (Full) return;
            switch (value)
            {
                case null: Append("null"); return;
                case string item: Quoted(item, '"'); return;
                case char item:
                    Append('\'');
                    Escaped(item, '\'');
                    Append('\'');
                    return;
                case bool item: Append(item ? "true" : "false"); return;
                case sbyte or byte or short or ushort or int or uint or long or ulong
                    or Int128 or UInt128 or IntPtr or UIntPtr or decimal:
                    Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                    return;
                case float item: Append(item.ToString("R", CultureInfo.InvariantCulture)); return;
                case double item: Append(item.ToString("R", CultureInfo.InvariantCulture)); return;
                case Half item: Append(item.ToString("R", CultureInfo.InvariantCulture)); return;
                case Enum item: Append(item.ToString("G")); return;
                case Guid item: Append(item.ToString("D")); return;
                case DateTime item: Append(item.ToString("O", CultureInfo.InvariantCulture)); return;
                case DateTimeOffset item: Append(item.ToString("O", CultureInfo.InvariantCulture)); return;
                case TimeSpan item: Append(item.ToString("c", CultureInfo.InvariantCulture)); return;
                case DateOnly item: Append(item.ToString("O", CultureInfo.InvariantCulture)); return;
                case TimeOnly item: Append(item.ToString("O", CultureInfo.InvariantCulture)); return;
                case Uri item: Quoted(item.OriginalString, '"'); return;
                case Version item: Append(item.ToString()); return;
                case byte[] item: Blob(item); return;
                case ArraySegment<byte> item: Blob(item.AsSpan()); return;
                case Memory<byte> item: Blob(item.Span); return;
                case ReadOnlyMemory<byte> item: Blob(item.Span); return;
                case OpaqueValue item: TypeName(item.Name); return;
            }

            if (value is not IGeneratedSummary && value is not IEnumerable)
            {
                TypeName(value.GetType().Name);
                return;
            }
            if (!_path.Add(value))
            {
                Append("<cycle>");
                return;
            }
            try
            {
                if (depth >= MaxDepth)
                {
                    Append("...");
                    return;
                }
                switch (value)
                {
                    case IGeneratedSummary model: Model(model, depth); break;
                    case IDictionary dictionary: Sequence(dictionary, depth, map: true); break;
                    case IEnumerable sequence: Sequence(sequence, depth, map: false); break;
                }
            }
            finally
            {
                _path.Remove(value);
            }
        }

        private void TypeName(string name)
        {
            Append('<');
            var arity = name.IndexOf('`');
            Append(arity < 0 ? name : name[..arity]);
            Append('>');
        }

        private void Model(IGeneratedSummary model, int depth)
        {
            Append(model.SummaryName);
            Append(" {");
            if (Full) return;
            var fields = model.GetSummaryFields();
            var count = Math.Min(fields.Length, MaxItems);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
                if (!names.Add(fields[i].Name)) duplicates.Add(fields[i].Name);

            for (var i = 0; i < count && !Full; i++)
            {
                var field = fields[i];
                Append(i == 0 ? " " : ", ");
                if (duplicates.Contains(field.Name))
                {
                    Append(field.DeclaringType);
                    Append('.');
                }
                Append(field.Name);
                Append(" = ");
                Write(field.Value, depth + 1);
            }
            if (fields.Length > count) Append(", ...");
            Append(" }");
        }

        private void Sequence(IEnumerable sequence, int depth, bool map)
        {
            Append(map ? "{" : "[");
            if (Full) return;
            var iterator = map ? ((IDictionary)sequence).GetEnumerator() : sequence.GetEnumerator();
            var count = 0;
            try
            {
                while (count < MaxItems && !Full && iterator.MoveNext())
                {
                    Append(count == 0 ? map ? " " : "" : ", ");
                    if (Full) break;
                    if (map)
                    {
                        var entry = ((IDictionaryEnumerator)iterator).Entry;
                        Write(entry.Key, depth + 1);
                        Append(" = ");
                        Write(entry.Value, depth + 1);
                    }
                    else
                        Write(iterator.Current, depth + 1);
                    count++;
                }
                if (!Full && count == MaxItems && (sequence is not ICollection collection || collection.Count > count))
                    Append(", ...");
            }
            finally
            {
                (iterator as IDisposable)?.Dispose();
            }
            Append(map ? " }" : "]");
        }

        private void Blob(ReadOnlySpan<byte> bytes)
        {
            Append("blob [");
            var count = Math.Min(bytes.Length, MaxItems);
            for (var i = 0; i < count && !Full; i++)
            {
                if (i != 0) Append(", ");
                Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            if (bytes.Length > count) Append(", ...");
            Append(']');
        }

        private void Quoted(string value, char quote)
        {
            Append(quote);
            var count = Math.Min(value.Length, MaxStringLength);
            for (var i = 0; i < count && !Full; i++)
                Escaped(value[i], quote);
            if (value.Length > count) Append("...");
            Append(quote);
        }

        private void Escaped(char value, char quote)
        {
            if (value == quote)
            {
                Append('\\');
                Append(value);
                return;
            }
            switch (value)
            {
                case '\\': Append(@"\\"); break;
                case '\0': Append(@"\0"); break;
                case '\a': Append(@"\a"); break;
                case '\b': Append(@"\b"); break;
                case '\f': Append(@"\f"); break;
                case '\n': Append(@"\n"); break;
                case '\r': Append(@"\r"); break;
                case '\t': Append(@"\t"); break;
                case '\v': Append(@"\v"); break;
                default:
                    if (char.IsControl(value) || char.IsSurrogate(value) || value is '\u2028' or '\u2029')
                    {
                        Append(@"\u");
                        Append(((ushort)value).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                        Append(value);
                    break;
            }
        }
    }
}
