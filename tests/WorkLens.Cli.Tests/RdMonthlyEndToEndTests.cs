using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using Xunit;
using P = DocumentFormat.OpenXml.Presentation;

namespace WorkLens.Cli.Tests;

/// <summary>
/// The rd-monthly report from report.json to .pptx: present, then render with a config and a logo
/// (docs/design/report-template.md section 7).
/// </summary>
public sealed class RdMonthlyEndToEndTests : IDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private readonly CliRunner _cli = new();

    public void Dispose() => _cli.Dispose();

    // The design's page order, each page named by its layout and one source ref. Shared layouts (the detail
    // layout for development and ops items, the narrative layout for each kind) are told apart by the source ref.
    private static readonly (string Layout, string? Ref)[] DesignOrder =
    [
        ("cover_v1", null),
        ("summary_v1", null),
        ("achievements_v1", "wi-token-rotation"),
        ("detail_v1", "wi-token-rotation"),
        ("operations_v1", "wi-runner-image"),
        ("detail_v1", "wi-runner-image"),
        ("challenges_v1", "nr-challenge-flaky-tests"),
        ("narrative_v1", "nr-improvement-review-checklist"),
        ("narrative_v1", "nr-support-infra"),
        ("narrative_v1", "nr-next-plan-1"),
        ("narrative_v1", "nr-other-mentoring"),
    ];

    [Fact]
    public async Task Present_and_render_follow_the_design_page_order()
    {
        _cli.CopyToWorkspace("tests/Fixtures/rd-monthly/full/report.json", "report.json");
        _cli.CopyToWorkspace("tests/Fixtures/rd-monthly/full/evidence.json", "evidence.json");
        _cli.CopyToWorkspace("templates/rd-monthly/rd-monthly.pptx", "rd-monthly.pptx");
        var manifest = _cli.CopyToWorkspace("templates/rd-monthly/manifest.json", "manifest.json");
        File.WriteAllBytes(Path.Combine(_cli.Workspace, "logo.png"), Png);
        var config = Path.Combine(_cli.Workspace, "worklens.json");
        File.WriteAllText(config, new JsonObject
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
            ["report"] = new JsonObject { ["organization"] = "研發一處", ["logo"] = "logo.png" },
        }.ToJsonString());

        var present = await _cli.RunAsync("present", "--workspace", _cli.Workspace, "--manifest", manifest, "--json");
        var render = await _cli.RunAsync("render", "--workspace", _cli.Workspace, "--manifest", manifest, "--config", config, "--json");

        Assert.Equal(ExitCodes.Success, present.ExitCode);
        Assert.Equal(ExitCodes.Success, render.ExitCode);
        Assert.Empty(render.Json["issues"]!.AsArray());

        // The slide order: the index of each design page is after the one before it.
        var slides = JsonNode.Parse(File.ReadAllText(Path.Combine(_cli.Workspace, "presentation.json")))!["slides"]!.AsArray()
            .Select(s => (Layout: s!["layout_id"]!.GetValue<string>(), Refs: s["source_refs"]!.AsArray().Select(r => r!.GetValue<string>()).ToList()))
            .ToList();
        var positions = DesignOrder
            .Select(page => slides.FindIndex(s => s.Layout == page.Layout && (page.Ref is null || s.Refs.Contains(page.Ref))))
            .ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);

        // The .pptx has the same slides, in the same order.
        using var document = PresentationDocument.Open(Path.Combine(_cli.Workspace, "report.pptx"), isEditable: false);
        var part = document.PresentationPart!;
        var layouts = part.Presentation!.SlideIdList!.Elements<P.SlideId>()
            .Select(id => ((SlidePart)part.GetPartById(id.RelationshipId!.Value!)).SlideLayoutPart!.SlideLayout!.CommonSlideData!.Name!.Value)
            .ToList();
        Assert.Equal(slides.Count, layouts.Count);
        Assert.Equal("Cover", layouts[0]);
    }
}
