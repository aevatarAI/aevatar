namespace Aevatar.Studio.Domain.Studio.Models;

public sealed record StepErrorPolicy
{
    public string Strategy { get; init; } = "fail";

    public string? FallbackStep { get; init; }

    public string? DefaultOutput { get; init; }
}
