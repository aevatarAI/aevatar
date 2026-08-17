using System.Text;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Tools;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatDurableToolPresentation
{
    internal const int MaxDescriptorBytes = 16 * 1024;
    internal const int MaxNameBytes = 512;
    internal const int MaxDescriptionBytes = 2 * 1024;
    internal const int MaxUriBytes = 2 * 1024;

    public static ToolPresentationDescriptor? Snapshot(
        ToolPresentationDescriptor? presentation,
        string? invocationName)
    {
        if (presentation is null)
            return null;

        var boundedInvocationName = TruncateUtf8(invocationName, MaxNameBytes);
        var descriptor = ToolPresentationDescriptors.Snapshot(
            presentation,
            boundedInvocationName);
        descriptor.InvocationName = boundedInvocationName;
        descriptor.DisplayName = TruncateUtf8(descriptor.DisplayName, MaxNameBytes);
        descriptor.Description = TruncateUtf8(descriptor.Description, MaxDescriptionBytes);
        descriptor.UnavailableReason = TruncateUtf8(
            descriptor.UnavailableReason,
            MaxDescriptionBytes);
        descriptor.IconUrl = TruncateUtf8(descriptor.IconUrl, MaxUriBytes);

        switch (descriptor.SourceRefCase)
        {
            case ToolPresentationDescriptor.SourceRefOneofCase.BuiltIn:
                descriptor.BuiltIn.ToolId = TruncateUtf8(
                    descriptor.BuiltIn.ToolId,
                    MaxNameBytes);
                break;
            case ToolPresentationDescriptor.SourceRefOneofCase.NyxIdOperation:
                BoundNyxIdOperation(descriptor.NyxIdOperation);
                break;
            case ToolPresentationDescriptor.SourceRefOneofCase.Mcp:
                descriptor.Mcp.ServerName = TruncateUtf8(
                    descriptor.Mcp.ServerName,
                    MaxNameBytes);
                descriptor.Mcp.ToolName = TruncateUtf8(
                    descriptor.Mcp.ToolName,
                    MaxNameBytes);
                break;
            case ToolPresentationDescriptor.SourceRefOneofCase.Skill:
                descriptor.Skill.SkillName = TruncateUtf8(
                    descriptor.Skill.SkillName,
                    MaxNameBytes);
                descriptor.Skill.Source = TruncateUtf8(
                    descriptor.Skill.Source,
                    MaxNameBytes);
                break;
        }

        if (descriptor.CalculateSize() <= MaxDescriptorBytes)
            return descriptor;

        return ToolPresentationDescriptors.Generic(boundedInvocationName);
    }

    private static void BoundNyxIdOperation(NyxIdOperationRef operation)
    {
        operation.ConnectedServiceId = TruncateUtf8(
            operation.ConnectedServiceId,
            MaxNameBytes);
        operation.ServiceSlug = TruncateUtf8(operation.ServiceSlug, MaxNameBytes);
        operation.CatalogServiceSlug = TruncateUtf8(
            operation.CatalogServiceSlug,
            MaxNameBytes);
        operation.ConnectionLabel = TruncateUtf8(
            operation.ConnectionLabel,
            MaxNameBytes);
        operation.ConnectorDisplayName = TruncateUtf8(
            operation.ConnectorDisplayName,
            MaxNameBytes);
        operation.OperationId = TruncateUtf8(operation.OperationId, MaxNameBytes);
        operation.HttpMethod = TruncateUtf8(operation.HttpMethod, MaxNameBytes);
        operation.PathTemplate = TruncateUtf8(operation.PathTemplate, MaxUriBytes);
        if (operation.HasReadinessCapabilityId)
        {
            operation.ReadinessCapabilityId = TruncateUtf8(
                operation.ReadinessCapabilityId,
                MaxNameBytes);
        }
    }

    private static string TruncateUtf8(string? value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value) || maxBytes <= 0)
            return string.Empty;

        Span<byte> buffer = stackalloc byte[maxBytes];
        var encoder = Encoding.UTF8.GetEncoder();
        encoder.Convert(
            value.AsSpan(),
            buffer,
            flush: true,
            out var charactersUsed,
            out _,
            out var completed);
        return completed ? value : value[..charactersUsed];
    }
}
