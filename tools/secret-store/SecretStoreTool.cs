using System.Globalization;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

namespace Aevatar.SecretStore.Tools;

public static class SecretStoreTool
{
    public static readonly IReadOnlyList<string> CommandNames =
    [
        "generate-keyring",
        "add-key",
        "reencrypt-sweep",
    ];

    public static async Task<int> MainAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        ISecretStoreSweepTarget? sweepTarget = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            if (args.Length == 0 || string.Equals(args[0], "--help", StringComparison.Ordinal))
            {
                WriteUsage(output);
                return args.Length == 0 ? 1 : 0;
            }

            return args[0] switch
            {
                "generate-keyring" => GenerateKeyring(args[1..], output),
                "add-key" => AddKey(args[1..], output),
                "reencrypt-sweep" => await ReencryptSweepAsync(args[1..], output, sweepTarget, ct),
                _ => UnknownCommand(args[0], error),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            await error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private static int GenerateKeyring(string[] args, TextWriter output)
    {
        var options = CommandOptions.Parse(args, ["overwrite"]);
        options.ThrowIfUnknown(["output", "active-key-id", "overwrite"]);
        var outputPath = options.RequireValue("output");
        var activeKeyId = options.ValueOrDefault("active-key-id", "key-1") ?? "key-1";
        var overwrite = options.HasFlag("overwrite");

        var document = SecretStoreKeyringDocument.Create(activeKeyId);
        document.SaveToFile(outputPath, overwrite);
        output.WriteLine($"generated keyring: {outputPath}");
        return 0;
    }

    private static int AddKey(string[] args, TextWriter output)
    {
        var options = CommandOptions.Parse(args);
        options.ThrowIfUnknown(["keyring", "key-id"]);
        var keyringPath = options.RequireValue("keyring");
        var keyId = options.RequireValue("key-id");

        var document = SecretStoreKeyringDocument.LoadFromFile(keyringPath);
        document.Validate();
        var fingerprintKey = document.FingerprintKey;
        document.AddDataKey(keyId);
        document.FingerprintKey = fingerprintKey;
        document.SaveToFile(keyringPath, overwrite: true);
        output.WriteLine($"added active key: {keyId}");
        return 0;
    }

    private static async Task<int> ReencryptSweepAsync(
        string[] args,
        TextWriter output,
        ISecretStoreSweepTarget? sweepTarget,
        CancellationToken ct)
    {
        var options = CommandOptions.Parse(args, ["dry-run", "resume", "verify"]);
        options.ThrowIfUnknown([
            "keyring",
            "connection-string",
            "database",
            "secret-vault-prefix",
            "runtime-secret-prefix",
            "dry-run",
            "checkpoint",
            "resume",
            "verify",
            "batch-size",
        ]);

        var keyringPath = options.RequireValue("keyring");
        var keyring = GarnetSecretStoreKeyring.LoadFromFile(keyringPath);
        var sweepOptions = new SecretStoreSweepOptions
        {
            SecretVaultPrefix = options.ValueOrDefault("secret-vault-prefix", "aevatar:secret-vault") ??
                "aevatar:secret-vault",
            RuntimeSecretPrefix = options.ValueOrDefault("runtime-secret-prefix", "aevatar:runtime-secrets") ??
                "aevatar:runtime-secrets",
            DryRun = options.HasFlag("dry-run"),
            CheckpointPath = options.ValueOrDefault("checkpoint", null),
            Resume = options.HasFlag("resume"),
            Verify = options.HasFlag("verify"),
            BatchSize = ParsePositiveInt(options.ValueOrDefault("batch-size", "500"), "batch-size"),
        };

        if (sweepTarget != null)
        {
            var injectedSweep = new SecretStoreReencryptionSweep(sweepTarget, keyring);
            var injectedResult = await injectedSweep.RunAsync(sweepOptions, ct);
            WriteSweepResult(output, injectedResult);
            return injectedResult.Succeeded ? 0 : 2;
        }

        var connectionString = options.RequireValue("connection-string");
        var database = ParseInt(options.ValueOrDefault("database", "-1"), "database");
        using var redisTarget = await RedisSecretStoreSweepTarget.ConnectAsync(connectionString, database);
        var sweep = new SecretStoreReencryptionSweep(redisTarget, keyring);
        var result = await sweep.RunAsync(sweepOptions, ct);
        WriteSweepResult(output, result);
        return result.Succeeded ? 0 : 2;
    }

    private static int UnknownCommand(string command, TextWriter error)
    {
        error.WriteLine($"Unknown secret-store command '{command}'. Allowed commands: {string.Join(", ", CommandNames)}.");
        return 1;
    }

    private static void WriteUsage(TextWriter output)
    {
        output.WriteLine("secret-store commands:");
        foreach (var command in CommandNames)
            output.WriteLine($"  {command}");
    }

    private static void WriteSweepResult(TextWriter output, SecretStoreSweepResult result)
    {
        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"sweep scanned={result.Scanned} changed={result.Changed} updated={result.Updated} alreadyActive={result.AlreadyActive} conflicts={result.Conflicts} missing={result.Missing} errors={result.Errors} verified={result.Verified} verifyFailures={result.VerifyFailures}"));
    }

    private static int ParsePositiveInt(string? value, string name)
    {
        var parsed = ParseInt(value, name);
        if (parsed <= 0)
            throw new InvalidOperationException($"Option --{name} must be positive.");

        return parsed;
    }

    private static int ParseInt(string? value, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new InvalidOperationException($"Option --{name} must be an integer.");

        return parsed;
    }

    private sealed class CommandOptions
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        private CommandOptions()
        {
        }

        public static CommandOptions Parse(string[] args, IEnumerable<string>? flags = null)
        {
            var flagSet = new HashSet<string>(flags ?? Array.Empty<string>(), StringComparer.Ordinal);
            var options = new CommandOptions();
            for (var i = 0; i < args.Length; i++)
            {
                var token = args[i];
                if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
                    throw new InvalidOperationException($"Unexpected argument '{token}'.");

                var name = token[2..];
                if (flagSet.Contains(name))
                {
                    options._values[name] = "true";
                    continue;
                }

                if (i + 1 >= args.Length)
                    throw new InvalidOperationException($"Option --{name} requires a value.");

                options._values[name] = args[++i];
            }

            return options;
        }

        public bool HasFlag(string name) => _values.TryGetValue(name, out var value) && value == "true";

        public string RequireValue(string name)
        {
            var value = ValueOrDefault(name, null);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Option --{name} is required.");

            return value;
        }

        public string? ValueOrDefault(string name, string? fallback) =>
            _values.TryGetValue(name, out var value) ? value : fallback;

        public void ThrowIfUnknown(IEnumerable<string> allowed)
        {
            var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var key in _values.Keys)
            {
                if (!allowedSet.Contains(key))
                    throw new InvalidOperationException($"Unknown option --{key}.");
            }
        }
    }
}
