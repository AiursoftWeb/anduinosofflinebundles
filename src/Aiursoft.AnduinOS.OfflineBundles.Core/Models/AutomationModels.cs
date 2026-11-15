namespace Aiursoft.AnduinOS.OfflineBundles.Core.Models;

public sealed record AutomationSettings
{
    public required string ServiceUrl { get; init; }
    public required string Bucket { get; init; }
    public required string StateDirectory { get; init; }
    public required SigningOptions Signing { get; init; }
    public string? TrustedKeyringSha256 { get; init; }
    public int IntervalHours { get; init; } = 48;
    public int RetryMinutes { get; init; } = 60;
    public required List<AutomationJob> Jobs { get; init; }
}

public sealed record AutomationJob(string Id, string Profile, string Inputs, string Summary);
public sealed record JobSchedule(DateTimeOffset NextAttemptUtc, string? PendingFile = null);
public sealed record AutomationState
{
    public Dictionary<string, JobSchedule> Jobs { get; init; } = new(StringComparer.Ordinal);
}
