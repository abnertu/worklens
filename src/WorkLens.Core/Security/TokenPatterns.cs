using System.Text.RegularExpressions;

namespace WorkLens.Core.Security;

/// <summary>
/// Shapes of platform access tokens (docs/security/security-privacy.md section 3). Shared by the configuration
/// secret check and the <c>Redactor</c>, so both recognise the same tokens.
/// </summary>
public static partial class TokenPatterns
{
    /// <summary>GitHub classic and OAuth tokens: <c>ghp_</c>, <c>gho_</c>, <c>ghu_</c>, <c>ghs_</c>, <c>ghr_</c>.</summary>
    public const string GitHubPrefixed = "gh[pousr]_[A-Za-z0-9]{36,}";

    /// <summary>GitHub fine-grained personal access tokens.</summary>
    public const string GitHubFineGrained = "github_pat_[A-Za-z0-9_]{22,}";

    /// <summary>GitLab personal, project and group access tokens.</summary>
    public const string GitLab = "glpat-[A-Za-z0-9_-]{20,}";

    private const string Alternatives = GitHubPrefixed + "|" + GitHubFineGrained + "|" + GitLab;

    /// <summary>Finds tokens anywhere in a text.</summary>
    [GeneratedRegex(Alternatives, RegexOptions.CultureInvariant)]
    public static partial Regex Any();

    [GeneratedRegex(@"\A(?:" + Alternatives + @")\z", RegexOptions.CultureInvariant)]
    private static partial Regex Whole();

    /// <summary>True when the whole of <paramref name="value"/> is a token of full length.</summary>
    public static bool IsToken(string value) => Whole().IsMatch(value);
}
