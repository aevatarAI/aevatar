using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunStepRouter
{
    private readonly WorkflowRunCapabilityRegistry _registry;
    private readonly WorkflowPrimitiveExecutorRegistry _primitiveRegistry;
    private readonly IReadOnlySet<string> _knownStepTypes;
    private readonly Func<IServiceProvider?> _servicesAccessor;
    private readonly Func<ILogger> _loggerAccessor;
    private readonly WorkflowRunReadContext _read;
    private readonly WorkflowRunWriteContext _write;
    private readonly WorkflowRunEffectPorts _effects;

    public WorkflowRunStepRouter(
        WorkflowRunCapabilityRegistry registry,
        WorkflowPrimitiveExecutorRegistry primitiveRegistry,
        IReadOnlySet<string> knownStepTypes,
        Func<IServiceProvider?> servicesAccessor,
        Func<ILogger> loggerAccessor,
        WorkflowRunReadContext read,
        WorkflowRunWriteContext write,
        WorkflowRunEffectPorts effects)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _primitiveRegistry = primitiveRegistry ?? throw new ArgumentNullException(nameof(primitiveRegistry));
        _knownStepTypes = knownStepTypes ?? throw new ArgumentNullException(nameof(knownStepTypes));
        _servicesAccessor = servicesAccessor ?? throw new ArgumentNullException(nameof(servicesAccessor));
        _loggerAccessor = loggerAccessor ?? throw new ArgumentNullException(nameof(loggerAccessor));
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
    }

    public async Task DispatchAsync(StepRequestEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_registry.TryGetStepCapability(request.StepType, out var capability))
        {
            await capability.HandleStepAsync(request, _read, _write, _effects, ct);
            return;
        }

        if (await TryHandleRegisteredPrimitiveAsync(request, ct))
            return;

        await _write.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = $"unknown workflow step type '{WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType)}'",
        }, EventDirection.Self, ct);
    }

    private async Task<bool> TryHandleRegisteredPrimitiveAsync(StepRequestEvent request, CancellationToken ct)
    {
        var services = _servicesAccessor();
        if (services == null ||
            !_primitiveRegistry.TryCreate(request.StepType, services, out var handler) ||
            handler == null)
        {
            return false;
        }

        var logger = _loggerAccessor();
        try
        {
            await handler.HandleAsync(
                request,
                new WorkflowPrimitiveExecutionContext(
                    _read.ActorId,
                    services,
                    logger,
                    _knownStepTypes,
                    new PrimitiveEventSink(_write)),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Workflow primitive {PrimitiveName} failed", handler.Name);
            await _write.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = false,
                Error = $"primitive '{handler.Name}' failed: {ex.Message}",
            }, EventDirection.Self, ct);
        }

        return true;
    }

    private sealed class PrimitiveEventSink(WorkflowRunWriteContext write) : WorkflowPrimitiveExecutionContext.IWorkflowPrimitiveEventSink
    {
        public Task PublishAsync<TEvent>(TEvent evt, EventDirection direction, CancellationToken ct)
            where TEvent : IMessage =>
            write.PublishAsync(evt, direction, ct);
    }
}
