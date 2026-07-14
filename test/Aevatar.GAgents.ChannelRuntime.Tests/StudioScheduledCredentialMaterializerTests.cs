using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Scheduled;
using Aevatar.Studio.Application.Authorization;
using Aevatar.Studio.Application.Provisioning;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class StudioScheduledCredentialMaterializerTests
{
    [Fact]
    public async Task MaterializeAsync_ShouldForwardAuthorizationFactsAndReturnDurableCredential()
    {
        var lifecycle = new RecordingLifecycle
        {
            ProvisionResult = new ScheduledAgentCredentialProvisionResult(
                ScheduledAgentApiKeyIssueResult.Succeeded(
                    "api-key-alpha",
                    "secret-value",
                    DateTimeOffset.Parse("2026-08-01T00:00:00Z").ToUnixTimeMilliseconds()),
                Secret("secret-ref-alpha", "schedule:schedule-alpha")),
        };
        var plan = Plan();
        var ownerScope = OwnerScope();

        var credential = await new StudioScheduledCredentialMaterializer(lifecycle).MaterializeAsync(
            "bearer-alpha", plan, "schedule-alpha", ownerScope);

        credential.ApiKeyId.Should().Be("api-key-alpha");
        credential.SecretReference.Ref.Should().Be("secret-ref-alpha");
        credential.ExpiresAtUtc.Should().Be(DateTimeOffset.Parse("2026-08-01T00:00:00Z"));
        var request = lifecycle.Provisions.Should().ContainSingle().Subject;
        request.Token.Should().Be("bearer-alpha");
        request.Plan.Should().BeSameAs(plan);
        request.Plan.NyxIdServiceGrants.Single().UserServiceId.Should().Be("service-alpha");
        request.Plan.NyxIdServiceGrants.Single().NodeGrants.Single().NodeId.Should().Be("node-alpha");
        request.CredentialName.Should().Be("studio-schedule-schedule-alpha");
        request.AgentId.Should().Be("schedule-alpha");
        request.OwnerScope.Should().BeSameAs(ownerScope);
        request.OwnerScopeKey.Should().Be("schedule:schedule-alpha");
    }

    [Fact]
    public async Task MaterializeAsync_WhenLifecycleFails_ShouldPropagateFailure()
    {
        var lifecycle = new RecordingLifecycle
        {
            ProvisionResult = new ScheduledAgentCredentialProvisionResult(
                ScheduledAgentApiKeyIssueResult.Failed("issuer-rejected"), null),
        };

        var action = () => new StudioScheduledCredentialMaterializer(lifecycle).MaterializeAsync(
            "bearer-alpha", Plan(), "schedule-alpha", OwnerScope());

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("issuer-rejected");
    }

    [Fact]
    public async Task RevokeAsync_ShouldForwardDistinctScheduleOwnerKeyAndSecretReference()
    {
        var lifecycle = new RecordingLifecycle();
        var ownerScope = OwnerScope();
        var secret = Secret("secret-ref-alpha", "schedule:schedule-alpha");
        var credential = new StudioScheduledCredential(
            "api-key-alpha", secret, DateTimeOffset.Parse("2026-08-01T00:00:00Z"));

        await new StudioScheduledCredentialMaterializer(lifecycle).RevokeAsync(
            "bearer-alpha", "schedule-alpha", ownerScope, credential);

        lifecycle.Revocations.Should().ContainSingle().Which.Should().BeEquivalentTo(
            ("bearer-alpha", "schedule-alpha", "api-key-alpha", ownerScope, secret));
    }

    private static ScheduledInvocationAuthorizationPlan Plan()
    {
        var plan = new ScheduledInvocationAuthorizationPlan { PermissionDigest = "digest-alpha" };
        var grant = new NyxIdServiceGrant { UserServiceId = "service-alpha" };
        grant.NodeGrants.Add(new NyxIdNodeGrant { NodeId = "node-alpha", Primary = true });
        plan.NyxIdServiceGrants.Add(grant);
        return plan;
    }

    private static OwnerScope OwnerScope() =>
        Aevatar.Foundation.Abstractions.OwnerScope.ForNyxIdNative("nyx-owner-alpha");

    private static SecretReference Secret(string reference, string ownerScopeKey) => new()
    {
        Ref = reference,
        Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
        OwnerScopeKey = ownerScopeKey,
        ExpiresAtUnixMs = DateTimeOffset.Parse("2026-08-01T00:00:00Z").ToUnixTimeMilliseconds(),
    };

    private sealed class RecordingLifecycle : IScheduledAgentCredentialLifecycle
    {
        public ScheduledAgentCredentialProvisionResult ProvisionResult { get; init; } =
            new(ScheduledAgentApiKeyIssueResult.Failed("not-configured"), null);
        public List<ProvisionRequest> Provisions { get; } = [];
        public List<(string Token, string AgentId, string ApiKeyId, OwnerScope OwnerScope, SecretReference Reference)> Revocations { get; } = [];

        public Task<ScheduledAgentCredentialProvisionResult> ProvisionAsync(
            string token,
            ScheduledInvocationAuthorizationPlan plan,
            string credentialName,
            string agentId,
            OwnerScope ownerScope,
            string purpose,
            string ownerScopeKey,
            string auditReason,
            CancellationToken ct = default)
        {
            Provisions.Add(new ProvisionRequest(token, plan, credentialName, agentId, ownerScope, purpose, ownerScopeKey, auditReason));
            return Task.FromResult(ProvisionResult);
        }

        public Task RequestRevocationAsync(
            string token,
            string agentId,
            string apiKeyId,
            OwnerScope ownerScope,
            SecretReference reference,
            CancellationToken ct = default)
        {
            Revocations.Add((token, agentId, apiKeyId, ownerScope, reference));
            return Task.CompletedTask;
        }
    }

    private sealed record ProvisionRequest(
        string Token,
        ScheduledInvocationAuthorizationPlan Plan,
        string CredentialName,
        string AgentId,
        OwnerScope OwnerScope,
        string Purpose,
        string OwnerScopeKey,
        string AuditReason);
}
