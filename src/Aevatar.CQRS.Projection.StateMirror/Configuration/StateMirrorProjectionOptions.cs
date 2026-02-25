namespace Aevatar.CQRS.Projection.StateMirror.Configuration;

public sealed class StateMirrorProjectionOptions
{
    public StateMirrorProjectionOptions()
    {
        IgnoredFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RenamedFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public HashSet<string> IgnoredFields { get; }

    public Dictionary<string, string> RenamedFields { get; }
}
