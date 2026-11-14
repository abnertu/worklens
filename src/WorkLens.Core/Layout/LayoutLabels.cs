using System.Text;
using WorkLens.Core.Templates;

namespace WorkLens.Core.Layout;

/// <summary>
/// The resolver's fixed text: the manifest <c>labels</c> where given, English defaults otherwise.
/// </summary>
public sealed record LayoutLabels
{
    public static LayoutLabels English { get; } = new();

    public string CoverTitle { get; init; } = "{period} Work Report";
    public string SummaryTitle { get; init; } = "Highlights";
    public string SummaryStatus { get; init; } = "{completed} completed, {in_progress} in progress, {unknown} to confirm";
    public string SummaryActivity { get; init; } = "{repositories} repositories, {commits} commits, {merged_changes} merged changes";
    public IReadOnlyList<string> ActivityColumns { get; init; } = ["Repository", "Commits", "Merged changes"];
    public string AchievementsTitle { get; init; } = "Achievements";
    public IReadOnlyList<string> AchievementsColumns { get; init; } = ["Work item", "Status"];
    public string ChallengesTitle { get; init; } = "Challenges";
    public string ImprovementsTitle { get; init; } = "Improvements";
    public string NextPlanTitle { get; init; } = "Next month";
    public string OtherWorkTitle { get; init; } = "Other work";
    public string ClosingTitle { get; init; } = "Thank you";
    public string Inference { get; init; } = "(Inference, confidence: {confidence}) {statement}";
    public string ConfidenceLow { get; init; } = "low";
    public string ConfidenceMedium { get; init; } = "medium";
    public string ConfidenceHigh { get; init; } = "high";
    public string CellStatusCompleted { get; init; } = "Completed";
    public string CellStatusInProgress { get; init; } = "In progress";
    public string CellStatusUnknown { get; init; } = "To confirm";
    public string StatusInProgress { get; init; } = "(Status: in progress)";
    public string StatusUnknown { get; init; } = "(Status: to confirm)";
    public string HumanNotes { get; init; } = "(Note) {notes}";

    // 1.1 labels.
    public string CoverKicker { get; init; } = "Monthly report";
    public string PeriodTitle { get; init; } = "{period}";
    public string CoverSubtitle { get; init; } = "Work report";
    public string CoverSources { get; init; } = "{start} – {end} ({timezone}) · {sources}";
    public string MetricCompleted { get; init; } = "Completed";
    public string MetricCompletedCaption { get; init; } = "work items completed";
    public string MetricInProgress { get; init; } = "In progress";
    public string MetricInProgressCaption { get; init; } = "work items in progress";
    public string MetricOperations { get; init; } = "Operations";
    public string MetricOperationsCaption { get; init; } = "operations work items";
    public string MetricProjects { get; init; } = "Projects";
    public string MetricProjectsCaption { get; init; } = "projects with activity";
    public string HighlightsTitle { get; init; } = "Highlights";
    public string HighlightsEmpty { get; init; } = "No work item was completed this month.";
    public string SupportTitle { get; init; } = "Support needed";
    public string SupportMore { get; init; } = "{count} more";
    public string SupportEmpty { get; init; } = "No support needed.";
    public string Continuation { get; init; } = " (cont. {index}/{total})";
    public string FactsTitle { get; init; } = "Key facts";
    public string EvidenceTitle { get; init; } = "Evidence";
    public string EvidenceStateOpen { get; init; } = "Open";
    public string EvidenceStateClosed { get; init; } = "Closed";
    public string EvidenceStateMerged { get; init; } = "Merged";
    public string EvidenceDate { get; init; } = "{date}";
    public string OperationsTitle { get; init; } = "Operations";
    public string ChallengesStatus { get; init; } = "{resolved} resolved, {open} open";
    public string ChallengeResolved { get; init; } = "Resolved";
    public string ChallengeOpen { get; init; } = "Open";
    public string ColumnTitle { get; init; } = "Work item";
    public string ColumnProject { get; init; } = "Project";
    public string ColumnStatus { get; init; } = "Status";
    public string ColumnEvidence { get; init; } = "Evidence";
    public string ColumnProblem { get; init; } = "Problem";
    public string ColumnResolution { get; init; } = "Resolution";
    public string EvidenceCommits { get; init; } = "{count} commits";
    public string EvidenceIssue { get; init; } = "Issue {id}";
    public string HumanOrigin { get; init; } = "Added by hand";
    public string DetailKicker { get; init; } = "{section} · {project}";
    public string ProjectMore { get; init; } = "{project} +{more}";
    public string Footer { get; init; } = "{period}";

    public static LayoutLabels From(TemplateLabels? labels)
    {
        if (labels is null)
        {
            return English;
        }

        var d = English;
        return new LayoutLabels
        {
            CoverTitle = labels.CoverTitle ?? d.CoverTitle,
            SummaryTitle = labels.SummaryTitle ?? d.SummaryTitle,
            SummaryStatus = labels.SummaryStatus ?? d.SummaryStatus,
            SummaryActivity = labels.SummaryActivity ?? d.SummaryActivity,
            ActivityColumns = labels.ActivityColumns ?? d.ActivityColumns,
            AchievementsTitle = labels.AchievementsTitle ?? d.AchievementsTitle,
            AchievementsColumns = labels.AchievementsColumns ?? d.AchievementsColumns,
            ChallengesTitle = labels.ChallengesTitle ?? d.ChallengesTitle,
            ImprovementsTitle = labels.ImprovementsTitle ?? d.ImprovementsTitle,
            NextPlanTitle = labels.NextPlanTitle ?? d.NextPlanTitle,
            OtherWorkTitle = labels.OtherWorkTitle ?? d.OtherWorkTitle,
            ClosingTitle = labels.ClosingTitle ?? d.ClosingTitle,
            Inference = labels.Inference ?? d.Inference,
            ConfidenceLow = labels.ConfidenceLow ?? d.ConfidenceLow,
            ConfidenceMedium = labels.ConfidenceMedium ?? d.ConfidenceMedium,
            ConfidenceHigh = labels.ConfidenceHigh ?? d.ConfidenceHigh,
            CellStatusCompleted = labels.CellStatusCompleted ?? d.CellStatusCompleted,
            CellStatusInProgress = labels.CellStatusInProgress ?? d.CellStatusInProgress,
            CellStatusUnknown = labels.CellStatusUnknown ?? d.CellStatusUnknown,
            StatusInProgress = labels.StatusInProgress ?? d.StatusInProgress,
            StatusUnknown = labels.StatusUnknown ?? d.StatusUnknown,
            HumanNotes = labels.HumanNotes ?? d.HumanNotes,
            CoverKicker = labels.CoverKicker ?? d.CoverKicker,
            PeriodTitle = labels.PeriodTitle ?? d.PeriodTitle,
            CoverSubtitle = labels.CoverSubtitle ?? d.CoverSubtitle,
            CoverSources = labels.CoverSources ?? d.CoverSources,
            MetricCompleted = labels.MetricCompleted ?? d.MetricCompleted,
            MetricCompletedCaption = labels.MetricCompletedCaption ?? d.MetricCompletedCaption,
            MetricInProgress = labels.MetricInProgress ?? d.MetricInProgress,
            MetricInProgressCaption = labels.MetricInProgressCaption ?? d.MetricInProgressCaption,
            MetricOperations = labels.MetricOperations ?? d.MetricOperations,
            MetricOperationsCaption = labels.MetricOperationsCaption ?? d.MetricOperationsCaption,
            MetricProjects = labels.MetricProjects ?? d.MetricProjects,
            MetricProjectsCaption = labels.MetricProjectsCaption ?? d.MetricProjectsCaption,
            HighlightsTitle = labels.HighlightsTitle ?? d.HighlightsTitle,
            HighlightsEmpty = labels.HighlightsEmpty ?? d.HighlightsEmpty,
            SupportTitle = labels.SupportTitle ?? d.SupportTitle,
            SupportMore = labels.SupportMore ?? d.SupportMore,
            SupportEmpty = labels.SupportEmpty ?? d.SupportEmpty,
            Continuation = labels.Continuation ?? d.Continuation,
            FactsTitle = labels.FactsTitle ?? d.FactsTitle,
            EvidenceTitle = labels.EvidenceTitle ?? d.EvidenceTitle,
            EvidenceStateOpen = labels.EvidenceStateOpen ?? d.EvidenceStateOpen,
            EvidenceStateClosed = labels.EvidenceStateClosed ?? d.EvidenceStateClosed,
            EvidenceStateMerged = labels.EvidenceStateMerged ?? d.EvidenceStateMerged,
            EvidenceDate = labels.EvidenceDate ?? d.EvidenceDate,
            OperationsTitle = labels.OperationsTitle ?? d.OperationsTitle,
            ChallengesStatus = labels.ChallengesStatus ?? d.ChallengesStatus,
            ChallengeResolved = labels.ChallengeResolved ?? d.ChallengeResolved,
            ChallengeOpen = labels.ChallengeOpen ?? d.ChallengeOpen,
            ColumnTitle = labels.ColumnTitle ?? d.ColumnTitle,
            ColumnProject = labels.ColumnProject ?? d.ColumnProject,
            ColumnStatus = labels.ColumnStatus ?? d.ColumnStatus,
            ColumnEvidence = labels.ColumnEvidence ?? d.ColumnEvidence,
            ColumnProblem = labels.ColumnProblem ?? d.ColumnProblem,
            ColumnResolution = labels.ColumnResolution ?? d.ColumnResolution,
            EvidenceCommits = labels.EvidenceCommits ?? d.EvidenceCommits,
            EvidenceIssue = labels.EvidenceIssue ?? d.EvidenceIssue,
            HumanOrigin = labels.HumanOrigin ?? d.HumanOrigin,
            DetailKicker = labels.DetailKicker ?? d.DetailKicker,
            ProjectMore = labels.ProjectMore ?? d.ProjectMore,
            Footer = labels.Footer ?? d.Footer,
        };
    }

    /// <summary>
    /// Single-pass substitution: each <c>{name}</c> in <paramref name="template"/> with a known name is replaced;
    /// braces inside substituted values are not expanded again. Unknown names are left as written.
    /// </summary>
    public static string Format(string template, IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                var close = template.IndexOf('}', i + 1);
                if (close > i && values.TryGetValue(template[(i + 1)..close], out var value))
                {
                    builder.Append(value);
                    i = close + 1;
                    continue;
                }
            }

            builder.Append(template[i]);
            i++;
        }

        return builder.ToString();
    }
}
