using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal sealed record WorkflowWebhookIngressBuildResult(
    WorkflowChatRunRequest? Request,
    WorkflowWebhookReplayAdmissionRequest? Admission,
    string? ErrorCode,
    string? ErrorMessage,
    int StatusCode)
{
    public bool Succeeded => Request != null && Admission != null && StatusCode == StatusCodes.Status200OK;

    public static WorkflowWebhookIngressBuildResult Success(
        WorkflowChatRunRequest request,
        WorkflowWebhookReplayAdmissionRequest admission) =>
        new(request, admission, null, null, StatusCodes.Status200OK);

    public static WorkflowWebhookIngressBuildResult Failure(
        string code,
        string message,
        int statusCode) =>
        new(null, null, code, message, statusCode);
}

internal sealed record WorkflowWebhookAuthenticationResult(
    bool Succeeded,
    string AuthScheme,
    string PrincipalSubject,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static WorkflowWebhookAuthenticationResult Success(string authScheme, string principalSubject) =>
        new(true, authScheme, principalSubject, null, null);

    public static WorkflowWebhookAuthenticationResult Failure(string code, string message) =>
        new(false, string.Empty, string.Empty, code, message);
}
