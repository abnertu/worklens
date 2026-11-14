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
/// Manifest problems are errors and give no presentation; report content that the layout cannot show is a warning
/// next to the presentation (see <see cref="LayoutResult.Warnings"/>).
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
/// <param name="Warnings">
/// Problems that do not stop the layout, with a presentation: <c>layout.unplaced_narrative</c> for a report narrative
/// that no slide shows. A 1.1 manifest reports each such narrative; a 1.0 manifest reports only support narratives.
/// <c>Details["path"]</c> is a JSON Pointer into the report.
/// </param>
public sealed record LayoutResult(
    PresentationDocument? Presentation,
    IReadOnlyList<WorkLensError> Errors,
    IReadOnlyList<WorkLensError> Warnings)
{
    public LayoutResult(PresentationDocument? presentation, IReadOnlyList<WorkLensError> errors)
        : this(presentation, errors, [])
    {
    }
}

/// <summary>Renders a validated Presentation IR into an output file (M0: pptx).</summary>
public interface IOutputRenderer
{
    string Format { get; }

    /// <param name="brand">Unit name and logo from the user's configuration; <c>null</c> for none.</param>
    Task<RenderResult> RenderAsync(
        PresentationDocument presentation,
        TemplateManifest manifest,
        string templatePath,
        string outputPath,
        BrandElements? brand,
        CancellationToken cancellationToken);
}

/// <summary>
/// The brand values that <c>render</c> writes into the brand positions that the manifest declares. They come from
/// the user's configuration, not from the report.
/// </summary>
public sealed record BrandElements(string? UnitName, LogoImage? Logo);

/// <summary>A logo image as the file holds it, with its pixel size from the file header.</summary>
public sealed record LogoImage(byte[] Bytes, string ContentType, int Width, int Height)
{
    /// <summary>
    /// Reads a PNG (IHDR) or JPEG (SOF marker) image; <c>null</c> for another format or a header without a size.
    /// The bytes are kept as they are, metadata included.
    /// </summary>
    public static LogoImage? Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(png) && bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            return Sized(bytes, "image/png", BigEndian(bytes, 16, 4), BigEndian(bytes, 20, 4));
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            // Walk the marker segments to the first start-of-frame (SOF0..SOF15 without DHT, JPG and DAC).
            var i = 2;
            while (i + 9 < bytes.Length && bytes[i] == 0xFF)
            {
                // Fill bytes: any number of 0xFF may come before a marker.
                if (bytes[i + 1] == 0xFF)
                {
                    i++;
                    continue;
                }

                var marker = bytes[i + 1];
                var length = BigEndian(bytes, i + 2, 2);
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    return Sized(bytes, "image/jpeg", BigEndian(bytes, i + 7, 2), BigEndian(bytes, i + 5, 2));
                }

                if (length < 2)
                {
                    return null;
                }

                i += 2 + length;
            }
        }

        return null;
    }

    private static LogoImage? Sized(byte[] bytes, string contentType, int width, int height) =>
        width > 0 && height > 0 ? new LogoImage(bytes, contentType, width, height) : null;

    private static int BigEndian(byte[] bytes, int offset, int count)
    {
        var value = 0L;
        for (var k = 0; k < count; k++)
        {
            value = (value << 8) | bytes[offset + k];
        }

        return value > int.MaxValue ? 0 : (int)value;
    }
}

/// <param name="Warnings">
/// Problems that do not stop the rendering: <c>render.brand_not_written</c> for a brand value that no position takes.
/// </param>
public sealed record RenderResult(string? OutputPath, IReadOnlyList<WorkLensError> Errors, IReadOnlyList<WorkLensError> Warnings)
{
    public RenderResult(string? outputPath, IReadOnlyList<WorkLensError> errors)
        : this(outputPath, errors, [])
    {
    }
}
