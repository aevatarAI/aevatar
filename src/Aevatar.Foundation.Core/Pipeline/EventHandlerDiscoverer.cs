using System.Collections.Concurrent;
using System.Reflection;
using Aevatar.Foundation.Abstractions.Attributes;
using Google.Protobuf;

namespace Aevatar.Foundation.Core.Pipeline;

internal sealed class EventHandlerMetadata
{
    public required MethodInfo Method { get; init; }
    public required Type ParameterType { get; init; }
    public required bool IsAllEventHandler { get; init; }
    public required int Priority { get; init; }
    public required bool AllowSelfHandling { get; init; }
    public required bool OnlySelfHandling { get; init; }
}

internal static class EventHandlerDiscoverer
{
    private static readonly ConcurrentDictionary<Type, EventHandlerMetadata[]> Cache = new();

    public static EventHandlerMetadata[] Discover(Type agentType) =>
        Cache.GetOrAdd(agentType, DiscoverCore);

    private static EventHandlerMetadata[] DiscoverCore(Type agentType)
    {
        var handlers = new List<EventHandlerMetadata>();
        var seen = new HashSet<string>();

        for (var type = agentType; type != null && type != typeof(object); type = type.BaseType)
        {
            var methods = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                var sig = $"{method.GetBaseDefinition().DeclaringType?.FullName}.{method.GetBaseDefinition().Name}";
                if (!seen.Add(sig)) continue;
                var metadata = TryBuild(method);
                if (metadata != null) handlers.Add(metadata);
            }
        }

        handlers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return handlers.ToArray();
    }

    private static EventHandlerMetadata? TryBuild(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length != 1) return null;
        var paramType = parameters[0].ParameterType;

        var ehAttr = method.GetCustomAttribute<EventHandlerAttribute>();
        if (ehAttr != null && typeof(IMessage).IsAssignableFrom(paramType))
            return new EventHandlerMetadata
            {
                Method = method, ParameterType = paramType, IsAllEventHandler = false,
                Priority = ehAttr.Priority, AllowSelfHandling = ehAttr.AllowSelfHandling,
                OnlySelfHandling = ehAttr.OnlySelfHandling,
            };

        var allAttr = method.GetCustomAttribute<AllEventHandlerAttribute>();
        if (allAttr != null && paramType == typeof(EventEnvelope))
            return new EventHandlerMetadata
            {
                Method = method, ParameterType = paramType, IsAllEventHandler = true,
                Priority = allAttr.Priority, AllowSelfHandling = allAttr.AllowSelfHandling,
                OnlySelfHandling = false,
            };

        if (method.Name is "HandleAsync" or "HandleEventAsync" &&
            typeof(IMessage).IsAssignableFrom(paramType) && !paramType.IsAbstract)
            return new EventHandlerMetadata
            {
                Method = method, ParameterType = paramType, IsAllEventHandler = false,
                Priority = 0, AllowSelfHandling = false, OnlySelfHandling = false,
            };

        return null;
    }
}
