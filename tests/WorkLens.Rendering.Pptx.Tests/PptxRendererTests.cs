using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using WorkLens.Core.Evidence;
using WorkLens.Core.Json;
using WorkLens.Core.Layout;
using WorkLens.Core.Presentation;
using WorkLens.Core.Report;
using WorkLens.Core.Templates;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using PackageDocument = DocumentFormat.OpenXml.Packaging.PresentationDocument;
using PresentationDocument = WorkLens.Core.Presentation.PresentationDocument;

namespace WorkLens.Rendering.Pptx.Tests;

/// <summary>
/// Structural checks of the rendered .pptx (pptx-poc-plan P1–P4, P7, P9, P10 and D12), without Office.
/// </summary>
public sealed class PptxRendererTests : IDisposable
{
    private static readonly string Output = AppContext.BaseDirectory;
    private static readonly string TemplateDirectory = Path.Combine(Output, "templates", "sample-monthly");
    private static readonly string ManifestPath = Path.Combine(TemplateDirectory, "manifest.json");
    private static readonly string TemplatePath = Path.Combine(TemplateDirectory, "sample-monthly.pptx");

    private readonly string _directory = Directory.CreateTempSubdirectory("worklens-render-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static TemplateManifest Manifest() =>
        JsonSerializer.Deserialize<TemplateManifest>(File.ReadAllBytes(ManifestPath), WorkLensJson.Options)!;

    private static PresentationDocument MonthlyPresentation() =>
        JsonSerializer.Deserialize<PresentationDocument>(
            File.ReadAllBytes(Path.Combine(Output, "examples", "monthly-presentation.json")), WorkLensJson.Options)!;

    private static PresentationDocument StressPresentation()
    {
        var reportBytes = File.ReadAllBytes(Path.Combine(Output, "Fixtures", "stress", "report.json"));
        var report = JsonSerializer.Deserialize<ReportDocument>(reportBytes, WorkLensJson.Options)!;
        var evidence = JsonSerializer.Deserialize<EvidenceDocument>(
            File.ReadAllBytes(Path.Combine(Output, "Fixtures", "stress", "evidence.json")), WorkLensJson.Options)!;
        var result = new LayoutResolver().Resolve(report, Sha256(reportBytes), evidence, Manifest());
        Assert.Empty(result.Errors);
        return result.Presentation!;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string Render(PresentationDocument presentation, string name = "out.pptx", TemplateManifest? manifest = null)
    {
        var path = Path.Combine(_directory, name);
        var result = PptxRenderer.Render(presentation, manifest ?? Manifest(), TemplatePath, path);
        Assert.Empty(result.Errors);
        Assert.Equal(path, result.OutputPath);
        return path;
    }

    private static List<SlidePart> Slides(PackageDocument document)
    {
        var part = document.PresentationPart!;
        return [.. part.Presentation!.SlideIdList!.Elements<P.SlideId>()
            .Select(id => (SlidePart)part.GetPartById(id.RelationshipId!.Value!))];
    }

    private static IEnumerable<OpenXmlElement> ShapesOf(SlidePart slide) =>
        slide.Slide!.CommonSlideData!.ShapeTree!.ChildElements
            .Where(e => e is not (P.NonVisualGroupShapeProperties or P.GroupShapeProperties));

    private static P.PlaceholderShape? PlaceholderOf(OpenXmlElement shape) =>
        shape.FirstChild!.GetFirstChild<P.ApplicationNonVisualDrawingProperties>()?.PlaceholderShape;

    private static bool IsType(P.PlaceholderShape ph, P.PlaceholderValues type) => ph.Type is { } t && t.Value == type;

    /// <summary>The slide shape whose placeholder matches the manifest slot's placeholder on its layout.</summary>
    private static OpenXmlElement SlotShape(SlidePart slide, SlotDefinition slot)
    {
        var layoutShape = slide.SlideLayoutPart!.SlideLayout!.CommonSlideData!.ShapeTree!.ChildElements
            .Where(e => e is not (P.NonVisualGroupShapeProperties or P.GroupShapeProperties))
            .Single(e =>
            {
                var ph = PlaceholderOf(e);
                var name = e.FirstChild!.GetFirstChild<P.NonVisualDrawingProperties>()!.Name!.Value;
                if (ph is null)
                {
                    return false;
                }

                var idxOk = slot.Placeholder.Idx is not { } idx
                    || (ph.Index?.Value is { } i ? i == idx
                        : idx == 0 && (IsType(ph, P.PlaceholderValues.Title) || IsType(ph, P.PlaceholderValues.CenteredTitle)));
                return idxOk && (slot.Placeholder.Name is null || slot.Placeholder.Name == name);
            });
        var layoutPh = PlaceholderOf(layoutShape)!;
        return ShapesOf(slide).Single(s => PlaceholderOf(s) is { } ph
            && ph.Index?.Value == layoutPh.Index?.Value
            && ph.Type?.InnerText == layoutPh.Type?.InnerText);
    }

    private static List<string> ParagraphTexts(IEnumerable<A.Paragraph> paragraphs) =>
        [.. paragraphs.Select(p => string.Concat(p.Descendants<A.Text>().Select(t => t.Text)))];

    // P2: one slide per IR slide on the manifest's layout; template slides and notes removed; unique sldId.
    [Fact]
    public void Slides_follow_the_presentation_and_template_slides_are_removed()
    {
        var presentation = MonthlyPresentation();
        var manifest = Manifest();
        using var document = PackageDocument.Open(Render(presentation), false);

        var slides = Slides(document);
        Assert.Equal(presentation.Slides.Count, slides.Count);
        for (var i = 0; i < slides.Count; i++)
        {
            var expected = manifest.Layouts.Single(l => l.Id == presentation.Slides[i].LayoutId).SlideLayoutName;
            Assert.Equal(expected, slides[i].SlideLayoutPart!.SlideLayout!.CommonSlideData!.Name!.Value);
        }

        var presentationPart = document.PresentationPart!;
        Assert.Equal(presentation.Slides.Count, presentationPart.SlideParts.Count());
        Assert.DoesNotContain(presentationPart.SlideParts, s => s.NotesSlidePart is not null);
        Assert.DoesNotContain(document.GetAllParts(), p => p is NotesSlidePart);
        Assert.DoesNotContain(
            slides.SelectMany(s => s.Slide!.Descendants<A.Text>()),
            t => t.Text.Contains("示範", StringComparison.Ordinal));

        var ids = presentationPart.Presentation!.SlideIdList!.Elements<P.SlideId>().Select(s => s.Id!.Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(Enumerable.Range(256, ids.Count).Select(i => (uint)i), ids);
    }

    // P1: every bound slot's text is in the placeholder the manifest names.
    [Fact]
    public void Slot_text_is_in_the_manifest_placeholder()
    {
        var presentation = MonthlyPresentation();
        var manifest = Manifest();
        using var document = PackageDocument.Open(Render(presentation), false);
        var slides = Slides(document);

        for (var i = 0; i < slides.Count; i++)
        {
            var layout = manifest.Layouts.Single(l => l.Id == presentation.Slides[i].LayoutId);
            foreach (var (slotId, value) in presentation.Slides[i].Bindings)
            {
                var shape = SlotShape(slides[i], layout.Slots.Single(s => s.Id == slotId));
                switch (value)
                {
                    case TextSlotValue text:
                        Assert.Equal(text.Text.Split('\n'), ParagraphTexts(((P.Shape)shape).TextBody!.Elements<A.Paragraph>()));
                        break;
                    case BulletsSlotValue bullets:
                        Assert.Equal(bullets.Items.Select(b => b.Text), ParagraphTexts(((P.Shape)shape).TextBody!.Elements<A.Paragraph>()));
                        break;
                    case TableSlotValue table:
                        var rows = shape.Descendants<A.TableRow>()
                            .Select(r => r.Elements<A.TableCell>().Select(c => string.Concat(c.Descendants<A.Text>().Select(t => t.Text))).ToList())
                            .ToList();
                        Assert.Equal([table.Columns.ToList(), .. table.Rows.Select(r => r.ToList())], rows);
                        break;
                }
            }
        }
    }

    // P1: an optional placeholder without a binding gets no shape on the slide.
    [Fact]
    public void Unbound_optional_placeholder_has_no_shape()
    {
        var presentation = MonthlyPresentation();
        var cover = presentation.Slides[0];
        var bindings = cover.Bindings.Where(b => b.Key != "subtitle").ToDictionary(b => b.Key, b => b.Value);
        presentation = presentation with { Slides = [cover with { Bindings = bindings }, .. presentation.Slides.Skip(1)] };

        using var document = PackageDocument.Open(Render(presentation), false);
        var coverShapes = ShapesOf(Slides(document)[0]).ToList();

        Assert.DoesNotContain(coverShapes, s => PlaceholderOf(s)?.Index?.Value == 1U);
        Assert.Contains(coverShapes, s => PlaceholderOf(s) is { } ph && IsType(ph, P.PlaceholderValues.CenteredTitle));
    }

    // P3: bullet levels become a:pPr/@lvl (level 0 omitted).
    [Fact]
    public void Bullet_levels_are_written_as_lvl()
    {
        var presentation = MonthlyPresentation();
        var manifest = Manifest();
        using var document = PackageDocument.Open(Render(presentation), false);
        var slides = Slides(document);

        var checkedNested = false;
        for (var i = 0; i < slides.Count; i++)
        {
            var layout = manifest.Layouts.Single(l => l.Id == presentation.Slides[i].LayoutId);
            foreach (var (slotId, value) in presentation.Slides[i].Bindings)
            {
                if (value is not BulletsSlotValue bullets)
                {
                    continue;
                }

                var paragraphs = ((P.Shape)SlotShape(slides[i], layout.Slots.Single(s => s.Id == slotId)))
                    .TextBody!.Elements<A.Paragraph>().ToList();
                Assert.Equal(bullets.Items.Count, paragraphs.Count);
                for (var p = 0; p < paragraphs.Count; p++)
                {
                    var level = paragraphs[p].ParagraphProperties?.Level?.Value;
                    Assert.Equal(bullets.Items[p].Level == 0 ? null : bullets.Items[p].Level, level);
                    checkedNested |= bullets.Items[p].Level > 0;
                }
            }
        }

        Assert.True(checkedNested, "The monthly example should contain a level 1 bullet.");
    }

    // P4: tables in a tbl placeholder (achievements) and in a content placeholder (summary activity).
    [Theory]
    [InlineData("slide-002", "activity", "obj")]
    [InlineData("slide-003", "items", "tbl")]
    public void Tables_have_their_rows_columns_and_the_default_style(string slideId, string slotId, string placeholderType)
    {
        var presentation = MonthlyPresentation();
        var index = presentation.Slides.ToList().FindIndex(s => s.Id == slideId);
        var value = (TableSlotValue)presentation.Slides[index].Bindings[slotId];
        var layout = Manifest().Layouts.Single(l => l.Id == presentation.Slides[index].LayoutId);
        using var document = PackageDocument.Open(Render(presentation), false);
        var slide = Slides(document)[index];

        var frame = Assert.IsType<P.GraphicFrame>(SlotShape(slide, layout.Slots.Single(s => s.Id == slotId)));
        Assert.Equal(placeholderType, PlaceholderOf(frame)!.Type?.InnerText ?? "obj");
        Assert.NotNull(frame.Transform?.Offset);
        var table = frame.Descendants<A.Table>().Single();
        Assert.Equal(value.Columns.Count, table.TableGrid!.Elements<A.GridColumn>().Count());
        Assert.Equal(value.Rows.Count + 1, table.Elements<A.TableRow>().Count());
        Assert.All(table.Elements<A.TableRow>(), r => Assert.Equal(value.Columns.Count, r.Elements<A.TableCell>().Count()));
        Assert.Equal(frame.Transform!.Extents!.Cx!.Value, table.TableGrid!.Elements<A.GridColumn>().Sum(g => g.Width!.Value));
        Assert.True(table.TableProperties!.FirstRow!.Value);
        Assert.True(table.TableProperties.BandRow!.Value);

        var defaultStyle = document.PresentationPart!.TableStylesPart!.TableStyleList!.Default!.Value;
        Assert.Equal("{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}", defaultStyle);
        Assert.Equal(defaultStyle, table.TableProperties.GetFirstChild<A.TableStyleId>()!.Text);
    }

    // P7: generated runs carry no direct font, color or size formatting.
    [Fact]
    public void Runs_have_no_direct_formatting()
    {
        using var document = PackageDocument.Open(Render(MonthlyPresentation()), false);
        foreach (var run in Slides(document).SelectMany(s => s.Slide!.Descendants<A.Run>()))
        {
            if (run.RunProperties is not { } properties)
            {
                continue;
            }

            Assert.Null(properties.FontSize);
            Assert.Null(properties.GetFirstChild<A.LatinFont>());
            Assert.Null(properties.GetFirstChild<A.EastAsianFont>());
            Assert.Null(properties.GetFirstChild<A.SolidFill>());
        }

        // Runs written for slot values have no a:rPr at all (only footer copies keep the layout's lang).
        var shapes = Slides(document).SelectMany(ShapesOf)
            .Where(s => PlaceholderOf(s) is { } ph
                && !IsType(ph, P.PlaceholderValues.Footer)
                && !IsType(ph, P.PlaceholderValues.SlideNumber));
        Assert.All(shapes.SelectMany(s => s.Descendants<A.Run>()), r => Assert.Null(r.RunProperties));
    }

    // Template section 2: master logo kept; footer text and slide number field on every slide; unique cNvPr ids.
    [Fact]
    public void Master_footer_and_slide_number_are_kept()
    {
        using var document = PackageDocument.Open(Render(MonthlyPresentation()), false);

        var master = document.PresentationPart!.SlideMasterParts.Single();
        var logo = master.SlideMaster!.CommonSlideData!.ShapeTree!.Elements<P.Picture>().Single();
        var embed = logo.BlipFill!.Blip!.Embed!.Value!;
        Assert.IsType<ImagePart>(master.GetPartById(embed));

        foreach (var slide in Slides(document))
        {
            var shapes = ShapesOf(slide).ToList();
            var footer = shapes.Single(s => PlaceholderOf(s) is { } ph && IsType(ph, P.PlaceholderValues.Footer));
            Assert.Equal("WorkLens Sample Template", string.Concat(footer.Descendants<A.Text>().Select(t => t.Text)));
            var number = shapes.Single(s => PlaceholderOf(s) is { } ph && IsType(ph, P.PlaceholderValues.SlideNumber));
            Assert.Equal("slidenum", number.Descendants<A.Field>().Single().Type!.Value);

            var ids = slide.Slide!.Descendants<P.NonVisualDrawingProperties>().Select(d => d.Id!.Value).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }

    // P9: the package is schema-valid for Office 2019, for the example and the stress case.
    [Fact]
    public void Output_passes_OpenXmlValidator()
    {
        foreach (var (presentation, name) in new[] { (MonthlyPresentation(), "monthly.pptx"), (StressPresentation(), "stress.pptx") })
        {
            using var document = PackageDocument.Open(Render(presentation, name), false);
            var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document, TestContext.Current.CancellationToken)
                .Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}")
                .ToList();
            Assert.True(errors.Count == 0, $"{name}:\n{string.Join("\n", errors)}");
        }
    }

    // P10: rendering is deterministic up to rIds / part URIs and matches the committed snapshot.
    [Fact]
    public void Normalized_output_is_deterministic_and_matches_the_snapshot()
    {
        var first = PptxInspector.ToJson(Render(MonthlyPresentation(), "a.pptx"));
        var second = PptxInspector.ToJson(Render(MonthlyPresentation(), "b.pptx"));
        Assert.Equal(first, second);

        if (Environment.GetEnvironmentVariable("WORKLENS_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(
                Path.Combine(RepositoryRoot(), "tests", "WorkLens.Rendering.Pptx.Tests", "Snapshots", "monthly.json"),
                first,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        var snapshot = File.ReadAllText(Path.Combine(Output, "Snapshots", "monthly.json"));
        Assert.Equal(snapshot, first);
    }

    // P10 also covers the stress case's shape: 11 slides, the 20 facts split over 4 pages.
    [Fact]
    public void Stress_case_renders_every_slide()
    {
        var presentation = StressPresentation();
        using var document = PackageDocument.Open(Render(presentation), false);
        Assert.Equal(11, Slides(document).Count);
        Assert.Equal(presentation.Slides.Count, Slides(document).Count);
    }

    // D12: document properties carry nothing from the template or its author.
    [Fact]
    public async Task Document_properties_are_rewritten()
    {
        var presentation = MonthlyPresentation();
        var path = Render(presentation);
        using (var document = PackageDocument.Open(path, false))
        {
            var core = document.PackageProperties;
            Assert.Equal(((TextSlotValue)presentation.Slides[0].Bindings["title"]).Text, core.Title);
            Assert.Equal("WorkLens", core.Creator);
            Assert.Null(core.LastModifiedBy);
            Assert.Null(core.Created);
            Assert.Null(core.Modified);
            Assert.Null(core.Revision);

            var app = document.ExtendedFilePropertiesPart!.Properties!;
            Assert.Equal(
                ["Slides=8", "Application=WorkLens"],
                app.ChildElements.Select(e => $"{e.LocalName}={e.InnerText}"));

            var parts = document.GetAllParts().ToList();
            Assert.DoesNotContain(parts, p => p is NotesSlidePart or CommentAuthorsPart or PowerPointAuthorsPart
                or SlideCommentsPart or PowerPointCommentPart or CustomFilePropertiesPart or ThumbnailPart);
            Assert.Null(document.PresentationPart!.Presentation!.RemovePersonalInfoOnSave);
        }

        using var zip = ZipFile.OpenRead(path);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("docProps/thumbnail", StringComparison.Ordinal));
        using var reader = new StreamReader(zip.GetEntry("ppt/presentation.xml")!.Open());
        Assert.DoesNotContain("removePersonalInfoOnSave", await reader.ReadToEndAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    // D12 for the committed template itself, and its binding to the manifest.
    [Fact]
    public void Template_has_no_author_and_matches_the_manifest_sha256()
    {
        using (var template = PackageDocument.Open(TemplatePath, false))
        {
            Assert.True(string.IsNullOrEmpty(template.PackageProperties.Creator));
            Assert.True(string.IsNullOrEmpty(template.PackageProperties.LastModifiedBy));
        }

        Assert.Equal(Manifest().Template.Sha256, Sha256(File.ReadAllBytes(TemplatePath)));
    }

    [Fact]
    public void Stress_fixture_report_is_bound_to_its_evidence_file()
    {
        var report = JsonSerializer.Deserialize<ReportDocument>(
            File.ReadAllBytes(Path.Combine(Output, "Fixtures", "stress", "report.json")), WorkLensJson.Options)!;
        var evidenceBytes = File.ReadAllBytes(Path.Combine(Output, "Fixtures", "stress", "evidence.json"));
        var evidence = JsonSerializer.Deserialize<EvidenceDocument>(evidenceBytes, WorkLensJson.Options)!;

        Assert.Equal(Sha256(evidenceBytes), report.EvidenceDocument.Sha256);
        Assert.Equal(evidence.DocumentId, report.EvidenceDocument.DocumentId);
    }

    [Fact]
    public void Missing_layout_is_reported_with_its_manifest_path()
    {
        var manifest = Manifest();
        var layouts = manifest.Layouts.ToList();
        layouts[1] = layouts[1] with { SlideLayoutName = "No Such Layout" };
        var result = RenderWith(manifest with { Layouts = layouts });

        var error = Assert.Single(result.Errors);
        Assert.Equal("template.layout_missing", error.Code);
        Assert.Equal("/layouts/1/slide_layout_name", error.Details!["path"]);
    }

    [Fact]
    public void Missing_placeholder_is_reported_with_its_manifest_path()
    {
        var manifest = Manifest();
        var layouts = manifest.Layouts.ToList();
        var slots = layouts[0].Slots.ToList();
        slots[1] = slots[1] with { Placeholder = new PlaceholderRef(Idx: 42) };
        layouts[0] = layouts[0] with { Slots = slots };
        var result = RenderWith(manifest with { Layouts = layouts });

        var error = Assert.Single(result.Errors);
        Assert.Equal("template.placeholder_missing", error.Code);
        Assert.Equal("/layouts/0/slots/1/placeholder", error.Details!["path"]);
    }

    [Fact]
    public void Template_sha256_mismatch_is_reported()
    {
        var manifest = Manifest();
        var result = RenderWith(manifest with { Template = manifest.Template with { Sha256 = new string('0', 64) } });

        var error = Assert.Single(result.Errors);
        Assert.Equal("template.sha256_mismatch", error.Code);
        Assert.Equal("/template/sha256", error.Details!["path"]);
    }

    [Fact]
    public void Duplicate_layout_id_is_reported()
    {
        var manifest = Manifest();
        var result = RenderWith(manifest with { Layouts = [.. manifest.Layouts, manifest.Layouts[0]] });

        var error = Assert.Single(result.Errors);
        Assert.Equal("manifest.duplicate_id", error.Code);
        Assert.Equal($"/layouts/{manifest.Layouts.Count}/id", error.Details!["path"]);
    }

    [Theory]
    [InlineData("malformed_xml")]
    [InlineData("missing_part")]
    [InlineData("missing_slide_part")]
    [InlineData("bad_attribute")]
    public void Broken_template_is_unreadable(string fault)
    {
        var template = Path.Combine(_directory, fault + ".pptx");
        File.Copy(TemplatePath, template);
        using (var zip = ZipFile.Open(template, ZipArchiveMode.Update))
        {
            if (fault == "missing_part")
            {
                zip.GetEntry("ppt/tableStyles.xml")!.Delete();
            }
            else if (fault == "missing_slide_part")
            {
                // The renderer deletes the demo slides; the SDK fails only when it saves the package.
                zip.GetEntry("ppt/slides/slide1.xml")!.Delete();
            }
            else
            {
                var entry = zip.GetEntry("ppt/slideLayouts/slideLayout2.xml")!;
                string xml;
                using (var reader = new StreamReader(entry.Open()))
                {
                    xml = reader.ReadToEnd();
                }

                Assert.Contains("idx=\"1\"", xml, StringComparison.Ordinal);
                entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("ppt/slideLayouts/slideLayout2.xml").Open());
                writer.Write(fault == "malformed_xml" ? xml[..(xml.Length / 2)] : xml.Replace("idx=\"1\"", "idx=\"abc\"", StringComparison.Ordinal));
            }
        }

        var manifest = Manifest();
        var result = RenderWith(manifest with { Template = manifest.Template with { Sha256 = null } }, template);

        var error = Assert.Single(result.Errors);
        Assert.Equal("template.unreadable", error.Code);
        Assert.Equal("/template/file", error.Details!["path"]);
    }

    private Core.Contracts.RenderResult RenderWith(TemplateManifest manifest, string? templatePath = null)
    {
        var path = Path.Combine(_directory, "failed.pptx");
        var result = PptxRenderer.Render(MonthlyPresentation(), manifest, templatePath ?? TemplatePath, path);
        Assert.Null(result.OutputPath);
        Assert.False(File.Exists(path));
        return result;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkLens.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("WorkLens.slnx not found above test output.");
    }
}
