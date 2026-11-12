using WorkLens.Core.Common;
using WorkLens.Core.Evidence;

namespace WorkLens.Core.Config;

/// <summary>
/// <c>worklens.json</c> (<c>urn:worklens:schema:config:1.0</c>, docs/design/cli-ux.md section 3.1).
/// Holds no secrets: each source names the environment variable of its token in <see cref="ConfigSource.TokenEnv"/>.
/// </summary>
public sealed record WorkLensConfig(
    string ConfigVersion,
    IReadOnlyList<Identity> Identities,
    IReadOnlyList<ConfigSource> Sources,
    ConfigSubject? Subject = null,
    string? Timezone = null,
    ExcludeConfig? Exclude = null,
    DigestConfig? Digest = null,
    ReportConfig? Report = null)
{
    public const string CurrentSchemaVersion = "1.0";
    public const string SchemaId = "urn:worklens:schema:config:1.0";
}

public sealed record ConfigSubject(string DisplayName);

/// <summary>One repository to collect from.</summary>
/// <param name="Id">Unique source id, used by <c>collect --source</c>.</param>
/// <param name="BaseUrl">REST API root over https, as written in the file (not normalized).</param>
/// <param name="TokenEnv">Environment variable that holds the token; when unset, the platform default applies.</param>
public sealed record ConfigSource(
    string Id,
    SourcePlatform Platform,
    string BaseUrl,
    string Repository,
    string? TokenEnv = null);

/// <summary>What the digest leaves out (M2); collect only flags items.</summary>
public sealed record ExcludeConfig(bool? Bots = null, bool? MergeCommits = null, IReadOnlyList<string>? Paths = null);

public sealed record DigestConfig(
    bool? IncludeDiff = null,
    int? MaxBodyChars = null,
    int? PathDepth = null,
    RedactionConfig? Redaction = null);

public sealed record RedactionConfig(string? Mode = null, IReadOnlyList<RedactionPattern>? Patterns = null);

public sealed record RedactionPattern(string Match, string Replace);

public sealed record ReportConfig(string? TemplateManifest = null);
