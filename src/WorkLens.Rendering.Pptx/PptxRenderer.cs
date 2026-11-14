using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WorkLens.Core.Contracts;
using WorkLens.Core.Errors;
using WorkLens.Core.Presentation;
using WorkLens.Core.Templates;
using WorkLens.Core.Validation;
using P = DocumentFormat.OpenXml.Presentation;
using PresentationDocument = WorkLens.Core.Presentation.PresentationDocument;

namespace WorkLens.Rendering.Pptx;

/// <summary>
/// Renders Presentation IR into a .pptx built from the template's slide layouts (ADR-0006, Open XML SDK).
/// The template's own slides, notes, comment authors, custom properties and thumbnail are removed; every IR slide is
/// created from its layout and only bound slots get a shape. Document properties are rewritten (D12).
/// </summary>
/// <remarks>
/// Expects a presentation already checked against the manifest (the CLI does this). Template problems are returned as
/// <c>template.*</c> errors whose <c>Details["path"]</c> is a JSON Pointer into the manifest. The output file is written
/// once, after the whole package was built in memory; on any error nothing is written.
/// </remarks>
public sealed class PptxRenderer : IOutputRenderer
{
    public string Format => "pptx";

    public Task<RenderResult> RenderAsync(
        PresentationDocument presentation,
        TemplateManifest manifest,
        string templatePath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Render(presentation, manifest, templatePath, outputPath));
    }

    public static RenderResult Render(
        PresentationDocument presentation,
        TemplateManifest manifest,
        string templatePath,
        string outputPath)
    {
        byte[] templateBytes;
        try
        {
            templateBytes = File.ReadAllBytes(templatePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(new WorkLensError(
                "template.unreadable",
                $"Cannot read template {templatePath}: {ex.Message}",
                new Dictionary<string, string> { ["path"] = "/template/file" }));
        }

        if (manifest.Template.Sha256 is { } expected)
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(templateBytes));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                return Failed(new WorkLensError(
                    "template.sha256_mismatch",
                    $"Template {templatePath} has SHA-256 {actual}, but the manifest expects {expected}.",
                    new Dictionary<string, string> { ["path"] = "/template/sha256" }));
            }
        }

        using var stream = new MemoryStream();
        stream.Write(templateBytes);
        stream.Position = 0;

        IReadOnlyList<WorkLensError> errors;
        try
        {
            using var document = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(stream, isEditable: true);
            errors = Build(document, presentation, manifest);
        }
        catch (Exception ex) when (IsTemplateFault(ex))
        {
            return Failed(new WorkLensError(
                "template.unreadable",
                $"Template {templatePath} is not a readable .pptx package: {ex.Message}",
                new Dictionary<string, string> { ["path"] = "/template/file" }));
        }

        if (errors.Count > 0)
        {
            return new RenderResult(null, errors);
        }

        try
        {
            File.WriteAllBytes(outputPath, stream.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(new WorkLensError(
                "output.write_failed",
                $"Cannot write {outputPath}: {ex.Message}",
                new Dictionary<string, string> { ["file"] = outputPath }));
        }

        return new RenderResult(outputPath, []);
    }

    private static RenderResult Failed(WorkLensError error) => new(null, [error]);

    /// <summary>
    /// Exceptions that the SDK throws for a broken template while it opens or lazily loads parts: a bad package or
    /// XML, a relationship to a missing part (<see cref="InvalidOperationException"/>, or
    /// <see cref="NotSupportedException"/> when the package is saved), or an attribute value it
    /// cannot parse (<see cref="FormatException"/>, <see cref="OverflowException"/>). The presentation has already been
    /// checked against the manifest, so these come from the template (D14: exit 3).
    /// </summary>
    private static bool IsTemplateFault(Exception ex) => ex is OpenXmlPackageException
        or InvalidDataException
        or FileFormatException
        or System.Xml.XmlException
        or InvalidOperationException
        or NotSupportedException
        or FormatException
        or OverflowException
        or KeyNotFoundException;

    private static IReadOnlyList<WorkLensError> Build(
        DocumentFormat.OpenXml.Packaging.PresentationDocument document,
        PresentationDocument presentation,
        TemplateManifest manifest)
    {
        var presentationPart = document.PresentationPart
            ?? throw new InvalidDataException("The package has no presentation part.");

        var idErrors = TemplateManifestChecker.CheckIds(manifest);
        if (idErrors.Count > 0)
        {
            return idErrors;
        }

        var template = TemplateLayouts.Resolve(presentationPart, manifest, out var errors);
        if (errors.Count == 0)
        {
            // Only when the manifest fits the template: a missing layout would also show up as an unknown slide layout.
            errors.AddRange(CheckSlides(presentation, template));
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        TemplateCleaner.RemoveSlidesAndPersonalParts(document, presentationPart);

        var root = presentationPart.Presentation
            ?? throw new InvalidDataException("The presentation part has no presentation element.");
        var slideIdList = root.SlideIdList;
        if (slideIdList is null)
        {
            slideIdList = new P.SlideIdList();
            root.InsertAfter(slideIdList, (OpenXmlElement?)root.NotesMasterIdList ?? root.SlideMasterIdList);
        }

        var nextRelationshipId = NextRelationshipNumber(presentationPart);
        var tableStyleId = presentationPart.TableStylesPart?.TableStyleList?.Default?.Value;
        var styles = new StyleWriter(manifest.Styles);
        uint slideId = 256;
        var numbered = 0;
        foreach (var slide in presentation.Slides)
        {
            // A continuation slide goes on numbering where the slide before it ended.
            if (slide.Continuation is not { Index: > 1 })
            {
                numbered = 0;
            }

            var layout = template[slide.LayoutId];
            var relationshipId = $"rId{nextRelationshipId++}";
            var slidePart = presentationPart.AddNewPart<SlidePart>(relationshipId);
            slidePart.AddPart(layout.Part, "rId1");
            slidePart.Slide = SlideBuilder.Build(slide, layout, tableStyleId, styles, numbered + 1);
            numbered += SlideBuilder.NumberedItems(slide);
            slideIdList.Append(new P.SlideId { Id = slideId++, RelationshipId = relationshipId });
        }

        root.RemovePersonalInfoOnSave = null;
        root.Save();
        DocumentPropertiesWriter.Write(document, presentation, template);
        return [];
    }

    /// <summary>Defensive: the CLI checks the presentation against the manifest before rendering.</summary>
    private static IEnumerable<WorkLensError> CheckSlides(PresentationDocument presentation, IReadOnlyDictionary<string, TemplateLayout> template)
    {
        for (var i = 0; i < presentation.Slides.Count; i++)
        {
            var slide = presentation.Slides[i];
            if (!template.TryGetValue(slide.LayoutId, out var layout))
            {
                yield return new WorkLensError(
                    "presentation.unknown_layout",
                    $"Slide {slide.Id} uses layout '{slide.LayoutId}', which the manifest does not define.",
                    new Dictionary<string, string> { ["path"] = $"/slides/{i}/layout_id" });
                continue;
            }

            foreach (var slot in slide.Bindings.Keys)
            {
                if (!layout.Slots.ContainsKey(slot))
                {
                    yield return new WorkLensError(
                        "presentation.unknown_slot",
                        $"Slide {slide.Id} binds slot '{slot}', which layout '{slide.LayoutId}' does not define.",
                        new Dictionary<string, string> { ["path"] = $"/slides/{i}/bindings/{slot}" });
                }
            }
        }
    }

    /// <summary>The first unused <c>rId&lt;n&gt;</c> number of the presentation part's relationships.</summary>
    private static int NextRelationshipNumber(PresentationPart part)
    {
        var ids = part.Parts.Select(p => p.RelationshipId)
            .Concat(part.ExternalRelationships.Select(r => r.Id))
            .Concat(part.HyperlinkRelationships.Select(r => r.Id))
            .Concat(part.DataPartReferenceRelationships.Select(r => r.Id));
        var max = 0;
        foreach (var id in ids)
        {
            if (id.StartsWith("rId", StringComparison.Ordinal) && int.TryParse(id.AsSpan(3), out var n) && n > max)
            {
                max = n;
            }
        }

        return max + 1;
    }
}
