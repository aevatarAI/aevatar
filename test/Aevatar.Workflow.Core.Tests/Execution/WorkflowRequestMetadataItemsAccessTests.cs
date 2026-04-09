using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowRequestMetadataItemsAccessTests
{
    [Fact]
    public void SetRequestMetadata_ShouldTrimValuesAndRemoveEmptyEntries()
    {
        var host = new RecordingStateHost();

        WorkflowRequestMetadataItemsAccess.SetRequestMetadata(
            host,
            new Dictionary<string, string?>
            {
                [" trace-id "] = "  abc  ",
                [" "] = "ignored",
                ["empty"] = " ",
            }!.ToDictionary(x => x.Key, x => x.Value!));

        host.Items.Should().ContainKey("workflow.request.metadata");
        var stored = host.Items["workflow.request.metadata"].Should().BeOfType<Dictionary<string, string>>().Subject;
        stored.Should().ContainSingle();
        stored["trace-id"].Should().Be("abc");
    }

    [Fact]
    public void SetRequestMetadata_ShouldRemoveItemWhenMetadataIsNullEmptyOrInvalid()
    {
        var host = new RecordingStateHost();
        host.Items["workflow.request.metadata"] = new Dictionary<string, string> { ["existing"] = "value" };

        WorkflowRequestMetadataItemsAccess.SetRequestMetadata(host, null);
        host.Items.Should().NotContainKey("workflow.request.metadata");

        host.Items["workflow.request.metadata"] = new Dictionary<string, string> { ["existing"] = "value" };
        WorkflowRequestMetadataItemsAccess.SetRequestMetadata(host, new Dictionary<string, string>());
        host.Items.Should().NotContainKey("workflow.request.metadata");

        host.Items["workflow.request.metadata"] = new Dictionary<string, string> { ["existing"] = "value" };
        WorkflowRequestMetadataItemsAccess.SetRequestMetadata(
            host,
            new Dictionary<string, string>
            {
                [" "] = " ",
            });
        host.Items.Should().NotContainKey("workflow.request.metadata");
    }

    [Fact]
    public void RemoveRequestMetadata_ShouldValidateAndDeleteItem()
    {
        var host = new RecordingStateHost();
        host.Items["workflow.request.metadata"] = new Dictionary<string, string> { ["trace-id"] = "abc" };

        WorkflowRequestMetadataItemsAccess.RemoveRequestMetadata(host);
        host.Items.Should().NotContainKey("workflow.request.metadata");

        FluentActions.Invoking(() => WorkflowRequestMetadataItemsAccess.RemoveRequestMetadata(null!))
            .Should()
            .Throw<ArgumentNullException>();
    }

    [Fact]
    public void CopyRequestMetadata_ShouldReturnZeroWhenMissingOrEmpty()
    {
        var target = new Dictionary<string, string>(StringComparer.Ordinal);

        WorkflowRequestMetadataItemsAccess.CopyRequestMetadata(new RecordingWorkflowExecutionContext(), target)
            .Should()
            .Be(0);

        var contextWithEmptyItem = new RecordingWorkflowExecutionContext();
        contextWithEmptyItem.Items["workflow.request.metadata"] = new Dictionary<string, string>();
        WorkflowRequestMetadataItemsAccess.CopyRequestMetadata(contextWithEmptyItem, target)
            .Should()
            .Be(0);
    }

    [Fact]
    public void CopyRequestMetadata_ShouldCopyOnlyValidEntries()
    {
        var context = new RecordingWorkflowExecutionContext();
        context.Items["workflow.request.metadata"] = new Dictionary<string, string>
        {
            ["trace-id"] = "abc",
            [" "] = "ignored",
            ["empty"] = " ",
        };
        var target = new Dictionary<string, string>(StringComparer.Ordinal);

        var copied = WorkflowRequestMetadataItemsAccess.CopyRequestMetadata(context, target);

        copied.Should().Be(1);
        target.Should().ContainSingle();
        target["trace-id"].Should().Be("abc");
    }

    [Fact]
    public void CopyRequestMetadata_ShouldValidateArguments()
    {
        var context = new RecordingWorkflowExecutionContext();
        var target = new Dictionary<string, string>(StringComparer.Ordinal);

        FluentActions.Invoking(() => WorkflowRequestMetadataItemsAccess.CopyRequestMetadata(null!, target))
            .Should()
            .Throw<ArgumentNullException>();
        FluentActions.Invoking(() => WorkflowRequestMetadataItemsAccess.CopyRequestMetadata(context, null!))
            .Should()
            .Throw<ArgumentNullException>();
        FluentActions.Invoking(() => WorkflowRequestMetadataItemsAccess.SetRequestMetadata(null!, target))
            .Should()
            .Throw<ArgumentNullException>();
    }

    private sealed class RecordingStateHost : IWorkflowExecutionStateHost
    {
        public string RunId => "run-1";

        public Dictionary<string, object?> Items { get; } = new(StringComparer.Ordinal);

        public Any? GetExecutionState(string scopeKey) => null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() => [];

        public bool TryGetExecutionItem(string itemKey, out object? value) => Items.TryGetValue(itemKey, out value);

        public void SetExecutionItem(string itemKey, object? value) => Items[itemKey] = value;

        public bool RemoveExecutionItem(string itemKey) => Items.Remove(itemKey);

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default) => Task.CompletedTask;

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingWorkflowExecutionContext : IWorkflowExecutionContext, IWorkflowExecutionItemsContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;

        public string RunId => "run-1";

        public Dictionary<string, object?> Items { get; } = new(StringComparer.Ordinal);

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new() => new();

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() => [];

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState> => Task.CompletedTask;

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;

        public bool TryGetItem<TItem>(string itemKey, out TItem? value)
        {
            if (Items.TryGetValue(itemKey, out var raw) && raw is TItem typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        public void SetItem(string itemKey, object? value) => Items[itemKey] = value;

        public bool RemoveItem(string itemKey) => Items.Remove(itemKey);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
