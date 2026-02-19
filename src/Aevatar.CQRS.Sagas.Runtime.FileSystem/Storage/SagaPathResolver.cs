using Aevatar.CQRS.Sagas.Abstractions.Configuration;
using Microsoft.Extensions.Options;

namespace Aevatar.CQRS.Sagas.Runtime.FileSystem.Storage;

internal sealed class SagaPathResolver
{
    public SagaPathResolver(IOptions<SagaRuntimeOptions> options)
    {
        var configured = options.Value.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(configured))
            configured = Path.Combine("artifacts", "cqrs", "sagas");

        Root = Path.GetFullPath(configured);
        States = Ensure("states");
        Timeouts = Ensure("timeouts");
        TimeoutPending = Ensure(Path.Combine("timeouts", "pending"));
        TimeoutProcessing = Ensure(Path.Combine("timeouts", "processing"));
    }

    public string Root { get; }

    public string States { get; }

    public string Timeouts { get; }

    public string TimeoutPending { get; }

    public string TimeoutProcessing { get; }

    public string EnsureSagaFolder(string sagaName)
    {
        var sanitized = SanitizeSegment(sagaName);
        var folder = Path.Combine(States, sanitized);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private string Ensure(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SanitizeSegment(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = input
            .Select(ch => invalid.Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch)
            .ToArray();

        var segment = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(segment) ? "unknown" : segment;
    }
}
