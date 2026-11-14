using WorkLens.Core.Common;
using WorkLens.Core.Evidence;
using WorkLens.Core.Identities;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class IdentityMatcherTests
{
    private static readonly SourcePerson Stranger = new("Someone Else", "someone@example.com", "someone");

    private static IdentityMatcher Matcher(params Identity[] identities) => new(identities, SourcePlatform.Github);

    [Theory]
    [InlineData("12345+Octocat@users.noreply.github.com", "Octocat")]
    [InlineData("octocat@users.noreply.github.com", "octocat")]
    [InlineData("49699333+dependabot[bot]@users.noreply.github.com", "dependabot[bot]")]
    [InlineData("OCTOCAT@USERS.NOREPLY.GITHUB.COM", "OCTOCAT")]
    public void Noreply_email_yields_the_login(string email, string login)
    {
        Assert.Equal(login, IdentityMatcher.LoginFromNoreplyEmail(email));
    }

    [Theory]
    [InlineData("octocat@example.com")]
    [InlineData("octocat@users.noreply.github.com.evil")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_email_yields_no_login(string? email)
    {
        Assert.Null(IdentityMatcher.LoginFromNoreplyEmail(email));
    }

    [Fact]
    public void Username_matches_ignoring_case()
    {
        var matcher = Matcher(new Identity(SourcePlatform.Github, Username: "octocat"));

        var author = matcher.MatchCommit(new SourcePerson("Octo", "o@example.com", "OctoCat"), Stranger, "fix");

        Assert.Equal(new EvidenceAuthor(AuthorRole.Author, "Octo", "OctoCat", "o@example.com"), author);
    }

    [Fact]
    public void Email_matches_ignoring_case()
    {
        var matcher = Matcher(new Identity(Email: "octo@example.com"));

        Assert.True(matcher.IsSelf(new SourcePerson(Email: "Octo@Example.COM")));
    }

    [Theory]
    [InlineData("12345+octocat@users.noreply.github.com")]
    [InlineData("OctoCat@users.noreply.github.com")]
    public void Both_noreply_forms_match_the_username(string email)
    {
        var matcher = Matcher(new Identity(SourcePlatform.Github, Username: "octocat"));

        Assert.True(matcher.IsSelf(new SourcePerson(Email: email)));
    }

    [Fact]
    public void Noreply_email_is_not_resolved_on_gitlab()
    {
        var matcher = new IdentityMatcher([new Identity(Username: "octocat")], SourcePlatform.Gitlab);

        Assert.False(matcher.IsSelf(new SourcePerson(Email: "1+octocat@users.noreply.github.com")));
    }

    [Fact]
    public void Identity_without_platform_applies_to_every_platform()
    {
        Identity[] identities = [new Identity(Username: "octocat")];

        Assert.True(new IdentityMatcher(identities, SourcePlatform.Github).IsSelf(new SourcePerson(Login: "octocat")));
        Assert.True(new IdentityMatcher(identities, SourcePlatform.Gitlab).IsSelf(new SourcePerson(Login: "octocat")));
    }

    [Fact]
    public void Identity_for_another_platform_does_not_apply()
    {
        var matcher = new IdentityMatcher([new Identity(SourcePlatform.Gitlab, Username: "octocat")], SourcePlatform.Github);

        Assert.False(matcher.IsSelf(new SourcePerson(Login: "octocat")));
    }

    [Fact]
    public void Co_authored_by_trailers_are_parsed_case_insensitively_and_in_order()
    {
        const string Message = "Add feature\n\nBody text.\n\nco-authored-by: Ada Lovelace <ada@example.com>\r\nCo-Authored-By:Grace <grace@example.com>  \nCo-authored-by: <anon@example.com>\n";

        var coAuthors = IdentityMatcher.ParseCoAuthors(Message);

        Assert.Equal(
            [new SourcePerson("Ada Lovelace", "ada@example.com"), new SourcePerson("Grace", "grace@example.com"), new SourcePerson(null, "anon@example.com")],
            coAuthors);
    }

    [Fact]
    public void Co_author_in_a_later_trailer_matches_with_the_trailer_identity()
    {
        var matcher = Matcher(new Identity(Email: "me@example.com"));
        const string Message = "Pair work\n\nCo-authored-by: Ada <ada@example.com>\nCo-authored-by: Me Myself <ME@example.com>\n";

        var author = matcher.MatchCommit(Stranger, Stranger, Message);

        Assert.Equal(new EvidenceAuthor(AuthorRole.CoAuthor, "Me Myself", null, "ME@example.com"), author);
    }

    [Theory]
    [InlineData("Document syntax\n\nCo-authored-by: Me <me@example.com>\n\nThis line explains the example above.")]
    [InlineData("Document syntax\n\nCo-authored-by: Me <me@example.com>\nThis is body text, not a trailer.")]
    [InlineData("Document syntax\nCo-authored-by: Me <me@example.com>")]
    public void Co_author_examples_outside_a_trailer_block_do_not_attribute_the_commit(string message)
    {
        var matcher = Matcher(new Identity(Email: "me@example.com"));

        Assert.Null(matcher.MatchCommit(Stranger, Stranger, message));
    }

    [Fact]
    public void Role_priority_is_author_then_co_author_then_committer()
    {
        var matcher = Matcher(new Identity(Email: "me@example.com"));
        var me = new SourcePerson("Me", "me@example.com", "me-login");
        const string Trailer = "x\n\nCo-authored-by: Me <me@example.com>";

        Assert.Equal(AuthorRole.Author, matcher.MatchCommit(me, me, Trailer)!.Role);
        Assert.Equal(AuthorRole.CoAuthor, matcher.MatchCommit(Stranger, me, Trailer)!.Role);
        var committer = matcher.MatchCommit(Stranger, me, "x");
        Assert.Equal(new EvidenceAuthor(AuthorRole.Committer, "Me", "me-login", "me@example.com"), committer);
        Assert.Null(matcher.MatchCommit(Stranger, Stranger, "x"));
    }

    [Fact]
    public void Invalid_email_is_left_out_of_the_author()
    {
        var matcher = Matcher(new Identity(SourcePlatform.Github, Username: "octocat"));

        var author = matcher.MatchCommit(new SourcePerson("Octo", "not an email", "octocat"), null, "x");

        Assert.Equal(new EvidenceAuthor(AuthorRole.Author, "Octo", "octocat"), author);
    }

    [Fact]
    public void Change_request_author_matches_by_login()
    {
        var matcher = Matcher(new Identity(SourcePlatform.Github, Username: "octocat"));

        Assert.Equal(new EvidenceAuthor(AuthorRole.Author, Username: "octocat"), matcher.MatchChangeRequest(new SourcePerson(Login: "octocat")));
        Assert.Null(matcher.MatchChangeRequest(Stranger));
    }
}
