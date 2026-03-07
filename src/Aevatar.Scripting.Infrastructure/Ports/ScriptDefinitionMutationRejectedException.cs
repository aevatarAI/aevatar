namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptDefinitionMutationRejectedException : InvalidOperationException
{
    public ScriptDefinitionMutationRejectedException(
        string message,
        IReadOnlyList<string>? diagnostics = null)
        : base(message)
    {
        Diagnostics = diagnostics ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> Diagnostics { get; }
}
