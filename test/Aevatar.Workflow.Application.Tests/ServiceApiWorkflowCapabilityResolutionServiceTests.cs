using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.ExternalCapabilities;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class ServiceApiWorkflowCapabilityResolutionServiceTests
{
    private const string TargetUserServiceId = "usvc-alpha";

    [Fact]
    public async Task DiscoverAsync_ShouldResolveTheSingleExactDescriptorBeforeManagedDiscovery()
    {
        var exactDescriptor = Descriptor(TargetUserServiceId, "send-message");
        var list = new StubListPort(exactDescriptor, Descriptor("usvc-beta", "send-message"));
        var managed = new StubManagedPort(ReliableManagedResult(RequestSelector()));
        var readiness = new StubReadinessPort(ReadyOperationReadiness(exactDescriptor));
        var fallback = new StubFallbackPort(FallbackExhausted());
        var service = new ServiceApiWorkflowCapabilityResolutionService(
            list,
            managed,
            readiness,
            fallback);

        var result = await service.DiscoverAsync(Request());

        result.ResultCase.Should()
            .Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution);
        result.Resolution.ResultCase.Should()
            .Be(ServiceApiCapabilityResolution.ResultOneofCase.NyxidOperation);
        result.Resolution.NyxidOperation.Selector.UserServiceId.Should().Be(TargetUserServiceId);
        result.Resolution.NyxidOperation.Selector.EndpointId.Should().Be("send-message");
        result.Resolution.NyxidOperation.Descriptor_.Selector.NyxIdOperation.EndpointId
            .Should().Be("send-message");
        list.Calls.Should().Be(1);
        managed.Calls.Should().Be(0);
        fallback.Calls.Should().Be(0);
        readiness.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldResolveTheCapabilityMatchedDescriptorAmongSameServiceEndpoints()
    {
        var exactDescriptor = Descriptor(
            TargetUserServiceId,
            "send-message",
            "Example Messaging / Send a message");
        var managed = new StubManagedPort(ReliableManagedResult(RequestSelector()));
        var service = new ServiceApiWorkflowCapabilityResolutionService(
            new StubListPort(
                Descriptor(TargetUserServiceId, "list-messages", "Example Messaging / List messages"),
                exactDescriptor,
                Descriptor(TargetUserServiceId, "delete-message", "Example Messaging / Delete message")),
            managed,
            new StubReadinessPort(ReadyOperationReadiness(exactDescriptor)),
            new StubFallbackPort(FallbackExhausted()));

        var result = await service.DiscoverAsync(Request());

        result.ResultCase.Should()
            .Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution);
        result.Resolution.ResultCase.Should()
            .Be(ServiceApiCapabilityResolution.ResultOneofCase.NyxidOperation);
        result.Resolution.NyxidOperation.Selector.EndpointId.Should().Be("send-message");
        managed.Calls.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldResolveDescriptorByTypedCapabilityKeyWhenDisplayNameDiffers()
    {
        var exactDescriptor = Descriptor(
            TargetUserServiceId,
            "send-message",
            "Example Messaging / Archive message");
        var managed = new StubManagedPort(ReliableManagedResult(RequestSelector()));
        var service = new ServiceApiWorkflowCapabilityResolutionService(
            new StubListPort(
                Descriptor(TargetUserServiceId, "list-messages", "Example Messaging / List messages"),
                exactDescriptor),
            managed,
            new MatchingReadinessPort(),
            new StubFallbackPort(FallbackExhausted()));

        var result = await service.DiscoverAsync(Request("send-message"));

        result.ResultCase.Should()
            .Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution);
        result.Resolution.ResultCase.Should()
            .Be(ServiceApiCapabilityResolution.ResultOneofCase.NyxidOperation);
        result.Resolution.NyxidOperation.Selector.EndpointId.Should().Be("send-message");
        managed.Calls.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldNotResolveSingleUnrelatedExactDescriptor()
    {
        var unrelatedDescriptor = Descriptor(
            TargetUserServiceId,
            "list-messages",
            "Example Messaging / List messages");
        var selector = RequestSelector();
        var managed = new StubManagedPort(ReliableManagedResult(selector));
        var service = new ServiceApiWorkflowCapabilityResolutionService(
            new StubListPort(unrelatedDescriptor),
            managed,
            new MatchingReadinessPort(),
            new StubFallbackPort(FallbackExhausted()));

        var result = await service.DiscoverAsync(Request());

        result.ResultCase.Should()
            .Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution);
        result.Resolution.ResultCase.Should()
            .Be(ServiceApiCapabilityResolution.ResultOneofCase.NyxidRequest);
        result.Resolution.NyxidRequest.RequestShape.Selector.Should().BeEquivalentTo(selector);
        managed.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldNotResolveDescriptorByDisplayNameWhenTypedCapabilityKeyDiffers()
    {
        var displayProxyDescriptor = Descriptor(
            TargetUserServiceId,
            "archive-message",
            "Example Messaging / send-message");
        var selector = RequestSelector();
        var managed = new StubManagedPort(ReliableManagedResult(selector));
        var service = new ServiceApiWorkflowCapabilityResolutionService(
            new StubListPort(displayProxyDescriptor),
            managed,
            new MatchingReadinessPort(),
            new StubFallbackPort(FallbackExhausted()));

        var result = await service.DiscoverAsync(Request("send-message"));

        result.ResultCase.Should()
            .Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution);
        result.Resolution.ResultCase.Should()
            .Be(ServiceApiCapabilityResolution.ResultOneofCase.NyxidRequest);
        result.Resolution.NyxidRequest.RequestShape.Selector.Should().BeEquivalentTo(selector);
        managed.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldMapVerifiedReadyOrnnCandidateToNyxIdRequest()
    {
        var selector = RequestSelector();
        var list = new StubListPort();
        var managed = new StubManagedPort(ReliableManagedResult(selector));
        var readiness = new StubReadinessPort(ReadyRequestReadiness(selector));
        var fallback = new StubFallbackPort(FallbackExhausted());
        var service = new ServiceApiWorkflowCapabilityResolutionService(
            list,
            managed,
            readiness,
            fallback);

        var result = await service.DiscoverAsync(Request());

        result.ResultCase.Should()
            .Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution);
        result.Resolution.ResultCase.Should()
            .Be(ServiceApiCapabilityResolution.ResultOneofCase.NyxidRequest);
        result.Resolution.NyxidRequest.ContractSourceCase.Should()
            .Be(ResolvedNyxIdRequest.ContractSourceOneofCase.OrnnSkill);
        result.Resolution.NyxidRequest.OrnnSkill.Guid.Should()
            .Be("d47a95c5-db2a-4f00-9057-27f674566bd5");
        result.Resolution.NyxidRequest.UserServiceId.Should().Be(TargetUserServiceId);
        result.Resolution.NyxidRequest.RequestShape.Selector.Should().BeEquivalentTo(selector);
        result.Resolution.NyxidRequest.AdmissionPolicyVersion.Should()
            .Be("explicit-request-admission.v1");
        managed.LastRequest.Should().NotBeNull();
        var discoveryInput = managed.LastRequest!.Input;
        discoveryInput.NormalizedCapabilityKey.Should().Be("send-message");
        discoveryInput.CapabilityFingerprint.Should().Be(
            ExternalWorkflowCapabilityContractDigest.Compute("send-message"));
        discoveryInput.WorkflowId.Should().Be("wf-alpha");
        discoveryInput.MemberId.Should().Be("m-alpha");
        discoveryInput.PublishedServiceId.Should().Be("svc-alpha");
        discoveryInput.DescriptorInventory.Should().BeEmpty();
        fallback.Calls.Should().Be(0);
        readiness.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldForwardNoReliableSkillToFallbackPort()
    {
        var noReliable = new NoReliableServiceApiSkill
        {
            Reason = ServiceApiNoReliableSkillReason.NoMatchingSkill,
        };
        var fallbackResolution = FallbackExhausted();
        var fallback = new StubFallbackPort(fallbackResolution);
        var service = new ServiceApiWorkflowCapabilityResolutionService(
            new StubListPort(),
            new StubManagedPort(new ManagedCodexServiceApiSkillDiscoveryResult
            {
                NoReliableApiSkill = noReliable,
            }),
            new StubReadinessPort(ReadyRequestReadiness(RequestSelector())),
            fallback);

        var result = await service.DiscoverAsync(Request());

        result.ResultCase.Should()
            .Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution);
        result.Resolution.Should().BeEquivalentTo(fallbackResolution.Resolution);
        fallback.Calls.Should().Be(1);
        fallback.LastRequest!.NoReliableApiSkill.Should().BeEquivalentTo(noReliable);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldInspectFallbackRequestReadinessBeforeResolving()
    {
        var selector = RequestSelector();
        var blocked = BlockedReadiness(new ExternalWorkflowCapabilitySelector
        {
            NyxIdRequest = selector.Clone(),
        });
        var readiness = new StubReadinessPort(blocked);
        var service = new ServiceApiWorkflowCapabilityResolutionService(
            new StubListPort(),
            new StubManagedPort(new ManagedCodexServiceApiSkillDiscoveryResult
            {
                NoReliableApiSkill = new NoReliableServiceApiSkill
                {
                    Reason = ServiceApiNoReliableSkillReason.NoMatchingSkill,
                },
            }),
            readiness,
            new StubFallbackPort(FallbackRequest(selector)));

        var result = await service.DiscoverAsync(Request());

        result.ResultCase.Should()
            .Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.ReadinessRequired);
        result.ReadinessRequired.Readiness.Should().BeEquivalentTo(blocked);
        readiness.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldPreserveManagedFailureWithoutCallingFallback()
    {
        var fallback = new StubFallbackPort(FallbackExhausted());
        var managed = new ThrowingManagedPort(new InvalidOperationException("typed managed failure"));
        var service = new ServiceApiWorkflowCapabilityResolutionService(
            new StubListPort(),
            managed,
            new StubReadinessPort(ReadyRequestReadiness(RequestSelector())),
            fallback);

        var act = () => service.DiscoverAsync(Request());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("typed managed failure");
        fallback.Calls.Should().Be(0);
    }

    [Fact]
    public async Task DiscoverAsync_ShouldProduceTypedExecutableReadinessHandoff()
    {
        var exactDescriptor = Descriptor(TargetUserServiceId, "send-message");
        var blocked = BlockedReadiness(exactDescriptor.Selector);
        var service = new ServiceApiWorkflowCapabilityResolutionService(
            new StubListPort(exactDescriptor),
            new StubManagedPort(ReliableManagedResult(RequestSelector())),
            new StubReadinessPort(blocked),
            new StubFallbackPort(FallbackExhausted()));

        var result = await service.DiscoverAsync(Request());

        result.ResultCase.Should()
            .Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.ReadinessRequired);
        result.ReadinessRequired.Readiness.Should().BeEquivalentTo(blocked);
        result.ReadinessRequired.Readiness.Remediations.Should().ContainSingle()
            .Which.TrustedLocator.Should().Be("nyxid:service:usvc-alpha:credentials");
        var retryInput = result.ReadinessRequired.Retry.DiscoveryInput;
        retryInput.ScopeId.Should().Be("scope-alpha");
        retryInput.CallerId.Should().Be("caller-alpha");
        retryInput.TargetUserServiceId.Should().Be(TargetUserServiceId);
        retryInput.WorkflowId.Should().Be("wf-alpha");
        retryInput.MemberId.Should().Be("m-alpha");
        retryInput.PublishedServiceId.Should().Be("svc-alpha");
        retryInput.DescriptorInventory.Should().ContainSingle();
        retryInput.CapabilityFingerprint.Should().Be(
            ExternalWorkflowCapabilityContractDigest.Compute("send-message"));
    }

    [Fact]
    public async Task RetryAfterRemediationAsync_ShouldReuseTheAuthoritativeResolutionInput()
    {
        var exactDescriptor = Descriptor(TargetUserServiceId, "send-message");
        var list = new StubListPort(exactDescriptor);
        var readiness = new QueueReadinessPort(
            BlockedReadiness(exactDescriptor.Selector),
            ReadyOperationReadiness(exactDescriptor));
        var service = new ServiceApiWorkflowCapabilityResolutionService(
            list,
            new StubManagedPort(ReliableManagedResult(RequestSelector())),
            readiness,
            new StubFallbackPort(FallbackExhausted()));

        var blocked = await service.DiscoverAsync(Request());
        var result = await service.RetryAfterRemediationAsync(
            Access(),
            blocked.ReadinessRequired.Retry);

        result.ResultCase.Should()
            .Be(ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution);
        result.Resolution.ResultCase.Should()
            .Be(ServiceApiCapabilityResolution.ResultOneofCase.NyxidOperation);
        list.Calls.Should().Be(1);
        readiness.Requests.Should().HaveCount(2);
        readiness.Requests[1].Access.ScopeId.Should().Be(readiness.Requests[0].Access.ScopeId);
        readiness.Requests[1].Access.CallerId.Should().Be(readiness.Requests[0].Access.CallerId);
        readiness.Requests[1].Selector.Should().BeEquivalentTo(readiness.Requests[0].Selector);
    }

    [Fact]
    public async Task RetryAfterRemediationAsync_ShouldRejectAuthoritySubstitution()
    {
        var exactDescriptor = Descriptor(TargetUserServiceId, "send-message");
        var service = new ServiceApiWorkflowCapabilityResolutionService(
            new StubListPort(exactDescriptor),
            new StubManagedPort(ReliableManagedResult(RequestSelector())),
            new StubReadinessPort(BlockedReadiness(exactDescriptor.Selector)),
            new StubFallbackPort(FallbackExhausted()));
        var blocked = await service.DiscoverAsync(Request());
        var substitutedAccess = new ExternalWorkflowCapabilityAccessContext(
            "scope-alpha",
            "caller-beta");

        var act = () => service.RetryAfterRemediationAsync(
            substitutedAccess,
            blocked.ReadinessRequired.Retry);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Service API capability retry authority does not match the original resolution authority.");
    }

    [Fact]
    public async Task RetryAfterRemediationAsync_ShouldRejectStoredCallerAuthoritySubstitution()
    {
        var exactDescriptor = Descriptor(TargetUserServiceId, "send-message");
        var service = new ServiceApiWorkflowCapabilityResolutionService(
            new StubListPort(exactDescriptor),
            new StubManagedPort(ReliableManagedResult(RequestSelector())),
            new StubReadinessPort(BlockedReadiness(exactDescriptor.Selector)),
            new StubFallbackPort(FallbackExhausted()));
        var blocked = await service.DiscoverAsync(Request());
        blocked.ReadinessRequired.Retry.DiscoveryInput.CallerAuthority.OwnerSubject = "caller-beta";

        var act = () => service.RetryAfterRemediationAsync(
            Access(),
            blocked.ReadinessRequired.Retry);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Caller authority does not match the authenticated caller identity.");
    }

    private static DiscoverServiceApiWorkflowCapabilityRequest Request(
        string capabilityKey = "send-message") =>
        new(
            Access(),
            new ExternalCapabilityAuthorizationOwner
            {
                Authority = "nyxid",
                OwnerKind = ExternalCapabilityAuthorizationOwnerKind.Personal,
                OwnerSubject = "caller-alpha",
            },
            TargetUserServiceId,
            "example-messaging",
            "Example Messaging",
            capabilityKey,
            "service_api_skill_discovery.v1",
            "explicit-request-admission.v1",
            ExternalCapabilityExecutionMode.Interactive,
            "wf-alpha",
            "m-alpha",
            "svc-alpha");

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new("scope-alpha", "caller-alpha");

    private static ExternalWorkflowCapabilityDescriptor Descriptor(
        string userServiceId,
        string endpointId,
        string displayName = "Send a message") =>
        new()
        {
            DisplayName = displayName,
            CapabilityKey = endpointId,
            Selector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdOperation = new NyxIdOperationSelector
                {
                    UserServiceId = userServiceId,
                    EndpointId = endpointId,
                },
            },
        };

    private static NyxIdRequestSelector RequestSelector() =>
        new()
        {
            UserServiceId = TargetUserServiceId,
            Method = NyxIdRequestMethod.Post,
            PathTemplate = "/v1/messages",
            BodyMode = NyxIdRequestBodyMode.Json,
            BodyRequired = true,
            ResponseMode = NyxIdRequestResponseMode.Text,
            Risk = NyxIdOperationRisk.Write,
        };

    private static ManagedCodexServiceApiSkillDiscoveryResult ReliableManagedResult(
        NyxIdRequestSelector selector) =>
        new()
        {
            ReliableSkill = new ReliableServiceApiSkillCandidate
            {
                CanonicalName = "example-messaging-service-api",
                Guid = "d47a95c5-db2a-4f00-9057-27f674566bd5",
                LiteralVersion = "1.1",
                SkillHash = "75f0e0480c4cbeed68ba97ffe0b26a0c0cc0ec2d8d0bed631306b383eec0f486",
                PublisherId = "publisher-alpha",
                RequestShape = new AdmittedNyxIdRequestShape
                {
                    Selector = selector,
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

    private static ExternalCapabilityReadiness ReadyOperationReadiness(
        ExternalWorkflowCapabilityDescriptor descriptor) =>
        new()
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedSelector = descriptor.Selector.Clone(),
            SelectedCapability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = descriptor.Selector.NyxIdOperation.UserServiceId,
                    EndpointId = descriptor.Selector.NyxIdOperation.EndpointId,
                },
            },
        };

    private static ExternalCapabilityReadiness ReadyRequestReadiness(
        NyxIdRequestSelector selector) =>
        new()
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedSelector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdRequest = selector.Clone(),
            },
            SelectedCapability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
                {
                    Request = selector.Clone(),
                },
            },
        };

    private static ExternalCapabilityReadiness BlockedReadiness(
        ExternalWorkflowCapabilitySelector selector)
    {
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.CredentialConnectionRequired,
            SelectedSelector = selector.Clone(),
        };
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = readiness.Status,
            Code = "NYXID_CREDENTIAL_CONNECTION_REQUIRED",
            SafeMessage = "Connect credentials for the selected service.",
        });
        readiness.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = ExternalCapabilityRemediationActionKind.ConnectCredential,
            Label = "Connect credentials",
            TrustedLocator = "nyxid:service:usvc-alpha:credentials",
        });
        return readiness;
    }

    private static ServiceApiWorkflowCapabilityDiscoveryResult FallbackExhausted() =>
        new()
        {
            Resolution = new ServiceApiCapabilityResolution
            {
                FallbackExhausted = new ServiceApiFallbackExhausted
                {
                    Reason = ServiceApiFallbackExhaustedReason.FallbackUnavailable,
                    SafeMessage = "No admitted fallback contract is available.",
                },
            },
        };

    private static ServiceApiWorkflowCapabilityDiscoveryResult FallbackRequest(
        NyxIdRequestSelector selector) =>
        new()
        {
            Resolution = new ServiceApiCapabilityResolution
            {
                NyxidRequest = new ResolvedNyxIdRequest
                {
                    OfficialWeb = new OfficialWebContractProvenance
                    {
                        CanonicalUrl = "https://docs.example.test/messages",
                        SourceTitle = "Messaging API",
                        FetchedContentDigest = new string('a', 64),
                    },
                    UserServiceId = TargetUserServiceId,
                    RequestShape = new AdmittedNyxIdRequestShape
                    {
                        Selector = selector.Clone(),
                    },
                    AdmissionPolicyVersion = "explicit-request-admission.v1",
                },
            },
        };

    private sealed class StubListPort(params ExternalWorkflowCapabilityDescriptor[] descriptors) :
        IExternalWorkflowCapabilityListPort
    {
        public int Calls { get; private set; }

        public Task<ExternalWorkflowCapabilityDiscoveryResult> ListAsync(
            ListExternalWorkflowCapabilitiesRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var result = new ExternalWorkflowCapabilityDiscoveryResult();
            result.Capabilities.Add(descriptors.Select(static descriptor => descriptor.Clone()));
            return Task.FromResult(result);
        }
    }

    private sealed class StubManagedPort(ManagedCodexServiceApiSkillDiscoveryResult result) :
        IManagedCodexServiceApiSkillDiscoveryPort
    {
        public int Calls { get; private set; }
        public ManagedCodexServiceApiSkillDiscoveryRequest? LastRequest { get; private set; }

        public Task<ManagedCodexServiceApiSkillDiscoveryResult> DiscoverAsync(
            ManagedCodexServiceApiSkillDiscoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(result.Clone());
        }
    }

    private sealed class ThrowingManagedPort(Exception exception) :
        IManagedCodexServiceApiSkillDiscoveryPort
    {
        public Task<ManagedCodexServiceApiSkillDiscoveryResult> DiscoverAsync(
            ManagedCodexServiceApiSkillDiscoveryRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ManagedCodexServiceApiSkillDiscoveryResult>(exception);
    }

    private sealed class StubReadinessPort(ExternalCapabilityReadiness readiness) :
        IExternalWorkflowCapabilityReadinessPort
    {
        public int Calls { get; private set; }

        public Task<ExternalCapabilityReadiness> InspectAsync(
            InspectExternalWorkflowCapabilityReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(readiness.Clone());
        }
    }

    private sealed class QueueReadinessPort(params ExternalCapabilityReadiness[] readiness) :
        IExternalWorkflowCapabilityReadinessPort
    {
        private readonly Queue<ExternalCapabilityReadiness> _readiness = new(readiness);

        public List<InspectExternalWorkflowCapabilityReadinessRequest> Requests { get; } = [];

        public Task<ExternalCapabilityReadiness> InspectAsync(
            InspectExternalWorkflowCapabilityReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_readiness.Dequeue().Clone());
        }
    }

    private sealed class MatchingReadinessPort : IExternalWorkflowCapabilityReadinessPort
    {
        public Task<ExternalCapabilityReadiness> InspectAsync(
            InspectExternalWorkflowCapabilityReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            var readiness = request.Selector.SelectorCase switch
            {
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation =>
                    ReadyOperationReadiness(new ExternalWorkflowCapabilityDescriptor
                    {
                        Selector = request.Selector.Clone(),
                    }),
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest =>
                    ReadyRequestReadiness(request.Selector.NyxIdRequest),
                _ => throw new InvalidOperationException("Unsupported selector."),
            };
            return Task.FromResult(readiness);
        }
    }

    private sealed class StubFallbackPort(ServiceApiWorkflowCapabilityDiscoveryResult result) :
        IServiceApiCapabilityFallbackPort
    {
        public int Calls { get; private set; }
        public ResolveServiceApiCapabilityFallbackRequest? LastRequest { get; private set; }

        public Task<ServiceApiWorkflowCapabilityDiscoveryResult> ResolveAsync(
            ResolveServiceApiCapabilityFallbackRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(result.Clone());
        }
    }
}
