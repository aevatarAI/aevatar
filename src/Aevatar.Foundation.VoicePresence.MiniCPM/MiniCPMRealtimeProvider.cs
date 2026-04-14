using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.MiniCPM.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.VoicePresence.MiniCPM;

/// <summary>
/// MiniCPM-o demo-protocol adapter for <see cref="IRealtimeVoiceProvider" />.
/// </summary>
public sealed class MiniCPMRealtimeProvider : IRealtimeVoiceProvider
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly MiniCPMRealtimeProviderOptions _options;
    private readonly ILogger _logger;

    private Channel<VoiceProviderEvent>? _eventChannel;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _dispatchLoop;
    private Task? _completionsLoop;
    private Uri? _endpoint;
    private string? _uid;
    private bool _disposed;
    private int _inputSampleRateHz = MiniCPMRealtimeProviderOptions.DefaultInputSampleRateHz;
    private int _nextResponseId;
    private int _activeResponseId;
    private int _suppressedResponseId;

    public MiniCPMRealtimeProvider(
        MiniCPMRealtimeProviderOptions? options = null,
        ILogger<MiniCPMRealtimeProvider>? logger = null)
        : this(
            new HttpClient(),
            ownsHttpClient: true,
            options ?? new MiniCPMRealtimeProviderOptions(),
            logger ?? NullLogger<MiniCPMRealtimeProvider>.Instance)
    {
    }

    internal MiniCPMRealtimeProvider(
        HttpClient httpClient,
        bool ownsHttpClient,
        MiniCPMRealtimeProviderOptions options,
        ILogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Func<VoiceProviderEvent, CancellationToken, Task>? OnEvent { private get; set; }

    public Task ConnectAsync(VoiceProviderConfig config, CancellationToken ct)
    {
        _ = ct;
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(config);
        EnsureDisconnected();
        ValidateProviderConfig(config);

        _endpoint = new Uri(config.Endpoint.Trim(), UriKind.Absolute);
        _uid = Guid.NewGuid().ToString("n");
        _eventChannel = Channel.CreateBounded<VoiceProviderEvent>(new BoundedChannelOptions(_options.EventQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
        _lifetimeCts = new CancellationTokenSource();
        _completionsLoop = RunCompletionsLoopAsync(_eventChannel.Writer, _lifetimeCts.Token);
        _dispatchLoop = RunDispatchLoopAsync(_eventChannel.Reader, _lifetimeCts.Token);
        return Task.CompletedTask;
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
    {
        if (pcm16.IsEmpty)
            return;

        var request = new MiniCPMMessageRequest
        {
            Messages =
            {
                new MiniCPMMessage
                {
                    Role = "user",
                    Content =
                    {
                        new MiniCPMMessageContent
                        {
                            Type = "input_audio",
                            InputAudio = new MiniCPMInputAudio
                            {
                                Data = Convert.ToBase64String(
                                    MiniCPMWaveCodec.EncodePcm16Mono(pcm16.Span, _inputSampleRateHz)),
                                Format = "wav",
                            },
                        },
                    },
                },
            },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(_options.StreamPath))
        {
            Content = JsonContent.Create(request),
        };
        ApplyUidHeader(message);

        using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await BuildHttpFailureMessageAsync("stream", response, ct));
    }

    public Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct)
    {
        _ = callId;
        _ = resultJson;
        _ = ct;
        throw new NotSupportedException(
            "MiniCPM-o demo protocol does not support provider-side tool result continuation.");
    }

    public Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct)
    {
        _ = injection;
        _ = ct;
        throw new NotSupportedException(
            "MiniCPM-o demo protocol does not support structured external event injection.");
    }

    public async Task CancelResponseAsync(CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(_options.StopPath));
        ApplyUidHeader(message);

        using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await BuildHttpFailureMessageAsync("stop", response, ct));

        var responseId = Interlocked.Exchange(ref _activeResponseId, 0);
        if (responseId <= 0 || _eventChannel == null)
            return;

        Volatile.Write(ref _suppressedResponseId, responseId);
        await _eventChannel.Writer.WriteAsync(new VoiceProviderEvent
        {
            ResponseCancelled = new VoiceResponseCancelled
            {
                ResponseId = responseId,
            },
        }, ct);
    }

    public Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct)
    {
        _ = ct;
        ArgumentNullException.ThrowIfNull(session);
        EnsureConnected();

        _inputSampleRateHz = ResolveInputSampleRate(session.SampleRateHz);

        if (!string.IsNullOrWhiteSpace(session.Voice))
            _logger.LogInformation("MiniCPM voice provider ignores voice selection '{Voice}'.", session.Voice);
        if (!string.IsNullOrWhiteSpace(session.Instructions))
            _logger.LogInformation("MiniCPM voice provider ignores session instructions.");
        if (session.ToolNames.Count > 0)
            _logger.LogInformation("MiniCPM voice provider does not expose provider-side tool registration.");

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        var cts = _lifetimeCts;
        _lifetimeCts = null;
        cts?.Cancel();

        if (_eventChannel != null)
            _eventChannel.Writer.TryComplete();

        await AwaitLoopAsync(_completionsLoop);
        await AwaitLoopAsync(_dispatchLoop);

        _completionsLoop = null;
        _dispatchLoop = null;
        _eventChannel = null;
        _endpoint = null;
        _uid = null;
        _activeResponseId = 0;
        _suppressedResponseId = 0;

        cts?.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private async Task RunCompletionsLoopAsync(ChannelWriter<VoiceProviderEvent> writer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(_options.CompletionsPath))
                {
                    Content = JsonContent.Create(new MiniCPMMessageRequest()),
                };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                ApplyUidHeader(request);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    await TryWriteAsync(writer, new VoiceProviderEvent
                    {
                        Error = new VoiceProviderError
                        {
                            ErrorCode = $"http_{(int)response.StatusCode}",
                            ErrorMessage = await BuildHttpFailureMessageAsync("completions", response, ct),
                        },
                    }, ct);
                    await TryWriteAsync(writer, new VoiceProviderEvent
                    {
                        Disconnected = new VoiceProviderDisconnected
                        {
                            Reason = $"mini-cpm completions request failed with HTTP {(int)response.StatusCode}.",
                        },
                    }, ct);
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await ReadCompletionStreamAsync(stream, writer, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MiniCPM completions loop terminated unexpectedly.");
            await TryWriteAsync(writer, new VoiceProviderEvent
            {
                Disconnected = new VoiceProviderDisconnected
                {
                    Reason = $"error:{ex.Message}",
                },
            }, ct);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task ReadCompletionStreamAsync(
        Stream stream,
        ChannelWriter<VoiceProviderEvent> writer,
        CancellationToken ct)
    {
        var responseStarted = false;
        var responseTerminated = false;
        var responseId = 0;

        await foreach (var payload in MiniCPMSsePayloadReader.ReadPayloadsAsync(stream, ct))
        {
            if (string.IsNullOrWhiteSpace(payload))
                continue;

            MiniCPMCompletionsFrame? frame;
            try
            {
                frame = JsonSerializer.Deserialize<MiniCPMCompletionsFrame>(payload);
            }
            catch (JsonException ex)
            {
                await TryWriteAsync(writer, new VoiceProviderEvent
                {
                    Error = new VoiceProviderError
                    {
                        ErrorCode = "invalid_payload",
                        ErrorMessage = $"Failed to parse MiniCPM SSE payload: {ex.Message}",
                    },
                }, ct);
                continue;
            }

            if (frame == null)
                continue;

            if (!string.IsNullOrWhiteSpace(frame.Error))
            {
                await TryWriteAsync(writer, new VoiceProviderEvent
                {
                    Error = new VoiceProviderError
                    {
                        ErrorCode = "provider_error",
                        ErrorMessage = frame.Error,
                    },
                }, ct);
                continue;
            }

            var choice = frame.Choices?.FirstOrDefault();
            if (choice == null)
                continue;

            if (ShouldIgnorePreludeFrame(frame, choice))
                continue;

            if (IsEndMarker(choice.Text))
            {
                if (!responseStarted)
                    continue;

                responseTerminated = true;
                Interlocked.CompareExchange(ref _activeResponseId, 0, responseId);
                if (Volatile.Read(ref _suppressedResponseId) == responseId)
                {
                    Volatile.Write(ref _suppressedResponseId, 0);
                    continue;
                }

                await TryWriteAsync(writer, new VoiceProviderEvent
                {
                    ResponseDone = new VoiceResponseDone
                    {
                        ResponseId = responseId,
                    },
                }, ct);
                continue;
            }

            if (!HasMeaningfulContent(choice))
                continue;

            if (!responseStarted)
            {
                responseStarted = true;
                responseId = Interlocked.Increment(ref _nextResponseId);
                Volatile.Write(ref _activeResponseId, responseId);
                await TryWriteAsync(writer, new VoiceProviderEvent
                {
                    ResponseStarted = new VoiceResponseStarted
                    {
                        ResponseId = responseId,
                    },
                }, ct);
            }

            if (Volatile.Read(ref _suppressedResponseId) == responseId)
                continue;

            if (string.IsNullOrWhiteSpace(choice.Audio))
                continue;

            try
            {
                var wavBytes = Convert.FromBase64String(choice.Audio);
                var decoded = MiniCPMWaveCodec.DecodePcm16Mono(wavBytes);
                await TryWriteAsync(writer, new VoiceProviderEvent
                {
                    AudioReceived = new VoiceAudioReceived
                    {
                        Pcm16 = Google.Protobuf.ByteString.CopyFrom(decoded.Pcm16),
                        SampleRateHz = decoded.SampleRateHz,
                    },
                }, ct);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
                await TryWriteAsync(writer, new VoiceProviderEvent
                {
                    Error = new VoiceProviderError
                    {
                        ErrorCode = "invalid_audio",
                        ErrorMessage = ex.Message,
                    },
                }, ct);
            }
        }

        if (!responseStarted)
            return;

        Interlocked.CompareExchange(ref _activeResponseId, 0, responseId);
        if (Volatile.Read(ref _suppressedResponseId) == responseId)
        {
            Volatile.Write(ref _suppressedResponseId, 0);
            return;
        }

        if (!responseTerminated)
        {
            await TryWriteAsync(writer, new VoiceProviderEvent
            {
                Disconnected = new VoiceProviderDisconnected
                {
                    Reason = "mini-cpm completions stream ended before response completion.",
                },
            }, ct);
        }
    }

    private async Task RunDispatchLoopAsync(ChannelReader<VoiceProviderEvent> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var providerEvent in reader.ReadAllAsync(ct))
            {
                var callback = OnEvent;
                if (callback == null)
                    continue;

                try
                {
                    await callback(providerEvent, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MiniCPM provider callback failed for event {EventCase}.",
                        providerEvent.EventCase);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private Uri BuildUri(string path) => new(EnsureConnected(), path);

    private void ApplyUidHeader(HttpRequestMessage message) =>
        message.Headers.TryAddWithoutValidation(_options.UidHeaderName, EnsureUid());

    private Uri EnsureConnected() =>
        _endpoint ?? throw new InvalidOperationException("MiniCPM voice provider is not connected.");

    private string EnsureUid() =>
        _uid ?? throw new InvalidOperationException("MiniCPM voice provider is not connected.");

    private void EnsureDisconnected()
    {
        if (_endpoint != null)
            throw new InvalidOperationException("MiniCPM voice provider is already connected.");
    }

    private int ResolveInputSampleRate(int requestedSampleRateHz)
    {
        if (requestedSampleRateHz == 0)
            return _options.SupportedInputSampleRateHz;

        if (requestedSampleRateHz != _options.SupportedInputSampleRateHz)
        {
            throw new InvalidOperationException(
                $"MiniCPM voice provider currently accepts PCM16 input at {_options.SupportedInputSampleRateHz} Hz only.");
        }

        return requestedSampleRateHz;
    }

    private static bool HasMeaningfulContent(MiniCPMCompletionsChoice choice) =>
        !string.IsNullOrWhiteSpace(choice.Audio) || !string.IsNullOrWhiteSpace(choice.Text);

    private static bool IsEndMarker(string? text) =>
        string.Equals(text?.Trim(), "<end>", StringComparison.Ordinal);

    private static bool ShouldIgnorePreludeFrame(MiniCPMCompletionsFrame frame, MiniCPMCompletionsChoice choice) =>
        frame.ResponseId.GetValueOrDefault() == 0 &&
        !string.IsNullOrWhiteSpace(choice.Audio) &&
        string.Equals(choice.Text?.Trim(), "assistant:", StringComparison.Ordinal);

    private static void ValidateProviderConfig(VoiceProviderConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ProviderName) &&
            !string.Equals(config.ProviderName, "minicpm", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(config.ProviderName, "minicpm-o", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"MiniCPMRealtimeProvider requires provider_name 'minicpm' or 'minicpm-o', but got '{config.ProviderName}'.");
        }

        if (string.IsNullOrWhiteSpace(config.Endpoint))
            throw new InvalidOperationException("MiniCPM voice provider requires endpoint.");
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException("MiniCPM voice provider endpoint must be an absolute URI.");
    }

    private static async Task<string> BuildHttpFailureMessageAsync(
        string operation,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var body = response.Content == null
            ? string.Empty
            : (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (string.IsNullOrWhiteSpace(body))
            return $"MiniCPM {operation} request failed with HTTP {(int)response.StatusCode}.";

        return $"MiniCPM {operation} request failed with HTTP {(int)response.StatusCode}: {body}";
    }

    private static async Task TryWriteAsync(
        ChannelWriter<VoiceProviderEvent> writer,
        VoiceProviderEvent providerEvent,
        CancellationToken ct)
    {
        try
        {
            await writer.WriteAsync(providerEvent, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private static async Task AwaitLoopAsync(Task? loop)
    {
        if (loop == null)
            return;

        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
