using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Aevatar.Scripting.Abstractions.Behaviors;

public sealed record ScriptBehaviorDescriptor(
    Type StateClrType,
    Type ReadModelClrType,
    MessageDescriptor StateDescriptor,
    MessageDescriptor ReadModelDescriptor,
    string StateTypeUrl,
    string ReadModelTypeUrl,
    IReadOnlyDictionary<string, ScriptCommandRegistration> Commands,
    IReadOnlyDictionary<string, ScriptSignalRegistration> Signals,
    IReadOnlyDictionary<string, ScriptDomainEventRegistration> DomainEvents,
    IReadOnlyDictionary<string, ScriptQueryRegistration> Queries,
    ByteString? ProtocolDescriptorSet,
    ScriptRuntimeSemanticsSpec? RuntimeSemantics = null)
{
    public ScriptBehaviorDescriptor WithProtocolDescriptorSet(ByteString descriptorSet) =>
        this with
        {
            ProtocolDescriptorSet = descriptorSet ?? ByteString.Empty,
        };

    public ScriptBehaviorDescriptor WithRuntimeSemantics(ScriptRuntimeSemanticsSpec runtimeSemantics) =>
        this with
        {
            RuntimeSemantics = runtimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec(),
        };

    public ScriptGAgentContract ToContract()
    {
        var queryResultTypeUrls = Queries.ToDictionary(
            static pair => pair.Key,
            static pair => ScriptMessageTypes.GetTypeUrl(pair.Value.ResultClrType),
            StringComparer.Ordinal);

        return new ScriptGAgentContract(
            StateTypeUrl: StateTypeUrl,
            ReadModelTypeUrl: ReadModelTypeUrl,
            CommandTypeUrls: Commands.Keys.OrderBy(static x => x, StringComparer.Ordinal).ToArray(),
            DomainEventTypeUrls: DomainEvents.Keys.OrderBy(static x => x, StringComparer.Ordinal).ToArray(),
            QueryTypeUrls: Queries.Keys.OrderBy(static x => x, StringComparer.Ordinal).ToArray(),
            QueryResultTypeUrls: queryResultTypeUrls,
            InternalSignalTypeUrls: Signals.Keys.OrderBy(static x => x, StringComparer.Ordinal).ToArray(),
            StateDescriptorFullName: StateDescriptor.FullName ?? string.Empty,
            ReadModelDescriptorFullName: ReadModelDescriptor.FullName ?? string.Empty,
            ProtocolDescriptorSet: ProtocolDescriptorSet ?? ByteString.Empty,
            RuntimeSemantics: RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec());
    }
}
