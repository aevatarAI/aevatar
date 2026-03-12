namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptingInteractionTimeoutOptions
{
    private static readonly TimeSpan DefaultEvolutionCompletionTimeout = TimeSpan.FromSeconds(90);

    public TimeSpan EvolutionCompletionTimeout { get; init; } = DefaultEvolutionCompletionTimeout;

    public TimeSpan ResolveEvolutionCompletionTimeout() =>
        ScriptingTimeoutValueNormalizer.NormalizeOrDefault(
            EvolutionCompletionTimeout,
            DefaultEvolutionCompletionTimeout);
}
