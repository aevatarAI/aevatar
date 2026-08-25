using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.Metadata;

public sealed class WorkflowDeliveryCurrentStateDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<WorkflowDeliveryCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "studio-workflow-deliveries",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["package"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["acceptance_policy"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["input"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                                    {
                                        ["literals"] = DisabledObject(),
                                    },
                                },
                            },
                        },
                    },
                },
                ["installation"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["acceptance_input"] = DisabledObject(),
                    },
                },
            },
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));

    private static Dictionary<string, object?> DisabledObject() => new(StringComparer.Ordinal)
    {
        ["type"] = "object",
        ["enabled"] = false,
    };
}
