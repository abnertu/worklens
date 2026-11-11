using System.Text.Json;
using WorkLens.Core.Errors;
using WorkLens.Core.Json;
using WorkLens.Core.Presentation;
using WorkLens.Core.Templates;
using WorkLens.Core.Validation;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class PresentationManifestCheckerTests
{
    private static TemplateManifest Manifest() => JsonSerializer.Deserialize<TemplateManifest>(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "templates", "sample-monthly", "manifest.json")),
        WorkLensJson.Options)!;

    private static PresentationDocument Golden() => JsonSerializer.Deserialize<PresentationDocument>(
        File.ReadAllBytes(Path.Combine(SchemaExamples.SchemasRoot, "presentation-ir", "1.0", "examples", "valid", "monthly-presentation.json")),
        WorkLensJson.Options)!;

    private static PresentationDocument WithSlide(PresentationDocument presentation, int index, Func<Slide, Slide> change)
    {
        var slides = presentation.Slides.ToList();
        slides[index] = change(slides[index]);
        return presentation with { Slides = slides };
    }

    private static Slide WithBinding(Slide slide, string slot, SlotValue? value)
    {
        var bindings = slide.Bindings.ToDictionary(b => b.Key, b => b.Value);
        if (value is null)
        {
            bindings.Remove(slot);
        }
        else
        {
            bindings[slot] = value;
        }

        return slide with { Bindings = bindings };
    }

    private static (string Code, string Path) Single(PresentationDocument presentation)
    {
        var issue = Assert.Single(PresentationManifestChecker.Check(presentation, Manifest()).Issues);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Null(issue.SchemaPath);
        return (issue.Code, issue.InstancePath);
    }

    [Fact]
    public void Resolver_output_passes() =>
        Assert.Empty(PresentationManifestChecker.Check(Golden(), Manifest()).Issues);

    [Fact]
    public void Duplicate_layout_id_uses_the_first_definition()
    {
        var manifest = Manifest();
        var duplicate = manifest.Layouts[0] with { Slots = [] };

        Assert.Empty(PresentationManifestChecker.Check(Golden(), manifest with { Layouts = [.. manifest.Layouts, duplicate] }).Issues);
    }

    [Fact]
    public void Template_mismatch() =>
        Assert.Equal(
            ("presentation.template_mismatch", "/template"),
            Single(Golden() with { Template = new TemplateRef("other", "1.0.0") }));

    [Fact]
    public void Unknown_layout() =>
        Assert.Equal(
            ("presentation.unknown_layout", "/slides/2/layout_id"),
            Single(WithSlide(Golden(), 2, s => s with { LayoutId = "nope_v1" })));

    [Fact]
    public void Unknown_slot() =>
        Assert.Equal(
            ("presentation.unknown_slot", "/slides/0/bindings/footnote"),
            Single(WithSlide(Golden(), 0, s => WithBinding(s, "footnote", new TextSlotValue("x")))));

    [Fact]
    public void Slot_kind_mismatch() =>
        Assert.Equal(
            ("presentation.slot_kind_mismatch", "/slides/0/bindings/subtitle/kind"),
            Single(WithSlide(Golden(), 0, s => WithBinding(s, "subtitle", new BulletsSlotValue([new BulletItem("x", 0)])))));

    [Fact]
    public void Missing_required_slot() =>
        Assert.Equal(
            ("presentation.missing_required_slot", "/slides/0/bindings"),
            Single(WithSlide(Golden(), 0, s => WithBinding(s, "title", null))));

    [Fact]
    public void Table_row_width() =>
        Assert.Equal(
            ("presentation.table_row_width", "/slides/2/bindings/items/rows/1"),
            Single(WithSlide(Golden(), 2, s => WithBinding(s, "items", new TableSlotValue(
                ["a", "b"],
                [["1", "2"], ["only one"]])))));

    [Theory]
    [InlineData(0, "title", "/slides/0/bindings/title/text")]
    [InlineData(3, "facts", "/slides/3/bindings/facts/items/0/text")]
    [InlineData(2, "items", "/slides/2/bindings/items/rows/0/1")]
    public void Invalid_character(int slide, string slot, string path)
    {
        var presentation = WithSlide(Golden(), slide, s => WithBinding(s, slot, s.Bindings[slot] switch
        {
            TextSlotValue t => t with { Text = "bad \u001B" },
            BulletsSlotValue b => b with { Items = [new BulletItem("bad \u001B", 0), .. b.Items.Skip(1)] },
            TableSlotValue t => t with { Rows = [[t.Rows[0][0], "bad ￿"], .. t.Rows.Skip(1)] },
            _ => throw new InvalidOperationException(),
        }));

        Assert.Equal(("presentation.invalid_character", path), Single(presentation));
    }
}
