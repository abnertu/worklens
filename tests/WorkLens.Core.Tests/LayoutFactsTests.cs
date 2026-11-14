using WorkLens.Core.Common;
using WorkLens.Core.Evidence;
using WorkLens.Core.Layout;
using WorkLens.Core.Report;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class LayoutFactsTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 10, 31, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset InPeriod = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    private const string GitLabApi = "https://gitlab.example.com/api/v4";

    private static EvidenceItem Ev(
        string id,
        EvidenceKind kind,
        string repository = "group/backend-api",
        SourcePlatform platform = SourcePlatform.Gitlab,
        string externalId = "1",
        DateTimeOffset? occurredAt = null,
        IReadOnlyList<EvidenceRelation>? relations = null,
        IReadOnlyList<EvidenceFlag>? flags = null,
        string baseUrl = GitLabApi) =>
        new(
            id,
            kind,
            new EvidenceSource(platform, baseUrl, repository, externalId, "https://example.com/x"),
            "title",
            new EvidenceAuthor(AuthorRole.Author),
            new EvidenceTimestamps(occurredAt ?? InPeriod),
            InPeriod,
            Relations: relations,
            Flags: flags);

    private static WorkItem Item(string id, IReadOnlyList<string> refs, IReadOnlyList<string>? factRefs = null, ReviewStatus? review = null) =>
        new(
            id,
            ContentOrigin.Agent,
            WorkCategory.Feature,
            WorkStatus.Completed,
            "t",
            "s",
            factRefs is null ? [] : [new ObservedFact("f", factRefs)],
            refs,
            ReviewStatus: review);

    private static LayoutFacts Facts(IReadOnlyList<EvidenceItem> items, IReadOnlyList<WorkItem>? workItems = null, string label = "2026-10", IReadOnlyList<SourceSpec>? sources = null)
    {
        var period = new Period(label, Start, End, "Asia/Taipei");
        var report = new ReportDocument("1.1", "rpt-x", period, new ReportSubject("A"), new EvidenceDocumentRef("evd-x", new string('0', 64)), workItems ?? [], []);
        var evidence = new EvidenceDocument(
            "1.0",
            "evd-x",
            InPeriod,
            new GeneratorInfo("worklens", "0.1.0"),
            new EvidenceQuery(period, [], sources ?? []),
            items);
        return new LayoutFacts(report, evidence, LayoutLabels.English);
    }

    [Fact]
    public void Month_label_gives_year_month_and_month_days()
    {
        var period = Facts([]).Period();

        Assert.Equal(new PeriodText("2026", "10", "2026-10-01", "2026-10-31"), period);
        Assert.Equal("2026-10-01 – 2026-10-31", period.Range);
    }

    [Fact]
    public void Other_label_gives_utc_dates_and_no_year_or_month() =>
        Assert.Equal(new PeriodText(string.Empty, string.Empty, "2026-09-30", "2026-10-30"), Facts([], label: "Q4").Period());

    [Fact]
    public void Project_is_the_last_segment_of_the_repository() =>
        Assert.Equal("backend-api", Facts([Ev("c1", EvidenceKind.Commit)]).Project(Item("w", ["c1"])));

    [Fact]
    public void Project_with_more_repositories_counts_them_in_reference_order()
    {
        var facts = Facts([Ev("c1", EvidenceKind.Commit, "group/web"), Ev("c2", EvidenceKind.Commit), Ev("c3", EvidenceKind.Commit, "group/web")]);

        Assert.Equal("web +1", facts.Project(Item("w", ["c1"], factRefs: ["c2", "c3"])));
    }

    // The same repository path on two platforms is two projects.
    [Fact]
    public void Same_path_on_another_platform_is_another_project()
    {
        var facts = Facts([Ev("c1", EvidenceKind.Commit), Ev("c2", EvidenceKind.Commit, platform: SourcePlatform.Github, baseUrl: "https://api.github.com")]);

        Assert.Equal("backend-api +1", facts.Project(Item("w", ["c1", "c2"])));
    }

    // Two GitLab instances can hold the same path; they are two projects.
    [Fact]
    public void Same_path_on_another_base_url_is_another_project()
    {
        var facts = Facts([Ev("c1", EvidenceKind.Commit), Ev("c2", EvidenceKind.Commit, baseUrl: "https://gitlab.other.example/api/v4")]);

        Assert.Equal("backend-api +1", facts.Project(Item("w", ["c1", "c2"])));
        Assert.Equal(2, facts.Repositories());
    }

    [Fact]
    public void Project_without_found_evidence_is_a_dash() =>
        Assert.Equal("—", Facts([]).Project(Item("w", ["missing"])));

    [Fact]
    public void Evidence_cell_lists_ids_then_commits()
    {
        var facts = Facts([
            Ev("m1", EvidenceKind.ChangeRequest, externalId: "128"),
            Ev("m2", EvidenceKind.ChangeRequest, platform: SourcePlatform.Github, externalId: "131"),
            Ev("c1", EvidenceKind.Commit),
            Ev("c2", EvidenceKind.Commit),
        ]);

        Assert.Equal("!128 · #131 · 2 commits", facts.EvidenceCell(Item("w", ["m1", "c1"], factRefs: ["m2", "c2", "c1"])));
    }

    [Fact]
    public void Evidence_cell_shows_three_ids_and_the_rest_as_a_count()
    {
        var facts = Facts([.. Enumerable.Range(1, 5).Select(i => Ev($"m{i}", EvidenceKind.ChangeRequest, externalId: $"{i}"))]);

        Assert.Equal("!1 · !2 · !3 · +2", facts.EvidenceCell(Item("w", ["m1", "m2", "m3", "m4", "m5"])));
    }

    [Fact]
    public void Evidence_cell_formats_an_issue() =>
        Assert.Equal("Issue #42", Facts([Ev("i1", EvidenceKind.Issue, externalId: "42")]).EvidenceCell(Item("w", ["i1"])));

    [Fact]
    public void Evidence_cell_with_only_commits_counts_them() =>
        Assert.Equal("1 commits", Facts([Ev("c1", EvidenceKind.Commit)]).EvidenceCell(Item("w", ["c1"])));

    [Fact]
    public void Evidence_cell_without_refs_is_human_origin() =>
        Assert.Equal("Added by hand", Facts([]).EvidenceCell(Item("w", [])));

    // Out-of-period and duplicate commits do not count, and a missing ID is skipped, so nothing is left to show.
    [Fact]
    public void Evidence_cell_with_nothing_to_show_is_a_dash()
    {
        var facts = Facts([Ev("c1", EvidenceKind.Commit, occurredAt: End), Ev("c2", EvidenceKind.Commit, flags: [EvidenceFlag.Duplicate])]);

        Assert.Equal("—", facts.EvidenceCell(Item("w", ["c1", "c2", "missing"])));
    }

    // Validated evidence can repeat an ID; the first item wins.
    [Fact]
    public void Repeated_evidence_id_takes_the_first_item()
    {
        var facts = Facts([Ev("m1", EvidenceKind.ChangeRequest, externalId: "7"), Ev("m1", EvidenceKind.ChangeRequest, externalId: "8")]);

        Assert.Equal("!7", facts.EvidenceCell(Item("w", ["m1"])));
    }

    [Fact]
    public void Contained_commits_count_once_and_only_when_counted()
    {
        var contains = new[] { "c1", "c1", "c2", "missing" }.Select(t => new EvidenceRelation(RelationType.Contains, t)).ToList();
        var facts = Facts([
            Ev("m1", EvidenceKind.ChangeRequest, relations: [.. contains, new EvidenceRelation(RelationType.References, "c3")]),
            Ev("c1", EvidenceKind.Commit),
            Ev("c2", EvidenceKind.Commit, occurredAt: End),
            Ev("c3", EvidenceKind.Commit),
        ]);

        Assert.Equal(["c1"], facts.ContainedCommits(facts.Find("m1")!).Select(e => e.Id));
    }

    [Fact]
    public void Highlight_meta_joins_project_and_evidence_or_is_human_origin()
    {
        var facts = Facts([Ev("m1", EvidenceKind.ChangeRequest, externalId: "5")]);

        Assert.Equal("backend-api · !5", facts.HighlightMeta(Item("w", ["m1"])));
        Assert.Equal("Added by hand", facts.HighlightMeta(Item("w", [])));
    }

    // Every counted kind and state counts; out-of-period and duplicate evidence does not.
    [Fact]
    public void Repositories_count_the_counted_evidence_by_platform_base_url_and_path()
    {
        var facts = Facts([
            Ev("c1", EvidenceKind.Commit),
            Ev("i1", EvidenceKind.Issue),
            Ev("m1", EvidenceKind.ChangeRequest, platform: SourcePlatform.Github, baseUrl: "https://api.github.com"),
            Ev("c2", EvidenceKind.Commit, "group/web", occurredAt: Start.AddSeconds(-1)),
            Ev("c3", EvidenceKind.Commit, "group/docs", flags: [EvidenceFlag.Duplicate]),
        ]);

        Assert.Equal(2, facts.Repositories());
    }

    [Fact]
    public void Sources_list_platforms_then_human_origin_for_items_without_refs()
    {
        SourceSpec[] sources = [new(SourcePlatform.Gitlab, GitLabApi, "group/a"), new(SourcePlatform.Github, "https://api.github.com", "o/b")];

        Assert.Equal("GitHub · GitLab · Added by hand", Facts([], [Item("w", [])], sources: sources).Sources());
        Assert.Equal("GitLab", Facts([], [Item("w", ["c1"])], sources: [sources[0]]).Sources());
    }

    // A rejected item is not in the report, so it adds no human_origin.
    [Fact]
    public void Sources_ignore_rejected_items() =>
        Assert.Equal(string.Empty, Facts([], [Item("w", [], review: ReviewStatus.Rejected)]).Sources());
}
