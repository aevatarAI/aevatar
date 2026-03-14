namespace Aevatar.Integration.Tests;

internal static class ScriptEvolutionIntegrationSources
{
    public static string BuildNormalizationBehaviorSource(
        string className,
        string marker,
        string schemaName,
        string schemaVersion) =>
        $$"""
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions.Behaviors;

        // schema hint: {{schemaName}}@{{schemaVersion}}
        public sealed class {{className}} : ScriptBehavior<TextNormalizationReadModel, TextNormalizationReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<TextNormalizationReadModel, TextNormalizationReadModel> builder)
            {
                builder
                    .OnCommand<TextNormalizationRequested>(HandleAsync)
                    .OnEvent<TextNormalizationCompleted>(
                        apply: static (_, evt, _) => evt.Current,
                        reduce: static (_, evt, _) => evt.Current)
                    .OnQuery<TextNormalizationQueryRequested, TextNormalizationQueryResponded>(HandleQueryAsync);
            }

            private static Task HandleAsync(
                TextNormalizationRequested inbound,
                ScriptCommandContext<TextNormalizationReadModel> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var normalized = "{{marker}}" + ":" + (inbound.InputText ?? string.Empty).Trim().ToUpperInvariant();
                context.Emit(new TextNormalizationCompleted
                {
                    CommandId = inbound.CommandId ?? string.Empty,
                    Current = new TextNormalizationReadModel
                    {
                        HasValue = true,
                        LastCommandId = inbound.CommandId ?? string.Empty,
                        InputText = inbound.InputText ?? string.Empty,
                        NormalizedText = normalized,
                        Lookup = new TextNormalizationLookup
                        {
                            Normalized = normalized,
                        },
                        Refs = new TextNormalizationRefs
                        {
                            ProfileId = "{{marker}}",
                        },
                    },
                });
                return Task.CompletedTask;
            }

            private static Task<TextNormalizationQueryResponded?> HandleQueryAsync(
                TextNormalizationQueryRequested query,
                ScriptQueryContext<TextNormalizationReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<TextNormalizationQueryResponded?>(new TextNormalizationQueryResponded
                {
                    RequestId = query.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new TextNormalizationReadModel(),
                });
            }
        }
        """;

    public static readonly string ScriptOnlyOrchestratorSource =
        """
        using System;
        using System.Security.Cryptography;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Abstractions.Definitions;
        using Google.Protobuf.WellKnownTypes;

        public sealed class ScriptOnlyOrchestrator : ScriptBehavior<ScriptOnlyEvolutionState, ScriptOnlyEvolutionState>
        {
            protected override void Configure(IScriptBehaviorBuilder<ScriptOnlyEvolutionState, ScriptOnlyEvolutionState> builder)
            {
                builder
                    .OnCommand<ScriptOnlyEvolutionRequested>(HandleAsync)
                    .OnEvent<ScriptOnlyEvolutionCompleted>(
                        apply: static (_, evt, _) => evt.Current,
                        reduce: static (_, evt, _) => evt.Current);
            }

            private static async Task HandleAsync(
                ScriptOnlyEvolutionRequested input,
                ScriptCommandContext<ScriptOnlyEvolutionState> context,
                CancellationToken ct)
            {
                var newScriptSource = input.NewScriptSource ?? string.Empty;
                var workerV2Source = input.WorkerV2Source ?? string.Empty;

                var tempRuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                    "worker-definition",
                    "rev-worker-1",
                    "worker-temp-" + context.RunId,
                    ct);
                await context.RuntimeCapabilities.RunScriptInstanceAsync(
                    tempRuntimeId,
                    "worker-temp-run-" + context.RunId,
                    Any.Pack(new TextNormalizationRequested
                    {
                        CommandId = "worker-temp-" + context.RunId,
                        InputText = "temp",
                    }),
                    "rev-worker-1",
                    "worker-definition",
                    "worker.temp.run",
                    ct);

                var newDefinitionActorId = await context.RuntimeCapabilities.UpsertScriptDefinitionAsync(
                    "new-script",
                    "rev-new-1",
                    newScriptSource,
                    ComputeHash(newScriptSource),
                    "new-definition",
                    ct);
                var newRuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                    newDefinitionActorId,
                    "rev-new-1",
                    "new-runtime-" + context.RunId,
                    ct);
                await context.RuntimeCapabilities.RunScriptInstanceAsync(
                    newRuntimeId,
                    "new-run-" + context.RunId,
                    Any.Pack(new TextNormalizationRequested
                    {
                        CommandId = "new-run-" + context.RunId,
                        InputText = "new",
                    }),
                    "rev-new-1",
                    newDefinitionActorId,
                    "new.runtime.run",
                    ct);

                var decision = await context.RuntimeCapabilities.ProposeScriptEvolutionAsync(
                    new ScriptEvolutionProposal(
                        ProposalId: "proposal-" + context.RunId,
                        ScriptId: "worker-script",
                        BaseRevision: "rev-worker-1",
                        CandidateRevision: "rev-worker-2",
                        CandidateSource: workerV2Source,
                        CandidateSourceHash: ComputeHash(workerV2Source),
                        Reason: "script-only autonomous promotion"),
                    ct);

                var evolvedRuntimeId = string.Empty;
                if (decision.Accepted)
                {
                    evolvedRuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                        decision.DefinitionActorId,
                        decision.CandidateRevision,
                        "worker-evolved-" + context.RunId,
                        ct);
                    await context.RuntimeCapabilities.RunScriptInstanceAsync(
                        evolvedRuntimeId,
                        "worker-evolved-run-" + context.RunId,
                        Any.Pack(new TextNormalizationRequested
                        {
                            CommandId = "worker-evolved-" + context.RunId,
                            InputText = "evolved",
                        }),
                        decision.CandidateRevision,
                        decision.DefinitionActorId,
                        "worker.evolved.run",
                        ct);
                }

                context.Emit(new ScriptOnlyEvolutionCompleted
                {
                    Current = new ScriptOnlyEvolutionState
                    {
                        TempRuntimeId = tempRuntimeId,
                        NewRuntimeId = newRuntimeId,
                        EvolvedRuntimeId = evolvedRuntimeId,
                        NewDefinitionActorId = newDefinitionActorId,
                        DecisionStatus = decision.Status,
                    },
                });
            }

            private static string ComputeHash(string source)
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source ?? string.Empty));
                return Convert.ToHexString(bytes);
            }
        }
        """;

    public static readonly string MultiScriptOrchestratorSource =
        """
        using System;
        using System.Security.Cryptography;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Abstractions.Definitions;
        using Google.Protobuf.WellKnownTypes;

        public sealed class MultiScriptEvolutionOrchestrator : ScriptBehavior<MultiScriptEvolutionState, MultiScriptEvolutionState>
        {
            protected override void Configure(IScriptBehaviorBuilder<MultiScriptEvolutionState, MultiScriptEvolutionState> builder)
            {
                builder
                    .OnCommand<MultiScriptEvolutionRequested>(HandleAsync)
                    .OnEvent<MultiScriptEvolutionCompleted>(
                        apply: static (_, evt, _) => evt.Current,
                        reduce: static (_, evt, _) => evt.Current);
            }

            private static async Task HandleAsync(
                MultiScriptEvolutionRequested input,
                ScriptCommandContext<MultiScriptEvolutionState> context,
                CancellationToken ct)
            {
                var workerAV2Source = input.WorkerAV2Source ?? string.Empty;
                var workerAV3Source = input.WorkerAV3Source ?? string.Empty;
                var workerBV2Source = input.WorkerBV2Source ?? string.Empty;
                var workerBV3Source = input.WorkerBV3Source ?? string.Empty;
                var generatedSource1 = input.GeneratedSource1 ?? string.Empty;
                var generatedSource2 = input.GeneratedSource2 ?? string.Empty;
                var runtimeAgentType = input.RuntimeAgentType ?? string.Empty;

                var lifecycleActorId = await context.RuntimeCapabilities.CreateAgentAsync(
                    runtimeAgentType,
                    "script-created-runtime-" + context.RunId,
                    ct);
                await context.RuntimeCapabilities.LinkAgentsAsync(context.ActorId, lifecycleActorId, ct);
                await context.RuntimeCapabilities.UnlinkAgentAsync(lifecycleActorId, ct);

                var tempARuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                    "multi-worker-a-definition",
                    "rev-a-1",
                    "temp-worker-a-" + context.RunId,
                    ct);
                await context.RuntimeCapabilities.RunScriptInstanceAsync(
                    tempARuntimeId,
                    "temp-worker-a-run-" + context.RunId,
                    Any.Pack(new TextNormalizationRequested
                    {
                        CommandId = "temp-worker-a-" + context.RunId,
                        InputText = "worker a",
                    }),
                    "rev-a-1",
                    "multi-worker-a-definition",
                    "worker.a.temp",
                    ct);

                var tempBRuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                    "multi-worker-b-definition",
                    "rev-b-1",
                    "temp-worker-b-" + context.RunId,
                    ct);
                await context.RuntimeCapabilities.RunScriptInstanceAsync(
                    tempBRuntimeId,
                    "temp-worker-b-run-" + context.RunId,
                    Any.Pack(new TextNormalizationRequested
                    {
                        CommandId = "temp-worker-b-" + context.RunId,
                        InputText = "worker b",
                    }),
                    "rev-b-1",
                    "multi-worker-b-definition",
                    "worker.b.temp",
                    ct);

                var generatedDefinitionActorId1 = await context.RuntimeCapabilities.UpsertScriptDefinitionAsync(
                    "generated-script-1",
                    "rev-g-1",
                    generatedSource1,
                    ComputeHash(generatedSource1),
                    "generated-definition-1-" + context.RunId,
                    ct);
                var generatedRuntimeId1 = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                    generatedDefinitionActorId1,
                    "rev-g-1",
                    "generated-runtime-1-" + context.RunId,
                    ct);
                await context.RuntimeCapabilities.RunScriptInstanceAsync(
                    generatedRuntimeId1,
                    "generated-run-1-" + context.RunId,
                    Any.Pack(new TextNormalizationRequested
                    {
                        CommandId = "generated-1-" + context.RunId,
                        InputText = "generated one",
                    }),
                    "rev-g-1",
                    generatedDefinitionActorId1,
                    "generated.script.1.run",
                    ct);

                var decisionA2 = await context.RuntimeCapabilities.ProposeScriptEvolutionAsync(
                    new ScriptEvolutionProposal(
                        ProposalId: "proposal-a-2-" + context.RunId,
                        ScriptId: "worker-a-script",
                        BaseRevision: "rev-a-1",
                        CandidateRevision: "rev-a-2",
                        CandidateSource: workerAV2Source,
                        CandidateSourceHash: ComputeHash(workerAV2Source),
                        Reason: "upgrade worker-a to rev-a-2"),
                    ct);
                var decisionB2 = await context.RuntimeCapabilities.ProposeScriptEvolutionAsync(
                    new ScriptEvolutionProposal(
                        ProposalId: "proposal-b-2-" + context.RunId,
                        ScriptId: "worker-b-script",
                        BaseRevision: "rev-b-1",
                        CandidateRevision: "rev-b-2",
                        CandidateSource: workerBV2Source,
                        CandidateSourceHash: ComputeHash(workerBV2Source),
                        Reason: "upgrade worker-b to rev-b-2"),
                    ct);
                var decisionA3 = await context.RuntimeCapabilities.ProposeScriptEvolutionAsync(
                    new ScriptEvolutionProposal(
                        ProposalId: "proposal-a-3-" + context.RunId,
                        ScriptId: "worker-a-script",
                        BaseRevision: "rev-a-2",
                        CandidateRevision: "rev-a-3",
                        CandidateSource: workerAV3Source,
                        CandidateSourceHash: ComputeHash(workerAV3Source),
                        Reason: "upgrade worker-a to rev-a-3"),
                    ct);
                var decisionB3 = await context.RuntimeCapabilities.ProposeScriptEvolutionAsync(
                    new ScriptEvolutionProposal(
                        ProposalId: "proposal-b-3-" + context.RunId,
                        ScriptId: "worker-b-script",
                        BaseRevision: "rev-b-2",
                        CandidateRevision: "rev-b-3",
                        CandidateSource: workerBV3Source,
                        CandidateSourceHash: ComputeHash(workerBV3Source),
                        Reason: "upgrade worker-b to rev-b-3"),
                    ct);

                var evolvedARuntimeId = string.Empty;
                if (decisionA3.Accepted)
                {
                    evolvedARuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                        decisionA3.DefinitionActorId,
                        decisionA3.CandidateRevision,
                        "worker-a-evolved-" + context.RunId,
                        ct);
                    await context.RuntimeCapabilities.RunScriptInstanceAsync(
                        evolvedARuntimeId,
                        "worker-a-evolved-run-" + context.RunId,
                        Any.Pack(new TextNormalizationRequested
                        {
                            CommandId = "worker-a-evolved-" + context.RunId,
                            InputText = "worker a evolved",
                        }),
                        decisionA3.CandidateRevision,
                        decisionA3.DefinitionActorId,
                        "worker.a.evolved",
                        ct);
                }

                var evolvedBRuntimeId = string.Empty;
                if (decisionB3.Accepted)
                {
                    evolvedBRuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                        decisionB3.DefinitionActorId,
                        decisionB3.CandidateRevision,
                        "worker-b-evolved-" + context.RunId,
                        ct);
                    await context.RuntimeCapabilities.RunScriptInstanceAsync(
                        evolvedBRuntimeId,
                        "worker-b-evolved-run-" + context.RunId,
                        Any.Pack(new TextNormalizationRequested
                        {
                            CommandId = "worker-b-evolved-" + context.RunId,
                            InputText = "worker b evolved",
                        }),
                        decisionB3.CandidateRevision,
                        decisionB3.DefinitionActorId,
                        "worker.b.evolved",
                        ct);
                }

                var generatedDefinitionActorId2 = await context.RuntimeCapabilities.UpsertScriptDefinitionAsync(
                    "generated-script-2",
                    "rev-g-2",
                    generatedSource2,
                    ComputeHash(generatedSource2),
                    "generated-definition-2-" + context.RunId,
                    ct);
                var generatedRuntimeId2 = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                    generatedDefinitionActorId2,
                    "rev-g-2",
                    "generated-runtime-2-" + context.RunId,
                    ct);
                await context.RuntimeCapabilities.RunScriptInstanceAsync(
                    generatedRuntimeId2,
                    "generated-run-2-" + context.RunId,
                    Any.Pack(new TextNormalizationRequested
                    {
                        CommandId = "generated-2-" + context.RunId,
                        InputText = "generated two",
                    }),
                    "rev-g-2",
                    generatedDefinitionActorId2,
                    "generated.script.2.run",
                    ct);

                await context.RuntimeCapabilities.DestroyAgentAsync(lifecycleActorId, ct);

                context.Emit(new MultiScriptEvolutionCompleted
                {
                    Current = new MultiScriptEvolutionState
                    {
                        LifecycleActorId = lifecycleActorId,
                        TempARuntimeId = tempARuntimeId,
                        TempBRuntimeId = tempBRuntimeId,
                        GeneratedRuntimeId1 = generatedRuntimeId1,
                        GeneratedRuntimeId2 = generatedRuntimeId2,
                        EvolvedARuntimeId = evolvedARuntimeId,
                        EvolvedBRuntimeId = evolvedBRuntimeId,
                        DecisionA2 = decisionA2.Status,
                        DecisionA3 = decisionA3.Status,
                        DecisionB2 = decisionB2.Status,
                        DecisionB3 = decisionB3.Status,
                    },
                });
            }

            private static string ComputeHash(string source)
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source ?? string.Empty));
                return Convert.ToHexString(bytes);
            }
        }
        """;

    public static readonly string SelfEvolutionV1Source =
        """
        using System;
        using System.Security.Cryptography;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Abstractions.Definitions;
        using Google.Protobuf.WellKnownTypes;

        public sealed class SelfEvolutionV1Runtime : ScriptBehavior<SelfEvolutionV1State, SelfEvolutionV1State>
        {
            protected override void Configure(IScriptBehaviorBuilder<SelfEvolutionV1State, SelfEvolutionV1State> builder)
            {
                builder
                    .OnCommand<SelfEvolutionV1Requested>(HandleAsync)
                    .OnEvent<SelfEvolutionV1Completed>(
                        apply: static (_, evt, _) => evt.Current,
                        reduce: static (_, evt, _) => evt.Current);
            }

            private static async Task HandleAsync(
                SelfEvolutionV1Requested input,
                ScriptCommandContext<SelfEvolutionV1State> context,
                CancellationToken ct)
            {
                var nextV2Source = input.NextV2Source ?? string.Empty;
                var nextV3Source = input.NextV3Source ?? string.Empty;
                var generatedSource = input.GeneratedSource ?? string.Empty;

                var generatedDefinitionActorId = await context.RuntimeCapabilities.UpsertScriptDefinitionAsync(
                    "self-generated-script",
                    "rev-self-generated-1",
                    generatedSource,
                    ComputeHash(generatedSource),
                    "self-generated-definition-" + context.RunId,
                    ct);
                var generatedRuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                    generatedDefinitionActorId,
                    "rev-self-generated-1",
                    "self-generated-runtime-" + context.RunId,
                    ct);
                await context.RuntimeCapabilities.RunScriptInstanceAsync(
                    generatedRuntimeId,
                    "self-generated-run-" + context.RunId,
                    Any.Pack(new TextNormalizationRequested
                    {
                        CommandId = "self-generated-" + context.RunId,
                        InputText = "generated",
                    }),
                    "rev-self-generated-1",
                    generatedDefinitionActorId,
                    "self.generated.run",
                    ct);

                var decisionV2 = await context.RuntimeCapabilities.ProposeScriptEvolutionAsync(
                    new ScriptEvolutionProposal(
                        ProposalId: "self-proposal-v2-" + context.RunId,
                        ScriptId: "self-evolving-script",
                        BaseRevision: "rev-self-1",
                        CandidateRevision: "rev-self-2",
                        CandidateSource: nextV2Source,
                        CandidateSourceHash: ComputeHash(nextV2Source),
                        Reason: "self evolution to rev-self-2"),
                    ct);

                var v2RuntimeId = string.Empty;
                if (decisionV2.Accepted)
                {
                    v2RuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                        decisionV2.DefinitionActorId,
                        decisionV2.CandidateRevision,
                        "self-v2-runtime-" + context.RunId,
                        ct);
                    await context.RuntimeCapabilities.RunScriptInstanceAsync(
                        v2RuntimeId,
                        "self-v2-run-" + context.RunId,
                        Any.Pack(new SelfEvolutionV2Requested
                        {
                            NextV3Source = nextV3Source,
                        }),
                        decisionV2.CandidateRevision,
                        decisionV2.DefinitionActorId,
                        "self.v2.run",
                        ct);
                }

                context.Emit(new SelfEvolutionV1Completed
                {
                    Current = new SelfEvolutionV1State
                    {
                        DecisionV2 = decisionV2.Status,
                        V2RuntimeId = v2RuntimeId,
                        GeneratedRuntimeId = generatedRuntimeId,
                    },
                });
            }

            private static string ComputeHash(string source)
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source ?? string.Empty));
                return Convert.ToHexString(bytes);
            }
        }
        """;

    public static readonly string SelfEvolutionV2Source =
        """
        using System;
        using System.Security.Cryptography;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Abstractions.Definitions;
        using Google.Protobuf.WellKnownTypes;

        public sealed class SelfEvolutionV2Runtime : ScriptBehavior<SelfEvolutionV2State, SelfEvolutionV2State>
        {
            protected override void Configure(IScriptBehaviorBuilder<SelfEvolutionV2State, SelfEvolutionV2State> builder)
            {
                builder
                    .OnCommand<SelfEvolutionV2Requested>(HandleAsync)
                    .OnEvent<SelfEvolutionV2Completed>(
                        apply: static (_, evt, _) => evt.Current,
                        reduce: static (_, evt, _) => evt.Current);
            }

            private static async Task HandleAsync(
                SelfEvolutionV2Requested input,
                ScriptCommandContext<SelfEvolutionV2State> context,
                CancellationToken ct)
            {
                var nextV3Source = input.NextV3Source ?? string.Empty;

                var decisionV3 = await context.RuntimeCapabilities.ProposeScriptEvolutionAsync(
                    new ScriptEvolutionProposal(
                        ProposalId: "self-proposal-v3-" + context.RunId,
                        ScriptId: "self-evolving-script",
                        BaseRevision: "rev-self-2",
                        CandidateRevision: "rev-self-3",
                        CandidateSource: nextV3Source,
                        CandidateSourceHash: ComputeHash(nextV3Source),
                        Reason: "self evolution to rev-self-3"),
                    ct);

                var v3RuntimeId = string.Empty;
                if (decisionV3.Accepted)
                {
                    v3RuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                        decisionV3.DefinitionActorId,
                        decisionV3.CandidateRevision,
                        "self-v3-runtime-" + context.RunId,
                        ct);
                    await context.RuntimeCapabilities.RunScriptInstanceAsync(
                        v3RuntimeId,
                        "self-v3-run-" + context.RunId,
                        Any.Pack(new TextNormalizationRequested
                        {
                            CommandId = "self-v3-" + context.RunId,
                            InputText = "self v3",
                        }),
                        decisionV3.CandidateRevision,
                        decisionV3.DefinitionActorId,
                        "self.v3.run",
                        ct);
                }

                context.Emit(new SelfEvolutionV2Completed
                {
                    Current = new SelfEvolutionV2State
                    {
                        DecisionV3 = decisionV3.Status,
                        V3RuntimeId = v3RuntimeId,
                    },
                });
            }

            private static string ComputeHash(string source)
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source ?? string.Empty));
                return Convert.ToHexString(bytes);
            }
        }
        """;

    public static readonly string CatalogControlOrchestratorSource =
        """
        using System;
        using System.Security.Cryptography;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Abstractions.Definitions;
        using Google.Protobuf.WellKnownTypes;

        public sealed class CatalogControlOrchestrator : ScriptBehavior<CatalogControlState, CatalogControlState>
        {
            protected override void Configure(IScriptBehaviorBuilder<CatalogControlState, CatalogControlState> builder)
            {
                builder
                    .OnCommand<CatalogControlRequested>(HandleAsync)
                    .OnEvent<CatalogControlCompleted>(
                        apply: static (_, evt, _) => evt.Current,
                        reduce: static (_, evt, _) => evt.Current);
            }

            private static async Task HandleAsync(
                CatalogControlRequested input,
                ScriptCommandContext<CatalogControlState> context,
                CancellationToken ct)
            {
                var manualV1Source = input.ManualV1Source ?? string.Empty;
                var manualV2Source = input.ManualV2Source ?? string.Empty;

                var manualDefinitionActorIdV1 = await context.RuntimeCapabilities.UpsertScriptDefinitionAsync(
                    "manual-catalog-script",
                    "rev-manual-1",
                    manualV1Source,
                    ComputeHash(manualV1Source),
                    "manual-catalog-definition-v1",
                    ct);
                await context.RuntimeCapabilities.PromoteRevisionAsync(
                    "script-catalog",
                    "manual-catalog-script",
                    "rev-manual-1",
                    manualDefinitionActorIdV1,
                    ComputeHash(manualV1Source),
                    "manual-promote-v1-" + context.RunId,
                    ct);

                var manualDefinitionActorIdV2 = await context.RuntimeCapabilities.UpsertScriptDefinitionAsync(
                    "manual-catalog-script",
                    "rev-manual-2",
                    manualV2Source,
                    ComputeHash(manualV2Source),
                    "manual-catalog-definition-v2",
                    ct);
                await context.RuntimeCapabilities.PromoteRevisionAsync(
                    "script-catalog",
                    "manual-catalog-script",
                    "rev-manual-2",
                    manualDefinitionActorIdV2,
                    ComputeHash(manualV2Source),
                    "manual-promote-v2-" + context.RunId,
                    ct);

                var manualRuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                    manualDefinitionActorIdV2,
                    "rev-manual-2",
                    "manual-catalog-runtime-" + context.RunId,
                    ct);
                await context.RuntimeCapabilities.RunScriptInstanceAsync(
                    manualRuntimeId,
                    "manual-catalog-run-" + context.RunId,
                    Any.Pack(new TextNormalizationRequested
                    {
                        CommandId = "manual-v2-" + context.RunId,
                        InputText = "manual",
                    }),
                    "rev-manual-2",
                    manualDefinitionActorIdV2,
                    "manual.catalog.run",
                    ct);

                await context.RuntimeCapabilities.RollbackRevisionAsync(
                    "script-catalog",
                    "manual-catalog-script",
                    "rev-manual-1",
                    "rollback by script capability",
                    "manual-rollback-" + context.RunId,
                    ct);

                context.Emit(new CatalogControlCompleted
                {
                    Current = new CatalogControlState
                    {
                        ManualDefinitionActorIdV1 = manualDefinitionActorIdV1,
                        ManualDefinitionActorIdV2 = manualDefinitionActorIdV2,
                        ManualRuntimeId = manualRuntimeId,
                    },
                });
            }

            private static string ComputeHash(string source)
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source ?? string.Empty));
                return Convert.ToHexString(bytes);
            }
        }
        """;

    public static readonly string InteractionUpsertOrchestratorSource =
        """
        using System;
        using System.Security.Cryptography;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Foundation.Abstractions;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Abstractions.Definitions;

        public sealed class InteractionUpsertOrchestrator : ScriptBehavior<InteractionUpsertState, InteractionUpsertState>
        {
            protected override void Configure(IScriptBehaviorBuilder<InteractionUpsertState, InteractionUpsertState> builder)
            {
                builder
                    .OnCommand<InteractionUpsertRequested>(HandleAsync)
                    .OnEvent<InteractionUpsertCompleted>(
                        apply: static (_, evt, _) => evt.Current,
                        reduce: static (_, evt, _) => evt.Current);
            }

            private static async Task HandleAsync(
                InteractionUpsertRequested input,
                ScriptCommandContext<InteractionUpsertState> context,
                CancellationToken ct)
            {
                var definitionType = input.DefinitionAgentType ?? string.Empty;
                var publishSource = input.PublishSource ?? string.Empty;
                var sendToSource = input.SendtoSource ?? string.Empty;
                var invokeSource = input.InvokeSource ?? string.Empty;

                var aiResponse = await context.RuntimeCapabilities.AskAIAsync("health-check", ct);

                var publishedDefinitionActorId = await context.RuntimeCapabilities.CreateAgentAsync(
                    definitionType,
                    "published-definition-" + context.RunId,
                    ct);
                await context.RuntimeCapabilities.LinkAgentsAsync(context.ActorId, publishedDefinitionActorId, ct);
                await context.RuntimeCapabilities.PublishAsync(
                    new InteractionPublishSignal
                    {
                        Value = "interaction.publish.signal",
                    },
                    TopologyAudience.Children,
                    ct);
                await context.RuntimeCapabilities.UnlinkAgentAsync(publishedDefinitionActorId, ct);

                var sendToDefinitionActorId = await context.RuntimeCapabilities.CreateAgentAsync(
                    definitionType,
                    "sendto-definition-" + context.RunId,
                    ct);
                await context.RuntimeCapabilities.SendToAsync(
                    sendToDefinitionActorId,
                    new UpsertScriptDefinitionRequestedEvent
                    {
                        ScriptId = "interaction-sendto-script",
                        ScriptRevision = "rev-sendto-1",
                        SourceText = sendToSource,
                        SourceHash = ComputeHash(sendToSource),
                    },
                    ct);

                var upsertDefinitionActorId = await context.RuntimeCapabilities.CreateAgentAsync(
                    definitionType,
                    "upsert-definition-" + context.RunId,
                    ct);
                upsertDefinitionActorId = await context.RuntimeCapabilities.UpsertScriptDefinitionAsync(
                    "interaction-invoke-script",
                    "rev-invoke-1",
                    invokeSource,
                    ComputeHash(invokeSource),
                    upsertDefinitionActorId,
                    ct);

                _ = publishSource;

                context.Emit(new InteractionUpsertCompleted
                {
                    Current = new InteractionUpsertState
                    {
                        AiResponseLength = (aiResponse ?? string.Empty).Length.ToString(),
                        PublishedDefinitionActorId = publishedDefinitionActorId,
                        SendtoDefinitionActorId = sendToDefinitionActorId,
                        UpsertDefinitionActorId = upsertDefinitionActorId,
                    },
                });
            }

            private static string ComputeHash(string source)
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source ?? string.Empty));
                return Convert.ToHexString(bytes);
            }
        }
        """;

    public static readonly string OrleansClusterOrchestratorSource =
        """
        using System;
        using System.Security.Cryptography;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Abstractions.Definitions;
        using Google.Protobuf.WellKnownTypes;

        public sealed class OrleansClusterScriptOrchestrator : ScriptBehavior<OrleansClusterState, OrleansClusterState>
        {
            protected override void Configure(IScriptBehaviorBuilder<OrleansClusterState, OrleansClusterState> builder)
            {
                builder
                    .OnCommand<OrleansClusterRequested>(HandleAsync)
                    .OnEvent<OrleansClusterCompleted>(
                        apply: static (_, evt, _) => evt.Current,
                        reduce: static (_, evt, _) => evt.Current);
            }

            private static async Task HandleAsync(
                OrleansClusterRequested input,
                ScriptCommandContext<OrleansClusterState> context,
                CancellationToken ct)
            {
                var workerADefinitionActorId = input.WorkerADefinitionActorId ?? string.Empty;
                var workerBDefinitionActorId = input.WorkerBDefinitionActorId ?? string.Empty;
                var newScriptId = input.NewScriptId ?? string.Empty;
                var newScriptSource = input.NewScriptSource ?? string.Empty;
                var tempARuntimeId = input.TempARuntimeId ?? string.Empty;
                var tempBRuntimeId = input.TempBRuntimeId ?? string.Empty;
                var generatedRuntimeId = input.GeneratedRuntimeId ?? string.Empty;
                var generatedDefinitionActorId = input.GeneratedDefinitionActorId ?? string.Empty;

                tempARuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                    workerADefinitionActorId,
                    "rev-a-1",
                    tempARuntimeId,
                    ct);
                await context.RuntimeCapabilities.RunScriptInstanceAsync(
                    tempARuntimeId,
                    "temp-a-run-" + context.RunId,
                    Any.Pack(new TextNormalizationRequested
                    {
                        CommandId = "temp-a-" + context.RunId,
                        InputText = "temp a",
                    }),
                    "rev-a-1",
                    workerADefinitionActorId,
                    "temp.a.run",
                    ct);

                tempBRuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                    workerBDefinitionActorId,
                    "rev-b-1",
                    tempBRuntimeId,
                    ct);
                await context.RuntimeCapabilities.RunScriptInstanceAsync(
                    tempBRuntimeId,
                    "temp-b-run-" + context.RunId,
                    Any.Pack(new TextNormalizationRequested
                    {
                        CommandId = "temp-b-" + context.RunId,
                        InputText = "temp b",
                    }),
                    "rev-b-1",
                    workerBDefinitionActorId,
                    "temp.b.run",
                    ct);

                generatedDefinitionActorId = await context.RuntimeCapabilities.UpsertScriptDefinitionAsync(
                    newScriptId,
                    "rev-new-1",
                    newScriptSource,
                    ComputeHash(newScriptSource),
                    generatedDefinitionActorId,
                    ct);
                generatedRuntimeId = await context.RuntimeCapabilities.SpawnScriptRuntimeAsync(
                    generatedDefinitionActorId,
                    "rev-new-1",
                    generatedRuntimeId,
                    ct);
                await context.RuntimeCapabilities.RunScriptInstanceAsync(
                    generatedRuntimeId,
                    "generated-run-" + context.RunId,
                    Any.Pack(new TextNormalizationRequested
                    {
                        CommandId = "generated-" + context.RunId,
                        InputText = "generated",
                    }),
                    "rev-new-1",
                    generatedDefinitionActorId,
                    "generated.run",
                    ct);

                context.Emit(new OrleansClusterCompleted
                {
                    Current = new OrleansClusterState
                    {
                        TempARuntimeId = tempARuntimeId,
                        TempBRuntimeId = tempBRuntimeId,
                        GeneratedRuntimeId = generatedRuntimeId,
                        GeneratedDefinitionActorId = generatedDefinitionActorId,
                    },
                });
            }

            private static string ComputeHash(string source)
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source ?? string.Empty));
                return Convert.ToHexString(bytes);
            }
        }
        """;
}
