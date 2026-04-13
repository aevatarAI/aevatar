using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.Foundation.VoicePresence.Abstractions;
using OpenAI.Realtime;
using System.ClientModel;

namespace Aevatar.Foundation.VoicePresence.OpenAI.Internal;

internal sealed class OpenAIRealtimeSessionFactory : IOpenAIRealtimeSessionFactory
{
    public async Task<IOpenAIRealtimeSession> StartConversationSessionAsync(
        VoiceProviderConfig config,
        string defaultModel,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        var options = BuildClientOptions(config.Endpoint);
        var client = options == null
            ? new RealtimeClient(new ApiKeyCredential(config.ApiKey))
            : new RealtimeClient(new ApiKeyCredential(config.ApiKey), options);

        var model = string.IsNullOrWhiteSpace(config.Model)
            ? defaultModel
            : config.Model.Trim();

        var session = await client.StartConversationSessionAsync(
            model,
            new RealtimeSessionClientOptions(),
            ct);

        return new OpenAIRealtimeSession(session);
    }

    private static RealtimeClientOptions? BuildClientOptions(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        var normalized = NormalizeEndpoint(endpoint.Trim());
        return new RealtimeClientOptions
        {
            Endpoint = normalized,
        };
    }

    private static Uri NormalizeEndpoint(string endpoint)
    {
        var uri = new Uri(endpoint, UriKind.Absolute);
        return uri.Scheme switch
        {
            "wss" => new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps }.Uri,
            "ws" => new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp }.Uri,
            _ => uri,
        };
    }
}

internal sealed class OpenAIRealtimeSession : IOpenAIRealtimeSession
{
    private readonly RealtimeSessionClient _session;

    public OpenAIRealtimeSession(RealtimeSessionClient session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task ConfigureConversationSessionAsync(RealtimeConversationSessionOptions options, CancellationToken ct) =>
        _session.ConfigureConversationSessionAsync(options, ct);

    public Task SendInputAudioAsync(BinaryData audio, CancellationToken ct) =>
        _session.SendInputAudioAsync(audio, ct);

    public Task AddItemAsync(RealtimeItem item, CancellationToken ct) =>
        _session.AddItemAsync(item, ct);

    public Task StartResponseAsync(CancellationToken ct) =>
        _session.StartResponseAsync(ct);

    public Task CancelResponseAsync(CancellationToken ct) =>
        _session.CancelResponseAsync(ct);

    public async IAsyncEnumerable<OpenAIRealtimeSessionEvent> ReceiveEventsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        Exception? failure = null;

        await using var enumerator = _session.ReceiveUpdatesAsync(ct).GetAsyncEnumerator(ct);
        while (true)
        {
            RealtimeServerUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync())
                    break;

                update = enumerator.Current;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex)
            {
                failure = ex;
                break;
            }

            if (TryMap(update, out var sessionEvent))
                yield return sessionEvent;
        }

        if (ct.IsCancellationRequested)
            yield break;

        if (failure != null)
        {
            yield return new OpenAIRealtimeDisconnectedEvent($"error:{failure.Message}");
            yield break;
        }

        if (_session.WebSocket.State != System.Net.WebSockets.WebSocketState.Open)
        {
            yield return new OpenAIRealtimeDisconnectedEvent(
                $"websocket-state:{_session.WebSocket.State}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _session.Dispose();
        return ValueTask.CompletedTask;
    }

    private static bool TryMap(RealtimeServerUpdate update, out OpenAIRealtimeSessionEvent sessionEvent)
    {
        switch (update)
        {
            case RealtimeServerUpdateInputAudioBufferSpeechStarted:
                sessionEvent = new OpenAIRealtimeSpeechStartedEvent();
                return true;
            case RealtimeServerUpdateInputAudioBufferSpeechStopped:
                sessionEvent = new OpenAIRealtimeSpeechStoppedEvent();
                return true;
            case RealtimeServerUpdateResponseCreated created when !string.IsNullOrWhiteSpace(created.Response?.Id):
                sessionEvent = new OpenAIRealtimeResponseCreatedEvent(created.Response.Id);
                return true;
            case RealtimeServerUpdateResponseDone done when !string.IsNullOrWhiteSpace(done.Response?.Id):
                sessionEvent = new OpenAIRealtimeResponseFinishedEvent(
                    done.Response.Id,
                    done.Response.Status == RealtimeResponseStatus.Cancelled);
                return true;
            case RealtimeServerUpdateResponseOutputAudioDelta audio when !string.IsNullOrWhiteSpace(audio.ResponseId):
                sessionEvent = new OpenAIRealtimeOutputAudioDeltaEvent(audio.ResponseId, audio.Delta.ToArray());
                return true;
            case RealtimeServerUpdateResponseFunctionCallArgumentsDone functionCall
                when !string.IsNullOrWhiteSpace(functionCall.ResponseId):
                sessionEvent = new OpenAIRealtimeFunctionCallEvent(
                    functionCall.ResponseId,
                    functionCall.CallId ?? string.Empty,
                    functionCall.FunctionName ?? string.Empty,
                    Encoding.UTF8.GetString(functionCall.FunctionArguments.ToArray()));
                return true;
            case RealtimeServerUpdateError error:
                sessionEvent = new OpenAIRealtimeErrorEvent(
                    error.Error?.Code ?? "unknown",
                    error.Error?.Message ?? string.Empty);
                return true;
            default:
                sessionEvent = null!;
                return false;
        }
    }
}
