using System.Security.Cryptography;
using System.Text.Json;
using WorkLens.Core.Json;
using WorkLens.Core.Templates;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class TemplateManifestExampleTests
{
    private static string TemplateDirectory => Path.Combine(SchemaExamples.RepositoryRoot(), "templates", "sample-monthly");

    // The schema example is a byte copy of the sample template's manifest, so the two cannot drift.
    [Fact]
    public void Schema_example_is_a_byte_copy_of_the_sample_template_manifest()
    {
        var root = SchemaExamples.RepositoryRoot();
        var manifest = File.ReadAllBytes(Path.Combine(TemplateDirectory, "manifest.json"));
        var example = File.ReadAllBytes(Path.Combine(
            root, "schemas", "template-manifest", "1.0", "examples", "valid", "sample-monthly.json"));

        Assert.Equal(manifest, example);
    }

    // template.file is relative to the manifest directory and template.sha256 is the hash of its bytes.
    [Fact]
    public void Sample_manifest_sha256_matches_template_file()
    {
        var manifest = JsonSerializer.Deserialize<TemplateManifest>(
            File.ReadAllBytes(Path.Combine(TemplateDirectory, "manifest.json")), WorkLensJson.Options)!;
        var template = File.ReadAllBytes(Path.Combine(TemplateDirectory, manifest.Template.File));

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(template)), manifest.Template.Sha256);
    }
}
