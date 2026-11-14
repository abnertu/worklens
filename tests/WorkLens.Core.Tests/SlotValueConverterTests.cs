using System.Text.Json;
using WorkLens.Core.Json;
using WorkLens.Core.Presentation;
using Xunit;

namespace WorkLens.Core.Tests;

// Each test uses the declared type SlotValue, so that the SlotValue converter runs.
public sealed class SlotValueConverterTests
{
    // The expected strings were captured from the [JsonPolymorphic] serializer that the converter replaced. The
    // golden examples have no `truncated`, so these cases guard its position and its `false` value.
    public static TheoryData<SlotValue, string> SerializedForms() => new()
    {
        { new TextSlotValue("a") { Truncated = true }, "{\n  \"kind\": \"text\",\n  \"text\": \"a\",\n  \"truncated\": true\n}" },
        { new TextSlotValue("a") { Truncated = false }, "{\n  \"kind\": \"text\",\n  \"text\": \"a\",\n  \"truncated\": false\n}" },
        {
            new BulletsSlotValue([new BulletItem("b", 1)]) { Truncated = true },
            "{\n  \"kind\": \"bullets\",\n  \"items\": [\n    {\n      \"text\": \"b\",\n      \"level\": 1\n    }\n  ],\n  \"truncated\": true\n}"
        },
        {
            new BulletsSlotValue([new BulletItem("b", 1)]) { Truncated = false },
            "{\n  \"kind\": \"bullets\",\n  \"items\": [\n    {\n      \"text\": \"b\",\n      \"level\": 1\n    }\n  ],\n  \"truncated\": false\n}"
        },
        {
            new TableSlotValue(["c"], [["x"]]) { Truncated = true },
            "{\n  \"kind\": \"table\",\n  \"columns\": [\n    \"c\"\n  ],\n  \"rows\": [\n    [\n      \"x\"\n    ]\n  ],\n  \"truncated\": true\n}"
        },
        {
            new TableSlotValue(["c"], [["x"]]) { Truncated = false },
            "{\n  \"kind\": \"table\",\n  \"columns\": [\n    \"c\"\n  ],\n  \"rows\": [\n    [\n      \"x\"\n    ]\n  ],\n  \"truncated\": false\n}"
        },
    };

    [Theory]
    [MemberData(nameof(SerializedForms))]
    public void Serializes_the_1_0_form(SlotValue value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, WorkLensJson.Options));
    }

    [Fact]
    public void Reads_kind_after_the_other_members()
    {
        var value = JsonSerializer.Deserialize<SlotValue>("""{"text":"a","truncated":true,"kind":"text"}""", WorkLensJson.Options);

        Assert.Equal(new TextSlotValue("a") { Truncated = true }, value);
    }

    [Theory]
    [InlineData("""{"kind":"text","text":"a","extra":1}""")]
    [InlineData("""{"kind":"bullets","items":[{"text":"b","level":0,"extra":1}]}""")]
    public void Rejects_undefined_members(string json)
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<SlotValue>(json, WorkLensJson.Options));
    }

    [Theory]
    [InlineData("""{"kind":"table","columns":["c"],"rows":[[{"text":"x","style":"s","extra":1}]]}""")]
    [InlineData("""{"kind":"table","columns":["c"],"rows":[[{"text":"x"}]]}""")]
    public void Rejects_an_invalid_cell_object(string json)
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<SlotValue>(json, WorkLensJson.Options));
    }

    // CellStyles must have the shape of Rows: one more row, or one more cell in a row.
    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void Rejects_cell_styles_in_another_shape(int styleRows, int stylesInARow)
    {
        SlotValue value = new TableSlotValue(["c"], [["x"]])
        {
            CellStyles = [.. Enumerable.Repeat<IReadOnlyList<string?>>([.. Enumerable.Repeat<string?>("s", stylesInARow)], styleRows)],
        };

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Serialize(value, WorkLensJson.Options));
    }
}
