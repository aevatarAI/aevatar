namespace Aevatar.Tools.Cli.Studio.Domain.Models;

public sealed record StepErrorPolicy
{
    public string Strategy { get; init; } = "fail";

    public string? FallbackStep { get; init; }

    public string? DefaultOutput { get; init; }
}
