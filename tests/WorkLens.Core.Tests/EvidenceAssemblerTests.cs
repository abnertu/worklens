using System.Globalization;
using WorkLens.Core.Common;
using WorkLens.Core.Evidence;
using WorkLens.Core.Json;
using WorkLens.Core.Validation;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class EvidenceAssemblerTests
{
    private static readonly GeneratorInfo Generator = new("worklens", "0.1.0");
    private static readonly DateTimeOffset Taipei = At("2026-10-15T09:00:00+08:00");

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static EvidenceQuery Query(string baseUrl = "https://API.github.com/") => new(
        new Period("2026-10", At("2026-10-01T00:00:00+08:00"), At("2026-11-01T00:00:00+08:00"), "Asia/Taipei"),
        [new Identity(SourcePlatform.Github, Username: "octocat")],
        [new SourceSpec(SourcePlatform.Github, baseUrl, "octo/hello")]);

    private static EvidenceItem Commit(string sha, DateTimeOffset occurredAt, AuthorRole role = AuthorRole.Author, ChangeStats? stats = null,
        IReadOnlyList<string>? paths = null, IReadOnlyList<EvidenceFlag>? flags = null) => new(
        EvidenceIds.ForItem(SourcePlatform.Github, "https://api.github.com", "octo/hello", EvidenceKind.Commit, sha),
        EvidenceKind.Commit,
        new EvidenceSource(SourcePlatform.Github, "https://api.github.com/", "octo/hello", sha, $"https://github.com/octo/hello/commit/{sha}"),
        "Commit " + sha,
        new EvidenceAuthor(role, Username: "octocat"),
        new EvidenceTimestamps(occurredAt, AuthoredAt: occurredAt),
        Taipei,
        Stats: stats,
        Paths: paths,
        Flags: flags);

    private static EvidenceItem Pr(int number, DateTimeOffset occurredAt, params string[] contains) => new(
        EvidenceIds.ForItem(SourcePlatform.Github, "https://api.github.com", "octo/hello", EvidenceKind.ChangeRequest, number.ToString(CultureInfo.InvariantCulture)),
        EvidenceKind.ChangeRequest,
        new EvidenceSource(SourcePlatform.Github, "https://api.github.com", "octo/hello", number.ToString(CultureInfo.InvariantCulture), $"https://github.com/octo/hello/pull/{number}"),
        "PR " + number,
        new EvidenceAuthor(AuthorRole.Author, Username: "octocat"),
        new EvidenceTimestamps(occurredAt, CreatedAt: occurredAt),
        Taipei,
        State: EvidenceState.Merged,
        Relations: [.. contains.Select(id => new EvidenceRelation(RelationType.Contains, id))]);

    [Fact]
    public void Output_is_schema_valid_and_normalized()
    {
        var a = Commit("a1", At("2026-10-05T10:00:00+08:00"), paths: ["src/b.cs", "src/a.cs", "src/a.cs"]);
        var b = Commit("b2", At("2026-10-03T10:00:00+08:00"));
        var pr = Pr(7, At("2026-10-06T10:00:00+08:00"), a.Id, b.Id);

        var document = EvidenceAssembler.Assemble(Query(), [a, pr, b], Taipei, Generator);

        var json = WorkLensJson.SerializeCanonical(document);
        var result = new IrSchemaValidator().Validate(EvidenceDocument.SchemaId, json);
        Assert.True(result.IsValid, string.Join(", ", result.Issues.Select(i => $"{i.Code}@{i.InstancePath}")));

        Assert.Equal([b.Id, a.Id, pr.Id], document.Items.Select(i => i.Id));
        Assert.Equal(["src/a.cs", "src/b.cs"], document.Items[1].Paths);
        Assert.Equal(new[] { a.Id, b.Id }.Order(StringComparer.Ordinal), document.Items[2].Relations!.Select(r => r.TargetId));
        Assert.All(document.Items, i => Assert.Equal("https://api.github.com", i.Source.BaseUrl));
        Assert.Equal("https://api.github.com", document.Query.Sources[0].BaseUrl);
        Assert.DoesNotContain("+08:00", json, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.Zero, document.GeneratedAt.Offset);
        Assert.Equal(TimeSpan.Zero, document.Query.Period.Start.Offset);
    }

    [Fact]
    public void Document_id_uses_the_normalized_query()
    {
        var document = EvidenceAssembler.Assemble(Query("https://API.github.com:443/"), [], Taipei, Generator);

        Assert.Equal(EvidenceIds.ForDocument(EvidenceAssembler.Assemble(Query("https://api.github.com"), [], Taipei, Generator).Query), document.DocumentId);
        Assert.StartsWith("evd-2026-10-", document.DocumentId, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_id_is_merged_with_the_detail_fields_union_and_best_role()
    {
        var occurred = At("2026-10-05T10:00:00Z");
        var fromPr = Commit("a1", occurred, AuthorRole.Committer, flags: [EvidenceFlag.Revert]) with
        {
            Relations = [new EvidenceRelation(RelationType.References, "ev-0000000000000001")],
        };
        var detail = Commit("a1", occurred, AuthorRole.Author, stats: new ChangeStats(2, 10, 1), flags: [EvidenceFlag.MergeCommit]);

        var document = EvidenceAssembler.Assemble(Query(), [fromPr, detail], Taipei, Generator);

        var item = Assert.Single(document.Items);
        Assert.Equal(new ChangeStats(2, 10, 1), item.Stats);
        Assert.Equal(AuthorRole.Author, item.Author.Role);
        Assert.Equal([EvidenceFlag.MergeCommit, EvidenceFlag.Revert], item.Flags);
        Assert.Equal("ev-0000000000000001", Assert.Single(item.Relations!).TargetId);
    }

    [Fact]
    public void Ties_on_occurred_at_sort_by_id_and_input_order_does_not_matter()
    {
        var at = At("2026-10-05T10:00:00Z");
        EvidenceItem[] items = [Commit("c3", at), Commit("a1", at), Commit("b2", at)];

        var forward = EvidenceAssembler.Assemble(Query(), items, Taipei, Generator);
        var backward = EvidenceAssembler.Assemble(Query(), items.Reverse(), Taipei, Generator);

        Assert.Equal(forward.Items.Select(i => i.Id).Order(StringComparer.Ordinal), forward.Items.Select(i => i.Id));
        Assert.Equal(WorkLensJson.SerializeCanonical(forward), WorkLensJson.SerializeCanonical(backward));
    }
}
