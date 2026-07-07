using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Governance.Projection.Orchestration;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application.Queries;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Application;
using Aevatar.Workflow.Extensions.Hosting;
using Google.Protobuf;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Integration.Tests;

public sealed class ScriptingServiceRevisionRepublishIntegrationTests
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ObservationPollInterval = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task ScopeScriptUpsertPromote_ShouldRepublishBoundServiceToNewRevision()
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddWorkflowProjectionReadModelProviders(configuration);
        // Scripting is opt-in: the host composes the scripting capability first, and
        // AddGAgentServiceCapability bridges to it only when present.
        services.AddScriptCapability(configuration);
        services.AddGAgentServiceCapability(configuration);
        services.Replace(ServiceDescriptor.Singleton<IScriptDefinitionSnapshotPort, InMemoryHarnessScriptDefinitionSnapshotPort>());
        services.Replace(ServiceDescriptor.Singleton<IServiceCommandPort, InMemoryHarnessServiceCommandPort>());

        await using var provider = services.BuildServiceProvider();
        var scopeScriptPort = provider.GetRequiredService<IScopeScriptCommandPort>();
        var scopeScriptObservationPort = provider.GetRequiredService<IScopeScriptSaveObservationPort>();
        var scopeBindingPort = provider.GetRequiredService<IScopeBindingCommandPort>();
        var lifecycleQueryPort = provider.GetRequiredService<IServiceLifecycleQueryPort>();
        var invocationPort = provider.GetRequiredService<IServiceInvocationPort>();
        var servingQueryPort = provider.GetRequiredService<IServiceServingQueryPort>();
        var definitionSnapshotReader = provider.GetRequiredService<IProjectionDocumentReader<ScriptDefinitionSnapshotDocument, string>>();
        var serviceRevisionReader = provider.GetRequiredService<IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string>>();

        var scopeId = $"scope-{Guid.NewGuid():N}";
        var scriptId = $"script-{Guid.NewGuid():N}";
        var serviceId = $"svc-{Guid.NewGuid():N}";

        var v1Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
            "RepublishScriptV1",
            "REPUBLISH-V1",
            "republish_normalization",
            "1");
        var v2Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
            "RepublishScriptV2",
            "REPUBLISH-V2",
            "republish_normalization",
            "2");

        var initialUpsert = await scopeScriptPort.UpsertAsync(
            new ScopeScriptUpsertRequest(
                scopeId,
                scriptId,
                ScriptPackageSpecExtensions.CreateSingleSource(v1Source),
                RevisionId: "rev-1"),
            CancellationToken.None);
        await WaitForScopeScriptAppliedAsync(
            scopeScriptObservationPort,
            scopeId,
            scriptId,
            initialUpsert,
            CancellationToken.None);
        await WaitForDefinitionSnapshotProjectionAsync(
            definitionSnapshotReader,
            initialUpsert.DefinitionActorId,
            "rev-1",
            CancellationToken.None);

        var binding = await scopeBindingPort.UpsertAsync(
            new ScopeBindingUpsertRequest(
                scopeId,
                ScopeBindingImplementationKind.Scripting,
                Script: new ScopeBindingScriptSpec(scriptId),
                ServiceId: serviceId,
                RevisionId: "svc-rev-1"),
            CancellationToken.None);

        var identity = BuildIdentity(scopeId, serviceId);
        var initialState = await WaitForServiceRevisionAsync(
            lifecycleQueryPort,
            servingQueryPort,
            serviceRevisionReader,
            identity,
            expectedScriptRevision: "rev-1",
            previousServiceRevisionId: null,
            CancellationToken.None);

        var before = await InvokeThroughServiceAsync(
            provider,
            invocationPort,
            identity,
            binding.Script!.EndpointIds.Single(),
            runtimeActorId: initialState.ActiveTarget.PrimaryActorId,
            commandId: "svc-command-1",
            inputText: "first input",
            expectedNormalizedText: "REPUBLISH-V1:FIRST INPUT",
            CancellationToken.None);
        before.NormalizedText.Should().Be("REPUBLISH-V1:FIRST INPUT");

        initialState.Revisions.Revisions.Should().ContainSingle(x => x.RevisionId == "svc-rev-1");

        var promoted = await scopeScriptPort.UpsertAsync(
            new ScopeScriptUpsertRequest(
                scopeId,
                scriptId,
                ScriptPackageSpecExtensions.CreateSingleSource(v2Source),
                RevisionId: "rev-2",
                ExpectedBaseRevision: "rev-1"),
            CancellationToken.None);
        await WaitForScopeScriptAppliedAsync(
            scopeScriptObservationPort,
            scopeId,
            scriptId,
            promoted,
            CancellationToken.None);

        var promotedState = await WaitForServiceRevisionAsync(
            lifecycleQueryPort,
            servingQueryPort,
            serviceRevisionReader,
            identity,
            expectedScriptRevision: "rev-2",
            previousServiceRevisionId: "svc-rev-1",
            CancellationToken.None);
        promotedState.Revisions.Revisions.Should().HaveCount(2);
        var liveRevision = promotedState.LiveRevision;
        liveRevision.Implementation!.Scripting.Should().NotBeNull();
        liveRevision.Implementation.Scripting!.Revision.Should().Be("rev-2");
        liveRevision.Implementation.Scripting.DefinitionActorId.Should().Be(promoted.AcceptedScript.DefinitionActorId);
        liveRevision.Implementation.Scripting.SourceHash.Should().Be(promoted.AcceptedScript.SourceHash);
        promotedState.Service.DefaultServingRevisionId.Should().Be(liveRevision.RevisionId);
        promotedState.ActiveTarget.PrimaryActorId.Should().Be($"gagent-service:script-runtime:{promotedState.ActiveTarget.DeploymentId}");

        var after = await InvokeThroughServiceAsync(
            provider,
            invocationPort,
            identity,
            binding.Script.EndpointIds.Single(),
            runtimeActorId: promotedState.ActiveTarget.PrimaryActorId,
            commandId: "svc-command-2",
            inputText: "second input",
            expectedNormalizedText: "REPUBLISH-V2:SECOND INPUT",
            CancellationToken.None);
        after.NormalizedText.Should().Be("REPUBLISH-V2:SECOND INPUT");
    }

    [Fact]
    public async Task EvolutionPromote_ShouldRepublishAllBoundServices_AndSkipWhenNoBindings()
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddWorkflowProjectionReadModelProviders(configuration);
        // Scripting is opt-in: the host composes the scripting capability first, and
        // AddGAgentServiceCapability bridges to it only when present.
        services.AddScriptCapability(configuration);
        services.AddGAgentServiceCapability(configuration);
        services.AddAttachOnlyScriptEvolutionApplicationService();
        services.Replace(ServiceDescriptor.Singleton<IScriptDefinitionSnapshotPort, InMemoryHarnessScriptDefinitionSnapshotPort>());
        services.Replace(ServiceDescriptor.Singleton<IServiceCommandPort, InMemoryHarnessServiceCommandPort>());

        await using var provider = services.BuildServiceProvider();
        var scopeScriptPort = provider.GetRequiredService<IScopeScriptCommandPort>();
        var scopeScriptObservationPort = provider.GetRequiredService<IScopeScriptSaveObservationPort>();
        var scopeBindingPort = provider.GetRequiredService<IScopeBindingCommandPort>();
        var lifecycleQueryPort = provider.GetRequiredService<IServiceLifecycleQueryPort>();
        var invocationPort = provider.GetRequiredService<IServiceInvocationPort>();
        var evolutionService = provider.GetRequiredService<IScriptEvolutionApplicationService>();
        var servingQueryPort = provider.GetRequiredService<IServiceServingQueryPort>();
        var definitionSnapshotReader = provider.GetRequiredService<IProjectionDocumentReader<ScriptDefinitionSnapshotDocument, string>>();
        var serviceRevisionReader = provider.GetRequiredService<IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string>>();

        var boundScopeId = $"scope-{Guid.NewGuid():N}";
        var unboundScopeId = $"scope-{Guid.NewGuid():N}";
        var scriptId = $"script-{Guid.NewGuid():N}";
        var leftServiceId = $"svc-left-{Guid.NewGuid():N}";
        var rightServiceId = $"svc-right-{Guid.NewGuid():N}";

        var v1Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
            "EvolutionScriptV1",
            "EVOLVE-V1",
            "evolution_normalization",
            "1");
        var v2Source = ScriptEvolutionIntegrationSources.BuildNormalizationBehaviorSource(
            "EvolutionScriptV2",
            "EVOLVE-V2",
            "evolution_normalization",
            "2");

        var boundUpsert = await scopeScriptPort.UpsertAsync(
            new ScopeScriptUpsertRequest(
                boundScopeId,
                scriptId,
                ScriptPackageSpecExtensions.CreateSingleSource(v1Source),
                RevisionId: "rev-1"),
            CancellationToken.None);
        await WaitForScopeScriptAppliedAsync(
            scopeScriptObservationPort,
            boundScopeId,
            scriptId,
            boundUpsert,
            CancellationToken.None);
        await WaitForDefinitionSnapshotProjectionAsync(
            definitionSnapshotReader,
            boundUpsert.DefinitionActorId,
            "rev-1",
            CancellationToken.None);
        var unboundUpsert = await scopeScriptPort.UpsertAsync(
            new ScopeScriptUpsertRequest(
                unboundScopeId,
                scriptId,
                ScriptPackageSpecExtensions.CreateSingleSource(v1Source),
                RevisionId: "rev-1"),
            CancellationToken.None);
        await WaitForScopeScriptAppliedAsync(
            scopeScriptObservationPort,
            unboundScopeId,
            scriptId,
            unboundUpsert,
            CancellationToken.None);

        var leftBinding = await scopeBindingPort.UpsertAsync(
            new ScopeBindingUpsertRequest(
                boundScopeId,
                ScopeBindingImplementationKind.Scripting,
                Script: new ScopeBindingScriptSpec(scriptId),
                ServiceId: leftServiceId,
                RevisionId: "svc-left-rev-1"),
            CancellationToken.None);
        var rightBinding = await scopeBindingPort.UpsertAsync(
            new ScopeBindingUpsertRequest(
                boundScopeId,
                ScopeBindingImplementationKind.Scripting,
                Script: new ScopeBindingScriptSpec(scriptId),
                ServiceId: rightServiceId,
                RevisionId: "svc-right-rev-1"),
            CancellationToken.None);

        var leftIdentity = BuildIdentity(boundScopeId, leftServiceId);
        var rightIdentity = BuildIdentity(boundScopeId, rightServiceId);
        await WaitForServiceRevisionAsync(
            lifecycleQueryPort,
            servingQueryPort,
            serviceRevisionReader,
            leftIdentity,
            expectedScriptRevision: "rev-1",
            previousServiceRevisionId: null,
            CancellationToken.None);
        await WaitForServiceRevisionAsync(
            lifecycleQueryPort,
            servingQueryPort,
            serviceRevisionReader,
            rightIdentity,
            expectedScriptRevision: "rev-1",
            previousServiceRevisionId: null,
            CancellationToken.None);

        var unboundDecision = await evolutionService.ProposeAsync(
            new ProposeScriptEvolutionRequest(
                ScriptId: scriptId,
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2-unbound",
                CandidateSource: v2Source,
                CandidateSourceHash: string.Empty,
                Reason: "unbound scope promote",
                ProposalId: $"proposal-{Guid.NewGuid():N}",
                ScopeId: unboundScopeId),
            CancellationToken.None);
        unboundDecision.Accepted.Should().BeTrue();
        var leftRevisionsAfterUnbound = await WaitForServiceRevisionCountAsync(
            lifecycleQueryPort,
            leftIdentity,
            expectedCount: 1,
            CancellationToken.None);
        leftRevisionsAfterUnbound.Revisions.Should().ContainSingle();

        var boundDecision = await evolutionService.ProposeAsync(
            new ProposeScriptEvolutionRequest(
                ScriptId: scriptId,
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: v2Source,
                CandidateSourceHash: string.Empty,
                Reason: "bound scope promote",
                ProposalId: $"proposal-{Guid.NewGuid():N}",
                ScopeId: boundScopeId),
            CancellationToken.None);
        boundDecision.Accepted.Should().BeTrue();
        boundDecision.Status.Should().Be("promoted");

        var leftPromotedState = await WaitForServiceRevisionAsync(
            lifecycleQueryPort,
            servingQueryPort,
            serviceRevisionReader,
            leftIdentity,
            expectedScriptRevision: "rev-2",
            previousServiceRevisionId: "svc-left-rev-1",
            CancellationToken.None);
        var rightPromotedState = await WaitForServiceRevisionAsync(
            lifecycleQueryPort,
            servingQueryPort,
            serviceRevisionReader,
            rightIdentity,
            expectedScriptRevision: "rev-2",
            previousServiceRevisionId: "svc-right-rev-1",
            CancellationToken.None);
        leftPromotedState.Revisions.Revisions.Should().HaveCount(2);
        rightPromotedState.Revisions.Revisions.Should().HaveCount(2);
        leftPromotedState.LiveRevision.Implementation!.Scripting!.Revision.Should().Be("rev-2");
        rightPromotedState.LiveRevision.Implementation!.Scripting!.Revision.Should().Be("rev-2");
        leftPromotedState.Service.DefaultServingRevisionId.Should().Be(leftPromotedState.LiveRevision.RevisionId);
        rightPromotedState.Service.DefaultServingRevisionId.Should().Be(rightPromotedState.LiveRevision.RevisionId);

        var leftFact = await InvokeThroughServiceAsync(
            provider,
            invocationPort,
            leftIdentity,
            leftBinding.Script!.EndpointIds.Single(),
            leftPromotedState.ActiveTarget.PrimaryActorId,
            "svc-left-command-1",
            "left path",
            "EVOLVE-V2:LEFT PATH",
            CancellationToken.None);
        var rightFact = await InvokeThroughServiceAsync(
            provider,
            invocationPort,
            rightIdentity,
            rightBinding.Script!.EndpointIds.Single(),
            rightPromotedState.ActiveTarget.PrimaryActorId,
            "svc-right-command-1",
            "right path",
            "EVOLVE-V2:RIGHT PATH",
            CancellationToken.None);
        leftFact.NormalizedText.Should().Be("EVOLVE-V2:LEFT PATH");
        rightFact.NormalizedText.Should().Be("EVOLVE-V2:RIGHT PATH");
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GAgentService:Demo:Enabled"] = "false",
                ["Projection:Document:Providers:InMemory:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
                ["Projection:Graph:Providers:Neo4j:Enabled"] = "false",
                ["Projection:Policies:Environment"] = "Development",
            })
            .Build();
    }

    private static ServiceIdentity BuildIdentity(string scopeId, string serviceId) =>
        new()
        {
            TenantId = scopeId,
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = serviceId,
        };

    private static async Task<TextNormalizationReadModel> InvokeThroughServiceAsync(
        IServiceProvider provider,
        IServiceInvocationPort invocationPort,
        ServiceIdentity identity,
        string endpointId,
        string runtimeActorId,
        string commandId,
        string inputText,
        string expectedNormalizedText,
        CancellationToken ct)
    {
        var projectionPort = provider.GetRequiredService<IScriptExecutionProjectionPort>();
        var lease = await provider.EnsureScriptExecutionProjectionAsync(runtimeActorId, ct)
            ?? throw new InvalidOperationException($"Failed to ensure script execution projection. actor_id={runtimeActorId}");

        try
        {
            await invocationPort.InvokeAsync(
                new ServiceInvocationRequest
                {
                    Identity = identity.Clone(),
                    EndpointId = endpointId,
                    CommandId = commandId,
                    CorrelationId = commandId,
                    Payload = Any.Pack(new TextNormalizationRequested
                    {
                        CommandId = commandId,
                        InputText = inputText,
                    }),
                },
                ct);

            return await WaitForTextNormalizationReadModelAsync(
                provider.GetRequiredService<IScriptReadModelQueryApplicationService>(),
                runtimeActorId,
                expectedNormalizedText,
                ct);
        }
        finally
        {
            await projectionPort.ReleaseActorProjectionAsync(lease, ct);
        }
    }

    private static async Task<TextNormalizationReadModel> WaitForTextNormalizationReadModelAsync(
        IScriptReadModelQueryApplicationService queryService,
        string runtimeActorId,
        string expectedNormalizedText,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ObservationTimeout);

        TextNormalizationReadModel? last = null;
        try
        {
            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();
                var snapshot = await queryService.GetSnapshotAsync(runtimeActorId, timeoutCts.Token);
                if (snapshot?.ReadModelPayload != null)
                {
                    last = snapshot.ReadModelPayload.Unpack<TextNormalizationReadModel>();
                    if (string.Equals(last.NormalizedText, expectedNormalizedText, StringComparison.Ordinal))
                        return last;
                }

                await Task.Delay(ObservationPollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Timed out waiting for script read model. actor_id={runtimeActorId}, expected_normalized_text={expectedNormalizedText}, last_normalized_text={last?.NormalizedText ?? "<null>"}");
        }
    }

    private static async Task WaitForScopeScriptAppliedAsync(
        IScopeScriptSaveObservationPort observationPort,
        string scopeId,
        string scriptId,
        ScopeScriptUpsertResult accepted,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        var request = new ScopeScriptSaveObservationRequest(
            accepted.RevisionId,
            accepted.DefinitionActorId,
            accepted.SourceHash,
            accepted.AcceptedScript.ProposalId,
            accepted.AcceptedScript.ExpectedBaseRevision,
            accepted.AcceptedScript.AcceptedAt);

        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            var observation = await observationPort.ObserveAsync(scopeId, scriptId, request, timeoutCts.Token);
            if (string.Equals(observation.Status, ScopeScriptSaveObservationStatuses.Applied, StringComparison.Ordinal))
                return;
            if (string.Equals(observation.Status, ScopeScriptSaveObservationStatuses.Rejected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Scope script promote was rejected while waiting for catalog visibility. scope_id={scopeId}, script_id={scriptId}, revision={accepted.RevisionId}, message={observation.Message}");
            }

            await Task.Delay(ObservationPollInterval, timeoutCts.Token);
        }
    }

    private static async Task WaitForDefinitionSnapshotProjectionAsync(
        IProjectionDocumentReader<ScriptDefinitionSnapshotDocument, string> definitionSnapshotReader,
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            var snapshot = await definitionSnapshotReader.GetAsync(definitionActorId, timeoutCts.Token);
            if (snapshot != null &&
                string.Equals(snapshot.Revision, requestedRevision, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(ObservationPollInterval, timeoutCts.Token);
        }
    }

    private static async Task<ServiceRevisionCatalogSnapshot> WaitForServiceRevisionCountAsync(
        IServiceLifecycleQueryPort lifecycleQueryPort,
        ServiceIdentity identity,
        int expectedCount,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            var revisions = await lifecycleQueryPort.GetServiceRevisionsAsync(identity, timeoutCts.Token);
            if (revisions != null && revisions.Revisions.Count == expectedCount)
                return revisions;

            await Task.Delay(ObservationPollInterval, timeoutCts.Token);
        }
    }

    private static async Task<ServiceRevisionObservation> WaitForServiceRevisionAsync(
        IServiceLifecycleQueryPort lifecycleQueryPort,
        IServiceServingQueryPort servingQueryPort,
        IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> serviceRevisionReader,
        ServiceIdentity identity,
        string expectedScriptRevision,
        string? previousServiceRevisionId,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        ServiceCatalogSnapshot? lastService = null;
        ServiceRevisionCatalogSnapshot? lastRevisions = null;
        ServiceServingSetSnapshot? lastServing = null;
        ServiceRevisionCatalogReadModel? lastProjectedRevisions = null;

        try
        {
            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();
                lastService = await lifecycleQueryPort.GetServiceAsync(identity, timeoutCts.Token);
                lastRevisions = await lifecycleQueryPort.GetServiceRevisionsAsync(identity, timeoutCts.Token);
                lastServing = await servingQueryPort.GetServiceServingSetAsync(identity, timeoutCts.Token);
                lastProjectedRevisions = await serviceRevisionReader.GetAsync(ServiceKeys.Build(identity), timeoutCts.Token);

                var liveRevision = lastRevisions?.Revisions.FirstOrDefault(revision =>
                    revision.Implementation?.Scripting != null &&
                    string.Equals(revision.Implementation.Scripting.Revision, expectedScriptRevision, StringComparison.Ordinal) &&
                    (string.IsNullOrWhiteSpace(previousServiceRevisionId) ||
                     !string.Equals(revision.RevisionId, previousServiceRevisionId, StringComparison.Ordinal)));
                var projectedLiveRevision = lastProjectedRevisions?.Revisions.FirstOrDefault(revision =>
                    string.Equals(revision.ScriptingRevision, expectedScriptRevision, StringComparison.Ordinal) &&
                    (string.IsNullOrWhiteSpace(previousServiceRevisionId) ||
                     !string.Equals(revision.RevisionId, previousServiceRevisionId, StringComparison.Ordinal)));

                if (lastService != null &&
                    lastRevisions != null &&
                    lastServing != null &&
                    projectedLiveRevision != null &&
                    liveRevision != null &&
                    string.Equals(lastService.DefaultServingRevisionId, liveRevision.RevisionId, StringComparison.Ordinal))
                {
                    var activeTarget = lastServing.Targets.FirstOrDefault(target =>
                        string.Equals(target.RevisionId, liveRevision.RevisionId, StringComparison.Ordinal) &&
                        target.AllocationWeight > 0 &&
                        string.Equals(target.ServingState, ServiceServingState.Active.ToString(), StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(target.PrimaryActorId));
                    if (activeTarget != null)
                    {
                        return new ServiceRevisionObservation(
                            lastService,
                            lastRevisions,
                            liveRevision,
                            activeTarget);
                    }
                }

                await Task.Delay(ObservationPollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Timed out waiting for service revision. service_key={ServiceKeys.Build(identity)}, expected_script_revision={expectedScriptRevision}, " +
                $"default_serving={lastService?.DefaultServingRevisionId ?? "<null>"}, " +
                $"fallback_revisions=[{string.Join(", ", lastRevisions?.Revisions.Select(x => $"{x.RevisionId}:{x.Implementation?.Scripting?.Revision ?? "<none>"}:{x.Status}") ?? [])}], " +
                $"projected_revisions=[{string.Join(", ", lastProjectedRevisions?.Revisions.Select(x => $"{x.RevisionId}:{x.ScriptingRevision}:{x.Status}") ?? [])}], " +
                $"serving_targets=[{string.Join(", ", lastServing?.Targets.Select(x => $"{x.RevisionId}:{x.ServingState}:{x.AllocationWeight}:{x.PrimaryActorId}") ?? [])}]");
        }
    }

    private sealed record ServiceRevisionObservation(
        ServiceCatalogSnapshot Service,
        ServiceRevisionCatalogSnapshot Revisions,
        ServiceRevisionSnapshot LiveRevision,
        ServiceServingTargetSnapshot ActiveTarget);

    private sealed class InMemoryHarnessServiceCommandPort : IServiceCommandPort
    {
        private readonly ServiceCommandApplicationService _inner;
        private readonly IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> _revisionReader;
        private readonly IScriptDefinitionSnapshotPort _definitionSnapshotPort;

        public InMemoryHarnessServiceCommandPort(
            IActorDispatchPort dispatchPort,
            IServiceCommandTargetProvisioner targetProvisioner,
            IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> revisionReader,
            IScriptDefinitionSnapshotPort definitionSnapshotPort)
        {
            _inner = new ServiceCommandApplicationService(
                dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort)),
                targetProvisioner ?? throw new ArgumentNullException(nameof(targetProvisioner)));
            _revisionReader = revisionReader ?? throw new ArgumentNullException(nameof(revisionReader));
            _definitionSnapshotPort = definitionSnapshotPort ?? throw new ArgumentNullException(nameof(definitionSnapshotPort));
        }

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(
            CreateServiceDefinitionCommand command,
            CancellationToken ct = default) =>
            _inner.CreateServiceAsync(command, ct);

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(
            UpdateServiceDefinitionCommand command,
            CancellationToken ct = default) =>
            _inner.UpdateServiceAsync(command, ct);

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(
            CreateServiceRevisionCommand command,
            CancellationToken ct = default) =>
            CreateRevisionAndWaitAsync(command, ct);

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(
            PrepareServiceRevisionCommand command,
            CancellationToken ct = default) =>
            PrepareRevisionAndWaitAsync(command, ct);

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(
            PublishServiceRevisionCommand command,
            CancellationToken ct = default) =>
            PublishRevisionAndWaitAsync(command, ct);

        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(
            RetireServiceRevisionCommand command,
            CancellationToken ct = default) =>
            _inner.RetireRevisionAsync(command, ct);

        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(
            SetDefaultServingRevisionCommand command,
            CancellationToken ct = default) =>
            _inner.SetDefaultServingRevisionAsync(command, ct);

        public async Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(
            ActivateServiceRevisionCommand command,
            CancellationToken ct = default)
        {
            await WaitForPreparedRevisionProjectionAsync(command.Identity, command.RevisionId, ct);
            return await _inner.ActivateServiceRevisionAsync(command, ct);
        }

        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(
            DeactivateServiceDeploymentCommand command,
            CancellationToken ct = default) =>
            _inner.DeactivateServiceDeploymentAsync(command, ct);

        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(
            ReplaceServiceServingTargetsCommand command,
            CancellationToken ct = default) =>
            _inner.ReplaceServiceServingTargetsAsync(command, ct);

        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(
            StartServiceRolloutCommand command,
            CancellationToken ct = default) =>
            _inner.StartServiceRolloutAsync(command, ct);

        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(
            AdvanceServiceRolloutCommand command,
            CancellationToken ct = default) =>
            _inner.AdvanceServiceRolloutAsync(command, ct);

        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(
            PauseServiceRolloutCommand command,
            CancellationToken ct = default) =>
            _inner.PauseServiceRolloutAsync(command, ct);

        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(
            ResumeServiceRolloutCommand command,
            CancellationToken ct = default) =>
            _inner.ResumeServiceRolloutAsync(command, ct);

        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(
            RollbackServiceRolloutCommand command,
            CancellationToken ct = default) =>
            _inner.RollbackServiceRolloutAsync(command, ct);

        private async Task WaitForPreparedRevisionProjectionAsync(
            ServiceIdentity identity,
            string revisionId,
            CancellationToken ct)
        {
            _ = await WaitForRevisionProjectionAsync(
                identity,
                revisionId,
                static revision =>
                    revision.PreparedArtifact != null &&
                    !string.IsNullOrWhiteSpace(revision.PreparedArtifact.RevisionId),
                $"prepared artifact for revision `{revisionId}`",
                ct);
        }

        private async Task<ServiceCommandAcceptedReceipt> CreateRevisionAndWaitAsync(
            CreateServiceRevisionCommand command,
            CancellationToken ct)
        {
            var receipt = await _inner.CreateRevisionAsync(command, ct);
            await WaitForRevisionProjectionAsync(
                command.Spec.Identity,
                command.Spec.RevisionId,
                static _ => true,
                $"created revision `{command.Spec.RevisionId}`",
                ct);
            return receipt;
        }

        private async Task<ServiceCommandAcceptedReceipt> PrepareRevisionAndWaitAsync(
            PrepareServiceRevisionCommand command,
            CancellationToken ct)
        {
            var created = await WaitForRevisionProjectionAsync(
                command.Identity,
                command.RevisionId,
                static _ => true,
                $"created revision `{command.RevisionId}`",
                ct);
            await WaitForScriptingDefinitionSnapshotAsync(created, ct);

            var receipt = await _inner.PrepareRevisionAsync(command, ct);
            await WaitForPreparedRevisionProjectionAsync(command.Identity, command.RevisionId, ct);
            return receipt;
        }

        private async Task<ServiceCommandAcceptedReceipt> PublishRevisionAndWaitAsync(
            PublishServiceRevisionCommand command,
            CancellationToken ct)
        {
            await WaitForPreparedRevisionProjectionAsync(command.Identity, command.RevisionId, ct);
            var receipt = await _inner.PublishRevisionAsync(command, ct);
            await WaitForRevisionProjectionAsync(
                command.Identity,
                command.RevisionId,
                static revision => string.Equals(revision.Status, ServiceRevisionStatus.Published.ToString(), StringComparison.Ordinal),
                $"published revision `{command.RevisionId}`",
                ct);
            return receipt;
        }

        private async Task WaitForScriptingDefinitionSnapshotAsync(
            ServiceRevisionEntryReadModel revision,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(revision.ScriptingDefinitionActorId) ||
                string.IsNullOrWhiteSpace(revision.ScriptingRevision))
            {
                return;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();
                var snapshot = await _definitionSnapshotPort.TryGetAsync(
                    revision.ScriptingDefinitionActorId,
                    revision.ScriptingRevision,
                    timeoutCts.Token);
                if (snapshot != null)
                    return;

                await Task.Delay(ObservationPollInterval, timeoutCts.Token);
            }
        }

        private async Task<ServiceRevisionEntryReadModel> WaitForRevisionProjectionAsync(
            ServiceIdentity identity,
            string revisionId,
            Func<ServiceRevisionEntryReadModel, bool> isReady,
            string description,
            CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();
                var readModel = await _revisionReader.GetAsync(ServiceKeys.Build(identity), timeoutCts.Token);
                var revision = readModel?.Revisions.FirstOrDefault(x =>
                    string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal));
                if (revision != null &&
                    string.Equals(revision.Status, ServiceRevisionStatus.PreparationFailed.ToString(), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Revision `{revisionId}` failed preparation before {description} was visible. reason={revision.FailureReason}");
                }

                if (revision != null && isReady(revision))
                {
                    return revision;
                }

                await Task.Delay(ObservationPollInterval, timeoutCts.Token);
            }
        }
    }

    private sealed class InMemoryHarnessScriptDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
    {
        private readonly IProjectionDocumentReader<ScriptDefinitionSnapshotDocument, string> _documentReader;
        private readonly IActorRuntime _runtime;

        public InMemoryHarnessScriptDefinitionSnapshotPort(
            IProjectionDocumentReader<ScriptDefinitionSnapshotDocument, string> documentReader,
            IActorRuntime runtime)
        {
            _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        public async Task<ScriptDefinitionSnapshot?> TryGetAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definitionActorId);

            var document = await _documentReader.GetAsync(definitionActorId, ct);
            if (document != null &&
                (string.IsNullOrWhiteSpace(requestedRevision) ||
                 string.Equals(document.Revision, requestedRevision, StringComparison.Ordinal)))
            {
                return ToSnapshot(document);
            }

            var actor = await _runtime.GetAsync(definitionActorId);
            if (actor?.Agent is not ScriptDefinitionGAgent definitionAgent)
                return null;

            var state = definitionAgent.State;
            if (string.IsNullOrWhiteSpace(state.Revision))
                return null;
            if (!string.IsNullOrWhiteSpace(requestedRevision) &&
                !string.Equals(state.Revision, requestedRevision, StringComparison.Ordinal))
            {
                return null;
            }

            return ToSnapshot(definitionActorId, state);
        }

        public async Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            var snapshot = await TryGetAsync(definitionActorId, requestedRevision, ct);
            if (snapshot == null)
            {
                throw new InvalidOperationException(
                    $"Script definition snapshot not found for actor `{definitionActorId}` revision `{requestedRevision}`.");
            }

            if ((snapshot.ScriptPackage?.CsharpSources.Count ?? 0) == 0)
            {
                throw new InvalidOperationException(
                    $"Script definition script_package is empty for actor `{definitionActorId}`.");
            }

            return snapshot;
        }

        private static ScriptDefinitionSnapshot ToSnapshot(ScriptDefinitionSnapshotDocument document)
        {
            var protocolDescriptorSet = string.IsNullOrWhiteSpace(document.ProtocolDescriptorSetBase64)
                ? ByteString.Empty
                : ByteString.FromBase64(document.ProtocolDescriptorSetBase64);

            return new ScriptDefinitionSnapshot(
                document.ScriptId,
                document.Revision,
                document.SourceHash,
                document.ScriptPackage?.Clone() ?? new ScriptPackageSpec(),
                document.StateTypeUrl,
                document.ReadModelTypeUrl,
                document.ReadModelSchemaVersion,
                document.ReadModelSchemaHash,
                protocolDescriptorSet,
                document.StateDescriptorFullName,
                document.ReadModelDescriptorFullName,
                document.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec(),
                document.DefinitionActorId,
                document.ScopeId);
        }

        private static ScriptDefinitionSnapshot ToSnapshot(
            string definitionActorId,
            ScriptDefinitionState state)
        {
            return new ScriptDefinitionSnapshot(
                state.ScriptId,
                state.Revision,
                state.SourceHash,
                state.ScriptPackage?.Clone() ?? new ScriptPackageSpec(),
                state.StateTypeUrl,
                state.ReadModelTypeUrl,
                state.ReadModelSchemaVersion,
                state.ReadModelSchemaHash,
                state.ProtocolDescriptorSet ?? ByteString.Empty,
                state.StateDescriptorFullName,
                state.ReadModelDescriptorFullName,
                state.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec(),
                definitionActorId,
                state.ScopeId);
        }
    }
}
