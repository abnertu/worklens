using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using WorkLens.Core.Json;
using WorkLens.Core.Presentation;
using WorkLens.Core.Templates;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using PackageDocument = DocumentFormat.OpenXml.Packaging.PresentationDocument;
using PresentationDocument = WorkLens.Core.Presentation.PresentationDocument;

namespace WorkLens.Rendering.Pptx.Tests;

/// <summary>The renderer applies the rd-monthly style roles, column widths and table style (ADR-0010).</summary>
public sealed class RdMonthlyRenderTests : IDisposable
{
    private static readonly string TemplateDirectory = Path.Combine(AppContext.BaseDirectory, "templates", "rd-monthly");

    private readonly string _directory = Directory.CreateTempSubdirectory("worklens-rd-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), WorkLensJson.Options)!;

    private static TemplateManifest Manifest => Load<TemplateManifest>(Path.Combine(TemplateDirectory, "manifest.json"));

    private static PresentationDocument Golden(string name) =>
        Load<PresentationDocument>(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rd-monthly", name, "presentation.json"));

    private string Render(string name, TemplateManifest? manifest = null)
    {
        var path = Path.Combine(_directory, $"{name}.pptx");
        var result = PptxRenderer.Render(Golden(name), manifest ?? Manifest, Path.Combine(TemplateDirectory, "rd-monthly.pptx"), path);
        Assert.Empty(result.Errors);
        return path;
    }

    /// <summary>The slides of a rendered file whose layout has the name, in deck order.</summary>
    private static List<P.Slide> Slides(PackageDocument document, string layoutName)
    {
        var part = document.PresentationPart!;
        return part.Presentation!.SlideIdList!.Elements<P.SlideId>()
            .Select(id => (SlidePart)part.GetPartById(id.RelationshipId!.Value!))
            .Where(p => p.SlideLayoutPart?.SlideLayout?.CommonSlideData?.Name?.Value == layoutName)
            .Select(p => p.Slide!)
            .ToList();
    }

    private static P.Shape ShapeNamed(P.Slide slide, string name) =>
        slide.Descendants<P.Shape>().Single(s => s.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value == name);

    private static string? FillOf(OpenXmlElement properties) =>
        properties.GetFirstChild<A.SolidFill>()?.GetFirstChild<A.RgbColorModelHex>()?.Val?.Value;

    private static string? ColorOf(A.RunProperties? properties) =>
        properties?.GetFirstChild<A.SolidFill>()?.GetFirstChild<A.RgbColorModelHex>()?.Val?.Value;

    private static A.Table TableOf(P.Slide slide) => slide.Descendants<A.Table>().Single();

    [Theory]
    [InlineData("full")]
    [InlineData("empty")]
    public void Output_is_valid_open_xml(string name)
    {
        using var document = PackageDocument.Open(Render(name), isEditable: false);

        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2016).Validate(document, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Table_header_and_rows_take_their_styles()
    {
        using var document = PackageDocument.Open(Render("full"), isEditable: false);
        var rows = TableOf(Slides(document, "Table")[0]).Elements<A.TableRow>().ToList();

        var header = rows[0].Elements<A.TableCell>().First();
        var line = header.TableCellProperties!.GetFirstChild<A.BottomBorderLineProperties>()!;
        Assert.Equal(19050, line.Width!.Value);
        Assert.Equal("1E3A8A", line.GetFirstChild<A.SolidFill>()!.GetFirstChild<A.RgbColorModelHex>()!.Val!.Value);
        var headerRun = header.Descendants<A.Run>().First().RunProperties!;
        Assert.Equal(1200, headerRun.FontSize!.Value);
        Assert.Equal("4B5563", ColorOf(headerRun));

        // Rows alternate table_row and table_row_alt; only the alternate row has a fill.
        Assert.Null(FillOf(rows[1].Elements<A.TableCell>().First().TableCellProperties!));
        Assert.Equal("F6F7F9", FillOf(rows[2].Elements<A.TableCell>().First().TableCellProperties!));
    }

    // A status cell takes its tag style on top of the row style: a square fill and the tag text color (D30).
    [Fact]
    public void Status_cell_takes_its_tag_style_over_the_row_style()
    {
        using var document = PackageDocument.Open(Render("full"), isEditable: false);
        var status = TableOf(Slides(document, "Table")[0]).Elements<A.TableRow>().ElementAt(1).Elements<A.TableCell>().ElementAt(2);

        Assert.Equal("E8EDF8", FillOf(status.TableCellProperties!));
        var run = status.Descendants<A.Run>().First().RunProperties!;
        Assert.Equal("1E3A8A", ColorOf(run));
        Assert.Equal(1050, run.FontSize!.Value);
        Assert.NotNull(status.TableCellProperties!.GetFirstChild<A.BottomBorderLineProperties>());
    }

    [Fact]
    public void Columns_take_the_slot_widths_and_the_table_takes_the_slot_style()
    {
        using var document = PackageDocument.Open(Render("full"), isEditable: false);
        var table = TableOf(Slides(document, "Table")[0]);

        var widths = table.TableGrid!.Elements<A.GridColumn>().Select(c => c.Width!.Value).ToList();
        var total = widths.Sum();
        Assert.Equal([42, 18, 12, 28], widths.Select(w => (int)Math.Round(100.0 * w / total)));
        Assert.Equal("{2D5ABB26-0587-4C30-8999-92F81FD0307C}", table.TableProperties!.GetFirstChild<A.TableStyleId>()!.Text);
    }

    [Fact]
    public void Metric_card_has_label_value_and_caption_with_their_roles()
    {
        using var document = PackageDocument.Open(Render("full"), isEditable: false);
        var card = ShapeNamed(Slides(document, "Summary")[0], "Metric Completed");

        var runs = card.TextBody!.Elements<A.Paragraph>().Select(p => p.Elements<A.Run>().Single()).ToList();
        Assert.Equal(3, runs.Count);
        Assert.Equal(1200, runs[0].RunProperties!.FontSize!.Value);
        Assert.Equal(3600, runs[1].RunProperties!.FontSize!.Value);
        Assert.True(runs[1].RunProperties!.Bold!.Value);
        Assert.Equal("4B5563", ColorOf(runs[2].RunProperties));
    }

    [Fact]
    public void Support_block_takes_its_fill_when_there_is_support()
    {
        using var document = PackageDocument.Open(Render("full"), isEditable: false);
        var summary = Slides(document, "Summary")[0];

        Assert.Equal("FDF3E7", FillOf(ShapeNamed(summary, "Support").ShapeProperties!));
        var title = ShapeNamed(summary, "Support Title");
        Assert.Equal("FDF3E7", FillOf(title.ShapeProperties!));
        Assert.Equal("9A3412", ColorOf(title.Descendants<A.Run>().Single().RunProperties));
    }

    [Fact]
    public void Evidence_id_is_bold_accent()
    {
        using var document = PackageDocument.Open(Render("full"), isEditable: false);
        var evidence = ShapeNamed(Slides(document, "Detail")[0], "Evidence");

        var id = evidence.TextBody!.Elements<A.Paragraph>().First().Elements<A.Run>().Single().RunProperties!;
        Assert.True(id.Bold!.Value);
        Assert.Equal("1E3A8A", ColorOf(id));
    }

    // The number takes the numbered color; the item text keeps the placeholder format (D76).
    [Fact]
    public void Numbered_items_number_in_the_role_color_and_keep_their_text_format()
    {
        using var document = PackageDocument.Open(Render("full"), isEditable: false);
        var body = Slides(document, "Narrative")
            .Select(s => ShapeNamed(s, "Body"))
            .First(b => b.Descendants<A.AutoNumberedBullet>().Any());

        var paragraph = body.TextBody!.Elements<A.Paragraph>().First();
        Assert.Equal(A.TextAutoNumberSchemeValues.ArabicPeriod, paragraph.ParagraphProperties!.GetFirstChild<A.AutoNumberedBullet>()!.Type!.Value);
        Assert.Equal("1E3A8A", paragraph.ParagraphProperties.GetFirstChild<A.BulletColor>()!.GetFirstChild<A.RgbColorModelHex>()!.Val!.Value);
        Assert.Null(paragraph.Elements<A.Run>().Single().RunProperties);
    }

    // The full fixture has 6 next plan items, split 5 + 1: the continuation slide numbers its item 6.
    [Fact]
    public void Numbered_items_go_on_numbering_on_a_continuation_slide()
    {
        using var document = PackageDocument.Open(Render("full"), isEditable: false);
        var numbered = Slides(document, "Narrative")
            .Select(s => ShapeNamed(s, "Body").Descendants<A.AutoNumberedBullet>().ToList())
            .Where(n => n.Count > 0)
            .ToList();

        Assert.Equal(2, numbered.Count);
        Assert.All(numbered[0], n => Assert.Null(n.StartAt));
        Assert.Equal(6, Assert.Single(numbered[1]).StartAt!.Value);
    }

    // A role that the manifest does not define gives no format, so the template format stays.
    [Fact]
    public void Role_missing_from_the_styles_gives_no_format()
    {
        var manifest = Manifest with { Styles = Manifest.Styles!.Where(s => s.Key != "evidence_id").ToDictionary(s => s.Key, s => s.Value) };
        using var document = PackageDocument.Open(Render("full", manifest), isEditable: false);
        var evidence = ShapeNamed(Slides(document, "Detail")[0], "Evidence");

        Assert.Null(evidence.TextBody!.Elements<A.Paragraph>().First().Elements<A.Run>().Single().RunProperties);
    }

    private sealed record UnknownSlotValue : SlotValue;

    // The presentation was checked before rendering, so an unknown value is a program error, not a bad template.
    [Fact]
    public void Unknown_slot_value_is_not_reported_as_a_template_fault()
    {
        var golden = Golden("empty");
        var slide = golden.Slides[0];
        var bindings = slide.Bindings.ToDictionary(b => b.Key, b => b.Value);
        bindings["title"] = new UnknownSlotValue();
        var presentation = golden with { Slides = [slide with { Bindings = bindings }, .. golden.Slides.Skip(1)] };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PptxRenderer.Render(presentation, Manifest, Path.Combine(TemplateDirectory, "rd-monthly.pptx"), Path.Combine(_directory, "x.pptx")));
    }
}
