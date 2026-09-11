using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions.Credentials;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Tools;

public sealed class ChannelRegistrationAuthorityAdmissionExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_DurableChannelRegistrationAgentKeyWithoutAdmissionPort_ShouldFailClosed()
    {
        var tool = new RecordingTool();
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync(CreateRequest(
            tool,
            CreateDurableChannelRegistrationContext()));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("credential_denied");
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.CredentialPolicy);
        outcome.TerminalInvoked.Should().BeFalse();
        tool.ExecuteCount.Should().Be(0);
    }

    [Theory]
    [InlineData("missing_port")]
    [InlineData("credential_kind_mismatch")]
    [InlineData("credential_descriptor_missing")]
    [InlineData("operation_admission_missing")]
    public async Task ExecuteAsync_ExecutorSideAdmissionDenial_ShouldFailClosed(string scenario)
    {
        var tool = new RecordingTool();
        IChannelRegistrationAuthorityAdmissionPort? admissionPort =
            new RecordingAuthorityAdmissionPort(ChannelRegistrationAuthorityAdmissionResult.Allow());
        var context = CreateDurableChannelRegistrationContext();
        switch (scenario)
        {
            case "missing_port":
                admissionPort = null;
                break;
            case "credential_kind_mismatch":
                context = context with
                {
                    Credentials = context.Credentials with
                    {
                        NyxIdCredentialKind = AgentToolNyxIdCredentialKind.ProxyDelegation,
                    },
                };
                break;
            case "credential_descriptor_missing":
                context = context with { DurableNyxIdCredential = null };
                break;
            case "operation_admission_missing":
                context = context with { OperationAdmission = null };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        var executor = CreateExecutor(admissionPort);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool, context));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("credential_denied");
        outcome.TerminalInvoked.Should().BeFalse();
        tool.ExecuteCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_PortDecision_ShouldControlExecution(bool allowed)
    {
        var tool = new RecordingTool();
        var portResult = allowed
            ? ChannelRegistrationAuthorityAdmissionResult.Allow()
            : ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.TargetNotAuthorized);
        var admissionPort = new RecordingAuthorityAdmissionPort(portResult);
        var executor = CreateExecutor(admissionPort);

        var outcome = await executor.ExecuteAsync(CreateRequest(
            tool,
            CreateDurableChannelRegistrationContext()));

        outcome.Kind.Should().Be(allowed
            ? AgentToolExecutionOutcomeKind.Executed
            : AgentToolExecutionOutcomeKind.Denied);
        tool.ExecuteCount.Should().Be(allowed ? 1 : 0);
        admissionPort.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("invalid_result")]
    [InlineData("exception")]
    public async Task ExecuteAsync_InvalidOrThrowingAuthorityPort_ShouldFailClosed(
        string scenario)
    {
        var tool = new RecordingTool();
        var admissionPort = new RecordingAuthorityAdmissionPort((_, _) => scenario switch
        {
            "invalid_result" => Task.FromResult(
                new ChannelRegistrationAuthorityAdmissionResult(
                    false,
                    ChannelRegistrationAuthorityAdmissionReason.Allowed)),
            "exception" => Task.FromException<ChannelRegistrationAuthorityAdmissionResult>(
                new InvalidOperationException("provider body contains key-secret-123")),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        });
        var executor = CreateExecutor(admissionPort);

        var outcome = await executor.ExecuteAsync(CreateRequest(
            tool,
            CreateDurableChannelRegistrationContext()));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("credential_denied");
        outcome.TerminalInvoked.Should().BeFalse();
        tool.ExecuteCount.Should().Be(0);
        admissionPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_SenderTokenLabeledChannelRegistrationWithoutDurableDescriptor_ShouldRemainSeparate()
    {
        var tool = new RecordingTool();
        var admissionPort = new RecordingAuthorityAdmissionPort(
            ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.TargetNotAuthorized));
        var executor = CreateExecutor(admissionPort);
        var context = CreateBaseContext() with
        {
            CredentialSource = AgentToolCredentialSource.ChannelRegistration,
            Credentials = new AgentToolCredentials(
                NyxIdAccessToken: null,
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: "sender-token"),
            SenderBinding = new AgentToolSenderBindingContext(
                "binding-alpha",
                "user-alpha",
                "tenant-alpha"),
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool, context));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        outcome.FailureCode.Should().BeEmpty();
        tool.ExecuteCount.Should().Be(1);
        admissionPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_NonChannelCredential_ShouldNotInvokeRegistrationAdmission()
    {
        var tool = new RecordingTool();
        var admissionPort = new RecordingAuthorityAdmissionPort(
            ChannelRegistrationAuthorityAdmissionResult.Allow());
        var executor = CreateExecutor(admissionPort);

        var outcome = await executor.ExecuteAsync(CreateRequest(tool, CreateBaseContext()));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        tool.ExecuteCount.Should().Be(1);
        admissionPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ProxyDelegationChannelRegistrationCandidate_ShouldFailClosedBeforeAuthorityPort()
    {
        var tool = new RecordingTool();
        var admissionPort = new RecordingAuthorityAdmissionPort(
            ChannelRegistrationAuthorityAdmissionResult.Allow());
        var executor = CreateExecutor(admissionPort);
        var context = CreateDurableChannelRegistrationContext() with
        {
            Credentials = new AgentToolCredentials(
                NyxIdAccessToken: "proxy-delegation-token",
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                NyxIdCredentialKind: AgentToolNyxIdCredentialKind.ProxyDelegation),
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool, context));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("credential_denied");
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.CredentialPolicy);
        tool.ExecuteCount.Should().Be(0);
        admissionPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_UnspecifiedCredentialWithChannelRegistrationLabel_ShouldFailClosed()
    {
        var tool = new RecordingTool();
        var executor = CreateExecutor();
        var context = CreateBaseContext() with
        {
            CredentialSource = AgentToolCredentialSource.ChannelRegistration,
            Credentials = new AgentToolCredentials(
                NyxIdAccessToken: "primary-token",
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                NyxIdCredentialKind: AgentToolNyxIdCredentialKind.Unspecified),
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool, context));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("credential_denied");
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.CredentialPolicy);
        tool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_UnspecifiedCredentialWithChannelRegistrationDescriptor_ShouldFailClosed()
    {
        var tool = new RecordingTool();
        var executor = CreateExecutor();
        var channelContext = CreateDurableChannelRegistrationContext();
        var context = CreateBaseContext() with
        {
            CredentialSource = AgentToolCredentialSource.BearerToken,
            Credentials = new AgentToolCredentials(
                NyxIdAccessToken: "primary-token",
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                NyxIdCredentialKind: AgentToolNyxIdCredentialKind.Unspecified),
            DurableNyxIdCredential = channelContext.DurableNyxIdCredential!.Clone(),
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool, context));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("credential_denied");
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.CredentialPolicy);
        tool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_UnspecifiedCredentialWithChannelRegistrationTopLevelPurpose_ShouldFailClosed()
    {
        var tool = new RecordingTool();
        var executor = CreateExecutor();
        var descriptor = CreateDurableChannelRegistrationContext().DurableNyxIdCredential!.Clone();
        descriptor.SourceKind = DurableCallerCredentialSourceKind.Unspecified;
        descriptor.SecretReference.Purpose = "different-purpose";
        var context = CreateBaseContext() with
        {
            CredentialSource = AgentToolCredentialSource.BearerToken,
            Credentials = new AgentToolCredentials(
                NyxIdAccessToken: "primary-token",
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                NyxIdCredentialKind: AgentToolNyxIdCredentialKind.Unspecified),
            DurableNyxIdCredential = descriptor,
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool, context));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("credential_denied");
        tool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_UnspecifiedCredentialWithChannelRegistrationNestedPurpose_ShouldFailClosed()
    {
        var tool = new RecordingTool();
        var executor = CreateExecutor();
        var descriptor = CreateDurableChannelRegistrationContext().DurableNyxIdCredential!.Clone();
        descriptor.SourceKind = DurableCallerCredentialSourceKind.Unspecified;
        descriptor.Purpose = string.Empty;
        var context = CreateBaseContext() with
        {
            CredentialSource = AgentToolCredentialSource.BearerToken,
            Credentials = new AgentToolCredentials(
                NyxIdAccessToken: "primary-token",
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                NyxIdCredentialKind: AgentToolNyxIdCredentialKind.Unspecified),
            DurableNyxIdCredential = descriptor,
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool, context));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("credential_denied");
        tool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_PreShapedSourceReadableBearerChannelCandidate_ShouldFailClosed()
    {
        var tool = new RecordingTool();
        var executor = CreateExecutor();
        var context = CreateDurableChannelRegistrationContext() with
        {
            Credentials = new AgentToolCredentials(
                NyxIdAccessToken: "pre-shaped-user-token",
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                NyxIdCredentialKind: AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            SenderBinding = AgentToolSenderBindingContext.Empty,
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool, context));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("credential_denied");
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.CredentialPolicy);
        tool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_DurableChannelRegistrationAgentKeyAllowedByPort_ShouldExecute()
    {
        var tool = new RecordingTool();
        var admissionPort = new RecordingAuthorityAdmissionPort(
            ChannelRegistrationAuthorityAdmissionResult.Allow());
        var executor = CreateExecutor(admissionPort);

        var outcome = await executor.ExecuteAsync(CreateRequest(
            tool,
            CreateDurableChannelRegistrationContext()));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Executed);
        tool.ExecuteCount.Should().Be(1);
        admissionPort.Requests.Should().ContainSingle();
        admissionPort.Requests[0].Credential.SubjectId.Should().Be("key-alpha");
        admissionPort.Requests[0].Operation.ServiceInstanceId.Should().Be("svc-alpha");
    }

    [Theory]
    [InlineData(
        ChannelRegistrationAuthorityAdmissionReason.TargetNotGranted,
        "channel_service_not_allowed")]
    [InlineData(
        ChannelRegistrationAuthorityAdmissionReason.TargetNotAuthorized,
        "channel_service_not_allowed")]
    [InlineData(
        ChannelRegistrationAuthorityAdmissionReason.RegistrationMissing,
        "registration_not_found")]
    [InlineData(
        ChannelRegistrationAuthorityAdmissionReason.AuthorizationContractInvalid,
        "channel_authorization_contract_invalid")]
    [InlineData(
        ChannelRegistrationAuthorityAdmissionReason.RegistrationAmbiguous,
        "credential_denied")]
    [InlineData(
        ChannelRegistrationAuthorityAdmissionReason.CredentialDescriptorMismatch,
        "credential_denied")]
    [InlineData(
        ChannelRegistrationAuthorityAdmissionReason.OperationAdmissionMissing,
        "credential_denied")]
    [InlineData(
        ChannelRegistrationAuthorityAdmissionReason.AuthorityUnavailable,
        "credential_denied")]
    public async Task ExecuteAsync_DurableChannelRegistrationAgentKeyDeniedByPort_ShouldMapSafeFailureCode(
        ChannelRegistrationAuthorityAdmissionReason reason,
        string expectedFailureCode)
    {
        var tool = new RecordingTool();
        var admissionPort = new RecordingAuthorityAdmissionPort(
            ChannelRegistrationAuthorityAdmissionResult.Deny(reason));
        var executor = CreateExecutor(admissionPort);

        var outcome = await executor.ExecuteAsync(CreateRequest(
            tool,
            CreateDurableChannelRegistrationContext()));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be(expectedFailureCode);
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.CredentialPolicy);
        tool.ExecuteCount.Should().Be(0);
        admissionPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_ChannelRegistrationAgentKeyWithoutDurableDescriptor_ShouldFailClosed()
    {
        var tool = new RecordingTool();
        var executor = CreateExecutor();
        var context = CreateDurableChannelRegistrationContext() with
        {
            DurableNyxIdCredential = null,
        };

        var outcome = await executor.ExecuteAsync(CreateRequest(tool, context));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("credential_denied");
        tool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ChannelRegistrationAgentKeyWithMalformedDurableDescriptor_ShouldFailClosed()
    {
        var tool = new RecordingTool();
        var executor = CreateExecutor();
        var context = CreateDurableChannelRegistrationContext();
        var malformed = context.DurableNyxIdCredential!.Clone();
        malformed.SourceKind = DurableCallerCredentialSourceKind.Unspecified;
        malformed.Purpose = string.Empty;

        var outcome = await executor.ExecuteAsync(CreateRequest(
            tool,
            context with { DurableNyxIdCredential = malformed }));

        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Denied);
        outcome.FailureCode.Should().Be("credential_denied");
        tool.ExecuteCount.Should().Be(0);
    }

    private static AdmittedAgentToolExecutor CreateExecutor(
        IChannelRegistrationAuthorityAdmissionPort? admissionPort = null) =>
        new(
            new StartingAdmissionLedger(),
            new AppendedAuditTrail(),
            new StableIdentityHasher(),
            channelRegistrationAuthorityAdmissionPort: admissionPort);

    private static AgentToolExecutionRequest CreateRequest(
        IAgentTool tool,
        AgentToolExecutionContext context) =>
        new(
            tool,
            "{}",
            context,
            AgentToolApprovalContinuationMode.None,
            null);

    private static AgentToolExecutionContext CreateDurableChannelRegistrationContext()
    {
        var secretReference = new SecretReference
        {
            Ref = "vault://channel/key-alpha",
            Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey,
            Fingerprint = "fingerprint-alpha",
            Version = 1,
            OwnerScopeKey = "scope-alpha",
            CreatedAtUnixMs = 1,
        };
        return CreateBaseContext() with
        {
            CredentialSource = AgentToolCredentialSource.ChannelRegistration,
            Credentials = new AgentToolCredentials(
                NyxIdAccessToken: "agent-key-token",
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                NyxIdCredentialKind: AgentToolNyxIdCredentialKind.AgentKey),
            DurableNyxIdCredential = new DurableCallerCredentialRef
            {
                Ref = secretReference.Ref,
                Purpose = secretReference.Purpose,
                OwnerScopeKey = secretReference.OwnerScopeKey,
                SubjectId = "key-alpha",
                SourceKind = DurableCallerCredentialSourceKind.ChannelRegistration,
                SecretReference = secretReference,
            },
        };
    }

    private static AgentToolExecutionContext CreateBaseContext() =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-alpha", "call-alpha"),
            Caller = new AgentToolCallerContext(
                "scope-alpha",
                "owner-alpha",
                "response-alpha",
                "scope-alpha"),
            OperationAdmission = new AgentToolOperationAdmission(
                "svc-alpha",
                "service-alpha",
                new AgentToolOperationIdentity.PublishedEndpoint("endpoint-alpha"),
                AgentToolOperationAuthorizationBasis.PublishedContract,
                "GET",
                "/resources",
                "contract-alpha",
                [],
                null,
                AgentToolOperationResponsePolicy.TextOnly,
                AgentToolOperationExecutionPolicy.Unspecified),
            ExecutionOwner = AgentToolExecutionOwners.WorkflowRun("run-alpha"),
        };

    private sealed class RecordingTool : IAgentTool
    {
        public string Name => "channel_authority_test";

        public string Description => "Exercises registration-authority admission.";

        public string ParametersSchema => "{\"type\":\"object\"}";

        public bool IsReadOnly => true;

        public int ExecuteCount { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("{\"ok\":true}");
        }
    }

    private sealed class StartingAdmissionLedger : IAgentToolAdmissionLedger
    {
        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentToolAdmissionResult(AgentToolAdmissionStatus.Started));
    }

    private sealed class RecordingAuthorityAdmissionPort : IChannelRegistrationAuthorityAdmissionPort
    {
        private readonly Func<
            ChannelRegistrationAuthorityAdmissionRequest,
            CancellationToken,
            Task<ChannelRegistrationAuthorityAdmissionResult>> _admit;

        public RecordingAuthorityAdmissionPort(ChannelRegistrationAuthorityAdmissionResult result)
            : this((_, _) => Task.FromResult(result))
        {
        }

        public RecordingAuthorityAdmissionPort(
            Func<
                ChannelRegistrationAuthorityAdmissionRequest,
                CancellationToken,
                Task<ChannelRegistrationAuthorityAdmissionResult>> admit)
        {
            _admit = admit;
        }

        public List<ChannelRegistrationAuthorityAdmissionRequest> Requests { get; } = [];

        public Task<ChannelRegistrationAuthorityAdmissionResult> AdmitAsync(
            ChannelRegistrationAuthorityAdmissionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return _admit(request, ct);
        }
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new("actor-hash", "identity-key-alpha");

        public bool Verify(
            string canonicalActorKey,
            string auditActorId,
            string identityKeyId) => true;
    }
}
