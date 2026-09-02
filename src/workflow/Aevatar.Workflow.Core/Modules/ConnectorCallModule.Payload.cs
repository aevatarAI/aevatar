using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

public sealed partial class ConnectorCallModule
{
    private async Task<string?> ResolvePayloadAsync(
        StepRequestEvent request,
        bool isSecureStep,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var mode = WorkflowParameterValueParser.GetString(
            request.Parameters,
            isSecureStep ? "secure_template" : "input",
            "stdin_mode",
            "stdin").Trim();
        if (string.Equals(mode, "input", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "inherit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase))
        {
            return request.Input;
        }

        if (string.Equals(mode, "secure_variable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "secure_input", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "secret_input", StringComparison.OrdinalIgnoreCase))
        {
            var variable = WorkflowParameterValueParser.GetString(
                request.Parameters,
                string.Empty,
                "stdin_secret_variable",
                "secret_variable",
                "secure_variable",
                "variable");
            return await ResolveSecureVariableAsync(ctx, request.RunId, variable, ct);
        }

        if (string.Equals(mode, "template", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "secure_template", StringComparison.OrdinalIgnoreCase))
        {
            var template = WorkflowParameterValueParser.GetString(
                request.Parameters,
                request.Input ?? string.Empty,
                "stdin_template",
                "payload_template",
                "stdin_value");
            return await ResolveSecureTemplateAsync(ctx, request.RunId, template, ct);
        }

        return request.Input;
    }

    private static async Task<string> ResolveSecureVariableAsync(
        IWorkflowExecutionContext ctx,
        string? runId,
        string variable,
        CancellationToken ct)
    {
        var normalizedVariable = NormalizeSecureVariableName(variable);
        if (string.IsNullOrWhiteSpace(normalizedVariable))
            throw new InvalidOperationException("connector_call secure stdin requires 'stdin_secret_variable'.");

        var captured = await SecureInputRuntimeContextAccess.TryGetCapturedValueAsync(ctx, runId, normalizedVariable, ct);
        if (captured.Found)
            return captured.Value;

        throw new InvalidOperationException(
            $"connector_call is missing captured secure value '{normalizedVariable}' for run '{WorkflowRunIdNormalizer.Normalize(runId)}'.");
    }

    private static async Task<string> ResolveSecureTemplateAsync(
        IWorkflowExecutionContext ctx,
        string? runId,
        string template,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        var withJsonEscapedSecureValues = await ReplaceSecurePlaceholdersAsync(
            template,
            SecureJsonPlaceholderPattern(),
            async variable =>
            {
                var value = await ResolveSecureVariableAsync(ctx, runId, variable, ct);
                return JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
            });

        return await ReplaceSecurePlaceholdersAsync(
            withJsonEscapedSecureValues,
            SecurePlaceholderPattern(),
            variable => ResolveSecureVariableAsync(ctx, runId, variable, ct));
    }

    private static async Task<string> ReplaceSecurePlaceholdersAsync(
        string template,
        Regex pattern,
        Func<string, Task<string>> resolveAsync)
    {
        var matches = pattern.Matches(template);
        if (matches.Count == 0)
            return template;

        var builder = new StringBuilder(template.Length);
        var cursor = 0;
        foreach (Match match in matches)
        {
            builder.Append(template, cursor, match.Index - cursor);
            var variable = match.Groups[1].Value;
            builder.Append(await resolveAsync(variable));
            cursor = match.Index + match.Length;
        }

        builder.Append(template, cursor, template.Length - cursor);
        return builder.ToString();
    }

    private static string NormalizeSecureVariableName(string? variable) =>
        string.IsNullOrWhiteSpace(variable) ? string.Empty : variable.Trim();

    [GeneratedRegex(@"\[\[secure:([A-Za-z0-9_.:-]+)\]\]", RegexOptions.Compiled)]
    private static partial Regex SecurePlaceholderPattern();

    [GeneratedRegex(@"\[\[secure_json:([A-Za-z0-9_.:-]+)\]\]", RegexOptions.Compiled)]
    private static partial Regex SecureJsonPlaceholderPattern();

    private static void AppendBaseMetadata(
        StepCompletedEvent evt,
        PendingConnectorCallState pending,
        double durationMs)
    {
        evt.Annotations["connector.name"] = pending.ConnectorName;
        evt.Annotations["connector.type"] = pending.ConnectorType;
        evt.Annotations["connector.operation"] = pending.Operation;
        evt.Annotations["connector.attempts"] = pending.Attempt.ToString();
        evt.Annotations["connector.timeout_ms"] = pending.TimeoutMs.ToString();
        evt.Annotations["connector.duration_ms"] = durationMs.ToString("F2");
    }

    private static bool TryAssertResponseOutput(
        IReadOnlyDictionary<string, string> parameters,
        string responseOutput,
        out string error)
    {
        error = string.Empty;
        var responsePath = WorkflowParameterValueParser.GetString(
            parameters,
            string.Empty,
            "assert_response_path");
        if (string.IsNullOrWhiteSpace(responsePath))
            return true;

        if (string.IsNullOrWhiteSpace(responseOutput))
        {
            error = $"connector_call assertion failed: response path '{responsePath}' is missing";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseOutput);
            if (!TryResolveJsonPath(document.RootElement, responsePath, out var value))
            {
                error = $"connector_call assertion failed: response path '{responsePath}' is missing";
                return false;
            }

            if (!IsTruthy(value))
            {
                error = $"connector_call assertion failed: response path '{responsePath}' was not truthy";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = $"connector_call assertion failed: response output is not valid JSON for path '{responsePath}'";
            return false;
        }
    }

    private static bool TryResolveJsonPath(JsonElement current, string path, out JsonElement value)
    {
        var normalizedSegments = path
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (normalizedSegments.Length == 0)
        {
            value = current;
            return true;
        }

        foreach (var segment in normalizedSegments)
        {
            if (current.ValueKind == JsonValueKind.Object &&
                current.TryGetProperty(segment, out var property))
            {
                current = property;
                continue;
            }

            if (current.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, out var index) &&
                index >= 0 &&
                index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }

            value = default;
            return false;
        }

        value = current;
        return true;
    }

    private static bool IsTruthy(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => !string.Equals(value.GetRawText(), "0", StringComparison.Ordinal),
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()) &&
                                    !string.Equals(value.GetString(), "false", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Null => false,
            JsonValueKind.Undefined => false,
            _ => true,
        };
    }

    private async Task<string> ReconstructConnectorHttpAuthorizationAsync(
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var credential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(ctx, ct);
        if (credential.Found)
        {
            var resolved = await WorkflowCallerAccessTokenResolver.ResolveAsync(
                credential.Credential,
                _callerAccessTokenProvider,
                ct);
            var parsed = WorkflowCallerCredentialTokens.ParseOptional(resolved.BearerToken);
            return parsed.IsValid ? $"Bearer {parsed.NormalizedBearerToken}" : string.Empty;
        }

        return string.Empty;
    }
}
