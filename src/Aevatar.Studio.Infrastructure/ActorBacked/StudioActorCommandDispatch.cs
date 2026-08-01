using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

// Refactor (iter56/cluster-911-studio-store-query-command):
//   old=Store mixed read/write + hand-built EventEnvelope
//   new=split query/command port + CQRS Core dispatch
internal sealed record StudioActorCommand(
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

internal sealed class StudioActorCommandTarget(IActor actor) : IActorCommandDispatchTarget
{
    public IActor Actor { get; } = actor ?? throw new ArgumentNullException(nameof(actor));

    public string TargetId => Actor.Id;
}

internal sealed record StudioActorCommandReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId);

internal sealed record StudioActorCommandStartError(string Message)
{
    public static StudioActorCommandStartError InvalidActor(string message) => new(message);
}

internal sealed class StudioActorCommandTargetResolver
    : ICommandTargetResolver<StudioActorCommand, StudioActorCommandTarget, StudioActorCommandStartError>
{
    public Task<CommandTargetResolution<StudioActorCommandTarget, StudioActorCommandStartError>> ResolveAsync(
        StudioActorCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Actor.Id))
        {
            return Task.FromResult(
                CommandTargetResolution<StudioActorCommandTarget, StudioActorCommandStartError>.Failure(
                    StudioActorCommandStartError.InvalidActor("Actor id is required.")));
        }

        return Task.FromResult(
            CommandTargetResolution<StudioActorCommandTarget, StudioActorCommandStartError>.Success(
                new StudioActorCommandTarget(command.Actor)));
    }
}

internal sealed class StudioActorCommandEnvelopeFactory : ICommandEnvelopeFactory<StudioActorCommand>
{
    public EventEnvelope CreateEnvelope(StudioActorCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return new EventEnvelope
        {
            Id = context.CommandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(command.Payload),
            Route = EnvelopeRouteSemantics.CreateDirect(command.PublisherId, context.TargetId),
        };
    }
}

internal sealed class StudioActorCommandReceiptFactory
    : ICommandReceiptFactory<StudioActorCommandTarget, StudioActorCommandReceipt>
{
    public StudioActorCommandReceipt Create(
        StudioActorCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);
        return new StudioActorCommandReceipt(target.TargetId, context.CommandId, context.CorrelationId);
    }
}

internal sealed class StudioActorCommandDispatch
{
    private readonly ICommandDispatchService<StudioActorCommand, StudioActorCommandReceipt, StudioActorCommandStartError>
        _dispatchService;

    public StudioActorCommandDispatch(
        ICommandDispatchService<StudioActorCommand, StudioActorCommandReceipt, StudioActorCommandStartError>
            dispatchService)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
    }

    public async Task<StudioActorCommandReceipt> DispatchAsync(
        IActor actor,
        IMessage payload,
        string publisherId,
        CancellationToken ct = default)
    {
        var result = await _dispatchService.DispatchAsync(
            new StudioActorCommand(actor, payload, publisherId),
            ct);
        if (!result.Succeeded || result.Receipt is null)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "Studio actor command dispatch failed.");
        }

        if (result.Admission is not { Accepted: true })
            throw new InvalidOperationException("Studio actor command dispatch admission was not accepted.");

        return result.Receipt;
    }
}

internal static class StudioActorCommandDispatchServiceCollectionExtensions
{
    public static IServiceCollection AddStudioActorCommandDispatch(this IServiceCollection services)
    {
        services.TryAddSingleton<ICommandTargetResolver<StudioActorCommand, StudioActorCommandTarget, StudioActorCommandStartError>, StudioActorCommandTargetResolver>();
        services.TryAddSingleton<ICommandEnvelopeFactory<StudioActorCommand>, StudioActorCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<StudioActorCommandTarget>, ActorCommandTargetDispatcher<StudioActorCommandTarget>>();
        services.TryAddSingleton<ICommandReceiptFactory<StudioActorCommandTarget, StudioActorCommandReceipt>, StudioActorCommandReceiptFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<StudioActorCommand, StudioActorCommandTarget, StudioActorCommandReceipt, StudioActorCommandStartError>, DefaultCommandDispatchPipeline<StudioActorCommand, StudioActorCommandTarget, StudioActorCommandReceipt, StudioActorCommandStartError>>();
        services.TryAddSingleton<ICommandDispatchService<StudioActorCommand, StudioActorCommandReceipt, StudioActorCommandStartError>, DefaultCommandDispatchService<StudioActorCommand, StudioActorCommandTarget, StudioActorCommandReceipt, StudioActorCommandStartError>>();
        services.TryAddSingleton<StudioActorCommandDispatch>();
        return services;
    }
}
