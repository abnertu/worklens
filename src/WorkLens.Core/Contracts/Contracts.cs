using WorkLens.Core.Common;
using WorkLens.Core.Errors;
using WorkLens.Core.Evidence;
using WorkLens.Core.Presentation;
using WorkLens.Core.Report;
using WorkLens.Core.Templates;

namespace WorkLens.Core.Contracts;

// Extension-point contracts (docs/design/domain-model.md section 7). v0.1 ships no implementations;
// result and option types are minimal and are refined by the milestone that implements each contract.
// Expected failures are returned in the result types; exceptions are reserved for programming errors.

/// <summary>Collects evidence from one source platform (M1).</summary>
public interface ISourceConnector
{
    SourcePlatform Platform { get; }

    Task<SourceCollectResult> CollectAsync(SourceQuery query, CancellationToken cancellationToken);
}

public sealed record SourceQuery(Period Period, IReadOnlyList<Identity> Identities, SourceSpec Source);

/// <summary>
/// Items of one source, or the errors that stopped collection. <see cref="Warnings"/> are non-fatal problems
/// (for example <c>source.truncated</c>) that the CLI reports as warning issues.
/// </summary>
public sealed record SourceCollectResult(
    IReadOnlyList<EvidenceItem> Items,
    IReadOnlyList<WorkLensError> Errors,
    IReadOnlyList<WorkLensError> Warnings)
{
    public bool IsSuccess => Errors.Count == 0;
}

/// <summary>Builds the minimized, redacted agent input from Evidence IR (M2).</summary>
public interface IEvidenceDigester
{
    EvidenceDigest CreateDigest(EvidenceDocument evidence, DigestOptions options);
}

public sealed record DigestOptions(bool IncludeDiff = false, int MaxBodyChars = 1000, int PathDepth = 3);

/// <summary>
/// Agent input. Its content format is versioned by <see cref="DigestVersion"/> and defined in M2. It carries the
/// evidence document id and SHA-256 so the agent can fill Report IR <c>evidence_document</c> without reading
/// evidence.json.
/// </summary>
public sealed record EvidenceDigest(
    string DigestVersion,
    string EvidenceDocumentId,
    string EvidenceDocumentSha256,
    string Content);

/// <summary>
/// Produces Report IR from a digest. In v0.1 the agent CLI plays this role and WorkLens has no implementation
/// (ADR-0004); the contract is reserved for a future built-in provider or headless runner.
/// </summary>
public interface IAnalysisModule
{
    string Name { get; }

    Task<AnalysisResult> AnalyzeAsync(EvidenceDigest digest, CancellationToken cancellationToken);
}

public sealed record AnalysisResult(ReportDocument? Report, IReadOnlyList<WorkLensError> Errors);

/// <summary>
/// Validates Report IR (M0) from its raw JSON text: JSON syntax, schema version and schema first (so unknown fields
/// are reported as <c>schema.additionalProperties</c> rather than failing deserialization), then referential
/// integrity and domain rules against the evidence document and its SHA-256. Each layer reports all of its issues;
/// a layer with an error stops the later layers. Never throws for invalid input.
/// </summary>
public interface IReportValidator
{
    /// <param name="reportJson">Report IR JSON text (without BOM).</param>
    /// <param name="evidence">The evidence document; <c>null</c> skips the reference and rule layers.</param>
    /// <param name="evidenceSha256">SHA-256 of the evidence file bytes; <c>null</c> skips the hash comparison.</param>
    ValidationResult Validate(string reportJson, EvidenceDocument? evidence, string? evidenceSha256);
}

/// <summary>
/// Deterministically maps a validated Report IR to Presentation IR using a Template Manifest (M0). A pure function.
/// </summary>
public interface ILayoutResolver
{
    /// <param name="reportSha256">SHA-256 of the report.json bytes, recorded as <c>source_report.sha256</c>.</param>
    LayoutResult Resolve(ReportDocument report, string reportSha256, EvidenceDocument evidence, TemplateManifest manifest);
}

/// <summary>
/// <see cref="Presentation"/> is <c>null</c> when <see cref="Errors"/> is not empty. Errors are manifest problems
/// (<c>manifest.*</c>) whose <c>Details["path"]</c> is a JSON Pointer into the manifest.
/// </summary>
public sealed record LayoutResult(PresentationDocument? Presentation, IReadOnlyList<WorkLensError> Errors);

/// <summary>Renders a validated Presentation IR into an output file (M0: pptx).</summary>
public interface IOutputRenderer
{
    string Format { get; }

    Task<RenderResult> RenderAsync(
        PresentationDocument presentation,
        TemplateManifest manifest,
        string templatePath,
        string outputPath,
        CancellationToken cancellationToken);
}

public sealed record RenderResult(string? OutputPath, IReadOnlyList<WorkLensError> Errors);
