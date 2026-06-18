using System.Text.Json;
using Aevatar.AGUI.Contracts;
using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.GAgentService.Hosting.Sse;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeWorkflowAguiEventMapperTests
{
    [Fact]
    public void TryMap_WhenRunStopped_ShouldEmitCustomStoppedEvent()
    {
        var mapped = ScopeWorkflowAguiEventMapper.TryMap(
            new WorkflowRunEventEnvelope
            {
                Timestamp = 123,
                RunStopped = new WorkflowRunStoppedEventPayload
                {
                    RunId = "run-1",
                    Reason = "manual",
                },
            },
            out var aguiEvent);

        mapped.Should().BeTrue();
        aguiEvent.Should().NotBeNull();
        aguiEvent!.Timestamp.Should().Be(123);
        aguiEvent.Custom.Should().NotBeNull();
        aguiEvent.Custom.Name.Should().Be("aevatar.run.stopped");
        aguiEvent.Custom.Payload.Should().NotBeNull();
        var payload = aguiEvent.Custom.Payload.Unpack<WorkflowRunStoppedEventPayload>();
        payload.RunId.Should().Be("run-1");
        payload.Reason.Should().Be("manual");
    }

    [Fact]
    public void TryMap_WhenRunFinishedMissingRunId_ShouldFallBackToThreadId()
    {
        var mapped = ScopeWorkflowAguiEventMapper.TryMap(
            new WorkflowRunEventEnvelope
            {
                Timestamp = 456,
                RunFinished = new WorkflowRunFinishedEventPayload
                {
                    ThreadId = "thread-1",
                },
            },
            out var aguiEvent);

        mapped.Should().BeTrue();
        aguiEvent.Should().NotBeNull();
        aguiEvent!.RunFinished.Should().NotBeNull();
        aguiEvent.RunFinished.ThreadId.Should().Be("thread-1");
        aguiEvent.RunFinished.RunId.Should().Be("thread-1");
    }

    [Fact]
    public void TryMap_WhenUsage_ShouldEmitTypedAguiUsageEvent()
    {
        var mapped = ScopeWorkflowAguiEventMapper.TryMap(
            new WorkflowRunEventEnvelope
            {
                Timestamp = 789,
                Usage = new WorkflowUsageEventPayload
                {
                    Available = false,
                },
            },
            out var aguiEvent);

        mapped.Should().BeTrue();
        aguiEvent.Should().NotBeNull();
        aguiEvent!.Timestamp.Should().Be(789);
        aguiEvent.Usage.Should().NotBeNull();
        aguiEvent.Usage.Available.Should().BeFalse();
        aguiEvent.Usage.TotalTokens.Should().Be(0);
    }
    
    [Fact]
    public async Task TypeRegistry_ShouldSerializeRawObservedInitializeRoleAgentEvent()
    {
        var http = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };

        await using var writer = new AGUISseWriter(http.Response, ScopeWorkflowAguiEventMapper.TypeRegistry);
        await writer.WriteAsync(
            new AGUIEvent
            {
                Timestamp = 789,
                Custom = new CustomEvent
                {
                    Name = "aevatar.raw.observed",
                    Payload = Any.Pack(new WorkflowObservedEnvelopeCustomPayload
                    {
                        EventId = "evt-1",
                        PayloadTypeUrl = "type.googleapis.com/aevatar.ai.InitializeRoleAgentEvent",
                        PublisherActorId = "workflow-role-actor-1",
                        Payload = Any.Pack(new InitializeRoleAgentEvent
                        {
                            RoleId = "onboarding_formatter",
                            RoleName = "Lark Onboarding Formatter",
                            Model = "deepseek-v4-flash",
                        }),
                    }),
                },
            },
            CancellationToken.None);

        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        var payload = text["data: ".Length..].Trim();
        using var doc = JsonDocument.Parse(payload);

        var observed = doc.RootElement.GetProperty("custom").GetProperty("payload");
        observed.GetProperty("@type").GetString().Should().Contain(nameof(WorkflowObservedEnvelopeCustomPayload));
        var roleInitialize = observed.GetProperty("payload");
        roleInitialize.GetProperty("@type").GetString().Should().Contain(nameof(InitializeRoleAgentEvent));
        roleInitialize.GetProperty("roleId").GetString().Should().Be("onboarding_formatter");
        roleInitialize.GetProperty("roleName").GetString().Should().Be("Lark Onboarding Formatter");
    }
}
