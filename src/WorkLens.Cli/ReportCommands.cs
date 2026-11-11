using System.CommandLine;
using System.Text.Json.Nodes;
using WorkLens.Core.Errors;
using WorkLens.Core.Evidence;
using WorkLens.Core.Validation;

namespace WorkLens.Cli;

internal static class ReportCommands
{
    /// <summary><c>report validate</c>: schema, reference and rule validation of report.json against evidence.json.</summary>
    public static Command Validate()
    {
        var command = new Command("validate", "Validate report.json against schema and evidence.");
        var workspace = Program.WorkspaceOption();
        var report = Program.FileOption("--report", InputFiles.ReportFileName);
        var evidence = Program.FileOption("--evidence", InputFiles.EvidenceFileName);
        var strict = Program.StrictOption();
        var json = Program.JsonOption();
        command.Options.Add(workspace);
        command.Options.Add(report);
        command.Options.Add(evidence);
        command.Options.Add(strict);
        command.Options.Add(json);
        command.SetAction(parse => CliOutput.Run("report validate", parse.GetValue(json), output =>
        {
            var ws = parse.GetValue(workspace);
            var reportPath = InputFiles.Resolve(parse.GetValue(report), ws, InputFiles.ReportFileName);
            var evidencePath = InputFiles.Resolve(parse.GetValue(evidence), ws, InputFiles.EvidenceFileName);

            var checkedReport = ValidateReport(reportPath, evidencePath, parse.GetValue(strict), output);
            output.Lines.Insert(0, Summary(reportPath, checkedReport.Issues));
            return checkedReport.ExitCode;
        }));
        return command;
    }

    /// <summary>
    /// Shared by <c>report validate</c> and <c>present</c>: reads both files (both must exist), loads the evidence
    /// (exit 3 when invalid) and validates the report. Sets <c>result</c> and the issues on <paramref name="output"/>.
    /// </summary>
    public static CheckedReport ValidateReport(string reportPath, string evidencePath, bool strict, CommandResultBuilder output)
    {
        var reportFile = InputFiles.Read(reportPath);
        var evidenceFile = InputFiles.Read(evidencePath);
        var evidenceDocument = InputFiles.LoadUpstream<EvidenceDocument>(evidenceFile, EvidenceDocument.SchemaId);

        var reportText = reportFile.TryGetText();
        var result = reportText is null
            ? new ValidationResult([InputFiles.Issue("json.invalid", string.Empty, "report.json is not valid UTF-8.")])
            : new ReportValidator().Validate(reportText, evidenceDocument, evidenceFile.Sha256);

        output.Issues.AddRange(result.Issues);
        var errors = result.Issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = result.Issues.Count - errors;
        output.Result = new JsonObject
        {
            ["files"] = new JsonObject { ["report"] = reportPath, ["evidence"] = evidencePath },
            ["error_count"] = errors,
            ["warning_count"] = warnings,
        };

        var failed = errors > 0 || (strict && warnings > 0);
        return new CheckedReport(
            failed ? ExitCodes.ValidationFailed : ExitCodes.Success,
            result.Issues,
            reportFile,
            evidenceFile,
            failed ? null : evidenceDocument);
    }

    private static string Summary(string reportPath, IReadOnlyList<ValidationIssue> issues)
    {
        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        return $"{reportPath}: {errors} error(s), {issues.Count - errors} warning(s)";
    }
}

/// <param name="Evidence">The loaded evidence when the report passed validation; otherwise <c>null</c>.</param>
internal sealed record CheckedReport(
    int ExitCode,
    IReadOnlyList<ValidationIssue> Issues,
    InputFile Report,
    InputFile EvidenceFile,
    EvidenceDocument? Evidence);
