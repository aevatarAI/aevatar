using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions.Definitions;
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
    private readonly IScriptPackageCompiler _compiler;
    private readonly IScriptReadModelSchemaActivationPolicy _schemaActivationPolicy;

    public ScriptDefinitionGAgent(
        IScriptPackageCompiler compiler,
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
        Logger.LogInformation(
            "Script definition upsert received. actor_id={ActorId} script_id={ScriptId} revision={Revision} request_id={RequestId} reply_stream_id={ReplyStreamId}",
            Id,
            evt.ScriptId,
            evt.ScriptRevision,
            evt.RequestId,
            evt.ReplyStreamId);
        try
        {
            var sourceText = evt.SourceText ?? string.Empty;
            var compilation = await _compiler.CompileAsync(
                new ScriptPackageCompilationRequest(
                    evt.ScriptId ?? string.Empty,
                    evt.ScriptRevision ?? string.Empty,
                    sourceText),
                CancellationToken.None);
            try
            {
            if (!compilation.IsSuccess || compilation.ContractManifest == null)
            {
                var diagnostics = compilation.Diagnostics?.ToArray() ?? Array.Empty<string>();
                var failureReason = "Script definition compilation failed: " + string.Join("; ", diagnostics);
                Logger.LogWarning(
                    "Script definition upsert rejected by compilation. actor_id={ActorId} script_id={ScriptId} revision={Revision} diagnostics={Diagnostics}",
                    Id,
                    evt.ScriptId,
                    evt.ScriptRevision,
                    string.Join(" | ", diagnostics));
                await SendCommandFailureIfRequestedAsync(evt, failureReason, diagnostics);
                return;
            }

            var readModelSchema = Any.Pack(new Empty());
            var readModelSchemaHash = string.Empty;
            var readModelSchemaVersion = string.Empty;
            IReadOnlyList<string> readModelSchemaStoreKinds = Array.Empty<string>();
            var hasReadModelSchema = false;
            var extracted = ScriptReadModelDefinitionExtraction.Empty;
            if (ScriptReadModelDefinitionExtractor.TryExtractFromContract(
                    compilation.ContractManifest,
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
                SourceHash = evt.SourceHash ?? string.Empty,
                ReadModelSchema = readModelSchema,
                ReadModelSchemaHash = readModelSchemaHash,
                ReadModelSchemaVersion = readModelSchemaVersion,
                ReadModelSchemaStoreKinds = { readModelSchemaStoreKinds },
            });

            if (!hasReadModelSchema)
            {
                Logger.LogInformation(
                    "Script definition upsert persisted without read model schema. actor_id={ActorId} script_id={ScriptId} revision={Revision}",
                    Id,
                    evt.ScriptId,
                    evt.ScriptRevision);
                await SendCommandResponseIfRequestedAsync(evt);
                return;
            }

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
                RequiresDocumentStore: extracted.Definition.Fields.Count > 0 || extracted.Definition.Indexes.Count > 0,
                RequiresGraphStore: extracted.Definition.Relations.Count > 0,
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
                Logger.LogInformation(
                    "Script definition upsert persisted with validated read model schema. actor_id={ActorId} script_id={ScriptId} revision={Revision} schema_version={SchemaVersion}",
                    Id,
                    evt.ScriptId,
                    evt.ScriptRevision,
                    readModelSchemaVersion);
                await SendCommandResponseIfRequestedAsync(evt);
                return;
            }

            await PersistDomainEventAsync(new ScriptReadModelSchemaActivationFailedEvent
            {
                ScriptId = evt.ScriptId ?? string.Empty,
                ScriptRevision = evt.ScriptRevision ?? string.Empty,
                ReadModelSchemaVersion = readModelSchemaVersion,
                FailureReason = activation.FailureReason,
            });
            Logger.LogInformation(
                "Script definition upsert persisted with schema activation failure. actor_id={ActorId} script_id={ScriptId} revision={Revision} reason={FailureReason}",
                Id,
                evt.ScriptId,
                evt.ScriptRevision,
                activation.FailureReason);
            await SendCommandResponseIfRequestedAsync(evt);
            }
            finally
            {
                await DisposeCompiledDefinitionAsync(compilation.CompiledDefinition);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Script definition upsert failed. actor_id={ActorId} script_id={ScriptId} revision={Revision} request_id={RequestId}",
                Id,
                evt.ScriptId,
                evt.ScriptRevision,
                evt.RequestId);
            await SendCommandFailureIfRequestedAsync(evt, ex.Message, Array.Empty<string>());
            throw;
        }
    }

    [EventHandler]
    public async Task HandleQueryScriptDefinitionSnapshotRequested(QueryScriptDefinitionSnapshotRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return;

        Logger.LogInformation(
            "Script definition query received. actor_id={ActorId} request_id={RequestId} requested_revision={RequestedRevision} active_revision={ActiveRevision} reply_stream_id={ReplyStreamId}",
            Id,
            evt.RequestId,
            evt.RequestedRevision,
            State.Revision,
            evt.ReplyStreamId);

        if (!string.IsNullOrWhiteSpace(evt.RequestedRevision) &&
            !string.Equals(evt.RequestedRevision, State.Revision, StringComparison.Ordinal))
        {
            Logger.LogInformation(
                "Script definition query rejected due to revision mismatch. actor_id={ActorId} request_id={RequestId} requested_revision={RequestedRevision} active_revision={ActiveRevision}",
                Id,
                evt.RequestId,
                evt.RequestedRevision,
                State.Revision);
            await SendQueryResponseAsync(evt.ReplyStreamId, new ScriptDefinitionSnapshotRespondedEvent
            {
                RequestId = evt.RequestId,
                Found = false,
                FailureReason = $"Requested revision `{evt.RequestedRevision}` does not match active revision `{State.Revision}`.",
            });
            return;
        }

        Logger.LogInformation(
            "Script definition query responding. actor_id={ActorId} request_id={RequestId} found={Found} revision={Revision}",
            Id,
            evt.RequestId,
            !string.IsNullOrWhiteSpace(State.SourceText),
            State.Revision);
        await SendQueryResponseAsync(evt.ReplyStreamId, new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = evt.RequestId,
            Found = !string.IsNullOrWhiteSpace(State.SourceText),
            ScriptId = State.ScriptId ?? string.Empty,
            Revision = State.Revision ?? string.Empty,
            SourceText = State.SourceText ?? string.Empty,
            ReadModelSchemaVersion = State.ReadModelSchemaVersion ?? string.Empty,
            ReadModelSchemaHash = State.ReadModelSchemaHash ?? string.Empty,
            FailureReason = string.IsNullOrWhiteSpace(State.SourceText)
                ? "Script source text is empty."
                : string.Empty,
        });
    }

    protected override ScriptDefinitionState TransitionState(ScriptDefinitionState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptDefinitionUpsertedEvent>(ApplyDefinitionUpserted)
            .On<ScriptReadModelSchemaDeclaredEvent>(ApplySchemaDeclared)
            .On<ScriptReadModelSchemaValidatedEvent>(ApplySchemaValidated)
            .On<ScriptReadModelSchemaActivationFailedEvent>(ApplySchemaActivationFailed)
            .OrCurrent();

    private Task SendQueryResponseAsync(
        string replyStreamId,
        ScriptDefinitionSnapshotRespondedEvent response,
        CancellationToken ct = default)
    {
        return EventPublisher.SendToAsync(replyStreamId, response, ct, sourceEnvelope: null);
    }

    private Task SendCommandResponseIfRequestedAsync(UpsertScriptDefinitionRequestedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return Task.CompletedTask;

        return SendCommandResponseAsync(evt.ReplyStreamId, BuildCommandResponse(evt.RequestId));
    }

    private Task SendCommandFailureIfRequestedAsync(
        UpsertScriptDefinitionRequestedEvent evt,
        string failureReason,
        IReadOnlyList<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return Task.CompletedTask;

        var response = new ScriptDefinitionCommandRespondedEvent
        {
            RequestId = evt.RequestId,
            Succeeded = false,
            ScriptId = evt.ScriptId ?? string.Empty,
            Revision = evt.ScriptRevision ?? string.Empty,
            FailureReason = failureReason ?? string.Empty,
        };
        if (diagnostics.Count > 0)
            response.Diagnostics.Add(diagnostics);
        return SendCommandResponseAsync(evt.ReplyStreamId, response);
    }

    private Task SendCommandResponseAsync(
        string replyStreamId,
        ScriptDefinitionCommandRespondedEvent response,
        CancellationToken ct = default)
    {
        return EventPublisher.SendToAsync(replyStreamId, response, ct, sourceEnvelope: null);
    }

    private ScriptDefinitionSnapshotRespondedEvent BuildSnapshotResponse(string requestId) =>
        new()
        {
            RequestId = requestId,
            Found = !string.IsNullOrWhiteSpace(State.SourceText),
            ScriptId = State.ScriptId ?? string.Empty,
            Revision = State.Revision ?? string.Empty,
            SourceText = State.SourceText ?? string.Empty,
            ReadModelSchemaVersion = State.ReadModelSchemaVersion ?? string.Empty,
            ReadModelSchemaHash = State.ReadModelSchemaHash ?? string.Empty,
            FailureReason = string.IsNullOrWhiteSpace(State.SourceText)
                ? "Script source text is empty."
                : string.Empty,
        };

    private ScriptDefinitionCommandRespondedEvent BuildCommandResponse(string requestId) =>
        new()
        {
            RequestId = requestId,
            Succeeded = !string.IsNullOrWhiteSpace(State.SourceText),
            ScriptId = State.ScriptId ?? string.Empty,
            Revision = State.Revision ?? string.Empty,
            FailureReason = string.IsNullOrWhiteSpace(State.SourceText)
                ? "Script source text is empty."
                : string.Empty,
        };

    private static async Task DisposeCompiledDefinitionAsync(IScriptPackageDefinition? definition)
    {
        if (definition is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
            return;
        }

        if (definition is IDisposable disposable)
            disposable.Dispose();
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
