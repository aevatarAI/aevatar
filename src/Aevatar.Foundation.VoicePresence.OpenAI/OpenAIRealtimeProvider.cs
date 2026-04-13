using System.Threading.Channels;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.OpenAI.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Realtime;

namespace Aevatar.Foundation.VoicePresence.OpenAI;

/// <summary>
/// OpenAI Realtime GA implementation of <see cref="IRealtimeVoiceProvider" />.
/// </summary>
public sealed class OpenAIRealtimeProvider : IRealtimeVoiceProvider
{
    private static readonly BinaryData PermissiveToolSchema =
        BinaryData.FromString("""{"type":"object","additionalProperties":true}""");

    private readonly IOpenAIRealtimeSessionFactory _sessionFactory;
    private readonly OpenAIRealtimeProviderOptions _options;
    private readonly ILogger _logger;

    private readonly Dictionary<string, int> _responseEpochs = [];

    private IOpenAIRealtimeSession? _session;
    private Channel<VoiceProviderEvent>? _eventChannel;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _receiveLoop;
    private Task? _dispatchLoop;
    private bool _disposed;
    private int _nextResponseId;
    private int _sampleRateHz = OpenAIRealtimeProviderOptions.DefaultSampleRateHz;

    public OpenAIRealtimeProvider(
        OpenAIRealtimeProviderOptions? options = null,
        ILogger<OpenAIRealtimeProvider>? logger = null)
        : this(
            new OpenAIRealtimeSessionFactory(),
            options ?? new OpenAIRealtimeProviderOptions(),
            logger ?? NullLogger<OpenAIRealtimeProvider>.Instance)
    {
    }

    internal OpenAIRealtimeProvider(
        IOpenAIRealtimeSessionFactory sessionFactory,
        OpenAIRealtimeProviderOptions options,
        ILogger logger)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Func<VoiceProviderEvent, CancellationToken, Task>? OnEvent { private get; set; }

    public async Task ConnectAsync(VoiceProviderConfig config, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(config);
        EnsureDisconnected();
        ValidateProviderConfig(config);

        _session = await _sessionFactory.StartConversationSessionAsync(config, _options.DefaultModel, ct);
        _eventChannel = Channel.CreateBounded<VoiceProviderEvent>(new BoundedChannelOptions(_options.EventQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = RunReceiveLoopAsync(_session, _eventChannel.Writer, _lifetimeCts.Token);
        _dispatchLoop = RunDispatchLoopAsync(_eventChannel.Reader, _lifetimeCts.Token);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
    {
        if (pcm16.IsEmpty)
            return Task.CompletedTask;

        return EnsureSession().SendInputAudioAsync(BinaryData.FromBytes(pcm16.ToArray()), ct);
    }

    public async Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callId))
            throw new ArgumentException("call_id is required.", nameof(callId));

        var session = EnsureSession();
        await session.AddItemAsync(
            new RealtimeFunctionCallOutputItem(callId, resultJson ?? string.Empty),
            ct);
        await session.StartResponseAsync(ct);
    }

    public Task CancelResponseAsync(CancellationToken ct) =>
        EnsureSession().CancelResponseAsync(ct);

    public async Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        _sampleRateHz = ResolveSampleRateHz(session.SampleRateHz);
        await EnsureSession().ConfigureConversationSessionAsync(BuildConversationSessionOptions(session), ct);
    }

    internal async Task InjectUserTextAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text input is required.", nameof(text));

        var item = new RealtimeMessageItem(
            RealtimeMessageRole.User,
            [new RealtimeInputTextMessageContentPart(text)]);

        var session = EnsureSession();
        await session.AddItemAsync(item, ct);
        await session.StartResponseAsync(ct);
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

        await AwaitLoopAsync(_receiveLoop);
        await AwaitLoopAsync(_dispatchLoop);

        _receiveLoop = null;
        _dispatchLoop = null;
        _eventChannel = null;

        if (_session != null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        cts?.Dispose();
    }

    private async Task RunReceiveLoopAsync(
        IOpenAIRealtimeSession session,
        ChannelWriter<VoiceProviderEvent> writer,
        CancellationToken ct)
    {
        try
        {
            await foreach (var sessionEvent in session.ReceiveEventsAsync(ct).WithCancellation(ct))
            {
                var providerEvent = MapSessionEvent(sessionEvent);
                if (providerEvent != null)
                    await writer.WriteAsync(providerEvent, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI realtime receive loop terminated unexpectedly.");
            TryWriteDisconnected(writer, $"error:{ex.Message}");
        }
        finally
        {
            writer.TryComplete();
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
                    _logger.LogWarning(ex, "OpenAI realtime provider callback failed for event {EventCase}.",
                        providerEvent.EventCase);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private VoiceProviderEvent? MapSessionEvent(OpenAIRealtimeSessionEvent sessionEvent) =>
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
                    ResponseId = ResolveResponseEpoch(created.ProviderResponseId),
                },
            },
            OpenAIRealtimeResponseFinishedEvent finished when finished.Cancelled => new VoiceProviderEvent
            {
                ResponseCancelled = new VoiceResponseCancelled
                {
                    ResponseId = ResolveAndRetireResponseEpoch(finished.ProviderResponseId),
                },
            },
            OpenAIRealtimeResponseFinishedEvent finished => new VoiceProviderEvent
            {
                ResponseDone = new VoiceResponseDone
                {
                    ResponseId = ResolveAndRetireResponseEpoch(finished.ProviderResponseId),
                },
            },
            OpenAIRealtimeOutputAudioDeltaEvent audio => new VoiceProviderEvent
            {
                AudioReceived = new VoiceAudioReceived
                {
                    Pcm16 = Google.Protobuf.ByteString.CopyFrom(audio.Pcm16),
                    SampleRateHz = _sampleRateHz,
                },
            },
            OpenAIRealtimeFunctionCallEvent functionCall => new VoiceProviderEvent
            {
                FunctionCall = new VoiceFunctionCallRequested
                {
                    CallId = functionCall.CallId,
                    ToolName = functionCall.FunctionName,
                    ArgumentsJson = functionCall.ArgumentsJson,
                    ResponseId = ResolveResponseEpoch(functionCall.ProviderResponseId),
                },
            },
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

    private RealtimeConversationSessionOptions BuildConversationSessionOptions(VoiceSessionConfig session)
    {
        var options = new RealtimeConversationSessionOptions
        {
            Instructions = session.Instructions ?? string.Empty,
            AudioOptions = new RealtimeConversationSessionAudioOptions
            {
                InputAudioOptions = BuildInputAudioOptions(),
                OutputAudioOptions = BuildOutputAudioOptions(session),
            },
        };
        options.OutputModalities.Add(RealtimeOutputModality.Audio);

        for (var i = 0; i < session.ToolNames.Count; i++)
        {
            var toolName = session.ToolNames[i].Trim();
            if (string.IsNullOrWhiteSpace(toolName))
                continue;

            // Phase 2 only needs provider-side tool registration. Tool execution wiring lands in phase 4.
            options.Tools.Add(new RealtimeFunctionTool(toolName)
            {
                FunctionDescription = $"Aevatar tool '{toolName}'.",
                FunctionParameters = PermissiveToolSchema,
            });
        }

        if (options.Tools.Count > 0)
            options.ToolChoice = new RealtimeToolChoice(RealtimeDefaultToolChoice.Auto);

        return options;
    }

    private RealtimeConversationSessionInputAudioOptions BuildInputAudioOptions()
    {
        var options = new RealtimeConversationSessionInputAudioOptions();
        if (_options.EnableServerVad)
        {
            options.TurnDetection = new RealtimeServerVadTurnDetection
            {
                DetectionThreshold = _options.DetectionThreshold,
                PrefixPadding = _options.PrefixPadding,
                SilenceDuration = _options.SilenceDuration,
                InterruptResponseEnabled = _options.InterruptResponseOnSpeech,
                CreateResponseEnabled = _options.AutoCreateResponse,
            };
        }

        return options;
    }

    private static RealtimeConversationSessionOutputAudioOptions BuildOutputAudioOptions(VoiceSessionConfig session) =>
        new()
        {
            Voice = string.IsNullOrWhiteSpace(session.Voice)
                ? RealtimeVoice.Alloy
                : new RealtimeVoice(session.Voice.Trim()),
        };

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

    private int ResolveResponseEpoch(string providerResponseId)
    {
        if (string.IsNullOrWhiteSpace(providerResponseId))
            return ++_nextResponseId;

        if (_responseEpochs.TryGetValue(providerResponseId, out var existing))
            return existing;

        var next = ++_nextResponseId;
        _responseEpochs[providerResponseId] = next;
        return next;
    }

    private int ResolveAndRetireResponseEpoch(string providerResponseId)
    {
        var epoch = ResolveResponseEpoch(providerResponseId);
        if (!string.IsNullOrWhiteSpace(providerResponseId))
            _responseEpochs.Remove(providerResponseId);
        return epoch;
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

    private IOpenAIRealtimeSession EnsureSession() =>
        _session ?? throw new InvalidOperationException("OpenAI realtime provider is not connected.");

    private void EnsureDisconnected()
    {
        if (_session != null)
            throw new InvalidOperationException("OpenAI realtime provider is already connected.");
    }

    private static void TryWriteDisconnected(ChannelWriter<VoiceProviderEvent> writer, string reason)
    {
        writer.TryWrite(new VoiceProviderEvent
        {
            Disconnected = new VoiceProviderDisconnected
            {
                Reason = reason,
            },
        });
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
