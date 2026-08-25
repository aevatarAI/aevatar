using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

internal enum NyxIdMcpOperationCatalogReadFailureKind
{
    AccessDenied = 1,
    SourceUnavailable = 2,
    AmbiguousServiceIdentity = 3,
    Transport = 4,
}

internal sealed record NyxIdMcpOperationCatalogReadFailure(
    NyxIdMcpOperationCatalogReadFailureKind Kind);

internal sealed record NyxIdMcpOperationCatalogReadResult(
    NyxIdMcpCatalogRead? Catalog,
    NyxIdMcpOperationCatalogReadFailure? Failure)
{
    public bool Succeeded => Catalog is not null && Failure is null;

    public static NyxIdMcpOperationCatalogReadResult Success(NyxIdMcpCatalogRead catalog) =>
        new(catalog, null);

    public static NyxIdMcpOperationCatalogReadResult Failed(
        NyxIdMcpOperationCatalogReadFailureKind kind,
        NyxIdMcpCatalogRead? catalog = null) =>
        new(catalog, new NyxIdMcpOperationCatalogReadFailure(kind));
}

internal sealed class NyxIdMcpOperationCatalogReader
{
    private const string SourceSuffix = "action-runtime";
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);

    private readonly INyxIdApiClientFactory _clientFactory;
    private readonly TimeProvider _timeProvider;

    public NyxIdMcpOperationCatalogReader(
        INyxIdApiClientFactory clientFactory,
        TimeProvider timeProvider)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<NyxIdMcpOperationCatalogReadResult> ReadAsync(
        string bearerToken,
        CancellationToken ct = default)
    {
        ValidateBearerToken(bearerToken);
        try
        {
            using var client = _clientFactory.CreateClient();
            var response = await client.GetMcpConfigAsync(bearerToken, ct).ConfigureAwait(false);
            var catalog = NyxIdMcpOperationCatalog.Parse(
                response,
                SourceSuffix,
                _timeProvider.GetUtcNow(),
                FreshnessWindow);

            if (catalog.AccessDenied)
            {
                return NyxIdMcpOperationCatalogReadResult.Failed(
                    NyxIdMcpOperationCatalogReadFailureKind.AccessDenied,
                    catalog);
            }

            if (catalog.SourceUnavailable)
            {
                return NyxIdMcpOperationCatalogReadResult.Failed(
                    NyxIdMcpOperationCatalogReadFailureKind.SourceUnavailable,
                    catalog);
            }

            if (catalog.Issues.Any(static issue =>
                    issue.Code == ExternalCapabilityDiscoveryDiagnosticCode.AmbiguousServiceIdentity))
            {
                return NyxIdMcpOperationCatalogReadResult.Failed(
                    NyxIdMcpOperationCatalogReadFailureKind.AmbiguousServiceIdentity,
                    catalog);
            }

            return NyxIdMcpOperationCatalogReadResult.Success(catalog);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return NyxIdMcpOperationCatalogReadResult.Failed(
                NyxIdMcpOperationCatalogReadFailureKind.Transport);
        }
    }

    private static void ValidateBearerToken(string bearerToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        if (!string.Equals(bearerToken, bearerToken.Trim(), StringComparison.Ordinal) ||
            bearerToken.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("NyxID MCP catalog bearer token must be canonical.");
        }
    }
}
