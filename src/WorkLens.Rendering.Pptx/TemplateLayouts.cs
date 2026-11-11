using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WorkLens.Core.Errors;
using WorkLens.Core.Templates;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace WorkLens.Rendering.Pptx;

/// <summary>A manifest layout matched to its slide layout part, with each slot's layout placeholder.</summary>
internal sealed record TemplateLayout(
    LayoutDefinition Definition,
    SlideLayoutPart Part,
    IReadOnlyDictionary<string, LayoutPlaceholder> Slots);

/// <param name="Shape">The layout's placeholder shape (<c>p:sp</c>, <c>p:graphicFrame</c> or <c>p:pic</c>).</param>
/// <param name="Position">The placeholder's position for a table slot (layout, else master); otherwise <c>null</c>.</param>
internal sealed record LayoutPlaceholder(OpenXmlElement Shape, P.PlaceholderShape Placeholder, string Name, Position? Position);

internal readonly record struct Position(long X, long Y, long Width, long Height);

internal static class TemplateLayouts
{
    /// <summary>
    /// Matches every manifest layout and slot to the template (all of them, not only those used by the presentation,
    /// so that a manifest that does not fit its template fails the same way for every report).
    /// </summary>
    public static IReadOnlyDictionary<string, TemplateLayout> Resolve(
        PresentationPart presentation,
        TemplateManifest manifest,
        out List<WorkLensError> errors)
    {
        errors = [];
        var layoutParts = presentation.SlideMasterParts
            .SelectMany(m => m.SlideLayoutParts)
            .ToList();
        var result = new Dictionary<string, TemplateLayout>(StringComparer.Ordinal);

        for (var i = 0; i < manifest.Layouts.Count; i++)
        {
            var definition = manifest.Layouts[i];
            var part = layoutParts.FirstOrDefault(p =>
                p.SlideLayout?.CommonSlideData?.Name?.Value == definition.SlideLayoutName);
            if (part is null)
            {
                errors.Add(Error(
                    "template.layout_missing",
                    $"The template has no slide layout named '{definition.SlideLayoutName}' (layout '{definition.Id}').",
                    $"/layouts/{i}/slide_layout_name"));
                continue;
            }

            var slots = new Dictionary<string, LayoutPlaceholder>(StringComparer.Ordinal);
            for (var j = 0; j < definition.Slots.Count; j++)
            {
                var slot = definition.Slots[j];
                var path = $"/layouts/{i}/slots/{j}/placeholder";
                var match = Shapes(part.SlideLayout?.CommonSlideData?.ShapeTree)
                    .FirstOrDefault(s => Matches(slot.Placeholder, s));
                if (match.Placeholder is null)
                {
                    errors.Add(Error(
                        "template.placeholder_missing",
                        $"Slide layout '{definition.SlideLayoutName}' has no placeholder {Describe(slot.Placeholder)} for slot '{slot.Id}'.",
                        path));
                    continue;
                }

                Position? position = null;
                if (slot.Kind == SlotKind.Table)
                {
                    position = PositionOf(match.Shape) ?? MasterPosition(part, match.Placeholder);
                    if (position is null)
                    {
                        errors.Add(Error(
                            "template.placeholder_position_missing",
                            $"Placeholder {Describe(slot.Placeholder)} of slide layout '{definition.SlideLayoutName}' has no position in the layout or the master; a table needs one.",
                            path));
                        continue;
                    }
                }

                slots[slot.Id] = new LayoutPlaceholder(match.Shape, match.Placeholder, match.Name, position);
            }

            result[definition.Id] = new TemplateLayout(definition, part, slots);
        }

        return result;
    }

    /// <summary>The top-level shapes of a shape tree with their non-visual name and placeholder (if any).</summary>
    public static IEnumerable<ShapeInfo> Shapes(P.ShapeTree? tree)
    {
        if (tree is null)
        {
            yield break;
        }

        foreach (var element in tree.ChildElements)
        {
            // p:sp, p:graphicFrame, p:pic, p:grpSp, p:cxnSp all start with their nv*Pr element:
            // (p:cNvPr, p:cNv*Pr, p:nvPr).
            var nonVisual = element.FirstChild;
            if (nonVisual is null || element is P.NonVisualGroupShapeProperties or P.GroupShapeProperties)
            {
                continue;
            }

            var drawing = nonVisual.GetFirstChild<P.NonVisualDrawingProperties>();
            var placeholder = nonVisual.GetFirstChild<P.ApplicationNonVisualDrawingProperties>()?.PlaceholderShape;
            if (drawing is null)
            {
                continue;
            }

            yield return new ShapeInfo(element, drawing, placeholder, drawing.Name?.Value ?? string.Empty);
        }
    }

    internal readonly record struct ShapeInfo(
        OpenXmlElement Shape,
        P.NonVisualDrawingProperties Drawing,
        P.PlaceholderShape? Placeholder,
        string Name);

    public static bool IsType(P.PlaceholderShape placeholder, P.PlaceholderValues type) =>
        placeholder.Type is { } t && t.Value == type;

    private static bool Matches(PlaceholderRef reference, ShapeInfo shape)
    {
        if (shape.Placeholder is not { } ph)
        {
            return false;
        }

        if (reference.Idx is { } idx)
        {
            var matchesIdx = ph.Index?.Value is { } index
                ? index == (uint)idx
                : idx == 0 && (IsType(ph, P.PlaceholderValues.Title) || IsType(ph, P.PlaceholderValues.CenteredTitle));
            if (!matchesIdx)
            {
                return false;
            }
        }

        return reference.Name is null || string.Equals(reference.Name, shape.Name, StringComparison.Ordinal);
    }

    private static Position? PositionOf(OpenXmlElement shape)
    {
        var (offset, extents) = shape switch
        {
            P.Shape s => (s.ShapeProperties?.Transform2D?.Offset, s.ShapeProperties?.Transform2D?.Extents),
            P.GraphicFrame g => (g.Transform?.Offset, g.Transform?.Extents),
            P.Picture p => (p.ShapeProperties?.Transform2D?.Offset, p.ShapeProperties?.Transform2D?.Extents),
            _ => ((A.Offset?)null, (A.Extents?)null),
        };

        return offset?.X is { } x && offset.Y is { } y && extents?.Cx is { } cx && extents.Cy is { } cy
            ? new Position(x.Value, y.Value, cx.Value, cy.Value)
            : null;
    }

    /// <summary>
    /// The master placeholder a layout placeholder inherits from: <c>title</c> for title / ctrTitle, <c>body</c>
    /// otherwise (obj, body, tbl or no type).
    /// </summary>
    private static Position? MasterPosition(SlideLayoutPart layout, P.PlaceholderShape placeholder)
    {
        var masterType = IsType(placeholder, P.PlaceholderValues.Title) || IsType(placeholder, P.PlaceholderValues.CenteredTitle)
            ? P.PlaceholderValues.Title
            : P.PlaceholderValues.Body;
        var master = layout.SlideMasterPart?.SlideMaster?.CommonSlideData?.ShapeTree;
        var shape = Shapes(master).FirstOrDefault(s => s.Placeholder is { } ph && IsType(ph, masterType));
        return shape.Placeholder is null ? null : PositionOf(shape.Shape);
    }

    private static string Describe(PlaceholderRef reference) =>
        (reference.Idx, reference.Name) switch
        {
            ({ } idx, { } name) => $"idx {idx} named '{name}'",
            ({ } idx, null) => $"idx {idx}",
            (null, { } name) => $"named '{name}'",
            _ => "(no idx or name)",
        };

    private static WorkLensError Error(string code, string message, string path) =>
        new(code, message, new Dictionary<string, string> { ["path"] = path });
}
