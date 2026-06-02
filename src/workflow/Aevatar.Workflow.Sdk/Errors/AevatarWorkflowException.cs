using System.Net;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Sdk.Contracts;

namespace Aevatar.Workflow.Sdk.Errors;

public enum AevatarWorkflowErrorKind
{
    InvalidRequest = 0,
    Http = 1,
    Transport = 2,
    StreamPayload = 3,
    RunFailed = 4,
}

public sealed class AevatarWorkflowException : Exception
{
    public AevatarWorkflowException(
        AevatarWorkflowErrorKind kind,
        string message,
        string? errorCode = null,
        HttpStatusCode? statusCode = null,
        string? rawPayload = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        ErrorCode = errorCode;
        StatusCode = statusCode;
        RawPayload = rawPayload;
    }

    public AevatarWorkflowErrorKind Kind { get; }

    public string? ErrorCode { get; }

    public HttpStatusCode? StatusCode { get; }

    public string? RawPayload { get; }

    public static AevatarWorkflowException InvalidRequest(string message) =>
        new(AevatarWorkflowErrorKind.InvalidRequest, message);

    public static AevatarWorkflowException Transport(string message, Exception? innerException = null) =>
        new(AevatarWorkflowErrorKind.Transport, message, innerException: innerException);

    public static AevatarWorkflowException StreamPayload(string message, string? rawPayload = null, Exception? innerException = null) =>
        new(AevatarWorkflowErrorKind.StreamPayload, message, rawPayload: rawPayload, innerException: innerException);

    public static AevatarWorkflowException RunFailed(WorkflowRunEventEnvelope frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var message = string.IsNullOrWhiteSpace(frame.RunError?.Message)
            ? "Workflow run failed."
            : frame.RunError.Message;
        return new AevatarWorkflowException(
            AevatarWorkflowErrorKind.RunFailed,
            message,
            errorCode: frame.RunError?.Code);
    }
}
