using DocumentFormat.OpenXml.Packaging;
using WorkLens.Core.Presentation;
using WorkLens.Core.Templates;
using Ap = DocumentFormat.OpenXml.ExtendedProperties;

namespace WorkLens.Rendering.Pptx;

/// <summary>Removes template content that must not reach a report (D12, security-privacy section 8).</summary>
internal static class TemplateCleaner
{
    /// <summary>
    /// Deletes the template's slides with their notes and comments, the <c>sldIdLst</c> entries, comment authors,
    /// custom properties and the thumbnail (which shows a template slide).
    /// </summary>
    public static void RemoveSlidesAndPersonalParts(
        DocumentFormat.OpenXml.Packaging.PresentationDocument document,
        PresentationPart presentation)
    {
        var slideIds = presentation.Presentation?.SlideIdList;
        foreach (var slidePart in presentation.SlideParts.ToList())
        {
            if (slidePart.NotesSlidePart is { } notes)
            {
                slidePart.DeletePart(notes);
            }

            presentation.DeletePart(slidePart);
        }

        slideIds?.RemoveAllChildren();

        if (presentation.CommentAuthorsPart is { } authors)
        {
            presentation.DeletePart(authors);
        }

        if (presentation.authorsPart is { } modernAuthors)
        {
            presentation.DeletePart(modernAuthors);
        }

        if (document.CustomFilePropertiesPart is { } custom)
        {
            document.DeletePart(custom);
        }

        if (document.ThumbnailPart is { } thumbnail)
        {
            document.DeletePart(thumbnail);
        }
    }
}

/// <summary>Rewrites core and app properties so they carry nothing from the template or its author (D12).</summary>
internal static class DocumentPropertiesWriter
{
    public const string Application = "WorkLens";

    public static void Write(
        DocumentFormat.OpenXml.Packaging.PresentationDocument document,
        WorkLens.Core.Presentation.PresentationDocument presentation,
        IReadOnlyDictionary<string, TemplateLayout> layouts)
    {
        var properties = document.PackageProperties;
        properties.Title = CoverTitle(presentation, layouts) ?? presentation.PresentationId;
        properties.Creator = Application;
        properties.LastModifiedBy = null;
        properties.Created = null;
        properties.Modified = null;
        properties.Revision = null;
        properties.LastPrinted = null;
        properties.Subject = null;
        properties.Keywords = null;
        properties.Description = null;
        properties.Category = null;

        var app = document.ExtendedFilePropertiesPart ?? document.AddExtendedFilePropertiesPart();
        app.Properties = new Ap.Properties(
            new Ap.Slides(presentation.Slides.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Ap.Application(Application));
        app.Properties.Save();
    }

    private static string? CoverTitle(
        WorkLens.Core.Presentation.PresentationDocument presentation,
        IReadOnlyDictionary<string, TemplateLayout> layouts) =>
        presentation.Slides
            .Where(s => layouts[s.LayoutId].Definition.Purpose == LayoutPurpose.Cover)
            .Select(s => s.Bindings.GetValueOrDefault("title") as TextSlotValue)
            .FirstOrDefault(t => t is not null)?.Text;
}
