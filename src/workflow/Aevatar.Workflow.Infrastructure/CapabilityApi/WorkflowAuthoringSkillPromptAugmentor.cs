using System.Text;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class WorkflowAuthoringSkillPromptAugmentor
{
    private const string SkillMarker = "[skill:workflow_authoring]";
    private const string SkillEndMarker = "[/skill:workflow_authoring]";
    private const string RuntimeCapabilitiesMarker = "[capabilities:workflow_runtime]";
    private const string RuntimeCapabilitiesEndMarker = "[/capabilities:workflow_runtime]";
    private const string AutoWorkflowName = "auto";
    private const string AutoReviewWorkflowName = "auto_review";
    private const string AuthoringEnabledMetadataKey = "workflow.authoring.enabled";
    private const string AuthoringIntentMetadataKey = "workflow.intent";
    private const string WorkflowAuthoringIntentValue = "workflow_authoring";
    private const string AutoInjectEnv = "AEVATAR_WORKFLOW_AUTHORING_AUTO_INJECT";

    public static string AugmentPrompt(
        string prompt,
        string? requestedWorkflowName,
        bool hasInlineWorkflowYamls,
        IReadOnlyDictionary<string, string>? metadata = null,
        WorkflowCapabilitiesDocument? capabilities = null)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return prompt;

        if (hasInlineWorkflowYamls)
            return prompt;

        if (prompt.Contains(SkillMarker, StringComparison.OrdinalIgnoreCase))
            return prompt;

        if (!ShouldInjectSkill(metadata, requestedWorkflowName))
            return prompt;

        var normalizedPrompt = prompt.Trim();
        return $"{normalizedPrompt}\n\n{BuildWorkflowAuthoringSkillBlock(capabilities)}";
    }

    private static bool ShouldInjectSkill(
        IReadOnlyDictionary<string, string>? metadata,
        string? requestedWorkflowName)
    {
        if (HasWorkflowAuthoringIntent(metadata))
            return true;

        if (IsEnvEnabled(AutoInjectEnv))
            return TargetsAutoPlannerWorkflow(requestedWorkflowName);

        return false;
    }

    private static string BuildWorkflowAuthoringSkillBlock(WorkflowCapabilitiesDocument? capabilities)
    {
        var skillBlock = $"{SkillMarker}\n{BuildSkillBody()}\n{SkillEndMarker}";
        var runtimeCapabilityBlock = BuildRuntimeCapabilitiesBlock(capabilities);
        if (string.IsNullOrWhiteSpace(runtimeCapabilityBlock))
            return skillBlock;

        return $"{skillBlock}\n{runtimeCapabilityBlock}";
    }

    private static string BuildRuntimeCapabilitiesBlock(WorkflowCapabilitiesDocument? capabilities)
    {
        if (capabilities == null)
            return string.Empty;

        var enabledConnectors = capabilities.Connectors
            .Where(connector => connector.Enabled && !string.IsNullOrWhiteSpace(connector.Name))
            .ToArray();
        var workflows = capabilities.Workflows
            .Select(workflow => workflow.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();
        var connectors = enabledConnectors
            .Select(connector => connector.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
        var connectorInputAllowlists = enabledConnectors
            .Where(connector => connector.AllowedInputKeys.Count > 0)
            .OrderBy(connector => connector.Name, StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .Select(connector =>
            {
                var keys = connector.AllowedInputKeys
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToArray();
                return keys.Length == 0
                    ? string.Empty
                    : $"`{connector.Name.Trim()}`: {FormatBacktickList(keys)}";
            })
            .Where(hint => !string.IsNullOrWhiteSpace(hint))
            .ToArray();
        var primitives = capabilities.Primitives
            .Select(primitive => primitive.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();

        if (workflows.Length == 0 &&
            connectors.Length == 0 &&
            primitives.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine(RuntimeCapabilitiesMarker);
        builder.AppendLine("Live workflow runtime capabilities snapshot:");
        if (workflows.Length > 0)
            builder.AppendLine($"- workflows: {FormatBacktickList(workflows)}");
        if (connectors.Length > 0)
            builder.AppendLine($"- enabled connectors: {FormatBacktickList(connectors)}");
        if (connectorInputAllowlists.Length > 0)
        {
            builder.AppendLine("- connector payload allowlists (only pass these top-level JSON keys):");
            foreach (var hint in connectorInputAllowlists)
                builder.AppendLine($"  - {hint}");
        }
        if (primitives.Length > 0)
            builder.AppendLine($"- primitives: {FormatBacktickList(primitives)}");
        builder.AppendLine(RuntimeCapabilitiesEndMarker);
        return builder.ToString().TrimEnd();
    }

    private static bool HasWorkflowAuthoringIntent(IReadOnlyDictionary<string, string>? metadata)
    {
        if (TryGetMetadataValue(metadata, AuthoringEnabledMetadataKey, out var enabledRaw) &&
            IsTruthy(enabledRaw))
        {
            return true;
        }

        if (TryGetMetadataValue(metadata, AuthoringIntentMetadataKey, out var intentRaw) &&
            string.Equals(intentRaw.Trim(), WorkflowAuthoringIntentValue, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool TargetsAutoPlannerWorkflow(string? requestedWorkflowName)
    {
        var normalizedWorkflowName = string.IsNullOrWhiteSpace(requestedWorkflowName)
            ? string.Empty
            : requestedWorkflowName.Trim();
        return normalizedWorkflowName.Length == 0 ||
               string.Equals(normalizedWorkflowName, AutoWorkflowName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedWorkflowName, AutoReviewWorkflowName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetMetadataValue(
        IReadOnlyDictionary<string, string>? metadata,
        string key,
        out string value)
    {
        value = string.Empty;
        if (metadata == null || metadata.Count == 0)
            return false;

        if (metadata.TryGetValue(key, out var direct) &&
            !string.IsNullOrWhiteSpace(direct))
        {
            value = direct.Trim();
            return true;
        }

        foreach (var (candidateKey, candidateValue) in metadata)
        {
            if (!string.Equals(candidateKey, key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(candidateValue))
                continue;

            value = candidateValue.Trim();
            return true;
        }

        return false;
    }

    private static bool IsEnvEnabled(string envName) =>
        IsTruthy(Environment.GetEnvironmentVariable(envName));

    private static bool IsTruthy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var normalized = raw.Trim();
        return string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "enabled", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBacktickList(IEnumerable<string> values) =>
        string.Join(", ", values.Select(x => $"`{x}`"));

    private static string BuildSkillBody() =>
        """
            Workflow authoring quick skill:

            - Output either a direct answer or exactly one ```yaml block; never both.
            - For generated workflow YAML:
              - Top-level keys: name, description, roles, steps
              - Keep step fields constrained to: id, type, role, target_role, parameters, next, branches, children, retry, on_error, timeout_ms
              - Keep primitive-specific options under step.parameters
              - Do not emit engine-internal `dynamic_workflow` in authored YAML
            - Prefer runtime-known resources:
              - Use workflows/connectors/primitives from injected [capabilities:workflow_runtime]
              - Do not invent connector names
            - Reliability guidance:
              - Add `on_error.fallback_step` for risky external integrations
              - Keep payloads minimal and aligned with connector allowlists
            """;
}
