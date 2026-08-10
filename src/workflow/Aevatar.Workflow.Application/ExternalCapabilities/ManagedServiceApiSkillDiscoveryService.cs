using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Workflow.Application.ExternalCapabilities;

public sealed class ManagedServiceApiSkillDiscoveryService :
    IManagedCodexServiceApiSkillDiscoveryPort
{
    private const string PolicyVersion = "service_api_skill_discovery.v1";
    private const int CataloguePageSize = 100;
    private const int MaxCataloguePages = 100;

    private readonly IServiceApiSkillCataloguePort _catalogue;
    private readonly IManagedCodexServiceApiSkillDiscoveryExecutor _ranker;
    private readonly IExactServiceApiSkillVerifier _verifier;

    public ManagedServiceApiSkillDiscoveryService(
        IServiceApiSkillCataloguePort catalogue,
        IManagedCodexServiceApiSkillDiscoveryExecutor ranker,
        IExactServiceApiSkillVerifier verifier)
    {
        _catalogue = catalogue;
        _ranker = ranker;
        _verifier = verifier;
    }

    public async Task<ManagedCodexServiceApiSkillDiscoveryResult> DiscoverAsync(
        ManagedCodexServiceApiSkillDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Access);
        ArgumentNullException.ThrowIfNull(request.Input);
        if (!string.Equals(
                request.Input.ManagedDiscoveryPolicyVersion,
                PolicyVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Managed Service API skill discovery policy is unsupported.");
        }

        var catalogueCandidates = await ReadCatalogueAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (catalogueCandidates.Count == 0)
            return NoReliable(ServiceApiNoReliableSkillReason.NoMatchingSkill);

        var excludedCandidates = new List<ReliableServiceApiSkillCandidate>();
        var excludedGuids = new HashSet<string>(StringComparer.Ordinal);
        var lastRejection = ServiceApiNoReliableSkillReason.AllCandidatesRejected;

        while (excludedGuids.Count < catalogueCandidates.Count)
        {
            var rankingInput = new ManagedCodexServiceApiSkillRankingInput
            {
                DiscoveryInput = request.Input.Clone(),
            };
            rankingInput.CatalogueCandidates.AddRange(
                catalogueCandidates.Select(static candidate => candidate.Clone()));
            rankingInput.ExcludedCandidates.AddRange(
                excludedCandidates.Select(static candidate => candidate.Clone()));

            var ranked = await _ranker.DiscoverAsync(
                    new ManagedCodexServiceApiSkillRankingRequest(request.Access, rankingInput),
                    cancellationToken)
                .ConfigureAwait(false);
            if (ranked.ResultCase ==
                ManagedCodexServiceApiSkillDiscoveryResult.ResultOneofCase.NoReliableApiSkill)
            {
                return NoReliable(
                    excludedGuids.Count == 0
                        ? ServiceApiNoReliableSkillReason.NoMatchingSkill
                        : lastRejection);
            }
            if (ranked.ResultCase !=
                ManagedCodexServiceApiSkillDiscoveryResult.ResultOneofCase.ReliableSkill)
            {
                throw new InvalidOperationException(
                    "Managed Codex Service API skill ranking returned no result.");
            }

            var candidate = ranked.ReliableSkill;
            EnsureCandidateBelongsToCatalogue(candidate, catalogueCandidates, excludedGuids);
            var verification = await _verifier.VerifyAsync(
                    new ExactServiceApiSkillVerificationRequest(
                        request.Access,
                        request.Input,
                        candidate),
                    cancellationToken)
                .ConfigureAwait(false);
            if (verification.IsVerified)
                return ranked.Clone();

            lastRejection = verification.Rejection?.Reason ??
                            ServiceApiNoReliableSkillReason.AllCandidatesRejected;
            excludedCandidates.Add(candidate.Clone());
            excludedGuids.Add(candidate.Guid);
        }

        return NoReliable(lastRejection);
    }

    private async Task<IReadOnlyList<ServiceApiSkillCatalogueCandidate>> ReadCatalogueAsync(
        ManagedCodexServiceApiSkillDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        var candidates = new List<ServiceApiSkillCatalogueCandidate>();
        var seenGuids = new HashSet<string>(StringComparer.Ordinal);
        int? total = null;
        int? totalPages = null;

        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await _catalogue.ReadPageAsync(
                    new ServiceApiSkillCataloguePageRequest(
                        request.Access,
                        request.Input.NormalizedCapabilityKey,
                        pageNumber,
                        CataloguePageSize),
                    cancellationToken)
                .ConfigureAwait(false);
            ValidatePage(page, pageNumber, total, totalPages);
            total ??= page.Total;
            totalPages ??= page.TotalPages;

            foreach (var candidate in page.Candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.Guid) ||
                    string.IsNullOrWhiteSpace(candidate.CanonicalName) ||
                    !seenGuids.Add(candidate.Guid))
                {
                    throw new InvalidOperationException(
                        "Ornn Service API skill catalogue returned an invalid candidate inventory.");
                }

                candidates.Add(candidate.Clone());
            }

            if (pageNumber >= page.TotalPages)
                break;
        }

        if (candidates.Count != total)
        {
            throw new InvalidOperationException(
                "Ornn Service API skill catalogue did not exhaust the authoritative result set.");
        }

        return candidates;
    }

    private static void ValidatePage(
        ServiceApiSkillCataloguePage page,
        int expectedPage,
        int? expectedTotal,
        int? expectedTotalPages)
    {
        ArgumentNullException.ThrowIfNull(page);
        var emptyCatalogue = page.Total == 0 &&
                             page.TotalPages == 0 &&
                             page.Candidates.Count == 0;
        if (page.Page != expectedPage ||
            page.PageSize != CataloguePageSize ||
            page.Total < 0 ||
            (!emptyCatalogue && page.TotalPages is < 1 or > MaxCataloguePages) ||
            page.Candidates.Count > CataloguePageSize ||
            expectedTotal is not null && page.Total != expectedTotal ||
            expectedTotalPages is not null && page.TotalPages != expectedTotalPages)
        {
            throw new InvalidOperationException(
                "Ornn Service API skill catalogue pagination is invalid.");
        }
    }

    private static void EnsureCandidateBelongsToCatalogue(
        ReliableServiceApiSkillCandidate candidate,
        IReadOnlyList<ServiceApiSkillCatalogueCandidate> catalogueCandidates,
        IReadOnlySet<string> excludedGuids)
    {
        if (string.IsNullOrWhiteSpace(candidate.Guid) ||
            excludedGuids.Contains(candidate.Guid) ||
            !catalogueCandidates.Any(item =>
                string.Equals(item.Guid, candidate.Guid, StringComparison.Ordinal) &&
                string.Equals(item.CanonicalName, candidate.CanonicalName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Managed Codex proposed a Service API skill outside the authoritative catalogue inventory.");
        }
    }

    private static ManagedCodexServiceApiSkillDiscoveryResult NoReliable(
        ServiceApiNoReliableSkillReason reason) =>
        new()
        {
            NoReliableApiSkill = new NoReliableServiceApiSkill { Reason = reason },
        };
}
