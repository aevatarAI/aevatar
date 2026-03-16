using Aevatar.Scripting.Core.Tests.Messages;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Scripting.Core.Tests;

internal static class ScriptSources
{
    public static readonly string UppercaseBehavior =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Core.Tests.Messages;

        public sealed class UppercaseBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
            {
                builder
                    .OnCommand<SimpleTextCommand>(HandleCommandAsync)
                    .OnEvent<SimpleTextEvent>(
                        apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty },
                        project: static (state, _, _) => new SimpleTextReadModel
                        {
                            HasValue = !string.IsNullOrWhiteSpace(state?.Value),
                            Value = state?.Value ?? string.Empty,
                        });
            }

            private static Task HandleCommandAsync(
                SimpleTextCommand command,
                ScriptCommandContext<SimpleTextState> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                context.Emit(new SimpleTextEvent
                {
                    CommandId = command.CommandId ?? string.Empty,
                    Current = new SimpleTextReadModel
                    {
                        HasValue = true,
                        Value = (command.Value ?? string.Empty).Trim().ToUpperInvariant(),
                    },
                });
                return Task.CompletedTask;
            }

            private static Task<SimpleTextQueryResponded?> HandleQueryAsync(
                SimpleTextQueryRequested query,
                ScriptQueryContext<SimpleTextReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<SimpleTextQueryResponded?>(new SimpleTextQueryResponded
                {
                    RequestId = query.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new SimpleTextReadModel(),
                });
            }
        }
        """;

    public static readonly string UppercaseBehaviorHash = ComputeSourceHash(UppercaseBehavior);

    public static readonly string StructuredProfileBehavior =
        """
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Core.Tests.Messages;

        public sealed class StructuredProfileBehavior : ScriptBehavior<ScriptProfileState, ScriptProfileReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ScriptProfileState, ScriptProfileReadModel> builder)
            {
                builder
                    .OnCommand<ScriptProfileUpdateCommand>(HandleCommandAsync)
                    .OnEvent<ScriptProfileUpdated>(
                        apply: static (state, evt, _) => new ScriptProfileState
                        {
                            CommandCount = (state?.CommandCount ?? 0) + 1,
                            LastCommandId = evt.CommandId ?? string.Empty,
                            NormalizedText = evt.Current?.NormalizedText ?? string.Empty,
                        },
                        project: static (_, evt, _) => evt.Current);
            }

            private static Task HandleCommandAsync(
                ScriptProfileUpdateCommand command,
                ScriptCommandContext<ScriptProfileState> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var normalized = (command.InputText ?? string.Empty).Trim().ToUpperInvariant();
                var tags = command.Tags
                    .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(static tag => tag.Trim().ToLowerInvariant())
                    .Distinct(System.StringComparer.Ordinal)
                    .OrderBy(static tag => tag, System.StringComparer.Ordinal)
                    .ToArray();
                var evt = new ScriptProfileUpdated
                {
                    CommandId = command.CommandId ?? string.Empty,
                    Current = new ScriptProfileReadModel
                    {
                        HasValue = true,
                        ActorId = command.ActorId ?? string.Empty,
                        PolicyId = command.PolicyId ?? string.Empty,
                        LastCommandId = command.CommandId ?? string.Empty,
                        InputText = command.InputText ?? string.Empty,
                        NormalizedText = normalized,
                        Search = new ScriptProfileSearchIndex
                        {
                            LookupKey = $"{command.ActorId}:{command.PolicyId}".ToLowerInvariant(),
                            SortKey = normalized,
                        },
                        Refs = new ScriptProfileDocumentRef
                        {
                            ActorId = command.ActorId ?? string.Empty,
                            PolicyId = command.PolicyId ?? string.Empty,
                        },
                    },
                };
                evt.Current.Tags.AddRange(tags);
                context.Emit(evt);
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

    public static readonly string StructuredProfileBehaviorHash = ComputeSourceHash(StructuredProfileBehavior);

    public static readonly string UppercaseStateTypeUrl = Any.Pack(new SimpleTextState()).TypeUrl;
    public static readonly string UppercaseReadModelTypeUrl = Any.Pack(new SimpleTextReadModel()).TypeUrl;
    public static readonly string UppercaseCommandTypeUrl = Any.Pack(new SimpleTextCommand()).TypeUrl;
    public static readonly string UppercaseSignalTypeUrl = Any.Pack(new SimpleTextSignal()).TypeUrl;
    public static readonly string UppercaseEventTypeUrl = Any.Pack(new SimpleTextEvent()).TypeUrl;
    public static readonly string UppercaseQueryTypeUrl = Any.Pack(new SimpleTextQueryRequested()).TypeUrl;
    public static readonly string UppercaseQueryResultTypeUrl = Any.Pack(new SimpleTextQueryResponded()).TypeUrl;

    public static readonly string StructuredProfileStateTypeUrl = Any.Pack(new ScriptProfileState()).TypeUrl;
    public static readonly string StructuredProfileReadModelTypeUrl = Any.Pack(new ScriptProfileReadModel()).TypeUrl;
    public static readonly string StructuredProfileCommandTypeUrl = Any.Pack(new ScriptProfileUpdateCommand()).TypeUrl;
    public static readonly string StructuredProfileEventTypeUrl = Any.Pack(new ScriptProfileUpdated()).TypeUrl;
    public static readonly string StructuredProfileQueryTypeUrl = Any.Pack(new ScriptProfileQueryRequested()).TypeUrl;
    public static readonly string StructuredProfileQueryResultTypeUrl = Any.Pack(new ScriptProfileQueryResponded()).TypeUrl;

    private static string ComputeSourceHash(string source)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
