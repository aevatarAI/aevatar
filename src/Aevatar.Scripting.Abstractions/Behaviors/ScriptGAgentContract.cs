using Google.Protobuf;

namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed record ScriptGAgentContract(
    string StateTypeUrl,
    string ReadModelTypeUrl,
    IReadOnlyList<string> CommandTypeUrls,
    IReadOnlyList<string> DomainEventTypeUrls,
    IReadOnlyList<string> InternalSignalTypeUrls,
    string StateDescriptorFullName,
    string ReadModelDescriptorFullName,
    ByteString? ProtocolDescriptorSet = null,
    ScriptRuntimeSemanticsSpec? RuntimeSemantics = null)
{
    public static ScriptGAgentContract Empty { get; } = new(
        StateTypeUrl: string.Empty,
        ReadModelTypeUrl: string.Empty,
        CommandTypeUrls: Array.Empty<string>(),
        DomainEventTypeUrls: Array.Empty<string>(),
        InternalSignalTypeUrls: Array.Empty<string>(),
        StateDescriptorFullName: string.Empty,
        ReadModelDescriptorFullName: string.Empty,
        ProtocolDescriptorSet: ByteString.Empty,
        RuntimeSemantics: new ScriptRuntimeSemanticsSpec());

    public IReadOnlyList<string> CommandTypes => CommandTypeUrls;

    public IReadOnlyList<string> DomainEventTypes => DomainEventTypeUrls;

    public IReadOnlyList<string> InternalSignalTypes => InternalSignalTypeUrls;
}
