using System.Diagnostics;
using System.Text.Json;
using Aevatar.Foundation.Abstractions.Connectors;

namespace Aevatar.Workflows.Core.Connectors;

/// <summary>
/// CLI connector for preinstalled allowlisted commands.
/// </summary>
public sealed class CliConnector : IConnector
{
    private readonly string _command;
    private readonly string[] _fixedArguments;
    private readonly HashSet<string> _allowedOperations;
    private readonly HashSet<string> _allowedInputKeys;
    private readonly string? _workingDirectory;
    private readonly Dictionary<string, string> _environment;
    private readonly int _defaultTimeoutMs;

    public CliConnector(
        string name,
        string command,
        IEnumerable<string>? fixedArguments = null,
        IEnumerable<string>? allowedOperations = null,
        IEnumerable<string>? allowedInputKeys = null,
        string? workingDirectory = null,
        IDictionary<string, string>? environment = null,
        int timeoutMs = 30_000)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("command is required", nameof(command));

        Name = name;
        _command = command;
        _fixedArguments = (fixedArguments ?? []).ToArray();
        _allowedOperations = new HashSet<string>(allowedOperations ?? [], StringComparer.OrdinalIgnoreCase);
        _allowedInputKeys = new HashSet<string>(allowedInputKeys ?? [], StringComparer.OrdinalIgnoreCase);
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        _environment = environment?.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase)
                      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _defaultTimeoutMs = Math.Clamp(timeoutMs, 100, 300_000);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Type => "cli";

    /// <inheritdoc />
    public async Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
    {
        var operation = request.Operation.Trim();
        if (_allowedOperations.Count > 0 && !string.IsNullOrWhiteSpace(operation) && !_allowedOperations.Contains(operation))
        {
            return new ConnectorResponse
            {
                Success = false,
                Error = $"operation '{operation}' is not allowed",
                Metadata = new Dictionary<string, string> { ["connector.cli.operation"] = operation },
            };
        }

        var timeoutMs = request.Parameters.TryGetValue("timeout_ms", out var t) && int.TryParse(t, out var parsedTimeout)
            ? Math.Clamp(parsedTimeout, 100, 300_000)
            : _defaultTimeoutMs;

        if (_allowedInputKeys.Count > 0 && !TryValidatePayloadKeys(request.Payload, _allowedInputKeys, out var schemaError))
        {
            return new ConnectorResponse
            {
                Success = false,
                Error = schemaError,
                Metadata = new Dictionary<string, string>
                {
                    ["connector.cli.command"] = _command,
                    ["connector.cli.operation"] = operation,
                },
            };
        }

        var args = new List<string>(_fixedArguments);
        if (!string.IsNullOrWhiteSpace(operation))
            args.Add(operation);
        var argsText = string.Join(" ", args.Select(QuoteArgument));

        var startInfo = new ProcessStartInfo
        {
            FileName = _command,
            Arguments = argsText,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(_workingDirectory))
            startInfo.WorkingDirectory = _workingDirectory;
        foreach (var (key, value) in _environment)
            startInfo.Environment[key] = value;

        var sw = Stopwatch.StartNew();
        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ConnectorResponse
                {
                    Success = false,
                    Error = "failed to start process",
                };
            }

            if (!string.IsNullOrEmpty(request.Payload))
                await process.StandardInput.WriteAsync(request.Payload);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

            var stdOutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stdErrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;
            sw.Stop();

            var success = process.ExitCode == 0;
            return new ConnectorResponse
            {
                Success = success,
                Output = success ? stdOut : stdOut + (string.IsNullOrWhiteSpace(stdErr) ? "" : "\n" + stdErr),
                Error = success ? "" : $"process exited with code {process.ExitCode}",
                Metadata = new Dictionary<string, string>
                {
                    ["connector.cli.command"] = _command,
                    ["connector.cli.operation"] = operation,
                    ["connector.cli.exit_code"] = process.ExitCode.ToString(),
                    ["connector.cli.duration_ms"] = sw.Elapsed.TotalMilliseconds.ToString("F2"),
                },
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new ConnectorResponse
            {
                Success = false,
                Error = $"cli timeout after {timeoutMs}ms",
                Metadata = new Dictionary<string, string>
                {
                    ["connector.cli.command"] = _command,
                    ["connector.cli.operation"] = operation,
                    ["connector.cli.duration_ms"] = sw.Elapsed.TotalMilliseconds.ToString("F2"),
                },
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectorResponse
            {
                Success = false,
                Error = ex.Message,
                Metadata = new Dictionary<string, string>
                {
                    ["connector.cli.command"] = _command,
                    ["connector.cli.operation"] = operation,
                    ["connector.cli.duration_ms"] = sw.Elapsed.TotalMilliseconds.ToString("F2"),
                },
            };
        }
    }

    private static string QuoteArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (!arg.Contains(' ') && !arg.Contains('"')) return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    private static bool TryValidatePayloadKeys(string payload, HashSet<string> allowedKeys, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(payload)) return true;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "payload schema violation: expected JSON object";
                return false;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!allowedKeys.Contains(prop.Name))
                {
                    error = $"payload schema violation: key '{prop.Name}' is not allowlisted";
                    return false;
                }
            }

            return true;
        }
        catch
        {
            error = "payload schema violation: invalid JSON";
            return false;
        }
    }
}
