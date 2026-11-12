using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkLens.Core.Common;
using WorkLens.Core.Json;

namespace WorkLens.Core.Evidence;

/// <summary>
/// Stable Evidence IR identifiers (docs/design/domain-model.md section 2.1).
/// </summary>
public static class EvidenceIds
{
    /// <summary>
    /// Normalizes a source base URL: lowercase scheme and host, no default port, no trailing <c>/</c>.
    /// The path keeps its case (for example GitHub Enterprise's <c>/api/v3</c>); query and fragment are dropped.
    /// </summary>
    /// <exception cref="UriFormatException"><paramref name="baseUrl"/> is not an absolute URI.</exception>
    public static string NormalizeBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new UriFormatException($"'{baseUrl}' is not an absolute URL.");
        }

        var builder = new StringBuilder();
        builder.Append(uri.Scheme.ToLowerInvariant()).Append("://").Append(uri.Host.ToLowerInvariant());
        if (!uri.IsDefaultPort)
        {
            builder.Append(':').Append(uri.Port);
        }

        builder.Append(uri.AbsolutePath.TrimEnd('/'));
        return builder.ToString();
    }

    /// <summary>
    /// <c>"ev-" + lowercase hex(SHA-256(UTF-8 of "platform|base_url|repository|kind|external_id"))[0..16]</c>,
    /// with <paramref name="baseUrl"/> normalized and <paramref name="repository"/> lowercased.
    /// </summary>
    public static string ForItem(
        SourcePlatform platform, string baseUrl, string repository, EvidenceKind kind, string externalId)
    {
        var input = string.Join(
            '|',
            JsonName(platform),
            NormalizeBaseUrl(baseUrl),
            repository.ToLowerInvariant(),
            JsonName(kind),
            externalId);
        return "ev-" + HashHex(input)[..16];
    }

    /// <summary>
    /// <c>"evd-" + period label + "-" + lowercase hex(SHA-256(UTF-8 of the canonical JSON of query))[0..8]</c>.
    /// The caller passes the query as it is written to the document (sources in configuration order, base URLs
    /// normalized), so re-running the same query gives the same ID.
    /// </summary>
    public static string ForDocument(EvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var hash = Convert.ToHexStringLower(SHA256.HashData(WorkLensJson.SerializeCanonicalUtf8(query)));
        return "evd-" + query.Period.Label + "-" + hash[..8];
    }

    private static string JsonName<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static string HashHex(string input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}
