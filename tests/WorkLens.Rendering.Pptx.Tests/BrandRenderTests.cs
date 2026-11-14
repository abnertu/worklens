using System.IO.Compression;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using WorkLens.Core.Contracts;
using WorkLens.Core.Json;
using WorkLens.Core.Templates;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using PackageDocument = DocumentFormat.OpenXml.Packaging.PresentationDocument;
using PresentationDocument = WorkLens.Core.Presentation.PresentationDocument;

namespace WorkLens.Rendering.Pptx.Tests;

/// <summary>The unit name, the logo and the footer (docs/design/report-template.md section 8.8).</summary>
public sealed class BrandRenderTests : IDisposable
{
    private const long EmuPerPoint = 12700;

    private static readonly string RdDirectory = Path.Combine(AppContext.BaseDirectory, "templates", "rd-monthly");

    private readonly string _directory = Directory.CreateTempSubdirectory("worklens-brand-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), WorkLensJson.Options)!;

    private static TemplateManifest RdManifest => Load<TemplateManifest>(Path.Combine(RdDirectory, "manifest.json"));

    private static PresentationDocument Full =>
        Load<PresentationDocument>(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rd-monthly", "full", "presentation.json"));

    /// <summary>A valid RGB PNG of the size (8 × 2 by default: a 4:1 logo).</summary>
    internal static byte[] Png(uint width = 8, uint height = 2)
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            var crc = Crc32(typed);
            return [.. BigEndian((uint)data.Length), .. typed, .. BigEndian(crc)];
        }

        byte[] header = [.. BigEndian(width), .. BigEndian(height), 8, 2, 0, 0, 0]; // 8-bit RGB
        var raw = new byte[height * (1 + (width * 3))]; // each row: filter byte, then black pixels
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", header), .. Chunk("IDAT", compressed.ToArray()), .. Chunk("IEND", [])];
    }

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private (string Path, RenderResult Result) Render(PresentationDocument presentation, TemplateManifest manifest, string template, BrandElements? brand)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.pptx");
        var result = PptxRenderer.Render(presentation, manifest, template, path, brand);
        Assert.Empty(result.Errors);
        return (path, result);
    }

    private (string Path, RenderResult Result) RenderRd(BrandElements? brand, PresentationDocument? presentation = null, TemplateManifest? manifest = null) =>
        Render(presentation ?? Full, manifest ?? RdManifest, Path.Combine(RdDirectory, "rd-monthly.pptx"), brand);

    private static List<P.Slide> Slides(PackageDocument document)
    {
        var part = document.PresentationPart!;
        return part.Presentation!.SlideIdList!.Elements<P.SlideId>()
            .Select(id => ((SlidePart)part.GetPartById(id.RelationshipId!.Value!)).Slide!)
            .ToList();
    }

    private static string? Footer(P.Slide slide) =>
        slide.Descendants<P.Shape>()
            .Where(s => s.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value == P.PlaceholderValues.Footer)
            .Select(s => string.Concat(s.Descendants<A.Text>().Select(t => t.Text)))
            .SingleOrDefault();

    private static string NamedText(P.Slide slide, string name) =>
        string.Concat(slide.Descendants<P.Shape>()
            .Single(s => s.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value == name)
            .Descendants<A.Text>().Select(t => t.Text));

    private static BrandElements Brand(string? unit = "研發一處", bool logo = true) =>
        new(unit, logo ? LogoImage.Read(Png()) : null);

    [Fact]
    public void Logo_fills_each_logo_position_with_its_aspect_ratio()
    {
        var (path, result) = RenderRd(Brand());
        using var document = PackageDocument.Open(path, isEditable: false);
        var slides = Slides(document);

        // The cover logo box is 90 × 30 pt: a 4:1 logo is 90 × 22.5 pt, centered in it.
        var cover = slides[0].Descendants<P.Picture>().Single();
        var transform = cover.ShapeProperties!.Transform2D!;
        Assert.Equal(90 * EmuPerPoint, transform.Extents!.Cx!.Value);
        Assert.Equal((long)(22.5 * EmuPerPoint), transform.Extents.Cy!.Value);
        Assert.Equal((465 + 3.75) * EmuPerPoint, transform.Offset!.Y!.Value);
        // Every content slide has a footer logo; the closing layout declares none.
        Assert.All(slides.Skip(1).SkipLast(1), s => Assert.Single(s.Descendants<P.Picture>()));
        Assert.Empty(slides[^1].Descendants<P.Picture>());
        Assert.Empty(result.Warnings);
        Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2016).Validate(document, TestContext.Current.CancellationToken));
    }

    // A tall logo is limited by the box height, so it is centered across the box width.
    [Fact]
    public void Logo_limited_by_height_is_centered_across_the_box()
    {
        var (path, _) = RenderRd(new BrandElements(null, LogoImage.Read(Png(2, 8))));
        using var document = PackageDocument.Open(path, isEditable: false);

        var transform = Slides(document)[0].Descendants<P.Picture>().Single().ShapeProperties!.Transform2D!;
        Assert.Equal((long)(7.5 * EmuPerPoint), transform.Extents!.Cx!.Value);
        Assert.Equal((long)((510 + 41.25) * EmuPerPoint), transform.Offset!.X!.Value);
    }

    // The slides share one image part, so the logo is stored once.
    [Fact]
    public void Logo_is_stored_once_for_all_slides()
    {
        var (path, _) = RenderRd(Brand());
        using var document = PackageDocument.Open(path, isEditable: false);

        var parts = document.PresentationPart!.SlideParts.SelectMany(p => p.ImageParts).Select(p => p.Uri).Distinct().ToList();
        Assert.Single(parts);
    }

    /// <summary>A JPEG header of 8 × 2 pixels, with fill bytes before its first marker.</summary>
    private static byte[] Jpeg() =>
    [
        0xFF, 0xD8, 0xFF, 0xFF, 0xE0, 0x00, 0x10, .. new byte[14],
        0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x02, 0x00, 0x08, 0x03, .. new byte[9],
        0xFF, 0xD9,
    ];

    [Fact]
    public void Logo_reader_reads_the_jpeg_size_after_fill_bytes()
    {
        var logo = LogoImage.Read(Jpeg())!;

        Assert.Equal(("image/jpeg", 8, 2), (logo.ContentType, logo.Width, logo.Height));
    }

    [Fact]
    public void Jpeg_logo_keeps_its_content_type()
    {
        var (path, _) = RenderRd(new BrandElements(null, LogoImage.Read(Jpeg())));
        using var document = PackageDocument.Open(path, isEditable: false);

        Assert.Equal("image/jpeg", document.PresentationPart!.SlideParts.SelectMany(p => p.ImageParts).First().ContentType);
    }

    // Characters that XML does not allow are removed, so the package stays valid.
    [Fact]
    public void Unit_name_loses_characters_that_xml_does_not_allow()
    {
        var (path, _) = RenderRd(Brand("研\u0001發", logo: false));
        using var document = PackageDocument.Open(path, isEditable: false);

        Assert.Equal("研發｜" + Full.Footer, Footer(Slides(document)[1]));
    }

    [Fact]
    public void Unit_name_fills_the_cover_position_within_its_capacity()
    {
        var unit = new string('研', 35);
        var (path, _) = RenderRd(Brand(unit, logo: false));
        using var document = PackageDocument.Open(path, isEditable: false);

        Assert.Equal(new string('研', 29) + "…", NamedText(Slides(document)[0], "Unit Name"));
    }

    [Fact]
    public void Footer_joins_the_unit_name_and_the_presentation_footer()
    {
        var (path, _) = RenderRd(Brand(logo: false));
        using var document = PackageDocument.Open(path, isEditable: false);
        var slides = Slides(document);

        Assert.Null(Footer(slides[0])); // the cover has no footer
        Assert.All(slides.Skip(1), s => Assert.Equal("研發一處｜" + Full.Footer, Footer(s)));
    }

    [Fact]
    public void Footer_without_a_brand_is_the_presentation_footer()
    {
        var (path, _) = RenderRd(null);
        using var document = PackageDocument.Open(path, isEditable: false);

        Assert.Equal(Full.Footer, Footer(Slides(document)[1]));
    }

    [Fact]
    public void Footer_without_a_presentation_footer_is_the_unit_name()
    {
        var (path, _) = RenderRd(Brand(logo: false), Full with { Footer = null });
        using var document = PackageDocument.Open(path, isEditable: false);

        Assert.Equal("研發一處", Footer(Slides(document)[1]));
    }

    // With neither, the footer keeps the template text, which is empty in rd-monthly.
    [Fact]
    public void Footer_without_both_keeps_the_template_text()
    {
        var (path, _) = RenderRd(null, Full with { Footer = null });
        using var document = PackageDocument.Open(path, isEditable: false);

        Assert.Equal(string.Empty, Footer(Slides(document)[1]));
    }

    // The footer text keeps its whole period; only the unit name is cut, and it is left out when no character fits.
    [Theory]
    [InlineData(4, "研發一…")]
    [InlineData(2, "研…")]
    [InlineData(1, null)]
    public void Footer_cuts_only_the_unit_name(int room, string? unit)
    {
        var footer = Full.Footer!;
        var manifest = RdManifest with { Footer = new FooterDefinition(new System.Globalization.StringInfo(footer).LengthInTextElements + 1 + room) };

        var (path, result) = RenderRd(Brand("研發一處處", logo: false), manifest: manifest);
        using var document = PackageDocument.Open(path, isEditable: false);

        Assert.Equal(unit is null ? footer : unit + "｜" + footer, Footer(Slides(document)[1]));
        if (unit is null)
        {
            var warning = Assert.Single(result.Warnings);
            Assert.Equal("render.brand_not_written", warning.Code);
            Assert.Equal("/footer", warning.Details!["path"]);
        }
    }

    // sample-monthly declares no brand positions and no footer: the brand is not written, the footer stays, and each
    // value gets a warning.
    [Fact]
    public void Template_without_brand_positions_keeps_its_footer_and_warns()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "templates", "sample-monthly");
        var manifest = Load<TemplateManifest>(Path.Combine(directory, "manifest.json"));
        var presentation = Load<PresentationDocument>(Path.Combine(AppContext.BaseDirectory, "examples", "monthly-presentation.json"));
        var template = Path.Combine(directory, "sample-monthly.pptx");

        var (plain, _) = Render(presentation, manifest, template, null);
        var (branded, result) = Render(presentation, manifest, template, Brand());
        using var plainDocument = PackageDocument.Open(plain, isEditable: false);
        using var brandedDocument = PackageDocument.Open(branded, isEditable: false);

        Assert.Equal(Slides(plainDocument).Select(Footer), Slides(brandedDocument).Select(Footer));
        Assert.Empty(Slides(brandedDocument).SelectMany(s => s.Descendants<P.Picture>()));
        Assert.Equal(2, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.Equal("render.brand_not_written", w.Code));
    }

    [Theory]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })] // GIF
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })] // a cut PNG header
    public void Logo_reader_accepts_only_png_and_jpeg_with_a_size(byte[] bytes) =>
        Assert.Null(LogoImage.Read(bytes));

    [Fact]
    public void Logo_reader_reads_the_png_size()
    {
        var logo = LogoImage.Read(Png())!;

        Assert.Equal(("image/png", 8, 2), (logo.ContentType, logo.Width, logo.Height));
    }
}
