using System.Security.Cryptography;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using WorkLens.Core.Contracts;
using WorkLens.Core.Json;
using WorkLens.Core.Templates;
using WorkLens.Core.Validation;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using PackageDocument = DocumentFormat.OpenXml.Packaging.PresentationDocument;
using PresentationDocument = WorkLens.Core.Presentation.PresentationDocument;

namespace WorkLens.Rendering.Pptx.Tests;

public sealed class SharedBrandRenderTests : IDisposable
{
    private static readonly string RdDirectory = Path.Combine(AppContext.BaseDirectory, "templates", "rd-monthly");
    private readonly string _directory = Directory.CreateTempSubdirectory("worklens-shared-brand-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), WorkLensJson.Options)!;
    private static TemplateManifest Manifest => Load<TemplateManifest>(Path.Combine(RdDirectory, "manifest.json"));
    private static PresentationDocument Presentation => Load<PresentationDocument>(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rd-monthly", "full", "presentation.json"));

    private string CopyTemplate(Action<PresentationPart> mutate)
    {
        var path = Path.Combine(_directory, $"template-{Guid.NewGuid():N}.pptx");
        File.Copy(Path.Combine(RdDirectory, "rd-monthly.pptx"), path);
        using var document = PackageDocument.Open(path, isEditable: true);
        mutate(document.PresentationPart!);
        return path;
    }

    private static P.Shape Footer(SlideMasterPart master) => master.SlideMaster!.Descendants<P.Shape>()
        .Single(s => s.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value == "WorkLens Footer");

    [Fact]
    public void Shared_brand_is_inherited_without_slide_copies_and_source_changes()
    {
        var template = Path.Combine(RdDirectory, "rd-monthly.pptx");
        var before = SHA256.HashData(File.ReadAllBytes(template));
        var output = Path.Combine(_directory, "report.pptx");
        var bytes = BrandRenderTests.Png();
        var result = PptxRenderer.Render(Presentation, Manifest, template, output, new BrandElements("研發 RD", LogoImage.Read(bytes)));
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(template)));

        using var document = PackageDocument.Open(output, isEditable: false);
        var master = Assert.Single(document.PresentationPart!.SlideMasterParts);
        Assert.Equal("研發 RD｜" + Presentation.Footer, string.Concat(Footer(master).Descendants<A.Text>().Select(t => t.Text)));
        var picture = Assert.Single(master.SlideMaster!.Descendants<P.Picture>());
        Assert.Empty(picture.Descendants<P.PlaceholderShape>());
        var embedded = (ImagePart)master.GetPartById(picture.BlipFill!.Blip!.Embed!.Value!);
        using var stream = embedded.GetStream();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.Equal(bytes, memory.ToArray());
        Assert.All(document.PresentationPart.SlideParts, slide =>
        {
            Assert.Empty(slide.Slide!.Descendants<P.Picture>());
            Assert.DoesNotContain(slide.Slide.Descendants<P.PlaceholderShape>(), p => p.Type?.Value == P.PlaceholderValues.Footer);
            Assert.Contains(slide.Slide.Descendants<P.PlaceholderShape>(), p => p.Type?.Value == P.PlaceholderValues.SlideNumber);
            Assert.Same(master, slide.SlideLayoutPart!.SlideMasterPart);
        });
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2016).Validate(document, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Master_footer_preserves_formats_and_uses_language_pair_before_replacement()
    {
        var template = CopyTemplate(part =>
        {
            var master = part.SlideMasterParts.Single();
            // Make the replaced footer the only source of the language pair.
            foreach (var element in master.SlideMaster!.Descendants().ToList())
            {
                element.RemoveAttribute("lang", string.Empty);
                element.RemoveAttribute("altLang", string.Empty);
            }

            var body = Footer(master).TextBody!;
            body.RemoveAllChildren<A.Paragraph>();
            body.Append(new A.Paragraph(new A.ParagraphProperties { Alignment = A.TextAlignmentTypeValues.Right },
                new A.Run(new A.RunProperties
                {
                    FontSize = 900, Bold = true, Language = "zh-TW", AlternativeLanguage = "en-US",
                }, new A.Text("模板"))));
        });
        var output = Path.Combine(_directory, "language.pptx");
        var result = PptxRenderer.Render(Presentation with { Footer = "研發 RD e\u0301 𠀀" },
            Manifest with { Template = Manifest.Template with { Sha256 = null } }, template, output);
        Assert.Empty(result.Errors);
        using var document = PackageDocument.Open(output, isEditable: false);
        var footer = Footer(document.PresentationPart!.SlideMasterParts.Single());
        Assert.Equal("研發 RD e\u0301 𠀀", string.Concat(footer.Descendants<A.Text>().Select(t => t.Text)));
        Assert.Equal(A.TextAlignmentTypeValues.Right, footer.Descendants<A.ParagraphProperties>().Single().Alignment!.Value);
        var runs = footer.Descendants<A.Run>().ToList();
        Assert.Contains(runs, r => r.Text!.Text.Contains('研') && r.RunProperties!.Language!.Value == "zh-TW");
        Assert.Contains(runs, r => r.Text!.Text.Contains("RD", StringComparison.Ordinal) && r.RunProperties!.Language!.Value == "en-US");
        Assert.Contains(runs, r => r.Text!.Text.Contains("e\u0301", StringComparison.Ordinal));
        Assert.Contains(runs, r => r.Text!.Text == "𠀀" && r.RunProperties!.Language!.Value == "zh-TW");
        Assert.All(runs, r =>
        {
            Assert.Equal(900, r.RunProperties!.FontSize!.Value);
            Assert.True(r.RunProperties.Bold!.Value);
            Assert.Empty(r.RunProperties.Elements<A.LatinFont>());
            Assert.Empty(r.RunProperties.Elements<A.EastAsianFont>());
        });
    }

    [Fact]
    public void Master_footer_keeps_explicit_languages_without_a_chinese_english_pair()
    {
        var template = CopyTemplate(part =>
        {
            var master = part.SlideMasterParts.Single();
            foreach (var element in master.SlideMaster!.Descendants().ToList())
            {
                element.RemoveAttribute("lang", string.Empty);
                element.RemoveAttribute("altLang", string.Empty);
            }

            var body = Footer(master).TextBody!;
            body.RemoveAllChildren<A.Paragraph>();
            body.Append(new A.Paragraph(new A.Run(new A.RunProperties
            {
                Language = "fr-FR", AlternativeLanguage = "de-DE", FontSize = 900,
            }, new A.Text("Template footer"))));
        });
        var output = Path.Combine(_directory, "explicit-languages.pptx");
        var result = PptxRenderer.Render(Presentation with { Footer = "Updated footer 中文" },
            Manifest with { Template = Manifest.Template with { Sha256 = null } }, template, output);
        Assert.Empty(result.Errors);
        using var document = PackageDocument.Open(output, isEditable: false);
        var run = Assert.Single(Footer(document.PresentationPart!.SlideMasterParts.Single()).Descendants<A.Run>());
        Assert.Equal("Updated footer 中文", run.Text!.Text);
        Assert.Equal("fr-FR", run.RunProperties!.Language?.Value);
        Assert.Equal("de-DE", run.RunProperties.AlternativeLanguage?.Value);
        Assert.Equal(900, run.RunProperties.FontSize!.Value);
    }

    [Theory]
    [InlineData("missing_master", "template.shared_brand_target")]
    [InlineData("duplicate_master", "template.shared_brand_target")]
    [InlineData("missing_footer", "template.shared_brand_target")]
    [InlineData("missing_logo", "template.shared_brand_target")]
    [InlineData("duplicate_footer", "template.shared_brand_target")]
    [InlineData("duplicate_logo", "template.shared_brand_target")]
    [InlineData("placeholder_footer", "template.shared_brand_target")]
    [InlineData("placeholder_logo", "template.shared_brand_target")]
    [InlineData("footer_frame", "template.shared_brand_target")]
    [InlineData("logo_frame", "template.shared_brand_target")]
    [InlineData("hidden_logo", "template.shared_brand_hidden")]
    [InlineData("hidden_unused_layout", "template.shared_brand_hidden")]
    public void Invalid_targets_leave_existing_output_unchanged(string fault, string code)
    {
        var template = CopyTemplate(part =>
        {
            var master = part.SlideMasterParts.Single();
            var tree = master.SlideMaster!.CommonSlideData!.ShapeTree!;
            var logo = TemplateLayouts.Shapes(tree).Single(s => s.Name == "WorkLens Logo");
            switch (fault)
            {
                case "missing_master": master.SlideMaster.CommonSlideData.Name = "Other"; break;
                case "duplicate_master":
                    part.AddNewPart<SlideMasterPart>().SlideMaster = (P.SlideMaster)master.SlideMaster.CloneNode(true);
                    break;
                case "missing_footer": Footer(master).Remove(); break;
                case "missing_logo": logo.Shape.Remove(); break;
                case "duplicate_footer": tree.Append(Footer(master).CloneNode(true)); break;
                case "duplicate_logo": tree.Append(logo.Shape.CloneNode(true)); break;
                case "placeholder_footer":
                    Footer(master).NonVisualShapeProperties!.ApplicationNonVisualDrawingProperties!.Append(new P.PlaceholderShape());
                    break;
                case "placeholder_logo":
                    logo.Shape.Descendants<P.ApplicationNonVisualDrawingProperties>().Single().Append(new P.PlaceholderShape());
                    break;
                case "footer_frame": Footer(master).ShapeProperties!.Transform2D!.Remove(); break;
                case "logo_frame": logo.Shape.Descendants<A.Transform2D>().Single().Remove(); break;
                case "hidden_logo": logo.Drawing.Hidden = true; break;
                case "hidden_unused_layout":
                    master.SlideLayoutParts.Single(p => p.SlideLayout!.CommonSlideData!.Name!.Value == "Closing").SlideLayout!.ShowMasterShapes = false;
                    break;
            }
        });
        var output = Path.Combine(_directory, "unchanged.pptx");
        byte[] original = [1, 3, 5, 7];
        File.WriteAllBytes(output, original);
        var presentation = Presentation with { Slides = [.. Presentation.Slides.Where(s => s.LayoutId != "closing_v1")] };
        var result = PptxRenderer.Render(presentation, Manifest with { Template = Manifest.Template with { Sha256 = null } }, template, output);
        Assert.Contains(result.Errors, e => e.Code == code && e.Details!["path"].StartsWith("/shared_brand/masters/0/", StringComparison.Ordinal));
        Assert.Equal(original, File.ReadAllBytes(output));
    }

    [Fact]
    public void Repeated_master_declarations_and_reused_shapes_are_rejected()
    {
        var definition = Manifest.SharedBrand!.Masters.Single();
        Assert.Contains(TemplateManifestChecker.CheckIds(Manifest with { SharedBrand = new SharedBrandDefinition([definition, definition]) }),
            e => e.Code == "manifest.duplicate_id" && e.Details!["path"] == "/shared_brand/masters/1/name");
        Assert.Contains(TemplateManifestChecker.CheckIds(Manifest with { SharedBrand = new SharedBrandDefinition([definition with { Logo = definition.Footer }]) }),
            e => e.Code == "manifest.shared_brand_conflict");
    }

    [Fact]
    public void Per_layout_logo_cannot_duplicate_a_shared_logo()
    {
        var manifest = Manifest with
        {
            Layouts = [.. Manifest.Layouts.Select(l => l.Id == "cover_v1"
                ? l with { Brand = new LayoutBrand(new BrandLogo(new PlaceholderRef(Name: "Unit Name"))) }
                : l)],
        };
        var result = PptxRenderer.Render(Presentation, manifest, Path.Combine(RdDirectory, "rd-monthly.pptx"), Path.Combine(_directory, "conflict.pptx"));
        Assert.Contains(result.Errors, e => e.Code == "template.shared_brand_conflict" && e.Details!["path"] == "/layouts/0/brand/logo");
    }
}
