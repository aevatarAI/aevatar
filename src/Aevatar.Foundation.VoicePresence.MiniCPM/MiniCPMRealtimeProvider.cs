using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.MiniCPM.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.VoicePresence.MiniCPM;

/// <summary>
/// MiniCPM-o demo-protocol adapter for <see cref="IRealtimeVoiceProvider" />.
/// </summary>
// Refactor (iter15/cluster-026-voice-provider-background-state):
//   Old pattern: realtime provider receive loop writes _responseEpochs dictionary from background thread outside actor event-loop
//   New principle: provider callbacks emit provider-native response ids only.
//   VoicePresenceModule owns actor response epoch mapping inside the actor turn.
// Refactor (iter106/cluster-106-voice-provider-session-runtime):
//   Old pattern: Realtime voice providers and the module keep provider session, event channel, cancellation source, dispatch loop, and transport pump as process-local mutable runtime objects.
//   New principle: Provider callbacks emit typed signals with lease/session keys; session ownership and pump lifecycle are actor-owned or distributed state, while provider objects are disposable transport handles only.
public sealed class MiniCPMRealtimeProvider : IRealtimeVoiceProvider
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly MiniCPMRealtimeProviderOptions _options;
    private readonly ILogger _logger;

    private bool _disposed;

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

    public Task<RealtimeVoiceProviderSession> ConnectAsync(
        VoiceProviderSessionKey sessionKey,
        VoiceProviderConfig config,
        Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
        Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink,
        CancellationToken ct)
    {
        _ = ct;
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(eventSink);
        ArgumentNullException.ThrowIfNull(audioSink);
        ValidateProviderConfig(config);

        var session = new MiniCPMRealtimeProviderSession(
            sessionKey,
            new Uri(config.Endpoint.Trim(), UriKind.Absolute),
            Guid.NewGuid().ToString("n"),
            _httpClient,
            _options,
            _logger,
            eventSink,
            audioSink);
        session.Start();
        return Task.FromResult<RealtimeVoiceProviderSession>(session);
    }

    // Refactor (iter15/cluster-026-voice-provider-background-state):
    //   Old pattern: cancel mutated active/suppressed response epoch state and emitted synthetic cancel events.
    //   New principle: cancel only sends provider stop; VoicePresenceModule owns actor cancellation state.
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await ValueTask.CompletedTask;
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    // Refactor (iter106/cluster-106-voice-provider-session-runtime):
    //   Old pattern: Realtime voice providers and the module keep provider session, event channel, cancellation source, dispatch loop, and transport pump as process-local mutable runtime objects.
    //   New principle: Provider callbacks emit typed signals with lease/session keys; session ownership and pump lifecycle are actor-owned or distributed state, while provider objects are disposable transport handles only.
    private sealed class MiniCPMRealtimeProviderSession : RealtimeVoiceProviderSession
    {
        private readonly VoiceProviderSessionKey _callbackKey;
        private readonly HttpClient _httpClient;
        private readonly MiniCPMRealtimeProviderOptions _options;
        private readonly ILogger _logger;
        private readonly Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> _eventSink;
        private readonly Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> _audioSink;
        private readonly CancellationTokenSource _physicalSessionCancellation = new();
        private Task? _completionsLoop;
        private bool _disposed;
        private int _inputSampleRateHz = MiniCPMRealtimeProviderOptions.DefaultInputSampleRateHz;

        public MiniCPMRealtimeProviderSession(
            VoiceProviderSessionKey sessionKey,
            Uri endpoint,
            string uid,
            HttpClient httpClient,
            MiniCPMRealtimeProviderOptions options,
            ILogger logger,
            Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
            Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink)
        {
            _callbackKey = sessionKey;
            Endpoint = endpoint;
            Uid = uid;
            _httpClient = httpClient;
            _options = options;
            _logger = logger;
            _eventSink = eventSink;
            _audioSink = audioSink;
        }

        public Uri Endpoint { get; }
        public string Uid { get; }

        public void Start()
        {
            _completionsLoop = RunCompletionsLoopAsync(_physicalSessionCancellation.Token);
        }

        public override async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
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

        public override Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct)
        {
            _ = callId;
            _ = resultJson;
            _ = ct;
            throw new NotSupportedException(
                "MiniCPM-o demo protocol does not support provider-side tool result continuation.");
        }

        public override Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct)
        {
            _ = injection;
            _ = ct;
            throw new NotSupportedException(
                "MiniCPM-o demo protocol does not support structured external event injection.");
        }

        public override async Task CancelResponseAsync(CancellationToken ct)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(_options.StopPath));
            ApplyUidHeader(message);

            using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await BuildHttpFailureMessageAsync("stop", response, ct));
        }

        public override Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct)
        {
            _ = ct;
            ArgumentNullException.ThrowIfNull(session);

            _inputSampleRateHz = ResolveInputSampleRate(_options, session.SampleRateHz);

            if (!string.IsNullOrWhiteSpace(session.Voice))
                _logger.LogInformation("MiniCPM voice provider ignores voice selection '{Voice}'.", session.Voice);
            if (!string.IsNullOrWhiteSpace(session.Instructions))
                _logger.LogInformation("MiniCPM voice provider ignores session instructions.");
            if (session.ToolNames.Count > 0 || session.ToolDefinitions.Count > 0)
                _logger.LogInformation("MiniCPM voice provider does not expose provider-side tool registration.");

            return Task.CompletedTask;
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            _physicalSessionCancellation.Cancel();
            await AwaitLoopAsync(_completionsLoop);
            _physicalSessionCancellation.Dispose();
        }

        private Uri BuildUri(string path) => new(Endpoint, path);

        private void ApplyUidHeader(HttpRequestMessage message) =>
            message.Headers.TryAddWithoutValidation(_options.UidHeaderName, Uid);

        private async Task RunCompletionsLoopAsync(CancellationToken ct)
        {
            try
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
                    await EmitAsync(new VoiceProviderEvent
                    {
                        Error = new VoiceProviderError
                        {
                            ErrorCode = $"http_{(int)response.StatusCode}",
                            ErrorMessage = await BuildHttpFailureMessageAsync("completions", response, ct),
                        },
                    }, ct);
                    await EmitAsync(new VoiceProviderEvent
                    {
                        Disconnected = new VoiceProviderDisconnected
                        {
                            Reason = $"mini-cpm completions request failed with HTTP {(int)response.StatusCode}.",
                        },
                    }, ct);
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await ReadCompletionStreamAsync(stream, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MiniCPM completions loop terminated unexpectedly.");
                await EmitAsync(new VoiceProviderEvent
                {
                    Disconnected = new VoiceProviderDisconnected
                    {
                        Reason = $"error:{ex.Message}",
                    },
                }, CancellationToken.None);
            }
        }

        // Refactor (iter15/cluster-026-voice-provider-background-state):
        //   Old pattern: completion stream allocated actor response ids and suppressed stale audio in provider state.
        //   New principle: stream parsing emits provider-native response ids; module actor turn maps and suppresses.
        private async Task ReadCompletionStreamAsync(Stream stream, CancellationToken ct)
        {
            var responseStarted = false;
            var responseTerminated = false;
            var providerResponseId = string.Empty;

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
                    await EmitAsync(new VoiceProviderEvent
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
                    await EmitAsync(new VoiceProviderEvent
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
                    await EmitAsync(new VoiceProviderEvent
                    {
                        ResponseDone = new VoiceResponseDone
                        {
                            ProviderResponseId = providerResponseId,
                        },
                    }, ct);
                    continue;
                }

                if (!HasMeaningfulContent(choice))
                    continue;

                if (!responseStarted)
                {
                    responseStarted = true;
                    providerResponseId = ResolveProviderResponseId(frame);
                    await EmitAsync(new VoiceProviderEvent
                    {
                        ResponseStarted = new VoiceResponseStarted
                        {
                            ProviderResponseId = providerResponseId,
                        },
                    }, ct);
                }

                if (string.IsNullOrWhiteSpace(choice.Audio))
                    continue;

                try
                {
                    var wavBytes = Convert.FromBase64String(choice.Audio);
                    var decoded = MiniCPMWaveCodec.DecodePcm16Mono(wavBytes);
                    await EmitAudioAsync(new VoiceProviderAudioFrame(
                        decoded.Pcm16,
                        decoded.SampleRateHz,
                        providerResponseId), ct);
                }
                catch (Exception ex) when (ex is FormatException or InvalidDataException)
                {
                    await EmitAsync(new VoiceProviderEvent
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

            if (!responseTerminated)
            {
                await EmitAsync(new VoiceProviderEvent
                {
                    Disconnected = new VoiceProviderDisconnected
                    {
                        Reason = "mini-cpm completions stream ended before response completion.",
                    },
                }, ct);
            }
        }

        private async Task EmitAsync(VoiceProviderEvent providerEvent, CancellationToken ct)
        {
            try
            {
                await _eventSink(_callbackKey, providerEvent, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MiniCPM provider callback failed for event {EventCase}.",
                    providerEvent.EventCase);
            }
        }

        private async Task EmitAudioAsync(VoiceProviderAudioFrame audioFrame, CancellationToken ct)
        {
            try
            {
                await _audioSink(_callbackKey, audioFrame, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MiniCPM provider audio callback failed.");
            }
        }
    }

    private static int ResolveInputSampleRate(
        MiniCPMRealtimeProviderOptions options,
        int requestedSampleRateHz)
    {
        if (requestedSampleRateHz == 0)
            return options.SupportedInputSampleRateHz;

        if (requestedSampleRateHz != options.SupportedInputSampleRateHz)
        {
            throw new InvalidOperationException(
                $"MiniCPM voice provider currently accepts PCM16 input at {options.SupportedInputSampleRateHz} Hz only.");
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

    // Refactor (iter15/cluster-026-voice-provider-background-state):
    //   Old pattern: MiniCPM fallback ids doubled as actor response epochs inside the provider loop.
    //   New principle: provider fallback ids are provider-local correlation keys; VoicePresenceModule allocates actor response ids.
    private static string ResolveProviderResponseId(MiniCPMCompletionsFrame frame) =>
        frame.ResponseId is > 0
            ? frame.ResponseId.GetValueOrDefault().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Guid.NewGuid().ToString("n");

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
