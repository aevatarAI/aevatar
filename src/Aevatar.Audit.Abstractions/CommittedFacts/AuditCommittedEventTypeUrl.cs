using Google.Protobuf.Reflection;

namespace Aevatar.Audit.Abstractions.CommittedFacts;

public static class AuditCommittedEventTypeUrl
{
    public static string FromDescriptor(MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return $"type.googleapis.com/{descriptor.FullName}";
    }
}
