using System.Text.RegularExpressions;

namespace WorkLens.Core.Security;

/// <summary>
/// Cleans Evidence IR text that a slide shows. Slides leave the machine, so a title of a change request or an issue
/// loses its links, addresses and commit hashes before it is shown.
/// </summary>
/// <remarks>
/// <para>
/// The text first goes through a <see cref="Redactor"/> without registered secrets: token shapes, credential headers and
/// credential URL parameters become <c>***</c>. Then these shapes are removed, in this order: URLs with a scheme, SSH
/// remotes, URLs without a scheme (<c>www.</c> or <c>host.tld/</c>), email addresses (with <c>mailto:</c>) and commit
/// hashes (7 to 40 hex characters with at least one digit and one letter a–f).
/// </para>
/// <para>
/// The shapes use only ASCII characters, so a CJK character or full-width punctuation ends a match even without a
/// space. A match gives back its trailing <c>.,;:!?)]'</c> characters, so sentence punctuation stays.
/// </para>
/// <para>
/// Limits: a host name without a path (for example <c>gitlab.example.com</c>) stays, and a word such as
/// <c>README.md/x</c> is removed as a URL.
/// </para>
/// </remarks>
public static partial class SlideDisclosure
{
    /// <summary>The text of a title that has nothing left.</summary>
    public const string Empty = "—";

    // RFC 3986 URL characters.
    private const string UrlChar = @"[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]";

    private const string TrailingChars = ".,;:!?)]'";

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.-]*://" + UrlChar + "+", RegexOptions.CultureInvariant)]
    private static partial Regex SchemeUrl();

    [GeneratedRegex(@"[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[A-Za-z0-9._~/-]+", RegexOptions.CultureInvariant)]
    private static partial Regex SshRemote();

    [GeneratedRegex(@"(?<![A-Za-z0-9.-])(?:www\." + UrlChar + "+|(?:[A-Za-z0-9-]+\\.)+[A-Za-z]{2,}/" + UrlChar + "*)", RegexOptions.CultureInvariant)]
    private static partial Regex SchemelessUrl();

    [GeneratedRegex(@"(?:mailto:)?[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?=[0-9A-Fa-f]*[0-9])(?=[0-9A-Fa-f]*[A-Fa-f])[0-9A-Fa-f]{7,40}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex CommitHash();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>The title as a slide may show it; <see cref="Empty"/> when nothing is left.</summary>
    public static string EvidenceTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var text = new Redactor().Redact(title);
        foreach (var shape in (Regex[])[SchemeUrl(), SshRemote(), SchemelessUrl(), Email(), CommitHash()])
        {
            text = shape.Replace(text, KeepTrailing);
        }

        text = Whitespace().Replace(text, " ").Trim();
        return text.Length == 0 ? Empty : text;
    }

    // Removes the match except its trailing sentence punctuation.
    private static string KeepTrailing(Match match)
    {
        var value = match.Value;
        var end = value.Length;
        while (end > 0 && TrailingChars.Contains(value[end - 1], StringComparison.Ordinal))
        {
            end--;
        }

        return value[end..];
    }
}
