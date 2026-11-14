using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using WorkLens.Core.Errors;
using WorkLens.Core.Security;

namespace WorkLens.Connectors.Git;

/// <summary>A successful value, or the error that stopped the request.</summary>
public sealed record GitLabResult<T>(T? Value, WorkLensError? Error)
{
    public bool IsSuccess => Error is null;

    public static GitLabResult<T> Success(T value) => new(value, null);

    public static GitLabResult<T> Failure(WorkLensError error) => new(default, error);
}

/// <summary>
/// GitLab REST v4 GET requests over an injected <see cref="HttpClient"/> (built by <see cref="GitLabHttp"/>):
/// relative paths, <c>per_page=100</c> on paged requests, and status-to-error mapping through a
/// <see cref="RetryPolicy"/> in <see cref="RetryPlatform.GitLab"/> mode. Pagination, in order:
/// <list type="number">
/// <item>A <c>Link</c> header decides alone: its <c>rel="next"</c> target must stay on the source origin and API
/// prefix and must not repeat an earlier page; no next target means the last page.</item>
/// <item>Otherwise an <c>X-Next-Page</c> header decides: empty means the last page; a page number greater than the
/// current one is requested next; any other value is <c>source.invalid_response</c>.</item>
/// <item>Otherwise a full page (<c>per_page</c> elements) requests the next offset page, for at most
/// <see cref="MaxHeaderlessPages"/> pages; a repeated page or the limit is <c>source.invalid_response</c>.</item>
/// </list>
/// Expected failures are returned as <see cref="GitLabResult{T}"/>; a caller cancellation throws
/// <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class GitLabApiClient
{
    public const int PageSize = 100;
    public const int MaxHeaderlessPages = 1000;
    private const string Method = "GET";

    private readonly HttpClient _http;
    private readonly RetryPolicy _retry;
    private readonly Redactor _redactor;
    private readonly Uri _baseAddress;

    /// <param name="retry">Must use <see cref="RetryPlatform.GitLab"/>.</param>
    /// <param name="redactor">Masks paths and <c>Location</c> values in error messages; a pattern-only one by default.</param>
    public GitLabApiClient(HttpClient http, RetryPolicy retry, Redactor? redactor = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _retry = retry ?? throw new ArgumentNullException(nameof(retry));
        if (retry.Platform != RetryPlatform.GitLab)
        {
            throw new ArgumentException("A GitLab client needs a GitLab retry policy.", nameof(retry));
        }

        _redactor = redactor ?? new Redactor();
        _baseAddress = http.BaseAddress ?? throw new ArgumentException("The client needs a BaseAddress.", nameof(http));
    }

    /// <summary>GitLab query time format: ISO 8601 UTC, second precision, <c>Z</c> suffix.</summary>
    public static string FormatTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The project path segment: <c>projects/</c> plus the URL-encoded path, such as <c>group%2Fproject</c>.</summary>
    public static string ProjectPath(string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        return "projects/" + Uri.EscapeDataString(project);
    }

    /// <summary>One unpaged request, for example <c>projects/group%2Fproject</c>.</summary>
    public async Task<GitLabResult<JsonElement>> GetAsync(
        string path, IEnumerable<KeyValuePair<string, string>>? query, CancellationToken cancellationToken)
    {
        var response = await SendAsync(BuildUri(path, query, paged: false), cancellationToken).ConfigureAwait(false);
        return response.Error is { } error
            ? GitLabResult<JsonElement>.Failure(error)
            : GitLabResult<JsonElement>.Success(response.Body);
    }

    /// <summary>
    /// Requests the first page and the following pages while <paramref name="onPage"/> returns <c>true</c>.
    /// The result is the number of pages received.
    /// </summary>
    public async Task<GitLabResult<int>> GetPagesAsync(
        string path,
        IEnumerable<KeyValuePair<string, string>>? query,
        Func<JsonElement, bool> onPage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onPage);

        var uri = BuildUri(path, query, paged: true);
        var pageSize = PageSizeOf(uri);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { uri.AbsoluteUri };
        var pages = 0;
        while (true)
        {
            var response = await SendAsync(uri, cancellationToken).ConfigureAwait(false);
            if (response.Error is { } error)
            {
                return GitLabResult<int>.Failure(error);
            }

            var current = PageOf(uri);
            var headerless = !response.HasLink && response.NextPage is null;
            if (headerless && !seen.Add(response.Hash))
            {
                return Invalid(uri, "an offset page repeated an earlier page");
            }

            pages++;
            if (!onPage(response.Body))
            {
                return GitLabResult<int>.Success(pages);
            }

            if (response.HasLink)
            {
                if (response.LinkNext is null)
                {
                    return GitLabResult<int>.Success(pages);
                }

                if (!visited.Add(response.LinkNext.AbsoluteUri))
                {
                    return Invalid(uri, "the next-page link repeats an earlier page");
                }

                uri = response.LinkNext;
                continue;
            }

            if (response.NextPage is { } nextPage)
            {
                if (nextPage.Length == 0)
                {
                    return GitLabResult<int>.Success(pages);
                }

                if (!int.TryParse(nextPage, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    || number <= current)
                {
                    return Invalid(uri, "the X-Next-Page header is malformed or does not advance");
                }

                uri = WithPage(uri, number);
                continue;
            }

            if (response.Body.ValueKind != JsonValueKind.Array || response.Body.GetArrayLength() < pageSize)
            {
                return GitLabResult<int>.Success(pages);
            }

            if (pages >= MaxHeaderlessPages)
            {
                return Invalid(
                    uri,
                    $"no pagination completion signal after {MaxHeaderlessPages.ToString(CultureInfo.InvariantCulture)} full pages");
            }

            uri = WithPage(uri, current + 1);
        }
    }

    /// <summary>All elements of a paged array endpoint.</summary>
    public async Task<GitLabResult<IReadOnlyList<JsonElement>>> GetAllAsync(
        string path, IEnumerable<KeyValuePair<string, string>>? query, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        WorkLensError? shapeError = null;
        var result = await GetPagesAsync(
            path,
            query,
            page =>
            {
                if (page.ValueKind != JsonValueKind.Array)
                {
                    shapeError = SourceErrors.Create(
                        SourceErrors.InvalidResponse, Method, DisplayPath(BuildUri(path, query, paged: true)), null,
                        "expected a JSON array");
                    return false;
                }

                items.AddRange(page.EnumerateArray());
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        if ((result.Error ?? shapeError) is { } error)
        {
            return GitLabResult<IReadOnlyList<JsonElement>>.Failure(error);
        }

        return GitLabResult<IReadOnlyList<JsonElement>>.Success(items);
    }

    private sealed record Response(
        JsonElement Body, string Hash, bool HasLink, Uri? LinkNext, string? NextPage, WorkLensError? Error);

    private async Task<Response> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        var path = DisplayPath(uri);
        var state = new RetryState();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RetryDecision decision;
            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                // An OperationCanceledException without a caller cancellation is the HttpClient timeout.
                decision = _retry.ForTransportFailure(state);
                if (decision.Action == RetryAction.Fail)
                {
                    return Failed(SourceErrors.Create(decision.ErrorCode!, Method, path, null, decision.Reason));
                }

                await _retry.DelayAsync(decision.Delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                var status = response.StatusCode;
                var code = (int)status;
                if (code is >= 200 and <= 299)
                {
                    return await ReadSuccessAsync(response, path, cancellationToken).ConfigureAwait(false);
                }

                if (code is >= 300 and <= 399)
                {
                    var location = response.Headers.Location is { } target
                        ? _redactor.Redact(target.OriginalString)
                        : null;
                    return Failed(SourceErrors.Create(
                        SourceErrors.Moved, Method, path, status,
                        "the resource moved (for example a renamed or transferred project); update the source",
                        location));
                }

                // GitLab rules never read the body, so it is not loaded.
                if (_retry.ForResponse(status, response.Headers, string.Empty, state) is not { } retry)
                {
                    return Failed(code switch
                    {
                        401 => SourceErrors.Create(SourceErrors.Unauthorized, Method, path, status,
                            "the token was rejected; check that it is valid and not expired"),
                        404 => SourceErrors.Create(SourceErrors.NotFound, Method, path, status,
                            "not found, or the token cannot see it"),
                        _ => SourceErrors.Create(SourceErrors.RequestFailed, Method, path, status, "request failed"),
                    });
                }

                if (retry.Action == RetryAction.Fail)
                {
                    return Failed(SourceErrors.Create(retry.ErrorCode!, Method, path, status, retry.Reason));
                }

                decision = retry;
            }

            await _retry.DelayAsync(decision.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Response> ReadSuccessAsync(
        HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        JsonElement body;
        string hash;
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            body = document.RootElement.Clone();
            hash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (JsonException)
        {
            return Failed(SourceErrors.Create(
                SourceErrors.InvalidResponse, Method, path, response.StatusCode, "the response is not valid JSON"));
        }

        var hasLink = response.Headers.TryGetValues("Link", out var links);
        if (hasLink && !LinkHeader.HasRelation(links!))
        {
            // A Link header without any relation cannot signal completion; ending here could drop pages.
            return Failed(SourceErrors.Create(
                SourceErrors.InvalidResponse, Method, path, response.StatusCode, "the Link header is malformed"));
        }

        Uri? next = null;
        if (hasLink && LinkHeader.FindNext(links!) is { } raw)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out next) || !IsUnderBase(next))
            {
                // Never send the token to another host or outside the API.
                return Failed(SourceErrors.Create(
                    SourceErrors.InvalidResponse, Method, path, response.StatusCode,
                    "the next-page link does not point to the source API"));
            }
        }

        var nextPage = response.Headers.TryGetValues("X-Next-Page", out var values)
            ? string.Concat(values).Trim()
            : null;
        return new Response(body, hash, hasLink, next, nextPage, null);
    }

    private bool IsUnderBase(Uri uri) =>
        string.Equals(uri.Scheme, _baseAddress.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, _baseAddress.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _baseAddress.Port
        && uri.AbsolutePath.StartsWith(_baseAddress.AbsolutePath, StringComparison.Ordinal);

    private Uri BuildUri(string path, IEnumerable<KeyValuePair<string, string>>? query, bool paged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith('/') || path.Contains('?', StringComparison.Ordinal))
        {
            throw new ArgumentException("Pass a relative path without a leading '/' or a query string.", nameof(path));
        }

        var parameters = (query ?? []).ToList();
        if (paged && !parameters.Exists(p => p.Key == "per_page"))
        {
            parameters.Add(new("per_page", PageSize.ToString(CultureInfo.InvariantCulture)));
        }

        var relative = parameters.Count == 0
            ? path
            : path + "?" + string.Join('&', parameters.Select(Pair));
        return new Uri(_baseAddress, relative);
    }

    private static string Pair(KeyValuePair<string, string> p) =>
        $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}";

    private static List<KeyValuePair<string, string>> Query(Uri uri) =>
        [.. uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            return equals < 0
                ? new KeyValuePair<string, string>(Uri.UnescapeDataString(part), string.Empty)
                : new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(part[..equals]), Uri.UnescapeDataString(part[(equals + 1)..]));
        })];

    private static int ParameterOr(Uri uri, string name, int fallback) =>
        Query(uri).LastOrDefault(p => p.Key == name).Value is { } value
        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number > 0
            ? number
            : fallback;

    private static int PageOf(Uri uri) => ParameterOr(uri, "page", 1);

    private static int PageSizeOf(Uri uri) => ParameterOr(uri, "per_page", PageSize);

    private static Uri WithPage(Uri uri, int page)
    {
        var parameters = Query(uri);
        parameters.RemoveAll(p => p.Key == "page");
        parameters.Add(new("page", page.ToString(CultureInfo.InvariantCulture)));
        return new UriBuilder(uri) { Query = string.Join('&', parameters.Select(Pair)) }.Uri;
    }

    private GitLabResult<int> Invalid(Uri uri, string detail) =>
        GitLabResult<int>.Failure(SourceErrors.Create(SourceErrors.InvalidResponse, Method, DisplayPath(uri), null, detail));

    private string DisplayPath(Uri uri) => _redactor.Redact(uri.PathAndQuery);

    private static Response Failed(WorkLensError error) => new(default, string.Empty, false, null, null, error);
}
