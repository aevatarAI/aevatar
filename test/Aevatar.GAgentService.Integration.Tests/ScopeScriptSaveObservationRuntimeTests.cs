using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeScriptSaveObservationRuntimeTests
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ObservationPollInterval = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task UpsertAsync_ShouldMakeAcceptedCatalogPromotionObservable_WithRealInMemoryProjection()
    {
        var configuration = new ConfigurationBuilder()
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
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddGAgentServiceCapability(configuration);

        await using var provider = services.BuildServiceProvider();
        var commandPort = provider.GetRequiredService<IScopeScriptCommandPort>();
        var observationPort = provider.GetRequiredService<IScopeScriptSaveObservationPort>();
        var queryPort = provider.GetRequiredService<IScopeScriptQueryPort>();
        var definitionSnapshotPort = provider.GetRequiredService<IScriptDefinitionSnapshotPort>();

        var scopeId = $"scope-{Guid.NewGuid():N}";
        var scriptId = $"script-{Guid.NewGuid():N}";
        const string revisionId = "draft-1";
        var accepted = await commandPort.UpsertAsync(
            new ScopeScriptUpsertRequest(
                scopeId,
                scriptId,
                CatalogOnlyBehaviorSource,
                revisionId),
            CancellationToken.None);
        var observationRequest = new ScopeScriptSaveObservationRequest(
            accepted.RevisionId,
            accepted.DefinitionActorId,
            accepted.SourceHash,
            accepted.AcceptedScript.ProposalId,
            accepted.AcceptedScript.ExpectedBaseRevision,
            accepted.AcceptedScript.AcceptedAt);

        var observation = await WaitForObservationAsync(
            token => observationPort.ObserveAsync(scopeId, scriptId, observationRequest, token),
            CancellationToken.None);
        var scripts = await queryPort.ListAsync(scopeId, CancellationToken.None);
        var definitionSnapshot = await WaitForDefinitionSnapshotAsync(
            token => definitionSnapshotPort.TryGetAsync(accepted.DefinitionActorId, revisionId, token),
            CancellationToken.None);

        observation.Status.Should().Be(ScopeScriptSaveObservationStatuses.Applied);
        observation.CurrentScript.Should().NotBeNull();
        observation.CurrentScript!.ScriptId.Should().Be(scriptId);
        observation.CurrentScript.ActiveRevision.Should().Be(revisionId);
        scripts.Should().ContainSingle(script => script.ScriptId == scriptId);
        definitionSnapshot.ScriptId.Should().Be(scriptId);
        definitionSnapshot.Revision.Should().Be(revisionId);
    }

    private static async Task<ScopeScriptSaveObservationResult> WaitForObservationAsync(
        Func<CancellationToken, Task<ScopeScriptSaveObservationResult>> observeAsync,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ObservationTimeout);

        ScopeScriptSaveObservationResult? last = null;
        try
        {
            while (true)
            {
                last = await observeAsync(timeoutCts.Token);
                if (string.Equals(last.Status, ScopeScriptSaveObservationStatuses.Applied, StringComparison.Ordinal))
                    return last;

                await Task.Delay(ObservationPollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Script save observation did not apply before timeout. last_status={last?.Status ?? "<none>"}");
        }
    }

    private static async Task<ScriptDefinitionSnapshot> WaitForDefinitionSnapshotAsync(
        Func<CancellationToken, Task<ScriptDefinitionSnapshot?>> readAsync,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ObservationTimeout);

        try
        {
            while (true)
            {
                var snapshot = await readAsync(timeoutCts.Token);
                if (snapshot != null)
                    return snapshot;

                await Task.Delay(ObservationPollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Script definition snapshot did not appear before timeout.");
        }
    }

    private const string CatalogOnlyBehaviorSource =
        """
        using Google.Protobuf.WellKnownTypes;
        using Aevatar.Scripting.Abstractions.Behaviors;

        public sealed class CatalogOnlyBehavior : ScriptBehavior<StringValue, StringValue>
        {
            protected override void Configure(IScriptBehaviorBuilder<StringValue, StringValue> builder)
            {
                builder.ProjectState(static (state, _) => state ?? new StringValue());
            }
        }
        """;
}
