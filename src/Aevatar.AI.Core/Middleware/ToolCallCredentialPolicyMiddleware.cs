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

        var current = AgentToolRequestContext.Current;
        var senderBindingId = current?.SenderBinding.BindingId?.Trim();
        if (string.IsNullOrWhiteSpace(senderBindingId))
        {
            // No binding at all. A direct/API caller (no Channel context) has no distinct
            // sender to isolate from the owner, so the existing owner-credential fallback
            // is correct. But a channel-mediated request (Lark/Discord/etc. — a real,
            // addressable third party who never ran /init) must not get a free pass on
            // mutating tool calls just because AgentToolCredentialPolicy never saw a binding
            // id to deny against.
            var isChannelMediated = !string.IsNullOrWhiteSpace(current?.Channel.SenderId);
            if (!isChannelMediated || !AgentToolCredentialPolicy.IsMutation(context.Tool, context.ArgumentsJson))
            {
                await next();
                return;
            }

            var unboundMessage = $"Tool '{context.ToolName}' was not executed because the sender is not bound to a NyxID account. Send /init to bind your NyxID account and retry. Owner credentials were not used.";
            context.Terminate = true;
            context.TerminationKind = ToolCallTerminationKind.MiddlewareTerminated;
            context.TerminationReason = unboundMessage;
            context.Result = JsonSerializer.Serialize(new
            {
                error = ErrorCode,
                code = ErrorCode,
                message = unboundMessage,
                tool_name = context.ToolName,
                sender_binding_id = (string?)null,
            });
            context.Receipt = AgentToolReceiptFactory.CreateError(
                context.Tool,
                context.ToolCallId,
                context.ToolName,
                context.Result,
                ErrorCode,
                unboundMessage);
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

            using var _ = AgentToolContextScope.Push(senderContext);
            await next();
            return;
        }

        if (!AgentToolCredentialPolicy.IsMutation(context.Tool, context.ArgumentsJson))
        {
            await next();
            return;
        }

        var message = $"Tool '{context.ToolName}' was not executed because sender binding '{senderBindingId}' has no valid NyxID credential. Send /init to re-bind your NyxID account and retry. Owner credentials were not used.";
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
        context.Receipt = AgentToolReceiptFactory.CreateError(
            context.Tool,
            context.ToolCallId,
            context.ToolName,
            context.Result,
            ErrorCode,
            message);
    }
}
