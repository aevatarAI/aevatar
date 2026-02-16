using Aevatar.Foundation.Abstractions;

namespace Aevatar.AI.Abstractions.Agents;

public interface IRoleAgentTypeResolver
{
    Type ResolveRoleAgentType();
}
