using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkLens.Core.Presentation;

/// <summary>
/// Reads and writes a <see cref="SlotValue"/> with its <c>kind</c> discriminator. It replaces
/// <c>[JsonPolymorphic]</c>, which does not allow a custom converter on a derived type.
/// </summary>
/// <remarks>
/// Writes <c>kind</c> first, then the members of the derived record. Reads <c>kind</c> at any position in the
/// object. The derived record is read with the same options, so an undefined member fails as
/// <see cref="JsonUnmappedMemberHandling.Disallow"/> requires.
/// <para>
/// A table cell is a string, or a <c>{text, style}</c> object (Presentation IR 1.1). The converter reads the text of
/// each cell into <see cref="TableSlotValue.Rows"/> and the style into <see cref="TableSlotValue.CellStyles"/>. It
/// writes a cell without a style as a string.
/// </para>
/// </remarks>
internal sealed class SlotValueConverter : JsonConverter<SlotValue>
{
    private const string KindProperty = "kind";
    private const string TextProperty = "text";
    private const string StyleProperty = "style";

    public override SlotValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"A slot value must be a JSON object, not {root.ValueKind}.");
        }

        string? kind = null;
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals(KindProperty))
            {
                kind = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : throw new JsonException("The slot value member 'kind' must be a string.");
            }
        }

        var type = kind switch
        {
            "text" => typeof(TextSlotValue),
            "bullets" => typeof(BulletsSlotValue),
            "table" => typeof(TableSlotValue),
            "metric" => typeof(MetricSlotValue),
            null => throw new JsonException("The slot value has no 'kind' member."),
            _ => throw new JsonException($"Unknown slot value kind '{kind}'."),
        };

        var rowsProperty = PropertyName(nameof(TableSlotValue.Rows), options);
        List<List<string?>>? cellStyles = null;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals(KindProperty))
                {
                    continue;
                }

                if (type == typeof(TableSlotValue) && property.NameEquals(rowsProperty) && property.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName(property.Name);
                    cellStyles = WriteRowTexts(writer, property.Value);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        var value = (SlotValue)(JsonSerializer.Deserialize(buffer.WrittenSpan, type, options)
            ?? throw new JsonException("The slot value is null."));
        return value is TableSlotValue table && cellStyles is not null && cellStyles.Any(row => row.Any(style => style is not null))
            ? table with { CellStyles = cellStyles }
            : value;
    }

    // Writes the rows with the text of each cell, and returns the style of each cell. A row or a cell of another JSON
    // type is copied, so that the table deserialization reports it.
    private static List<List<string?>> WriteRowTexts(Utf8JsonWriter writer, JsonElement rows)
    {
        var styles = new List<List<string?>>();
        writer.WriteStartArray();
        foreach (var row in rows.EnumerateArray())
        {
            var rowStyles = new List<string?>();
            styles.Add(rowStyles);
            if (row.ValueKind != JsonValueKind.Array)
            {
                row.WriteTo(writer);
                continue;
            }

            writer.WriteStartArray();
            foreach (var cell in row.EnumerateArray())
            {
                if (cell.ValueKind == JsonValueKind.Object)
                {
                    var (text, style) = ReadStyledCell(cell);
                    writer.WriteStringValue(text);
                    rowStyles.Add(style);
                }
                else
                {
                    cell.WriteTo(writer);
                    rowStyles.Add(null);
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        return styles;
    }

    private static (string Text, string Style) ReadStyledCell(JsonElement cell)
    {
        string? text = null;
        string? style = null;
        foreach (var member in cell.EnumerateObject())
        {
            if (member.NameEquals(TextProperty) && member.Value.ValueKind == JsonValueKind.String)
            {
                text = member.Value.GetString();
            }
            else if (member.NameEquals(StyleProperty) && member.Value.ValueKind == JsonValueKind.String)
            {
                style = member.Value.GetString();
            }
            else
            {
                throw new JsonException($"A table cell object has the undefined or non-string member '{member.Name}'.");
            }
        }

        return text is not null && style is not null
            ? (text, style)
            : throw new JsonException("A table cell object must have the string members 'text' and 'style'.");
    }

    public override void Write(Utf8JsonWriter writer, SlotValue value, JsonSerializerOptions options)
    {
        var kind = value switch
        {
            TextSlotValue => "text",
            BulletsSlotValue => "bullets",
            TableSlotValue => "table",
            MetricSlotValue => "metric",
            _ => throw new JsonException($"Unsupported slot value {value.GetType().Name}."),
        };

        var styledTable = value is TableSlotValue { CellStyles: not null } table ? table : null;
        if (styledTable is not null)
        {
            CheckCellStylesShape(styledTable);
        }

        var rowsProperty = PropertyName(nameof(TableSlotValue.Rows), options);
        writer.WriteStartObject();
        writer.WriteString(KindProperty, kind);
        using var members = JsonSerializer.SerializeToDocument(value, value.GetType(), options);
        foreach (var property in members.RootElement.EnumerateObject())
        {
            if (styledTable is not null && property.NameEquals(rowsProperty))
            {
                writer.WritePropertyName(property.Name);
                WriteStyledRows(writer, styledTable);
            }
            else
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static void CheckCellStylesShape(TableSlotValue table)
    {
        var styles = table.CellStyles!;
        if (styles.Count != table.Rows.Count)
        {
            throw new JsonException($"The table has {table.Rows.Count} rows, but {styles.Count} rows of cell styles.");
        }

        for (var r = 0; r < styles.Count; r++)
        {
            if (styles[r].Count != table.Rows[r].Count)
            {
                throw new JsonException($"The cell styles of row {r} do not match its cells.");
            }
        }
    }

    private static void WriteStyledRows(Utf8JsonWriter writer, TableSlotValue table)
    {
        writer.WriteStartArray();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            writer.WriteStartArray();
            for (var c = 0; c < table.Rows[r].Count; c++)
            {
                var style = table.CellStyles![r][c];
                if (style is null)
                {
                    writer.WriteStringValue(table.Rows[r][c]);
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteString(TextProperty, table.Rows[r][c]);
                    writer.WriteString(StyleProperty, style);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }

    private static string PropertyName(string name, JsonSerializerOptions options) =>
        options.PropertyNamingPolicy?.ConvertName(name) ?? name;
}
