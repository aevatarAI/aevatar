using Aevatar.Foundation.Abstractions;

namespace Aevatar.AI.Abstractions.Agents;

public interface IRoleAgentTypeResolver
{
    Type ResolveRoleAgentType();
}

public sealed class ReflectionRoleAgentTypeResolver : IRoleAgentTypeResolver
{
    private const string DefaultRoleAgentType = "Aevatar.AI.Core.RoleGAgent, Aevatar.AI.Core";

    public Type ResolveRoleAgentType()
    {
        var resolved = Type.GetType(DefaultRoleAgentType, throwOnError: false);
        if (resolved == null)
            throw new InvalidOperationException(
                $"Unable to resolve role agent type '{DefaultRoleAgentType}'. " +
                "Ensure Aevatar.AI.Core is referenced by the host.");

        if (!typeof(IAgent).IsAssignableFrom(resolved))
            throw new InvalidOperationException(
                $"Resolved role agent type '{resolved.FullName}' does not implement IAgent.");

        return resolved;
    }
}
