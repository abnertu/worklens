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
