using System.Globalization;
using System.Text;
using WorkLens.Core.Contracts;
using WorkLens.Core.Errors;
using WorkLens.Core.Evidence;
using WorkLens.Core.Presentation;
using WorkLens.Core.Report;
using WorkLens.Core.Templates;

namespace WorkLens.Core.Layout;

/// <summary>
/// Deterministic Report IR → Presentation IR mapping (docs/design/domain-model.md section 5). A pure function: the
/// same inputs always give the same output, with no clock, randomness or environment access.
/// </summary>
public sealed class LayoutResolver : ILayoutResolver
{
    private const string Ellipsis = "…";

    /// <summary>The slots each purpose binds, with their kinds; <c>Required</c> slots must exist in the layout.</summary>
    private static readonly Dictionary<LayoutPurpose, (string Id, SlotKind Kind, bool Required)[]> PurposeSlots = new()
    {
        [LayoutPurpose.Cover] = [("title", SlotKind.Text, true), ("subtitle", SlotKind.Text, false)],
        [LayoutPurpose.Summary] = [("title", SlotKind.Text, true), ("highlights", SlotKind.Bullets, false), ("activity", SlotKind.Table, false)],
        [LayoutPurpose.Achievements] = [("title", SlotKind.Text, true), ("items", SlotKind.Table, false)],
        [LayoutPurpose.WorkItemDetail] = [("title", SlotKind.Text, true), ("facts", SlotKind.Bullets, true)],
        [LayoutPurpose.Narrative] = [("title", SlotKind.Text, true), ("body", SlotKind.Bullets, true)],
        [LayoutPurpose.Closing] = [("title", SlotKind.Text, true)],
    };

    /// <summary>The slot whose items flow onto continuation slides when <c>overflow: split</c>.</summary>
    private static readonly Dictionary<LayoutPurpose, string> FlowSlots = new()
    {
        [LayoutPurpose.Achievements] = "items",
        [LayoutPurpose.WorkItemDetail] = "facts",
        [LayoutPurpose.Narrative] = "body",
    };

    public LayoutResult Resolve(ReportDocument report, string reportSha256, EvidenceDocument evidence, TemplateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(manifest);

        var context = new Context(report, evidence, LayoutLabels.From(manifest.Labels));
        var errors = new List<WorkLensError>();
        var checkedLayouts = new Dictionary<int, bool>();
        var pages = new List<Page>();

        for (var s = 0; s < manifest.Sequence.Count; s++)
        {
            var entry = manifest.Sequence[s];
            var layoutIndex = IndexOfLayout(manifest, entry.LayoutId);
            if (layoutIndex < 0)
            {
                errors.Add(Error("manifest.unknown_layout", $"/sequence/{s}/layout_id",
                    $"Sequence entry {s} ({Name(entry.Section)}) uses layout '{entry.LayoutId}', which the manifest does not define."));
                continue;
            }

            var layout = manifest.Layouts[layoutIndex];
            var expected = PurposeOf(entry.Section);
            if (layout.Purpose != expected)
            {
                errors.Add(Error("manifest.purpose_mismatch", $"/sequence/{s}/layout_id",
                    $"Section {Name(entry.Section)} needs a layout with purpose {Name(expected)}, but '{layout.Id}' has purpose {Name(layout.Purpose)}."));
                continue;
            }

            if (!checkedLayouts.TryGetValue(layoutIndex, out var layoutOk))
            {
                layoutOk = CheckSlots(layout, layoutIndex, errors);
                checkedLayouts[layoutIndex] = layoutOk;
            }

            if (!layoutOk)
            {
                continue;
            }

            foreach (var content in context.Contents(entry.Section))
            {
                pages.AddRange(Bind(layout, layoutIndex, content, errors));
            }
        }

        if (errors.Count > 0)
        {
            return new LayoutResult(null, errors);
        }

        var slides = pages
            .Select((p, i) => new Slide(
                $"slide-{i + 1:000}",
                p.LayoutId,
                p.Bindings,
                p.SourceRefs,
                p.Continuation))
            .ToList();

        var presentation = new PresentationDocument(
            PresentationDocument.CurrentSchemaVersion,
            "prs-" + (report.ReportId.StartsWith("rpt-", StringComparison.Ordinal) ? report.ReportId[4..] : report.ReportId),
            new SourceReportRef(report.ReportId, reportSha256),
            new TemplateRef(manifest.Template.Id, manifest.Template.Version),
            slides);
        return new LayoutResult(presentation, []);
    }

    private static int IndexOfLayout(TemplateManifest manifest, string id)
    {
        for (var i = 0; i < manifest.Layouts.Count; i++)
        {
            if (string.Equals(manifest.Layouts[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static LayoutPurpose PurposeOf(ReportSection section) => section switch
    {
        ReportSection.Cover => LayoutPurpose.Cover,
        ReportSection.Summary => LayoutPurpose.Summary,
        ReportSection.Achievements => LayoutPurpose.Achievements,
        ReportSection.WorkItemDetails => LayoutPurpose.WorkItemDetail,
        ReportSection.Closing => LayoutPurpose.Closing,
        _ => LayoutPurpose.Narrative,
    };

    /// <summary>Checks that the layout has the purpose's required slots and that known slots have the right kind.</summary>
    private static bool CheckSlots(LayoutDefinition layout, int layoutIndex, List<WorkLensError> errors)
    {
        var before = errors.Count;
        var conventions = PurposeSlots[layout.Purpose];
        foreach (var (id, _, required) in conventions)
        {
            if (required && !layout.Slots.Any(slot => slot.Id == id))
            {
                errors.Add(Error("manifest.slot_mismatch", $"/layouts/{layoutIndex}/slots",
                    $"Layout '{layout.Id}' (purpose {Name(layout.Purpose)}) has no '{id}' slot."));
            }
        }

        for (var k = 0; k < layout.Slots.Count; k++)
        {
            var slot = layout.Slots[k];
            var convention = conventions.FirstOrDefault(c => c.Id == slot.Id);
            if (convention.Id is not null && convention.Kind != slot.Kind)
            {
                errors.Add(Error("manifest.slot_mismatch", $"/layouts/{layoutIndex}/slots/{k}/kind",
                    $"Slot '{slot.Id}' of layout '{layout.Id}' must be kind {Name(convention.Kind)}, not {Name(slot.Kind)}."));
            }
        }

        return errors.Count == before;
    }

    /// <summary>Binds one slide's content to the layout's slots, applying max_chars / max_items and splitting.</summary>
    private static IEnumerable<Page> Bind(LayoutDefinition layout, int layoutIndex, SlideContent content, List<WorkLensError> errors)
    {
        FlowSlots.TryGetValue(layout.Purpose, out var flowSlot);
        var bound = new List<(string Id, List<SlotValue> Chunks)>();
        var known = PurposeSlots[layout.Purpose];

        for (var k = 0; k < layout.Slots.Count; k++)
        {
            var slot = layout.Slots[k];
            if (!known.Any(c => c.Id == slot.Id))
            {
                continue;
            }

            var value = content.Values.GetValueOrDefault(slot.Id);
            if (value is null || IsEmpty(value))
            {
                if (slot.Required == true)
                {
                    errors.Add(Error("manifest.slot_mismatch", $"/layouts/{layoutIndex}/slots/{k}",
                        $"Slot '{slot.Id}' of layout '{layout.Id}' is required, but the report has no content for it."));
                }

                continue;
            }

            var split = slot.Overflow == OverflowPolicy.Split && slot.Id == flowSlot;
            bound.Add((slot.Id, Fit(value, slot.MaxChars, slot.MaxItems, split)));
        }

        var total = bound.Count == 0 ? 1 : bound.Max(b => b.Chunks.Count);
        for (var page = 0; page < total; page++)
        {
            var bindings = new Dictionary<string, SlotValue>(StringComparer.Ordinal);
            foreach (var (id, chunks) in bound)
            {
                bindings[id] = chunks[Math.Min(page, chunks.Count - 1)];
            }

            yield return new Page(
                layout.Id,
                bindings,
                content.SourceRefs,
                total > 1 ? new SlideContinuation(page + 1, total) : null);
        }
    }

    private static bool IsEmpty(SlotValue value) => value switch
    {
        TextSlotValue t => t.Text.Length == 0,
        BulletsSlotValue b => b.Items.Count == 0,
        TableSlotValue t => t.Rows.Count == 0,
        _ => true,
    };

    /// <summary>
    /// Sanitizes and truncates text, then truncates or splits items. Returns one value per page; a page's value is
    /// marked <c>truncated</c> when text on that page was shortened or items were dropped.
    /// </summary>
    private static List<SlotValue> Fit(SlotValue value, int? maxChars, int? maxItems, bool split)
    {
        (string Text, bool Truncated) Clip(string text)
        {
            var clean = RemoveInvalidXmlCharacters(text);
            if (maxChars is { } max && new StringInfo(clean).LengthInTextElements > max)
            {
                return (new StringInfo(clean).SubstringByTextElements(0, max - 1) + Ellipsis, true);
            }

            return (clean, false);
        }

        switch (value)
        {
            case TextSlotValue text:
                {
                    var (clipped, truncated) = Clip(text.Text);
                    return [new TextSlotValue(clipped) { Truncated = Flag(truncated) }];
                }

            case BulletsSlotValue bullets:
                {
                    var items = bullets.Items
                        .Select(i =>
                        {
                            var (text, truncated) = Clip(i.Text);
                            return (new BulletItem(text, i.Level), truncated);
                        })
                        .ToList();
                    return Chunk(items, maxItems, split)
                        .Select(c => (SlotValue)new BulletsSlotValue(c.Items) { Truncated = Flag(c.Truncated) })
                        .ToList();
                }

            case TableSlotValue table:
                {
                    var columns = table.Columns.Select(RemoveInvalidXmlCharacters).ToList();
                    var rows = table.Rows
                        .Select(r =>
                        {
                            var cells = r.Select(Clip).ToList();
                            return ((IReadOnlyList<string>)cells.Select(c => c.Text).ToList(), cells.Any(c => c.Truncated));
                        })
                        .ToList();
                    return Chunk(rows, maxItems, split)
                        .Select(c => (SlotValue)new TableSlotValue(columns, c.Items) { Truncated = Flag(c.Truncated) })
                        .ToList();
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(value));
        }

        static bool? Flag(bool truncated) => truncated ? true : null;
    }

    /// <summary>Applies max_items: one chunk, a truncated chunk, or (split) one chunk per page.</summary>
    private static List<(List<T> Items, bool Truncated)> Chunk<T>(List<(T Item, bool Truncated)> items, int? maxItems, bool split)
    {
        if (maxItems is not { } max || items.Count <= max)
        {
            return [(items.Select(i => i.Item).ToList(), items.Any(i => i.Truncated))];
        }

        if (!split)
        {
            return [(items.Take(max).Select(i => i.Item).ToList(), true)];
        }

        return items.Chunk(max).Select(c => (c.Select(i => i.Item).ToList(), c.Any(i => i.Truncated))).ToList();
    }

    /// <summary>Whether <paramref name="text"/> has a character that XML 1.0 does not allow.</summary>
    internal static bool ContainsInvalidXmlCharacter(string text) =>
        !ReferenceEquals(RemoveInvalidXmlCharacters(text), text);

    /// <summary>
    /// Removes characters that XML 1.0 does not allow (control characters, lone surrogates, U+FFFE/U+FFFF). Returns the
    /// same instance when nothing was removed.
    /// </summary>
    internal static string RemoveInvalidXmlCharacters(string text)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var valid = c is '\t' or '\n' or '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD);
            var width = 1;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                valid = true;
                width = 2;
            }

            if (!valid)
            {
                builder ??= new StringBuilder(text, 0, i, text.Length);
            }
            else
            {
                builder?.Append(text, i, width);
            }

            i += width - 1;
        }

        return builder?.ToString() ?? text;
    }

    private static WorkLensError Error(string code, string path, string message) =>
        new(code, message, new Dictionary<string, string> { ["path"] = path });

    private static string Name<T>(T value)
        where T : struct, Enum =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private sealed record Page(
        string LayoutId,
        Dictionary<string, SlotValue> Bindings,
        IReadOnlyList<string> SourceRefs,
        SlideContinuation? Continuation);

    private sealed record SlideContent(Dictionary<string, SlotValue> Values, IReadOnlyList<string> SourceRefs);

    /// <summary>Report-derived content per section, computed once per resolve.</summary>
    private sealed class Context
    {
        private readonly ReportDocument _report;
        private readonly EvidenceDocument _evidence;
        private readonly LayoutLabels _labels;
        private readonly List<WorkItem> _items;
        private readonly List<EvidenceItem> _counted;

        public Context(ReportDocument report, EvidenceDocument evidence, LayoutLabels labels)
        {
            _report = report;
            _evidence = evidence;
            _labels = labels;

            // Rejected items are excluded; rank ascending with a missing rank last, ties in array order (stable sort).
            _items = report.WorkItems
                .Where(w => w.ReviewStatus != ReviewStatus.Rejected)
                .OrderBy(w => w.Rank.HasValue ? 0 : 1)
                .ThenBy(w => w.Rank ?? 0)
                .ToList();

            // Statistics count in-period, non-duplicate commits and merged change requests.
            _counted = evidence.Items
                .Where(e => e.Timestamps.OccurredAt >= report.Period.Start && e.Timestamps.OccurredAt < report.Period.End)
                .Where(e => e.Flags?.Contains(EvidenceFlag.Duplicate) != true)
                .Where(e => IsCommit(e) || IsMerged(e))
                .ToList();
        }

        public IEnumerable<SlideContent> Contents(ReportSection section)
        {
            switch (section)
            {
                case ReportSection.Cover:
                    yield return new SlideContent(
                        new()
                        {
                            ["title"] = Text(LayoutLabels.Format(_labels.CoverTitle, new Dictionary<string, string> { ["period"] = _report.Period.Label })),
                            ["subtitle"] = Text(_report.Subject.DisplayName),
                        },
                        []);
                    break;

                case ReportSection.Summary:
                    yield return Summary();
                    break;

                case ReportSection.Achievements:
                    if (_items.Count > 0)
                    {
                        yield return new SlideContent(
                            new()
                            {
                                ["title"] = Text(_labels.AchievementsTitle),
                                ["items"] = new TableSlotValue(
                                    _labels.AchievementsColumns,
                                    _items.Select(w => (IReadOnlyList<string>)[w.Title, CellStatus(w.Status)]).ToList()),
                            },
                            _items.Select(w => w.Id).ToList());
                    }

                    break;

                case ReportSection.WorkItemDetails:
                    foreach (var item in _items)
                    {
                        yield return Detail(item);
                    }

                    break;

                case ReportSection.Closing:
                    yield return new SlideContent(new() { ["title"] = Text(_labels.ClosingTitle) }, []);
                    break;

                default:
                    {
                        var (kind, title) = section switch
                        {
                            ReportSection.Challenges => (NarrativeKind.Challenge, _labels.ChallengesTitle),
                            ReportSection.Improvements => (NarrativeKind.Improvement, _labels.ImprovementsTitle),
                            ReportSection.NextPlan => (NarrativeKind.NextPlan, _labels.NextPlanTitle),
                            _ => (NarrativeKind.OtherWork, _labels.OtherWorkTitle),
                        };
                        var narratives = _report.Narratives.Where(n => n.Kind == kind).ToList();
                        if (narratives.Count > 0)
                        {
                            yield return new SlideContent(
                                new()
                                {
                                    ["title"] = Text(title),
                                    ["body"] = new BulletsSlotValue(narratives.Select(n => new BulletItem(n.Text, 0)).ToList()),
                                },
                                narratives.Select(n => n.Id).ToList());
                        }

                        break;
                    }
            }
        }

        private SlideContent Summary()
        {
            var status = new Dictionary<string, string>
            {
                ["completed"] = Count(WorkStatus.Completed),
                ["in_progress"] = Count(WorkStatus.InProgress),
                ["unknown"] = Count(WorkStatus.Unknown),
            };
            var activity = new Dictionary<string, string>
            {
                ["repositories"] = Invariant(_evidence.Query.Sources.Select(s => s.Repository).Distinct(StringComparer.Ordinal).Count()),
                ["commits"] = Invariant(_counted.Count(IsCommit)),
                ["merged_changes"] = Invariant(_counted.Count(IsMerged)),
            };

            var rows = _evidence.Query.Sources
                .DistinctBy(s => (s.Platform, s.BaseUrl, s.Repository))
                .OrderBy(s => s.Repository, StringComparer.Ordinal)
                .ThenBy(s => Name(s.Platform), StringComparer.Ordinal)
                .ThenBy(s => s.BaseUrl, StringComparer.Ordinal)
                .Select(s =>
                {
                    var fromSource = _counted.Where(e =>
                        e.Source.Platform == s.Platform
                        && string.Equals(e.Source.BaseUrl, s.BaseUrl, StringComparison.Ordinal)
                        && string.Equals(e.Source.Repository, s.Repository, StringComparison.Ordinal)).ToList();
                    return (IReadOnlyList<string>)[s.Repository, Invariant(fromSource.Count(IsCommit)), Invariant(fromSource.Count(IsMerged))];
                })
                .ToList();

            var refs = _items.Select(w => w.Id)
                .Concat(_counted.Select(e => e.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                .ToList();

            return new SlideContent(
                new()
                {
                    ["title"] = Text(_labels.SummaryTitle),
                    ["highlights"] = new BulletsSlotValue(
                    [
                        new BulletItem(LayoutLabels.Format(_labels.SummaryStatus, status), 0),
                        new BulletItem(LayoutLabels.Format(_labels.SummaryActivity, activity), 1),
                    ]),
                    ["activity"] = new TableSlotValue(_labels.ActivityColumns, rows),
                },
                refs);

            string Count(WorkStatus s) => Invariant(_items.Count(w => w.Status == s));
        }

        private SlideContent Detail(WorkItem item)
        {
            var bullets = item.ObservedFacts.Count > 0
                ? item.ObservedFacts.Select(f => new BulletItem(f.Statement, 0)).ToList()
                : [new BulletItem(item.Summary, 0)];

            if (item.AgentInference is { } inference)
            {
                bullets.Add(new BulletItem(LayoutLabels.Format(_labels.Inference, new Dictionary<string, string>
                {
                    ["confidence"] = inference.Confidence switch
                    {
                        Confidence.Low => _labels.ConfidenceLow,
                        Confidence.Medium => _labels.ConfidenceMedium,
                        _ => _labels.ConfidenceHigh,
                    },
                    ["statement"] = inference.Statement,
                }), 1));
            }

            if (item.Status != WorkStatus.Completed)
            {
                bullets.Add(new BulletItem(item.Status == WorkStatus.InProgress ? _labels.StatusInProgress : _labels.StatusUnknown, 1));
            }

            if (!string.IsNullOrEmpty(item.HumanNotes))
            {
                bullets.Add(new BulletItem(LayoutLabels.Format(_labels.HumanNotes, new Dictionary<string, string> { ["notes"] = item.HumanNotes }), 1));
            }

            var refs = item.EvidenceRefs
                .Concat(item.ObservedFacts.SelectMany(f => f.EvidenceRefs))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);

            return new SlideContent(
                new() { ["title"] = Text(item.Title), ["facts"] = new BulletsSlotValue(bullets) },
                [item.Id, .. refs]);
        }

        private string CellStatus(WorkStatus status) => status switch
        {
            WorkStatus.Completed => _labels.CellStatusCompleted,
            WorkStatus.InProgress => _labels.CellStatusInProgress,
            _ => _labels.CellStatusUnknown,
        };

        private static TextSlotValue Text(string text) => new(text);

        private static bool IsCommit(EvidenceItem e) => e.Kind == EvidenceKind.Commit;

        private static bool IsMerged(EvidenceItem e) => e is { Kind: EvidenceKind.ChangeRequest, State: EvidenceState.Merged };

        private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
