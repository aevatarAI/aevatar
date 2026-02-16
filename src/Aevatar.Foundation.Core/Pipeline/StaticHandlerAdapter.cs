using System.Collections.Concurrent;
using System.Linq.Expressions;
using Aevatar.Foundation.Abstractions.EventModules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Pipeline;

internal sealed class StaticHandlerAdapter : IEventModule
{
    private readonly EventHandlerMetadata _meta;
    private readonly IAgent _agent;
    private readonly Func<Any, IMessage>? _unpacker;
    private static readonly ConcurrentDictionary<System.Type, Func<Any, IMessage>?> UnpackerCache = new();

    public StaticHandlerAdapter(EventHandlerMetadata meta, IAgent agent)
    {
        _meta = meta;
        _agent = agent;
        _unpacker = meta.IsAllEventHandler ? null : UnpackerCache.GetOrAdd(meta.ParameterType, CompileUnpacker);
    }

    public string Name => _meta.Method.Name;
    public int Priority => _meta.Priority;

    public bool CanHandle(EventEnvelope envelope)
    {
        if (_meta.IsAllEventHandler) return true;
        if (!_meta.AllowSelfHandling && envelope.PublisherId == _agent.Id) return false;
        if (_meta.OnlySelfHandling && envelope.Direction != EventDirection.Self) return false;
        if (envelope.Payload == null) return false;
        return envelope.Payload.TypeUrl == GetTypeUrl(_meta.ParameterType);
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        object? arg = _meta.IsAllEventHandler ? envelope : Unpack(envelope);
        if (arg == null) return;
        var result = _meta.Method.Invoke(_agent, [arg]);
        if (result is Task task) await task;
    }

    private object? Unpack(EventEnvelope envelope)
    {
        if (_unpacker == null || envelope.Payload == null) return null;
        try { return _unpacker(envelope.Payload); }
        catch { return null; }
    }

    private static string GetTypeUrl(System.Type type)
    {
        var prop = type.GetProperty("Descriptor",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (prop?.GetValue(null) is Google.Protobuf.Reflection.MessageDescriptor desc)
            return "type.googleapis.com/" + desc.FullName;
        return "type.googleapis.com/" + type.FullName;
    }

    private static Func<Any, IMessage>? CompileUnpacker(System.Type messageType)
    {
        try
        {
            var unpack = typeof(Any).GetMethods()
                .First(m => m.Name == nameof(Any.Unpack) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var p = Expression.Parameter(typeof(Any), "any");
            var call = Expression.Call(p, unpack.MakeGenericMethod(messageType));
            return Expression.Lambda<Func<Any, IMessage>>(Expression.Convert(call, typeof(IMessage)), p).Compile();
        }
        catch { return null; }
    }
}
