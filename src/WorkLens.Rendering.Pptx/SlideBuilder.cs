using DocumentFormat.OpenXml;
using WorkLens.Core.Contracts;
using WorkLens.Core.Presentation;
using WorkLens.Core.Templates;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace WorkLens.Rendering.Pptx;

/// <summary>
/// Builds one slide from its layout: a shape per bound slot (same <c>p:ph</c> as the layout placeholder, empty
/// <c>p:spPr</c> so position and formatting are inherited), plus copies of the layout's footer and slide number
/// placeholders. Shapes follow the layout's shape order; <c>cNvPr/@id</c> is numbered from 2. Formats come only from
/// the manifest style roles that the presentation names (ADR-0010); without roles, runs carry no <c>a:rPr</c>.
/// </summary>
internal static class SlideBuilder
{
    private const string TableUri = "http://schemas.openxmlformats.org/drawingml/2006/table";

    /// <summary>Default row height (0.39 in); PowerPoint grows rows to fit their text.</summary>
    private const long RowHeight = 370840;

    /// <summary>The style roles of the three parts of a metric card.</summary>
    private const string MetricLabelRole = "metric_label";
    private const string MetricValueRole = "metric_value";
    private const string MetricCaptionRole = "metric_caption";

    /// <summary>Items with this role are numbered; the role formats the number, not the text.</summary>
    private const string NumberedRole = "numbered";

    /// <param name="numberStart">The number of the first numbered item on the slide (greater than 1 on a continuation).</param>
    /// <param name="brand">The brand values and the footer text of the render.</param>
    /// <param name="addImage">Adds an image part to the slide and returns its relationship id.</param>
    public static P.Slide Build(
        Slide slide,
        TemplateLayout layout,
        string? defaultTableStyleId,
        StyleWriter styles,
        int numberStart,
        BrandWriter brand,
        Func<LogoImage, string> addImage)
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

                var slot = layout.Definition.Slots.First(s => s.Id == slotId);
                var style = styles.Find(value.Style);
                tree.Append(value switch
                {
                    TableSlotValue table => TableShape(nextId++, bound, slot, table, slot.TableStyle ?? defaultTableStyleId, styles),
                    TextSlotValue text => TextShape(nextId++, bound, style, TextParagraphs(text.Text, StyleWriter.RunProperties(style))),
                    BulletsSlotValue bullets => TextShape(nextId++, bound, style, bullets.Items.Select(i => BulletParagraph(i, style, styles, numberStart))),
                    MetricSlotValue metric => TextShape(nextId++, bound, style, MetricParagraphs(metric, style, styles)),

                    // Not a template fault: the presentation was checked before rendering, so this is a program error.
                    _ => throw new ArgumentOutOfRangeException(nameof(slide), $"Unsupported slot value {value.GetType().Name}."),
                });
            }

            var brandPositions = layout.Definition.Brand;
            if (brandPositions?.Logo is { } logoPosition && brand.Logo is { } logo && TemplateLayouts.Matches(logoPosition.Placeholder, shape)
                && TemplateLayouts.PositionOf(shape.Shape) is { } box)
            {
                tree.Append(Picture(nextId++, shape.Name, placeholder, logo, addImage(logo), box));
                brand.LogoWritten();
            }

            if (brandPositions?.UnitName is { } unitPosition && TemplateLayouts.Matches(unitPosition.Placeholder, shape)
                && brand.UnitName(unitPosition.MaxChars) is { } unit)
            {
                var bound = new LayoutPlaceholder(shape.Shape, placeholder, shape.Name, null);
                tree.Append(TextShape(nextId++, bound, null, TextParagraphs(unit, null)));
            }

            if (TemplateLayouts.IsType(placeholder, P.PlaceholderValues.Footer))
            {
                var footer = CopyLayoutShape(shape.Shape, nextId++);
                if (brand.FooterText is { } text)
                {
                    BrandWriter.SetText(footer, text);
                    brand.FooterWritten();
                }

                tree.Append(footer);
            }
            else if (TemplateLayouts.IsType(placeholder, P.PlaceholderValues.SlideNumber))
            {
                tree.Append(CopyLayoutShape(shape.Shape, nextId++));
            }
        }

        return new P.Slide(
            new P.CommonSlideData(tree),
            new P.ColorMapOverride(new A.MasterColorMapping()));
    }

    private static P.Shape TextShape(uint id, LayoutPlaceholder bound, StyleDefinition? style, IEnumerable<A.Paragraph> paragraphs)
    {
        var body = new P.TextBody(new A.BodyProperties(), new A.ListStyle());
        body.Append(paragraphs);
        if (!body.Elements<A.Paragraph>().Any())
        {
            // txBody needs at least one a:p; a hand-edited presentation may bind an empty bullet list.
            body.Append(new A.Paragraph());
        }

        var properties = new P.ShapeProperties();
        if (StyleWriter.Fill(style) is { } fill)
        {
            properties.Append(fill);
        }

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = bound.Name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties(PlaceholderCopy(bound))),
            properties,
            body);
    }

    private static P.GraphicFrame TableShape(
        uint id, LayoutPlaceholder bound, SlotDefinition slot, TableSlotValue value, string? tableStyleId, StyleWriter styles)
    {
        var position = bound.Position
            ?? throw new InvalidOperationException($"Table placeholder '{bound.Name}' has no resolved position.");
        var columnCount = value.Columns.Count;

        var properties = new A.TableProperties { FirstRow = true, BandRow = true };
        if (tableStyleId is not null)
        {
            properties.Append(new A.TableStyleId(tableStyleId));
        }

        // Column widths: the percentages of the slot columns, else equal widths. The last column takes the rounding
        // remainder so the grid fills the placeholder.
        var grid = new A.TableGrid();
        var percents = slot.Columns is { } columns && columns.Count == columnCount ? columns.Select(c => c.Width).ToList() : null;
        long used = 0;
        for (var c = 0; c < columnCount; c++)
        {
            var w = c == columnCount - 1
                ? position.Width - used
                : percents is null ? position.Width / columnCount : position.Width * percents[c] / 100;
            used += w;
            grid.Append(new A.GridColumn { Width = w });
        }

        var table = new A.Table(properties, grid);
        table.Append(TableRow(value.Columns, styles.Find(slot.HeaderStyle), null, styles));
        var rowStyles = slot.RowStyles ?? [];
        for (var r = 0; r < value.Rows.Count; r++)
        {
            var rowStyle = rowStyles.Count > 0 ? styles.Find(rowStyles[r % rowStyles.Count]) : null;
            table.Append(TableRow(value.Rows[r], rowStyle, value.CellStyles?[r], styles));
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

    /// <summary>A row whose cells take the row style, each with its own cell style on top (status tags).</summary>
    private static A.TableRow TableRow(
        IReadOnlyList<string> cells, StyleDefinition? rowStyle, IReadOnlyList<string?>? cellStyles, StyleWriter styles)
    {
        var row = new A.TableRow { Height = RowHeight };
        for (var c = 0; c < cells.Count; c++)
        {
            var style = StyleWriter.Merge(rowStyle, styles.Find(cellStyles?[c]));
            var body = new A.TextBody(new A.BodyProperties(), new A.ListStyle());
            body.Append(TextParagraphs(cells[c], StyleWriter.RunProperties(style)));
            row.Append(new A.TableCell(body, StyleWriter.CellProperties(style)));
        }

        return row;
    }

    /// <summary>One paragraph per <c>\n</c>-separated line; runs carry <c>a:rPr</c> only when a style sets one.</summary>
    private static IEnumerable<A.Paragraph> TextParagraphs(string text, A.RunProperties? runProperties) =>
        text.Split('\n').Select(line => line.Length == 0 ? new A.Paragraph() : new A.Paragraph(Run(line, runProperties)));

    /// <summary>The numbered items that a slide shows, so that its continuation goes on from them.</summary>
    public static int NumberedItems(Slide slide) =>
        slide.Bindings.Values.OfType<BulletsSlotValue>().Sum(b => b.Items.Count(i => i.Style == NumberedRole));

    private static A.Paragraph BulletParagraph(BulletItem item, StyleDefinition? slotStyle, StyleWriter styles, int numberStart)
    {
        var paragraph = new A.Paragraph();
        var numbered = item.Style == NumberedRole;
        var itemStyle = numbered ? null : styles.Find(item.Style);
        if (item.Level > 0 || numbered)
        {
            var properties = new A.ParagraphProperties();
            if (item.Level > 0)
            {
                properties.Level = item.Level;
            }

            if (numbered)
            {
                // The number takes the role's color; the item text keeps the placeholder's body format.
                if (styles.Find(NumberedRole)?.Color is { } color)
                {
                    properties.Append(new A.BulletColor(new A.RgbColorModelHex { Val = color.TrimStart('#').ToUpperInvariant() }));
                }

                var number = new A.AutoNumberedBullet { Type = A.TextAutoNumberSchemeValues.ArabicPeriod };
                if (numberStart > 1)
                {
                    number.StartAt = numberStart;
                }

                properties.Append(number);
            }

            paragraph.Append(properties);
        }

        paragraph.Append(Run(item.Text, StyleWriter.RunProperties(StyleWriter.Merge(slotStyle, itemStyle))));
        return paragraph;
    }

    /// <summary>The label, the value and the caption of a metric card, each a paragraph with its own role.</summary>
    private static IEnumerable<A.Paragraph> MetricParagraphs(MetricSlotValue metric, StyleDefinition? slotStyle, StyleWriter styles)
    {
        A.Paragraph Part(string text, string role) =>
            new(Run(text, StyleWriter.RunProperties(StyleWriter.Merge(slotStyle, styles.Find(role)))));

        yield return Part(metric.Label, MetricLabelRole);
        yield return Part(metric.Value, MetricValueRole);
        if (metric.Caption is { } caption)
        {
            yield return Part(caption, MetricCaptionRole);
        }
    }

    private static A.Run Run(string text, A.RunProperties? runProperties) =>
        runProperties is null ? new A.Run(new A.Text(text)) : new A.Run((A.RunProperties)runProperties.CloneNode(true), new A.Text(text));

    private static P.PlaceholderShape PlaceholderCopy(LayoutPlaceholder bound) => PlaceholderCopy(bound.Placeholder);

    private static P.PlaceholderShape PlaceholderCopy(P.PlaceholderShape placeholder)
    {
        var copy = (P.PlaceholderShape)placeholder.CloneNode(true);
        copy.HasCustomPrompt = null;
        return copy;
    }

    /// <summary>The logo, scaled into the placeholder box with its aspect ratio kept, and centered in it.</summary>
    private static P.Picture Picture(uint id, string name, P.PlaceholderShape placeholder, LogoImage logo, string relationshipId, Position box)
    {
        var scale = Math.Min((double)box.Width / logo.Width, (double)box.Height / logo.Height);
        var width = (long)Math.Round(logo.Width * scale);
        var height = (long)Math.Round(logo.Height * scale);
        return new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoGrouping = true, NoChangeAspect = true }),
                new P.ApplicationNonVisualDrawingProperties(PlaceholderCopy(placeholder))),
            new P.BlipFill(new A.Blip { Embed = relationshipId }, new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(new A.Transform2D(
                new A.Offset { X = box.X + ((box.Width - width) / 2), Y = box.Y + ((box.Height - height) / 2) },
                new A.Extents { Cx = width, Cy = height })));
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
