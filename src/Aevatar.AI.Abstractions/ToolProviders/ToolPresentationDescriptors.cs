using Aevatar.Foundation.Abstractions.Tools;

namespace Aevatar.AI.Abstractions.ToolProviders;

public static class ToolPresentationDescriptors
{
    public static ToolPresentationDescriptor Generic(
        string invocationName,
        string? description = null) =>
        new()
        {
            InvocationName = invocationName ?? string.Empty,
            DisplayName = invocationName ?? string.Empty,
            Description = description ?? string.Empty,
            Kind = ToolPresentationKind.Generic,
            Availability = ToolAvailability.Available,
        };

    public static ToolPresentationDescriptor BuiltIn(
        string invocationName,
        string displayName,
        string? description = null) =>
        new()
        {
            InvocationName = invocationName ?? string.Empty,
            DisplayName = displayName ?? string.Empty,
            Description = description ?? string.Empty,
            Kind = ToolPresentationKind.BuiltIn,
            Availability = ToolAvailability.Available,
            BuiltIn = new BuiltInToolRef { ToolId = invocationName ?? string.Empty },
        };

    public static ToolPresentationDescriptor Mcp(
        string invocationName,
        string displayName,
        string? description,
        string serverName,
        string providerToolName) =>
        new()
        {
            InvocationName = invocationName ?? string.Empty,
            DisplayName = displayName ?? string.Empty,
            Description = description ?? string.Empty,
            Kind = ToolPresentationKind.Mcp,
            Availability = ToolAvailability.Available,
            Mcp = new McpToolRef
            {
                ServerName = serverName ?? string.Empty,
                ToolName = providerToolName ?? string.Empty,
            },
        };

    public static ToolPresentationDescriptor Skill(
        string invocationName,
        string displayName,
        string? description,
        string skillName,
        string source) =>
        new()
        {
            InvocationName = invocationName ?? string.Empty,
            DisplayName = displayName ?? string.Empty,
            Description = description ?? string.Empty,
            Kind = ToolPresentationKind.Skill,
            Availability = ToolAvailability.Available,
            Skill = new SkillRef
            {
                SkillName = skillName ?? string.Empty,
                Source = source ?? string.Empty,
            },
        };

    public static ToolPresentationDescriptor Snapshot(
        IAgentTool? tool,
        string invocationName,
        string? argumentsJson = null)
    {
        return Snapshot(
            tool?.ResolvePresentation(argumentsJson ?? string.Empty),
            invocationName,
            tool?.Description);
    }

    public static ToolPresentationDescriptor Snapshot(
        ToolPresentationDescriptor? presentation,
        string invocationName,
        string? fallbackDescription = null)
    {
        var normalizedInvocationName = invocationName?.Trim() ?? string.Empty;
        var descriptor = presentation?.Clone()
                         ?? Generic(normalizedInvocationName, fallbackDescription);
        descriptor.InvocationName = normalizedInvocationName;
        if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
            descriptor.DisplayName = normalizedInvocationName;
        if (string.IsNullOrWhiteSpace(descriptor.Description))
            descriptor.Description = fallbackDescription ?? string.Empty;
        if (descriptor.Kind == ToolPresentationKind.Unspecified)
            descriptor.Kind = ToolPresentationKind.Generic;
        if (descriptor.Availability == ToolAvailability.Unspecified)
            descriptor.Availability = ToolAvailability.Available;
        return descriptor;
    }
}
