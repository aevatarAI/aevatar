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
        using Google.Protobuf;
        using Google.Protobuf.WellKnownTypes;

        public sealed class UppercaseBehavior : ScriptBehavior<StringValue, StringValue>
        {
            protected override void Configure(IScriptBehaviorBuilder<StringValue, StringValue> builder)
            {
                builder
                    .OnCommand<StringValue>(HandleCommandAsync)
                    .OnEvent<StringValue>(
                        apply: static (_, evt, _) => new StringValue { Value = evt.Value },
                        reduce: static (_, evt, _) => new StringValue { Value = evt.Value })
                    .OnQuery<Empty, StringValue>(HandleQueryAsync);
            }

            private static Task HandleCommandAsync(
                StringValue command,
                ScriptCommandContext<StringValue> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                context.Emit(new StringValue { Value = (command.Value ?? string.Empty).Trim().ToUpperInvariant() });
                return Task.CompletedTask;
            }

            private static Task<StringValue?> HandleQueryAsync(
                Empty query,
                ScriptQueryContext<StringValue> snapshot,
                CancellationToken ct)
            {
                _ = query;
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<StringValue?>(snapshot.CurrentReadModel == null
                    ? null
                    : new StringValue { Value = snapshot.CurrentReadModel.Value });
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
                        reduce: static (_, evt, _) => evt.Current)
                    .OnQuery<ScriptProfileQueryRequested, ScriptProfileQueryResponded>(HandleQueryAsync);
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
