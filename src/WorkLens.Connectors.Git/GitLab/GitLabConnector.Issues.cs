using System.Globalization;
using WorkLens.Core.Evidence;
using WorkLens.Core.Identities;

namespace WorkLens.Connectors.Git.GitLab;

public sealed partial class GitLabConnector
{
    private sealed partial class Run
    {
        private readonly List<EvidenceItem> _issues = [];

        private sealed record CountedEvent(long Id, int Endpoint, IssueTransition Transition, UserDto? User);

        private bool IsSubject(UserDto? user) => user?.Username is { } username
            && _matcher.MatchChangeRequest(new SourcePerson(Login: username)) is not null;

        private async Task CollectIssuesAsync(CancellationToken cancellationToken)
        {
            var path = _projectPath + "/issues";
            var issues = new Dictionary<long, IssueDto>();
            await PagesAsync(path,
                [new("state", "all"), new("scope", "all"), new("order_by", "updated_at"), new("sort", "desc"),
                    new("updated_after", GitLabApiClient.FormatTime(_period.Start))],
                page =>
                {
                    foreach (var issue in DeserializeItems<IssueDto>(page, path))
                    {
                        issues[Require(issue.Iid, path, "iid")] = issue;
                    }
                    return true;
                }, cancellationToken).ConfigureAwait(false);

            foreach (var issue in issues.OrderBy(pair => pair.Key).Select(pair => pair.Value))
            {
                await CollectIssueAsync(issue, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task CollectIssueAsync(IssueDto issue, CancellationToken cancellationToken)
        {
            var iid = issue.Iid!.Value.ToString(CultureInfo.InvariantCulture);
            var path = $"{_projectPath}/issues/{iid}";
            var assignee = (issue.Assignees ?? []).Where(IsSubject)
                .OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
                .ThenBy(user => user.Username, StringComparer.Ordinal).FirstOrDefault();
            var labels = _source.IssueStatusLabels!.ToHashSet(StringComparer.Ordinal);
            var events = new List<CountedEvent>();
            var labelsPath = path + "/resource_label_events";
            await PagesAsync(labelsPath, null, page =>
            {
                foreach (var change in DeserializeItems<LabelEventDto>(page, labelsPath))
                {
                    if (change.Action != "add" || change.Label?.Name is not { } label || !labels.Contains(label))
                    {
                        continue;
                    }
                    var at = Require(change.CreatedAt, labelsPath, "created_at");
                    var subject = IsSubject(change.User);
                    if (at >= _period.Start && at < _period.End && (subject || assignee is not null))
                    {
                        events.Add(new CountedEvent(Require(change.Id, labelsPath, "id"), 0,
                            new IssueTransition(at.ToUniversalTime(), IssueChange.Status,
                                subject ? IssueActor.Subject : IssueActor.Other, label), change.User));
                    }
                }
                return true;
            }, cancellationToken).ConfigureAwait(false);

            var statesPath = path + "/resource_state_events";
            await PagesAsync(statesPath, null, page =>
            {
                foreach (var change in DeserializeItems<StateEventDto>(page, statesPath))
                {
                    var at = Require(change.CreatedAt, statesPath, "created_at");
                    var subject = IsSubject(change.User);
                    if (at >= _period.Start && at < _period.End && (subject || assignee is not null))
                    {
                        var kind = change.State switch
                        {
                            "closed" => IssueChange.Closed,
                            "opened" => IssueChange.Reopened,
                            _ => throw Invalid(statesPath, "unknown issue state event"),
                        };
                        events.Add(new CountedEvent(Require(change.Id, statesPath, "id"), 1,
                            new IssueTransition(at.ToUniversalTime(), kind, subject ? IssueActor.Subject : IssueActor.Other), change.User));
                    }
                }
                return true;
            }, cancellationToken).ConfigureAwait(false);

            if (events.Count == 0)
            {
                return;
            }
            var ordered = events.OrderBy(e => e.Transition.At).ThenBy(e => e.Endpoint).ThenBy(e => e.Id).ToArray();
            var actor = ordered.LastOrDefault(e => e.Transition.Actor == IssueActor.Subject)?.User;
            var person = actor ?? assignee!;
            _issues.Add(new EvidenceItem(
                EvidenceIds.ForItem(Core.Common.SourcePlatform.Gitlab, _baseUrl, _source.Repository, EvidenceKind.IssueActivity, iid),
                EvidenceKind.IssueActivity,
                new EvidenceSource(Core.Common.SourcePlatform.Gitlab, _baseUrl, _source.Repository, iid, Url(issue.WebUrl, path)),
                issue.Title ?? string.Empty,
                new EvidenceAuthor(actor is null ? AuthorRole.Assignee : AuthorRole.Actor,
                    Name: string.IsNullOrWhiteSpace(person.Name) ? null : person.Name, Username: person.Username),
                new EvidenceTimestamps(ordered[^1].Transition.At, CreatedAt: Require(issue.CreatedAt, path, "created_at").ToUniversalTime(),
                    ClosedAt: issue.ClosedAt?.ToUniversalTime()),
                _time.GetUtcNow(),
                Body: string.IsNullOrWhiteSpace(issue.Description) ? null : issue.Description,
                State: issue.State switch
                {
                    "opened" => EvidenceState.Open,
                    "closed" => EvidenceState.Closed,
                    _ => throw Invalid(path, "unknown issue state"),
                },
                Transitions: [.. ordered.Select(e => e.Transition)]));
        }
    }
}
