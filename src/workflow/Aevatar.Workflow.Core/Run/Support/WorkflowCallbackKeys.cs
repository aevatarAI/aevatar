using Aevatar.Foundation.Abstractions.Runtime.Callbacks;

namespace Aevatar.Workflow.Core;

internal static class WorkflowCallbackKeys
{
    public static string BuildStepTimeoutCallbackId(string runId, string stepId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-step-timeout", runId, stepId);

    public static string BuildRetryBackoffCallbackId(string runId, string stepId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-step-retry-backoff", runId, stepId);

    public static string BuildDelayCallbackId(string runId, string stepId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("delay-step", runId, stepId);

    public static string BuildWaitSignalCallbackId(string runId, string signalName, string stepId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("wait-signal-timeout", runId, signalName, stepId);

    public static string BuildLlmWatchdogCallbackId(string sessionId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("llm-watchdog", sessionId);
}
