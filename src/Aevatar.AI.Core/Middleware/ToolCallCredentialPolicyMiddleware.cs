using System.Text.Json;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;

namespace Aevatar.AI.Core.Middleware;

public sealed class ToolCallCredentialPolicyMiddleware : IToolCallMiddleware
{
    private const string ErrorCode = "credential_denied";

    public async Task InvokeAsync(ToolCallContext context, Func<Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var current = context.ExecutionContext ?? AgentToolRequestContext.Current;
        if (current?.Credentials.NyxIdCredentialKind == AgentToolNyxIdCredentialKind.ProxyDelegation)
        {
            var proxyDelegationToken = current.Credentials.NyxIdAccessToken?.Trim();
            if (string.IsNullOrWhiteSpace(proxyDelegationToken))
            {
                Deny(
                    context,
                    $"Tool '{context.ToolName}' was not executed because the typed NyxID proxy delegation credential has no valid primary token. Credential fallback was not used.",
                    ResolveCredentialSource(current),
                    current.SenderBinding.BindingId?.Trim());
                return;
            }

            var delegationContext = current with
            {
                Credentials = current.Credentials with
                {
                    NyxIdAccessToken = proxyDelegationToken,
                    NyxIdOrgToken = null,
                    SenderNyxIdAccessToken = null,
                },
            };

            context.CredentialSource = ResolveCredentialSource(delegationContext);
            using var delegationScope = AgentToolContextScope.Push(delegationContext);
            await next();
            return;
        }

        var senderBindingId = current?.SenderBinding.BindingId?.Trim();
        if (string.IsNullOrWhiteSpace(senderBindingId))
        {
            // No binding at all. A direct/API caller (no Channel context) has no distinct
            // sender to isolate from the owner, so the existing owner-credential fallback
            // is correct. But a channel-mediated request (Lark/Discord/etc., a real,
            // addressable third party who never ran /init) must not get a free pass on
            // mutating tool calls just because AgentToolCredentialPolicy never saw a binding
            // id to deny against.
            var isChannelMediated = !string.IsNullOrWhiteSpace(current?.Channel.SenderId);
            if (!isChannelMediated || !AgentToolCredentialPolicy.IsMutation(context.Tool, context.ArgumentsJson))
            {
                context.CredentialSource = ResolveDirectCredentialSource(current);
                await next();
                return;
            }

            Deny(
                context,
                $"Tool '{context.ToolName}' was not executed because the sender is not bound to a NyxID account. Send /init to bind your NyxID account and retry. Owner credentials were not used.",
                AgentToolCredentialSource.ChannelRegistration,
                senderBindingId: null);
            return;
        }

        var senderToken = current?.Credentials.SenderNyxIdAccessToken?.Trim();
        if (!string.IsNullOrWhiteSpace(senderToken))
        {
            var senderContext = current! with
            {
                Credentials = current.Credentials with
                {
                    NyxIdAccessToken = senderToken,
                    NyxIdOrgToken = senderToken,
                    SenderNyxIdAccessToken = senderToken,
                },
            };

            context.CredentialSource = ResolveCredentialSource(senderContext);
            using var _ = AgentToolContextScope.Push(senderContext);
            await next();
            return;
        }

        if (!AgentToolCredentialPolicy.IsMutation(context.Tool, context.ArgumentsJson))
        {
            context.CredentialSource = ResolveDirectCredentialSource(current);
            await next();
            return;
        }

        Deny(
            context,
            $"Tool '{context.ToolName}' was not executed because sender binding '{senderBindingId}' has no valid NyxID credential. Send /init to re-bind your NyxID account and retry. Owner credentials were not used.",
            AgentToolCredentialSource.ChannelRegistration,
            senderBindingId);
    }

    // Single deny shape for every credential-policy denial: error contract (error/code/message/
    // tool_name/sender_binding_id), termination semantics, and receipt stay in lockstep.
    private static void Deny(
        ToolCallContext context,
        string message,
        AgentToolCredentialSource credentialSource,
        string? senderBindingId)
    {
        context.CredentialSource = credentialSource;
        context.Terminate = true;
        context.TerminationKind = ToolCallTerminationKind.MiddlewareTerminated;
        context.TerminationReason = message;
        context.Result = JsonSerializer.Serialize(new
        {
            error = ErrorCode,
            code = ErrorCode,
            message,
            tool_name = context.ToolName,
            sender_binding_id = senderBindingId,
        });
    }

    private static AgentToolCredentialSource ResolveDirectCredentialSource(AgentToolExecutionContext? current)
    {
        if (current?.CredentialSource is { } source && source != AgentToolCredentialSource.Unspecified)
            return source;

        if (!string.IsNullOrWhiteSpace(current?.Schedule.ScheduleId))
            return AgentToolCredentialSource.ScheduledRun;

        return string.IsNullOrWhiteSpace(current?.Credentials.NyxIdAccessToken)
            ? AgentToolCredentialSource.System
            : AgentToolCredentialSource.BearerToken;
    }

    private static AgentToolCredentialSource ResolveCredentialSource(AgentToolExecutionContext current)
    {
        if (current.CredentialSource != AgentToolCredentialSource.Unspecified)
            return current.CredentialSource;

        if (!string.IsNullOrWhiteSpace(current.Schedule.ScheduleId))
            return AgentToolCredentialSource.ScheduledRun;

        return string.IsNullOrWhiteSpace(current.SenderBinding.BindingId)
            ? ResolveDirectCredentialSource(current)
            : AgentToolCredentialSource.ChannelRegistration;
    }
}
