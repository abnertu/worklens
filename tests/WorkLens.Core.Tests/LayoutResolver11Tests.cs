using System.Text.Json;
using WorkLens.Core.Evidence;
using WorkLens.Core.Json;
using WorkLens.Core.Layout;
using WorkLens.Core.Presentation;
using WorkLens.Core.Report;
using WorkLens.Core.Templates;
using Xunit;

namespace WorkLens.Core.Tests;

/// <summary>The Layout rules of a 1.1 manifest. LayoutResolverTests covers the 1.0 rules.</summary>
public sealed class LayoutResolver11Tests
{
    private static readonly LayoutResolver Resolver = new();

    private static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), WorkLensJson.Options)!;

    private static TemplateManifest Manifest =>
        Load<TemplateManifest>(SchemaExamples.ExamplePath("template-manifest", "1.1", "valid", "rd-monthly.json"));

    private static ReportDocument Report => Load<ReportDocument>(SchemaExamples.ExamplePath("report-ir", "1.1", "valid", "monthly-report.json"));

    private static EvidenceDocument Evidence =>
        Load<EvidenceDocument>(SchemaExamples.ExamplePath("evidence-ir", "valid", "monthly-github-gitlab.json"));

    private static PresentationDocument ResolveOk(TemplateManifest manifest, ReportDocument? report = null)
    {
        var result = Resolver.Resolve(report ?? Report, new string('0', 64), Evidence, manifest);
        Assert.Empty(result.Errors);
        return result.Presentation!;
    }

    private static string Text(Slide slide, string slot) => Assert.IsType<TextSlotValue>(slide.Bindings[slot]).Text;

    private static IReadOnlyList<BulletItem> Bullets(Slide slide, string slot) => Assert.IsType<BulletsSlotValue>(slide.Bindings[slot]).Items;

    private static Slide NarrativeSlide(PresentationDocument presentation, string narrativeId) =>
        presentation.Slides.Single(s => s.LayoutId == "narrative_v1" && s.SourceRefs.Contains(narrativeId));

    // The example manifest gives challenges a challenge_table layout, so this also shows that 1.1 accepts it.
    [Fact]
    public void Example_manifest_resolves_the_example_report()
    {
        var presentation = ResolveOk(Manifest);

        Assert.Equal(
            ["cover_v1", "summary_v1", "achievements_v1", "detail_v1", "detail_v1", "operations_v1", "detail_v1", "challenges_v1", "narrative_v1", "narrative_v1", "closing_v1"],
            presentation.Slides.Select(s => s.LayoutId));
    }

    private const string Auth = "wi-auth-refresh-rotation";
    private const string Billing = "wi-billing-rounding-fix";
    private const string Onboarding = "wi-onboarding-mentoring";

    private static Slide DetailOf(PresentationDocument presentation, string itemId) =>
        presentation.Slides.Single(s => s.LayoutId == "detail_v1" && s.SourceRefs[0] == itemId);

    private static Slide? Optional(PresentationDocument presentation, string layoutId) => presentation.Slides.SingleOrDefault(s => s.LayoutId == layoutId);

    [Fact]
    public void Achievements_table_takes_the_slot_columns_and_column_labels()
    {
        var slide = SlideOf(ResolveOk(Manifest), "achievements_v1");

        var table = Assert.IsType<TableSlotValue>(slide.Bindings["items"]);
        Assert.Equal(["工作項目", "專案", "狀態", "證據"], table.Columns);
        Assert.Equal([WorkItem(Auth).Title, "auth-service", "Completed", "#42 · 1 個 commit"], table.Rows[0]);
        Assert.Equal([WorkItem(Billing).Title, "billing", "To confirm", "1 個 commit"], table.Rows[1]);
        Assert.Equal([null, null, "tag_completed", "meta"], table.CellStyles![0]);
        Assert.Equal("tag_unknown", table.CellStyles[1][2]);
        Assert.Equal("1 completed, 0 in progress, 1 to confirm", Text(slide, "status"));
        Assert.Equal([Auth, Billing], slide.SourceRefs);
    }

    [Fact]
    public void Achievements_table_without_slot_columns_has_the_two_default_columns()
    {
        var slide = SlideOf(ResolveOk(WithSlot("achievements_v1", "items", s => s with { Columns = null })), "achievements_v1");

        var table = Assert.IsType<TableSlotValue>(slide.Bindings["items"]);
        Assert.Equal(["Work item", "Status"], table.Columns);
        Assert.Equal([WorkItem(Auth).Title, "Completed"], table.Rows[0]);
    }

    [Fact]
    public void Operations_table_without_slot_columns_has_the_three_default_columns()
    {
        var slide = SlideOf(ResolveOk(Manifest), "operations_v1");

        var table = Assert.IsType<TableSlotValue>(slide.Bindings["items"]);
        Assert.Equal(["工作項目", "狀態", "證據"], table.Columns);
        Assert.Equal([WorkItem(Onboarding).Title, "Completed", "人工補充"], Assert.Single(table.Rows));
        Assert.Equal([Onboarding], slide.SourceRefs);
    }

    // Ops items leave the achievements table only when the operations table shows them.
    [Fact]
    public void Achievements_table_keeps_ops_items_without_an_operations_section()
    {
        var slide = SlideOf(ResolveOk(WithoutSections(ReportSection.Operations)), "achievements_v1");

        Assert.Equal([Auth, Billing, Onboarding], slide.SourceRefs);
    }

    // Ops detail slides leave the development details only when the operation details show them.
    [Fact]
    public void Development_details_keep_ops_items_without_an_operation_details_section()
    {
        var presentation = ResolveOk(WithoutSections(ReportSection.OperationDetails));

        Assert.Equal(3, presentation.Slides.Count(s => s.LayoutId == "detail_v1"));
        Assert.Equal("開發成果 · —", Text(DetailOf(presentation, Onboarding), "kicker"));
    }

    [Fact]
    public void Table_without_rows_gives_no_slide()
    {
        var report = Report with { WorkItems = [.. Report.WorkItems.Where(w => w.Category != WorkCategory.Ops)] };

        Assert.Null(Optional(ResolveOk(Manifest, report), "operations_v1"));
    }

    [Fact]
    public void Detail_shows_kicker_status_tag_and_facts_without_a_status_line()
    {
        var presentation = ResolveOk(Manifest);
        var auth = DetailOf(presentation, Auth);
        var billing = DetailOf(presentation, Billing);

        Assert.Equal("開發成果 · auth-service", Text(auth, "kicker"));
        Assert.Equal("維運工作 · —", Text(DetailOf(presentation, Onboarding), "kicker"));
        Assert.Equal(new TextSlotValue("To confirm") { Style = "tag_unknown" }, billing.Bindings["status"]);
        Assert.Equal("重點", Text(auth, "facts_title"));
        Assert.Equal("證據", Text(auth, "evidence_title"));
        Assert.DoesNotContain(Bullets(billing, "facts"), i => i.Text.Contains("to confirm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Detail_evidence_lists_each_change_request_then_the_other_commits()
    {
        var presentation = ResolveOk(Manifest);
        var change = Evidence.Items.Single(e => e.Id == "ev-1a2b3c4d5e6f7a8b");

        Assert.Equal(
            [
                new BulletItem("#42", 0, "evidence_id"),
                new BulletItem(change.Title, 1),
                new BulletItem("已合併 · 2026-10-14 · 1 個 commit", 1, "meta"),
            ],
            Bullets(DetailOf(presentation, Auth), "evidence"));
        Assert.Equal([new BulletItem("1 個 commit", 0, "meta")], Bullets(DetailOf(presentation, Billing), "evidence"));
        Assert.Equal([new BulletItem("人工補充", 0, "meta")], Bullets(DetailOf(presentation, Onboarding), "evidence"));
    }

    [Fact]
    public void Detail_evidence_with_refs_to_nothing_is_a_dash()
    {
        var report = Report with { WorkItems = [.. Report.WorkItems.Select(w => w.Id == Billing ? w with { EvidenceRefs = ["missing"], ObservedFacts = [] } : w)] };

        Assert.Equal([new BulletItem("—", 0, "meta")], Bullets(DetailOf(ResolveOk(Manifest, report), Billing), "evidence"));
    }

    // A change request is an ID, a title and a meta line, so max_items keeps them together; the first one keeps what fits.
    [Fact]
    public void Detail_evidence_keeps_whole_change_requests_within_max_items()
    {
        var slide = DetailOf(ResolveOk(WithSlot("detail_v1", "evidence", s => s with { MaxItems = 2 })), Auth);

        var evidence = Assert.IsType<BulletsSlotValue>(slide.Bindings["evidence"]);
        Assert.Equal(["#42", Evidence.Items.Single(e => e.Id == "ev-1a2b3c4d5e6f7a8b").Title], evidence.Items.Select(i => i.Text));
        Assert.True(evidence.Truncated);
    }

    // Evidence IR titles come from the platform and go through the disclosure rules; work item text shows as written.
    [Fact]
    public void Evidence_titles_lose_links_but_work_item_titles_do_not()
    {
        const string Url = "https://git.example.com/x";
        var evidence = Evidence with
        {
            Items = [.. Evidence.Items.Select(e => e.Id == "ev-1a2b3c4d5e6f7a8b" ? e with { Title = "Rotate tokens " + Url } : e)],
        };
        var report = Report with { WorkItems = [.. Report.WorkItems.Select(w => w.Id == Auth ? w with { Title = "Auth " + Url } : w)] };
        var manifest = WithSlot("detail_v1", "title", s => s with { MaxChars = null });

        var result = Resolver.Resolve(report, new string('0', 64), evidence, manifest);

        var slide = DetailOf(result.Presentation!, Auth);
        Assert.Equal("Auth " + Url, Text(slide, "title"));
        Assert.Equal("Rotate tokens", Bullets(slide, "evidence")[1].Text);
    }

    private static Slide SlideOf(PresentationDocument presentation, string layoutId) => presentation.Slides.Single(s => s.LayoutId == layoutId);

    private static MetricSlotValue Metric(Slide slide, string slot) => Assert.IsType<MetricSlotValue>(slide.Bindings[slot]);

    private static WorkItem WorkItem(string id) => Report.WorkItems.Single(w => w.Id == id);

    private static TemplateManifest WithoutSections(params ReportSection[] sections) =>
        Manifest with { Sequence = [.. Manifest.Sequence.Where(e => !sections.Contains(e.Section))] };

    private static TemplateManifest WithSlot(string layoutId, string slotId, Func<SlotDefinition, SlotDefinition> change) =>
        ReplaceSlot(LayoutIndex(layoutId), slotId, change);

    [Fact]
    public void Cover_binds_each_cover_element_and_no_source_refs()
    {
        var cover = SlideOf(ResolveOk(Manifest), "cover_v1");

        Assert.Equal("2026-10", Text(cover, "title"));
        Assert.Equal("月報", Text(cover, "kicker"));
        Assert.Equal("研發工作月報", Text(cover, "subtitle"));
        Assert.Equal("Example User", Text(cover, "subject"));
        Assert.Equal("資料來源: GitHub · GitLab · 人工補充", Text(cover, "meta"));
        Assert.Equal("2026", Text(cover, "band_year"));
        Assert.Equal("10", Text(cover, "band_month"));
        Assert.Empty(cover.SourceRefs);
    }

    [Fact]
    public void Cover_labels_take_the_period_names()
    {
        var manifest = Manifest with
        {
            Labels = Manifest.Labels! with { PeriodTitle = "{year}/{month} ({period})", CoverSources = "{start}..{end} {timezone}" },
        };

        var cover = SlideOf(ResolveOk(manifest), "cover_v1");

        Assert.Equal("2026/10 (2026-10)", Text(cover, "title"));
        Assert.Equal("2026-10-01..2026-10-31 Asia/Taipei", Text(cover, "meta"));
    }

    // A label that is not yyyy-MM has no year or month, so the band slots stay unbound.
    [Fact]
    public void Cover_without_a_month_label_leaves_the_band_unbound()
    {
        var cover = SlideOf(ResolveOk(Manifest, Report with { Period = Report.Period with { Label = "Q4" } }), "cover_v1");

        Assert.False(cover.Bindings.ContainsKey("band_year"));
        Assert.False(cover.Bindings.ContainsKey("band_month"));
    }

    [Fact]
    public void Summary_cards_leave_ops_to_the_operations_card_when_there_is_an_operations_section()
    {
        var summary = SlideOf(ResolveOk(Manifest), "summary_v1");

        Assert.Equal(new MetricSlotValue("完成", "1", "項工作完成"), Metric(summary, "metric_completed"));
        Assert.Equal("0", Metric(summary, "metric_in_progress").Value);
        Assert.Equal(new MetricSlotValue("維運", "1", "項維運工作"), Metric(summary, "metric_operations"));
        Assert.Equal(new MetricSlotValue("專案", "2", "個專案"), Metric(summary, "metric_projects"));
    }

    [Theory]
    [InlineData(ReportSection.Operations)]
    [InlineData(ReportSection.Operations, ReportSection.OperationDetails)]
    public void Summary_cards_count_ops_as_development_without_an_operations_section(params ReportSection[] removed)
    {
        var summary = SlideOf(ResolveOk(WithoutSections(removed)), "summary_v1");

        Assert.Equal("2", Metric(summary, "metric_completed").Value);
        Assert.False(summary.Bindings.ContainsKey("metric_operations"));
    }

    [Fact]
    public void Summary_shows_the_period_titles_and_activity()
    {
        var summary = SlideOf(ResolveOk(Manifest), "summary_v1");

        Assert.Equal("2026-10-01 – 2026-10-31", Text(summary, "period"));
        Assert.Equal("本月亮點", Text(summary, "highlights_title"));
        Assert.Equal("2 repositories, 2 commits, 1 merged changes", Text(summary, "activity_note"));
    }

    [Fact]
    public void Highlights_list_completed_items_with_a_meta_line()
    {
        var items = Bullets(SlideOf(ResolveOk(Manifest), "summary_v1"), "highlights");

        Assert.Equal(
            [
                new BulletItem(WorkItem("wi-auth-refresh-rotation").Title, 0),
                new BulletItem("auth-service · #42 · 1 個 commit", 1, "meta"),
                new BulletItem(WorkItem("wi-onboarding-mentoring").Title, 0),
                new BulletItem("人工補充", 1, "meta"),
            ],
            items);
    }

    // Each highlight is a title and its meta line, so max_items keeps or drops both.
    [Theory]
    [InlineData(3, 2)]
    [InlineData(1, 1)]
    public void Highlights_keep_whole_items_within_max_items(int maxItems, int kept)
    {
        var summary = SlideOf(ResolveOk(WithSlot("summary_v1", "highlights", s => s with { MaxItems = maxItems })), "summary_v1");

        var value = Assert.IsType<BulletsSlotValue>(summary.Bindings["highlights"]);
        Assert.Equal(kept, value.Items.Count);
        Assert.Equal(WorkItem("wi-auth-refresh-rotation").Title, value.Items[0].Text);
        Assert.True(value.Truncated);
    }

    [Fact]
    public void Highlights_without_completed_work_show_the_empty_text()
    {
        var report = Report with { WorkItems = [.. Report.WorkItems.Select(w => w with { Status = WorkStatus.InProgress })] };

        var items = Bullets(SlideOf(ResolveOk(Manifest, report), "summary_v1"), "highlights");

        Assert.Equal([new BulletItem("本月沒有亮點", 0, "empty")], items);
    }

    private static ReportDocument WithSupport(int count)
    {
        var support = Report.Narratives.Single(n => n.Kind == NarrativeKind.Support);
        return Report with
        {
            Narratives =
            [
                .. Report.Narratives.Where(n => n.Kind != NarrativeKind.Support),
                .. Enumerable.Range(1, count).Select(i => support with { Id = $"nr-support-{i}", Text = $"support {i}" }),
            ],
        };
    }

    [Fact]
    public void Support_shows_two_narratives_and_a_line_for_the_rest()
    {
        var summary = SlideOf(ResolveOk(Manifest, WithSupport(4)), "summary_v1");

        var support = Assert.IsType<BulletsSlotValue>(summary.Bindings["support"]);
        Assert.Equal("support", support.Style);
        Assert.Equal(
            [new BulletItem("support 1", 0), new BulletItem("support 2", 0), new BulletItem("另有 2 項", 0, "meta")],
            support.Items);
        Assert.Equal("support_title", summary.Bindings["support_title"].Style);
    }

    // Two narratives do not fit one item, so the one item says how many there are.
    [Fact]
    public void Support_over_max_items_moves_every_narrative_to_the_more_line()
    {
        var manifest = WithSlot("summary_v1", "support", s => s with { MaxItems = 1 });

        var support = Bullets(SlideOf(ResolveOk(manifest, WithSupport(2)), "summary_v1"), "support");

        Assert.Equal([new BulletItem("另有 2 項", 0, "meta")], support);
    }

    [Fact]
    public void Highlights_show_at_most_three_completed_items()
    {
        var auth = WorkItem(Auth);
        var report = Report with
        {
            WorkItems = [.. Report.WorkItems, .. Enumerable.Range(1, 2).Select(i => auth with { Id = $"wi-extra-{i}", Rank = 10 + i })],
        };
        var manifest = WithSlot("summary_v1", "highlights", s => s with { MaxItems = null });

        var items = Bullets(SlideOf(ResolveOk(manifest, report), "summary_v1"), "highlights");

        Assert.Equal(6, items.Count);
    }

    // The cards and the activity note count the repositories of the counted evidence, not the query sources.
    [Fact]
    public void Summary_counts_repositories_from_the_counted_evidence()
    {
        var unused = new SourceSpec(Common.SourcePlatform.Gitlab, "https://gitlab.example.com/api/v4", "platform/unused");
        var evidence = Evidence with { Query = Evidence.Query with { Sources = [.. Evidence.Query.Sources, unused] } };

        var result = Resolver.Resolve(Report, new string('0', 64), evidence, Manifest);

        var summary = SlideOf(result.Presentation!, "summary_v1");
        Assert.Equal("2", Metric(summary, "metric_projects").Value);
        Assert.StartsWith("2 repositories", Text(summary, "activity_note"), StringComparison.Ordinal);
    }

    // A challenge table without an items slot shows no narrative, so the narratives are unplaced.
    [Fact]
    public void Challenge_table_without_an_items_slot_places_no_narrative()
    {
        var index = LayoutIndex("challenges_v1");
        var manifest = Manifest with
        {
            Layouts = [.. Manifest.Layouts.Select((l, i) => i == index ? l with { Slots = [.. l.Slots.Where(s => s.Id != "items")] } : l)],
        };

        var result = Resolver.Resolve(Report, new string('0', 64), Evidence, manifest);

        Assert.Empty(SlideOf(result.Presentation!, "challenges_v1").SourceRefs);
        Assert.Contains(result.Warnings, w => w.Details!["path"] == "/narratives/0");
    }

    // The support_more line must fit, so max_items 2 leaves room for one narrative.
    [Fact]
    public void Support_shows_fewer_narratives_to_fit_the_more_line()
    {
        var manifest = WithSlot("summary_v1", "support", s => s with { MaxItems = 2 });

        var support = Bullets(SlideOf(ResolveOk(manifest, WithSupport(4)), "summary_v1"), "support");

        Assert.Equal([new BulletItem("support 1", 0), new BulletItem("另有 3 項", 0, "meta")], support);
    }

    [Fact]
    public void Support_without_narratives_shows_the_empty_text_and_empty_styles()
    {
        var summary = SlideOf(ResolveOk(WithoutSections(ReportSection.Support), WithSupport(0)), "summary_v1");

        var support = Assert.IsType<BulletsSlotValue>(summary.Bindings["support"]);
        Assert.Equal("support_empty", support.Style);
        Assert.Equal([new BulletItem("目前不需要支援", 0)], support.Items);
        Assert.Equal("support_title_empty", summary.Bindings["support_title"].Style);
    }

    // The summary places the first two support narratives; the third needs the support page.
    [Fact]
    public void Summary_source_refs_list_items_evidence_then_the_first_two_support()
    {
        var presentation = Resolver.Resolve(WithSupport(3), new string('0', 64), Evidence, WithoutSections(ReportSection.Support));

        var summary = SlideOf(presentation.Presentation!, "summary_v1");
        Assert.Equal(
            [
                "wi-auth-refresh-rotation", "wi-billing-rounding-fix", "wi-onboarding-mentoring",
                "ev-1a2b3c4d5e6f7a8b", "ev-2b3c4d5e6f7a8b9c", "ev-3c4d5e6f7a8b9c0d",
                "nr-support-1", "nr-support-2",
            ],
            summary.SourceRefs);
        Assert.Contains(presentation.Warnings, w => w.Details!["path"] == $"/narratives/{WithSupport(3).Narratives.Count - 1}");
    }

    // Without a support slot, the summary shows no support, so it places none.
    [Fact]
    public void Summary_without_a_support_slot_places_no_support()
    {
        var index = LayoutIndex("summary_v1");
        var manifest = WithoutSections(ReportSection.Support) with
        {
            Layouts = [.. Manifest.Layouts.Select((l, i) => i == index ? l with { Slots = [.. l.Slots.Where(s => s.Id != "support")] } : l)],
        };

        var result = Resolver.Resolve(Report, new string('0', 64), Evidence, manifest);

        Assert.DoesNotContain("nr-support-billing-team", SlideOf(result.Presentation!, "summary_v1").SourceRefs);
        Assert.Contains(result.Warnings, w => w.Code == "layout.unplaced_narrative");
    }

    [Fact]
    public void Support_page_takes_the_support_title()
    {
        var slide = NarrativeSlide(ResolveOk(Manifest), "nr-support-billing-team");

        Assert.Equal("需要支援", Text(slide, "title"));
        Assert.Equal(["nr-support-billing-team"], slide.SourceRefs);
    }

    [Fact]
    public void Next_plan_items_are_numbered_and_other_narratives_have_no_style()
    {
        var presentation = ResolveOk(Manifest);

        Assert.All(Bullets(NarrativeSlide(presentation, "nr-next-plan-sso"), "body"), i => Assert.Equal("numbered", i.Style));
        Assert.All(Bullets(NarrativeSlide(presentation, "nr-support-billing-team"), "body"), i => Assert.Null(i.Style));
    }

    [Fact]
    public void Closing_has_the_closing_title_and_no_source_refs()
    {
        var closing = ResolveOk(Manifest).Slides[^1];

        Assert.Equal("謝謝", Text(closing, "title"));
        Assert.Empty(closing.SourceRefs);
    }

    [Fact]
    public void Challenges_take_a_narrative_or_a_challenge_table_layout()
    {
        var manifest = Manifest with
        {
            Sequence = [.. Manifest.Sequence.Select(e => e.Section == ReportSection.Challenges ? e with { LayoutId = "narrative_v1" } : e)],
        };

        var presentation = ResolveOk(manifest);

        Assert.Equal("問題與解決", Text(NarrativeSlide(presentation, "nr-challenge-token-migration"), "title"));
        // Without a challenge table, an improvement that resolves a challenge stays on the improvements page.
        Assert.Equal("改善事項", Text(NarrativeSlide(presentation, "nr-improvement-dual-token"), "title"));
    }

    private static ReportDocument WithChallenges()
    {
        var challenge = Report.Narratives.Single(n => n.Kind == NarrativeKind.Challenge);
        var improvement = Report.Narratives.Single(n => n.Kind == NarrativeKind.Improvement);
        return Report with
        {
            Narratives =
            [
                .. Report.Narratives,
                improvement with { Id = "nr-improvement-2", Text = "second fix" },
                challenge with { Id = "nr-challenge-open", Text = "open problem" },
                improvement with { Id = "nr-improvement-free", Text = "free improvement", Resolves = null },
            ],
        };
    }

    [Fact]
    public void Challenge_table_lists_each_challenge_with_its_improvements_and_status()
    {
        var slide = SlideOf(ResolveOk(Manifest, WithChallenges()), "challenges_v1");

        var table = Assert.IsType<TableSlotValue>(slide.Bindings["items"]);
        var challenge = Report.Narratives.Single(n => n.Kind == NarrativeKind.Challenge);
        var improvement = Report.Narratives.Single(n => n.Kind == NarrativeKind.Improvement);
        Assert.Equal(["問題", "解決方式", "狀態"], table.Columns);
        Assert.Equal([challenge.Text, improvement.Text + "\nsecond fix", "已解決"], table.Rows[0]);
        Assert.Equal(["open problem", "—", "處理中"], table.Rows[1]);
        Assert.Equal([[null, null, "tag_resolved"], [null, null, "tag_open"]], table.CellStyles!.Select(r => r.ToArray()));
        Assert.Equal("已處理 1 · 處理中 1", Text(slide, "status"));
        Assert.Equal([challenge.Id, improvement.Id, "nr-improvement-2", "nr-challenge-open"], slide.SourceRefs);
    }

    // With a challenge table, only improvements that resolve no challenge stay on the improvements page.
    [Fact]
    public void Challenge_table_takes_the_improvements_that_resolve_a_challenge()
    {
        var slide = NarrativeSlide(ResolveOk(Manifest, WithChallenges()), "nr-improvement-free");

        Assert.Equal(["free improvement"], Bullets(slide, "body").Select(i => i.Text));
        Assert.Equal(["nr-improvement-free"], slide.SourceRefs);
    }

    [Fact]
    public void No_challenges_give_no_challenge_table()
    {
        var report = Report with { Narratives = [.. Report.Narratives.Where(n => n.Kind != NarrativeKind.Challenge).Select(n => n with { Resolves = null })] };

        Assert.Null(Optional(ResolveOk(Manifest, report), "challenges_v1"));
    }

    [Fact]
    public void Source_refs_of_a_slide_are_unique_in_first_order()
    {
        // One improvement resolves two challenges, so the table lists it after each of them.
        var challenge = Report.Narratives.Single(n => n.Kind == NarrativeKind.Challenge);
        var improvement = Report.Narratives.Single(n => n.Kind == NarrativeKind.Improvement);
        var report = Report with
        {
            Narratives =
            [
                .. Report.Narratives.Where(n => n.Kind != NarrativeKind.Improvement),
                challenge with { Id = "nr-challenge-2" },
                improvement with { Resolves = [challenge.Id, "nr-challenge-2"] },
            ],
        };

        var slide = SlideOf(ResolveOk(Manifest, report), "challenges_v1");

        Assert.Equal([challenge.Id, improvement.Id, "nr-challenge-2"], slide.SourceRefs);
    }

    /// <summary>
    /// The example report with 7 improvements that resolve no challenge, so they go to the improvements page: more than
    /// the 5 items of the example narrative body.
    /// </summary>
    private static ReportDocument SevenImprovements()
    {
        var improvement = Report.Narratives.Single(n => n.Kind == NarrativeKind.Improvement);
        return Report with
        {
            Narratives =
            [
                .. Report.Narratives.Where(n => n.Kind != NarrativeKind.Improvement),
                .. Enumerable.Range(1, 7).Select(i => improvement with { Id = $"nr-improvement-{i}", Text = $"item {i}", Resolves = null }),
            ],
        };
    }

    private static TemplateManifest WithNarrativeTitle(int? maxChars, string continuation)
    {
        var manifest = ReplaceSlot(LayoutIndex("narrative_v1"), "title", s => s with { MaxChars = maxChars });
        return manifest with { Labels = manifest.Labels! with { Continuation = continuation } };
    }

    private static List<Slide> ImprovementSlides(TemplateManifest manifest) =>
        ResolveOk(manifest, SevenImprovements()).Slides.Where(s => s.SourceRefs.Contains("nr-improvement-1")).ToList();

    [Fact]
    public void Continuation_marker_follows_the_title_from_the_second_slide()
    {
        var slides = ImprovementSlides(Manifest);

        Assert.Equal(["改善事項", "改善事項（續 2/2）"], slides.Select(s => Text(s, "title")));
        Assert.Equal(new SlideContinuation(2, 2), slides[1].Continuation);
    }

    [Fact]
    public void Split_slides_each_list_every_source_ref_of_the_content()
    {
        var slides = ImprovementSlides(Manifest);

        var all = Enumerable.Range(1, 7).Select(i => $"nr-improvement-{i}").ToList();
        Assert.All(slides, s => Assert.Equal(all, s.SourceRefs));
    }

    [Fact]
    public void Long_title_is_truncated_before_the_whole_marker()
    {
        var slides = ImprovementSlides(WithNarrativeTitle(4, "+{index}"));

        Assert.Equal(["改善事項", "改…+2"], slides.Select(s => Text(s, "title")));
        Assert.True(slides[1].Bindings["title"].Truncated);
    }

    [Fact]
    public void Title_too_short_for_the_marker_is_truncated_as_a_whole()
    {
        var slides = ImprovementSlides(WithNarrativeTitle(3, "+{index}"));

        Assert.Equal("改善…", Text(slides[1], "title"));
    }

    [Fact]
    public void Footer_formats_the_period_into_the_footer_label()
    {
        var presentation = ResolveOk(Manifest);

        Assert.Equal($"研發工作月報 · {Report.Period.Label}", presentation.Footer);
    }

    [Fact]
    public void Footer_is_cleaned_and_cut_to_its_capacity()
    {
        var manifest = Manifest with { Footer = new FooterDefinition(5), Labels = Manifest.Labels! with { Footer = "研發\u0001工作月報" } };

        Assert.Equal("研發工作…", ResolveOk(manifest).Footer);
    }

    [Fact]
    public void No_footer_without_a_manifest_footer() =>
        Assert.Null(ResolveOk(Manifest with { Footer = null }).Footer);

    [Fact]
    public void Narrative_on_no_slide_is_a_warning_with_its_report_path()
    {
        var manifest = Manifest with
        {
            Sequence =
            [
                .. Manifest.Sequence
                    .Where(e => e.Section is not (ReportSection.Summary or ReportSection.Support))
                    .Select(e => e.Section == ReportSection.Challenges ? e with { LayoutId = "narrative_v1" } : e),
            ],
        };

        var result = Resolver.Resolve(Report, new string('0', 64), Evidence, manifest);

        Assert.NotNull(result.Presentation);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("layout.unplaced_narrative", warning.Code);
        var index = Report.Narratives.Select((n, i) => (n, i)).Single(x => x.n.Kind == NarrativeKind.Support).i;
        Assert.Equal($"/narratives/{index}", warning.Details!["path"]);
    }

    [Fact]
    public void Placed_narratives_give_no_warning()
    {
        var manifest = Manifest with
        {
            Sequence = [.. Manifest.Sequence.Select(e => e.Section == ReportSection.Challenges ? e with { LayoutId = "narrative_v1" } : e)],
        };

        Assert.Empty(Resolver.Resolve(Report, new string('0', 64), Evidence, manifest).Warnings);
    }

    // A 1.0 manifest cannot show support, the only 1.1 narrative kind; the other kinds keep the 1.0 behavior, so the
    // next plan narrative left out of this sequence gives no warning.
    [Fact]
    public void Manifest_1_0_warns_only_about_support_narratives()
    {
        var sample = Load<TemplateManifest>(Path.Combine(AppContext.BaseDirectory, "templates", "sample-monthly", "manifest.json"));
        var manifest = sample with { Sequence = [.. sample.Sequence.Where(e => e.Section != ReportSection.NextPlan)] };
        var support = Report.Narratives.Single(n => n.Kind == NarrativeKind.Support);
        var report = Report with { Narratives = [.. Report.Narratives, support with { Id = "nr-support-2" }] };

        var result = Resolver.Resolve(report, new string('0', 64), Evidence, manifest);

        Assert.NotNull(result.Presentation);
        var expected = report.Narratives.Select((n, i) => (n, i)).Where(x => x.n.Kind == NarrativeKind.Support).Select(x => $"/narratives/{x.i}");
        Assert.Equal(expected, result.Warnings.Select(w => w.Details!["path"]));
        Assert.All(result.Warnings, w => Assert.Equal("layout.unplaced_narrative", w.Code));
    }

    [Fact]
    public void Unsupported_version_is_reported_before_other_manifest_problems()
    {
        var manifest = Manifest with { SchemaVersion = "1.2", Layouts = [.. Manifest.Layouts, Manifest.Layouts[0]] };

        AssertSingleError(manifest, "manifest.unsupported_version", "/schema_version");
    }

    [Fact]
    public void Contract_problem_of_a_manifest_built_in_code_is_an_error()
    {
        var index = LayoutIndex("achievements_v1");
        var manifest = ReplaceSlot(index, "items", s => s with { Columns = [new TableColumn("owner", 100)] });

        AssertSingleError(manifest, "manifest.column_mismatch", $"/layouts/{index}/slots/1/columns/0/key");
    }

    [Fact]
    public void Operations_section_needs_an_operations_layout()
    {
        var manifest = Manifest with
        {
            Sequence = [.. Manifest.Sequence.Select(e => e.Section == ReportSection.Operations ? e with { LayoutId = "achievements_v1" } : e)],
        };
        var entry = Manifest.Sequence.Select((e, i) => (e, i)).Single(x => x.e.Section == ReportSection.Operations).i;

        AssertSingleError(manifest, "manifest.purpose_mismatch", $"/sequence/{entry}/layout_id");
    }

    [Fact]
    public void Wrong_kind_of_a_1_1_slot_is_an_error()
    {
        var index = LayoutIndex("summary_v1");
        var manifest = ReplaceSlot(index, "metric_completed", s => s with { Kind = SlotKind.Text });

        AssertSingleError(manifest, "manifest.slot_mismatch", $"/layouts/{index}/slots/{SlotIndex(index, "metric_completed")}/kind");
    }

    [Fact]
    public void Required_metric_operations_is_an_error()
    {
        var index = LayoutIndex("summary_v1");
        var manifest = ReplaceSlot(index, "metric_operations", s => s with { Required = true });

        AssertSingleError(manifest, "manifest.slot_mismatch", $"/layouts/{index}/slots/{SlotIndex(index, "metric_operations")}");
    }

    private static int LayoutIndex(string id) => Manifest.Layouts.Select((l, i) => (l, i)).Single(x => x.l.Id == id).i;

    private static int SlotIndex(int layout, string id) => Manifest.Layouts[layout].Slots.Select((s, i) => (s, i)).Single(x => x.s.Id == id).i;

    private static TemplateManifest ReplaceSlot(int layout, string slot, Func<SlotDefinition, SlotDefinition> change) =>
        Manifest with
        {
            Layouts = [.. Manifest.Layouts.Select((l, i) => i == layout ? l with { Slots = [.. l.Slots.Select(s => s.Id == slot ? change(s) : s)] } : l)],
        };

    private static void AssertSingleError(TemplateManifest manifest, string code, string path)
    {
        var result = Resolver.Resolve(Report, new string('0', 64), Evidence, manifest);

        Assert.Null(result.Presentation);
        var error = Assert.Single(result.Errors);
        Assert.Equal(code, error.Code);
        Assert.Equal(path, error.Details!["path"]);
    }
}
