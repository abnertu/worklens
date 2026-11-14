namespace WorkLens.Tests.Shared;

/// <summary>A clock that always returns <paramref name="now"/>, in an optional local time zone (default UTC).</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now, TimeZoneInfo? localTimeZone = null) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone { get; } = localTimeZone ?? TimeZoneInfo.Utc;
}
