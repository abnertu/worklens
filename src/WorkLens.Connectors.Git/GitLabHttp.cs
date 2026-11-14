using System.Net.Http.Headers;
using WorkLens.Core.Evidence;

namespace WorkLens.Connectors.Git;

/// <summary>Builds the <see cref="HttpClient"/> of one GitLab source (one client per source, no factory).</summary>
public static class GitLabHttp
{
    public const string MediaType = "application/json";
    public const string TokenHeader = "PRIVATE-TOKEN";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Creates a client whose <see cref="HttpClient.BaseAddress"/> is the normalized <paramref name="baseUrl"/> plus
    /// <c>/</c>, so relative request paths keep the API prefix, such as <c>/gitlab/api/v4</c> under a relative URL
    /// root. The handler is owned by the caller and is not disposed with the client.
    /// </summary>
    /// <param name="handler">For the CLI a <see cref="SocketsHttpHandler"/> with <c>AllowAutoRedirect = false</c>.</param>
    /// <param name="version">WorkLens version, sent as <c>User-Agent: worklens/&lt;version&gt;</c>.</param>
    public static HttpClient CreateClient(HttpMessageHandler handler, Uri baseUrl, string token, string version)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(EvidenceIds.NormalizeBaseUrl(baseUrl.AbsoluteUri) + "/"),
            Timeout = Timeout,
        };

        var headers = client.DefaultRequestHeaders;
        headers.UserAgent.Add(new ProductInfoHeaderValue("worklens", version));
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaType));
        headers.Add(TokenHeader, token);
        return client;
    }
}
