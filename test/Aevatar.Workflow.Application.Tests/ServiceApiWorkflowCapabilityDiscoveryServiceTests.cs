using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.DependencyInjection;
using Aevatar.Workflow.Application.ExternalCapabilities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Application.Tests;

public sealed class ServiceApiWorkflowCapabilityDiscoveryServiceTests
{
    private const string TargetUserServiceId = "usvc-alpha";
    private const string OtherUserServiceId = "usvc-beta";
    private const string SkillGuid = "d47a95c5-db2a-4f00-9057-27f674566bd5";
    private const string SkillHash = "75f0e0480c4cbeed68ba97ffe0b26a0c0cc0ec2d8d0bed631306b383eec0f486";
    private const string PublisherId = "9f42ce90-8b05-406d-8461-acb5fdfa4fab";
    private const string CapabilityFingerprint =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task DiscoverAsync_ShouldResolveExactDescriptorBeforeManagedDiscovery()
    {
        var managed = new StubManagedDiscoveryExecutor(ReliableManagedResult());
        var verifier = new StubExactSkillVerifier(ExactServiceApiSkillVerificationResult.Verified(OrnnProvenance()));
        var readiness = new StubReadinessPort(ReadyReadiness(NyxIdRequestSelector()));
        var service = new ServiceApiWorkflowCapabilityDiscoveryService(
            managed,
            verifier,
            readiness,
            UnusedWebFallback());
        var exactDescriptor = Descriptor(new ExternalWorkflowCapabilitySelector
        {
            NyxIdOperation = new NyxIdOperationSelector
            {
                UserServiceId = TargetUserServiceId,
                EndpointId = "send-message",
            },
        });
        var otherDescriptor = Descriptor(new ExternalWorkflowCapabilitySelector
        {
            NyxIdOperation = new NyxIdOperationSelector
            {
                UserServiceId = OtherUserServiceId,
                EndpointId = "send-message",
            },
        });

        var result = await service.DiscoverAsync(
            Request(Input([otherDescriptor, exactDescriptor])),
            CancellationToken.None);

        result.ResultCase.Should().Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution);
        result.Resolution.ResultCase.Should().Be(ServiceApiCapabilityResolution.ResultOneofCase.NyxidOperation);
        result.Resolution.NyxidOperation.Selector.UserServiceId.Should().Be(TargetUserServiceId);
        result.Resolution.NyxidOperation.Selector.EndpointId.Should().Be("send-message");
        result.Resolution.NyxidOperation.Descriptor_.Selector.NyxIdOperation.UserServiceId
            .Should().Be(TargetUserServiceId);
        managed.Calls.Should().Be(0);
        verifier.Calls.Should().Be(0);
        readiness.Calls.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldAdmitReliableExactSkillAsNyxIdRequest()
    {
        var requestSelector = NyxIdRequestSelector();
        var admittedSelector = requestSelector.Clone();
        admittedSelector.QueryParameters.Add("conversation_id");
        var readiness = new StubReadinessPort(ReadyReadiness(admittedSelector));
        var managed = new StubManagedDiscoveryExecutor(ReliableManagedResult(requestSelector));
        var verifier = new StubExactSkillVerifier(ExactServiceApiSkillVerificationResult.Verified(OrnnProvenance()));
        var service = new ServiceApiWorkflowCapabilityDiscoveryService(
            managed,
            verifier,
            readiness,
            UnusedWebFallback());

        var result = await service.DiscoverAsync(
            Request(Input([])),
            CancellationToken.None);

        result.ResultCase.Should().Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution);
        result.Resolution.ResultCase.Should().Be(ServiceApiCapabilityResolution.ResultOneofCase.NyxidRequest);
        var resolved = result.Resolution.NyxidRequest;
        resolved.ContractSourceCase.Should().Be(ResolvedNyxIdRequest.ContractSourceOneofCase.OrnnSkill);
        resolved.OrnnSkill.Guid.Should().Be(SkillGuid);
        resolved.OrnnSkill.SkillHash.Should().Be(SkillHash);
        resolved.UserServiceId.Should().Be(TargetUserServiceId);
        resolved.AdmissionPolicyVersion.Should().Be("explicit-request-admission.v1");
        resolved.RequestShape.Selector.UserServiceId.Should().Be(TargetUserServiceId);
        resolved.RequestShape.Selector.QueryParameters.Should().ContainSingle().Which.Should().Be("conversation_id");
        managed.Calls.Should().Be(1);
        verifier.Calls.Should().Be(1);
        readiness.Calls.Should().Be(1);
        readiness.LastRequest!.Selector.SelectorCase.Should()
            .Be(ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest);
        readiness.LastRequest.Selector.NyxIdRequest.UserServiceId.Should().Be(TargetUserServiceId);
        readiness.LastRequest.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldDelegateValidNoReliableSkillToApplicationOwnedWebFallback()
    {
        var managed = new StubManagedDiscoveryExecutor(new ManagedCodexServiceApiSkillDiscoveryResult
        {
            NoReliableApiSkill = new NoReliableServiceApiSkill
            {
                Reason = ServiceApiNoReliableSkillReason.NoMatchingSkill,
            },
        });
        var verifier = new StubExactSkillVerifier(ExactServiceApiSkillVerificationResult.Verified(OrnnProvenance()));
        var readiness = new StubReadinessPort(ReadyReadiness(NyxIdRequestSelector()));
        var webFallback = new StubWebFallbackPort(new ServiceApiWebFallbackResult
        {
            FallbackExhausted = new ServiceApiFallbackExhausted
            {
                Reason = ServiceApiFallbackExhaustedReason.OfficialDocumentationNotFound,
                SafeMessage = "Official API documentation was not found.",
            },
        });
        var service = new ServiceApiWorkflowCapabilityDiscoveryService(
            managed,
            verifier,
            readiness,
            webFallback);

        var result = await service.DiscoverAsync(
            Request(Input([])),
            CancellationToken.None);

        result.ResultCase.Should().Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution);
        result.Resolution.ResultCase.Should().Be(ServiceApiCapabilityResolution.ResultOneofCase.FallbackExhausted);
        result.Resolution.FallbackExhausted.Reason.Should().Be(
            ServiceApiFallbackExhaustedReason.OfficialDocumentationNotFound);
        managed.Calls.Should().Be(1);
        verifier.Calls.Should().Be(0);
        readiness.Calls.Should().Be(0);
        webFallback.Calls.Should().Be(1);
        webFallback.LastRequest!.NoReliableApiSkill.Reason.Should().Be(
            ServiceApiNoReliableSkillReason.NoMatchingSkill);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldAdmitApplicationOwnedWebFallbackCandidate()
    {
        var managed = new StubManagedDiscoveryExecutor(new ManagedCodexServiceApiSkillDiscoveryResult
        {
            NoReliableApiSkill = new NoReliableServiceApiSkill
            {
                Reason = ServiceApiNoReliableSkillReason.NoMatchingSkill,
            },
        });
        var verifier = new StubExactSkillVerifier(ExactServiceApiSkillVerificationResult.Verified(OrnnProvenance()));
        var admittedSelector = NyxIdRequestSelector();
        admittedSelector.QueryParameters.Add("conversation_id");
        var readiness = new StubReadinessPort(ReadyReadiness(admittedSelector));
        var webFallback = new StubWebFallbackPort(new ServiceApiWebFallbackResult
        {
            RequestShapeCandidate = new OfficialWebRequestShapeCandidate
            {
                Provenance = new OfficialWebContractProvenance
                {
                    CanonicalUrl = "https://docs.example.com/api/messages",
                    SourceTitle = "Messages API",
                    FetchedContentDigest = CapabilityFingerprint,
                },
                Selector = NyxIdRequestSelector(),
            },
        });
        var service = new ServiceApiWorkflowCapabilityDiscoveryService(
            managed,
            verifier,
            readiness,
            webFallback);

        var result = await service.DiscoverAsync(Request(Input([])), CancellationToken.None);

        result.Resolution.ResultCase.Should().Be(ServiceApiCapabilityResolution.ResultOneofCase.NyxidRequest);
        result.Resolution.NyxidRequest.ContractSourceCase.Should().Be(
            ResolvedNyxIdRequest.ContractSourceOneofCase.OfficialWeb);
        result.Resolution.NyxidRequest.OfficialWeb.CanonicalUrl.Should().Be(
            "https://docs.example.com/api/messages");
        result.Resolution.NyxidRequest.RequestShape.Selector.QueryParameters.Should()
            .ContainSingle().Which.Should().Be("conversation_id");
        verifier.Calls.Should().Be(0);
        readiness.Calls.Should().Be(1);
        webFallback.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldMapExactVerificationRejectionToNoReliableSkill()
    {
        var managed = new StubManagedDiscoveryExecutor(ReliableManagedResult());
        var verifier = new StubExactSkillVerifier(ExactServiceApiSkillVerificationResult.Rejected(
            ServiceApiNoReliableSkillReason.SkillIntegrityMismatch));
        var readiness = new StubReadinessPort(ReadyReadiness(NyxIdRequestSelector()));
        var service = new ServiceApiWorkflowCapabilityDiscoveryService(
            managed,
            verifier,
            readiness,
            UnusedWebFallback());

        var result = await service.DiscoverAsync(
            Request(Input([])),
            CancellationToken.None);

        result.ResultCase.Should().Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.NoReliableApiSkill);
        result.NoReliableApiSkill.Reason.Should().Be(ServiceApiNoReliableSkillReason.SkillIntegrityMismatch);
        managed.Calls.Should().Be(1);
        verifier.Calls.Should().Be(1);
        readiness.Calls.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldMapReadinessRejectionToNoReliableSkill()
    {
        var managed = new StubManagedDiscoveryExecutor(ReliableManagedResult());
        var verifier = new StubExactSkillVerifier(ExactServiceApiSkillVerificationResult.Verified(OrnnProvenance()));
        var readiness = new StubReadinessPort(new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.CredentialConnectionRequired,
            SelectedSelector = new ExternalWorkflowCapabilitySelector { NyxIdRequest = NyxIdRequestSelector() },
        });
        var service = new ServiceApiWorkflowCapabilityDiscoveryService(
            managed,
            verifier,
            readiness,
            UnusedWebFallback());

        var result = await service.DiscoverAsync(
            Request(Input([])),
            CancellationToken.None);

        result.ResultCase.Should().Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.NoReliableApiSkill);
        result.NoReliableApiSkill.Reason.Should().Be(
            ServiceApiNoReliableSkillReason.RequestShapeAdmissionRejected);
        managed.Calls.Should().Be(1);
        verifier.Calls.Should().Be(1);
        readiness.Calls.Should().Be(1);
    }

    [Fact]
    public void AddWorkflowApplication_ShouldNotRegisterManagedServiceApiDiscoveryPort()
    {
        var services = new ServiceCollection();

        services.AddWorkflowApplication();

        services.Should().NotContain(static descriptor =>
            descriptor.ServiceType == typeof(IServiceApiWorkflowCapabilityDiscoveryPort));
    }

    private static DiscoverServiceApiWorkflowCapabilityRequest Request(ServiceApiSkillDiscoveryInput input) =>
        new(Access(), input);

    private static ServiceApiSkillDiscoveryInput Input(
        IReadOnlyList<ExternalWorkflowCapabilityDescriptor> descriptors) =>
        new()
        {
            CallerAuthority = new ExternalCapabilityAuthorizationOwner
            {
                Authority = "nyxid",
                OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            },
            ScopeId = "scope-alpha",
            CallerId = "caller-alpha",
            TargetUserServiceId = TargetUserServiceId,
            ServiceSlugSnapshot = "example-messaging",
            ServiceLabelSnapshot = "Example Messaging",
            NormalizedCapability = "send a message to a conversation",
            ManagedDiscoveryPolicyVersion = "service_api_skill_discovery.v1",
            AdmissionPolicyVersion = "explicit-request-admission.v1",
            CapabilityFingerprint = CapabilityFingerprint,
            DescriptorInventory = { descriptors },
        };

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new(
            "scope-alpha",
            "caller-alpha",
            NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                "runtime-caller-credential"));

    private static ExternalWorkflowCapabilityDescriptor Descriptor(ExternalWorkflowCapabilitySelector selector) =>
        new()
        {
            Selector = selector,
            DisplayName = "Example descriptor",
        };

    private static ManagedCodexServiceApiSkillDiscoveryResult ReliableManagedResult(
        NyxIdRequestSelector? selector = null)
    {
        var candidate = ReliableCandidate(selector ?? NyxIdRequestSelector());
        return new ManagedCodexServiceApiSkillDiscoveryResult
        {
            ReliableSkill = candidate,
        };
    }

    private static ReliableServiceApiSkillCandidate ReliableCandidate(NyxIdRequestSelector selector)
    {
        var candidate = new ReliableServiceApiSkillCandidate
        {
            CanonicalName = "example-messaging-service-api",
            Guid = SkillGuid,
            LiteralVersion = "1.1",
            SkillHash = SkillHash,
            PublisherId = PublisherId,
            RequestShape = new AdmittedNyxIdRequestShape
            {
                Selector = selector,
            },
        };
        candidate.Evidence.Add(new ExactOrnnApiSkillEvidence
        {
            SkillFilePath = "SKILL.md",
            Section = "Send a message",
            OperationId = "send-message",
        });
        return candidate;
    }

    private static ExactOrnnApiSkillProvenance OrnnProvenance()
    {
        var provenance = new ExactOrnnApiSkillProvenance
        {
            CanonicalName = "example-messaging-service-api",
            Guid = SkillGuid,
            LiteralVersion = "1.1",
            SkillHash = SkillHash,
            PublisherId = PublisherId,
        };
        provenance.Evidence.Add(new ExactOrnnApiSkillEvidence
        {
            SkillFilePath = "SKILL.md",
            Section = "Send a message",
            OperationId = "send-message",
        });
        return provenance;
    }

    private static NyxIdRequestSelector NyxIdRequestSelector() =>
        new()
        {
            UserServiceId = TargetUserServiceId,
            Method = NyxIdRequestMethod.Post,
            PathTemplate = "/v1/messages",
            HeaderParameters = { "Accept" },
            BodyMode = NyxIdRequestBodyMode.Json,
            BodyRequired = true,
            ResponseMode = NyxIdRequestResponseMode.Text,
            Risk = NyxIdOperationRisk.Write,
        };

    private static ExternalCapabilityReadiness ReadyReadiness(NyxIdRequestSelector selector) =>
        new()
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedSelector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdRequest = selector,
            },
            SelectedCapability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
                {
                    Request = selector,
                    ServiceSlugSnapshot = "example-messaging",
                    ContractDigest = "nyxid-request-digest",
                    ExecutionPolicy = new NyxIdOperationExecutionPolicy
                    {
                        Risk = NyxIdOperationRisk.Write,
                        Approval = NyxIdOperationApproval.Required,
                        EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
                        AllowedExecutionModes = { ExternalCapabilityExecutionMode.Interactive },
                    },
                },
            },
        };

    private static StubWebFallbackPort UnusedWebFallback() =>
        new(new ServiceApiWebFallbackResult
        {
            FallbackExhausted = new ServiceApiFallbackExhausted
            {
                Reason = ServiceApiFallbackExhaustedReason.WebResearchFailed,
                SafeMessage = "Web fallback should not be called by this test.",
            },
        });

    private sealed class StubManagedDiscoveryExecutor(
        ManagedCodexServiceApiSkillDiscoveryResult result) : IManagedCodexServiceApiSkillDiscoveryExecutor
    {
        public int Calls { get; private set; }

        public Task<ManagedCodexServiceApiSkillDiscoveryResult> DiscoverAsync(
            ManagedCodexServiceApiSkillDiscoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result.Clone());
        }
    }

    private sealed class StubExactSkillVerifier(
        ExactServiceApiSkillVerificationResult result) : IExactServiceApiSkillVerifier
    {
        public int Calls { get; private set; }

        public Task<ExactServiceApiSkillVerificationResult> VerifyAsync(
            ExactServiceApiSkillVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result.Clone());
        }
    }

    private sealed class StubReadinessPort(ExternalCapabilityReadiness readiness)
        : IExternalWorkflowCapabilityReadinessPort
    {
        public int Calls { get; private set; }

        public InspectExternalWorkflowCapabilityReadinessRequest? LastRequest { get; private set; }

        public Task<ExternalCapabilityReadiness> InspectAsync(
            InspectExternalWorkflowCapabilityReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(readiness.Clone());
        }
    }

    private sealed class StubWebFallbackPort(ServiceApiWebFallbackResult result)
        : IServiceApiWebFallbackPort
    {
        public int Calls { get; private set; }

        public ResolveServiceApiWebFallbackRequest? LastRequest { get; private set; }

        public Task<ServiceApiWebFallbackResult> ResolveAsync(
            ResolveServiceApiWebFallbackRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(result.Clone());
        }
    }
}
