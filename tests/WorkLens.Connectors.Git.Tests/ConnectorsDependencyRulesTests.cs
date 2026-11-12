using System.Text.Json.Nodes;
using Xunit;

namespace WorkLens.Connectors.Git.Tests;

/// <summary>
/// Enforces docs/architecture/overview.md section 4.3 rule 6: WorkLens.Connectors.Git depends on Core and the BCL
/// only. Reads the restored <c>project.assets.json</c>, so transitive packages are covered too.
/// </summary>
public sealed class ConnectorsDependencyRulesTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "WorkLens.Rendering.Pptx",
        "WorkLens.Cli",
        "DocumentFormat.OpenXml",
        "System.CommandLine",
    ];

    private static JsonNode LoadAssets()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "WorkLens.Connectors.Git", "obj", "project.assets.json");
        Assert.True(File.Exists(path), $"Restore output not found: {path}");
        return JsonNode.Parse(File.ReadAllText(path))!;
    }

    [Fact]
    public void Connectors_have_no_direct_package_dependencies()
    {
        var frameworks = LoadAssets()["project"]!["frameworks"]!.AsObject();
        Assert.NotEmpty(frameworks);

        foreach (var (framework, value) in frameworks)
        {
            var dependencies = value!["dependencies"]?.AsObject().Select(d => d.Key).ToArray() ?? [];
            Assert.True(dependencies.Length == 0, $"{framework}: unexpected direct dependencies [{string.Join(", ", dependencies)}]");
        }
    }

    [Fact]
    public void Connectors_reference_only_Core_among_WorkLens_projects()
    {
        var references = LoadAssets()["project"]!["restore"]!["frameworks"]!.AsObject()
            .SelectMany(f => f.Value!["projectReferences"]?.AsObject().Select(r => Path.GetFileName(r.Key)) ?? [])
            .Distinct()
            .ToArray();

        Assert.Equal(["WorkLens.Core.csproj"], references);
    }

    [Fact]
    public void Connectors_have_no_forbidden_libraries()
    {
        var targets = LoadAssets()["targets"]!.AsObject();
        Assert.NotEmpty(targets);

        foreach (var (target, libraries) in targets)
        {
            foreach (var (library, _) in libraries!.AsObject())
            {
                var name = library[..library.IndexOf('/')];
                Assert.False(
                    ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)),
                    $"{target}: Connectors.Git must not depend on '{name}'.");
            }
        }
    }

    /// <summary>Repository root, found by walking up to the directory containing WorkLens.slnx.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkLens.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("WorkLens.slnx not found above test output.");
    }
}
