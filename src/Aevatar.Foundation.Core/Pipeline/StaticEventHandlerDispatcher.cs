using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Google.Protobuf;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace Aevatar.Foundation.Core.Pipeline;

internal static class StaticEventHandlerDispatcher
{
    private static readonly ConcurrentDictionary<System.Type, Func<Any, IMessage>?> UnpackerCache = new();

    public static bool CanHandle(EventHandlerMetadata metadata, IAgent agent, EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(envelope);

        if (metadata.IsAllEventHandler)
            return true;

        if (!metadata.AllowSelfHandling && envelope.PublisherId == agent.Id)
            return false;

        if (metadata.OnlySelfHandling && envelope.Direction != EventDirection.Self)
            return false;

        if (envelope.Payload == null)
            return false;

        return envelope.Payload.TypeUrl == GetTypeUrl(metadata.ParameterType);
    }

    public static async Task InvokeAsync(
        EventHandlerMetadata metadata,
        IAgent agent,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(envelope);

        _ = ct;

        object? arg = metadata.IsAllEventHandler ? envelope : Unpack(metadata.ParameterType, envelope);
        if (arg == null)
            return;

        try
        {
            var result = metadata.Method.Invoke(agent, [arg]);
            if (result is Task task)
                await task;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static object? Unpack(System.Type messageType, EventEnvelope envelope)
    {
        if (envelope.Payload == null)
            return null;

        var unpacker = UnpackerCache.GetOrAdd(messageType, CompileUnpacker);
        if (unpacker == null)
            return null;

        try
        {
            return unpacker(envelope.Payload);
        }
        catch
        {
            return null;
        }
    }

    private static string GetTypeUrl(System.Type type)
    {
        var descriptorProperty = type.GetProperty(
            "Descriptor",
            BindingFlags.Public | BindingFlags.Static);
        if (descriptorProperty?.GetValue(null) is Google.Protobuf.Reflection.MessageDescriptor descriptor)
            return "type.googleapis.com/" + descriptor.FullName;

        return "type.googleapis.com/" + type.FullName;
    }

    private static Func<Any, IMessage>? CompileUnpacker(System.Type messageType)
    {
        try
        {
            var unpack = typeof(Any).GetMethods()
                .First(m => m.Name == nameof(Any.Unpack) &&
                            m.IsGenericMethodDefinition &&
                            m.GetParameters().Length == 0);
            var anyParameter = Expression.Parameter(typeof(Any), "any");
            var call = Expression.Call(anyParameter, unpack.MakeGenericMethod(messageType));
            return Expression.Lambda<Func<Any, IMessage>>(
                Expression.Convert(call, typeof(IMessage)),
                anyParameter).Compile();
        }
        catch
        {
            return null;
        }
    }
}
