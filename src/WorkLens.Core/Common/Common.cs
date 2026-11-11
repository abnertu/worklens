namespace WorkLens.Core.Common;

/// <summary>Source platform of a work system.</summary>
public enum SourcePlatform
{
    Github,
    Gitlab,
}

/// <summary>Who produced a piece of report content.</summary>
public enum ContentOrigin
{
    Agent,
    Human,
}

/// <summary>
/// Reporting period. <see cref="Start"/> is inclusive and <see cref="End"/> is exclusive; both are UTC instants
/// computed from month boundaries in <see cref="Timezone"/> (an IANA time zone name).
/// </summary>
public sealed record Period(string Label, DateTimeOffset Start, DateTimeOffset End, string Timezone);
