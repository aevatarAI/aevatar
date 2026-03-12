using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Execution;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using System.Reflection;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace Aevatar.Workflow.Core.Modules;

public sealed class ActorSendModule : IEventModule<IWorkflowExecutionContext>
{
    public string Name => "actor_send";

    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        if (envelope.Payload?.Is(StepRequestEvent.Descriptor) != true)
            return;

        var request = envelope.Payload.Unpack<StepRequestEvent>();
        if (request.StepType != Name)
            return;

        var sendStateKey = request.Parameters.GetValueOrDefault("send_state_key", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sendStateKey))
        {
            await PublishFailureAsync(request, ctx, "actor_send missing send_state_key", ct);
            return;
        }

        var sendState = WorkflowExecutionStateAccess.Load<ActorSendState>(ctx, sendStateKey);
        if (string.IsNullOrWhiteSpace(sendState.TargetActorId))
        {
            await PublishFailureAsync(request, ctx, $"actor_send state '{sendStateKey}' missing target_actor_id", ct);
            return;
        }

        if (sendState.Payload == null)
        {
            await PublishFailureAsync(request, ctx, $"actor_send state '{sendStateKey}' missing payload", ct);
            return;
        }

        var message = Unpack(sendState.Payload);
        await ctx.SendToAsync(sendState.TargetActorId, message, ct);

        var completion = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = sendState.TargetActorId,
        };
        completion.Annotations["actor.target_actor_id"] = sendState.TargetActorId;
        completion.Annotations["actor.payload_type_url"] = sendState.Payload.TypeUrl ?? string.Empty;
        await ctx.PublishAsync(completion, BroadcastDirection.Self, ct);
    }

    private static Task PublishFailureAsync(
        StepRequestEvent request,
        IWorkflowExecutionContext ctx,
        string error,
        CancellationToken ct) =>
        ctx.PublishAsync(
            new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = false,
                Error = error,
            },
            BroadcastDirection.Self,
            ct);

    private static IMessage Unpack(Any packed)
    {
        ArgumentNullException.ThrowIfNull(packed);

        var typeUrl = packed.TypeUrl ?? string.Empty;
        var typeName = typeUrl.StartsWith("type.googleapis.com/", StringComparison.Ordinal)
            ? typeUrl["type.googleapis.com/".Length..]
            : typeUrl;
        if (string.IsNullOrWhiteSpace(typeName))
            throw new InvalidOperationException("actor_send payload type_url is required.");

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(static type => type != null).Cast<Type>().ToArray();
            }

            foreach (var type in types)
            {
                if (!typeof(IMessage).IsAssignableFrom(type))
                    continue;

                var descriptorProperty = type.GetProperty(
                    nameof(Any.Descriptor),
                    BindingFlags.Public | BindingFlags.Static);
                if (descriptorProperty?.GetValue(null) is not MessageDescriptor descriptor)
                    continue;

                if (!string.Equals(descriptor.FullName, typeName, StringComparison.Ordinal))
                    continue;

                return descriptor.Parser.ParseFrom(packed.Value);
            }
        }

        throw new InvalidOperationException($"actor_send cannot resolve protobuf message for '{typeUrl}'.");
    }
}
