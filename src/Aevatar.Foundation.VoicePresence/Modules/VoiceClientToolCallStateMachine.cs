using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;

namespace Aevatar.Foundation.VoicePresence.Modules;

internal static class VoiceClientToolCallStateMachine
{
    public static VoicePendingClientToolCall RecordPendingCall(
        VoicePresenceRuntimeState state,
        VoiceFunctionCallRequested request,
        DateTimeOffset expiresAt,
        string? transportLeaseId)
    {
        RemovePendingCall(state, request.CallId);

        var pendingCall = new VoicePendingClientToolCall
        {
            CallId = request.CallId?.Trim() ?? string.Empty,
            ToolName = request.ToolName?.Trim() ?? string.Empty,
            ArgumentsJson = string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson,
            ResponseId = request.ResponseId,
            ProviderResponseId = request.ProviderResponseId ?? string.Empty,
            SessionId = state.ActiveSessionId ?? string.Empty,
            OwnerId = state.ActiveLeaseOwnerId ?? string.Empty,
            TransportLeaseId = !string.IsNullOrWhiteSpace(transportLeaseId)
                ? transportLeaseId
                : state.ActiveTransportLeaseId ?? string.Empty,
            LeaseEpoch = state.LeaseEpoch,
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt),
        };

        state.PendingClientToolCalls.Add(pendingCall);
        return pendingCall;
    }

    public static VoicePendingClientToolCall? CompletePendingCall(
        VoicePresenceRuntimeState state,
        VoiceFunctionCallOutput output)
    {
        if (string.IsNullOrWhiteSpace(output.CallId))
            return null;

        for (var i = 0; i < state.PendingClientToolCalls.Count; i++)
        {
            var pendingCall = state.PendingClientToolCalls[i];
            if (!string.Equals(pendingCall.CallId, output.CallId, StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrWhiteSpace(output.ToolName) &&
                !string.Equals(pendingCall.ToolName, output.ToolName, StringComparison.Ordinal))
            {
                return null;
            }

            state.PendingClientToolCalls.RemoveAt(i);
            return pendingCall;
        }

        return null;
    }

    public static VoicePendingClientToolCall? ExpirePendingCall(
        VoicePresenceRuntimeState state,
        VoiceClientToolCallTimeoutExpired timeout,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(timeout.CallId))
            return null;

        for (var i = 0; i < state.PendingClientToolCalls.Count; i++)
        {
            var pendingCall = state.PendingClientToolCalls[i];
            if (!MatchesTimeout(pendingCall, timeout))
                continue;

            if (pendingCall.ExpiresAt == null ||
                pendingCall.ExpiresAt.ToDateTimeOffset() > now)
            {
                return null;
            }

            state.PendingClientToolCalls.RemoveAt(i);
            return pendingCall;
        }

        return null;
    }

    public static void RemovePendingCallsForTransport(
        VoicePresenceRuntimeState state,
        string? transportLeaseId)
    {
        if (string.IsNullOrWhiteSpace(transportLeaseId))
            return;

        for (var i = state.PendingClientToolCalls.Count - 1; i >= 0; i--)
        {
            if (string.Equals(
                    state.PendingClientToolCalls[i].TransportLeaseId,
                    transportLeaseId,
                    StringComparison.Ordinal))
            {
                state.PendingClientToolCalls.RemoveAt(i);
            }
        }
    }

    public static void RemovePendingCallsForProviderResponse(
        VoicePresenceRuntimeState state,
        string? providerResponseId)
    {
        if (string.IsNullOrWhiteSpace(providerResponseId))
            return;

        for (var i = state.PendingClientToolCalls.Count - 1; i >= 0; i--)
        {
            if (string.Equals(
                    state.PendingClientToolCalls[i].ProviderResponseId,
                    providerResponseId,
                    StringComparison.Ordinal))
            {
                state.PendingClientToolCalls.RemoveAt(i);
            }
        }
    }

    public static string BuildOutputJson(VoiceFunctionCallOutput output)
    {
        return output.ResultCase switch
        {
            VoiceFunctionCallOutput.ResultOneofCase.OutputJson when !string.IsNullOrWhiteSpace(output.OutputJson) =>
                output.OutputJson,
            VoiceFunctionCallOutput.ResultOneofCase.OutputJson => "{}",
            VoiceFunctionCallOutput.ResultOneofCase.Failure => BuildFailureJson(output.Failure),
            _ => BuildFailureJson(new VoiceFunctionCallFailure
            {
                ErrorCode = "invalid_client_tool_output",
                ErrorMessage = "client tool output did not include a result",
            }),
        };
    }

    public static string BuildTimeoutJson(VoicePendingClientToolCall pendingCall, TimeSpan timeout) =>
        BuildFailureJson(new VoiceFunctionCallFailure
        {
            ErrorCode = "client_tool_timeout",
            ErrorMessage = $"client-owned tool '{pendingCall.ToolName}' timed out after {(int)timeout.TotalMilliseconds} ms",
        });

    private static bool RemovePendingCall(VoicePresenceRuntimeState state, string callId)
    {
        for (var i = state.PendingClientToolCalls.Count - 1; i >= 0; i--)
        {
            if (string.Equals(state.PendingClientToolCalls[i].CallId, callId, StringComparison.Ordinal))
            {
                state.PendingClientToolCalls.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    private static bool MatchesTimeout(
        VoicePendingClientToolCall pendingCall,
        VoiceClientToolCallTimeoutExpired timeout)
    {
        return string.Equals(pendingCall.SessionId, timeout.SessionId, StringComparison.Ordinal) &&
               string.Equals(pendingCall.OwnerId, timeout.OwnerId, StringComparison.Ordinal) &&
               string.Equals(pendingCall.TransportLeaseId, timeout.TransportLeaseId, StringComparison.Ordinal) &&
               pendingCall.LeaseEpoch == timeout.LeaseEpoch &&
               string.Equals(pendingCall.CallId, timeout.CallId, StringComparison.Ordinal) &&
               (string.IsNullOrWhiteSpace(timeout.ToolName) ||
                string.Equals(pendingCall.ToolName, timeout.ToolName, StringComparison.Ordinal));
    }

    private static string BuildFailureJson(VoiceFunctionCallFailure? failure)
    {
        var errorCode = string.IsNullOrWhiteSpace(failure?.ErrorCode)
            ? "client_tool_failed"
            : failure.ErrorCode.Trim();
        var errorMessage = string.IsNullOrWhiteSpace(failure?.ErrorMessage)
            ? "client-owned tool failed"
            : failure.ErrorMessage;
        return JsonSerializer.Serialize(new
        {
            error = new
            {
                code = errorCode,
                message = errorMessage,
            },
        });
    }
}
