using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowPrimitiveExecutionContext
{
    private readonly IWorkflowPrimitiveEventSink _eventSink;

    public WorkflowPrimitiveExecutionContext(
        string agentId,
        IServiceProvider services,
        ILogger logger,
        IReadOnlySet<string> knownStepTypes,
        Func<IMessage, EventDirection, CancellationToken, Task> publishAsync)
        : this(
            agentId,
            services,
            logger,
            knownStepTypes,
            new DelegateEventSink(publishAsync))
    {
    }

    internal WorkflowPrimitiveExecutionContext(
        string agentId,
        IServiceProvider services,
        ILogger logger,
        IReadOnlySet<string> knownStepTypes,
        IWorkflowPrimitiveEventSink eventSink)
    {
        AgentId = agentId;
        Services = services;
        Logger = logger;
        KnownStepTypes = knownStepTypes;
        _eventSink = eventSink;
    }

    public string AgentId { get; }

    public IServiceProvider Services { get; }

    public ILogger Logger { get; }

    public IReadOnlySet<string> KnownStepTypes { get; }

    public Task PublishAsync<TEvent>(
        TEvent evt,
        EventDirection direction,
        CancellationToken ct)
        where TEvent : IMessage =>
        _eventSink.PublishAsync(evt, direction, ct);

    internal interface IWorkflowPrimitiveEventSink
    {
        Task PublishAsync<TEvent>(TEvent evt, EventDirection direction, CancellationToken ct)
            where TEvent : IMessage;
    }

    private sealed class DelegateEventSink(
        Func<IMessage, EventDirection, CancellationToken, Task> publishAsync)
        : IWorkflowPrimitiveEventSink
    {
        private readonly Func<IMessage, EventDirection, CancellationToken, Task> _publishAsync =
            publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));

        public Task PublishAsync<TEvent>(TEvent evt, EventDirection direction, CancellationToken ct)
            where TEvent : IMessage =>
            _publishAsync(evt, direction, ct);
    }
}
