using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Integration.Tests;

internal static class ScriptingCommandEnvelopeTestKit
{
    public static readonly string UppercaseBehaviorSource =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;

        public sealed class IntegrationUppercaseBehavior : ScriptBehavior<TextNormalizationReadModel, TextNormalizationReadModel>
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
                context.Emit(new TextNormalizationCompleted
                {
                    CommandId = inbound.CommandId ?? string.Empty,
                    Current = new TextNormalizationReadModel
                    {
                        HasValue = true,
                        LastCommandId = inbound.CommandId ?? string.Empty,
                        InputText = inbound.InputText ?? string.Empty,
                        NormalizedText = (inbound.InputText ?? string.Empty).Trim().ToUpperInvariant(),
                        Lookup = new TextNormalizationLookup
                        {
                            Normalized = (inbound.InputText ?? string.Empty).Trim().ToUpperInvariant(),
                        },
                        Refs = new TextNormalizationRefs
                        {
                            ProfileId = inbound.CommandId ?? string.Empty,
                        },
                    },
                });
                return Task.CompletedTask;
            }

            private static Task<TextNormalizationQueryResponded?> HandleQueryAsync(
                TextNormalizationQueryRequested queryPayload,
                ScriptQueryContext<TextNormalizationReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<TextNormalizationQueryResponded?>(new TextNormalizationQueryResponded
                {
                    RequestId = queryPayload.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new TextNormalizationReadModel(),
                });
            }
        }
        """;

    public static readonly string UppercaseBehaviorHash = ComputeSourceHash(UppercaseBehaviorSource);

    public static string ComputeSourceHash(string source)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
