using System.Text.Json;

namespace Aevatar.Tools.Cli.Commands;

internal sealed record ConfigCliResult(
    bool Ok,
    string Code,
    string Message,
    int ExitCode,
    object? Data = null);

internal static class ConfigCliExecution
{
    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static ConfigCliResult Ok(string message, object? data = null) => new(
        Ok: true,
        Code: "OK",
        Message: message,
        ExitCode: 0,
        Data: data);

    public static ConfigCliResult InvalidArgument(string message, object? data = null) => new(
        Ok: false,
        Code: "INVALID_ARGUMENT",
        Message: message,
        ExitCode: 2,
        Data: data);

    public static ConfigCliResult NotFound(string message, object? data = null) => new(
        Ok: false,
        Code: "NOT_FOUND",
        Message: message,
        ExitCode: 3,
        Data: data);

    public static ConfigCliResult ValidationFailed(string message, object? data = null) => new(
        Ok: false,
        Code: "VALIDATION_FAILED",
        Message: message,
        ExitCode: 4,
        Data: data);

    public static ConfigCliResult IoError(string message, object? data = null) => new(
        Ok: false,
        Code: "IO_ERROR",
        Message: message,
        ExitCode: 5,
        Data: data);

    public static ConfigCliResult ExternalProbeFailed(string message, object? data = null) => new(
        Ok: false,
        Code: "EXTERNAL_PROBE_FAILED",
        Message: message,
        ExitCode: 6,
        Data: data);

    public static ConfigCliResult Unexpected(string message, object? data = null) => new(
        Ok: false,
        Code: "UNEXPECTED_ERROR",
        Message: message,
        ExitCode: 1,
        Data: data);

    public static async Task<int> ExecuteAsync(
        bool asJson,
        bool quiet,
        Func<CancellationToken, Task<ConfigCliResult>> action,
        CancellationToken cancellationToken)
    {
        ConfigCliResult result;
        try
        {
            result = await action(cancellationToken);
        }
        catch (ArgumentException ex)
        {
            result = InvalidArgument(ex.Message);
        }
        catch (JsonException ex)
        {
            result = ValidationFailed(ex.Message);
        }
        catch (FormatException ex)
        {
            result = ValidationFailed(ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            result = NotFound(ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            result = NotFound(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            result = NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            result = ValidationFailed(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            result = ExternalProbeFailed(ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            result = ExternalProbeFailed(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            result = IoError(ex.Message);
        }
        catch (IOException ex)
        {
            result = IoError(ex.Message);
        }
        catch (Exception ex)
        {
            result = Unexpected(ex.Message);
        }

        Print(result, asJson, quiet);
        return result.ExitCode;
    }

    public static void Print(ConfigCliResult result, bool asJson, bool quiet)
    {
        if (asJson)
        {
            var payload = new
            {
                ok = result.Ok,
                code = result.Code,
                message = result.Message,
                data = result.Data,
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(payload, JsonOutputOptions));
            return;
        }

        if (!quiet || !result.Ok)
        {
            if (result.Ok)
                Console.Out.WriteLine(result.Message);
            else
                Console.Error.WriteLine(result.Message);
        }

        if (result.Data != null && !quiet)
            Console.Out.WriteLine(JsonSerializer.Serialize(result.Data, JsonOutputOptions));
    }

    public static async Task<string> ResolveInputValueAsync(
        string? explicitValue,
        bool readFromStdin,
        string valueName)
    {
        var hasExplicit = !string.IsNullOrWhiteSpace(explicitValue);
        if (readFromStdin && hasExplicit)
            throw new ArgumentException($"{valueName} cannot be set together with --stdin");

        if (readFromStdin)
        {
            var stdin = await Console.In.ReadToEndAsync();
            var normalized = stdin.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                throw new ArgumentException($"{valueName} is empty on stdin");
            return normalized;
        }

        if (!hasExplicit)
            throw new ArgumentException($"{valueName} is required");
        return explicitValue!.Trim();
    }

    public static bool ConfirmOrThrow(bool yes, string prompt)
    {
        if (yes)
            return true;

        if (Console.IsInputRedirected)
            throw new InvalidOperationException($"confirmation required: rerun with --yes to {prompt}");

        Console.Write($"{prompt} [y/N]: ");
        var answer = Console.ReadLine();
        return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }
}
