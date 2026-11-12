using System.Globalization;
using System.Text.Json;
using WorkLens.Core.Common;
using WorkLens.Core.Contracts;
using WorkLens.Core.Errors;
using WorkLens.Core.Evidence;
using WorkLens.Core.Identities;

namespace WorkLens.Connectors.Git.GitHub;

/// <summary>
/// Collects the subject's commits and pull requests of one GitHub repository (docs/design/domain-model.md
/// section 2.1): default-branch commits authored in the period, pull requests whose <c>occurred_at</c> is in the
/// period, and the subject's commits of those pull requests (with <c>contains</c> relations). Each included commit
/// is read once in detail for its stats and paths. Flags are set, nothing is excluded.
/// </summary>
public sealed class GitHubConnector : ISourceConnector
{
    /// <summary>GitHub returns at most this many commits for a pull request.</summary>
    public const int MaxPullRequestCommits = 250;

    /// <summary>GitHub returns at most this many files for a commit.</summary>
    public const int MaxCommitFiles = 3000;

    /// <summary>The commit list filters by committer date; rebased commits are committed after they are authored (D6).</summary>
    public static readonly TimeSpan CommitWindowSlack = TimeSpan.FromDays(30);

    private readonly GitHubApiClient _api;
    private readonly TimeProvider _time;

    public GitHubConnector(GitHubApiClient api, TimeProvider time)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public SourcePlatform Platform => SourcePlatform.Github;

    public async Task<SourceCollectResult> CollectAsync(SourceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Source.Platform != SourcePlatform.Github)
        {
            throw new ArgumentException("The source is not a GitHub source.", nameof(query));
        }

        var run = new Run(_api, _time, query);
        try
        {
            await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return new SourceCollectResult(run.Items, [], run.Warnings);
        }
        catch (SourceFailure failure)
        {
            return new SourceCollectResult([], [failure.Error], run.Warnings);
        }
    }

    private sealed class SourceFailure(WorkLensError error) : Exception(error.Message)
    {
        public WorkLensError Error { get; } = error;
    }

    /// <summary>State of one collection: the commit cache, the collected items and warnings.</summary>
    private sealed class Run
    {
        private readonly GitHubApiClient _api;
        private readonly TimeProvider _time;
        private readonly Period _period;
        private readonly SourceSpec _source;
        private readonly string _baseUrl;
        private readonly string _repoPath;
        private readonly IdentityMatcher _matcher;
        private readonly Dictionary<string, EvidenceItem> _commits = new(StringComparer.Ordinal);
        private readonly List<EvidenceItem> _pullRequests = [];
        private readonly List<WorkLensError> _warnings = [];

        public Run(GitHubApiClient api, TimeProvider time, SourceQuery query)
        {
            _api = api;
            _time = time;
            _period = query.Period;
            _source = query.Source;
            _baseUrl = EvidenceIds.NormalizeBaseUrl(query.Source.BaseUrl);
            _repoPath = "repos/" + string.Join(
                '/', query.Source.Repository.Split('/').Select(Uri.EscapeDataString));
            _matcher = new IdentityMatcher(query.Identities, SourcePlatform.Github);
        }

        public IReadOnlyList<EvidenceItem> Items => [.. _commits.Values, .. _pullRequests];

        public IReadOnlyList<WorkLensError> Warnings => _warnings;

        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var repository = await GetAsync<RepositoryDto>(_repoPath, null, cancellationToken).ConfigureAwait(false);
            var branch = Require(repository.DefaultBranch, _repoPath, "default_branch");

            await CollectBranchCommitsAsync(branch, cancellationToken).ConfigureAwait(false);
            foreach (var pull in await ListPullRequestsAsync(cancellationToken).ConfigureAwait(false))
            {
                await CollectPullRequestAsync(pull, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task CollectBranchCommitsAsync(string branch, CancellationToken cancellationToken)
        {
            var path = _repoPath + "/commits";
            var selected = new List<(string Sha, EvidenceAuthor Author)>();
            await PagesAsync(
                path,
                [
                    new("sha", branch),
                    new("since", GitHubApiClient.FormatTime(_period.Start)),
                    new("until", GitHubApiClient.FormatTime(_period.End + CommitWindowSlack)),
                ],
                GitHubRequestOptions.CommitList,
                page =>
                {
                    foreach (var commit in Deserialize<List<CommitDto>>(page, path))
                    {
                        var authored = AuthorDate(commit, path);
                        if (authored >= _period.Start && authored < _period.End && Match(commit) is { } author)
                        {
                            selected.Add((Require(commit.Sha, path, "sha"), author));
                        }
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var (sha, author) in selected)
            {
                await CommitAsync(sha, author, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>The subject's pull requests whose occurred_at is in the period, most recently updated first.</summary>
        private async Task<List<PullRequestDto>> ListPullRequestsAsync(CancellationToken cancellationToken)
        {
            var path = _repoPath + "/pulls";
            var selected = new List<PullRequestDto>();
            await PagesAsync(
                path,
                [new("state", "all"), new("sort", "updated"), new("direction", "desc")],
                null,
                page =>
                {
                    var older = false;
                    foreach (var pull in Deserialize<List<PullRequestDto>>(page, path))
                    {
                        var created = Require(pull.CreatedAt, path, "created_at");
                        var updated = Require(pull.UpdatedAt, path, "updated_at");
                        older |= updated < _period.Start;

                        var occurred = pull.MergedAt ?? created;
                        if (occurred >= _period.Start && occurred < _period.End
                            && _matcher.MatchChangeRequest(new SourcePerson(Login: pull.User?.Login)) is not null)
                        {
                            selected.Add(pull);
                        }
                    }

                    // Sorted by updated_at descending: once one is older than the period, the rest are too.
                    return !older;
                },
                cancellationToken).ConfigureAwait(false);
            return selected;
        }

        private async Task CollectPullRequestAsync(PullRequestDto listed, CancellationToken cancellationToken)
        {
            var number = listed.Number.ToString(CultureInfo.InvariantCulture);
            var path = $"{_repoPath}/pulls/{number}";
            var pull = await GetAsync<PullRequestDto>(path, null, cancellationToken).ConfigureAwait(false);
            var author = _matcher.MatchChangeRequest(new SourcePerson(Login: pull.User?.Login))
                ?? _matcher.MatchChangeRequest(new SourcePerson(Login: listed.User?.Login))!;

            var commitsPath = path + "/commits";
            var commits = new List<CommitDto>();
            await PagesAsync(
                commitsPath,
                null,
                null,
                page =>
                {
                    commits.AddRange(Deserialize<List<CommitDto>>(page, commitsPath));
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            if (pull.Commits is { } expected && commits.Count < expected)
            {
                _warnings.Add(Warning(
                    number,
                    $"pull request #{number} has {expected} commits but GitHub returned {commits.Count} " +
                    $"(at most {MaxPullRequestCommits}); the rest are not collected"));
            }

            var relations = new List<EvidenceRelation>();
            foreach (var commit in commits)
            {
                if (Match(commit) is { } commitAuthor)
                {
                    var item = await CommitAsync(Require(commit.Sha, commitsPath, "sha"), commitAuthor, cancellationToken)
                        .ConfigureAwait(false);
                    relations.Add(new EvidenceRelation(RelationType.Contains, item.Id));
                }
            }

            var created = Require(pull.CreatedAt, path, "created_at");
            var title = pull.Title ?? string.Empty;
            var flags = new List<EvidenceFlag>();
            if (IsBot(pull.User))
            {
                flags.Add(EvidenceFlag.Bot);
            }

            if (IsRevert(title))
            {
                flags.Add(EvidenceFlag.Revert);
            }

            _pullRequests.Add(new EvidenceItem(
                EvidenceIds.ForItem(SourcePlatform.Github, _baseUrl, _source.Repository, EvidenceKind.ChangeRequest, number),
                EvidenceKind.ChangeRequest,
                new EvidenceSource(SourcePlatform.Github, _baseUrl, _source.Repository, number, Url(pull.HtmlUrl, path)),
                title,
                author,
                new EvidenceTimestamps(
                    OccurredAt: pull.MergedAt ?? created,
                    CreatedAt: created,
                    MergedAt: pull.MergedAt,
                    ClosedAt: pull.ClosedAt),
                _time.GetUtcNow(),
                Body: string.IsNullOrWhiteSpace(pull.Body) ? null : pull.Body,
                State: State(pull, path),
                Stats: pull is { ChangedFiles: { } files, Additions: { } additions, Deletions: { } deletions }
                    ? new ChangeStats(files, additions, deletions)
                    : null,
                Relations: relations,
                Flags: flags));
        }

        /// <summary>The commit item, read in detail once per SHA (all file pages).</summary>
        private async Task<EvidenceItem> CommitAsync(string sha, EvidenceAuthor author, CancellationToken cancellationToken)
        {
            if (_commits.TryGetValue(sha, out var cached))
            {
                return cached;
            }

            var path = $"{_repoPath}/commits/{Uri.EscapeDataString(sha)}";
            CommitDto? detail = null;
            var files = new List<string>();
            await PagesAsync(
                path,
                null,
                null,
                page =>
                {
                    var commit = Deserialize<CommitDto>(page, path);
                    detail ??= commit;
                    files.AddRange((commit.Files ?? []).Select(f => Require(f.Filename, path, "files[].filename")));
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            if (detail is null)
            {
                throw Invalid(path, "the commit detail is empty");
            }

            if (files.Count >= MaxCommitFiles)
            {
                _warnings.Add(Warning(
                    sha, $"commit {sha} lists {files.Count} files, the most GitHub returns; stats may be incomplete"));
            }

            var message = detail.Commit?.Message ?? string.Empty;
            var newline = message.IndexOf('\n', StringComparison.Ordinal);
            var title = (newline < 0 ? message : message[..newline]).TrimEnd('\r');
            var body = newline < 0 ? null : message[(newline + 1)..].Trim();

            var flags = new List<EvidenceFlag>();
            if (detail.Parents is { Count: > 1 })
            {
                flags.Add(EvidenceFlag.MergeCommit);
            }

            if (IsRevert(title))
            {
                flags.Add(EvidenceFlag.Revert);
            }

            if (IsBot(detail.Author))
            {
                flags.Add(EvidenceFlag.Bot);
            }

            var authored = AuthorDate(detail, path);
            var stats = detail.Stats ?? throw Invalid(path, "the commit detail has no stats");
            var item = new EvidenceItem(
                EvidenceIds.ForItem(SourcePlatform.Github, _baseUrl, _source.Repository, EvidenceKind.Commit, sha),
                EvidenceKind.Commit,
                new EvidenceSource(SourcePlatform.Github, _baseUrl, _source.Repository, sha, Url(detail.HtmlUrl, path)),
                title,
                author,
                new EvidenceTimestamps(
                    OccurredAt: authored,
                    AuthoredAt: authored,
                    CommittedAt: detail.Commit?.Committer?.Date),
                _time.GetUtcNow(),
                Body: string.IsNullOrEmpty(body) ? null : body,
                Stats: new ChangeStats(files.Count, stats.Additions, stats.Deletions),
                Paths: files,
                Flags: flags);
            _commits.Add(sha, item);
            return item;
        }

        private EvidenceAuthor? Match(CommitDto commit) => _matcher.MatchCommit(
            new SourcePerson(commit.Commit?.Author?.Name, commit.Commit?.Author?.Email, commit.Author?.Login),
            new SourcePerson(commit.Commit?.Committer?.Name, commit.Commit?.Committer?.Email, commit.Committer?.Login),
            commit.Commit?.Message);

        private static DateTimeOffset AuthorDate(CommitDto commit, string path) =>
            Require(commit.Commit?.Author?.Date, path, "commit.author.date");

        private static bool IsBot(AccountDto? account) =>
            account is not null
            && (string.Equals(account.Type, "Bot", StringComparison.Ordinal)
                || (account.Login?.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ?? false));

        private static bool IsRevert(string title) => title.StartsWith("Revert \"", StringComparison.Ordinal);

        private static EvidenceState State(PullRequestDto pull, string path) => pull switch
        {
            { MergedAt: not null } => EvidenceState.Merged,
            { State: "open" } => EvidenceState.Open,
            { State: "closed" } => EvidenceState.Closed,
            _ => throw Invalid(path, "unknown pull request state"),
        };

        private static string Url(string? value, string path) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : throw Invalid(path, "missing html_url");

        private WorkLensError Warning(string externalId, string message) => new(
            SourceErrors.Truncated,
            message,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = _source.Repository,
                ["external_id"] = externalId,
            });

        private async Task<T> GetAsync<T>(
            string path, IEnumerable<KeyValuePair<string, string>>? query, CancellationToken cancellationToken)
        {
            var result = await _api.GetAsync(path, query, cancellationToken).ConfigureAwait(false);
            return result.Error is { } error ? throw new SourceFailure(error) : Deserialize<T>(result.Value, path);
        }

        private async Task PagesAsync(
            string path,
            IEnumerable<KeyValuePair<string, string>>? query,
            GitHubRequestOptions? options,
            Func<JsonElement, bool> onPage,
            CancellationToken cancellationToken)
        {
            var result = await _api.GetPagesAsync(path, query, onPage, options, cancellationToken).ConfigureAwait(false);
            if (result.Error is { } error)
            {
                throw new SourceFailure(error);
            }
        }

        private static T Deserialize<T>(JsonElement element, string path)
        {
            try
            {
                return element.Deserialize<T>(GitHubJson.Options) ?? throw Invalid(path, "the response is null");
            }
            catch (JsonException)
            {
                throw Invalid(path, "the response does not have the expected shape");
            }
        }

        private static T Require<T>(T? value, string path, string field)
            where T : class =>
            value ?? throw Invalid(path, $"missing {field}");

        private static TValue Require<TValue>(TValue? value, string path, string field)
            where TValue : struct =>
            value ?? throw Invalid(path, $"missing {field}");

        private static SourceFailure Invalid(string path, string detail) =>
            new(SourceErrors.Create(SourceErrors.InvalidResponse, "GET", path, null, detail));
    }
}
