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

    // The golden achievements table has 2 columns; this manifest gives its items slot the columns of the keys.
    private static TemplateManifest ItemsColumnsManifest(params string[] keys)
    {
        var manifest = Manifest();
        var layouts = manifest.Layouts.ToList();
        var index = layouts.FindIndex(l => l.Id == "achievements_v1");
        layouts[index] = layouts[index] with
        {
            Slots = [.. layouts[index].Slots.Select(s => s.Id == "items" ? s with { Columns = [.. keys.Select(k => new TableColumn(k, 100 / keys.Length))] } : s)],
        };
        return manifest with { Layouts = layouts };
    }

    [Fact]
    public void Table_with_another_column_count_than_the_slot_columns()
    {
        var issue = Assert.Single(Issues(Golden(), ItemsColumnsManifest("title", "status", "evidence")));

        Assert.Equal(("presentation.table_column_count", "/slides/2/bindings/items/columns"), (issue.Code, issue.InstancePath));
    }

    [Fact]
    public void Table_with_the_slot_column_count_passes() =>
        Assert.Empty(Issues(Golden(), ItemsColumnsManifest("title", "status")));

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

    // Presentation IR 1.1 -------------------------------------------------------------------------------------------

    private static IReadOnlyList<ValidationIssue> Issues(PresentationDocument presentation, TemplateManifest? manifest = null) =>
        PresentationManifestChecker.Check(presentation, manifest ?? Manifest()).Issues;

    // The sample manifest has no metric slot: this one makes the subtitle of the cover a metric slot.
    private static TemplateManifest MetricSubtitleManifest()
    {
        var manifest = Manifest();
        var layouts = manifest.Layouts.ToList();
        var cover = layouts.FindIndex(l => l.Id == "cover_v1");
        layouts[cover] = layouts[cover] with
        {
            Slots = [.. layouts[cover].Slots.Select(s => s.Id == "subtitle" ? s with { Kind = SlotKind.Metric } : s)],
        };
        return manifest with { Layouts = layouts };
    }

    private static PresentationDocument WithItems(TableSlotValue table) =>
        WithSlide(Golden(), 2, s => WithBinding(s, "items", table));

    [Fact]
    public void Metric_in_a_text_slot_is_a_kind_mismatch()
    {
        var issue = Assert.Single(Issues(WithSlide(Golden(), 0, s => WithBinding(s, "subtitle", new MetricSlotValue("l", "1")))));

        Assert.Equal(("presentation.slot_kind_mismatch", "/slides/0/bindings/subtitle/kind"), (issue.Code, issue.InstancePath));
        Assert.Contains("binds metric", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Metric_in_a_metric_slot_passes() =>
        Assert.Empty(Issues(
            WithSlide(Golden(), 0, s => WithBinding(s, "subtitle", new MetricSlotValue("l", "1", "c"))),
            MetricSubtitleManifest()));

    [Theory]
    [InlineData("label")]
    [InlineData("value")]
    [InlineData("caption")]
    public void Metric_text_with_an_invalid_character(string field)
    {
        var metric = new MetricSlotValue(
            field == "label" ? "bad \u001B" : "l",
            field == "value" ? "bad \u001B" : "1",
            field == "caption" ? "bad \u001B" : "c");

        var issue = Assert.Single(Issues(WithSlide(Golden(), 0, s => WithBinding(s, "subtitle", metric)), MetricSubtitleManifest()));

        Assert.Equal(("presentation.invalid_character", $"/slides/0/bindings/subtitle/{field}"), (issue.Code, issue.InstancePath));
    }

    [Fact]
    public void Footer_with_an_invalid_character() =>
        Assert.Equal(("presentation.invalid_character", "/footer"), Single(Golden() with { Footer = "bad \u001B" }));

    [Fact]
    public void Styled_cell_with_an_invalid_character_reports_its_text()
    {
        var presentation = WithItems(new TableSlotValue(["a", "b"], [["1", "bad \u001B"]]) { CellStyles = [[null, "s"]] });

        Assert.Equal(("presentation.invalid_character", "/slides/2/bindings/items/rows/0/1/text"), Single(presentation));
    }

    // CellStyles with fewer rows: one issue for each row without styles; no row uses its styles.
    [Fact]
    public void Cell_styles_with_fewer_rows()
    {
        var presentation = WithItems(new TableSlotValue(["a", "b"], [["1", "2"], ["3", "bad \u001B"], ["5", "6"]])
        {
            CellStyles = [[null, "s"]],
        });

        Assert.Equal(
            [
                ("presentation.table_row_width", "/slides/2/bindings/items/rows/1", "The cell styles of row 1 do not match its cells."),
                ("presentation.table_row_width", "/slides/2/bindings/items/rows/2", "The cell styles of row 2 do not match its cells."),
                ("presentation.invalid_character", "/slides/2/bindings/items/rows/1/1", null),
            ],
            Issues(presentation).Select(i => (i.Code, i.InstancePath, i.Code == "presentation.table_row_width" ? i.Message : null)));
    }

    // CellStyles with more rows: the extra row paths point past the rows in the JSON, and no row uses its styles.
    [Fact]
    public void Cell_styles_with_more_rows()
    {
        var presentation = WithItems(new TableSlotValue(["a", "b"], [["1", "bad \u001B"]])
        {
            CellStyles = [[null, "s"], [null, null], [null, null]],
        });

        Assert.Equal(
            [
                ("presentation.table_row_width", "/slides/2/bindings/items/rows/1"),
                ("presentation.table_row_width", "/slides/2/bindings/items/rows/2"),
                ("presentation.invalid_character", "/slides/2/bindings/items/rows/0/1"),
            ],
            Issues(presentation).Select(i => (i.Code, i.InstancePath)));
    }

    // Same row count: a row whose styles differ in width gets one issue and loses its styles; other rows keep them.
    [Fact]
    public void Cell_styles_row_of_another_width()
    {
        var presentation = WithItems(new TableSlotValue(["a", "b"], [["1", "bad \u001B"], ["3", "bad \u001B"]])
        {
            CellStyles = [[null, "s", "t"], [null, "s"]],
        });

        Assert.Equal(
            [
                ("presentation.table_row_width", "/slides/2/bindings/items/rows/0", "The cell styles of row 0 do not match its cells."),
                ("presentation.invalid_character", "/slides/2/bindings/items/rows/0/1", null),
                ("presentation.invalid_character", "/slides/2/bindings/items/rows/1/1/text", null),
            ],
            Issues(presentation).Select(i => (i.Code, i.InstancePath, i.Code == "presentation.table_row_width" ? i.Message : null)));
    }

    // A row whose width differs from the columns and from its styles gets only the columns issue.
    [Fact]
    public void Row_of_another_width_than_columns_and_styles_gets_one_issue()
    {
        var presentation = WithItems(new TableSlotValue(["a", "b"], [["bad \u001B"]]) { CellStyles = [["s", "t"]] });

        Assert.Equal(
            [
                ("presentation.table_row_width", "/slides/2/bindings/items/rows/0", "Row 0 has 1 cells, but the table has 2 columns."),
                ("presentation.invalid_character", "/slides/2/bindings/items/rows/0/0", null),
            ],
            Issues(presentation).Select(i => (i.Code, i.InstancePath, i.Code == "presentation.table_row_width" ? i.Message : null)));
    }
}
