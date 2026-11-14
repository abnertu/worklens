using System.Text.RegularExpressions;

namespace WorkLens.Connectors.Git;

/// <summary>Parses the RFC 8288 <c>Link</c> header GitHub uses for pagination.</summary>
public static partial class LinkHeader
{
    [GeneratedRegex(@"<(?<url>[^>]*)>(?<params>[^,]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@";\s*rel\s*=\s*""?(?<rel>[^"";]*)""?", RegexOptions.CultureInvariant)]
    private static partial Regex Rel();

    /// <summary>True when the header holds at least one link with a <c>rel</c> parameter.</summary>
    public static bool HasRelation(IEnumerable<string> values) =>
        values.Any(value => Link().Matches(value).Any(link => Rel().IsMatch(link.Groups["params"].Value)));

    /// <summary>
    /// The raw <c>rel="next"</c> target, or <c>null</c> when there is none. The caller validates it (it must be an
    /// absolute URL on the source host).
    /// </summary>
    public static string? FindNext(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            foreach (Match link in Link().Matches(value))
            {
                foreach (Match rel in Rel().Matches(link.Groups["params"].Value))
                {
                    var relations = rel.Groups["rel"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (relations.Contains("next", StringComparer.OrdinalIgnoreCase))
                    {
                        return link.Groups["url"].Value.Trim();
                    }
                }
            }
        }

        return null;
    }
}
