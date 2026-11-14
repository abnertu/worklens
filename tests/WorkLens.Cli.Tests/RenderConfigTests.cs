using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace WorkLens.Cli.Tests;

/// <summary><c>render --config</c>: the unit name and the logo of config 1.1.</summary>
public sealed class RenderConfigTests : IDisposable
{
    // A 1 × 1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private readonly CliRunner _cli = new();

    public void Dispose() => _cli.Dispose();

    private string WriteConfig(string? logo)
    {
        var report = new JsonObject { ["organization"] = "研發一處" };
        if (logo is not null)
        {
            report["logo"] = logo;
        }

        var config = new JsonObject
        {
            ["config_version"] = "1.1",
            ["identities"] = new JsonArray(new JsonObject { ["platform"] = "gitlab", ["username"] = "example.user" }),
            ["sources"] = new JsonArray(new JsonObject
            {
                ["id"] = "backend",
                ["platform"] = "gitlab",
                ["base_url"] = "https://gitlab.example.com/api/v4",
                ["repository"] = "group/backend-api",
            }),
            ["report"] = report,
        };
        var path = Path.Combine(_cli.Workspace, "worklens.json");
        File.WriteAllText(path, config.ToJsonString());
        return path;
    }

    /// <summary>
    /// Copies the rd-monthly template, its manifest and the full fixture, and runs present, so that the presentation
    /// names the template version of the manifest.
    /// </summary>
    private async Task<string> CopyRdInputs()
    {
        _cli.CopyToWorkspace("tests/Fixtures/rd-monthly/full/report.json", "report.json");
        _cli.CopyToWorkspace("tests/Fixtures/rd-monthly/full/evidence.json", "evidence.json");
        _cli.CopyToWorkspace("templates/rd-monthly/rd-monthly.pptx", "rd-monthly.pptx");
        var manifest = _cli.CopyToWorkspace("templates/rd-monthly/manifest.json", "manifest.json");
        var present = await _cli.RunAsync("present", "--workspace", _cli.Workspace, "--manifest", manifest, "--json");
        Assert.Equal(ExitCodes.Success, present.ExitCode);
        return manifest;
    }

    private Task<CliRun> Render(string manifest, string config) =>
        _cli.RunAsync("render", "--workspace", _cli.Workspace, "--manifest", manifest, "--config", config, "--json");

    [Fact]
    public async Task Render_writes_the_unit_name_and_the_logo_of_the_config()
    {
        var manifest = await CopyRdInputs();
        File.WriteAllBytes(Path.Combine(_cli.Workspace, "logo.png"), Png);

        var run = await Render(manifest, WriteConfig("logo.png"));

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Empty(run.Json["issues"]!.AsArray());
        using var document = PresentationDocument.Open(Path.Combine(_cli.Workspace, "report.pptx"), isEditable: false);
        var cover = ((SlidePart)document.PresentationPart!.GetPartById(
            document.PresentationPart.Presentation!.SlideIdList!.Elements<P.SlideId>().First().RelationshipId!.Value!)).Slide!;
        Assert.Contains("研發一處", cover.Descendants<A.Text>().Select(t => t.Text));
        Assert.Single(cover.Descendants<P.Picture>());
    }

    [Fact]
    public async Task Missing_logo_file_is_input_not_found()
    {
        var manifest = await CopyRdInputs();

        var run = await Render(manifest, WriteConfig("missing.png"));

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("input.not_found", run.Json["error"]!["code"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_cli.Workspace, "report.pptx")));
    }

    [Fact]
    public async Task Logo_that_is_not_png_or_jpeg_is_config_invalid_logo()
    {
        var manifest = await CopyRdInputs();
        File.WriteAllBytes(Path.Combine(_cli.Workspace, "logo.gif"), [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0]);

        var run = await Render(manifest, WriteConfig("logo.gif"));

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("config.invalid_logo", run.Json["error"]!["code"]!.GetValue<string>());
    }

    // A path that the file system cannot take is a configuration problem, not an internal error.
    [Fact]
    public async Task Logo_path_that_is_not_a_file_path_is_config_invalid_logo()
    {
        var manifest = await CopyRdInputs();

        var run = await Render(manifest, WriteConfig("logo\u0000.png"));

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("config.invalid_logo", run.Json["error"]!["code"]!.GetValue<string>());
    }

    // sample-monthly declares no brand positions: render succeeds and warns about each value.
    [Fact]
    public async Task Template_without_brand_positions_warns_and_exits_0()
    {
        _cli.CopyToWorkspace("schemas/presentation-ir/1.0/examples/valid/monthly-presentation.json", "presentation.json");
        _cli.CopyToWorkspace("templates/sample-monthly/sample-monthly.pptx", "sample-monthly.pptx");
        var manifest = _cli.CopyToWorkspace("templates/sample-monthly/manifest.json", "manifest.json");
        File.WriteAllBytes(Path.Combine(_cli.Workspace, "logo.png"), Png);

        var run = await Render(manifest, WriteConfig("logo.png"));

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var issues = run.Json["issues"]!.AsArray();
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(("render.brand_not_written", "warning"), (i!["code"]!.GetValue<string>(), i["severity"]!.GetValue<string>())));
    }
}
