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
        artifact.Contract.StateTypeUrl.Should().Be("type.googleapis.com/google.protobuf.StringValue");
        artifact.Contract.ReadModelTypeUrl.Should().Be("type.googleapis.com/google.protobuf.StringValue");

        var behavior = artifact.CreateBehavior();
        try
        {
            behavior.Should().BeAssignableTo<IScriptBehaviorBridge>();
            behavior.Descriptor.DomainEvents.Keys.Should().ContainSingle("type.googleapis.com/google.protobuf.StringValue");
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
        artifact.Contract.StateTypeUrl.Should().Be("type.googleapis.com/google.protobuf.Int32Value");
        artifact.Contract.ReadModelTypeUrl.Should().Be(Any.Pack(new ScriptProfileReadModel()).TypeUrl);
        artifact.Contract.CommandTypeUrls.Should().ContainSingle(Any.Pack(new ScriptProfileUpdateCommand()).TypeUrl);
        artifact.Contract.DomainEventTypeUrls.Should().ContainSingle(Any.Pack(new ScriptProfileUpdated()).TypeUrl);
        artifact.Contract.QueryTypeUrls.Should().ContainSingle(Any.Pack(new ScriptProfileQueryRequested()).TypeUrl);
        artifact.Contract.QueryResultTypeUrls.Should().ContainKey(Any.Pack(new ScriptProfileQueryRequested()).TypeUrl);
        artifact.Contract.InternalSignalTypeUrls.Should().ContainSingle("type.googleapis.com/google.protobuf.Empty");
        artifact.Contract.StateDescriptorFullName.Should().Be(Int32Value.Descriptor.FullName);
        artifact.Contract.ReadModelDescriptorFullName.Should().Be(ScriptProfileReadModel.Descriptor.FullName);
        artifact.Contract.ProtocolDescriptorSet.Should().NotBeNull();
        artifact.Contract.ProtocolDescriptorSet!.IsEmpty.Should().BeFalse();

        var plan = new ScriptReadModelMaterializationCompiler().GetOrCompile(
            artifact,
            schemaHash: "contract-hash",
            schemaVersion: "3");
        plan.SchemaId.Should().Be("script_profile");
        plan.DocumentFields.Should().Contain(x => x.Path == "search.lookup_key");
        plan.GraphRelations.Should().ContainSingle(x => x.Name == "rel_policy");
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
        using Google.Protobuf.WellKnownTypes;

        public sealed class ContractBehavior : ScriptBehavior<Int32Value, ScriptProfileReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<Int32Value, ScriptProfileReadModel> builder)
            {
                builder
                    .OnCommand<ScriptProfileUpdateCommand>(HandleAsync)
                    .OnSignal<Empty>(HandleAsync)
                    .OnEvent<ScriptProfileUpdated>(
                        apply: static (_, evt, _) => new Int32Value { Value = 1 },
                        reduce: static (_, evt, _) => evt.Current)
                    .OnQuery<ScriptProfileQueryRequested, ScriptProfileQueryResponded>(HandleQueryAsync);
            }

            private static Task HandleAsync(
                ScriptProfileUpdateCommand inbound,
                ScriptCommandContext<Int32Value> context,
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

            private static Task HandleAsync(
                Empty inbound,
                ScriptCommandContext<Int32Value> context,
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
}
