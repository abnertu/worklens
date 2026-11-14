using System.Text.Json;
using System.Text.Json.Nodes;
using WorkLens.Core.Errors;
using WorkLens.Core.Schemas;
using WorkLens.Core.Validation;
using Xunit;

namespace WorkLens.Core.Tests;

/// <summary><see cref="IrSchemaValidator.ValidateDocument"/> picks the schema version that the document names.</summary>
public sealed class IrSchemaVersionSelectionTests
{
    private static readonly IrSchemaValidator Validator = new();

    public static TheoryData<string, string, string> ValidExamples() => SchemaExamples.Cases("valid");

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void Valid_example_passes_with_the_version_it_names(string schemaName, string version, string fileName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SchemaExamples.ExamplePath(schemaName, version, "valid", fileName)));

        var result = Validator.ValidateDocument(schemaName, document.RootElement);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues.Select(i => $"{i.Code} at {i.InstancePath}: {i.Message}")));
    }

    public static TheoryData<string, string> UnsupportedVersions()
    {
        var data = new TheoryData<string, string>();
        foreach (var name in IrSchemaCatalog.All.Select(i => i.Name).Distinct())
        {
            foreach (var version in new[] { "\"1.9\"", "\"2.0\"", "1", "null", "missing" })
            {
                data.Add(name, version);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(UnsupportedVersions))]
    public void Missing_non_string_or_unknown_version_is_unsupported(string schemaName, string version)
    {
        var info = IrSchemaCatalog.Latest(schemaName)!;
        var example = SchemaExamples.Examples(info, "valid").First();
        var node = JsonNode.Parse(File.ReadAllText(example))!.AsObject();
        node.Remove(info.VersionProperty);
        if (version != "missing")
        {
            node[info.VersionProperty] = JsonNode.Parse(version);
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        var result = Validator.ValidateDocument(schemaName, document.RootElement);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("schema.unsupported_version", issue.Code);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal("/" + info.VersionProperty, issue.InstancePath);
        foreach (var known in IrSchemaCatalog.Versions(schemaName))
        {
            Assert.Contains($"\"{known.Version}\"", issue.Message, StringComparison.Ordinal);
        }
    }

    // A 1.0 report with a 1.1 field is checked against 1.0, not against the latest minor.
    [Fact]
    public void Report_1_0_with_resolves_fails()
    {
        var report = JsonNode.Parse(File.ReadAllText(SchemaExamples.ExamplePath(IrSchemaCatalog.ReportIr, "valid", "monthly-report.json")))!;
        report["narratives"]![0]!["kind"] = "improvement";
        report["narratives"]![0]!["resolves"] = new JsonArray("nr-next-plan-sso");

        using var document = JsonDocument.Parse(report.ToJsonString());
        var result = Validator.ValidateDocument(IrSchemaCatalog.ReportIr, document.RootElement);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("schema.additionalProperties", issue.Code);
        Assert.Equal("/narratives/0/resolves", issue.InstancePath);
    }

    [Fact]
    public void Non_object_root_reports_schema_type_at_the_root()
    {
        using var document = JsonDocument.Parse("[]");

        var result = Validator.ValidateDocument(IrSchemaCatalog.EvidenceIr, document.RootElement);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("schema.type", issue.Code);
        Assert.Equal(string.Empty, issue.InstancePath);
    }
}
