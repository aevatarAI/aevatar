using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Context;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Infrastructure.Ports;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class RuntimeScriptProvisioningServiceBranchTests
{
    [Fact]
    public async Task EnsureRuntimeAsync_ShouldThrow_WhenDispatchFailsWithTypedError()
    {
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(_ => Task.FromResult(
                CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Failure(
                    ScriptingCommandStartError.InvalidArgument("definitionActorId", "definition id is required")))));

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("definition id is required*");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldThrow_WhenDispatchFailsWithoutTypedError()
    {
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(_ => Task.FromResult(new CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>
            {
                Succeeded = false,
                Error = null!,
                Receipt = null,
            })));

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Script runtime provisioning dispatch failed.");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldThrow_WhenReceiptIsMissing()
    {
        var service = new RuntimeScriptProvisioningService(
            new StaticDispatchService(_ => Task.FromResult(new CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>
            {
                Succeeded = true,
                Error = null!,
                Receipt = null,
            })));

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Script runtime provisioning did not produce a receipt.");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldThrow_WhenBindingQueryReturnsNotPending()
    {
        var service = CreateService(
            _ => Task.FromResult(CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(
                new ScriptingCommandAcceptedReceipt("runtime-1", "command-1", "corr-1"))),
            new StaticQueryClient(_ => Task.FromResult<IMessage>(new ScriptBehaviorBindingRespondedEvent
            {
                RequestId = "request-1",
                Found = false,
                Pending = false,
                FailureReason = string.Empty,
            })));

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Script runtime `runtime-1` is not bound.");
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShouldThrowTimeout_WhenBindingQueryCancelsWithoutCallerCancellation()
    {
        var service = CreateService(
            _ => Task.FromResult(CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(
                new ScriptingCommandAcceptedReceipt("runtime-timeout", "command-1", "corr-1"))),
            new StaticQueryClient(_ => throw new OperationCanceledException("query canceled")));

        var act = () => service.EnsureRuntimeAsync("definition-1", "rev-1", null, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("Timed out waiting for script runtime `runtime-timeout` to finish binding.");
    }

    private static RuntimeScriptProvisioningService CreateService(
        Func<ProvisionScriptRuntimeCommand, Task<CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>>> dispatch,
        StaticQueryClient queryClient)
    {
        return new RuntimeScriptProvisioningService(
            new StaticDispatchService(dispatch),
            new RuntimeScriptActorQueryClient(new NullStreamProvider(), queryClient, new NullDispatchPort()),
            new NullAgentContextAccessor());
    }

    private sealed class StaticDispatchService(
        Func<ProvisionScriptRuntimeCommand, Task<CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>>> dispatch)
        : ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>
    {
        public Task<CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>> DispatchAsync(
            ProvisionScriptRuntimeCommand command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return dispatch(command);
        }
    }

    private sealed class StaticQueryClient(
        Func<string, Task<IMessage>> query) : IStreamRequestReplyClient
    {
        public Task<TResponse> QueryAsync<TResponse>(
            IStreamProvider streams,
            string replyStreamPrefix,
            TimeSpan timeout,
            Func<string, string, Task> dispatchAsync,
            Func<TResponse, string, bool> isMatch,
            Func<string, string> timeoutMessageFactory,
            CancellationToken ct = default)
            where TResponse : IMessage, new() =>
            throw new NotSupportedException();

        public Task<TResponse> QueryActorAsync<TResponse>(
            IStreamProvider streams,
            IActor actor,
            string replyStreamPrefix,
            TimeSpan timeout,
            Func<string, string, EventEnvelope> envelopeFactory,
            Func<TResponse, string, bool> isMatch,
            Func<string, string> timeoutMessageFactory,
            CancellationToken ct = default)
            where TResponse : IMessage, new() =>
            throw new NotSupportedException();

        public async Task<TResponse> QueryActorAsync<TResponse>(
            IStreamProvider streams,
            string actorId,
            IActorDispatchPort dispatchPort,
            string replyStreamPrefix,
            TimeSpan timeout,
            Func<string, string, EventEnvelope> envelopeFactory,
            Func<TResponse, string, bool> isMatch,
            Func<string, string> timeoutMessageFactory,
            CancellationToken ct = default)
            where TResponse : IMessage, new()
        {
            _ = streams;
            _ = dispatchPort;
            _ = replyStreamPrefix;
            _ = timeout;
            _ = isMatch;
            _ = timeoutMessageFactory;
            ct.ThrowIfCancellationRequested();
            var envelope = envelopeFactory("request-1", "reply-stream");
            envelope.Should().NotBeNull();
            actorId.Should().NotBeNullOrWhiteSpace();
            return (TResponse)await query(actorId);
        }
    }

    private sealed class NullStreamProvider : IStreamProvider
    {
        public IStream GetStream(string actorId) => throw new NotSupportedException($"No stream for `{actorId}`.");
    }

    private sealed class NullDispatchPort : IActorDispatchPort
    {
        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = actorId;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }
    }

    private sealed class NullAgentContextAccessor : IAgentContextAccessor
    {
        public IAgentContext? Context { get; set; }
    }
}
