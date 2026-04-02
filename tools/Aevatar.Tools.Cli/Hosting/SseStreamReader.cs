using System.Text;
using System.Text.Json;

namespace Aevatar.Tools.Cli.Hosting;

internal static class SseStreamReader
{
    public static async Task ReadAndPrintAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var dataLines = new List<string>();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                FlushDataLines(dataLines);
                break;
            }

            if (line is "" or "\r")
            {
                FlushDataLines(dataLines);
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var payload = line.Length > 5 ? line[5..] : "";
                if (payload.StartsWith(' '))
                    payload = payload[1..];
                dataLines.Add(payload);
            }
        }

        Console.WriteLine();
    }

    private static void FlushDataLines(List<string> dataLines)
    {
        if (dataLines.Count == 0)
            return;

        var data = string.Join('\n', dataLines).Trim();
        dataLines.Clear();

        if (string.IsNullOrEmpty(data) || data == "[DONE]")
            return;

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            HandleFrame(root);
        }
        catch (JsonException)
        {
            // Skip malformed frames.
        }
    }

    private static void HandleFrame(JsonElement root)
    {
        // Already typed format.
        if (root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
        {
            HandleTypedFrame(typeProp.GetString()!, root);
            return;
        }

        // Protobuf-JSON oneof format: detect by field name.
        if (root.TryGetProperty("runStarted", out var runStarted))
        {
            var runId = GetString(runStarted, "runId");
            var threadId = GetString(runStarted, "threadId");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Run started: runId={runId} threadId={threadId}");
            Console.ResetColor();
            return;
        }

        if (root.TryGetProperty("textMessageStart", out _))
            return; // Silently consume, streaming begins.

        if (root.TryGetProperty("textMessageContent", out var textContent))
        {
            var delta = GetString(textContent, "delta");
            Console.Write(delta);
            return;
        }

        if (root.TryGetProperty("textMessageEnd", out _))
        {
            Console.WriteLine();
            return;
        }

        if (root.TryGetProperty("stepStarted", out var stepStarted))
        {
            var stepName = GetString(stepStarted, "stepName");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[step: {stepName}] started");
            Console.ResetColor();
            return;
        }

        if (root.TryGetProperty("stepFinished", out var stepFinished))
        {
            var stepName = GetString(stepFinished, "stepName");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[step: {stepName}] finished");
            Console.ResetColor();
            return;
        }

        if (root.TryGetProperty("runError", out var runError))
        {
            var message = GetString(runError, "message");
            var code = GetString(runError, "code");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Run error: {message}" + (string.IsNullOrEmpty(code) ? "" : $" (code: {code})"));
            Console.ResetColor();
            return;
        }

        if (root.TryGetProperty("runFinished", out var runFinished))
        {
            var runId = GetString(runFinished, "runId");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Run finished: runId={runId}");
            Console.ResetColor();
            return;
        }

        if (root.TryGetProperty("humanInputRequest", out var humanInput))
        {
            var prompt = GetString(humanInput, "prompt");
            var stepId = GetString(humanInput, "stepId");
            var runId = GetString(humanInput, "runId");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[human input requested] runId={runId} stepId={stepId}");
            if (!string.IsNullOrEmpty(prompt))
                Console.WriteLine($"  Prompt: {prompt}");
            Console.ResetColor();
            return;
        }

        if (root.TryGetProperty("custom", out var custom))
        {
            var name = GetString(custom, "name");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[custom: {name}]");
            Console.ResetColor();
            return;
        }

        if (root.TryGetProperty("stateSnapshot", out _))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("[state snapshot received]");
            Console.ResetColor();
        }
    }

    private static void HandleTypedFrame(string type, JsonElement root)
    {
        switch (type)
        {
            case "RUN_STARTED":
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"Run started: runId={GetString(root, "runId")} threadId={GetString(root, "threadId")}");
                Console.ResetColor();
                break;
            case "TEXT_MESSAGE_CONTENT":
                Console.Write(GetString(root, "delta"));
                break;
            case "TEXT_MESSAGE_END":
                Console.WriteLine();
                break;
            case "STEP_STARTED":
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[step: {GetString(root, "stepName")}] started");
                Console.ResetColor();
                break;
            case "STEP_FINISHED":
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[step: {GetString(root, "stepName")}] finished");
                Console.ResetColor();
                break;
            case "RUN_ERROR":
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Run error: {GetString(root, "message")}");
                Console.ResetColor();
                break;
            case "RUN_FINISHED":
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Run finished: runId={GetString(root, "runId")}");
                Console.ResetColor();
                break;
            case "HUMAN_INPUT_REQUEST":
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[human input requested] runId={GetString(root, "runId")} stepId={GetString(root, "stepId")}");
                var prompt = GetString(root, "prompt");
                if (!string.IsNullOrEmpty(prompt))
                    Console.WriteLine($"  Prompt: {prompt}");
                Console.ResetColor();
                break;
        }
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? "";
        return "";
    }
}
