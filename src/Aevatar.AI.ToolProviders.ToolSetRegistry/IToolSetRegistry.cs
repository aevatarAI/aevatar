using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.ChatRouting.Abstractions;

namespace Aevatar.AI.ToolProviders.ToolSetRegistry;

public interface IToolSetRegistry
{
    IReadOnlyList<string> GetRegisteredNames();

    ToolSetResolveResult Resolve(string? name);
}

public static class ToolSetRegistryExtensions
{
    public static ToolSetResolveResult Resolve(
        this IToolSetRegistry registry,
        ChatRouteToolSetRef? toolSetRef)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Resolve(toolSetRef?.Name);
    }
}

public sealed class ToolSetResolveResult
{
    private ToolSetResolveResult(
        bool isSuccess,
        string? name,
        IReadOnlyList<IAgentToolSource> sources,
        ToolSetResolveError? error)
    {
        IsSuccess = isSuccess;
        Name = name;
        Sources = sources;
        Error = error;
    }

    public bool IsSuccess { get; }

    public string? Name { get; }

    public IReadOnlyList<IAgentToolSource> Sources { get; }

    public ToolSetResolveError? Error { get; }

    public static ToolSetResolveResult Success(
        string name,
        IReadOnlyList<IAgentToolSource> sources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(sources);
        return new ToolSetResolveResult(true, name.Trim(), sources, null);
    }

    public static ToolSetResolveResult Failure(ToolSetResolveError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ToolSetResolveResult(false, error.Name, [], error);
    }
}

public sealed record ToolSetResolveError(
    string Code,
    string Name,
    string Message,
    IReadOnlyList<string> RegisteredNames)
{
    public const string EmptyNameCode = "tool_set_name_required";
    public const string UnknownNameCode = "unknown_tool_set";
}
