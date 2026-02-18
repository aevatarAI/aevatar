using Aevatar.CQRS.Runtime.Abstractions.Configuration;
using Microsoft.Extensions.Options;

namespace Aevatar.CQRS.Runtime.FileSystem.Storage;

internal sealed class CqrsPathResolver
{
    public CqrsPathResolver(IOptions<CqrsRuntimeOptions> options)
    {
        Root = ResolveAbsolute(options.Value.WorkingDirectory);
        Commands = Ensure("commands");
        Inbox = Ensure("inbox");
        Outbox = Ensure("outbox");
        DeadLetters = Ensure("dlq");
        Checkpoints = Ensure("checkpoints");
    }

    public string Root { get; }
    public string Commands { get; }
    public string Inbox { get; }
    public string Outbox { get; }
    public string DeadLetters { get; }
    public string Checkpoints { get; }

    private string Ensure(string child)
    {
        var path = Path.Combine(Root, child);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine("artifacts", "cqrs");

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
    }
}
