using System.Text.Json;
using WorkLens.Core.Evidence;
using WorkLens.Core.Json;
using WorkLens.Core.Report;
using WorkLens.Core.Templates;
using WorkLens.Core.Validation;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class ImageContractTests
{
    [Fact]
    public void Image_ids_are_unique_across_work_items()
    {
        var report = JsonSerializer.Deserialize<ReportDocument>(File.ReadAllText(
            SchemaExamples.ExamplePath("report-ir", "1.1", "valid", "image-report.json")), WorkLensJson.Options)!;
        var evidence = JsonSerializer.Deserialize<EvidenceDocument>(File.ReadAllText(
            SchemaExamples.ExamplePath("evidence-ir", "1.0", "valid", "monthly-github-gitlab.json")), WorkLensJson.Options)!;
        var items = report.WorkItems.ToArray();
        items[1] = items[1] with { Images = items[0].Images };

        var result = new ReportValidator().Validate(WorkLensJson.SerializeCanonical(report with { WorkItems = items }),
            evidence, report.EvidenceDocument.Sha256);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("ref.duplicate_id", issue.Code);
        Assert.Equal("/work_items/1/images/0/id", issue.InstancePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Image_layout_reference_requires_a_dedicated_target(bool wrongPurpose)
    {
        var detail = new LayoutDefinition("detail", "Detail", LayoutPurpose.WorkItemDetail, [], ImageLayoutId: "picture");
        LayoutDefinition[] layouts = wrongPurpose
            ? [detail, new("picture", "Picture", LayoutPurpose.Narrative, [])]
            : [detail];
        var manifest = new TemplateManifest("1.1", new("test", "1.0.0", "test.pptx"), layouts, []);

        var error = Assert.Single(TemplateManifestChecker.CheckContracts(manifest));

        Assert.Equal("manifest.image_layout_invalid", error.Code);
        Assert.Equal("/layouts/0/image_layout_id", error.Details!["path"]);
    }

    [Fact]
    public void Image_page_requires_its_title_and_one_required_image_slot()
    {
        var manifest = new TemplateManifest("1.1", new("test", "1.0.0", "test.pptx"),
            [new("picture", "Picture", LayoutPurpose.WorkItemImage,
                [new("image", new(Idx: 1), SlotKind.Image)])], []);

        Assert.Equal("manifest.image_layout_invalid", Assert.Single(TemplateManifestChecker.CheckContracts(manifest)).Code);
    }
}
