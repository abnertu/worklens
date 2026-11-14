using System.Globalization;
using System.Text.RegularExpressions;
using WorkLens.Core.Common;
using WorkLens.Core.Evidence;
using WorkLens.Core.Report;

namespace WorkLens.Core.Layout;

/// <summary>
/// The values that 1.1 slides compute from Report IR and Evidence IR (docs/design/report-template.md section 9): the
/// period, the project and evidence text of a work item, and the counts of the summary.
/// </summary>
/// <remarks>
/// A work item refers to evidence through its <c>evidence_refs</c>, then through the <c>evidence_refs</c> of its facts.
/// An ID that appears more than once in the evidence takes its first item, and an ID that the evidence lacks is skipped.
/// </remarks>
internal sealed partial class LayoutFacts
{
    /// <summary>Joins the parts of a meta text.</summary>
    public const string Separator = " · ";

    /// <summary>The text of a cell that has refs but nothing to show.</summary>
    public const string Dash = "—";

    private readonly ReportDocument _report;
    private readonly EvidenceDocument _evidence;
    private readonly LayoutLabels _labels;
    private readonly Dictionary<string, EvidenceItem> _byId = new(StringComparer.Ordinal);

    public LayoutFacts(ReportDocument report, EvidenceDocument evidence, LayoutLabels labels)
    {
        _report = report;
        _evidence = evidence;
        _labels = labels;
        foreach (var item in evidence.Items)
        {
            _byId.TryAdd(item.Id, item);
        }
    }

    [GeneratedRegex(@"\A(?<year>[0-9]{4})-(?<month>0[1-9]|1[0-2])\z", RegexOptions.CultureInvariant)]
    private static partial Regex MonthLabel();

    /// <summary>
    /// The period values. A <c>yyyy-MM</c> label gives the year, the month without a leading zero, and the first and
    /// last day of that month. Another label gives an empty year and month, and the UTC dates of the start and of the
    /// day before the end.
    /// </summary>
    public PeriodText Period()
    {
        var match = MonthLabel().Match(_report.Period.Label);
        if (match.Success)
        {
            var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
            var first = new DateOnly(year, month, 1);
            return new PeriodText(match.Groups["year"].Value, Invariant(month), Date(first), Date(first.AddMonths(1).AddDays(-1)));
        }

        return new PeriodText(
            string.Empty,
            string.Empty,
            Date(DateOnly.FromDateTime(_report.Period.Start.UtcDateTime)),
            Date(DateOnly.FromDateTime(_report.Period.End.UtcDateTime.AddDays(-1))));
    }

    /// <summary>Whether a piece of evidence counts in statistics: in the period and not a duplicate.</summary>
    public bool Counts(EvidenceItem item) =>
        item.Timestamps.OccurredAt >= _report.Period.Start
        && item.Timestamps.OccurredAt < _report.Period.End
        && item.Flags?.Contains(EvidenceFlag.Duplicate) != true;

    /// <summary>Whether the work item refers to any evidence, found or not.</summary>
    public static bool HasRefs(WorkItem item) =>
        item.EvidenceRefs.Count > 0 || item.ObservedFacts.Any(f => f.EvidenceRefs.Count > 0);

    /// <summary>The evidence that the work item refers to, without repeats, in reference order.</summary>
    public IReadOnlyList<EvidenceItem> Referenced(WorkItem item) =>
        item.EvidenceRefs
            .Concat(item.ObservedFacts.SelectMany(f => f.EvidenceRefs))
            .Distinct(StringComparer.Ordinal)
            .Select(id => _byId.GetValueOrDefault(id))
            .OfType<EvidenceItem>()
            .ToList();

    public EvidenceItem? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// The project text: the last segment of the first repository, with <c>project_more</c> when there are more;
    /// <see cref="Dash"/> when no referenced evidence is found. Repositories count by platform, base URL and path.
    /// </summary>
    public string Project(WorkItem item)
    {
        var repositories = Referenced(item)
            .Select(e => (e.Source.Platform, e.Source.BaseUrl, e.Source.Repository))
            .Distinct()
            .ToList();
        if (repositories.Count == 0)
        {
            return Dash;
        }

        var name = repositories[0].Repository.Split('/')[^1];
        return repositories.Count == 1
            ? name
            : LayoutLabels.Format(_labels.ProjectMore, new Dictionary<string, string>
            {
                ["project"] = name,
                ["count"] = Invariant(repositories.Count),
                ["more"] = Invariant(repositories.Count - 1),
            });
    }

    /// <summary>
    /// The evidence text of a table cell: up to 3 change request and issue IDs, <c>+N</c> for the rest, then the count
    /// of counted commits. <c>human_origin</c> without refs; <see cref="Dash"/> when the refs give nothing to show.
    /// </summary>
    public string EvidenceCell(WorkItem item)
    {
        if (!HasRefs(item))
        {
            return _labels.HumanOrigin;
        }

        var referenced = Referenced(item);
        var ids = referenced.Where(e => e.Kind != EvidenceKind.Commit).Select(Id).ToList();
        var parts = ids.Take(3).ToList();
        if (ids.Count > 3)
        {
            parts.Add("+" + Invariant(ids.Count - 3));
        }

        var commits = referenced.Count(e => e.Kind == EvidenceKind.Commit && Counts(e));
        if (commits > 0)
        {
            parts.Add(Commits(commits));
        }

        return parts.Count == 0 ? Dash : string.Join(Separator, parts);
    }

    /// <summary>The meta line of a highlight: the project and the evidence text, or <c>human_origin</c> without refs.</summary>
    public string HighlightMeta(WorkItem item) =>
        HasRefs(item) ? Project(item) + Separator + EvidenceCell(item) : _labels.HumanOrigin;

    /// <summary>The ID of a change request or an issue as a slide shows it.</summary>
    public string Id(EvidenceItem item)
    {
        var prefix = item.Kind == EvidenceKind.ChangeRequest && item.Source.Platform == SourcePlatform.Gitlab ? "!" : "#";
        var id = prefix + item.Source.ExternalId;
        return item.Kind == EvidenceKind.Issue
            ? LayoutLabels.Format(_labels.EvidenceIssue, new Dictionary<string, string> { ["id"] = id })
            : id;
    }

    /// <summary>The <c>evidence_commits</c> text for a count.</summary>
    public string Commits(int count) =>
        LayoutLabels.Format(_labels.EvidenceCommits, new Dictionary<string, string> { ["count"] = Invariant(count) });

    /// <summary>The counted commits that a change request contains, without repeats.</summary>
    public IReadOnlyList<EvidenceItem> ContainedCommits(EvidenceItem changeRequest) =>
        (changeRequest.Relations ?? [])
            .Where(r => r.Type == RelationType.Contains)
            .Select(r => r.TargetId)
            .Distinct(StringComparer.Ordinal)
            .Select(Find)
            .OfType<EvidenceItem>()
            .Where(e => e.Kind == EvidenceKind.Commit && Counts(e))
            .ToList();

    /// <summary>The distinct repositories of the counted evidence, of every kind and state.</summary>
    public int Repositories() =>
        _evidence.Items.Where(Counts).Select(e => (e.Source.Platform, e.Source.BaseUrl, e.Source.Repository)).Distinct().Count();

    /// <summary>
    /// The source summary of the cover: the platforms of the query sources (GitHub, then GitLab), then
    /// <c>human_origin</c> when a work item that is not rejected refers to no evidence.
    /// </summary>
    public string Sources()
    {
        var parts = new List<string>();
        var platforms = _evidence.Query.Sources.Select(s => s.Platform).ToHashSet();
        if (platforms.Contains(SourcePlatform.Github))
        {
            parts.Add("GitHub");
        }

        if (platforms.Contains(SourcePlatform.Gitlab))
        {
            parts.Add("GitLab");
        }

        if (_report.WorkItems.Any(w => w.ReviewStatus != ReviewStatus.Rejected && !HasRefs(w)))
        {
            parts.Add(_labels.HumanOrigin);
        }

        return string.Join(Separator, parts);
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The period values of the 1.1 labels.</summary>
/// <param name="Start">First day, <c>yyyy-MM-dd</c>.</param>
/// <param name="End">Last day, <c>yyyy-MM-dd</c>.</param>
internal sealed record PeriodText(string Year, string Month, string Start, string End)
{
    /// <summary>The period range of the summary.</summary>
    public string Range => Start + " – " + End;
}
