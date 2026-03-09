using System.Reflection;
using System.Text.Json;
using Aevatar.Tools.Cli.Bridge;
using Aevatar.Workflow.Sdk;
using Aevatar.Workflow.Sdk.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppBridgeEndpointsTests
{
    [Fact]
    public async Task HandleSignalAsync_WhenStepIdIsProvided_ShouldForwardPreciseCorrelationToClient()
    {
        var method = typeof(AppBridgeEndpoints).GetMethod(
            "HandleSignalAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var client = new CapturingWorkflowClient();
        var request = new WorkflowSignalRequest
        {
            ActorId = "actor-1",
            RunId = "run-1",
            SignalName = "channel_binding_ready",
            StepId = "wait-channel-ready",
            CommandId = "cmd-1",
            Payload = """{"status":"ok"}""",
        };

        var task = method!
            .Invoke(null, new object?[] { request, client, CancellationToken.None })
            .Should()
            .BeAssignableTo<Task<IResult>>()
            .Subject;

        var result = await task;
        client.LastSignalRequest.Should().NotBeNull();
        client.LastSignalRequest!.ActorId.Should().Be("actor-1");
        client.LastSignalRequest.RunId.Should().Be("run-1");
        client.LastSignalRequest.SignalName.Should().Be("channel_binding_ready");
        client.LastSignalRequest.StepId.Should().Be("wait-channel-ready");
        client.LastSignalRequest.CommandId.Should().Be("cmd-1");

        result.Should().NotBeNull();
    }

    private sealed class CapturingWorkflowClient : IAevatarWorkflowClient
    {
        public WorkflowSignalRequest? LastSignalRequest { get; private set; }

        public IAsyncEnumerable<WorkflowEvent> StartRunStreamAsync(
            ChatRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunResult> RunToCompletionAsync(
            ChatRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkflowResumeResponse> ResumeAsync(
            WorkflowResumeRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkflowSignalResponse> SignalAsync(
            WorkflowSignalRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSignalRequest = request;
            return Task.FromResult(new WorkflowSignalResponse
            {
                Accepted = true,
                ActorId = request.ActorId,
                RunId = request.RunId,
                SignalName = request.SignalName,
                StepId = request.StepId,
                CommandId = request.CommandId,
            });
        }

        public Task<IReadOnlyList<JsonElement>> GetWorkflowCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JsonElement?> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JsonElement?> GetWorkflowDetailAsync(string workflowName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<JsonElement?> GetActorSnapshotAsync(string actorId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<JsonElement>> GetActorTimelineAsync(
            string actorId,
            int take = 200,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
