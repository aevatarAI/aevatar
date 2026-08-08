using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.ExternalCapabilities;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class ManagedServiceApiSkillDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverAsync_ShouldExhaustAuthoritativeCatalogueBeforeReturningNoReliableSkill()
    {
        var catalogue = new StubCataloguePort(
            Page(1, 2, Candidate("skill-a", "alpha-service-api")),
            Page(2, 2, Candidate("skill-b", "beta-service-api")));
        var ranker = new StubRanker((_, _) =>
            NoReliable(ServiceApiNoReliableSkillReason.RequestShapeUnsupported));
        var verifier = new StubVerifier(_ => throw new InvalidOperationException("No candidate should be verified."));
        var service = new ManagedServiceApiSkillDiscoveryService(catalogue, ranker, verifier);

        var result = await service.DiscoverAsync(Request());

        catalogue.RequestedPages.Should().Equal(1, 2);
        ranker.Requests.Should().ContainSingle();
        ranker.Requests[0].CatalogueCandidates.Select(static candidate => candidate.Guid)
            .Should().Equal("skill-a", "skill-b");
        result.ResultCase.Should().Be(ManagedCodexServiceApiSkillDiscoveryResult.ResultOneofCase.NoReliableApiSkill);
        result.NoReliableApiSkill.Reason.Should().Be(ServiceApiNoReliableSkillReason.NoMatchingSkill);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldExcludeRejectedCandidateBeforeRankingNextCandidate()
    {
        var catalogue = new StubCataloguePort(
            Page(
                1,
                1,
                Candidate("skill-a", "alpha-service-api"),
                Candidate("skill-b", "beta-service-api")));
        var ranker = new StubRanker((request, _) =>
        {
            var excluded = request.ExcludedCandidates
                .Select(static candidate => candidate.Guid)
                .ToHashSet(StringComparer.Ordinal);
            var candidate = request.CatalogueCandidates.First(item => !excluded.Contains(item.Guid));
            return Reliable(candidate.Guid, candidate.CanonicalName);
        });
        var verifier = new StubVerifier(request =>
            request.Candidate.Guid == "skill-a"
                ? ExactServiceApiSkillVerificationResult.Rejected(
                    ServiceApiNoReliableSkillReason.SkillIntegrityMismatch)
                : ExactServiceApiSkillVerificationResult.Verified(new ExactOrnnApiSkillProvenance
                {
                    Guid = request.Candidate.Guid,
                    CanonicalName = request.Candidate.CanonicalName,
                    LiteralVersion = request.Candidate.LiteralVersion,
                    SkillHash = request.Candidate.SkillHash,
                    PublisherId = request.Candidate.PublisherId,
                }));
        var service = new ManagedServiceApiSkillDiscoveryService(catalogue, ranker, verifier);

        var result = await service.DiscoverAsync(Request());

        ranker.Requests.Should().HaveCount(2);
        ranker.Requests[0].ExcludedCandidates.Should().BeEmpty();
        ranker.Requests[1].ExcludedCandidates.Select(static candidate => candidate.Guid)
            .Should().Equal("skill-a");
        verifier.Requests.Select(static request => request.Candidate.Guid)
            .Should().Equal("skill-a", "skill-b");
        result.ReliableSkill.Guid.Should().Be("skill-b");
    }

    private static ManagedCodexServiceApiSkillDiscoveryRequest Request() =>
        new(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-alpha",
                "caller-alpha",
                NyxIdCallerCredentialSelection.SourceReadableUserBearer("caller-token")),
            new ServiceApiSkillDiscoveryInput
            {
                CallerAuthority = new ExternalCapabilityAuthorizationOwner
                {
                    Authority = "nyxid",
                    OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                    OwnerSubject = "caller-alpha",
                },
                ScopeId = "scope-alpha",
                CallerId = "caller-alpha",
                TargetUserServiceId = "usvc-alpha",
                ServiceSlugSnapshot = "example-service",
                ServiceLabelSnapshot = "Example Service",
                NormalizedCapability = "send a message",
                ManagedDiscoveryPolicyVersion = "service_api_skill_discovery.v1",
                AdmissionPolicyVersion = "explicit-request-admission.v1",
                CapabilityFingerprint = new string('a', 64),
            });

    private static ServiceApiSkillCataloguePage Page(
        int page,
        int totalPages,
        params ServiceApiSkillCatalogueCandidate[] candidates)
    {
        var result = new ServiceApiSkillCataloguePage
        {
            Page = page,
            PageSize = 100,
            Total = totalPages == 1 ? candidates.Length : 2,
            TotalPages = totalPages,
        };
        result.Candidates.AddRange(candidates);
        return result;
    }

    private static ServiceApiSkillCatalogueCandidate Candidate(string guid, string canonicalName) =>
        new()
        {
            Guid = guid,
            CanonicalName = canonicalName,
            Description = $"{canonicalName} description",
        };

    private static ManagedCodexServiceApiSkillDiscoveryResult Reliable(string guid, string canonicalName) =>
        new()
        {
            ReliableSkill = new ReliableServiceApiSkillCandidate
            {
                Guid = guid,
                CanonicalName = canonicalName,
                LiteralVersion = "1.1",
                SkillHash = new string('b', 64),
                PublisherId = "publisher-alpha",
                RequestShape = new AdmittedNyxIdRequestShape
                {
                    Selector = new NyxIdRequestSelector
                    {
                        UserServiceId = "usvc-alpha",
                        Method = NyxIdRequestMethod.Post,
                        PathTemplate = "/v1/messages",
                        BodyMode = NyxIdRequestBodyMode.Json,
                        BodyRequired = true,
                        ResponseMode = NyxIdRequestResponseMode.Text,
                        Risk = NyxIdOperationRisk.Write,
                    },
                },
                Evidence =
                {
                    new ExactOrnnApiSkillEvidence
                    {
                        SkillFilePath = "SKILL.md",
                        Section = "Send a message",
                        OperationId = "send-message",
                    },
                },
            },
        };

    private static ManagedCodexServiceApiSkillDiscoveryResult NoReliable(
        ServiceApiNoReliableSkillReason reason) =>
        new()
        {
            NoReliableApiSkill = new NoReliableServiceApiSkill { Reason = reason },
        };

    private sealed class StubCataloguePort(params ServiceApiSkillCataloguePage[] pages) :
        IServiceApiSkillCataloguePort
    {
        public List<int> RequestedPages { get; } = [];

        public Task<ServiceApiSkillCataloguePage> ReadPageAsync(
            ServiceApiSkillCataloguePageRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestedPages.Add(request.Page);
            return Task.FromResult(pages.Single(page => page.Page == request.Page).Clone());
        }
    }

    private sealed class StubRanker(
        Func<ManagedCodexServiceApiSkillRankingInput, CancellationToken, ManagedCodexServiceApiSkillDiscoveryResult> rank) :
        IManagedCodexServiceApiSkillDiscoveryExecutor
    {
        public List<ManagedCodexServiceApiSkillRankingInput> Requests { get; } = [];

        public Task<ManagedCodexServiceApiSkillDiscoveryResult> DiscoverAsync(
            ManagedCodexServiceApiSkillRankingRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Input.Clone());
            return Task.FromResult(rank(request.Input, cancellationToken));
        }
    }

    private sealed class StubVerifier(
        Func<ExactServiceApiSkillVerificationRequest, ExactServiceApiSkillVerificationResult> verify) :
        IExactServiceApiSkillVerifier
    {
        public List<ExactServiceApiSkillVerificationRequest> Requests { get; } = [];

        public Task<ExactServiceApiSkillVerificationResult> VerifyAsync(
            ExactServiceApiSkillVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(verify(request));
        }
    }
}
