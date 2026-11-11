using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace WorkLens.Rendering.Pptx.Tests;

/// <summary>
/// Normalized, comparable view of a rendered .pptx (P10): per slide its layout name and shapes (cNvPr id and name,
/// placeholder type / idx, paragraphs with level and text, tables), plus the document properties. Contains no
/// relationship ids, part URIs or times, which may legitimately differ between equivalent packages.
/// </summary>
internal static class PptxInspector
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n" };

    public static string ToJson(string path)
    {
        using var document = PresentationDocument.Open(path, isEditable: false);
        return Inspect(document).ToJsonString(Indented) + "\n";
    }

    public static JsonObject Inspect(PresentationDocument document)
    {
        var presentationPart = document.PresentationPart!;
        var slides = new JsonArray();
        foreach (var slideId in presentationPart.Presentation!.SlideIdList!.Elements<P.SlideId>())
        {
            var slidePart = (SlidePart)presentationPart.GetPartById(slideId.RelationshipId!.Value!);
            var shapes = new JsonArray();
            foreach (var element in slidePart.Slide!.CommonSlideData!.ShapeTree!.ChildElements)
            {
                if (element is P.NonVisualGroupShapeProperties or P.GroupShapeProperties)
                {
                    continue;
                }

                shapes.Add(Shape(element));
            }

            slides.Add(new JsonObject
            {
                ["slide_id"] = slideId.Id!.Value,
                ["layout"] = slidePart.SlideLayoutPart!.SlideLayout!.CommonSlideData!.Name!.Value,
                ["shapes"] = shapes,
            });
        }

        var core = document.PackageProperties;
        var app = document.ExtendedFilePropertiesPart?.Properties;
        return new JsonObject
        {
            ["slides"] = slides,
            ["core"] = new JsonObject
            {
                ["title"] = core.Title,
                ["creator"] = core.Creator,
                ["last_modified_by"] = core.LastModifiedBy,
                ["revision"] = core.Revision,
                ["has_created"] = core.Created is not null,
                ["has_modified"] = core.Modified is not null,
            },
            ["app"] = app is null ? null : new JsonArray([.. app.ChildElements.Select(e => (JsonNode)$"{e.LocalName}={e.InnerText}")]),
        };
    }

    private static JsonObject Shape(DocumentFormat.OpenXml.OpenXmlElement element)
    {
        var nonVisual = element.FirstChild!;
        var drawing = nonVisual.GetFirstChild<P.NonVisualDrawingProperties>()!;
        var placeholder = nonVisual.GetFirstChild<P.ApplicationNonVisualDrawingProperties>()?.PlaceholderShape;
        var shape = new JsonObject
        {
            ["element"] = element.LocalName,
            ["id"] = drawing.Id!.Value,
            ["name"] = drawing.Name?.Value,
            ["placeholder"] = placeholder is null
                ? null
                : new JsonObject { ["type"] = placeholder.Type?.InnerText, ["idx"] = placeholder.Index?.Value },
        };

        if (element is P.Shape { TextBody: { } body })
        {
            shape["paragraphs"] = Paragraphs(body.Elements<A.Paragraph>());
        }

        if (element.Descendants<A.Table>().FirstOrDefault() is { } table)
        {
            shape["table"] = new JsonObject
            {
                ["style"] = table.TableProperties?.GetFirstChild<A.TableStyleId>()?.Text,
                ["first_row"] = table.TableProperties?.FirstRow?.Value,
                ["band_row"] = table.TableProperties?.BandRow?.Value,
                ["grid"] = new JsonArray([.. table.TableGrid!.Elements<A.GridColumn>().Select(g => (JsonNode)g.Width!.Value)]),
                ["rows"] = new JsonArray([.. table.Elements<A.TableRow>().Select(r => (JsonNode)new JsonArray(
                    [.. r.Elements<A.TableCell>().Select(c => (JsonNode)Paragraphs(c.TextBody!.Elements<A.Paragraph>()))]))]),
            };
        }

        return shape;
    }

    private static JsonArray Paragraphs(IEnumerable<A.Paragraph> paragraphs) =>
        new([.. paragraphs.Select(p =>
        {
            var paragraph = new JsonObject
            {
                ["level"] = p.ParagraphProperties?.Level?.Value ?? 0,
                ["text"] = string.Concat(p.Descendants<A.Text>().Select(t => t.Text)),
            };
            if (p.Elements<A.Field>().FirstOrDefault() is { } field)
            {
                paragraph["field"] = field.Type?.Value;
            }

            return (JsonNode)paragraph;
        })]);
}
