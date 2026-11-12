using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using WorkLens.Core.Common;
using WorkLens.Core.Evidence;
using WorkLens.Core.Json;
using WorkLens.Core.Layout;
using WorkLens.Core.Presentation;
using WorkLens.Core.Report;
using WorkLens.Core.Templates;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class LayoutResolverTests
{
    private static readonly LayoutResolver Resolver = new();

    private static string Output(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);

    private static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), WorkLensJson.Options)!;

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string ReportPath => SchemaExamples.ExamplePath("report-ir", "valid", "monthly-report.json");

    private static string EvidencePath => SchemaExamples.ExamplePath("evidence-ir", "valid", "monthly-github-gitlab.json");

    private static string StressReportPath => Output("Fixtures", "stress", "report.json");

    private static string StressEvidencePath => Output("Fixtures", "stress", "evidence.json");

    private static TemplateManifest Manifest => Load<TemplateManifest>(Output("templates", "sample-monthly", "manifest.json"));

    private static ReportDocument Report => Load<ReportDocument>(ReportPath);

    private static EvidenceDocument Evidence => Load<EvidenceDocument>(EvidencePath);

    private static PresentationDocument ResolveOk(ReportDocument report, EvidenceDocument evidence, TemplateManifest manifest, string sha = "")
    {
        var result = Resolver.Resolve(report, sha.Length > 0 ? sha : new string('0', 64), evidence, manifest);
        Assert.Empty(result.Errors);
        return result.Presentation!;
    }

    private static PresentationDocument Example() => ResolveOk(Report, Evidence, Manifest, Sha256(ReportPath));

    private static PresentationDocument Stress() =>
        ResolveOk(Load<ReportDocument>(StressReportPath), Load<EvidenceDocument>(StressEvidencePath), Manifest, Sha256(StressReportPath));

    private static string Text(Slide slide, string slot) => Assert.IsType<TextSlotValue>(slide.Bindings[slot]).Text;

    private static IReadOnlyList<BulletItem> Bullets(Slide slide, string slot) => Assert.IsType<BulletsSlotValue>(slide.Bindings[slot]).Items;

    private static TableSlotValue Table(Slide slide, string slot) => Assert.IsType<TableSlotValue>(slide.Bindings[slot]);

    // The checked-in presentation example is exactly the resolver output for the example report and evidence.
    [Fact]
    public void Example_output_equals_golden_bytes()
    {
        var golden = File.ReadAllBytes(SchemaExamples.ExamplePath("presentation-ir", "valid", "monthly-presentation.json"));

        Assert.Equal(golden, WorkLensJson.SerializeCanonicalUtf8(Example()));
    }

    [Fact]
    public void Output_is_deterministic() =>
        Assert.Equal(WorkLensJson.SerializeCanonicalUtf8(Stress()), WorkLensJson.SerializeCanonicalUtf8(Stress()));

    [Fact]
    public void Example_key_slides()
    {
        var presentation = Example();

        Assert.Equal("prs-2026-10-example-user", presentation.PresentationId);
        Assert.Equal(Sha256(ReportPath), presentation.SourceReport.Sha256);
        Assert.Equal(
            ["cover_v1", "summary_v1", "achievements_v1", "achievement_detail_v1", "achievement_detail_v1", "achievement_detail_v1", "narrative_v1", "narrative_v1"],
            presentation.Slides.Select(s => s.LayoutId));
        Assert.Equal(
            Enumerable.Range(1, 8).Select(i => $"slide-{i:000}"),
            presentation.Slides.Select(s => s.Id));

        var cover = presentation.Slides[0];
        Assert.Equal("2026-10 工作月報", Text(cover, "title"));
        Assert.Equal("Example User", Text(cover, "subtitle"));
        Assert.Empty(cover.SourceRefs);

        var summary = presentation.Slides[1];
        Assert.Equal(["title", "highlights", "activity"], summary.Bindings.Keys);
        Assert.Equal(
            [new BulletItem("完成 2 項工作, 1 項待確認", 0), new BulletItem("2 個 repository, 1 個已合併的 PR", 1)],
            Bullets(summary, "highlights"));
        var activity = Table(summary, "activity");
        Assert.Equal(["Repository", "Commits", "Merged PR/MR"], activity.Columns);
        Assert.Equal([["example-org/auth-service", "1", "1"], ["platform/billing", "1", "0"]], activity.Rows);
        Assert.Null(activity.Truncated);

        // Rank order: rank 1, rank 2, then the item without rank.
        var achievements = Table(presentation.Slides[2], "items");
        Assert.Equal(["使用者驗證機制優化", "修正新台幣發票進位", "新進同仁 Onboarding"], achievements.Rows.Select(r => r[0]));
        Assert.Equal(["完成", "待確認", "完成"], achievements.Rows.Select(r => r[1]));

        var auth = Bullets(presentation.Slides[3], "facts");
        Assert.Equal(new BulletItem("(推論, 信心: 中) 降低被竊取的 Refresh Token 被重複使用的風險。", 1), auth[^1]);
        Assert.Equal(["wi-auth-refresh-rotation", "ev-1a2b3c4d5e6f7a8b", "ev-2b3c4d5e6f7a8b9c"], presentation.Slides[3].SourceRefs);

        Assert.Equal(new BulletItem("(狀態: 待確認)", 1), Bullets(presentation.Slides[4], "facts")[^1]);

        var onboarding = Bullets(presentation.Slides[5], "facts");
        Assert.Equal(new BulletItem("協助兩位新進同仁完成開發環境建置與 code review 流程。", 0), onboarding[0]);
        Assert.Equal(new BulletItem("(人工補充) 非 Git 活動, 人工補充。", 1), onboarding[1]);

        Assert.Equal("遇到的問題", Text(presentation.Slides[6], "title"));
        Assert.Equal("下月規劃", Text(presentation.Slides[7], "title"));
    }

    [Fact]
    public void Stress_report_is_bound_to_stress_evidence_bytes() =>
        Assert.Equal(Sha256(StressEvidencePath), Load<ReportDocument>(StressReportPath).EvidenceDocument.Sha256);

    [Fact]
    public void Stress_overflow_splits_and_truncates()
    {
        var presentation = Stress();
        var maxFacts = Manifest.Layouts.Single(l => l.Id == "achievement_detail_v1").Slots.Single(s => s.Id == "facts").MaxItems!.Value;
        var factSlides = presentation.Slides
            .Where(s => s.LayoutId == "achievement_detail_v1" && s.SourceRefs[0] == "wi-stress-twenty-facts")
            .ToList();

        // 20 facts split by max_items onto continuation slides with the same title.
        var pages = (20 + maxFacts - 1) / maxFacts;
        Assert.Equal(pages, factSlides.Count);
        Assert.Equal(Enumerable.Range(1, pages).Select(i => new SlideContinuation(i, pages)), factSlides.Select(s => s.Continuation));
        Assert.All(factSlides, s => Assert.Equal("二十項事實的工作項目", Text(s, "title")));
        Assert.Equal(20, factSlides.Sum(s => Bullets(s, "facts").Count));

        // The 120-character title is truncated to max_chars text elements, ending with an ellipsis.
        var titleSlot = Manifest.Layouts.Single(l => l.Id == "achievement_detail_v1").Slots.Single(s => s.Id == "title");
        var longTitle = presentation.Slides.Single(s => s.LayoutId == "achievement_detail_v1" && s.SourceRefs[0] == "wi-stress-long-title");
        var title = Assert.IsType<TextSlotValue>(longTitle.Bindings["title"]);
        Assert.True(title.Truncated);
        Assert.Equal(titleSlot.MaxChars, new StringInfo(title.Text).LengthInTextElements);
        Assert.EndsWith("…", title.Text, StringComparison.Ordinal);

        // No facts: the summary is the first bullet, then the in-progress status line.
        var facts = Bullets(longTitle, "facts");
        Assert.Equal(0, facts[0].Level);
        Assert.EndsWith("…", facts[0].Text, StringComparison.Ordinal);
        Assert.Equal(new BulletItem("(狀態: 進行中)", 1), facts[1]);

        // Rejected items appear nowhere.
        Assert.DoesNotContain(presentation.Slides, s => s.SourceRefs.Contains("wi-stress-rejected"));
        Assert.Equal(2, Table(presentation.Slides.Single(s => s.LayoutId == "achievements_v1"), "items").Rows.Count);

        // Statistics exclude the out-of-period and duplicate commits.
        var summary = presentation.Slides.Single(s => s.LayoutId == "summary_v1");
        Assert.Equal([["example-org/stress-service", "10", "1"], ["platform/stress-billing", "11", "0"]], Table(summary, "activity").Rows);
        Assert.DoesNotContain("ev-c000000000000100", summary.SourceRefs);
        Assert.DoesNotContain("ev-c000000000000101", summary.SourceRefs);
        Assert.Contains("ev-c000000000000102", summary.SourceRefs);
    }

    [Fact]
    public void Text_without_xml_invalid_characters()
    {
        var report = Report;
        var item = report.WorkItems[0];
        var fact = item.ObservedFacts[0] with { Statement = "esc\u001B char and \uD800 lone surrogate 😀" };
        report = report with { WorkItems = [item with { ObservedFacts = [fact, .. item.ObservedFacts.Skip(1)] }, .. report.WorkItems.Skip(1)] };

        var presentation = ResolveOk(report, Evidence, Manifest);

        Assert.Equal("esc char and  lone surrogate 😀", Bullets(presentation.Slides[3], "facts")[0].Text);
    }

    [Fact]
    public void Empty_sections_produce_no_slide()
    {
        // The sample sequence includes improvements and other_work; the example report has neither.
        var report = Report with
        {
            WorkItems = [.. Report.WorkItems.Select(w => w with { ReviewStatus = ReviewStatus.Rejected })],
        };

        var presentation = ResolveOk(report, Evidence, Manifest);

        Assert.Equal(["cover_v1", "summary_v1", "narrative_v1", "narrative_v1"], presentation.Slides.Select(s => s.LayoutId));
    }

    [Fact]
    public void Every_narrative_kind_has_a_slide_in_the_sample_sequence()
    {
        var report = Report with
        {
            Narratives =
            [
                .. Report.Narratives,
                new Narrative("nr-improvement", NarrativeKind.Improvement, ContentOrigin.Human, "改善項目"),
                new Narrative("nr-other", NarrativeKind.OtherWork, ContentOrigin.Human, "其他工作項目"),
            ],
        };

        var narratives = ResolveOk(report, Evidence, Manifest).Slides.Where(s => s.LayoutId == "narrative_v1").ToList();

        Assert.Equal(["遇到的問題", "改善事項", "下月規劃", "其他工作"], narratives.Select(s => Text(s, "title")));
        Assert.Equal(["nr-improvement"], narratives[1].SourceRefs);
        Assert.Equal(["nr-other"], narratives[3].SourceRefs);
    }

    [Fact]
    public void Missing_labels_fall_back_to_english()
    {
        var presentation = ResolveOk(Report, Evidence, Manifest with { Labels = null });

        Assert.Equal("2026-10 Work Report", Text(presentation.Slides[0], "title"));
        Assert.Equal(["Work item", "Status"], Table(presentation.Slides[2], "items").Columns);
    }

    [Fact]
    public void Label_substitution_is_single_pass() =>
        Assert.Equal(
            "x {period} y {unknown}",
            LayoutLabels.Format("x {name} y {unknown}", new Dictionary<string, string> { ["name"] = "{period}", ["period"] = "P" }));

    [Fact]
    public void Unknown_layout_is_an_error()
    {
        var manifest = Manifest with { Sequence = [.. Manifest.Sequence, new SequenceEntry(ReportSection.Closing, "closing_v1")] };

        AssertSingleError(manifest, Evidence, "manifest.unknown_layout", $"/sequence/{Manifest.Sequence.Count}/layout_id");
    }

    [Fact]
    public void Purpose_mismatch_is_an_error()
    {
        var manifest = Manifest with { Sequence = [.. Manifest.Sequence, new SequenceEntry(ReportSection.Closing, "narrative_v1")] };

        AssertSingleError(manifest, Evidence, "manifest.purpose_mismatch", $"/sequence/{Manifest.Sequence.Count}/layout_id");
    }

    [Fact]
    public void Missing_required_purpose_slot_is_an_error()
    {
        var index = LayoutIndex("achievement_detail_v1");
        var manifest = ReplaceLayout(index, l => l with { Slots = [.. l.Slots.Where(s => s.Id != "facts")] });

        AssertSingleError(manifest, Evidence, "manifest.slot_mismatch", $"/layouts/{index}/slots");
    }

    [Fact]
    public void Wrong_slot_kind_is_an_error()
    {
        var index = LayoutIndex("summary_v1");
        var manifest = ReplaceLayout(index, l => l with { Slots = [.. l.Slots.Select(s => s.Id == "activity" ? s with { Kind = SlotKind.Bullets } : s)] });

        AssertSingleError(manifest, Evidence, "manifest.slot_mismatch", $"/layouts/{index}/slots/2/kind");
    }

    [Fact]
    public void Required_slot_without_content_is_an_error()
    {
        var index = LayoutIndex("summary_v1");
        var manifest = ReplaceLayout(index, l => l with { Slots = [.. l.Slots.Select(s => s.Id == "activity" ? s with { Required = true } : s)] });
        var evidence = Evidence with { Query = Evidence.Query with { Sources = [] } };

        AssertSingleError(manifest, evidence, "manifest.slot_mismatch", $"/layouts/{index}/slots/2");
    }

    [Fact]
    public void Required_slot_outside_purpose_conventions_is_an_error()
    {
        var index = LayoutIndex("cover_v1");
        var logo = new SlotDefinition("logo", new PlaceholderRef(Idx: 1), SlotKind.Text, Required: true);
        var manifest = ReplaceLayout(index, l => l with { Slots = [.. l.Slots, logo] });

        AssertSingleError(manifest, Evidence, "manifest.slot_mismatch", $"/layouts/{index}/slots/{Manifest.Layouts[index].Slots.Count}");
    }

    [Fact]
    public void Duplicate_layout_id_is_an_error()
    {
        var manifest = Manifest with { Layouts = [.. Manifest.Layouts, Manifest.Layouts[0]] };

        AssertSingleError(manifest, Evidence, "manifest.duplicate_id", $"/layouts/{Manifest.Layouts.Count}/id");
    }

    [Fact]
    public void Duplicate_slot_id_is_an_error()
    {
        var index = LayoutIndex("summary_v1");
        var manifest = ReplaceLayout(index, l => l with { Slots = [.. l.Slots, l.Slots[0]] });

        AssertSingleError(manifest, Evidence, "manifest.duplicate_id", $"/layouts/{index}/slots/{Manifest.Layouts[index].Slots.Count}/id");
    }

    private static int LayoutIndex(string id) => Manifest.Layouts.Select((l, i) => (l, i)).Single(x => x.l.Id == id).i;

    private static TemplateManifest ReplaceLayout(int index, Func<LayoutDefinition, LayoutDefinition> change) =>
        Manifest with { Layouts = [.. Manifest.Layouts.Select((l, i) => i == index ? change(l) : l)] };

    private static void AssertSingleError(TemplateManifest manifest, EvidenceDocument evidence, string code, string path)
    {
        var result = Resolver.Resolve(Report, new string('0', 64), evidence, manifest);

        Assert.Null(result.Presentation);
        var error = Assert.Single(result.Errors);
        Assert.Equal(code, error.Code);
        Assert.Equal(path, error.Details!["path"]);
    }
}
