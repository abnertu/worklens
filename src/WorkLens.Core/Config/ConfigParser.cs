using System.Text.Json;
using System.Text.Json.Nodes;
using WorkLens.Core.Errors;
using WorkLens.Core.Evidence;
using WorkLens.Core.Json;
using WorkLens.Core.Schemas;
using WorkLens.Core.Security;
using WorkLens.Core.Validation;

namespace WorkLens.Core.Config;

/// <summary>
/// Result of <see cref="ConfigParser.Parse"/>: either a configuration, or an error code
/// (<c>config.secret_in_file</c> or <c>config.invalid</c>) with the issues that explain it.
/// </summary>
public sealed record ConfigParseResult(WorkLensConfig? Config, string? ErrorCode, IReadOnlyList<ValidationIssue> Issues)
{
    public const string SecretInFile = "config.secret_in_file";
    public const string Invalid = "config.invalid";

    public bool IsValid => Config is not null;
}

/// <summary>
/// Parses <c>worklens.json</c> in layers; a layer runs only when the previous one found no error:
/// JSON syntax, secrets (<c>config.secret_in_file</c>), schema, deserialization, then semantic rules.
/// Issue messages name JSON Pointers only and never repeat a secret value.
/// </summary>
public static class ConfigParser
{
    private static readonly HashSet<string> SecretPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "access_token", "private_token", "password", "secret", "api_key",
    };

    private static readonly IrSchemaValidator SchemaValidator = new();

    public static ConfigParseResult Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                AllowDuplicateProperties = false,
            });
        }
        catch (JsonException ex)
        {
            return Invalid(Issue("json.invalid", string.Empty, $"Configuration is not valid JSON: {ex.Message}"));
        }

        var secrets = new List<ValidationIssue>();
        FindSecrets(root, string.Empty, secrets);
        if (secrets.Count > 0)
        {
            return new ConfigParseResult(null, ConfigParseResult.SecretInFile, secrets);
        }

        if (root is not JsonObject)
        {
            return Invalid(Issue("schema.type", string.Empty, "Configuration must be a JSON object."));
        }

        ValidationResult schema;
        using (var document = JsonDocument.Parse(json))
        {
            schema = SchemaValidator.ValidateDocument(IrSchemaCatalog.Config, document.RootElement);
        }

        if (!schema.IsValid)
        {
            return new ConfigParseResult(null, ConfigParseResult.Invalid, schema.Issues);
        }

        WorkLensConfig config;
        try
        {
            config = JsonSerializer.Deserialize<WorkLensConfig>(json, WorkLensJson.Options)
                ?? throw new JsonException("Configuration deserialized to null.");
        }
        catch (JsonException ex)
        {
            return Invalid(Issue("json.deserialize_failed", JsonPointer.FromJsonPath(ex.Path), $"Configuration cannot be read: {ex.Message}"));
        }

        var semantic = CheckRules(config);
        return semantic.Count > 0
            ? new ConfigParseResult(null, ConfigParseResult.Invalid, semantic)
            : new ConfigParseResult(config, null, []);
    }

    /// <summary>
    /// True when <paramref name="id"/> names a time zone on this host by its IANA id
    /// (Windows ids such as <c>Taipei Standard Time</c> are rejected).
    /// </summary>
    public static bool IsIanaTimeZone(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) && zone.HasIanaId;

    private static ConfigParseResult Invalid(ValidationIssue issue) => new(null, ConfigParseResult.Invalid, [issue]);

    private static ValidationIssue Issue(string code, string path, string message) =>
        new(code, IssueSeverity.Error, path, null, message);

    // Secret-looking property names are rejected at any depth. String values are rejected when the whole value is
    // a full-length token, except the custom redaction patterns (whose match/replace may legitimately describe tokens).
    private static void FindSecrets(JsonNode? node, string path, List<ValidationIssue> issues)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj)
                {
                    var childPath = path + "/" + JsonPointer.Escape(name);
                    if (SecretPropertyNames.Contains(name))
                    {
                        issues.Add(Issue(ConfigParseResult.SecretInFile, childPath,
                            $"Configuration must not contain secrets: remove the property at {childPath} and name an environment variable in token_env instead."));
                        continue;
                    }

                    FindSecrets(value, childPath, issues);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    FindSecrets(array[i], path + "/" + i, issues);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                if (!IsRedactionPattern(path) && TokenPatterns.IsToken(value.GetValue<string>()))
                {
                    issues.Add(Issue(ConfigParseResult.SecretInFile, path,
                        $"Configuration must not contain secrets: the value at {path} is an access token. Put the token in an environment variable and name it in token_env."));
                }

                break;
        }
    }

    private static bool IsRedactionPattern(string path)
    {
        var segments = path.Split('/');
        // "", "digest", "redaction", "patterns", "<n>", "match" | "replace"
        return segments.Length == 6
            && segments[1] == "digest" && segments[2] == "redaction" && segments[3] == "patterns"
            && segments[5] is "match" or "replace";
    }

    private static List<ValidationIssue> CheckRules(WorkLensConfig config)
    {
        var issues = new List<ValidationIssue>();

        if (config.Timezone is { } timezone && !IsIanaTimeZone(timezone))
        {
            issues.Add(Issue(ConfigParseResult.Invalid, "/timezone",
                $"Time zone '{timezone}' is not an IANA time zone name known on this host (for example Asia/Taipei)."));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var repositories = new HashSet<(string, string, string)>();
        for (var i = 0; i < config.Sources.Count; i++)
        {
            var source = config.Sources[i];
            var path = $"/sources/{i}";
            if (!ids.Add(source.Id))
            {
                issues.Add(Issue(ConfigParseResult.Invalid, path + "/id", $"Source id '{source.Id}' is used more than once."));
            }

            var baseUrlProblem = CheckBaseUrl(source.BaseUrl);
            if (baseUrlProblem is not null)
            {
                issues.Add(Issue(ConfigParseResult.Invalid, path + "/base_url", baseUrlProblem));
                continue;
            }

            var key = (source.Platform.ToString(), EvidenceIds.NormalizeBaseUrl(source.BaseUrl), source.Repository.ToLowerInvariant());
            if (!repositories.Add(key))
            {
                issues.Add(Issue(ConfigParseResult.Invalid, path + "/repository",
                    $"Source '{source.Id}' repeats a repository that another source already collects."));
            }
        }

        return issues;
    }

    // Messages never echo the URL: a URL with user info could carry a credential.
    private static string? CheckBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return "base_url must be an absolute URL.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return "base_url must use https so that the token is not sent in clear text.";
        }

        if (uri.UserInfo.Length > 0 || baseUrl.Contains('@', StringComparison.Ordinal))
        {
            return "base_url must not contain user info; put the token in an environment variable named by token_env.";
        }

        if (uri.Query.Length > 0 || baseUrl.Contains('?', StringComparison.Ordinal))
        {
            return "base_url must not contain a query string.";
        }

        if (uri.Fragment.Length > 0 || baseUrl.Contains('#', StringComparison.Ordinal))
        {
            return "base_url must not contain a fragment.";
        }

        return null;
    }
}
