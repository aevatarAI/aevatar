using Type = System.Type;

namespace Aevatar.GAgentService.Application.ScopeGAgents;

public static class ScopeGAgentActorTypeResolver
{
    public static Type? Resolve(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        var type = Type.GetType(typeName, throwOnError: false);
        if (type is not null)
            return type;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;

            try
            {
                type = assembly.GetType(typeName);
                if (type is not null)
                    return type;
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }
}
