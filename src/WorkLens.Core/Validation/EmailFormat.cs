using System.Text.Json;
using Json.Schema;

namespace WorkLens.Core.Validation;

/// <summary>
/// The JSON Schema <c>format: email</c> check used by the IR schemas, so that an email accepted here also passes
/// schema validation of the written Evidence IR. Lives in Validation because only Schemas / Validation use
/// JsonSchema.Net (ADR-0008).
/// </summary>
public static class EmailFormat
{
    private static readonly JsonSchema Schema = JsonSchema.FromText(
        """{ "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "string", "format": "email" }""",
        new BuildOptions { SchemaRegistry = new SchemaRegistry() });

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.Flag,
        RequireFormatValidation = true,
    };

    public static bool IsValid(string? email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return false;
        }

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(email));
        return Schema.Evaluate(document.RootElement, Options).IsValid;
    }
}
