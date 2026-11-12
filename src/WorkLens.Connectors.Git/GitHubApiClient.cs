using System.Globalization;
using System.Text.Json;
using WorkLens.Core.Errors;
using WorkLens.Core.Security;

namespace WorkLens.Connectors.Git;

/// <summary>A successful value, or the error that stopped the request.</summary>
public sealed record GitHubResult<T>(T? Value, WorkLensError? Error)
{
    public bool IsSuccess => Error is null;

    public static GitHubResult<T> Success(T value) => new(value, null);

    public static GitHubResult<T> Failure(WorkLensError error) => new(default, error);
}

public sealed record GitHubRequestOptions(bool EmptyOnConflict = false)
{
    public static GitHubRequestOptions Default { get; } = new();

    /// <summary>For the commits endpoint: 409 (empty repository) means no pages instead of an error.</summary>
    public static GitHubRequestOptions CommitList { get; } = new(EmptyOnConflict: true);
}

/// <summary>
/// GitHub REST GET requests over an injected <see cref="HttpClient"/> (built by <see cref="GitHubHttp"/>):
/// relative paths, <c>per_page=100</c> on paged requests, <c>Link</c> pagination restricted to the source host,
/// <see cref="RetryPolicy"/> for rate limits and transient failures, and status-to-error mapping. Expected failures
/// are returned as <see cref="GitHubResult{T}"/>; a caller cancellation throws <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class GitHubApiClient
{
    public const int PageSize = 100;
    private const string Method = "GET";

    private readonly HttpClient _http;
    private readonly RetryPolicy _retry;
    private readonly Redactor _redactor;
    private readonly Uri _baseAddress;

    /// <param name="redactor">Masks paths and <c>Location</c> values in error messages; a pattern-only one by default.</param>
    public GitHubApiClient(HttpClient http, RetryPolicy retry, Redactor? redactor = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _retry = retry ?? throw new ArgumentNullException(nameof(retry));
        _redactor = redactor ?? new Redactor();
        _baseAddress = http.BaseAddress ?? throw new ArgumentException("The client needs a BaseAddress.", nameof(http));
    }

    /// <summary>GitHub query time format: UTC, second precision, <c>Z</c> suffix.</summary>
    public static string FormatTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>One unpaged request, for example <c>repos/{owner}/{repo}</c>.</summary>
    public async Task<GitHubResult<JsonElement>> GetAsync(
        string path, IEnumerable<KeyValuePair<string, string>>? query, CancellationToken cancellationToken)
    {
        var response = await SendAsync(BuildUri(path, query, paged: false), GitHubRequestOptions.Default, cancellationToken)
            .ConfigureAwait(false);
        return response.Error is { } error
            ? GitHubResult<JsonElement>.Failure(error)
            : GitHubResult<JsonElement>.Success(response.Body!.Value);
    }

    /// <summary>
    /// Requests the first page and follows <c>rel="next"</c> while <paramref name="onPage"/> returns <c>true</c>.
    /// The result is the number of pages received (0 for a 409 with <see cref="GitHubRequestOptions.EmptyOnConflict"/>).
    /// </summary>
    public async Task<GitHubResult<int>> GetPagesAsync(
        string path,
        IEnumerable<KeyValuePair<string, string>>? query,
        Func<JsonElement, bool> onPage,
        GitHubRequestOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onPage);
        options ??= GitHubRequestOptions.Default;

        var uri = BuildUri(path, query, paged: true);
        var pages = 0;
        while (true)
        {
            var response = await SendAsync(uri, options, cancellationToken).ConfigureAwait(false);
            if (response.Error is { } error)
            {
                return GitHubResult<int>.Failure(error);
            }

            if (response.Body is not { } body)
            {
                return GitHubResult<int>.Success(pages);
            }

            pages++;
            if (!onPage(body) || response.Next is null)
            {
                return GitHubResult<int>.Success(pages);
            }

            uri = response.Next;
        }
    }

    /// <summary>All elements of a paged array endpoint.</summary>
    public async Task<GitHubResult<IReadOnlyList<JsonElement>>> GetAllAsync(
        string path,
        IEnumerable<KeyValuePair<string, string>>? query,
        GitHubRequestOptions? options,
        CancellationToken cancellationToken)
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
            options,
            cancellationToken).ConfigureAwait(false);

        if ((result.Error ?? shapeError) is { } error)
        {
            return GitHubResult<IReadOnlyList<JsonElement>>.Failure(error);
        }

        return GitHubResult<IReadOnlyList<JsonElement>>.Success(items);
    }

    private sealed record Response(JsonElement? Body, Uri? Next, WorkLensError? Error);

    private async Task<Response> SendAsync(Uri uri, GitHubRequestOptions options, CancellationToken cancellationToken)
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

                if (code == 409 && options.EmptyOnConflict)
                {
                    return new Response(null, null, null);
                }

                if (code is >= 300 and <= 399)
                {
                    var location = response.Headers.Location is { } target
                        ? _redactor.Redact(target.OriginalString)
                        : null;
                    return Failed(SourceErrors.Create(
                        SourceErrors.Moved, Method, path, status,
                        "the resource moved (for example a renamed or transferred repository); update the source",
                        location));
                }

                var body = code is 403 or 429
                    ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                    : string.Empty;
                if (_retry.ForResponse(status, response.Headers, body, state) is not { } retry)
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
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            body = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Failed(SourceErrors.Create(
                SourceErrors.InvalidResponse, Method, path, response.StatusCode, "the response is not valid JSON"));
        }

        Uri? next = null;
        if (response.Headers.TryGetValues("Link", out var links) && LinkHeader.FindNext(links) is { } raw)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out next) || !IsSameOrigin(next))
            {
                // Never send the token to another host.
                return Failed(SourceErrors.Create(
                    SourceErrors.InvalidResponse, Method, path, response.StatusCode,
                    "the next-page link does not point to the source host"));
            }
        }

        return new Response(body, next, null);
    }

    private bool IsSameOrigin(Uri uri) =>
        string.Equals(uri.Scheme, _baseAddress.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, _baseAddress.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _baseAddress.Port;

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
            : path + "?" + string.Join(
                '&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return new Uri(_baseAddress, relative);
    }

    private string DisplayPath(Uri uri) => _redactor.Redact(uri.PathAndQuery);

    private static Response Failed(WorkLensError error) => new(null, null, error);
}
