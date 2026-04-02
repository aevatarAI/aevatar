namespace Aevatar.Studio.Domain.Studio.Models;

public sealed record StepRetryPolicy
{
    public int MaxAttempts { get; init; } = 3;

    public string Backoff { get; init; } = "fixed";

    public int DelayMs { get; init; } = 1000;
}
