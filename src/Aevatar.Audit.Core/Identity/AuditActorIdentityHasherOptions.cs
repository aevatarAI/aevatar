namespace Aevatar.Audit.Core.Identity;

public sealed class AuditActorIdentityHasherOptions
{
    public const string SectionName = "Audit:ActorIdentityHasher";

    public string? ActiveKeyId { get; set; }

    public List<AuditActorIdentityHasherKeyOptions> Keys { get; set; } = [];
}

public sealed class AuditActorIdentityHasherKeyOptions
{
    public string? KeyId { get; set; }

    public string? Key { get; set; }

    public string? KeyBase64 { get; set; }
}
