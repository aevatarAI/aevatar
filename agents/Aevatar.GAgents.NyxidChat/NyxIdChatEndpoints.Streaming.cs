using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Capabilities;
using Aevatar.AGUI.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgents.NyxidChat;

public static partial class NyxIdChatEndpoints
{
    internal static TimeSpan StreamKeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);
    internal static TimeSpan StreamTerminalTimeout { get; set; } = TimeSpan.FromMinutes(5);

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
        var turnId = CreateTurnId(actorId, ResolveClientRequestId(http, request.ClientRequestId));

        try
        {
            // Refactor (iter21/cluster-002-request-path-projection-session-priming):
            //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
            //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            accessToken = ExtractNyxIdAccessToken(http);
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
        var terminalWritten = false;
        await using var heartbeat = new NyxIdChatStreamKeepAlive(
            writer,
            writerLock,
            logger,
            actorId,
            turnId,
            StreamKeepAliveInterval,
            ct);
        using var terminalDeadline = CreateTerminalDeadline(ct);
        try
        {
            await writer.StartAsync(ct);
            await WriteSerializedAsync(writerLock, token => writer.WriteRunStartedAsync(actorId, turnId, token), ct);
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
                    turnId,
                    accessToken,
                    request.InputParts,
                    metadata,
                    llmControl),
                async (evt, token) =>
                {
                    var isTerminal = IsTerminalFrame(evt);
                    if (isTerminal)
                        heartbeat.Stop();

                    await WriteSerializedAsync(
                        writerLock,
                        async writeToken =>
                        {
                            if (terminalWritten)
                                return;

                            await NyxIdChatAguiSseEventWriter.WriteAsync(evt, turnId, writer, writeToken);
                            if (isTerminal)
                                terminalWritten = true;
                        },
                        token);
                },
                null,
                terminalDeadline.Token);

            if (!result.Succeeded)
                heartbeat.Stop();
            await HandleInteractionFailureAsync(
                result,
                writer,
                writerLock,
                turnId,
                "The chat request failed. Please try again.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && terminalDeadline.IsCancellationRequested)
        {
            logger.LogWarning("NyxID chat stream timed out for actor {ActorId} turn {TurnId}", actorId, turnId);
            heartbeat.Stop();
            if (!terminalWritten)
            {
                await WriteSerializedAsync(
                    writerLock,
                    token => writer.WriteRunErrorAsync(
                        turnId,
                        "STREAM_TIMEOUT",
                        "The chat request timed out. Please try again.",
                        0,
                        token),
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            heartbeat.Stop();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID streaming request failed for actor {ActorId}", actorId);
            heartbeat.Stop();
            if (!terminalWritten)
            {
                await WriteSerializedAsync(
                    writerLock,
                    token => writer.WriteRunErrorAsync(
                        turnId,
                        "STREAM_FAILURE",
                        "The chat request failed. Please try again.",
                        0,
                        token),
                    CancellationToken.None);
            }
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
        var turnId = CreateTurnId(actorId, clientRequestId: null);

        try
        {
            // Refactor (iter21/cluster-002-request-path-projection-session-priming):
            //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
            //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var accessToken = ExtractNyxIdAccessToken(http);
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
        var terminalWritten = false;
        await using var heartbeat = new NyxIdChatStreamKeepAlive(
            writer,
            writerLock,
            logger,
            actorId,
            turnId,
            StreamKeepAliveInterval,
            ct);
        using var terminalDeadline = CreateTerminalDeadline(ct);
        try
        {
            await writer.StartAsync(ct);
            await WriteSerializedAsync(writerLock, token => writer.WriteRunStartedAsync(actorId, turnId, token), ct);
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
                    turnId),
                async (evt, token) =>
                {
                    var isTerminal = IsTerminalFrame(evt);
                    if (isTerminal)
                        heartbeat.Stop();

                    await WriteSerializedAsync(
                        writerLock,
                        async writeToken =>
                        {
                            if (terminalWritten)
                                return;

                            await NyxIdChatAguiSseEventWriter.WriteAsync(evt, turnId, writer, writeToken);
                            if (isTerminal)
                                terminalWritten = true;
                        },
                        token);
                },
                null,
                terminalDeadline.Token);

            if (!result.Succeeded)
                heartbeat.Stop();
            await HandleInteractionFailureAsync(
                result,
                writer,
                writerLock,
                turnId,
                "The approval continuation failed. Please try again.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && terminalDeadline.IsCancellationRequested)
        {
            logger.LogWarning("NyxID approval stream timed out for actor {ActorId} turn {TurnId}", actorId, turnId);
            heartbeat.Stop();
            if (!terminalWritten)
            {
                await WriteSerializedAsync(
                    writerLock,
                    token => writer.WriteRunErrorAsync(
                        turnId,
                        "APPROVAL_STREAM_TIMEOUT",
                        "The approval continuation timed out. Please try again.",
                        0,
                        token),
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            heartbeat.Stop();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID approval streaming request failed for actor {ActorId}", actorId);
            heartbeat.Stop();
            if (!terminalWritten)
            {
                await WriteSerializedAsync(
                    writerLock,
                    token => writer.WriteRunErrorAsync(
                        turnId,
                        "STREAM_FAILURE",
                        "The approval continuation failed. Please try again.",
                        0,
                        token),
                    CancellationToken.None);
            }
        }
    }

    private static async Task HandleInteractionFailureAsync(
        CommandInteractionResult<NyxIdChatAcceptedReceipt, NyxIdChatStartError, NyxIdChatCompletionStatus> result,
        NyxIdChatSseWriter writer,
        SemaphoreSlim writerLock,
        string turnId,
        string message)
    {
        if (result.Succeeded)
            return;

        await WriteSerializedAsync(
            writerLock,
            token => writer.WriteRunErrorAsync(
                turnId,
                result.Error switch
                {
                    NyxIdChatStartError.ProjectionUnavailable => "PROJECTION_UNAVAILABLE",
                    NyxIdChatStartError.ActorNotFound => "ACTOR_NOT_FOUND",
                    _ => "COMMAND_START_FAILED",
                },
                result.Error switch
                {
                    NyxIdChatStartError.ProjectionUnavailable => "NyxID chat projection pipeline is unavailable.",
                    NyxIdChatStartError.ActorNotFound => "NyxID chat conversation was not found.",
                    _ => message,
                },
                0,
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

    private static string? ResolveClientRequestId(HttpContext http, string? clientRequestId)
    {
        if (!string.IsNullOrWhiteSpace(clientRequestId))
            return clientRequestId.Trim();

        var headerValue = http.Request.Headers["Idempotency-Key"].ToString();
        return string.IsNullOrWhiteSpace(headerValue) ? null : headerValue.Trim();
    }

    private static string CreateTurnId(string actorId, string? clientRequestId)
    {
        if (string.IsNullOrWhiteSpace(clientRequestId))
            return $"turn-{Guid.NewGuid():N}";

        var normalizedActorId = actorId?.Trim() ?? string.Empty;
        var normalizedClientRequestId = clientRequestId.Trim();
        var identity = $"{normalizedActorId.Length}:{normalizedActorId}{normalizedClientRequestId.Length}:{normalizedClientRequestId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"turn-{Convert.ToHexStringLower(hash)[..32]}";
    }

    private static CancellationTokenSource CreateTerminalDeadline(CancellationToken requestCancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken);
        var timeout = StreamTerminalTimeout <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(5)
            : StreamTerminalTimeout;
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private sealed class NyxIdChatStreamKeepAlive : IAsyncDisposable
    {
        private readonly NyxIdChatSseWriter _writer;
        private readonly SemaphoreSlim _writerLock;
        private readonly ILogger _logger;
        private readonly string _actorId;
        private readonly string _turnId;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _cts;
        private Task? _loop;

        public NyxIdChatStreamKeepAlive(
            NyxIdChatSseWriter writer,
            SemaphoreSlim writerLock,
            ILogger logger,
            string actorId,
            string turnId,
            TimeSpan interval,
            CancellationToken requestCancellationToken)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _writerLock = writerLock ?? throw new ArgumentNullException(nameof(writerLock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _actorId = actorId;
            _turnId = turnId;
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
                        token => _writer.WriteKeepAliveAsync(_actorId, _turnId, token),
                        _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NyxID chat keepalive stopped for actor {ActorId}", _actorId);
            }
        }
    }

    public sealed record NyxIdApprovalRequest(
        string? RequestId,
        bool Approved = true,
        string? Reason = null,
        [property: Obsolete("sessionId is deprecated and ignored. Approval continuation turn identity is server-owned.")]
        string? SessionId = null);

    public sealed record NyxIdChatStreamRequest(
        string? Prompt,
        [property: Obsolete("sessionId is deprecated and ignored. Use clientRequestId for retry idempotency.")]
        string? SessionId = null,
        IReadOnlyList<ContentPartDto>? InputParts = null,
        string? ClientRequestId = null);

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
