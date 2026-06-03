using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Compatibility;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Pipeline;

internal sealed class StaticHandlerAdapter : IEventModule<IEventHandlerContext>
{
    private readonly EventHandlerMetadata _meta;
    private readonly IAgent _agent;
    private readonly Func<Any, IMessage>? _unpacker;
    private readonly Func<IAgent, object, Task> _handler;
    private static readonly ConcurrentDictionary<System.Type, Func<Any, IMessage>?> UnpackerCache = new();

    public StaticHandlerAdapter(EventHandlerMetadata meta, IAgent agent)
    {
        _meta = meta;
        _agent = agent;
        _unpacker = meta.IsAllEventHandler ? null : UnpackerCache.GetOrAdd(meta.ParameterType, CompileUnpacker);
        _handler = CompileHandler(meta.Method, meta.ParameterType);
    }

    public string Name => _meta.Method.Name;
    public int Priority => _meta.Priority;

    public bool CanHandle(EventEnvelope envelope)
    {
        if (_meta.IsAllEventHandler) return true;
        if (!_meta.AllowSelfHandling && envelope.Route?.PublisherActorId == _agent.Id) return false;
        if (_meta.OnlySelfHandling && envelope.Route.GetTopologyAudience() != TopologyAudience.Self) return false;
        if (envelope.Payload == null) return false;
        return ProtobufContractCompatibility.MatchesPayload(envelope.Payload, _meta.ParameterType);
    }

    public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        object? arg = _meta.IsAllEventHandler ? envelope : Unpack(envelope);
        return arg == null ? Task.CompletedTask : _handler(_agent, arg);
    }

    private object? Unpack(EventEnvelope envelope)
    {
        if (_unpacker == null || envelope.Payload == null) return null;
        try { return _unpacker(envelope.Payload); }
        catch { return null; }
    }

    private static Func<Any, IMessage>? CompileUnpacker(System.Type messageType)
    {
        try
        {
            var unpack = typeof(StaticHandlerAdapter).GetMethod(
                nameof(UnpackCompatible),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (unpack == null)
                return null;

            var p = Expression.Parameter(typeof(Any), "any");
            var call = Expression.Call(unpack.MakeGenericMethod(messageType), p);
            return Expression.Lambda<Func<Any, IMessage>>(Expression.Convert(call, typeof(IMessage)), p).Compile();
        }
        catch { return null; }
    }

    // Refactor (iter11/cluster-020):
    // Old: per-message handler dispatch used MethodInfo.Invoke, allocating an argument array and wrapping exceptions.
    // New: adapter construction compiles a direct typed call delegate; HandleAsync only invokes the cached delegate.
    private static Func<IAgent, object, Task> CompileHandler(MethodInfo method, System.Type parameterType)
    {
        var agent = Expression.Parameter(typeof(IAgent), "agent");
        var payload = Expression.Parameter(typeof(object), "payload");
        var target = Expression.Convert(agent, method.DeclaringType ?? throw new InvalidOperationException(
            $"Handler '{method.Name}' has no declaring type."));
        var argument = Expression.Convert(payload, parameterType);
        var call = Expression.Call(target, method, argument);

        Expression body = typeof(Task).IsAssignableFrom(method.ReturnType)
            ? Expression.Convert(call, typeof(Task))
            : Expression.Block(call, Expression.Property(null, typeof(Task), nameof(Task.CompletedTask)));

        return Expression.Lambda<Func<IAgent, object, Task>>(body, agent, payload).Compile();
    }

    private static IMessage UnpackCompatible<TMessage>(Any any)
        where TMessage : class, IMessage<TMessage>, new()
    {
        if (ProtobufContractCompatibility.TryUnpack<TMessage>(any, out var message) &&
            message != null)
        {
            return message;
        }

        throw new InvalidOperationException(
            $"Could not unpack payload '{any.TypeUrl}' into '{typeof(TMessage).FullName}'.");
    }
}
