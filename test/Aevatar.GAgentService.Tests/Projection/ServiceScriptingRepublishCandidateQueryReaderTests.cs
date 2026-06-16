using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceScriptingRepublishCandidateQueryReaderTests
{
    [Fact]
    public async Task QueryServingByScopeScriptAsync_ShouldReturnOnlyActiveServingScriptingMatches()
    {
        var catalogStore = new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id);
        var revisionStore = new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id);
        var servingSetStore = new RecordingDocumentStore<ServiceServingSetReadModel>(x => x.Id);
        var reader = new ServiceScriptingRepublishCandidateQueryReader(catalogStore, revisionStore, servingSetStore);

        var matchingIdentity = CreateIdentity("scope-a", "svc-a");
        var inactiveIdentity = CreateIdentity("scope-a", "svc-b");
        var otherScriptIdentity = CreateIdentity("scope-a", "svc-c");
        await catalogStore.UpsertAsync(new ServiceCatalogReadModel
        {
            Id = ServiceKeys.Build(matchingIdentity),
            TenantId = "scope-a",
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = "svc-a",
            DefaultServingRevisionId = "rev-live",
        });
        await catalogStore.UpsertAsync(new ServiceCatalogReadModel
        {
            Id = ServiceKeys.Build(inactiveIdentity),
            TenantId = "scope-a",
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = "svc-b",
            DefaultServingRevisionId = "rev-inactive",
        });
        await catalogStore.UpsertAsync(new ServiceCatalogReadModel
        {
            Id = ServiceKeys.Build(otherScriptIdentity),
            TenantId = "scope-a",
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = "svc-c",
            DefaultServingRevisionId = "rev-other-script",
        });

        await servingSetStore.UpsertAsync(new ServiceServingSetReadModel
        {
            Id = ServiceKeys.Build(matchingIdentity),
            Targets =
            {
                new ServiceServingTargetReadModel
                {
                    DeploymentId = "dep-live",
                    RevisionId = "rev-live",
                    PrimaryActorId = "actor-live",
                    AllocationWeight = 100,
                    ServingState = ServiceServingState.Active.ToString(),
                },
            },
        });
        await servingSetStore.UpsertAsync(new ServiceServingSetReadModel
        {
            Id = ServiceKeys.Build(inactiveIdentity),
            Targets =
            {
                new ServiceServingTargetReadModel
                {
                    DeploymentId = "dep-inactive",
                    RevisionId = "rev-inactive",
                    PrimaryActorId = "actor-inactive",
                    AllocationWeight = 0,
                    ServingState = ServiceServingState.Active.ToString(),
                },
            },
        });
        await servingSetStore.UpsertAsync(new ServiceServingSetReadModel
        {
            Id = ServiceKeys.Build(otherScriptIdentity),
            Targets =
            {
                new ServiceServingTargetReadModel
                {
                    DeploymentId = "dep-other",
                    RevisionId = "rev-other-script",
                    PrimaryActorId = "actor-other",
                    AllocationWeight = 100,
                    ServingState = ServiceServingState.Active.ToString(),
                },
            },
        });

        await revisionStore.UpsertAsync(new ServiceRevisionCatalogReadModel
        {
            Id = ServiceKeys.Build(matchingIdentity),
            Revisions =
            {
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "rev-live",
                    ScriptingScriptId = "script-a",
                    ScriptingRevision = "script-rev-2",
                    ScriptingDefinitionActorId = "script-def-2",
                    ScriptingSourceHash = "hash-2",
                    PreparedArtifact = new PreparedServiceRevisionArtifact
                    {
                        RevisionId = "rev-live",
                    },
                },
            },
        });
        await revisionStore.UpsertAsync(new ServiceRevisionCatalogReadModel
        {
            Id = ServiceKeys.Build(inactiveIdentity),
            Revisions =
            {
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "rev-inactive",
                    ScriptingScriptId = "script-a",
                    ScriptingRevision = "script-rev-1",
                    ScriptingDefinitionActorId = "script-def-1",
                    ScriptingSourceHash = "hash-1",
                },
            },
        });
        await revisionStore.UpsertAsync(new ServiceRevisionCatalogReadModel
        {
            Id = ServiceKeys.Build(otherScriptIdentity),
            Revisions =
            {
                new ServiceRevisionEntryReadModel
                {
                    RevisionId = "rev-other-script",
                    ScriptingScriptId = "script-b",
                    ScriptingRevision = "script-rev-9",
                    ScriptingDefinitionActorId = "script-def-9",
                    ScriptingSourceHash = "hash-9",
                },
            },
        });

        var candidates = await reader.QueryServingByScopeScriptAsync("scope-a", "script-a");

        candidates.Should().ContainSingle();
        candidates[0].Identity.Should().BeEquivalentTo(matchingIdentity);
        candidates[0].CurrentServingRevisionId.Should().Be("rev-live");
        candidates[0].CurrentServingDeploymentId.Should().Be("dep-live");
        candidates[0].Scripting.Should().BeEquivalentTo(
            new ServiceRevisionScriptingSnapshot("script-a", "script-rev-2", "script-def-2", "hash-2"));
        candidates[0].PreparedArtifact.Should().NotBeNull();
    }

    [Fact]
    public async Task QueryServingByScopeScriptAsync_ShouldReturnEmpty_WhenProjectionDisabled()
    {
        var reader = new ServiceScriptingRepublishCandidateQueryReader(
            new RecordingDocumentStore<ServiceCatalogReadModel>(x => x.Id),
            new RecordingDocumentStore<ServiceRevisionCatalogReadModel>(x => x.Id),
            new RecordingDocumentStore<ServiceServingSetReadModel>(x => x.Id),
            new Aevatar.GAgentService.Projection.Configuration.ServiceProjectionOptions
            {
                Enabled = false,
            });

        var candidates = await reader.QueryServingByScopeScriptAsync("scope-a", "script-a");

        candidates.Should().BeEmpty();
    }

    private static ServiceIdentity CreateIdentity(string scopeId, string serviceId) =>
        new()
        {
            TenantId = scopeId,
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = serviceId,
        };
}
