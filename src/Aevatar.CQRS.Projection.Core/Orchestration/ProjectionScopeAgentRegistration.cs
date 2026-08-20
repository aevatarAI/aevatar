using System.Reflection;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal static class ProjectionScopeAgentRegistration
{
    public static AgentRegistration Create<TScopeAgent>()
        where TScopeAgent : IAgent
    {
        var scopeAgentType = typeof(TScopeAgent);
        if (!scopeAgentType.IsGenericType &&
            scopeAgentType.GetCustomAttribute<GAgentAttribute>(inherit: false) != null)
        {
            return AgentRegistration.FromAgentType(scopeAgentType);
        }

        return new AgentRegistration(
            Kind: BuildKind(scopeAgentType),
            ImplementationType: scopeAgentType,
            StateContractType: typeof(ProjectionScopeState));
    }

    private static string BuildKind(Type scopeAgentType)
    {
        if (!scopeAgentType.IsGenericType)
            return "projection.scope";

        var definition = scopeAgentType.GetGenericTypeDefinition();
        var contextType = scopeAgentType.GetGenericArguments()[0];
        var contextToken = ToKindToken(contextType.Name);

        if (definition == typeof(ProjectionSessionScopeGAgent<>))
            return $"projection.session-scope.{contextToken}";

        if (definition == typeof(ProjectionMaterializationScopeGAgent<>))
            return $"projection.materialization-scope.{contextToken}";

        return $"projection.scope.{contextToken}";
    }

    private static string ToKindToken(string name)
    {
        var token = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && token.Length > 0 && token[^1] != '-')
                    token.Append('-');

                token.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsLetterOrDigit(c))
            {
                token.Append(char.ToLowerInvariant(c));
            }
            else if (token.Length > 0 && token[^1] != '-')
            {
                token.Append('-');
            }
        }

        return token.ToString().Trim('-');
    }
}
