using System.Globalization;
using System.Text;
using WorkLens.Core.Contracts;
using WorkLens.Core.Errors;
using WorkLens.Core.Evidence;
using WorkLens.Core.Presentation;
using WorkLens.Core.Report;
using WorkLens.Core.Templates;
using WorkLens.Core.Validation;

namespace WorkLens.Core.Layout;

/// <summary>
/// Deterministic Report IR → Presentation IR mapping (docs/design/domain-model.md section 5). A pure function: the
/// same inputs always give the same output, with no clock, randomness or environment access.
/// </summary>
public sealed partial class LayoutResolver : ILayoutResolver
{
    /// <summary>
    /// The slots each purpose binds under a 1.0 manifest, with their kinds; <c>Required</c> slots must exist in the
    /// layout.
    /// </summary>
    private static readonly Dictionary<LayoutPurpose, (string Id, SlotKind Kind, bool Required)[]> PurposeSlots = new()
    {
        [LayoutPurpose.Cover] = [("title", SlotKind.Text, true), ("subtitle", SlotKind.Text, false)],
        [LayoutPurpose.Summary] = [("title", SlotKind.Text, true), ("highlights", SlotKind.Bullets, false), ("activity", SlotKind.Table, false)],
        [LayoutPurpose.Achievements] = [("title", SlotKind.Text, true), ("items", SlotKind.Table, false)],
        [LayoutPurpose.WorkItemDetail] = [("title", SlotKind.Text, true), ("facts", SlotKind.Bullets, true)],
        [LayoutPurpose.Narrative] = [("title", SlotKind.Text, true), ("body", SlotKind.Bullets, true)],
        [LayoutPurpose.Closing] = [("title", SlotKind.Text, true)],
    };

    /// <summary>The slots each purpose binds under a 1.1 manifest: one slot for each element of the report design.</summary>
    private static readonly Dictionary<LayoutPurpose, (string Id, SlotKind Kind, bool Required)[]> PurposeSlots11 = new()
    {
        [LayoutPurpose.Cover] =
        [
            ("title", SlotKind.Text, true), ("kicker", SlotKind.Text, false), ("subtitle", SlotKind.Text, false),
            ("subject", SlotKind.Text, false), ("meta", SlotKind.Text, false), ("band_year", SlotKind.Text, false),
            ("band_month", SlotKind.Text, false),
        ],
        [LayoutPurpose.Summary] =
        [
            ("title", SlotKind.Text, true), ("period", SlotKind.Text, false), ("metric_completed", SlotKind.Metric, false),
            ("metric_in_progress", SlotKind.Metric, false), ("metric_operations", SlotKind.Metric, false),
            ("metric_projects", SlotKind.Metric, false), ("highlights_title", SlotKind.Text, false),
            ("highlights", SlotKind.Bullets, false), ("support_title", SlotKind.Text, false), ("support", SlotKind.Bullets, false),
            ("activity_note", SlotKind.Text, false),
        ],
        [LayoutPurpose.Achievements] = TableSlots(),
        [LayoutPurpose.Operations] = TableSlots(),
        [LayoutPurpose.ChallengeTable] = TableSlots(),
        [LayoutPurpose.WorkItemDetail] =
        [
            ("title", SlotKind.Text, true), ("kicker", SlotKind.Text, false), ("status", SlotKind.Text, false),
            ("facts_title", SlotKind.Text, false), ("facts", SlotKind.Bullets, true), ("evidence_title", SlotKind.Text, false),
            ("evidence", SlotKind.Bullets, false),
        ],
        [LayoutPurpose.Narrative] = [("title", SlotKind.Text, true), ("body", SlotKind.Bullets, true)],
        [LayoutPurpose.Closing] = [("title", SlotKind.Text, true)],
    };

    /// <summary>The slot whose items flow onto continuation slides when <c>overflow: split</c>.</summary>
    private static readonly Dictionary<LayoutPurpose, string> FlowSlots = new()
    {
        [LayoutPurpose.Achievements] = "items",
        [LayoutPurpose.Operations] = "items",
        [LayoutPurpose.ChallengeTable] = "items",
        [LayoutPurpose.WorkItemDetail] = "facts",
        [LayoutPurpose.Narrative] = "body",
    };

    /// <summary>
    /// A 1.1 summary slot that stays unbound when the sequence has no operations section, so a manifest cannot
    /// require it.
    /// </summary>
    private const string MetricOperationsSlot = "metric_operations";

    private const string TitleSlot = "title";

    private static (string Id, SlotKind Kind, bool Required)[] TableSlots() =>
        [("title", SlotKind.Text, true), ("status", SlotKind.Text, false), ("items", SlotKind.Table, false)];

    public LayoutResult Resolve(ReportDocument report, string reportSha256, EvidenceDocument evidence, TemplateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(manifest);

        bool v11;
        switch (manifest.SchemaVersion)
        {
            case "1.0":
                v11 = false;
                break;
            case "1.1":
                v11 = true;
                break;
            default:
                // Only a manifest built in code gets here: the CLI's schema validation refuses other versions first.
                return new LayoutResult(null, [Error("manifest.unsupported_version", "/schema_version",
                    $"Manifest schema_version {manifest.SchemaVersion} is not supported; use 1.0 or 1.1.")]);
        }

        var idErrors = TemplateManifestChecker.CheckIds(manifest);
        if (idErrors.Count > 0)
        {
            return new LayoutResult(null, idErrors);
        }

        // A 1.0 manifest has no table contracts, so this finds nothing for it. For a 1.1 manifest built in code, it
        // keeps a bad column key from failing the table build.
        var contractErrors = TemplateManifestChecker.CheckContracts(manifest);
        if (contractErrors.Count > 0)
        {
            return new LayoutResult(null, contractErrors);
        }

        var conventions = v11 ? PurposeSlots11 : PurposeSlots;
        var labels = LayoutLabels.From(manifest.Labels);
        var context = new Context(report, evidence, labels, v11, manifest);
        var continuation = v11 ? labels.Continuation : null;
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
            LayoutPurpose[] expected = v11 ? PurposesOf11(entry.Section) : [PurposeOf(entry.Section)];
            if (!expected.Contains(layout.Purpose))
            {
                errors.Add(Error("manifest.purpose_mismatch", $"/sequence/{s}/layout_id",
                    $"Section {Name(entry.Section)} needs a layout with purpose {string.Join(" or ", expected.Select(Name))}, but '{layout.Id}' has purpose {Name(layout.Purpose)}."));
                continue;
            }

            if (!checkedLayouts.TryGetValue(layoutIndex, out var layoutOk))
            {
                layoutOk = CheckSlots(layout, layoutIndex, conventions[layout.Purpose], v11, errors);
                checkedLayouts[layoutIndex] = layoutOk;
            }

            if (!layoutOk)
            {
                continue;
            }

            foreach (var content in context.Contents(entry.Section, layout))
            {
                pages.AddRange(Bind(layout, layoutIndex, conventions[layout.Purpose], content, continuation, errors));
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
                // The 1.1 schema makes source_refs unique, and one improvement can resolve several challenges.
                v11 ? p.SourceRefs.Distinct(StringComparer.Ordinal).ToList() : p.SourceRefs,
                p.Continuation))
            .ToList();

        var presentation = new PresentationDocument(
            PresentationDocument.CurrentSchemaVersion,
            "prs-" + (report.ReportId.StartsWith("rpt-", StringComparison.Ordinal) ? report.ReportId[4..] : report.ReportId),
            new SourceReportRef(report.ReportId, reportSha256),
            new TemplateRef(manifest.Template.Id, manifest.Template.Version),
            slides,
            v11 && manifest.Footer is { } footer ? Footer(labels, report, footer) : null);
        return new LayoutResult(presentation, [], UnplacedNarratives(report, slides, v11));
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

    private static LayoutPurpose[] PurposesOf11(ReportSection section) => section switch
    {
        ReportSection.Operations => [LayoutPurpose.Operations],
        ReportSection.OperationDetails => [LayoutPurpose.WorkItemDetail],
        ReportSection.Challenges => [LayoutPurpose.Narrative, LayoutPurpose.ChallengeTable],
        _ => [PurposeOf(section)],
    };

    /// <summary>Checks that the layout has the purpose's required slots and that known slots have the right kind.</summary>
    private static bool CheckSlots(
        LayoutDefinition layout,
        int layoutIndex,
        (string Id, SlotKind Kind, bool Required)[] conventions,
        bool v11,
        List<WorkLensError> errors)
    {
        var before = errors.Count;
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
            if (convention.Id is null && slot.Required == true)
            {
                // The resolver never binds a slot outside the purpose's conventions, so a required one would make every
                // presentation fail the render-time check (presentation.missing_required_slot).
                errors.Add(Error("manifest.slot_mismatch", $"/layouts/{layoutIndex}/slots/{k}",
                    $"Slot '{slot.Id}' of layout '{layout.Id}' is required, but purpose {Name(layout.Purpose)} never fills it."));
            }

            if (v11 && layout.Purpose == LayoutPurpose.Summary && slot.Id == MetricOperationsSlot && slot.Required == true)
            {
                errors.Add(Error("manifest.slot_mismatch", $"/layouts/{layoutIndex}/slots/{k}",
                    $"Slot '{slot.Id}' of layout '{layout.Id}' cannot be required: it stays unbound when the sequence has no operations section."));
            }

            if (convention.Id is not null && convention.Kind != slot.Kind)
            {
                errors.Add(Error("manifest.slot_mismatch", $"/layouts/{layoutIndex}/slots/{k}/kind",
                    $"Slot '{slot.Id}' of layout '{layout.Id}' must be kind {Name(convention.Kind)}, not {Name(slot.Kind)}."));
            }
        }

        return errors.Count == before;
    }

    /// <summary>Binds one slide's content to the layout's slots, applying max_chars / max_items and splitting.</summary>
    private static IEnumerable<Page> Bind(
        LayoutDefinition layout,
        int layoutIndex,
        (string Id, SlotKind Kind, bool Required)[] known,
        SlideContent content,
        string? continuation,
        List<WorkLensError> errors)
    {
        FlowSlots.TryGetValue(layout.Purpose, out var flowSlot);
        var bound = new List<(string Id, List<SlotValue> Chunks)>();
        SlotDefinition? titleSlot = null;

        for (var k = 0; k < layout.Slots.Count; k++)
        {
            var slot = layout.Slots[k];
            if (!known.Any(c => c.Id == slot.Id))
            {
                continue;
            }

            var value = content.Values.GetValueOrDefault(slot.Id);
            if (value is null || SlotFitter.IsEmpty(value))
            {
                if (slot.Required == true)
                {
                    errors.Add(Error("manifest.slot_mismatch", $"/layouts/{layoutIndex}/slots/{k}",
                        $"Slot '{slot.Id}' of layout '{layout.Id}' is required, but the report has no content for it."));
                }

                continue;
            }

            var split = slot.Overflow == OverflowPolicy.Split && slot.Id == flowSlot;
            bound.Add((slot.Id, SlotFitter.Fit(value, slot, split, content.Grouped?.Contains(slot.Id) == true)));
            if (slot.Id == TitleSlot)
            {
                titleSlot = slot;
            }
        }

        IReadOnlyList<string> sourceRefs = content.SlotRefs is { } slotRefs
            ? [.. content.SourceRefs, .. bound.SelectMany(b => slotRefs.GetValueOrDefault(b.Id) ?? [])]
            : content.SourceRefs;
        var total = bound.Count == 0 ? 1 : bound.Max(b => b.Chunks.Count);
        for (var page = 0; page < total; page++)
        {
            var bindings = new Dictionary<string, SlotValue>(StringComparer.Ordinal);
            foreach (var (id, chunks) in bound)
            {
                bindings[id] = chunks[Math.Min(page, chunks.Count - 1)];
            }

            if (page > 0 && continuation is not null && titleSlot is not null && content.Values[TitleSlot] is TextSlotValue title)
            {
                var marker = LayoutLabels.Format(continuation, new Dictionary<string, string>
                {
                    ["index"] = (page + 1).ToString(CultureInfo.InvariantCulture),
                    ["total"] = total.ToString(CultureInfo.InvariantCulture),
                });
                bindings[TitleSlot] = ContinuedTitle(title, marker, titleSlot);
            }

            yield return new Page(
                layout.Id,
                bindings,
                sourceRefs,
                total > 1 ? new SlideContinuation(page + 1, total) : null);
        }
    }

    /// <summary>
    /// <c>layout.unplaced_narrative</c> for each narrative that no slide shows. Under 1.1, a narrative is placed when a
    /// slide lists it in <c>source_refs</c>. A 1.0 manifest has no section for support, so only support narratives are
    /// reported; the other kinds keep the 1.0 behavior.
    /// </summary>
    private static List<WorkLensError> UnplacedNarratives(ReportDocument report, IReadOnlyList<Slide> slides, bool v11)
    {
        var placed = slides.SelectMany(s => s.SourceRefs).ToHashSet(StringComparer.Ordinal);
        var warnings = new List<WorkLensError>();
        for (var i = 0; i < report.Narratives.Count; i++)
        {
            var narrative = report.Narratives[i];
            var unplaced = v11 ? !placed.Contains(narrative.Id) : narrative.Kind == NarrativeKind.Support;
            if (unplaced)
            {
                warnings.Add(new WorkLensError(
                    "layout.unplaced_narrative",
                    $"Report narrative '{narrative.Id}' ({Name(narrative.Kind)}) is on no slide: the manifest sequence has no section that shows it.",
                    new Dictionary<string, string> { ["path"] = $"/narratives/{i}" }));
            }
        }

        return warnings;
    }

    /// <summary>The footer of every slide: the <c>footer</c> label for the period, cut to the footer capacity.</summary>
    private static string Footer(LayoutLabels labels, ReportDocument report, FooterDefinition footer)
    {
        var text = LayoutLabels.Format(labels.Footer, new Dictionary<string, string> { ["period"] = report.Period.Label });
        var slot = new SlotDefinition("footer", new PlaceholderRef(), SlotKind.Text, MaxChars: footer.MaxChars);
        return ((TextSlotValue)SlotFitter.Fit(new TextSlotValue(text), slot, split: false).Single()).Text;
    }

    /// <summary>
    /// The title of a continuation slide: the title, then the marker with no character between. A title over
    /// <c>max_chars</c> loses its own end, so that the whole marker stays readable. When <c>max_chars</c> leaves no
    /// room for 2 characters of title, the joined text is truncated like any other text.
    /// </summary>
    private static SlotValue ContinuedTitle(TextSlotValue title, string marker, SlotDefinition slot)
    {
        var cleanMarker = RemoveInvalidXmlCharacters(marker);
        var markerLength = new StringInfo(cleanMarker).LengthInTextElements;
        if (slot.MaxChars is { } max && max < markerLength + 2)
        {
            return SlotFitter.Fit(title with { Text = title.Text + cleanMarker }, slot, split: false).Single();
        }

        var fitted = (TextSlotValue)SlotFitter.Fit(title, slot with { MaxChars = slot.MaxChars - markerLength }, split: false).Single();
        return fitted with { Text = fitted.Text + cleanMarker };
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

    /// <param name="SlotRefs">
    /// IDs that a slide lists in <c>source_refs</c> only when it binds the slot that shows them, after
    /// <paramref name="SourceRefs"/>.
    /// </param>
    /// <param name="Grouped">Slots whose items Layout already fitted to <c>max_items</c> in groups.</param>
    private sealed record SlideContent(
        Dictionary<string, SlotValue> Values,
        IReadOnlyList<string> SourceRefs,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? SlotRefs = null,
        IReadOnlySet<string>? Grouped = null);

    /// <summary>Report-derived content per section, computed once per resolve.</summary>
    private sealed partial class Context
    {
        private readonly ReportDocument _report;
        private readonly EvidenceDocument _evidence;
        private readonly LayoutLabels _labels;
        private readonly List<WorkItem> _items;
        private readonly List<EvidenceItem> _counted;
        private readonly bool _v11;
        private readonly LayoutFacts _facts;
        private readonly HashSet<ReportSection> _sections;
        private readonly bool _challengeTable;

        public Context(ReportDocument report, EvidenceDocument evidence, LayoutLabels labels, bool v11, TemplateManifest manifest)
        {
            _report = report;
            _evidence = evidence;
            _labels = labels;
            _v11 = v11;
            _facts = new LayoutFacts(report, evidence, labels);
            _sections = manifest.Sequence.Select(e => e.Section).ToHashSet();

            // With a challenge table, an improvement that resolves a challenge shows in the table, not on its own page.
            _challengeTable = manifest.Sequence.Any(e =>
                e.Section == ReportSection.Challenges
                && manifest.Layouts.FirstOrDefault(l => l.Id == e.LayoutId)?.Purpose == LayoutPurpose.ChallengeTable);

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

        public IEnumerable<SlideContent> Contents(ReportSection section, LayoutDefinition layout) =>
            _v11 ? Contents11(section, layout) : Contents10(section);

        private IEnumerable<SlideContent> Contents10(ReportSection section)
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
