using System.Text;
using System.Text.Json;
using Aevatar.AGUI.Contracts;
using Aevatar.GAgentService.Hosting.Sse;
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
    public async Task WriteAsync_WhileIdleAfterFrame_ShouldEmitKeepAliveHeartbeat()
    {
        var body = new KeepAliveSignalingStream();
        var http = new DefaultHttpContext
        {
            Response = { Body = body },
        };

        await using var writer = new AGUISseWriter(
            http.Response,
            heartbeatInterval: TimeSpan.FromMilliseconds(20));
        await writer.WriteAsync(
            new AGUIEvent
            {
                RunStarted = new RunStartedEvent
                {
                    ThreadId = "thread-1",
                    RunId = "run-1",
                },
            },
            CancellationToken.None);

        await body.KeepAliveSeen.WaitAsync(TimeSpan.FromSeconds(5));
        body.KeepAliveSeen.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_WithoutWrite_ShouldNotThrow()
    {
        var http = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };

        var writer = new AGUISseWriter(http.Response);
        var dispose = async () => await writer.DisposeAsync();

        await dispose.Should().NotThrowAsync();
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

    private sealed class KeepAliveSignalingStream : Stream
    {
        private const string KeepAliveMarker = ": keepalive";
        private readonly MemoryStream _inner = new();
        private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task KeepAliveSeen => _seen.Task;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken);
            Signal(Encoding.UTF8.GetString(buffer.Span));
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            Signal(Encoding.UTF8.GetString(buffer, offset, count));
        }

        private void Signal(string written)
        {
            if (written.Contains(KeepAliveMarker, StringComparison.Ordinal))
                _seen.TrySetResult();
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
