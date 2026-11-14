using System.Globalization;
using WorkLens.Core.Evidence;
using WorkLens.Core.Presentation;
using WorkLens.Core.Report;
using WorkLens.Core.Security;
using WorkLens.Core.Templates;

namespace WorkLens.Core.Layout;

/// <summary>The content rules of a 1.1 manifest (docs/design/report-template.md sections 7 to 9).</summary>
public sealed partial class LayoutResolver
{
    /// <summary>The style roles that Layout writes: only formats that change with the value.</summary>
    private static class Roles
    {
        public const string Empty = "empty";
        public const string Meta = "meta";
        public const string Numbered = "numbered";
        public const string Support = "support";
        public const string SupportEmpty = "support_empty";
        public const string SupportTitle = "support_title";
        public const string SupportTitleEmpty = "support_title_empty";
        public const string EvidenceId = "evidence_id";
        public const string TagCompleted = "tag_completed";
        public const string TagInProgress = "tag_in_progress";
        public const string TagUnknown = "tag_unknown";
        public const string TagResolved = "tag_resolved";
        public const string TagOpen = "tag_open";
    }

    private sealed partial class Context
    {
        /// <summary>The summary shows at most this many support narratives; the rest is one support_more line.</summary>
        private const int SupportShown = 2;

        /// <summary>The summary shows at most this many completed work items.</summary>
        private const int HighlightsShown = 3;

        private IEnumerable<SlideContent> Contents11(ReportSection section, LayoutDefinition layout)
        {
            switch (section)
            {
                case ReportSection.Cover:
                    yield return Cover();
                    break;

                case ReportSection.Summary:
                    yield return Summary11(layout);
                    break;

                case ReportSection.Achievements:
                    if (WorkTable(Development(ReportSection.Operations), _labels.AchievementsTitle, layout) is { } achievements)
                    {
                        yield return achievements;
                    }

                    break;

                case ReportSection.Operations:
                    if (WorkTable(Ops(), _labels.OperationsTitle, layout) is { } operations)
                    {
                        yield return operations;
                    }

                    break;

                case ReportSection.WorkItemDetails:
                    foreach (var item in Development(ReportSection.OperationDetails))
                    {
                        yield return Detail11(item, _labels.AchievementsTitle, layout);
                    }

                    break;

                case ReportSection.OperationDetails:
                    foreach (var item in Ops())
                    {
                        yield return Detail11(item, _labels.OperationsTitle, layout);
                    }

                    break;

                case ReportSection.Closing:
                    yield return new SlideContent(new() { ["title"] = Text(_labels.ClosingTitle) }, []);
                    break;

                case ReportSection.Challenges when layout.Purpose == LayoutPurpose.ChallengeTable:
                    if (ChallengeTable(layout) is { } challenges)
                    {
                        yield return challenges;
                    }

                    break;

                case ReportSection.Challenges when layout.Purpose == LayoutPurpose.Narrative:
                case ReportSection.Improvements:
                case ReportSection.NextPlan:
                case ReportSection.Support:
                case ReportSection.OtherWork:
                    if (NarrativePage(section) is { } page)
                    {
                        yield return page;
                    }

                    break;
            }
        }

        private SlideContent Cover()
        {
            var period = _facts.Period();
            return new SlideContent(
                new()
                {
                    ["title"] = Text(LayoutLabels.Format(_labels.PeriodTitle, new Dictionary<string, string>
                    {
                        ["period"] = _report.Period.Label,
                        ["year"] = period.Year,
                        ["month"] = period.Month,
                    })),
                    ["kicker"] = Text(_labels.CoverKicker),
                    ["subtitle"] = Text(_labels.CoverSubtitle),
                    ["subject"] = Text(_report.Subject.DisplayName),
                    ["meta"] = Text(LayoutLabels.Format(_labels.CoverSources, new Dictionary<string, string>
                    {
                        ["start"] = period.Start,
                        ["end"] = period.End,
                        ["timezone"] = _report.Period.Timezone,
                        ["sources"] = _facts.Sources(),
                    })),
                    ["band_year"] = Text(period.Year),
                    ["band_month"] = Text(period.Month),
                },
                []);
        }

        private SlideContent Summary11(LayoutDefinition layout)
        {
            // With an operations section, ops items have their own card and table, so each item counts on one card.
            var splitOps = _sections.Contains(ReportSection.Operations);
            var development = splitOps ? _items.Where(w => w.Category != WorkCategory.Ops).ToList() : _items;
            var values = new Dictionary<string, SlotValue>
            {
                ["title"] = Text(_labels.SummaryTitle),
                ["period"] = Text(_facts.Period().Range),
                ["metric_completed"] = Metric(_labels.MetricCompleted, development.Count(w => w.Status == WorkStatus.Completed), _labels.MetricCompletedCaption),
                ["metric_in_progress"] = Metric(_labels.MetricInProgress, development.Count(w => w.Status == WorkStatus.InProgress), _labels.MetricInProgressCaption),
                ["metric_projects"] = Metric(_labels.MetricProjects, _facts.Repositories(), _labels.MetricProjectsCaption),
                ["highlights_title"] = Text(_labels.HighlightsTitle),
                ["highlights"] = Highlights(SlotOf(layout, "highlights")),
                ["activity_note"] = Text(LayoutLabels.Format(_labels.SummaryActivity, new Dictionary<string, string>
                {
                    ["repositories"] = Invariant(_facts.Repositories()),
                    ["commits"] = Invariant(_counted.Count(IsCommit)),
                    ["merged_changes"] = Invariant(_counted.Count(IsMerged)),
                })),
            };
            if (splitOps)
            {
                values["metric_operations"] = Metric(_labels.MetricOperations, _items.Count(w => w.Category == WorkCategory.Ops), _labels.MetricOperationsCaption);
            }

            var support = _report.Narratives.Where(n => n.Kind == NarrativeKind.Support).ToList();
            values["support_title"] = Text(_labels.SupportTitle) with
            {
                Style = support.Count > 0 ? Roles.SupportTitle : Roles.SupportTitleEmpty,
            };
            values["support"] = Support(support, SlotOf(layout, "support")?.MaxItems);

            // Same as 1.0: the work items, then the counted evidence.
            var refs = _items.Select(w => w.Id)
                .Concat(_counted.Select(e => e.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                .ToList();
            return new SlideContent(
                values,
                refs,
                new Dictionary<string, IReadOnlyList<string>> { ["support"] = support.Take(SupportShown).Select(n => n.Id).ToList() },
                new HashSet<string>(StringComparer.Ordinal) { "highlights", "support" });
        }

        /// <summary>
        /// The first completed work items, each a title and a meta line, fitted to <c>max_items</c> in whole items;
        /// one <c>highlights_empty</c> line without completed work.
        /// </summary>
        private BulletsSlotValue Highlights(SlotDefinition? slot)
        {
            var groups = _items
                .Where(w => w.Status == WorkStatus.Completed)
                .Take(HighlightsShown)
                .Select(w => (IReadOnlyList<BulletItem>)[new BulletItem(w.Title, 0), new BulletItem(_facts.HighlightMeta(w), 1, Roles.Meta)])
                .ToList();
            return groups.Count == 0
                ? new BulletsSlotValue([new BulletItem(_labels.HighlightsEmpty, 0, Roles.Empty)])
                : FitGroups(groups, slot?.MaxItems);
        }

        /// <summary>
        /// The first support narratives and a <c>support_more</c> line for the rest. The narratives and the line always
        /// fit <c>max_items</c>: fewer narratives are shown when it needs the room.
        /// </summary>
        private BulletsSlotValue Support(IReadOnlyList<Narrative> support, int? maxItems)
        {
            if (support.Count == 0)
            {
                return new BulletsSlotValue([new BulletItem(_labels.SupportEmpty, 0)]) { Style = Roles.SupportEmpty };
            }

            var shown = Math.Min(SupportShown, support.Count);
            if (maxItems is { } max)
            {
                shown = Math.Min(shown, max);
                if (shown < support.Count)
                {
                    shown = Math.Max(0, Math.Min(shown, max - 1));
                }
            }

            List<BulletItem> items = [.. support.Take(shown).Select(n => new BulletItem(n.Text, 0))];
            if (shown < support.Count)
            {
                items.Add(new BulletItem(
                    LayoutLabels.Format(_labels.SupportMore, new Dictionary<string, string> { ["count"] = Invariant(support.Count - shown) }),
                    0,
                    Roles.Meta));
            }

            return new BulletsSlotValue(items) { Style = Roles.Support };
        }

        /// <summary>
        /// The development items: every item, or the items that are not ops when the sequence has
        /// <paramref name="opsSection"/>, which then shows the ops items.
        /// </summary>
        private List<WorkItem> Development(ReportSection opsSection) =>
            _sections.Contains(opsSection) ? _items.Where(w => w.Category != WorkCategory.Ops).ToList() : _items;

        private List<WorkItem> Ops() => _items.Where(w => w.Category == WorkCategory.Ops).ToList();

        /// <summary>
        /// The achievements or operations table: one row for each item, in rank order. The columns come from the
        /// <c>items</c> slot, or from the purpose default. No slide without items.
        /// </summary>
        private SlideContent? WorkTable(List<WorkItem> items, string title, LayoutDefinition layout)
        {
            if (items.Count == 0)
            {
                return null;
            }

            var slot = SlotOf(layout, "items");
            var keys = slot?.Columns?.Select(c => c.Key).ToList()
                ?? (layout.Purpose == LayoutPurpose.Achievements ? ["title", "status"] : ["title", "status", "evidence"]);
            var header = slot?.Columns is null && layout.Purpose == LayoutPurpose.Achievements
                ? _labels.AchievementsColumns
                : keys.Select(ColumnName).ToList();

            var rows = new List<IReadOnlyList<string>>();
            var styles = new List<IReadOnlyList<string?>>();
            foreach (var item in items)
            {
                var cells = keys.Select(key => Cell(item, key)).ToList();
                rows.Add(cells.Select(c => c.Text).ToList());
                styles.Add(cells.Select(c => c.Style).ToList());
            }

            return new SlideContent(
                new()
                {
                    ["title"] = Text(title),
                    ["status"] = Text(LayoutLabels.Format(_labels.SummaryStatus, new Dictionary<string, string>
                    {
                        ["completed"] = Invariant(items.Count(w => w.Status == WorkStatus.Completed)),
                        ["in_progress"] = Invariant(items.Count(w => w.Status == WorkStatus.InProgress)),
                        ["unknown"] = Invariant(items.Count(w => w.Status == WorkStatus.Unknown)),
                    })),
                    ["items"] = new TableSlotValue(header, rows) { CellStyles = styles },
                },
                items.Select(w => w.Id).ToList());

            (string Text, string? Style) Cell(WorkItem item, string key) => key switch
            {
                "project" => (_facts.Project(item), null),
                "status" => (CellStatus(item.Status), TagRole(item.Status)),
                "evidence" => (_facts.EvidenceCell(item), Roles.Meta),
                _ => (item.Title, null),
            };
        }

        private string ColumnName(string key) => key switch
        {
            "title" => _labels.ColumnTitle,
            "project" => _labels.ColumnProject,
            "status" => _labels.ColumnStatus,
            "evidence" => _labels.ColumnEvidence,
            "problem" => _labels.ColumnProblem,
            _ => _labels.ColumnResolution,
        };

        private static string TagRole(WorkStatus status) => status switch
        {
            WorkStatus.Completed => Roles.TagCompleted,
            WorkStatus.InProgress => Roles.TagInProgress,
            _ => Roles.TagUnknown,
        };

        /// <summary>
        /// A detail slide: the title and status tag, the facts without a status line, and the evidence list. Agent and
        /// user text shows as written; only Evidence IR titles go through <see cref="SlideDisclosure"/>.
        /// </summary>
        private SlideContent Detail11(WorkItem item, string sectionTitle, LayoutDefinition layout)
        {
            var facts = item.ObservedFacts.Count > 0
                ? item.ObservedFacts.Select(f => new BulletItem(f.Statement, 0)).ToList()
                : [new BulletItem(item.Summary, 0)];
            if (item.AgentInference is { } inference)
            {
                facts.Add(new BulletItem(LayoutLabels.Format(_labels.Inference, new Dictionary<string, string>
                {
                    ["confidence"] = ConfidenceText(inference.Confidence),
                    ["statement"] = inference.Statement,
                }), 1));
            }

            if (!string.IsNullOrEmpty(item.HumanNotes))
            {
                facts.Add(new BulletItem(LayoutLabels.Format(_labels.HumanNotes, new Dictionary<string, string> { ["notes"] = item.HumanNotes }), 1));
            }

            var refs = item.EvidenceRefs
                .Concat(item.ObservedFacts.SelectMany(f => f.EvidenceRefs))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);

            return new SlideContent(
                new()
                {
                    ["title"] = Text(item.Title),
                    ["kicker"] = Text(LayoutLabels.Format(_labels.DetailKicker, new Dictionary<string, string>
                    {
                        ["section"] = sectionTitle,
                        ["project"] = _facts.Project(item),
                    })),
                    ["status"] = Text(CellStatus(item.Status)) with { Style = TagRole(item.Status) },
                    ["facts_title"] = Text(_labels.FactsTitle),
                    ["facts"] = new BulletsSlotValue(facts),
                    ["evidence_title"] = Text(_labels.EvidenceTitle),
                    ["evidence"] = FitGroups(EvidenceGroups(item), SlotOf(layout, "evidence")?.MaxItems),
                },
                [item.Id, .. refs],
                null,
                new HashSet<string>(StringComparer.Ordinal) { "evidence" });
        }

        /// <summary>
        /// The evidence list of a detail slide: for each change request or issue, its ID, its cleaned title, and a meta
        /// line of state, date and contained commits; then the referenced commits that no referenced change request
        /// contains. A dash when the refs give nothing, and <c>human_origin</c> without refs.
        /// </summary>
        private List<IReadOnlyList<BulletItem>> EvidenceGroups(WorkItem item)
        {
            if (!LayoutFacts.HasRefs(item))
            {
                return [[new BulletItem(_labels.HumanOrigin, 0, Roles.Meta)]];
            }

            var referenced = _facts.Referenced(item);
            var changes = referenced.Where(e => e.Kind != EvidenceKind.Commit).ToList();
            var groups = new List<IReadOnlyList<BulletItem>>();
            foreach (var change in changes)
            {
                var meta = new List<string>();
                if (change.State is { } state)
                {
                    meta.Add(state switch
                    {
                        EvidenceState.Open => _labels.EvidenceStateOpen,
                        EvidenceState.Closed => _labels.EvidenceStateClosed,
                        _ => _labels.EvidenceStateMerged,
                    });
                }

                meta.Add(LayoutLabels.Format(_labels.EvidenceDate, new Dictionary<string, string>
                {
                    ["date"] = change.Timestamps.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                }));
                var contained = _facts.ContainedCommits(change).Count;
                if (contained > 0)
                {
                    meta.Add(_facts.Commits(contained));
                }

                groups.Add(
                [
                    new BulletItem(_facts.Id(change), 0, Roles.EvidenceId),
                    new BulletItem(SlideDisclosure.EvidenceTitle(change.Title), 1),
                    new BulletItem(string.Join(LayoutFacts.Separator, meta), 1, Roles.Meta),
                ]);
            }

            // The change requests above already count the commits they contain, even those that max_items drops later.
            var contains = changes
                .SelectMany(c => c.Relations ?? [])
                .Where(r => r.Type == RelationType.Contains)
                .Select(r => r.TargetId)
                .ToHashSet(StringComparer.Ordinal);
            var commits = referenced.Count(e => e.Kind == EvidenceKind.Commit && _facts.Counts(e) && !contains.Contains(e.Id));
            if (commits > 0)
            {
                groups.Add([new BulletItem(_facts.Commits(commits), 0, Roles.Meta)]);
            }

            return groups.Count > 0 ? groups : [[new BulletItem(LayoutFacts.Dash, 0, Roles.Meta)]];
        }

        private string ConfidenceText(Confidence confidence) => confidence switch
        {
            Report.Confidence.Low => _labels.ConfidenceLow,
            Report.Confidence.Medium => _labels.ConfidenceMedium,
            _ => _labels.ConfidenceHigh,
        };

        private static bool Resolves(Narrative improvement) => improvement.Resolves is { Count: > 0 };

        /// <summary>
        /// The challenge table: one row for each challenge, in report order, with the improvements that resolve it.
        /// A challenge is resolved when an improvement resolves it. No slide without challenges.
        /// </summary>
        private SlideContent? ChallengeTable(LayoutDefinition layout)
        {
            var challenges = _report.Narratives.Where(n => n.Kind == NarrativeKind.Challenge).ToList();
            if (challenges.Count == 0)
            {
                return null;
            }

            var keys = SlotOf(layout, "items")?.Columns?.Select(c => c.Key).ToList() ?? ["problem", "resolution", "status"];
            var rows = new List<IReadOnlyList<string>>();
            var styles = new List<IReadOnlyList<string?>>();
            var refs = new List<string>();
            var resolved = 0;
            foreach (var challenge in challenges)
            {
                var improvements = _report.Narratives
                    .Where(n => n.Kind == NarrativeKind.Improvement && n.Resolves?.Contains(challenge.Id, StringComparer.Ordinal) == true)
                    .ToList();
                var done = improvements.Count > 0;
                resolved += done ? 1 : 0;
                rows.Add(keys.Select(key => key switch
                {
                    "resolution" => done ? string.Join('\n', improvements.Select(n => n.Text)) : LayoutFacts.Dash,
                    "status" => done ? _labels.ChallengeResolved : _labels.ChallengeOpen,
                    _ => challenge.Text,
                }).ToList());
                styles.Add(keys.Select(key => key == "status" ? (done ? Roles.TagResolved : Roles.TagOpen) : null).ToList());
                refs.Add(challenge.Id);
                refs.AddRange(improvements.Select(n => n.Id));
            }

            return new SlideContent(
                new()
                {
                    ["title"] = Text(_labels.ChallengesTitle),
                    ["status"] = Text(LayoutLabels.Format(_labels.ChallengesStatus, new Dictionary<string, string>
                    {
                        ["resolved"] = Invariant(resolved),
                        ["open"] = Invariant(challenges.Count - resolved),
                    })),
                    ["items"] = new TableSlotValue(keys.Select(ColumnName).ToList(), rows) { CellStyles = styles },
                },
                [],
                new Dictionary<string, IReadOnlyList<string>> { ["items"] = refs });
        }

        private SlideContent? NarrativePage(ReportSection section)
        {
            var (kind, title) = section switch
            {
                ReportSection.Challenges => (NarrativeKind.Challenge, _labels.ChallengesTitle),
                ReportSection.Improvements => (NarrativeKind.Improvement, _labels.ImprovementsTitle),
                ReportSection.NextPlan => (NarrativeKind.NextPlan, _labels.NextPlanTitle),
                ReportSection.Support => (NarrativeKind.Support, _labels.SupportTitle),
                _ => (NarrativeKind.OtherWork, _labels.OtherWorkTitle),
            };
            var style = kind == NarrativeKind.NextPlan ? Roles.Numbered : null;
            var narratives = _report.Narratives
                .Where(n => n.Kind == kind && !(kind == NarrativeKind.Improvement && _challengeTable && Resolves(n)))
                .ToList();
            if (narratives.Count == 0)
            {
                return null;
            }

            return new SlideContent(
                new()
                {
                    ["title"] = Text(title),
                    ["body"] = new BulletsSlotValue(narratives.Select(n => new BulletItem(n.Text, 0, style)).ToList()),
                },
                [],
                new Dictionary<string, IReadOnlyList<string>> { ["body"] = narratives.Select(n => n.Id).ToList() });
        }

        /// <summary>
        /// Keeps whole groups in order while they fit <c>max_items</c>, and marks the value truncated when one is left
        /// out. A first group over <c>max_items</c> keeps its first items, so that the slot is not empty.
        /// </summary>
        private static BulletsSlotValue FitGroups(IReadOnlyList<IReadOnlyList<BulletItem>> groups, int? maxItems)
        {
            var items = new List<BulletItem>();
            var truncated = false;
            foreach (var group in groups)
            {
                if (maxItems is { } max && items.Count + group.Count > max)
                {
                    truncated = true;
                    if (items.Count == 0)
                    {
                        items.AddRange(group.Take(max));
                    }

                    break;
                }

                items.AddRange(group);
            }

            return new BulletsSlotValue(items) { Truncated = truncated ? true : null };
        }

        private static MetricSlotValue Metric(string label, int value, string caption) => new(label, Invariant(value), caption);

        private static SlotDefinition? SlotOf(LayoutDefinition layout, string id) => layout.Slots.FirstOrDefault(s => s.Id == id);
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
