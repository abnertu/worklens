using System.Text.Json.Nodes;
using WorkLens.Core.Common;
using WorkLens.Core.Config;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class ConfigParserTests
{
    private static readonly string ClassicToken = "ghp_" + new string('a', 36);
    private static readonly string FineGrainedToken = "github_pat_" + new string('B', 22) + "_" + new string('c', 59);
    private static readonly string GitLabToken = "glpat-" + new string('x', 20);

    private static JsonObject Minimal() => new()
    {
        ["config_version"] = "1.0",
        ["identities"] = new JsonArray(new JsonObject { ["platform"] = "github", ["username"] = "octocat" }),
        ["sources"] = new JsonArray(Source("gh", "https://api.github.com", "octo/hello")),
    };

    private static JsonObject Source(string id, string baseUrl, string repository) => new()
    {
        ["id"] = id,
        ["platform"] = "github",
        ["base_url"] = baseUrl,
        ["repository"] = repository,
    };

    private static ConfigParseResult Parse(JsonNode node) => ConfigParser.Parse(node.ToJsonString());

    [Fact]
    public void Full_example_parses()
    {
        var json = File.ReadAllText(SchemaExamples.ExamplePath("config", "valid", "full.json"));

        var result = ConfigParser.Parse(json);

        Assert.True(result.IsValid, string.Join(", ", result.Issues.Select(i => $"{i.Code}@{i.InstancePath}")));
        Assert.Null(result.ErrorCode);
        Assert.Equal(["gh-auth", "gl-billing"], result.Config!.Sources.Select(s => s.Id));
        Assert.Equal(SourcePlatform.Gitlab, result.Config.Sources[1].Platform);
        Assert.Equal("WORKLENS_GITLAB_TOKEN_CORP", result.Config.Sources[1].TokenEnv);
        Assert.Equal("Asia/Taipei", result.Config.Timezone);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("Token")]
    [InlineData("access_token")]
    [InlineData("private_token")]
    [InlineData("password")]
    [InlineData("secret")]
    [InlineData("API_KEY")]
    public void Secret_property_name_is_rejected(string name)
    {
        var config = Minimal();
        ((JsonObject)config["sources"]![0]!)[name] = "hunter2-value";

        var result = Parse(config);

        Assert.Equal(ConfigParseResult.SecretInFile, result.ErrorCode);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("config.secret_in_file", issue.Code);
        Assert.Equal("/sources/0/" + name, issue.InstancePath);
        Assert.DoesNotContain("hunter2-value", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_property_is_found_at_any_depth_before_schema_validation()
    {
        // "extra" is not in the schema, so a schema-first parser would report additionalProperties instead.
        var config = Minimal();
        config["extra"] = new JsonObject { ["nested"] = new JsonArray(new JsonObject { ["password"] = "p" }) };

        var result = Parse(config);

        Assert.Equal(ConfigParseResult.SecretInFile, result.ErrorCode);
        Assert.Equal("/extra/nested/0/password", Assert.Single(result.Issues).InstancePath);
    }

    public static TheoryData<string> Tokens() => new() { ClassicToken, FineGrainedToken, GitLabToken };

    [Theory]
    [MemberData(nameof(Tokens))]
    public void Full_length_token_value_is_rejected(string token)
    {
        var config = Minimal();
        ((JsonObject)config["sources"]![0]!)["repository"] = token;

        var result = Parse(config);

        Assert.Equal(ConfigParseResult.SecretInFile, result.ErrorCode);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("/sources/0/repository", issue.InstancePath);
        Assert.DoesNotContain(token, issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Token_value_inside_longer_text_or_too_short_is_not_rejected()
    {
        var config = Minimal();
        config["subject"] = new JsonObject { ["display_name"] = "ghp_short" };
        ((JsonObject)config["sources"]![0]!)["repository"] = "octo/" + ClassicToken;

        Assert.True(Parse(config).IsValid);
    }

    [Fact]
    public void Token_env_does_not_trigger_the_secret_check()
    {
        var config = Minimal();
        ((JsonObject)config["sources"]![0]!)["token_env"] = "WORKLENS_GITHUB_TOKEN";

        var result = Parse(config);

        Assert.True(result.IsValid);
        Assert.Equal("WORKLENS_GITHUB_TOKEN", result.Config!.Sources[0].TokenEnv);
    }

    [Theory]
    [InlineData("ghp_")]
    [InlineData("ghp_[A-Za-z0-9]{36}")]
    [InlineData(null)]
    public void Redaction_patterns_may_describe_tokens(string? match)
    {
        var config = Minimal();
        config["digest"] = new JsonObject
        {
            ["redaction"] = new JsonObject
            {
                ["patterns"] = new JsonArray(new JsonObject { ["match"] = match ?? ClassicToken, ["replace"] = ClassicToken }),
            },
        };

        Assert.True(Parse(config).IsValid);
    }

    [Fact]
    public void Invalid_json_is_config_invalid()
    {
        var result = ConfigParser.Parse("{ \"config_version\": ");

        Assert.Equal(ConfigParseResult.Invalid, result.ErrorCode);
        Assert.Equal("json.invalid", Assert.Single(result.Issues).Code);
    }

    [Theory]
    [InlineData("{\"config_version\":\"1.0\",\"config_version\":\"1.0\"}")]
    [InlineData("{\"identities\":[{\"username\":\"alice\",\"username\":\"bob\"}]}")]
    public void Duplicate_properties_are_config_invalid(string json)
    {
        var result = ConfigParser.Parse(json);

        Assert.Equal(ConfigParseResult.Invalid, result.ErrorCode);
        Assert.Equal("json.invalid", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Non_object_root_is_config_invalid()
    {
        var result = ConfigParser.Parse("[]");

        Assert.Equal(ConfigParseResult.Invalid, result.ErrorCode);
        Assert.Equal("schema.type", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Unknown_config_version_is_unsupported()
    {
        var config = Minimal();
        config["config_version"] = "1.9";

        var result = Parse(config);

        Assert.Equal(ConfigParseResult.Invalid, result.ErrorCode);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("schema.unsupported_version", issue.Code);
        Assert.Equal("/config_version", issue.InstancePath);
    }

    [Fact]
    public void Schema_violation_is_config_invalid_with_schema_issues()
    {
        var config = Minimal();
        config["identities"] = new JsonArray();

        var result = Parse(config);

        Assert.Equal(ConfigParseResult.Invalid, result.ErrorCode);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("schema.minItems", issue.Code);
        Assert.Equal("/identities", issue.InstancePath);
    }

    [Fact]
    public void Value_out_of_int_range_is_a_deserialize_failure()
    {
        var config = Minimal();
        config["digest"] = new JsonObject { ["max_body_chars"] = 99999999999 };

        var result = Parse(config);

        Assert.Equal(ConfigParseResult.Invalid, result.ErrorCode);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("json.deserialize_failed", issue.Code);
        Assert.Equal("/digest/max_body_chars", issue.InstancePath);
    }

    [Theory]
    [InlineData("Asia/Taipei", true)]
    [InlineData("America/New_York", true)]
    [InlineData("UTC", true)]
    [InlineData("Taipei Standard Time", false)]
    [InlineData("Mars/Olympus_Mons", false)]
    public void Timezone_must_be_an_iana_name(string timezone, bool valid)
    {
        var config = Minimal();
        config["timezone"] = timezone;

        var result = Parse(config);

        Assert.Equal(valid, result.IsValid);
        if (!valid)
        {
            Assert.Equal(ConfigParseResult.Invalid, result.ErrorCode);
            var issue = Assert.Single(result.Issues);
            Assert.Equal("config.invalid", issue.Code);
            Assert.Equal("/timezone", issue.InstancePath);
        }
    }

    [Fact]
    public void Duplicate_source_id_is_rejected()
    {
        var config = Minimal();
        ((JsonArray)config["sources"]!).Add(Source("gh", "https://api.github.com", "octo/other"));

        var issue = Assert.Single(Parse(config).Issues);

        Assert.Equal("config.invalid", issue.Code);
        Assert.Equal("/sources/1/id", issue.InstancePath);
    }

    [Fact]
    public void Same_repository_with_another_spelling_of_the_base_url_is_rejected()
    {
        var config = Minimal();
        ((JsonArray)config["sources"]!).Add(Source("gh2", "https://API.github.com:443/", "Octo/Hello"));

        var issue = Assert.Single(Parse(config).Issues);

        Assert.Equal("/sources/1/repository", issue.InstancePath);
    }

    [Fact]
    public void Same_repository_on_another_host_is_allowed()
    {
        var config = Minimal();
        ((JsonArray)config["sources"]!).Add(Source("ghe", "https://ghe.example/api/v3", "octo/hello"));

        Assert.True(Parse(config).IsValid);
    }

    [Theory]
    [InlineData("http://api.github.com")]
    [InlineData("https://user:pass@api.github.com")]
    [InlineData("https://api.github.com?access=1")]
    [InlineData("https://api.github.com/#frag")]
    public void Base_url_must_be_plain_https(string baseUrl)
    {
        var config = Minimal();
        ((JsonObject)config["sources"]![0]!)["base_url"] = baseUrl;

        var result = Parse(config);

        Assert.Equal(ConfigParseResult.Invalid, result.ErrorCode);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("config.invalid", issue.Code);
        Assert.Equal("/sources/0/base_url", issue.InstancePath);
        Assert.DoesNotContain("pass", issue.Message, StringComparison.Ordinal);
    }
}
