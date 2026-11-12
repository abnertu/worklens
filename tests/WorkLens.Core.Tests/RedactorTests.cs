using WorkLens.Core.Security;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class RedactorTests
{
    private static readonly string ClassicToken = "ghp_" + new string('a', 36);
    private static readonly string FineGrainedToken = "github_pat_" + new string('B', 22) + "_" + new string('c', 59);
    private static readonly string GitLabToken = "glpat-" + new string('x', 18) + "-_";

    [Fact]
    public void Registered_secret_is_masked_even_without_a_known_shape()
    {
        var redactor = new Redactor();
        const string Secret = "0123456789abcdef0123456789abcdef01234567";
        redactor.AddSecret(Secret);

        Assert.Equal("boom: *** failed", redactor.Redact($"boom: {Secret} failed"));
    }

    [Fact]
    public void Longer_secret_is_masked_whole_when_it_contains_a_shorter_one()
    {
        var redactor = new Redactor();
        redactor.AddSecret("abcd1234");
        redactor.AddSecret("xxabcd1234yy");

        Assert.Equal("v=***", redactor.Redact("v=xxabcd1234yy"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_secret_is_ignored(string? secret)
    {
        var redactor = new Redactor();
        redactor.AddSecret(secret);

        Assert.Equal("a b c", redactor.Redact("a b c"));
    }

    [Fact]
    public void Whole_fine_grained_token_is_masked()
    {
        var text = $"token {FineGrainedToken} end";

        var redacted = new Redactor().Redact(text);

        Assert.Equal("token *** end", redacted);
        Assert.DoesNotContain("github_pat_", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('c', 10), redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Classic_and_gitlab_tokens_are_masked()
    {
        Assert.Equal("a *** b *** c", new Redactor().Redact($"a {ClassicToken} b {GitLabToken} c"));
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.def", "Authorization: ***")]
    [InlineData("authorization:token xyz\nnext", "authorization:***\nnext")]
    [InlineData("PRIVATE-TOKEN: secret-value", "PRIVATE-TOKEN: ***")]
    [InlineData("Cookie: session=1; other=2", "Cookie: ***")]
    public void Credential_header_lines_are_masked(string text, string expected)
    {
        Assert.Equal(expected, new Redactor().Redact(text));
    }

    [Theory]
    [InlineData("https://h/api?token=abc&page=2", "https://h/api?token=***&page=2")]
    [InlineData("https://h/api?page=2&private_token=abc", "https://h/api?page=2&private_token=***")]
    [InlineData("GET https://h/api?ACCESS_TOKEN=abc#x", "GET https://h/api?ACCESS_TOKEN=***#x")]
    public void Credential_url_parameters_are_masked(string text, string expected)
    {
        Assert.Equal(expected, new Redactor().Redact(text));
    }

    [Fact]
    public void Header_values_are_masked_by_name()
    {
        var redactor = new Redactor();

        Assert.Equal(Redactor.Mask, redactor.RedactHeader("authorization", "Bearer abc"));
        Assert.Equal(Redactor.Mask, redactor.RedactHeader("PRIVATE-TOKEN", "abc"));
        Assert.Equal("4999", redactor.RedactHeader("x-ratelimit-remaining", "4999"));
        Assert.Equal("see ***", redactor.RedactHeader("Location", "see " + ClassicToken));
    }

    [Fact]
    public void Text_without_secrets_is_unchanged()
    {
        const string Text = "GET https://api.github.com/repos/octo/hello/commits?per_page=100 -> 200";

        Assert.Equal(Text, new Redactor().Redact(Text));
    }
}
