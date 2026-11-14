using WorkLens.Core.Layout;
using WorkLens.Core.Presentation;
using WorkLens.Core.Templates;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class SlotFitterTests
{
    private static SlotDefinition Slot(SlotKind kind, int? maxChars = null, int? maxItems = null, IReadOnlyList<TableColumn>? columns = null) =>
        new("s", new PlaceholderRef(Idx: 1), kind, MaxChars: maxChars, MaxItems: maxItems, Columns: columns);

    [Fact]
    public void Text_keeps_its_style_and_input_truncated_flag()
    {
        var value = new TextSlotValue("short") { Style = "empty", Truncated = true };

        var fitted = Assert.IsType<TextSlotValue>(Assert.Single(SlotFitter.Fit(value, Slot(SlotKind.Text, maxChars: 10), split: false)));

        Assert.Equal("short", fitted.Text);
        Assert.Equal("empty", fitted.Style);
        Assert.True(fitted.Truncated);
    }

    [Fact]
    public void Bullets_keep_item_and_value_styles_on_every_page()
    {
        var value = new BulletsSlotValue([new BulletItem("a", 0, "numbered"), new BulletItem("b", 1, "meta"), new BulletItem("c", 0)]) { Style = "support" };

        var pages = SlotFitter.Fit(value, Slot(SlotKind.Bullets, maxItems: 2), split: true).Cast<BulletsSlotValue>().ToList();

        Assert.Equal(2, pages.Count);
        Assert.All(pages, p => Assert.Equal("support", p.Style));
        Assert.Equal(["numbered", "meta"], pages[0].Items.Select(i => i.Style));
        Assert.Null(Assert.Single(pages[1].Items).Style);
        Assert.All(pages, p => Assert.Null(p.Truncated));
    }

    [Fact]
    public void Grouped_bullets_skip_max_items_but_keep_text_truncation()
    {
        var value = new BulletsSlotValue([new BulletItem("one", 0), new BulletItem("two", 1), new BulletItem("three", 0)]) { Truncated = true };

        var fitted = Assert.IsType<BulletsSlotValue>(Assert.Single(
            SlotFitter.Fit(value, Slot(SlotKind.Bullets, maxChars: 4, maxItems: 1), split: false, grouped: true)));

        Assert.Equal(["one", "two", "thr…"], fitted.Items.Select(i => i.Text));
        Assert.True(fitted.Truncated);
    }

    [Fact]
    public void Table_split_carries_the_cell_styles_of_each_row()
    {
        var value = new TableSlotValue(["T", "S"], [["r1", "done"], ["r2", "open"], ["r3", "x"]])
        {
            CellStyles = [[null, "tag_completed"], [null, "tag_open"], [null, null]],
        };

        var pages = SlotFitter.Fit(value, Slot(SlotKind.Table, maxItems: 2), split: true).Cast<TableSlotValue>().ToList();

        Assert.Equal([[null, "tag_completed"], [null, "tag_open"]], pages[0].CellStyles!.Select(r => r.ToArray()));
        Assert.Equal([[null, null]], pages[1].CellStyles!.Select(r => r.ToArray()));
    }

    [Fact]
    public void Table_truncation_cuts_the_cell_styles_with_the_rows()
    {
        var value = new TableSlotValue(["T"], [["r1"], ["r2"]]) { CellStyles = [["meta"], [null]] };

        var fitted = Assert.IsType<TableSlotValue>(Assert.Single(SlotFitter.Fit(value, Slot(SlotKind.Table, maxItems: 1), split: false)));

        Assert.Equal([["meta"]], fitted.CellStyles!.Select(r => r.ToArray()));
        Assert.True(fitted.Truncated);
    }

    [Fact]
    public void Table_column_max_chars_overrides_the_slot_max_chars()
    {
        var value = new TableSlotValue(["T", "P"], [["abcdefgh", "abcdefgh"]]);
        var slot = Slot(SlotKind.Table, maxChars: 6, columns: [new TableColumn("title", 50, MaxChars: 3), new TableColumn("project", 50)]);

        var fitted = Assert.IsType<TableSlotValue>(Assert.Single(SlotFitter.Fit(value, slot, split: false)));

        Assert.Equal(["ab…", "abcde…"], fitted.Rows[0]);
        Assert.True(fitted.Truncated);
    }

    [Fact]
    public void Metric_fields_are_cleaned_and_truncated_on_one_page()
    {
        var value = new MetricSlotValue("Comp\u0001leted", "12345", "work items") { Style = "metric" };

        var pages = SlotFitter.Fit(value, Slot(SlotKind.Metric, maxChars: 4, maxItems: 1), split: true);

        var fitted = Assert.IsType<MetricSlotValue>(Assert.Single(pages));
        Assert.Equal("Com…", fitted.Label);
        Assert.Equal("123…", fitted.Value);
        Assert.Equal("wor…", fitted.Caption);
        Assert.Equal("metric", fitted.Style);
        Assert.True(fitted.Truncated);
    }

    [Fact]
    public void Metric_without_truncation_is_not_marked()
    {
        var fitted = Assert.IsType<MetricSlotValue>(Assert.Single(
            SlotFitter.Fit(new MetricSlotValue("Done", "3"), Slot(SlotKind.Metric, maxChars: 8), split: false)));

        Assert.Null(fitted.Caption);
        Assert.Null(fitted.Truncated);
    }

    [Fact]
    public void Metric_is_empty_only_without_any_text()
    {
        Assert.True(SlotFitter.IsEmpty(new MetricSlotValue(string.Empty, string.Empty)));
        Assert.False(SlotFitter.IsEmpty(new MetricSlotValue(string.Empty, "0")));
    }
}
