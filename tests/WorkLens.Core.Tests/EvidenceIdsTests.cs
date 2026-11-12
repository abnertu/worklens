using System.Globalization;
using WorkLens.Core.Common;
using WorkLens.Core.Evidence;
using Xunit;

namespace WorkLens.Core.Tests;

// Expected values were computed outside .NET (Python hashlib) from the hash inputs written in each test, so the
// tests check the documented formula (docs/design/domain-model.md section 2.1), not just the implementation.
// The IDs in the schema examples are illustrative and do not follow the formula.
public sealed class EvidenceIdsTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Commit_id_matches_the_independent_vector()
    {
        // sha256("github|https://api.github.com|octo/hello|commit|0123456789abcdef0123456789abcdef01234567")
        Assert.Equal(
            "ev-92500b2b11d631d5",
            EvidenceIds.ForItem(SourcePlatform.Github, "https://api.github.com", "octo/hello", EvidenceKind.Commit, Sha));
    }

    [Fact]
    public void Change_request_id_uses_the_snake_case_kind()
    {
        // sha256("github|https://api.github.com|octo/hello|change_request|42")
        Assert.Equal(
            "ev-1b75f06f8c965d8d",
            EvidenceIds.ForItem(SourcePlatform.Github, "https://api.github.com", "octo/hello", EvidenceKind.ChangeRequest, "42"));
    }

    [Fact]
    public void Non_default_port_and_path_are_kept()
    {
        // sha256("gitlab|https://gitlab.example:8443/api/v4|grp/sub/proj|commit|abc")
        Assert.Equal(
            "ev-8e5c49fcc9118b31",
            EvidenceIds.ForItem(SourcePlatform.Gitlab, "https://gitlab.example:8443/api/v4/", "grp/sub/proj", EvidenceKind.Commit, "abc"));
    }

    [Theory]
    [InlineData("https://api.github.com/", "octo/hello")]
    [InlineData("https://API.GitHub.com", "Octo/Hello")]
    [InlineData("HTTPS://api.github.com:443/", "OCTO/HELLO")]
    public void Base_url_spelling_and_repository_case_do_not_change_the_id(string baseUrl, string repository)
    {
        Assert.Equal(
            "ev-92500b2b11d631d5",
            EvidenceIds.ForItem(SourcePlatform.Github, baseUrl, repository, EvidenceKind.Commit, Sha));
    }

    [Theory]
    [InlineData("https://api.github.com", "https://api.github.com")]
    [InlineData("https://api.github.com/", "https://api.github.com")]
    [InlineData("https://API.GITHUB.COM:443", "https://api.github.com")]
    [InlineData("https://ghe.example/api/v3/", "https://ghe.example/api/v3")]
    [InlineData("https://ghe.example:8443/api/v3", "https://ghe.example:8443/api/v3")]
    [InlineData("https://gitlab.example/API/v4", "https://gitlab.example/API/v4")]
    public void Base_url_is_normalized(string input, string expected)
    {
        Assert.Equal(expected, EvidenceIds.NormalizeBaseUrl(input));
    }

    [Fact]
    public void Relative_base_url_is_rejected()
    {
        Assert.Throws<UriFormatException>(() => EvidenceIds.NormalizeBaseUrl("api.github.com"));
    }

    [Fact]
    public void Document_id_hashes_the_canonical_json_of_the_query()
    {
        // hashlib.sha256(Canonical.encode()).hexdigest()[:8] == "428a957c"
        const string Canonical = """
            {
              "period": {
                "label": "2026-10",
                "start": "2026-09-30T16:00:00+00:00",
                "end": "2026-10-31T16:00:00+00:00",
                "timezone": "Asia/Taipei"
              },
              "identities": [
                {
                  "platform": "github",
                  "username": "octocat"
                },
                {
                  "email": "octo@example.com"
                }
              ],
              "sources": [
                {
                  "platform": "github",
                  "base_url": "https://api.github.com",
                  "repository": "octo/hello"
                }
              ]
            }

            """;
        var query = new EvidenceQuery(
            new Period("2026-10", Utc("2026-09-30T16:00:00Z"), Utc("2026-10-31T16:00:00Z"), "Asia/Taipei"),
            [new Identity(SourcePlatform.Github, Username: "octocat"), new Identity(Email: "octo@example.com")],
            [new SourceSpec(SourcePlatform.Github, "https://api.github.com", "octo/hello")]);

        Assert.Equal(Canonical.ReplaceLineEndings("\n"), Json.WorkLensJson.SerializeCanonical(query));
        Assert.Equal("evd-2026-10-428a957c", EvidenceIds.ForDocument(query));
    }

    [Fact]
    public void Document_id_depends_on_source_order()
    {
        var period = new Period("2026-10", Utc("2026-09-30T16:00:00Z"), Utc("2026-10-31T16:00:00Z"), "Asia/Taipei");
        SourceSpec a = new(SourcePlatform.Github, "https://api.github.com", "octo/a");
        SourceSpec b = new(SourcePlatform.Github, "https://api.github.com", "octo/b");

        Assert.NotEqual(
            EvidenceIds.ForDocument(new EvidenceQuery(period, [new Identity(Email: "o@example.com")], [a, b])),
            EvidenceIds.ForDocument(new EvidenceQuery(period, [new Identity(Email: "o@example.com")], [b, a])));
    }

    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();
}
