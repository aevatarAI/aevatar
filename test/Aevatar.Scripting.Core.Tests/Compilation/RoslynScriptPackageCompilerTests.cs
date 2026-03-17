using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Compilation;

public class RoslynScriptBehaviorCompilerTests
{
    [Fact]
    public void Compile_ShouldReject_WhenSandboxPolicyFails()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var request = new ScriptBehaviorCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-1",
            Source: "Task.Run(() => 1);");

        var result = compiler.Compile(request);

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().NotBeEmpty();
        result.Artifact.Should().BeNull();
    }

    [Fact]
    public void Compile_ShouldReject_WhenSourceHasSyntaxError()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var request = new ScriptBehaviorCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-2",
            Source: "if (true {");

        var result = compiler.Compile(request);

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().NotBeEmpty();
        result.Artifact.Should().BeNull();
    }

    [Fact]
    public async Task Compile_ShouldCreateArtifact_WhenSourceIsValid()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var result = compiler.Compile(new ScriptBehaviorCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-3",
            Source: ScriptSources.UppercaseBehavior));

        result.IsSuccess.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
        result.Artifact.Should().NotBeNull();

        await using var artifact = result.Artifact!;
        artifact.ScriptId.Should().Be("script-1");
        artifact.Revision.Should().Be("rev-3");
        artifact.Contract.StateTypeUrl.Should().Be(Any.Pack(new SimpleTextState()).TypeUrl);
        artifact.Contract.ReadModelTypeUrl.Should().Be(Any.Pack(new SimpleTextReadModel()).TypeUrl);

        var behavior = artifact.CreateBehavior();
        try
        {
            behavior.Should().BeAssignableTo<IScriptBehaviorBridge>();
            behavior.Descriptor.DomainEvents.Keys.Should().ContainSingle(Any.Pack(new SimpleTextEvent()).TypeUrl);
        }
        finally
        {
            if (behavior is IDisposable disposable)
                disposable.Dispose();
        }
    }

    [Fact]
    public void Compile_ShouldReject_WhenBehaviorInterfaceIsOnlyMentionedButNotImplemented()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var result = compiler.Compile(new ScriptBehaviorCompilationRequest(
            ScriptId: "script-invalid-runtime",
            Revision: "rev-invalid-runtime",
            Source: """
                    // IScriptBehaviorBridge should be implemented by a concrete behavior class.
                    using System.Threading.Tasks;
                    using Aevatar.Scripting.Abstractions.Behaviors;

                    public sealed class InvalidBehaviorScript
                    {
                        public Task<string> DispatchAsync() => Task.FromResult("invalid");
                    }
                    """));

        result.IsSuccess.Should().BeFalse();
        result.Artifact.Should().BeNull();
        result.Diagnostics.Should().Contain(x => x.Contains("IScriptBehaviorBridge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compile_ShouldExtractContractDefinition_WhenBehaviorDeclaresTypedContract()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var result = compiler.Compile(new ScriptBehaviorCompilationRequest(
            ScriptId: "script-contract",
            Revision: "rev-contract",
            Source: ContractBehaviorSource));

        result.IsSuccess.Should().BeTrue();
        result.Artifact.Should().NotBeNull();

        await using var artifact = result.Artifact!;
        artifact.Contract.StateTypeUrl.Should().Be(Any.Pack(new ScriptProfileState()).TypeUrl);
        artifact.Contract.ReadModelTypeUrl.Should().Be(Any.Pack(new ScriptProfileReadModel()).TypeUrl);
        artifact.Contract.CommandTypeUrls.Should().ContainSingle(Any.Pack(new ScriptProfileUpdateCommand()).TypeUrl);
        artifact.Contract.DomainEventTypeUrls.Should().ContainSingle(Any.Pack(new ScriptProfileUpdated()).TypeUrl);
        artifact.Contract.InternalSignalTypeUrls.Should().ContainSingle(Any.Pack(new SimpleTextSignal()).TypeUrl);
        artifact.Contract.StateDescriptorFullName.Should().Be(ScriptProfileState.Descriptor.FullName);
        artifact.Contract.ReadModelDescriptorFullName.Should().Be(ScriptProfileReadModel.Descriptor.FullName);
        artifact.Contract.ProtocolDescriptorSet.Should().NotBeNull();
        artifact.Contract.ProtocolDescriptorSet!.IsEmpty.Should().BeFalse();
        artifact.Contract.RuntimeSemantics.Should().NotBeNull();
        artifact.Contract.RuntimeSemantics!.Messages.Should().Contain(x =>
            x.TypeUrl == Any.Pack(new ScriptProfileUpdateCommand()).TypeUrl &&
            x.Kind == ScriptMessageKind.Command);
        artifact.Contract.RuntimeSemantics.Messages.Should().Contain(x =>
            x.TypeUrl == Any.Pack(new ScriptProfileUpdated()).TypeUrl &&
            x.Kind == ScriptMessageKind.DomainEvent &&
            x.Projectable);

        var plan = new ScriptReadModelMaterializationCompiler().GetOrCompile(
            artifact,
            schemaHash: "contract-hash",
            schemaVersion: "3");
        plan.SchemaId.Should().Be("script_profile");
        plan.DocumentFields.Should().Contain(x => x.Path == "search.lookup_key");
        plan.GraphRelations.Should().ContainSingle(x => x.Name == "rel_policy");
    }

    [Fact]
    public void Compile_ShouldReject_WhenLocalProtoMessageMissesRuntimeOptions()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var result = compiler.Compile(new ScriptBehaviorCompilationRequest(
            ScriptId: "script-missing-runtime-options",
            Revision: "rev-missing-runtime-options",
            Package: CreatePackageMissingRuntimeOptions()));

        result.IsSuccess.Should().BeFalse();
        result.Artifact.Should().BeNull();
        result.Diagnostics.Should().Contain(x =>
            x.Contains("Runtime semantics are missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Compile_ShouldReject_WhenCommandDeclaresProjectableFlag()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var result = compiler.Compile(new ScriptBehaviorCompilationRequest(
            ScriptId: "script-projectable-command",
            Revision: "rev-projectable-command",
            Package: CreateInvalidRuntimePackage(
                commandRuntimeOptions: """
                                       kind: SCRIPTING_MESSAGE_KIND_COMMAND
                                       projectable: true
                                       command_id_field: "command_id"
                                       """)));

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().Contain(x =>
            x.Contains("projectable = true", StringComparison.Ordinal));
    }

    [Fact]
    public void Compile_ShouldReject_WhenIdentityFieldDoesNotExist()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var result = compiler.Compile(new ScriptBehaviorCompilationRequest(
            ScriptId: "script-missing-field",
            Revision: "rev-missing-field",
            Package: CreateInvalidRuntimePackage(
                commandRuntimeOptions: """
                                       kind: SCRIPTING_MESSAGE_KIND_COMMAND
                                       command_id_field: "missing_command_id"
                                       """)));

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().Contain(x =>
            x.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Compile_ShouldReject_WhenIdentityFieldIsRepeated()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var result = compiler.Compile(new ScriptBehaviorCompilationRequest(
            ScriptId: "script-repeated-field",
            Revision: "rev-repeated-field",
            Package: CreateInvalidRuntimePackage(
                commandRuntimeOptions: """
                                       kind: SCRIPTING_MESSAGE_KIND_COMMAND
                                       command_id_field: "tags"
                                       """,
                extraCommandFields: "repeated string tags = 2;")));

        result.IsSuccess.Should().BeFalse();
        result.Diagnostics.Should().Contain(x =>
            x.Contains("only singular scalar fields are supported", StringComparison.Ordinal));
    }

    private const string ContractBehaviorSource =
        """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Core.Tests.Messages;

        public sealed class ContractBehavior : ScriptBehavior<ScriptProfileState, ScriptProfileReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ScriptProfileState, ScriptProfileReadModel> builder)
            {
                builder
                    .OnCommand<ScriptProfileUpdateCommand>(HandleAsync)
                    .OnSignal<SimpleTextSignal>(HandleSignalAsync)
                    .OnEvent<ScriptProfileUpdated>(
                        apply: static (state, evt, _) => new ScriptProfileState
                        {
                            CommandCount = (state?.CommandCount ?? 0) + 1,
                            ActorId = evt.Current?.ActorId ?? string.Empty,
                            PolicyId = evt.Current?.PolicyId ?? string.Empty,
                            LastCommandId = evt.CommandId ?? string.Empty,
                            InputText = evt.Current?.InputText ?? string.Empty,
                            NormalizedText = evt.Current?.NormalizedText ?? string.Empty,
                            Tags = { evt.Current == null ? global::System.Array.Empty<string>() : (global::System.Collections.Generic.IEnumerable<string>)evt.Current.Tags },
                        })
                    .ProjectState(static (state, _) => state == null
                        ? new ScriptProfileReadModel()
                        : new ScriptProfileReadModel
                        {
                            HasValue = true,
                            ActorId = state.ActorId,
                            PolicyId = state.PolicyId,
                            LastCommandId = state.LastCommandId,
                            InputText = state.InputText,
                            NormalizedText = state.NormalizedText,
                            Search = new ScriptProfileSearchIndex
                            {
                                LookupKey = $"{state.ActorId}:{state.PolicyId}".ToLowerInvariant(),
                                SortKey = state.NormalizedText ?? string.Empty,
                            },
                            Refs = new ScriptProfileDocumentRef
                            {
                                ActorId = state.ActorId ?? string.Empty,
                                PolicyId = state.PolicyId ?? string.Empty,
                            },
                            Tags = { state.Tags },
                        });
            }

            private static Task HandleAsync(
                ScriptProfileUpdateCommand inbound,
                ScriptCommandContext<ScriptProfileState> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                context.Emit(new ScriptProfileUpdated
                {
                    CommandId = inbound.CommandId ?? string.Empty,
                    Current = new ScriptProfileReadModel
                    {
                        HasValue = true,
                        ActorId = inbound.ActorId ?? string.Empty,
                        PolicyId = inbound.PolicyId ?? string.Empty,
                        LastCommandId = inbound.CommandId ?? string.Empty,
                        InputText = inbound.InputText ?? string.Empty,
                        NormalizedText = (inbound.InputText ?? string.Empty).Trim().ToUpperInvariant(),
                        Search = new ScriptProfileSearchIndex
                        {
                            LookupKey = $"{inbound.ActorId}:{inbound.PolicyId}".ToLowerInvariant(),
                            SortKey = (inbound.InputText ?? string.Empty).Trim().ToUpperInvariant(),
                        },
                        Refs = new ScriptProfileDocumentRef
                        {
                            ActorId = inbound.ActorId ?? string.Empty,
                            PolicyId = inbound.PolicyId ?? string.Empty,
                        },
                    },
                });
                return Task.CompletedTask;
            }

            private static Task HandleSignalAsync(
                SimpleTextSignal inbound,
                ScriptCommandContext<ScriptProfileState> context,
                CancellationToken ct)
            {
                _ = inbound;
                ct.ThrowIfCancellationRequested();
                context.Emit(new ScriptProfileUpdated
                {
                    CommandId = context.CommandId,
                    Current = new ScriptProfileReadModel
                    {
                        HasValue = true,
                        ActorId = context.ActorId,
                        PolicyId = "signal",
                        LastCommandId = context.CommandId,
                        InputText = context.MessageType,
                        NormalizedText = context.MessageType,
                        Search = new ScriptProfileSearchIndex
                        {
                            LookupKey = $"{context.ActorId}:signal".ToLowerInvariant(),
                            SortKey = context.MessageType,
                        },
                        Refs = new ScriptProfileDocumentRef
                        {
                            ActorId = context.ActorId,
                            PolicyId = "signal",
                        },
                    },
                });
                return Task.CompletedTask;
            }

            private static Task<ScriptProfileQueryResponded?> HandleQueryAsync(
                ScriptProfileQueryRequested query,
                ScriptQueryContext<ScriptProfileReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ScriptProfileQueryResponded?>(new ScriptProfileQueryResponded
                {
                    RequestId = query.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new ScriptProfileReadModel(),
                });
            }
        }
        """;

    private static ScriptSourcePackage CreateInvalidRuntimePackage(
        string? commandRuntimeOptions = null,
        string? extraCommandFields = null,
        string? resultRuntimeOptions = null,
        string? queryResultFullName = null)
    {
        const string behaviorSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Aevatar.Scripting.Abstractions.Behaviors;
            using Dynamic.InvalidRuntime;

            public sealed class InvalidRuntimeBehavior : ScriptBehavior<InvalidRuntimeState, InvalidRuntimeReadModel>
            {
                protected override void Configure(IScriptBehaviorBuilder<InvalidRuntimeState, InvalidRuntimeReadModel> builder)
                {
                    builder
                        .OnCommand<InvalidRuntimeCommand>(HandleAsync)
                        .OnEvent<InvalidRuntimeUpdated>(
                            apply: static (_, evt, _) => evt.Current == null
                                ? new InvalidRuntimeState()
                                : new InvalidRuntimeState { LastCommandId = evt.CommandId ?? string.Empty })
                        .ProjectState(static (state, _) => new InvalidRuntimeReadModel
                        {
                            LastCommandId = state?.LastCommandId ?? string.Empty,
                        });
                }

                private static Task HandleAsync(
                    InvalidRuntimeCommand command,
                    ScriptCommandContext<InvalidRuntimeState> context,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    context.Emit(new InvalidRuntimeUpdated
                    {
                        CommandId = command.CommandId ?? string.Empty,
                        Current = new InvalidRuntimeReadModel
                        {
                            LastCommandId = command.CommandId ?? string.Empty,
                        },
                    });
                    return Task.CompletedTask;
                }

                private static Task<InvalidRuntimeQueryResponded?> HandleQueryAsync(
                    InvalidRuntimeQueryRequested query,
                    ScriptQueryContext<InvalidRuntimeReadModel> snapshot,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<InvalidRuntimeQueryResponded?>(new InvalidRuntimeQueryResponded
                    {
                        RequestId = query.RequestId ?? string.Empty,
                        Current = snapshot.CurrentReadModel ?? new InvalidRuntimeReadModel(),
                    });
                }
            }
            """;

        var commandOptions = string.IsNullOrWhiteSpace(commandRuntimeOptions)
            ? """
              kind: SCRIPTING_MESSAGE_KIND_COMMAND
              command_id_field: "command_id"
              """
            : commandRuntimeOptions;
        var queryOptions = string.IsNullOrWhiteSpace(queryResultFullName)
            ? "result_full_name: \"dynamic.invalidruntime.InvalidRuntimeQueryResponded\""
            : $"result_full_name: \"{queryResultFullName}\"";
        var resultOptions = string.IsNullOrWhiteSpace(resultRuntimeOptions)
            ? """
              kind: SCRIPTING_MESSAGE_KIND_QUERY_RESULT
              read_model_scope: "dynamic.invalidruntime.InvalidRuntimeReadModel"
              """
            : resultRuntimeOptions;
        var extraFields = string.IsNullOrWhiteSpace(extraCommandFields)
            ? string.Empty
            : Environment.NewLine + "  " + extraCommandFields;
        var protoSource =
            $$"""
            syntax = "proto3";

            package dynamic.invalidruntime;

            option csharp_namespace = "Dynamic.InvalidRuntime";

            import "scripting_schema_options.proto";
            import "scripting_runtime_options.proto";

            message InvalidRuntimeState {
              string last_command_id = 1;
            }

            message InvalidRuntimeReadModel {
              option (aevatar.scripting.schema.scripting_read_model) = {
                schema_id: "invalid_runtime"
                schema_version: "1"
                store_kinds: "document"
              };
              string last_command_id = 1 [(aevatar.scripting.schema.scripting_field) = { storage_type: "keyword" }];
            }

            message InvalidRuntimeCommand {
              option (aevatar.scripting.runtime.scripting_runtime) = {
            {{commandOptions}}
              };
              string command_id = 1;{{extraFields}}
            }

            message InvalidRuntimeUpdated {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_DOMAIN_EVENT
                projectable: true
                replay_safe: true
                command_id_field: "command_id"
                read_model_scope: "dynamic.invalidruntime.InvalidRuntimeReadModel"
              };
              string command_id = 1;
              InvalidRuntimeReadModel current = 2;
            }

            message InvalidRuntimeQueryRequested {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_REQUEST
                read_model_scope: "dynamic.invalidruntime.InvalidRuntimeReadModel"
              };
              option (aevatar.scripting.runtime.scripting_query) = {
                {{queryOptions}}
              };
              string request_id = 1;
            }

            message InvalidRuntimeQueryResponded {
              option (aevatar.scripting.runtime.scripting_runtime) = {
            {{resultOptions}}
              };
              string request_id = 1;
              InvalidRuntimeReadModel current = 2;
            }
            """;

        return new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            [new ScriptSourceFile("Behavior.cs", behaviorSource)],
            [new ScriptSourceFile("invalid_runtime.proto", protoSource)],
            "InvalidRuntimeBehavior");
    }

    private static ScriptSourcePackage CreatePackageMissingRuntimeOptions()
    {
        const string behaviorSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Aevatar.Scripting.Abstractions.Behaviors;
            using Dynamic.MissingRuntime;

            public sealed class MissingRuntimeBehavior : ScriptBehavior<MissingRuntimeState, MissingRuntimeReadModel>
            {
                protected override void Configure(IScriptBehaviorBuilder<MissingRuntimeState, MissingRuntimeReadModel> builder)
                {
                    builder.OnCommand<MissingRuntimeCommand>(HandleAsync);
                }

                private static Task HandleAsync(
                    MissingRuntimeCommand command,
                    ScriptCommandContext<MissingRuntimeState> context,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }
            }
            """;

        const string protoSource =
            """
            syntax = "proto3";

            package dynamic.missingruntime;

            option csharp_namespace = "Dynamic.MissingRuntime";

            import "scripting_schema_options.proto";

            message MissingRuntimeState {
              string value = 1;
            }

            message MissingRuntimeReadModel {
              option (aevatar.scripting.schema.scripting_read_model) = {
                schema_id: "missing_runtime"
                schema_version: "1"
                store_kinds: "document"
              };
              string value = 1 [(aevatar.scripting.schema.scripting_field) = { storage_type: "keyword" }];
            }

            message MissingRuntimeCommand {
              string command_id = 1;
            }
            """;

        return new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            [new ScriptSourceFile("Behavior.cs", behaviorSource)],
            [new ScriptSourceFile("missing_runtime.proto", protoSource)],
            "MissingRuntimeBehavior");
    }
}
