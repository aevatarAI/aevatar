using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Hosting;
using Aevatar.AGUI.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public static partial class NyxIdChatEndpoints
{
    internal static TimeSpan StreamKeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    private static async Task HandleStreamMessageAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        NyxIdChatStreamRequest request,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] ICommandInteractionService<NyxIdChatCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus> interactionService,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Endpoints");
        var accessToken = string.Empty;
        var prompt = string.Empty;
        var messageId = request.SessionId ?? Guid.NewGuid().ToString("N");

        try
        {
            // Refactor (iter21/cluster-002-request-path-projection-session-priming):
            //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
            //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            accessToken = ExtractBearerToken(http);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            prompt = request.Prompt?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt) && request.InputParts is not { Count: > 0 })
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!await TryAuthorizeConversationAsync(
                    http,
                    admissionPort,
                    scopeId,
                    actorId,
                    ScopeResourceOperation.Stream,
                    ct))
                return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID chat request setup failed for actor {ActorId}", actorId);
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var writer = new NyxIdChatSseWriter(http.Response);
        var writerLock = new SemaphoreSlim(1, 1);
        await using var heartbeat = new NyxIdChatStreamKeepAlive(
            writer,
            writerLock,
            actorId,
            messageId,
            StreamKeepAliveInterval,
            ct);
        try
        {
            await writer.StartAsync(ct);
            await WriteSerializedAsync(writerLock, token => writer.WriteRunStartedAsync(actorId, token), ct);
            heartbeat.Start();
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            var llmControl = await BuildLlmControlAsync(http, accessToken, ct);
            await InjectConnectedServicesAsync(http, accessToken, metadata, ct);

            // Refactor (iter56/cluster-868-endpoint-runtime-lifecycle): old=endpoint direct IActorRuntime, new=IGAgentDraftRunInteractionPort + CQRS Core
            // Streaming endpoints no longer pre-read runtime state before command dispatch.
            // The CQRS command target resolver owns actor lookup and reports typed start errors.
            // Endpoint responsibility stays at auth, admission, input mapping, and SSE writing.
            var result = await interactionService.ExecuteAsync(
                new NyxIdChatCommand(
                    actorId,
                    scopeId,
                    prompt,
                    messageId,
                    accessToken,
                    request.InputParts,
                    metadata,
                    llmControl),
                async (evt, token) =>
                {
                    if (IsTerminalFrame(evt))
                        heartbeat.Stop();

                    await WriteSerializedAsync(
                        writerLock,
                        async writeToken =>
                        {
                            await NyxIdChatAguiSseEventWriter.WriteAsync(evt, messageId, writer, writeToken);
                        },
                        token);
                },
                null,
                ct);

            if (!result.Succeeded)
                heartbeat.Stop();
            await HandleInteractionFailureAsync(
                result,
                writer,
                writerLock,
                "The chat request failed. Please try again.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID streaming request failed for actor {ActorId}", actorId);
            heartbeat.Stop();
            await WriteSerializedAsync(
                writerLock,
                token => writer.WriteRunErrorAsync("The chat request failed. Please try again.", token),
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Handles tool approval decisions from the frontend.
    /// Opens an SSE connection to stream the continuation chat response.
    /// </summary>
    private static async Task HandleApproveAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        NyxIdApprovalRequest request,
        [FromServices] IScopeResourceAdmissionPort admissionPort,
        [FromServices] ICommandInteractionService<NyxIdApprovalCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus> interactionService,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Endpoints");
        var messageId = request.SessionId ?? scopeId;

        try
        {
            // Refactor (iter21/cluster-002-request-path-projection-session-priming):
            //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
            //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var accessToken = ExtractBearerToken(http);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!await TryAuthorizeConversationAsync(
                    http,
                    admissionPort,
                    scopeId,
                    actorId,
                    ScopeResourceOperation.Approve,
                    ct))
                return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID approval request setup failed for actor {ActorId}", actorId);
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var writer = new NyxIdChatSseWriter(http.Response);
        var writerLock = new SemaphoreSlim(1, 1);
        await using var heartbeat = new NyxIdChatStreamKeepAlive(
            writer,
            writerLock,
            actorId,
            messageId,
            StreamKeepAliveInterval,
            ct);
        try
        {
            await writer.StartAsync(ct);
            await WriteSerializedAsync(writerLock, token => writer.WriteRunStartedAsync(actorId, token), ct);
            heartbeat.Start();
            // Refactor (iter56/cluster-868-endpoint-runtime-lifecycle): old=endpoint direct IActorRuntime, new=IGAgentDraftRunInteractionPort + CQRS Core
            // Approval continuation follows the same resolver-owned lookup path as chat streaming.
            // Missing actors are typed command start failures, not Host-side runtime probes.
            // This keeps endpoint lifecycle independent from the actor runtime implementation.
            var result = await interactionService.ExecuteAsync(
                new NyxIdApprovalCommand(
                    actorId,
                    request.RequestId,
                    request.Approved,
                    request.Reason ?? string.Empty,
                    messageId),
                async (evt, token) =>
                {
                    if (IsTerminalFrame(evt))
                        heartbeat.Stop();

                    await WriteSerializedAsync(
                        writerLock,
                        async writeToken =>
                        {
                            await NyxIdChatAguiSseEventWriter.WriteAsync(evt, messageId, writer, writeToken);
                        },
                        token);
                },
                null,
                ct);

            if (!result.Succeeded)
                heartbeat.Stop();
            await HandleInteractionFailureAsync(
                result,
                writer,
                writerLock,
                "The approval continuation failed. Please try again.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID approval streaming request failed for actor {ActorId}", actorId);
            heartbeat.Stop();
            await WriteSerializedAsync(
                writerLock,
                token => writer.WriteRunErrorAsync("The approval continuation failed. Please try again.", token),
                CancellationToken.None);
        }
    }

    private static async Task HandleInteractionFailureAsync(
        CommandInteractionResult<NyxIdChatAcceptedReceipt, NyxIdChatStartError, NyxIdChatCompletionStatus> result,
        NyxIdChatSseWriter writer,
        SemaphoreSlim writerLock,
        string message)
    {
        if (result.Succeeded)
            return;

        await WriteSerializedAsync(
            writerLock,
            token => writer.WriteRunErrorAsync(
                result.Error switch
                {
                    NyxIdChatStartError.ProjectionUnavailable => "NyxID chat projection pipeline is unavailable.",
                    NyxIdChatStartError.ActorNotFound => "NyxID chat conversation was not found.",
                    _ => message,
                },
                token),
            CancellationToken.None);
    }

    private static async ValueTask WriteSerializedAsync(
        SemaphoreSlim writerLock,
        Func<CancellationToken, ValueTask> writeAsync,
        CancellationToken ct)
    {
        await writerLock.WaitAsync(ct);
        try
        {
            await writeAsync(ct);
        }
        finally
        {
            writerLock.Release();
        }
    }

    private static bool IsTerminalFrame(AGUIEvent evt) =>
        evt.EventCase is AGUIEvent.EventOneofCase.RunFinished or AGUIEvent.EventOneofCase.RunError;

    private sealed class NyxIdChatStreamKeepAlive : IAsyncDisposable
    {
        private readonly NyxIdChatSseWriter _writer;
        private readonly SemaphoreSlim _writerLock;
        private readonly string _actorId;
        private readonly string _sessionId;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _cts;
        private Task? _loop;

        public NyxIdChatStreamKeepAlive(
            NyxIdChatSseWriter writer,
            SemaphoreSlim writerLock,
            string actorId,
            string sessionId,
            TimeSpan interval,
            CancellationToken requestCancellationToken)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _writerLock = writerLock ?? throw new ArgumentNullException(nameof(writerLock));
            _actorId = actorId;
            _sessionId = sessionId;
            _interval = interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : interval;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken);
        }

        public void Start()
        {
            if (_loop != null)
                return;

            _loop = RunAsync();
        }

        public void Stop() => _cts.Cancel();

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            if (_loop != null)
            {
                try
                {
                    await _loop;
                }
                catch (OperationCanceledException)
                {
                }
            }

            _cts.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(_interval);
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    await WriteSerializedAsync(
                        _writerLock,
                        token => _writer.WriteKeepAliveAsync(_actorId, _sessionId, token),
                        _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }
    }

    public sealed record NyxIdApprovalRequest(
        string? RequestId,
        bool Approved = true,
        string? Reason = null,
        string? SessionId = null);

    public sealed record NyxIdChatStreamRequest(
        string? Prompt,
        string? SessionId = null,
        IReadOnlyList<ContentPartDto>? InputParts = null);

    public sealed record ContentPartDto(
        string Type,
        string? Text = null,
        string? DataBase64 = null,
        string? MediaType = null,
        string? Uri = null,
        string? Name = null)
    {
        public ChatContentPart ToProto() => new()
        {
            Kind = Type?.ToLowerInvariant() switch
            {
                "image" => ChatContentPartKind.Image,
                "audio" => ChatContentPartKind.Audio,
                "video" => ChatContentPartKind.Video,
                "text" => ChatContentPartKind.Text,
                _ => ChatContentPartKind.Unspecified,
            },
            Text = Text ?? string.Empty,
            DataBase64 = DataBase64 ?? string.Empty,
            MediaType = MediaType ?? string.Empty,
            Uri = Uri ?? string.Empty,
            Name = Name ?? string.Empty,
        };
    }
}
