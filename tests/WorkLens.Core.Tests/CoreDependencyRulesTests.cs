using System.Text.Json.Nodes;
using Xunit;

namespace WorkLens.Core.Tests;

/// <summary>
/// Enforces docs/architecture/overview.md section 4.3: WorkLens.Core depends on no other WorkLens project and on
/// no vendor or I/O package. Reads the restored <c>project.assets.json</c>, so packages added through
/// Directory.Build.props or GlobalPackageReference and transitive packages are covered too.
/// </summary>
public sealed class CoreDependencyRulesTests
{
    private static readonly string[] AllowedDirectPackages = ["JsonSchema.Net"];

    private static readonly string[] ForbiddenPackagePrefixes =
    [
        "DocumentFormat.OpenXml",
        "Octokit",
        "Microsoft.Extensions.AI",
        "OpenAI",
        "Anthropic",
        "System.CommandLine",
        "Microsoft.Extensions.Http",
    ];

    private static JsonNode LoadAssets()
    {
        var path = Path.Combine(SchemaExamples.RepositoryRoot(), "src", "WorkLens.Core", "obj", "project.assets.json");
        Assert.True(File.Exists(path), $"Restore output not found: {path}");
        return JsonNode.Parse(File.ReadAllText(path))!;
    }

    [Fact]
    public void Core_direct_package_dependencies_are_allowlisted()
    {
        var frameworks = LoadAssets()["project"]!["frameworks"]!.AsObject();
        Assert.NotEmpty(frameworks);

        foreach (var (framework, value) in frameworks)
        {
            var dependencies = value!["dependencies"]?.AsObject().Select(d => d.Key).Order().ToArray() ?? [];
            Assert.True(
                dependencies.SequenceEqual(AllowedDirectPackages.Order()),
                $"{framework}: unexpected direct dependencies [{string.Join(", ", dependencies)}]");
        }
    }

    [Fact]
    public void Core_has_no_project_references_or_forbidden_packages()
    {
        var targets = LoadAssets()["targets"]!.AsObject();
        Assert.NotEmpty(targets);

        foreach (var (target, libraries) in targets)
        {
            foreach (var (library, details) in libraries!.AsObject())
            {
                var name = library[..library.IndexOf('/')];
                var type = details!["type"]!.GetValue<string>();

                Assert.True(type != "project", $"{target}: Core must not reference project '{name}'.");
                Assert.DoesNotContain(
                    ForbiddenPackagePrefixes,
                    prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
