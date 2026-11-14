using System.Text.Json;
using WorkLens.Core.Json;
using WorkLens.Core.Templates;
using WorkLens.Core.Validation;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class TemplateManifestCheckerTests
{
    private static readonly TemplateManifest Manifest = JsonSerializer.Deserialize<TemplateManifest>(
        File.ReadAllText(SchemaExamples.ExamplePath("template-manifest", "1.1", "valid", "rd-monthly.json")),
        WorkLensJson.Options)!;

    private static readonly int AchievementsIndex = IndexOf("achievements_v1");

    [Fact]
    public void Valid_1_1_example_passes_every_check()
    {
        Assert.Empty(TemplateManifestChecker.CheckIds(Manifest));
        Assert.Empty(TemplateManifestChecker.CheckContracts(Manifest));
    }

    [Fact]
    public void Column_key_outside_the_purpose_set_is_a_column_mismatch()
    {
        var manifest = WithItemsColumns(
            AchievementsIndex,
            [new TableColumn("title", 50), new TableColumn("problem", 50)]);

        AssertSingle(manifest, "manifest.column_mismatch", $"/layouts/{AchievementsIndex}/slots/1/columns/1/key");
    }

    [Fact]
    public void Columns_on_a_purpose_without_a_column_set_are_a_column_mismatch()
    {
        var summary = IndexOf("summary_v1");
        var activity = new SlotDefinition("activity", new PlaceholderRef(Name: "Activity"), SlotKind.Table, Columns: [new TableColumn("title", 100)]);
        var manifest = ReplaceLayout(summary, l => l with { Slots = [.. l.Slots, activity] });

        AssertSingle(manifest, "manifest.column_mismatch", $"/layouts/{summary}/slots/{Manifest.Layouts[summary].Slots.Count}/columns");
    }

    [Fact]
    public void Repeated_column_key_is_a_column_mismatch()
    {
        var manifest = WithItemsColumns(
            AchievementsIndex,
            [new TableColumn("title", 50), new TableColumn("status", 25), new TableColumn("title", 25)]);

        AssertSingle(manifest, "manifest.column_mismatch", $"/layouts/{AchievementsIndex}/slots/1/columns/2/key");
    }

    [Fact]
    public void Column_widths_that_do_not_add_up_to_100_are_a_column_mismatch()
    {
        var manifest = WithItemsColumns(AchievementsIndex, [new TableColumn("title", 60), new TableColumn("status", 30)]);

        AssertSingle(manifest, "manifest.column_mismatch", $"/layouts/{AchievementsIndex}/slots/1/columns");
    }

    [Fact]
    public void Header_style_without_a_role_is_an_unknown_style()
    {
        var manifest = ReplaceItems(AchievementsIndex, s => s with { HeaderStyle = "table_head" });

        AssertSingle(manifest, "manifest.unknown_style", $"/layouts/{AchievementsIndex}/slots/1/header_style");
    }

    [Fact]
    public void Row_style_without_a_role_is_an_unknown_style()
    {
        var manifest = ReplaceItems(AchievementsIndex, s => s with { RowStyles = ["table_row", "zebra"] });

        AssertSingle(manifest, "manifest.unknown_style", $"/layouts/{AchievementsIndex}/slots/1/row_styles/1");
    }

    private static void AssertSingle(TemplateManifest manifest, string code, string path)
    {
        var error = Assert.Single(TemplateManifestChecker.CheckContracts(manifest));
        Assert.Equal(code, error.Code);
        Assert.Equal(path, error.Details!["path"]);
    }

    private static int IndexOf(string layoutId) =>
        Manifest.Layouts.Select((l, i) => (l, i)).Single(p => p.l.Id == layoutId).i;

    private static TemplateManifest WithItemsColumns(int layout, IReadOnlyList<TableColumn> columns) =>
        ReplaceItems(layout, s => s with { Columns = columns });

    private static TemplateManifest ReplaceItems(int layout, Func<SlotDefinition, SlotDefinition> change) =>
        ReplaceLayout(layout, l => l with { Slots = [.. l.Slots.Select(s => s.Id == "items" ? change(s) : s)] });

    private static TemplateManifest ReplaceLayout(int index, Func<LayoutDefinition, LayoutDefinition> change) =>
        Manifest with { Layouts = [.. Manifest.Layouts.Select((l, i) => i == index ? change(l) : l)] };
}
