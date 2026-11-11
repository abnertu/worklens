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
