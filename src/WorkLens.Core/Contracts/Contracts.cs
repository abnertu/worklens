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

public sealed record SourceCollectResult(IReadOnlyList<EvidenceItem> Items, IReadOnlyList<WorkLensError> Errors);

/// <summary>Builds the minimized, redacted agent input from Evidence IR (M2).</summary>
public interface IEvidenceDigester
{
    EvidenceDigest CreateDigest(EvidenceDocument evidence, DigestOptions options);
}

public sealed record DigestOptions(bool IncludeDiff = false, int MaxBodyChars = 1000, int PathDepth = 3);

/// <summary>Agent input. Its content format is versioned by <see cref="DigestVersion"/> and defined in M2.</summary>
public sealed record EvidenceDigest(string DigestVersion, string EvidenceDocumentId, string Content);

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

/// <summary>Validates Report IR: schema, referential integrity and domain rules (M0).</summary>
public interface IReportValidator
{
    ValidationResult Validate(ReportDocument report, EvidenceDocument? evidence);
}

/// <summary>Deterministically maps Report IR to Presentation IR using a Template Manifest (M0).</summary>
public interface ILayoutResolver
{
    LayoutResult Resolve(ReportDocument report, EvidenceDocument evidence, TemplateManifest manifest);
}

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
