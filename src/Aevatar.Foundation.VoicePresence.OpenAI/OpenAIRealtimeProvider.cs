using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.OpenAI.Internal;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Realtime;

namespace Aevatar.Foundation.VoicePresence.OpenAI;

/// <summary>
/// OpenAI Realtime GA implementation of <see cref="IRealtimeVoiceProvider" />.
/// </summary>
// Refactor (iter15/cluster-026-voice-provider-background-state):
//   Old pattern: realtime provider receive loop writes _responseEpochs dictionary from background thread outside actor event-loop
//   New principle: provider callbacks emit provider-native response ids only.
//   VoicePresenceModule owns actor response epoch mapping inside the actor turn.
// Refactor (iter106/cluster-106-voice-provider-session-runtime):
//   Old pattern: Realtime voice providers and the module keep provider session, event channel, cancellation source, dispatch loop, and transport pump as process-local mutable runtime objects.
//   New principle: Provider callbacks emit typed signals with lease/session keys; session ownership and pump lifecycle are actor-owned or distributed state, while provider objects are disposable transport handles only.
public sealed class OpenAIRealtimeProvider : IRealtimeVoiceProvider
{
    private static readonly BinaryData PermissiveToolSchema =
        BinaryData.FromString("""{"type":"object","additionalProperties":true}""");
    private static readonly JsonFormatter InjectionJsonFormatter = new(JsonFormatter.Settings.Default);

    private readonly IOpenAIRealtimeSessionFactory _sessionFactory;
    private readonly OpenAIRealtimeProviderOptions _options;
    private readonly IRealtimeProviderCredentialResolver? _credentialResolver;
    private readonly ILogger _logger;

    private bool _disposed;

    public OpenAIRealtimeProvider(
        OpenAIRealtimeProviderOptions? options = null,
        ILogger<OpenAIRealtimeProvider>? logger = null,
        IRealtimeProviderCredentialResolver? credentialResolver = null)
        : this(
            new OpenAIRealtimeSessionFactory(),
            options ?? new OpenAIRealtimeProviderOptions(),
            logger ?? NullLogger<OpenAIRealtimeProvider>.Instance,
            credentialResolver)
    {
    }

    internal OpenAIRealtimeProvider(
        IOpenAIRealtimeSessionFactory sessionFactory,
        OpenAIRealtimeProviderOptions options,
        ILogger logger,
        IRealtimeProviderCredentialResolver? credentialResolver = null)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _credentialResolver = credentialResolver;
    }

    public async Task<RealtimeVoiceProviderSession> ConnectAsync(
        VoiceProviderSessionKey sessionKey,
        VoiceProviderConfig config,
        Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
        Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(eventSink);
        ArgumentNullException.ThrowIfNull(audioSink);

        var effectiveConfig = await ResolveEffectiveConfigAsync(sessionKey, config, ct);
        ValidateProviderConfig(effectiveConfig);

        var session = await _sessionFactory.StartConversationSessionAsync(effectiveConfig, _options.DefaultModel, ct);
        var providerSession = new OpenAIRealtimeProviderSession(sessionKey, session, this, _logger, eventSink, audioSink);
        providerSession.Start();
        await providerSession.UpdateSessionAsync(BuildNoAutoResponseSessionConfig(), createResponse: false, ct);
        return providerSession;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await ValueTask.CompletedTask;
    }

    private static string BuildInjectedEventText(VoiceConversationEventInjection injection) =>
        $"External event observed:\n{InjectionJsonFormatter.Format(injection)}";

    private static VoiceProviderEvent? MapSessionEvent(OpenAIRealtimeSessionEvent sessionEvent) =>
        sessionEvent switch
        {
            OpenAIRealtimeSpeechStartedEvent => new VoiceProviderEvent
            {
                SpeechStarted = new VoiceSpeechStarted(),
            },
            OpenAIRealtimeSpeechStoppedEvent => new VoiceProviderEvent
            {
                SpeechStopped = new VoiceSpeechStopped(),
            },
            OpenAIRealtimeResponseCreatedEvent created => new VoiceProviderEvent
            {
                ResponseStarted = new VoiceResponseStarted
                {
                    ProviderResponseId = created.ProviderResponseId,
                },
            },
            OpenAIRealtimeResponseFinishedEvent finished when finished.Cancelled => new VoiceProviderEvent
            {
                ResponseCancelled = new VoiceResponseCancelled
                {
                    ProviderResponseId = finished.ProviderResponseId,
                },
            },
            OpenAIRealtimeResponseFinishedEvent finished => new VoiceProviderEvent
            {
                ResponseDone = new VoiceResponseDone
                {
                    ProviderResponseId = finished.ProviderResponseId,
                },
            },
            OpenAIRealtimeFunctionCallEvent functionCall => new VoiceProviderEvent
            {
                FunctionCall = new VoiceFunctionCallRequested
                {
                    CallId = functionCall.CallId,
                    ToolName = functionCall.FunctionName,
                    ArgumentsJson = functionCall.ArgumentsJson,
                    ProviderResponseId = functionCall.ProviderResponseId,
                },
            },
            OpenAIRealtimeErrorEvent error when IsBenignRealtimeRaceError(error.Code) => null,
            OpenAIRealtimeErrorEvent error => new VoiceProviderEvent
            {
                Error = new VoiceProviderError
                {
                    ErrorCode = error.Code,
                    ErrorMessage = error.Message,
                },
            },
            OpenAIRealtimeDisconnectedEvent disconnected => new VoiceProviderEvent
            {
                Disconnected = new VoiceProviderDisconnected
                {
                    Reason = disconnected.Reason,
                },
            },
            _ => null,
        };

    // Benign idempotent realtime races that must NOT reach the client as fatal errors:
    //   response_cancel_not_active               — an explicit response.cancel that lost the race to
    //                                              OpenAI's server-side interrupt_response; the active
    //                                              response is already gone.
    //   conversation_already_has_active_response — a redundant response.create while one is active.
    // Both mean the cancel/create post-condition already holds, so they are no-ops, not failures.
    private static bool IsBenignRealtimeRaceError(string code) =>
        string.Equals(code, "response_cancel_not_active", StringComparison.Ordinal) ||
        string.Equals(code, "conversation_already_has_active_response", StringComparison.Ordinal);

    private BinaryData BuildSessionUpdateEvent(
        VoiceSessionConfig session,
        int sampleRateHz,
        bool? createResponseOverride = null)
    {
        // Refactor (iter94/cluster-809):
        // Old:OpenAI SDK typed beta session options.
        // New:GA shape session.update JSON envelope per OpenAI docs
        // (audio.input/audio.output/instructions/voice/tools fields per GA contract).
        var sessionObject = new JsonObject
        {
            ["type"] = "realtime",
            ["instructions"] = session.Instructions ?? string.Empty,
            ["output_modalities"] = new JsonArray("audio"),
            ["audio"] = new JsonObject
            {
                ["input"] = new JsonObject
                {
                    ["format"] = BuildPcmAudioFormat(sampleRateHz),
                    ["turn_detection"] = BuildTurnDetection(session, createResponseOverride),
                },
                ["output"] = new JsonObject
                {
                    ["format"] = BuildPcmAudioFormat(sampleRateHz),
                    ["voice"] = string.IsNullOrWhiteSpace(session.Voice)
                        ? "alloy"
                        : session.Voice.Trim(),
                },
            },
        };

        var tools = BuildTools(session);
        sessionObject["tools"] = tools;
        if (tools.Count > 0)
            sessionObject["tool_choice"] = "auto";

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var toolNames = new List<string>(tools.Count);
            foreach (var t in tools)
                toolNames.Add(t?["name"]?.GetValue<string>() ?? "?");
            _logger.LogInformation(
                "voice session.update built: {ToolCount} tools, tool_choice={ToolChoice}, instructions={InstrChars} chars, tools=[{ToolNames}]",
                tools.Count,
                tools.Count > 0 ? "auto" : "none",
                (sessionObject["instructions"]?.GetValue<string>() ?? string.Empty).Length,
                string.Join(",", toolNames));
        }

        var updateEvent = new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = sessionObject,
        };

        return BinaryData.FromString(updateEvent.ToJsonString());
    }

    private static VoiceSessionConfig BuildNoAutoResponseSessionConfig() =>
        new()
        {
            SampleRateHz = OpenAIRealtimeProviderOptions.DefaultSampleRateHz,
            TurnDetectionMode = VoiceTurnDetectionMode.ServerVad,
        };

    private JsonArray BuildTools(VoiceSessionConfig session)
    {
        var tools = new JsonArray();
        var registeredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in session.ToolDefinitions)
        {
            var toolName = definition.Name?.Trim();
            if (string.IsNullOrWhiteSpace(toolName) || !registeredNames.Add(toolName))
                continue;

            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = toolName,
                ["description"] = string.IsNullOrWhiteSpace(definition.Description)
                    ? $"Aevatar tool '{toolName}'."
                    : definition.Description.Trim(),
                ["parameters"] = BuildToolParameters(definition.ParametersSchema, toolName),
            });
        }

        for (var i = 0; i < session.ToolNames.Count; i++)
        {
            var toolName = session.ToolNames[i].Trim();
            if (string.IsNullOrWhiteSpace(toolName) || !registeredNames.Add(toolName))
                continue;

            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = toolName,
                ["description"] = $"Aevatar tool '{toolName}'.",
                ["parameters"] = BuildToolParameters(null, toolName),
            });
        }

        return tools;
    }

    private JsonNode BuildToolParameters(string? parametersSchema, string toolName)
    {
        if (string.IsNullOrWhiteSpace(parametersSchema))
            return JsonNode.Parse(PermissiveToolSchema.ToString())!;

        try
        {
            return JsonNode.Parse(parametersSchema)!;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Voice tool schema for {ToolName} is invalid JSON. Falling back to permissive schema.", toolName);
            return JsonNode.Parse(PermissiveToolSchema.ToString())!;
        }
    }

    private static JsonObject BuildPcmAudioFormat(int sampleRateHz) =>
        new()
        {
            ["type"] = "audio/pcm",
            ["rate"] = sampleRateHz,
        };

    private JsonNode? BuildTurnDetection(VoiceSessionConfig session, bool? createResponseOverride = null)
    {
        return session.TurnDetectionMode switch
        {
            VoiceTurnDetectionMode.Disabled or VoiceTurnDetectionMode.ClientVad => null,
            VoiceTurnDetectionMode.Unspecified when !_options.EnableServerVad => null,
            VoiceTurnDetectionMode.Unspecified or VoiceTurnDetectionMode.ServerVad =>
                BuildServerVadTurnDetection(session, createResponseOverride),
            _ => null,
        };
    }

    private JsonObject BuildServerVadTurnDetection(VoiceSessionConfig session, bool? createResponseOverride = null)
    {
        return new JsonObject
        {
            ["type"] = "server_vad",
            ["threshold"] = session.VadDetectionThreshold > 0
                ? session.VadDetectionThreshold
                : _options.DetectionThreshold,
            ["prefix_padding_ms"] = session.VadPrefixPaddingMs > 0
                ? session.VadPrefixPaddingMs
                : (int)_options.PrefixPadding.TotalMilliseconds,
            ["silence_duration_ms"] = session.VadSilenceDurationMs > 0
                ? session.VadSilenceDurationMs
                : (int)_options.SilenceDuration.TotalMilliseconds,
            ["interrupt_response"] = _options.InterruptResponseOnSpeech,
            ["create_response"] = createResponseOverride ?? _options.AutoCreateResponse,
        };
    }

    private int ResolveSampleRateHz(int requested)
    {
        if (requested == 0)
            return _options.SupportedSampleRateHz;

        if (requested != _options.SupportedSampleRateHz)
        {
            throw new InvalidOperationException(
                $"OpenAI realtime voice currently supports PCM16 at {_options.SupportedSampleRateHz} Hz only.");
        }

        return requested;
    }

    // Refactor (cluster-voice-nyxid-ephemeral-broker):
    //   Old pattern: config.ApiKey was a static OPENAI_API_KEY loaded from host config/env and used as-is.
    //   New principle: when a credential resolver is wired (NyxID broker mode), resolve a short-lived
    //     ephemeral key per connect; otherwise fall back to the static config key (local/direct dev).
    //   The resolved key only opens the provider connection and is never persisted.
    private async Task<VoiceProviderConfig> ResolveEffectiveConfigAsync(
        VoiceProviderSessionKey sessionKey,
        VoiceProviderConfig config,
        CancellationToken ct)
    {
        if (_credentialResolver is null)
            return config;

        var resolvedApiKey = await _credentialResolver.ResolveApiKeyAsync(sessionKey, config, ct);
        if (string.IsNullOrWhiteSpace(resolvedApiKey))
            return config;

        var effective = config.Clone();
        effective.ApiKey = resolvedApiKey;
        return effective;
    }

    private static void ValidateProviderConfig(VoiceProviderConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ProviderName) &&
            !string.Equals(config.ProviderName, "openai", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"OpenAIRealtimeProvider requires provider_name 'openai', but got '{config.ProviderName}'.");
        }

        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("OpenAI realtime provider requires api_key.");
    }

    // Refactor (iter106/cluster-106-voice-provider-session-runtime):
    //   Old pattern: Realtime voice providers and the module keep provider session, event channel, cancellation source, dispatch loop, and transport pump as process-local mutable runtime objects.
    //   New principle: Provider callbacks emit typed signals with lease/session keys; session ownership and pump lifecycle are actor-owned or distributed state, while provider objects are disposable transport handles only.
    private sealed class OpenAIRealtimeProviderSession : RealtimeVoiceProviderSession
    {
        private readonly VoiceProviderSessionKey _callbackKey;
        private readonly IOpenAIRealtimeSession _physicalSession;
        private readonly OpenAIRealtimeProvider _connector;
        private readonly ILogger _logger;
        private readonly Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> _eventSink;
        private readonly Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> _audioSink;
        private readonly CancellationTokenSource _physicalSessionCancellation = new();
        private Task? _receiveTask;
        private bool _disposed;
        private int _outputSampleRateHz = OpenAIRealtimeProviderOptions.DefaultSampleRateHz;

        public OpenAIRealtimeProviderSession(
            VoiceProviderSessionKey sessionKey,
            IOpenAIRealtimeSession session,
            OpenAIRealtimeProvider connector,
            ILogger logger,
            Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
            Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink)
        {
            _callbackKey = sessionKey;
            _physicalSession = session;
            _connector = connector;
            _logger = logger;
            _eventSink = eventSink;
            _audioSink = audioSink;
        }

        public void Start()
        {
            _receiveTask = RunReceiveLoopAsync(_physicalSessionCancellation.Token);
        }

        public override Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            if (pcm16.IsEmpty)
                return Task.CompletedTask;

            return _physicalSession.SendInputAudioAsync(BinaryData.FromBytes(pcm16.ToArray()), ct);
        }

        public override async Task SendInputImageAsync(VoiceInputImage inputImage, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(inputImage);
            if (inputImage.Data.IsEmpty)
                return;

            var mediaType = string.IsNullOrWhiteSpace(inputImage.MediaType)
                ? "image/png"
                : inputImage.MediaType.Trim();
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Image media_type must start with 'image/'.", nameof(inputImage));

            await _physicalSession.SendInputImageAsync(BuildInputImageEvent(inputImage, mediaType), ct);
            await _physicalSession.StartResponseAsync(ct);
        }

        public override async Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(callId))
                throw new ArgumentException("call_id is required.", nameof(callId));

            await _physicalSession.AddItemAsync(
                new RealtimeFunctionCallOutputItem(callId, resultJson ?? string.Empty),
                ct);
            await _physicalSession.StartResponseAsync(ct);
        }

        public override Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(injection);
            return InjectUserTextAsync(BuildInjectedEventText(injection), ct);
        }

        public override Task CancelResponseAsync(CancellationToken ct) =>
            _physicalSession.CancelResponseAsync(ct);

        public override async Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(session);

            _outputSampleRateHz = _connector.ResolveSampleRateHz(session.SampleRateHz);
            await _physicalSession.SendSessionUpdateAsync(_connector.BuildSessionUpdateEvent(session, _outputSampleRateHz), ct);
        }

        internal async Task UpdateSessionAsync(
            VoiceSessionConfig session,
            bool createResponse,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(session);

            _outputSampleRateHz = _connector.ResolveSampleRateHz(session.SampleRateHz);
            await _physicalSession.SendSessionUpdateAsync(
                _connector.BuildSessionUpdateEvent(session, _outputSampleRateHz, createResponse),
                ct);
        }

        internal async Task InjectUserTextAsync(string text, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text input is required.", nameof(text));

            var item = new RealtimeMessageItem(
                RealtimeMessageRole.User,
                [new RealtimeInputTextMessageContentPart(text)]);

            await _physicalSession.AddItemAsync(item, ct);
            await _physicalSession.StartResponseAsync(ct);
        }

        private static BinaryData BuildInputImageEvent(VoiceInputImage inputImage, string mediaType)
        {
            var eventObject = new JsonObject
            {
                ["type"] = "conversation.item.create",
                ["item"] = new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = $"data:{mediaType};base64,{Convert.ToBase64String(inputImage.Data.ToByteArray())}",
                        },
                    },
                },
            };

            return BinaryData.FromString(eventObject.ToJsonString());
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            _physicalSessionCancellation.Cancel();
            await AwaitLoopAsync(_receiveTask);
            await _physicalSession.DisposeAsync();
            _physicalSessionCancellation.Dispose();
        }

        private async Task RunReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var sessionEvent in _physicalSession.ReceiveEventsAsync(ct).WithCancellation(ct))
                {
                    if (sessionEvent is OpenAIRealtimeOutputAudioDeltaEvent audio)
                    {
                        await EmitAudioAsync(new VoiceProviderAudioFrame(
                            audio.Pcm16,
                            _outputSampleRateHz,
                            audio.ProviderResponseId), ct);
                        continue;
                    }

                    // Genuine server-side rejections (rate_limit, auth, …) surface at Warning and reach
                    // the client as an Error frame. Benign idempotent races
                    // (response_cancel_not_active from losing to interrupt_response;
                    // conversation_already_has_active_response from a redundant response.create) are
                    // expected on every barge-in — log at Debug and let MapSessionEvent drop them so a
                    // working voice session is never shown to the user as a fatal error.
                    if (sessionEvent is OpenAIRealtimeErrorEvent errorEvent)
                    {
                        if (IsBenignRealtimeRaceError(errorEvent.Code))
                            _logger.LogDebug(
                                "OpenAI realtime benign race error code={ErrorCode} message={ErrorMessage} (suppressed)",
                                errorEvent.Code,
                                errorEvent.Message);
                        else
                            _logger.LogWarning(
                                "OpenAI realtime error code={ErrorCode} message={ErrorMessage}",
                                errorEvent.Code,
                                errorEvent.Message);
                    }

                    var providerEvent = MapSessionEvent(sessionEvent);
                    if (providerEvent != null)
                        await EmitAsync(providerEvent, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenAI realtime receive loop terminated unexpectedly.");
                await EmitAsync(BuildDisconnected($"error:{ex.Message}"), CancellationToken.None);
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
                _logger.LogWarning(ex, "OpenAI realtime provider callback failed for event {EventCase}.",
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
                _logger.LogWarning(ex, "OpenAI realtime provider audio callback failed.");
            }
        }
    }

    private static VoiceProviderEvent BuildDisconnected(string reason) =>
        new()
        {
            Disconnected = new VoiceProviderDisconnected
            {
                Reason = reason,
            },
        };

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
