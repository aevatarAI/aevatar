using System.Reflection;
using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Sagas.Abstractions.Actions;
using Aevatar.CQRS.Sagas.Abstractions.Runtime;

namespace Aevatar.CQRS.Sagas.Core.Dispatch;

public sealed class CommandBusSagaCommandEmitter : ISagaCommandEmitter
{
    private static readonly MethodInfo EnqueueMethod = ResolveRequiredMethod(
        typeof(ICommandBus),
        nameof(ICommandBus.EnqueueAsync));

    private static readonly MethodInfo ScheduleMethod = ResolveRequiredMethod(
        typeof(ICommandScheduler),
        nameof(ICommandScheduler.ScheduleAsync));

    private readonly ICommandBus _commandBus;
    private readonly ICommandScheduler? _scheduler;

    public CommandBusSagaCommandEmitter(
        ICommandBus commandBus,
        ICommandScheduler? scheduler = null)
    {
        _commandBus = commandBus;
        _scheduler = scheduler;
    }

    public Task EnqueueAsync(SagaEnqueueCommandAction action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeEnqueueAsync(action.Target, action.Command, action.CommandId, action.CorrelationId, action.Metadata, ct);
    }

    public async Task ScheduleAsync(SagaScheduleCommandAction action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (action.Delay <= TimeSpan.Zero)
        {
            await InvokeEnqueueAsync(action.Target, action.Command, action.CommandId, action.CorrelationId, action.Metadata, ct);
            return;
        }

        if (_scheduler == null)
        {
            await Task.Delay(action.Delay, ct);
            await InvokeEnqueueAsync(action.Target, action.Command, action.CommandId, action.CorrelationId, action.Metadata, ct);
            return;
        }

        var command = action.Command;
        var commandType = command.GetType();
        var envelope = BuildEnvelope(
            action.Target,
            action.CommandId,
            action.CorrelationId,
            action.Metadata);

        var method = ScheduleMethod.MakeGenericMethod(commandType);
        var task = method.Invoke(_scheduler, [envelope, command, action.Delay, ct]) as Task;
        if (task == null)
            throw new InvalidOperationException($"Failed to invoke scheduler for command type '{commandType.FullName}'.");

        await task;
    }

    private async Task InvokeEnqueueAsync(
        string target,
        object command,
        string? commandId,
        string? correlationId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(command);

        var commandType = command.GetType();
        var envelope = BuildEnvelope(target, commandId, correlationId, metadata);
        var method = EnqueueMethod.MakeGenericMethod(commandType);

        var task = method.Invoke(_commandBus, [envelope, command, ct]) as Task;
        if (task == null)
            throw new InvalidOperationException($"Failed to invoke command bus for command type '{commandType.FullName}'.");

        await task;
    }

    private static CommandEnvelope BuildEnvelope(
        string target,
        string? commandId,
        string? correlationId,
        IReadOnlyDictionary<string, string>? metadata)
    {
        var resolvedCommandId = string.IsNullOrWhiteSpace(commandId)
            ? Guid.NewGuid().ToString("N")
            : commandId;

        var resolvedCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? resolvedCommandId
            : correlationId;

        return CommandEnvelope.Create(
            resolvedCommandId,
            resolvedCorrelationId,
            target,
            metadata);
    }

    private static MethodInfo ResolveRequiredMethod(Type targetType, string methodName)
    {
        var method = targetType.GetMethods()
            .FirstOrDefault(x =>
                x.Name == methodName &&
                x.IsGenericMethodDefinition);

        return method ?? throw new InvalidOperationException(
            $"Method '{targetType.FullName}.{methodName}`1' was not found.");
    }
}
