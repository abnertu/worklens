using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using WorkLens.Connectors.Git;
using WorkLens.Connectors.Git.GitHub;
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
/// <c>collect</c>: reads <c>worklens.json</c>, collects the subject's evidence of one period from each selected
/// source and writes <c>evidence.json</c> (docs/design/cli-ux.md section 1). All configuration checks run before
/// the first network request; any source failure stops the command without writing a file (D16).
/// </summary>
internal static class CollectCommand
{
    public const string DefaultGitHubApi = "https://api.github.com";
    public static readonly string[] DefaultGitHubTokenVariables = ["WORKLENS_GITHUB_TOKEN", "GITHUB_TOKEN"];

    /// <summary>Fine-grained PAT permissions that the GitHub connector needs (security-privacy section 2).</summary>
    public static readonly string[] GitHubScopes = ["Metadata: read", "Contents: read", "Pull requests: read"];

    public static Command Create(CliHost host)
    {
        var command = new Command("collect", "Collect evidence from GitHub into evidence.json.");
        var period = new Option<string>("--period")
        {
            Description = "Report month, yyyy-MM (for example 2026-10).",
            Required = true,
        };
        period.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (value is null || !PeriodCalculator.IsValidLabel(value))
            {
                result.AddError($"--period must be a month in the form yyyy-MM, got '{value}'.");
            }
        });
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
        foreach (var option in new Option[] { period, source, config, workspace, output, dryRun, verbose, json })
        {
            command.Options.Add(option);
        }

        command.SetAction((parse, cancellationToken) => new CliOutput(host).RunAsync(
            "collect",
            parse.GetValue(json),
            (result, token) => RunAsync(
                host,
                new CollectOptions(
                    parse.GetValue(period)!,
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
        if (sources.FirstOrDefault(s => s.Platform != SourcePlatform.Github) is { } unsupported)
        {
            throw new CliException(
                ExitCodes.Configuration,
                "config.unsupported_platform",
                $"Source '{unsupported.Id}' uses platform '{PlatformName(unsupported.Platform)}', which this version of WorkLens cannot collect from yet.",
                new JsonObject { ["source"] = unsupported.Id, ["file"] = configPath });
        }

        var (zone, zoneName) = ResolveTimeZone(host, config);
        Period period;
        try
        {
            period = PeriodCalculator.Calculate(options.Period, zone, zoneName);
        }
        catch (FormatException ex)
        {
            throw new CliException(ExitCodes.Usage, "command.usage_error", ex.Message);
        }

        if (options.DryRun)
        {
            DryRun(host, options, period, sources, result);
            return ExitCodes.Success;
        }

        var tokens = sources.Select(s => ResolveToken(host, s)).ToList();
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
                using var http = GitHubHttp.CreateClient(counter, new Uri(s.BaseUrl), tokens[i], Program.Version);
                var api = new GitHubApiClient(http, new RetryPolicy(host.TimeProvider, host.Delay), host.Redactor);
                var collected = await new GitHubConnector(api, host.TimeProvider)
                    .CollectAsync(new SourceQuery(period, config.Identities, Spec(s)), cancellationToken)
                    .ConfigureAwait(false);
                requests += counter.Count;
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

        var byKind = new JsonObject { ["commit"] = 0, ["change_request"] = 0 };
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

    /// <summary>The variable that holds the source's token, or <c>null</c> when no default applies (D14).</summary>
    private static IReadOnlyList<string> TokenVariables(ConfigSource source) =>
        source.TokenEnv is { } named
            ? [named]
            : EvidenceIds.NormalizeBaseUrl(source.BaseUrl) == DefaultGitHubApi ? DefaultGitHubTokenVariables : [];

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
            ? $"Source '{source.Id}' needs \"token_env\" in {ConfigFiles.FileName}: the default token variables are sent only to {DefaultGitHubApi}."
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
        var until = period.End + GitHubConnector.CommitWindowSlack;
        var nodes = new JsonArray();
        result.Lines.Add($"Dry run: {period.Label} = {FormatUtc(period.Start)} to {FormatUtc(period.End)} ({period.Timezone}); nothing is requested or written.");
        foreach (var s in sources)
        {
            var baseUrl = EvidenceIds.NormalizeBaseUrl(s.BaseUrl);
            var repo = $"repos/{s.Repository}";
            string[] endpoints =
            [
                $"GET {baseUrl}/{repo}",
                $"GET {baseUrl}/{repo}/commits?sha=<default branch>&since={GitHubApiClient.FormatTime(period.Start)}&until={GitHubApiClient.FormatTime(until)}&per_page=100",
                $"GET {baseUrl}/{repo}/commits/<sha>",
                $"GET {baseUrl}/{repo}/pulls?state=all&sort=updated&direction=desc&per_page=100",
                $"GET {baseUrl}/{repo}/pulls/<number>",
                $"GET {baseUrl}/{repo}/pulls/<number>/commits?per_page=100",
            ];

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
                ["scopes"] = new JsonArray([.. GitHubScopes.Select(e => (JsonNode)e)]),
            });

            result.Lines.Add($"{s.Id}: {baseUrl} {s.Repository}; token {variable ?? "(none: set token_env)"} {(set ? "set" : "not set")}; needs {string.Join(", ", GitHubScopes)}");
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

    private static SourceSpec Spec(ConfigSource source) => new(source.Platform, source.BaseUrl, source.Repository);

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
        _ => kind.ToString().ToLowerInvariant(),
    };

    private sealed record CollectOptions(
        string Period,
        IReadOnlyList<string> SourceIds,
        string? ConfigPath,
        string OutputPath,
        bool DryRun,
        bool Verbose);
}

/// <summary>
/// Counts every HTTP attempt of one source (retries included) for <c>result.requests</c>, and with
/// <c>--verbose</c> logs one redacted line per attempt: method, URL, status and remaining rate limit. Never logs a
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
                var remaining = response.Headers.TryGetValues("x-ratelimit-remaining", out var values) ? values.First() : "-";
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
}
