using System.Globalization;
using System.Text.Json;
using WorkLens.Core.Common;
using WorkLens.Core.Contracts;
using WorkLens.Core.Errors;
using WorkLens.Core.Evidence;
using WorkLens.Core.Identities;

namespace WorkLens.Connectors.Git.GitLab;

/// <summary>
/// Collects the subject's commits and merge requests of one GitLab project (docs/design/domain-model.md
/// section 2.1): default-branch commits authored in the period, merge requests whose <c>occurred_at</c>
/// (<c>merged_at ?? created_at</c>) is in the period, and the subject's commits of those merge requests (with
/// <c>contains</c> relations). Each included commit is read once in detail for its stats and once through its
/// diff for its paths. GitLab commits carry no platform account, so commits match by email only. Flags are set,
/// nothing is excluded.
/// </summary>
public sealed class GitLabConnector : ISourceConnector
{
    /// <summary>The most diff files read for one commit; more files give a <c>source.truncated</c> warning.</summary>
    public const int MaxCommitFiles = 3000;

    /// <summary>The commit list filters by committer date; rebased commits are committed after they are authored.</summary>
    public static readonly TimeSpan CommitWindowSlack = TimeSpan.FromDays(30);

    private readonly GitLabApiClient _api;
    private readonly TimeProvider _time;

    public GitLabConnector(GitLabApiClient api, TimeProvider time)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public SourcePlatform Platform => SourcePlatform.Gitlab;

    public async Task<SourceCollectResult> CollectAsync(SourceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Source.Platform != SourcePlatform.Gitlab)
        {
            throw new ArgumentException("The source is not a GitLab source.", nameof(query));
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
        private readonly GitLabApiClient _api;
        private readonly TimeProvider _time;
        private readonly Period _period;
        private readonly SourceSpec _source;
        private readonly string _baseUrl;
        private readonly IdentityMatcher _matcher;
        private readonly Dictionary<string, EvidenceItem> _commits = new(StringComparer.Ordinal);
        private readonly List<EvidenceItem> _mergeRequests = [];
        private readonly List<WorkLensError> _warnings = [];
        private string _projectPath;

        public Run(GitLabApiClient api, TimeProvider time, SourceQuery query)
        {
            _api = api;
            _time = time;
            _period = query.Period;
            _source = query.Source;
            _baseUrl = EvidenceIds.NormalizeBaseUrl(query.Source.BaseUrl);
            _projectPath = GitLabApiClient.ProjectPath(query.Source.Repository);
            _matcher = new IdentityMatcher(query.Identities, SourcePlatform.Gitlab);
        }

        public IReadOnlyList<EvidenceItem> Items => [.. _commits.Values, .. _mergeRequests];

        public IReadOnlyList<WorkLensError> Warnings => _warnings;

        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var path = _projectPath;
            var project = await GetAsync<ProjectDto>(path, null, cancellationToken).ConfigureAwait(false);
            var id = Require(project.Id, path, "id");

            // The numeric ID addresses the resolved project in every later request.
            _projectPath = "projects/" + id.ToString(CultureInfo.InvariantCulture);

            // GitLab answers some requests on an empty repository with 409; empty_repo says it without a request.
            if (project.EmptyRepo != true)
            {
                var branch = Require(project.DefaultBranch, path, "default_branch");
                await CollectBranchCommitsAsync(branch, cancellationToken).ConfigureAwait(false);
            }

            if (!project.HasMergeRequests)
            {
                return;
            }

            foreach (var mergeRequest in await ListMergeRequestsAsync(cancellationToken).ConfigureAwait(false))
            {
                await CollectMergeRequestAsync(mergeRequest, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task CollectBranchCommitsAsync(string branch, CancellationToken cancellationToken)
        {
            var path = _projectPath + "/repository/commits";
            var selected = new List<(string Sha, EvidenceAuthor Author)>();
            await PagesAsync(
                path,
                [
                    new("ref_name", branch),
                    new("since", GitLabApiClient.FormatTime(_period.Start)),
                    new("until", GitLabApiClient.FormatTime(_period.End + CommitWindowSlack)),
                ],
                page =>
                {
                    foreach (var commit in DeserializeItems<CommitDto>(page, path))
                    {
                        var authored = Require(commit.AuthoredDate, path, "authored_date");
                        if (authored >= _period.Start && authored < _period.End && Match(commit) is { } author)
                        {
                            selected.Add((Require(commit.Id, path, "id"), author));
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

        /// <summary>The subject's merge requests whose occurred_at is in the period, most recently updated first.</summary>
        private async Task<List<MergeRequestDto>> ListMergeRequestsAsync(CancellationToken cancellationToken)
        {
            var path = _projectPath + "/merge_requests";
            var selected = new List<MergeRequestDto>();
            await PagesAsync(
                path,
                [
                    new("state", "all"),
                    new("scope", "all"),
                    new("order_by", "updated_at"),
                    new("sort", "desc"),
                    new("updated_after", GitLabApiClient.FormatTime(_period.Start)),
                ],
                page =>
                {
                    foreach (var mergeRequest in DeserializeItems<MergeRequestDto>(page, path))
                    {
                        Require(mergeRequest.Iid, path, "iid");
                        var occurred = mergeRequest.MergedAt ?? Require(mergeRequest.CreatedAt, path, "created_at");
                        if (occurred >= _period.Start && occurred < _period.End && MatchAuthor(mergeRequest) is not null)
                        {
                            selected.Add(mergeRequest);
                        }
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            return selected;
        }

        private async Task CollectMergeRequestAsync(MergeRequestDto listed, CancellationToken cancellationToken)
        {
            var number = listed.Iid!.Value.ToString(CultureInfo.InvariantCulture);
            var path = $"{_projectPath}/merge_requests/{number}";
            var mergeRequest = await GetAsync<MergeRequestDto>(path, null, cancellationToken).ConfigureAwait(false);
            if (mergeRequest.Iid != listed.Iid)
            {
                throw Invalid(path, "the merge request detail has another iid");
            }

            var created = Require(mergeRequest.CreatedAt, path, "created_at");
            var author = MatchAuthor(mergeRequest) ?? MatchAuthor(listed)!;

            var commitsPath = path + "/commits";
            var commits = new List<CommitDto>();
            await PagesAsync(
                commitsPath,
                null,
                page =>
                {
                    commits.AddRange(DeserializeItems<CommitDto>(page, commitsPath));
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            var relations = new List<EvidenceRelation>();
            foreach (var commit in commits)
            {
                if (Match(commit) is { } commitAuthor)
                {
                    var item = await CommitAsync(Require(commit.Id, commitsPath, "id"), commitAuthor, cancellationToken)
                        .ConfigureAwait(false);
                    relations.Add(new EvidenceRelation(RelationType.Contains, item.Id));
                }
            }

            var title = mergeRequest.Title ?? string.Empty;
            var flags = new List<EvidenceFlag>();
            if (mergeRequest.Author?.Bot == true)
            {
                flags.Add(EvidenceFlag.Bot);
            }

            if (IsRevert(title))
            {
                flags.Add(EvidenceFlag.Revert);
            }

            // Stats and paths stay unset: summed commit stats are not the merge request's net change.
            _mergeRequests.Add(new EvidenceItem(
                EvidenceIds.ForItem(SourcePlatform.Gitlab, _baseUrl, _source.Repository, EvidenceKind.ChangeRequest, number),
                EvidenceKind.ChangeRequest,
                new EvidenceSource(SourcePlatform.Gitlab, _baseUrl, _source.Repository, number, Url(mergeRequest.WebUrl, path)),
                title,
                author,
                new EvidenceTimestamps(
                    OccurredAt: mergeRequest.MergedAt ?? created,
                    CreatedAt: created,
                    MergedAt: mergeRequest.MergedAt,
                    ClosedAt: mergeRequest.ClosedAt),
                _time.GetUtcNow(),
                Body: string.IsNullOrWhiteSpace(mergeRequest.Description) ? null : mergeRequest.Description,
                State: State(mergeRequest.State, path),
                Relations: relations,
                Flags: flags));
        }

        /// <summary>The commit item, read once per SHA: the detail for its stats, the diff pages for its paths.</summary>
        private async Task<EvidenceItem> CommitAsync(string sha, EvidenceAuthor author, CancellationToken cancellationToken)
        {
            if (_commits.TryGetValue(sha, out var cached))
            {
                return cached;
            }

            var path = $"{_projectPath}/repository/commits/{Uri.EscapeDataString(sha)}";
            var detail = await GetAsync<CommitDto>(path, null, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(detail.Id, sha, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(path, "the commit detail has another id");
            }

            var diffPath = path + "/diff";
            var paths = new List<string>();
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            var read = 0;
            var truncated = false;
            await PagesAsync(
                diffPath,
                null,
                page =>
                {
                    foreach (var diff in DeserializeItems<DiffDto>(page, diffPath))
                    {
                        if (read == MaxCommitFiles)
                        {
                            truncated = true;
                            return false;
                        }

                        read++;
                        var file = diff.DeletedFile == true
                            ? Require(diff.OldPath, diffPath, "old_path")
                            : Require(diff.NewPath, diffPath, "new_path");
                        if (distinct.Add(file))
                        {
                            paths.Add(file);
                        }
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            if (truncated)
            {
                _warnings.Add(Warning(
                    sha, $"commit {sha} changes more than {MaxCommitFiles} files; only the first {MaxCommitFiles} are collected"));
            }

            var message = detail.Message ?? string.Empty;
            var newline = message.IndexOf('\n', StringComparison.Ordinal);
            var title = (newline < 0 ? message : message[..newline]).TrimEnd('\r');
            var body = newline < 0 ? null : message[(newline + 1)..].Trim();

            var flags = new List<EvidenceFlag>();
            if (detail.ParentIds is { Count: > 1 })
            {
                flags.Add(EvidenceFlag.MergeCommit);
            }

            if (IsRevert(title))
            {
                flags.Add(EvidenceFlag.Revert);
            }

            var authored = Require(detail.AuthoredDate, path, "authored_date");
            var stats = detail.Stats ?? throw Invalid(path, "the commit detail has no stats");
            var item = new EvidenceItem(
                EvidenceIds.ForItem(SourcePlatform.Gitlab, _baseUrl, _source.Repository, EvidenceKind.Commit, sha),
                EvidenceKind.Commit,
                new EvidenceSource(SourcePlatform.Gitlab, _baseUrl, _source.Repository, sha, Url(detail.WebUrl, path)),
                title,
                author,
                new EvidenceTimestamps(
                    OccurredAt: authored,
                    AuthoredAt: authored,
                    CommittedAt: detail.CommittedDate),
                _time.GetUtcNow(),
                Body: string.IsNullOrEmpty(body) ? null : body,
                Stats: new ChangeStats(
                    paths.Count,
                    Require(stats.Additions, path, "stats.additions"),
                    Require(stats.Deletions, path, "stats.deletions")),
                Paths: paths,
                Flags: flags);
            _commits.Add(sha, item);
            return item;
        }

        private EvidenceAuthor? Match(CommitDto commit) => _matcher.MatchCommit(
            new SourcePerson(commit.AuthorName, commit.AuthorEmail),
            new SourcePerson(commit.CommitterName, commit.CommitterEmail),
            commit.Message);

        private EvidenceAuthor? MatchAuthor(MergeRequestDto mergeRequest) => _matcher.MatchChangeRequest(
            new SourcePerson(Name: mergeRequest.Author?.Name, Login: mergeRequest.Author?.Username));

        private static bool IsRevert(string title) => title.StartsWith("Revert \"", StringComparison.Ordinal);

        // locked is a short transient state while GitLab merges; the merge request is still open.
        private static EvidenceState State(string? state, string path) => state switch
        {
            "opened" or "locked" => EvidenceState.Open,
            "closed" => EvidenceState.Closed,
            "merged" => EvidenceState.Merged,
            _ => throw Invalid(path, "unknown merge request state"),
        };

        private static string Url(string? value, string path) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : throw Invalid(path, "missing web_url");

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
            Func<JsonElement, bool> onPage,
            CancellationToken cancellationToken)
        {
            var result = await _api.GetPagesAsync(path, query, onPage, cancellationToken).ConfigureAwait(false);
            if (result.Error is { } error)
            {
                throw new SourceFailure(error);
            }
        }

        /// <summary>The elements of a list page; a null element is a missing required response.</summary>
        private static List<T> DeserializeItems<T>(JsonElement page, string path)
            where T : class
        {
            var items = Deserialize<List<T?>>(page, path);
            return items.Contains(null) ? throw Invalid(path, "the response holds a null list element") : items.ConvertAll(item => item!);
        }

        private static T Deserialize<T>(JsonElement element, string path)
        {
            try
            {
                return element.Deserialize<T>(GitLabJson.Options) ?? throw Invalid(path, "the response is null");
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
