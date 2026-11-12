using System.Globalization;
using System.Net;
using WorkLens.Core.Errors;

namespace WorkLens.Connectors.Git;

/// <summary>
/// Source error codes (docs/design/cli-ux.md). Messages carry the method, the redacted path and the status only,
/// never a response body.
/// </summary>
public static class SourceErrors
{
    public const string Unauthorized = "source.unauthorized";
    public const string Forbidden = "source.forbidden";
    public const string NotFound = "source.not_found";
    public const string Moved = "source.moved";
    public const string RequestFailed = "source.request_failed";
    public const string RateLimited = "source.rate_limited";
    public const string ServerError = "source.server_error";
    public const string Unreachable = "source.unreachable";
    public const string InvalidResponse = "source.invalid_response";
    public const string Truncated = "source.truncated";

    public static WorkLensError Create(
        string code, string method, string path, HttpStatusCode? status, string detail, string? location = null)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = method,
            ["path"] = path,
        };

        var text = $"{method} {path}";
        if (status is { } value)
        {
            var code3 = ((int)value).ToString(CultureInfo.InvariantCulture);
            details["status"] = code3;
            text += $" returned {code3}";
        }

        if (location is not null)
        {
            details["location"] = location;
            text += $" (Location: {location})";
        }

        return new WorkLensError(code, $"{text}: {detail}", details);
    }
}
