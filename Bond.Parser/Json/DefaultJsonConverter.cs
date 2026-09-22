using System.Text.Json;
using Bond.Parser.Syntax;

namespace Bond.Parser.Json;

internal sealed class DefaultJsonConverter : WriteOnlyJsonConverter<Default>
{
    public override void Write(Utf8JsonWriter writer, Default value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        switch (value)
        {
            case Default.Bool boolValue:
                writer.WriteString("type", "bool");
                writer.WriteBoolean("value", boolValue.Value);
                break;

            case Default.Integer intValue:
                writer.WriteString("type", "integer");
                writer.WritePropertyName("value");
                // BigInteger can be arbitrarily large, write as raw JSON number
                writer.WriteRawValue(intValue.Value.ToString());
                break;

            case Default.Float floatValue:
                writer.WriteString("type", "float");
                // Collapse negative zero to match the upstream JSON output.
                var normalized = floatValue.Value == 0 ? 0 : floatValue.Value;
                writer.WriteNumber("value", normalized);
                break;

            case Default.String stringValue:
                writer.WriteString("type", "string");
                writer.WriteString("value", stringValue.Value);
                break;

            case Default.Enum enumValue:
                writer.WriteString("type", "enum");
                writer.WriteString("value", enumValue.Identifier);
                break;

            case Default.Nothing:
                writer.WriteString("type", "nothing");
                break;

            default:
                throw new JsonException($"Unknown Default type: {value.GetType().Name}");
        }

        writer.WriteEndObject();
    }
}
