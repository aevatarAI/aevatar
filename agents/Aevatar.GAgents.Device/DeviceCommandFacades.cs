using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Household;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Device;

public sealed record DeviceCommandAcceptedReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId,
    string RegistrationId = "");

public enum DeviceRegistrationCommandStartError
{
    None = 0,
    StoreActorUnavailable = 1,
}

public enum DeviceCallbackCommandStartError
{
    None = 0,
    RegistrationNotFound = 1,
    RegistrationNotAdmitted = 2,
    TargetActorUnavailable = 3,
}

public interface IDeviceCallbackCommandService
{
    Task<CommandDispatchResult<DeviceCommandAcceptedReceipt, DeviceCallbackCommandStartError>> DispatchCallbackAsync(
        DeviceCallbackDispatchCommand command,
        CancellationToken ct = default);
}

public sealed record DeviceCallbackDispatchCommand(
    string RegistrationId,
    DeviceInbound Inbound,
    DeviceCallbackAdmission? Admission = null,
    string? CommandId = null,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, string>? Headers = null) : ICommandContextSeed
{
    string? ICommandContextSeed.CommandId => FirstNonEmpty(CommandId, Admission?.MessageId, Inbound?.EventId);

    string? ICommandContextSeed.CorrelationId => FirstNonEmpty(CorrelationId, CommandId, Admission?.MessageId, Inbound?.EventId);

    IReadOnlyDictionary<string, string>? ICommandContextSeed.Headers => Headers;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}

// Refactor (iter47/issue-873-device-endpoint-direct-runtime-dispatch):
//   Old pattern: Device HTTP endpoint resolves/creates actors, builds EventEnvelope directly, and dispatches through runtime/dispatch ports.
//   New principle: Endpoint delegates to typed application command facade; target resolution, envelope construction, dispatch receipt, and resource ownership live behind command skeleton contracts. No callback-time auto-create.
public sealed class DeviceRegistrationCommandFacade
{
    private readonly ICommandDispatchService<DeviceRegisterCommand, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError> _registerDispatchService;
    private readonly ICommandDispatchService<DeviceUnregisterCommand, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError> _unregisterDispatchService;

    public DeviceRegistrationCommandFacade(
        ICommandDispatchService<DeviceRegisterCommand, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError> registerDispatchService,
        ICommandDispatchService<DeviceUnregisterCommand, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError> unregisterDispatchService)
    {
        _registerDispatchService = registerDispatchService ?? throw new ArgumentNullException(nameof(registerDispatchService));
        _unregisterDispatchService = unregisterDispatchService ?? throw new ArgumentNullException(nameof(unregisterDispatchService));
    }

    public async Task<DeviceCommandAcceptedReceipt> RegisterAsync(
        DeviceRegisterCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = await _registerDispatchService.DispatchAsync(command, ct);
        return ResolveReceipt(result);
    }

    public async Task<DeviceCommandAcceptedReceipt> UnregisterAsync(
        string registrationId,
        CancellationToken ct = default)
    {
        var result = await _unregisterDispatchService.DispatchAsync(
            new DeviceUnregisterCommand
            {
                RegistrationId = registrationId ?? string.Empty,
            },
            ct);
        return ResolveReceipt(result);
    }

    private static DeviceCommandAcceptedReceipt ResolveReceipt(
        CommandDispatchResult<DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError> result)
    {
        if (result.Succeeded && result.Receipt is not null)
            return result.Receipt;

        throw new InvalidOperationException($"Device registration command dispatch failed: {result.Error}");
    }
}

// Refactor (iter47/issue-873-device-endpoint-direct-runtime-dispatch):
//   Old pattern: Device HTTP endpoint resolves/creates actors, builds EventEnvelope directly, and dispatches through runtime/dispatch ports.
//   New principle: Endpoint delegates to typed application command facade; target resolution, envelope construction, dispatch receipt, and resource ownership live behind command skeleton contracts. No callback-time auto-create.
public sealed class DeviceCallbackCommandFacade : IDeviceCallbackCommandService
{
    private readonly ICommandDispatchService<DeviceCallbackDispatchCommand, DeviceCommandAcceptedReceipt, DeviceCallbackCommandStartError> _dispatchService;

    public DeviceCallbackCommandFacade(
        ICommandDispatchService<DeviceCallbackDispatchCommand, DeviceCommandAcceptedReceipt, DeviceCallbackCommandStartError> dispatchService)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
    }

    public Task<CommandDispatchResult<DeviceCommandAcceptedReceipt, DeviceCallbackCommandStartError>> DispatchCallbackAsync(
        DeviceCallbackDispatchCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _dispatchService.DispatchAsync(command, ct);
    }
}

internal sealed record DeviceRegistrationCommandTarget(IActor Actor) : IActorCommandDispatchTarget
{
    public string TargetId => DeviceRegistrationGAgent.WellKnownId;
}

internal sealed class DeviceRegistrationCommandTargetResolver<TCommand>
    : ICommandTargetResolver<TCommand, DeviceRegistrationCommandTarget, DeviceRegistrationCommandStartError>
{
    private readonly IActorRuntime _actorRuntime;

    public DeviceRegistrationCommandTargetResolver(IActorRuntime actorRuntime)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
    }

    public async Task<CommandTargetResolution<DeviceRegistrationCommandTarget, DeviceRegistrationCommandStartError>> ResolveAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        var actor = await _actorRuntime.GetAsync(DeviceRegistrationGAgent.WellKnownId)
            ?? await _actorRuntime.CreateAsync<DeviceRegistrationGAgent>(
                DeviceRegistrationGAgent.WellKnownId,
                ct);

        return actor is null
            ? CommandTargetResolution<DeviceRegistrationCommandTarget, DeviceRegistrationCommandStartError>.Failure(DeviceRegistrationCommandStartError.StoreActorUnavailable)
            : CommandTargetResolution<DeviceRegistrationCommandTarget, DeviceRegistrationCommandStartError>.Success(new DeviceRegistrationCommandTarget(actor));
    }
}

internal sealed class DeviceRegistrationCommandEnvelopeFactory :
    ICommandEnvelopeFactory<DeviceRegisterCommand>,
    ICommandEnvelopeFactory<DeviceUnregisterCommand>
{
    private const string PublisherActorId = "device-events.registration";

    public EventEnvelope CreateEnvelope(DeviceRegisterCommand command, CommandContext context) =>
        CreateEnvelopeCore(command, context);

    public EventEnvelope CreateEnvelope(DeviceUnregisterCommand command, CommandContext context) =>
        CreateEnvelopeCore(command, context);

    private static EventEnvelope CreateEnvelopeCore(IMessage command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return new EventEnvelope
        {
            Id = context.CommandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, DeviceRegistrationGAgent.WellKnownId),
        };
    }
}

internal sealed class DeviceRegistrationCommandReceiptFactory
    : ICommandReceiptFactory<DeviceRegistrationCommandTarget, DeviceCommandAcceptedReceipt>
{
    public DeviceCommandAcceptedReceipt Create(
        DeviceRegistrationCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new DeviceCommandAcceptedReceipt(
            target.TargetId,
            context.CommandId,
            context.CorrelationId);
    }
}

internal sealed record DeviceCallbackCommandTarget(IActor Actor, string RegistrationId)
    : IActorCommandDispatchTarget
{
    public string TargetId => Actor.Id;
}

internal sealed class DeviceCallbackCommandTargetResolver
    : ICommandTargetResolver<DeviceCallbackDispatchCommand, DeviceCallbackCommandTarget, DeviceCallbackCommandStartError>
{
    private readonly IDeviceRegistrationQueryPort _queryPort;
    private readonly IActorRuntime _actorRuntime;

    public DeviceCallbackCommandTargetResolver(
        IDeviceRegistrationQueryPort queryPort,
        IActorRuntime actorRuntime)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
    }

    public async Task<CommandTargetResolution<DeviceCallbackCommandTarget, DeviceCallbackCommandStartError>> ResolveAsync(
        DeviceCallbackDispatchCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.RegistrationId))
        {
            return CommandTargetResolution<DeviceCallbackCommandTarget, DeviceCallbackCommandStartError>.Failure(
                DeviceCallbackCommandStartError.RegistrationNotFound);
        }

        var registration = await _queryPort.GetAsync(command.RegistrationId, ct);
        if (registration is null)
        {
            return CommandTargetResolution<DeviceCallbackCommandTarget, DeviceCallbackCommandStartError>.Failure(
                DeviceCallbackCommandStartError.RegistrationNotFound);
        }

        if (registration.Tombstoned || string.IsNullOrWhiteSpace(registration.DeviceEventTargetActorId))
        {
            return CommandTargetResolution<DeviceCallbackCommandTarget, DeviceCallbackCommandStartError>.Failure(
                DeviceCallbackCommandStartError.RegistrationNotAdmitted);
        }

        var actor = await _actorRuntime.GetAsync(registration.DeviceEventTargetActorId);
        if (actor is null)
        {
            return CommandTargetResolution<DeviceCallbackCommandTarget, DeviceCallbackCommandStartError>.Failure(
                DeviceCallbackCommandStartError.TargetActorUnavailable);
        }

        return CommandTargetResolution<DeviceCallbackCommandTarget, DeviceCallbackCommandStartError>.Success(
            new DeviceCallbackCommandTarget(actor, registration.Id ?? command.RegistrationId));
    }
}

internal sealed class DeviceCallbackCommandEnvelopeFactory
    : ICommandEnvelopeFactory<DeviceCallbackDispatchCommand>
{
    private const string PublisherActorId = "device-events.callback";

    public EventEnvelope CreateEnvelope(DeviceCallbackDispatchCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Inbound);
        ArgumentNullException.ThrowIfNull(context);

        // Refactor (issue1485/first-slice): Old pattern: adding a second callback envelope shape for typed device callbacks.
        // New principle: keep the existing command facade as the only dispatch skeleton.
        // DeviceInbound is packed once into the single EventEnvelope payload used by the actor system.
        // The endpoint must have already rejected unknown adapter event_type values; this facade only dispatches typed inbound payloads.
        // Payload typing changes at the message contract, not by adding a new transport abstraction.
        return new EventEnvelope
        {
            Id = context.CommandId,
            Timestamp = Timestamp.FromDateTimeOffset(command.Admission?.OccurredAt ?? DateTimeOffset.UtcNow),
            Payload = Any.Pack(command.Inbound),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, context.TargetId),
            Runtime = new EnvelopeRuntime
            {
                DeliveryIdentity = new DeliveryIdentity
                {
                    OperationId = ResolveOperationId(command, context),
                },
            },
        };
    }

    private static string ResolveOperationId(DeviceCallbackDispatchCommand command, CommandContext context)
    {
        if (!string.IsNullOrWhiteSpace(command.Admission?.OperationId))
            return command.Admission.OperationId;

        return context.CommandId;
    }
}

internal sealed class DeviceCallbackCommandReceiptFactory
    : ICommandReceiptFactory<DeviceCallbackCommandTarget, DeviceCommandAcceptedReceipt>
{
    public DeviceCommandAcceptedReceipt Create(
        DeviceCallbackCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new DeviceCommandAcceptedReceipt(
            target.TargetId,
            context.CommandId,
            context.CorrelationId,
            target.RegistrationId);
    }
}
