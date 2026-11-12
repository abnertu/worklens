using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkLens.Tests.Shared;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from fixtures. Each fixture (a JSON file, or one built in code)
/// names <c>method</c>, <c>path</c> (the absolute request path, e.g. <c>/api/v3/repos/o/r</c>) and <c>query</c>
/// (an object; compared after sorting and URL-decoding, so order and escaping do not matter), and the
/// <c>responses</c> returned in order to repeated identical requests (the last one is reused):
/// <code>
/// { "method": "GET", "path": "/repos/o/r", "query": { "per_page": "100" },
///   "responses": [ { "status": 200, "headers": { "Link": "..." }, "body": { ... } },
///                  { "status": 200, "body_text": "not json" },
///                  { "throw": "http" | "timeout", "message": "..." } ] }
/// </code>
/// Paths compare ignoring case, like GitHub owner / repository names. File names do not take part in matching. Every request is recorded; a request without a fixture throws
/// <see cref="FixtureMismatchException"/> (not an HTTP exception, so retry logic cannot swallow it).
/// Shared by test projects through <c>&lt;Compile Include=... Link=...&gt;</c> (overview section 4.3 rule 5).
/// </summary>
internal sealed class FixtureHandler : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<Route> _routes = [];
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<string> _unmatched = [];
    private (int Request, CancellationTokenSource Source)? _cancelAt;

    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public IReadOnlyList<string> Unmatched
    {
        get
        {
            lock (_gate)
            {
                return [.. _unmatched];
            }
        }
    }

    /// <summary>Loads every <c>*.json</c> file in <paramref name="directory"/> (not recursive).</summary>
    public static FixtureHandler FromDirectory(string directory)
    {
        var handler = new FixtureHandler();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            handler.Add(JsonNode.Parse(File.ReadAllText(file))!.AsObject());
        }

        return handler;
    }

    /// <summary>Adds a fixture in the file format.</summary>
    public FixtureHandler Add(JsonObject fixture)
    {
        var query = (fixture["query"]?.AsObject() ?? [])
            .Select(p => new KeyValuePair<string, string>(p.Key, p.Value!.GetValue<string>()));
        var responses = fixture["responses"]!.AsArray().Select(r => r!.AsObject()).ToList();
        if (responses.Count == 0)
        {
            throw new ArgumentException("A fixture needs at least one response.", nameof(fixture));
        }

        lock (_gate)
        {
            _routes.Add(new Route(
                fixture["method"]?.GetValue<string>() ?? "GET",
                fixture["path"]!.GetValue<string>(),
                Canonical(query),
                responses));
        }

        return this;
    }

    /// <summary>Adds a fixture in code: <paramref name="pathAndQuery"/> such as <c>/repos/o/r?per_page=100</c>.</summary>
    public FixtureHandler Add(string method, string pathAndQuery, params JsonObject[] responses)
    {
        var question = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = question < 0 ? pathAndQuery : pathAndQuery[..question];
        var query = new JsonObject();
        foreach (var (key, value) in ParseQuery(question < 0 ? string.Empty : pathAndQuery[(question + 1)..]))
        {
            query[key] = value;
        }

        return Add(new JsonObject
        {
            ["method"] = method,
            ["path"] = path,
            ["query"] = query,
            ["responses"] = new JsonArray([.. responses]),
        });
    }

    /// <summary>Response helper: status, JSON body and headers.</summary>
    public static JsonObject Respond(int status, JsonNode? body = null, params (string Name, string Value)[] headers)
    {
        var response = new JsonObject { ["status"] = status };
        if (body is not null)
        {
            response["body"] = body;
        }

        if (headers.Length > 0)
        {
            var map = new JsonObject();
            foreach (var (name, value) in headers)
            {
                map[name] = value;
            }

            response["headers"] = map;
        }

        return response;
    }

    /// <summary>On the <paramref name="request"/>-th request (1-based), cancels <paramref name="source"/>.</summary>
    public FixtureHandler CancelOnRequest(int request, CancellationTokenSource source)
    {
        _cancelAt = (request, source);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int number;
        lock (_gate)
        {
            _requests.Add(request);
            number = _requests.Count;
        }

        if (_cancelAt is { } cancel && cancel.Request == number)
        {
            cancel.Source.Cancel();
        }

        cancellationToken.ThrowIfCancellationRequested();

        var uri = request.RequestUri!;
        var query = Canonical(ParseQuery(uri.Query.TrimStart('?')));
        JsonObject response;
        lock (_gate)
        {
            var route = _routes.Find(r =>
                string.Equals(r.Method, request.Method.Method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Path, uri.AbsolutePath, StringComparison.OrdinalIgnoreCase)
                && r.Query == query);
            if (route is null)
            {
                var description = $"{request.Method} {uri}";
                _unmatched.Add(description);
                throw new FixtureMismatchException($"No fixture for {description}");
            }

            response = route.Responses[Math.Min(route.Used, route.Responses.Count - 1)];
            route.Used++;
        }

        return Task.FromResult(Build(response, request));
    }

    private static HttpResponseMessage Build(JsonObject response, HttpRequestMessage request)
    {
        switch (response["throw"]?.GetValue<string>())
        {
            case "http":
                throw new HttpRequestException(response["message"]?.GetValue<string>() ?? "connection failed");
            case "timeout":
                throw new TaskCanceledException(
                    response["message"]?.GetValue<string>() ?? "timed out", new TimeoutException());
            case null:
                break;
            case var other:
                throw new ArgumentException($"Unknown fixture exception '{other}'.");
        }

        var text = response["body_text"]?.GetValue<string>()
            ?? response["body"]?.ToJsonString(new JsonSerializerOptions { WriteIndented = false })
            ?? string.Empty;
        var message = new HttpResponseMessage((HttpStatusCode)response["status"]!.GetValue<int>())
        {
            RequestMessage = request,
            Content = new StringContent(text, Encoding.UTF8, "application/json"),
        };

        foreach (var (name, value) in response["headers"]?.AsObject() ?? [])
        {
            var headerValue = value!.GetValue<string>();
            if (!message.Headers.TryAddWithoutValidation(name, headerValue))
            {
                message.Content.Headers.Remove(name);
                message.Content.Headers.TryAddWithoutValidation(name, headerValue);
            }
        }

        return message;
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseQuery(string query) =>
        query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            var key = equals < 0 ? part : part[..equals];
            var value = equals < 0 ? string.Empty : part[(equals + 1)..];
            return new KeyValuePair<string, string>(Uri.UnescapeDataString(key), Uri.UnescapeDataString(value));
        });

    private static string Canonical(IEnumerable<KeyValuePair<string, string>> query) =>
        string.Join('&', query
            .Select(p => $"{p.Key}={p.Value}")
            .Order(StringComparer.Ordinal));

    private sealed class Route(string method, string path, string query, List<JsonObject> responses)
    {
        public string Method { get; } = method;

        public string Path { get; } = path;

        public string Query { get; } = query;

        public List<JsonObject> Responses { get; } = responses;

        public int Used { get; set; }
    }
}

internal sealed class FixtureMismatchException(string message) : Exception(message);
