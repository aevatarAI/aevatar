using System.Text.Json;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class AGUISseWriterTests
{
    [Fact]
    public async Task WriteAsync_WithEvent_ShouldEmitSseFrameUsingCamelCase()
    {
        var http = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };

        await using var writer = new AGUISseWriter(http.Response);
        await writer.WriteAsync(
            new AGUIEvent
            {
                Timestamp = 123,
                RunFinished = new RunFinishedEvent
                {
                    ThreadId = "thread-1",
                    RunId = "run-1",
                    Result = Any.Pack(new StringValue { Value = "ok" }),
                },
            },
            CancellationToken.None);

        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();

        text.Should().StartWith("data: ");
        text.Should().Contain("\n\n");

        var payload = text["data: ".Length..].Trim();
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        root.GetProperty("runFinished").GetProperty("threadId").GetString().Should().Be("thread-1");
        root.GetProperty("runFinished").GetProperty("runId").GetString().Should().Be("run-1");
        root.GetProperty("runFinished").GetProperty("result").GetProperty("@type").GetString().Should().Contain("StringValue");
        root.GetProperty("runFinished").GetProperty("result").GetProperty("value").GetString().Should().Be("ok");
        ReadFlexibleInt64(root.GetProperty("timestamp")).Should().Be(123);
    }

    [Fact]
    public async Task WriteAsync_WithNullEvent_ShouldDoNothing()
    {
        var http = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };

        await using var writer = new AGUISseWriter(http.Response);
        AGUIEvent? evt = null;
        await writer.WriteAsync(evt!, CancellationToken.None);

        http.Response.Body.Length.Should().Be(0);
    }

    [Fact]
    public async Task WriteAsync_WithWorkflowRegistry_ShouldSerializeNestedWorkflowExecutionStatePayload()
    {
        var http = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };

        await using var writer = new AGUISseWriter(
            http.Response,
            WorkflowJsonTypeRegistry.Create(AGUIEvent.Descriptor.File));
        await writer.WriteAsync(
            new AGUIEvent
            {
                Timestamp = 456,
                Custom = new CustomEvent
                {
                    Name = "aevatar.raw.observed",
                    Payload = Any.Pack(new WorkflowObservedEnvelopeCustomPayload
                    {
                        EventId = "evt-2",
                        PayloadTypeUrl = "type.googleapis.com/aevatar.workflow.WorkflowExecutionStateUpsertedEvent",
                        PublisherActorId = "workflow-run-actor-1",
                        CorrelationId = "corr-1",
                        StateVersion = 2,
                        Payload = Any.Pack(new WorkflowExecutionStateUpsertedEvent
                        {
                            ScopeKey = "workflow_execution_kernel",
                            State = Any.Pack(new WorkflowExecutionKernelState
                            {
                                Active = true,
                                RunId = "run-1",
                                CurrentStepId = "analyze",
                                Variables =
                                {
                                    ["decision"] = "approved",
                                },
                            }),
                        }),
                    }),
                },
            },
            CancellationToken.None);

        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();

        text.Should().StartWith("data: ");
        text.Should().Contain("WorkflowExecutionStateUpsertedEvent");
        text.Should().Contain("WorkflowExecutionKernelState");
        text.Should().Contain("\"scopeKey\": \"workflow_execution_kernel\"");
        text.Should().Contain("\"runId\": \"run-1\"");
        text.Should().Contain("\"currentStepId\": \"analyze\"");
        text.Should().Contain("\"decision\": \"approved\"");
    }

    private static long ReadFlexibleInt64(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64(),
            JsonValueKind.String => long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Unexpected timestamp JSON kind: {value.ValueKind}"),
        };
    }
}
