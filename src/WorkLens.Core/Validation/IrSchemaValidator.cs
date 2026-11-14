using System.Text.Json;
using Json.Schema;
using WorkLens.Core.Errors;
using WorkLens.Core.Schemas;

namespace WorkLens.Core.Validation;

/// <summary>
/// Validates an IR document against a built-in JSON Schema and converts the results into
/// <see cref="ValidationIssue"/>s with code <c>schema.&lt;keyword&gt;</c>.
/// </summary>
/// <remarks>
/// Uses hierarchical output and walks only failing nodes, so errors from subschemas that do not make the
/// document invalid are not reported: a false <c>if</c> condition, the non-matching branches of a passing
/// <c>oneOf</c>/<c>anyOf</c>, or the subschema of <c>not</c>. Applicator errors (<c>properties</c>,
/// <c>items</c>, ...) are turned off so that only the actionable leaf keywords remain.
/// </remarks>
public sealed class IrSchemaValidator
{
    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.Hierarchical,
        RequireFormatValidation = true,
        IncludeApplicatorErrors = false,
    };

    private readonly WorkLensSchemaRegistry _registry;

    public IrSchemaValidator()
        : this(WorkLensSchemaRegistry.Default)
    {
    }

    public IrSchemaValidator(WorkLensSchemaRegistry registry) => _registry = registry;

    public ValidationResult Validate(string schemaId, JsonElement instance)
    {
        var results = _registry.Get(schemaId).Evaluate(instance, Options);
        if (results.IsValid)
        {
            return ValidationResult.Success;
        }

        var issues = new List<ValidationIssue>();
        Collect(results, issues);

        if (issues.Count == 0)
        {
            issues.Add(new ValidationIssue("schema.invalid", IssueSeverity.Error, string.Empty, null,
                "Document does not match the schema."));
        }

        return new ValidationResult(issues);
    }

    public ValidationResult Validate(string schemaId, string json)
    {
        using var document = JsonDocument.Parse(json);
        return Validate(schemaId, document.RootElement);
    }

    /// <summary>
    /// Validates a document against the built-in version of <paramref name="schemaName"/> that its version property
    /// names. A missing, non-string or unknown version is <c>schema.unsupported_version</c> at the version property.
    /// A root that is not an object is validated against the latest version, which reports <c>schema.type</c>.
    /// </summary>
    public ValidationResult ValidateDocument(string schemaName, JsonElement instance)
    {
        var versions = IrSchemaCatalog.Versions(schemaName);
        if (versions.Count == 0)
        {
            throw new ArgumentException($"Unknown schema name '{schemaName}'.", nameof(schemaName));
        }

        if (instance.ValueKind != JsonValueKind.Object)
        {
            return Validate(versions[^1].Id, instance);
        }

        var property = versions[0].VersionProperty;
        var info = instance.TryGetProperty(property, out var version) && version.ValueKind == JsonValueKind.String
            ? IrSchemaCatalog.Find(schemaName, version.GetString()!)
            : null;
        if (info is null)
        {
            var known = string.Join(", ", versions.Select(v => $"\"{v.Version}\""));
            return new ValidationResult([new ValidationIssue(
                "schema.unsupported_version",
                IssueSeverity.Error,
                "/" + property,
                null,
                $"{property} must be one of {known} (the {schemaName} versions built into this WorkLens).")]);
        }

        return Validate(info.Id, instance);
    }

    private static void Collect(EvaluationResults node, List<ValidationIssue> issues)
    {
        if (node.IsValid)
        {
            return;
        }

        var evaluationPath = node.EvaluationPath.ToString();
        var lastSegment = LastSegment(evaluationPath);
        if (lastSegment is "if" or "not")
        {
            // A failing `if` only selects `else`; a failing `not` subschema means `not` passed.
            return;
        }

        if (node.Errors is not null)
        {
            foreach (var (key, message) in node.Errors)
            {
                // additionalProperties:false reports its error under an empty key; the keyword is then
                // the last segment of the evaluation path.
                var keyword = key.Length > 0 ? key : lastSegment;
                if (keyword == "dependentSchemas")
                {
                    // An applicator: IncludeApplicatorErrors = false does not suppress it, and its failing
                    // subschema reports the leaf keyword below.
                    continue;
                }

                issues.Add(new ValidationIssue(
                    Code: "schema." + keyword,
                    Severity: IssueSeverity.Error,
                    InstancePath: node.InstanceLocation.ToString(),
                    SchemaPath: evaluationPath,
                    Message: message));
            }
        }

        if (node.Details is null)
        {
            return;
        }

        foreach (var child in node.Details)
        {
            Collect(child, issues);
        }
    }

    private static string LastSegment(string pointer)
    {
        var index = pointer.LastIndexOf('/');
        var segment = index >= 0 ? pointer[(index + 1)..] : pointer;
        return segment.Replace("~1", "/").Replace("~0", "~");
    }
}
