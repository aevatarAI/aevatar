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
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

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
        [FromServices] ICommandInteractionService<NyxIdActionContinuationCommand, NyxIdChatAcceptedReceipt, NyxIdChatStartError, AGUIEvent, NyxIdChatCompletionStatus> actionContinuationInteractionService,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Endpoints");
        var accessToken = string.Empty;
        var prompt = string.Empty;
        var streamType = request.Type?.Trim() ?? string.Empty;
        var clientRequestId = ResolveClientRequestId(http, request.ClientRequestId);
        var turnId = CreateTurnId(actorId, clientRequestId);
        var ownerSubject = string.Empty;
        IReadOnlyList<NyxIdChatActionReport> actionReports = [];

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

            if (string.Equals(streamType, "text", StringComparison.Ordinal))
            {
                prompt = request.Prompt?.Trim() ?? string.Empty;
                if ((string.IsNullOrWhiteSpace(prompt) && request.InputParts is not { Count: > 0 }) ||
                    !string.IsNullOrWhiteSpace(request.OriginTurnId) ||
                    request.Actions is { Count: > 0 })
                {
                    http.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
            }
            else if (string.Equals(streamType, "action.continue", StringComparison.Ordinal))
            {
                ownerSubject = ResolveAuthenticatedOwnerSubject(http) ?? string.Empty;
                if (!TryValidateControlIdentity(clientRequestId, out clientRequestId) ||
                    !TryValidateControlIdentity(request.OriginTurnId, out var originTurnId) ||
                    string.IsNullOrWhiteSpace(ownerSubject) ||
                    !string.IsNullOrWhiteSpace(request.Prompt) ||
                    request.InputParts is { Count: > 0 } ||
                    !TryMapActionReports(request.Actions, originTurnId, out actionReports))
                {
                    http.Response.StatusCode = string.IsNullOrWhiteSpace(ownerSubject)
                        ? StatusCodes.Status401Unauthorized
                        : StatusCodes.Status400BadRequest;
                    return;
                }
            }
            else
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
            async ValueTask EmitAsync(AGUIEvent evt, CancellationToken token)
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
            }

            CommandInteractionResult<NyxIdChatAcceptedReceipt, NyxIdChatStartError, NyxIdChatCompletionStatus> result;
            if (string.Equals(streamType, "action.continue", StringComparison.Ordinal))
            {
                result = await actionContinuationInteractionService.ExecuteAsync(
                    new NyxIdActionContinuationCommand(
                        actorId,
                        scopeId,
                        request.OriginTurnId!.Trim(),
                        turnId,
                        ownerSubject,
                        clientRequestId!,
                        actionReports),
                    EmitAsync,
                    null,
                    terminalDeadline.Token);
            }
            else
            {
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                var llmControl = await BuildLlmControlAsync(http, accessToken, ct);

                // Streaming endpoints do not pre-read runtime state before command dispatch.
                // The shared CQRS resolver owns actor lookup and attach-existing observation.
                result = await interactionService.ExecuteAsync(
                    new NyxIdChatCommand(
                        actorId,
                        scopeId,
                        prompt,
                        turnId,
                        accessToken,
                        request.InputParts,
                        metadata,
                        llmControl,
                        ClientRequestId: clientRequestId),
                    EmitAsync,
                    null,
                    terminalDeadline.Token);
            }

            if (!result.Succeeded)
                heartbeat.Stop();
            await HandleInteractionFailureAsync(
                result,
                writer,
                writerLock,
                turnId,
                string.Equals(streamType, "action.continue", StringComparison.Ordinal)
                    ? "The action continuation failed. Please try again."
                    : "The chat request failed. Please try again.");
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

    private static string? ResolveAuthenticatedOwnerSubject(HttpContext http)
    {
        foreach (var claimType in new[] { "sub", ClaimTypes.NameIdentifier })
        {
            var value = http.User.FindFirst(claimType)?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool TryMapActionReports(
        IReadOnlyList<NyxIdChatActionReportDto>? reports,
        string originTurnId,
        out IReadOnlyList<NyxIdChatActionReport> mapped)
    {
        mapped = [];
        if (reports is not { Count: > 0 })
            return false;

        var values = new List<NyxIdChatActionReport>(reports.Count);
        var actionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var report in reports)
        {
            if (report is null ||
                !TryValidateControlIdentity(report.ActionRequestId, out var actionRequestId) ||
                !TryValidateControlIdentity(report.OriginTurnId, out var reportOriginTurnId) ||
                !string.Equals(reportOriginTurnId, originTurnId, StringComparison.Ordinal) ||
                !actionIds.Add(actionRequestId) ||
                !TryParseActionDisposition(report.Disposition, out var disposition) ||
                !TryMapActionResource(report.Resource, out var resource))
            {
                return false;
            }

            var value = new NyxIdChatActionReport
            {
                ActionRequestId = actionRequestId,
                OriginTurnId = reportOriginTurnId,
                Disposition = disposition,
            };
            if (resource is not null)
                value.Resource = resource;
            values.Add(value);
        }

        mapped = values;
        return true;
    }

    private static bool TryParseActionDisposition(
        string? value,
        out NyxIdChatActionDisposition disposition)
    {
        disposition = value?.Trim() switch
        {
            "completed" => NyxIdChatActionDisposition.Completed,
            "declined" => NyxIdChatActionDisposition.Declined,
            "failed" => NyxIdChatActionDisposition.Failed,
            "cancelled" => NyxIdChatActionDisposition.Cancelled,
            "expired" => NyxIdChatActionDisposition.Expired,
            _ => NyxIdChatActionDisposition.Unspecified,
        };
        return disposition != NyxIdChatActionDisposition.Unspecified;
    }

    private static bool TryMapActionResource(
        NyxIdChatActionResourceDto? resource,
        out NyxIdChatSafeResourceRef? mapped)
    {
        mapped = null;
        if (resource is null)
            return true;

        var variants = new object?[]
        {
            resource.UserService,
            resource.Key,
            resource.Node,
            resource.ServiceAccount,
            resource.DeveloperApp,
            resource.Device,
        };
        if (variants.Count(static value => value is not null) != 1)
            return false;

        if (resource.UserService is { } userService &&
            TryValidateControlIdentity(userService.UserServiceId, out var userServiceId))
        {
            mapped = new NyxIdChatSafeResourceRef
            {
                UserService = new NyxIdChatUserServiceRef { UserServiceId = userServiceId },
            };
            return true;
        }

        if (resource.Key is { } key && TryValidateControlIdentity(key.KeyId, out var keyId))
        {
            mapped = new NyxIdChatSafeResourceRef
            {
                Key = new NyxIdChatKeyRef { KeyId = keyId },
            };
            return true;
        }

        if (resource.Node is { } node && TryValidateControlIdentity(node.NodeId, out var nodeId))
        {
            mapped = new NyxIdChatSafeResourceRef
            {
                Node = new NyxIdChatNodeRef { NodeId = nodeId },
            };
            return true;
        }

        if (resource.ServiceAccount is { } serviceAccount &&
            TryValidateControlIdentity(serviceAccount.ServiceAccountId, out var serviceAccountId))
        {
            mapped = new NyxIdChatSafeResourceRef
            {
                ServiceAccount = new NyxIdChatServiceAccountRef
                {
                    ServiceAccountId = serviceAccountId,
                },
            };
            return true;
        }

        if (resource.DeveloperApp is { } developerApp &&
            TryValidateControlIdentity(developerApp.ClientId, out var clientId))
        {
            mapped = new NyxIdChatSafeResourceRef
            {
                DeveloperApp = new NyxIdChatDeveloperAppRef { ClientId = clientId },
            };
            return true;
        }

        if (resource.Device is { } device &&
            TryValidateControlIdentity(device.DeviceId, out var deviceId))
        {
            mapped = new NyxIdChatSafeResourceRef
            {
                Device = new NyxIdChatDeviceRef { DeviceId = deviceId },
            };
            return true;
        }

        return false;
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

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatStreamRequest(
        string? Prompt,
        [property: Obsolete("sessionId is deprecated and ignored. Use clientRequestId for retry idempotency.")]
        string? SessionId = null,
        IReadOnlyList<ContentPartDto>? InputParts = null,
        string? ClientRequestId = null,
        string? Type = null,
        string? OriginTurnId = null,
        IReadOnlyList<NyxIdChatActionReportDto>? Actions = null);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatActionReportDto(
        string? ActionRequestId,
        string? OriginTurnId,
        string? Disposition,
        NyxIdChatActionResourceDto? Resource = null);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatActionResourceDto(
        NyxIdChatUserServiceRefDto? UserService = null,
        NyxIdChatKeyRefDto? Key = null,
        NyxIdChatNodeRefDto? Node = null,
        NyxIdChatServiceAccountRefDto? ServiceAccount = null,
        NyxIdChatDeveloperAppRefDto? DeveloperApp = null,
        NyxIdChatDeviceRefDto? Device = null);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatUserServiceRefDto(string? UserServiceId);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatKeyRefDto(string? KeyId);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatNodeRefDto(string? NodeId);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatServiceAccountRefDto(string? ServiceAccountId);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatDeveloperAppRefDto(string? ClientId);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NyxIdChatDeviceRefDto(string? DeviceId);

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
