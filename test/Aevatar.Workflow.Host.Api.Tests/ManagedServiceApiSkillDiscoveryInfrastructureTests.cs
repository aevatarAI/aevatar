using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
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
    }

    [Fact]
    public async Task ManagedExecutor_ShouldUseManagedSandboxAndStrictlyDecodeStdout()
    {
        var codex = new StubCodexExecutionPort(CompletedManagedOutput());
        var executor = new ManagedCodexServiceApiSkillDiscoveryExecutor([codex]);

        var result = await executor.DiscoverAsync(
            new ManagedCodexServiceApiSkillDiscoveryRequest(Access(), Input()));

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
        codex.LastRequest.Prompt.Should().NotContain("runtime-caller-credential");
    }

    [Fact]
    public async Task ManagedExecutor_ShouldFailClosedWhenCodexReturnsMalformedOutput()
    {
        var codex = new StubCodexExecutionPort("```json\n{}\n```");
        var executor = new ManagedCodexServiceApiSkillDiscoveryExecutor([codex]);

        Func<Task> act = async () => await executor.DiscoverAsync(
            new ManagedCodexServiceApiSkillDiscoveryRequest(Access(), Input()));

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
            BoundedSearchPolicyExhausted = true,
        };

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

    private sealed class StubCodexExecutionPort(string output) : ICodexExecutionPort
    {
        public CodexExecutionRequest? LastRequest { get; private set; }

        public CodexExecutionTarget.TargetOneofCase TargetKind =>
            CodexExecutionTarget.TargetOneofCase.ManagedSandbox;

        public async IAsyncEnumerable<CodexExecutionEvent> ExecuteAsync(
            CodexExecutionRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            yield return CodexExecutionEvent.Started();
            await Task.Yield();
            yield return CodexExecutionEvent.Completed(new CodexExecutionResult(output));
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
