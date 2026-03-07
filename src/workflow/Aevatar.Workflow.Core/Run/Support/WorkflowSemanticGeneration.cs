using System.Globalization;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Core;

internal static class WorkflowSemanticGeneration
{
    public static int Next(int current) =>
        current >= int.MaxValue - 1 ? 1 : current + 1;

    public static bool Matches(EventEnvelope envelope, int expectedGeneration)
    {
        if (expectedGeneration <= 0)
            return false;

        if (envelope.Metadata == null ||
            !envelope.Metadata.TryGetValue("workflow.semantic_generation", out var rawGeneration) ||
            !int.TryParse(rawGeneration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actualGeneration))
        {
            return false;
        }

        return actualGeneration == expectedGeneration;
    }
}
