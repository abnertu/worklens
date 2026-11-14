using System.Security.Cryptography;
using System.Text.Json;
using WorkLens.Core.Evidence;
using WorkLens.Core.Json;
using WorkLens.Core.Layout;
using WorkLens.Core.Report;
using WorkLens.Core.Schemas;
using WorkLens.Core.Templates;
using WorkLens.Core.Validation;
using Xunit;

namespace WorkLens.Core.Tests;

/// <summary>The rd-monthly manifest is a valid 1.1 manifest that Layout accepts.</summary>
public sealed class RdMonthlyManifestTests
{
    private static string Directory => Path.Combine(SchemaExamples.RepositoryRoot(), "templates", "rd-monthly");

    private static string ManifestPath => Path.Combine(Directory, "manifest.json");

    private static TemplateManifest Manifest => JsonSerializer.Deserialize<TemplateManifest>(File.ReadAllBytes(ManifestPath), WorkLensJson.Options)!;

    [Fact]
    public void Manifest_passes_the_schema_and_the_contract_checks()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(ManifestPath));

        Assert.Empty(new IrSchemaValidator().ValidateDocument(IrSchemaCatalog.TemplateManifest, document.RootElement).Issues);
        Assert.Empty(TemplateManifestChecker.CheckIds(Manifest));
        Assert.Empty(TemplateManifestChecker.CheckContracts(Manifest));
    }

    // render refuses a template whose bytes differ from template.sha256.
    [Fact]
    public void Manifest_sha256_matches_the_template_file() =>
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Directory, Manifest.Template.File)))),
            Manifest.Template.Sha256);

    [Theory]
    [InlineData("full")]
    [InlineData("empty")]
    public void Layout_resolves_the_fixtures_with_the_manifest(string name)
    {
        string Fixture(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "rd-monthly", name, file);
        T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), WorkLensJson.Options)!;

        var result = new LayoutResolver().Resolve(
            Load<ReportDocument>(Fixture("report.json")), new string('0', 64), Load<EvidenceDocument>(Fixture("evidence.json")), Manifest);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    // Layout leaves a slot id outside its conventions unbound without an error, so a misspelled slot would never be
    // filled. The full fixture has content for every slot of every layout in the sequence.
    [Fact]
    public void Full_fixture_binds_every_slot_of_the_sequence_layouts()
    {
        string Fixture(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "rd-monthly", "full", file);
        T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), WorkLensJson.Options)!;
        var manifest = Manifest;

        var slides = new LayoutResolver().Resolve(
            Load<ReportDocument>(Fixture("report.json")), new string('0', 64), Load<EvidenceDocument>(Fixture("evidence.json")), manifest)
            .Presentation!.Slides;

        foreach (var layoutId in manifest.Sequence.Select(e => e.LayoutId).Distinct())
        {
            var bound = slides.Where(s => s.LayoutId == layoutId).SelectMany(s => s.Bindings.Keys).ToHashSet();
            var slots = manifest.Layouts.Single(l => l.Id == layoutId).Slots.Select(s => s.Id);
            Assert.True(slots.All(bound.Contains), $"Layout {layoutId} leaves unbound: {string.Join(", ", slots.Where(s => !bound.Contains(s)))}");
        }
    }
}
