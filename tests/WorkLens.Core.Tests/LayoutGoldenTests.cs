using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkLens.Core.Errors;
using WorkLens.Core.Evidence;
using WorkLens.Core.Json;
using WorkLens.Core.Layout;
using WorkLens.Core.Presentation;
using WorkLens.Core.Report;
using WorkLens.Core.Templates;
using WorkLens.Core.Validation;
using Xunit;

namespace WorkLens.Core.Tests;

/// <summary>The 1.1 Layout output of the rd-monthly fixtures and of the 1.1 examples, byte for byte.</summary>
public sealed partial class LayoutGoldenTests
{
    private static readonly LayoutResolver Resolver = new();

    private static string FixturePath(string name, string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "rd-monthly", name, file);

    private static T Load<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), WorkLensJson.Options)!;

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static TemplateManifest Manifest =>
        Load<TemplateManifest>(SchemaExamples.ExamplePath("template-manifest", "1.1", "valid", "rd-monthly.json"));

    private static PresentationDocument Resolve(string reportPath, string evidencePath, TemplateManifest manifest)
    {
        var result = Resolver.Resolve(Load<ReportDocument>(reportPath), Sha256(reportPath), Load<EvidenceDocument>(evidencePath), manifest);
        Assert.Empty(result.Errors);
        return result.Presentation!;
    }

    private static PresentationDocument Fixture(string name, TemplateManifest? manifest = null) =>
        Resolve(FixturePath(name, "report.json"), FixturePath(name, "evidence.json"), manifest ?? Manifest);

    [Theory]
    [InlineData("full")]
    [InlineData("empty")]
    public void Fixture_reports_have_no_validation_error(string name)
    {
        var evidencePath = FixturePath(name, "evidence.json");

        var result = new ReportValidator().Validate(
            File.ReadAllText(FixturePath(name, "report.json")),
            Load<EvidenceDocument>(evidencePath),
            Sha256(evidencePath));

        Assert.DoesNotContain(result.Issues, i => i.Severity == IssueSeverity.Error);
    }

    // The golden fixes every 1.1 content rule at once; regenerate it with `worklens present` when a rule changes.
    [Theory]
    [InlineData("full")]
    [InlineData("empty")]
    public void Fixture_output_equals_golden_bytes(string name) =>
        Assert.Equal(File.ReadAllBytes(FixturePath(name, "presentation.json")), WorkLensJson.SerializeCanonicalUtf8(Fixture(name)));

    // The 11 page types of the design (docs/design/report-template.md section 7).
    [Fact]
    public void Full_golden_has_every_page_type()
    {
        var slides = Load<PresentationDocument>(FixturePath("full", "presentation.json")).Slides;

        bool Has(string layoutId, string? sourceRef = null) =>
            slides.Any(s => s.LayoutId == layoutId && (sourceRef is null || s.SourceRefs.Contains(sourceRef)));

        Assert.True(Has("cover_v1"));
        Assert.True(Has("summary_v1"));
        Assert.True(Has("achievements_v1", "wi-token-rotation"));
        Assert.True(Has("detail_v1", "wi-token-rotation"));
        Assert.True(Has("operations_v1", "wi-runner-image"));
        Assert.True(Has("detail_v1", "wi-runner-image"));
        Assert.True(Has("challenges_v1", "nr-challenge-flaky-tests"));
        Assert.True(Has("narrative_v1", "nr-improvement-review-checklist"));
        Assert.True(Has("narrative_v1", "nr-support-infra"));
        Assert.True(Has("narrative_v1", "nr-next-plan-6"));
        Assert.True(Has("narrative_v1", "nr-other-mentoring"));
    }

    // The 1.1 presentation example is the resolver output for the 1.1 report example, so it stays true to Layout.
    [Fact]
    public void Presentation_1_1_example_is_the_resolver_output()
    {
        var reportPath = SchemaExamples.ExamplePath("report-ir", "1.1", "valid", "monthly-report.json");
        var evidencePath = SchemaExamples.ExamplePath("evidence-ir", "valid", "monthly-github-gitlab.json");

        Assert.Equal(
            File.ReadAllBytes(SchemaExamples.ExamplePath("presentation-ir", "1.1", "valid", "monthly-presentation.json")),
            WorkLensJson.SerializeCanonicalUtf8(Resolve(reportPath, evidencePath, Manifest)));
    }

    /// <summary>
    /// Text labels that the 1.1 rules leave out, with the reason. The list labels are left out too: the 1.1 summary has
    /// no activity table, and the example achievements table declares its columns.
    /// </summary>
    private static readonly HashSet<string> UnusedLabels = new(StringComparer.Ordinal)
    {
        nameof(TemplateLabels.CoverTitle), // the 1.1 cover uses period_title
        nameof(TemplateLabels.StatusInProgress), // 1.1 detail slides have no status line
        nameof(TemplateLabels.StatusUnknown),
    };

    /// <summary>The substitution names of each label, as Layout fills them.</summary>
    private static readonly Dictionary<string, string[]> Names = new(StringComparer.Ordinal)
    {
        [nameof(TemplateLabels.SummaryStatus)] = ["completed", "in_progress", "unknown"],
        [nameof(TemplateLabels.SummaryActivity)] = ["repositories", "commits", "merged_changes"],
        [nameof(TemplateLabels.Inference)] = ["confidence", "statement"],
        [nameof(TemplateLabels.HumanNotes)] = ["notes"],
        [nameof(TemplateLabels.PeriodTitle)] = ["period", "year", "month"],
        [nameof(TemplateLabels.CoverSources)] = ["start", "end", "timezone", "sources"],
        [nameof(TemplateLabels.SupportMore)] = ["count"],
        [nameof(TemplateLabels.EvidenceCommits)] = ["count"],
        [nameof(TemplateLabels.Continuation)] = ["index", "total"],
        [nameof(TemplateLabels.ChallengesStatus)] = ["resolved", "open"],
        [nameof(TemplateLabels.EvidenceIssue)] = ["id"],
        [nameof(TemplateLabels.EvidenceDate)] = ["date"],
        [nameof(TemplateLabels.Footer)] = ["period"],
        [nameof(TemplateLabels.DetailKicker)] = ["section", "project"],
        [nameof(TemplateLabels.ProjectMore)] = ["project", "count", "more"],
    };

    private static readonly PropertyInfo[] TextLabels =
        [.. typeof(TemplateLabels).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(string))];

    private static string Marker(string name) => $"@@{name}@@";

    [GeneratedRegex(@"\{[a-z_]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    // Each label reaches some slide of the two fixtures, with every substitution name filled. A label that
    // LayoutLabels.From drops, or a name that Layout does not fill, fails this test.
    [Fact]
    public void Every_1_1_label_reaches_the_output()
    {
        // The list labels are both unused with this manifest, so only the text labels get a marker.
        var labels = new TemplateLabels();
        foreach (var property in TextLabels)
        {
            property.SetValue(labels, Marker(property.Name) + string.Concat(Names.GetValueOrDefault(property.Name, []).Select(n => $"{{{n}}}")));
        }

        // No capacity, except the narrative body, so that every text shows whole and next plan items continue.
        var manifest = Manifest with
        {
            Labels = labels,
            Footer = new FooterDefinition(),
            Layouts = [.. Manifest.Layouts.Select(l => l with
            {
                Slots = [.. l.Slots.Select(s => s with
                {
                    MaxChars = null,
                    MaxItems = l.Id == "narrative_v1" && s.Id == "body" ? s.MaxItems : null,
                    Columns = s.Columns?.Select(c => c with { MaxChars = null }).ToList(),
                })],
            })],
        };

        var output = string.Concat(
            System.Text.Encoding.UTF8.GetString(WorkLensJson.SerializeCanonicalUtf8(Fixture("full", manifest))),
            System.Text.Encoding.UTF8.GetString(WorkLensJson.SerializeCanonicalUtf8(Fixture("empty", manifest))));

        var missing = TextLabels.Select(p => p.Name).Where(n => !UnusedLabels.Contains(n) && !output.Contains(Marker(n), StringComparison.Ordinal)).ToList();
        var unfilled = Placeholder().Matches(output).Select(m => m.Value).Where(m => Names.Values.Any(names => names.Contains(m[1..^1]))).ToList();

        Assert.Equal([], missing);
        Assert.Equal([], unfilled);
    }
}
