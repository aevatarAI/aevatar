using System.Collections.Frozen;

namespace Aevatar.AI.Abstractions.ToolProviders;

public sealed class ExactAgentToolSet
{
    private ExactAgentToolSet(
        IReadOnlyDictionary<string, IAgentTool> toolsByName,
        IReadOnlySet<string> collisionNames)
    {
        ToolsByName = toolsByName;
        CollisionNames = collisionNames;
    }

    public IReadOnlyDictionary<string, IAgentTool> ToolsByName { get; }

    public IReadOnlySet<string> CollisionNames { get; }

    public static ExactAgentToolSet Create(IEnumerable<IAgentTool>? tools)
    {
        var exact = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools ?? [])
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
                continue;

            var name = tool.Name.Trim();
            if (collisions.Contains(name))
                continue;
            if (!exact.TryGetValue(name, out var existing))
            {
                exact.Add(name, tool);
                continue;
            }
            if (ReferenceEquals(existing, tool))
                continue;

            exact.Remove(name);
            collisions.Add(name);
        }

        return new ExactAgentToolSet(
            exact.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            collisions.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
    }
}
