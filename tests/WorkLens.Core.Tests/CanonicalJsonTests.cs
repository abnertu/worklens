using System.Text.Json;
using WorkLens.Core.Json;
using WorkLens.Core.Presentation;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class CanonicalJsonTests
{
    // The checked-in presentation example is in canonical form, so deserializing and re-serializing it is
    // byte-identical (UTF-8 without BOM, LF line endings, one trailing LF).
    [Fact]
    public void Presentation_example_round_trips_byte_for_byte()
    {
        var path = SchemaExamples.ExamplePath("presentation-ir", "valid", "monthly-presentation.json");
        var bytes = File.ReadAllBytes(path);
        var model = JsonSerializer.Deserialize<PresentationDocument>(bytes, WorkLensJson.Options);

        var canonical = WorkLensJson.SerializeCanonicalUtf8(model);

        Assert.Equal(bytes, canonical);
    }

    // The 1.1 example has each 1.1 member, so this test fixes the member order of each slot value kind, of a bullet
    // item and of a table cell object.
    [Fact]
    public void Presentation_1_1_example_round_trips_byte_for_byte()
    {
        var path = SchemaExamples.ExamplePath("presentation-ir", "1.1", "valid", "all-fields.json");
        var bytes = File.ReadAllBytes(path);
        var model = JsonSerializer.Deserialize<PresentationDocument>(bytes, WorkLensJson.Options);

        var canonical = WorkLensJson.SerializeCanonicalUtf8(model);

        Assert.Equal(bytes, canonical);
    }

    [Fact]
    public void Canonical_form_uses_lf_and_one_trailing_newline_without_bom()
    {
        var bytes = WorkLensJson.SerializeCanonicalUtf8(new Dictionary<string, int[]> { ["a"] = [1, 2] });

        Assert.NotEqual(0xEF, bytes[0]);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
        Assert.False(text.EndsWith("\n\n", StringComparison.Ordinal));
    }
}
