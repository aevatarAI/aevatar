using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Workflow.Infrastructure.ExternalCapabilities;

internal sealed class OrnnServiceApiSkillCataloguePort(OrnnSkillClient client) :
    IServiceApiSkillCataloguePort
{
    public async Task<ServiceApiSkillCataloguePage> ReadPageAsync(
        ServiceApiSkillCataloguePageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Access);
        var bearerToken = request.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken;
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            throw new InvalidOperationException(
                "A source-readable NyxID caller credential is required for Ornn skill catalogue discovery.");
        }

        var result = await client.SearchSkillsAsync(
                bearerToken,
                request.Query,
                "mixed",
                request.Page,
                request.PageSize,
                "keyword",
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.Error))
            throw new InvalidOperationException("Ornn skill catalogue discovery failed.");

        var page = new ServiceApiSkillCataloguePage
        {
            Page = result.Page,
            PageSize = result.PageSize,
            Total = result.Total,
            TotalPages = result.TotalPages,
        };
        foreach (var item in result.Items)
        {
            page.Candidates.Add(new ServiceApiSkillCatalogueCandidate
            {
                Guid = item.Guid ?? string.Empty,
                CanonicalName = item.Name ?? string.Empty,
                Description = item.Description ?? string.Empty,
            });
        }

        return page;
    }
}
