using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.MiniCPM.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.MiniCPM.Tests;

public class MiniCPMRealtimeProviderTests
{
    [Fact]
    public async Task SendAudio_should_post_wav_stream_request_with_uid_header()
    {
        string? requestBody = null;
        string? uidHeader = null;
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/v1/completions", StringComparison.Ordinal))
                return CreateSseResponse(blockAfterPayload: true);

            if (request.RequestUri.AbsolutePath.EndsWith("/api/v1/stream", StringComparison.Ordinal))
            {
                uidHeader = request.Headers.GetValues("uid").Single();
                requestBody = await request.Content!.ReadAsStringAsync(ct);
                return CreateJsonResponse("{}");
            }

            throw new InvalidOperationException($"Unexpected path: {request.RequestUri.AbsolutePath}");
        });

        await using var provider = CreateProvider(handler);
        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);
        await provider.SendAudioAsync(new byte[] { 1, 2, 3, 4 }, CancellationToken.None);

        uidHeader.ShouldNotBeNullOrWhiteSpace();
        requestBody.ShouldNotBeNullOrWhiteSpace();

        using var document = JsonDocument.Parse(requestBody);
        var inputAudio = document.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")[0]
            .GetProperty("input_audio");
        inputAudio.GetProperty("format").GetString().ShouldBe("wav");

        var wavBytes = Convert.FromBase64String(inputAudio.GetProperty("data").GetString()!);
        var decoded = MiniCPMWaveCodec.DecodePcm16Mono(wavBytes);
        decoded.SampleRateHz.ShouldBe(MiniCPMRealtimeProviderOptions.DefaultInputSampleRateHz);
        decoded.Pcm16.ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public async Task SendAudio_should_ignore_empty_payload()
    {
        var streamCalls = 0;
        var handler = new StubHttpMessageHandler((request, ct) =>
        {
            _ = ct;
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/v1/completions", StringComparison.Ordinal))
                return Task.FromResult(CreateSseResponse(blockAfterPayload: true));

            if (request.RequestUri.AbsolutePath.EndsWith("/api/v1/stream", StringComparison.Ordinal))
            {
                streamCalls++;
                return Task.FromResult(CreateJsonResponse("{}"));
            }

            throw new InvalidOperationException($"Unexpected path: {request.RequestUri.AbsolutePath}");
        });

        await using var provider = CreateProvider(handler);
        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);

        await provider.SendAudioAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        streamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Receive_loop_should_map_sse_audio_to_voice_provider_events()
    {
        var completionCalls = 0;
        var handler = new StubHttpMessageHandler((request, ct) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/api/v1/completions", StringComparison.Ordinal))
                return Task.FromResult(CreateJsonResponse("{}"));

            completionCalls++;
            if (completionCalls == 1)
            {
                var preludeAudio = Convert.ToBase64String(MiniCPMWaveCodec.EncodePcm16Mono([9, 9], 16000));
                var chunkAudio = Convert.ToBase64String(MiniCPMWaveCodec.EncodePcm16Mono([10, 20, 30, 40], 24000));
                return Task.FromResult(CreateSseResponse(
                    $$"""
                    data: {"id":"u1","response_id":0,"choices":[{"role":"assistant","audio":"{{preludeAudio}}","text":"assistant:\n","finish_reason":"processing"}]}

                    data: {"id":"u1","response_id":1,"choices":[{"role":"assistant","audio":"{{chunkAudio}}","text":"hello","finish_reason":"processing"}]}

                    data: {"id":"u1","response_id":1,"choices":[{"role":"assistant","audio":null,"text":"\n<end>","finish_reason":"done"}]}

                    """));
            }

            return Task.FromResult(CreateSseResponse(blockAfterPayload: true));
        });

        await using var provider = CreateProvider(handler);
        var events = new List<VoiceProviderEvent>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.OnEvent = (evt, ct) =>
        {
            _ = ct;
            events.Add(evt);
            if (evt.EventCase == VoiceProviderEvent.EventOneofCase.ResponseDone)
                done.TrySetResult();
            return Task.CompletedTask;
        };

        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        events.Select(x => x.EventCase).ShouldBe(
        [
            VoiceProviderEvent.EventOneofCase.ResponseStarted,
            VoiceProviderEvent.EventOneofCase.AudioReceived,
            VoiceProviderEvent.EventOneofCase.ResponseDone,
        ]);
        events[0].ResponseStarted.ResponseId.ShouldBe(1);
        events[1].AudioReceived.SampleRateHz.ShouldBe(24000);
        events[1].AudioReceived.Pcm16.ToByteArray().ShouldBe([10, 20, 30, 40]);
        events[2].ResponseDone.ResponseId.ShouldBe(1);
    }

    [Fact]
    public async Task CancelResponse_should_post_stop_and_emit_synthetic_cancel()
    {
        var responseCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/v1/completions", StringComparison.Ordinal))
                return CreateSseResponse(blockAfterPayload: true);

            if (path.EndsWith("/api/v1/stop", StringComparison.Ordinal))
            {
                request.Headers.GetValues("uid").Single().ShouldNotBeNullOrWhiteSpace();
                stopRequested.TrySetResult();
                return CreateJsonResponse("{}");
            }

            if (path.EndsWith("/api/v1/stream", StringComparison.Ordinal))
                return CreateJsonResponse("{}");

            throw new InvalidOperationException($"Unexpected path: {path}");
        });

        await using var provider = CreateProvider(handler);
        provider.OnEvent = (evt, ct) =>
        {
            _ = ct;
            switch (evt.EventCase)
            {
                case VoiceProviderEvent.EventOneofCase.ResponseCancelled:
                    responseCancelled.TrySetResult();
                    break;
            }

            return Task.CompletedTask;
        };

        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);
        typeof(MiniCPMRealtimeProvider)
            .GetField("_activeResponseId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(provider, 7);

        await provider.CancelResponseAsync(CancellationToken.None);

        await stopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await responseCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UpdateSession_should_apply_supported_sample_rate_and_ignore_optional_fields()
    {
        string? requestBody = null;
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/v1/completions", StringComparison.Ordinal))
                return CreateSseResponse(blockAfterPayload: true);

            if (request.RequestUri.AbsolutePath.EndsWith("/api/v1/stream", StringComparison.Ordinal))
            {
                requestBody = await request.Content!.ReadAsStringAsync(ct);
                return CreateJsonResponse("{}");
            }

            throw new InvalidOperationException($"Unexpected path: {request.RequestUri.AbsolutePath}");
        });

        await using var provider = CreateProvider(handler);
        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);
        await provider.UpdateSessionAsync(new VoiceSessionConfig
        {
            SampleRateHz = 16000,
            Voice = "friendly",
            Instructions = "be helpful",
            ToolNames = { "lookup" },
            ToolDefinitions =
            {
                new VoiceToolDefinition
                {
                    Name = "lookup",
                    Description = "Lookup a fact",
                },
            },
        }, CancellationToken.None);

        await provider.SendAudioAsync(new byte[] { 5, 6, 7, 8 }, CancellationToken.None);

        requestBody.ShouldNotBeNullOrWhiteSpace();
        using var document = JsonDocument.Parse(requestBody);
        var wavBytes = Convert.FromBase64String(document.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")[0]
            .GetProperty("input_audio")
            .GetProperty("data")
            .GetString()!);
        var decoded = MiniCPMWaveCodec.DecodePcm16Mono(wavBytes);
        decoded.SampleRateHz.ShouldBe(16000);
        decoded.Pcm16.ShouldBe([5, 6, 7, 8]);
    }

    [Fact]
    public async Task Completions_http_failure_should_emit_error_and_disconnected()
    {
        var events = new List<VoiceProviderEvent>();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler((request, ct) =>
        {
            _ = ct;
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/v1/completions", StringComparison.Ordinal))
                return Task.FromResult(CreateJsonResponse("""{"detail":"boom"}""", HttpStatusCode.InternalServerError));

            return Task.FromResult(CreateJsonResponse("{}"));
        });

        await using var provider = CreateProvider(handler);
        provider.OnEvent = (evt, ct) =>
        {
            _ = ct;
            events.Add(evt);
            if (evt.EventCase == VoiceProviderEvent.EventOneofCase.Disconnected)
                disconnected.TrySetResult();
            return Task.CompletedTask;
        };

        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        events.Select(x => x.EventCase).ShouldBe(
        [
            VoiceProviderEvent.EventOneofCase.Error,
            VoiceProviderEvent.EventOneofCase.Disconnected,
        ]);
        events[0].Error.ErrorCode.ShouldBe("http_500");
        events[0].Error.ErrorMessage.ShouldContain("boom");
    }

    [Fact]
    public async Task Receive_loop_should_emit_error_events_for_invalid_payloads_and_disconnect_on_incomplete_response()
    {
        await using var provider = CreateProvider(new StubHttpMessageHandler((request, ct) =>
        {
            _ = request;
            _ = ct;
            return Task.FromResult(CreateJsonResponse("{}"));
        }));
        var channel = Channel.CreateUnbounded<VoiceProviderEvent>();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """
            data: {"id":"u1","response_id":0,"choices":[{"role":"assistant","audio":null,"text":"\n<end>","finish_reason":"processing"}]}

            data: not-json

            data: {"id":"u1","error":"provider boom"}

            data: {"id":"u1","choices":[]}

            data: {"id":"u1","response_id":3,"choices":[{"role":"assistant","audio":"%%%","text":"speak","finish_reason":"processing"}]}

            """));

        var readCompletionStreamAsync = typeof(MiniCPMRealtimeProvider)
            .GetMethod("ReadCompletionStreamAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)readCompletionStreamAsync.Invoke(provider, [stream, channel.Writer, CancellationToken.None])!);
        channel.Writer.TryComplete();

        var events = new List<VoiceProviderEvent>();
        await foreach (var evt in channel.Reader.ReadAllAsync())
            events.Add(evt);

        events.Select(static x => x.EventCase).ShouldBe(
        [
            VoiceProviderEvent.EventOneofCase.Error,
            VoiceProviderEvent.EventOneofCase.Error,
            VoiceProviderEvent.EventOneofCase.ResponseStarted,
            VoiceProviderEvent.EventOneofCase.Error,
            VoiceProviderEvent.EventOneofCase.Disconnected,
        ]);
        events[0].Error.ErrorCode.ShouldBe("invalid_payload");
        events[1].Error.ErrorCode.ShouldBe("provider_error");
        events[2].ResponseStarted.ResponseId.ShouldBe(1);
        events[3].Error.ErrorCode.ShouldBe("invalid_audio");
        events[4].Disconnected.Reason.ShouldContain("before response completion");
    }

    [Fact]
    public async Task DisposeAsync_should_allow_repeat_calls_and_block_reconnect()
    {
        await using var provider = CreateProvider(new StubHttpMessageHandler((request, ct) =>
        {
            _ = request;
            _ = ct;
            return Task.FromResult(CreateSseResponse(blockAfterPayload: true));
        }));
        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);

        await provider.DisposeAsync();
        await provider.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() =>
            provider.ConnectAsync(CreateConfig(), CancellationToken.None));
    }

    [Fact]
    public async Task UpdateSession_should_reject_unsupported_input_sample_rate()
    {
        await using var provider = CreateProvider(new StubHttpMessageHandler((request, ct) =>
        {
            _ = request;
            _ = ct;
            return Task.FromResult(CreateSseResponse(blockAfterPayload: true));
        }));
        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(() => provider.UpdateSessionAsync(new VoiceSessionConfig
        {
            SampleRateHz = 24000,
        }, CancellationToken.None));
    }

    [Fact]
    public async Task SendToolResult_should_throw_not_supported()
    {
        await using var provider = CreateProvider(new StubHttpMessageHandler((request, ct) =>
        {
            _ = request;
            _ = ct;
            return Task.FromResult(CreateSseResponse(blockAfterPayload: true));
        }));

        await Should.ThrowAsync<NotSupportedException>(() =>
            provider.SendToolResultAsync("call-1", "{}", CancellationToken.None));
    }

    [Fact]
    public async Task InjectEvent_should_throw_not_supported()
    {
        await using var provider = CreateProvider(new StubHttpMessageHandler((request, ct) =>
        {
            _ = request;
            _ = ct;
            return Task.FromResult(CreateSseResponse(blockAfterPayload: true));
        }));

        await Should.ThrowAsync<NotSupportedException>(() =>
            provider.InjectEventAsync(new VoiceConversationEventInjection(), CancellationToken.None));
    }

    [Fact]
    public async Task Connect_with_wrong_provider_name_should_throw()
    {
        await using var provider = CreateProvider(new StubHttpMessageHandler((request, ct) =>
        {
            _ = request;
            _ = ct;
            return Task.FromResult(CreateSseResponse(blockAfterPayload: true));
        }));

        await Should.ThrowAsync<InvalidOperationException>(() => provider.ConnectAsync(new VoiceProviderConfig
        {
            ProviderName = "openai",
            Endpoint = "http://127.0.0.1:32550",
        }, CancellationToken.None));
    }

    private static MiniCPMRealtimeProvider CreateProvider(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://127.0.0.1:32550"),
            },
            ownsHttpClient: true,
            new MiniCPMRealtimeProviderOptions(),
            NullLogger<MiniCPMRealtimeProvider>.Instance);

    private static VoiceProviderConfig CreateConfig() => new()
    {
        ProviderName = "minicpm",
        Endpoint = "http://127.0.0.1:32550",
    };

    private static HttpResponseMessage CreateJsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage CreateSseResponse(string payload = "", bool blockAfterPayload = false)
    {
        HttpContent content = blockAfterPayload
            ? new StreamContent(new BlockingStream(Encoding.UTF8.GetBytes(payload)))
            : new StringContent(payload, Encoding.UTF8, "text/event-stream");
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    private sealed class BlockingStream(byte[] prefix) : Stream
    {
        private readonly byte[] _prefix = prefix;
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (_position < _prefix.Length)
            {
                var length = Math.Min(count, _prefix.Length - _position);
                Array.Copy(_prefix, _position, buffer, offset, length);
                _position += length;
                return length;
            }

            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _prefix.Length)
            {
                var length = Math.Min(buffer.Length, _prefix.Length - _position);
                _prefix.AsMemory(_position, length).CopyTo(buffer);
                _position += length;
                return length;
            }

            await _blocked.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var result = await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            return result;
        }
    }
}
