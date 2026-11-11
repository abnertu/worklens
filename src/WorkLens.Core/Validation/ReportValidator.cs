using System.Text.Json;
using System.Text.RegularExpressions;
using WorkLens.Core.Contracts;
using WorkLens.Core.Errors;
using WorkLens.Core.Evidence;
using WorkLens.Core.Json;
using WorkLens.Core.Report;
using WorkLens.Core.Schemas;

namespace WorkLens.Core.Validation;

/// <summary>
/// Report IR validation (docs/design/domain-model.md section 3.2). Layers run in order: JSON syntax, root type and
/// schema version, schema, deserialization, references, rules. Each layer reports all of its issues; an error in a
/// layer stops the later layers. Rules produce warnings only.
/// </summary>
public sealed partial class ReportValidator : IReportValidator
{
    private readonly IrSchemaValidator _schemaValidator;

    public ReportValidator()
        : this(new IrSchemaValidator())
    {
    }

    public ReportValidator(IrSchemaValidator schemaValidator) => _schemaValidator = schemaValidator;

    /// <summary>Numeric effect claims such as <c>30%</c>, <c>2 倍</c> or <c>3x</c> (rule.unreferenced_numeric_claim).</summary>
    [GeneratedRegex(@"\d+(?:\.\d+)?\s*(?:%|％|倍|x\b|×)", RegexOptions.CultureInvariant)]
    private static partial Regex NumericClaim();

    public ValidationResult Validate(string reportJson, EvidenceDocument? evidence, string? evidenceSha256)
    {
        ArgumentNullException.ThrowIfNull(reportJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(reportJson);
        }
        catch (JsonException ex)
        {
            return Single("json.invalid", string.Empty, $"Report is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Single("schema.type", string.Empty, $"Report root must be an object, not {root.ValueKind}.");
            }

            if (!IsSupportedVersion(root))
            {
                return Single(
                    "schema.unsupported_version",
                    "/schema_version",
                    $"schema_version must be \"{ReportDocument.CurrentSchemaVersion}\" (the version built into this WorkLens).");
            }

            var schemaResult = _schemaValidator.Validate(ReportDocument.SchemaId, root);
            if (!schemaResult.IsValid)
            {
                return schemaResult;
            }

            ReportDocument report;
            try
            {
                report = root.Deserialize<ReportDocument>(WorkLensJson.Options)
                    ?? throw new JsonException("Report deserialized to null.");
            }
            catch (JsonException ex)
            {
                return Single("json.deserialize_failed", JsonPointer.FromJsonPath(ex.Path), $"Report cannot be read: {ex.Message}");
            }

            if (evidence is null)
            {
                return ValidationResult.Success;
            }

            var issues = new List<ValidationIssue>();
            var evidenceById = evidence.Items
                .GroupBy(i => i.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            CheckReferences(report, evidence, evidenceSha256, evidenceById, issues);
            if (issues.Count > 0)
            {
                return new ValidationResult(issues);
            }

            CheckRules(report, evidenceById, issues);
            return new ValidationResult(issues);
        }
    }

    private static bool IsSupportedVersion(JsonElement root) =>
        root.TryGetProperty("schema_version", out var version)
        && version.ValueKind == JsonValueKind.String
        && IrSchemaCatalog.FindById(ReportDocument.SchemaId) is { } info
        && string.Equals(version.GetString(), info.Version, StringComparison.Ordinal);

    private static void CheckReferences(
        ReportDocument report,
        EvidenceDocument evidence,
        string? evidenceSha256,
        Dictionary<string, EvidenceItem> evidenceById,
        List<ValidationIssue> issues)
    {
        if (!string.Equals(report.EvidenceDocument.DocumentId, evidence.DocumentId, StringComparison.Ordinal))
        {
            issues.Add(Error(
                "ref.evidence_document_mismatch",
                "/evidence_document/document_id",
                $"Report is bound to evidence document '{report.EvidenceDocument.DocumentId}', but the evidence file is '{evidence.DocumentId}'."));
        }

        if (evidenceSha256 is not null
            && !string.Equals(report.EvidenceDocument.Sha256, evidenceSha256, StringComparison.Ordinal))
        {
            issues.Add(Error(
                "ref.evidence_document_mismatch",
                "/evidence_document/sha256",
                $"Report is bound to evidence SHA-256 {report.EvidenceDocument.Sha256}, but the evidence file is {evidenceSha256}."));
        }

        foreach (var (pointer, id) in EvidenceReferences(report))
        {
            if (!evidenceById.ContainsKey(id))
            {
                issues.Add(Error("ref.evidence_missing", pointer, $"Evidence '{id}' does not exist in the evidence document."));
            }
        }

        AddDuplicates(report.WorkItems.Select(w => w.Id), "/work_items", issues);
        AddDuplicates(report.Narratives.Select(n => n.Id), "/narratives", issues);
    }

    private static void AddDuplicates(IEnumerable<string> ids, string arrayPointer, List<ValidationIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var id in ids)
        {
            if (!seen.Add(id))
            {
                issues.Add(Error("ref.duplicate_id", $"{arrayPointer}/{index}/id", $"Id '{id}' is already used."));
            }

            index++;
        }
    }

    private static void CheckRules(
        ReportDocument report,
        Dictionary<string, EvidenceItem> evidenceById,
        List<ValidationIssue> issues)
    {
        for (var i = 0; i < report.WorkItems.Count; i++)
        {
            var item = report.WorkItems[i];
            if (item is { Status: WorkStatus.Completed, Origin: Common.ContentOrigin.Agent }
                && item.ReviewStatus != ReviewStatus.Approved
                && !WorkItemReferences(item).Any(id => evidenceById[id] is { Kind: EvidenceKind.ChangeRequest, State: EvidenceState.Merged }))
            {
                issues.Add(Warning(
                    "rule.completed_without_merged_change",
                    $"/work_items/{i}/status",
                    "Status is completed, but no referenced change request is merged. Use unknown or in_progress, or have a human set review_status to approved."));
            }
        }

        foreach (var (pointer, id) in EvidenceReferences(report))
        {
            var occurredAt = evidenceById[id].Timestamps.OccurredAt;
            if (occurredAt < report.Period.Start || occurredAt >= report.Period.End)
            {
                issues.Add(Warning(
                    "rule.evidence_outside_period",
                    pointer,
                    $"Evidence '{id}' occurred at {occurredAt:O}, outside the report period {report.Period.Label}."));
            }
        }

        for (var i = 0; i < report.WorkItems.Count; i++)
        {
            var item = report.WorkItems[i];
            if (item.ObservedFacts.Count > 0)
            {
                continue;
            }

            if (NumericClaim().IsMatch(item.Summary))
            {
                issues.Add(NumericWarning($"/work_items/{i}/summary"));
            }

            if (item.AgentInference is { } inference && NumericClaim().IsMatch(inference.Statement))
            {
                issues.Add(NumericWarning($"/work_items/{i}/agent_inference/statement"));
            }
        }
    }

    private static ValidationIssue NumericWarning(string pointer) => Warning(
        "rule.unreferenced_numeric_claim",
        pointer,
        "Contains a numeric effect (percentage or multiple) but the work item has no observed facts to support it.");

    /// <summary>Every evidence reference in the report with its JSON Pointer, in document order.</summary>
    private static IEnumerable<(string Pointer, string Id)> EvidenceReferences(ReportDocument report)
    {
        for (var i = 0; i < report.WorkItems.Count; i++)
        {
            var item = report.WorkItems[i];
            for (var f = 0; f < item.ObservedFacts.Count; f++)
            {
                var refs = item.ObservedFacts[f].EvidenceRefs;
                for (var r = 0; r < refs.Count; r++)
                {
                    yield return ($"/work_items/{i}/observed_facts/{f}/evidence_refs/{r}", refs[r]);
                }
            }

            for (var r = 0; r < item.EvidenceRefs.Count; r++)
            {
                yield return ($"/work_items/{i}/evidence_refs/{r}", item.EvidenceRefs[r]);
            }
        }

        for (var n = 0; n < report.Narratives.Count; n++)
        {
            var refs = report.Narratives[n].EvidenceRefs ?? [];
            for (var r = 0; r < refs.Count; r++)
            {
                yield return ($"/narratives/{n}/evidence_refs/{r}", refs[r]);
            }
        }
    }

    private static IEnumerable<string> WorkItemReferences(WorkItem item) =>
        item.EvidenceRefs.Concat(item.ObservedFacts.SelectMany(f => f.EvidenceRefs));

    private static ValidationResult Single(string code, string pointer, string message) =>
        new([Error(code, pointer, message)]);

    private static ValidationIssue Error(string code, string pointer, string message) =>
        new(code, IssueSeverity.Error, pointer, null, message);

    private static ValidationIssue Warning(string code, string pointer, string message) =>
        new(code, IssueSeverity.Warning, pointer, null, message);
}
