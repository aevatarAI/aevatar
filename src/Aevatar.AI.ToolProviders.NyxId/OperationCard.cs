namespace Aevatar.AI.ToolProviders.NyxId;

public sealed record OperationCard(
    string Service,
    string OperationId,
    string Method,
    string Path,
    string Summary,
    string? Parameters,
    string? RequestBodySchema);
