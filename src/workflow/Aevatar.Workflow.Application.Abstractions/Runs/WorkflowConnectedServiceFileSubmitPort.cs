namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record WorkflowConnectedServiceFileSubmitArgumentPolicy(
    string Name,
    bool Required = false,
    IReadOnlySet<string>? AllowedValues = null,
    string? MissingError = null,
    string? UnsupportedValueError = null);

public sealed record WorkflowConnectedServiceFileSubmitEndpoint(
    string ServiceSlug,
    string Path,
    string Method,
    string FileFieldName,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyDictionary<string, string>? Body = null);

public sealed record WorkflowConnectedServiceFileSubmitTarget(
    string Target,
    string Provider,
    string OutputField,
    long MaxFileBytes,
    IReadOnlySet<string> AllowedMediaTypes,
    IReadOnlyDictionary<string, WorkflowConnectedServiceFileSubmitArgumentPolicy> Arguments,
    WorkflowConnectedServiceFileSubmitEndpoint? Endpoint = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>>? MaxFileBytesByArgumentValue = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>>? AllowedMediaTypesByArgumentValue = null);

public sealed record WorkflowConnectedServiceFileSubmitRequest(
    WorkflowConnectedServiceFileSubmitTarget Target,
    WorkflowFileRef FileRef,
    string FileName,
    string MediaType,
    long SizeBytes,
    Stream Content,
    WorkflowCallerCredential CallerCredential,
    IReadOnlyDictionary<string, string> Arguments);

public sealed record WorkflowConnectedServiceFileSubmitResult(
    bool Succeeded,
    string? OutputCode = null,
    string? Error = null,
    string? Detail = null,
    int? Code = null);

public interface IWorkflowConnectedServiceFileSubmitAdapter
{
    string Provider { get; }

    IReadOnlyList<WorkflowConnectedServiceFileSubmitTarget> Targets { get; }

    ValueTask<WorkflowConnectedServiceFileSubmitResult> SubmitAsync(
        WorkflowConnectedServiceFileSubmitRequest request,
        CancellationToken cancellationToken = default);
}
