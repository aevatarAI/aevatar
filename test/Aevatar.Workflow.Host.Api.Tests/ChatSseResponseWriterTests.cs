using System.Text;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatSseResponseWriterTests
{
    [Fact]
    public async Task WriteAsync_WithFrame_ShouldEmitSseDataFrame()
    {
        var http = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };

        await using var writer = new ChatSseResponseWriter(http.Response);
        await writer.WriteAsync(
            new WorkflowRunEventEnvelope { Timestamp = 7 },
            CancellationToken.None);

        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();

        text.Should().StartWith("data: ");
        text.Should().EndWith("\n\n");
    }

    [Fact]
    public async Task StartAsync_WhileIdle_ShouldEmitKeepAliveHeartbeat()
    {
        // A long agent run can go silent (no data frame) for longer than the nginx ingress idle
        // proxy-read-timeout, which would sever the SSE connection mid-run. The writer must emit a
        // keepalive comment during that silence. Drive a tiny interval and assert one is written
        // without sending any data frame.
        var body = new KeepAliveSignalingStream();
        var http = new DefaultHttpContext
        {
            Response = { Body = body },
        };

        await using var writer = new ChatSseResponseWriter(
            http.Response,
            heartbeatInterval: TimeSpan.FromMilliseconds(20));
        await writer.StartAsync(CancellationToken.None);

        // Completes as soon as the keepalive comment is written; the timeout is only a safety net.
        await body.KeepAliveSeen.WaitAsync(TimeSpan.FromSeconds(5));
        body.KeepAliveSeen.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_WithoutStart_ShouldNotThrow()
    {
        var http = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };

        var writer = new ChatSseResponseWriter(http.Response);
        var dispose = async () => await writer.DisposeAsync();

        await dispose.Should().NotThrowAsync();
    }

    // Wraps a MemoryStream and signals the first time a keepalive comment is written, so the heartbeat
    // assertion is deterministic (completes on the write) instead of polling on a wall-clock delay.
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
