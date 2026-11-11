using System.Text.Json.Nodes;
using Xunit;

namespace WorkLens.Core.Tests;

/// <summary>
/// (f) Catches schema typos that the meta-schema accepts silently, e.g. <c>addtionalProperties</c> or
/// <c>format: date_time</c>. JsonSchema.Net ignores unknown keywords and unknown format names.
/// </summary>
public sealed class SchemaKeywordAllowlistTests
{
    private static readonly HashSet<string> Keywords =
    [
        // core
        "$schema", "$id", "$ref", "$defs", "$anchor", "$dynamicRef", "$dynamicAnchor", "$vocabulary", "$comment",
        // applicator
        "allOf", "anyOf", "oneOf", "not", "if", "then", "else", "dependentSchemas", "prefixItems", "items",
        "contains", "properties", "patternProperties", "additionalProperties", "propertyNames",
        // unevaluated
        "unevaluatedItems", "unevaluatedProperties",
        // validation
        "type", "const", "enum", "multipleOf", "maximum", "exclusiveMaximum", "minimum", "exclusiveMinimum",
        "maxLength", "minLength", "pattern", "maxItems", "minItems", "uniqueItems", "maxContains", "minContains",
        "maxProperties", "minProperties", "required", "dependentRequired",
        // meta-data, format, content
        "title", "description", "default", "deprecated", "readOnly", "writeOnly", "examples",
        "format", "contentEncoding", "contentMediaType", "contentSchema",
    ];

    private static readonly HashSet<string> Formats =
    [
        "date-time", "date", "time", "duration", "email", "idn-email", "hostname", "idn-hostname", "ipv4", "ipv6",
        "uri", "uri-reference", "iri", "iri-reference", "uuid", "uri-template", "json-pointer",
        "relative-json-pointer", "regex",
    ];

    private static readonly HashSet<string> SchemaMapKeywords = ["properties", "patternProperties", "$defs", "dependentSchemas"];

    private static readonly HashSet<string> SingleSchemaKeywords =
    [
        "items", "additionalProperties", "not", "if", "then", "else", "contains", "propertyNames",
        "unevaluatedItems", "unevaluatedProperties", "contentSchema",
    ];

    private static readonly HashSet<string> SchemaArrayKeywords = ["allOf", "anyOf", "oneOf", "prefixItems"];

    public static TheoryData<string> SchemaNames() => SchemaConsistencyTests.SchemaNames();

    [Theory]
    [MemberData(nameof(SchemaNames))]
    public void Schema_uses_only_known_keywords_and_formats(string schemaName)
    {
        var text = File.ReadAllText(SchemaExamples.SchemaFile(SchemaExamples.Info(schemaName)));

        var problems = FindProblems(JsonNode.Parse(text));

        Assert.Empty(problems);
    }

    [Fact]
    public void Walker_detects_misspelled_keyword_and_unknown_format()
    {
        var schema = JsonNode.Parse("""
            {
              "type": "object",
              "addtionalProperties": false,
              "properties": { "at": { "type": "string", "format": "date_time" } },
              "$defs": { "x": { "items": { "minLenght": 1 } } }
            }
            """);

        var problems = FindProblems(schema);

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("addtionalProperties", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("date_time", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("minLenght", StringComparison.Ordinal));
    }

    private static List<string> FindProblems(JsonNode? schema)
    {
        var problems = new List<string>();
        Visit(schema, string.Empty, problems);
        return problems;
    }

    private static void Visit(JsonNode? node, string path, List<string> problems)
    {
        if (node is not JsonObject schema)
        {
            return; // boolean schema
        }

        foreach (var (keyword, value) in schema)
        {
            var keywordPath = $"{path}/{keyword}";
            if (!Keywords.Contains(keyword))
            {
                problems.Add($"Unknown keyword at {keywordPath}");
                continue;
            }

            if (keyword == "format" && (value is not JsonValue v || !v.TryGetValue<string>(out var format) || !Formats.Contains(format)))
            {
                problems.Add($"Unknown format '{value}' at {keywordPath}");
            }
            else if (SchemaMapKeywords.Contains(keyword) && value is JsonObject map)
            {
                foreach (var (name, subschema) in map)
                {
                    Visit(subschema, $"{keywordPath}/{name}", problems);
                }
            }
            else if (SingleSchemaKeywords.Contains(keyword))
            {
                Visit(value, keywordPath, problems);
            }
            else if (SchemaArrayKeywords.Contains(keyword) && value is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    Visit(array[i], $"{keywordPath}/{i}", problems);
                }
            }
        }
    }
}
