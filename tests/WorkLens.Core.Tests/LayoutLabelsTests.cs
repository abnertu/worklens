using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkLens.Core.Layout;
using WorkLens.Core.Templates;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class LayoutLabelsTests
{
    private static readonly PropertyInfo[] LabelProperties = typeof(TemplateLabels).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    // A label that From does not copy would silently fall back to English for every manifest.
    [Fact]
    public void From_takes_every_manifest_label()
    {
        var labels = new TemplateLabels();
        foreach (var property in LabelProperties)
        {
            object marker = property.PropertyType == typeof(string)
                ? $"<{property.Name}>"
                : (IReadOnlyList<string>)[$"<{property.Name}>"];
            property.SetValue(labels, marker);
        }

        var resolved = LayoutLabels.From(labels);

        foreach (var property in LabelProperties)
        {
            var target = typeof(LayoutLabels).GetProperty(property.Name);
            Assert.True(target is not null, $"LayoutLabels has no property {property.Name}.");
            Assert.Equal(property.GetValue(labels), target.GetValue(resolved));
        }
    }

    // A label in the schema without a TemplateLabels property would be accepted by validation and then ignored.
    [Fact]
    public void Manifest_1_1_schema_labels_match_TemplateLabels()
    {
        var schema = JsonNode.Parse(File.ReadAllText(SchemaExamples.SchemaFile(SchemaExamples.Info("template-manifest", "1.1"))))!;
        var keys = schema["$defs"]!["labels"]!["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);
        var properties = LabelProperties.Select(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name)).Order(StringComparer.Ordinal);

        Assert.Equal(keys, properties);
    }

    [Fact]
    public void Continuation_default_puts_the_space_in_the_label() =>
        Assert.Equal(
            " (cont. 2/3)",
            LayoutLabels.Format(LayoutLabels.English.Continuation, new Dictionary<string, string> { ["index"] = "2", ["total"] = "3" }));
}
