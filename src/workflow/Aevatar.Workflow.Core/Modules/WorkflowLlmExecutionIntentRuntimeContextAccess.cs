using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

internal static class WorkflowLlmExecutionIntentRuntimeContextAccess
{
    public static bool ApplyChannelAgentKeyOrSenderNyxIdAccessToken(
        IWorkflowExecutionContext ctx,
        WorkflowLlmExecutionIntent intent)
    {
        if (WorkflowRunExecutionContextStateAccess.TryGetDurableCallerCredential(
                ctx,
                out var credential) &&
            credential.DurableCallerCredential?.SourceKind ==
            DurableCallerCredentialSourceKind.ChannelRegistration)
        {
            intent.CallerCredential = new WorkflowCallerCredential
            {
                DurableCallerCredential = credential.DurableCallerCredential.Clone(),
                Kind = NyxIdCallerCredentialKind.ProxyDelegation,
            };
            intent.SenderNyxIdAccessToken = string.Empty;
            return true;
        }

        ApplySenderNyxIdAccessToken(ctx, intent);
        return false;
    }

    public static void ApplySenderNyxIdAccessToken(
        IWorkflowExecutionContext ctx,
        WorkflowLlmExecutionIntent intent)
    {
        if (ctx is IWorkflowExecutionRuntimeContextAccessor runtimeAccessor)
            intent.SenderNyxIdAccessToken = Normalize(runtimeAccessor.RuntimeContext.SenderNyxIdAccessToken) ?? string.Empty;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
