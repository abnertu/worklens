using System.Text.RegularExpressions;
using WorkLens.Core.Common;
using WorkLens.Core.Evidence;
using WorkLens.Core.Validation;

namespace WorkLens.Core.Identities;

/// <summary>A person as a source reports it: git name / email, and the platform login when the platform knows it.</summary>
public sealed record SourcePerson(string? Name = null, string? Email = null, string? Login = null);

/// <summary>
/// Decides whether the report subject took part in an item, and in which role (docs/design/domain-model.md
/// section 2.1, OQ-15). Comparisons ignore case. An identity without a platform applies to every platform.
/// </summary>
public sealed partial class IdentityMatcher
{
    private readonly HashSet<string> _usernames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _emails = new(StringComparer.OrdinalIgnoreCase);
    private readonly SourcePlatform _platform;

    public IdentityMatcher(IEnumerable<Identity> identities, SourcePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(identities);
        _platform = platform;
        foreach (var identity in identities)
        {
            if (identity.Platform is { } p && p != platform)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(identity.Username))
            {
                _usernames.Add(identity.Username);
            }

            if (!string.IsNullOrEmpty(identity.Email))
            {
                _emails.Add(identity.Email);
            }
        }
    }

    // <id>+<login>@users.noreply.github.com, or the older <login>@users.noreply.github.com.
    [GeneratedRegex(@"\A(?:\d+\+)?(?<login>[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\[bot\])?)@users\.noreply\.github\.com\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubNoreply();

    [GeneratedRegex(@"^[ \t]*Co-authored-by:[ \t]*(?<name>[^<\r\n]*?)[ \t]*<(?<email>[^>\r\n]+)>[ \t]*\r?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CoAuthorTrailer();

    /// <summary>The login encoded in a GitHub noreply email, or null for any other email.</summary>
    public static string? LoginFromNoreplyEmail(string? email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return null;
        }

        var match = GitHubNoreply().Match(email);
        return match.Success ? match.Groups["login"].Value : null;
    }

    /// <summary>All <c>Co-authored-by: Name &lt;email&gt;</c> trailers in a commit message, in order.</summary>
    public static IReadOnlyList<SourcePerson> ParseCoAuthors(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return [];
        }

        return CoAuthorTrailer().Matches(message)
            .Select(m => new SourcePerson(
                Name: m.Groups["name"].Value.Length > 0 ? m.Groups["name"].Value : null,
                Email: m.Groups["email"].Value.Trim()))
            .ToList();
    }

    /// <summary>True when <paramref name="person"/> is one of the subject's identities.</summary>
    public bool IsSelf(SourcePerson? person)
    {
        if (person is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(person.Login) && _usernames.Contains(person.Login))
        {
            return true;
        }

        if (string.IsNullOrEmpty(person.Email))
        {
            return false;
        }

        if (_emails.Contains(person.Email))
        {
            return true;
        }

        return _platform == SourcePlatform.Github
            && LoginFromNoreplyEmail(person.Email) is { } login
            && _usernames.Contains(login);
    }

    /// <summary>
    /// Matches a commit: author, then <c>Co-authored-by</c> trailers, then committer. Returns null when the subject
    /// is none of them. The returned author is the subject's appearance in the commit.
    /// </summary>
    public EvidenceAuthor? MatchCommit(SourcePerson author, SourcePerson? committer, string? message)
    {
        if (IsSelf(author))
        {
            return ToAuthor(AuthorRole.Author, author);
        }

        foreach (var coAuthor in ParseCoAuthors(message))
        {
            if (IsSelf(coAuthor))
            {
                return ToAuthor(AuthorRole.CoAuthor, coAuthor);
            }
        }

        return IsSelf(committer) ? ToAuthor(AuthorRole.Committer, committer!) : null;
    }

    /// <summary>Matches the author of a change request (only the author role applies).</summary>
    public EvidenceAuthor? MatchChangeRequest(SourcePerson author) =>
        IsSelf(author) ? ToAuthor(AuthorRole.Author, author) : null;

    // Emails that the schema's format: email would reject are left out, so the written evidence stays valid.
    private static EvidenceAuthor ToAuthor(AuthorRole role, SourcePerson person) => new(
        role,
        Name: string.IsNullOrEmpty(person.Name) ? null : person.Name,
        Username: string.IsNullOrEmpty(person.Login) ? null : person.Login,
        Email: EmailFormat.IsValid(person.Email) ? person.Email : null);
}
