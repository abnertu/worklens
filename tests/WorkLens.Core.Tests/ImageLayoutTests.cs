using System.Text.Json;
using WorkLens.Core.Evidence;
using WorkLens.Core.Json;
using WorkLens.Core.Layout;
using WorkLens.Core.Presentation;
using WorkLens.Core.Report;
using WorkLens.Core.Templates;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class ImageLayoutTests
{
    private static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), WorkLensJson.Options)!;

    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "rd-monthly", "full");
    private static TemplateManifest Manifest => Load<TemplateManifest>(SchemaExamples.ExamplePath("template-manifest", "1.1", "valid", "rd-monthly.json"));

    private static ReportDocument Report()
    {
        var report = Load<ReportDocument>(Path.Combine(Fixture, "report.json"));
        var owner = report.WorkItems[0] with
        {
            ObservedFacts = Enumerable.Range(1, 25).Select(i => new ObservedFact($"Fact {i}", report.WorkItems[0].EvidenceRefs)).ToList(),
            Images = [new("img-flow", "Flow", "flow.png", new string('a', 64), "archify"), new("img-general", "General", "general.jpg", new string('b', 64))],
        };
        return report with { WorkItems = [owner, .. report.WorkItems.Skip(1), owner with { Id = "wi-rejected", ReviewStatus = ReviewStatus.Rejected, Images = [new("img-rejected", "Rejected", "missing.png", new string('c', 64))] }] };
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void Images_follow_all_owner_continuations_in_attachment_order(bool includeArchify, bool operations)
    {
        var report = Report();
        if (operations) { report = report with { WorkItems = [report.WorkItems[0] with { Category = WorkCategory.Ops }, .. report.WorkItems.Skip(1)] }; }
        var result = new LayoutResolver().Resolve(report, new string('d', 64), Load<EvidenceDocument>(Path.Combine(Fixture, "evidence.json")), Manifest, includeArchify);
        Assert.Empty(result.Errors);
        var slides = result.Presentation!.Slides.ToList();
        var text = slides.Where(s => s.LayoutId == "detail_v1" && s.SourceRefs.Contains(report.WorkItems[0].Id)).ToList();
        Assert.True(text.Count > 1);
        var images = slides.Where(s => s.LayoutId == "image_v1").ToList();
        Assert.Equal(includeArchify ? new[] { "img-flow", "img-general" } : ["img-general"], images.Select(s => Assert.IsType<ImageSlotValue>(s.Bindings["image"]).Id));
        Assert.Equal(slides.IndexOf(text[^1]) + 1, slides.IndexOf(images[0]));
        foreach (var image in images)
        {
            Assert.Contains(report.WorkItems[0].Id, image.SourceRefs);
            Assert.All(report.WorkItems[0].EvidenceRefs, id => Assert.Contains(id, image.SourceRefs));
        }
        Assert.DoesNotContain(slides, s => s.SourceRefs.Contains("img-rejected"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Active_images_require_a_detail_section_and_image_layout(bool removeSection)
    {
        var manifest = Manifest;
        manifest = removeSection
            ? manifest with { Sequence = manifest.Sequence.Where(s => s.Section != ReportSection.WorkItemDetails).ToList() }
            : manifest with { Layouts = manifest.Layouts.Select(l => l with { ImageLayoutId = null }).ToList() };
        var result = new LayoutResolver().Resolve(Report(), new string('d', 64), Load<EvidenceDocument>(Path.Combine(Fixture, "evidence.json")), manifest);
        Assert.Null(result.Presentation);
        Assert.Contains(result.Errors, e => e.Code == "manifest.image_layout_missing");
    }
}
