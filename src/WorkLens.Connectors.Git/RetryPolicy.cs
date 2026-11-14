using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace WorkLens.Connectors.Git;

public enum RetryAction
{
    /// <summary>Wait <see cref="RetryDecision.Delay"/>, then send the request again.</summary>
    Retry,

    /// <summary>Stop with <see cref="RetryDecision.ErrorCode"/>.</summary>
    Fail,
}

public readonly record struct RetryDecision(RetryAction Action, TimeSpan Delay, string? ErrorCode, string Reason)
{
    public static RetryDecision Wait(TimeSpan delay, string reason) => new(RetryAction.Retry, delay, null, reason);

    public static RetryDecision Stop(string code, string reason) => new(RetryAction.Fail, TimeSpan.Zero, code, reason);
}

/// <summary>The platform whose rate-limit rules <see cref="RetryPolicy"/> applies.</summary>
public enum RetryPlatform
{
    GitHub,
    GitLab,
}

/// <summary>Attempt counters of one logical request; <see cref="RetryPolicy"/> updates them.</summary>
public sealed class RetryState
{
    public int RateLimitWaits { get; internal set; }

    public int Retries { get; internal set; }
}

/// <summary>
/// Retry and rate-limit rules (plan D22). For 403 / 429, in order: ① <c>x-ratelimit-remaining: 0</c> waits until
/// <c>x-ratelimit-reset</c>; ② <c>Retry-After</c> (seconds or HTTP-date) waits that long; ③ a secondary rate limit
/// (body mentions it, or any other 429) waits 60 seconds doubling per wait; ④ any other 403 is
/// <c>source.forbidden</c>. A single wait is at most <see cref="MaxWait"/> and one request waits at most
/// <see cref="MaxRateLimitWaits"/> times, otherwise <c>source.rate_limited</c>. 5xx and transport failures
/// (connection errors, timeouts) back off 1 / 2 / 4 seconds (5xx honours <c>Retry-After</c>) for at most
/// <see cref="MaxRetries"/> retries. 2xx responses never reach this policy.
/// <para>
/// <see cref="RetryPlatform.GitLab"/> replaces the 403 / 429 rules: 403 is always <c>source.forbidden</c>; 429 waits
/// for <c>Retry-After</c>, else until <c>RateLimit-Reset</c>, else 60 seconds doubling per wait. The wait limits
/// and the 5xx / transport rules are shared.
/// </para>
/// </summary>
public sealed class RetryPolicy
{
    public const int MaxRateLimitWaits = 3;
    public const int MaxRetries = 3;
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan SecondaryLimitWait = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly RetryPlatform _platform;

    /// <param name="delay">Performs a wait; tests record it and return at once.</param>
    /// <param name="platform">Selects the 403 / 429 rules; GitHub by default.</param>
    public RetryPolicy(
        TimeProvider time, Func<TimeSpan, CancellationToken, Task> delay, RetryPlatform platform = RetryPlatform.GitHub)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _platform = Enum.IsDefined(platform) ? platform : throw new ArgumentOutOfRangeException(nameof(platform));
    }

    public RetryPlatform Platform => _platform;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => _delay(delay, cancellationToken);

    /// <summary>The decision for a 403, 429 or 5xx response; <c>null</c> for any other status.</summary>
    public RetryDecision? ForResponse(
        HttpStatusCode status, HttpResponseHeaders headers, string body, RetryState state)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(state);
        var code = (int)status;

        if (code is 403 or 429)
        {
            return _platform == RetryPlatform.GitLab
                ? ForGitLabRateLimit(code, headers, state)
                : ForRateLimit(code, headers, body, state);
        }

        if (code is >= 500 and <= 599)
        {
            if (state.Retries >= MaxRetries)
            {
                return RetryDecision.Stop(SourceErrors.ServerError, $"server error persisted after {MaxRetries} retries");
            }

            var wait = RetryAfter(headers) ?? Backoff(state.Retries);
            if (wait > MaxWait)
            {
                return RetryDecision.Stop(SourceErrors.ServerError, $"server asked to retry after {Seconds(wait)} seconds");
            }

            state.Retries++;
            return RetryDecision.Wait(wait, "server error");
        }

        return null;
    }

    /// <summary>The decision for a connection failure or a timeout (not a caller cancellation).</summary>
    public RetryDecision ForTransportFailure(RetryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Retries >= MaxRetries)
        {
            return RetryDecision.Stop(SourceErrors.Unreachable, $"request failed after {MaxRetries} retries");
        }

        var wait = Backoff(state.Retries);
        state.Retries++;
        return RetryDecision.Wait(wait, "connection failed or timed out");
    }

    private RetryDecision ForRateLimit(int code, HttpResponseHeaders headers, string body, RetryState state)
    {
        TimeSpan wait;
        string reason;
        if (Header(headers, "x-ratelimit-remaining") == "0" && ResetWait(headers, "x-ratelimit-reset") is { } reset)
        {
            (wait, reason) = (reset, "primary rate limit");
        }
        else if (RetryAfter(headers) is { } retryAfter)
        {
            (wait, reason) = (retryAfter, "rate limited (Retry-After)");
        }
        else if (code == 429 || body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase))
        {
            (wait, reason) = (SecondaryLimitWait * Math.Pow(2, state.RateLimitWaits), "secondary rate limit");
        }
        else
        {
            return RetryDecision.Stop(SourceErrors.Forbidden, "access denied");
        }

        return LimitedWait(wait, reason, state);
    }

    private RetryDecision ForGitLabRateLimit(int code, HttpResponseHeaders headers, RetryState state)
    {
        if (code == 403)
        {
            return RetryDecision.Stop(SourceErrors.Forbidden, "access denied; check the token scope and project access");
        }

        var (wait, reason) = RetryAfter(headers) is { } retryAfter
            ? (retryAfter, "rate limited (Retry-After)")
            : ResetWait(headers, "ratelimit-reset") is { } reset
                ? (reset, "rate limited (RateLimit-Reset)")
                : (SecondaryLimitWait * Math.Pow(2, state.RateLimitWaits), "rate limited");
        return LimitedWait(wait, reason, state);
    }

    private static RetryDecision LimitedWait(TimeSpan wait, string reason, RetryState state)
    {
        if (state.RateLimitWaits >= MaxRateLimitWaits)
        {
            return RetryDecision.Stop(SourceErrors.RateLimited, $"{reason}; still limited after {MaxRateLimitWaits} waits");
        }

        if (wait > MaxWait)
        {
            return RetryDecision.Stop(
                SourceErrors.RateLimited, $"{reason}; the wait of {Seconds(wait)} seconds exceeds {Seconds(MaxWait)}");
        }

        state.RateLimitWaits++;
        return RetryDecision.Wait(wait, reason);
    }

    private TimeSpan? ResetWait(HttpResponseHeaders headers, string name)
    {
        if (!long.TryParse(Header(headers, name), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            || epoch > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return null;
        }

        return NotNegative(DateTimeOffset.FromUnixTimeSeconds(epoch) - _time.GetUtcNow());
    }

    private TimeSpan? RetryAfter(HttpResponseHeaders headers) => headers.RetryAfter switch
    {
        { Delta: { } delta } => NotNegative(delta),
        { Date: { } date } => NotNegative(date - _time.GetUtcNow()),
        _ => null,
    };

    private static TimeSpan Backoff(int retries) => TimeSpan.FromSeconds(Math.Pow(2, retries));

    private static TimeSpan NotNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static string Seconds(TimeSpan value) =>
        Math.Ceiling(value.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    private static string? Header(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;
}
