using Aevatar.AI.Abstractions.Agents;

namespace Aevatar.AI.Core.Agents;

/// <summary>
/// Default role agent resolver for hosts that use <see cref="RoleGAgent"/>.
/// </summary>
public sealed class RoleGAgentTypeResolver : IRoleAgentTypeResolver
{
    public Type ResolveRoleAgentType() => typeof(RoleGAgent);
}
