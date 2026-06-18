using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.AI.ToolProviders.Lark;

public sealed class LarkMessageResourceFetchAdapter(ILarkNyxClient client)
    : IWorkflowConnectedServiceResourceFetchAdapter
{
    private static readonly WorkflowConnectedServiceResourceFetchRoute ImageRoute =
        new("lark", "message_resource_download", "image");
    private static readonly WorkflowConnectedServiceResourceFetchRoute FileRoute =
        new("lark", "message_resource_download", "file");

    private readonly ILarkNyxClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public IReadOnlyList<WorkflowConnectedServiceResourceFetchRoute> Routes { get; } =
    [
        ImageRoute,
        FileRoute,
    ];

    public async ValueTask<WorkflowConnectedServiceResourceFetchResult> FetchAsync(
        WorkflowConnectedServiceResourceFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = ResolveResourceKind(request.Route);
        var token = Normalize(request.CallerCredential.BearerToken)
                    ?? throw new ArgumentException("Lark message resource download requires a caller bearer token.", nameof(request));

        var result = await _client.DownloadMessageResourceAsync(
            token,
            new LarkMessageResourceDownloadRequest(
                request.SourceMessageId,
                request.SourceResourceKey,
                kind),
            cancellationToken).ConfigureAwait(false);

        return new WorkflowConnectedServiceResourceFetchResult(
            result.Succeeded,
            result.Content,
            result.ContentType,
            result.FileName,
            result.Detail,
            result.HttpStatus);
    }

    private static LarkMessageResourceKind ResolveResourceKind(
        WorkflowConnectedServiceResourceFetchRoute route)
    {
        if (RouteEquals(route, ImageRoute))
            return LarkMessageResourceKind.Image;
        if (RouteEquals(route, FileRoute))
            return LarkMessageResourceKind.File;

        throw new ArgumentException(
            "Lark message resource fetch only supports lark/message_resource_download/image and lark/message_resource_download/file.",
            nameof(route));
    }

    private static bool RouteEquals(
        WorkflowConnectedServiceResourceFetchRoute left,
        WorkflowConnectedServiceResourceFetchRoute right) =>
        string.Equals(left.Provider, right.Provider, StringComparison.Ordinal) &&
        string.Equals(left.Operation, right.Operation, StringComparison.Ordinal) &&
        string.Equals(left.ResourceKind, right.ResourceKind, StringComparison.Ordinal);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
