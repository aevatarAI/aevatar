using Aevatar.Scripting.Core.Schema;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Schema;

public class DefaultScriptReadModelSchemaActivationPolicyTests
{
    [Fact]
    public void ValidateActivation_ShouldDefaultToDocumentAndGraph_WhenKindsAreNullOrEmpty()
    {
        var nullConfigured = new DefaultScriptReadModelSchemaActivationPolicy(null);
        var emptyConfigured = new DefaultScriptReadModelSchemaActivationPolicy([]);

        var request = new ScriptReadModelSchemaActivationRequest(
            RequiresDocumentStore: true,
            RequiresGraphStore: true,
            DeclaredProviderHints: []);

        var nullResult = nullConfigured.ValidateActivation(request);
        var emptyResult = emptyConfigured.ValidateActivation(request);

        nullResult.IsActivated.Should().BeTrue();
        nullResult.MissingStoreKinds.Should().BeEmpty();
        emptyResult.IsActivated.Should().BeTrue();
        emptyResult.MissingStoreKinds.Should().BeEmpty();
    }

    [Fact]
    public void ValidateActivation_ShouldFail_WhenGraphStoreNotConfigured()
    {
        var policy = new DefaultScriptReadModelSchemaActivationPolicy([ScriptReadModelStoreKind.Document]);
        var request = new ScriptReadModelSchemaActivationRequest(
            RequiresDocumentStore: true,
            RequiresGraphStore: true,
            DeclaredProviderHints: []);

        var result = policy.ValidateActivation(request);

        result.IsActivated.Should().BeFalse();
        result.MissingStoreKinds.Should().ContainSingle(x => x == ScriptReadModelStoreKind.Graph);
    }
}
