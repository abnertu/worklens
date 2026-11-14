using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using WorkLens.Connectors.Git;
using WorkLens.Connectors.Git.GitHub;
using WorkLens.Connectors.Git.GitLab;
using WorkLens.Core.Common;
using WorkLens.Core.Config;
using WorkLens.Core.Contracts;
using WorkLens.Core.Errors;
using WorkLens.Core.Evidence;
using WorkLens.Core.Json;
using WorkLens.Core.Periods;
using WorkLens.Core.Security;
using WorkLens.Core.Validation;

namespace WorkLens.Cli;

/// <summary>
/// Collects one month or an inclusive month range. Configuration and periods are checked before requests.
/// A failed month writes no evidence. A stopped range keeps the evidence of completed months.
/// </summary>
internal static class CollectCommand
{
    public const string DefaultGitHubApi = "https://api.github.com";
    public static readonly string[] DefaultGitHubTokenVariables = ["WORKLENS_GITHUB_TOKEN", "GITHUB_TOKEN"];
    public const string DefaultGitLabApi = "https://gitlab.com/api/v4";
    public static readonly string[] DefaultGitLabTokenVariables = ["WORKLENS_GITLAB_TOKEN"];

    /// <summary>Every default token variable, registered with the redactor before arguments are parsed.</summary>
    public static readonly string[] DefaultTokenVariables = [.. DefaultGitHubTokenVariables, .. DefaultGitLabTokenVariables];

    /// <summary>Fine-grained PAT permissions that the GitHub connector needs (security-privacy section 2).</summary>
    public static readonly string[] GitHubScopes = ["Metadata: read", "Contents: read", "Pull requests: read"];

    /// <summary>The GitLab token scope that the GitLab connector needs (security-privacy section 2).</summary>
    public static readonly string[] GitLabScopes = ["read_api"];

    public static Command Create(CliHost host)
    {
        var command = new Command("collect", "Collect evidence from GitHub and GitLab into evidence.json.");
        var period = new Option<string?>("--period")
        {
            Description = "Report month, yyyy-MM (for example 2026-10).",
        };
        var from = new Option<string?>("--from") { Description = "First month of an inclusive range, yyyy-MM." };
        var to = new Option<string?>("--to") { Description = "Last month of an inclusive range, yyyy-MM (at most 24 months)." };
        var source = new Option<string[]>("--source")
        {
            Description = "Collect only this source id (repeatable; default: every source).",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var config = new Option<string?>("--config") { Description = "Configuration file (default: see docs)." };
        var workspace = Program.WorkspaceOption();
        var output = Program.FileOption("--output", InputFiles.EvidenceFileName);
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would be requested; send nothing, write nothing." };
        var verbose = new Option<bool>("--verbose") { Description = "Log each HTTP request to stderr (redacted)." };
        var json = Program.JsonOption();
        foreach (var option in new Option[] { period, from, to, source, config, workspace, output, dryRun, verbose, json })
        {
            command.Options.Add(option);
        }

        command.SetAction((parse, cancellationToken) => new CliOutput(host).RunAsync(
            "collect",
            parse.GetValue(json),
            (result, token) => RunAsync(
                host,
                new CollectOptions(
                    MonthLabels(parse.GetValue(period), parse.GetValue(from), parse.GetValue(to), parse.GetValue(output)),
                    parse.GetValue(from) is not null,
                    parse.GetValue(source) ?? [],
                    parse.GetValue(config),
                    InputFiles.Resolve(host, parse.GetValue(output), parse.GetValue(workspace), InputFiles.EvidenceFileName),
                    parse.GetValue(dryRun),
                    parse.GetValue(verbose)),
                result,
                token),
            cancellationToken));
        return command;
    }

    private static async Task<int> RunAsync(CliHost host, CollectOptions options, CommandResultBuilder result, CancellationToken cancellationToken)
    {
        var (configPath, config) = ConfigFiles.Load(host, options.ConfigPath);
        var sources = SelectSources(config, options.SourceIds);
        if (sources.FirstOrDefault(s => s.Platform is not (SourcePlatform.Github or SourcePlatform.Gitlab)) is { } unsupported)
        {
            throw new CliException(
                ExitCodes.Configuration,
                "config.unsupported_platform",
                $"Source '{unsupported.Id}' uses platform '{PlatformName(unsupported.Platform)}', which this version of WorkLens cannot collect from yet.",
                new JsonObject { ["source"] = unsupported.Id, ["file"] = configPath });
        }

        var (zone, zoneName) = ResolveTimeZone(host, config);
        Period[] periods;
        try
        {
            periods = [.. options.Periods.Select(label => PeriodCalculator.Calculate(label, zone, zoneName))];
        }
        catch (FormatException ex)
        {
            throw new CliException(ExitCodes.Usage, "command.usage_error", ex.Message);
        }

        if (options.DryRun)
        {
            if (!options.IsRange)
            {
                DryRun(host, options, periods[0], sources, result);
            }
            else
            {
                var months = new JsonArray();
                foreach (var period in periods)
                {
                    var month = new CommandResultBuilder();
                    DryRun(host, options with { OutputPath = MonthOutput(options.OutputPath, period.Label) }, period, sources, month);
                    var node = month.Result!.AsObject();
                    node.Remove("dry_run");
                    months.Add(node);
                    result.Lines.Add($"{period.Label}: {string.Join("; ", month.Lines)}");
                }

                result.Result = new JsonObject
                {
                    ["dry_run"] = true,
                    ["files"] = new JsonObject { ["config"] = configPath },
                    ["months"] = months,
                };
            }
            return ExitCodes.Success;
        }

        var tokens = sources.Select(s => ResolveToken(host, s)).ToList();
        if (!options.IsRange)
        {
            return await CollectMonthAsync(host, options, configPath, config, sources, tokens, periods[0], result, null, cancellationToken).ConfigureAwait(false);
        }

        return await CollectRangeAsync(host, options, configPath, config, sources, tokens, periods, result, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> MonthLabels(string? period, string? from, string? to, string? output)
    {
        if (period is not null && (from is not null || to is not null)
            || period is null && (from is null || to is null))
        {
            throw new CliException(ExitCodes.Usage, "command.usage_error", "Specify --period or both --from and --to.");
        }

        if (period is not null)
        {
            if (!PeriodCalculator.IsValidLabel(period))
            {
                throw new CliException(ExitCodes.Usage, "command.usage_error", "--period must be a representable month in the form yyyy-MM.");
            }

            return [period];
        }

        if (output is not null || !PeriodCalculator.IsValidLabel(from!) || !PeriodCalculator.IsValidLabel(to!))
        {
            throw new CliException(ExitCodes.Usage, "command.usage_error", "A range requires representable yyyy-MM months and cannot use --output.");
        }

        var first = DateOnly.ParseExact(from!, "yyyy-MM", CultureInfo.InvariantCulture);
        var last = DateOnly.ParseExact(to!, "yyyy-MM", CultureInfo.InvariantCulture);
        var count = (last.Year - first.Year) * 12 + last.Month - first.Month + 1;
        if (count is < 1 or > 24)
        {
            throw new CliException(ExitCodes.Usage, "command.usage_error", "The month range must be ascending and contain at most 24 months.");
        }

        return [.. Enumerable.Range(0, count).Select(i => first.AddMonths(i).ToString("yyyy-MM", CultureInfo.InvariantCulture))];
    }

    private static string MonthOutput(string defaultOutput, string label) =>
        Path.Combine(Path.GetDirectoryName(defaultOutput)!, label, InputFiles.EvidenceFileName);

    private static async Task<int> CollectRangeAsync(
        CliHost host, CollectOptions options, string configPath, WorkLensConfig config,
        List<ConfigSource> sources, List<string> tokens, Period[] periods,
        CommandResultBuilder result, CancellationToken cancellationToken)
    {
        var months = new JsonArray([.. periods.Select(period => (JsonNode)new JsonObject
        {
            ["period"] = PeriodNode(period),
            ["status"] = "skipped",
            ["files"] = new JsonObject { ["evidence"] = MonthOutput(options.OutputPath, period.Label) },
        })]);
        var requests = 0;
        var summary = new JsonObject
        {
            ["files"] = new JsonObject { ["config"] = configPath },
            ["months"] = months,
        };
        result.Result = summary;
        try
        {
            for (var i = 0; i < periods.Length; i++)
            {
                var period = periods[i];
                var output = MonthOutput(options.OutputPath, period.Label);
                var directory = Path.GetDirectoryName(output)!;
                var existed = Directory.Exists(directory);
                var month = new CommandResultBuilder();
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await CollectMonthAsync(host, options with { OutputPath = output }, configPath, config,
                        sources, tokens, period, month, count => requests += count, cancellationToken).ConfigureAwait(false);
                    months[i]!["status"] = "written";
                    months[i]!["document_id"] = month.Result!["document_id"]!.DeepClone();
                    months[i]!["item_count"] = month.Result["items_by_kind"]!.AsObject().Sum(pair => pair.Value!.GetValue<int>());
                }
                catch (Exception ex)
                {
                    var cancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
                    months[i]!["status"] = cancelled ? "cancelled" : "failed";
                    var written = months.Where(m => m!["status"]!.GetValue<string>() == "written")
                        .Select(m => m!["period"]!["label"]!.GetValue<string>()).ToArray();
                    var failure = ex as CliException;
                    var message = cancelled ? "The command was cancelled." : ex.Message;
                    message += $" Written months: {(written.Length == 0 ? "none" : string.Join(", ", written))}.";
                    throw new CollectStoppedException(
                        failure?.ExitCode ?? (cancelled ? ExitCodes.Cancelled : ExitCodes.Internal),
                        failure?.Code ?? (cancelled ? "command.cancelled" : "command.internal_error"),
                        message, failure?.Details, failure?.Issues);
                }
                finally
                {
                    result.Issues.AddRange(month.Issues.Select(issue => issue with { Message = $"[{period.Label}] {issue.Message}" }));
                    if (!existed)
                    {
                        TryDeleteEmptyDirectory(directory);
                    }
                }
            }
        }
        finally
        {
            summary["requests"] = requests;
            summary["warnings"] = result.Issues.Count;
            foreach (var month in months)
            {
                result.Lines.Add($"{month!["period"]!["label"]}: {month["status"]} {month["files"]!["evidence"]}.");
            }
        }

        return ExitCodes.Success;
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not replace the original failure or delete existing files.
        }
    }

    private static async Task<int> CollectMonthAsync(
        CliHost host, CollectOptions options, string configPath, WorkLensConfig config,
        List<ConfigSource> sources, List<string> tokens, Period period, CommandResultBuilder result,
        Action<int>? countRequests, CancellationToken cancellationToken)
    {
        CreateOutputDirectory(options.OutputPath);

        var items = new List<EvidenceItem>();
        var warnings = new List<WorkLensError>();
        var requests = 0;
        var log = new CliOutput(host);
        using (var inner = host.CreateHttpHandler())
        {
            for (var i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                var counter = new RequestCounter(inner, host.Redactor, options.Verbose ? log : null);
                using var http = s.Platform == SourcePlatform.Gitlab
                    ? GitLabHttp.CreateClient(counter, new Uri(s.BaseUrl), tokens[i], Program.Version)
                    : GitHubHttp.CreateClient(counter, new Uri(s.BaseUrl), tokens[i], Program.Version);
                SourceCollectResult collected;
                try
                {
                    collected = await Connector(host, s.Platform, http)
                        .CollectAsync(new SourceQuery(period, config.Identities, Spec(s)), cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    requests += counter.Count;
                    countRequests?.Invoke(counter.Count);
                }
                warnings.AddRange(collected.Warnings);
                if (!collected.IsSuccess)
                {
                    result.Issues.AddRange(warnings.Select(WarningIssue));
                    throw SourceFailure(s, collected.Errors[0]);
                }

                items.AddRange(collected.Items);
            }
        }

        var document = EvidenceAssembler.Assemble(
            new EvidenceQuery(period, config.Identities, [.. sources.Select(Spec)]),
            items,
            host.TimeProvider.GetUtcNow(),
            new GeneratorInfo("worklens", Program.Version));
        var bytes = WorkLensJson.SerializeCanonicalUtf8(document);
        var selfCheck = new IrSchemaValidator().Validate(EvidenceDocument.SchemaId, Encoding.UTF8.GetString(bytes));
        if (!selfCheck.IsValid)
        {
            // A bug, not a user error: exit 70 with the first schema problem.
            var first = selfCheck.Issues[0];
            throw new InvalidOperationException($"Collected evidence fails its schema at {first.InstancePath}: {first.Message}");
        }

        result.Issues.AddRange(warnings.Select(WarningIssue));
        var ignored = await host.GitProbe
            .CheckIgnoredAsync(Path.GetDirectoryName(options.OutputPath)!, Path.GetFileName(options.OutputPath), cancellationToken)
            .ConfigureAwait(false);
        if (ignored == GitIgnoreStatus.NotIgnored)
        {
            result.Issues.Add(new ValidationIssue(
                "output.not_ignored",
                IssueSeverity.Warning,
                string.Empty,
                null,
                $"{options.OutputPath} is inside a git work tree and not ignored; add it to .gitignore so evidence is not committed."));
        }

        // Complete cancellable work before committing the output, including probes that ignore cancellation.
        cancellationToken.ThrowIfCancellationRequested();
        AtomicFile.WriteAllBytes(options.OutputPath, bytes);

        var byKind = new JsonObject { ["commit"] = 0, ["change_request"] = 0, ["issue_activity"] = 0 };
        foreach (var group in document.Items.GroupBy(i => KindName(i.Kind)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            byKind[group.Key] = group.Count();
        }

        result.Result = new JsonObject
        {
            ["files"] = new JsonObject { ["evidence"] = options.OutputPath, ["config"] = configPath },
            ["document_id"] = document.DocumentId,
            ["period"] = PeriodNode(period),
            ["items_by_kind"] = byKind,
            ["requests"] = requests,
            ["warnings"] = result.Issues.Count,
        };
        result.Lines.Add(
            $"Wrote {options.OutputPath}: {document.Items.Count} item(s) from {sources.Count} source(s) "
            + $"for {period.Label} ({FormatUtc(period.Start)} to {FormatUtc(period.End)}), {requests} request(s).");
        return ExitCodes.Success;
    }

    private static ISourceConnector Connector(CliHost host, SourcePlatform platform, HttpClient http) => platform switch
    {
        SourcePlatform.Gitlab => new GitLabConnector(
            new GitLabApiClient(http, new RetryPolicy(host.TimeProvider, host.Delay, RetryPlatform.GitLab), host.Redactor),
            host.TimeProvider),
        _ => new GitHubConnector(
            new GitHubApiClient(http, new RetryPolicy(host.TimeProvider, host.Delay), host.Redactor),
            host.TimeProvider),
    };

    private static List<ConfigSource> SelectSources(WorkLensConfig config, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return [.. config.Sources];
        }

        var known = config.Sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.FirstOrDefault(id => !known.Contains(id)) is { } unknown)
        {
            throw new CliException(
                ExitCodes.Configuration,
                "config.unknown_source",
                $"No source '{unknown}' in the configuration. Configured sources: {string.Join(", ", known)}.",
                new JsonObject { ["source"] = unknown });
        }

        // Configuration order, so the same selection always gives the same document_id (D9).
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        return [.. config.Sources.Where(s => selected.Contains(s.Id))];
    }

    /// <summary>The configured time zone, else the host's local zone by its IANA name.</summary>
    private static (TimeZoneInfo Zone, string Name) ResolveTimeZone(CliHost host, WorkLensConfig config)
    {
        if (config.Timezone is { } configured)
        {
            return (TimeZoneInfo.FindSystemTimeZoneById(configured), configured);
        }

        var local = host.TimeProvider.LocalTimeZone;
        if (local.HasIanaId)
        {
            return (local, local.Id);
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana))
        {
            return (local, iana);
        }

        throw new CliException(
            ExitCodes.Configuration,
            ConfigParseResult.Invalid,
            $"The local time zone '{local.Id}' has no IANA name. Set \"timezone\" in {ConfigFiles.FileName}, for example \"Asia/Taipei\".");
    }

    /// <summary>
    /// The variables that can hold the source's token, in order. A configured <c>token_env</c> is the only one.
    /// Otherwise the default variables apply only to the public API of the source's platform (D14), so a
    /// default token never goes to a self-hosted instance.
    /// </summary>
    private static IReadOnlyList<string> TokenVariables(ConfigSource source)
    {
        if (source.TokenEnv is { } named)
        {
            return [named];
        }

        var (api, variables) = Defaults(source.Platform);
        return EvidenceIds.NormalizeBaseUrl(source.BaseUrl) == api ? variables : [];
    }

    private static (string Api, string[] Variables) Defaults(SourcePlatform platform) => platform == SourcePlatform.Gitlab
        ? (DefaultGitLabApi, DefaultGitLabTokenVariables)
        : (DefaultGitHubApi, DefaultGitHubTokenVariables);

    private static string? ReadVariable(CliHost host, string name) =>
        host.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string ResolveToken(CliHost host, ConfigSource source)
    {
        var variables = TokenVariables(source);
        foreach (var name in variables)
        {
            if (ReadVariable(host, name) is { } token)
            {
                host.Redactor.AddSecret(token);
                return token;
            }
        }

        var message = variables.Count == 0
            ? $"Source '{source.Id}' needs \"token_env\" in {ConfigFiles.FileName}: the default token variables are sent only to {Defaults(source.Platform).Api}."
            : $"Source '{source.Id}' requires environment variable {string.Join(" or ", variables)}.";
        throw new CliException(ExitCodes.Configuration, "config.missing_token", message, new JsonObject { ["source"] = source.Id });
    }

    private static void CreateOutputDirectory(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath)!;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliException(
                ExitCodes.Configuration,
                "output.write_failed",
                $"Cannot create output directory {directory}: {ex.Message}",
                CliException.FileDetails(outputPath));
        }
    }

    private static void DryRun(CliHost host, CollectOptions options, Period period, List<ConfigSource> sources, CommandResultBuilder result)
    {
        var nodes = new JsonArray();
        result.Lines.Add($"Dry run: {period.Label} = {FormatUtc(period.Start)} to {FormatUtc(period.End)} ({period.Timezone}); nothing is requested or written.");
        foreach (var s in sources)
        {
            var baseUrl = EvidenceIds.NormalizeBaseUrl(s.BaseUrl);
            var (endpoints, scopes) = s.Platform == SourcePlatform.Gitlab
                ? (GitLabEndpoints(baseUrl, s.Repository, period, s.IssueStatusLabels is not null), GitLabScopes)
                : (GitHubEndpoints(baseUrl, s.Repository, period), GitHubScopes);

            var variables = TokenVariables(s);
            var variable = variables.FirstOrDefault(name => ReadVariable(host, name) is not null) ?? variables.FirstOrDefault();
            var set = variable is not null && ReadVariable(host, variable) is not null;
            nodes.Add(new JsonObject
            {
                ["id"] = s.Id,
                ["platform"] = PlatformName(s.Platform),
                ["base_url"] = baseUrl,
                ["repository"] = s.Repository,
                ["endpoints"] = new JsonArray([.. endpoints.Select(e => (JsonNode)e)]),
                ["token"] = new JsonObject { ["variable"] = variable, ["set"] = set },
                ["scopes"] = new JsonArray([.. scopes.Select(e => (JsonNode)e)]),
            });

            result.Lines.Add($"{s.Id}: {baseUrl} {s.Repository}; token {variable ?? "(none: set token_env)"} {(set ? "set" : "not set")}; needs {string.Join(", ", scopes)}");
            result.Lines.AddRange(endpoints.Select(e => "  " + e));
        }

        result.Lines.Add($"Would write {options.OutputPath}.");
        result.Result = new JsonObject
        {
            ["dry_run"] = true,
            ["files"] = new JsonObject { ["evidence"] = options.OutputPath },
            ["period"] = PeriodNode(period),
            ["sources"] = nodes,
        };
    }

    private static string[] GitHubEndpoints(string baseUrl, string repository, Period period)
    {
        var repo = $"{baseUrl}/repos/{repository}";
        var until = period.End + GitHubConnector.CommitWindowSlack;
        return
        [
            $"GET {repo}",
            $"GET {repo}/commits?sha=<default branch>&since={GitHubApiClient.FormatTime(period.Start)}&until={GitHubApiClient.FormatTime(until)}&per_page=100",
            $"GET {repo}/commits/<sha>",
            $"GET {repo}/pulls?state=all&sort=updated&direction=desc&per_page=100",
            $"GET {repo}/pulls/<number>",
            $"GET {repo}/pulls/<number>/commits?per_page=100",
        ];
    }

    /// <summary>The project path is resolved once; later requests address the project by its numeric ID.</summary>
    private static string[] GitLabEndpoints(string baseUrl, string repository, Period period, bool issues)
    {
        var project = $"{baseUrl}/projects/<id>";
        var until = period.End + GitLabConnector.CommitWindowSlack;
        return
        [
            $"GET {baseUrl}/{GitLabApiClient.ProjectPath(repository)}",
            $"GET {project}/repository/commits?ref_name=<default branch>&since={GitLabApiClient.FormatTime(period.Start)}&until={GitLabApiClient.FormatTime(until)}&per_page=100",
            $"GET {project}/repository/commits/<sha>",
            $"GET {project}/repository/commits/<sha>/diff?per_page=100",
            $"GET {project}/merge_requests?state=all&scope=all&order_by=updated_at&sort=desc&updated_after={GitLabApiClient.FormatTime(period.Start)}&per_page=100",
            $"GET {project}/merge_requests/<iid>",
            $"GET {project}/merge_requests/<iid>/commits?per_page=100",
            .. issues ? new[]
            {
                $"GET {project}/issues?state=all&scope=all&order_by=updated_at&sort=desc&updated_after={GitLabApiClient.FormatTime(period.Start)}&per_page=100",
                $"GET {project}/issues/<iid>/resource_label_events?per_page=100",
                $"GET {project}/issues/<iid>/resource_state_events?per_page=100",
            } : Array.Empty<string>(),
        ];
    }

    private static CliException SourceFailure(ConfigSource source, WorkLensError error)
    {
        var details = new JsonObject { ["source"] = source.Id };
        foreach (var (key, value) in error.Details ?? new Dictionary<string, string>())
        {
            details[key] = value;
        }

        return new CliException(ExitCodes.Source, error.Code, $"Source '{source.Id}': {error.Message}", details);
    }

    private static ValidationIssue WarningIssue(WorkLensError warning) =>
        new(warning.Code, IssueSeverity.Warning, string.Empty, null, warning.Message);

    private static SourceSpec Spec(ConfigSource source) => new(source.Platform, source.BaseUrl, source.Repository, source.IssueStatusLabels);

    private static JsonObject PeriodNode(Period period) => new()
    {
        ["label"] = period.Label,
        ["start"] = FormatUtc(period.Start),
        ["end"] = FormatUtc(period.End),
        ["timezone"] = period.Timezone,
    };

    private static string FormatUtc(DateTimeOffset value) => GitHubApiClient.FormatTime(value);

    private static string PlatformName(SourcePlatform platform) => platform.ToString().ToLowerInvariant();

    private static string KindName(EvidenceKind kind) => kind switch
    {
        EvidenceKind.ChangeRequest => "change_request",
        EvidenceKind.IssueActivity => "issue_activity",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private sealed record CollectOptions(
        IReadOnlyList<string> Periods,
        bool IsRange,
        IReadOnlyList<string> SourceIds,
        string? ConfigPath,
        string OutputPath,
        bool DryRun,
        bool Verbose);
}

/// <summary>
/// Counts every HTTP attempt of one source (retries included) for <c>result.requests</c>, and with
/// <c>--verbose</c> logs one redacted line per attempt: method, URL, status and remaining rate limit (GitHub
/// <c>X-RateLimit-Remaining</c>, GitLab <c>RateLimit-Remaining</c>). Never logs a
/// body or a header other than the rate limit. It is never disposed: the inner handler is shared by every source
/// and disposed once by the command.
/// </summary>
internal sealed class RequestCounter(HttpMessageHandler inner, Redactor redactor, CliOutput? log) : DelegatingHandler(inner)
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _count);
        var target = $"{request.Method} {redactor.Redact(request.RequestUri?.AbsoluteUri)}";
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (log is not null)
            {
                var remaining = Remaining(response, "x-ratelimit-remaining") ?? Remaining(response, "ratelimit-remaining") ?? "-";
                await log.LogAsync($"http: {target} {(int)response.StatusCode} remaining={remaining}").ConfigureAwait(false);
            }

            return response;
        }
        catch (Exception ex) when (log is not null && ex is HttpRequestException or TaskCanceledException)
        {
            await log.LogAsync($"http: {target} failed ({ex.GetType().Name})").ConfigureAwait(false);
            throw;
        }
    }

    private static string? Remaining(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values) ? values.FirstOrDefault() : null;
}
