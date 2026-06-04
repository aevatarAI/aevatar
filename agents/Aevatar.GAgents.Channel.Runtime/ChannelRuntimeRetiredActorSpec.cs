using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Retired-actor declaration for the channel-bot-registration surface.
/// </summary>
public sealed class ChannelRuntimeRetiredActorSpec : RetiredActorSpec
{
    public override string SpecId => "channel-runtime";

    // Retire only the legacy actor body left by the deleted Aevatar.GAgents.ChannelRuntime
    // assembly. The durable materialization scope MUST NOT be a retired target: its runtime
    // kind is derived from the context's simple type name, so the legacy and current
    // ChannelBotRegistrationMaterializationContext collapse to the same
    // "projection.materialization-scope.channel-bot-registration-materialization-context"
    // kind. Retiring it would destroy the live projection scope on every startup cleanup
    // pass and leave the channel-bot-registration read model un-materialized (#1763 regression).
    public override IReadOnlyList<RetiredActorTarget> Targets { get; } =
    [
        new(
            ChannelBotRegistrationGAgent.WellKnownId,
            ["channel-runtime.channel-bot-registration"],
            CleanupReadModels: true),
    ];

    public override Task DeleteReadModelsForActorAsync(
        IServiceProvider services,
        string actorId,
        CancellationToken ct) =>
        RetiredActorReadModelHelpers.DeleteByActorAsync<ChannelBotRegistrationDocument>(
            services, actorId, ct);
}

/// <summary>
/// Shared paged "delete documents whose <see cref="IProjectionReadModel.ActorId"/>
/// equals X" helper, used by every module's <see cref="IRetiredActorSpec"/>
/// implementation. The reader/writer pair is resolved softly — projection stores
/// not registered in the host become a no-op.
/// </summary>
public static class RetiredActorReadModelHelpers
{
    private const int DefaultPageSize = 500;

    public static async Task DeleteByActorAsync<TReadModel>(
        IServiceProvider services,
        string actorId,
        CancellationToken ct)
        where TReadModel : class, IProjectionReadModel
    {
        var reader = services.GetService<IProjectionDocumentReader<TReadModel, string>>();
        var writer = services.GetService<IProjectionWriteDispatcher<TReadModel>>();
        if (reader == null || writer == null)
            return;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var result = await reader.QueryAsync(
                new ProjectionDocumentQuery
                {
                    Cursor = cursor,
                    Take = DefaultPageSize,
                    Filters =
                    [
                        new ProjectionDocumentFilter
                        {
                            FieldPath = nameof(IProjectionReadModel.ActorId),
                            Operator = ProjectionDocumentFilterOperator.Eq,
                            Value = ProjectionDocumentValue.FromString(actorId),
                        },
                    ],
                },
                ct).ConfigureAwait(false);

            foreach (var item in result.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Id))
                    ids.Add(item.Id);
            }

            cursor = result.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        foreach (var id in ids)
            await writer.DeleteAsync(id, ct).ConfigureAwait(false);
    }
}
