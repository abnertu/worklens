using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Xunit;
using P = DocumentFormat.OpenXml.Presentation;

namespace WorkLens.Cli.Tests;

public sealed class ImagePipelineTests : IDisposable
{
    private readonly CliRunner _cli = new();
    private string Manifest => Path.Combine(TestPaths.RepositoryRoot(), "templates", "rd-monthly", "manifest.json");
    private string ReportPath => Path.Combine(_cli.Workspace, "report.json");
    private string PresentationPath => Path.Combine(_cli.Workspace, "nested", "presentation.json");
    private string OutputPath => Path.Combine(_cli.Workspace, "report.pptx");
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose() => _cli.Dispose();

    private void Prepare(string imagePath = "wide.png")
    {
        _cli.CopyToWorkspace("tests/Fixtures/rd-monthly/full/report.json", "report.json");
        _cli.CopyToWorkspace("tests/Fixtures/rd-monthly/full/evidence.json", "evidence.json");
        _cli.CopyToWorkspace("tests/Fixtures/images/wide.png", "wide.png");
        _cli.CopyToWorkspace("tests/Fixtures/images/tall.jpg", "tall.jpg");
        var report = JsonNode.Parse(File.ReadAllBytes(ReportPath))!;
        report["work_items"]![0]!["images"] = new JsonArray(
            new JsonObject { ["id"] = "img-flow", ["title"] = "Flow", ["file"] = imagePath, ["sha256"] = Hash(File.ReadAllBytes(Path.Combine(_cli.Workspace, "wide.png"))), ["producer"] = "archify" },
            new JsonObject { ["id"] = "img-general", ["title"] = "General", ["file"] = "tall.jpg", ["sha256"] = Hash(File.ReadAllBytes(Path.Combine(_cli.Workspace, "tall.jpg"))) });
        File.WriteAllText(ReportPath, report.ToJsonString());
        Directory.CreateDirectory(Path.GetDirectoryName(PresentationPath)!);
    }

    private Task<CliRun> Present(params string[] extra) => _cli.RunAsync(["present", "--workspace", _cli.Workspace,
        "--manifest", Manifest, "--output", PresentationPath, "--json", .. extra]);

    private Task<CliRun> Render(params string[] extra) => _cli.RunAsync(["render", "--presentation", PresentationPath,
        "--manifest", Manifest, "--output", OutputPath, "--json", .. extra]);

    [Theory]
    [InlineData("Flow", "Flow")]
    [InlineData("\u0001", "img-flow")]
    public async Task Relocated_presentation_renders_exact_assets_and_binds_original_report_bytes(string title, string expectedTitle)
    {
        Prepare(".\\wide.png");
        var report = JsonNode.Parse(File.ReadAllBytes(ReportPath))!;
        report["work_items"]![0]!["images"]![0]!["title"] = title;
        File.WriteAllText(ReportPath, report.ToJsonString());
        var original = File.ReadAllBytes(ReportPath);
        _cli.CurrentDirectory = _cli.Home;
        var present = await Present();
        Assert.Equal(0, present.ExitCode);
        var presentation = JsonNode.Parse(File.ReadAllBytes(PresentationPath))!;
        Assert.Equal(Hash(original), (string)presentation["source_report"]!["sha256"]!);
        var images = presentation["slides"]!.AsArray().Where(s => (string)s!["layout_id"]! == "image_v1").ToList();
        Assert.Equal(new[] { "../wide.png", "../tall.jpg" }, images.Select(s => (string)s!["bindings"]!["image"]!["file"]!));
        Assert.Equal("archify", (string)images[0]!["bindings"]!["image"]!["producer"]!);
        Assert.Equal(expectedTitle, (string)images[0]!["bindings"]!["image"]!["title"]!);
        Assert.Equal(expectedTitle, (string)images[0]!["bindings"]!["title"]!["text"]!);
        var render = await Render();
        Assert.Equal(0, render.ExitCode);
        using var package = PresentationDocument.Open(OutputPath, false);
        Assert.Empty(new OpenXmlValidator().Validate(package, TestContext.Current.CancellationToken));
        Assert.Equal((int)present.Json["result"]!["slide_count"]!, package.PresentationPart!.SlideParts.Count());
        var embedded = package.PresentationPart.SlideParts.SelectMany(s => s.ImageParts).Distinct().Select(Read).ToList();
        Assert.Contains(embedded, bytes => bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(_cli.Workspace, "wide.png"))));
        Assert.Contains(embedded, bytes => bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(_cli.Workspace, "tall.jpg"))));
        Assert.Equal(original, File.ReadAllBytes(ReportPath));
    }

    [Fact]
    public async Task Both_opt_outs_skip_missing_archify_assets_and_preserve_text_general_image_and_logo()
    {
        Prepare();
        File.Delete(Path.Combine(_cli.Workspace, "wide.png"));
        var original = File.ReadAllBytes(ReportPath);
        var full = await Present();
        Assert.Equal(0, full.ExitCode);
        var originalPresentation = File.ReadAllBytes(PresentationPath);
        var config = Path.Combine(_cli.Workspace, "worklens.json");
        File.WriteAllText(config, """
            {"config_version":"1.1","identities":[{"platform":"gitlab","username":"example"}],
             "sources":[{"id":"backend","platform":"gitlab","base_url":"https://gitlab.example.com/api/v4","repository":"group/backend"}],
             "report":{"organization":"Example","logo":"tall.jpg"}}
            """);
        var render = await Render("--no-archify", "--config", config);
        Assert.Equal(0, render.ExitCode);
        using (var package = PresentationDocument.Open(OutputPath, false))
        {
            var slides = package.PresentationPart!.SlideParts.ToList();
            Assert.Equal((int)full.Json["result"]!["slide_count"]! - 1, slides.Count);
            Assert.Equal(slides.Count, (int)render.Json["result"]!["slide_count"]!);
            Assert.All(slides, slide => Assert.Contains(slide.SlideLayoutPart!.SlideMasterPart!.SlideMaster!.Descendants<P.Picture>(),
                picture => picture.NonVisualPictureProperties!.NonVisualDrawingProperties!.Name!.Value == "WorkLens Logo"));
            Assert.DoesNotContain(slides.SelectMany(s => s.Slide!.Descendants<P.Picture>()),
                picture => picture.NonVisualPictureProperties!.NonVisualDrawingProperties!.Name!.Value == "WorkLens Logo");
            Assert.Single(slides.SelectMany(s => s.Slide!.Descendants<P.Picture>()), p => p.NonVisualPictureProperties!.NonVisualDrawingProperties!.Description?.Value == "General");
            Assert.Empty(new OpenXmlValidator().Validate(package, TestContext.Current.CancellationToken));
        }
        Assert.Equal(originalPresentation, File.ReadAllBytes(PresentationPath));
        var noArchify = await Present("--no-archify");
        Assert.Equal(0, noArchify.ExitCode);
        Assert.Equal((int)render.Json["result"]!["slide_count"]!, (int)noArchify.Json["result"]!["slide_count"]!);
        Assert.DoesNotContain("img-flow", File.ReadAllText(PresentationPath), StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(ReportPath));
    }

    [Theory]
    [InlineData("missing", "input.not_found")]
    [InlineData("hash", "input.image_sha256_mismatch")]
    [InlineData("header", "input.invalid_image")]
    [InlineData("locked", "input.unreadable")]
    public async Task Active_asset_failure_preserves_output_and_leaves_no_temp_file(string failure, string code)
    {
        Prepare();
        if (failure == "header")
        {
            var path = Path.Combine(_cli.Workspace, "wide.png");
            File.WriteAllText(path, "not an image");
            var report = JsonNode.Parse(File.ReadAllBytes(ReportPath))!;
            report["work_items"]![0]!["images"]![0]!["sha256"] = Hash(File.ReadAllBytes(path));
            File.WriteAllText(ReportPath, report.ToJsonString());
        }
        Assert.Equal(0, (await Present()).ExitCode);
        var image = Path.Combine(_cli.Workspace, "wide.png");
        if (failure == "missing") { File.Delete(image); }
        if (failure == "hash") { File.WriteAllText(image, "changed"); }
        using var locked = failure == "locked" ? new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        File.WriteAllText(OutputPath, "existing output");
        var before = Directory.GetFiles(_cli.Workspace, "*", SearchOption.AllDirectories).Order().ToList();
        var run = await Render();
        Assert.Equal(3, run.ExitCode);
        Assert.Equal(code, (string)run.Json["error"]!["code"]!);
        Assert.Equal("existing output", File.ReadAllText(OutputPath));
        Assert.Equal(before, Directory.GetFiles(_cli.Workspace, "*", SearchOption.AllDirectories).Order().ToList());
    }

    [Theory]
    [InlineData("C:diagram.png")]
    [InlineData("C:/diagram.png")]
    [InlineData("/diagram.png")]
    [InlineData("https://example.com/diagram.png")]
    [InlineData("file:///diagram.png")]
    [InlineData("\\\\server\\share\\diagram.png")]
    [InlineData("\\\\?\\C:\\diagram.png")]
    [InlineData("diagram.png:stream")]
    [InlineData("NUL.png")]
    [InlineData("images/con.txt")]
    [InlineData("LPT1")]
    [InlineData("COM¹.jpg")]
    public async Task Nonlocal_reference_is_rejected_before_output(string path)
    {
        Prepare(path);
        File.WriteAllText(PresentationPath, "existing presentation");
        var run = await Present();
        Assert.Equal(3, run.ExitCode);
        Assert.Equal("input.invalid_image_path", (string)run.Json["error"]!["code"]!);
        Assert.Equal("existing presentation", File.ReadAllText(PresentationPath));
    }

    [Fact]
    public async Task Opt_out_keeps_schema_and_template_identity_checks()
    {
        Prepare();
        Assert.Equal(0, (await Present()).ExitCode);
        var document = JsonNode.Parse(File.ReadAllBytes(PresentationPath))!;
        var templateVersion = (string)document["template"]!["version"]!;
        document["template"]!["version"] = "1.0.1";
        File.WriteAllText(PresentationPath, document.ToJsonString());
        var stale = await Render("--no-archify");
        Assert.Equal(2, stale.ExitCode);
        Assert.Contains(stale.Json["issues"]!.AsArray(), i => (string)i!["code"]! == "presentation.template_mismatch");
        document["template"]!["version"] = templateVersion;
        document["slides"]!.AsArray().First(s => (string)s!["layout_id"]! == "image_v1")!["bindings"]!["image"]!["producer"] = "unknown";
        File.WriteAllText(PresentationPath, document.ToJsonString());
        Assert.Equal(2, (await Render("--no-archify")).ExitCode);
    }

    private static byte[] Read(ImagePart part)
    {
        using var data = new MemoryStream();
        part.GetStream().CopyTo(data);
        return data.ToArray();
    }
}
