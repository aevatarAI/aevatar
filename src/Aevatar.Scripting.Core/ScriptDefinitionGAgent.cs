using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Schema;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Core;

public sealed class ScriptDefinitionGAgent : GAgentBase<ScriptDefinitionState>
{
    private const string SchemaStatusPending = "pending";
    private const string SchemaStatusDeclared = "declared";
    private const string SchemaStatusValidated = "validated";
    private const string SchemaStatusActivationFailed = "activation_failed";
    private readonly IScriptBehaviorCompiler _compiler;
    private readonly IScriptReadModelSchemaActivationPolicy _schemaActivationPolicy;

    public ScriptDefinitionGAgent(
        IScriptBehaviorCompiler compiler,
        IScriptReadModelSchemaActivationPolicy schemaActivationPolicy)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _schemaActivationPolicy = schemaActivationPolicy ?? throw new ArgumentNullException(nameof(schemaActivationPolicy));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleUpsertScriptDefinitionRequested(UpsertScriptDefinitionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var parsedPackage = ScriptSourcePackageSerializer.DeserializeOrWrapCSharp(evt.SourceText ?? string.Empty);
        var scriptPackage = evt.ScriptPackage?.Clone();
        if (scriptPackage == null || scriptPackage.CsharpSources.Count == 0)
            scriptPackage = ScriptPackageModel.ToPackageSpec(parsedPackage);
        var sourceText = ScriptPackageModel.GetEntrySourceText(scriptPackage);
        var packageHash = string.IsNullOrWhiteSpace(evt.SourceHash)
            ? ScriptPackageModel.ComputePackageHash(scriptPackage)
            : evt.SourceHash;
        var compilation = _compiler.Compile(
            new ScriptBehaviorCompilationRequest(
                evt.ScriptId ?? string.Empty,
                evt.ScriptRevision ?? string.Empty,
                scriptPackage,
                packageHash));
        try
        {
            if (!compilation.IsSuccess || compilation.Artifact == null)
                throw new InvalidOperationException(
                    "Script definition compilation failed: " + string.Join("; ", compilation.Diagnostics));

            var readModelSchema = Any.Pack(new Empty());
            var readModelSchemaHash = string.Empty;
            var readModelSchemaVersion = string.Empty;
            IReadOnlyList<string> readModelSchemaStoreKinds = Array.Empty<string>();
            var hasReadModelSchema = false;
            var extracted = ScriptSchemaDescriptorExtraction.Empty;
            if (ScriptSchemaDescriptorExtractor.TryExtractFromDescriptor(
                    compilation.Artifact.Descriptor,
                    out extracted))
            {
                hasReadModelSchema = true;
                readModelSchema = extracted.SchemaPayload.Clone();
                readModelSchemaHash = extracted.SchemaHash;
                readModelSchemaVersion = extracted.SchemaVersion;
                readModelSchemaStoreKinds = extracted.StoreCapabilities;
            }

            await PersistDomainEventAsync(new ScriptDefinitionUpsertedEvent
            {
                ScriptId = evt.ScriptId ?? string.Empty,
                ScriptRevision = evt.ScriptRevision ?? string.Empty,
                SourceText = sourceText,
                SourceHash = packageHash,
                ReadModelSchema = readModelSchema,
                ReadModelSchemaHash = readModelSchemaHash,
                ReadModelSchemaVersion = readModelSchemaVersion,
                ReadModelSchemaStoreKinds = { readModelSchemaStoreKinds },
                StateTypeUrl = compilation.Artifact.Contract.StateTypeUrl ?? string.Empty,
                ReadModelTypeUrl = compilation.Artifact.Contract.ReadModelTypeUrl ?? string.Empty,
                CommandTypeUrls = { compilation.Artifact.Contract.CommandTypeUrls },
                DomainEventTypeUrls = { compilation.Artifact.Contract.DomainEventTypeUrls },
                InternalSignalTypeUrls = { compilation.Artifact.Contract.InternalSignalTypeUrls },
                ScriptPackage = scriptPackage,
                ProtocolDescriptorSet = compilation.Artifact.Contract.ProtocolDescriptorSet ?? ByteString.Empty,
                StateDescriptorFullName = compilation.Artifact.Contract.StateDescriptorFullName ?? string.Empty,
                ReadModelDescriptorFullName = compilation.Artifact.Contract.ReadModelDescriptorFullName ?? string.Empty,
                RuntimeSemantics = compilation.Artifact.Contract.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec(),
            });

            if (!hasReadModelSchema)
                return;

            await PersistDomainEventAsync(new ScriptReadModelSchemaDeclaredEvent
            {
                ScriptId = evt.ScriptId ?? string.Empty,
                ScriptRevision = evt.ScriptRevision ?? string.Empty,
                ReadModelSchema = readModelSchema,
                ReadModelSchemaHash = readModelSchemaHash,
                ReadModelSchemaVersion = readModelSchemaVersion,
                ReadModelSchemaStoreKinds = { readModelSchemaStoreKinds },
            });

            var activation = _schemaActivationPolicy.ValidateActivation(new ScriptReadModelSchemaActivationRequest(
                RequiresDocumentStore: extracted.RequiresDocumentStore,
                RequiresGraphStore: extracted.RequiresGraphStore,
                DeclaredProviderHints: extracted.StoreCapabilities));
            if (activation.IsActivated)
            {
                await PersistDomainEventAsync(new ScriptReadModelSchemaValidatedEvent
                {
                    ScriptId = evt.ScriptId ?? string.Empty,
                    ScriptRevision = evt.ScriptRevision ?? string.Empty,
                    ReadModelSchemaVersion = readModelSchemaVersion,
                    ValidatedStoreKinds =
                {
                    activation.ValidatedStoreKinds
                        .Select(x => x.ToString())
                        .ToArray(),
                },
                });
                return;
            }

            await PersistDomainEventAsync(new ScriptReadModelSchemaActivationFailedEvent
            {
                ScriptId = evt.ScriptId ?? string.Empty,
                ScriptRevision = evt.ScriptRevision ?? string.Empty,
                ReadModelSchemaVersion = readModelSchemaVersion,
                FailureReason = activation.FailureReason,
            });
        }
        finally
        {
            await DisposeCompiledArtifactAsync(compilation.Artifact);
        }
    }

    protected override ScriptDefinitionState TransitionState(ScriptDefinitionState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptDefinitionUpsertedEvent>(ApplyDefinitionUpserted)
            .On<ScriptReadModelSchemaDeclaredEvent>(ApplySchemaDeclared)
            .On<ScriptReadModelSchemaValidatedEvent>(ApplySchemaValidated)
            .On<ScriptReadModelSchemaActivationFailedEvent>(ApplySchemaActivationFailed)
            .OrCurrent();

    private static async Task DisposeCompiledArtifactAsync(ScriptBehaviorArtifact? artifact)
    {
        if (artifact != null)
            await artifact.DisposeAsync();
    }

    private static ScriptDefinitionState ApplyDefinitionUpserted(
        ScriptDefinitionState state,
        ScriptDefinitionUpsertedEvent evt)
    {
        var next = state.Clone();
        next.ScriptId = evt.ScriptId ?? string.Empty;
        next.Revision = evt.ScriptRevision ?? string.Empty;
        next.SourceText = evt.SourceText ?? string.Empty;
        next.SourceHash = evt.SourceHash ?? string.Empty;
        next.ReadModelSchema = evt.ReadModelSchema?.Clone() ?? Any.Pack(new Empty());
        next.ReadModelSchemaHash = evt.ReadModelSchemaHash ?? string.Empty;
        next.ReadModelSchemaVersion = evt.ReadModelSchemaVersion ?? string.Empty;
        next.ReadModelSchemaStoreKinds.Clear();
        next.ReadModelSchemaStoreKinds.Add(evt.ReadModelSchemaStoreKinds);
        next.StateTypeUrl = evt.StateTypeUrl ?? string.Empty;
        next.ReadModelTypeUrl = evt.ReadModelTypeUrl ?? string.Empty;
        next.CommandTypeUrls.Clear();
        next.CommandTypeUrls.Add(evt.CommandTypeUrls);
        next.DomainEventTypeUrls.Clear();
        next.DomainEventTypeUrls.Add(evt.DomainEventTypeUrls);
        next.InternalSignalTypeUrls.Clear();
        next.InternalSignalTypeUrls.Add(evt.InternalSignalTypeUrls);
        next.ScriptPackage = evt.ScriptPackage?.Clone() ?? new ScriptPackageSpec();
        next.ProtocolDescriptorSet = evt.ProtocolDescriptorSet;
        next.StateDescriptorFullName = evt.StateDescriptorFullName ?? string.Empty;
        next.ReadModelDescriptorFullName = evt.ReadModelDescriptorFullName ?? string.Empty;
        next.RuntimeSemantics = evt.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec();
        next.ReadModelSchemaStatus = next.ReadModelSchema == null || next.ReadModelSchema.Is(Empty.Descriptor)
            ? string.Empty
            : SchemaStatusPending;
        next.ReadModelSchemaFailureReason = string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = evt.ScriptRevision ?? string.Empty;
        return next;
    }

    private static ScriptDefinitionState ApplySchemaDeclared(
        ScriptDefinitionState state,
        ScriptReadModelSchemaDeclaredEvent evt)
    {
        var next = state.Clone();
        next.ReadModelSchemaStatus = SchemaStatusDeclared;
        next.ReadModelSchemaFailureReason = string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(evt.ScriptRevision ?? string.Empty, ":schema-declared");
        return next;
    }

    private static ScriptDefinitionState ApplySchemaValidated(
        ScriptDefinitionState state,
        ScriptReadModelSchemaValidatedEvent evt)
    {
        var next = state.Clone();
        next.ReadModelSchemaStatus = SchemaStatusValidated;
        next.ReadModelSchemaFailureReason = string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(evt.ScriptRevision ?? string.Empty, ":schema-validated");
        return next;
    }

    private static ScriptDefinitionState ApplySchemaActivationFailed(
        ScriptDefinitionState state,
        ScriptReadModelSchemaActivationFailedEvent evt)
    {
        var next = state.Clone();
        next.ReadModelSchemaStatus = SchemaStatusActivationFailed;
        next.ReadModelSchemaFailureReason = evt.FailureReason ?? string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(evt.ScriptRevision ?? string.Empty, ":schema-activation-failed");
        return next;
    }

}
