using System.Security.Cryptography;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using WorkLens.Core.Contracts;
using WorkLens.Core.Json;
using WorkLens.Core.Presentation;
using WorkLens.Core.Templates;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using PackageDocument = DocumentFormat.OpenXml.Packaging.PresentationDocument;

namespace WorkLens.Rendering.Pptx.Tests;

public sealed class ImageRenderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("worklens-images-").FullName;
    private static readonly string TemplateDirectory = Path.Combine(AppContext.BaseDirectory, "templates", "rd-monthly");
    private static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), WorkLensJson.Options)!;
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData("wide.png", 864, 216, 48, 177)]
    [InlineData("tall.jpg", 97.5, 390, 431.25, 90)]
    public void Body_picture_preserves_bytes_and_fits_its_frame(string file, double width, double height, double x, double y)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "images", file));
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var manifest = Load<TemplateManifest>(Path.Combine(TemplateDirectory, "manifest.json"));
        var presentation = Load<WorkLens.Core.Presentation.PresentationDocument>(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rd-monthly", "empty", "presentation.json")) with
        {
            Slides = [new("slide-001", "image_v1", new Dictionary<string, SlotValue>
            {
                ["title"] = new TextSlotValue("Delivery flow"),
                ["image"] = new ImageSlotValue("img-flow", "Accessible delivery flow", file, hash, "archify"),
            }, ["wi-flow", "img-flow"])],
        };
        var path = Path.Combine(_directory, "report.pptx");
        var result = PptxRenderer.Render(presentation, manifest, Path.Combine(TemplateDirectory, "rd-monthly.pptx"), path,
            images: new Dictionary<string, RasterImage> { [hash] = RasterImage.Read(bytes)! });
        Assert.Empty(result.Errors);
        using var package = PackageDocument.Open(path, false);
        Assert.Empty(new OpenXmlValidator().Validate(package, TestContext.Current.CancellationToken));
        var slide = Assert.Single(package.PresentationPart!.SlideParts);
        var picture = Assert.Single(slide.Slide!.Descendants<P.Picture>());
        Assert.Equal("Accessible delivery flow", picture.NonVisualPictureProperties!.NonVisualDrawingProperties!.Description!.Value);
        var part = Assert.IsType<ImagePart>(slide.GetPartById(picture.BlipFill!.Blip!.Embed!.Value!));
        using var actual = new MemoryStream();
        part.GetStream().CopyTo(actual);
        Assert.Equal(bytes, actual.ToArray());
        var transform = picture.ShapeProperties!.Transform2D!;
        Assert.Equal((long)(width * 12700), transform.Extents!.Cx!.Value);
        Assert.Equal((long)(height * 12700), transform.Extents.Cy!.Value);
        Assert.Equal((long)(x * 12700), transform.Offset!.X!.Value);
        Assert.Equal((long)(y * 12700), transform.Offset.Y!.Value);

        // The byte boundary also protects callers of the renderer API, before it touches output.
        var invalid = PptxRenderer.Render(presentation, manifest, "unused.pptx", path);
        Assert.Equal("render.image_missing", Assert.Single(invalid.Errors).Code);
        var changed = PptxRenderer.Render(presentation, manifest, "unused.pptx", path,
            images: new Dictionary<string, RasterImage> { [hash] = RasterImage.Read(bytes)! with { Width = 1 } });
        Assert.Equal("render.image_invalid", Assert.Single(changed.Errors).Code);
        using var preserved = PackageDocument.Open(path, false);
        Assert.Single(preserved.PresentationPart!.SlideParts);
    }
}
