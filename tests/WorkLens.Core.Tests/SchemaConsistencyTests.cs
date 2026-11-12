using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using WorkLens.Core.Json;
using WorkLens.Core.Schemas;
using WorkLens.Core.Validation;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class SchemaConsistencyTests
{
    public static TheoryData<string> SchemaNames()
    {
        var data = new TheoryData<string>();
        foreach (var info in IrSchemaCatalog.All)
        {
            data.Add(info.Name);
        }

        return data;
    }

    public static TheoryData<string, string> ValidExamples() => SchemaExamples.Cases("valid");

    public static TheoryData<string, string> InvalidExamples() => SchemaExamples.Cases("invalid");

    private static readonly IrSchemaValidator Validator = new();

    // (a) Each schema is valid against the draft 2020-12 meta-schema.
    [Theory]
    [MemberData(nameof(SchemaNames))]
    public void Schema_is_valid_against_draft_2020_12_meta_schema(string schemaName)
    {
        var text = File.ReadAllText(SchemaExamples.SchemaFile(SchemaExamples.Info(schemaName)));
        using var document = JsonDocument.Parse(text);

        var results = MetaSchemas.Draft202012.Evaluate(
            document.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(results.IsValid, $"{schemaName} is not a valid 2020-12 schema.");
    }

    // The embedded copy used at runtime is the file in /schemas, and its $id matches the catalog.
    [Theory]
    [MemberData(nameof(SchemaNames))]
    public void Embedded_schema_matches_file_and_catalog_id(string schemaName)
    {
        var info = SchemaExamples.Info(schemaName);
        var fileText = File.ReadAllText(SchemaExamples.SchemaFile(info));
        var embeddedText = WorkLensSchemaRegistry.Default.GetText(info.Id);

        Assert.Equal(fileText, embeddedText);
        Assert.Equal(info.Id, JsonNode.Parse(fileText)!["$id"]!.GetValue<string>());
    }

    // (b) Each valid example passes, with format validation enabled.
    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void Valid_example_passes(string schemaName, string fileName)
    {
        var info = SchemaExamples.Info(schemaName);
        var json = File.ReadAllText(SchemaExamples.ExamplePath(schemaName, "valid", fileName));

        var result = Validator.Validate(info.Id, json);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues.Select(i => $"{i.Code} at {i.InstancePath}: {i.Message}")));
    }

    // (c) Each invalid example fails, and fails for the keyword named by its file name prefix.
    [Theory]
    [MemberData(nameof(InvalidExamples))]
    public void Invalid_example_fails_for_expected_keyword(string schemaName, string fileName)
    {
        var separator = fileName.IndexOf("--", StringComparison.Ordinal);
        Assert.True(separator > 0, $"Invalid example '{fileName}' must be named '<keyword>--<description>.json'.");
        var expectedCode = "schema." + fileName[..separator];
        var info = SchemaExamples.Info(schemaName);
        var json = File.ReadAllText(SchemaExamples.ExamplePath(schemaName, "invalid", fileName));

        var result = Validator.Validate(info.Id, json);

        Assert.False(result.IsValid);
        Assert.True(
            result.Issues.Any(i => i.Code == expectedCode),
            $"Expected {expectedCode}; got: {string.Join(", ", result.Issues.Select(i => $"{i.Code}@{i.InstancePath}"))}");
    }

    // Issues point at the actionable leaf (keyword + instance path), without parent applicator noise.
    [Fact]
    public void Issues_report_leaf_keyword_and_instance_path()
    {
        var json = File.ReadAllText(SchemaExamples.ExamplePath("report-ir", "invalid", "minItems--agent-item-without-evidence.json"));

        var result = Validator.Validate(SchemaExamples.Info("report-ir").Id, json);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("schema.minItems", issue.Code);
        Assert.Equal("/work_items/1/evidence_refs", issue.InstancePath);
    }

    // (d) Each valid example round-trips through its C# root type without losing or adding data.
    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void Valid_example_round_trips_through_csharp_model(string schemaName, string fileName)
    {
        var info = SchemaExamples.Info(schemaName);
        var json = File.ReadAllText(SchemaExamples.ExamplePath(schemaName, "valid", fileName));

        var model = JsonSerializer.Deserialize(json, info.RootType, WorkLensJson.Options);
        Assert.NotNull(model);
        Assert.IsType(info.RootType, model);
        var roundTripped = JsonSerializer.Serialize(model, info.RootType, WorkLensJson.Options);

        var expected = JsonNode.Parse(json);
        var actual = JsonNode.Parse(roundTripped);
        Assert.True(JsonNode.DeepEquals(expected, actual), $"Round-trip changed {fileName}:{Environment.NewLine}{roundTripped}");
    }

    // The serialized form of a model is itself schema-valid (guards against model/schema drift on write).
    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void Serialized_model_is_schema_valid(string schemaName, string fileName)
    {
        var info = SchemaExamples.Info(schemaName);
        var json = File.ReadAllText(SchemaExamples.ExamplePath(schemaName, "valid", fileName));
        var model = JsonSerializer.Deserialize(json, info.RootType, WorkLensJson.Options);

        var result = Validator.Validate(info.Id, JsonSerializer.Serialize(model, info.RootType, WorkLensJson.Options));

        Assert.True(result.IsValid);
    }

    // The valid examples form one consistent set (usable as M0 golden inputs): each document's SHA-256 binding
    // matches the file it points to.
    [Fact]
    public void Valid_examples_have_consistent_sha256_bindings()
    {
        var evidencePath = SchemaExamples.ExamplePath("evidence-ir", "valid", "monthly-github-gitlab.json");
        var reportPath = SchemaExamples.ExamplePath("report-ir", "valid", "monthly-report.json");
        var presentationPath = SchemaExamples.ExamplePath("presentation-ir", "valid", "monthly-presentation.json");

        var report = JsonNode.Parse(File.ReadAllText(reportPath))!;
        var presentation = JsonNode.Parse(File.ReadAllText(presentationPath))!;

        Assert.Equal(Sha256(evidencePath), report["evidence_document"]!["sha256"]!.GetValue<string>());
        Assert.Equal(Sha256(reportPath), presentation["source_report"]!["sha256"]!.GetValue<string>());
    }

    private static string Sha256(string path) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    // (e) Every catalog schema has examples of both kinds, and every schema directory is in the catalog.
    [Fact]
    public void Every_schema_has_valid_and_invalid_examples()
    {
        Assert.Equal(5, IrSchemaCatalog.All.Count);
        foreach (var info in IrSchemaCatalog.All)
        {
            Assert.True(File.Exists(SchemaExamples.SchemaFile(info)), $"Missing schema file for {info.Id}.");
            Assert.True(SchemaExamples.Examples(info, "valid").Any(), $"{info.Id} has no valid example.");
            Assert.True(SchemaExamples.Examples(info, "invalid").Any(), $"{info.Id} has no invalid example.");
        }

        var directories = Directory.EnumerateDirectories(SchemaExamples.SchemasRoot).Select(Path.GetFileName).Order();
        Assert.Equal(IrSchemaCatalog.All.Select(i => i.Name).Order(), directories);
    }
}
