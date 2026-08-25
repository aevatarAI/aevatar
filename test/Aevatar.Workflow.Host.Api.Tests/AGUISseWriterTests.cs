using System.IO.Pipelines;
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
using Microsoft.AspNetCore.Http.Features;

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
    public async Task WriteAsync_WithEvent_ShouldStartSseResponse()
    {
        var bodyFeature = new RecordingResponseBodyFeature(new MemoryStream());
        var http = new DefaultHttpContext();
        http.Features.Set<IHttpResponseBodyFeature>(bodyFeature);

        await using var writer = new AGUISseWriter(http.Response);
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

        writer.ResponseStarted.Should().BeTrue();
        bodyFeature.StartCount.Should().Be(1);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        http.Response.Headers.ContentType.ToString().Should().Be("text/event-stream; charset=utf-8");
        http.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        http.Response.Headers.Pragma.ToString().Should().Be("no-cache");
        http.Response.Headers["X-Accel-Buffering"].ToString().Should().Be("no");
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
    public async Task DisposeAsync_AfterWrite_ShouldCancelActiveHeartbeat()
    {
        var body = new KeepAliveSignalingStream();
        var http = new DefaultHttpContext
        {
            Response = { Body = body },
        };

        var writer = new AGUISseWriter(
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
        var dispose = async () => await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await dispose.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteAsync_WithConcurrentFirstWrites_ShouldStartResponseOnce()
    {
        var bodyFeature = new RecordingResponseBodyFeature(new MemoryStream());
        var http = new DefaultHttpContext();
        http.Features.Set<IHttpResponseBodyFeature>(bodyFeature);

        await using var writer = new AGUISseWriter(
            http.Response,
            heartbeatInterval: TimeSpan.FromMinutes(5));
        using var startGate = new ManualResetEventSlim(false);
        var writes = Enumerable.Range(0, 20)
            .Select(index => Task.Run(async () =>
            {
                startGate.Wait();
                await writer.WriteAsync(
                    new AGUIEvent
                    {
                        RunStarted = new RunStartedEvent
                        {
                            ThreadId = $"thread-{index}",
                            RunId = $"run-{index}",
                        },
                    },
                    CancellationToken.None);
            }))
            .ToArray();

        startGate.Set();
        await Task.WhenAll(writes);

        bodyFeature.StartCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_WhenFirstStartFails_ShouldRemainRetryable()
    {
        var bodyFeature = new RecordingResponseBodyFeature(
            new MemoryStream(),
            failFirstStart: true);
        var http = new DefaultHttpContext();
        http.Features.Set<IHttpResponseBodyFeature>(bodyFeature);

        await using var writer = new AGUISseWriter(
            http.Response,
            heartbeatInterval: TimeSpan.FromMinutes(5));
        var firstStart = async () => await writer.StartAsync();

        await firstStart.Should().ThrowAsync<IOException>();
        writer.ResponseStarted.Should().BeFalse();

        await writer.StartAsync();

        writer.ResponseStarted.Should().BeTrue();
        bodyFeature.StartCount.Should().Be(2);
    }

    [Fact]
    public async Task DisposeAsync_DuringWrite_ShouldWaitForActiveFrame()
    {
        var body = new BlockingWriteStream();
        var http = new DefaultHttpContext
        {
            Response = { Body = body },
        };

        var writer = new AGUISseWriter(
            http.Response,
            heartbeatInterval: TimeSpan.FromMinutes(5));
        var write = writer.WriteAsync(
            new AGUIEvent
            {
                RunStarted = new RunStartedEvent
                {
                    ThreadId = "thread-1",
                    RunId = "run-1",
                },
            },
            CancellationToken.None);

        await body.WriteEntered.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = writer.DisposeAsync().AsTask();

        dispose.IsCompleted.Should().BeFalse();
        body.ReleaseWrite();
        await write;
        await dispose;
        body.WriteCompleted.IsCompletedSuccessfully.Should().BeTrue();
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
            if (!written.Contains(KeepAliveMarker, StringComparison.Ordinal))
                return;

            _seen.TrySetResult();
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly TaskCompletionSource _writeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WriteEntered => _writeEntered.Task;
        public Task WriteCompleted => _writeCompleted.Task;

        public void ReleaseWrite()
        {
            _releaseWrite.TrySetResult();
        }

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
            _writeEntered.TrySetResult();
            await _releaseWrite.Task.WaitAsync(cancellationToken);
            _writeCompleted.TrySetResult();
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class RecordingResponseBodyFeature(
        Stream stream,
        bool failFirstStart = false) : IHttpResponseBodyFeature
    {
        private readonly StreamResponseBodyFeature _inner = new(stream);
        private int _startCount;

        public int StartCount => Volatile.Read(ref _startCount);
        public Stream Stream => _inner.Stream;
        public PipeWriter Writer => _inner.Writer;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            var startCount = Interlocked.Increment(ref _startCount);
            if (failFirstStart && startCount == 1)
                return Task.FromException(new IOException("Injected response start failure."));

            return _inner.StartAsync(cancellationToken);
        }

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            _inner.SendFileAsync(path, offset, count, cancellationToken);

        public Task CompleteAsync() => _inner.CompleteAsync();
        public void DisableBuffering() => _inner.DisableBuffering();
    }
}
