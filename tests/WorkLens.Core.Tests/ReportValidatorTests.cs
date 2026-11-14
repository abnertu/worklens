using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkLens.Core.Errors;
using WorkLens.Core.Evidence;
using WorkLens.Core.Json;
using WorkLens.Core.Validation;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class ReportValidatorTests
{
    private static readonly ReportValidator Validator = new();

    private static string ReportPath => SchemaExamples.ExamplePath("report-ir", "valid", "monthly-report.json");

    private static string EvidencePath => SchemaExamples.ExamplePath("evidence-ir", "valid", "monthly-github-gitlab.json");

    private static string ReportJson => File.ReadAllText(ReportPath);

    private static string EvidenceSha256 => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(EvidencePath)));

    private static JsonObject EvidenceNode() => JsonNode.Parse(File.ReadAllText(EvidencePath))!.AsObject();

    private static EvidenceDocument ToEvidence(JsonNode node) =>
        node.Deserialize<EvidenceDocument>(WorkLensJson.Options)!;

    /// <summary>
    /// One fixture per case: (report JSON, evidence, evidence SHA-256), each made by editing the valid examples.
    /// </summary>
    private static (string Report, EvidenceDocument Evidence, string Sha256) Fixture(string name)
    {
        var report = JsonNode.Parse(ReportJson)!.AsObject();
        var evidence = EvidenceNode();
        var sha = EvidenceSha256;
        var items = report["work_items"]!.AsArray();

        switch (name)
        {
            case "truncated":
                return (ReportJson[..(ReportJson.Length / 2)], ToEvidence(evidence), sha);
            case "root_array":
                return ("[]", ToEvidence(evidence), sha);
            case "rank_overflows_int":
                items[0]!["rank"] = 99999999999;
                break;
            case "unsupported_version":
                report["schema_version"] = "1.9";
                break;
            case "missing_title":
                items[0]!.AsObject().Remove("title");
                break;
            case "unknown_field":
                items[0]!["priority"] = "high";
                break;
            case "evidence_document_id_mismatch":
                report["evidence_document"]!["document_id"] = "evd-2026-10-other";
                break;
            case "evidence_sha256_mismatch":
                sha = new string('0', 64);
                break;
            case "work_item_ref_missing":
                items[1]!["evidence_refs"]![0] = "ev-ffffffffffffffff";
                break;
            case "fact_ref_missing":
                items[0]!["observed_facts"]![0]!["evidence_refs"]![0] = "ev-ffffffffffffffff";
                break;
            case "narrative_ref_missing":
                report["narratives"]![0]!["evidence_refs"]![0] = "ev-ffffffffffffffff";
                break;
            case "duplicate_work_item_id":
                items[1]!["id"] = items[0]!["id"]!.GetValue<string>();
                break;
            case "completed_without_merge":
                items[1]!["status"] = "completed";
                break;
            case "evidence_outside_period":
                evidence["items"]![2]!["timestamps"]!["occurred_at"] = "2026-11-05T09:05:00+00:00";
                break;
            case "numeric_claim_without_facts":
                items[2]!["summary"] = "新進同仁上手時間縮短 30%。";
                break;
            default:
                throw new ArgumentException(name);
        }

        return (report.ToJsonString(WorkLensJson.Options), ToEvidence(evidence), sha);
    }

    [Fact]
    public void Lone_surrogate_escape_is_invalid_json()
    {
        // JsonDocument accepts the escape, but JSON Schema's minLength check reads the string and would throw.
        var json = ReplaceFirst(ReportJson, "\"title\": \"", "\"title\": \"\\ud800");

        var issue = Assert.Single(Validator.Validate(json, ToEvidence(EvidenceNode()), EvidenceSha256).Issues);

        Assert.Equal(("json.invalid", IssueSeverity.Error, ""), (issue.Code, issue.Severity, issue.InstancePath));
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0);
        return string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }

    // Each row is a distinct (code, instance_path); every code of domain-model section 3.2 and of the validator's
    // input checks appears at least once.
    [Theory]
    [InlineData("truncated", "json.invalid", IssueSeverity.Error, "")]
    [InlineData("rank_overflows_int", "json.deserialize_failed", IssueSeverity.Error, "/work_items/0/rank")]
    [InlineData("root_array", "schema.type", IssueSeverity.Error, "")]
    [InlineData("unsupported_version", "schema.unsupported_version", IssueSeverity.Error, "/schema_version")]
    [InlineData("missing_title", "schema.required", IssueSeverity.Error, "/work_items/0")]
    [InlineData("unknown_field", "schema.additionalProperties", IssueSeverity.Error, "/work_items/0/priority")]
    [InlineData("evidence_document_id_mismatch", "ref.evidence_document_mismatch", IssueSeverity.Error, "/evidence_document/document_id")]
    [InlineData("evidence_sha256_mismatch", "ref.evidence_document_mismatch", IssueSeverity.Error, "/evidence_document/sha256")]
    [InlineData("work_item_ref_missing", "ref.evidence_missing", IssueSeverity.Error, "/work_items/1/evidence_refs/0")]
    [InlineData("fact_ref_missing", "ref.evidence_missing", IssueSeverity.Error, "/work_items/0/observed_facts/0/evidence_refs/0")]
    [InlineData("narrative_ref_missing", "ref.evidence_missing", IssueSeverity.Error, "/narratives/0/evidence_refs/0")]
    [InlineData("duplicate_work_item_id", "ref.duplicate_id", IssueSeverity.Error, "/work_items/1/id")]
    [InlineData("completed_without_merge", "rule.completed_without_merged_change", IssueSeverity.Warning, "/work_items/1/status")]
    [InlineData("evidence_outside_period", "rule.evidence_outside_period", IssueSeverity.Warning, "/work_items/1/observed_facts/0/evidence_refs/0")]
    [InlineData("numeric_claim_without_facts", "rule.unreferenced_numeric_claim", IssueSeverity.Warning, "/work_items/2/summary")]
    public void IssueCodes(string fixture, string code, IssueSeverity severity, string instancePath)
    {
        var (report, evidence, sha) = Fixture(fixture);

        var result = Validator.Validate(report, evidence, sha);

        Assert.Contains(result.Issues, i => i.Code == code && i.Severity == severity && i.InstancePath == instancePath);
        if (severity == IssueSeverity.Warning)
        {
            Assert.True(result.IsValid, "Warnings alone keep the report valid.");
        }
    }

    private static string Report11Json =>
        File.ReadAllText(SchemaExamples.ExamplePath("report-ir", "1.1", "valid", "monthly-report.json"));

    [Fact]
    public void Report_1_1_valid_example_has_no_issues_with_the_1_0_evidence_example()
    {
        var result = Validator.Validate(Report11Json, ToEvidence(EvidenceNode()), EvidenceSha256);

        Assert.Empty(result.Issues);
    }

    // The 1.1 example: narratives 0 challenge, 1 improvement (resolves the challenge), 2 next_plan, 3 support.
    [Theory]
    [InlineData("nr-missing", "ref.resolves_missing")]
    [InlineData("nr-next-plan-sso", "ref.resolves_not_challenge")]
    [InlineData("nr-improvement-dual-token", "ref.resolves_not_challenge")]
    public void Resolves_must_name_a_challenge_narrative(string target, string code)
    {
        var report = JsonNode.Parse(Report11Json)!;
        report["narratives"]![1]!["resolves"] = new JsonArray("nr-challenge-token-migration", target);

        var result = Validator.Validate(report.ToJsonString(), ToEvidence(EvidenceNode()), EvidenceSha256);

        var issue = Assert.Single(result.Issues);
        Assert.Equal((code, IssueSeverity.Error, "/narratives/1/resolves/1"), (issue.Code, issue.Severity, issue.InstancePath));
    }

    [Fact]
    public void Valid_examples_have_no_issues()
    {
        var evidence = ToEvidence(EvidenceNode());

        var result = Validator.Validate(ReportJson, evidence, EvidenceSha256);

        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("""{"report_id": "rpt-x"}""")]
    [InlineData("""{"schema_version": 1}""")]
    [InlineData("""{"schema_version": "2.0"}""")]
    [InlineData("""{"schema_version": "1.0.0"}""")]
    public void Missing_or_unknown_schema_version_is_unsupported(string json)
    {
        var result = Validator.Validate(json, null, null);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("schema.unsupported_version", issue.Code);
        Assert.Equal("/schema_version", issue.InstancePath);
    }

    // A schema error stops the later layers: no reference issues are reported for a schema-invalid report.
    [Fact]
    public void Schema_error_stops_reference_layer()
    {
        var (report, evidence, _) = Fixture("missing_title");

        var result = Validator.Validate(report, evidence, new string('0', 64));

        Assert.All(result.Issues, i => Assert.StartsWith("schema.", i.Code, StringComparison.Ordinal));
    }

    // Without evidence only the input and schema layers run.
    [Fact]
    public void Null_evidence_skips_reference_and_rule_layers()
    {
        var (report, _, _) = Fixture("fact_ref_missing");

        var result = Validator.Validate(report, null, null);

        Assert.Empty(result.Issues);
    }

    // An approved review accepts a completed item without a merged change request.
    [Fact]
    public void Approved_completed_item_without_merge_has_no_warning()
    {
        var report = JsonNode.Parse(ReportJson)!;
        report["work_items"]![1]!["status"] = "completed";
        report["work_items"]![1]!["review_status"] = "approved";

        var result = Validator.Validate(report.ToJsonString(), ToEvidence(EvidenceNode()), EvidenceSha256);

        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("$", "")]
    [InlineData("$.work_items[0].rank", "/work_items/0/rank")]
    [InlineData("$['a.b'][2]", "/a.b/2")]
    [InlineData("$.x/y", "/x~1y")]
    public void Json_path_converts_to_json_pointer(string path, string pointer) =>
        Assert.Equal(pointer, JsonPointer.FromJsonPath(path));
}
