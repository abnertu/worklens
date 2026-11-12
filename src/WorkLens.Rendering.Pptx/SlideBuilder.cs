using DocumentFormat.OpenXml;
using WorkLens.Core.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace WorkLens.Rendering.Pptx;

/// <summary>
/// Builds one slide from its layout: a shape per bound slot (same <c>p:ph</c> as the layout placeholder, empty
/// <c>p:spPr</c> so position and formatting are inherited), plus copies of the layout's footer and slide number
/// placeholders. Shapes follow the layout's shape order; <c>cNvPr/@id</c> is numbered from 2.
/// </summary>
internal static class SlideBuilder
{
    private const string TableUri = "http://schemas.openxmlformats.org/drawingml/2006/table";

    /// <summary>Default row height (0.39 in); PowerPoint grows rows to fit their text.</summary>
    private const long RowHeight = 370840;

    public static P.Slide Build(Slide slide, TemplateLayout layout, string? tableStyleId)
    {
        var tree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties());

        uint nextId = 2;
        foreach (var shape in TemplateLayouts.Shapes(layout.Part.SlideLayout?.CommonSlideData?.ShapeTree))
        {
            if (shape.Placeholder is not { } placeholder)
            {
                continue;
            }

            foreach (var (slotId, bound) in layout.Slots)
            {
                if (!ReferenceEquals(bound.Shape, shape.Shape) || !slide.Bindings.TryGetValue(slotId, out var value))
                {
                    continue;
                }

                tree.Append(value switch
                {
                    TableSlotValue table => TableShape(nextId++, bound, table, tableStyleId),
                    TextSlotValue text => TextShape(nextId++, bound, TextParagraphs(text.Text)),
                    BulletsSlotValue bullets => TextShape(nextId++, bound, bullets.Items.Select(BulletParagraph)),
                    _ => throw new NotSupportedException($"Unsupported slot value {value.GetType().Name}."),
                });
            }

            if (TemplateLayouts.IsType(placeholder, P.PlaceholderValues.Footer)
                || TemplateLayouts.IsType(placeholder, P.PlaceholderValues.SlideNumber))
            {
                tree.Append(CopyLayoutShape(shape.Shape, nextId++));
            }
        }

        return new P.Slide(
            new P.CommonSlideData(tree),
            new P.ColorMapOverride(new A.MasterColorMapping()));
    }

    private static P.Shape TextShape(uint id, LayoutPlaceholder bound, IEnumerable<A.Paragraph> paragraphs)
    {
        var body = new P.TextBody(new A.BodyProperties(), new A.ListStyle());
        body.Append(paragraphs);
        if (!body.Elements<A.Paragraph>().Any())
        {
            // txBody needs at least one a:p; a hand-edited presentation may bind an empty bullet list.
            body.Append(new A.Paragraph());
        }

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = bound.Name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties(PlaceholderCopy(bound))),
            new P.ShapeProperties(),
            body);
    }

    private static P.GraphicFrame TableShape(uint id, LayoutPlaceholder bound, TableSlotValue value, string? tableStyleId)
    {
        var position = bound.Position
            ?? throw new InvalidOperationException($"Table placeholder '{bound.Name}' has no resolved position.");
        var columnCount = value.Columns.Count;

        var properties = new A.TableProperties { FirstRow = true, BandRow = true };
        if (tableStyleId is not null)
        {
            properties.Append(new A.TableStyleId(tableStyleId));
        }

        // Equal-width columns; the last column takes the rounding remainder so the grid fills the placeholder.
        var grid = new A.TableGrid();
        var width = position.Width / columnCount;
        for (var c = 0; c < columnCount; c++)
        {
            var w = c == columnCount - 1 ? position.Width - (width * (columnCount - 1)) : width;
            grid.Append(new A.GridColumn { Width = w });
        }

        var table = new A.Table(properties, grid);
        table.Append(TableRow(value.Columns));
        foreach (var row in value.Rows)
        {
            table.Append(TableRow(row));
        }

        return new P.GraphicFrame(
            new P.NonVisualGraphicFrameProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = bound.Name },
                new P.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties(PlaceholderCopy(bound))),
            new P.Transform(
                new A.Offset { X = position.X, Y = position.Y },
                new A.Extents { Cx = position.Width, Cy = position.Height }),
            new A.Graphic(new A.GraphicData(table) { Uri = TableUri }));
    }

    private static A.TableRow TableRow(IEnumerable<string> cells)
    {
        var row = new A.TableRow { Height = RowHeight };
        foreach (var cell in cells)
        {
            var body = new A.TextBody(new A.BodyProperties(), new A.ListStyle());
            body.Append(TextParagraphs(cell));
            row.Append(new A.TableCell(body, new A.TableCellProperties()));
        }

        return row;
    }

    /// <summary>One paragraph per <c>\n</c>-separated line; runs carry no <c>a:rPr</c> (P7).</summary>
    private static IEnumerable<A.Paragraph> TextParagraphs(string text) =>
        text.Split('\n').Select(line => line.Length == 0 ? new A.Paragraph() : new A.Paragraph(Run(line)));

    private static A.Paragraph BulletParagraph(BulletItem item)
    {
        var paragraph = new A.Paragraph();
        if (item.Level > 0)
        {
            paragraph.Append(new A.ParagraphProperties { Level = item.Level });
        }

        paragraph.Append(Run(item.Text));
        return paragraph;
    }

    private static A.Run Run(string text) => new(new A.Text(text));

    private static P.PlaceholderShape PlaceholderCopy(LayoutPlaceholder bound)
    {
        var copy = (P.PlaceholderShape)bound.Placeholder.CloneNode(true);
        copy.HasCustomPrompt = null;
        return copy;
    }

    /// <summary>Copies a footer / slide number placeholder with its text and fields, renumbered, without extensions.</summary>
    private static OpenXmlElement CopyLayoutShape(OpenXmlElement shape, uint id)
    {
        var copy = shape.CloneNode(true);
        var drawing = copy.FirstChild!.GetFirstChild<P.NonVisualDrawingProperties>()!;
        drawing.Id = id;
        drawing.RemoveAllChildren<A.NonVisualDrawingPropertiesExtensionList>();
        return copy;
    }
}
