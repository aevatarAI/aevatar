using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using Aevatar.Configuration;
using Aevatar.Tools.Cli.Commands;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Aevatar.Workflow.Sdk;
using Aevatar.Workflow.Sdk.Contracts;
using Aevatar.Workflow.Sdk.Errors;
using Aevatar.Workflow.Sdk.Options;
using Aevatar.Workflow.Sdk.Streaming;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Hosting;

internal static class ChatCommandHandler
{
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly Regex YamlFenceRegex = new(
        @"```ya?ml\s*\n([\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task RunAsync(
        string? message,
        int port,
        string? urlOverride,
        CancellationToken cancellationToken)
    {
        var prompt = (message ?? string.Empty).Trim();
        if (prompt.Length == 0)
        {
            Console.Error.WriteLine("Chat message is required. Example: aevatar chat \"hello\".");
            return;
        }

        var normalizedPort = port > 0 ? port : 6688;
        var localBaseUrl = $"http://localhost:{normalizedPort}";

        string apiBaseUrl;
        try
        {
            apiBaseUrl = CliAppConfigStore.ResolveApiBaseUrl(urlOverride, localBaseUrl, out var warning);
            if (!string.IsNullOrWhiteSpace(warning))
                Console.WriteLine($"[warn] {warning}");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        var health = await ProbeAppHealthAsync(localBaseUrl, cancellationToken);
        if (health == AppHealthStatus.ReachableButNotAevatar)
        {
            Console.Error.WriteLine(
                $"Port {normalizedPort} is occupied by a non-aevatar service. " +
                $"Please free the port or run with --port <newPort>.");
            return;
        }

        if (health == AppHealthStatus.Unreachable)
        {
            Console.WriteLine($"aevatar app is not running on port {normalizedPort}. Starting app...");
            if (!TryStartAppProcess(normalizedPort, apiBaseUrl, localBaseUrl, out var startError))
            {
                Console.Error.WriteLine(startError);
                return;
            }

            try
            {
                await WaitForAppReadyAsync(localBaseUrl, normalizedPort, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return;
            }
        }

        var uiUrl = BuildUiUrl(localBaseUrl, prompt);
        BrowserLauncher.Open(uiUrl);
        Console.WriteLine($"Opened aevatar app UI: {uiUrl}");
    }

    public static void SetApiBaseUrl(string url)
    {
        try
        {
            CliAppConfigStore.SetApiBaseUrl(url);
            var configured = CliAppConfigStore.GetApiBaseUrl(out _);
            Console.WriteLine($"Saved chat API base URL: {configured}");
            Console.WriteLine($"Config file: {AevatarPaths.ConfigJson}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
        }
    }

    public static void GetApiBaseUrl()
    {
        var value = CliAppConfigStore.GetApiBaseUrl(out var warning);
        if (!string.IsNullOrWhiteSpace(warning))
            Console.WriteLine($"[warn] {warning}");

        if (string.IsNullOrWhiteSpace(value))
        {
            Console.WriteLine("Chat API base URL is not configured.");
            return;
        }

        Console.WriteLine(value);
    }

    public static void ClearApiBaseUrl()
    {
        try
        {
            var removed = CliAppConfigStore.ClearApiBaseUrl();
            Console.WriteLine(removed
                ? "Cleared chat API base URL."
                : "Chat API base URL is already empty.");
            Console.WriteLine($"Config file: {AevatarPaths.ConfigJson}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
        }
    }

    public static Task<int> RunWorkflowYamlAsync(
        string? message,
        bool readFromStdin,
        string? urlOverride,
        string? filenameOverride,
        bool yes,
        CancellationToken cancellationToken) =>
        RunWorkflowYamlAsync(
            message,
            readFromStdin,
            urlOverride,
            filenameOverride,
            yes,
            cancellationToken,
            workflowClient: null,
            confirmSave: null);

    internal static async Task<int> RunWorkflowYamlAsync(
        string? message,
        bool readFromStdin,
        string? urlOverride,
        string? filenameOverride,
        bool yes,
        CancellationToken cancellationToken,
        IAevatarWorkflowClient? workflowClient = null,
        Func<bool, string, bool>? confirmSave = null)
    {
        string prompt;
        try
        {
            prompt = await ConfigCliExecution.ResolveInputValueAsync(message, readFromStdin, "chat message");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        string? warning;
        string? apiBaseUrl;
        try
        {
            apiBaseUrl = ResolveWorkflowApiBaseUrl(urlOverride, out warning);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(warning))
            Console.WriteLine($"[warn] {warning}");

        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            Console.Error.WriteLine(
                "Workflow API base URL is not configured. Use --url or run `aevatar chat config set-url <url>`.");
            return 2;
        }

        try
        {
            var client = workflowClient ?? CreateWorkflowClient(apiBaseUrl);
            var runResult = await client.RunToCompletionAsync(
                new ChatRunRequest
                {
                    Prompt = prompt,
                    Workflow = "auto_review",
                    Metadata = BuildWorkflowAuthoringMetadata(),
                },
                cancellationToken);

            if (!TryExtractWorkflowYaml(runResult, out var generatedYaml))
            {
                Console.Error.WriteLine("Failed to extract workflow YAML from workflow run output.");
                return 4;
            }

            var validation = ValidateWorkflowYaml(generatedYaml);
            if (!validation.IsValid || validation.Definition == null)
            {
                Console.Error.WriteLine($"Generated workflow YAML is invalid: {validation.ErrorMessage}");
                return 4;
            }

            Console.WriteLine(generatedYaml);
            Console.WriteLine();

            var normalizedFilename = ResolveWorkflowFilename(filenameOverride, validation.Definition.Name);
            var targetPath = Path.Combine(AevatarPaths.Workflows, normalizedFilename);
            var confirm = confirmSave ?? ConfigCliExecution.ConfirmOrThrow;
            var shouldSave = confirm(yes, $"save workflow YAML to {targetPath}");
            if (!shouldSave)
            {
                Console.WriteLine("Skipped saving workflow YAML.");
                return 0;
            }

            Directory.CreateDirectory(AevatarPaths.Workflows);
            var normalizedContent = AppDemoPlaygroundEndpoints.NormalizeWorkflowContentForSave(generatedYaml);
            await File.WriteAllTextAsync(targetPath, normalizedContent, cancellationToken);
            Console.WriteLine($"Saved workflow YAML: {targetPath}");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 4;
        }
        catch (AevatarWorkflowException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 6;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 6;
        }
        catch (TaskCanceledException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 6;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 5;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    internal static bool TryExtractWorkflowYaml(WorkflowRunResult runResult, out string yaml)
    {
        if (TryExtractValidateYamlStepOutput(runResult.Events, out yaml))
            return true;
        if (TryExtractAnyStepOutput(runResult.Events, out yaml))
            return true;
        if (runResult.TerminalEvent != null &&
            TryExtractYamlFromFrame(runResult.TerminalEvent.Frame, out yaml))
        {
            return true;
        }

        foreach (var evt in runResult.Events.Reverse())
        {
            if (TryExtractYamlFromFrame(evt.Frame, out yaml))
                return true;
        }

        yaml = string.Empty;
        return false;
    }

    internal static WorkflowYamlValidationResult ValidateWorkflowYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new WorkflowYamlValidationResult(
                IsValid: false,
                Definition: null,
                Errors: ["workflow YAML is empty"],
                ErrorMessage: "workflow YAML is empty");
        }

        WorkflowDefinition definition;
        try
        {
            definition = new WorkflowParser().Parse(yaml);
        }
        catch (Exception ex)
        {
            return new WorkflowYamlValidationResult(
                IsValid: false,
                Definition: null,
                Errors: [ex.Message],
                ErrorMessage: ex.Message);
        }

        var errors = WorkflowValidator.Validate(definition);
        if (errors.Count > 0)
        {
            return new WorkflowYamlValidationResult(
                IsValid: false,
                Definition: definition,
                Errors: errors,
                ErrorMessage: string.Join("; ", errors));
        }

        return new WorkflowYamlValidationResult(
            IsValid: true,
            Definition: definition,
            Errors: Array.Empty<string>(),
            ErrorMessage: string.Empty);
    }

    private static IDictionary<string, string> BuildWorkflowAuthoringMetadata() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["workflow.authoring.enabled"] = "true",
            ["workflow.intent"] = "workflow_authoring",
        };

    private static string ResolveWorkflowFilename(string? filenameOverride, string workflowName)
    {
        try
        {
            return AppDemoPlaygroundEndpoints.NormalizeWorkflowSaveFilename(filenameOverride, workflowName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new ArgumentException($"invalid workflow filename: {ex.Message}", nameof(filenameOverride), ex);
        }
    }

    private static bool TryExtractValidateYamlStepOutput(
        IReadOnlyList<WorkflowEvent> events,
        out string yaml)
    {
        foreach (var evt in events.Reverse())
        {
            if (!string.Equals(evt.Frame.Type, WorkflowEventTypes.Custom, StringComparison.Ordinal))
                continue;
            if (!WorkflowCustomEventParser.TryParseStepCompleted(evt.Frame, out var completed))
                continue;
            if (!string.Equals(completed.StepId, "validate_yaml", StringComparison.OrdinalIgnoreCase))
                continue;
            if (completed.Success != true)
                continue;
            if (TryExtractYamlCandidate(completed.Output, out yaml))
                return true;
        }

        yaml = string.Empty;
        return false;
    }

    private static bool TryExtractAnyStepOutput(
        IReadOnlyList<WorkflowEvent> events,
        out string yaml)
    {
        foreach (var evt in events.Reverse())
        {
            if (!string.Equals(evt.Frame.Type, WorkflowEventTypes.Custom, StringComparison.Ordinal))
                continue;
            if (!WorkflowCustomEventParser.TryParseStepCompleted(evt.Frame, out var completed))
                continue;
            if (TryExtractYamlCandidate(completed.Output, out yaml))
                return true;
        }

        yaml = string.Empty;
        return false;
    }

    private static bool TryExtractYamlFromFrame(WorkflowOutputFrame frame, out string yaml)
    {
        var candidates = new[]
        {
            frame.Message,
            frame.Delta,
            ExtractFrameText(frame.Result),
            ExtractFrameText(frame.Value),
        };
        foreach (var candidate in candidates)
        {
            if (TryExtractYamlCandidate(candidate, out yaml))
                return true;
        }

        yaml = string.Empty;
        return false;
    }

    private static string ExtractFrameText(JsonElement? element)
    {
        if (element is not { } value)
            return string.Empty;

        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? string.Empty;

        if (value.ValueKind == JsonValueKind.Object)
        {
            var output = ReadJsonValue(value, "output", "Output", "content", "Content");
            if (!string.IsNullOrWhiteSpace(output))
                return output;
        }

        return value.GetRawText();
    }

    private static string ReadJsonValue(JsonElement node, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(node, propertyName, out var property))
                continue;

            return property.ValueKind switch
            {
                JsonValueKind.String => property.GetString() ?? string.Empty,
                JsonValueKind.Object or JsonValueKind.Array => property.GetRawText(),
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => property.ToString(),
            };
        }

        return string.Empty;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement node, string propertyName, out JsonElement value)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryExtractYamlCandidate(string? rawText, out string yaml)
    {
        yaml = string.Empty;
        if (string.IsNullOrWhiteSpace(rawText))
            return false;

        var text = rawText.Trim();
        var matches = YamlFenceRegex.Matches(text);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var candidate = matches[i].Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            yaml = candidate;
            return true;
        }

        if (!LooksLikeWorkflowYaml(text))
            return false;

        var validation = ValidateWorkflowYaml(text);
        if (!validation.IsValid)
            return false;

        yaml = text;
        return true;
    }

    private static bool LooksLikeWorkflowYaml(string candidate) =>
        candidate.Contains("name:", StringComparison.OrdinalIgnoreCase) &&
        candidate.Contains("steps:", StringComparison.OrdinalIgnoreCase);

    internal sealed record WorkflowYamlValidationResult(
        bool IsValid,
        WorkflowDefinition? Definition,
        IReadOnlyList<string> Errors,
        string ErrorMessage);

    private static string ResolveWorkflowApiBaseUrl(string? urlOverride, out string? warning)
    {
        var localFallback = "http://localhost:6688";
        return CliAppConfigStore.ResolveApiBaseUrl(urlOverride, localFallback, out warning);
    }

    private static IAevatarWorkflowClient CreateWorkflowClient(string apiBaseUrl)
    {
        var options = Options.Create(new AevatarWorkflowClientOptions
        {
            BaseUrl = apiBaseUrl,
        });
        return new AevatarWorkflowClient(new HttpClient(), new SseChatTransport(), options);
    }

    private static string BuildUiUrl(string localBaseUrl, string prompt)
    {
        var encodedPrompt = Uri.EscapeDataString(prompt);
        return $"{localBaseUrl.TrimEnd('/')}/?chat={encodedPrompt}";
    }

    private static async Task WaitForAppReadyAsync(
        string localBaseUrl,
        int port,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < StartupTimeout)
        {
            var health = await ProbeAppHealthAsync(localBaseUrl, cancellationToken);
            if (health == AppHealthStatus.HealthyAevatar)
                return;

            if (health == AppHealthStatus.ReachableButNotAevatar)
            {
                throw new InvalidOperationException(
                    $"Port {port} is now responding, but it is not the aevatar app process.");
            }

            await Task.Delay(StartupPollInterval, cancellationToken);
        }

        throw new TimeoutException(
            $"aevatar app did not become ready on port {port} within {StartupTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.");
    }

    private static async Task<AppHealthStatus> ProbeAppHealthAsync(
        string localBaseUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = HealthProbeTimeout };
            using var response = await http.GetAsync(
                $"{localBaseUrl.TrimEnd('/')}/api/app/health",
                cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return AppHealthStatus.ReachableButNotAevatar;

            if (string.IsNullOrWhiteSpace(payload))
                return AppHealthStatus.ReachableButNotAevatar;

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            var service = root.TryGetProperty("service", out var serviceProp)
                ? serviceProp.GetString()
                : null;
            if (ok && string.Equals(service, "aevatar.app", StringComparison.Ordinal))
                return AppHealthStatus.HealthyAevatar;

            return AppHealthStatus.ReachableButNotAevatar;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AppHealthStatus.Unreachable;
        }
        catch (HttpRequestException)
        {
            return AppHealthStatus.Unreachable;
        }
    }

    private static bool TryStartAppProcess(
        int port,
        string apiBaseUrl,
        string localBaseUrl,
        out string error)
    {
        error = string.Empty;
        try
        {
            var startInfo = BuildAppStartInfo(port, apiBaseUrl, localBaseUrl);
            var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "Failed to launch aevatar app process.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to launch aevatar app process: {ex.Message}";
            return false;
        }
    }

    private static ProcessStartInfo BuildAppStartInfo(int port, string apiBaseUrl, string localBaseUrl)
    {
        var appArgs = new List<string>
        {
            "app",
            "--no-browser",
            "--port",
            port.ToString(CultureInfo.InvariantCulture),
        };

        if (!string.Equals(apiBaseUrl, localBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            appArgs.Add("--api-base");
            appArgs.Add(apiBaseUrl);
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            processPath = "aevatar";

        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        if (IsDotnetHost(processPath))
        {
            var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            startInfo.ArgumentList.Add(assemblyPath);
        }

        foreach (var arg in appArgs)
            startInfo.ArgumentList.Add(arg);

        return startInfo;
    }

    private static bool IsDotnetHost(string processPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private enum AppHealthStatus
    {
        HealthyAevatar = 0,
        ReachableButNotAevatar = 1,
        Unreachable = 2,
    }
}
