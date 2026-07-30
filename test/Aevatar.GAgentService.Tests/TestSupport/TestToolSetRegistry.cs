using Aevatar.AI.ToolProviders.ToolSetRegistry;

namespace Aevatar.GAgentService.Tests.TestSupport;

/// <summary>
/// Configurable <see cref="IToolSetRegistry"/> test double. <see cref="Empty"/> resolves every name
/// to a failure (no tool sets), which is the right default for run-core tests whose commands carry no
/// <c>tool_set_name</c>; pass a custom resolver to exercise success / throw paths.
/// </summary>
internal sealed class TestToolSetRegistry(Func<string, ToolSetResolveResult> resolver) : IToolSetRegistry
{
    public static IToolSetRegistry Empty { get; } = new TestToolSetRegistry(name =>
        ToolSetResolveResult.Failure(new ToolSetResolveError(
            ToolSetResolveError.UnknownNameCode, name, "no tool sets in test", [])));

    public IReadOnlyList<string> GetRegisteredNames() => [];

    public ToolSetResolveResult Resolve(string? name) =>
        resolver(name?.Trim() ?? string.Empty);
}
