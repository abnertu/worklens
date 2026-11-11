namespace WorkLens.Core.Errors;

public enum IssueSeverity
{
    Error,
    Warning,
}

/// <summary>
/// A machine-readable validation problem, designed so that an agent can locate and fix it.
/// </summary>
/// <param name="Code">Namespaced code, e.g. <c>schema.required</c> or <c>ref.evidence_missing</c>.</param>
/// <param name="InstancePath">JSON Pointer into the validated document, e.g. <c>/work_items/0/evidence_refs</c>.</param>
/// <param name="SchemaPath">Evaluation path in the schema, when the issue comes from schema validation.</param>
public sealed record ValidationIssue(
    string Code,
    IssueSeverity Severity,
    string InstancePath,
    string? SchemaPath,
    string Message);

public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public static ValidationResult Success { get; } = new([]);

    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);
}

/// <summary>A non-validation failure (configuration, source, rendering).</summary>
public sealed record WorkLensError(string Code, string Message, IReadOnlyDictionary<string, string>? Details = null);
