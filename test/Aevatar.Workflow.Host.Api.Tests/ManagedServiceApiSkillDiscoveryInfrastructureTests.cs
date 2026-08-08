using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.ExternalCapabilities;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.ExternalCapabilities;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ManagedServiceApiSkillDiscoveryInfrastructureTests
{
    private const string TargetUserServiceId = "usvc-alpha";
    private const string OrnnApiUserServiceId = "ornn-usvc-alpha";
    private const string SkillGuid = "d47a95c5-db2a-4f00-9057-27f674566bd5";
    private const string SkillHash = "75f0e0480c4cbeed68ba97ffe0b26a0c0cc0ec2d8d0bed631306b383eec0f486";
    private const string PublisherId = "9f42ce90-8b05-406d-8461-acb5fdfa4fab";
    private const string CapabilityFingerprint =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void AddManagedServiceApiWorkflowCapabilityDiscovery_ShouldRegisterNarrowDiscoveryPortExplicitly()
    {
        var services = new ServiceCollection();

        services.AddManagedServiceApiWorkflowCapabilityDiscovery();

        services.Should().Contain(static descriptor =>
            descriptor.ServiceType == typeof(IManagedCodexServiceApiSkillDiscoveryExecutor) &&
            descriptor.ImplementationType == typeof(ManagedCodexServiceApiSkillDiscoveryExecutor));
        services.Should().Contain(static descriptor =>
            descriptor.ServiceType == typeof(IExactServiceApiSkillVerifier) &&
            descriptor.ImplementationType == typeof(ManagedCodexExactOrnnApiSkillVerifier));
        services.Should().Contain(static descriptor =>
            descriptor.ServiceType == typeof(IServiceApiSkillCataloguePort) &&
            descriptor.ImplementationType == typeof(OrnnServiceApiSkillCataloguePort));
        services.Should().Contain(static descriptor =>
            descriptor.ServiceType == typeof(IManagedCodexServiceApiSkillDiscoveryPort) &&
            descriptor.ImplementationType == typeof(ManagedServiceApiSkillDiscoveryService));
    }

    [Fact]
    public async Task CataloguePort_ShouldMapAuthoritativeOrnnPageThroughSourceBearer()
    {
        var handler = new CatalogueApiHandler("""
            {
              "data": {
                "total": 2,
                "totalPages": 2,
                "page": 2,
                "pageSize": 100,
                "items": [
                  {
                    "guid": "skill-beta",
                    "name": "beta-service-api",
                    "description": "Beta Service API skill"
                  }
                ]
              }
            }
            """);
        var port = CreateCataloguePort(handler);

        var result = await port.ReadPageAsync(new ServiceApiSkillCataloguePageRequest(
            Access(),
            "send a message",
            2,
            100));

        result.Page.Should().Be(2);
        result.PageSize.Should().Be(100);
        result.Total.Should().Be(2);
        result.TotalPages.Should().Be(2);
        result.Candidates.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ServiceApiSkillCatalogueCandidate
            {
                Guid = "skill-beta",
                CanonicalName = "beta-service-api",
                Description = "Beta Service API skill",
            });
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Headers.Authorization.Should().NotBeNull();
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().Be("runtime-caller-credential");
        request.RequestUri!.AbsoluteUri.Should().Be(
            "https://nyx.example/api/v1/proxy/s/ornn-api/api/v1/skill-search?query=send%20a%20message&mode=keyword&scope=mixed&page=2&pageSize=100");
    }

    [Fact]
    public async Task CataloguePort_ShouldRejectMissingSourceReadableBearer()
    {
        var handler = new CatalogueApiHandler("""{ "data": { "items": [] } }""");
        var port = CreateCataloguePort(handler);

        Func<Task> act = async () => await port.ReadPageAsync(new ServiceApiSkillCataloguePageRequest(
            new ExternalWorkflowCapabilityAccessContext("scope-alpha", "caller-alpha"),
            "send a message",
            1,
            100));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*source-readable NyxID caller credential*");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CataloguePort_ShouldFailClosedWhenOrnnSearchReturnsError()
    {
        var handler = new CatalogueApiHandler(
            """{ "error": "upstream failed" }""",
            HttpStatusCode.InternalServerError);
        var port = CreateCataloguePort(handler);

        Func<Task> act = async () => await port.ReadPageAsync(new ServiceApiSkillCataloguePageRequest(
            Access(),
            "send a message",
            1,
            100));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Ornn skill catalogue discovery failed.");
    }

    [Fact]
    public async Task ManagedExecutor_ShouldUseManagedSandboxAndStrictlyDecodeStdout()
    {
        var rankingInput = RankingInput();
        rankingInput.DiscoveryInput.CallerId = " ";
        rankingInput.DiscoveryInput.DescriptorInventory.AddRange(PromptDescriptors());
        rankingInput.ExcludedCandidates.Add(Candidate());
        var codex = new StubCodexExecutionPort(
            CodexExecutionEvent.Started(),
            CodexExecutionEvent.Output("ignored streaming output"),
            CodexExecutionEvent.Completed(new CodexExecutionResult(CompletedManagedOutput())));
        var executor = new ManagedCodexServiceApiSkillDiscoveryExecutor([codex]);

        var result = await executor.DiscoverAsync(
            new ManagedCodexServiceApiSkillRankingRequest(Access(), rankingInput));

        result.ResultCase.Should().Be(ManagedCodexServiceApiSkillDiscoveryResult.ResultOneofCase.ReliableSkill);
        result.ReliableSkill.Guid.Should().Be(SkillGuid);
        codex.LastRequest.Should().NotBeNull();
        codex.LastRequest!.Target.TargetCase.Should().Be(CodexExecutionTarget.TargetOneofCase.ManagedSandbox);
        codex.LastRequest.Workspace!.WorkspaceCase.Should().Be(CodexExecutionWorkspace.WorkspaceOneofCase.EmptyGit);
        codex.LastRequest.Caller.NyxIdAccessToken.Should().BeNull();
        codex.LastRequest.Caller.NyxIdAuthority.Should().Be(new CodexExecutionNyxIdAuthority(
            OwnerScope.NyxIdPlatform,
            string.Empty,
            "caller-alpha"));
        codex.LastRequest.Prompt.Should().Contain("service_api_skill_discovery.v1");
        codex.LastRequest.Prompt.Should().Contain("ornn-api");
        codex.LastRequest.Prompt.Should().Contain("NyxIdOperation");
        codex.LastRequest.Prompt.Should().Contain("NyxIdRequest");
        codex.LastRequest.Prompt.Should().Contain("excluded_candidates");
        codex.LastRequest.Prompt.Should().NotContain("runtime-caller-credential");

        var typedInputMarker = "Typed input:" + Environment.NewLine;
        var typedInputStart = codex.LastRequest.Prompt.IndexOf(typedInputMarker, StringComparison.Ordinal);
        typedInputStart.Should().BeGreaterThanOrEqualTo(0);
        var typedInput = JsonNode.Parse(
            codex.LastRequest.Prompt[(typedInputStart + typedInputMarker.Length)..])!.AsObject();
        var descriptors = typedInput["descriptor_inventory"]!.AsArray();
        descriptors.Should().HaveCount(3);
        descriptors[0]!["nyx_id_operation"]!["endpoint_id"]!.GetValue<string>()
            .Should().Be("send-message");
        descriptors[1]!["nyx_id_request"]!["path_template"]!.GetValue<string>()
            .Should().Be("/v1/messages/{message_id}");
        descriptors[1]!["nyx_id_request"]!["query_parameters"]!.AsArray()
            .Select(static item => item!.GetValue<string>())
            .Should().Equal("hard");
        var excluded = typedInput["excluded_candidates"]!.AsArray();
        excluded.Should().ContainSingle();
        excluded[0]!["guid"]!.GetValue<string>().Should().Be(SkillGuid);
    }

    [Fact]
    public void ManagedExecutor_ShouldRequireExactlyOneManagedSandboxPort()
    {
        var nonManaged = new StubCodexExecutionPort(
            CodexExecutionTarget.TargetOneofCase.PrivateSsh,
            CodexExecutionEvent.Started());

        var noManaged = () => new ManagedCodexServiceApiSkillDiscoveryExecutor([nonManaged]);
        var multipleManaged = () => new ManagedCodexServiceApiSkillDiscoveryExecutor(
            [new StubCodexExecutionPort(CompletedManagedOutput()), new StubCodexExecutionPort(CompletedManagedOutput())]);

        noManaged.Should().Throw<InvalidOperationException>()
            .WithMessage("Exactly one managed Codex execution port*");
        multipleManaged.Should().Throw<InvalidOperationException>()
            .WithMessage("Exactly one managed Codex execution port*");
    }

    [Fact]
    public async Task ManagedExecutor_ShouldRejectMissingCompletion()
    {
        var executor = new ManagedCodexServiceApiSkillDiscoveryExecutor(
            [new StubCodexExecutionPort(CodexExecutionEvent.Started())]);

        Func<Task> act = async () => await executor.DiscoverAsync(
            new ManagedCodexServiceApiSkillRankingRequest(Access(), RankingInput()));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*did not return a completion result*");
    }

    [Fact]
    public async Task ManagedExecutor_ShouldRejectNullOrDuplicateCompletion()
    {
        var nullCompletion = new ManagedCodexServiceApiSkillDiscoveryExecutor(
            [new StubCodexExecutionPort(new CodexExecutionEvent(CodexExecutionEventKind.Completed))]);
        var duplicateCompletion = new ManagedCodexServiceApiSkillDiscoveryExecutor(
            [new StubCodexExecutionPort(
                CodexExecutionEvent.Completed(new CodexExecutionResult(CompletedManagedOutput())),
                CodexExecutionEvent.Completed(new CodexExecutionResult(CompletedManagedOutput())))]);

        Func<Task> nullAct = async () => await nullCompletion.DiscoverAsync(
            new ManagedCodexServiceApiSkillRankingRequest(Access(), RankingInput()));
        Func<Task> duplicateAct = async () => await duplicateCompletion.DiscoverAsync(
            new ManagedCodexServiceApiSkillRankingRequest(Access(), RankingInput()));

        await nullAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid terminal stream*");
        await duplicateAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid terminal stream*");
    }

    [Fact]
    public async Task ManagedExecutor_ShouldPropagateTypedManagedFailure()
    {
        var failure = new CodexExecutionFailure(
            CodexExecutionFailureKind.CapacityUnavailable,
            "managed-capacity-unavailable",
            "Managed capacity is unavailable.");
        var executor = new ManagedCodexServiceApiSkillDiscoveryExecutor(
            [new StubCodexExecutionPort(CodexExecutionEvent.Failed(failure))]);

        Func<Task> act = async () => await executor.DiscoverAsync(
            new ManagedCodexServiceApiSkillRankingRequest(Access(), RankingInput()));

        var exception = await act.Should().ThrowAsync<CodexExecutionException>();
        exception.Which.Failure.Should().Be(failure);
    }

    [Fact]
    public async Task ManagedExecutor_ShouldUseStableFailureWhenTerminalFailureOmitsDetails()
    {
        var executor = new ManagedCodexServiceApiSkillDiscoveryExecutor(
            [new StubCodexExecutionPort(new CodexExecutionEvent(CodexExecutionEventKind.Failed))]);

        Func<Task> act = async () => await executor.DiscoverAsync(
            new ManagedCodexServiceApiSkillRankingRequest(Access(), RankingInput()));

        var exception = await act.Should().ThrowAsync<CodexExecutionException>();
        exception.Which.Failure.Code.Should().Be("managed_service_api_skill_discovery_failed");
    }

    [Fact]
    public async Task ManagedExecutor_ShouldRejectUnsupportedStreamEvent()
    {
        var executor = new ManagedCodexServiceApiSkillDiscoveryExecutor(
            [new StubCodexExecutionPort(new CodexExecutionEvent(CodexExecutionEventKind.Unspecified))]);

        Func<Task> act = async () => await executor.DiscoverAsync(
            new ManagedCodexServiceApiSkillRankingRequest(Access(), RankingInput()));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unsupported event*");
    }

    [Fact]
    public async Task ManagedExecutor_ShouldRejectMissingNativeCallerIdentity()
    {
        var input = RankingInput();
        input.DiscoveryInput.CallerId = " ";
        var executor = new ManagedCodexServiceApiSkillDiscoveryExecutor(
            [new StubCodexExecutionPort(CompletedManagedOutput())]);

        Func<Task> act = async () => await executor.DiscoverAsync(
            new ManagedCodexServiceApiSkillRankingRequest(
                new ExternalWorkflowCapabilityAccessContext("scope-alpha", " "),
                input));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*native NyxID caller identity*");
    }

    [Fact]
    public async Task ManagedExecutor_ShouldFailClosedWhenCodexReturnsMalformedOutput()
    {
        var codex = new StubCodexExecutionPort("```json\n{}\n```");
        var executor = new ManagedCodexServiceApiSkillDiscoveryExecutor([codex]);

        Func<Task> act = async () => await executor.DiscoverAsync(
            new ManagedCodexServiceApiSkillRankingRequest(Access(), RankingInput()));

        await act.Should().ThrowAsync<ManagedCodexServiceApiSkillDiscoveryOutputException>()
            .WithMessage("*exactly one JSON object*");
    }

    [Fact]
    public async Task ExactVerifier_ShouldUseManagedApiKeyAndExactOrnnUserService()
    {
        var handler = new OrnnApiHandler();
        var query = await CredentialQueryAsync();
        var verifier = new ManagedCodexExactOrnnApiSkillVerifier(
            query,
            query.Vault,
            new TestNyxIdApiClientFactory(handler));

        var result = await verifier.VerifyAsync(
            new ExactServiceApiSkillVerificationRequest(
                Access(),
                Input(),
                Candidate()));

        result.IsVerified.Should().BeTrue();
        result.Provenance!.Guid.Should().Be(SkillGuid);
        result.Provenance.SkillHash.Should().Be(SkillHash);
        handler.Requests.Should().HaveCount(2);
        handler.Requests.Select(request => request.RequestUri!.AbsoluteUri).Should().Equal(
            $"https://nyx.example/api/v1/proxy/s/ornn-api/api/v1/skills/{SkillGuid}?_nyxid_via={OrnnApiUserServiceId}&version=1.1",
            $"https://nyx.example/api/v1/proxy/s/ornn-api/api/v1/skills/{SkillGuid}/json?_nyxid_via={OrnnApiUserServiceId}&version=1.1");
        handler.Requests.Should().AllSatisfy(request =>
        {
            request.Headers.TryGetValues("X-API-Key", out var values).Should().BeTrue();
            values!.Single().Should().Be("managed-api-key");
            request.Headers.Authorization.Should().BeNull();
        });
    }

    [Fact]
    public async Task ExactVerifier_ShouldRejectIntegrityMismatchWithoutWebFallbackResult()
    {
        var handler = new OrnnApiHandler(detailHash: new string('b', 64));
        var query = await CredentialQueryAsync();
        var verifier = new ManagedCodexExactOrnnApiSkillVerifier(
            query,
            query.Vault,
            new TestNyxIdApiClientFactory(handler));

        var result = await verifier.VerifyAsync(
            new ExactServiceApiSkillVerificationRequest(
                Access(),
                Input(),
                Candidate()));

        result.IsVerified.Should().BeFalse();
        result.Rejection!.Reason.Should().Be(ServiceApiNoReliableSkillReason.SkillIntegrityMismatch);
    }

    [Fact]
    public async Task ExactVerifier_ShouldRejectMissingEvidenceWithoutTrustingManagedOutput()
    {
        var handler = new OrnnApiHandler(skillMarkdown: "# Example Messaging\n\noperation_id: other-operation");
        var query = await CredentialQueryAsync();
        var verifier = new ManagedCodexExactOrnnApiSkillVerifier(
            query,
            query.Vault,
            new TestNyxIdApiClientFactory(handler));

        var result = await verifier.VerifyAsync(
            new ExactServiceApiSkillVerificationRequest(
                Access(),
                Input(),
                Candidate()));

        result.IsVerified.Should().BeFalse();
        result.Rejection!.Reason.Should().Be(ServiceApiNoReliableSkillReason.SkillIntegrityMismatch);
    }

    [Fact]
    public async Task ExactVerifier_ShouldRejectOperationOutsideDeclaredEvidenceSection()
    {
        var handler = new OrnnApiHandler(
            skillMarkdown:
            "# Example Messaging\n\n## Send a message\noperation_id: other-operation\n\n## Delete a message\noperation_id: send-message");
        var query = await CredentialQueryAsync();
        var verifier = new ManagedCodexExactOrnnApiSkillVerifier(
            query,
            query.Vault,
            new TestNyxIdApiClientFactory(handler));

        var result = await verifier.VerifyAsync(
            new ExactServiceApiSkillVerificationRequest(
                Access(),
                Input(),
                Candidate()));

        result.IsVerified.Should().BeFalse();
        result.Rejection!.Reason.Should().Be(ServiceApiNoReliableSkillReason.SkillIntegrityMismatch);
    }

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new(
            "scope-alpha",
            "caller-alpha",
            NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                "runtime-caller-credential"));

    private static ServiceApiSkillDiscoveryInput Input() =>
        new()
        {
            CallerAuthority = new ExternalCapabilityAuthorizationOwner
            {
                Authority = OwnerScope.NyxIdPlatform,
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
        };

    private static ManagedCodexServiceApiSkillRankingInput RankingInput()
    {
        var input = new ManagedCodexServiceApiSkillRankingInput
        {
            DiscoveryInput = Input(),
        };
        input.CatalogueCandidates.Add(new ServiceApiSkillCatalogueCandidate
        {
            Guid = SkillGuid,
            CanonicalName = "example-messaging-service-api",
            Description = "Example messaging Service API skill.",
        });
        return input;
    }

    private static IEnumerable<ExternalWorkflowCapabilityDescriptor> PromptDescriptors()
    {
        yield return new ExternalWorkflowCapabilityDescriptor
        {
            DisplayName = "Send by operation",
            ReadOnly = false,
            Selector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdOperation = new NyxIdOperationSelector
                {
                    UserServiceId = TargetUserServiceId,
                    EndpointId = "send-message",
                },
            },
        };
        yield return new ExternalWorkflowCapabilityDescriptor
        {
            DisplayName = "Send by request",
            Destructive = true,
            Selector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdRequest = new NyxIdRequestSelector
                {
                    UserServiceId = TargetUserServiceId,
                    Method = NyxIdRequestMethod.Delete,
                    PathTemplate = "/v1/messages/{message_id}",
                    QueryParameters = { "hard" },
                    HeaderParameters = { "If-Match" },
                    BodyMode = NyxIdRequestBodyMode.None,
                    ResponseMode = NyxIdRequestResponseMode.Text,
                    Risk = NyxIdOperationRisk.Destructive,
                },
            },
        };
        yield return new ExternalWorkflowCapabilityDescriptor
        {
            DisplayName = "Unselected descriptor",
        };
    }

    private static ReliableServiceApiSkillCandidate Candidate()
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
                Selector = new NyxIdRequestSelector
                {
                    UserServiceId = TargetUserServiceId,
                    Method = NyxIdRequestMethod.Post,
                    PathTemplate = "/v1/messages",
                    HeaderParameters = { "Accept" },
                    BodyMode = NyxIdRequestBodyMode.Json,
                    BodyRequired = true,
                    ResponseMode = NyxIdRequestResponseMode.Text,
                    Risk = NyxIdOperationRisk.Write,
                },
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

    private static string CompletedManagedOutput() =>
        $$"""
        {
          "schema_version": "service_api_skill_discovery.v1",
          "target_user_service_id": "{{TargetUserServiceId}}",
          "capability_fingerprint": "{{CapabilityFingerprint}}",
          "outcome": "reliable_skill",
          "reliable_skill": {
            "canonical_name": "example-messaging-service-api",
            "guid": "{{SkillGuid}}",
            "literal_version": "1.1",
            "skill_hash": "{{SkillHash}}",
            "publisher_id": "{{PublisherId}}",
            "request_shape": {
              "method": "POST",
              "path_template": "/v1/messages",
              "query_parameters": [],
              "header_parameters": ["Accept"],
              "body_mode": "json",
              "body_required": true,
              "response_mode": "text",
              "risk": "WRITE"
            },
            "evidence": [
              {
                "skill_file_path": "SKILL.md",
                "section": "Send a message",
                "operation_id": "send-message"
              }
            ]
          }
        }
        """;

    private static async Task<StubManagedCodexCredentialQueryPort> CredentialQueryAsync()
    {
        var owner = new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = string.Empty,
            ExternalUserId = "caller-alpha",
        };
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);
        var vault = new InMemorySecretVault();
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
            actorId,
            ManagedCodexCredentialActorIdentity.SecretSubjectId,
            "managed-api-key",
            "test"));
        return new StubManagedCodexCredentialQueryPort(vault, new ManagedCodexCredentialSnapshot
        {
            Credential = new ManagedCodexCredentialDescriptor
            {
                Owner = owner,
                ApiKeyId = "api-key-alpha",
                SecretReference = stored.Reference,
                ChronoSandboxUserServiceId = "chrono-sandbox-usvc",
                ChronoSandboxServiceSlug = "chrono-sandbox",
                ChronoLlmUserServiceId = "chrono-llm-usvc",
                OrnnApiUserServiceId = OrnnApiUserServiceId,
                OrnnApiServiceSlug = "ornn-api",
                ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddHours(1)),
                Status = ManagedCodexCredentialStatus.Active,
            },
            StateVersion = 7,
            ReadinessEvidence = ManagedCodexCredentialReadinessEvidence.RemoteValidated,
        });
    }

    private sealed class StubCodexExecutionPort : ICodexExecutionPort
    {
        private readonly IReadOnlyList<CodexExecutionEvent> _events;

        public StubCodexExecutionPort(string output)
            : this(
                CodexExecutionTarget.TargetOneofCase.ManagedSandbox,
                CodexExecutionEvent.Started(),
                CodexExecutionEvent.Completed(new CodexExecutionResult(output)))
        {
        }

        public StubCodexExecutionPort(params CodexExecutionEvent[] events)
            : this(CodexExecutionTarget.TargetOneofCase.ManagedSandbox, events)
        {
        }

        public StubCodexExecutionPort(
            CodexExecutionTarget.TargetOneofCase targetKind,
            params CodexExecutionEvent[] events)
        {
            TargetKind = targetKind;
            _events = events;
        }

        public CodexExecutionRequest? LastRequest { get; private set; }

        public CodexExecutionTarget.TargetOneofCase TargetKind { get; }

        public async IAsyncEnumerable<CodexExecutionEvent> ExecuteAsync(
            CodexExecutionRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            foreach (var item in _events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }
    }

    private sealed class StubManagedCodexCredentialQueryPort(
        InMemorySecretVault vault,
        ManagedCodexCredentialSnapshot snapshot) : IManagedCodexCredentialQueryPort
    {
        public InMemorySecretVault Vault { get; } = vault;

        public Task<ManagedCodexCredentialSnapshot?> ResolveAsync(
            ExternalSubjectRef owner,
            CancellationToken ct = default) =>
            Task.FromResult<ManagedCodexCredentialSnapshot?>(snapshot.Clone());
    }

    private sealed class TestNyxIdApiClientFactory(OrnnApiHandler handler) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() =>
            new(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
                new HttpClient(handler),
                null);
    }

    private sealed class OrnnApiHandler(
        string detailHash = SkillHash,
        string skillMarkdown = "# Example Messaging\n\n## Send a message\noperation_id: send-message")
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));
            var path = request.RequestUri!.AbsolutePath;
            var content = path.EndsWith("/json", StringComparison.Ordinal)
                ? SkillJson(skillMarkdown)
                : DetailJson(detailHash);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return clone;
        }
    }

    private sealed class CatalogueApiHandler(
        string content,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return clone;
        }
    }

    private static OrnnServiceApiSkillCataloguePort CreateCataloguePort(CatalogueApiHandler handler)
    {
        var nyxIdClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler),
            null);
        return new OrnnServiceApiSkillCataloguePort(
            new OrnnSkillClient(
                new OrnnOptions { NyxIdSlug = "ornn-api" },
                nyxIdClient));
    }

    private static string DetailJson(string hash) =>
        "{\"data\":{\"guid\":\"" + SkillGuid +
        "\",\"name\":\"example-messaging-service-api\",\"skillHash\":\"" + hash +
        "\",\"createdBy\":\"" + PublisherId + "\"}}";

    private static string SkillJson(string skillMarkdown) =>
        "{\"data\":{\"name\":\"example-messaging-service-api\",\"version\":\"1.1\",\"files\":{\"SKILL.md\":" +
        JsonEscaped(skillMarkdown) + "}}}";

    private static string JsonEscaped(string value) =>
        "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal) + "\"";
}
