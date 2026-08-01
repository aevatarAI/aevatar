using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Studio.Projection.CommandServices;

// Refactor (iter56/cluster-911-studio-store-query-command):
//   old=Store mixed read/write + hand-built EventEnvelope
//   new=split query/command port + CQRS Core dispatch
internal sealed record StudioProjectionActorCommand(
    IActor Actor,
    IMessage Payload,
    string PublisherId,
    string? CommandId = null,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, string>? Headers = null) : ICommandContextSeed
{
    string? ICommandContextSeed.CommandId => CommandId;

    string? ICommandContextSeed.CorrelationId => CorrelationId;

    IReadOnlyDictionary<string, string>? ICommandContextSeed.Headers => Headers;
}

internal sealed class StudioProjectionActorCommandTarget(IActor actor) : IActorCommandDispatchTarget
{
    public IActor Actor { get; } = actor ?? throw new ArgumentNullException(nameof(actor));

    public string TargetId => Actor.Id;
}

internal sealed record StudioProjectionActorCommandReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId,
    DateTimeOffset? AckedAt = null);

internal sealed record StudioProjectionActorCommandStartError(string Message);

internal sealed class StudioProjectionActorCommandTargetResolver
    : ICommandTargetResolver<StudioProjectionActorCommand, StudioProjectionActorCommandTarget, StudioProjectionActorCommandStartError>
{
    public Task<CommandTargetResolution<StudioProjectionActorCommandTarget, StudioProjectionActorCommandStartError>> ResolveAsync(
        StudioProjectionActorCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Actor.Id))
        {
            return Task.FromResult(
                CommandTargetResolution<StudioProjectionActorCommandTarget, StudioProjectionActorCommandStartError>.Failure(
                    new StudioProjectionActorCommandStartError("Actor id is required.")));
        }

        return Task.FromResult(
            CommandTargetResolution<StudioProjectionActorCommandTarget, StudioProjectionActorCommandStartError>.Success(
                new StudioProjectionActorCommandTarget(command.Actor)));
    }
}

internal sealed class StudioProjectionActorCommandEnvelopeFactory
    : ICommandEnvelopeFactory<StudioProjectionActorCommand>
{
    internal const string DeliveryOperationIdHeader = "aevatar-delivery-operation-id";

    public EventEnvelope CreateEnvelope(StudioProjectionActorCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var envelope = new EventEnvelope
        {
            Id = context.CommandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(command.Payload),
            Route = EnvelopeRouteSemantics.CreateDirect(command.PublisherId, context.TargetId),
        };
        if (context.Headers.TryGetValue(DeliveryOperationIdHeader, out var deliveryOperationId) &&
            !string.IsNullOrWhiteSpace(deliveryOperationId))
        {
            envelope.EnsureRuntime().EnsureDeliveryIdentity().OperationId = deliveryOperationId;
        }

        return envelope;
    }
}

internal sealed class StudioProjectionActorCommandReceiptFactory
    : ICommandReceiptFactory<StudioProjectionActorCommandTarget, StudioProjectionActorCommandReceipt>
{
    public StudioProjectionActorCommandReceipt Create(
        StudioProjectionActorCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);
        return new StudioProjectionActorCommandReceipt(target.TargetId, context.CommandId, context.CorrelationId);
    }
}

internal sealed class StudioProjectionActorCommandDispatch
{
    private readonly ICommandDispatchService<StudioProjectionActorCommand, StudioProjectionActorCommandReceipt, StudioProjectionActorCommandStartError>
        _dispatchService;

    public StudioProjectionActorCommandDispatch(
        ICommandDispatchService<StudioProjectionActorCommand, StudioProjectionActorCommandReceipt, StudioProjectionActorCommandStartError>
            dispatchService)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
    }

    public async Task<StudioProjectionActorCommandReceipt> DispatchAsync(
        IActor actor,
        IMessage payload,
        string publisherId,
        string? commandId = null,
        string? correlationId = null,
        string? deliveryOperationId = null,
        CancellationToken ct = default)
    {
        var headers = string.IsNullOrWhiteSpace(deliveryOperationId)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StudioProjectionActorCommandEnvelopeFactory.DeliveryOperationIdHeader] =
                    deliveryOperationId,
            };

        var result = await _dispatchService.DispatchAsync(
            new StudioProjectionActorCommand(actor, payload, publisherId, commandId, correlationId, headers),
            ct);
        if (!result.Succeeded || result.Receipt is null)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "Studio projection actor command dispatch failed.");
        }

        return result.Admission == null
            ? result.Receipt
            : result.Receipt with { AckedAt = result.Admission.AckedAt };
    }
}

internal static class StudioProjectionActorCommandDispatchServiceCollectionExtensions
{
    public static IServiceCollection AddStudioProjectionActorCommandDispatch(this IServiceCollection services)
    {
        services.TryAddSingleton<ICommandTargetResolver<StudioProjectionActorCommand, StudioProjectionActorCommandTarget, StudioProjectionActorCommandStartError>, StudioProjectionActorCommandTargetResolver>();
        services.TryAddSingleton<ICommandEnvelopeFactory<StudioProjectionActorCommand>, StudioProjectionActorCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<StudioProjectionActorCommandTarget>, ActorCommandTargetDispatcher<StudioProjectionActorCommandTarget>>();
        services.TryAddSingleton<ICommandReceiptFactory<StudioProjectionActorCommandTarget, StudioProjectionActorCommandReceipt>, StudioProjectionActorCommandReceiptFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<StudioProjectionActorCommand, StudioProjectionActorCommandTarget, StudioProjectionActorCommandReceipt, StudioProjectionActorCommandStartError>, DefaultCommandDispatchPipeline<StudioProjectionActorCommand, StudioProjectionActorCommandTarget, StudioProjectionActorCommandReceipt, StudioProjectionActorCommandStartError>>();
        services.TryAddSingleton<ICommandDispatchService<StudioProjectionActorCommand, StudioProjectionActorCommandReceipt, StudioProjectionActorCommandStartError>, DefaultCommandDispatchService<StudioProjectionActorCommand, StudioProjectionActorCommandTarget, StudioProjectionActorCommandReceipt, StudioProjectionActorCommandStartError>>();
        services.TryAddSingleton<StudioProjectionActorCommandDispatch>();
        return services;
    }
}
