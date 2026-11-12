namespace WorkLens.Core.Evidence;

/// <summary>
/// Builds the Evidence IR document from the items of every source (docs/design/domain-model.md section 2.1):
/// merges items with the same ID, sorts everything deterministically and normalizes times to UTC, so the same
/// input always serializes to the same bytes.
/// </summary>
public static class EvidenceAssembler
{
    /// <param name="query">The query as configured; base URLs are normalized here.</param>
    /// <param name="items">Items of all sources, in any order.</param>
    /// <param name="generatedAt">Time of the run, supplied by the caller (no clock in Core).</param>
    public static EvidenceDocument Assemble(
        EvidenceQuery query,
        IEnumerable<EvidenceItem> items,
        DateTimeOffset generatedAt,
        GeneratorInfo generator)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(items);

        var normalizedQuery = query with
        {
            Period = query.Period with { Start = Utc(query.Period.Start), End = Utc(query.Period.End) },
            Sources = [.. query.Sources.Select(s => s with { BaseUrl = EvidenceIds.NormalizeBaseUrl(s.BaseUrl) })],
        };

        var merged = new Dictionary<string, EvidenceItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            merged[item.Id] = merged.TryGetValue(item.Id, out var existing) ? Merge(existing, item) : item;
        }

        var sorted = merged.Values
            .Select(Normalize)
            .OrderBy(i => i.Timestamps.OccurredAt)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

        return new EvidenceDocument(
            EvidenceDocument.CurrentSchemaVersion,
            EvidenceIds.ForDocument(normalizedQuery),
            Utc(generatedAt),
            generator,
            normalizedQuery,
            sorted);
    }

    // Same ID: relations and flags are united and the higher-priority role wins (author > co_author > committer).
    // Other fields come from the more complete item: one with stats (a commit detail) over one without.
    private static EvidenceItem Merge(EvidenceItem a, EvidenceItem b)
    {
        var primary = a.Stats is null && b.Stats is not null ? b : a;
        var author = b.Author.Role < a.Author.Role ? b.Author : a.Author;
        return primary with
        {
            Author = author,
            Relations = Union(a.Relations, b.Relations),
            Flags = Union(a.Flags, b.Flags),
        };
    }

    private static List<T>? Union<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
    {
        if (a is null && b is null)
        {
            return null;
        }

        return [.. (a ?? []).Concat(b ?? []).Distinct()];
    }

    private static EvidenceItem Normalize(EvidenceItem item) => item with
    {
        Source = item.Source with { BaseUrl = EvidenceIds.NormalizeBaseUrl(item.Source.BaseUrl) },
        Timestamps = new EvidenceTimestamps(
            Utc(item.Timestamps.OccurredAt),
            Utc(item.Timestamps.AuthoredAt),
            Utc(item.Timestamps.CommittedAt),
            Utc(item.Timestamps.CreatedAt),
            Utc(item.Timestamps.MergedAt),
            Utc(item.Timestamps.ClosedAt)),
        FetchedAt = Utc(item.FetchedAt),
        Paths = item.Paths is null ? null : [.. item.Paths.Distinct().Order(StringComparer.Ordinal)],
        Relations = item.Relations is null or { Count: 0 }
            ? null
            : [.. item.Relations.Distinct().OrderBy(r => r.TargetId, StringComparer.Ordinal).ThenBy(r => r.Type)],
        Flags = item.Flags is null or { Count: 0 } ? null : [.. item.Flags.Distinct().Order()],
    };

    private static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();

    private static DateTimeOffset? Utc(DateTimeOffset? value) => value?.ToUniversalTime();
}
